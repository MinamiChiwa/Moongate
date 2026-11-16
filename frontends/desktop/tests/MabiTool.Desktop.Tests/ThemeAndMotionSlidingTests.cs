using System.Diagnostics;
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

/// <summary>侧栏与各处单选开关的选中底色在选中项之间滑动。</summary>
public sealed class ThemeAndMotionSlidingTests
{
    private static Control Sidebar(Window window) => Named<SelectionBorder>(window, "Sidebar");

    private static Rect RectIn(Visual visual, Visual host) =>
        new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(host) ?? Matrix.Identity);

    // 静止：没有滑块，选中项自己的底色与边框没有被压住。
    internal static void AssertAtRest(Button selected)
    {
        var host = SlidingSelection.HostOf(selected)!;
        Assert.False(SlidingSelection.IsSliding(host));
        Assert.Null(SlidingSelection.SliderRect(host));
        Assert.False(ReferenceEquals(selected.Background, Avalonia.Media.Brushes.Transparent), "选中项的底色仍被滑动压住");
    }

    // 推进真实时间与渲染帧，直到容器里的滑动结束。
    internal static void FinishSliding(Control host)
    {
        var waited = Stopwatch.StartNew();
        while (SlidingSelection.IsSliding(host) && waited.Elapsed < TimeSpan.FromSeconds(3))
        {
            Thread.Sleep(16);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            LayoutProbe.Settle();
        }
        Assert.False(SlidingSelection.IsSliding(host), "滑动没有在 3 秒内结束");
    }

    internal static Window OpenWithMotion(IDesktopBackend backend, string? module = null, double width = 1280, double height = 860,
        UiThemeChoice theme = UiThemeChoice.Light)
    {
        var window = LayoutProbe.Open(backend, width, height, module, theme: theme);
        UiMotion.Enabled = true;
        LayoutProbe.Settle();
        Assert.Contains("motion", window.Classes);
        return window;
    }

    private static bool Near(Rect a, Rect b, double tolerance = 4) =>
        Math.Abs(a.X - b.X) < tolerance && Math.Abs(a.Y - b.Y) < tolerance
        && Math.Abs(a.Width - b.Width) < tolerance && Math.Abs(a.Height - b.Height) < tolerance;

    // 执行一次操作，记下其间开始的滑动，以及滑动开始那一刻滑块所在的位置。
    // 无界面模式下渲染计时器按真实时间走，操作结束时滑动可能已经走完，所以在开始时取样。
    private static (SlideStartedEventArgs? Slide, Rect AtStart) Observe(Control? host, Action action)
    {
        SlideStartedEventArgs? slide = null;
        var atStart = default(Rect);
        EventHandler<SlideStartedEventArgs> handler = (sender, e) =>
        {
            if (host is not null && sender != host) return;
            slide = e;
            atStart = SlidingSelection.SliderRect((Control)sender!) ?? default;
        };
        SlidingSelection.Started += handler;
        try
        {
            action();
        }
        finally { SlidingSelection.Started -= handler; }
        return (slide, atStart);
    }

    // 点击开始滑动：滑块从原选中项出发，走完后选中项自己画出底色。
    internal static void ClickAndAssertSlide(Window window, Button from, Func<Button> to)
    {
        var target = to();
        var host = SlidingSelection.HostOf(target)!;
        Assert.Same(host, SlidingSelection.HostOf(from));
        var start = RectIn(from, host);
        var end = RectIn(target, host);
        var (slide, atStart) = Observe(host, () =>
        {
            Click(window, target);
            // 画一帧：选中变化之后的排版在这一帧里完成，滑动随之开始。
            LayoutProbe.Render(window);
        });
        var selected = to();
        Assert.True(SlidingSelection.GetIsSelected(selected));
        Assert.NotNull(slide);
        Assert.Same(selected, slide.Target);
        Assert.True(Near(slide.From, start), $"滑动起点 {slide.From} 应是原选中项 {start}");
        Assert.True(Near(atStart, start), $"滑动开始时滑块应在原选中项 {start}，实际在 {atStart}");
        Assert.False(Near(atStart, end, 1), "滑动开始时滑块不应已在新选中项上");
        Assert.True(Near(slide.To, RectIn(selected, host), 1), $"滑动终点 {slide.To} 应是新选中项 {RectIn(selected, host)}");
        FinishSliding(host);
        AssertAtRest(selected);
    }

    // 进度取到缓动后恰好走了一半的时刻。
    private static double HalfwayProgress()
    {
        double low = 0, high = 1;
        for (var step = 0; step < 40; step++)
        {
            var middle = (low + high) / 2;
            if (SlidingSelection.Ease(middle) < 0.5) low = middle;
            else high = middle;
        }
        return low;
    }

    private static int[] Pixel(Window window, Point point)
    {
        LayoutProbe.Settle();
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var bgra = buffer.Format == PixelFormat.Bgra8888;
        var sum = new int[3];
        // 取 3×3 的平均，避开抗锯齿的单点误差。
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var offset = ((int)point.Y + dy) * buffer.RowBytes + ((int)point.X + dx) * 4;
                byte At(int index) => System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address + offset + index);
                sum[0] += bgra ? At(2) : At(0);
                sum[1] += At(1);
                sum[2] += bgra ? At(0) : At(2);
            }
        return [sum[0] / 9, sum[1] / 9, sum[2] / 9];
    }

    private static int Distance(int[] a, int[] b) => Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);

    /// <summary>
    /// 滑动停在一半时渲染一帧：两项之间（经过的那一项上、不与起点和终点重叠的位置）实际画出了滑块，
    /// 与滑动前同一位置的像素明显不同；滑动结束后该处恢复原样。
    /// </summary>
    private static void AssertSliderDrawnBetween(Window window, Button from, Button to, Button between, Func<Rect, Point> probeIn)
    {
        var host = SlidingSelection.HostOf(to)!;
        var probeRect = RectIn(between, window);
        var probe = probeIn(probeRect);
        Assert.False(RectIn(from, window).Contains(probe));
        Assert.False(RectIn(to, window).Contains(probe));
        var before = Pixel(window, probe);
        SlidingSelection.FrozenProgress = HalfwayProgress();
        try
        {
            var (slide, _) = Observe(host, () =>
            {
                Click(window, to);
                LayoutProbe.Render(window);
            });
            Assert.NotNull(slide);
            LayoutProbe.Render(window);
            var slider = SlidingSelection.SliderRect(host)!.Value;
            var sliderInWindow = slider.Translate((Vector)host.TranslatePoint(default, window)!.Value);
            Assert.True(sliderInWindow.Contains(probe), $"滑块 {sliderInWindow} 应经过 {probe}");
            var during = Pixel(window, probe);
            Assert.True(Distance(before, during) > 30,
                $"滑动到一半时两项之间没有画出滑块：前 {string.Join(',', before)}，中 {string.Join(',', during)}");
        }
        finally { SlidingSelection.FrozenProgress = null; }
        FinishSliding(host);
        AssertAtRest(to);
        LayoutProbe.Render(window);
        Assert.True(Distance(before, Pixel(window, probe)) < 12, "滑动结束后经过的位置应恢复原样");
    }

    [Fact]
    public void EasingStartsAtZeroEndsAtOneAndOvershootsOnlySlightly()
    {
        Assert.Equal(0, SlidingSelection.Ease(0), 6);
        Assert.Equal(1, SlidingSelection.Ease(1), 6);
        var samples = Enumerable.Range(0, 201).Select(step => SlidingSelection.Ease(step / 200d)).ToList();
        Assert.InRange(samples.Max(), 1, 1.02);
        // 前段是减速：头 30% 的时间已走完一大半。
        Assert.True(samples[60] > 0.6);
        Assert.InRange(SlidingSelection.Duration.TotalMilliseconds, 200, 280);
    }

    [AvaloniaFact]
    public void WithMotionOffTheSidebarSelectionMovesImmediately()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            AssertAtRest(Nav(window, "combat-observation"));
            var (slide, _) = Observe(null, () =>
            {
                Click(window, Nav(window, "rooms"));
                LayoutProbe.Render(window);
            });
            Assert.Null(slide);
            AssertAtRest(Nav(window, "rooms"));
            Click(window, Nav(window, "shell.settings"));
            AssertAtRest(Nav(window, "shell.settings"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarSelectionSlidesBetweenEntriesAndGroups()
    {
        var window = OpenWithMotion(new SampleBackend(), "combat-observation");
        try
        {
            AssertAtRest(Nav(window, "combat-observation"));
            ClickAndAssertSlide(window, Nav(window, "combat-observation"), () => Nav(window, "color"));
            // 从模块入口滑到底部分组的“设置”。
            ClickAndAssertSlide(window, Nav(window, "color"), () => Nav(window, "shell.settings"));
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SidebarSliderIsDrawnBetweenEntriesMidway()
    {
        // 深色下选中底色与面板的差别更大，像素比较更稳。
        var window = OpenWithMotion(new SampleBackend(), "combat-observation", theme: UiThemeChoice.Dark);
        try
        {
            AssertSliderDrawnBetween(window, Nav(window, "combat-observation"), Nav(window, "color"), Nav(window, "rooms"),
                rect => new Point(rect.Right - 12, rect.Center.Y));
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    [AvaloniaFact]
    public void SegmentSliderIsDrawnBetweenItemsMidway()
    {
        var window = OpenWithMotion(new SampleBackend(), "shell.settings");
        try
        {
            var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
            Button Named(string name) => page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
            AssertSliderDrawnBetween(window, Named("ThemeLightButton"), Named("ThemeSystemButton"), Named("ThemeDarkButton"),
                rect => new Point(rect.X + 6, rect.Center.Y));
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    [AvaloniaFact]
    public void KeyboardActivationSlidesToo()
    {
        var window = OpenWithMotion(new SampleBackend(), "combat-observation");
        try
        {
            var rooms = Nav(window, "rooms");
            var host = Sidebar(window);
            var start = RectIn(Nav(window, "combat-observation"), host);
            rooms.Focus(NavigationMethod.Tab);
            var (slide, atStart) = Observe(host, () =>
            {
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                LayoutProbe.Render(window);
            });
            Assert.True(SlidingSelection.GetIsSelected(rooms));
            Assert.NotNull(slide);
            Assert.Same(rooms, slide.Target);
            Assert.True(Near(atStart, start), $"滑动开始时滑块应在原选中项 {start}，实际在 {atStart}");
            FinishSliding(host);
            AssertAtRest(rooms);
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FirstShowResizeLanguageAndProgrammaticChangesDoNotSlide()
    {
        var window = OpenWithMotion(new SampleBackend(), "combat-observation", 900, 700);
        try
        {
            var host = Sidebar(window);
            var (slide, _) = Observe(null, () => FirstShowResizeLanguageAndProgrammaticChanges(window, host));
            Assert.Null(slide);
        }
        finally
        {
            UiMotion.Enabled = false;
            UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
            window.Close();
        }
    }

    private static void FirstShowResizeLanguageAndProgrammaticChanges(Window window, Control host)
    {
        Assert.False(SlidingSelection.IsSliding(host));
        AssertAtRest(Nav(window, "combat-observation"));

        window.Width = 1680;
        window.Height = 1000;
        LayoutProbe.Render(window);
        Assert.False(SlidingSelection.IsSliding(host));
        AssertAtRest(Nav(window, "combat-observation"));

        // 不经点击的选中变化（等同后端状态或程序切换）直接到位。
        LayoutProbe.Select(window, "rooms");
        LayoutProbe.Render(window);
        Assert.False(SlidingSelection.IsSliding(host));
        AssertAtRest(Nav(window, "rooms"));

        UiText.Apply(Application.Current!, UiLanguage.TraditionalChinese);
        LayoutProbe.Render(window);
        Assert.False(SlidingSelection.IsSliding(host));
        AssertAtRest(Nav(window, "rooms"));

        // 选中收起分组里的模块：分组展开，选中项直接画出底色。
        var shell = (ShellViewModel)window.DataContext!;
        Assert.False(shell.IsPendingOpen);
        LayoutProbe.Select(window, shell.PendingModules[0].Module.Id);
        LayoutProbe.Render(window);
        var pending = Nav(window, shell.PendingModules[0].Module.Id);
        Assert.True(pending.IsEffectivelyVisible);
        AssertAtRest(pending);
    }

    [AvaloniaFact]
    public void CombatBreakdownSegmentSlides()
    {
        var window = OpenWithMotion(new SampleBackend(), "combat-observation");
        try
        {
            var page = window.GetVisualDescendants().OfType<CombatPage>().Single();
            Button ByTarget() => page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ByTargetButton");
            Button BySkill() => page.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("segment") && button != ByTarget() && SlidingSelection.HostOf(button) == SlidingSelection.HostOf(ByTarget()));
            AssertAtRest(BySkill());
            ClickAndAssertSlide(window, BySkill(), ByTarget);
            Assert.True(((CombatPageModel)page.DataContext!).ByTarget);
            ClickAndAssertSlide(window, ByTarget(), BySkill);
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsThemeSegmentSlidesBetweenItemsOfDifferentWidth()
    {
        var window = OpenWithMotion(new SampleBackend(), "shell.settings");
        try
        {
            var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
            Button Named(string name) => page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
            Assert.NotEqual(Named("ThemeLightButton").Bounds.Width, Named("ThemeSystemButton").Bounds.Width);
            ClickAndAssertSlide(window, Named("ThemeLightButton"), () => Named("ThemeSystemButton"));
            ClickAndAssertSlide(window, Named("ThemeSystemButton"), () => Named("ThemeLightButton"));
            // 由列表生成的分段（检查间隔）同样滑动：点击后整列按钮重新生成。
            List<Button> Intervals() => Named<ItemsControl>(window, "IntervalChoices").GetVisualDescendants().OfType<Button>().ToList();
            var current = Intervals().Single(button => SlidingSelection.GetIsSelected(button));
            var label = Intervals().First(button => !SlidingSelection.GetIsSelected(button)).Content;
            ClickAndAssertSlide(window, current, () => Intervals().Single(button => Equals(button.Content, label)));
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    [AvaloniaFact]
    public void WithMotionOffSegmentsSwitchImmediately()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.settings");
        try
        {
            var page = window.GetVisualDescendants().OfType<SettingsPage>().Single();
            Button Named(string name) => page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
            var (slide, _) = Observe(null, () =>
            {
                Click(window, Named("ThemeDarkButton"));
                LayoutProbe.Render(window);
            });
            Assert.Null(slide);
            AssertAtRest(Named("ThemeDarkButton"));
        }
        finally
        {
            window.Close();
            UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        }
    }

    // 页面上某个选择容器里的单选按钮：按数据上下文类型与选中状态取。
    private static List<Button> Choices<T>(Visual page) => page.GetVisualDescendants().OfType<Button>()
        .Where(button => button.DataContext is T && SlidingSelection.HostOf(button) is not null).ToList();

    [AvaloniaFact]
    public void FilterAndListChoicesSlide()
    {
        var window = OpenWithMotion(new SampleBackend(), "combat-observation");
        try
        {
            // 战斗页怪物效果的目标切换。
            var combat = window.GetVisualDescendants().OfType<CombatPage>().Single();
            // 先让怪物效果面板整体进入视野，目标按钮不落在底部活动胶囊的下面。
            combat.GetVisualDescendants().OfType<Card>().Single(border => border.Name == "TargetEffectsPanel").BringIntoView();
            LayoutProbe.Render(window);
            var targets = Choices<TargetChip>(combat);
            Assert.True(targets.Count >= 2);
            var targetId = ((TargetChip)targets.First(button => !SlidingSelection.GetIsSelected(button)).DataContext!).Id;
            ClickAndAssertSlide(window, targets.Single(SlidingSelection.GetIsSelected),
                () => Choices<TargetChip>(combat).Single(button => ((TargetChip)button.DataContext!).Id == targetId));

            // 乐谱库的清单选择。
            LayoutProbe.Select(window, "music-library");
            FinishPageSwitch(window);
            var music = window.GetVisualDescendants().OfType<MusicPage>().Single();
            var playlists = Choices<PlaylistChip>(music);
            Assert.True(playlists.Count >= 2);
            var playlistId = ((PlaylistChip)playlists.First(button => !SlidingSelection.GetIsSelected(button)).DataContext!).Id;
            ClickAndAssertSlide(window, playlists.Single(SlidingSelection.GetIsSelected),
                () => Choices<PlaylistChip>(music).Single(button => ((PlaylistChip)button.DataContext!).Id == playlistId));
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
        }
    }

    private static void FinishPageSwitch(Window window) => Pump(window, 400);

    [AvaloniaFact]
    public void MiniWindowPlaylistChoiceSlides()
    {
        var window = OpenMini(new SampleBackend(), 440, 920, UiThemeChoice.Light, UiLanguage.SimplifiedChinese);
        UiMotion.Enabled = true;
        LayoutProbe.Settle();
        try
        {
            List<Button> Chips() => Choices<PlaylistChip>(window);
            Assert.True(Chips().Count >= 2);
            var id = ((PlaylistChip)Chips().First(button => !SlidingSelection.GetIsSelected(button)).DataContext!).Id;
            ClickAndAssertSlide(window, Chips().Single(SlidingSelection.GetIsSelected),
                () => Chips().Single(button => ((PlaylistChip)button.DataContext!).Id == id));
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
        }
    }
}
