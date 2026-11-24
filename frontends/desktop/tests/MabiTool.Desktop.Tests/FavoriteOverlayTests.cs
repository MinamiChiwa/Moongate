using System.Diagnostics;
using Avalonia.Headless.XUnit;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages.Combat;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>
/// 收藏浮窗：选择与顺序的保存与旧文件默认、新收藏追加与取消收藏移除、不受“只显示收藏”影响、换图后自身项推算与到期结束；
/// 旧版面里收藏浮窗的觉醒项被移除；觉醒监控浮窗的两项、提示设置保存与提示音条件；
/// 觉醒按后端标记识别、同名不同 Id 不触发、冷却按实际或推算、1 秒内重复只算一次。
/// </summary>
public sealed class FavoriteOverlayTests : IDisposable
{
    private static readonly EntityKey Local = new("game", new(1), new(42), null);
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string effects = Path.Combine(Path.GetTempPath(), $"moongate-effects-{Guid.NewGuid():N}.json");
    private readonly string layouts = Path.Combine(Path.GetTempPath(), $"moongate-layout-{Guid.NewGuid():N}.json");

    private static long Ms(double value) => (long)((100_000 + value) * Stopwatch.Frequency / 1000);

    public FavoriteOverlayTests()
    {
        // 收藏 100（增益）与 200（减益）；目录里另有未收藏的增益 300。
        File.WriteAllText(effects, """
            { "Favorites": ["100", "200"], "Catalog": [
              { "Id": "100", "Name": "戰鬥吟唱", "Polarity": "Positive" },
              { "Id": "200", "Name": "破甲", "Polarity": "Negative" },
              { "Id": "300", "Name": "集中", "Polarity": "Positive" } ] }
            """);
        CombatEffectFavorites.Use(effects);
        CombatEffectOrder.Reset();
        FavoriteEffectTracker.Reset();
    }

    public void Dispose()
    {
        CombatEffectFavorites.Use(null);
        CombatEffectOrder.Reset();
        FavoriteEffectTracker.Reset();
        new OverlayLayoutStore(layouts).Flush();
        File.Delete(layouts);
        File.Delete(effects);
    }

    private OverlayLayoutStore OldStore()
    {
        File.WriteAllText(layouts, """
            { "ActiveId": "a", "Layouts": [ { "Id": "a", "Name": "A", "Widgets": [
                { "WidgetId": "combat.favorites-1", "X": 0, "Y": 0, "Scale": 1, "Skin": "glass" },
                { "WidgetId": "combat.favorites-2", "X": 1, "Y": 0, "Scale": 1, "Skin": "glass" } ] } ] }
            """);
        return new OverlayLayoutStore(layouts);
    }

    private static CombatEffectView View(ulong id, double remaining, DateTimeOffset applied, DateTimeOffset seen, string? awakening = null,
        EffectPresence presence = EffectPresence.Observed, string? name = null) =>
        new(new(Local, new(id), new(1), Local, applied, null, EffectDurationKind.Finite, 1, null, presence, ObservationQuality.Confirmed, new(1)),
            remaining, seen, false, false, name is null ? null : new("effect", new(id), name, "resolved"), "buff", awakening);

    private static CombatPageData Data(long at, bool observing, params CombatEffectView[] self)
    {
        var entity = new CombatEntityEffects("self", "owner", "obs", new(1), new(Local, true, [], Now, ObservationQuality.Confirmed), self);
        var status = new CombatObservationStatus("obs", "owner", "game", observing ? "observing" : "stopped", new("tw", "1", "TW", "b"), Local,
            new("game"), new(1), Now, Now, 0, ObservationQuality.Confirmed, [], new(0));
        return CombatEffectFavorites.Apply(CombatMapper.Map(new(true, true, new CombatWorkspace(status, null, [], entity, new(1)), [], null, null, [],
            EffectsAt: at)));
    }

    [AvaloniaFact]
    public void SelectionAndOrderAreSavedAndOldFilesUseTheDefaults()
    {
        var store = OldStore();
        var (first, second) = (store.Active.Widgets[0], store.Active.Widgets[1]);
        // 旧文件：收藏浮窗 1 按默认包含全部收藏（增益为自身、减益为目标），收藏浮窗 2 为空；保存时不写出选择。
        Assert.Equal(["self:100", "target:200"], FavoriteBoard.Effective(first));
        Assert.Empty(FavoriteBoard.Effective(second));
        store.Commit();
        store.Flush();
        Assert.DoesNotContain("FavoriteItems", File.ReadAllText(layouts));

        store.SetFavoriteItem(second, "target:100", true);
        store.MoveFavoriteItem(first, "target:200", 0);
        store.Flush();
        var reloaded = new OverlayLayoutStore(layouts);
        Assert.Equal(["target:200", "self:100"], FavoriteBoard.Effective(reloaded.Active.Widgets[0]));
        Assert.Equal(["target:100"], FavoriteBoard.Effective(reloaded.Active.Widgets[1]));
    }

    [AvaloniaFact]
    public void OldBoardsDropTheirAwakeningItemsAndKeepTheOtherFavoritesInOrder()
    {
        // 早期版本：收藏浮窗开着觉醒监控，觉醒两项混在收藏项里。
        File.WriteAllText(layouts, """
            { "ActiveId": "a", "Layouts": [ { "Id": "a", "Name": "A", "Widgets": [
                { "WidgetId": "combat.favorites-1", "X": 0, "Y": 0, "Scale": 1, "Skin": "glass", "AwakeningMonitor": true,
                  "FavoriteItems": ["awakening:badge", "target:200", "self:100", "awakening:class"] },
                { "WidgetId": "combat.favorites-2", "X": 1, "Y": 0, "Scale": 1, "Skin": "glass", "AwakeningMonitor": true, "Horizontal": true,
                  "FavoriteItems": ["awakening:badge", "awakening:class"] } ] } ] }
            """);
        var store = new OverlayLayoutStore(layouts);
        var (first, second) = (store.Active.Widgets[0], store.Active.Widgets[1]);
        Assert.Equal(["target:200", "self:100"], FavoriteBoard.Effective(first));
        Assert.Equal(["target:200", "self:100"], first.FavoriteItems);
        Assert.Null(second.FavoriteItems);
        Assert.True(second.Horizontal);
        // 不自动放置觉醒监控浮窗。
        Assert.Equal([FavoriteBoard.First, FavoriteBoard.Second], store.Active.Widgets.Select(widget => widget.WidgetId));
        store.Commit();
        store.Flush();
        var saved = File.ReadAllText(layouts);
        Assert.DoesNotContain("AwakeningMonitor", saved);
        Assert.DoesNotContain("awakening:", saved);
    }

    [AvaloniaFact]
    public void AwakeningWidgetShowsBothItemsKeepsItsSettingsAndGatesTheSound()
    {
        var store = OldStore();
        var catalog = MabiTool.Desktop.Modules.BuiltInModules.CreateCatalog(new SampleBackend(), store);
        // 收藏浮窗不再有觉醒项，选中收藏浮窗时卡片里没有觉醒提示。
        using (var favorites = new MabiTool.Desktop.Pages.Overlay.OverlayPageModel(catalog, catalog.Overlay!) { SelectedWidgetId = FavoriteBoard.First })
            Assert.Equal((true, false, false), (favorites.HasLayoutSetting, favorites.ShowAlerts, favorites.HasAwakeningSetting));

        var placement = store.Place(AwakeningBoard.Id);
        using var page = new MabiTool.Desktop.Pages.Overlay.OverlayPageModel(catalog, catalog.Overlay!) { SelectedWidgetId = AwakeningBoard.Id };
        Assert.Equal((true, true, false), (page.HasLayoutSetting, page.ShowAlerts, page.HasFavoritesSetting));
        Assert.Equal([FavoriteEffectTracker.Badge, FavoriteEffectTracker.Class], page.Alerts.Select(alert => alert.Kind));
        page.AlertSoundCommand.Execute(FavoriteEffectTracker.Badge);
        page.AlertVolumeCommand.Execute(FavoriteEffectTracker.Badge + ":-");
        page.AlertSecondsCommand.Execute(FavoriteEffectTracker.Class + ":+");
        page.FavoriteLayoutCommand.Execute("horizontal");

        using var widget = new OverlayWidgetModel(AwakeningBoard.Id, new SampleBackend());
        widget.SetFavoriteSource(placement, store);
        Assert.True(widget.IsAwakening);
        Assert.True(widget.FavoritesHorizontal);
        Assert.Equal([AwakeningBoard.BadgeKey, AwakeningBoard.ClassKey], widget.FavoriteItems.Select(item => item.Key));
        Assert.All(widget.FavoriteItems, item => Assert.True(item.Describe(Ms(0)).Dim));
        Assert.Equal(["徽章觉醒", "职业觉醒"], widget.FavoriteItems.Select(item => item.Describe(Ms(0)).Name));

        store.Flush();
        var reloaded = new OverlayLayoutStore(layouts);
        Assert.Equal((true, 0.7, 4d), (reloaded.Awakening.Badge.Sound, reloaded.Awakening.Badge.Volume, reloaded.Awakening.Class.FlashSeconds));
        Assert.True(reloaded.Active.Widgets.Single(widget => widget.WidgetId == AwakeningBoard.Id).Horizontal);

        // 提示音只在当前版面有未关闭的觉醒监控浮窗、且该项打开音效时播放。
        var played = new List<string>();
        AwakeningSound.Player = (kind, _, _) => played.Add(kind);
        try
        {
            AwakeningSound.Notify(FavoriteEffectTracker.Badge, store);
            AwakeningSound.Notify(FavoriteEffectTracker.Class, store);
            Assert.Equal([FavoriteEffectTracker.Badge], played);
            store.Close(placement);
            AwakeningSound.Notify(FavoriteEffectTracker.Badge, store);
            store.Unplace(AwakeningBoard.Id);
            AwakeningSound.Notify(FavoriteEffectTracker.Badge, store);
            Assert.Equal([FavoriteEffectTracker.Badge], played);
        }
        finally { AwakeningSound.Player = null; }
    }

    [AvaloniaFact]
    public void NewFavoritesJoinTheFirstBoardAndRemovedFavoritesLeaveBoth()
    {
        var store = OldStore();
        var (first, second) = (store.Active.Widgets[0], store.Active.Widgets[1]);
        store.MoveFavoriteItem(first, "target:200", 0);
        store.SetFavoriteItem(second, "self:100", true);
        CombatEffectFavorites.Toggled += store.FavoriteToggled;
        try
        {
            CombatEffectFavorites.Toggle("300");
            Assert.Equal(["target:200", "self:100", "self:300"], FavoriteBoard.Effective(first));
            Assert.Equal(["self:100"], FavoriteBoard.Effective(second));
            CombatEffectFavorites.Toggle("100");
            Assert.Equal(["target:200", "self:300"], FavoriteBoard.Effective(first));
            Assert.Empty(FavoriteBoard.Effective(second));
            Assert.DoesNotContain("self:100", first.FavoriteItems!);
        }
        finally { CombatEffectFavorites.Toggled -= store.FavoriteToggled; }
    }

    [AvaloniaFact]
    public void ClientBuffsWidenTheFavoritesSearchAndUnobservedFavoritesKeepTheirClientName()
    {
        // 客户端表：300 与目录里观测到的“集中”同号；400、401 同名“集中攻擊”；500 只在客户端表里。
        var client = new ClientBuffList([
            new("300", "集中", EffectPolarity.Positive, "暴擊機率增加"),
            new("400", "集中攻擊", EffectPolarity.Positive, "破防成功時，攻擊力增加 150\n持續 10 秒"),
            new("401", "集中攻擊", EffectPolarity.Negative, null),
            new("500", "減速", EffectPolarity.Negative, "移動速度減少")], false, null);

        // 已收藏在前，其次观测到过的，其余按正面、负面再按名称；同名的标出以显示描述摘要（没有描述写“无描述”）。
        Assert.Equal(["100", "200", "300", "400", "500", "401"], CombatEffectFavorites.Search(null, client).Select(entry => entry.Id));
        var result = CombatEffectFavorites.Find("集中", client);
        Assert.Equal(3, result.Total);
        Assert.Equal(["300", "400", "401"], result.Items.Select(entry => entry.Id));
        Assert.Equal((true, false, "暴擊機率增加"), (result.Items[0].Observed, result.Items[0].Duplicate, result.Items[0].Description));
        Assert.Equal(("破防成功時，攻擊力增加 150", Localization.UiText.Pick("无描述", "無描述")), (result.Items[1].Summary, result.Items[2].Summary));
        // 简体输入匹配客户端繁体原文；编号同样可搜。
        Assert.Equal(["400", "401"], CombatEffectFavorites.Search("攻击", client).Select(entry => entry.Id));
        Assert.Equal("100", Assert.Single(CombatEffectFavorites.Search("战斗", client)).Id);
        Assert.Equal("500", Assert.Single(CombatEffectFavorites.Search("500", client)).Id);
        // 客户端表不可用时只搜本机观测目录。
        Assert.Empty(CombatEffectFavorites.Search("攻击", ClientBuffList.Unsupported));

        // 收藏从未观测到的减益 401：名称与极性记进目录，按减益归到“收藏浮窗 1”的目标；收藏浮窗与效果卡片都显示客户端名称。
        var store = OldStore();
        var first = store.Active.Widgets[0];
        store.MoveFavoriteItem(first, "target:200", 0);
        CombatEffectFavorites.Toggled += store.FavoriteToggled;
        try { CombatEffectFavorites.Toggle(result.Items[2]); }
        finally { CombatEffectFavorites.Toggled -= store.FavoriteToggled; }
        Assert.Equal(("集中攻擊", EffectPolarity.Negative), (CombatEffectFavorites.NameOf("401"), CombatEffectFavorites.PolarityOf("401")));
        Assert.Equal(["target:200", "self:100", "target:401"], FavoriteBoard.Effective(first));
        var data = Data(Ms(0), true, View(401, 30_000, Now, Now));
        Assert.Equal("集中攻擊", FavoriteBoard.Items(first, data).Single(item => item.Key == "target:401").Name);
        Assert.Equal("集中攻擊", Assert.Single(data.SelfEffects).Name);
        CombatEffectFavorites.Use(effects);
        Assert.Equal("集中攻擊", CombatEffectFavorites.NameOf("401"));
    }

    [AvaloniaFact]
    public void BoardsIgnoreTheFavoritesOnlySwitches()
    {
        var store = OldStore();
        var first = store.Active.Widgets[0];
        string Describe(CombatPageData data) => string.Join(",", FavoriteBoard.Items(first, data).Select(item => $"{item.Key}:{item.Row is not null}"));
        var before = Describe(Data(Ms(0), true, View(100, 30_000, Now, Now), View(500, 30_000, Now, Now)));
        CombatEffectFavorites.SetSelfOnly(true);
        CombatEffectFavorites.SetTargetOnly(true);
        var data = Data(Ms(0), true, View(100, 30_000, Now, Now), View(500, 30_000, Now, Now));
        Assert.Equal(before, Describe(data));
        Assert.Equal("self:100:True,target:200:False", before);
        Assert.Single(data.SelfEffects);
    }

    [AvaloniaFact]
    public void SelfItemsKeepCountingDownAfterAMapChangeUntilTheExpireArrives()
    {
        var store = OldStore();
        var first = store.Active.Widgets[0];
        FavoriteEffectTracker.Clock = () => Ms(0);
        Data(Ms(0), true, View(100, 40_000, Now, Now));
        // 换图：宿主清空效果，自身项按记下的到期时间继续推算并标“推算”。
        FavoriteEffectTracker.Clock = () => Ms(5000);
        var cleared = Data(Ms(5000), true);
        var item = FavoriteBoard.Items(first, cleared).Single(entry => entry.Key == "self:100");
        Assert.True(item.Predicted);
        var look = item.Describe(Ms(5000));
        Assert.Equal(("35s", "推算", false), (look.Remaining, look.Note, look.Dim));
        // 之后收到该效果的到期移除：推算结束，项变暗。
        FavoriteEffectTracker.Clock = () => Ms(8000);
        var expired = Data(Ms(8000), true, View(100, 0, Now, Now.AddSeconds(8), presence: EffectPresence.ConfirmedRemoved));
        Assert.Null(FavoriteEffectTracker.Predicted("100"));
        Assert.True(FavoriteBoard.Items(first, expired).Single(entry => entry.Key == "self:100").Describe(Ms(8000)).Dim);

        // 正常到期（收到到期移除）不进入推算。
        FavoriteEffectTracker.Clock = () => Ms(9000);
        Data(Ms(9000), true, View(100, 20_000, Now.AddSeconds(9), Now.AddSeconds(9)));
        FavoriteEffectTracker.Clock = () => Ms(10_000);
        Data(Ms(10_000), true, View(100, 0, Now.AddSeconds(9), Now.AddSeconds(10), presence: EffectPresence.ConfirmedRemoved));
        Assert.Null(FavoriteEffectTracker.Predicted("100"));
    }

    [AvaloniaFact]
    public void AwakeningIsRecognizedByTheBackendMarkWithCooldownAndRepeatSuppression()
    {
        var fired = new List<string>();
        void OnTriggered(string kind) => fired.Add(kind);
        FavoriteEffectTracker.Triggered += OnTriggered;
        try
        {
            FavoriteEffectTracker.Clock = () => Ms(0);
            FavoriteEffectTracker.WallClock = () => Now;
            // 同名“覺醒”但后端没有标记：不触发。
            Data(Ms(0), true, View(2018547152, 5000, Now, Now, name: "覺醒"));
            Assert.Empty(fired);
            Assert.Equal(AwakeningPhase.Idle, FavoriteEffectTracker.Describe(FavoriteEffectTracker.Badge, Ms(0)).Phase);

            // 徽章觉醒效果与冷却效果相隔 0.4 秒先后到达：只算一次触发，显示触发的徽章与剩余时间。
            FavoriteEffectTracker.Clock = () => Ms(100);
            FavoriteEffectTracker.WallClock = () => Now.AddMilliseconds(100);
            Data(Ms(100), true, View(355098955, 35_000, Now, Now, "badge", name: "飛舞之劍"));
            FavoriteEffectTracker.Clock = () => Ms(500);
            FavoriteEffectTracker.WallClock = () => Now.AddMilliseconds(500);
            Data(Ms(500), true, View(355098955, 34_600, Now, Now, "badge", name: "飛舞之劍"),
                View(315606915, 90_000, Now.AddMilliseconds(400), Now.AddMilliseconds(400), "badge-cooldown"));
            Assert.Equal([FavoriteEffectTracker.Badge], fired);
            var active = FavoriteEffectTracker.Describe(FavoriteEffectTracker.Badge, Ms(10_000));
            Assert.Equal((AwakeningPhase.Active, "飛舞之劍"), (active.Phase, active.Badge));
            Assert.Equal("徽章觉醒·飛舞之劍", new FavoriteItem(AwakeningBoard.BadgeKey, FavoriteItemKind.Badge, "徽章觉醒", string.Empty,
                EffectPolarity.Positive, null, false).Describe(Ms(10_000)).Name);
            // 觉醒结束后按冷却效果的实际剩余显示冷却，不标推算。
            var cooling = FavoriteEffectTracker.Describe(FavoriteEffectTracker.Badge, Ms(40_000));
            Assert.Equal((AwakeningPhase.Cooldown, false), (cooling.Phase, cooling.Predicted));
            Assert.Equal(50_500, cooling.RemainingMs!.Value, 0);
            Assert.Equal(AwakeningPhase.Idle, FavoriteEffectTracker.Describe(FavoriteEffectTracker.Badge, Ms(91_000)).Phase);

            // 职业觉醒没有冷却效果：从触发时刻按 60 秒推算冷却。
            FavoriteEffectTracker.Clock = () => Ms(1000);
            FavoriteEffectTracker.WallClock = () => Now.AddSeconds(1);
            Data(Ms(1000), true, View(674865859, 15_000, Now.AddSeconds(1), Now.AddSeconds(1), "class"));
            Assert.Equal([FavoriteEffectTracker.Badge, FavoriteEffectTracker.Class], fired);
            var predicted = FavoriteEffectTracker.Describe(FavoriteEffectTracker.Class, Ms(20_000));
            Assert.Equal((AwakeningPhase.Cooldown, true), (predicted.Phase, predicted.Predicted));
            Assert.Equal(41_000, predicted.RemainingMs!.Value, 0);
        }
        finally { FavoriteEffectTracker.Triggered -= OnTriggered; }
    }
}
