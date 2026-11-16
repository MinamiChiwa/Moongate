using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class ActivityBarrierTests
{
    [Fact]
    public void MaintenanceCredentialIsBoundToItsBarrierAndDrainsWorkBeforeReopening()
    {
        var barrier = new ActivityBarrier();
        var other = new ActivityBarrier();
        var maintenance = barrier.TryEnterMaintenance("updater");
        Assert.NotNull(maintenance);
        using var foreign = other.TryEnterMaintenance("updater");
        Assert.NotNull(foreign);
        Assert.Equal("update.maintenanceLeaseInvalid", Assert.Throws<ActivityBarrierException>(() =>
            barrier.EnterMaintenanceActivity(foreign, "storage.write")).Code);
        var writing = barrier.EnterMaintenanceActivity(maintenance, "storage.write");
        maintenance.Dispose();
        Assert.Equal("updater", barrier.Snapshot().MaintenanceOwner);
        Assert.Throws<ActivityBarrierException>(() => barrier.EnterActivity("game", "game.resource"));
        Assert.Throws<ActivityBarrierException>(() => barrier.EnterMaintenanceActivity(maintenance, "storage.write"));
        Assert.Null(barrier.TryEnterMaintenance("next"));
        writing.Dispose();
        Assert.Null(barrier.Snapshot().MaintenanceOwner);
        using var next = barrier.TryEnterMaintenance("next");
        Assert.NotNull(next);
        maintenance.Dispose();
        Assert.Equal("next", barrier.Snapshot().MaintenanceOwner);
        Assert.Throws<ActivityBarrierException>(() => barrier.EnterMaintenanceActivity(maintenance, "storage.write"));
    }

    [Fact]
    public void WaitingForIdleAllowsWorkAndMaintenanceRejectsNewActivities()
    {
        var barrier = new ActivityBarrier();
        var first = barrier.EnterActivity("import", "storage.import");
        Assert.Null(barrier.TryEnterMaintenance("updater"));
        var second = barrier.EnterActivity("room", "room.ready");
        first.Dispose();
        first.Dispose();
        Assert.Null(barrier.TryEnterMaintenance("updater"));
        second.Dispose();
        var maintenance = barrier.TryEnterMaintenance("updater");
        Assert.NotNull(maintenance);
        Assert.Equal("updater", barrier.Snapshot().MaintenanceOwner);
        Assert.Null(barrier.TryEnterMaintenance("another-updater"));
        Assert.Equal("update.maintenanceActive", Assert.Throws<ActivityBarrierException>(() =>
            barrier.EnterActivity("game", "game.resource")).Code);
        maintenance.Dispose();
        using var next = barrier.TryEnterMaintenance("next");
        maintenance.Dispose();
        Assert.Equal("next", barrier.Snapshot().MaintenanceOwner);
    }

    [Fact]
    public async Task ConcurrentMaintenanceAndActivityHaveExactlyOneWinner()
    {
        var barrier = new ActivityBarrier();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            using var start = new Barrier(2);
            var activity = Task.Run(() =>
            {
                start.SignalAndWait();
                try { return barrier.EnterActivity("game", "game.resource"); }
                catch (ActivityBarrierException error) when (error.Code == "update.maintenanceActive") { return null; }
            });
            var maintenance = Task.Run(() =>
            {
                start.SignalAndWait();
                return barrier.TryEnterMaintenance("updater");
            });
            await Task.WhenAll(activity, maintenance);
            var activityLease = await activity;
            var maintenanceLease = await maintenance;
            Assert.True((activityLease is null) != (maintenanceLease is null));
            activityLease?.Dispose();
            maintenanceLease?.Dispose();
        }
        Assert.Empty(barrier.Snapshot().Activities);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("owner")]
    [InlineData("session")]
    [InlineData("resource")]
    [InlineData("all")]
    [InlineData("expiry")]
    public void ResourceLeaseLifetimeHoldsBarrierAndRejectedAcquisitionLeavesNoClaim(string release)
    {
        var clock = new Clock();
        var barrier = new ActivityBarrier();
        var leases = new ResourceLeaseCoordinator(clock, barrier);
        var request = new ResourceLeaseRequest("owner", "operation", "game", new UInt64String(1),
            ["foreground-input"], TimeSpan.FromSeconds(5));
        var lease = leases.Acquire(request);
        Assert.Null(barrier.TryEnterMaintenance("updater"));
        Assert.Single(barrier.Snapshot().Activities);
        switch (release)
        {
            case "release": Assert.True(leases.Release(lease.Id)); break;
            case "owner": Assert.Equal(1, leases.RevokeOwner("owner")); break;
            case "session": Assert.Equal(1, leases.RevokeSession("game")); break;
            case "resource": Assert.Equal(1, leases.RevokeResource("foreground-input")); break;
            case "all": Assert.Equal(1, leases.RevokeAll()); break;
            case "expiry": clock.Timestamp += TimeSpan.FromSeconds(5).Ticks; Assert.Equal(1, leases.SweepExpired()); break;
        }
        using (var maintenance = barrier.TryEnterMaintenance("updater"))
        {
            Assert.NotNull(maintenance);
            Assert.Throws<ActivityBarrierException>(() => leases.Acquire(request));
            Assert.Empty(barrier.Snapshot().Activities);
        }
        var next = leases.Acquire(request);
        Assert.True(leases.Release(next.Id));
        Assert.Empty(barrier.Snapshot().Activities);
    }

    private sealed class Clock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
