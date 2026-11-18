using MabiTool.Platform.Windows;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>
/// 客户区物理口径：游戏窗口为每显示器感知、系统级感知（系统 DPI 96）或未感知，位于 150% 显示器（144 DPI）上，
/// 目标窗口坐标系里的客户区 1280×720（逻辑）换算为物理四角后，绑定尺寸与 DPI 都按物理像素与显示器有效 DPI 给出。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsClientGeometryTests
{
    [Theory]
    [InlineData(WindowDpiAwareness.PerMonitor, 1920, 1080, 144u, 1920, 1080)]
    [InlineData(WindowDpiAwareness.System, 1280, 720, 96u, 1920, 1080)]
    [InlineData(WindowDpiAwareness.Unaware, 1280, 720, 96u, 1920, 1080)]
    [InlineData(WindowDpiAwareness.System, 1024, 683, 120u, 1229, 820)]
    public void ClientAreaIsPhysicalForEveryAwareness(WindowDpiAwareness awareness, int logicalWidth, int logicalHeight, uint windowDpi,
        int physicalWidth, int physicalHeight)
    {
        var area = WindowsClientGeometry.Resolve(awareness, logicalWidth, logicalHeight, (-1910, 40), (-1910 + physicalWidth, 40 + physicalHeight), windowDpi, 144);
        Assert.NotNull(area);
        Assert.Equal(new(-1910, 40, physicalWidth, physicalHeight), area.Value.Bounds);
        Assert.Equal(144u, area.Value.Dpi);
    }

    [Theory]
    [InlineData(WindowDpiAwareness.Unaware, 1280, 720)]
    [InlineData(WindowDpiAwareness.PerMonitor, 1280, 720)]
    public void InconsistentConversionIsRejected(WindowDpiAwareness awareness, int physicalWidth, int physicalHeight)
    {
        // 未感知窗口在 150% 显示器上换算后仍是逻辑尺寸、或每显示器感知窗口的四角与逻辑尺寸不符：换算失败，不给出错位的绑定。
        var logical = awareness == WindowDpiAwareness.Unaware ? (1280, 720) : (1920, 1080);
        Assert.Null(WindowsClientGeometry.Resolve(awareness, logical.Item1, logical.Item2, (0, 0), (physicalWidth, physicalHeight), 96, 144));
    }

    [Fact]
    public void MinimizedWindowKeepsOriginWithZeroSize() =>
        Assert.Equal(new(-32000, -32000, 0, 0), WindowsClientGeometry.Resolve(WindowDpiAwareness.Unaware, 0, 0, (-32000, -32000), (-32000, -32000), 96, 144)!.Value.Bounds);
}
