using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

/// <summary>“模块与扩展”页与模块注册：合并后端状态、启停、未实现状态、扩展模块入口、重复标识。</summary>
public sealed class ShellModulesTests
{
    [AvaloniaFact]
    public void ModulesPageMergesCatalogWithBackendStateAndTogglesModules()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.modules");
        var page = window.GetVisualDescendants().OfType<ModulesPage>().Single();
        var model = (ModulesPageModel)page.DataContext!;

        Assert.Contains(model.Cards, card => card.Id == "combat-observation" && card.Name == "DPS检测");
        // 只有后端知道的模块：名称与功能说明取自模块清单，来源一行写明没有前端页面（内部标识不上界面），并带上后端给出的原因。
        var environment = model.Cards.Single(card => card.Id == "environment");
        Assert.Equal("环境信息", environment.Name);
        Assert.Equal("提供游戏内时间、天气、频道与区域等环境信息", environment.Description);
        Assert.Equal("后端模块，没有前端页面", environment.Source);
        // 环境信息已由游戏连接器提供（宿主登记为已实现），样本后端报告为可用。
        Assert.StartsWith("可用", environment.State);
        Assert.DoesNotContain("未实", environment.State);
        Assert.Contains(VisibleTexts(page), text => text == "提供游戏内时间、天气、频道与区域等环境信息");
        // 模块清单的多语言文字按界面语言选取，缺当前语言时取另一种中文，旧宿主没有时为空（卡片以标识显示）。
        Assert.Equal("游戏连接", ModuleText.Pick(new Dictionary<string, string> { ["zh-Hans"] = "游戏连接", ["zh-Hant"] = "遊戲連線" }));
        Assert.Equal("遊戲連線", ModuleText.Pick(new Dictionary<string, string> { ["zh-Hant"] = "遊戲連線" }));
        Assert.Null(ModuleText.Pick(null));
        Assert.Equal(6, model.ExtensionPoints.Count);
        Assert.Equal(6, model.AssetSlots.Count);

        // 核心模块（模块清单标为 essential）没有停用入口，只写明始终启用。
        var core = model.Cards.Single(card => card.Id == "game-access");
        Assert.True(core.IsEssential);
        Assert.False(core.CanToggle);
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<Button>(),
            button => button.IsEffectivelyVisible && button.CommandParameter as string == "modules.toggle:game-access");
        Assert.Contains(VisibleTexts(page), text => text == "核心模块，始终启用");

        var toggle = page.GetVisualDescendants().OfType<Button>()
            .Single(button => button.CommandParameter as string == "modules.toggle:color");
        Click(window, toggle);
        Assert.Equal("已停用", model.Cards.Single(card => card.Id == "color").ToggleLabel);
        window.Close();
    }

    [AvaloniaFact]
    public void ExtensionModuleAppearsInSidebarAndOpensItsPage()
    {
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Register(new ModuleDescriptor("sample.fishing", "釣魚計時（範例）", "釣", Color.Parse("#4B5563"),
            ModuleOrigin.Extension, () => new PendingPage { DataContext = new PendingPageModel("釣魚計時", "範例擴充") }));
        var shell = new ShellViewModel(catalog, backend);
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 860 };
        window.Show();
        LayoutProbe.Settle();

        Assert.True(shell.HasExtensions);
        var entry = window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.DataContext is NavigationItem { Module.Id: "sample.fishing" });
        Click(window, entry);

        Assert.Equal("sample.fishing", shell.Selected.Module.Id);
        Assert.IsType<PendingPage>(shell.CurrentPage);
        Assert.Empty(LayoutProbe.FindTextOverflow(window));
        window.Close();
    }

    [AvaloniaFact]
    public void DuplicateModuleIdIsRejected()
    {
        var catalog = BuiltInModules.CreateCatalog(new SampleBackend());
        Assert.Throws<InvalidOperationException>(() => catalog.Register(new ModuleDescriptor("color", "重複", "重",
            Colors.Gray, ModuleOrigin.Extension, () => new PendingPage())));
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese, "未实现，")]
    [InlineData(UiLanguage.TraditionalChinese, "未實作，")]
    public void InstalledModulesListShowsPendingStatus(UiLanguage language, string prefix)
    {
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        catalog.Register(new ModuleDescriptor("ext.pending", "未实现模块（示例）", "未", Colors.Gray, ModuleOrigin.BuiltIn, () => new PendingPage(), pending: true));
        UiText.Apply(Application.Current!, language);
        var model = new ModulesPageModel(backend, catalog);
        var pending = model.Cards.Single(card => card.Id == "ext.pending");
        Assert.StartsWith(prefix, pending.State);
        // 内建模块（含乐谱库）都已实现，不带“未实现”。
        Assert.All(model.Cards.Where(card => card.Id != "ext.pending"), card => Assert.DoesNotContain("未实", card.State));
        model.Dispose();
    }

    [AvaloniaFact]
    public void ReturningToTheCombatPageReusesItWithoutWaitingForTheBackend()
    {
        var backend = new SampleBackend();
        var shell = new ShellViewModel(BuiltInModules.CreateCatalog(backend), backend);
        try
        {
            var combat = shell.BuiltIn.Single(item => item.Module.Id == "combat-observation");
            var settings = shell.ShellEntries.Single(item => item.Module.Id == "shell.settings");
            // 页面在切换后的调度帧里建立：每次切换后推进调度队列，等页面建立完成再检查。
            shell.Select(combat);
            LayoutProbe.Settle();
            var page = Assert.IsType<CombatPage>(shell.CurrentPage);
            var model = (CombatPageModel)page.DataContext!;
            var kpis = model.Kpis;
            shell.Select(settings);
            LayoutProbe.Settle();
            Assert.IsType<SettingsPage>(shell.CurrentPage);
            // 切走后停止接收后端通知；回到 DPS检测页时复用同一页面与固定集合，数据取自内存中的最近结果。
            shell.Select(combat);
            Assert.Same(page, shell.CurrentPage);
            Assert.Same(kpis, model.Kpis);
            Assert.NotEmpty(model.Kpis);
        }
        finally { shell.Dispose(); }
    }

    [AvaloniaFact]
    public void NavigationShowsTheSkeletonFirstAndReusesVisitedPages()
    {
        var backend = new SampleBackend();
        var catalog = BuiltInModules.CreateCatalog(backend);
        var created = 0;
        // 计数用的不保留页面登记为内建模块：扩展分组的入口由后端的扩展界面决定，手工登记的扩展模块会在同步时被换掉。
        catalog.Register(new ModuleDescriptor("sample.counted", "計數（範例）", "計", Color.Parse("#4B5563"), ModuleOrigin.BuiltIn,
            () => { created++; return new PendingPage { DataContext = new PendingPageModel("計數", "範例擴充") }; }));
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        UiMotion.Enabled = false;
        var shell = new ShellViewModel(catalog, backend);
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 860 };
        window.Show();
        LayoutProbe.Settle();
        try
        {
            var modules = shell.ShellEntries.Single(item => item.Module.Id == "shell.modules");
            var counted = shell.BuiltIn.Single(item => item.Module.Id == "sample.counted");
            var intents = backend.UiIntents.Count;

            // 切换返回时只显示了骨架（模块标题与占位面板），页面还没有建立。
            shell.Select(modules);
            var frame = shell.CurrentFrame!;
            Assert.True(frame.ShowsSkeleton);
            Assert.Null(shell.CurrentPage);
            Assert.Equal(0, frame.BuildCount);
            Assert.Contains(frame.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "模块与扩展");
            Assert.Same(frame, Named<PageHost>(window, "PageHost").Current);

            // 之后的调度帧里建立页面；右栏是延后建立的一块，再下一帧建立。
            LayoutProbe.Settle();
            var page = Assert.IsType<ModulesPage>(shell.CurrentPage);
            Assert.False(frame.ShowsSkeleton);
            Assert.Equal(1, frame.BuildCount);
            Assert.All(page.GetVisualDescendants().OfType<DeferredContent>(), block => Assert.True(block.IsBuilt));

            // 页面建立之前就切走：建立被取消，不白建；回到保留的页面立即显示同一实例，不再建立。
            shell.Select(counted);
            Assert.True(shell.CurrentFrame!.ShowsSkeleton);
            shell.Select(modules);
            Assert.Same(frame, shell.CurrentFrame);
            Assert.Same(page, shell.CurrentPage);
            LayoutProbe.Settle();
            Assert.Equal(0, created);
            Assert.Equal(1, frame.BuildCount);

            // 不保留的页面：外框复用，切走时释放页面，回来时先显示骨架再重建。
            shell.Select(counted);
            LayoutProbe.Settle();
            var countedFrame = shell.CurrentFrame!;
            Assert.Equal(1, created);
            shell.Select(modules);
            shell.Select(counted);
            Assert.Same(countedFrame, shell.CurrentFrame);
            Assert.True(countedFrame.ShowsSkeleton);
            LayoutProbe.Settle();
            Assert.Equal(2, created);
            // 切换页面不向后端发送任何意图。
            Assert.Equal(intents, backend.UiIntents.Count);

            // 切走的外框留在页面区的逻辑树上（回来不重新套用样式），视觉树上只有当前外框。
            var host = Named<PageHost>(window, "PageHost");
            Assert.Contains(frame, host.Kept);
            Assert.DoesNotContain(frame, host.GetVisualChildren());

            // 分块排版：页面建立的那一帧只排首屏的几块面板，其余每帧放出一块；排完后页面完整。
            shell.Select(shell.BuiltIn.Single(item => item.Module.Id == "rooms"));
            var rooms = shell.CurrentFrame!;
            rooms.EnsureBuilt();
            var roomsPanels = rooms.Page!.GetLogicalDescendants().OfType<Border>().Where(border => border.Classes.Contains("panel")).ToList();
            Assert.True(rooms.PendingPanels > 0);
            Assert.Contains(roomsPanels, panel => panel.IsAnimating(Visual.IsVisibleProperty));
            LayoutProbe.Settle();
            // 放出后撤销的是动画优先级的隐藏，面板回到页面自己的可见性（绑定不受影响）。
            Assert.Equal(0, rooms.PendingPanels);
            Assert.All(roomsPanels, panel => Assert.False(panel.IsAnimating(Visual.IsVisibleProperty)));

            // 测试用开关：切换在返回前就建立页面。
            PageFrame.BuildImmediately = true;
            shell.Select(shell.ShellEntries.Single(item => item.Module.Id == "shell.settings"));
            Assert.IsType<SettingsPage>(shell.CurrentPage);
        }
        finally
        {
            PageFrame.BuildImmediately = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CommonPagesArePrebuiltWhileIdleAndOpenWithoutRebuilding()
    {
        var backend = new SampleBackend();
        ShellViewModel.PrebuildOnIdle = true;
        Window window;
        try { window = LayoutProbe.Open(backend, 1280, 860, "shell.settings"); }
        finally { ShellViewModel.PrebuildOnIdle = false; }
        try
        {
            var shell = (ShellViewModel)window.DataContext!;
            // 空闲时建好合奏与背包页（默认页 DPS检测在启动时已建立），建好后停用，不接收后端通知。
            foreach (var id in new[] { "rooms", "inventory" })
            {
                var item = shell.BuiltIn.Single(entry => entry.Module.Id == id);
                var frame = shell.FrameFor(item.Module);
                Assert.Equal(1, frame.BuildCount);
                shell.Select(item);
                Assert.False(frame.ShowsSkeleton);
                LayoutProbe.Settle();
                Assert.Equal(1, frame.BuildCount);
                Assert.Equal(0, frame.PendingPanels);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RefreshBetweenPressAndReleaseKeepsTheClick()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.modules");
        try
        {
            UiMotion.Enabled = true;
            Pump(window, 200);
            var page = Page<ModulesPage>(window);
            var model = (ModulesPageModel)page.DataContext!;
            Button Toggle() => page.GetVisualDescendants().OfType<Button>()
                .Single(button => button.CommandParameter as string == "modules.toggle:color");
            var toggle = Toggle();
            toggle.BringIntoView();
            LayoutProbe.Render(window);
            // 按在按钮布局范围内靠右边缘处：悬停放大、按下缩小只作用在外观层，点击区不变。
            var point = toggle.TranslatePoint(new Point(toggle.Bounds.Width * 0.995, toggle.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            Pump(window, 200);
            window.MouseDown(point, MouseButton.Left);
            // 按下与松开之间后端刷新一次：卡片列表内容没变，按钮不被换掉。
            backend.RaiseChanged();
            Pump(window, 200);
            Assert.Same(toggle, Toggle());
            window.MouseUp(point, MouseButton.Left);
            LayoutProbe.Settle();
            Assert.Equal("已停用", model.Cards.Single(card => card.Id == "color").ToggleLabel);
        }
        finally
        {
            UiMotion.Enabled = false;
            window.Close();
        }
    }
}
