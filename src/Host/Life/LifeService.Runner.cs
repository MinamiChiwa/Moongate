using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.Life;

// 总队列的执行：一个后台循环按顺序取第一项待执行任务，每次发出一轮；每轮之前核对暂停请求、已确认的花费上限与租约，
// 任一轮被拒绝、阻塞、不足或中断即暂停整队（立即执行的单项只记该项失败），不自动重发。循环持有活动锁并每 10 秒续租。
public sealed partial class LifeService
{
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(10);

    private sealed class Runner
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public IPerformanceLease? Lease;
        /// <summary>立即执行的单项；为空表示按队列顺序执行。</summary>
        public QueueTask? Only { get; init; }
        public bool Immediate => Only is not null;
        public volatile bool PauseRequested, UserStopped, LeaseLost;
        public QueueTask? Current;
        /// <summary>立即执行结束后恢复的队列状态。</summary>
        public (LifeQueueState State, string? Code, string? Detail, string? TaskId, int? Approved, string? Currency)? Restore;
        public Task? Task;
        public void ReleaseLease() => Interlocked.Exchange(ref Lease, null)?.Dispose();
    }

    private sealed record Halt(QueueTask Task, string Code, string? Detail, bool Unknown);

    private Runner? runner;

    /// <summary>取得活动锁并开始后台循环；<paramref name="only"/> 不为空时只执行这一项（插到最前），结束后队列回到原状态。</summary>
    private void Launch(QueueTask? only, int approved, string? currency)
    {
        var session = controls.Bindings.Current?.Session.Id ?? throw new OperationException("input.bindingChanged", "没有绑定的游戏会话");
        var lease = leases.Acquire(OwnerId, "life-" + Guid.NewGuid().ToString("N"), session);
        Runner created;
        lock (gate)
        {
            if (runner is not null || disposed) { lease.Dispose(); throw new OperationException("life.actionRunning", "生活队列正在执行"); }
            created = new Runner { Lease = lease, Only = only };
            if (only is not null)
            {
                created.Restore = (queueState, pauseCode, pauseDetail, pauseTaskId, approvedCost, costCurrency);
                active.Insert(0, only);
            }
            else { queueState = LifeQueueState.Running; pauseCode = pauseDetail = pauseTaskId = null; }
            approvedCost = approved; estimatedSpent = 0; costCurrency = currency;
            runner = created;
            Changed();
        }
        trace?.Emit(DiagnosticEvents.GameCliAction, new DiagnosticFields().Value("action", only is null ? "life.queue" : "life." + only.Kind).Value("result", "started").Value("code", null));
        created.Task = Task.Run(() => RunAsync(created));
    }

    private async Task RunAsync(Runner run)
    {
        var ct = run.Cancellation.Token;
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renew = Task.Run(() => RenewAsync(run, renewal.Token));
        Halt? halt = null;
        QueueTask? last = null;
        try
        {
            while (true)
            {
                QueueTask? task;
                long unit;
                var catalog = life.CachedCatalog();
                lock (gate)
                {
                    task = run.Only is { } only ? (only.State is LifeTaskState.Pending or LifeTaskState.Running ? only : null)
                        : active.FirstOrDefault(x => !x.Immediate && x.State is LifeTaskState.Pending or LifeTaskState.Running);
                    if (task is null) break;
                    var unitCost = UnitCost(catalog, task.Kind);
                    unit = unitCost?.Amount ?? 0;
                    task.CostText = unitCost?.Text ?? task.CostText;
                    if (run.PauseRequested) { halt = new(task, "life.pausedByUser", null, false); break; }
                    // 立即执行的单项同样以确认的花费为限：采集按获得数量结束，每次实得少于 10 个时调用次数会多于确认时的估算，到上限即停。
                    if (approvedCost is { } approved && estimatedSpent + unit > approved)
                    { halt = new(task, "life.costLimitReached", null, false); break; }
                    task.State = LifeTaskState.Running;
                    task.StartedAt ??= clock.GetUtcNow();
                    run.Current = task;
                    Changed();
                }
                last = task;
                if (ct.IsCancellationRequested) { halt = new(task, CancelCode(run), null, false); break; }
                if (run.Lease is not { IsValid: true } lease) { halt = new(task, "input.activityLeaseRequired", null, false); break; }
                string? workName = null;
                if (task.Kind == "collect")
                {
                    lock (gate) workName = WorkToCollect(task.Facility!);
                    if (workName is null) { halt = new(task, "life.nothingToCollect", null, false); break; }
                }
                var outcome = await ExecuteRoundAsync(task, workName, lease.Proof, ct).ConfigureAwait(false);
                var finished = false;
                lock (gate)
                {
                    var result = Map(++task.RoundNumber, outcome);
                    lock (task.Results)
                    {
                        task.Results.Add(result);
                        if (task.Results.Count > ResultsPerTask) task.Results.RemoveAt(0);
                    }
                    if (!outcome.NotSent) estimatedSpent = (int)Math.Min(int.MaxValue, estimatedSpent + unit);
                    var success = outcome.Kind switch
                    {
                        TwCliLifeKind.Completed => task.Kind is "gather" or "craft",
                        TwCliLifeKind.Started => task.Kind == "alter",
                        TwCliLifeKind.Collected => task.Kind == "collect",
                        _ => false
                    };
                    if (success)
                    {
                        task.Done = task.Kind switch
                        {
                            "craft" or "collect" => task.Count,
                            // 采集按本次返回的获得数量累计（同一采集点的额外掉落计入同名物品，可能超过目标）。
                            "gather" => (int)Math.Min(int.MaxValue, task.Done + Math.Max(0L, outcome.Gained ?? outcome.Target ?? GatherPerCall)),
                            _ => task.Done + 1
                        };
                        task.LastCode = task.LastDetail = null;
                        task.StateUnknown = false;
                        run.Current = null;
                        if (task.Done >= task.Count) { Finish(task, LifeTaskState.Completed); finished = true; lastAction = Describe(task, LifeActionPhase.Completed); }
                        else task.State = LifeTaskState.Pending;
                        Changed();
                    }
                    else if (outcome.Kind == TwCliLifeKind.Started && task.Kind == "gather")
                    {
                        // 目录把钓鱼类采集物也列在采集里；命令行只会开启悠闲钓鱼，立即关掉并记住该物品。
                        knownFishing.Add(task.Target);
                        halt = new(task, "life.fishingStarted", null, false);
                    }
                    else
                    {
                        var interrupted = outcome.Kind == TwCliLifeKind.Interrupted;
                        var code = interrupted ? CancelCode(run) : Code(outcome);
                        if (task.Kind == "alter" && code == "gameCli.queueFull" && task.Facility is { } facility) fullFacilities.Add(facility);
                        halt = new(task, code, outcome.BlockedKind ?? outcome.Message, interrupted || outcome.Kind == TwCliLifeKind.Failed);
                    }
                }
                if (halt is { Code: "life.fishingStarted" }) { await life.StopActionAsync(lease.Proof, CancellationToken.None).ConfigureAwait(false); break; }
                if (halt is not null) break;
                if (finished) await ReReadAsync(task.Kind, CancellationToken.None).ConfigureAwait(false);
                else if (WorksDue()) await ReReadAsync("works", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { if (last is not null) halt ??= new(last, CancelCode(run), null, true); }
        finally
        {
            renewal.Cancel();
            try { await renew.ConfigureAwait(false); } catch (OperationCanceledException) { }
            run.ReleaseLease();
            End(run, halt);
        }
        if (halt is not null) await ReReadAsync(halt.Task.Kind, CancellationToken.None).ConfigureAwait(false);
    }

    // 循环结束：暂停整队（停在出问题的那一项），或在全部完成时回到空闲；立即执行的单项结束为完成、失败或停止，队列回到原状态。
    private void End(Runner run, Halt? halt)
    {
        lock (gate)
        {
            if (halt is { } stop)
            {
                var task = stop.Task;
                task.LastCode = stop.Code; task.LastDetail = stop.Detail;
                task.StateUnknown |= stop.Unknown;
                var phase = stop.Code is "life.stoppedByUser" or "gameCli.interrupted" or "life.stoppedInGame" or "input.activityLeaseRequired" or "life.pausedByUser"
                    or "life.costLimitReached" ? LifeActionPhase.Stopped : LifeActionPhase.Failed;
                if (task.Immediate) Finish(task, phase == LifeActionPhase.Stopped ? LifeTaskState.Stopped : LifeTaskState.Failed);
                else
                {
                    task.State = LifeTaskState.Pending;
                    queueState = LifeQueueState.Paused;
                    pauseCode = stop.Code; pauseDetail = stop.Detail; pauseTaskId = task.Id;
                }
                lastAction = Describe(task, phase);
            }
            else if (!run.Immediate)
            {
                queueState = active.Count == 0 ? LifeQueueState.Idle : LifeQueueState.Paused;
                if (active.Count > 0) { pauseCode = "life.pausedByUser"; pauseDetail = null; pauseTaskId = active[0].Id; }
            }
            if (run.Only is { } only && active.Contains(only)) Finish(only, LifeTaskState.Stopped);
            if (run.Restore is { } restore)
                (queueState, pauseCode, pauseDetail, pauseTaskId, approvedCost, costCurrency) = restore;
            else { approvedCost = null; estimatedSpent = 0; }
            run.Current = null;
            runner = null;
            Changed();
        }
        trace?.Emit(DiagnosticEvents.GameCliAction, new DiagnosticFields().Value("action", run.Only is null ? "life.queue" : "life." + run.Only.Kind)
            .Value("result", halt is null ? "ok" : "failed").Value("code", halt?.Code));
    }

    // 调用方持有 gate：任务结束，移到记录里（记录有上限，最早的先移除）。
    private void Finish(QueueTask task, LifeTaskState state)
    {
        task.State = state;
        task.EndedAt = clock.GetUtcNow();
        active.Remove(task);
        history.Add(task);
        while (history.Count > HistoryCapacity) history.RemoveAt(0);
    }

    private async Task<TwCliLifeOutcome> ExecuteRoundAsync(QueueTask task, string? workName, GameActivityProof proof, CancellationToken ct)
    {
        switch (task.Kind)
        {
            case "gather":
                return await life.ExecuteAsync(proof, TwCliLifeControl.Gather, TwCliBody.Json(new Dictionary<string, object> { ["displayName"] = task.Target }), ct).ConfigureAwait(false);
            case "alter":
                return await life.ExecuteAsync(proof, TwCliLifeControl.Alter, TwCliBody.Json(new Dictionary<string, object> { ["displayName"] = task.Target }), ct).ConfigureAwait(false);
            case "collect":
                return await life.ExecuteAsync(proof, TwCliLifeControl.Collect, TwCliBody.Json(new Dictionary<string, object> { ["displayName"] = workName! }), ct).ConfigureAwait(false);
            default:
            {
                var body = new Dictionary<string, object> { ["displayName"] = task.Target };
                if (task.Variant is { } variant) body["variant"] = variant;
                body["craftCount"] = task.Count;
                return await life.ExecuteAsync(proof, TwCliLifeControl.Craft, TwCliBody.Json(body), ct).ConfigureAwait(false);
            }
        }
    }

    // 取消的原因：用户点停止、租约被撤销（紧急停止或过期），其余按命令被中断处理。
    private static string CancelCode(Runner run) =>
        run.UserStopped ? "life.stoppedByUser" : run.LeaseLost || run.Lease is not { IsValid: true } ? "input.activityLeaseRequired" : "gameCli.interrupted";

    private static string Code(TwCliLifeOutcome outcome) => outcome.Kind switch
    {
        TwCliLifeKind.Stopped or TwCliLifeKind.StoppedByUser => "life.stoppedInGame",
        _ => outcome.Code ?? "gameCli.failed"
    };

    private static LifeRoundResult Map(int round, TwCliLifeOutcome outcome)
    {
        var spent = TwCliLifeCost.FromResult(outcome.Cost);
        var kind = outcome.Kind switch
        {
            TwCliLifeKind.Completed => LifeRoundOutcome.Completed,
            TwCliLifeKind.Started => LifeRoundOutcome.Started,
            TwCliLifeKind.Collected => LifeRoundOutcome.Collected,
            TwCliLifeKind.Stopped or TwCliLifeKind.StoppedByUser => LifeRoundOutcome.Stopped,
            TwCliLifeKind.Rejected => LifeRoundOutcome.Rejected,
            TwCliLifeKind.Interrupted => LifeRoundOutcome.Interrupted,
            TwCliLifeKind.Unavailable => LifeRoundOutcome.NotSent,
            _ => LifeRoundOutcome.Failed
        };
        return new(round, kind, outcome.Code, outcome.Message, outcome.BlockedKind, Int(outcome.Gained), Int(outcome.Target), outcome.Cost, Int(spent?.Spent), Int(spent?.Left), spent?.Currency,
            Int(outcome.Collected), (outcome.Rewards ?? []).Select(x => new LifeReward(x.Name, Int(x.Amount))).ToArray(), Int(outcome.CraftCount), Int(outcome.MaxCount));
    }

    private async Task RenewAsync(Runner run, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RenewInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                if (run.Lease is not { } lease || !lease.Renew()) { run.LeaseLost = true; run.Cancellation.Cancel(); return; }
        }
        catch (OperationCanceledException) { }
    }

    // 任务结束或暂停后重读受影响的块：加工排队与领取重读队列与配方（领取腾出槽位后配方不再报告 queue_full，“＋”空槽随之出现），
    // 加工到点只重读队列，制作重读配方；被中断或出错时同样重读，游戏里的动作状态按未知处理。
    private async Task ReReadAsync(string kind, CancellationToken ct)
    {
        string[] sections = kind switch { "alter" or "collect" => ["works", "alter"], "works" => ["works"], "craft" => ["craft"], _ => [] };
        if (sections.Length == 0) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await ReadAsync(sections, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
    }
}
