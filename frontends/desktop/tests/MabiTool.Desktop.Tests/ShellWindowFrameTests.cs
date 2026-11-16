using Path = Avalonia.Controls.Shapes.Path;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Overlay;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.Desktop.Pages.Settings;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>无边框窗口：顶部拖动带、双击最大化、最大化按钮状态、迷你窗拖动范围、最大化客户区。</summary>
public sealed class ShellWindowFrameTests
{
    [AvaloniaFact]
    public void BlankTopBandStartsDragButControlsDoNot()
    {
        var window = (MainWindow)LayoutProbe.Open(new SampleBackend(), 1280, 860, "presentation");
        LayoutProbe.Render(window);
        var drags = 0;
        window.CaptionDragStarting += (_, _) => drags++;

        // 页面标题右侧到窗口按钮之间的空白。
        Press(window, new Point(900, 30));
        Assert.Equal(1, drags);
        // 页面标题文字本身。
        var title = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("h1"));
        Press(window, title.TranslatePoint(new Point(10, title.Bounds.Height / 2), window)!.Value);
        Assert.Equal(2, drags);
        // 侧栏品牌区。
        var brand = Named<Control>(window, "BrandArea");
        Press(window, brand.TranslatePoint(new Point(brand.Bounds.Width - 4, brand.Bounds.Height / 2), window)!.Value);
        Assert.Equal(3, drags);
        // 窗口按钮上只触发按钮：最小化按钮让窗口最小化，不拖动。
        var minimize = Named<Button>(window, "MinimizeButton");
        Press(window, Center(minimize, window));
        Assert.Equal(3, drags);
        Assert.Equal(WindowState.Minimized, window.WindowState);
        window.WindowState = WindowState.Normal;
        LayoutProbe.Render(window);
        // 带以下的空白不拖动。
        Press(window, new Point(640, 400));
        Assert.Equal(3, drags);
        window.Close();
    }

    [AvaloniaFact]
    public void InteractiveControlsInsideTheBandAreNotDragSurfaces()
    {
        var window = (MainWindow)LayoutProbe.Open(new SampleBackend(), 1280, 860, "presentation");
        LayoutProbe.Render(window);
        foreach (var name in new[] { "MinimizeButton", "MaximizeButton", "ThemeToDarkButton" })
            Assert.False(WindowCaption.IsDragSurface(window, Center(Named<Button>(window, name), window), WindowCaption.MainBandHeight), name);
        // 页面标题区第二行的分段开关在带以下，带的高度再大也不算拖动区。
        var segment = Named<Button>(window, "ShowOverlayButton");
        Assert.False(WindowCaption.IsDragSurface(window, Center(segment, window), 10_000));
        Assert.True(WindowCaption.IsDragSurface(window, new Point(900, 30), WindowCaption.MainBandHeight));
        Assert.False(WindowCaption.IsDragSurface(window, new Point(900, WindowCaption.MainBandHeight + 1), WindowCaption.MainBandHeight));
        window.Close();
    }

    [AvaloniaFact]
    public void DoubleClickOnBlankBandTogglesMaximize()
    {
        var window = (MainWindow)LayoutProbe.Open(new SampleBackend(), 1280, 860, "presentation");
        LayoutProbe.Render(window);
        DoubleClick(window, new Point(900, 30));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        LayoutProbe.Render(window);
        DoubleClick(window, new Point(900, 30));
        Assert.Equal(WindowState.Normal, window.WindowState);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese, "最大化", "还原")]
    [InlineData(UiLanguage.TraditionalChinese, "最大化", "還原")]
    public void MaximizeButtonFollowsActualWindowState(UiLanguage language, string maximize, string restore)
    {
        var window = (MainWindow)LayoutProbe.Open(new SampleBackend(), 1280, 860, "presentation", language);
        var button = Named<Button>(window, "MaximizeButton");
        Assert.True(Named<Path>(window, "MaximizeIcon").IsVisible);
        Assert.False(Named<Path>(window, "RestoreIcon").IsVisible);
        Assert.Equal(maximize, ToolTip.GetTip(button));
        Assert.Equal(maximize, AutomationProperties.GetName(button));

        // 状态由按钮以外的途径改变（快捷键、系统贴靠）时同样切换。
        window.WindowState = WindowState.Maximized;
        LayoutProbe.Settle();
        Assert.False(Named<Path>(window, "MaximizeIcon").IsVisible);
        Assert.True(Named<Path>(window, "RestoreIcon").IsVisible);
        Assert.Equal(restore, ToolTip.GetTip(button));
        Assert.Equal(restore, AutomationProperties.GetName(button));

        // 按下还原图标的按钮回到原来的状态。
        LayoutProbe.Render(window);
        Press(window, Center(button, window));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(maximize, ToolTip.GetTip(button));
        window.Close();
    }

    [AvaloniaFact]
    public void MiniWindowDragsFromBlankTopPanelOnly()
    {
        var backend = new SampleBackend();
        var main = LayoutProbe.Open(backend, 1280, 860, "music-library");
        var controller = ((ShellViewModel)main.DataContext!).Catalog.Mini!;
        ClickInPlace(main, Named<Button>(main, "OpenMiniButton"));
        var mini = Assert.IsType<MiniWindow>(controller.Window);
        mini.Width = 440;
        mini.Height = 920;
        LayoutProbe.Render(mini);
        var drags = 0;
        mini.CaptionDragStarting += (_, _) => drags++;

        // 顶部面板上方的窗口边距与面板内清单名一行都可拖动。
        Press(mini, new Point(200, 4));
        Press(mini, new Point(30, 30));
        Assert.Equal(2, drags);
        // 清单与按钮上不拖动。
        Press(mini, Center(mini.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("chip")
            && button.Name is null), mini));
        Assert.Equal(2, drags);
        Assert.False(WindowCaption.IsDragSurface(mini, Center(Named<Button>(mini, "CloseButton"), mini), mini.CaptionBandBottom));
        // 面板以下不算。
        Press(mini, new Point(200, mini.CaptionBandBottom + 20));
        Assert.Equal(2, drags);
        mini.Close();
        main.Close();
    }

    [Fact]
    public void MaximizedClientShrinksByFrameThickness()
    {
        // 96 DPI 下系统把最大化窗口放到工作区外 7 像素，客户区收回后正好是工作区。
        Assert.Equal(new PixelRect(0, 0, 1920, 1032), SnapFrame.Inset(new PixelRect(-7, -7, 1934, 1046), new Thickness(7)));
        // 125% 显示器在主显示器左侧。
        Assert.Equal(new PixelRect(-1920, 0, 1536, 816), SnapFrame.Inset(new PixelRect(-1926, -6, 1548, 828), new Thickness(6)));
        Assert.Equal(0, SnapFrame.Inset(new PixelRect(0, 0, 10, 10), new Thickness(8)).Width);
    }

    private static void Press(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        LayoutProbe.Settle();
    }

    private static void DoubleClick(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        LayoutProbe.Settle();
    }
}
