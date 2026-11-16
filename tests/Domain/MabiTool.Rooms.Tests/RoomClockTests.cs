using MabiTool.Modules.Rooms.Domain;
using Xunit;

namespace MabiTool.Rooms.Tests;

public sealed class RoomClockTests
{
    [Fact]
    public void EstimatesOffsetFromFourTimestampSample()
    {
        var clock = new FakeClock(104);
        var roomClock = new RoomClock(clock);

        Assert.True(roomClock.AddSample(100, 112, 113, 104));

        var estimate = Assert.IsType<ClockEstimate>(roomClock.Estimate);
        Assert.Equal(10.5, estimate.OffsetMs, 5);
        Assert.Equal(3, estimate.RoundTripMs, 5);
        Assert.Equal(1.5, estimate.UncertaintyMs, 5);
        Assert.Equal(0, estimate.AgeMs, 5);
    }

    [Fact]
    public void RejectsInvalidSamplesAndExpiresOldOnes()
    {
        var clock = new FakeClock(104);
        var roomClock = new RoomClock(clock);

        Assert.False(roomClock.AddSample(double.NaN, 0, 0, 0));
        Assert.False(roomClock.AddSample(100, 90, 90, 80));
        Assert.Null(roomClock.Estimate);

        Assert.True(roomClock.AddSample(100, 100, 100, 100));
        clock.NowMs += 10_001;
        Assert.Null(roomClock.Estimate);
    }

    [Fact]
    public void KeepsLowLatencyOffsetSamplesAndResetsOnEpochChange()
    {
        var clock = new FakeClock(100);
        var roomClock = new RoomClock(clock);
        Assert.True(roomClock.AddSample(90, 100, 100, 100));
        Assert.True(roomClock.AddSample(90, 120, 120, 100));
        Assert.Equal(15, roomClock.Estimate!.OffsetMs, 5);

        roomClock.Reset();

        Assert.Null(roomClock.Estimate);
    }

    private sealed class FakeClock(double nowMs) : IMonotonicClock
    {
        public double NowMs { get; set; } = nowMs;
    }
}
