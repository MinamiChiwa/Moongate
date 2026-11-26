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

/// <summary>外壳：窗口按钮、语言切换与随包字体、紧急停止、滚动区淡出、右上角更新提示。</summary>
public sealed class ShellTests
{
    [AvaloniaTheory]
    [InlineData(760)]
    [InlineData(1680)]
    public void WindowButtonsSitAtTopRightInWindowsOrder(double width)
    {
        var window = LayoutProbe.Open(new SampleBackend(), width, 700, "combat-observation");
        var group = window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "WindowButtons");
        var origin = group.TranslatePoint(default, window)!.Value;
        Assert.InRange(window.Bounds.Width - (origin.X + group.Bounds.Width), 0, 24);
        Assert.InRange(origin.Y, 0, 24);

        // 三个窗口按钮按最小化、最大化、关闭排在最右；主题按钮在它们左侧，中间空隙比按钮间距大。
        var all = group.GetVisualChildren().OfType<Button>().Where(button => button.IsVisible).ToList();
        Assert.Equal(4, all.Count);
        Assert.StartsWith("ThemeTo", all[0].Name);
        var buttons = all.Skip(1).ToList();
        Assert.Equal("MinimizeButton", buttons[0].Name);
        Assert.Contains("close", buttons[2].Classes);
        var gap = buttons[0].Bounds.Left - all[0].Bounds.Right;
        Assert.True(gap > buttons[1].Bounds.Left - buttons[0].Bounds.Right && gap >= 11, $"主题按钮与窗口按钮的空隙 {gap}");

        Click(window, buttons[1]);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Click(window, buttons[1]);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Click(window, buttons[0]);
        Assert.Equal(WindowState.Minimized, window.WindowState);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese, "Noto Sans SC")]
    [InlineData(UiLanguage.TraditionalChinese, "Noto Sans TC")]
    public void InterfaceUsesBundledFontWithDistinctWeights(UiLanguage language, string family)
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation", language);
        var title = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("h1"));
        var typeface = new Typeface(title.FontFamily, FontStyle.Normal, title.FontWeight);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));
        Assert.StartsWith(family, glyphs!.FamilyName);
        Assert.Equal(FontWeight.Black, glyphs.Weight);

        double Width(FontWeight weight) => new TextBlock
        {
            Text = "戰鬥觀測 战斗观测 18,420", FontFamily = title.FontFamily, FontSize = 34, FontWeight = weight
        }.MeasureWidth();
        Assert.True(Width(FontWeight.Black) > Width(FontWeight.Bold));
        Assert.True(Width(FontWeight.Bold) > Width(FontWeight.Normal));
        window.Close();
    }

    [AvaloniaFact]
    public void EmergencyStopButtonSendsOneRequestToBackend()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860);
        // 外壳的紧急停止按钮在底部活动胶囊里；页面（DPS检测页的取消等）也有 stop 类的按钮，不在查找范围内。
        var stop = Named<Border>(window, "ActivityCapsule").GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("stop"));
        Click(window, stop);
        Assert.Equal(1, backend.EmergencyStopRequests);
        window.Close();
    }

    [AvaloniaFact]
    public void ScrollAreasFadeAtTheEdgeWhereContentContinues()
    {
        var window = LayoutProbe.Open(new LongTextBackend(), 760, 600, "combat-observation");
        var navigation = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "NavigationScroller");
        var page = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PageScroller");
        Assert.True(navigation.Extent.Height > navigation.Viewport.Height);
        Assert.Equal(Colors.Black, EdgeColors(navigation).Top);
        Assert.Equal(Colors.Transparent, EdgeColors(navigation).Bottom);
        Assert.Equal(Colors.Transparent, EdgeColors(page).Bottom);

        page.Offset = new Vector(0, page.Extent.Height);
        LayoutProbe.Settle();
        Assert.Equal(Colors.Transparent, EdgeColors(page).Top);
        Assert.Equal(Colors.Black, EdgeColors(page).Bottom);
        LayoutProbe.SaveScreenshot(window, "combat-scrolled-760x600");
        window.Close();

        // 默认尺寸 1280×860 下九个内建入口一屏放下：不滚动、不渐隐，最后一个入口完整在视口内。
        var tall = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        var fitting = tall.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "NavigationScroller");
        var last = Nav(tall, "presentation");
        var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), fitting)!.Value.Y;
        Assert.True(fitting.Extent.Height <= fitting.Viewport.Height + 0.5 && bottom <= fitting.Viewport.Height,
            $"九个内建入口放不下：内容 {fitting.Extent.Height}，视口 {fitting.Viewport.Height}，最后一个入口止于 {bottom}");
        Assert.Null(fitting.OpacityMask);
        // 窗口显示后再拉高，侧栏放得下时同样不出现渐隐。
        tall.Height = 1300;
        LayoutProbe.Settle();
        var roomy = tall.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "NavigationScroller");
        Assert.Null(roomy.OpacityMask);
        tall.Close();
    }

    [AvaloniaFact]
    public void InterfaceStartsInSimplifiedSwitchesToTraditionalAndKeepsGameNames()
    {
        string[] gameNames = ["琉特", "重擊", "連續射擊", "火球術", "戰鬥吟唱", "破甲", "緩速"];
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        // 明细表在 1280×860 下位于首屏以下，是虚拟化表格：先滚到表格处，行才生成。
        void RevealDamageTable()
        {
            Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "DamageTable").BringIntoView();
            LayoutProbe.Render(window);
        }
        // 效果名称由效果行自绘，不在文字块里，按各行显示的名称取。
        HashSet<string> Shown() => [.. VisibleTexts(window), .. window.GetVisualDescendants().OfType<MabiTool.Desktop.Pages.Combat.EffectLine>()
            .Where(line => line.IsEffectivelyVisible && line.Row is not null).Select(line => line.Row!.Name)];
        try
        {
            RevealDamageTable();
            var simplified = VisibleTexts(window);
            Assert.Contains("DPS检测", simplified);
            Assert.Contains("紧急停止", simplified);
            Assert.Contains("暴击", simplified);
            Assert.DoesNotContain("DPS檢測", simplified);
            // 游戏来源的名称在两种语言下都保持原文。
            Assert.Subset(Shown(), gameNames.ToHashSet());

            Click(window, Named<Button>(window, "LanguageTraditional"));

            RevealDamageTable();
            var traditional = VisibleTexts(window);
            Assert.Equal(UiLanguage.TraditionalChinese, UiText.Language);
            Assert.Contains("DPS檢測", traditional);
            Assert.Contains("緊急停止", traditional);
            Assert.Contains("暴擊", traditional);
            Assert.DoesNotContain("DPS检测", traditional);
            Assert.Subset(Shown(), gameNames.ToHashSet());
        }
        finally
        {
            window.Close();
            ResetAppearance();
        }
    }

    private static (Color Top, Color Bottom) EdgeColors(ScrollViewer scroller)
    {
        var mask = Assert.IsType<LinearGradientBrush>(scroller.OpacityMask);
        return (mask.GradientStops[0].Color, mask.GradientStops[^1].Color);
    }

    private static Button Badge(Window window) => Named<Button>(window, "UpdateBadge");

    [AvaloniaFact]
    public void BadgeAppearsOnlyWhenAutoCheckFindsANewRelease()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        try
        {
            // 没有新版本：不显示、不占位，按钮组宽度不变。
            var buttons = Named<StackPanel>(window, "WindowButtons");
            var narrow = buttons.Bounds.Width;
            Assert.False(Badge(window).IsVisible);

            backend.AvailableRelease = "1.2.0";
            backend.RaiseChanged();
            LayoutProbe.Render(window);
            Assert.True(Badge(window).IsEffectivelyVisible);
            Assert.Equal("有新版本 1.2.0", ((ShellViewModel)window.DataContext!).UpdateLabel);
            // 在主题按钮左侧，间隔与其他按钮相同。
            var theme = Named<Button>(window, "ThemeToDarkButton");
            var badgeRight = Badge(window).TranslatePoint(new Point(Badge(window).Bounds.Width, 0), window)!.Value.X;
            var themeLeft = theme.TranslatePoint(default, window)!.Value.X;
            Assert.Equal(buttons.Spacing, themeLeft - badgeRight, 1);
            Assert.True(buttons.Bounds.Width > narrow);
            // 页面标题不与按钮重叠。
            var title = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("h1") && text.IsEffectivelyVisible);
            var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), window)!.Value.X;
            var buttonsLeft = buttons.TranslatePoint(default, window)!.Value.X;
            Assert.True(titleRight <= buttonsLeft, $"标题右缘 {titleRight} 与按钮左缘 {buttonsLeft} 重叠");

            // 关闭自动检查后不显示。
            Click(window, Nav(window, "shell.settings"));
            Click(window, Named<Button>(window, "AutoCheckOffButton"));
            LayoutProbe.Render(window);
            Assert.False(Badge(window).IsVisible);
            Click(window, Named<Button>(window, "AutoCheckOnButton"));
            LayoutProbe.Render(window);
            Assert.True(Badge(window).IsVisible);

            // 后端不再报告新版本时消失。
            backend.AvailableRelease = null;
            backend.RaiseChanged();
            LayoutProbe.Render(window);
            Assert.False(Badge(window).IsVisible);
            Assert.Equal(narrow, buttons.Bounds.Width, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BadgeOpensSettingsScrolledToUpdates()
    {
        var backend = new SampleBackend { AvailableRelease = "1.2.0" };
        var window = LayoutProbe.Open(backend, 900, 600, "combat-observation");
        try
        {
            Click(window, Badge(window));
            LayoutProbe.Render(window);
            LayoutProbe.Render(window);
            Assert.Equal("shell.settings", ((ShellViewModel)window.DataContext!).Selected.Module.Id);
            var page = Page<SettingsPage>(window);
            var scroller = page.GetVisualDescendants().OfType<ScrollViewer>().Single(item => item.Name == "PageScroller");
            var panel = page.GetVisualDescendants().OfType<Card>().Single(item => item.Name == "UpdatePanel");
            Assert.True(scroller.Offset.Y > 0, "设置页没有滚到更新区");
            var top = panel.TranslatePoint(default, scroller)!.Value.Y;
            Assert.InRange(top, -1, 30);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BadgeColorsReadInBothThemes()
    {
        foreach (var theme in new[] { UiThemeChoice.Light, UiThemeChoice.Dark })
        {
            var window = LayoutProbe.Open(new SampleBackend { AvailableRelease = "1.2.0" }, 1280, 860, theme: theme);
            try
            {
                var badge = Badge(window);
                var background = ((ISolidColorBrush)badge.Background!).Color;
                var foreground = ((ISolidColorBrush)badge.Foreground!).Color;
                Assert.True(Contrast(foreground, background) >= 4.5,
                    $"{theme} 下更新提示的对比度 {Contrast(foreground, background):F2}");
            }
            finally
            {
                window.Close();
                UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
            }
        }
    }
}
