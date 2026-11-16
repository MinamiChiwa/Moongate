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

/// <summary>浮窗版面页与游戏浮窗：元件编辑、拖动对齐、多套版面、贴边、覆盖区域与比例、显示开关、需要游戏数据的元件、逐元件的锁、浮窗上直接拖动与缩放、接收鼠标区域。</summary>
public sealed class OverlayTests
{
    [AvaloniaFact]
    public void EditorPlacesSelectsScalesAndRemovesWidgets()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "presentation");
        var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
        var model = (OverlayPageModel)page.DataContext!;
        var canvas = page.GetVisualDescendants().OfType<OverlayCanvas>().Single();
        Assert.Equal(["rooms.readiness"], canvas.WidgetIds);

        Button Library(string id) => page.GetVisualDescendants().OfType<Button>()
            .Single(button => button.CommandParameter as string == id && button.Classes.Contains("tile"));
        Click(window, Library("combat.dps"));
        Assert.Contains("combat.dps", canvas.WidgetIds);
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
        Assert.DoesNotContain("combat.dps", canvas.WidgetIds);
        Assert.True(model.NoSelection);
        Assert.Empty(LayoutProbe.FindTextOverflow(window));
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingMovesWidgetOnEightPixelGridAndKeepsItInsideCanvas()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "presentation");
        var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
        var model = (OverlayPageModel)page.DataContext!;
        var canvas = page.GetVisualDescendants().OfType<OverlayCanvas>().Single();
        // 默认版面不再摆放当前演奏：先从“需要游戏数据的元件”里的元件放一个到画面中央。
        model.Store.Place("performance.now-playing");
        LayoutProbe.Settle();
        var before = canvas.BoundsOf("performance.now-playing")!.Value;
        var origin = canvas.TranslatePoint(before.Center, window)!.Value;

        window.MouseDown(origin, MouseButton.Left);
        window.MouseMove(origin + new Vector(101, 53));
        window.MouseUp(origin + new Vector(101, 53), MouseButton.Left);
        LayoutProbe.Settle();

        var after = canvas.BoundsOf("performance.now-playing")!.Value;
        Assert.Equal("performance.now-playing", model.SelectedWidgetId);
        // 画布按实际浮窗等比缩小，格线是实际浮窗上的 8 px，换到画布上是 8 × 显示比例。
        var scale = canvas.ContentScale;
        var grid = 8 * scale;
        Assert.InRange(after.X - before.X, 101 - grid, 101 + grid);
        Assert.InRange(after.Y - before.Y, 53 - grid, 53 + grid);
        // 画布上的位置经过布局取整，允许半个像素的偏差。
        Assert.InRange(after.X - Math.Round(after.X / grid) * grid, -0.6, 0.6);
        Assert.InRange(after.Y - Math.Round(after.Y / grid) * grid, -0.6, 0.6);

        // 拖出画布时停在边缘。
        var grab = canvas.TranslatePoint(after.Center, window)!.Value;
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(grab + new Vector(5000, 5000));
        window.MouseUp(grab + new Vector(5000, 5000), MouseButton.Left);
        LayoutProbe.Settle();
        var edge = canvas.BoundsOf("performance.now-playing")!.Value;
        Assert.True(edge.Right <= canvas.Bounds.Width);
        Assert.True(edge.Bottom <= canvas.Bounds.Height);
        var placement = model.Store.Active.Widgets.Single(widget => widget.WidgetId == "performance.now-playing");
        Assert.Equal((1d, 1d), (placement.X, placement.Y));
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

            File.WriteAllText(path, "{ not json");
            Assert.Equal(2, new OverlayLayoutStore(path).Layouts.Count);
        }
        finally
        {
            File.Delete(path);
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

    [Fact]
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
            File.Delete(path);
        }
    }

    // 实际使用中的版面（摘录）：7 个元件，Screen 都为空，color.current 已在浮窗上关闭。
    private const string UserLayoutJson = """
        { "ActiveId": "v3", "Layouts": [ { "Id": "v3", "Name": "版面 3", "Widgets": [
          { "WidgetId": "combat.dps", "X": 0.6550925925925926, "Y": 0.5048076923076923, "Scale": 1, "Opacity": 1, "Width": 168, "Height": 176, "Skin": "glass", "Locked": true, "Screen": null, "Closed": false },
          { "WidgetId": "combat.skills", "X": 0.5552995391705069, "Y": 0.4557522123893805, "Scale": 1, "Opacity": 1, "Width": 160, "Height": 104, "Skin": "glass", "Locked": true, "Screen": null, "Closed": false },
          { "WidgetId": "combat.measurement", "X": 0.6383495145631068, "Y": 0.7476190476190476, "Scale": 1, "Opacity": 0.9, "Skin": "glass", "Locked": true, "Screen": null, "Closed": false },
          { "WidgetId": "rooms.readiness", "X": 0.6237373737373737, "Y": 0.06626506024096386, "Scale": 1, "Opacity": 1, "Skin": "glass", "Locked": true, "Screen": null, "Closed": false },
          { "WidgetId": "combat.self-effects", "X": 0.3911917098445596, "Y": 0.2149122807017544, "Scale": 1, "Opacity": 1, "Skin": "glass", "Locked": true, "Screen": null, "Closed": false },
          { "WidgetId": "combat.target-effects", "X": 0.3963730569948187, "Y": 0.06756756756756757, "Scale": 1, "Opacity": 1, "Skin": "glass", "Locked": true, "Screen": null, "Closed": false },
          { "WidgetId": "color.current", "X": 0.5, "Y": 0.5, "Scale": 1, "Opacity": 1, "Skin": "glass", "Locked": true, "Screen": null, "Closed": true } ] } ] }
        """;

    [AvaloniaFact]
    public void EditorCanvasKeepsItsWidgetsAfterTheRetainedPageLeavesAndReturns()
    {
        // 版面编辑页被保留后离开再回来，画布离开视觉树时丢了版面来源，回来后一个元件也没有。
        var path = Path.Combine(Path.GetTempPath(), "moongate-user-layout-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, UserLayoutJson);
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        UiMotion.Enabled = false;
        var backend = new SampleBackend();
        var window = App.CreateMainWindow(backend, new OverlayLayoutStore(path));
        window.Width = 1280;
        window.Height = 860;
        window.Show();
        try
        {
            LayoutProbe.Select(window, "presentation");
            LayoutProbe.Select(window, "rooms");
            LayoutProbe.Select(window, "presentation");
            LayoutProbe.Render(window);
            var canvas = window.GetVisualDescendants().OfType<OverlayPage>().Single().Canvas;
            AssertReadable(canvas, 7);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void EditorCanvasOnTheUsersMonitorsShowsEveryWidgetAndRearrangesWhenItGetsItsSize()
    {
        var path = Path.Combine(Path.GetTempPath(), "moongate-user-layout-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, UserLayoutJson);
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        var store = new OverlayLayoutStore(path);
        var canvas = new OverlayCanvas { IsEditing = true, AspectRatio = 1.7778 };
        var area = OverlayArea.ForEditor(UserScreens, store.Active.Widgets.Select(widget => widget.Screen))!;
        canvas.ReferenceSize = area.Logical;
        canvas.Monitors = area.Monitors;
        canvas.Attach(store, catalog, backend);
        // 先以 0 宽度排版（页面分块放出前、保留页面重新挂上时的首次测量），再给出页面里的实际宽度。
        var holder = new Border { Width = 0, Child = canvas };
        var window = new Window { Width = 760, Height = 600, Content = holder };
        window.Show();
        try
        {
            LayoutProbe.Render(window);
            holder.Width = 682;
            LayoutProbe.Render(window);
            Assert.Equal(new Size(1920, 1080), area.Logical);
            Assert.Equal("主显示器（1920×1080 · 100%）", OverlayArea.Caption(area.Screens));
            AssertReadable(canvas, 7);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    // 每个元件都建出、在画布里、按“缩放前尺寸 × 画布比例”显示且边长不小于 32 px；关闭的元件变淡。
    private static void AssertReadable(OverlayCanvas canvas, int count)
    {
        Assert.Equal(count, canvas.WidgetIds.Count);
        Assert.True(canvas.ContentScale > 0.3, canvas.ContentScale.ToString());
        var report = string.Join("; ", canvas.WidgetIds.Select(id => $"{id} {canvas.BoundsOf(id)}"));
        foreach (var id in canvas.WidgetIds)
        {
            var bounds = canvas.BoundsOf(id)!.Value;
            Assert.True(canvas.FrameOf(id)!.IsEffectivelyVisible, id);
            Assert.True(bounds.X >= 0 && bounds.Y >= 0 && bounds.Right <= canvas.Bounds.Width + 0.5 && bounds.Bottom <= canvas.Bounds.Height + 0.5, $"{id} {bounds} in {canvas.Bounds}");
            Assert.True(Math.Min(bounds.Width, bounds.Height) >= 32, report);
            if (canvas.SizeOf(id) is { } size)
                Assert.InRange(bounds.Width - size.Width * canvas.ContentScale, -1, 1);
        }
        Assert.Equal(0.4, canvas.FrameOf("color.current")!.Opacity, 2);
        Console.WriteLine("editor canvas " + canvas.Bounds + " scale " + canvas.ContentScale + ": " + report);
    }

    [Fact]
    public void EditorCoversOnlyMonitorsThatHoldWidgets()
    {
        // 编辑画布此前画整个虚拟桌面（3840 宽），元件只有约 1/5；现在只画摆有元件的显示器。
        Assert.Equal(new Size(1920, 1080), OverlayArea.ForEditor(UserScreens, [null, null])!.Logical);
        var both = OverlayArea.ForEditor(UserScreens, [null, UserScreens[1].Key])!;
        Assert.Equal(new Size(3840, 1080), both.Logical);
        Assert.Equal(2, both.Monitors.Count);
        // 记住的显示器已不在：回到主显示器。
        Assert.Equal(new Size(1920, 1080), OverlayArea.ForEditor(UserScreens, ["3840,0,1920,1080@1"])!.Logical);
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
            Assert.Equal(960 + 12, onSecond.X, 1);
            // 副显示器的缩放比例是主显示器的 1.5 倍，元件在窗口里放大 1.5 倍，物理大小一致。
            Assert.Equal(onPrimary.Width * 1.5, onSecond.Width, 1);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void PreviewScaleIsCanvasWidthOverAreaWidth()
    {
        Assert.Equal(1d / 3, OverlayCanvas.PreviewScale(640, new Size(1920, 1080)), 6);
        Assert.Equal(0.25, OverlayCanvas.PreviewScale(640, new Size(2560, 1440)), 6);
        Assert.Equal(1, OverlayCanvas.PreviewScale(640, default));
        Assert.Equal(1, OverlayCanvas.PreviewScale(double.PositiveInfinity, new Size(1920, 1080)));
    }

    [AvaloniaFact]
    public void EditorCanvasShowsWidgetsAtTheirRealRelativeSize()
    {
        // 编辑画布按浮窗覆盖的范围（全部显示器拼成的虚拟桌面）等比缩小；游戏窗口不影响画布。
        var backend = new SampleBackend { GameWindowBounds = new PixelRect(0, 0, 3840, 2160) };
        var window = LayoutProbe.Open(backend, 1280, 860, "presentation");
        var canvas = window.GetVisualDescendants().OfType<OverlayPage>().Single().Canvas;
        var placement = canvas.WidgetIds[0];
        var desktop = OverlayArea.Resolve(OverlayArea.Describe(window.Screens))?.Logical ?? OverlayArea.FallbackLogical;
        Assert.Equal(desktop, canvas.ReferenceSize);
        Assert.Equal(canvas.Bounds.Width / desktop.Width, canvas.ContentScale, 6);
        // 画布取覆盖范围的宽高比。
        Assert.Equal(desktop.Width / desktop.Height, canvas.Bounds.Width / canvas.Bounds.Height, 2);

        // 元件的实际大小：画布上的宽度除以显示比例，等于浮窗上 1:1 的宽度。
        var overlay = new OverlayWindow(canvasStore(window), ((ShellViewModel)window.DataContext!).Catalog, backend) { Width = desktop.Width, Height = desktop.Height };
        overlay.Show();
        LayoutProbe.Settle();
        var real = overlay.Canvas.BoundsOf(placement)!.Value.Width;
        // 画布上的尺寸经过布局取整，缩小后的半个像素换回实际尺寸约 2%。
        Assert.InRange(canvas.BoundsOf(placement)!.Value.Width / canvas.ContentScale / real, 0.97, 1.03);
        overlay.Close();
        window.Close();

        static OverlayLayoutStore canvasStore(Window window) => ((ShellViewModel)window.DataContext!).Catalog.Overlay!.Store;
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
            // 旧文件没有不透明度：读取为不透明。
            Assert.All(store.Active.Widgets, widget => Assert.Equal(1, widget.Opacity));
            // 旧文件没有宽高：按元件的默认尺寸显示，不需要迁移。
            var dps = store.Active.Widgets[0];
            Assert.Null(dps.Width);
            var descriptor = BuiltInModules.CreateCatalog(new SampleBackend()).Modules.SelectMany(module => module.Widgets)
                .Single(widget => widget.Id == "combat.dps");
            Assert.Equal(OverlayWidgetSizes.DefaultOf(descriptor),
                OverlayWidgetSizes.Resolve(dps, OverlayWidgetSizes.DefaultOf(descriptor), OverlayWidgetSizes.MinimumOf(descriptor)));
        }
        finally { File.Delete(path); }
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
            dps.Opacity = 0.4;
            store.Commit();

            // 重新读取：每个元件各自的不透明度恢复，没有调整过的仍为不透明。
            var reloaded = new OverlayLayoutStore(path);
            Assert.Equal(0.4, reloaded.Active.Widgets.Single(widget => widget.WidgetId == "combat.dps").Opacity, 3);
            Assert.Equal(1, reloaded.Active.Widgets.Single(widget => widget.WidgetId == skills.WidgetId).Opacity);

            // 文件里的值超出范围时限制在 0.2–1，不会完全看不见。
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"Opacity\": 0.4", "\"Opacity\": 0.01"));
            Assert.Equal(WidgetPlacement.MinimumOpacity, new OverlayLayoutStore(path).Active.Widgets
                .Single(widget => widget.WidgetId == "combat.dps").Opacity);
        }
        finally { File.Delete(path); }

        // 浮窗上的按钮每点一次降 20%，到下限后回到不透明；编辑页的“－／＋”步进 10%，不越过 20% 与 100%。
        var cycle = new List<double>();
        for (double value = 1, i = 0; i < 6; i++) cycle.Add(value = WidgetPlacement.CycleOpacity(value));
        Assert.Equal([0.8, 0.6, 0.4, 0.2, 1, 0.8], cycle.Select(value => Math.Round(value, 3)));
        Assert.Equal(WidgetPlacement.MinimumOpacity, WidgetPlacement.StepOpacity(0.2, false));
        Assert.Equal(1, WidgetPlacement.StepOpacity(1, true));
        Assert.Equal(0.9, WidgetPlacement.StepOpacity(1, false), 3);
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

            // 不透明度按钮：点一次降到 80%，外框跟随，设置写入版面；锁与按钮本身不变淡。
            canvas.HoverAt(frame.Center);
            PressAt(window, Center(canvas.OpacityButtonOf("probe.widget")!, window));
            Assert.Equal(0.8, probe.Opacity, 3);
            LayoutProbe.Render(window);
            Assert.Equal(0.8, canvas.FrameOf("probe.widget")!.Opacity, 3);
            Assert.Equal(1, zoom.Opacity);
        }
        finally { window.Close(); }
    }

    /// <summary>扩展模块的测试元件：说明文字下方一个发出意图的按钮，元件其余部分可以按住拖动。</summary>
    private static ModuleCatalog CatalogWithProbe(SampleBackend backend)
    {
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Register(new ModuleDescriptor("sample.probe", "测试元件", "测", Color.Parse("#4B5563"),
            ModuleOrigin.Extension, () => new TextBlock(),
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
    public void UnlockedWidgetDragsOnTheOverlayAndTheEditorFollows()
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
            // 编辑页详情与画布上的锁同步显示已解锁。
            LayoutProbe.Render(main);
            Assert.Equal("已解锁", Named<TextBlock>(page, "LockStateText").Text);
            Assert.Contains("open", LockFace(page.Canvas, "rooms.readiness").Classes);

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

            // 写回同一份版面：编辑画布按同一比例坐标摆放，所选元件的详情显示新的比例。
            LayoutProbe.Render(main);
            var editor = page.Canvas;
            var shown = editor.BoundsOf("rooms.readiness")!.Value;
            var inset = 12 * editor.ContentScale;
            Assert.Equal(placement.X, (shown.X - inset) / (editor.Bounds.Width - shown.Width - inset * 2), 2);
            Assert.Equal(placement.Y, (shown.Y - inset) / (editor.Bounds.Height - shown.Height - inset * 2), 2);
            var expected = $"横向 {placement.X:P0}，纵向 {placement.Y:P0}";
            Assert.Contains(model.SelectedDetails, row => row.Value == expected);

            // 编辑页的改动同样立即反映在浮窗上。
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
    public void EditorHandleChangesTheSizeAndDetailsFollow()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "presentation");
        try
        {
            var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
            var model = (OverlayPageModel)page.DataContext!;
            var canvas = page.Canvas;
            var placement = model.Store.Active.Widgets.Single(widget => widget.WidgetId == "rooms.readiness");
            model.SelectedWidgetId = "rooms.readiness";
            LayoutProbe.Render(window);
            var size = canvas.SizeOf("rooms.readiness")!.Value;
            Assert.Contains(model.SelectedDetails, row => row.Value == $"{size.Width:0} × {size.Height:0}");

            // 编辑画布上的手柄同样改变尺寸：拖动距离按实际浮窗像素换算。
            var handle = canvas.HandleOf("rooms.readiness")!;
            Drag(window, Center(handle, window), new Vector(16 * canvas.ContentScale * 8, 0));
            Assert.Equal(size.Width + 128, placement.Width!.Value, 0);
            Assert.Equal(1, placement.Scale);
            Assert.Contains(model.SelectedDetails, row => row.Value == $"{size.Width + 128:0} × {placement.Height:0}");

            // 画布上的“＋”与详情里的缩放控件改的是同一个倍率。
            var plus = canvas.ZoomButtonOf("rooms.readiness", true)!;
            Assert.True(plus.IsEffectivelyVisible);
            PressAt(window, Center(plus, window));
            Assert.Equal(1.1, placement.Scale, 3);
            Assert.Equal("110%", model.ScaleText.Replace(" ", string.Empty));
            model.ScaleCommand.Execute("-");
            Assert.Equal(1, placement.Scale, 3);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EditorShowsLockIconsAndTogglesTheSelectedWidgetsLock()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "presentation");
        try
        {
            var page = window.GetVisualDescendants().OfType<OverlayPage>().Single();
            var model = (OverlayPageModel)page.DataContext!;
            var canvas = page.Canvas;
            var lockIcon = canvas.LockOf("rooms.readiness")!;
            // 画布上的锁只作示意：可见，不接收点击。
            Assert.True(lockIcon.IsEffectivelyVisible);
            Assert.False(lockIcon.IsHitTestVisible);
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
            Assert.Contains("open", LockFace(canvas, "rooms.readiness").Classes);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));

            // 编辑画布上的拖动不受锁状态限制。
            model.Store.ToggleLock(placement);
            LayoutProbe.Render(window);
            var before = canvas.BoundsOf("rooms.readiness")!.Value;
            // 点详情里的按钮时页面滚动过，先让画布回到视野里。
            canvas.BringIntoView();
            LayoutProbe.Render(window);
            Drag(window, canvas.TranslatePoint(before.Center, window)!.Value, new Vector(-120, 60));
            Assert.NotEqual(before, canvas.BoundsOf("rooms.readiness")!.Value);
            Assert.True(placement.Locked);
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

    private static (Window Window, OverlayPage Page, OverlayPageModel Model, OverlayCanvas Canvas) OpenEditor(
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
        return (window, page, model, page.GetVisualDescendants().OfType<OverlayCanvas>().Single());
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
        finally { File.Delete(path); }
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese)]
    [InlineData(UiLanguage.TraditionalChinese)]
    public void LayerMarksUnsupportedWidgetAndStillAddsItToTheLayout(UiLanguage language)
    {
        var backend = new SampleBackend { PresentationNotices = NearbyUnsupported };
        var (window, page, model, canvas) = OpenEditor(backend, 1280, 860, language);
        try
        {
            Assert.DoesNotContain("performance.nearby", canvas.WidgetIds);
            Click(window, Named<Button>(page, "GameDataButton"));
            Assert.True(model.IsGameDataOpen);
            Assert.True(Named<Panel>(page, "GameDataLayer").IsEffectivelyVisible);

            // 层内的卡片标出“台服暂不支持”。
            var tile = NearbyTile(page);
            Assert.Equal("performance.nearby", tile.CommandParameter);
            Assert.Contains(Unsupported(language), VisibleTexts(tile));

            // 仍可从层内加入版面：画布上显示元件名称加标注，选取、缩放照常。
            Click(window, tile);
            Assert.Contains("performance.nearby", canvas.WidgetIds);
            Assert.Equal("performance.nearby", model.SelectedWidgetId);
            Assert.Equal(["附近演奏字幕", Unsupported(language)], VisibleTexts(WidgetView(canvas, "performance.nearby")));
            Assert.True(canvas.BoundsOf("performance.nearby")!.Value.Width > 0);
            Assert.Contains(model.SelectedDetails, row => row.Value == Unsupported(language));
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
        var (window, page, model, canvas) = OpenEditor(backend, 1280, 860, placed: true);
        try
        {
            Click(window, Named<Button>(page, "GameDataButton"));
            var tile = NearbyTile(page);
            Assert.Contains("暂不可用：provider.unconfigured", VisibleTexts(tile));
            Assert.Contains("暂不可用：provider.unconfigured", VisibleTexts(WidgetView(canvas, "performance.nearby")));

            backend.PresentationNotices = null;
            backend.RaiseChanged();
            LayoutProbe.Render(window);
            Assert.All(model.GameDataLibrary, item => Assert.False(item.HasNotice));
            tile = NearbyTile(page);
            Assert.DoesNotContain(VisibleTexts(tile), text => text!.Contains("暂不"));
            var shown = VisibleTexts(WidgetView(canvas, "performance.nearby"));
            Assert.NotEmpty(shown);
            Assert.DoesNotContain(shown, text => text!.Contains("暂不"));
            Assert.All(model.GameDataLibrary, item => Assert.False(item.HasNotice));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void GameDataLayerClosesWithEscapeOrOutsideClickAndFocusReturnsToTheEntry()
    {
        var (window, page, model, _) = OpenEditor(new SampleBackend { PresentationNotices = NearbyUnsupported }, 1280, 860);
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
            Assert.Equal(["暂不可用：provider.unconfigured"], VisibleTexts(nearby));
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
}
