using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Rooms.Domain;
using Xunit;

namespace MabiTool.Rooms.Tests;

public sealed class LeasedRoomInputExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreparationAtomicallyLeasesActivityAndForegroundInputThenExecutesTypedAction()
    {
        var fixture = new Fixture();
        var action = fixture.Action();

        Assert.True(fixture.Executor.Prepare(action).Ready);
        Assert.Throws<ResourceLeaseException>(() => fixture.Leases.Acquire(new("color", "color-run",
            fixture.Binding.Session.Id, fixture.Binding.Session.BindingRevision, ["foreground-input"], TimeSpan.FromSeconds(10))));

        var outcome = fixture.Executor.Execute(action, CancellationToken.None);

        Assert.Equal("sent", outcome.Status);
        Assert.Equal([InputControl.Space], fixture.Device.Pressed);
        Assert.Equal([InputControl.Space], fixture.Device.Released);
        Assert.True(fixture.CanAcquire("foreground-input"));
        Assert.True(fixture.CanAcquire("activity-control:" + fixture.Binding.Session.Id));
    }

    [Fact]
    public void BindingChangeAfterPreparationRejectsActionAndReleasesLease()
    {
        var fixture = new Fixture();
        var action = fixture.Action();
        Assert.True(fixture.Executor.Prepare(action).Ready);
        fixture.Tracker.Update(new("TW", 100, Now, new UInt64String(501), "build-1",
            new PhysicalWindowRect(100, 200, 800, 600), 96, 96, false));

        var outcome = fixture.Executor.Execute(action, CancellationToken.None);

        Assert.Equal("rejected", outcome.Status);
        Assert.Equal("input.bindingChanged", outcome.Detail);
        Assert.Empty(fixture.Device.Pressed);
        Assert.True(fixture.CanAcquire("foreground-input"));
    }

    [Fact]
    public void InsufficientClockQualityPreventsLeaseAcquisition()
    {
        var fixture = new Fixture(clockQuality: "room.clockQualityInsufficient");

        var result = fixture.Executor.Prepare(fixture.Action());

        Assert.False(result.Ready);
        Assert.Equal("room.clockQualityInsufficient", result.Error);
        Assert.True(fixture.CanAcquire("foreground-input"));
    }

    [Fact]
    public void ConflictingResourceDoesNotLeavePartialLease()
    {
        var fixture = new Fixture();
        fixture.Leases.Acquire(new("color", "color-run", fixture.Binding.Session.Id,
            fixture.Binding.Session.BindingRevision, ["activity-control:" + fixture.Binding.Session.Id], TimeSpan.FromSeconds(10)));

        var result = fixture.Executor.Prepare(fixture.Action());

        Assert.False(result.Ready);
        Assert.StartsWith("resource.busy", result.Error);
        Assert.True(fixture.CanAcquire("foreground-input"));
    }

    private sealed class Fixture
    {
        public GameSessionBindingTracker Tracker { get; } = new();
        public GameWindowBinding Binding { get; }
        public ResourceLeaseCoordinator Leases { get; } = new(new FixedTimeProvider(Now));
        public FakeDevice Device { get; }
        public LeasedRoomInputExecutor Executor { get; }

        public Fixture(string? clockQuality = null)
        {
            Binding = Tracker.Update(new("TW", 100, Now, new UInt64String(500), "build-1",
                new PhysicalWindowRect(100, 200, 800, 600), 96, 96, false));
            Device = new() { ForegroundWindowIdentity = Binding.Session.WindowIdentity };
            var broker = new GameInputBroker(Leases, Tracker, Device, new FixedTimeProvider(Now));
            var monotonicClock = new FixedMonotonicClock();
            var roomClock = new RoomClock(monotonicClock);
            Executor = new("rooms:client", Leases, new GameSessionRoomInputBindingSource(Tracker),
                new FixedActionMapper(), new FixedTimingQuality(clockQuality), broker, monotonicClock, roomClock, _ => 0);
        }

        public ScheduledAction Action() => new("action-1", "room-member-session", "start", 1000, "epoch-1") { Mode = "ensemble" };

        public bool CanAcquire(string resource)
        {
            try
            {
                var lease = Leases.Acquire(new("probe", Guid.NewGuid().ToString("N"), Binding.Session.Id,
                    Binding.Session.BindingRevision, [resource], TimeSpan.FromSeconds(10)));
                Leases.Release(lease.Id);
                return true;
            }
            catch (ResourceLeaseException) { return false; }
        }
    }

    private sealed class FixedTimingQuality(string? error) : IRoomInputTimingQuality
    {
        public string? ValidateForPreparation(string actionSessionId, RoomClock roomClock, int timingOffsetMs) => error;
    }

    private sealed class FixedActionMapper : IRoomInputActionMapper
    {
        public bool TryMap(ScheduledAction action, out InputControl control)
        {
            control = InputControl.Space;
            return true;
        }
    }

    private sealed class FixedMonotonicClock : IMonotonicClock { public double NowMs => 500; }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class FakeDevice : IGameInputDevice
    {
        public UInt64String? ForegroundWindowIdentity { get; set; }
        public List<InputControl> Pressed { get; } = [];
        public List<InputControl> Released { get; } = [];
        public void Press(InputControl control) => Pressed.Add(control);
        public void Release(InputControl control) => Released.Add(control);
        public void MovePointer(PhysicalScreenPoint point) { }
        public void ReleaseAll() { }
    }
}
