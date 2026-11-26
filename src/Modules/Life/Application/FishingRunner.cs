using MabiTool.Contracts;
using MabiTool.Modules.Life.Domain;

namespace MabiTool.Modules.Life.Application;

/// <summary>输入没有发出（<see cref="Rejected"/>，可重试）、条件已失效不能继续（<see cref="Fatal"/>），或发出过程中失败、效果未知（<see cref="Unknown"/>）。</summary>
public enum FishingInputFailure { Rejected, Fatal, Unknown }

public sealed class FishingInputException(string code, FishingInputFailure failure, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
    public FishingInputFailure Failure { get; } = failure;
}

/// <summary>识别来源失效：Worker 退出、断连、窗口变化，或超过期限没有帧。</summary>
public sealed class FishingVisionException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

public interface IFishingVision
{
    /// <summary>取下一帧观察；<paramref name="timeout"/> 内没有帧以 <c>capture.sourceFailed</c> 失败。</summary>
    ValueTask<FishingObservation> NextAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IFishingInput
{
    /// <summary>
    /// 分发一个动作并等到它完成。抛竿的 W/S、空格是两次独立分发，中间等待 300 毫秒并可被取消：
    /// 取消发生在两次之间时第二次不会发出。取消以 <see cref="OperationCanceledException"/> 报告。
    /// </summary>
    Task PerformAsync(FishingAction action, CancellationToken cancellationToken);

    /// <summary>续期输入租约；租约已被撤销或绑定失效时抛出 <see cref="FishingInputException"/>（Fatal）。</summary>
    void RenewLease();
}

/// <summary>运行的最终结果；<see cref="InputDispatched"/> 表示本次运行向游戏发出过输入，<see cref="EffectUnknown"/> 表示最后一次输入效果未知。</summary>
public sealed record FishingOutcome(FishingEnd End, string StopReason, string? ProblemCode, OpenEnum<FishingPoseState> PoseLeft,
    bool InputDispatched, bool EffectUnknown, FishingProgress Final);

public sealed record FishingRunnerOptions
{
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan LeaseRenewal { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>进度发布的最长间隔；相同进度之间只在已用时长变化时按此间隔发布。</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(5);
    public FishingOptions Machine { get; init; } = new();
}

/// <summary>
/// 观察、决策、输入的异步循环。取消、识别失效、租约失效与输入失败都在这里收口为一个 <see cref="FishingOutcome"/>：
/// 取消不再发送任何输入，输入失败不重放；释放按下的输入与租约由宿主在拿到结果前完成。
/// </summary>
public sealed class FishingRunner(FishingPlan plan, IFishingVision vision, IFishingInput input, TimeProvider clock,
    Action<FishingProgress> publish, FishingRunnerOptions? options = null)
{
    private readonly FishingRunnerOptions settings = options ?? new();
    private volatile bool stopRequested;

    /// <summary>请求优雅停止：离开钓鱼姿态后正常结束。线程安全。</summary>
    public void RequestStop() => stopRequested = true;

    public async Task<FishingOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var machine = new FishingMachine(plan, settings.Machine);
        var dispatched = false;
        var unknown = false;
        var first = true;
        var renewedAt = clock.GetTimestamp();
        FishingProgress? published = null;
        long publishedAt = 0;
        void Publish(bool force)
        {
            var progress = machine.Progress;
            var due = clock.GetElapsedTime(publishedAt) >= settings.ProgressInterval;
            if (!force && published is not null && !due &&
                progress with { ElapsedSeconds = published.ElapsedSeconds } == published) return;
            published = progress; publishedAt = clock.GetTimestamp();
            publish(progress);
        }
        FishingOutcome Outcome(FishingEnd end, string stopReason, string? problem, FishingPoseState pose) =>
            new(end, stopReason, problem, pose, dispatched, unknown, machine.Progress);

        Publish(true);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopRequested) machine.RequestStop();
                FishingObservation observation;
                try { observation = await vision.NextAsync(first ? settings.FirstFrameTimeout : settings.FrameTimeout, cancellationToken).ConfigureAwait(false); }
                catch (FishingVisionException error) { return Outcome(FishingEnd.Interrupted, error.Code, error.Code, FishingPoseState.Unconfirmed); }
                first = false;
                // 取消在等待帧期间到达时，已取得的这一帧也不再决策，避免取消之后多发一个动作。
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(renewedAt) >= settings.LeaseRenewal)
                {
                    input.RenewLease();
                    renewedAt = clock.GetTimestamp();
                }
                var step = machine.Step(observation, Seconds());
                if (step.Action is { } action)
                {
                    try
                    {
                        await input.PerformAsync(action, cancellationToken).ConfigureAwait(false);
                        dispatched = true; unknown = false;
                        machine.Dispatched(action, Seconds());
                    }
                    catch (FishingInputException error) when (error.Failure == FishingInputFailure.Rejected)
                    { machine.Rejected(action); }
                }
                Publish(false);
                if (step.Terminal is { } terminal)
                {
                    Publish(true);
                    return new(terminal.End, terminal.StopReason, terminal.ProblemCode, terminal.PoseLeft, dispatched, unknown, machine.Progress);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Outcome(FishingEnd.Cancelled, "cancelled", null, FishingPoseState.Unconfirmed);
        }
        catch (FishingInputException error)
        {
            unknown = error.Failure == FishingInputFailure.Unknown;
            return Outcome(FishingEnd.Interrupted, error.Code, error.Code, FishingPoseState.Unconfirmed);
        }
    }

    private double Seconds() => clock.GetTimestamp() / (double)clock.TimestampFrequency;
}
