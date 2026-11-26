// 改写自 MabiAgent 82bc0ef 的 dotnet/MabiAgent.Windows.Tests/FishingGaugeTests.cs（MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：识别参数固定为台服浮动进度条的颜色（填充 RGB(40,220,88)、空槽 RGB(39,42,50)），合成用例改用这两种颜色；
// 夹具改由随仓库分发的 PNG 解码，不再经过 OpenCvSharp。

using MabiTool.LifeVision.Worker;
using Xunit;

namespace MabiTool.LifeVision.Tests;

public sealed class FishingGaugeTests
{
    private static readonly PixelBox GaugeBox = new(38, 99, 142, 125);

    private static RgbImage Painted(int width, int height, Func<int, int, byte[]> color)
    {
        var image = new RgbImage(width, height, new byte[width * height * 3]);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) color(x, y).CopyTo(image.Pixels, (y * width + x) * 3);
        return image;
    }

    [Theory]
    [InlineData(0, 101)]
    [InlineData(1, 72)]
    [InlineData(2, 42)]
    [InlineData(3, 14)]
    public void LiveFloatingGaugeReadsCalibratedInterior(int sample, int fill)
    {
        var reading = FishingGauge.Read(TestAssets.Fixture($"fishing-gauge/live-{sample}.png"), GaugeBox);
        Assert.Equal(new FishingGauge.Reading(fill, 104), reading);
        Assert.Equal(fill / 104d, reading!.Value.Value);
    }

    [Fact]
    public void MovingLiveGaugeCanBeLocatedWithoutHookTemplate()
    {
        var values = new List<double>();
        for (var sample = 0; sample < 4; sample++)
        {
            var image = TestAssets.Fixture($"fishing-gauge/live-{sample}.png");
            var located = FishingGauge.Find(image, new(0, 0, image.Width, image.Height), 104);
            Assert.NotNull(located);
            Assert.Equal(located.Value.FillPixels / (double)located.Value.TrackPixels, located.Value.Value);
            values.Add(located.Value.Value);
        }
        Assert.True(values.Zip(values.Skip(1)).All(pair => pair.First > pair.Second), string.Join(",", values));
    }

    [Fact]
    public void SearchFindsGaugeOutsideAnIncorrectFixedBox()
    {
        var image = TestAssets.Fixture("fishing-gauge/live-0.png");
        Assert.Null(FishingGauge.Read(image, new(0, 0, 104, 26)));
        var located = FishingGauge.Find(image, new(0, 0, image.Width, image.Height), 104);
        Assert.NotNull(located);
        Assert.True(located.Value.Value > .75);
    }

    [Fact]
    public void SearchRejectsRealFrameAfterGaugeDisappearsAndRegionsAwayFromGauge()
    {
        var ended = TestAssets.Fixture("fishing-gauge/live-4.png");
        Assert.Null(FishingGauge.Find(ended, new(0, 0, ended.Width, ended.Height), 104));
        var active = TestAssets.Fixture("fishing-gauge/live-0.png");
        Assert.Null(FishingGauge.Find(active, new(0, 0, active.Width, 80), 104));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, .2)]
    [InlineData(9, .9)]
    [InlineData(10, 1)]
    public void AdjacentTrackRowsTakePriorityOverDarkBackground(int fill, double expected)
    {
        // 轨道占 8–12 行，其余行是整行空槽色：整行读成零的背景不能压过轨道内部的相邻行。
        var image = Painted(10, 20, (x, y) => y is >= 8 and < 13 && x < fill ? TestAssets.FillColor : TestAssets.EmptyColor);
        Assert.Equal(expected, FishingGauge.Read(image, new(0, 0, 10, 20))!.Value.Value);
    }

    [Fact]
    public void IsolatedGreenRowsDoNotEstablishATrack()
    {
        var image = Painted(10, 7, (x, y) => y % 2 == 0 ? x < 8 ? TestAssets.FillColor : TestAssets.EmptyColor : [150, 100, 180]);
        Assert.Null(FishingGauge.Read(image, new(0, 0, 10, 7)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, .2)]
    [InlineData(10, 1)]
    public void TrackReportsFillFraction(int fill, double expected)
    {
        var image = Painted(10, 3, (x, _) => x < fill ? TestAssets.FillColor : TestAssets.EmptyColor);
        Assert.Equal(expected, FishingGauge.Read(image, new(0, 0, 10, 3))!.Value.Value);
    }

    [Fact]
    public void ObscuredOrForeignColoursRemainUnknown()
    {
        Assert.Null(FishingGauge.Read(new RgbImage(10, 3, new byte[90]), new(0, 0, 10, 3)));
        // 填充出现在空槽之后：不是左→右的进度条。
        var reversed = Painted(10, 3, (x, _) => x < 5 ? TestAssets.EmptyColor : TestAssets.FillColor);
        Assert.Null(FishingGauge.Read(reversed, new(0, 0, 10, 3)));
    }
}
