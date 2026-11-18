using Avalonia;
using MabiTool.Desktop.Shell;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>主窗口按工作区收缩：1366×768（100%、125%）与 1920×1080（150%）下初始尺寸与最小尺寸都放得进工作区。</summary>
public sealed class WindowFitTests
{
    [Theory]
    [InlineData(1366, 728, 1.0)]
    [InlineData(1366, 728, 1.25)]
    [InlineData(1920, 1032, 1.5)]
    [InlineData(2560, 1400, 1.0)]
    public void InitialAndMinimumSizesFitTheWorkArea(int workWidth, int workHeight, double scaling)
    {
        var work = new Size(workWidth / scaling, workHeight / scaling);
        var (size, minimum) = WindowFit.Fit(new(1280, 860), new(760, 600), work);
        Assert.InRange(size.Width, minimum.Width, work.Width - 2 * WindowFit.Margin);
        Assert.InRange(size.Height, minimum.Height, work.Height - 2 * WindowFit.Margin);
        Assert.True(minimum.Width <= 760 && minimum.Height <= 600);
        if (work.Width >= 1280 + 2 * WindowFit.Margin && work.Height >= 860 + 2 * WindowFit.Margin)
            Assert.Equal((new Size(1280, 860), new Size(760, 600)), (size, minimum));
    }

    [Fact]
    public void PositionOutsideTheWorkAreaIsPulledBack()
    {
        var work = new PixelRect(0, 0, 1366, 728);
        Assert.Equal(new PixelPoint(0, 0), WindowFit.Clamp(new(-200, -50), new(1000, 600), work));
        Assert.Equal(new PixelPoint(366, 128), WindowFit.Clamp(new(900, 700), new(1000, 600), work));
        Assert.Equal(new PixelPoint(100, 50), WindowFit.Clamp(new(100, 50), new(1000, 600), work));
    }
}
