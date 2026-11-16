using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class ResourceLeaseTests
{
    private static ResourceLeaseRequest Request(string owner, params string[] resources) => new(owner,
        "op-" + owner, "session-1", new UInt64String(7), resources, TimeSpan.FromSeconds(5));

    [Fact]
    public void ForegroundInputIsExclusiveAcrossWindowsAndFailureDoesNotPartiallyAcquire()
    {
        var coordinator = new ResourceLeaseCoordinator();
        var first = coordinator.Acquire(Request("color", "foreground-input", "activity-control:session-1"));

        var conflict = Assert.Throws<ResourceLeaseException>(() => coordinator.Acquire(
            Request("room", "provider-channel:new-provider", "activity-control:session-1")));
        Assert.Equal("resource.busy", conflict.Code);
        Assert.Equal("color", conflict.ConflictingOwner);
        Assert.False(coordinator.Validate(first.Id, "provider-channel:new-provider", first.FencingToken,
            "session-1", new UInt64String(7)));

        var unclaimed = coordinator.Acquire(Request("observer", "provider-channel:new-provider"));
        coordinator.Release(unclaimed.Id);
        coordinator.Release(first.Id);
        var room = coordinator.Acquire(Request("room", "activity-control:session-1"));
        Assert.True(coordinator.Validate(room.Id, "activity-control:session-1", room.FencingToken,
            "session-1", new UInt64String(7)));
    }

    [Fact]
    public void ProviderChannelSerializesWritesWithoutBlockingIndependentProviders()
    {
        var coordinator = new ResourceLeaseCoordinator();
        var first = coordinator.Acquire(Request("solo", "provider-channel:tw-cli"));
        Assert.Throws<ResourceLeaseException>(() => coordinator.Acquire(Request("room", "provider-channel:tw-cli")));
        var other = coordinator.Acquire(Request("observer", "provider-channel:passive-observer"));

        Assert.NotEqual(first.FencingToken, other.FencingToken);
    }

    [Fact]
    public void CliWriteConflictsWithRoomActivityOnTheSameGameSession()
    {
        var coordinator = new ResourceLeaseCoordinator();
        coordinator.Acquire(Request("room", "activity-control:session-1"));

        var conflict = Assert.Throws<ResourceLeaseException>(() => coordinator.Acquire(
            Request("cli", "activity-control:session-1", "provider-channel:tw-cli")));
        Assert.Equal("room", conflict.ConflictingOwner);
    }

    [Fact]
    public void FencingTokenAndBindingRevisionRejectStaleActionsAfterReacquire()
    {
        var coordinator = new ResourceLeaseCoordinator();
        var first = coordinator.Acquire(Request("color", "foreground-input"));
        coordinator.Release(first.Id);
        var next = coordinator.Acquire(Request("color", "foreground-input"));

        Assert.True(next.FencingToken.Value > first.FencingToken.Value);
        Assert.False(coordinator.Validate(next.Id, "foreground-input", first.FencingToken,
            "session-1", new UInt64String(7)));
        Assert.False(coordinator.Validate(next.Id, "foreground-input", next.FencingToken,
            "session-1", new UInt64String(8)));
    }

    [Fact]
    public void RevokingOwnerOrSessionReleasesEveryResourceAtomically()
    {
        var coordinator = new ResourceLeaseCoordinator();
        var ownerLease = coordinator.Acquire(Request("color", "foreground-input", "activity-control:session-1"));
        Assert.Equal(1, coordinator.RevokeOwner("color"));
        Assert.False(coordinator.Validate(ownerLease.Id, "foreground-input", ownerLease.FencingToken,
            "session-1", new UInt64String(7)));

        var sessionLease = coordinator.Acquire(Request("room", "activity-control:session-1"));
        Assert.Equal(1, coordinator.RevokeSession("session-1"));
        Assert.False(coordinator.Validate(sessionLease.Id, "activity-control:session-1", sessionLease.FencingToken,
            "session-1", new UInt64String(7)));
    }

    [Fact]
    public void ExpiredLeaseCannotBeRenewedOrValidated()
    {
        var clock = new ManualClock();
        var coordinator = new ResourceLeaseCoordinator(clock);
        var lease = coordinator.Acquire(Request("color", "foreground-input"));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.Validate(lease.Id, "foreground-input", lease.FencingToken,
            "session-1", new UInt64String(7)));
        Assert.Equal("resource.leaseExpired", Assert.Throws<ResourceLeaseException>(() =>
            coordinator.Renew(lease.Id, lease.FencingToken, TimeSpan.FromSeconds(5))).Code);
        var next = coordinator.Acquire(Request("room", "foreground-input"));
        Assert.True(next.FencingToken.Value > lease.FencingToken.Value);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utcNow = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan value) { timestamp += value.Ticks; utcNow += value; }
    }
}
