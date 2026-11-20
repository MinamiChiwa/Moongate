using Avalonia.Platform;
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

/// <summary>弹出层的底色完全不透明：渲染出的像素等于 PopupBrush，与其下方的页面内容无关。</summary>
public sealed class ThemeAndMotionPopupTests
{
    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static (byte R, byte G, byte B)[] Render(Window window, IReadOnlyList<Point> points)
    {
        LayoutProbe.Settle();
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var bgra = buffer.Format == PixelFormat.Bgra8888;
        return points.Select(point =>
        {
            var offset = (int)point.Y * buffer.RowBytes + (int)point.X * 4;
            byte At(int index) => System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address + offset + index);
            return bgra ? (At(2), At(1), At(0)) : (At(0), At(1), At(2));
        }).ToArray();
    }

    /// <summary>
    /// 弹出层内没有任何内容的点（命中的就是弹出层自己）：逐点与 PopupBrush 比较，
    /// 并把下方页面内容隐藏前后各渲染一次，像素必须一致。
    /// </summary>
    private static void AssertOpaque(Window window, Border popup, Visual underlying)
    {
        Assert.True(window.TryFindResource("PopupBrush", window.ActualThemeVariant, out var resource));
        var brush = Assert.IsType<SolidColorBrush>(resource);
        Assert.Equal(255, brush.Color.A);
        var origin = popup.TranslatePoint(default, window)!.Value;
        // 弹出层自己的区域里，避开所有有内容的控件（文字、按钮、内嵌块、图形、自绘的说明问号），剩下的就是纯底色。
        var occupied = popup.GetVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && control is TextBlock or Button or Border or Avalonia.Controls.Shapes.Shape or TextBox or HelpTip)
            .Select(control => new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size).Inflate(1))
            .ToList();
        var points = new List<Point>();
        for (var y = 16.0; y < popup.Bounds.Height - 16; y += 3)
            for (var x = 16.0; x < popup.Bounds.Width - 16; x += 3)
            {
                var point = new Point(origin.X + x, origin.Y + y);
                if (!occupied.Any(rect => rect.Contains(point))) points.Add(point);
            }
        Assert.True(points.Count >= 10, $"弹出层内空白点太少：{points.Count}");

        var shown = Render(window, points);
        // 下方页面内容隐藏前后各渲染一次。
        var before = underlying.Opacity;
        underlying.Opacity = 0;
        var hidden = Render(window, points);
        underlying.Opacity = before;

        for (var index = 0; index < points.Count; index++)
        {
            Assert.InRange(Math.Abs(shown[index].R - brush.Color.R), 0, 2);
            Assert.InRange(Math.Abs(shown[index].G - brush.Color.G), 0, 2);
            Assert.InRange(Math.Abs(shown[index].B - brush.Color.B), 0, 2);
            Assert.InRange(Math.Abs(shown[index].R - hidden[index].R), 0, 1);
            Assert.InRange(Math.Abs(shown[index].G - hidden[index].G), 0, 1);
            Assert.InRange(Math.Abs(shown[index].B - hidden[index].B), 0, 1);
        }
    }

    [AvaloniaTheory]
    [InlineData(UiThemeChoice.Light)]
    [InlineData(UiThemeChoice.Dark)]
    public void GamePickerLayerHidesWhatIsBelowIt(UiThemeChoice theme)
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "music-library", theme: theme);
        try
        {
            OpenPicker(window);
            var popup = Named<Border>(window, "GamePickerPanel");
            Assert.True(popup.IsEffectivelyVisible);
            var content = window.GetVisualDescendants().OfType<Grid>().First(grid => grid.ColumnDefinitions.Count == 2);
            AssertOpaque(window, popup, content);
        }
        finally
        {
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    [AvaloniaTheory]
    [InlineData(UiThemeChoice.Light)]
    [InlineData(UiThemeChoice.Dark)]
    public void MusicGameLayerHidesWhatIsBelowIt(UiThemeChoice theme)
    {
        var window = LayoutProbe.Open(new SampleBackend { CapabilityNotices = _ => null }, 1280, 860, "music-library", theme: theme);
        try
        {
            var page = window.GetVisualDescendants().OfType<MusicPage>().Single();
            ((MusicPageModel)page.DataContext!).OpenGameCommand.Execute(null);
            LayoutProbe.Settle();
            var popup = Named<Border>(page, "GamePanel");
            AssertOpaque(window, popup, page.GetVisualDescendants().OfType<DockPanel>().First());
        }
        finally
        {
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    [AvaloniaTheory]
    [InlineData(UiThemeChoice.Light)]
    [InlineData(UiThemeChoice.Dark)]
    public void GameDataLayerHidesWhatIsBelowIt(UiThemeChoice theme)
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "presentation", theme: theme);
        try
        {
            var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
            ((OverlayPageModel)page.DataContext!).OpenGameDataCommand.Execute(null);
            LayoutProbe.Settle();
            var popup = Named<Border>(page, "GameDataPanel");
            AssertOpaque(window, popup, page.GetVisualDescendants().OfType<DockPanel>().First());
        }
        finally
        {
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }
}
