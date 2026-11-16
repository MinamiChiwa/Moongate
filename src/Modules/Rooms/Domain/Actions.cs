namespace MabiTool.Modules.Rooms.Domain;

public sealed record ScheduledAction(string Id, string SessionId, string Kind, double TargetServerMs, string ServerEpoch)
{
    public string Mode { get; init; } = "emote";
    public bool LocalOnly { get; init; }
    public string? ScoreDraftId { get; init; }
    public string? ScoreDraftRevision { get; init; }
}
public sealed record InputPreparation(bool Ready, string? Error);
public sealed record InputOutcome(string Status, double? SentLocalMs, bool Simulated, string? Detail);
public sealed record ActionReceipt(string ActionId, string SessionId, string Status, double PlannedLocalMs,
    double? ActualLocalMs, double? ActualServerMs, double? ClockUncertaintyMs, bool Simulated, string? Detail)
{
    public bool GameResponseVerified => false;
    public bool ReportToRoom { get; init; } = true;
    public int AppliedTimingOffsetMs { get; init; }
}
public interface IInputExecutor
{
    bool IsSimulation(ScheduledAction action) => true;
    InputPreparation Prepare(ScheduledAction action);
    InputOutcome Execute(ScheduledAction action, CancellationToken cancellationToken);
    /// <summary>到点前 <see cref="ActionScheduler.PrimeLeadMs"/> 调用一次：做执行前必须的准备（如把游戏窗口切到前台），到点时只剩发送输入。</summary>
    void Prime(ScheduledAction action) { }
    void Forget(string actionId) { }
    void CancelPendingForStop(string sessionId) => ReleaseAll();
    void ReleaseSession(string sessionId) => ReleaseAll();
    void ReleaseAll();
}
