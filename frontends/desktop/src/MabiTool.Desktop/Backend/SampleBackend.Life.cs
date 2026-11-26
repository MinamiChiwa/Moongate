using MabiTool.Contracts;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的“生活”：默认没有读到任何快照（页面显示未读取，不编造游戏数据）；测试注入快照并记录页面发出的读取、执行与停止。
public sealed partial class SampleBackend
{
    /// <summary>测试用：当前的“生活”快照；为空时页面显示未读取。</summary>
    internal LifeSnapshot? Life { get; set; }

    /// <summary>测试用：页面发出的读取（块与筛选文字），按先后记录。</summary>
    internal List<(string Section, string? Query)> LifeRefreshes { get; } = [];

    /// <summary>测试用：页面发出的执行请求，按先后记录。</summary>
    internal List<LifeActionRequest> LifeActions { get; } = [];

    /// <summary>测试用：页面发出的停止次数。</summary>
    internal int LifeStops { get; private set; }

    public LifeSnapshot? GetLife() => Life;

    public Task<BackendReply> RefreshLifeAsync(string section, string? query, CancellationToken cancellationToken)
    {
        LifeRefreshes.Add((section, query));
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    public Task<BackendReply> StartLifeActionAsync(LifeActionRequest request, CancellationToken cancellationToken)
    {
        LifeActions.Add(request);
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    public Task<BackendReply> StopLifeAsync(CancellationToken cancellationToken)
    {
        LifeStops++;
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    /// <summary>测试用：页面发出的总队列请求，按先后记录（加入、修改、删除、命令）。</summary>
    internal List<LifeTaskAddRequest> LifeTaskAdds { get; } = [];
    internal List<(string Id, LifeTaskUpdateRequest Request)> LifeTaskUpdates { get; } = [];
    internal List<string> LifeTaskRemovals { get; } = [];
    internal List<LifeQueueCommand> LifeQueueCommands { get; } = [];

    public Task<BackendReply> AddLifeTaskAsync(LifeTaskAddRequest request, CancellationToken cancellationToken)
    {
        LifeTaskAdds.Add(request);
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    public Task<BackendReply> UpdateLifeTaskAsync(string taskId, LifeTaskUpdateRequest request, CancellationToken cancellationToken)
    {
        LifeTaskUpdates.Add((taskId, request));
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    public Task<BackendReply> RemoveLifeTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        LifeTaskRemovals.Add(taskId);
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    public Task<BackendReply> CommandLifeQueueAsync(LifeQueueCommand command, CancellationToken cancellationToken)
    {
        LifeQueueCommands.Add(command);
        return Task.FromResult(new BackendReply(true, string.Empty));
    }

    /// <summary>测试用：换上新的快照并通知页面。</summary>
    internal void PublishLife(LifeSnapshot? snapshot)
    {
        Life = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // 样本后端的钓鱼：默认可用、没有运行；命令按后端的规则改变状态（运行中不能再开始，没有运行时不能停止）。
    /// <summary>测试用：钓鱼的当前状态；命令会按后端的规则改变它。</summary>
    internal FishingView FishingState { get; set; } = new(FishingRead.Loaded, true, null, null);

    /// <summary>测试用：收到的钓鱼命令，按先后记录（动作、目标时长）。</summary>
    internal List<(string Action, int? DurationSeconds)> FishingCommands { get; } = [];

    public FishingView GetFishing() => Offline ? FishingView.Offline : FishingState;

    public Task<BackendReply> CommandFishingAsync(string action, int? durationSeconds, CancellationToken cancellationToken)
    {
        FishingCommands.Add((action, durationSeconds));
        if (Offline) return Task.FromResult(new BackendReply(false, Pick("未连接后端", "未連接後端")));
        var view = FishingState;
        string? refusal = action switch
        {
            "start" when !view.Available => view.UnavailableReason ?? "capability.unavailable",
            "start" when view.Running => "resource.busy",
            "stop" or "cancel" when !view.Running => "life.fishing.runInactive",
            _ => null
        };
        if (refusal is not null)
            return Task.FromResult(new BackendReply(false, Pick("请求未完成：", "請求未完成：") + CodeText.Reason(refusal), refusal));
        var now = DateTimeOffset.UtcNow;
        FishingState = action switch
        {
            "start" => view with
            {
                Run = new("sample-run", "sample-operation", "waiting-bite", true, false, null, durationSeconds, new(0, 0, 0, 0), 0, null, null, null, now)
            },
            "stop" => view with { Run = view.Run! with { Phase = "succeeded", Active = false, StopReason = "user-stop", PoseLeft = FishingPoseState.Unconfirmed, UpdatedAt = now } },
            _ => view with { Run = view.Run! with { Phase = "cancelled", Active = false, StopReason = "cancelled", PoseLeft = FishingPoseState.Unconfirmed, UpdatedAt = now } }
        };
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, action == "start" ? Pick("已开始钓鱼", "已開始釣魚") : Pick("已停止钓鱼", "已停止釣魚")));
    }
}
