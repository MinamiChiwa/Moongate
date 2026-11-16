using MabiTool.Host.Color;
using MabiTool.Infrastructure;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class ColorPresenceTrackerTests
{
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public DateTimeOffset Wall = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => Wall;
    }
    [Fact]
    public void RecoveryWindowIsMonotonicAndExpiresAtFiveSeconds()
    {
        var clock = new Clock(); var presence = new ColorInteractionPresenceTracker(clock); presence.Begin("controller");
        clock.Wall = clock.Wall.AddDays(100); clock.Milliseconds = 4999;
        Assert.True(presence.HasLiveClient(_ => true));
        clock.Wall = clock.Wall.AddDays(-200); clock.Milliseconds = 5000;
        Assert.False(presence.HasLiveClient(_ => true));
        Assert.Equal("color.runInactive", Assert.Throws<OperationException>(() => presence.Renew("owner", _ => true)).Code);
    }
    [Fact]
    public void RenewalOnlyUpdatesItsPrincipalAndRevokedClientsAreRemoved()
    {
        var clock = new Clock(); var presence = new ColorInteractionPresenceTracker(clock); presence.Begin("first");
        clock.Milliseconds = 4000; presence.Renew("owner", _ => true);
        clock.Milliseconds = 5000; Assert.True(presence.HasLiveClient(id => id == "owner"));
        clock.Milliseconds = 9000; Assert.False(presence.HasLiveClient(_ => true));
    }
    [Fact]
    public void CapacityIsBoundedAndExpiredClientsMakeRoomForRecovery()
    {
        var clock = new Clock(); var presence = new ColorInteractionPresenceTracker(clock); presence.Begin("first");
        for (var i = 1; i < 8; i++) presence.Renew(i.ToString(), _ => true);
        Assert.Equal("resource.capacityExceeded", Assert.Throws<OperationException>(() => presence.Renew("ninth", _ => true)).Code);
        clock.Milliseconds = 4999; presence.Renew("first", _ => true);
        clock.Milliseconds = 5000; presence.Renew("ninth", _ => true);
        Assert.True(presence.HasLiveClient(_ => true));
    }
}
