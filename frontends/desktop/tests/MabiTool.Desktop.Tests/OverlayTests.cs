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

/// <summary>浮窗版面页与游戏浮窗：元件管理、在屏幕上编辑、拖动对齐与跨显示器、多套版面、贴边、按显示器记忆位置、显示开关、需要游戏数据的元件、逐元件的锁、浮窗上直接拖动与缩放、接收鼠标区域。</summary>
public sealed class OverlayTests
{
    [AvaloniaFact]
    public void EditorPlacesSelectsScalesAndRemovesWidgets()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "presentation");
        var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
        var model = (OverlayPageModel)page.DataContext!;
        // 版面页没有预览画布，元件在实际屏幕上摆放。
        Assert.Empty(page.GetVisualDescendants().OfType<OverlayCanvas>());
        List<string> Placed() => model.Store.Active.Widgets.Select(widget => widget.WidgetId).ToList();
        Assert.Equal(["rooms.readiness"], Placed());

        Button Library(string id) => page.GetVisualDescendants().OfType<Button>()
            .Single(button => button.CommandParameter as string == id && button.Classes.Contains("tile"));
        Click(window, Library("combat.dps"));
        Assert.Contains("combat.dps", Placed());
        Assert.Equal("combat.dps", model.SelectedWidgetId);
        Assert.True(model.HasSelection);

        Click(window, page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ScaleUp"));
        Click(window, page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ScaleUp"));
        Assert.Equal(1.2, model.Store.Active.Widgets.Single(widget => widget.WidgetId == "combat.dps").Scale, 3);
        Assert.Equal("120%", model.ScaleText.Replace(" ", string.Empty));

        var solid = page.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == "solid");
        Click(window, solid);
        Assert.Equal("solid", model.Store.Active.Widgets.Single(widget => widget.WidgetId == "combat.dps").Skin);

        Click(window, page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoveWidgetButton"));
        Assert.DoesNotContain("combat.dps", Placed());
        Assert.True(model.NoSelection);
        Assert.Empty(LayoutProbe.FindTextOverflow(window));
        window.Close();
    }

    [AvaloniaFact]
    public void LayoutsAreSeparateAndSurviveRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "moongate-layouts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new OverlayLayoutStore(path);
            Assert.Equal(2, store.Layouts.Count);
            var created = store.Add("钓鱼");
            var placed = store.Place("combat.dps");
            (placed.Scale, placed.Locked, placed.Width, placed.Height) = (9, false, 320, 200);
            store.Commit();
            store.Select("raid");

            var reopened = new OverlayLayoutStore(path);
            Assert.Equal("raid", reopened.Active.Id);
            var saved = reopened.Layouts.Single(layout => layout.Id == created.Id);
            Assert.Equal("钓鱼", saved.Name);
            // 锁状态按元件保存；其他版面的元件仍是已上锁。
            Assert.False(saved.Widgets.Single().Locked);
            Assert.All(reopened.Layouts.Where(layout => layout.Id != created.Id).SelectMany(layout => layout.Widgets),
                widget => Assert.True(widget.Locked));
            // 超出范围的缩放被收回到上限；元件尺寸按缩放前的宽高保存。
            Assert.Equal(WidgetPlacement.MaximumScale, saved.Widgets.Single().Scale);
            Assert.Equal((320d, 200d), (saved.Widgets.Single().Width, saved.Widgets.Single().Height));
            Assert.DoesNotContain(reopened.Layouts.Single(layout => layout.Id == "performance").Widgets,
                widget => widget.WidgetId == "combat.dps");

            Assert.True(reopened.Remove(created.Id));
            Assert.True(reopened.Remove("performance"));
            Assert.False(reopened.Remove("raid"));

            // 保存在后台线程写盘：先等删除后的保存写完，再写入损坏的内容，否则迟到的保存会覆盖它。
            reopened.Flush();
            File.WriteAllText(path, "{ not json");
            Assert.Equal(2, new OverlayLayoutStore(path).Layouts.Count);
        }
        finally
        {
            DeleteLayoutFile(path);
        }
    }

    [AvaloniaTheory]
    [InlineData(960, 540)]
    [InlineData(1920, 1080)]
    public void OverlayWindowAnchorsWidgetsToTheSameEdgesAtAnyResolution(double width, double height)
    {
        UiMotion.Enabled = false;
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Overlay!.Store.Select("raid");
        var window = new OverlayWindow(catalog.Overlay.Store, catalog, backend) { Width = width, Height = height };
        window.Show();
        LayoutProbe.Settle();

        var canvas = window.Canvas;
        Assert.Equal(["combat.dps", "combat.skills", "combat.self-effects", "combat.target-effects"],
            canvas.WidgetIds);
        var dps = canvas.BoundsOf("combat.dps")!.Value;
        var self = canvas.BoundsOf("combat.self-effects")!.Value;
        Assert.Equal(canvas.Bounds.Width - 12, dps.Right, 1);
        Assert.Equal(12, dps.Top, 1);
        Assert.Equal(12, self.Left, 1);
        Assert.Equal(canvas.Bounds.Height - 12, self.Bottom, 1);
        Assert.Empty(LayoutProbe.FindTextOverflow(window));
        LayoutProbe.SaveScreenshot(window, $"overlay-{width}x{height}");
        window.Close();
    }

    [AvaloniaFact]
    public void ExtensionWidgetAppearsInLibraryAndRendersItsOwnView()
    {
        UiMotion.Enabled = false;
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Register(new ModuleDescriptor("sample.fishing", "釣魚計時（範例）", "釣", Color.Parse("#4B5563"),
            ModuleOrigin.Extension, () => new TextBlock(),
            [new("fishing.timer", "釣魚計時", ["scale"], _ => new TextBlock { Text = "00:42", Foreground = Brushes.White })]));
        var model = new OverlayPageModel(catalog, catalog.Overlay!);
        Assert.Contains(model.Library, item => item.WidgetId == "fishing.timer" && item.Group == "釣魚計時（範例）");

        model.ToggleWidgetCommand.Execute("fishing.timer");
        var window = new OverlayWindow(catalog.Overlay!.Store, catalog, backend) { Width = 960, Height = 540 };
        window.Show();
        LayoutProbe.Settle();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "00:42");
        window.Close();
        model.Dispose();
    }

    [AvaloniaFact]
    public void ShowSwitchFollowsButtonsAndShortcutAndClosesWithTheMainWindow()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "presentation");
        var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
        var model = (OverlayPageModel)page.DataContext!;
        var show = page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ShowOverlayButton");
        var hide = page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "HideOverlayButton");
        Assert.Contains("active", hide.Classes);
        Assert.DoesNotContain("active", show.Classes);
        Click(window, show);
        Assert.True(model.OverlayShown);
        Assert.Contains("active", show.Classes);
        Assert.DoesNotContain("active", hide.Classes);
        // 已显示时再按“显示浮窗”不会关掉浮窗。
        Click(window, show);
        Assert.True(model.OverlayShown);
        Click(window, hide);
        Assert.False(model.OverlayShown);
        Assert.Contains("active", hide.Classes);

        // 由快捷键改变显示状态时，分段开关同样跟随实际状态。
        backend.TriggerShortcut("overlay-toggle");
        LayoutProbe.Settle();
        Assert.Contains("active", show.Classes);
        Assert.DoesNotContain("active", hide.Classes);
        backend.TriggerShortcut("overlay-toggle");
        LayoutProbe.Settle();
        Assert.Contains("active", hide.Classes);
        Assert.DoesNotContain("active", show.Classes);

        // 主窗口关闭时浮窗一并关闭。
        Click(window, show);
        var shell = (ShellViewModel)window.DataContext!;
        window.Close();
        LayoutProbe.Settle();
        Assert.False(model.OverlayShown);
        Assert.NotNull(shell);
    }

    [AvaloniaFact]
    public void ShellTogglesOverlayAndAllLocksAndUnsubscribesOnClose()
    {
        var backend = new SampleBackend();
        var store = new OverlayLayoutStore();
        store.Select("raid");
        var window = App.CreateMainWindow(backend, store);
        var shell = (ShellViewModel)window.DataContext!;
        window.Show();
        LayoutProbe.Select(window, "presentation");
        var page = (OverlayPageModel)window.GetVisualDescendants().OfType<OverlayPage>().Single().DataContext!;
        var lockAll = Named<Button>(window, "LockAllButton");
        try
        {
            Assert.False(shell.Catalog.Overlay!.IsShown);
            backend.TriggerShortcut("overlay-toggle");
            Assert.True(shell.Catalog.Overlay.IsShown);
            Assert.True(page.OverlayShown);
            backend.TriggerShortcut("overlay-toggle");
            Assert.False(shell.Catalog.Overlay.IsShown);

            // 全部已上锁时 F7 全部解锁，开关不再选中。
            Assert.True(store.AllLocked);
            Assert.Contains("active", lockAll.Classes);
            backend.TriggerShortcut("overlay-click-through-toggle");
            LayoutProbe.Settle();
            Assert.All(store.Active.Widgets, widget => Assert.False(widget.Locked));
            Assert.False(page.AllLocked);
            Assert.DoesNotContain("active", lockAll.Classes);

            // 只要有一个元件解锁，F7 与“全部上锁”都把全部元件上锁。
            store.Active.Widgets.ForEach(widget => widget.Locked = true);
            store.Active.Widgets[1].Locked = false;
            store.Commit();
            backend.TriggerShortcut("overlay-click-through-toggle");
            Assert.True(store.AllLocked);
            store.Active.Widgets[2].Locked = false;
            store.Commit();
            LayoutProbe.Settle();
            Assert.DoesNotContain("active", lockAll.Classes);
            Click(window, lockAll);
            Assert.True(store.AllLocked);
            Assert.Contains("active", lockAll.Classes);
            Click(window, lockAll);
            Assert.All(store.Active.Widgets, widget => Assert.False(widget.Locked));
            // 只作用于当前版面。
            Assert.All(store.Layouts.Single(layout => layout.Id == "performance").Widgets, widget => Assert.True(widget.Locked));

            backend.TriggerShortcut("emergency-stop");
            Assert.False(shell.Catalog.Overlay.IsShown);
            var shortcuts = backend.GetSettings().Shortcuts;
            Assert.Equal("F9", shortcuts.Single(row => row.Id == "emergency-stop").Chord);
            Assert.Equal("F8", shortcuts.Single(row => row.Id == "overlay-toggle").Chord);
            var lockRow = shortcuts.Single(row => row.Id == "overlay-click-through-toggle");
            Assert.Equal("F7", lockRow.Chord);
            Assert.Contains(lockRow.Action, new[] { "浮窗元件全部上锁或解锁", "浮窗元件全部上鎖或解鎖" });
        }
        finally { window.Close(); }
        var afterClose = store.AllLocked;
        backend.TriggerShortcut("overlay-toggle");
        backend.TriggerShortcut("overlay-click-through-toggle");
        Assert.False(shell.Catalog.Overlay!.IsShown);
        Assert.Equal(afterClose, store.AllLocked);
    }

    [AvaloniaFact]
    public void OverlayAreaCoversEveryScreenAndRemembersEachWidgetsMonitor()
    {
        var primary = new ScreenInfo(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 1, true);
        var left = new ScreenInfo(new PixelRect(-2880, 0, 2880, 1620), new PixelRect(-2880, 0, 2880, 1572), 1.5, false);
        // 浮窗与游戏窗口无关：铺满两块显示器拼成的虚拟桌面，按窗口的缩放比例换算。
        var area = OverlayArea.Resolve([primary, left], 1)!;
        Assert.Equal(new PixelRect(-2880, 0, 4800, 1620), area.Pixels);
        Assert.Equal(new Size(4800, 1620), area.Logical);
        var monitors = area.Monitors;
        Assert.Equal(new Rect(2880, 0, 1920, 1032), monitors.Single(monitor => monitor.Primary).Area);
        Assert.Equal((new Rect(0, 0, 2880, 1572), 1.5), (monitors.Single(monitor => !monitor.Primary).Area, monitors.Single(monitor => !monitor.Primary).Dpi));
        Assert.Null(OverlayArea.Resolve([]));

        // 记住的显示器还在时回到它；缩放改了但范围相同仍认它；拔掉后回到主显示器（比例位置不变，总在可见区域里）。
        Assert.Equal(left.Key, OverlayArea.Locate(monitors, left.Key)!.Key);
        Assert.Equal(left.Key, OverlayArea.Locate(monitors, "-2880,0,2880,1620@1.25")!.Key);
        Assert.Equal(primary.Key, OverlayArea.Locate(monitors, "3840,0,1920,1080@1")!.Key);
        Assert.Equal(primary.Key, OverlayArea.Locate(monitors, null)!.Key);
        Assert.Equal(primary.Key, OverlayArea.Locate(OverlayArea.Resolve([primary])!.Monitors, left.Key)!.Key);
        // 同一位置的显示器只改了分辨率：仍认这块显示器。
        Assert.Equal(left.Key, OverlayArea.Locate(monitors, "-2880,0,1920,1080@1.5")!.Key);

        // 版面页的说明：各显示器的方位、分辨率与缩放；记住的显示器不在时写明暂时显示在主显示器。
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        Assert.Equal("左侧显示器（2880×1620 · 150%）、主显示器（1920×1080 · 100%）", OverlayArea.Caption([primary, left]));
        Assert.Equal("左侧显示器（2880×1620 · 150%）", OverlayArea.Where([primary, left], left.Key));
        Assert.Equal("主显示器（1920×1080 · 100%），原显示器未连接，暂时显示在这里", OverlayArea.Where([primary], left.Key));
        Assert.Equal("未读取到显示器信息", OverlayArea.Where([], left.Key));
    }

    // 混合缩放的双显示器：主显示器 1920×1080（100%），副显示器在左侧 x=-1920，1920×1080（125%）。
    private static readonly IReadOnlyList<ScreenInfo> UserScreens =
    [
        new(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 1, true),
        new(new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1020), 1.25, false)
    ];

    // 早期的版面文件格式（没有 Screen、Closed，第一代还没有 Locked），按游戏窗口里的比例位置保存。
    private const string OldLayoutJson = """
        { "ActiveId": "old", "Layouts": [ { "Id": "old", "Name": "旧版面", "Widgets": [
          { "WidgetId": "combat.dps", "X": 0, "Y": 0, "Scale": 1, "Skin": "glass" },
          { "WidgetId": "combat.skills", "X": 1, "Y": 0, "Scale": 1, "Skin": "glass", "Locked": false },
          { "WidgetId": "rooms.readiness", "X": 0.5, "Y": 0.5, "Scale": 1.2, "Skin": "solid" } ] } ] }
        """;

    [AvaloniaFact]
    public void EachMonitorWindowShowsItsOwnWidgetsInsideTheWindowAndOldLayoutsLandOnThePrimary()
    {
        var path = Path.Combine(Path.GetTempPath(), "moongate-old-layout-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, OldLayoutJson);
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        var store = new OverlayLayoutStore(path);
        var primary = new OverlayWindow(store, catalog, backend);
        var secondary = new OverlayWindow(store, catalog, backend, UserScreens[1].Key);
        try
        {
            // 旧版面直接读入：没有显示器标识按主显示器，没有锁状态按已上锁，都没有关闭；不需要迁移。
            Assert.All(store.Active.Widgets, widget => { Assert.Null(widget.Screen); Assert.False(widget.Closed); });
            Assert.True(store.Active.Widgets[0].Locked);
            Assert.False(store.Active.Widgets[1].Locked);

            // 每个窗口按所在显示器自己的比例铺满（主显示器 1 倍、副显示器 1.25 倍），画布只放本显示器的元件。
            foreach (var (window, screen) in new[] { (primary, UserScreens[0]), (secondary, UserScreens[1]) })
            {
                window.Show();
                var area = OverlayArea.ForScreen(UserScreens, screen, screen.Scaling);
                window.Width = area.Logical.Width;
                window.Height = area.Logical.Height;
                window.Canvas.Monitors = area.Monitors;
                window.Canvas.OwnMonitorKey = screen.Key;
                LayoutProbe.Render(window);
            }
            Assert.Equal(new Size(1536, 864), OverlayArea.ForScreen(UserScreens, UserScreens[1], 1.25).Logical);
            var canvas = primary.Canvas;
            foreach (var id in canvas.WidgetIds)
            {
                var bounds = canvas.BoundsOf(id)!.Value;
                Assert.True(canvas.FrameOf(id)!.IsVisible);
                Assert.True(bounds.X >= 0 && bounds.Y >= 0 && bounds.Right <= 1920 && bounds.Bottom <= 1080, $"{id} {bounds}");
                Assert.False(secondary.Canvas.FrameOf(id)!.IsVisible);
            }
            // 比例位置贴着主显示器工作区的边：左上 12 边距，右上贴右边。
            Assert.Equal(new Point(12, 12), canvas.BoundsOf("combat.dps")!.Value.TopLeft);
            Assert.Equal(1920 - 12, canvas.BoundsOf("combat.skills")!.Value.Right, 1);

            // 命中区域：解锁元件整块、上锁元件只有锁与关闭按钮，全部在本窗口内（物理像素，主显示器 1 倍）。
            var region = primary.InteractiveRegion;
            Assert.True(OverlayHitRegion.Accepts(region, PixelPoint.FromPoint(canvas.BoundsOf("combat.skills")!.Value.Center, 1)));
            Assert.All(region, rect => Assert.True(rect.X >= 0 && rect.Right <= 1920 && rect.Bottom <= 1080));
            Assert.False(OverlayHitRegion.Accepts(region, PixelPoint.FromPoint(canvas.BoundsOf("combat.dps")!.Value.Center, 1)));

            // 元件改属副显示器：主显示器的窗口不再显示它，副显示器的窗口按 1.25 倍显示在其工作区内。
            var skills = store.Active.Widgets.Single(widget => widget.WidgetId == "combat.skills");
            skills.Screen = UserScreens[1].Key;
            store.Commit();
            LayoutProbe.Render(primary);
            LayoutProbe.Render(secondary);
            Assert.False(canvas.FrameOf("combat.skills")!.IsVisible);
            Assert.True(secondary.Canvas.FrameOf("combat.skills")!.IsVisible);
            var moved = secondary.Canvas.BoundsOf("combat.skills")!.Value;
            Assert.True(moved.X >= 0 && moved.Right <= 1536 + 0.5, moved.ToString());
        }
        finally
        {
            primary.Close();
            secondary.Close();
            DeleteLayoutFile(path);
        }
    }

    [AvaloniaFact]
    public void WidgetsSitOnTheirRememberedMonitorAtThatMonitorsScale()
    {
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        var store = catalog.Overlay!.Store;
        store.Select("raid");
        var dps = store.Active.Widgets.Single(widget => widget.WidgetId == "combat.dps");
        var window = new OverlayWindow(store, catalog, backend) { Width = 1920, Height = 540 };
        window.Show();
        try
        {
            // 左半是 1 倍的主显示器，右半是 1.5 倍的副显示器（窗口逻辑像素）。
            var area = OverlayArea.Resolve(
            [
                new(new PixelRect(0, 0, 960, 540), new PixelRect(0, 0, 960, 540), 1, true),
                new(new PixelRect(960, 0, 1440, 810), new PixelRect(960, 0, 1440, 810), 1.5, false)
            ], 1)!;
            window.Canvas.Monitors = area.Monitors;
            window.Canvas.OwnMonitorKey = null;
            LayoutProbe.Render(window);
            var onPrimary = window.Canvas.BoundsOf("combat.dps")!.Value;
            Assert.InRange(onPrimary.Right, 960 - 13, 960 - 11);

            dps.Screen = area.Screens[1].Key;
            dps.X = 0;
            dps.Y = 0;
            store.Commit();
            LayoutProbe.Render(window);
            var onSecond = window.Canvas.BoundsOf("combat.dps")!.Value;
            // 边距 12 按那块显示器的逻辑像素计，在本窗口里同样放大 1.5 倍。
            Assert.Equal(960 + 12 * 1.5, onSecond.X, 1);
            // 副显示器的缩放比例是主显示器的 1.5 倍，元件在窗口里放大 1.5 倍，物理大小一致。
            Assert.Equal(onPrimary.Width * 1.5, onSecond.Width, 1);
        }
        finally { window.Close(); }
    }

    // 两块显示器各一个浮窗窗口（无界面窗口按给定的逻辑尺寸），画布拿到以本窗口为原点与比例的全部显示器。
    private static (OverlayWindow Primary, OverlayWindow Secondary) OpenMonitorWindows(OverlayLayoutStore store, ModuleCatalog catalog, SampleBackend backend)
    {
        var primary = new OverlayWindow(store, catalog, backend);
        var secondary = new OverlayWindow(store, catalog, backend, UserScreens[1].Key);
        foreach (var (window, screen) in new[] { (primary, UserScreens[0]), (secondary, UserScreens[1]) })
        {
            window.Show();
            var area = OverlayArea.ForScreen(UserScreens, screen, screen.Scaling);
            window.Width = area.Logical.Width;
            window.Height = area.Logical.Height;
            window.Canvas.Monitors = area.Monitors;
            window.Canvas.OwnMonitorKey = screen.Key;
            LayoutProbe.Render(window);
        }
        return (primary, secondary);
    }

    // 元件外框换成物理像素（虚拟桌面坐标）：窗口所在显示器的左上角加上逻辑坐标乘以窗口的缩放比例。
    private static Rect Physical(OverlayWindow window, ScreenInfo screen, string widgetId)
    {
        var bounds = window.Canvas.BoundsOf(widgetId)!.Value;
        return new Rect(screen.Bounds.X + bounds.X * screen.Scaling, screen.Bounds.Y + bounds.Y * screen.Scaling,
            bounds.Width * screen.Scaling, bounds.Height * screen.Scaling);
    }

    [AvaloniaFact]
    public void WidgetDragsAcrossMonitorsWithDifferentScalingKeepsItsPlaceAndIsRememberedPerMonitor()
    {
        // 0.1.0 预览包：元件只能放在主显示器。拖动按光标位置跨显示器（主显示器 100%、左侧副显示器 125%），不中断、不漂移，松开后按显示器保存。
        UiMotion.Enabled = false;
        var path = Path.Combine(Path.GetTempPath(), "moongate-monitors-" + Guid.NewGuid().ToString("N") + ".json");
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        var store = new OverlayLayoutStore(path);
        store.Select("raid");
        var dps = store.Active.Widgets.Single(widget => widget.WidgetId == "combat.dps");
        (dps.X, dps.Y, dps.Locked) = (0.5, 0.5, false);
        store.Commit();
        var (primary, secondary) = OpenMonitorWindows(store, catalog, backend);
        try
        {
            // 先在主显示器上拖到格线上的一点，作为来回拖动的起点。
            Drag(primary, primary.Canvas.BoundsOf("combat.dps")!.Value.Center, new Vector(101, 53));
            var (x, y) = (dps.X, dps.Y);
            Assert.Null(dps.Screen);
            var start = primary.Canvas.BoundsOf("combat.dps")!.Value;
            var press = start.Center;
            var size = start.Size;

            // 按住拖进左侧副显示器（本窗口坐标为负）：元件改属副显示器，由副显示器的窗口显示，抓取点留在光标下，逻辑大小不变。
            primary.MouseDown(press, MouseButton.Left);
            var cursor = new Point(-800, 400);
            primary.MouseMove(cursor);
            LayoutProbe.Render(primary);
            LayoutProbe.Render(secondary);
            Assert.Equal(UserScreens[1].Key, dps.Screen);
            Assert.True(primary.Canvas.IsManipulating);
            Assert.True(secondary.Canvas.FrameOf("combat.dps")!.IsVisible);
            var onSecond = Physical(secondary, UserScreens[1], "combat.dps");
            Assert.Equal(size.Width * 1.25, onSecond.Width, 0);
            // 发起拖动的窗口算出的位置与副显示器窗口画出的位置一致（物理像素）。
            var fromPrimary = Physical(primary, UserScreens[0], "combat.dps");
            Assert.InRange(onSecond.X - fromPrimary.X, -1, 1);
            Assert.InRange(onSecond.Y - fromPrimary.Y, -1, 1);
            // 光标仍在按下时的相对位置上（允许副显示器上一格 10 px 的对齐）。
            Assert.InRange(onSecond.X + onSecond.Width * (press.X - start.X) / start.Width - cursor.X, -10, 10);
            Assert.InRange(onSecond.Y + onSecond.Height * (press.Y - start.Y) / start.Height - cursor.Y, -10, 10);

            // 再拖到副显示器的另一处，最后回到按下的位置松开：回到主显示器原来的比例位置，没有累积偏差。
            primary.MouseMove(new Point(-1500, 200));
            primary.MouseMove(press);
            primary.MouseUp(press, MouseButton.Left);
            LayoutProbe.Render(primary);
            LayoutProbe.Render(secondary);
            Assert.Equal(UserScreens[0].Key, dps.Screen);
            Assert.Equal((x, y), (dps.X, dps.Y));
            Assert.Equal(start, primary.Canvas.BoundsOf("combat.dps")!.Value);
            Assert.False(secondary.Canvas.FrameOf("combat.dps")!.IsVisible);

            // 拖到副显示器上松开：留在那里并保存；重新读取版面后仍在副显示器的同一位置。
            Drag(primary, press, cursor - press);
            LayoutProbe.Render(secondary);
            Assert.Equal(UserScreens[1].Key, dps.Screen);
            Assert.False(primary.Canvas.FrameOf("combat.dps")!.IsVisible);
            var saved = new OverlayLayoutStore(path).Active.Widgets.Single(widget => widget.WidgetId == "combat.dps");
            Assert.Equal((UserScreens[1].Key, dps.X, dps.Y), (saved.Screen, saved.X, saved.Y));
        }
        finally
        {
            primary.Close();
            secondary.Close();
            DeleteLayoutFile(path);
        }
    }

    // 版面在后台写盘：删除测试文件之前先等写完，免得删除与替换文件撞在一起。
    private static void DeleteLayoutFile(string path)
    {
        new OverlayLayoutStore(path).Flush();
        File.Delete(path);
    }

    private sealed class FakeCursor : IOverlayCursor
    {
        public PixelPoint? Position { get; set; }
        public bool PrimaryButtonDown { get; set; }
    }

    [AvaloniaFact]
    public void DragFollowsTheSystemCursorOntoAnotherMonitorWithoutPointerEventsAndSavesOnRelease()
    {
        // 真实环境下发起拖动的窗口不在前台时，光标越过它所在的显示器后收不到指针移动，捕获也可能被收回：
        // 这里按下后不再发指针移动，只改系统光标的位置，并模拟捕获丢失；拖动仍跟到副显示器，主按键松开后保存。
        UiMotion.Enabled = false;
        var path = Path.Combine(Path.GetTempPath(), "moongate-cursor-drag-" + Guid.NewGuid().ToString("N") + ".json");
        var trace = Path.ChangeExtension(path, ".log");
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        var store = new OverlayLayoutStore(path);
        store.Select("raid");
        var dps = store.Active.Widgets.Single(widget => widget.WidgetId == "combat.dps");
        (dps.X, dps.Y, dps.Locked) = (0.5, 0.5, false);
        store.Commit();
        var (primary, secondary) = OpenMonitorWindows(store, catalog, backend);
        var cursor = new FakeCursor();
        primary.Canvas.Area = OverlayArea.ForScreen(UserScreens, UserScreens[0], 1);
        primary.Canvas.CursorSource = cursor;
        OverlayDragTrace.PathOverride = trace;
        OverlayDragTrace.Enabled = true;
        try
        {
            var press = primary.Canvas.BoundsOf("combat.dps")!.Value.Center;
            (cursor.Position, cursor.PrimaryButtonDown) = (new PixelPoint((int)press.X, (int)press.Y), true);
            primary.MouseDown(press, MouseButton.Left);
            Assert.True(primary.Canvas.IsManipulating);

            // 捕获丢失只记录，不结束拖动；光标移到左侧副显示器中部（物理像素），轮询把元件带过去。
            primary.Canvas.FrameOf("combat.dps")!.RaiseEvent(new PointerCaptureLostEventArgs(primary.Canvas.FrameOf("combat.dps")!,
                new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true)));
            Assert.True(primary.Canvas.IsManipulating);
            cursor.Position = new PixelPoint(-900, 500);
            primary.Canvas.PollDrag();
            LayoutProbe.Render(primary);
            LayoutProbe.Render(secondary);
            Assert.Equal(UserScreens[1].Key, dps.Screen);
            Assert.True(secondary.Canvas.FrameOf("combat.dps")!.IsVisible);
            var onSecond = Physical(secondary, UserScreens[1], "combat.dps");
            Assert.True(onSecond.Contains(new Point(-900, 500)), onSecond.ToString());

            // 主按键松开（松开消息送到了别的窗口）：下一次轮询结束拖动并保存到副显示器。
            cursor.PrimaryButtonDown = false;
            primary.Canvas.PollDrag();
            Assert.False(primary.Canvas.IsManipulating);
            var saved = new OverlayLayoutStore(path).Active.Widgets.Single(widget => widget.WidgetId == "combat.dps");
            Assert.Equal(UserScreens[1].Key, saved.Screen);
            // 诊断记录写明来源、判定的显示器与结束原因。
            var log = File.ReadAllText(trace);
            Assert.Contains("capture-lost", log);
            Assert.Contains($"poll cursor=-900,500", log);
            Assert.Contains($"monitor={UserScreens[1].Key}", log);
            Assert.Contains($"end released screen={UserScreens[1].Key}", log);
        }
        finally
        {
            OverlayDragTrace.Enabled = false;
            OverlayDragTrace.PathOverride = null;
            primary.Close();
            secondary.Close();
            DeleteLayoutFile(path);
            File.Delete(trace);
        }
    }

    // 用户实例的染色元件：左侧 125% 副显示器上，保存的尺寸 232×304，已解锁。
    private static (OverlayLayoutStore Store, WidgetPlacement Color, OverlayWindow Primary, OverlayWindow Secondary, FakeCursor Cursor, string Path)
        OpenUserColorWidget()
    {
        UiMotion.Enabled = false;
        var path = Path.Combine(Path.GetTempPath(), "moongate-resize-" + Guid.NewGuid().ToString("N") + ".json");
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        var store = new OverlayLayoutStore(path);
        var color = store.Place("color.current");
        (color.Screen, color.X, color.Y, color.Width, color.Height, color.Locked) = (UserScreens[1].Key, 0.5, 0.5, 232, 304, false);
        store.Commit();
        var (primary, secondary) = OpenMonitorWindows(store, catalog, backend);
        var cursor = new FakeCursor();
        secondary.Canvas.Area = OverlayArea.ForScreen(UserScreens, UserScreens[1], 1.25);
        secondary.Canvas.CursorSource = cursor;
        LayoutProbe.Render(secondary);
        return (store, color, primary, secondary, cursor, path);
    }

    // 副显示器窗口的逻辑坐标换成系统光标的物理坐标（左侧显示器原点 -1920,0，125%）。
    private static PixelPoint OnSecondary(Point logical) =>
        new((int)Math.Round(UserScreens[1].Bounds.X + logical.X * 1.25), (int)Math.Round(UserScreens[1].Bounds.Y + logical.Y * 1.25));

    [AvaloniaFact]
    public void HandleResizeByCursorPollingSettlesEvenWhenEveryLayoutPassPollsAgain()
    {
        // 实机崩溃：浮窗在每次排版后都轮询光标，改尺寸的轮询无论尺寸变没变都要求重新排版，排版与轮询互相触发，
        // Avalonia 判定为无限排版循环（Infinite layout loop detected）并结束进程。这里按同样的接线驱动改尺寸。
        var (store, color, primary, secondary, cursor, path) = OpenUserColorWidget();
        var canvas = secondary.Canvas;
        var layoutPasses = 0;
        canvas.LayoutUpdated += (_, _) => { layoutPasses++; canvas.PollDrag(); };
        try
        {
            var press = Center(canvas.HandleOf("color.current")!, secondary);
            (cursor.Position, cursor.PrimaryButtonDown) = (OnSecondary(press), true);
            secondary.MouseDown(press, MouseButton.Left);
            Assert.True(canvas.IsManipulating);

            // 拖过显示器右边界，一直拖到主显示器右下角：宽度停在副显示器工作区以内（扣除两侧边距），高度对齐 8 px 且不超出工作区。
            cursor.Position = new PixelPoint(1900, 1079);
            canvas.PollDrag();
            LayoutProbe.Render(secondary);
            var monitor = UserScreens[1].WorkingArea;
            Assert.Equal(Math.Floor((monitor.Width / 1.25 - 24) / 8) * 8, color.Width);
            Assert.InRange(color.Height!.Value, 304, monitor.Height / 1.25 - 24);
            Assert.Equal(0, color.Height!.Value % 8);
            Assert.True(canvas.BoundsOf("color.current")!.Value.Right <= monitor.Width / 1.25 + 0.5);
            // 光标不动时再排版几次：尺寸不变，不再要求排版。
            layoutPasses = 0;
            LayoutProbe.Render(secondary);
            LayoutProbe.Render(secondary);
            Assert.InRange(layoutPasses, 0, 4);

            // 拖到最小以下：停在元件的最小尺寸 200×120。
            cursor.Position = OnSecondary(press - new Vector(2000, 2000));
            canvas.PollDrag();
            LayoutProbe.Render(secondary);
            Assert.Equal((200d, 120d), (color.Width!.Value, color.Height!.Value));

            // 主按键松开：结束并保存，最终尺寸合法。
            cursor.Position = OnSecondary(press + new Vector(40, 24));
            cursor.PrimaryButtonDown = false;
            canvas.PollDrag();
            Assert.False(canvas.IsManipulating);
            var saved = new OverlayLayoutStore(path).Active.Widgets.Single(widget => widget.WidgetId == "color.current");
            Assert.Equal((color.Width, color.Height), (saved.Width, saved.Height));
            Assert.True(color.Width >= 200 && color.Height >= 120 && double.IsFinite(color.Width!.Value) && double.IsFinite(color.Height!.Value));

            // 之后仍可操作：再拖动一次元件。
            LayoutProbe.Render(secondary);
            var frame = canvas.BoundsOf("color.current")!.Value;
            var grab = frame.TopLeft + new Vector(24, 24);
            (cursor.Position, cursor.PrimaryButtonDown) = (OnSecondary(grab), true);
            secondary.MouseDown(grab, MouseButton.Left);
            Assert.True(canvas.IsManipulating);
            cursor.Position = OnSecondary(grab + new Vector(-96, -64));
            cursor.PrimaryButtonDown = false;
            canvas.PollDrag();
            Assert.False(canvas.IsManipulating);
            LayoutProbe.Render(secondary);
            Assert.NotEqual(frame.TopLeft, canvas.BoundsOf("color.current")!.Value.TopLeft);
        }
        finally
        {
            primary.Close();
            secondary.Close();
            DeleteLayoutFile(path);
        }
    }

    [AvaloniaFact]
    public void GestureWhoseReleaseNeverArrivesEndsAfterTheIdleTimeout()
    {
        // 远程桌面可能丢掉松开：系统报告的主按键一直按住、光标不再移动。超过空闲时限后结束改尺寸并保存当时的尺寸。
        var (store, color, primary, secondary, cursor, path) = OpenUserColorWidget();
        var canvas = secondary.Canvas;
        var clock = new ManualClock();
        canvas.Clock = clock;
        var trace = Path.ChangeExtension(path, ".log");
        OverlayDragTrace.PathOverride = trace;
        try
        {
            var press = Center(canvas.HandleOf("color.current")!, secondary);
            (cursor.Position, cursor.PrimaryButtonDown) = (OnSecondary(press), true);
            secondary.MouseDown(press, MouseButton.Left);
            cursor.Position = OnSecondary(press + new Vector(64, 40));
            canvas.PollDrag();
            var resized = (color.Width, color.Height);
            Assert.Equal((296d, 344d), resized);

            clock.Advance(OverlayCanvas.GestureIdleTimeout - TimeSpan.FromSeconds(1));
            canvas.PollDrag();
            Assert.True(canvas.IsManipulating);
            clock.Advance(TimeSpan.FromSeconds(2));
            canvas.PollDrag();
            Assert.False(canvas.IsManipulating);
            var saved = new OverlayLayoutStore(path).Active.Widgets.Single(widget => widget.WidgetId == "color.current");
            Assert.Equal(resized, (saved.Width, saved.Height));
            // 诊断未打开也记下这次超时结束。
            Assert.Contains("resize color.current ended: no input for 10 s", File.ReadAllText(trace));
        }
        finally
        {
            OverlayDragTrace.PathOverride = null;
            File.Delete(trace);
            primary.Close();
            secondary.Close();
            DeleteLayoutFile(path);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    [AvaloniaFact]
    public void ZoomButtonsAndPageScaleOnTheScaledMonitorStayInRangeAndSave()
    {
        // 125% 副显示器上用元件的“－／＋”与版面页的缩放改到最大与最小：倍率停在 0.5–2，外框尺寸有限，已保存，之后仍可拖动。
        var (store, color, primary, secondary, cursor, path) = OpenUserColorWidget();
        var canvas = secondary.Canvas;
        var catalog = BuiltInModules.CreateCatalog(new SampleBackend(), store);
        var model = new OverlayPageModel(catalog, catalog.Overlay!) { SelectedWidgetId = "color.current" };
        try
        {
            for (var i = 0; i < 12; i++)
            {
                PressAt(secondary, Center(canvas.ZoomButtonOf("color.current", true)!, secondary));
                LayoutProbe.Render(secondary);
            }
            Assert.Equal(WidgetPlacement.MaximumScale, color.Scale);
            var large = canvas.BoundsOf("color.current")!.Value;
            Assert.True(double.IsFinite(large.Width) && large.Width > 0 && large.Right <= secondary.Width + 0.5, large.ToString());
            for (var i = 0; i < 20; i++) model.ScaleCommand.Execute("-");
            LayoutProbe.Render(secondary);
            Assert.Equal(WidgetPlacement.MinimumScale, color.Scale);
            Assert.Equal(WidgetPlacement.MinimumScale, new OverlayLayoutStore(path).Active.Widgets.Single(widget => widget.WidgetId == "color.current").Scale);

            var grab = canvas.BoundsOf("color.current")!.Value.TopLeft + new Vector(8, 8);
            (cursor.Position, cursor.PrimaryButtonDown) = (OnSecondary(grab), true);
            secondary.MouseDown(grab, MouseButton.Left);
            Assert.True(canvas.IsManipulating);
            cursor.PrimaryButtonDown = false;
            canvas.PollDrag();
            Assert.False(canvas.IsManipulating);
        }
        finally
        {
            model.Dispose();
            primary.Close();
            secondary.Close();
            DeleteLayoutFile(path);
        }
    }

    [AvaloniaFact]
    public void WidgetsMoveToAnotherMonitorWithoutDragging()
    {
        // 远程桌面一次只显示一块屏幕时拖不过显示器边界：版面页选显示器、浮窗上的“移到下一块显示器”都能换屏，比例位置不变并立即保存。
        UiMotion.Enabled = false;
        var path = Path.Combine(Path.GetTempPath(), "moongate-move-monitor-" + Guid.NewGuid().ToString("N") + ".json");
        var backend = new SampleBackend();
        var store = new OverlayLayoutStore(path);
        var catalog = BuiltInModules.CreateCatalog(backend, store);
        store.Select("raid");
        var dps = store.Active.Widgets.Single(widget => widget.WidgetId == "combat.dps");
        (dps.X, dps.Y, dps.Screen) = (0.25, 0.75, null);
        store.Commit();
        var model = new OverlayPageModel(catalog, catalog.Overlay!) { SelectedWidgetId = "combat.dps" };
        var (primary, secondary) = OpenMonitorWindows(store, catalog, backend);
        try
        {
            // 只有一块显示器：不显示选择与“移到下一块显示器”。
            model.Screens = [UserScreens[0]];
            Assert.False(model.HasMonitorChoice);
            Assert.Empty(model.MonitorChoices);
            store.BeginEdit();
            primary.Canvas.Monitors = OverlayArea.ForScreen([UserScreens[0]], UserScreens[0], 1).Monitors;
            LayoutProbe.Render(primary);
            Assert.False(primary.Canvas.NextMonitorButtonOf("combat.dps")!.IsVisible);

            // 两块显示器：版面页从左到右列出，当前在主显示器；选左侧显示器后元件改属它，比例位置不变，已保存。
            model.Screens = UserScreens;
            Assert.Equal(["左侧显示器", "主显示器"], model.MonitorChoices.Select(option => option.Label));
            Assert.True(model.MonitorChoices.Single(option => option.Key == UserScreens[0].Key).Selected);
            model.MoveToMonitorCommand.Execute(UserScreens[1].Key);
            Assert.Equal((UserScreens[1].Key, 0.25, 0.75), (dps.Screen, dps.X, dps.Y));
            Assert.Equal(UserScreens[1].Key, new OverlayLayoutStore(path).Active.Widgets.Single(widget => widget.WidgetId == "combat.dps").Screen);
            LayoutProbe.Render(secondary);
            Assert.True(secondary.Canvas.FrameOf("combat.dps")!.IsVisible);

            // 浮窗上的按钮（编辑状态、多于一块显示器时显示）依次循环到下一块显示器。
            primary.Canvas.Monitors = OverlayArea.ForScreen(UserScreens, UserScreens[0], 1).Monitors;
            LayoutProbe.Render(primary);
            LayoutProbe.Render(secondary);
            var next = secondary.Canvas.NextMonitorButtonOf("combat.dps")!;
            Assert.True(next.IsVisible);
            Click(secondary, next);
            Assert.Equal(UserScreens[0].Key, dps.Screen);
            LayoutProbe.Render(primary);
            Click(primary, primary.Canvas.NextMonitorButtonOf("combat.dps")!);
            Assert.Equal(UserScreens[1].Key, dps.Screen);

            // “全部移到”主显示器。
            model.MoveAllToMonitorCommand.Execute(UserScreens[0].Key);
            Assert.All(store.Active.Widgets, widget => Assert.Equal(UserScreens[0].Key, widget.Screen));
        }
        finally
        {
            store.EndEdit();
            primary.Close();
            secondary.Close();
            model.Dispose();
            DeleteLayoutFile(path);
        }
    }

    [AvaloniaFact]
    public void EditingOnScreenMovesLockedAndClosedWidgetsAndSavesWhenFinished()
    {
        var path = Path.Combine(Path.GetTempPath(), "moongate-editing-" + Guid.NewGuid().ToString("N") + ".json");
        var backend = new SampleBackend();
        var store = new OverlayLayoutStore(path);
        var placement = store.Active.Widgets.Single(widget => widget.WidgetId == "rooms.readiness");
        store.Close(placement);
        var window = App.CreateMainWindow(backend, store);
        window.Width = 1280;
        window.Height = 860;
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        UiMotion.Enabled = false;
        window.Show();
        var controller = ((ShellViewModel)window.DataContext!).Catalog.Overlay!;
        try
        {
            LayoutProbe.Select(window, "presentation");
            var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
            var model = (OverlayPageModel)page.DataContext!;
            var edit = Named<Button>(page, "EditButton");
            Assert.Equal("编辑位置", edit.Content);
            Assert.False(controller.IsShown);

            // “编辑位置”：浮窗显示并进入编辑状态；已上锁、已关闭的元件也显示（变淡），整块可拖动，控件常显，提示条在顶部。
            Click(window, edit);
            Assert.True(controller.IsShown);
            Assert.True(store.IsEditing);
            Assert.Equal("完成编辑", edit.Content);
            var overlay = controller.Windows[0];
            if (overlay.Area is null) (overlay.Width, overlay.Height) = (960, 540);
            LayoutProbe.Render(overlay);
            var canvas = overlay.Canvas;
            Assert.True(placement.Locked);
            Assert.True(canvas.FrameOf("rooms.readiness")!.IsVisible);
            Assert.Equal(0.4, canvas.FrameOf("rooms.readiness")!.Opacity, 2);
            Assert.True(canvas.HandleOf("rooms.readiness")!.IsVisible);
            Assert.True(canvas.ControlsShown("rooms.readiness"));
            Assert.True(canvas.Banner.IsEffectivelyVisible);
            var frame = canvas.BoundsOf("rooms.readiness")!.Value;
            Assert.True(OverlayHitRegion.Accepts(overlay.InteractiveRegion, new PixelPoint((int)frame.Center.X, (int)frame.Center.Y)));
            Assert.True(OverlayHitRegion.Accepts(overlay.InteractiveRegion,
                new PixelPoint((int)canvas.Banner.Bounds.Center.X, (int)canvas.Banner.Bounds.Center.Y)));

            // 编辑状态下按关闭按钮重新显示关闭的元件；拖动上锁的元件（按在内容上也是拖动），锁状态不变。
            PressAt(overlay, Center(canvas.CloseOf("rooms.readiness")!, overlay));
            Assert.False(placement.Closed);
            LayoutProbe.Render(overlay);
            Drag(overlay, frame.Center, new Vector(-120, 64));
            Assert.InRange(canvas.BoundsOf("rooms.readiness")!.Value.X - frame.X, -128, -112);
            Assert.True(placement.Locked);

            // 浮窗上的“完成编辑”：退出编辑并保存；元件回到上锁的行为（只有锁与关闭接收鼠标）。
            Click(overlay, Named<Button>(overlay, "FinishEditButton"));
            LayoutProbe.Render(overlay);
            Assert.False(store.IsEditing);
            Assert.Equal("编辑位置", edit.Content);
            Assert.False(canvas.Banner.IsVisible);
            Assert.False(canvas.HandleOf("rooms.readiness")!.IsVisible);
            var moved = canvas.BoundsOf("rooms.readiness")!.Value;
            Assert.False(OverlayHitRegion.Accepts(overlay.InteractiveRegion, new PixelPoint((int)moved.Center.X, (int)moved.Center.Y)));
            var saved = new OverlayLayoutStore(path).Active.Widgets.Single(widget => widget.WidgetId == "rooms.readiness");
            Assert.Equal((placement.X, placement.Y, false, true), (saved.X, saved.Y, saved.Closed, saved.Locked));

            // 恢复默认位置回到内建版面里的位置；全部移回主显示器清掉记住的显示器。
            model.SelectedWidgetId = "rooms.readiness";
            placement.Screen = UserScreens[1].Key;
            Click(window, Named<Button>(page, "ResetPositionButton"));
            Assert.Equal((1d, 0d, (string?)null), (placement.X, placement.Y, placement.Screen));
            placement.Screen = UserScreens[1].Key;
            Click(window, Named<Button>(page, "MoveAllToPrimaryButton"));
            Assert.Null(placement.Screen);

            // 编辑中隐藏浮窗同样退出编辑并保存。
            Click(window, edit);
            Assert.True(store.IsEditing);
            controller.Hide();
            Assert.False(store.IsEditing);
        }
        finally
        {
            window.Close();
            DeleteLayoutFile(path);
        }
    }

    [AvaloniaFact]
    public void OverlayHidesWithAMinimizedGameOnlyWhenTheOptionIsOn()
    {
        var backend = new SampleBackend { GameMinimized = true };
        var window = LayoutProbe.Open(backend, 1280, 860, "presentation");
        var store = ((ShellViewModel)window.DataContext!).Catalog.Overlay!.Store;
        var overlay = new OverlayWindow(store, ((ShellViewModel)window.DataContext!).Catalog, backend) { Width = 960, Height = 540 };
        overlay.Show();
        try
        {
            // 缺省不隐藏：游戏最小化时浮窗照常显示。
            LayoutProbe.Render(overlay);
            Assert.False(store.HideWhenGameMinimized);
            Assert.True(overlay.Canvas.IsVisible);
            var option = Named<Button>(window, "HideWithGameButton");
            Assert.DoesNotContain("active", option.Classes);

            Click(window, option);
            LayoutProbe.Render(overlay);
            Assert.True(store.HideWhenGameMinimized);
            Assert.False(overlay.Canvas.IsVisible);
            Assert.Empty(overlay.InteractiveRegion);

            backend.GameMinimized = false;
            backend.RaiseChanged();
            LayoutProbe.Render(overlay);
            Assert.True(overlay.Canvas.IsVisible);
        }
        finally
        {
            overlay.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LockedWidgetHidesItsActionsAndTheCloseButtonHidesItUntilReopened()
    {
        UiMotion.Enabled = false;
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Register(new ModuleDescriptor("sample.actions", "操作元件", "操", Color.Parse("#4B5563"),
            ModuleOrigin.Extension, () => new TextBlock(),
            [new("actions.widget", "操作元件", [], _ => new StackPanel
            {
                Width = 200, Spacing = 8,
                Children = { new TextBlock { Text = "操作元件" }, new Button { Name = "ActionButton", Content = "开始", Classes = { "overlayactions" } } }
            })]));
        var store = catalog.Overlay!.Store;
        var placement = store.Place("actions.widget");
        var window = new OverlayWindow(store, catalog, backend) { Width = 960, Height = 540 };
        window.Show();
        LayoutProbe.Render(window);
        try
        {
            var canvas = window.Canvas;
            var action = Named<Button>(window, "ActionButton");
            // 上锁（鼠标穿透）时操作按钮不显示；解锁后显示。
            Assert.True(placement.Locked);
            Assert.False(action.IsEffectivelyVisible);
            store.ToggleLock(placement);
            LayoutProbe.Render(window);
            Assert.True(action.IsEffectivelyVisible);
            store.ToggleLock(placement);
            LayoutProbe.Render(window);

            // 关闭按钮在锁的右边、外框内最右侧；上锁时也接收鼠标，按下即从浮窗上关闭，摆放数据保留。
            var close = canvas.CloseOf("actions.widget")!;
            var lockIcon = canvas.LockOf("actions.widget")!;
            Assert.True(close.Bounds.X > lockIcon.Bounds.Right);
            Assert.True(OverlayHitRegion.Accepts(window.InteractiveRegion, new PixelPoint((int)close.Bounds.Center.X, (int)close.Bounds.Center.Y)));
            canvas.HoverAt(canvas.BoundsOf("actions.widget")!.Value.Center);
            PressAt(window, Center(close, window));
            LayoutProbe.Render(window);
            Assert.True(placement.Closed);
            Assert.False(canvas.FrameOf("actions.widget")!.IsVisible);
            Assert.Contains(store.Active.Widgets, widget => widget.WidgetId == "actions.widget");

            // 版面编辑页的元件库再点一次（Place）重新显示在浮窗上。
            store.Place("actions.widget");
            LayoutProbe.Render(window);
            Assert.False(placement.Closed);
            Assert.True(canvas.FrameOf("actions.widget")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void HitRegionTakesWholeUnlockedWidgetsAndOnlyLocksOfLockedOnes()
    {
        var window = new PixelSize(1920, 1080);
        var locked = new WidgetHitArea(new Rect(10.2, 20, 100, 50.1), new Rect(86.2, 26, 18, 18), true, true);
        var unlocked = new WidgetHitArea(new Rect(600, 300, 100, 100), new Rect(676, 306, 18, 18), false, true);
        var hidden = new WidgetHitArea(new Rect(400, 400, 0, 0), new Rect(400, 400, 18, 18), false, false);

        // 150% 缩放：逻辑矩形换成物理像素并向外取整。上锁的元件只有锁图标，解锁的元件是整个外框，隐藏的元件不计。
        var rects = OverlayHitRegion.Interactive([locked, unlocked, hidden], 1.5, window);
        Assert.Equal([new PixelRect(129, 39, 28, 27), new PixelRect(900, 450, 150, 150)], rects);
        Assert.True(OverlayHitRegion.Accepts(rects, new PixelPoint(140, 50)));
        Assert.True(OverlayHitRegion.Accepts(rects, new PixelPoint(975, 525)));
        // 上锁元件的内容不接收鼠标，元件之外也不接收。
        Assert.False(OverlayHitRegion.Accepts(rects, new PixelPoint(60, 60)));
        Assert.False(OverlayHitRegion.Accepts(rects, new PixelPoint(300, 800)));
        // 超出窗口的部分裁掉，完全在窗口外的去掉。
        Assert.Equal([new PixelRect(1875, 1050, 45, 30)],
            OverlayHitRegion.Rectangles([new Rect(1250, 700, 100, 100), new Rect(1300, 0, 40, 40)], 1.5, window));

        // 画出内容的区域是各可见元件的外框。
        Assert.Equal([new PixelRect(10, 20, 101, 51), new PixelRect(600, 300, 100, 100)],
            OverlayHitRegion.Painted([locked, unlocked, hidden], 1, window));

        // 没有可见元件时整窗都不接收鼠标。
        Assert.Empty(OverlayHitRegion.Interactive([], 1, window));
        Assert.Empty(OverlayHitRegion.Interactive([hidden], 1, window));
    }

    [AvaloniaFact]
    public void HitRegionFollowsLocksAndSkipsWidgetsHiddenForMissingTopics()
    {
        var backend = new SampleBackend();
        var main = LayoutProbe.Open(backend, 1280, 860, "presentation");
        var shell = (ShellViewModel)main.DataContext!;
        var store = shell.Catalog.Overlay!.Store;
        store.Place("performance.nearby");
        var overlay = new OverlayWindow(store, shell.Catalog, backend) { Width = 1920, Height = 1080 };
        overlay.Show();
        LayoutProbe.Settle();
        try
        {
            // 全部上锁：每个元件只有锁图标一块。
            Assert.Equal(overlay.Canvas.WidgetIds.Count, overlay.InteractiveRegion.Count);
            var frame = overlay.Canvas.BoundsOf("rooms.readiness")!.Value;
            var lockRect = overlay.Canvas.LockOf("rooms.readiness")!.Bounds;
            Assert.True(OverlayHitRegion.Accepts(overlay.InteractiveRegion, new PixelPoint((int)lockRect.Center.X, (int)lockRect.Center.Y)));
            Assert.False(OverlayHitRegion.Accepts(overlay.InteractiveRegion, new PixelPoint((int)frame.Center.X, (int)frame.Center.Y)));

            // 解锁后该元件的外框整块计入。
            store.ToggleLock(store.Active.Widgets.Single(widget => widget.WidgetId == "rooms.readiness"));
            LayoutProbe.Settle();
            Assert.True(OverlayHitRegion.Accepts(overlay.InteractiveRegion, new PixelPoint((int)frame.Center.X, (int)frame.Center.Y)));

            // 主题缺失而隐藏的元件不计入。
            var before = overlay.InteractiveRegion.Count;
            backend.PresentationNotices = topic => topic == "nearby" ? new TopicNotice("provider.unavailable", "台服暂不支持") : null;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal(before - 1, overlay.InteractiveRegion.Count);
            Assert.False(overlay.Canvas.LockOf("performance.nearby")!.IsVisible);
        }
        finally
        {
            overlay.Close();
            main.Close();
        }
    }

    [Fact]
    public void OldLayoutFilesReadEveryWidgetAsLocked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moongate-layout-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "ActiveId": "a", "PassThrough": false, "HideFromCapture": true,
                  "Layouts": [ { "Id": "a", "Name": "A", "Widgets": [
                    { "WidgetId": "combat.dps", "X": 1, "Y": 0, "Scale": 1, "Skin": "glass" },
                    { "WidgetId": "combat.skills", "X": 0, "Y": 1, "Scale": 1.5, "Skin": "solid" } ] } ] }
                """);
            var store = new OverlayLayoutStore(path);
            Assert.True(store.HideFromCapture);
            Assert.Equal(2, store.Active.Widgets.Count);
            Assert.All(store.Active.Widgets, widget => Assert.True(widget.Locked));
            Assert.True(store.AllLocked);
            // 旧文件没有不透明度：背景与内容都读取为不透明。
            Assert.All(store.Active.Widgets, widget => Assert.Equal((1d, 1d), (widget.BackgroundOpacity, widget.ContentOpacity)));
            // 旧文件没有宽高：按元件的默认尺寸显示，不需要迁移。
            var dps = store.Active.Widgets[0];
            Assert.Null(dps.Width);
            var descriptor = BuiltInModules.CreateCatalog(new SampleBackend()).Modules.SelectMany(module => module.Widgets)
                .Single(widget => widget.Id == "combat.dps");
            Assert.Equal(OverlayWidgetSizes.DefaultOf(descriptor),
                OverlayWidgetSizes.Resolve(dps, OverlayWidgetSizes.DefaultOf(descriptor), OverlayWidgetSizes.MinimumOf(descriptor)));
        }
        finally { DeleteLayoutFile(path); }
    }

    [Fact]
    public void WidgetOpacityIsSavedPerWidgetAndClampedOnLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moongate-layout-{Guid.NewGuid():N}.json");
        try
        {
            var store = new OverlayLayoutStore(path);
            var dps = store.Place("combat.dps");
            var skills = store.Place("combat.skills");
            (dps.BackgroundOpacity, dps.ContentOpacity) = (0, 0.6);
            store.Commit();

            // 重新读取：每个元件的背景与内容各自恢复，没有调整过的仍为不透明。
            WidgetPlacement Dps(OverlayLayoutStore layouts) => layouts.Active.Widgets.Single(widget => widget.WidgetId == "combat.dps");
            var reloaded = new OverlayLayoutStore(path);
            Assert.Equal((0d, 0.6), (Dps(reloaded).BackgroundOpacity, Math.Round(Dps(reloaded).ContentOpacity, 3)));
            var other = reloaded.Active.Widgets.Single(widget => widget.WidgetId == skills.WidgetId);
            Assert.Equal((1d, 1d), (other.BackgroundOpacity, other.ContentOpacity));

            // 文件里的值超出范围时限制在各自范围内：背景 0–1，内容 0.2–1，内容不会完全看不见。
            File.WriteAllText(path, File.ReadAllText(path)
                .Replace("\"BackgroundOpacity\": 0,", "\"BackgroundOpacity\": -0.5,").Replace("\"ContentOpacity\": 0.6", "\"ContentOpacity\": 0.01"));
            var clamped = Dps(new OverlayLayoutStore(path));
            Assert.Equal((WidgetPlacement.MinimumBackgroundOpacity, WidgetPlacement.MinimumContentOpacity), (clamped.BackgroundOpacity, clamped.ContentOpacity));
        }
        finally { DeleteLayoutFile(path); }

        // 浮窗上的按钮每点一次降 20%，到下限后回到不透明：背景可以降到 0，内容最低 20%。编辑页的“－／＋”步进 10%，不越过各自的下限与 100%。
        static List<double> Cycle(double minimum)
        {
            var values = new List<double>();
            for (double value = 1, i = 0; i < 7; i++) values.Add(Math.Round(value = WidgetPlacement.CycleOpacity(value, minimum), 3));
            return values;
        }
        Assert.Equal([0.8, 0.6, 0.4, 0.2, 0, 1, 0.8], Cycle(WidgetPlacement.MinimumBackgroundOpacity));
        Assert.Equal([0.8, 0.6, 0.4, 0.2, 1, 0.8, 0.6], Cycle(WidgetPlacement.MinimumContentOpacity));
        Assert.Equal(WidgetPlacement.MinimumContentOpacity, WidgetPlacement.StepOpacity(0.2, false, WidgetPlacement.MinimumContentOpacity));
        Assert.Equal(0, WidgetPlacement.StepOpacity(0.1, false, WidgetPlacement.MinimumBackgroundOpacity));
        Assert.Equal(1, WidgetPlacement.StepOpacity(1, true, WidgetPlacement.MinimumContentOpacity));
        Assert.Equal(0.9, WidgetPlacement.StepOpacity(1, false, WidgetPlacement.MinimumBackgroundOpacity), 3);
    }

    [Fact]
    public void LegacySingleOpacitySeedsBothValuesAndIsSavedAsTheNewFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moongate-layout-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "ActiveId": "a", "Layouts": [ { "Id": "a", "Name": "A", "Widgets": [
                    { "WidgetId": "combat.dps", "X": 1, "Y": 0, "Scale": 1, "Skin": "glass", "Opacity": 0.4 },
                    { "WidgetId": "combat.skills", "X": 0, "Y": 1, "Scale": 1, "Skin": "glass", "Opacity": 0.05 } ] } ] }
                """);
            // 升级前的单一不透明度同时作为背景与内容的初值（先限制在旧的 0.2–1），视觉与升级前一致。
            var store = new OverlayLayoutStore(path);
            var (dps, skills) = (store.Active.Widgets[0], store.Active.Widgets[1]);
            Assert.Equal((0.4, 0.4), (dps.BackgroundOpacity, dps.ContentOpacity));
            Assert.Equal((0.2, 0.2), (skills.BackgroundOpacity, skills.ContentOpacity));

            // 保存后写两个新字段、不再写旧字段；之后分别调整的值各自读回。
            dps.BackgroundOpacity = 0.1;
            store.Commit();
            store.Flush();
            var saved = File.ReadAllText(path);
            Assert.DoesNotContain("\"Opacity\"", saved);
            Assert.Contains("\"BackgroundOpacity\"", saved);
            Assert.Contains("\"ContentOpacity\"", saved);
            var reread = new OverlayLayoutStore(path).Active.Widgets[0];
            Assert.Equal((0.1, 0.4), (reread.BackgroundOpacity, reread.ContentOpacity));
        }
        finally { DeleteLayoutFile(path); }
    }

    [AvaloniaFact]
    public void OverlayControlsShowOnlyWhileTheCursorIsOverTheWidget()
    {
        var backend = new SampleBackend();
        var (window, store, probe) = OpenProbeOverlay(backend);
        try
        {
            var canvas = window.Canvas;
            var frame = canvas.BoundsOf("probe.widget")!.Value;
            var lockIcon = canvas.LockOf("probe.widget")!;
            // 光标不在元件上：已上锁元件的锁也隐藏，但仍在接收鼠标的区域里。
            canvas.HoverAt(new Point(frame.X - 40, frame.Y - 40));
            Assert.Null(canvas.HoveredWidgetId);
            Assert.False(canvas.ControlsShown("probe.widget"));
            Assert.Equal(0, lockIcon.Opacity);
            Assert.True(OverlayHitRegion.Accepts(window.InteractiveRegion,
                new PixelPoint((int)lockIcon.Bounds.Center.X, (int)lockIcon.Bounds.Center.Y)));

            // 光标进入上锁的元件（按位置判断，元件本身鼠标穿透）：锁显示，点锁可以解锁。
            canvas.HoverAt(frame.Center);
            Assert.Equal("probe.widget", canvas.HoveredWidgetId);
            Assert.True(canvas.ControlsShown("probe.widget"));
            Assert.Equal(1, lockIcon.Opacity);
            PressAt(window, Center(lockIcon, window));
            Assert.False(probe.Locked);
            LayoutProbe.Render(window);

            // 解锁后锁、不透明度按钮、“－／＋”与手柄同样随光标进出显示与隐藏。
            var zoom = canvas.ZoomOf("probe.widget")!;
            var handle = canvas.HandleOf("probe.widget")!;
            Assert.Equal(1, zoom.Opacity);
            Assert.Equal(1, handle.Opacity);
            canvas.HoverAt(null);
            Assert.Equal(0, zoom.Opacity);
            Assert.Equal(0, handle.Opacity);
            Assert.Equal(0, lockIcon.Opacity);

            // 背景与内容两个不透明度按钮：各点一次降到 80%，只有底板或内容跟随，设置写入版面；外框整体、锁与按钮本身不变淡。
            canvas.HoverAt(frame.Center);
            PressAt(window, Center(canvas.OpacityButtonOf("probe.widget", false)!, window));
            Assert.Equal((0.8, 1d), (Math.Round(probe.BackgroundOpacity, 3), probe.ContentOpacity));
            LayoutProbe.Render(window);
            Assert.Equal(0.8, canvas.PlateOf("probe.widget")!.Opacity, 3);
            Assert.Equal(1, canvas.ContentOf("probe.widget")!.Opacity);
            PressAt(window, Center(canvas.OpacityButtonOf("probe.widget", true)!, window));
            Assert.Equal(0.8, probe.ContentOpacity, 3);
            LayoutProbe.Render(window);
            Assert.Equal(0.8, canvas.ContentOf("probe.widget")!.Opacity, 3);
            Assert.Equal(0.8, canvas.PlateOf("probe.widget")!.Opacity, 3);
            Assert.Equal(1, canvas.FrameOf("probe.widget")!.Opacity);
            Assert.Equal(1, zoom.Opacity);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// 带控件的测试元件：说明文字下方一个发出意图的按钮，元件其余部分可以按住拖动。
    /// 按内建模块登记：扩展来源的条目随后端的扩展界面同步，后端每次变化都会撤下后端不认识的扩展模块。
    /// </summary>
    private static ModuleCatalog CatalogWithProbe(SampleBackend backend)
    {
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Register(new ModuleDescriptor("sample.probe", "测试元件", "测", Color.Parse("#4B5563"),
            ModuleOrigin.BuiltIn, () => new TextBlock(),
            [new("probe.widget", "测试元件", [], source => new StackPanel
            {
                Width = 180, Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "测试元件", Foreground = Brushes.White },
                    new Button
                    {
                        Name = "ProbeButton", Content = "按一下",
                        Command = new RelayCommand(_ => _ = source.SendIntentAsync("probe.press", "1", CancellationToken.None))
                    }
                }
            })]));
        return catalog;
    }

    private static (OverlayWindow Window, OverlayLayoutStore Store, WidgetPlacement Probe) OpenProbeOverlay(SampleBackend backend)
    {
        UiMotion.Enabled = false;
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var catalog = CatalogWithProbe(backend);
        var store = catalog.Overlay!.Store;
        var probe = store.Place("probe.widget");
        var window = new OverlayWindow(store, catalog, backend) { Width = 960, Height = 540 };
        window.Show();
        LayoutProbe.Render(window);
        return (window, store, probe);
    }

    private static Border LockFace(OverlayCanvas canvas, string widgetId) =>
        (Border)((LayoutTransformControl)canvas.LockOf(widgetId)!).Child!;

    private static void Drag(Window window, Point from, Vector by)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + by / 2);
        window.MouseMove(from + by);
        window.MouseUp(from + by, MouseButton.Left);
        LayoutProbe.Settle();
    }

    [AvaloniaFact]
    public void LockedWidgetPassesInputThroughAndItsLockStillToggles()
    {
        var backend = new SampleBackend();
        var (window, store, probe) = OpenProbeOverlay(backend);
        try
        {
            var button = Named<Button>(window, "ProbeButton");
            var lockIcon = window.Canvas.LockOf("probe.widget")!;
            Assert.True(probe.Locked);
            Assert.True(lockIcon.IsEffectivelyVisible);
            Assert.DoesNotContain("open", LockFace(window.Canvas, "probe.widget").Classes);
            Assert.Equal(OverlayCanvas.LockSize, lockIcon.Bounds.Width, 1);

            // 已上锁：按钮所在位置命中不到按钮，点下去没有意图，按住拖动也不移动元件。
            var buttonCenter = Center(button, window);
            var hit = window.InputHitTest(buttonCenter) as Visual;
            Assert.False(hit is not null && (hit == button || hit.GetVisualAncestors().Contains(button)));
            PressAt(window, buttonCenter);
            Assert.DoesNotContain(backend.UiIntents, intent => intent.Intent == "probe.press");
            var before = window.Canvas.BoundsOf("probe.widget")!.Value;
            Drag(window, before.Center, new Vector(80, 40));
            Assert.Equal(before, window.Canvas.BoundsOf("probe.widget")!.Value);

            // 点锁解锁：锁换成开锁，按钮可点并发出意图一次，元件不移动。
            PressAt(window, Center(lockIcon, window));
            Assert.False(probe.Locked);
            Assert.Contains("open", LockFace(window.Canvas, "probe.widget").Classes);
            LayoutProbe.Render(window);
            PressAt(window, Center(button, window));
            Assert.Single(backend.UiIntents, intent => intent.Intent == "probe.press" && intent.Argument == "1");
            Assert.Equal(before, window.Canvas.BoundsOf("probe.widget")!.Value);

            // 再点锁上锁，按钮又点不到。
            PressAt(window, Center(lockIcon, window));
            Assert.True(probe.Locked);
            Assert.DoesNotContain("open", LockFace(window.Canvas, "probe.widget").Classes);
            LayoutProbe.Render(window);
            PressAt(window, Center(button, window));
            Assert.Single(backend.UiIntents, intent => intent.Intent == "probe.press");
            Assert.True(store.AllLocked);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MeasurementWidgetFollowsThePhaseAndSendsStartAndCancel()
    {
        var backend = new SampleBackend();
        using var model = new OverlayWidgetModel("combat.measurement", backend);
        Assert.True(model.IsMeasurement);
        Assert.True(model.MeasureIdle);
        Assert.Equal("1:00 · 从首次伤害开始计时", model.MeasureSettings);
        Assert.True(model.HasMeasureResult);
        Assert.Equal("18,904", model.MeasureResult!.Dps);
        model.IntentCommand.Execute("combat.measure.start");
        Assert.Equal(("combat.measure.start", (string?)null), backend.UiIntents[^1]);
        Assert.True(model.MeasureWaiting);
        Assert.Equal("1:00", model.MeasureDuration);
        backend.SampleMeasurementPhase = MeasurementPhase.Running;
        backend.RaiseChanged();
        Assert.True(model.MeasureRunning);
        Assert.Equal(("1,032,700", "55,822"), (model.MeasureDamage, model.MeasureDps));
        Assert.InRange(model.MeasureProgress, 0.3, 0.32);
        // 元件显示且测量进行中时剩余时间按本机时钟重算。
        model.SetShown(true);
        Assert.True(model.ClockRunning);
        model.IntentCommand.Execute("combat.measure.cancel");
        Assert.True(model.MeasureIdle);
        Assert.False(model.ClockRunning);
    }

    [AvaloniaFact]
    public void BuiltInWidgetIntentCommandSendsNameAndArgument()
    {
        var backend = new SampleBackend();
        using var model = new OverlayWidgetModel("rooms.readiness", backend);
        model.IntentCommand.Execute("rooms.ready:true");
        model.IntentCommand.Execute("rooms.leave");
        Assert.Equal([("rooms.ready", "true"), ("rooms.leave", (string?)null)], backend.UiIntents.TakeLast(2).ToList());
    }

    [AvaloniaFact]
    public void UnlockedWidgetDragsOnTheOverlayAndTheDetailsFollow()
    {
        var backend = new SampleBackend();
        var main = LayoutProbe.Open(backend, 1280, 860, "presentation");
        var shell = (ShellViewModel)main.DataContext!;
        var page = main.GetVisualDescendants().OfType<OverlayPage>().Single();
        var model = (OverlayPageModel)page.DataContext!;
        var store = shell.Catalog.Overlay!.Store;
        var overlay = new OverlayWindow(store, shell.Catalog, backend) { Width = 960, Height = 540 };
        overlay.Show();
        LayoutProbe.Render(overlay);
        try
        {
            var placement = store.Active.Widgets.Single(widget => widget.WidgetId == "rooms.readiness");
            model.SelectedWidgetId = "rooms.readiness";
            var canvas = overlay.Canvas;
            var start = canvas.BoundsOf("rooms.readiness")!.Value;
            Assert.Equal((1d, 0d), (placement.X, placement.Y));

            store.ToggleLock(placement);
            LayoutProbe.Render(overlay);
            Assert.True(canvas.HandleOf("rooms.readiness")!.IsVisible);
            // 版面页详情同步显示已解锁。
            LayoutProbe.Render(main);
            Assert.Equal("已解锁", Named<TextBlock>(page, "LockStateText").Text);

            // 向左下拖动 (-101, 53)：跟随指针并对齐 8 px 格线，比例坐标随之改变。
            Drag(overlay, start.Center, new Vector(-101, 53));
            var moved = canvas.BoundsOf("rooms.readiness")!.Value;
            Assert.InRange(moved.X - start.X, -101 - 8, -101 + 8);
            Assert.InRange(moved.Y - start.Y, 53 - 8, 53 + 8);
            Assert.InRange(moved.X - Math.Round(moved.X / 8) * 8, -0.6, 0.6);
            Assert.InRange(moved.Y - Math.Round(moved.Y / 8) * 8, -0.6, 0.6);
            Assert.InRange(placement.X, 0.01, 0.99);
            Assert.InRange(placement.Y, 0.01, 0.99);
            Assert.False(canvas.IsManipulating);

            // 写回同一份版面：所选元件的详情显示新的比例。
            LayoutProbe.Render(main);
            var expected = $"横向 {placement.X:P0}，纵向 {placement.Y:P0}";
            Assert.Contains(model.SelectedDetails, row => row.Value == expected);

            // 版面数据的改动同样立即反映在浮窗上。
            placement.X = 0;
            store.Commit();
            LayoutProbe.Settle();
            Assert.Equal(12, canvas.BoundsOf("rooms.readiness")!.Value.X, 1);

            // 拖出浮窗时停在边缘。
            Drag(overlay, canvas.BoundsOf("rooms.readiness")!.Value.Center, new Vector(5000, 5000));
            Assert.Equal((1d, 1d), (placement.X, placement.Y));
        }
        finally
        {
            overlay.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public void ResizeHandleChangesTheSizeAndZoomButtonsScaleOnlyUnlockedWidgets()
    {
        var backend = new SampleBackend();
        var (window, store, probe) = OpenProbeOverlay(backend);
        try
        {
            var canvas = window.Canvas;
            var handle = canvas.HandleOf("probe.widget")!;
            var zoom = canvas.ZoomOf("probe.widget")!;
            // 已上锁：手柄与“－／＋”都不显示，也不接收鼠标。
            Assert.False(handle.IsVisible);
            Assert.False(zoom.IsVisible);
            Assert.All(canvas.HitAreas, area => Assert.Equal(default, area.Zoom));

            store.ToggleLock(probe);
            LayoutProbe.Render(window);
            Assert.True(handle.IsEffectivelyVisible);
            Assert.True(zoom.IsEffectivelyVisible);
            // 解锁后“－／＋”在锁左侧，属于接收鼠标的区域。
            Assert.True(zoom.Bounds.Right <= canvas.LockOf("probe.widget")!.Bounds.Left);
            var zoomCenter = zoom.Bounds.Center;
            Assert.True(OverlayHitRegion.Accepts(window.InteractiveRegion, new PixelPoint((int)zoomCenter.X, (int)zoomCenter.Y)));

            // 手柄改变元件尺寸（缩放前的逻辑像素，对齐 8 px），缩放倍率不变，外框按新尺寸排版。
            var start = canvas.BoundsOf("probe.widget")!.Value;
            Drag(window, Center(handle, window), new Vector(64, 40));
            Assert.Equal(1, probe.Scale);
            Assert.Equal(Math.Round((start.Width + 64) / 8) * 8, probe.Width);
            Assert.Equal(Math.Round((start.Height + 40) / 8) * 8, probe.Height);
            LayoutProbe.Render(window);
            Assert.Equal(probe.Width!.Value, canvas.BoundsOf("probe.widget")!.Value.Width, 0);
            Assert.Equal(probe.Height!.Value, canvas.BoundsOf("probe.widget")!.Value.Height, 0);

            // 不能拖到最小尺寸以下（扩展元件没有声明时为 96 × 48）。
            Drag(window, Center(handle, window), new Vector(-2000, -2000));
            Assert.Equal((OverlayWidgetSizes.FallbackMinimum.Width, OverlayWidgetSizes.FallbackMinimum.Height), (probe.Width, probe.Height));

            // “＋”每次放大 0.1，元件、锁与按钮一起变大；“－”不低于 0.5。
            PressAt(window, Center(canvas.ZoomButtonOf("probe.widget", true)!, window));
            LayoutProbe.Render(window);
            PressAt(window, Center(canvas.ZoomButtonOf("probe.widget", true)!, window));
            LayoutProbe.Render(window);
            Assert.Equal(1.2, probe.Scale, 3);
            // 布局按像素取整，允许 1 px 的偏差。
            Assert.InRange(canvas.BoundsOf("probe.widget")!.Value.Width - probe.Width!.Value * 1.2, -1, 1);
            Assert.InRange(canvas.LockOf("probe.widget")!.Bounds.Width - OverlayCanvas.LockSize * 1.2, -1, 1);
            Assert.InRange(zoom.Bounds.Width - OverlayCanvas.ZoomRowWidth * 1.2, -1, 1);
            for (var i = 0; i < 10; i++)
            {
                PressAt(window, Center(canvas.ZoomButtonOf("probe.widget", false)!, window));
                LayoutProbe.Render(window);
            }
            Assert.Equal(WidgetPlacement.MinimumScale, probe.Scale);
            LayoutProbe.SaveScreenshot(window, "overlay-unlocked-960x540");

            // 上锁后手柄与“－／＋”隐藏，“＋”所在位置不再改变缩放。
            store.ToggleLock(probe);
            LayoutProbe.Render(window);
            Assert.False(handle.IsVisible);
            Assert.False(zoom.IsVisible);
            PressAt(window, Center(canvas.LockOf("probe.widget")!, window) - new Vector(20, 0));
            Assert.Equal(WidgetPlacement.MinimumScale, probe.Scale);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DetailsToggleTheSelectedWidgetsLock()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "presentation");
        try
        {
            var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
            var model = (OverlayPageModel)page.DataContext!;
            Assert.False(Named<Button>(page, "LockToggleButton").IsEffectivelyVisible);

            Click(window, page.GetVisualDescendants().OfType<Button>()
                .Single(button => button.CommandParameter as string == "rooms.readiness" && button.Classes.Contains("tile")));
            Assert.Equal("rooms.readiness", model.SelectedWidgetId);
            Assert.Equal("已上锁", Named<TextBlock>(page, "LockStateText").Text);
            var toggle = Named<Button>(page, "LockToggleButton");
            Assert.Equal("解锁", toggle.Content);
            Click(window, toggle);
            var placement = model.Store.Active.Widgets.Single(widget => widget.WidgetId == "rooms.readiness");
            Assert.False(placement.Locked);
            Assert.Equal("已解锁", Named<TextBlock>(page, "LockStateText").Text);
            Assert.Equal("上锁", toggle.Content);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    private static string Unsupported(UiLanguage language) =>
        language == UiLanguage.TraditionalChinese ? "台服暫不支援" : "台服暂不支持";

    private static TopicNotice? NearbyUnsupported(string topic) => topic == PresentationTopics.Nearby
        ? TopicNoticeText.Describe(new(topic, CapabilityStatus.Unavailable, "provider.unavailable"))
        : null;

    private static Button NearbyTile(Visual page) => Named<ItemsControl>(page, "GameDataList").GetVisualDescendants().OfType<Button>()
        .Single(button => button.CommandParameter as string == "performance.nearby");

    private static OverlayWidgetView WidgetView(Visual root, string widgetId) =>
        root.GetVisualDescendants().OfType<OverlayWidgetView>().Single(view => ((OverlayWidgetModel)view.DataContext!).WidgetId == widgetId);

    private static List<string?> VisibleTexts(Visual view) =>
        view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))
            .Select(text => text.Text).ToList();

    private static (Window Window, OverlayPage Page, OverlayPageModel Model) OpenEditor(
        SampleBackend backend, double width, double height, UiLanguage language = UiLanguage.SimplifiedChinese,
        UiThemeChoice theme = UiThemeChoice.Light, bool placed = false)
    {
        var window = LayoutProbe.Open(backend, width, height, "presentation", language, theme);
        var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
        var model = (OverlayPageModel)page.DataContext!;
        if (placed)
        {
            model.Store.Place("performance.nearby");
            LayoutProbe.Settle();
        }
        return (window, page, model);
    }

    [AvaloniaFact]
    public void DefaultLayoutsOmitNearbyAndSavedPlacementsAreKept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moongate-layout-{Guid.NewGuid():N}.json");
        try
        {
            // 内建默认版面不再摆放依赖游戏内数据的元件。
            var fresh = new OverlayLayoutStore(path);
            Assert.All(fresh.Layouts, layout => Assert.DoesNotContain(layout.Widgets, widget => widget.WidgetId == "performance.nearby"));

            // 用户保存的版面里已摆放的元件，重新读取后位置、缩放与外观都保留。
            var placement = fresh.Place("performance.nearby");
            (placement.X, placement.Y, placement.Scale, placement.Skin) = (0.5, 1, 1.4, "solid");
            fresh.Commit();
            var reopened = new OverlayLayoutStore(path);
            var kept = reopened.Active.Widgets.Single(widget => widget.WidgetId == "performance.nearby");
            Assert.Equal((0.5, 1, 1.4, "solid"), (kept.X, kept.Y, kept.Scale, kept.Skin));
        }
        finally { DeleteLayoutFile(path); }
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese)]
    [InlineData(UiLanguage.TraditionalChinese)]
    public void LayerMarksUnsupportedWidgetAndStillAddsItToTheLayout(UiLanguage language)
    {
        var backend = new SampleBackend { PresentationNotices = NearbyUnsupported };
        var (window, page, model) = OpenEditor(backend, 1280, 860, language);
        try
        {
            Assert.DoesNotContain(model.Store.Active.Widgets, widget => widget.WidgetId == "performance.nearby");
            Click(window, Named<Button>(page, "GameDataButton"));
            Assert.True(model.IsGameDataOpen);
            Assert.True(Named<Panel>(page, "GameDataLayer").IsEffectivelyVisible);

            // 层内的卡片标出“台服暂不支持”。
            var tile = NearbyTile(page);
            Assert.Equal("performance.nearby", tile.CommandParameter);
            Assert.Contains(Unsupported(language), VisibleTexts(tile));

            // 仍可从层内加入版面：详情写明标注，编辑状态下浮窗上显示元件名称加标注，选取、缩放照常。
            Click(window, tile);
            Assert.Contains(model.Store.Active.Widgets, widget => widget.WidgetId == "performance.nearby");
            Assert.Equal("performance.nearby", model.SelectedWidgetId);
            Assert.Contains(model.SelectedDetails, row => row.Value == Unsupported(language));
            model.Store.BeginEdit();
            var overlay = new OverlayWindow(model.Store, model.Catalog, backend) { Width = 960, Height = 540 };
            overlay.Show();
            LayoutProbe.Render(overlay);
            Assert.Equal(["附近演奏字幕", Unsupported(language)], VisibleTexts(WidgetView(overlay, "performance.nearby")));
            Assert.True(overlay.Canvas.BoundsOf("performance.nearby")!.Value.Width > 0);
            model.Store.EndEdit();
            overlay.Close();
            var scale = model.Store.Active.Widgets.Single(widget => widget.WidgetId == "performance.nearby").Scale;
            model.ScaleCommand.Execute("+");
            Assert.True(model.Store.Active.Widgets.Single(widget => widget.WidgetId == "performance.nearby").Scale > scale);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LayerShowsOtherReasonsAndDropsTheMarkWhenTheTopicRecovers()
    {
        var backend = new SampleBackend
        {
            PresentationNotices = topic => topic == PresentationTopics.Nearby
                ? TopicNoticeText.Describe(new(topic, CapabilityStatus.Unavailable, "provider.unconfigured")) : null
        };
        var (window, page, model) = OpenEditor(backend, 1280, 860, placed: true);
        try
        {
            Click(window, Named<Button>(page, "GameDataButton"));
            var tile = NearbyTile(page);
            Assert.Contains("暂不可用：数据来源未配置", VisibleTexts(tile));

            backend.PresentationNotices = null;
            backend.RaiseChanged();
            LayoutProbe.Render(window);
            Assert.All(model.GameDataLibrary, item => Assert.False(item.HasNotice));
            tile = NearbyTile(page);
            Assert.DoesNotContain(VisibleTexts(tile), text => text!.Contains("暂不"));
            Assert.All(model.GameDataLibrary, item => Assert.False(item.HasNotice));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void GameDataLayerClosesWithEscapeOrOutsideClickAndFocusReturnsToTheEntry()
    {
        var (window, page, model) = OpenEditor(new SampleBackend { PresentationNotices = NearbyUnsupported }, 1280, 860);
        try
        {
            var entry = Named<Button>(page, "GameDataButton");
            entry.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            LayoutProbe.Render(window);
            Assert.True(model.IsGameDataOpen);
            Assert.Same(Named<Button>(page, "GameDataClose"), window.FocusManager?.GetFocusedElement());
            PressKey(window, Key.Escape);
            Assert.False(model.IsGameDataOpen);
            Assert.Same(entry, window.FocusManager?.GetFocusedElement());

            Click(window, entry);
            Assert.True(model.IsGameDataOpen);
            ClickOutside(window);
            Assert.False(model.IsGameDataOpen);
            Assert.Same(entry, window.FocusManager?.GetFocusedElement());
            Assert.DoesNotContain("performance.nearby", model.Store.Active.Widgets.Select(widget => widget.WidgetId));

            Click(window, entry);
            var panel = Named<Border>(page, "GameDataPanel");
            for (var index = 0; index < 8; index++)
            {
                PressKey(window, Key.Tab);
                var focused = window.FocusManager?.GetFocusedElement() as Visual;
                Assert.True(focused is not null && focused.GetVisualAncestors().Contains(panel),
                    $"第 {index + 1} 次 Tab 后焦点离开了层：{focused?.GetType().Name}");
            }
            Click(window, Named<Button>(page, "GameDataClose"));
            Assert.False(model.IsGameDataOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese)]
    [InlineData(UiLanguage.TraditionalChinese)]
    public void GameOverlayHidesSavedWidgetWhoseTopicIsUnsupportedAndShowsItAgainOnRecovery(UiLanguage language)
    {
        UiMotion.Enabled = false;
        UiText.Apply(Application.Current!, language);
        var backend = new SampleBackend { PresentationNotices = NearbyUnsupported };
        var catalog = BuiltInModules.CreateCatalog(backend);
        // 旧版面里已摆放的元件：摆放数据保留，但主题不可用时浮窗里没有内容、外框与占位。
        catalog.Overlay!.Store.Place("performance.nearby");
        catalog.Overlay.Store.Place("performance.now-playing");
        var window = new OverlayWindow(catalog.Overlay.Store, catalog, backend) { Width = 960, Height = 540 };
        window.Show();
        LayoutProbe.Settle();
        try
        {
            Assert.Contains("performance.nearby", window.Canvas.WidgetIds);
            var nearby = WidgetView(window, "performance.nearby");
            Assert.False(nearby.IsEffectivelyVisible);
            Assert.Equal(0, window.Canvas.BoundsOf("performance.nearby")!.Value.Width);
            Assert.DoesNotContain(VisibleTexts(window), text => text!.Contains("暂不支") || text.Contains("暫不支"));
            Assert.NotEmpty(VisibleTexts(WidgetView(window, "performance.now-playing")));

            backend.PresentationNotices = null;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.True(nearby.IsEffectivelyVisible);
            Assert.NotEmpty(VisibleTexts(nearby));
            Assert.True(window.Canvas.BoundsOf("performance.nearby")!.Value.Width > 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void GameOverlayShowsOtherMissingReasonsVerbatim()
    {
        UiMotion.Enabled = false;
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend
        {
            PresentationNotices = topic => topic == PresentationTopics.Nearby
                ? TopicNoticeText.Describe(new(topic, CapabilityStatus.Unavailable, "provider.unconfigured")) : null
        };
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Overlay!.Store.Place("performance.nearby");
        var window = new OverlayWindow(catalog.Overlay.Store, catalog, backend) { Width = 960, Height = 540 };
        window.Show();
        LayoutProbe.Settle();
        try
        {
            var nearby = WidgetView(window, "performance.nearby");
            Assert.True(nearby.IsEffectivelyVisible);
            Assert.Equal(["暂不可用：数据来源未配置"], VisibleTexts(nearby));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    private static void PressKey(Window window, Key key)
    {
        var physical = key == Key.Escape ? PhysicalKey.Escape : PhysicalKey.Tab;
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        LayoutProbe.Settle();
    }

    private static void ClickOutside(Window window)
    {
        // 页面左下方：在层的面板之外，也不在任何按钮上。
        window.MouseDown(new Point(600, 400), MouseButton.Left);
        window.MouseUp(new Point(600, 400), MouseButton.Left);
        LayoutProbe.Settle();
    }

    [AvaloniaFact]
    public void PendingModuleWidgetsMoveToTheGameDataLayerAndSavedPlacementsStay()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "presentation");
        try
        {
            var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
            var model = (OverlayPageModel)page.DataContext!;
            Assert.DoesNotContain(model.Library, item => item.WidgetId is "performance.now-playing" or "performance.nearby");
            Assert.Equal(["performance.now-playing", "performance.nearby"], model.GameDataLibrary.Select(item => item.WidgetId));
            // 没有缺失主题的元件只是换了位置，不加标注。
            Assert.All(model.GameDataLibrary, item => Assert.False(item.HasNotice));
            // 主元件库的磁贴里没有它们；层的入口可用，层默认关闭。
            var libraryTiles = Named<Card>(page, "LibraryPanel").GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("tile")).Select(button => button.CommandParameter as string).ToList();
            Assert.DoesNotContain("performance.nearby", libraryTiles);
            Assert.DoesNotContain("performance.now-playing", libraryTiles);
            Assert.True(Named<Button>(page, "GameDataButton").IsEffectivelyEnabled);
            Assert.False(Named<Panel>(page, "GameDataLayer").IsEffectivelyVisible);

            // 内建默认版面不再默认摆放当前演奏。
            Assert.All(model.Store.Layouts, layout => Assert.DoesNotContain(layout.Widgets, widget => widget.WidgetId == "performance.now-playing"));
        }
        finally { window.Close(); }

        // 已保存版面里的摆放照旧保留并显示。
        UiMotion.Enabled = false;
        var catalog = BuiltInModules.CreateCatalog(backend);
        var saved = catalog.Overlay!.Store.Place("performance.now-playing");
        saved.X = 1;
        var overlay = new OverlayWindow(catalog.Overlay.Store, catalog, backend) { Width = 960, Height = 540 };
        overlay.Show();
        LayoutProbe.Settle();
        try
        {
            Assert.Contains("performance.now-playing", overlay.Canvas.WidgetIds);
            var view = overlay.GetVisualDescendants().OfType<OverlayWidgetView>()
                .Single(item => ((OverlayWidgetModel)item.DataContext!).WidgetId == "performance.now-playing");
            Assert.True(view.IsEffectivelyVisible);
            Assert.True(overlay.Canvas.BoundsOf("performance.now-playing")!.Value.Width > 0);
        }
        finally { overlay.Close(); }
    }

    [AvaloniaFact]
    public void DpsWidgetHealthSwitchesTargetsAtOnceShowsTheDisabledReasonAndCollapsesWithoutResizing()
    {
        var motion = UiMotion.Enabled;
        UiMotion.Enabled = true;
        var backend = new SampleBackend { SampleCurrentTarget = SampleBackend.SampleTarget("ce-a", 80, 100, "怪物甲") };
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Overlay!.Store.Select("raid");
        var window = new OverlayWindow(catalog.Overlay.Store, catalog, backend) { Width = 960, Height = 540 };
        window.Show();
        LayoutProbe.Settle();
        try
        {
            var view = WidgetView(window, "combat.dps");
            var model = (OverlayWidgetModel)view.DataContext!;
            var gauge = Named<HealthBar>(view, "HealthGauge");
            var frame = window.Canvas.BoundsOf("combat.dps")!.Value;
            Assert.True(model.HealthExpanded);
            Assert.Equal(("怪物甲", "80.0%", "80 / 100"),
                (Named<TextBlock>(view, "HealthName").Text, Named<TextBlock>(view, "HealthPercent").Text, Named<TextBlock>(view, "HealthValue").Text));
            Assert.Equal(0.8, gauge.Shown, 6);

            // 后端换了目标：名称、数值与血量槽在同一次刷新里一起换，血量槽直接到新目标的比例（动效开启也不从旧比例过渡）。
            backend.SampleCurrentTarget = SampleBackend.SampleTarget("ce-b", 20, 100, "怪物乙");
            backend.RaiseChanged();
            Assert.Equal(("怪物乙", "20.0%", "20 / 100"),
                (Named<TextBlock>(view, "HealthName").Text, Named<TextBlock>(view, "HealthPercent").Text, Named<TextBlock>(view, "HealthValue").Text));
            Assert.Equal(0.2, gauge.Shown, 6);
            Assert.False(gauge.IsTransitioning);

            // 未读取：名称保留，数值写“未读取”，血量槽不填充，任何位置不出现 0。
            backend.SampleCurrentTarget = backend.SampleCurrentTarget with { Health = new(null, null, null, "unread", "combat.healthUnread") };
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal(0, gauge.Shown);
            Assert.Equal(["—", "首领", "怪物乙", "未读取"], VisibleTexts(Named<TargetHealthPanel>(view, "DpsHealth")));

            // 能力停用：整块只有停用原因。
            backend.CapabilityNotices = id => id == CapabilityIds.MonsterHealth ? "暂不可用：客户端版本不支持" : null;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal(["怪物血量暂不可用：客户端版本不支持"], VisibleTexts(Named<TargetHealthPanel>(view, "DpsHealth")));

            // 没有当前目标：区块收起，外框尺寸不变。
            backend.CapabilityNotices = null;
            backend.SampleCurrentTarget = null;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.False(model.HealthExpanded);
            Assert.False(Named<ExpandPanel>(view, "DpsHealthExpander").IsExpanded);
            Assert.Equal(frame.Size, window.Canvas.BoundsOf("combat.dps")!.Value.Size);
        }
        finally
        {
            window.Close();
            UiMotion.Enabled = motion;
        }
    }

    [AvaloniaFact]
    public void DpsWidgetHealthSwitchIsSavedInTheLayoutAndOldLayoutsShowIt()
    {
        UiMotion.Enabled = false;
        var path = Path.Combine(Path.GetTempPath(), $"moongate-layout-{Guid.NewGuid():N}.json");
        try
        {
            // 旧版面没有该字段：读入时显示，保存时不写出。
            File.WriteAllText(path, """
                { "ActiveId": "a", "Layouts": [ { "Id": "a", "Name": "A", "Widgets": [
                    { "WidgetId": "combat.dps", "X": 1, "Y": 0, "Scale": 1, "Skin": "glass" } ] } ] }
                """);
            var store = new OverlayLayoutStore(path);
            Assert.False(store.Active.Widgets.Single().HideMonsterHealth);
            var backend = new SampleBackend();
            var catalog = BuiltInModules.CreateCatalog(backend, store);
            using var page = new OverlayPageModel(catalog, catalog.Overlay!) { SelectedWidgetId = "combat.dps" };
            Assert.True(page.HasHealthSetting && page.HealthShown);
            var window = new OverlayWindow(store, catalog, backend) { Width = 960, Height = 540 };
            window.Show();
            LayoutProbe.Settle();
            try
            {
                var model = (OverlayWidgetModel)WidgetView(window, "combat.dps").DataContext!;
                Assert.True(model.HealthExpanded);

                // 在版面页隐藏：浮窗上的区块收起，保存进版面并在重新读取后保持。
                page.HealthCommand.Execute("hide");
                LayoutProbe.Settle();
                Assert.False(model.HealthExpanded);
                Assert.True(page.HealthHidden);
                store.Flush();
                Assert.Contains("\"HideMonsterHealth\": true", File.ReadAllText(path));
                Assert.True(new OverlayLayoutStore(path).Active.Widgets.Single().HideMonsterHealth);

                page.HealthCommand.Execute("show");
                LayoutProbe.Settle();
                Assert.True(model.HealthExpanded);
                store.Flush();
                Assert.DoesNotContain("HideMonsterHealth", File.ReadAllText(path));
            }
            finally { window.Close(); }
        }
        finally { DeleteLayoutFile(path); }
    }
}
