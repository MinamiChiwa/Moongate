using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// “生活”：快照由页面的读取、队列编辑与命令的回应更新；生活页或生活浮窗元件显示时例行读取快照（队列执行中约每秒一次，平时五秒一次，
// 快照读取不调用命令行），队列执行中即使界面不显示也继续跟踪到结束。执行结束后再查几次，取得后端重读的结果。
public sealed partial class LiveBackend
{
    private LifeSnapshot? life;
    private string? lifeStamp;
    private bool lifePolling;
    private long lastLifeRead;
    private bool lifeUnsupported;

    public LifeSnapshot? GetLife()
    {
        if (!Read().State.Connected) return null;
        lock (gate) return life;
    }

    public async Task<BackendReply> RefreshLifeAsync(string section, string? query, CancellationToken cancellationToken)
    {
        var reply = await LifeReplyAsync(async (api, token) => ApplyLife(await api.RefreshLifeAsync(new(section, query), token)), cancellationToken);
        // 执行进行中读取被拒绝（例如前端重启后页面重新打开）：取当前快照显示进度，并继续跟踪到结束。
        if (reply.Code == "life.actionRunning")
            await LifeReplyAsync(async (api, token) => { ApplyLife(await api.GetLifeAsync(token)); BeginLifePolling(); }, cancellationToken);
        return reply;
    }

    public Task<BackendReply> StartLifeActionAsync(LifeActionRequest request, CancellationToken cancellationToken) =>
        LifeReplyAsync(async (api, token) =>
        {
            ApplyLife(await api.StartLifeActionAsync(request, IdempotencyKey(), token));
            BeginLifePolling();
        }, cancellationToken);

    public Task<BackendReply> StopLifeAsync(CancellationToken cancellationToken) =>
        LifeReplyAsync(async (api, token) => ApplyLife(await api.StopLifeActionAsync(token)), cancellationToken);

    public Task<BackendReply> AddLifeTaskAsync(LifeTaskAddRequest request, CancellationToken cancellationToken) =>
        LifeReplyAsync(async (api, token) => ApplyLife(await api.AddLifeTaskAsync(request, IdempotencyKey(), token)), cancellationToken);

    public Task<BackendReply> UpdateLifeTaskAsync(string taskId, LifeTaskUpdateRequest request, CancellationToken cancellationToken) =>
        LifeReplyAsync(async (api, token) => ApplyLife(await api.UpdateLifeTaskAsync(taskId, request, token)), cancellationToken);

    public Task<BackendReply> RemoveLifeTaskAsync(string taskId, CancellationToken cancellationToken) =>
        LifeReplyAsync(async (api, token) => ApplyLife(await api.RemoveLifeTaskAsync(taskId, token)), cancellationToken);

    public Task<BackendReply> CommandLifeQueueAsync(LifeQueueCommand command, CancellationToken cancellationToken) =>
        LifeReplyAsync(async (api, token) =>
        {
            ApplyLife(await api.CommandLifeQueueAsync(command, token));
            BeginLifePolling();
        }, cancellationToken);

    // 调用方持有 gate。队列正在执行（有当前任务）时例行读取改为约每秒一次。
    private bool LifeRunning => life?.Tasks?.CurrentTaskId is not null || life?.Action?.Phase.KnownValue == LifeActionPhase.Running;

    // 调用方持有 gate。生活页或生活浮窗显示，或队列正在执行时读取快照。
    private bool LifeWanted => !surfacesReported || surfaces.GetValueOrDefault("life") > 0 || surfaces.GetValueOrDefault("overlay.life") > 0 || LifeRunning;

    /// <summary>例行读取生活快照（只读宿主内存，不调用命令行）；后端没有这个接口（旧宿主）时本次连接不再读取。</summary>
    private async Task RefreshLifeSnapshotAsync(MabiToolClient api, CancellationToken token)
    {
        lock (gate)
        {
            if (lifeUnsupported || !LifeWanted) return;
            if (!Elapsed(lastLifeRead, LifeRunning ? StatusInterval : SlowInterval)) return;
            lastLifeRead = Now;
        }
        try { ApplyLife(await api.GetLifeAsync(token)); }
        catch (MabiToolApiException error) when (error.Status == 404) { lock (gate) lifeUnsupported = true; }
    }

    // 调用方持有 gate：下一次例行读取生活快照的时刻；不需要读取时为空。
    private long? LifeDueAt() => lifeUnsupported || !LifeWanted ? null : lastLifeRead + Span(LifeRunning ? StatusInterval : SlowInterval);

    // 生活页的读取与执行结果显示在页面里，不写进活动胶囊。
    private Task<BackendReply> LifeReplyAsync(Func<MabiToolClient, CancellationToken, Task> send, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) => { await send(api, token); return string.Empty; }, cancellationToken, remember: false);

    private void ApplyLife(LifeSnapshot snapshot)
    {
        var stamp = snapshot.BackendEpoch + "|" + snapshot.Revision;
        bool changed;
        lock (gate)
        {
            changed = stamp != lifeStamp;
            life = snapshot;
            lifeStamp = stamp;
        }
        if (changed) Notify();
    }

    /// <summary>新连接：旧快照作废（后端重启后的执行状态与读取结果都不再适用）。</summary>
    private void ResetLife()
    {
        lock (gate) { life = null; lifeStamp = null; lastLifeRead = 0; lifeUnsupported = false; }
    }

    private void BeginLifePolling()
    {
        lock (gate)
        {
            if (lifePolling) return;
            lifePolling = true;
        }
        _ = Task.Run(async () =>
        {
            var token = lifetime.Token;
            var settle = 0;
            try
            {
                while (!token.IsCancellationRequested && settle < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    if (client is not { } api) break;
                    try
                    {
                        var next = await api.GetLifeAsync(token);
                        ApplyLife(next);
                        if (next.Action is not { } action || action.Phase.KnownValue != LifeActionPhase.Running) settle++;
                    }
                    catch (Exception error) when (error is MabiToolApiException or HttpRequestException or IOException) { settle++; }
                }
            }
            catch (OperationCanceledException) { }
            finally { lock (gate) lifePolling = false; }
        });
    }

    // 钓鱼：快照随五秒级例行刷新读取（界面有可见处时），后端的 life.fishing.changed 事件使其立即重读；
    // 命令的回应直接作为最新快照的来源，后端没有这个接口（旧宿主）时本次连接不再探测。开始钓鱼使用当前游戏绑定，不让界面指定窗口或输入。
    private FishingView? fishing;
    private string? fishingStamp;
    private bool fishingUnsupported;
    private long lastFishingRead;

    public FishingView GetFishing()
    {
        if (!Read().State.Connected) return FishingView.Offline;
        lock (gate) return fishing ?? FishingView.NotRead;
    }

    /// <summary>新连接：钓鱼快照缓存作废，下一轮重新读取。</summary>
    private void ResetFishing()
    {
        lock (gate) { fishing = null; fishingStamp = null; fishingUnsupported = false; lastFishingRead = 0; }
    }

    private async Task RefreshFishingAsync(MabiToolClient api, CancellationToken token)
    {
        lock (gate)
        {
            if (fishingUnsupported) return;
            if ((due & Due.Fishing) == 0 && !(FishingWanted && Elapsed(lastFishingRead, SlowInterval))) return;
            due &= ~Due.Fishing;
            lastFishingRead = Now;
        }
        FishingSnapshot? snapshot = null;
        var unsupported = false;
        try { snapshot = await api.GetFishingAsync(token); }
        catch (MabiToolApiException error) { unsupported = error.Status == 404; }
        ApplyFishing(snapshot, unsupported);
    }

    // 只有显示的内容变化时才通知界面；修订相同但内容不同（已用时长推进）同样通知，界面按事实刷新。
    private void ApplyFishing(FishingSnapshot? snapshot, bool unsupported)
    {
        var next = snapshot is not null ? FishingView.From(snapshot) : unsupported ? FishingView.Unsupported : FishingView.NotRead;
        var stamp = snapshot is null ? null : snapshot.BackendEpoch + "|" + snapshot.ResourceRevision;
        bool changed;
        lock (gate)
        {
            changed = !Equals(fishing, next) || stamp != fishingStamp;
            fishing = next;
            fishingStamp = stamp;
            if (unsupported) fishingUnsupported = true;
        }
        if (changed) Notify();
    }

    public async Task<BackendReply> CommandFishingAsync(string action, int? durationSeconds, CancellationToken cancellationToken)
    {
        var reply = await ReplyAsync(async (api, token) =>
        {
            switch (action)
            {
                case "start":
                {
                    var session = (await api.GetGameBindingAsync(token)).Binding?.Session
                        ?? throw new InvalidOperationException(Pick("尚未绑定游戏窗口", "尚未綁定遊戲視窗"));
                    await api.StartFishingAsync(new(session.Id, session.BindingRevision, durationSeconds), IdempotencyKey(), token);
                    return Pick("已开始钓鱼", "已開始釣魚");
                }
                case "stop" or "cancel":
                {
                    var run = (await api.GetFishingAsync(token)).Run;
                    if (run is not { Phase: not ("succeeded" or "failed" or "interrupted" or "cancelled") })
                        throw new InvalidOperationException(Pick("目前没有进行中的钓鱼", "目前沒有進行中的釣魚"));
                    // 停止：离开钓鱼姿态后结束；取消：立即停止并释放输入，之后不再发送游戏输入。
                    if (action == "stop") await api.StopFishingAsync(run.Id, token);
                    else await api.CancelOperationAsync(run.OperationId, new("user"), token);
                    return action == "stop" ? Pick("正在停止钓鱼", "正在停止釣魚") : Pick("已取消钓鱼", "已取消釣魚");
                }
                default: throw new InvalidOperationException("未知的钓鱼命令：" + action);
            }
        }, cancellationToken);
        // 命令之后（成功或被拒绝）状态都可能已变：立即重读一次。
        MarkDue(Due.Fishing, immediate: true);
        return reply;
    }
}
