using MabiTool.ObservationDebugTool;
using Xunit;

namespace MabiTool.ObservationDebugTool.Tests;

public sealed class ProbeCaptureWindowTests
{
    [Fact]
    public void SlowTravelDoesNotConsumeCombatBudget()
    {
        var clock = new Clock();
        var window = new ProbeCaptureWindow(900, 120, clock);
        Assert.False(window.StartCombat());
        Assert.True(window.StartPreparation());
        Assert.False(window.StartPreparation());
        window.CaptureReady();
        Assert.False(window.StartCombat());
        clock.Advance(600);
        window.IdentityReady();
        Assert.True(window.Snapshot().CanStart);
        Assert.True(window.StartCombat());
        Assert.Equal(120, window.Snapshot().RemainingSeconds);
        clock.Advance(90);
        Assert.False(window.StartCombat());
        Assert.Equal(30, window.Snapshot().RemainingSeconds);
        clock.Advance(30);
        Assert.True(window.Expired);
    }

    [Fact]
    public void PreparationTimeoutAndStopCannotBeRestarted()
    {
        var clock = new Clock();
        var window = new ProbeCaptureWindow(900, 120, clock);
        window.StartPreparation(); window.CaptureReady(); window.IdentityReady();
        clock.Advance(901);
        Assert.True(window.Expired);
        Assert.False(window.StartCombat());
        Assert.False(window.Snapshot().CanStart);
        window.Stop();
        window.CaptureReady(); window.IdentityReady();
        Assert.False(window.StartPreparation());
        Assert.False(window.StartCombat());
        Assert.True(window.Snapshot().IsStopped);
    }

    [Fact]
    public void RetryRevokesReadySignalWithoutExtendingPreparation()
    {
        var clock = new Clock();
        var window = new ProbeCaptureWindow(900, 120, clock);
        window.StartPreparation(); window.CaptureReady(); window.IdentityReady();
        clock.Advance(400);
        window.AwaitingCapture();
        Assert.Equal("正在启动", window.Snapshot().Stage);
        Assert.False(window.StartCombat());
        window.CaptureReady();
        Assert.Equal("可以切图", window.Snapshot().Stage);
        Assert.False(window.StartCombat());
        window.IdentityReady();
        Assert.Equal(500, window.Snapshot().RemainingSeconds);
        Assert.True(window.StartCombat());
    }

    [Fact]
    public void WallClockChangeDoesNotChangeRemainingBudget()
    {
        var clock = new Clock();
        var window = new ProbeCaptureWindow(900, 120, clock);
        window.StartPreparation(); window.CaptureReady(); window.IdentityReady(); window.StartCombat();
        clock.UtcOffset = TimeSpan.FromHours(4);
        clock.Advance(10);
        Assert.Equal(110, window.Snapshot().RemainingSeconds);
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public TimeSpan UtcOffset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks) + UtcOffset;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
