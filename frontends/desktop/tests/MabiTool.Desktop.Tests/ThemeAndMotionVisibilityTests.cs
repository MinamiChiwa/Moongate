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

/// <summary>动效在“开启”与“减弱”之间切换时，各页内容都实际画出来。</summary>
public sealed class ThemeAndMotionVisibilityTests
{
    private static readonly string[] Pages =
        ["combat-observation", "rooms", "color", "inventory", "presentation", "music-library", "shell.modules", "shell.settings"];

    private static double EffectiveOpacity(Visual visual)
    {
        var opacity = visual.Opacity;
        foreach (var ancestor in visual.GetVisualAncestors()) opacity *= ancestor.Opacity;
        return opacity;
    }

    // 透明度不为 1 的那几层，失败时写进说明。
    private static string Faded(Visual visual) => string.Join(" < ", visual.GetVisualAncestors().Prepend(visual)
        .Where(layer => layer.Opacity < 0.99).Select(layer => $"{layer.GetType().Name}#{(layer as Control)?.Name}={layer.Opacity:F2}"));

    private static void Click(Window window, string moduleId)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.DataContext is NavigationItem item && item.Module.Id == moduleId && b.Classes.Contains("nav"));
        TestUi.Click(window, button);
    }

    /// <summary>
    /// 当前页面的主要内容可见：页面与它的每一块面板有效透明度为 1，且页面区域渲染出的像素有足够的变化（不是空白底色）。
    /// </summary>
    internal static void AssertPageVisible(Window window, string moduleId)
    {
        var host = Named<PageHost>(window, "PageHost");
        var page = (Control)((ShellViewModel)window.DataContext!).CurrentPage!;
        Assert.True(page.IsEffectivelyVisible, $"{moduleId}：页面不可见");
        Assert.True(Math.Abs(EffectiveOpacity(page) - 1) < 0.01, $"{moduleId}：页面有效透明度 {EffectiveOpacity(page):F2}（{Faded(page)}）");
        var panels = page.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("panel") && !border.Classes.Contains("popup") && border.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(panels);
        foreach (var panel in panels)
            Assert.True(Math.Abs(EffectiveOpacity(panel) - 1) < 0.01, $"{moduleId}：面板有效透明度 {EffectiveOpacity(panel):F2}（{Faded(panel)}）");

        // 渲染一帧，取页面区域的像素：内容实际画出时，深浅不同的像素占相当比例。
        var origin = host.TranslatePoint(default, window)!.Value;
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var bright = new List<int>();
        for (var y = origin.Y + 70; y < origin.Y + host.Bounds.Height - 10; y += 4)
            for (var x = origin.X + 10; x < origin.X + host.Bounds.Width - 10; x += 4)
            {
                var offset = (int)y * buffer.RowBytes + (int)x * 4;
                var sum = 0;
                for (var channel = 0; channel < 3; channel++)
                    sum += System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address + offset + channel);
                bright.Add(sum);
            }
        var dark = bright.Count(value => value < 3 * 120);
        Assert.True(dark > bright.Count / 200, $"{moduleId}：页面区域几乎没有深色像素（文字），{dark}/{bright.Count}");
    }

    private static void AssertAllPages(Window window, bool clickWithMotion)
    {
        foreach (var page in Pages)
        {
            if (page == "music-library") LayoutProbe.Select(window, page);
            else Click(window, page);
            Pump(window, clickWithMotion ? 500 : 60);
            AssertPageVisible(window, page);
        }
    }

    /// <summary>减弱状态下启动并逐页切换，开启后逐页切换，运行中切到减弱再逐页切换，最后再切回开启。</summary>
    [AvaloniaFact]
    public void StartingReducedAndSwitchingWhileRunningKeepPagesVisible()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            AssertPageVisible(window, "combat-observation");
            AssertAllPages(window, clickWithMotion: false);
            UiMotion.Enabled = true;
            Pump(window);
            AssertAllPages(window, clickWithMotion: true);
            // 运行中从开启切到减弱：当前页仍可见，再逐页切换。
            UiMotion.Enabled = false;
            Pump(window, 60);
            AssertPageVisible(window, "shell.settings");
            AssertAllPages(window, clickWithMotion: false);
            // 再切回开启：带动效切一页仍可见。
            UiMotion.Enabled = true;
            Pump(window);
            Click(window, "rooms");
            Pump(window, 500);
            AssertPageVisible(window, "rooms");
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SwitchingToReducedMidTransitionKeepsPagesVisible()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            UiMotion.Enabled = true;
            Pump(window);
            Click(window, "rooms");
            // 页面切换与面板入场进行到一半时切到减弱。
            Pump(window, 80);
            UiMotion.Enabled = false;
            Pump(window, 60);
            AssertPageVisible(window, "rooms");
            AssertAllPages(window, clickWithMotion: false);
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
        }
    }
}
