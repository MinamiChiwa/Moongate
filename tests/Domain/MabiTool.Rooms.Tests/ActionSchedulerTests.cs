using MabiTool.Modules.Rooms.Domain;
using Xunit;

namespace MabiTool.Rooms.Tests;

public sealed class ActionSchedulerTests
{
    [Fact]
    public async Task PreparedActionRunsOnlyAfterCommitAndCancellationPreventsInput()
    {
        var clock = new FakeClock(100);
        var roomClock = new RoomClock(clock);
        Assert.True(roomClock.AddSample(100, 100, 100, 100));
        var input = new RecordingInput();
        using var scheduler = new ActionScheduler(clock, roomClock, input);
        scheduler.ChangeEpoch("epoch-1");
        Assert.True(roomClock.AddSample(100, 100, 100, 100));
        var action = new ScheduledAction("action-1", "session-1", "start", 10_000, "epoch-1");
        var receipt = new TaskCompletionSource<ActionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Receipt += value => receipt.TrySetResult(value);

        Assert.True(scheduler.Prepare(action).Ready);
        Assert.False(scheduler.Prepare(action).Ready);
        Assert.Empty(input.Executed);
        Assert.True(scheduler.Commit(action));
        Assert.False(scheduler.Commit(action));

        scheduler.CancelAction(action.Id);

        var result = await receipt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("cancelled", result.Status);
        Assert.Empty(input.Executed);
        Assert.Contains(action.Id, input.Forgotten);
    }

    [Fact]
    public void EpochChangeCancelsPreparedWorkAndResetsClockSamples()
    {
        var clock = new FakeClock(100);
        var roomClock = new RoomClock(clock);
        Assert.True(roomClock.AddSample(100, 100, 100, 100));
        var input = new RecordingInput();
        using var scheduler = new ActionScheduler(clock, roomClock, input);
        scheduler.ChangeEpoch("epoch-1");
        Assert.True(roomClock.AddSample(100, 100, 100, 100));
        var action = new ScheduledAction("action-2", "session-1", "start", 10_000, "epoch-1");
        Assert.True(scheduler.Prepare(action).Ready);

        scheduler.ChangeEpoch("epoch-2");

        Assert.False(scheduler.Commit(action));
        Assert.Null(roomClock.Estimate);
        Assert.Empty(input.Executed);
        Assert.Contains(action.Id, input.Forgotten);
    }

    [Fact]
    public void PreparedRoundFreezesOffsetAndNewRoundUsesLatestPreference()
    {
        var clock = new FakeClock(100); var roomClock = new RoomClock(clock); var input = new RecordingInput();
        using var scheduler = new ActionScheduler(clock, roomClock, input);
        scheduler.ChangeEpoch("epoch-1"); Assert.True(roomClock.AddSample(100, 100, 100, 100));
        scheduler.TimingOffsetMs = 35;
        var first = new ScheduledAction("offset-first", "round-first", "start", 10000, "epoch-1") { Mode = "ensemble" };
        Assert.True(scheduler.Prepare(first).Ready); Assert.Equal(10035d, scheduler.LocalDeadline(first.Id));
        scheduler.TimingOffsetMs = -25;
        var stop = new ScheduledAction("offset-stop", "round-first", "stop", 14000, "epoch-1") { Mode = "ensemble" };
        var next = first with { Id = "offset-next", SessionId = "round-next" };
        Assert.True(scheduler.Prepare(stop).Ready); Assert.Equal(14035d, scheduler.LocalDeadline(stop.Id));
        Assert.True(scheduler.Prepare(next).Ready); Assert.Equal(9975d, scheduler.LocalDeadline(next.Id));
        Assert.True(scheduler.Commit(first)); Assert.Equal(10035d, scheduler.LocalDeadline(first.Id));
        scheduler.CancelAll(); Assert.Empty(input.Executed);
    }

    [Fact]
    public async Task InputIsPrimedBeforeTheDeadlineSoTheStopIsSentRightOnTime()
    {
        // 自动停止：切换前台等准备在到点前 PrimeLeadMs 完成，到点时只剩发送按键。
        var clock = new FakeClock(100); var roomClock = new RoomClock(clock); var input = new RecordingInput();
        using var scheduler = new ActionScheduler(clock, roomClock, input);
        scheduler.ChangeEpoch("epoch-1"); Assert.True(roomClock.AddSample(100, 100, 100, 100));
        var stop = new ScheduledAction("prime-stop", "round-1", "stop", 1000, "epoch-1") { Mode = "ensemble" };
        Assert.True(scheduler.Prepare(stop).Ready); Assert.True(scheduler.Commit(stop));
        await Task.Delay(400);
        lock (input.Primed) Assert.Empty(input.Primed);
        clock.NowMs = 1000 - ActionScheduler.PrimeLeadMs + 50;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { lock (input.Primed) if (input.Primed.Count > 0) break; await Task.Delay(10, limit.Token); }
        lock (input.Executed) Assert.Empty(input.Executed);
        clock.NowMs = 1000;
        while (true) { lock (input.Executed) if (input.Executed.Count > 0) break; await Task.Delay(10, limit.Token); }
        lock (input.Primed) Assert.Equal(["prime-stop"], input.Primed);
    }

    private sealed class FakeClock(double nowMs) : IMonotonicClock
    {
        private double now = nowMs;
        public double NowMs { get => Volatile.Read(ref now); set => Volatile.Write(ref now, value); }
    }

    private sealed class RecordingInput : IInputExecutor
    {
        public List<string> Executed { get; } = [];
        public List<string> Forgotten { get; } = [];
        public List<string> Primed { get; } = [];
        public void Prime(ScheduledAction action) { lock (Primed) Primed.Add(action.Id); }
        public InputPreparation Prepare(ScheduledAction action) => new(true, null);
        public InputOutcome Execute(ScheduledAction action, CancellationToken cancellationToken)
        {
            lock (Executed) Executed.Add(action.Id);
            return new("sent", 100, true, null);
        }
        public void Forget(string actionId) => Forgotten.Add(actionId);
        public void ReleaseAll() { }
    }
}
