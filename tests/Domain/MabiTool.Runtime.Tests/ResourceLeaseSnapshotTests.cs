using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class ResourceLeaseSnapshotTests
{
    [Fact]
    public void SnapshotIsAnImmutablePointInTimeViewAndExpiresByMonotonicTime()
    {
        var clock = new Clock();
        var leases = new ResourceLeaseCoordinator(clock);
        var requestResources = new[] { "foreground-input", "activity-control:game" };
        var lease = leases.Acquire(new("color", "operation", "game", new(7), requestResources, TimeSpan.FromSeconds(5)));
        var original = leases.Snapshot();
        requestResources[0] = "provider-channel:changed";
        Assert.Equal(lease.Id, Assert.Single(original).Id);
        Assert.Contains("foreground-input", original[0].Resources);
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceLeaseSnapshot>)original).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)original[0].Resources).Clear());
        clock.Timestamp += TimeSpan.FromSeconds(5).Ticks;
        Assert.Empty(leases.Snapshot());
        Assert.Single(original);
        Assert.False(leases.Validate(lease.Id, "foreground-input", lease.FencingToken, "game", new(7)));
    }

    private sealed class Clock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    }
}
