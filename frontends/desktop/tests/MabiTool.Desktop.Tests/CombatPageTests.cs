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

/// <summary>DPS检测页：操作意图、明细切换、分段记录、攻击特性的显示与刷新后就地更新。</summary>
public sealed class CombatPageTests
{
    [AvaloniaFact]
    public void CombatButtonsSendIntentsAndFollowBackendAvailability()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        var page = window.GetVisualDescendants().OfType<CombatPage>().Single();
        Button Named(string name) => page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

        // 自动观测开启时由后端保持观测，开始与停止都不显示；关闭自动观测后改为手动停止。
        Assert.False(Named("StartButton").IsVisible);
        Assert.False(Named("StopButton").IsVisible);
        Click(window, Named("AutoButton"));
        Assert.Contains(("combat.auto", "off"), backend.UiIntents);
        LayoutProbe.Render(window);
        Click(window, Named("StopButton"));
        Click(window, Named("EndButton"));
        Click(window, Named("SplitButton"));
        Assert.Equal([(CombatIntent.StopObservation, null), (CombatIntent.EndEncounter, null), (CombatIntent.SplitEncounter, null)],
            backend.Intents);

        var archer = page.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is TargetChip { Id: "archer" });
        // 先让怪物效果面板整体进入视野，目标按钮不落在底部活动胶囊的下面。
        page.GetVisualDescendants().OfType<Card>().Single(b => b.Name == "TargetEffectsPanel").BringIntoView();
        Click(window, archer);
        var chips = ((CombatPageModel)page.DataContext!).Data.TargetChips;
        Assert.True(chips.Single(chip => chip.Id == "archer").Current);
        Assert.False(chips.Single(chip => chip.Id == "boss").Current);

        // 先让战斗记录卡片整体进入视野，标题行右侧的按钮不落在底部活动胶囊的下面。
        page.GetVisualDescendants().OfType<Card>().Single(b => b.Name == "HistoryPanel").BringIntoView();
        Click(window, Named("FavoritesOnlyButton"));
        var model = (CombatPageModel)page.DataContext!;
        Assert.Equal(["e1"], model.History.Select(row => row.Id));
        // 每条记录有“明细”与“收藏”两个按钮，按命令取收藏按钮。
        var favorite = page.GetVisualDescendants().OfType<Button>()
            .Single(b => b.DataContext is EncounterRow { Id: "e1" } && b.Command == model.FavoriteCommand);
        Click(window, favorite);
        Assert.Empty(model.History);
        Assert.Empty(LayoutProbe.FindTextOverflow(window));
        window.Close();
    }

    [AvaloniaFact]
    public void BreakdownSwitchesBetweenSkillsAndTargets()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        var page = window.GetVisualDescendants().OfType<CombatPage>().Single();
        var model = (CombatPageModel)page.DataContext!;
        Assert.Same(model.Data.Skills, model.Rows);

        var byTarget = page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ByTargetButton");
        Click(window, byTarget);

        Assert.Same(model.Data.Targets, model.Rows);
        Assert.Equal("目标", model.NameColumn);
        Assert.Empty(LayoutProbe.FindTextOverflow(window));
        window.Close();
    }

    [AvaloniaFact]
    public void MainNumbersAndAttackTraitsFollowBackendRefreshes()
    {
        // 页面建立后，主数据与攻击特性要随后续刷新更新：这些列表是页面模型持有的固定集合，刷新只改其内容。
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        try
        {
            string[] Texts(string name) => window.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == name)
                .GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();
            Assert.Contains("18,420", Texts("KpiTiles"));
            Assert.NotEmpty(Texts("RateGrid"));
            backend.SampleDps = "20,000";
            backend.RaiseChanged();
            LayoutProbe.Render(window);
            var model = (CombatPageModel)window.GetVisualDescendants().OfType<CombatPage>().Single().DataContext!;
            Assert.Equal("20,000", model.Kpis[0].Value);
            Assert.Contains("20,000", Texts("KpiTiles"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PanelsFollowTheAgreedOrderWithTheDetailTableAboveTheHistory()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            var page = window.GetVisualDescendants().OfType<CombatPage>().Single();
            double Top(string name) => page.GetVisualDescendants().OfType<Control>().Single(border => border.Name == name)
                .TranslatePoint(new Point(0, 0), page)!.Value.Y;
            string[] order = ["ToolbarPanel", "ScopePanel", "DamagePanel", "HealingPanel", "SelfEffectsPanel", "BreakPanel", "DetailPanel", "HistoryPanel"];
            var tops = order.Select(Top).ToArray();
            Assert.Equal(tops.Order().ToArray(), tops);
            // 两张效果卡片并排；治疗是全宽随内容的紧凑卡片（不撑到一行高），治疗目标表随明细表在战斗记录上方。
            Assert.Equal(Top("SelfEffectsPanel"), Top("TargetEffectsPanel"));
            Assert.True(page.GetVisualDescendants().OfType<Card>().Single(border => border.Name == "HealingPanel").Bounds.Height < CardGrid.RowUnit);
            Assert.True(page.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "HealingTargetsGroup")
                .TranslatePoint(new Point(0, 0), page)!.Value.Y > Top("DetailPanel"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CombatHistoryShowsSegmentsAndOmitsThemForOldRecords()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            string[] Shown(string id) => window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.DataContext is Pages.Combat.EncounterRow row && row.Id == id)
                .Select(text => text.Text ?? string.Empty).ToArray();
            Assert.Contains("第 2 段 · 切换地图", Shown("e1"));
            Assert.Contains("第 1 段 · 首次伤害", Shown("e2"));
            // 没有分段的旧记录：没有分段文字，也没有空白占位。
            Assert.DoesNotContain(Shown("e3"), text => text.Contains('段') || text.Length == 0);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    private static CombatPageModel Model(Window window) =>
        (CombatPageModel)window.GetVisualDescendants().OfType<CombatPage>().Single().DataContext!;

    [AvaloniaFact]
    public void SampleShowsTheNewColumnsTilesAndElementBar()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            var model = Model(window);
            Assert.True(model.ShowTraitColumns);
            var table = Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "DamageTable");
            // 每个标签一组“次数／伤害”两列；攻击特性、首击与出现过的元素都在总表里，末尾是元素构成。
            Assert.Equal(["tag.critical.count", "tag.critical.damage", "tag.powerful.count"], table.VisibleColumns.Skip(5).Take(3).Select(column => column.Id));
            Assert.Contains(table.VisibleColumns, column => column.Id == "tag.first.count");
            Assert.Contains(table.VisibleColumns, column => column.Id == "tag.fire.damage");
            Assert.Equal("elements", table.VisibleColumns[^1].Id);
            Assert.Contains("各项占比相加可超过 100%", Named<TextBlock>(window, "TagNote").Text);

            var traits = VisibleTexts(Named<StackPanel>(window, "TraitsGroup"));
            Assert.Contains("攻击特性", traits);
            Assert.Contains("普通攻击", traits);
            Assert.Contains("多重打击", traits);
            // 首击不算比例：攻击特性块里没有，只单独显示最近一次的技能、伤害与目标，一行。
            Assert.DoesNotContain("首击", traits);
            Assert.True(Named<StackPanel>(window, "FirstAttackGroup").IsEffectivelyVisible);
            Assert.Contains("首击", VisibleTexts(Named<StackPanel>(window, "FirstAttackGroup")));
            Assert.Equal(["21,480", "重擊", "巨魔首領"], VisibleTexts(Named<Border>(window, "FirstAttackRow")));            // 连击与连段是两项不同的标记，都显示。
            Assert.Contains("连击", VisibleTexts(window.GetVisualDescendants().OfType<CombatPage>().Single()));

            Assert.True(Named<StackPanel>(window, "ElementsGroup").IsEffectivelyVisible);
            Assert.True(Named<MabiTool.Desktop.Controls.SegmentBar>(window, "ElementBar").IsEffectivelyVisible);
            Assert.False(Named<TextBlock>(window, "NoElementsText").IsVisible);
            // 图例的一项：元素名（图标可用时换成图标）与占比各一段文字。
            Assert.Equal(["火焰", "25%"], VisibleTexts(Named<ItemsControl>(window, "ElementLegend")).Take(2));
            Assert.Equal(model.ElementParts.Count, model.ElementSegments.Count);
            // 页面不显示“本机破防伤害”。
            Assert.DoesNotContain(VisibleTexts(window), text => text.Contains("破防伤害"));
        }
        finally { window.Close(); }
    }

    // 随包图标：登记的每个图标都能解码为 64×64；属性图标不需要绑定游戏，包里键齐全时不向后端登记使用（不读运行时目录），
    // 关掉随包图标（等同包里缺键）时回到后端运行时图标。生活技能与货币按游戏原文名称或货币类型对应图标。
    [AvaloniaFact]
    public void BundledIconsWorkWithoutAGameBindingAndTheRuntimeReadIsOnlyAFallback()
    {
        foreach (var entry in MabiTool.Desktop.Theme.GameIcons.All.Values)
        {
            var bitmap = MabiTool.Desktop.Theme.GameIcons.Bitmap(entry);
            Assert.True(bitmap is { PixelSize.Width: 64, PixelSize.Height: 64 }, entry.Id);
        }
        Assert.Equal("life/mining", MabiTool.Desktop.Theme.GameIcons.LifeSkill("採礦"));
        Assert.Null(MabiTool.Desktop.Theme.GameIcons.LifeSkill("不存在的技能"));
        Assert.Equal("currency/gold", MabiTool.Desktop.Theme.GameIcons.Currency("Gold", null));
        Assert.Equal("currency/mCash", MabiTool.Desktop.Theme.GameIcons.Currency(null, "M幣(免費)"));

        var backend = new SampleBackend();
        var previous = ClientIconHub.Source;
        ClientIconHub.Use(backend);
        try
        {
            ClientIconHub.UseBundled = true;
            Assert.False(ClientIconHub.NeedsRuntime);
            Assert.All(MabiTool.Contracts.ClientIconKeys.All, key => Assert.NotNull(ClientIconHub.Get(key)));
            Assert.Equal("#6CF08F", ClientIconHub.Get("critical")!.Tint);
            ClientIconHub.Acquire();
            Assert.False(backend.ClientIconsInUse);
            ClientIconHub.Release();

            ClientIconHub.UseBundled = false;
            Assert.Null(ClientIconHub.Get("critical"));
            ClientIconHub.Acquire();
            Assert.True(backend.ClientIconsInUse);
            ClientIconHub.Release();
            Assert.False(backend.ClientIconsInUse);
        }
        finally
        {
            ClientIconHub.UseBundled = false;
            ClientIconHub.Use(previous);
        }
    }

    // 比率卡片与总表列组标题：图标可用时只显示图标，名称在悬浮提示与无障碍名称里；不可用的键与图标撤下后回到文字，占位不变。
    [AvaloniaFact]
    public void RateIconsReplaceNamesWithTheNameInTheTooltipAndFallBackWithoutReflow()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        try
        {
            var grid = Named<ItemsControl>(window, "RateGrid");
            ClientIconSlot Slot(string key) => grid.GetVisualDescendants().OfType<ClientIconSlot>().Single(slot => slot.Icon == key);
            var critical = Slot("critical");
            var cards = grid.GetVisualDescendants().OfType<ClientIconSlot>().Select(slot => slot.TranslatePoint(default, grid)).ToList();
            var bounds = critical.Bounds;
            Assert.True(backend.ClientIconsInUse);
            Assert.False(critical.ShowsIcon);
            Assert.Equal(1, critical.Child!.Opacity);
            Assert.Null(ToolTip.GetTip(critical));
            Assert.Equal("暴击", AutomationProperties.GetName(critical));

            backend.ClientIcons = new() { ["critical"] = TestIcons.Image("critical") };
            backend.RaiseClientIconsChanged();
            LayoutProbe.Render(window);
            Assert.True(critical.ShowsIcon);
            Assert.Equal(0, critical.Child!.Opacity);
            Assert.Equal("暴击", ToolTip.GetTip(critical));
            Assert.Equal("暴击", AutomationProperties.GetName(critical));
            Assert.Equal(bounds, critical.Bounds);
            Assert.Equal(cards, grid.GetVisualDescendants().OfType<ClientIconSlot>().Select(slot => slot.TranslatePoint(default, grid)).ToList());
            Assert.False(Slot("powerfulHit").ShowsIcon);
            Assert.Equal(1, Slot("powerfulHit").Child!.Opacity);
            Assert.Contains(Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "DamageTable").GetVisualDescendants().OfType<ClientIconSlot>(),
                slot => slot.Icon == "critical" && slot.ShowsIcon && Equals(ToolTip.GetTip(slot), "暴击"));

            backend.ClientIcons = null;
            backend.RaiseClientIconsChanged();
            LayoutProbe.Settle();
            Assert.False(critical.ShowsIcon);
            Assert.Equal(1, critical.Child!.Opacity);
            Assert.Null(ToolTip.GetTip(critical));
            Assert.Equal(bounds, critical.Bounds);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AllUnknownKeepsTheTableAsBefore()
    {
        var window = LayoutProbe.Open(new SampleBackend { UnknownAttackTraits = true }, 1280, 860, "combat-observation");
        try
        {
            var model = Model(window);
            Assert.False(model.ShowTraitColumns);
            Assert.DoesNotContain(Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "DamageTable").VisibleColumns,
                column => column.Id.StartsWith("tag.breaked", StringComparison.Ordinal) || column.Id.StartsWith("tag.multi", StringComparison.Ordinal)
                    || column.Id.StartsWith("tag.fire", StringComparison.Ordinal) || column.Id == "elements");
            // 覆盖率四块总在；全部未知时攻击特性的其余块、首击与元素不显示、不占位。
            var rates = VisibleTexts(Named<ItemsControl>(window, "RateGrid"));
            Assert.DoesNotContain("无防备", rates);
            Assert.DoesNotContain("多重打击", rates);
            Assert.False(Named<StackPanel>(window, "ElementsGroup").IsEffectivelyVisible);
            Assert.False(Named<StackPanel>(window, "FirstAttackGroup").IsEffectivelyVisible);
            Assert.Equal(0, Named<StackPanel>(window, "FirstAttackGroup").Bounds.Height);
            Assert.Equal(0, Named<StackPanel>(window, "ElementsGroup").Bounds.Height);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void KnownButEmptyElementsShowOneLine()
    {
        var backend = new SampleBackend { NoElements = true };
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        try
        {
            Assert.True(Named<StackPanel>(window, "ElementsGroup").IsEffectivelyVisible);
            Assert.False(Named<MabiTool.Desktop.Controls.SegmentBar>(window, "ElementBar").IsVisible);
            Assert.True(Named<TextBlock>(window, "NoElementsText").IsEffectivelyVisible);
            Assert.Equal("无元素", Named<TextBlock>(window, "NoElementsText").Text);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ToolbarScopeTargetAndLayersSendTheirIntents()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        try
        {
            var model = Model(window);
            Click(window, Named<Button>(window, "ScopeSegmentButton"));
            Assert.Contains(("combat.scope", "segment"), backend.UiIntents);
            // 定时测量范围：没有选中时取最近一次结果。
            Click(window, Named<Button>(window, "ScopeMeasurementButton"));
            Assert.Contains(("combat.scope", "measurement:m1"), backend.UiIntents);
            LayoutProbe.Render(window);
            Click(window, Named<Button>(window, "BackToLiveButton"));
            Assert.Contains(("combat.scope.live", (string?)null), backend.UiIntents);

            LayoutProbe.Render(window);
            var boss = window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is RangeTargetChip { Id: "boss" });
            Click(window, boss);
            Assert.Contains(("combat.target", "boss"), backend.UiIntents);
            Assert.True(Named<Border>(window, "TargetDetailCard").IsEffectivelyVisible);

            // 设定层：越界不保存并写明范围；合法时发送毫秒设定。
            Click(window, Named<Button>(window, "TimerSettingsButton"));
            Assert.True(model.IsTimerOpen);
            model.TimerMinutes = "0"; model.TimerSeconds = "3";
            model.ApplyTimerCommand.Execute(null);
            Assert.True(model.HasTimerError);
            Assert.DoesNotContain(backend.UiIntents, intent => intent.Intent == "combat.measure.settings");
            model.TimerMinutes = "2"; model.TimerSeconds = "30";
            model.ApplyTimerCommand.Execute(null);
            Assert.Contains(("combat.measure.settings", "150000,0,0"), backend.UiIntents);
            Assert.False(model.IsTimerOpen);

            LayoutProbe.Render(window);
            Click(window, Named<Button>(window, "StartTimerButton"));
            Assert.Contains(("combat.measure.start", (string?)null), backend.UiIntents);
            LayoutProbe.Render(window);
            // 开始后等待首次伤害：显示“等待首次伤害”与取消，剩余时间要等首击后才开始走。
            Assert.True(Named<Border>(window, "TimerWaiting").IsEffectivelyVisible);
            Assert.False(model.ClockRunning);
            Click(window, Named<Button>(window, "CancelWaitingButton"));
            Assert.Contains(("combat.measure.cancel", (string?)null), backend.UiIntents);

            // 保存记录：默认名称取范围与时刻，空名不能保存。
            LayoutProbe.Render(window);
            Click(window, Named<Button>(window, "SaveRecordButton"));
            Assert.Equal("整场 11/14 21:30", model.RecordName);
            model.RecordName = "  ";
            Assert.False(model.CanSaveRecord);
            model.RecordName = "木桩";
            model.SaveRecordCommand.Execute(null);
            Assert.Contains(("combat.save", "木桩"), backend.UiIntents);

            // 重设需要确认一次。
            LayoutProbe.Render(window);
            Click(window, Named<Button>(window, "ResetButton"));
            Assert.DoesNotContain((CombatIntent.ResetEncounter, (string?)null), backend.Intents);
            LayoutProbe.Render(window);
            Click(window, Named<Button>(window, "ResetConfirm"));
            Assert.Contains((CombatIntent.ResetEncounter, (string?)null), backend.Intents);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TimerSettingsKeepTheOpeningRevisionThroughBackendRefreshes()
    {
        var backend = new SampleBackend { SampleMeasurementSettingsRevision = 7 };
        using var model = new CombatPageModel(backend);
        model.OpenTimerCommand.Execute(null);
        model.TimerMinutes = "2";
        model.TimerSeconds = "30";

        await backend.SendIntentAsync("combat.measure.settings", "180000,0,7", TestContext.Current.CancellationToken);
        Assert.Equal(8UL, model.Measure.SettingsRevision);
        Assert.Equal("2", model.TimerMinutes);
        model.ApplyTimerCommand.Execute(null);
        Assert.Equal(("combat.measure.settings", "150000,0,7"), backend.UiIntents.Last());

        model.OpenTimerCommand.Execute(null);
        Assert.Equal("3", model.TimerMinutes);
        model.ApplyTimerCommand.Execute(null);
        Assert.Equal(("combat.measure.settings", "180000,0,8"), backend.UiIntents.Last());
    }

    [AvaloniaFact]
    public void UnreadTimerSettingsDoNotSubmitPreferences()
    {
        var backend = new SampleBackend { SampleMeasurementSettingsRevision = null };
        using var model = new CombatPageModel(backend);
        model.OpenTimerCommand.Execute(null);
        model.ApplyTimerCommand.Execute(null);
        Assert.True(model.IsTimerOpen);
        Assert.True(model.HasTimerError);
        Assert.Empty(backend.UiIntents);
    }

    [AvaloniaFact]
    public void DetailWindowKeepsItsOwnScopeWithoutRefreshingTheMainPage()
    {
        var backend = new SampleBackend { SampleScope = CombatScopeKind.Segment };
        using var main = new CombatPageModel(backend);
        using var detail = main.Companion();
        detail.SetDetailOpen(true);
        var mainRefreshes = 0;
        backend.Changed += (_, _) => mainRefreshes++;
        // 窗口里切到整场：只改窗口自己的选择，不通知主页面。
        detail.SelectScopeCommand.Execute("observation");
        Assert.Equal(CombatScopeKind.Whole, detail.Scope.Kind);
        Assert.Equal(CombatScopeKind.Segment, main.Scope.Kind);
        Assert.Equal(0, mainRefreshes);
        Assert.Equal(("combat.detail.scope", "observation"), backend.UiIntents.Last());
        // 主页面切换范围也不改变窗口。
        main.SelectScopeCommand.Execute("measurement");
        Assert.Equal(CombatScopeKind.Measurement, main.Scope.Kind);
        Assert.Equal(CombatScopeKind.Whole, detail.Scope.Kind);
        // 整场曲线按本机时钟标注，悬停提示写时间与 DPS。
        Assert.NotNull(detail.Timeline.Origin);
        Assert.Matches(@"^\d\d:\d\d:\d\d · DPS [\d,]+$", detail.Timeline.Tips[0]);
    }

    [AvaloniaFact]
    public async Task LiveDetailSelectionNeitherChangesNorWakesTheMainPage()
    {
        await using var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "runtime", "instance.json"),
            action => action());
        var (main, detail) = (0, 0);
        backend.Changed += (_, _) => main++;
        backend.CombatDetailChanged += (_, _) => detail++;
        backend.SetCombatDetailOpen(true);
        await backend.SendIntentAsync("combat.detail.scope", "observation", TestContext.Current.CancellationToken);
        await backend.SendIntentAsync("combat.detail.target", "ce-1", TestContext.Current.CancellationToken);
        Assert.Equal(("observation", "ce-1"), (backend.GetCombatDetail().SelectedScope, backend.GetCombatDetail().SelectedTarget));
        Assert.Equal(("segment", (string?)null), (backend.GetCombat().SelectedScope, backend.GetCombat().SelectedTarget));
        Assert.Equal((0, 2), (main, detail));
        await backend.SendIntentAsync("combat.scope", "measurement", TestContext.Current.CancellationToken);
        Assert.Equal("observation", backend.GetCombatDetail().SelectedScope);
    }

    [AvaloniaFact]
    public void HealingWidgetShowsTheSameHealingAsThePage()
    {
        var backend = new SampleBackend();
        using var widget = new OverlayWidgetModel("combat.healing", backend);
        var healing = backend.GetCombat().Healing!;
        Assert.True(widget.IsHealing && widget.HealingAvailable);
        Assert.Equal((healing.Hps, healing.Cast, healing.CastCount, healing.Effective),
            (widget.HealingHps, widget.HealingCast, widget.HealingCount, widget.HealingEffective));
        Assert.Equal("整场", widget.HealingScope);
        backend.NoHealingData = true;
        backend.RaiseChanged();
        Assert.False(widget.HealingAvailable);
    }
}
