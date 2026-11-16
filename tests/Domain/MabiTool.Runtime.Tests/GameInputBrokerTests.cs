using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class GameInputBrokerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    private static (GameSessionBindingTracker Tracker, ResourceLeaseCoordinator Leases, FakeDevice Device,
        GameInputBroker Broker, ResourceLeaseSnapshot Lease, GameWindowBinding Binding) Create()
    {
        var tracker = new GameSessionBindingTracker();
        var binding = tracker.Update(new("TW", 100, Now, new UInt64String(500), "build-1",
            new PhysicalWindowRect(100, 200, 800, 600), 96, 96, false));
        var leases = new ResourceLeaseCoordinator(new FixedClock(Now));
        var lease = leases.Acquire(new("color", "op-1", binding.Session.Id, binding.Session.BindingRevision,
            ["foreground-input"], TimeSpan.FromSeconds(10)));
        var device = new FakeDevice { ForegroundWindowIdentity = binding.Session.WindowIdentity };
        return (tracker, leases, device, new GameInputBroker(leases, tracker, device, new FixedClock(Now)), lease, binding);
    }

    private static GameInputAction Action(GameWindowBinding binding, ResourceLeaseSnapshot lease,
        InputActionKind kind, InputControl? control = null, PhysicalScreenPoint? point = null) => new(
        lease.OwnerId, lease.Id, lease.FencingToken, binding.Session.Id, binding.Session.BindingRevision,
        binding.Session.WindowIdentity, kind, control, point);

    [Fact]
    public void PressesAreTrackedAndEmergencyStopRevokesLeasesAndReleasesControls()
    {
        var (_, leases, device, broker, lease, binding) = Create();
        broker.Execute(Action(binding, lease, InputActionKind.Press, InputControl.MouseLeft));

        var result = broker.EmergencyStop();

        Assert.Equal(1, result.RevokedLeases);
        Assert.Equal(1, result.ReleasedControls);
        Assert.Contains(InputControl.MouseLeft, device.Released);
        Assert.False(leases.Validate(lease.Id, "foreground-input", lease.FencingToken,
            binding.Session.Id, binding.Session.BindingRevision));
    }

    [Fact]
    public void StaleWorkerActionIsRejectedWithoutRevokingCurrentOwner()
    {
        var (tracker, leases, device, broker, lease, binding) = Create();
        broker.Execute(Action(binding, lease, InputActionKind.Press, InputControl.Space));
        leases.Release(lease.Id);
        var currentLease = leases.Acquire(new("color", "op-2", binding.Session.Id, binding.Session.BindingRevision,
            ["foreground-input"], TimeSpan.FromSeconds(10)));

        Assert.Equal("input.leaseInvalid", Assert.Throws<GameInputException>(() =>
            broker.Execute(Action(binding, lease, InputActionKind.Press, InputControl.MouseLeft))).Code);
        Assert.True(leases.Validate(currentLease.Id, "foreground-input", currentLease.FencingToken,
            binding.Session.Id, binding.Session.BindingRevision));
        Assert.Equal([InputControl.Space], device.Pressed);
        Assert.Contains(InputControl.Space, device.Released);
        Assert.Equal(0, device.ReleaseAllCalls);
    }

    [Fact]
    public void ActionOwnerMustMatchTheLeaseOwner()
    {
        var (_, leases, _, broker, lease, binding) = Create();
        var forged = Action(binding, lease, InputActionKind.Press, InputControl.Space) with { OwnerId = "other" };

        Assert.Equal("input.leaseInvalid", Assert.Throws<GameInputException>(() => broker.Execute(forged)).Code);
        Assert.True(leases.Validate(lease.Id, "foreground-input", lease.FencingToken,
            binding.Session.Id, binding.Session.BindingRevision));
    }

    [Fact]
    public void EmergencyStopKeepsUnrelatedResourceLeases()
    {
        var (_, leases, _, broker, lease, binding) = Create();
        var providerLease = leases.Acquire(new("combat", "observe", binding.Session.Id,
            binding.Session.BindingRevision, ["provider-channel:combat"], TimeSpan.FromSeconds(10)));

        var result = broker.EmergencyStop();

        Assert.Equal(1, result.RevokedLeases);
        Assert.False(leases.Validate(lease.Id, "foreground-input", lease.FencingToken,
            binding.Session.Id, binding.Session.BindingRevision));
        Assert.True(leases.Validate(providerLease.Id, "provider-channel:combat", providerLease.FencingToken,
            binding.Session.Id, binding.Session.BindingRevision));
    }

    [Fact]
    public void FocusLossCancelsInputAndReleasesKeys()
    {
        var (_, _, device, broker, lease, binding) = Create();
        broker.Execute(Action(binding, lease, InputActionKind.Press, InputControl.Space));
        device.ForegroundWindowIdentity = new UInt64String(999);

        Assert.Equal("input.focusChanged", Assert.Throws<GameInputException>(() =>
            broker.Execute(Action(binding, lease, InputActionKind.MovePointer, point: new(150, 250)))).Code);
        Assert.Contains(InputControl.Space, device.Released);
        Assert.Equal(1, device.ReleaseAllCalls);
    }

    [Fact]
    public void FocusChangeDuringInputReleasesTheJustPressedControl()
    {
        var (_, _, device, broker, lease, binding) = Create();
        device.ChangeFocusAfterNextPress = true;

        Assert.Equal("input.focusChanged", Assert.Throws<GameInputException>(() =>
            broker.Execute(Action(binding, lease, InputActionKind.Press, InputControl.Space))).Code);
        Assert.Contains(InputControl.Space, device.Pressed);
        Assert.Equal(1, device.ReleaseAllCalls);
    }

    [Fact]
    public void BindingRevisionChangeInvalidatesActionsAndReleasesInput()
    {
        var (tracker, _, device, broker, lease, binding) = Create();
        broker.Execute(Action(binding, lease, InputActionKind.Press, InputControl.MouseLeft));
        tracker.Update(new("TW", 100, Now, new UInt64String(500), "build-1",
            new PhysicalWindowRect(120, 200, 800, 600), 96, 96, false));

        Assert.Equal("input.bindingChanged", Assert.Throws<GameInputException>(() =>
            broker.Execute(Action(binding, lease, InputActionKind.MovePointer, point: new(150, 250)))).Code);
        Assert.Contains(InputControl.MouseLeft, device.Released);
    }

    [Fact]
    public void PointerCoordinatesMustBeWithinTheBoundPhysicalWindow()
    {
        var (_, _, device, broker, lease, binding) = Create();
        Assert.Equal("input.pointOutsideWindow", Assert.Throws<GameInputException>(() =>
            broker.Execute(Action(binding, lease, InputActionKind.MovePointer, point: new(900, 250)))).Code);
        Assert.Empty(device.Moved);
    }

    [Fact]
    public void ToggleStopRequiresFreshConfirmedPlayingEvidenceForSameSession()
    {
        var (_, _, device, broker, lease, binding) = Create();
        var stopAction = Action(binding, lease, InputActionKind.Press, InputControl.Space);
        var stale = new GamePlaybackEvidence(binding.Session.Id, true, ObservationQuality.Confirmed, Now.AddSeconds(-2));
        var request = new GameStopRequest(stopAction, StopKeyBehavior.ToggleOnlyWhenPlaying, stale, TimeSpan.FromSeconds(1));

        Assert.Equal("input.stopEvidenceRequired", Assert.Throws<GameInputException>(() => broker.DispatchStop(request)).Code);
        Assert.Empty(device.Pressed);

        var current = stale with { ObservedAt = Now };
        broker.DispatchStop(request with { Evidence = current });
        Assert.Equal([InputControl.Space], device.Pressed);
        Assert.Equal([InputControl.Space], device.Released);
    }

    [Fact]
    public void StopRejectsUndefinedBehavior()
    {
        var (_, _, device, broker, lease, binding) = Create();
        var stopAction = Action(binding, lease, InputActionKind.Press, InputControl.Space);

        Assert.Equal("input.stopRequestInvalid", Assert.Throws<GameInputException>(() => broker.DispatchStop(
            new(stopAction, (StopKeyBehavior)999, null, TimeSpan.FromSeconds(1)))).Code);
        Assert.Empty(device.Pressed);
    }

    [Fact]
    public void StopRechecksFocusAfterPressBeforeRelease()
    {
        var (_, _, device, broker, lease, binding) = Create();
        device.ChangeFocusAfterNextPress = true;
        var stopAction = Action(binding, lease, InputActionKind.Press, InputControl.Space);

        Assert.Equal("input.focusChanged", Assert.Throws<GameInputException>(() => broker.DispatchStop(
            new(stopAction, StopKeyBehavior.PressOnce, null, TimeSpan.FromSeconds(1)))).Code);
        Assert.Contains(InputControl.Space, device.Pressed);
        Assert.Contains(InputControl.Space, device.Released);
        Assert.Equal(1, device.ReleaseAllCalls);
    }

    private sealed class FakeDevice : IGameInputDevice
    {
        public UInt64String? ForegroundWindowIdentity { get; set; }
        public List<InputControl> Pressed { get; } = [];
        public List<InputControl> Released { get; } = [];
        public List<PhysicalScreenPoint> Moved { get; } = [];
        public int ReleaseAllCalls { get; private set; }
        public bool ChangeFocusAfterNextPress { get; set; }
        public void Press(InputControl control)
        {
            Pressed.Add(control);
            if (ChangeFocusAfterNextPress) { ForegroundWindowIdentity = new UInt64String(999); ChangeFocusAfterNextPress = false; }
        }
        public void Release(InputControl control) => Released.Add(control);
        public void MovePointer(PhysicalScreenPoint point) => Moved.Add(point);
        public void ReleaseAll()
        {
            ReleaseAllCalls++;
            foreach (var control in Pressed.Except(Released)) Released.Add(control);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
