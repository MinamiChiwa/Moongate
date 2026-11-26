using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

/// <summary>钓鱼快照的读取情况：桌面没连上后端、已连上但还没读到、后端没有这个接口（旧宿主）、已读到。</summary>
public enum FishingRead { Offline, NotRead, Unsupported, Loaded }

/// <summary>
/// 一次钓鱼运行的当前事实，对应契约 <see cref="FishingRun"/>。<see cref="ElapsedSeconds"/> 在首次抛竿前为空（未开始计时，不是 0）；
/// <see cref="StopReason"/> 是结束原因码（duration-reached、user-stop、backpack-full、cancelled、emergency-stop 或失败码），运行中为空；
/// <see cref="PoseLeft"/> 只有观察到离开钓鱼姿态的画面证据才是 Observed。文字由界面按语言生成。
/// </summary>
public sealed record FishingRunView(string RunId, string OperationId, string Phase, bool Active, bool ClockStarted, double? ElapsedSeconds,
    int? TargetSeconds, FishingRounds Rounds, int ConsecutiveRoundsWithoutHook, string? Reason, string? StopReason,
    FishingPoseState? PoseLeft, DateTimeOffset UpdatedAt)
{
    public static FishingRunView From(FishingRun run) => new(run.Id, run.OperationId, run.Phase,
        run.Phase is not ("succeeded" or "failed" or "interrupted" or "cancelled"), run.ClockStarted, run.ElapsedSeconds, run.DurationSeconds,
        run.Rounds, run.ConsecutiveRoundsWithoutHook, run.Reason, run.Stop?.Reason, run.Stop?.PoseLeft.KnownValue, run.UpdatedAt);
}

/// <summary>
/// 钓鱼能力与当前（或最近一次）运行。<see cref="Available"/> 为假时 <see cref="UnavailableReason"/> 是后端给出的原因码，
/// 入口显示为停用并写明原因；没有读到快照时两者都为空，界面显示“未读取”。
/// </summary>
public sealed record FishingView(FishingRead Read, bool Available, string? UnavailableReason, FishingRunView? Run)
{
    public static FishingView Offline { get; } = new(FishingRead.Offline, false, null, null);
    public static FishingView NotRead { get; } = Offline with { Read = FishingRead.NotRead };
    public static FishingView Unsupported { get; } = Offline with { Read = FishingRead.Unsupported };

    public static FishingView From(FishingSnapshot snapshot) => new(FishingRead.Loaded,
        snapshot.Capability.Status.KnownValue == CapabilityStatus.Available,
        snapshot.Capability.Status.KnownValue == CapabilityStatus.Available ? null : snapshot.Capability.ReasonCode,
        snapshot.Run is null ? null : FishingRunView.From(snapshot.Run));

    /// <summary>当前有进行中的运行。</summary>
    public bool Running => Run is { Active: true };
}
