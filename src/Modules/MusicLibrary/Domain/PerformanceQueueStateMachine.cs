using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.MusicLibrary.Domain;

/// <summary>纯队列决策层；只返回下一步意图，不调用游戏、文件或网络。</summary>
public sealed class PerformanceQueueStateMachine
{
    private readonly Random random;
    public PerformanceQueueStateMachine(int seed = 1) => random = new(seed);

    public PerformanceQueueState Create(string id, string owner, string mode, string gameSessionId,
        IReadOnlyList<PlaylistItem> items, bool shuffle, PerformanceRepeatMode repeat,
        double gapSeconds = 2, double advanceMargin = 2)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(gameSessionId))
            throw new OperationException("request.invalid", "演奏会话身份不能为空");
        if (gapSeconds is < 0 or > 60 || advanceMargin is < 0 or > 30)
            throw new OperationException("request.invalid", "演奏等待或提前切换参数超出范围");
        var order = Enumerable.Range(0, items.Count).ToArray();
        if (shuffle) Shuffle(order);
        return new(id, owner, mode, gameSessionId,
            items.Select((item, index) => new PerformanceQueueItem(item, index)).ToArray(), order,
            order.Length == 0 ? -1 : 0, shuffle, repeat, gapSeconds, advanceMargin,
            PerformancePhase.Idle, 0, new(0), null, null, null, null);
    }

    public PerformanceQueueState Arm(PerformanceQueueState state)
    {
        Ensure(state, PerformancePhase.Idle, PerformancePhase.Stopped, PerformancePhase.Failed, PerformancePhase.Cancelled);
        if (state.Order.Count == 0) throw new OperationException("queue.empty", "演奏队列为空");
        return Next(state, PerformancePhase.Armed, null, null) with { Generation = NextGeneration(state) };
    }

    public PerformanceQueueState Dispatch(PerformanceQueueState state, DateTimeOffset now)
    {
        Ensure(state, PerformancePhase.Armed, PerformancePhase.Waiting);
        var item = Current(state);
        return state with { Phase = PerformancePhase.Executing, CurrentScoreId = item.Item.ScoreId,
            CurrentStartedAt = now, GapUntil = null, Generation = NextGeneration(state) };
    }

    public PerformanceQueueState Playing(PerformanceQueueState state)
    {
        Ensure(state, PerformancePhase.Executing, PerformancePhase.Performing);
        return state with { Phase = PerformancePhase.Performing, Generation = NextGeneration(state) };
    }

    public PerformanceQueueState ObserveStopped(PerformanceQueueState state, PerformanceStopReason reason, DateTimeOffset now)
    {
        Ensure(state, PerformancePhase.Executing, PerformancePhase.Performing, PerformancePhase.Stopping);
        if (reason is PerformanceStopReason.ManualStop or PerformanceStopReason.GameStopped or PerformanceStopReason.ControlFailure or PerformanceStopReason.ObservationLost)
            return state with { Phase = PerformancePhase.Stopped, StopReason = reason, GapUntil = null, CurrentStartedAt = null,
                Generation = NextGeneration(state) };
        return AdvanceAfterNaturalEnd(state, now);
    }

    public PerformanceQueueState MarkFailure(PerformanceQueueState state, DateTimeOffset now)
    {
        Ensure(state, PerformancePhase.Executing, PerformancePhase.Performing);
        var failures = checked(state.ConsecutiveFailures + 1);
        if (failures >= Math.Min(3, Math.Max(1, state.Order.Count)))
            return state with { Phase = PerformancePhase.Failed, StopReason = PerformanceStopReason.ControlFailure,
                ConsecutiveFailures = 0, GapUntil = null, Generation = NextGeneration(state) };
        return AdvanceAfterFailure(state, now, failures);
    }

    public PerformanceQueueState Stop(PerformanceQueueState state, PerformanceStopReason reason)
    {
        if (state.Phase is PerformancePhase.Stopped or PerformancePhase.Cancelled or PerformancePhase.Failed) return state;
        return state with { Phase = reason == PerformanceStopReason.Cancelled ? PerformancePhase.Cancelled : PerformancePhase.Stopping,
            StopReason = reason, GapUntil = null, Generation = NextGeneration(state) };
    }

    public PerformanceQueueState Advance(PerformanceQueueState state, int direction, DateTimeOffset now)
    {
        Ensure(state, PerformancePhase.Performing, PerformancePhase.Executing, PerformancePhase.Waiting, PerformancePhase.Armed);
        if (direction == 0 && state.Repeat == PerformanceRepeatMode.One) return Dispatch(state with { Phase = PerformancePhase.Armed }, now);
        if (direction is < -1 or > 1) throw new OperationException("request.invalid", "演奏方向无效");
        var position = state.CurrentPosition + (direction == 0 ? 1 : direction);
        if (position >= state.Order.Count)
        {
            if (state.Repeat != PerformanceRepeatMode.All)
                return state with { Phase = PerformancePhase.Stopped, StopReason = PerformanceStopReason.QueueExhausted,
                    CurrentStartedAt = null, GapUntil = null, Generation = NextGeneration(state) };
            position = 0;
        }
        if (position < 0) position = state.Repeat == PerformanceRepeatMode.All ? state.Order.Count - 1 : 0;
        var next = state with { CurrentPosition = position, Phase = PerformancePhase.Waiting,
            GapUntil = state.GapSeconds == 0 ? now : now.AddSeconds(state.GapSeconds), CurrentStartedAt = null,
            CurrentScoreId = null, StopReason = null, Generation = NextGeneration(state) };
        return next.GapSeconds == 0 ? Dispatch(next with { Phase = PerformancePhase.Armed }, now) : next;
    }

    public PerformanceQueueState ApplyQueueEdit(PerformanceQueueState state, IReadOnlyList<PlaylistItem> items)
    {
        if (state.Phase is PerformancePhase.Executing or PerformancePhase.Performing)
            throw new OperationException("performance.queueBusy", "演奏中不能直接替换队列");
        var order = Enumerable.Range(0, items.Count).ToArray();
        if (state.Shuffle) Shuffle(order);
        return state with { Items = items.Select((item, index) => new PerformanceQueueItem(item, index)).ToArray(),
            Order = order, CurrentPosition = order.Length == 0 ? -1 : 0, Phase = PerformancePhase.Idle,
            CurrentScoreId = null, CurrentStartedAt = null, GapUntil = null, StopReason = null, Generation = NextGeneration(state) };
    }

    private PerformanceQueueState AdvanceAfterNaturalEnd(PerformanceQueueState state, DateTimeOffset now) => Advance(state with { Phase = PerformancePhase.Waiting, ConsecutiveFailures = 0 }, 0, now);
    private PerformanceQueueState AdvanceAfterFailure(PerformanceQueueState state, DateTimeOffset now, int failures) => Advance(state with { Phase = PerformancePhase.Waiting, ConsecutiveFailures = failures }, 0, now);
    private PerformanceQueueItem Current(PerformanceQueueState state) => state.CurrentPosition < 0 || state.CurrentPosition >= state.Order.Count
        ? throw new OperationException("queue.empty", "当前队列没有曲目") : state.Items[state.Order[state.CurrentPosition]];
    private static void Ensure(PerformanceQueueState state, params PerformancePhase[] allowed)
    { if (!allowed.Contains(state.Phase)) throw new OperationException("performance.invalidState", "演奏状态不允许此操作"); }
    private UInt64String NextGeneration(PerformanceQueueState state) => new(checked(state.Generation.Value + 1));
    private void Shuffle(int[] values) { for (var i = values.Length - 1; i > 0; i--) { var j = random.Next(i + 1); (values[i], values[j]) = (values[j], values[i]); } }
    private static PerformanceQueueState Next(PerformanceQueueState state, PerformancePhase phase, DateTimeOffset? started, DateTimeOffset? gap) => state with { Phase = phase, CurrentScoreId = null, CurrentStartedAt = started, GapUntil = gap, StopReason = null };
}
