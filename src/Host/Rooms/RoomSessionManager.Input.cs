using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Rooms.Domain;

namespace MabiTool.Host.Rooms;

public sealed partial class RoomSessionManager
{
    private sealed record ReleaseReceipt(string PreparationId, RoomLifecycleSnapshot Snapshot, long Created);

    public async Task<RoomLifecycleSnapshot> ReleaseInputAsync(string owner, string id, string? key, CancellationToken token)
    {
        RequireKey(key);
        var entry = Find(owner);
        await entry.Gate.WaitAsync(token);
        try
        {
            foreach (var old in entry.ReleaseReceipts.Where(x => timeProvider.GetElapsedTime(x.Value.Created) >= TimeSpan.FromHours(24)).Select(x => x.Key).ToArray())
                entry.ReleaseReceipts.Remove(old);
            if (entry.ReleaseReceipts.TryGetValue(key!, out var previous))
                return previous.PreparationId == id ? previous.Snapshot : throw new OperationException("idempotency.conflict", "幂等键已用于其他准备");
            var input = entry.LocalInput ?? throw new OperationException("room.planStale", "本机没有该准备");
            if (input.Snapshot.Id != id) throw new OperationException("room.planStale", "本机准备已变化");
            if (entry.ReleaseReceipts.Count >= 1000) throw new OperationException("resource.capacityExceeded", "输入释放回执达到上限");
            // 先核对 ID，再撤销计划；旧请求不能取消后来建立的准备。
            input.Release(id);
            entry.Client.CancelLocalPreparations();
            entry.Scheduler.CancelAll();
            lock (entry.ReadinessGate) ReleaseReadiness(entry);
            var result = Snapshot(entry);
            entry.ReleaseReceipts.Add(key!, new(id, result, timeProvider.GetTimestamp()));
            QueueWithdrawal(entry, owner);
            Notify(owner);
            return result;
        }
        catch (RoomInputException error) { throw InputFailure(error); }
        finally { entry.Gate.Release(); }
    }

    /// <summary>本机停止送达游戏的最长等待；紧急停止在它之后撤销输入占用。</summary>
    public static readonly TimeSpan EmergencyStopDelivery = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// 紧急停止（F9 与界面按钮）。游戏内乐曲自动循环、不会自然停止，所以与原工具 StopLocally 相同：
    /// 本机正在参与的音乐轮次先排入仅本机的停止动作并等它执行（最多 <see cref="EmergencyStopDelivery"/>），
    /// 再取消其余本机准备与待执行动作（含尚未到点的自动停止），并通知房间本机已停止。
    /// 调用方在这之后撤销前台输入占用，所以停止键送达后不再有本工具发起的输入。
    /// </summary>
    public void EmergencyStop()
    {
        foreach (var pair in entries)
        {
            var entry = pair.Value;
            if (entry.LocalInput?.Snapshot.State == "performing") DeliverLocalStop(entry);
            entry.Client.CancelLocalPreparations();
            entry.Scheduler.CancelAll();
            lock (entry.ReadinessGate) { entry.HostConfigured = false; ReleaseReadiness(entry); }
            QueueWithdrawal(entry, pair.Key);
            Notify(pair.Key);
        }
    }

    private static void DeliverLocalStop(Entry entry)
    {
        // 停止动作的标识在排入时才生成，回执可能先于标识返回到达：记下期间到达的全部回执再核对。
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        using var arrived = new SemaphoreSlim(0);
        void OnReceipt(ActionReceipt receipt) { seen.TryAdd(receipt.ActionId, 0); arrived.Release(); }
        entry.Scheduler.Receipt += OnReceipt;
        try
        {
            var (local, id) = entry.Client.StopLocally();
            if (local != "stopScheduled" || id is null) return;
            var deadline = Environment.TickCount64 + (long)EmergencyStopDelivery.TotalMilliseconds;
            while (!seen.ContainsKey(id) && arrived.Wait(TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64)))) { }
        }
        finally { entry.Scheduler.Receipt -= OnReceipt; }
        lock (entry.ReadinessGate) entry.HostConfigured = false;
        if (entry.Client.IsConnected && entry.Client.Session is not null)
            _ = entry.Client.NotifyLocalStopAsync().ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200), timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var pair in entries) RefreshLocal(pair.Value, pair.Key);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { EmergencyStop(); }
    }

    private void RefreshLocal(Entry entry, string owner)
    {
        if (!entry.Gate.Wait(0)) return;
        var changed = false;
        try
        {
            changed = RefreshScoreSource(entry, owner);
            if (entry.LocalInput is not { } input) return;
            input.KeepAlive();
            var state = input.Snapshot;
            if (state.Revision == entry.LastInputRevision) return;
            changed = true;
            if (state.State == "released")
            {
                entry.Client.CancelLocalPreparations();
                entry.Scheduler.CancelAll();
                lock (entry.ReadinessGate) ReleaseReadiness(entry);
                QueueWithdrawal(entry, owner);
            }
            entry.LastInputRevision = input.Snapshot.Revision;
        }
        finally { entry.Gate.Release(); if (changed) Notify(owner); }
    }

    private void QueueWithdrawal(Entry entry, string owner)
    {
        lock (entry.ReadinessGate)
        {
            if (entry.WithdrawalPending || !entry.Client.IsConnected || entry.Client.Session is null) return;
            if (entry.Client.Room is not { } room || !room.TryGetProperty("members", out var members) ||
                !members.EnumerateArray().Any(x => x.GetProperty("id").GetString() == entry.Client.Session.MemberId &&
                    x.TryGetProperty("ready", out var ready) && ready.ValueKind == System.Text.Json.JsonValueKind.True)) return;
            entry.WithdrawalPending = true;
        }
        _ = WithdrawAsync();
        async Task WithdrawAsync()
        {
            try
            {
                await entry.Gate.WaitAsync();
                try
                {
                    if (entry.Client.IsConnected && entry.LocalInput?.Snapshot.State is not ("prepared" or "armed" or "performing"))
                        await entry.Client.SetReadyAsync(false, Simulated(owner), false, false, 0, "initiator", null, 0, 0);
                }
                finally { entry.Gate.Release(); }
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                lock (entry.ReadinessGate) entry.WithdrawalPending = false;
                Notify(owner);
            }
        }
    }

    private static OperationException InputFailure(RoomInputException error) => new(error.Code, "本机输入准备无法完成",
        error.OccupiedBy is null ? null : new Dictionary<string, System.Text.Json.JsonElement>
        { ["occupiedBy"] = System.Text.Json.JsonSerializer.SerializeToElement(error.OccupiedBy) });

    private static void RequireKey(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "操作需要 Idempotency-Key");
        if (key.Length is < 8 or > 128 || key.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '-')))
            throw new OperationException("request.invalid", "幂等键无效");
    }
}
