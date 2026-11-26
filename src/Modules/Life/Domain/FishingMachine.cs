// 移植自 MabiAgent 82bc0ef 的 dotnet/MabiAgent.Workflows/IdleFishingRunner.cs（原地专注钓鱼部分）与
// dotnet/MabiAgent.Windows/ScreenFishingPort.cs 的轮次推导（Decorate、ObserveFrame）（MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：原实现把状态保存在 JSON 文件、与导航/背包/让位调度和屏幕端口缠在一起；这里去掉选点、导航、背包整理与让位，
// 状态机改为纯同步、时间由调用方传入，识别由独立 Worker 提供类型化观察，输入由宿主按租约分发。

using MabiTool.Contracts;

namespace MabiTool.Modules.Life.Domain;

public enum FishingActionKind { PrepareMode, ConfirmMode, CancelMode, Dismount, Cast, Reel, Leave }

/// <summary>抛竿前的轻移方向：W 或 S 按 60 毫秒；<see cref="None"/> 表示收竿后两秒窗口内原地只按空格。</summary>
public enum FishingKey { None, W, S }

/// <param name="Point">点击类动作的客户区坐标（物理像素）。</param>
/// <param name="Move">仅 Cast 使用。</param>
public sealed record FishingAction(FishingActionKind Kind, VisionPoint? Point = null, FishingKey Move = FishingKey.None);

public enum FishingEnd { Succeeded, Failed, Interrupted, Cancelled }

/// <param name="StopReason">结束原因：duration-reached、user-stop、backpack-full 等。</param>
/// <param name="ProblemCode">失败或中断的公共错误码；成功与取消为空。</param>
public sealed record FishingTerminal(FishingEnd End, string StopReason, string? ProblemCode, OpenEnum<FishingPoseState> PoseLeft);

public sealed record FishingStep(FishingAction? Action = null, FishingTerminal? Terminal = null)
{
    public static FishingStep None { get; } = new();
}

public sealed record FishingPlan(int? DurationSeconds);

public sealed record FishingProgress(string Phase, bool ClockStarted, double? ElapsedSeconds, FishingRounds Rounds,
    int ConsecutiveRoundsWithoutHook, string? Reason);

/// <summary>计时与阈值，默认值来自 MabiAgent 的实机校准（见 design）。</summary>
public sealed record FishingOptions
{
    public double CastObservationSeconds { get; init; } = 20;
    public double ActionSeconds { get; init; } = 15;
    public double ReboundThreshold { get; init; } = .12;
    public double LowWatermark { get; init; } = .30;
    public double GaugeSampleSeconds { get; init; } = .1;
    public double ReelAnimationSeconds { get; init; } = 3;
    public double RecastWindowSeconds { get; init; } = 2;
    public double MoveGuardSeconds { get; init; } = .25;
    public double ModeProbeSeconds { get; init; } = .5;
    public double DismountSettleSeconds { get; init; } = 1;
    public double DialogRepeatSeconds { get; init; } = 1;
    public double InterfaceUnrecognizedSeconds { get; init; } = 45;
    public double LeaveObservationSeconds { get; init; } = 5;
    public int CleanFramesAfterLeave { get; init; } = 3;
    public int PreflightRejectionLimit { get; init; } = 3;
    public int HookMissingRoundLimit { get; init; } = 5;
    /// <summary>模式按钮（1095,777,1145,826）与下坐骑图标控制框（1093,773,1147,827）的中心。</summary>
    public VisionPoint ModeButton { get; init; } = new(1120, 801);
    public VisionPoint DismountButton { get; init; } = new(1120, 800);
}

/// <summary>
/// 原地专注钓鱼状态机。每帧观察经 <see cref="Step"/> 产生至多一个动作或终态；动作的分发由调用方完成后回报
/// <see cref="Dispatched"/> 或 <see cref="Rejected"/>。意图在 Step 返回前已记录（抛竿、收竿、弹窗确认的待确认记录），
/// 因此分发失败不会留下“已抛竿”的假象，也不会重放：被拒绝的动作只会由下一帧的新决策重新产生。
/// 时间一律是调用方的单调秒；状态机不读时钟、不做 IO。
/// </summary>
public sealed class FishingMachine
{
    private enum Page { Unknown, Scene, Fishing, Dialog, Mounted }

    private sealed record Pending(FishingActionKind Kind, ulong FrameId, double At, double Deadline);

    private sealed record Frame(Page Page, bool Stable, bool ModeConfirm, bool LeisureConfirm, bool Mounted, bool Compass, bool Cast,
        bool RoundActive, bool Hook, double? Gauge, bool BackpackFull, VisionPoint? ConfirmPoint, VisionPoint? CancelPoint, ulong FrameId, bool SceneVisible);

    private sealed class Stopping(FishingTerminal template)
    {
        public FishingTerminal Template { get; } = template;
        public bool PoseEvidence { get; set; }
        public double? LeaveAt { get; set; }
        public int LeaveAttempts { get; set; }
        public int CleanFrames { get; set; }
    }

    private readonly FishingOptions options;
    private readonly double target;
    private int submitted, reeled, depleted, retries, hookMissingRounds, rejections;
    private bool clockStarted;
    private double elapsed;
    private double? lastAt;
    private string phase = "starting";
    private string? reason;
    private Pending? pending;
    private bool roundSeen, hookSeenInRound, reelIssuedInRound;
    private double? gaugeMin, sampleAt;
    private bool roundObserved, reelIssued;
    private double? clearSince, recastUntil;
    private bool modePrepared;
    private double? modeProbeAt, dismountAt, lastMoveAt, dialogActionAt, unknownSince;
    private FishingKey nextDirection = FishingKey.W;
    private Page? previousPage;
    private volatile bool stopRequested;
    private Stopping? stopping;
    private FishingTerminal? terminal;

    public FishingMachine(FishingPlan plan, FishingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.DurationSeconds is <= 0) throw new ArgumentOutOfRangeException(nameof(plan));
        this.options = options ?? new();
        target = plan.DurationSeconds ?? double.PositiveInfinity;
    }

    public bool Finished => terminal is not null;

    public FishingProgress Progress => new(phase, clockStarted, clockStarted ? elapsed : null,
        new(submitted, reeled, depleted, retries), hookMissingRounds, reason);

    /// <summary>请求优雅停止：下一帧起离开钓鱼姿态并正常结束。线程安全。</summary>
    public void RequestStop() => stopRequested = true;

    public FishingStep Step(FishingObservation observation, double now)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (terminal is not null) return new(Terminal: terminal);
        var frame = Derive(observation, now);
        TrackClock(now);

        if (stopping is not null)
        {
            unknownSince = null;
            return StepStopping(frame, now);
        }
        if (frame.Page == Page.Unknown)
        {
            unknownSince ??= now;
            if (now - unknownSince >= options.InterfaceUnrecognizedSeconds)
                return Finish(FishingEnd.Failed, "interface-unrecognized", LifeFishingErrors.InterfaceUnrecognized, FishingPoseState.Unconfirmed);
            return FishingStep.None;
        }
        unknownSince = null;
        if (!frame.Stable) return FishingStep.None;

        if (frame.BackpackFull) return BeginStop(FishingEnd.Failed, "backpack-full", LifeFishingErrors.BackpackFull, frame, now);
        if (elapsed >= target || stopRequested)
            return BeginStop(FishingEnd.Succeeded, stopRequested ? "user-stop" : "duration-reached", null, frame, now);

        // 模式弹窗可以由初始悠闲状态、模式准备点击或首次抛竿打开，先处理它再等待抛竿回执。
        if (frame.ModeConfirm && pending?.Kind != FishingActionKind.ConfirmMode)
        {
            pending = null; roundSeen = false; ResetRoundSamples();
            return Dialog(FishingActionKind.ConfirmMode, frame.ConfirmPoint, frame, now, pend: true);
        }
        if (frame.LeisureConfirm) return Dialog(FishingActionKind.CancelMode, frame.CancelPoint, frame, now, pend: false);
        if (!Reconcile(frame, now)) return FishingStep.None;

        if (roundSeen && RoundEnded(frame))
        {
            if (FinishRound() is { } selfCheck) return BeginStop(selfCheck.End, selfCheck.StopReason, selfCheck.ProblemCode, frame, now);
        }
        if (!roundSeen && !frame.RoundActive && pending is null && !frame.Mounted)
        {
            if (!modePrepared)
            {
                modeProbeAt = now; phase = "preparing-mode";
                return new(new FishingAction(FishingActionKind.PrepareMode, options.ModeButton));
            }
            if (modeProbeAt is { } probe && now - probe < options.ModeProbeSeconds) return FishingStep.None;
            modeProbeAt = null;
        }
        if (!roundSeen && pending is null)
        {
            if (frame.Mounted)
            {
                if (dismountAt is null)
                {
                    dismountAt = now; phase = "dismounting";
                    return new(new FishingAction(FishingActionKind.Dismount, options.DismountButton));
                }
                if (now - dismountAt >= options.ActionSeconds)
                    return Finish(FishingEnd.Failed, "dismount-not-observed", LifeFishingErrors.DismountNotObserved, FishingPoseState.NotNeeded);
                return FishingStep.None;
            }
            // 坐骑图标可能先于下坐骑动画结束消失，动画结束前不开始移动与抛竿。
            if (dismountAt is { } settled && now - settled < options.DismountSettleSeconds) return FishingStep.None;
            dismountAt = null;
        }
        if (frame.RoundActive)
        {
            if (!roundSeen)
            {
                ResetRoundSamples();
                roundSeen = true; submitted++; clockStarted = true; lastAt ??= now;
            }
            phase = "fishing";
            if (frame.Hook) hookSeenInRound = true;
            return frame.Hook || frame.Gauge is not null ? SampleGauge(frame, now) : FishingStep.None;
        }
        if (roundSeen) return FishingStep.None;
        if (rejections >= options.PreflightRejectionLimit)
            return Finish(FishingEnd.Failed, "cast-preflight-retry-limit", LifeFishingErrors.CastPreflightRetryLimit, FishingPoseState.NotNeeded);
        if (frame.Cast) return StartCast(frame, FishingKey.None, now);
        if (frame.Compass && (lastMoveAt is null || now - lastMoveAt >= options.MoveGuardSeconds))
        {
            var direction = nextDirection;
            nextDirection = direction == FishingKey.W ? FishingKey.S : FishingKey.W;
            lastMoveAt = now; phase = "recasting";
            return StartCast(frame, direction, now);
        }
        return FishingStep.None;
    }

    /// <summary>动作已发出。</summary>
    public void Dispatched(FishingAction action, double now)
    {
        switch (action.Kind)
        {
            case FishingActionKind.PrepareMode: modePrepared = true; break;
            case FishingActionKind.Cast:
                InvalidatePreparation(); ResetRoundSamples();
                clockStarted = true; lastAt ??= now; phase = "waiting-bite";
                break;
            case FishingActionKind.Reel:
                reeled++; reelIssuedInRound = true; reelIssued = true; clearSince = null; phase = "reeling";
                break;
            case FishingActionKind.CancelMode: modePrepared = true; break;
            case FishingActionKind.Leave when stopping is { } leaving: leaving.LeaveAt = now; break;
        }
    }

    /// <summary>动作在发出之前被拒绝（租约、几何、触点条件）：撤销待确认记录，不当作已发出。</summary>
    public void Rejected(FishingAction action)
    {
        switch (action.Kind)
        {
            case FishingActionKind.Cast:
                pending = null; phase = "recasting";
                reason = rejections >= options.PreflightRejectionLimit - 1 ? "cast_preflight_retry_limit" : "target_changed_before_input";
                rejections++;
                break;
            case FishingActionKind.Dismount: dismountAt = null; break;
            case FishingActionKind.PrepareMode: modeProbeAt = null; break;
            case FishingActionKind.Leave when stopping is { } leaving: leaving.LeaveAt = null; break;
            default: pending = null; reason = "target_changed_before_input"; break;
        }
    }

    private FishingStep Dialog(FishingActionKind kind, VisionPoint? point, Frame frame, double now, bool pend)
    {
        if (dialogActionAt is { } at && now - at < options.DialogRepeatSeconds) return FishingStep.None;
        dialogActionAt = now;
        if (pend) pending = new(kind, frame.FrameId, now, now + options.ActionSeconds);
        phase = "preparing-mode";
        return new(new FishingAction(kind, point));
    }

    private FishingStep StartCast(Frame frame, FishingKey move, double now)
    {
        phase = "casting";
        pending = new(FishingActionKind.Cast, frame.FrameId, now, now + options.CastObservationSeconds);
        return new(new FishingAction(FishingActionKind.Cast, null, move));
    }

    private FishingStep SampleGauge(Frame frame, double now)
    {
        if (frame.Gauge is not { } gauge || !double.IsFinite(gauge) || gauge is < 0 or > 1) return FishingStep.None;
        var previousMin = gaugeMin;
        if (previousMin is null || gauge < previousMin) gaugeMin = gauge;
        if (sampleAt is { } last && now - last < options.GaugeSampleSeconds) return FishingStep.None;
        sampleAt = now;
        if (previousMin is { } low && low <= options.LowWatermark && gauge - low >= options.ReboundThreshold)
        {
            pending = new(FishingActionKind.Reel, frame.FrameId, now, now + options.ActionSeconds);
            phase = "reeling";
            return new(new FishingAction(FishingActionKind.Reel));
        }
        return FishingStep.None;
    }

    /// <summary>对账待确认动作；返回 false 表示仍在等待回执，本帧不再做其他决策。</summary>
    private bool Reconcile(Frame frame, double now)
    {
        if (pending is not { } wait) return true;
        if (frame.FrameId <= wait.FrameId) return false;
        var confirmed = false;
        switch (wait.Kind)
        {
            case FishingActionKind.Cast:
                if (frame.RoundActive)
                {
                    ResetRoundSamples();
                    submitted++; roundSeen = true; phase = "fishing"; confirmed = true;
                }
                break;
            case FishingActionKind.ConfirmMode:
                confirmed = !frame.ModeConfirm && (frame.Page == Page.Fishing || frame.Compass || frame.Cast);
                break;
            case FishingActionKind.Reel:
                if (RoundEnded(frame))
                {
                    // 自检失败在这里不能直接返回终态；留给下一帧的停止流程（轮次已结束，不需要离开姿态）。
                    if (FinishRound() is { } selfCheck) { stopping = new(selfCheck); phase = "leaving"; }
                    return true;
                }
                break;
        }
        if (confirmed) { pending = null; reason = null; return true; }
        // 观察期限到达：优先看本帧是否已有上钩或进度条（上面已处理），仍无反应才重试或恢复。
        if (now >= wait.Deadline)
        {
            if (wait.Kind == FishingActionKind.Cast)
            {
                if (!frame.RoundActive && !frame.Hook && frame.Gauge is null && !frame.ModeConfirm)
                {
                    retries++; pending = null; phase = "recasting"; reason = "cast_retry_no_response";
                    return true;
                }
            }
            else
            {
                pending = null; phase = frame.RoundActive ? "fishing" : "recasting"; gaugeMin = null; sampleAt = null;
                roundSeen = frame.RoundActive; reason = (wait.Kind == FishingActionKind.Reel ? "reel" : "confirm_mode") + "_observation_timeout";
                return true;
            }
        }
        return false;
    }

    private static bool RoundEnded(Frame frame) =>
        !frame.RoundActive && !frame.ModeConfirm && !frame.Hook && frame.Gauge is null && (frame.Compass || frame.Cast);

    /// <summary>结束一轮并记账；连续多轮没有识别到上钩图标时返回自检失败终态模板。</summary>
    private FishingTerminal? FinishRound()
    {
        if (!reelIssuedInRound && gaugeMin is not null) depleted++;
        hookMissingRounds = hookSeenInRound ? 0 : hookMissingRounds + 1;
        reelIssuedInRound = false;
        ResetRoundSamples();
        pending = null; roundSeen = false; rejections = 0; reason = null; phase = "recasting";
        return hookMissingRounds >= options.HookMissingRoundLimit
            ? new(FishingEnd.Failed, "hook-not-recognized", LifeFishingErrors.HookNotRecognized, FishingPoseState.NotNeeded) : null;
    }

    private void ResetRoundSamples() { gaugeMin = null; sampleAt = null; hookSeenInRound = false; }

    private void InvalidatePreparation()
    {
        clearSince = recastUntil = null;
        roundObserved = reelIssued = false;
    }

    private void TrackClock(double now)
    {
        if (!clockStarted || stopping is not null) { lastAt = null; return; }
        if (lastAt is { } previous) elapsed = Math.Min(target, elapsed + Math.Max(0, now - previous));
        lastAt = now;
    }

    /// <summary>
    /// 把一帧观察推导成钓鱼帧：上钩图标或有效进度条确立一轮；信号消失后三秒内仍算轮次进行中（收竿动画，场景 HUD 暂时隐藏），
    /// 动画结束后再有两秒窗口可以原地空格续抛。
    /// </summary>
    private Frame Derive(FishingObservation observation, double now)
    {
        Frame Build(Page page, bool modeConfirm = false, bool leisure = false, bool mounted = false, bool compass = false, bool cast = false,
            bool active = false, bool hook = false, double? gauge = null, VisionPoint? confirm = null, VisionPoint? cancel = null, bool scene = false)
        {
            var stable = previousPage == page;
            previousPage = page;
            return new(page, stable, modeConfirm, leisure, mounted, compass, cast, active, hook, gauge, observation.BackpackFull, confirm, cancel, observation.FrameId.Value, scene);
        }
        if (observation.Dialog is { } dialog)
        {
            modePrepared = true;
            var focused = dialog.Kind == "focused";
            return Build(Page.Dialog, modeConfirm: focused, leisure: !focused, active: focused, confirm: dialog.Confirm, cancel: dialog.Cancel);
        }
        if (observation.DismountVisible)
        {
            InvalidatePreparation();
            return Build(Page.Mounted, mounted: true);
        }
        var hook = observation.Hook is not null;
        double? gauge = observation.Gauge?.Value;
        var signal = hook || gauge is not null;
        if (signal && !reelIssued && !roundObserved) roundObserved = true;
        if (signal) clearSince = null;
        else if (roundObserved || reelIssued) clearSince ??= now;
        var tracked = roundObserved || reelIssued;
        var active = signal || tracked && (clearSince is null || now - clearSince < options.ReelAnimationSeconds);
        if (signal) recastUntil = null;
        else if (!active && tracked && clearSince is { } ended) recastUntil ??= ended + options.ReelAnimationSeconds + options.RecastWindowSeconds;
        var compass = observation.SceneVisible && !active;
        var cast = compass && recastUntil is { } until && now < until;
        // 场景恢复且动画窗口已过：本轮的轨道记忆结束（recastUntil 保留到续抛窗口结束或下次抛竿）。
        if (!active && observation.SceneVisible) { reelIssued = false; roundObserved = false; clearSince = null; }
        var page = active ? Page.Fishing : observation.SceneVisible ? Page.Scene : Page.Unknown;
        return Build(page, compass: compass, cast: cast, active: active, hook: hook, gauge: gauge, scene: observation.SceneVisible);
    }

    private FishingStep BeginStop(FishingEnd end, string stopReason, string? problem, Frame frame, double now)
    {
        stopping = new(new(end, stopReason, problem, FishingPoseState.NotNeeded));
        phase = "leaving";
        return StepStopping(frame, now);
    }

    /// <summary>
    /// 离开钓鱼姿态：先取消弹窗；处于钓鱼姿态时按一次离开键，随后观察。连续三帧场景 HUD 可见且没有上钩图标、进度条与弹窗（收竿动画期间 HUD 隐藏，不会误判），
    /// 并且离开前有轮次画面作证据，才记为已观察到离开；五秒内没有这样的画面则记为未确认。
    /// </summary>
    private FishingStep StepStopping(Frame frame, double now)
    {
        var stop = stopping!;
        if (frame.ModeConfirm || frame.LeisureConfirm)
            return Dialog(FishingActionKind.CancelMode, frame.CancelPoint, frame, now, pend: false);
        if (stop.LeaveAt is null)
        {
            var inPose = frame.Page == Page.Fishing || roundSeen || pending is not null;
            if (!inPose || stop.LeaveAttempts >= 3)
                return Finish(stop.Template, inPose ? FishingPoseState.Unconfirmed : FishingPoseState.NotNeeded);
            stop.PoseEvidence = frame.RoundActive || frame.Hook || frame.Gauge is not null;
            stop.LeaveAttempts++;
            stop.LeaveAt = now;
            return new(new FishingAction(FishingActionKind.Leave));
        }
        stop.CleanFrames = frame.SceneVisible && !frame.Hook && frame.Gauge is null ? stop.CleanFrames + 1 : 0;
        if (stop.CleanFrames >= options.CleanFramesAfterLeave)
            return Finish(stop.Template, stop.PoseEvidence ? FishingPoseState.Observed : FishingPoseState.Unconfirmed);
        if (now - stop.LeaveAt >= options.LeaveObservationSeconds) return Finish(stop.Template, FishingPoseState.Unconfirmed);
        return FishingStep.None;
    }

    private FishingStep Finish(FishingEnd end, string stopReason, string? problem, FishingPoseState pose) =>
        Finish(new FishingTerminal(end, stopReason, problem, pose), pose);

    private FishingStep Finish(FishingTerminal template, FishingPoseState pose)
    {
        terminal = template with { PoseLeft = pose };
        phase = template.End.ToString().ToLowerInvariant();
        return new(Terminal: terminal);
    }
}
