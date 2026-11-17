using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Diagnostics;
using MabiTool.Desktop.Pages.Dyeing;
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

/// <summary>
/// 排版清单：<see cref="States"/> 的每一行是一个需要检查排版的界面状态。每个状态在最窄与常用两种窗口尺寸、
/// 繁体中文、浅色主题下检查文字不出框；常用尺寸的同一个窗口再切到深色复查一次。
/// </summary>
public sealed class LayoutTests
{
    private enum Shot { None, Full, Narrow }

    /// <summary>一个界面状态：所在模块、样本后端、进入状态的操作、额外检查与截图。</summary>
    private sealed record LayoutState(string Name, string? Module, string Backend,
        Func<Window, IDisposable?>? Prepare = null, Action<Window>? Check = null, Shot Shot = Shot.None, bool Mini = false);

    private static readonly (double Width, double Height) Narrowest = (760, 600), Common = (1280, 860);
    private static readonly (double Width, double Height) MiniNarrowest = (340, 640), MiniCommon = (440, 920);

    // 简体文字比繁体长的界面状态（倒计时、自定义等），两种语言都检查。
    private static readonly HashSet<string> SimplifiedToo = ["rooms", "rooms.join", "settings", "settings.limit-invalid"];

    // 另在中间与最宽尺寸下各检查一轮的代表状态。
    private static readonly HashSet<string> AllSizes = ["combat", "rooms", "presentation", "settings", "mini"];

    private static SampleBackend Sample() => new();

    private static string? PlayUnavailable(string id) => id == CapabilityIds.PerformancePlay
        ? CapabilityNotice.Describe(new(id, "performance", CapabilityStatus.Unavailable, null, null, null, null,
            "module.notImplemented", DateTimeOffset.UnixEpoch, []))
        : null;

    private static TopicNotice? NearbyUnsupported(string topic) => topic == PresentationTopics.Nearby
        ? TopicNoticeText.Describe(new(topic, CapabilityStatus.Unavailable, "provider.unavailable"))
        : null;

    private static IDisposable? OpenGameLayer(Window window)
    {
        Click(window, Named<Button>(Page<MusicPage>(window), "GameLayerButton"));
        return null;
    }

    private static IDisposable? OpenGameDataLayer(Window window)
    {
        var page = Page<OverlayPage>(window);
        ((OverlayPageModel)page.DataContext!).Store.Place("performance.nearby");
        LayoutProbe.Settle();
        Click(window, Named<Button>(page, "GameDataButton"));
        return null;
    }

    // 选取版面里的第一个元件：详情显示位置、锁状态与切换、缩放和外观。
    private static IDisposable? SelectWidget(Window window)
    {
        var model = (OverlayPageModel)Page<OverlayPage>(window).DataContext!;
        model.SelectedWidgetId = model.Store.Active.Widgets[0].WidgetId;
        LayoutProbe.Settle();
        return null;
    }

    // 把全部内建浮窗元件放进当前版面，尺寸设为各自的最小尺寸或较大的尺寸；游戏数据元件的主题都可用。
    private static Func<Window, IDisposable?> PlaceAllWidgets(bool minimum) => window =>
    {
        var shell = (ShellViewModel)window.DataContext!;
        var store = shell.Catalog.Overlay!.Store;
        foreach (var widget in shell.Catalog.Modules.Where(module => module.Origin == ModuleOrigin.BuiltIn).SelectMany(module => module.Widgets))
        {
            var placement = store.Place(widget.Id);
            var size = minimum ? OverlayWidgetSizes.MinimumOf(widget) : OverlayWidgetSizes.DefaultOf(widget)!.Value * 1.5;
            (placement.Width, placement.Height) = (size.Width, size.Height);
        }
        store.Commit();
        LayoutProbe.Settle();
        return null;
    };

    /// <summary>
    /// 在 1:1 的游戏浮窗上检查每个元件的文字都留在自己的外框内；在元件内滚动的列表里，滚出可见范围的文字不算出框。
    /// </summary>
    private static void WidgetTextInsideFrames(Window window)
    {
        var shell = (ShellViewModel)window.DataContext!;
        var overlay = new MabiTool.Desktop.Overlay.OverlayWindow(shell.Catalog.Overlay!.Store, shell.Catalog, shell.Catalog.Overlay.Backend)
            { Width = 1920, Height = 1080 };
        overlay.Show();
        try
        {
            LayoutProbe.Render(overlay);
            var problems = new List<string>();
            foreach (var frame in overlay.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("overlayframe") && b.IsEffectivelyVisible))
            {
                var inner = new Rect(frame.Bounds.Size).Deflate(frame.Padding).Inflate(1);
                foreach (var text in frame.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
                {
                    var rect = new Rect(text.Bounds.Size).TransformToAABB(text.TransformToVisual(frame)!.Value);
                    if (text.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } scroller
                        && scroller.GetVisualAncestors().Contains(frame))
                    {
                        var view = new Rect(scroller.Bounds.Size).TransformToAABB(scroller.TransformToVisual(frame)!.Value);
                        if (!view.Inflate(1).Contains(rect)) continue;
                    }
                    if (!inner.Contains(rect)) problems.Add($"「{text.Text}」{rect} 超出外框 {inner}");
                }
            }
            Assert.True(problems.Count == 0, string.Join("；", problems));
        }
        finally { overlay.Close(); }
    }

    private static ColorPageModel ColorModel(Window window) => (ColorPageModel)Page<ColorPage>(window).DataContext!;

    // 选色与目标色集合浮层锚定在第一张卡上；浮层留在窗口内。
    private static IDisposable? OpenColorPicker(Window window) => Do(w => ColorModel(w).Cards[0].OpenPickerCommand.Execute(null), window);

    private static IDisposable? OpenColorAtlas(Window window) => Do(w => ColorModel(w).Cards[0].OpenAtlasCommand.Execute(null), window);

    // 相似颜色卡的集合浮层：等缩略图读回后打开，全集图随后读取。
    private static IDisposable? OpenSimilarAtlas(Window window) => Do(w =>
    {
        Pump(w, 200);
        ColorModel(w).Cards[1].OpenAtlasCommand.Execute(null);
        Pump(w, 50);
    }, window);

    // 三列并排时，精准 HEX 与相似颜色的卡片上集合与读数行的位置相同。
    private static void ColorCardsAligned(Window window)
    {
        foreach (var name in new[] { "AtlasThumb", "RegionMark" })
        {
            var points = window.GetVisualDescendants().OfType<Control>().Where(control => control.Name == name && control.IsEffectivelyVisible)
                .Select(control => control.TranslatePoint(default, window)!.Value).ToList();
            Assert.Equal(3, points.Count);
            if (points.Select(point => Math.Round(point.X)).Distinct().Count() == 3)
                Assert.Single(points.Select(point => Math.Round(point.Y, 1)).Distinct());
        }
    }

    private static IDisposable? OpenColorLayer(Window window, string name) =>
        Do(w => ClickInPlace(w, Named<Button>(w, name)), window);

    private static Action<Window> InsideWindow(string panelName) => window =>
    {
        var panel = Named<Border>(window, panelName);
        Assert.True(panel.IsEffectivelyVisible);
        var origin = panel.TranslatePoint(default, window)!.Value;
        Assert.True(origin.Y >= 0 && origin.Y + panel.Bounds.Height <= window.Bounds.Height + 1, $"{panelName} 超出窗口上下边");
        Assert.True(origin.X >= 0 && origin.X + panel.Bounds.Width <= window.Bounds.Width + 1, $"{panelName} 超出窗口左右边");
    };

    // 展开每条带文字详情的记录。
    private static IDisposable? ExpandRecordDetails(Window window)
    {
        var model = (DiagnosticsPageModel)Page<DiagnosticsPage>(window).DataContext!;
        foreach (var row in model.Records.Rows.Concat(model.Capture.LiveRows).Where(row => row.HasDetail))
            row.ToggleCommand.Execute(null);
        LayoutProbe.Settle();
        return null;
    }

    private static IDisposable? CollapseDiagnostics(Window window)
    {
        foreach (var card in new[] { "export", "capture", "runtime", "records" })
            CardStateStore.SetExpanded("diagnostics", card, false);
        LayoutProbe.Settle();
        return new Release(() => CardStateStore.Restore(null));
    }

    // 合奏页卡片全部收起。
    private static IDisposable? CollapseRooms(Window window)
    {
        foreach (var card in new[] { "connection", "local", "roster", "diagnostics", "status" })
            CardStateStore.SetExpanded("rooms", card, false);
        LayoutProbe.Settle();
        return new Release(() => CardStateStore.Restore(null));
    }

    private static IDisposable? OpenExportConfirm(Window window)
    {
        Click(window, Named<Button>(window, "ExportButton"));
        return null;
    }

    private static void ExportConfirmInsideWindow(Window window)
    {
        Assert.True(Named<Border>(window, "ConfirmPanel").IsEffectivelyVisible);
        var panel = Named<Border>(window, "ConfirmPanel");
        var origin = panel.TranslatePoint(default, window)!.Value;
        Assert.True(origin.Y >= 0 && origin.Y + panel.Bounds.Height <= window.Bounds.Height, "确认层超出窗口");
    }

    private static IDisposable? LoadPack(Window window)
    {
        var pack = new ThemeAndMotionAssetTests.TemporaryPack();
        ((ModulesPageModel)Page<ModulesPage>(window).DataContext!).LoadPack(pack.Directory);
        LayoutProbe.Settle();
        return pack;
    }

    private static IDisposable? Do(Action<Window> action, Window window)
    {
        action(window);
        LayoutProbe.Settle();
        return null;
    }

    // 背包表格按 Ctrl+F 打开检索框并输入检索词。
    private static IDisposable? OpenInventorySearch(Window window)
    {
        var table = Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "ItemsTable");
        table.OpenSearch();
        table.SearchText = "琴";
        LayoutProbe.Settle();
        return null;
    }

    // 背包窗口与主窗口同尺寸打开，检查完关闭。
    private static IDisposable? OpenInventoryWindow(Window window)
    {
        Click(window, Named<Button>(window, "OpenWindowButton"));
        var opened = MabiTool.Desktop.Pages.Inventory.InventoryTableWindow.Current!;
        opened.Width = window.Width;
        opened.Height = window.Height;
        LayoutProbe.Settle();
        return new Release(opened.Close);
    }

    private static void InventoryWindowFits(Window window)
    {
        var opened = MabiTool.Desktop.Pages.Inventory.InventoryTableWindow.Current!;
        LayoutProbe.Render(opened);
        Assert.Empty(LayoutProbe.FindTextOverflow(opened));
        Assert.NotEmpty(Named<MabiTool.Desktop.Controls.Tables.DataTable>(opened, "ItemsTable").View);
        var model = (MabiTool.Desktop.Pages.Inventory.InventoryPageModel)opened.DataContext!;
        var name = model.Data.AccountId == "account-long" ? "inventory-window.long" : "inventory-window";
        LayoutProbe.SaveScreenshot(opened, $"{name}-{opened.Width:0}x{opened.Height:0}-{(window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "dark" : "light")}");
    }

    // 横向滚到最右，攻击特性的元素列表头整列落在表格可见范围内。
    private static void TraitColumnsReachable(Window window)
    {
        var data = Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "DamageTable");
        Assert.Contains(data.VisibleColumns, column => column.Id == "elements");
        var table = data.List.GetVisualDescendants().OfType<ScrollViewer>().First();
        data.BringIntoView();
        LayoutProbe.Render(window);
        table.Offset = new Vector(table.Extent.Width, 0);
        LayoutProbe.Render(window);
        var cell = data.HeaderCells.Single(header => header.Column.Id == "elements").Title;
        var rect = new Rect(cell.Bounds.Size).TransformToAABB(cell.TransformToVisual(table)!.Value);
        Assert.True(rect.X >= -1 && rect.Right <= table.Viewport.Width + 1, $"元素列 {rect} 不在表格可见宽度 {table.Viewport.Width} 内");
    }

    // 目标标签：折叠时只占一行，“全部”、选中的目标与“+N”在可见范围内；展开时全部可见、多于一行。
    private static void TargetRow(Window window, bool expanded)
    {
        var row = Named<ItemsControl>(window, "RangeTargetChips").ItemsPanelRoot as MabiTool.Desktop.Controls.OverflowRow
            ?? throw new InvalidOperationException("目标标签不是 OverflowRow");
        var chips = row.Children.ToList();
        var height = chips.Max(chip => chip.DesiredSize.Height);
        bool Inside(Control chip) => chip.Bounds.X >= 0 && chip.Bounds.Right <= row.Bounds.Width + 0.5;
        var selected = chips.Single(chip => chip.DataContext is RangeTargetChip { Current: true });
        var toggle = chips[^1];
        Assert.True(Inside(selected), "选中的目标不在可见范围内");
        Assert.True(Inside(chips[0]), "“全部”不在可见范围内");
        Assert.True(Inside(toggle), "展开项不在可见范围内");
        if (expanded)
        {
            Assert.True(chips.All(Inside), "展开后仍有目标不可见");
            Assert.True(row.Bounds.Height > height * 1.5, $"展开后只有一行（{row.Bounds.Height}）");
        }
        else
        {
            Assert.True(row.Bounds.Height <= height + 0.5, $"折叠时高度 {row.Bounds.Height} 超过一行 {height}");
            Assert.Contains(chips, chip => !Inside(chip));
            Assert.True(MabiTool.Desktop.Controls.OverflowRow.GetOverflowCount(toggle) > 0);
        }
    }

    private static readonly LayoutState[] Rows =
    [
        // DPS检测
        new("combat", "combat-observation", "sample", Check: TraitColumnsReachable, Shot: Shot.Full),
        new("combat.traits-unknown", "combat-observation", "traits-unknown",
            Check: window =>
            {
                Assert.DoesNotContain(Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "DamageTable").VisibleColumns,
                    column => column.Id == "elements");
                Assert.DoesNotContain("无防备", VisibleTexts(Named<ItemsControl>(window, "RateGrid")));
                Assert.False(Named<StackPanel>(window, "FirstAttackGroup").IsEffectivelyVisible);
            }),
        new("combat.update-badge", "combat-observation",
            "update-badge",
            Check: window => Assert.True(Named<Button>(window, "UpdateBadge").IsEffectivelyVisible)),
        new("combat.long", "combat-observation", "long", Check: TraitColumnsReachable, Shot: Shot.Narrow),
        new("combat.healing", "combat-observation", "sample",
            Check: window =>
            {
                Assert.True(Named<ItemsControl>(window, "HealingTiles").IsEffectivelyVisible);
                Assert.False(Named<TextBlock>(window, "NoHealingText").IsEffectivelyVisible);
                Assert.True(Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "HealingTable").IsEffectivelyVisible);
            }),
        new("combat.healing-unavailable", "combat-observation", "no-healing",
            Check: window =>
            {
                Assert.True(Named<TextBlock>(window, "NoHealingText").IsEffectivelyVisible);
                Assert.False(Named<ItemsControl>(window, "HealingTiles").IsEffectivelyVisible);
            }),
        // 工具栏的定时测量各阶段、目标选中、定时测量范围与三个弹出层。
        new("combat.measure-countdown", "combat-observation", "measure-countdown",
            Check: window => Assert.True(Named<Border>(window, "TimerWaiting").IsEffectivelyVisible)),
        new("combat.measure-running", "combat-observation", "measure-running",
            Check: window =>
            {
                Assert.True(Named<Border>(window, "TimerRunning").IsEffectivelyVisible);
                Assert.True(Named<Border>(window, "TargetDetailCard").IsEffectivelyVisible);
            }, Shot: Shot.Narrow),
        new("combat.measure-unavailable", "combat-observation", "measure-unavailable",
            Check: window =>
            {
                Assert.True(Named<TextBlock>(window, "MeasureHintText").IsEffectivelyVisible);
                Assert.False(Named<Button>(window, "StartTimerButton").IsEffectivelyEnabled);
                Assert.True(Named<Border>(window, "AutoBanner").IsEffectivelyVisible);
            }),
        new("combat.measure-scope", "combat-observation", "measure-scope",
            Check: window =>
            {
                Assert.True(Named<Border>(window, "IncompleteBanner").IsEffectivelyVisible);
                Assert.True(Named<ItemsControl>(window, "MeasurementOptions").IsEffectivelyVisible);
                Assert.True(Named<Button>(window, "BackToLiveButton").IsEffectivelyVisible);
            }, Shot: Shot.Full),
        new("combat.timer-layer", "combat-observation", "sample", window => CombatLayer(window, model => model.OpenTimerCommand),
            Check: window => Assert.True(Named<Border>(window, "TimerPanel").IsEffectivelyVisible)),
        new("combat.save-layer", "combat-observation", "sample", window => CombatLayer(window, model => model.OpenSaveCommand),
            Check: window => Assert.True(Named<TextBox>(window, "RecordNameBox").IsEffectivelyVisible)),
        new("combat.reset-layer", "combat-observation", "sample", window => CombatLayer(window, model => model.OpenResetCommand),
            Check: window => Assert.True(Named<Button>(window, "ResetConfirm").IsEffectivelyVisible)),
        new("combat.timer-layer.long", "combat-observation", "long", window => CombatLayer(window, model => model.OpenTimerCommand)),
        // 目标很多：默认折叠成一行，选中的目标与“+N”可见；展开后换行显示全部，最后是“收起”。
        new("combat.targets-collapsed", "combat-observation", "many-targets", Check: window => TargetRow(window, expanded: false), Shot: Shot.Narrow),
        new("combat.targets-expanded", "combat-observation", "many-targets",
            window => CombatLayer(window, model => model.ToggleTargetsCommand),
            Check: window => TargetRow(window, expanded: true)),
        // 未抓包：原因只在侧栏左上角写明，DPS检测页不重复显示。
        new("combat.capture-off", "combat-observation", "capture-failed",
            Check: window => Assert.True(Named<TextBlock>(window, "CaptureLineText").IsEffectivelyVisible)),

        // 乐谱库与迷你窗
        new("music", "music-library", "sample", Shot: Shot.Full),
        new("music.game-layer", "music-library", "play-unavailable", OpenGameLayer,
            Shot: Shot.Full),
        new("music.long", "music-library", "long"),
        new("music.game-layer.long", "music-library", "long", OpenGameLayer, Shot: Shot.Narrow),
        new("mini", null, "sample", window => Do(w => ((MiniWindowModel)w.DataContext!).SelectCommand.Execute("s2"), window),
            Shot: Shot.Full, Mini: true),
        new("mini.notice", null, "play-unavailable",
            window => Do(w => ((MiniWindowModel)w.DataContext!).SelectCommand.Execute("s2"), window),
            Check: window => Assert.True(Named<TextBlock>(window, "CapabilityNotice").IsEffectivelyVisible), Mini: true),
        new("mini.long", null, "long",
            window => Do(w => ((MiniWindowModel)w.DataContext!).SelectCommand.Execute("s1"), window), Mini: true),

        // 合奏
        new("rooms", "rooms", "sample", Shot: Shot.Full),
        new("rooms.member", "rooms", "member"),
        new("rooms.interrupted", "rooms", "interrupted", Shot: Shot.Full),
        new("rooms.committed", "rooms", "committed",
            Check: window => Assert.True(Named<DockPanel>(window, "CountdownPanel").IsEffectivelyVisible)),
        new("rooms.emote", "rooms", "sample",
            window => Do(w => ((RoomsPageModel)Page<RoomsPage>(w).DataContext!).ModeCommand.Execute("emote"), window)),
        new("rooms.recording", "rooms", "sample",
            window => Do(w => ((RoomsPageModel)Page<RoomsPage>(w).DataContext!).RecordCommand.Execute("stop"), window)),
        new("rooms.fallback-invite", "rooms", "clipboard-fails",
            window => Do(w => ((RoomsPageModel)Page<RoomsPage>(w).DataContext!).IntentCommand.Execute("rooms.copy-invite"), window),
            Check: window => Assert.True(Named<TextBox>(window, "FallbackInvitationBox").IsEffectivelyVisible)),
        new("rooms.join", "rooms", "not-in-room", Shot: Shot.Full),
        new("rooms.kicked", "rooms", "kicked", Check: window => Assert.True(Named<Border>(window, "ExitNotice").IsEffectivelyVisible)),
        new("rooms.lan-guide", "rooms", "not-in-room",
            window => Do(w => ((RoomsPageModel)Page<RoomsPage>(w).DataContext!).ToggleCommand.Execute("lan-guide"), window)),
        new("rooms.advanced", "rooms", "not-in-room",
            window => Do(w => ((RoomsPageModel)Page<RoomsPage>(w).DataContext!).ToggleCommand.Execute("advanced"), window)),
        // 本机动作里只读显示识别到的本机与当前乐谱；左上角抓包状态：已识别角色、未识别（提示切图）、未抓包原因。
        new("rooms.capture", "rooms", "capture-running",
            Check: window =>
            {
                Assert.True(Named<TextBlock>(window, "ObservedScoreText").IsEffectivelyVisible);
                Assert.True(Named<TextBlock>(window, "CaptureLineText").IsEffectivelyVisible);
            }, Shot: Shot.Full),
        new("rooms.capture-off", "rooms", "capture-failed",
            Check: window => Assert.True(Named<Button>(window, "CaptureChip").IsEffectivelyVisible)),
        new("shell.capture-unidentified", "rooms", "capture-unidentified",
            Check: window => Assert.True(Named<TextBlock>(window, "CaptureLineText").IsEffectivelyVisible)),
        new("rooms.collapsed", "rooms", "sample", CollapseRooms),
        new("rooms.long", "rooms", "long", Shot: Shot.Narrow),
        new("rooms.advanced.long", "rooms", "long",
            window => Do(w => ((RoomsPageModel)Page<RoomsPage>(w).DataContext!).ToggleCommand.Execute("advanced"), window)),

        // 染色
        new("color", "color", "sample", Shot: Shot.Full),
        new("color.running", "color", "color-running", Shot: Shot.Full),
        new("color.picker", "color", "sample", OpenColorPicker, InsideWindow("PickerPanel"), Shot.Full),
        new("color.atlas", "color", "sample", OpenColorAtlas, InsideWindow("AtlasPanel"), Shot.Narrow),
        new("color.atlas.similar", "color", "sample", OpenSimilarAtlas, InsideWindow("AtlasPanel"), Shot.Narrow),
        new("color.cards", "color", "sample", Check: ColorCardsAligned),
        new("color.low-time", "color", "color-low-time", Shot: Shot.Narrow),
        new("color.presets", "color", "sample", window => OpenColorLayer(window, "PresetsButton"), Shot: Shot.Narrow),
        new("color.history", "color", "sample", window => OpenColorLayer(window, "RecordsButton"), Shot: Shot.Narrow),
        new("color.long", "color", "long", Shot: Shot.Narrow),
        new("color.presets.long", "color", "long", window => OpenColorLayer(window, "PresetsButton")),
        new("color.history.long", "color", "long", window => OpenColorLayer(window, "RecordsButton")),

        // 背包
        new("inventory", "inventory", "sample", Shot: Shot.Full),
        new("inventory.long", "inventory", "long"),
        new("inventory.search", "inventory", "sample", OpenInventorySearch,
            Check: window => Assert.NotEmpty(Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "ItemsTable").MatchIndices), Shot: Shot.Narrow),
        new("inventory.window", "inventory", "sample", OpenInventoryWindow, InventoryWindowFits, Shot.Narrow),
        new("inventory.window.long", "inventory", "long", OpenInventoryWindow, InventoryWindowFits),

        // 浮窗版面
        new("presentation", "presentation", "sample",
            // 屏幕卡片写明当前有哪几块显示器（浮窗按显示器分窗，元件在实际屏幕上编辑，页面里不再画画布）。
            Check: window =>
            {
                var caption = Named<TextBlock>(window, "MonitorsText");
                Assert.True(caption.IsEffectivelyVisible);
                Assert.Contains("示器", caption.Text);
            }, Shot: Shot.Full),
        new("presentation.game-data", "presentation", "nearby-unsupported",
            OpenGameDataLayer, Shot: Shot.Full),
        new("presentation.selected", "presentation", "sample", SelectWidget),
        new("presentation.long", "presentation", "long"),
        new("presentation.selected.long", "presentation", "long", SelectWidget),
        new("presentation.game-data.long", "presentation", "long", OpenGameDataLayer, Shot: Shot.Narrow),
        // 浮窗元件在最小尺寸与较大尺寸下的排版（合奏信息浮窗分房主与成员两种按钮）。
        new("presentation.widgets-min", "presentation", "sample", PlaceAllWidgets(true), WidgetTextInsideFrames),
        new("presentation.widgets-large", "presentation", "sample", PlaceAllWidgets(false), WidgetTextInsideFrames),
        new("presentation.widgets-min.member", "presentation", "member", PlaceAllWidgets(true), WidgetTextInsideFrames),
        new("presentation.widgets-min.long", "presentation", "long", PlaceAllWidgets(true), WidgetTextInsideFrames),

        // 模块与扩展
        new("modules", "shell.modules", "sample", Shot: Shot.Full),
        new("modules.pack", "shell.modules", "sample", LoadPack),
        new("modules.long", "shell.modules", "long"),
        new("modules.pack.long", "shell.modules", "long", LoadPack),

        // 设置
        new("settings", "shell.settings", "sample", Shot: Shot.Full),
        new("settings.recording", "shell.settings", "sample",
            window => Do(w => ((SettingsPageModel)Page<SettingsPage>(w).DataContext!).RecordCommand.Execute("emergency-stop"), window)),
        // 越界输入：输入框保留超长数字，下方显示范围说明，输入框留在更新面板内。
        new("settings.limit-invalid", "shell.settings", "sample",
            window => Do(w => SettingsUpdatePolicyTests.TypeAndEnter(w, "123456789012345678901234567890"), window),
            Check: window =>
            {
                Assert.True(Named<TextBlock>(window, "LimitRangeNote").IsEffectivelyVisible);
                var box = Named<TextBox>(window, "LimitBox");
                var panel = Named<Card>(window, "UpdatePanel");
                var rect = new Rect(box.Bounds.Size).TransformToAABB(box.TransformToVisual(panel)!.Value);
                Assert.True(new Rect(panel.Bounds.Size).Contains(rect), $"输入框超出面板：{rect}");
            }),
        new("settings.offline", "shell.settings", "offline",
            Check: window => Assert.True(Named<TextBlock>(window, "PolicyUnavailableNote").IsEffectivelyVisible)),
        new("settings.long", "shell.settings", "long", Shot: Shot.Narrow),
        // 抓包：列出网卡、网卡检测进行中、未抓包的原因。
        new("settings.capture-channels", "shell.settings", "sample",
            window => Do(w => ((SettingsPageModel)Page<SettingsPage>(w).DataContext!).ListChannelsCommand.Execute(null), window),
            Check: window => Assert.True(Named<ItemsControl>(window, "CaptureChannelList").IsEffectivelyVisible)),
        new("settings.capture-channels-collapsed", "shell.settings", "sample",
            window => Do(w =>
            {
                var model = (SettingsPageModel)Page<SettingsPage>(w).DataContext!;
                model.ListChannelsCommand.Execute(null); model.ListChannelsCommand.Execute(null);
            }, window),
            Check: window => Assert.False(Named<ItemsControl>(window, "CaptureChannelList").IsEffectivelyVisible)),
        new("settings.capture-confirmed", "shell.settings", "sample",
            window => Do(w =>
            {
                var model = (SettingsPageModel)Page<SettingsPage>(w).DataContext!;
                model.StartDetectionCommand.Execute(null); model.ConfirmDetectionCommand.Execute(null);
            }, window),
            Check: window => Assert.True(Named<TextBlock>(window, "DetectionMessageText").IsEffectivelyVisible), Shot: Shot.Narrow),
        new("settings.capture-detecting", "shell.settings", "capture-detecting",
            Check: window => Assert.True(Named<ItemsControl>(window, "DetectionList").IsEffectivelyVisible), Shot: Shot.Narrow),
        new("settings.capture-off", "shell.settings", "capture-failed",
            Check: window => Assert.True(Named<TextBlock>(window, "CaptureStatusText").IsEffectivelyVisible)),
        new("settings.recording.long", "shell.settings", "long",
            window => Do(w => ((SettingsPageModel)Page<SettingsPage>(w).DataContext!).RecordCommand.Execute("emergency-stop"), window)),

        // 诊断
        new("diagnostics", "shell.diagnostics", "sample", Shot: Shot.Full),
        new("diagnostics.detail", "shell.diagnostics", "sample", ExpandRecordDetails, Shot: Shot.Full),
        new("diagnostics.capture", "shell.diagnostics", "capture-active", ExpandRecordDetails, Shot: Shot.Full),
        new("diagnostics.confirm", "shell.diagnostics", "capture-detail", OpenExportConfirm, ExportConfirmInsideWindow, Shot.Full),
        new("diagnostics.offline", "shell.diagnostics", "offline"),
        new("diagnostics.long", "shell.diagnostics", "long", ExpandRecordDetails, Shot: Shot.Narrow),
        new("diagnostics.confirm.long", "shell.diagnostics", "long", OpenExportConfirm, ExportConfirmInsideWindow),
        // 四块全部收起：只剩标题行。
        new("diagnostics.collapsed", "shell.diagnostics", "sample", CollapseDiagnostics),

        // 外壳：选择游戏窗口弹出层、侧栏
        new("shell.game-picker", null, "sample", window => Do(OpenPicker, window), PickerInsideWindow, Shot.Full),
        new("shell.game-picker.empty", null, "no-windows", window => Do(OpenPicker, window),
            PickerInsideWindow),
        new("shell.game-picker.long", null, "long", window => Do(OpenPicker, window), PickerInsideWindow),
        new("shell.sidebar-expanded", null, "sample", window =>
        {
            Click(window, Named<Avalonia.Controls.Primitives.ToggleButton>(window, "PendingToggle"));
            Assert.True(Named<ItemsControl>(window, "PendingList").IsEffectivelyVisible);
            return null;
        }, Shot: Shot.Full),
        // 拖动排序中：拽起放大的入口与让位的入口。
        new("shell.sidebar-dragging", "combat-observation", "sample", window =>
        {
            var start = Center(Nav(window, "color"), window);
            ShellSidebarTests.DragTo(window, start, new Point(start.X, start.Y - 30), release: false);
            // 检查完松开，不把按下状态留给后面的测试。
            return new Release(() =>
            {
                window.MouseUp(start, MouseButton.Left);
                LayoutProbe.Settle();
            });
        })
    ];

    private static void PickerInsideWindow(Window window)
    {
        Assert.True(Picker(window).IsOpen);
        var panel = Named<Border>(window, "GamePickerPanel");
        var origin = panel.TranslatePoint(default, window)!.Value;
        Assert.True(origin.Y + panel.Bounds.Height <= window.Bounds.Height, "弹出层超出窗口底部");
    }

    private static IDisposable? CombatLayer(Window window, Func<CombatPageModel, System.Windows.Input.ICommand> command)
    {
        command((CombatPageModel)window.GetVisualDescendants().OfType<CombatPage>().Single().DataContext!).Execute(null);
        LayoutProbe.Settle();
        return null;
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private static IDesktopBackend CreateBackend(string variant) => variant switch
    {
        "sample" => new SampleBackend(),
        "long" => new LongTextBackend(),
        "traits-unknown" => new SampleBackend { UnknownAttackTraits = true },
        "measure-countdown" => new SampleBackend { SampleMeasurementPhase = MeasurementPhase.Waiting },
        "measure-running" => new SampleBackend { SampleMeasurementPhase = MeasurementPhase.Running, SampleRangeTarget = "boss" },
        "measure-unavailable" => new SampleBackend { MeasurementUnavailable = true,
            SampleAutoText = "未抓包：多张网卡有游戏连接，请在设置中指定" },
        "measure-scope" => new SampleBackend { SampleScope = CombatScopeKind.Measurement, IncompleteRange = true, SampleRangeTarget = "archer-2" },
        "many-targets" => new SampleBackend { ExtraRangeTargets = 24, SampleRangeTarget = "mob-20" },
        "no-healing" => new SampleBackend { NoHealingData = true },
        "update-badge" => new SampleBackend { AvailableRelease = "2026.11.13-preview-build-with-a-very-long-identifier" },
        "play-unavailable" => new SampleBackend { CapabilityNotices = PlayUnavailable },
        "not-in-room" => new SampleBackend { InRoom = false },
        "kicked" => new SampleBackend { InRoom = false, ExitReason = "kicked" },
        "member" => new SampleBackend { IsRoomOwner = false },
        "interrupted" => new SampleBackend { RoundPhase = "interrupted" },
        "committed" => new SampleBackend { RoundPhase = "committed" },
        "capture-running" => new SampleBackend { CaptureState = "running" },
        "capture-unidentified" => new SampleBackend { CaptureState = "running", CaptureCharacter = null },
        "capture-failed" => new SampleBackend { CaptureState = "faulted", CaptureReason = "observation.channelAmbiguous" },
        "capture-detecting" => new SampleBackend { Detecting = true },
        "clipboard-fails" => new SampleBackend { ClipboardFails = true },
        "nearby-unsupported" => new SampleBackend { PresentationNotices = NearbyUnsupported },
        "offline" => new SampleBackend { Offline = true },
        "capture-active" => new SampleBackend
        {
            SampleCapture = new("capture-running", "0123456789abcdef0123456789abcdef", ["combat-observation", "game-access"], "trace", true,
                "active", null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(4), null, 342, 5, 12, 20480, 3)
        },
        "capture-detail" => new SampleBackend { SampleCapture = new("capture-detail", "0123456789abcdef0123456789abcdef", ["rooms"], "debug", true,
            "ended", "expired", DateTimeOffset.UtcNow.AddMinutes(-9), DateTimeOffset.UtcNow.AddMinutes(-8), DateTimeOffset.UtcNow.AddMinutes(-8), 120, 0, 0, 4096, 0) },
        "no-windows" => new SampleBackend { NoGameWindows = true },
        "color-running" => new SampleBackend { ColorRunning = true },
        "color-low-time" => new SampleBackend { ColorRunning = true, ColorRemainingMs = 8_000 },
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
    };

    // 清单按页面分组执行：一组是名称第一段相同的状态（combat、rooms……）。
    private static string Family(LayoutState state) => state.Name.Split('.')[0];

    private static IEnumerable<(double Width, double Height)> SizesOf(LayoutState state)
    {
        var sizes = state.Mini ? new[] { MiniNarrowest, MiniCommon } : [Narrowest, Common];
        if (AllSizes.Contains(state.Name)) sizes = state.Mini ? [.. sizes, (600, 720)] : [.. sizes, (900, 700), (1680, 1000)];
        return sizes;
    }

    private static bool Applies(LayoutState state, double width, double height, UiLanguage language) =>
        SizesOf(state).Contains((width, height)) && (language == UiLanguage.TraditionalChinese
            || SimplifiedToo.Contains(state.Name) && (width, height) is (760, 600) or (1280, 860));

    public static TheoryData<string, double, double, UiLanguage> Cases
    {
        get
        {
            var data = new TheoryData<string, double, double, UiLanguage>();
            foreach (var family in Rows.GroupBy(Family))
                foreach (var size in family.SelectMany(SizesOf).Distinct())
                    foreach (var language in new[] { UiLanguage.TraditionalChinese, UiLanguage.SimplifiedChinese })
                        if (family.Any(state => Applies(state, size.Width, size.Height, language)))
                            data.Add(family.Key, size.Width, size.Height, language);
            return data;
        }
    }

    /// <summary>
    /// 依次检查一组里适用于这个尺寸与语言的每个状态，失败的状态逐个列出。
    /// 同一后端样本的相邻页面状态共用一个主窗口：每个状态重新选中模块，页面与它打开的层、编辑器都是新建的，
    /// 样本后端只记录意图、不因这些操作改变数据；外壳状态（弹出层、侧栏）与迷你窗各开一个窗口。
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void PageStatesKeepTextInsideTheirContainers(string family, double width, double height, UiLanguage language)
    {
        var failures = new List<string>();
        Window? window = null;
        string? openVariant = null;
        try
        {
            foreach (var state in Rows.Where(row => Family(row) == family && Applies(row, width, height, language)))
            {
                var shared = !state.Mini && family != "shell";
                if (window is not null && (!shared || openVariant != state.Backend))
                {
                    window.Close();
                    window = null;
                }
                IDisposable? cleanup = null;
                try
                {
                    SidebarPreference.Restore(false);
                    if (window is null)
                    {
                        var backend = CreateBackend(state.Backend);
                        window = state.Mini
                            ? OpenMini(backend, width, height, UiThemeChoice.Light, language)
                            : LayoutProbe.Open(backend, width, height, state.Module, language);
                        openVariant = shared ? state.Backend : null;
                    }
                    else LayoutProbe.Select(window, state.Module!);
                    cleanup = state.Prepare?.Invoke(window);
                    Check(window, state, width, height, language);
                }
                catch (Exception error)
                {
                    failures.Add($"{state.Name}：{error.Message}");
                    window?.Close();
                    window = null;
                }
                finally
                {
                    cleanup?.Dispose();
                    UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
                    LayoutProbe.Settle();
                    if (!shared)
                    {
                        window?.Close();
                        window = null;
                    }
                }
            }
        }
        finally
        {
            window?.Close();
            ResetAppearance();
            SidebarPreference.Restore(false);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void Check(Window window, LayoutState state, double width, double height, UiLanguage language)
    {
        var common = (width, height) == Common || (width, height) == MiniCommon;
        var narrowest = (width, height) == Narrowest || (width, height) == MiniNarrowest;
        var suffix = $"{width}x{height}" + (language == UiLanguage.SimplifiedChinese ? "-zh-Hans" : string.Empty);
        Verify(window, state, "浅色");
        if (state.Shot != Shot.None && narrowest || state.Shot == Shot.Full && common)
            LayoutProbe.SaveScreenshot(window, $"{state.Name}-{suffix}-light");
        if (!common) return;

        // 同一个窗口切到深色再查一次：换主题不重建页面，打开的层与输入都保留。
        UiTheme.Apply(Application.Current!, UiThemeChoice.Dark);
        LayoutProbe.Settle();
        Verify(window, state, "深色");
        if (state.Shot == Shot.Full) LayoutProbe.SaveScreenshot(window, $"{state.Name}-{suffix}-dark");
    }

    private static void Verify(Window window, LayoutState state, string theme)
    {
        var problems = LayoutProbe.FindTextOverflow(window);
        Assert.True(problems.Count == 0, $"{state.Name}（{theme}）：{string.Join("；", problems)}");
        state.Check?.Invoke(window);
        if (state.Mini) return;

        // 页面标题实际画出的文字不与右上角按钮组重叠。
        var buttons = Named<StackPanel>(window, "WindowButtons");
        var area = new Rect(buttons.TranslatePoint(default, window)!.Value, buttons.Bounds.Size);
        var title = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("h1") && text.IsEffectivelyVisible);
        var rendered = new Rect(title.TranslatePoint(default, window)!.Value,
            new Size(Math.Min(title.Bounds.Width, title.TextLayout.WidthIncludingTrailingWhitespace), title.Bounds.Height));
        Assert.False(rendered.Intersects(area), $"{state.Name}（{theme}）：标题 {rendered} 与按钮组 {area} 重叠");
    }

    /// <summary>每个登记的页面（内建、外壳入口、未实现分组）都至少有一行排版清单。</summary>
    [AvaloniaFact]
    public void ChecklistCoversEveryRegisteredPage()
    {
        var catalog = BuiltInModules.CreateCatalog(new SampleBackend());
        var listed = Rows.Where(row => row.Module is not null).Select(row => row.Module).ToHashSet();
        Assert.All(catalog.Modules.Select(module => module.Id).Concat(["shell.modules", "shell.settings"]),
            id => Assert.True(listed.Contains(id), $"排版清单里没有 {id}"));
    }

    [AvaloniaFact]
    public void PairRowStacksValueUnderLabelWhenTheyDoNotFit()
    {
        PairRow Row(double width)
        {
            var row = new PairRow
            {
                Children =
                {
                    new TextBlock { Text = "个人提前或延后" }, new TextBlock { Text = "提前 40 ms，已按上次合奏结果校正" }
                }
            };
            row.Measure(new Size(width, double.PositiveInfinity));
            row.Arrange(new Rect(row.DesiredSize));
            return row;
        }

        var wide = Row(600);
        Assert.Equal(wide.Children[0].Bounds.Y, wide.Children[1].Bounds.Y);
        Assert.True(wide.Children[1].Bounds.Right > 590);

        var narrow = Row(160);
        Assert.True(narrow.Children[1].Bounds.Y > narrow.Children[0].Bounds.Y);
        Assert.Equal(0, narrow.Children[1].Bounds.X);
    }

    [AvaloniaFact]
    public void SplitPanelPlacesSideBesideMainOnlyWhenWideEnough()
    {
        SplitPanel Panel(double width)
        {
            var panel = new SplitPanel
            {
                SideWidth = 300, Threshold = 800,
                Children = { new Border { MinHeight = 200 }, new Border { MinHeight = 120 } }
            };
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            return panel;
        }

        var wide = Panel(1000);
        Assert.Equal(300, wide.Children[1].Bounds.Width);
        Assert.Equal(0, wide.Children[1].Bounds.Y);
        Assert.Equal(1000 - 300 - 14, wide.Children[0].Bounds.Width);

        var narrow = Panel(600);
        Assert.Equal(600, narrow.Children[1].Bounds.Width);
        Assert.Equal(214, narrow.Children[1].Bounds.Y);
    }

    // 背包、模块与扩展、设置三页滚到最底时可见区里没有按钮，不在此列。
    public static TheoryData<string> BottomActionPages => new()
    {
        "music-library", "rooms", "color", "combat-observation"
    };

    /// <summary>页面滚到最底时，滚动区里看得见的按钮都能直接点到，没有被活动胶囊或其他元素盖住。</summary>
    [AvaloniaTheory]
    [MemberData(nameof(BottomActionPages))]
    public void ButtonsAtTheBottomOfAPageAreNotCovered(string module)
    {
        var window = LayoutProbe.Open(new SampleBackend(), 760, 600, module);
        try
        {
            var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PageScroller");
            scroller.Offset = new Vector(0, 100000);
            LayoutProbe.Render(window);
            var viewport = new Rect(scroller.TranslatePoint(default, window)!.Value, scroller.Bounds.Size);
            var checkedButtons = 0;
            foreach (var button in scroller.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled))
            {
                var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                // 只查看得见的按钮：中心在页面滚动区内，也没有被表格的横向滚动区裁掉。
                if (!viewport.Contains(center) || button.GetTransformedBounds() is not { } shown || !shown.Clip.Contains(center)) continue;
                var hit = window.InputHitTest(center) as Visual;
                Assert.True(hit is not null && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button)),
                    $"{module}：按钮 {button.Content} 的中心 {center} 处是 {(hit as Control)?.GetType().Name}#{(hit as Control)?.Name}");
                checkedButtons++;
            }
            Assert.True(checkedButtons > 0, $"{module}：滚到底后滚动区 {viewport} 内没有看得见的按钮");
        }
        finally { window.Close(); }
    }
}
