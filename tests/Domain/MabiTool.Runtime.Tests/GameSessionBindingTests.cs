using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class GameSessionBindingTests
{
    private static GameWindowObservation Observe(int pid = 100, int startSecond = 1, ulong hwnd = 500,
        int x = 10, int y = 20, int width = 1280, int height = 720, double dpiX = 96, double dpiY = 96,
        bool minimized = false) => new("TW", pid,
            new DateTimeOffset(2026, 11, 8, 0, 0, startSecond, TimeSpan.Zero), new UInt64String(hwnd),
            "tw-build-1", new PhysicalWindowRect(x, y, width, height), dpiX, dpiY, minimized);

    [Fact]
    public void ProcessRestartWithReusedPidAndWindowGetsNewSession()
    {
        var tracker = new GameSessionBindingTracker();
        var first = tracker.Update(Observe());
        var restarted = tracker.Update(Observe(startSecond: 2));

        Assert.NotEqual(first.Session.Id, restarted.Session.Id);
        Assert.True(restarted.Session.BindingRevision.Value > first.Session.BindingRevision.Value);
    }

    [Fact]
    public void WindowIdentityChangeAndMovementInvalidatePreparedBinding()
    {
        var tracker = new GameSessionBindingTracker();
        var first = tracker.Update(Observe());
        var moved = tracker.Update(Observe(hwnd: 501, x: 30));

        Assert.Equal(first.Session.Id, moved.Session.Id);
        Assert.NotEqual(first.Session.BindingRevision, moved.Session.BindingRevision);
        Assert.Equal(30, moved.Bounds.X);
    }

    [Fact]
    public void DestroyedWindowRecreatedWithSameHandleGetsFreshSession()
    {
        var tracker = new GameSessionBindingTracker();
        var first = tracker.Update(Observe());
        tracker.Clear();
        var recreated = tracker.Update(Observe());

        Assert.NotEqual(first.Session.Id, recreated.Session.Id);
    }

    [Fact]
    public void DpiChangesInvalidateBindingAndUsePhysicalBounds()
    {
        var tracker = new GameSessionBindingTracker();
        var first = tracker.Update(Observe());
        var scaled = tracker.Update(Observe(width: 2560, height: 1440, dpiX: 192, dpiY: 192));

        Assert.NotEqual(first.Session.BindingRevision, scaled.Session.BindingRevision);
        Assert.Equal(2560, scaled.Bounds.Width);
        Assert.Equal(192, scaled.DpiX);
    }

    [Fact]
    public void MinimizedWindowRejectsInputAndRestorationChangesBindingRevision()
    {
        var tracker = new GameSessionBindingTracker();
        var first = tracker.Update(Observe());
        var minimized = tracker.Update(Observe(minimized: true));
        var restored = tracker.Update(Observe(x: 50));

        Assert.True(first.CanAcceptInput);
        Assert.False(minimized.CanAcceptInput);
        Assert.NotEqual(first.Session.BindingRevision, minimized.Session.BindingRevision);
        Assert.NotEqual(first.Session.BindingRevision, restored.Session.BindingRevision);
        Assert.True(restored.CanAcceptInput);
    }

    [Fact]
    public void ReadsReuseTheLastCheckWithinTheIntervalWhileCurrentAlwaysChecks()
    {
        var clock = new ManualClock();
        var tracker = new GameSessionBindingTracker(clock);
        GameWindowObservation? window = Observe();
        var calls = 0;
        var bound = tracker.BindSource(() => { calls++; return window; })!;
        Assert.Equal(1, calls);

        // 窗口未变化时，查询与即时核对都不改变会话和修订。
        for (var i = 0; i < 5; i++) Assert.Same(bound, tracker.Recent);
        Assert.Equal(1, calls);
        Assert.Equal(bound.Session, tracker.Current!.Session);
        Assert.Equal(2, calls);

        // 窗口关闭：间隔内的查询仍返回上次结果，即时核对立刻发现。
        window = null;
        clock.Advance(GameSessionBindingTracker.ReadRefreshInterval / 2);
        Assert.NotNull(tracker.Recent);
        Assert.Equal(2, calls);
        Assert.Null(tracker.Current);
        Assert.Equal(3, calls);
        Assert.Null(tracker.Recent);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void ReadsCheckAgainOnceTheIntervalElapses()
    {
        var clock = new ManualClock();
        var tracker = new GameSessionBindingTracker(clock);
        GameWindowObservation? window = Observe();
        var calls = 0;
        var bound = tracker.BindSource(() => { calls++; return window; })!;
        window = Observe(x: 40);
        clock.Advance(GameSessionBindingTracker.ReadRefreshInterval - TimeSpan.FromTicks(1));
        Assert.Same(bound, tracker.Recent);
        clock.Advance(TimeSpan.FromTicks(1));
        var moved = tracker.Recent!;
        Assert.Equal(2, calls);
        Assert.Equal(40, moved.Bounds.X);
        Assert.Equal(bound.Session.BindingRevision.Value + 1, moved.Session.BindingRevision.Value);

        window = null;
        clock.Advance(GameSessionBindingTracker.ReadRefreshInterval);
        Assert.Null(tracker.Recent);
        Assert.Null(tracker.LastKnown);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }

    [Fact]
    public void InvalidPhysicalGeometryAndNonUtcStartTimeAreRejected()
    {
        var tracker = new GameSessionBindingTracker();
        Assert.Throws<ArgumentException>(() => tracker.Update(Observe(width: 0)));
        Assert.Throws<ArgumentException>(() => tracker.Update(Observe() with
        {
            ProcessStartTime = new DateTimeOffset(2026, 11, 8, 8, 0, 1, TimeSpan.FromHours(8))
        }));
    }
}
