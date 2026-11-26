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

/// <summary>侧栏：外壳入口分组、“未实现模块”分组的收起与展开、模块入口的默认顺序与拖动排序。</summary>
public sealed class ShellSidebarTests : IDisposable
{
    [AvaloniaFact]
    public void ShellEntriesAndLanguageShareOneGroupWithUniformRows()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860);
        var group = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "ShellGroup");
        var tiles = group.GetVisualDescendants().OfType<MabiTool.Desktop.Controls.IconTile>().ToList();
        Assert.Equal(4, tiles.Count);
        var left = tiles.Select(tile => Math.Round(tile.TranslatePoint(default, group)!.Value.X)).Distinct().ToList();
        Assert.Single(left);

        var simplified = group.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LanguageSimplified");
        var traditional = group.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LanguageTraditional");
        Assert.Contains("active", simplified.Classes);
        Click(window, traditional);
        Assert.Contains("active", traditional.Classes);
        Assert.DoesNotContain("active", simplified.Classes);
        window.Close();
    }

    private static Button Entry(Visual root, string moduleId) =>
        root.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is NavigationItem item && item.Module.Id == moduleId);

    private static void ResetPreference() => SidebarPreference.Restore(false);

    /// <summary>打开主窗口但不选择任何模块：默认页。</summary>
    private static Window OpenDefault(double width = 1280, double height = 860, UiLanguage language = UiLanguage.SimplifiedChinese,
        UiThemeChoice theme = UiThemeChoice.Light, IDesktopBackend? backend = null) =>
        LayoutProbe.Open(backend ?? new SampleBackend(), width, height, null, language, theme);

    private const string PendingId = "ext.pending";

    /// <summary>主窗口，侧栏多一个登记为“未实现”的模块：乐谱库已实现，内建模块里没有未实现的，分组由这个示例模块提供。</summary>
    private static Window OpenWithPending(UiLanguage language = UiLanguage.SimplifiedChinese, IDesktopBackend? backend = null)
    {
        UiText.Apply(Application.Current!, language);
        UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        UiMotion.Enabled = false;
        backend ??= new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        // 内建来源：扩展来源的条目会在后端扩展列表变化时被整体替换掉。
        catalog.Register(new ModuleDescriptor(PendingId, "未实现模块（示例）", "未", Color.Parse("#4B5563"), ModuleOrigin.BuiltIn,
            () => new Pages.PendingPage { DataContext = new Pages.PendingPageModel("未实现模块", "示例") }, pending: true));
        var window = new MainWindow { DataContext = new ShellViewModel(catalog, backend), Width = 1280, Height = 860 };
        window.Show();
        LayoutProbe.Settle();
        return window;
    }

    [AvaloniaFact]
    public void MusicLibraryIsARegularBuiltInModuleRightAfterRoomsAndNoPendingGroupAppears()
    {
        ResetPreference();
        var window = OpenDefault();
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            Assert.Empty(shell.PendingModules);
            Assert.False(shell.HasPending);
            Assert.False(Named<StackPanel>(window, "PendingGroup").IsEffectivelyVisible);
            var order = shell.BuiltIn.Select(item => item.Module.Id).ToList();
            Assert.Equal(order.IndexOf("rooms") + 1, order.IndexOf("music-library"));
            Assert.False(shell.BuiltIn[order.IndexOf("music-library")].Module.Pending);
            Assert.True(Entry(window, "music-library").IsEffectivelyVisible);
            Assert.Equal("combat-observation", shell.Selected.Module.Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarCollapsesPendingModulesAndStartsOnTheFirstImplementedOne()
    {
        ResetPreference();
        var window = OpenWithPending();
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            Assert.Equal([PendingId], shell.PendingModules.Select(item => item.Module.Id));
            Assert.DoesNotContain(shell.BuiltIn, item => item.Module.Id == PendingId);
            // 默认页不是未实现模块：取第一个未标记的内建模块。
            Assert.Equal(shell.BuiltIn[0].Module.Id, shell.Selected.Module.Id);
            Assert.NotEqual(PendingId, shell.Selected.Module.Id);

            // 侧栏上有收起的分组，没有该入口。
            var group = Named<StackPanel>(window, "PendingGroup");
            Assert.True(group.IsEffectivelyVisible);
            var toggle = Named<ToggleButton>(window, "PendingToggle");
            Assert.False(toggle.IsChecked);
            Assert.False(Named<ItemsControl>(window, "PendingList").IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                button => button.IsEffectivelyVisible && button.DataContext is NavigationItem { Module.Id: PendingId });
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(toggle)));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese, "未实现模块")]
    [InlineData(UiLanguage.TraditionalChinese, "未實作模組")]
    public void ExpandingTheGroupListsTheModuleAndOpensItsPage(UiLanguage language, string title)
    {
        ResetPreference();
        var window = OpenWithPending(language);
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            var toggle = Named<ToggleButton>(window, "PendingToggle");
            Assert.Contains(title, toggle.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));

            Click(window, toggle);
            Assert.True(toggle.IsChecked);
            Assert.True(shell.IsPendingOpen);
            Assert.True(Named<ItemsControl>(window, "PendingList").IsEffectivelyVisible);
            var entry = Entry(window, PendingId);
            Assert.True(entry.IsEffectivelyVisible);

            // 入口与普通入口同样式，可进入页面。
            Assert.Contains("nav", entry.Classes);
            Click(window, entry);
            Assert.Equal(PendingId, shell.Selected.Module.Id);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));

            // 再按一次标题收起：选中的模块在分组内，分组仍保持展开。
            Click(window, toggle);
            Assert.True(shell.IsPendingOpen);
            Assert.True(toggle.IsChecked);
            Assert.True(Entry(window, PendingId).IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            ResetPreference();
        }
    }

    [AvaloniaFact]
    public void SelectingAModuleInTheGroupKeepsItOpenWithoutChangingTheSavedChoice()
    {
        ResetPreference();
        var window = OpenWithPending();
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            Assert.False(shell.IsPendingOpen);
            LayoutProbe.Select(window, PendingId);
            Assert.True(shell.IsPendingOpen);
            Assert.True(Named<ItemsControl>(window, "PendingList").IsEffectivelyVisible);
            Assert.False(SidebarPreference.PendingExpanded);

            // 切到别的模块后回到用户的选择：收起。
            LayoutProbe.Select(window, "rooms");
            Assert.False(shell.IsPendingOpen);
            Assert.False(Named<ItemsControl>(window, "PendingList").IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ToggleWorksFromTheKeyboardAndExposesItsState()
    {
        ResetPreference();
        var window = OpenWithPending();
        try
        {
            var toggle = Named<ToggleButton>(window, "PendingToggle");
            toggle.Focus(NavigationMethod.Tab);
            Assert.Same(toggle, window.FocusManager?.GetFocusedElement());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            LayoutProbe.Settle();
            // 展开状态通过开关的选中状态暴露给辅助技术。
            Assert.True(toggle.IsChecked);
            Assert.True(Named<ItemsControl>(window, "PendingList").IsEffectivelyVisible);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            LayoutProbe.Settle();
            Assert.False(toggle.IsChecked);
            Assert.False(Named<ItemsControl>(window, "PendingList").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            ResetPreference();
        }
    }

    [AvaloniaFact]
    public void ExpandedStateIsSavedAndRestoredFromThePreferenceFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moongate-prefs-{Guid.NewGuid():N}.json");
        ResetPreference();
        var window = OpenWithPending();
        try
        {
            var store = new DesktopPreferenceStore(path);
            // 缺省与旧偏好文件（没有这个字段）都按收起处理。
            Assert.False(store.Load().PendingExpanded);
            File.WriteAllText(path, "{ \"Language\": \"SimplifiedChinese\", \"Theme\": \"Light\", \"Motion\": true }");
            Assert.False(store.Load().PendingExpanded);

            Click(window, Named<ToggleButton>(window, "PendingToggle"));
            Assert.True(DesktopPreferences.Current.PendingExpanded);
            store.Save(DesktopPreferences.Current);
            Assert.Contains("\"PendingExpanded\": true", File.ReadAllText(path));
            window.Close();

            // 重新启动：读取偏好后分组直接展开。
            ResetPreference();
            SidebarPreference.Restore(store.Load().PendingExpanded);
            window = OpenWithPending();
            Assert.True(Named<ToggleButton>(window, "PendingToggle").IsChecked);
            Assert.True(Entry(window, PendingId).IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            File.Delete(path);
            ResetPreference();
        }
    }

    [AvaloniaFact]
    public void ChangingTheStateRaisesTheSaveNotification()
    {
        ResetPreference();
        var saved = 0;
        void Count(object? sender, EventArgs e) => saved++;
        SidebarPreference.Changed += Count;
        var window = OpenWithPending();
        try
        {
            Click(window, Named<ToggleButton>(window, "PendingToggle"));
            Assert.Equal(1, saved);
            Click(window, Named<ToggleButton>(window, "PendingToggle"));
            Assert.Equal(2, saved);
        }
        finally
        {
            SidebarPreference.Changed -= Count;
            window.Close();
            ResetPreference();
        }
    }

    [AvaloniaFact]
    public void NoGroupAppearsWithoutPendingModules()
    {
        ResetPreference();
        var backend = new SampleBackend();
        var catalog = new ModuleCatalog();
        catalog.Register(new ModuleDescriptor("sample.fishing", "釣魚計時（範例）", "釣", Color.Parse("#4B5563"),
            ModuleOrigin.BuiltIn, () => new Pages.PendingPage { DataContext = new Pages.PendingPageModel("釣魚計時", "範例") }));
        var window = new MainWindow { DataContext = new ShellViewModel(catalog, backend), Width = 1280, Height = 860 };
        window.Show();
        LayoutProbe.Settle();
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            Assert.False(shell.HasPending);
            Assert.False(Named<StackPanel>(window, "PendingGroup").IsEffectivelyVisible);
            Assert.Equal("sample.fishing", shell.Selected.Module.Id);
        }
        finally { window.Close(); }
    }

    public ShellSidebarTests() => SidebarPreference.Restore(false);

    public void Dispose()
    {
        SidebarPreference.Restore(false);
        UiMotion.Enabled = false;
    }

    private static ShellViewModel Shell(Window window) => (ShellViewModel)window.DataContext!;

    private static ItemsControl List(Window window) => Named<ItemsControl>(window, "BuiltInList");

    private static List<string> Order(Window window) => Shell(window).BuiltIn.Select(item => item.Module.Id).ToList();

    private static Control Container(Window window, string id) =>
        List(window).ContainerFromItem(Shell(window).BuiltIn.Single(item => item.Module.Id == id))!;

    private static Point Center(Window window, string id)
    {
        var button = Nav(window, id);
        return button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
    }

    // 实际画出的位置（含变换）在分组坐标里的矩形。
    private static Rect Drawn(Window window, Control container) =>
        new Rect(container.Bounds.Size).TransformToAABB(container.TransformToVisual(List(window))!.Value);

    // 按住入口，分几步拖到 to，期间每一步都画一帧。
    internal static void DragTo(Window window, Point from, Point to, bool release = true)
    {
        window.MouseDown(from, MouseButton.Left);
        for (var step = 1; step <= 6; step++)
        {
            window.MouseMove(new Point(from.X + (to.X - from.X) * step / 6, from.Y + (to.Y - from.Y) * step / 6));
            LayoutProbe.Settle();
        }
        LayoutProbe.Render(window);
        if (release)
        {
            window.MouseUp(to, MouseButton.Left);
            LayoutProbe.Render(window);
        }
    }

    [AvaloniaFact]
    public void DefaultOrderPutsDpsFirstAndStartsThere()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860);
        try
        {
            Assert.Equal(["combat-observation", "rooms", "music-library", "color", "inventory", "character", "market", "life", "presentation"], Order(window));
            Assert.Equal("combat-observation", Shell(window).Selected.Module.Id);
            Assert.Equal("DPS检测", Shell(window).BuiltIn[0].Title);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraggingAnEntryReordersTheGroupWithoutSelectingIt(bool motion)
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        UiMotion.Enabled = motion;
        try
        {
            var target = Center(window, "combat-observation");
            var start = Center(window, "color");
            var states = new List<bool>();
            EventHandler<bool> watch = (_, state) => states.Add(state);
            SidebarReorder.DragStateChanged += watch;
            try
            {
                DragTo(window, start, new Point(start.X, target.Y - 4), release: false);
                // 拽起：放大、在分组范围内；被越过的两项向下让位，不越出分组。
                var lifted = Container(window, "color");
                var list = new Rect(List(window).Bounds.Size);
                Assert.True(Drawn(window, lifted).Width > lifted.Bounds.Width + 1, "拽起的入口没有放大");
                Assert.Contains("dragging", Nav(window, "color").Classes);
                foreach (var id in new[] { "combat-observation", "rooms", "music-library" })
                {
                    var drawn = Drawn(window, Container(window, id));
                    Assert.True(drawn.Y > Container(window, id).Bounds.Y + 1, $"{id} 没有让位");
                }
                if (!motion)
                    foreach (var container in Shell(window).BuiltIn.Select(item => List(window).ContainerFromItem(item)!))
                        Assert.True(list.Inflate(new Thickness(4, 1)).Contains(Drawn(window, container)), $"入口 {Drawn(window, container)} 画出了分组范围 {list}");
                window.MouseUp(new Point(start.X, target.Y - 4), MouseButton.Left);
                LayoutProbe.Render(window);
            }
            finally { SidebarReorder.DragStateChanged -= watch; }

            Assert.Equal([true, false], states);
            Assert.Equal(["color", "combat-observation", "rooms", "music-library", "inventory", "character", "market", "life", "presentation"], Order(window));
            Assert.StartsWith("color,combat-observation,rooms", string.Join(',', SidebarPreference.ModuleOrder));
            // 拖动不是点击：页面不切换，选中项仍是 DPS检测，选中底色在它的新位置上。
            Assert.Equal("combat-observation", Shell(window).Selected.Module.Id);
            var selected = Nav(window, "combat-observation");
            ThemeAndMotionSlidingTests.AssertAtRest(selected);
            Assert.Equal(1, Shell(window).BuiltIn.IndexOf(Shell(window).Selected));
            Assert.DoesNotContain("dragging", Nav(window, "color").Classes);
            Pump(window, 300);
            foreach (var id in Order(window))
                Assert.True(Math.Abs(Drawn(window, Container(window, id)).Y - Container(window, id).Bounds.Y) < 0.5,
                    $"{id} 落位后仍有偏移");
            Assert.True(Container(window, "combat-observation").Bounds.Y > Container(window, "color").Bounds.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReleasingOutsideTheGroupPutsTheEntryBack()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            var start = Center(window, "inventory");
            // 拖到窗口右侧的页面区再松开。
            DragTo(window, start, new Point(start.X + 600, start.Y - 80));
            Assert.Equal(["combat-observation", "rooms", "music-library", "color", "inventory", "character", "market", "life", "presentation"], Order(window));
            Assert.Empty(SidebarPreference.ModuleOrder);
            foreach (var id in Order(window))
                Assert.True(Math.Abs(Drawn(window, Container(window, id)).Y - Container(window, id).Bounds.Y) < 0.5);
            Assert.Equal("combat-observation", Shell(window).Selected.Module.Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ShortMovesStillClickAndDraggingClampsToTheGroup()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            // 移动不到阈值仍是点击。
            var rooms = Center(window, "rooms");
            window.MouseDown(rooms, MouseButton.Left);
            window.MouseMove(new Point(rooms.X + 2, rooms.Y + 3));
            window.MouseUp(new Point(rooms.X + 2, rooms.Y + 3), MouseButton.Left);
            LayoutProbe.Settle();
            Assert.Equal("rooms", Shell(window).Selected.Module.Id);
            Assert.Equal(["combat-observation", "rooms", "music-library", "color", "inventory", "character", "market", "life", "presentation"], Order(window));

            // 拖到列表上方很远处：拽起的入口停在首位，不越出分组；松开后排在首位。
            var last = Center(window, "presentation");
            DragTo(window, last, new Point(last.X, last.Y - 900), release: false);
            var drawn = Drawn(window, Container(window, "presentation"));
            Assert.True(Math.Abs(drawn.Center.Y - Container(window, "combat-observation").Bounds.Center.Y) < 1,
                $"拽起的入口 {drawn} 应停在首位 {Container(window, "combat-observation").Bounds}");
            window.MouseUp(new Point(last.X, Container(window, "combat-observation").TranslatePoint(default, window)!.Value.Y + 4),
                MouseButton.Left);
            LayoutProbe.Render(window);
            Assert.Equal("presentation", Order(window)[0]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AltArrowsMoveTheFocusedEntry()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            Nav(window, "rooms").Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            LayoutProbe.Render(window);
            Assert.Equal(["combat-observation", "music-library", "rooms", "color", "inventory", "character", "market", "life", "presentation"], Order(window));
            Assert.Same(Nav(window, "rooms"), window.FocusManager!.GetFocusedElement());
            window.KeyPress(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
            window.KeyRelease(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
            window.KeyPress(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
            window.KeyRelease(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
            LayoutProbe.Render(window);
            Assert.Equal(["rooms", "combat-observation", "music-library", "color", "inventory", "character", "market", "life", "presentation"], Order(window));
            // 首位再往上不动；键盘移动不切换页面。
            window.KeyPress(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
            LayoutProbe.Render(window);
            Assert.Equal("rooms", Order(window)[0]);
            Assert.Equal("combat-observation", Shell(window).Selected.Module.Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OrderIsSavedAndRestoredAndNewModulesGoLast()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moongate-order-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DesktopPreferenceStore(path);
            SidebarPreference.Restore(false, ["inventory", "combat-observation", "music-library", "gone-module"]);
            store.Save(DesktopPreferences.Current);
            SidebarPreference.Restore(false);
            var loaded = store.Load();
            Assert.Equal(["inventory", "combat-observation", "music-library", "gone-module"], loaded.ModuleOrder!);
            SidebarPreference.Restore(loaded.PendingExpanded, loaded.ModuleOrder);

            var backend = new SampleBackend();
            var catalog = BuiltInModules.CreateCatalog(backend);
            catalog.Register(new ModuleDescriptor("ext.later", "后装扩展", "扩", Avalonia.Media.Color.Parse("#4B5563"),
                ModuleOrigin.Extension, () => new TextBlock()));
            catalog.Register(new ModuleDescriptor("ext.first", "已排序扩展", "扩", Avalonia.Media.Color.Parse("#4B5563"),
                ModuleOrigin.Extension, () => new TextBlock()));
            SidebarPreference.Restore(false, ["inventory", "combat-observation", "ext.first"]);
            var shell = new ShellViewModel(catalog, backend);
            // 保存过的排在前面，其余保持注册顺序排在末尾；扩展分组同样处理。
            Assert.Equal(["inventory", "combat-observation", "rooms", "music-library", "color", "character", "market", "life", "presentation"], shell.BuiltIn.Select(item => item.Module.Id));
            Assert.Equal(["ext.first", "ext.later"], shell.Extensions.Select(item => item.Module.Id));
            // 启动默认页取排在第一位的已实现内建模块。
            Assert.Equal("inventory", shell.Selected.Module.Id);
            // 不跨分组：外壳入口不能排序。
            Assert.Null(shell.GroupOf(shell.ShellEntries[0]));
            Assert.False(shell.MoveModule(shell.ShellEntries[0], 0));
            shell.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void DisabledModulesLeaveTheSidebarAndReturnToTheirPlace()
    {
        ResetPreference();
        var backend = new SampleBackend();
        var window = OpenDefault(backend: backend);
        var token = TestContext.Current.CancellationToken;
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            var order = shell.BuiltIn.Select(item => item.Module.Id).ToList();
            var rooms = shell.BuiltIn.Single(item => item.Module.Id == "rooms");
            var index = shell.BuiltIn.IndexOf(rooms);
            shell.Select(rooms);

            // 停用：入口从侧栏消失，当前页回到默认页。
            backend.SendIntentAsync("modules.toggle", "rooms", token).GetAwaiter().GetResult();
            LayoutProbe.Render(window);
            Assert.DoesNotContain(shell.BuiltIn, item => item.Module.Id == "rooms");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                button => button.DataContext is NavigationItem { Module.Id: "rooms" });
            Assert.Equal(shell.BuiltIn[0], shell.Selected);

            // 停用期间调整其他入口的顺序后再启用：停用的模块回到原来的位置。
            var last = shell.BuiltIn[^1];
            Assert.True(shell.MoveModule(last, 0));
            backend.SendIntentAsync("modules.toggle", "rooms", token).GetAwaiter().GetResult();
            LayoutProbe.Render(window);
            Assert.Equal(index, shell.BuiltIn.IndexOf(rooms));
            // 显示的入口依次填回原来占用的位置，停用的入口留在自己的位置上。
            var expected = order.Where(id => id != "rooms").ToList();
            expected.Remove(last.Module.Id);
            expected.Insert(0, last.Module.Id);
            expected.Insert(index, "rooms");
            Assert.Equal(expected, shell.BuiltIn.Select(item => item.Module.Id));
            Assert.Equal(expected, SidebarPreference.ModuleOrder.Where(id => expected.Contains(id)));
            Assert.True(Entry(window, "rooms").IsEffectivelyVisible);

            // 乐谱库同样按启用状态显示：停用后离开侧栏，启用后回到原来的位置。
            var musicIndex = shell.BuiltIn.Select(item => item.Module.Id).ToList().IndexOf("music-library");
            backend.SendIntentAsync("modules.toggle", "music-library", token).GetAwaiter().GetResult();
            LayoutProbe.Render(window);
            Assert.DoesNotContain(shell.BuiltIn, item => item.Module.Id == "music-library");
            backend.SendIntentAsync("modules.toggle", "music-library", token).GetAwaiter().GetResult();
            LayoutProbe.Render(window);
            Assert.Equal(musicIndex, shell.BuiltIn.Select(item => item.Module.Id).ToList().IndexOf("music-library"));
        }
        finally
        {
            window.Close();
            ResetPreference();
        }
    }

    [AvaloniaFact]
    public void PendingGroupIsSortedOnItsOwn()
    {
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Register(new ModuleDescriptor("ext.pending", "未实现扩展", "扩", Avalonia.Media.Color.Parse("#4B5563"),
            ModuleOrigin.Extension, () => new TextBlock(), pending: true));
        catalog.Register(new ModuleDescriptor("ext.pending.two", "另一个未实现扩展", "扩", Avalonia.Media.Color.Parse("#4B5563"),
            ModuleOrigin.Extension, () => new TextBlock(), pending: true));
        var shell = new ShellViewModel(catalog, backend);
        try
        {
            Assert.Equal(["ext.pending", "ext.pending.two"], shell.PendingModules.Select(item => item.Module.Id));
            Assert.True(shell.MoveModule(shell.PendingModules[1], 0));
            Assert.Equal(["ext.pending.two", "ext.pending"], shell.PendingModules.Select(item => item.Module.Id));
            Assert.Equal(9, shell.BuiltIn.Count);
            Assert.DoesNotContain(shell.BuiltIn, item => item.Module.Pending);
        }
        finally { shell.Dispose(); }
    }
}
