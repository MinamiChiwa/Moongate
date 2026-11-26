using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages.Character;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Market;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>
/// 游戏连接器的数据读取（第二批）与交易所查价、背包估价（第三批）在桌面一侧的行为：角色页的任务分组与旧数据、缺省字段“未读取”、
/// 来源标注、天气与状态文字、附近演奏只提示一次、查价的名称下限与冷却、成本下限、估价状态与合计、读取只在界面可见时进行。
/// </summary>
public sealed class GameDataTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 25, 12, 3, 11, TimeSpan.Zero);

    public GameDataTests()
    {
        if (UiText.Language != UiLanguage.SimplifiedChinese && Avalonia.Application.Current is { } application)
            UiText.Apply(application, UiLanguage.SimplifiedChinese);
    }

    private static GameDataStatus Fresh(GameDataSource source = GameDataSource.Connector, string? reason = null, bool stale = false,
        params GameDataConflict[] conflicts) => new(source, reason, At, stale, conflicts);

    private static GameMission Mission(string title, int current, int goal, bool done, bool received) =>
        new(title, "说明", current, goal, done, received, false);

    private static string Local(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // ---- 任务 ----

    [AvaloniaFact]
    public void MissionsListTodoFirstUnclaimedRewardsSeparatelyAndOnlyCountReceivedOnes()
    {
        var list = new GameMissionList(Fresh(), [Mission("甲", 0, 3, false, false), Mission("乙", 5, 5, true, false), Mission("丙", 1, 1, true, true),
            Mission("丁", 2, 5, false, false)]);
        var tab = CharacterMapper.MissionTab(list);
        Assert.Equal(["甲", "丁"], tab.Todo.Select(line => line.Title));
        Assert.Equal(["乙"], tab.Ready.Select(line => line.Title));
        Assert.True(tab.Ready[0].Ready);
        Assert.Equal(1, tab.ReceivedCount);
        Assert.Equal("已领奖 1 项", tab.ReceivedText);
        Assert.Equal("待办 2 · 可领奖 1 · 已领奖 1", tab.Summary);
        Assert.Equal("0 / 3", tab.Todo[0].Progress);
        Assert.False(tab.Unread);
    }

    [AvaloniaFact]
    public void AHeldEmptyListKeepsTheOldMissionsMarkedAsOldDataAndAnUnreadListIsNotNoMissions()
    {
        var held = new GameMissionList(Fresh(reason: "gameCli.emptyHeld", stale: true), [Mission("甲", 0, 3, false, false)]);
        var tab = CharacterMapper.MissionTab(held);
        Assert.Single(tab.Todo);
        Assert.StartsWith("旧数据", tab.Note.Text);
        Assert.True(tab.Note.Dim);
        Assert.Contains("保留上次结果", tab.Note.Detail);

        var cleared = CharacterMapper.MissionTab(new GameMissionList(Fresh(), []));
        Assert.False(cleared.Unread);
        Assert.Equal("当前没有任务", cleared.Summary);
        var unread = CharacterMapper.MissionTab(new GameMissionList(new(GameDataSource.None, "gameCli.disabled", null, false, []), null));
        Assert.True(unread.Unread);
        Assert.Equal("未读取", unread.Summary);
        Assert.Contains("游戏连接器未启用", unread.Note.Detail);
    }

    // ---- 缺省字段、昵称与称号、来源标注 ----

    [AvaloniaFact]
    public void MissingFieldsShowUnreadInsteadOfZeroAndMaxLevelSkillsShowOnlyTheLevel()
    {
        var profile = new GameProfile("艾薇", null, null, null, null, [], [], null,
            [new("伐木", 28, 19378, 21040), new("鍊金術", 10, null, null), new("兼職", null, null, null)]);
        var page = CharacterMapper.Map(CharacterRead.Loaded, new GameDataSnapshot("e", new(Fresh(), profile), null, null, null, null, null));
        Assert.Equal("未读取", page.Overview.Level);
        Assert.Equal("未读取", page.Overview.Job);
        Assert.Equal("未读取", page.Overview.Title);
        Assert.Equal(5, page.Vitals.Count);
        Assert.All(page.Vitals, line => { Assert.Equal("未读取", line.Value); Assert.True(line.Unread); });
        Assert.Empty(page.Stats);
        Assert.Equal("19,378 / 21,040", page.Skills[0].Exp);
        Assert.Equal("Lv.10", page.Skills[1].Level);
        Assert.False(page.Skills[1].HasExp);
        Assert.False(page.Skills[1].HasFraction);
        Assert.Equal("未读取", page.Skills[2].Level);
        // 四项评分始终在概况，没读到的为未读取（货币不在角色页）。
        Assert.Equal(4, page.Scores.Count);
        Assert.All(page.Scores, line => { Assert.Equal("未读取", line.Value); Assert.True(line.Unread); });
        Assert.Equal(CharacterRead.Offline, CharacterPageData.Offline.Read);
        Assert.Equal("未连接后端", CharacterPageData.Offline.Status);
    }

    [AvaloniaFact]
    public void NicknameServerAndTitleEachHaveTheirOwnLineAndTheNicknameNeverFallsBackToTheServer()
    {
        var profile = new GameProfile("艾薇", "病弱美少女", "愛麗沙", 65, "魔法師", [new("STR", "力量", 2215), new("CombatScore", "戰鬥力", 32503)], [], null, []);
        var page = CharacterMapper.Map(CharacterRead.Loaded, new GameDataSnapshot("e", new(Fresh(), profile), null, null, null, null, null));
        Assert.Equal(("艾薇", "愛麗沙", "病弱美少女"), (page.Overview.Nickname, page.Overview.Server, page.Overview.Title));
        Assert.Equal("Lv.65", page.Overview.Level);
        Assert.Equal("力量", page.Stats[0].Label);
        Assert.Equal("2,215", page.Stats[0].Value);
        // 评分按字段名进概况（名称取游戏原文），属性卡不再重复；没读到的评分为未读取。
        Assert.Equal(("戰鬥力", "32,503", false), (page.Scores[0].Label, page.Scores[0].Value, page.Scores[0].Unread));
        Assert.Equal(("生活力", "未读取", true), (page.Scores[1].Label, page.Scores[1].Value, page.Scores[1].Unread));
        Assert.DoesNotContain(page.Stats, line => line.Label == "戰鬥力");

        // 抓包没有识别本机：昵称未读取，不拿服务器名或称号顶替；服务器与称号照常显示。
        var noCapture = CharacterMapper.Map(CharacterRead.Loaded, new GameDataSnapshot("e", new(Fresh(), profile with { Nickname = null }), null, null, null, null, null));
        Assert.Equal(("未读取", true, "愛麗沙", "病弱美少女"), (noCapture.Overview.Nickname, noCapture.Overview.NicknameUnread, noCapture.Overview.Server, noCapture.Overview.Title));
    }

    [AvaloniaFact]
    public void SourceNotesNameTheSourceReadTimeOldDataAndConflicts()
    {
        var connector = CharacterText.Note(Fresh());
        Assert.Equal("游戏连接器 · " + Local(At), connector.Text);
        Assert.Empty(connector.Detail);
        Assert.False(connector.Dim);

        var capture = CharacterText.Note(Fresh(GameDataSource.Capture, "gameCli.disabled"));
        Assert.Equal("抓包 · " + Local(At), capture.Text);
        Assert.Contains("游戏连接器未启用", capture.Detail);

        var stale = CharacterText.Note(Fresh(reason: "gameCli.busy", stale: true));
        Assert.StartsWith("旧数据 · 游戏连接器", stale.Text);
        Assert.True(stale.Dim);
        Assert.Contains("指令通道正忙", stale.Detail);

        var conflict = CharacterText.Note(Fresh(conflicts: [new GameDataConflict("area", "庫漢", "提爾·克拉")]));
        Assert.True(conflict.HasConflict);
        Assert.Contains("来源冲突", conflict.Conflict);
        Assert.Contains("庫漢", conflict.Conflict);
        Assert.Contains("提爾·克拉", conflict.Conflict);

        var sample = CharacterText.Note(Fresh(), sample: true);
        Assert.StartsWith("样本数据", sample.Text);
        var none = CharacterText.Note(new GameDataStatus(GameDataSource.None, "gameCli.expired", null, false, []));
        Assert.Equal("未读取", none.Text);
        Assert.Contains("已失效", none.Detail);
        Assert.Equal("未读取", CharacterText.Note(null).Text);
    }

    [AvaloniaFact]
    public void ErinnTimeShowsOnlyTheClockAndWeightKeepsAtMostTwoDecimals()
    {
        Assert.Equal("13:13", CharacterText.ErinnClock("2968-6-08 13:13"));
        Assert.Equal("07:05", CharacterText.ErinnClock("2968-06-08 7:05:42.1234567"));
        Assert.Equal("19:30", CharacterText.ErinnClock("2968/6/8 下午 7:30:00"));
        Assert.Equal("Erinn Day 8", CharacterText.ErinnClock(" Erinn Day 8 "));
        Assert.Equal("未读取", CharacterText.ErinnClock(null));
        Assert.Equal("696.39 / 1,110", CharacterText.Pair(696.387634, 1110, 2));
    }

    [AvaloniaFact]
    public void WeatherBecomesChineseInBothLanguagesAndUnknownWeatherKeepsTheGameText()
    {
        Assert.Equal("晴", CharacterText.Weather("Sunny"));
        Assert.Equal("多云", CharacterText.Weather("cloudy"));
        Assert.Equal("Hailstorm9", CharacterText.Weather("Hailstorm9"));
        Assert.Equal("未读取", CharacterText.Weather(null));
        var application = Avalonia.Application.Current!;
        try
        {
            UiText.Apply(application, UiLanguage.TraditionalChinese);
            Assert.Equal("多雲", CharacterText.Weather("Cloudy"));
            Assert.Equal("霧", CharacterText.Weather("Foggy"));
            Assert.Equal("未讀取", CharacterText.Weather(""));
        }
        finally { UiText.Apply(application, UiLanguage.SimplifiedChinese); }
    }

    [AvaloniaFact]
    public void StatusTextsForTheNewReasonCodesContainNoEnglishCodes()
    {
        string[] codes =
        [
            "gameCli.busy", "gameCli.emptyHeld", "gameCli.readFailed", "gameCli.guideLocked", "gameCli.nameTooShort", "gameCli.categoryNotFound",
            "gameCli.marketLocked", "gameCli.marketUnavailable", "gameCli.rateLimited", "gameCli.notFound", "gameCli.ambiguousItem", "gameCli.invalidBody",
            "gameCli.interrupted", "gameCli.notConnected", "gameCli.disabled", "gameCli.gameNotRunning", "gameCli.expired", "gameCli.commandMissing",
            "gameCli.catalogLoading", "gameCli.cliMissing", "platform.unsupported", "storage.unavailable"
        ];
        var application = Avalonia.Application.Current!;
        try
        {
            foreach (var language in new[] { UiLanguage.SimplifiedChinese, UiLanguage.TraditionalChinese })
            {
                UiText.Apply(application, language);
                foreach (var code in codes)
                    foreach (var text in new[] { CharacterText.ReasonLine(code), MarketText.Reason(code) })
                    {
                        Assert.NotEmpty(text);
                        Assert.DoesNotMatch(new Regex(@"[A-Za-z]{2,}\.[A-Za-z]"), text);
                        Assert.DoesNotContain("gameCli", text);
                    }
            }
        }
        finally { UiText.Apply(application, UiLanguage.SimplifiedChinese); }
    }

    // ---- 环境与附近 ----

    [AvaloniaFact]
    public void EnvironmentKeepsTheGameTextAndShowsUnreadForMissingItems()
    {
        var view = CharacterMapper.Environment(new(Fresh(), new("庫漢", "庫漢", 12.0674438, 52.9489059, "Sunny", "2968-6-08 13:13", null)));
        Assert.Equal("庫漢", view.Channel);
        Assert.Equal(("13:13", "游戏原文：2968-6-08 13:13"), (view.Time, view.TimeTip));
        Assert.Equal("晴", view.Weather);
        Assert.Equal("(12.1, 52.9)", view.Position);
        Assert.Equal("未读取", view.Housing);
        Assert.False(view.Unread);

        var area = CharacterMapper.Environment(new(Fresh(GameDataSource.Capture, "gameCli.disabled"), new(null, "提爾·克拉", null, null, null, null, null)));
        Assert.Equal("提爾·克拉", area.Area);
        Assert.Equal("未读取", area.Channel);
        Assert.Equal("未读取", area.Weather);
        Assert.Equal("未读取", area.Time);
        Assert.StartsWith("抓包", area.Note.Text);
        Assert.True(CharacterMapper.Environment(null).Unread);
    }

    [AvaloniaFact]
    public void NearbyListsPerformersFirstAndTellsNoPlayersApartFromUnread()
    {
        var players = new GameNearbyPlayer[]
        {
            new("愛麗沙", "称号甲", 20, 50, "劍士", false, false, false, false, false, null),
            new("愛麗沙", null, 8, 65, "魔法師", false, false, false, false, false, new(true, "月光", true, true, 240, 92, 148, 3)),
            new("愛麗沙", " ", 4, 40, "弓手", false, false, false, false, false, new(false, null, null, null, null, null, null, null))
        };
        var view = CharacterMapper.Nearby(new(Fresh(), players));
        // 附近玩家没有昵称字段：身份是称号，没有称号写“附近玩家”，服务器名不当昵称（只在悬停提示里作“服务器”）。
        Assert.Equal(["附近玩家", "附近玩家", "称号甲"], view.Players.Select(line => line.Name));
        Assert.Equal(["Lv.65 · 魔法師 · 距离 8 m", "Lv.40 · 弓手 · 距离 4 m", "Lv.50 · 劍士 · 距离 20 m"], view.Players.Select(line => line.Detail));
        Assert.Equal("服务器：愛麗沙", view.Players[2].Tip);
        Assert.DoesNotContain(view.Players, line => line.Name == "愛麗沙");
        Assert.Contains("演奏《月光》", view.Players[0].Performance);
        Assert.Contains("3 声部", view.Players[0].Performance);
        Assert.Contains("剩余 2:28", view.Players[0].Performance);
        Assert.Empty(view.Players[1].Performance);
        Assert.Equal([new NearbyPerformer("附近玩家（Lv.65 魔法師）", "月光", 3)], view.Performers);

        var empty = CharacterMapper.Nearby(new(Fresh(), []));
        Assert.False(empty.Unread);
        Assert.Equal("附近没有玩家", empty.Empty);
        var unread = CharacterMapper.Nearby(new(new(GameDataSource.None, "gameCli.disabled", null, false, []), null));
        Assert.True(unread.Unread);
        Assert.Equal("未读取", unread.Empty);
    }

    [AvaloniaFact]
    public void NearbyPerformancesAreAnnouncedOncePerTitleAndResetWhenNobodyPlaysIt()
    {
        var announcer = new NearbyAnnouncer();
        NearbyPerformer Player(string name, string title, int channels = 1) => new(name, title, channels);
        // 两人连续演奏同一首：只提示一次。
        var first = announcer.Update([Player("甲", "月光", 2)]);
        Assert.Equal(["月光"], first.Select(item => item.Title));
        Assert.Equal("甲 正在演奏《月光》 · 2 声部", first[0].Text);
        Assert.Empty(announcer.Update([Player("甲", "月光", 2), Player("乙", "月光", 2)]));
        // 甲停下、乙还在：仍是同一首，不重复提示。
        Assert.Empty(announcer.Update([Player("乙", "月光", 2)]));
        // 同时出现另一首：只提示新的。
        Assert.Equal(["夜曲"], announcer.Update([Player("乙", "月光", 2), Player("丙", "夜曲")]).Select(item => item.Title));
        // 全部停止后重置，再次开始可以再次提示。
        Assert.Empty(announcer.Update([]));
        Assert.Equal(["月光"], announcer.Update([Player("丁", "月光")]).Select(item => item.Title));
    }

    [AvaloniaFact]
    public void TheCaptionNamesPerformersByTitleOrNearbyPlayerWithCountsInsteadOfTheServerName()
    {
        var text = NearbyAnnouncer.Caption("月光", [new("月光騎士（Lv.65 魔法師）", "月光", 2), new("附近玩家（Lv.50 弓手）", "月光", 1), new("附近玩家（Lv.50 弓手）", "月光", 1)]);
        Assert.Equal("月光騎士（Lv.65 魔法師）、附近玩家（Lv.50 弓手） ×2 正在演奏《月光》 · 2 声部", text);
    }

    [AvaloniaFact]
    public void TheCaptionStaysForTheHoldTimeAndAnUnreadReadDoesNotResetTheRule()
    {
        var now = At;
        var state = new NearbyCaptionState(TimeSpan.FromSeconds(8), () => now);
        NearbyView View(params NearbyPerformer[] performers) => new NearbyView(BlockNote.Unread(), false, [], performers);
        Assert.Null(state.Current);
        Assert.True(state.Observe(View(new NearbyPerformer("甲", "月光", 1))));
        Assert.Equal("甲 正在演奏《月光》", state.Current);
        now = At.AddSeconds(7);
        Assert.NotNull(state.Current);
        now = At.AddSeconds(9);
        Assert.Null(state.Current);
        Assert.False(state.Active);
        // 读取失败（未读取）不改变规则的状态：恢复后同一首仍在演奏，不再提示。
        Assert.False(state.Observe(NearbyView.NotRead));
        Assert.False(state.Observe(View(new NearbyPerformer("甲", "月光", 1))));
        Assert.Null(state.Current);
    }

    // ---- 角色页模型 ----

    [AvaloniaFact]
    public void TheCharacterPageReportsItsSurfaceOnlyWhileShownAndRefreshSendsTheIntent()
    {
        var backend = new SampleBackend();
        using var model = new CharacterPageModel(backend);
        Assert.Equal(1, backend.VisibleSurfaces["character"]);
        Assert.Equal(CharacterRead.Loaded, model.Data.Read);
        Assert.True(model.Data.Sample);
        Assert.StartsWith("样本数据", model.Data.ProfileNote.Text);
        model.Deactivate();
        Assert.Equal(0, backend.VisibleSurfaces["character"]);
        model.Activate();
        Assert.Equal(1, backend.VisibleSurfaces["character"]);

        model.IntentCommand.Execute("character.refresh");
        Assert.Equal(1, backend.CharacterRefreshes);

        Assert.True(model.ShowDaily);
        var daily = model.SelectedMission;
        model.SelectMissionCommand.Execute("weekly");
        Assert.True(model.ShowWeekly);
        Assert.NotSame(daily, model.SelectedMission);
        Assert.Same(model.Data.Weekly, model.SelectedMission);

        backend.Offline = true;
        backend.RaiseChanged();
        Assert.Equal(CharacterRead.Offline, model.Data.Read);
        Assert.False(model.CanRefresh);
    }

    // ---- 浮窗元件 ----

    [AvaloniaFact]
    public void TheEnvironmentAndNearbyWidgetsReadTheirOwnTopicOnlyWhileShownAndShowTheGameText()
    {
        var backend = new SampleBackend();
        using (var environment = new OverlayWidgetModel("environment.info", backend, PresentationTopics.Environment))
        {
            Assert.True(environment.IsEnvironment);
            Assert.Equal("庫漢", environment.EnvironmentArea);
            Assert.Equal("频道 庫漢 · 晴", environment.EnvironmentChannelLine);
            Assert.Equal("游戏内时间 13:13", environment.EnvironmentTimeLine);
            Assert.False(backend.VisibleSurfaces.GetValueOrDefault("overlay.environment") > 0);
            environment.SetShown(true);
            Assert.Equal(1, backend.VisibleSurfaces["overlay.environment"]);
            Assert.False(backend.VisibleSurfaces.GetValueOrDefault("overlay.nearby") > 0);
            environment.SetShown(false);
            Assert.Equal(0, backend.VisibleSurfaces["overlay.environment"]);
        }
        using (var nearby = new OverlayWidgetModel("performance.nearby", backend, PresentationTopics.Nearby))
        {
            Assert.True(nearby.IsNearby);
            Assert.True(nearby.HasNearbyCaption);
            Assert.Equal("新手冒險家（Lv.41 劍士） 正在演奏《月光下的艾明馬夏》 · 3 声部", nearby.NearbyCaption);
            nearby.SetShown(true);
            Assert.Equal(1, backend.VisibleSurfaces["overlay.nearby"]);
            Assert.True(nearby.ClockRunning);
            Assert.False(backend.VisibleSurfaces.GetValueOrDefault("overlay.environment") > 0);
        }
        Assert.Equal(0, backend.VisibleSurfaces["overlay.nearby"]);
    }

    [AvaloniaFact]
    public void TheCharacterAndMarketPagesBuildAndFitInTheWindow()
    {
        foreach (var module in new[] { "character", "market" })
        {
            var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, module);
            try
            {
                Assert.Contains(window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(), text => text.IsEffectivelyVisible);
                Assert.Empty(LayoutProbe.FindTextOverflow(window));
            }
            finally { window.Close(); }
        }
    }

    // ---- 交易所查价 ----

    private static GameMarketTier Tier(double price, int count) => new(price, count);

    [AvaloniaFact]
    public void PurchaseCostIsALowerBoundBeyondTheThreeTiersAndNeverMultipliesPastTheAvailableCount()
    {
        GameMarketTier[] tiers = [Tier(100, 3), Tier(110, 5), Tier(125, 10)];
        var within = MarketCost.Compute(tiers, 5);
        Assert.Equal((520d, 5, 0), (within.PricedSubtotal, within.PricedCount, within.UnpricedCount));
        Assert.True(within.Known);
        Assert.Contains("520", within.Text);
        Assert.Contains("成本", within.Text);

        var exact = MarketCost.Compute(tiers, 18);
        Assert.True(exact.Known);
        Assert.Equal(2100d, exact.PricedSubtotal);

        var beyond = MarketCost.Compute(tiers, 30);
        Assert.True(beyond.IsLowerBound);
        Assert.False(beyond.Known);
        Assert.Equal((2100d, 18, 12), (beyond.PricedSubtotal, beyond.PricedCount, beyond.UnpricedCount));
        Assert.Contains("下限", beyond.Text);
        Assert.Contains("其余 12 件成本未知", beyond.Text);

        var none = MarketCost.Compute([], 4);
        Assert.Equal(0, none.PricedCount);
        Assert.Contains("成本未知", none.Text);
        Assert.Empty(MarketCost.Compute(tiers, 0).Text);
    }

    [AvaloniaFact]
    public void MarketFailuresExplainThemselvesWithoutPricesAndCategoryMissesListCandidates()
    {
        var locked = MarketMapper.Prices(new("e", "names", "gameCli.marketLocked", null, null, [], null, false, null, null, [], []));
        Assert.False(locked.Ok);
        Assert.Equal("交易所尚未解锁，不能查价", locked.Message);
        Assert.Empty(locked.Groups);

        var missing = MarketMapper.Prices(new("e", "category", "gameCli.categoryNotFound", null, null, ["樂器", "材料"], null, false, "x", null, [], []));
        Assert.True(missing.HasCandidates);
        Assert.Contains("樂器", missing.CandidatesText);

        var ambiguous = MarketMapper.Detail(new("e", "gameCli.ambiguousItem", null, null,
            [new("冬夜禮服", "傳說", 2, "S1", null, false, true, 340000), new("冬夜禮服", "傳說", 3, "S1", null, false, true, 520000)], null, null, null));
        Assert.True(ambiguous.HasCandidates);
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.Contains("请选择具体的品阶", ambiguous.Message);
        Assert.False(ambiguous.HasTiers);

        var ok = MarketMapper.Prices(new("e", "names", null, null, null, [], At, false, null, null,
            [new("魯特琴", true, [new("魯特琴", "稀有", null, null, null, false, true, 18000), new("魯特琴", "普通", null, null, null, false, false, null)])], []));
        Assert.True(ok.Ok);
        Assert.Equal("交易所价格，单位德卡 · 取价于 " + Local(At), ok.Note);
        Assert.Equal(["18,000", "—"], ok.Groups[0].Rows.Select(row => row.Price));
        Assert.Equal("无在售", ok.Groups[0].Rows[1].Stock);
    }

    [AvaloniaFact]
    public async Task NamesShorterThanTwoCharactersAreRefusedWithoutSendingAQuery()
    {
        var backend = new SampleBackend();
        using var model = new MarketPageModel(backend);
        Assert.Equal("gameCli.nameTooShort", new MarketPriceRequest(MarketMode.Names, ["木"]).Problem);
        Assert.Null(new MarketPriceRequest(MarketMode.Names, ["原木"]).Problem);
        Assert.Equal("gameCli.nameTooShort", new MarketPriceRequest(MarketMode.Names, []).Problem);
        Assert.Equal(["原木", "魯特琴"], MarketPriceRequest.SplitNames("原木，魯特琴 原木"));

        model.NamesText = "木";
        await model.QueryAsync();
        Assert.Empty(backend.MarketPriceRequests);
        Assert.Equal("名称至少 2 个字", model.PriceMessage);
        Assert.Empty(model.Groups);

        model.NamesText = "原木";
        await model.QueryAsync();
        Assert.Single(backend.MarketPriceRequests);
        Assert.Contains("交易所价格，单位德卡", model.PriceNote);
        Assert.True(model.HasGroups);
        Assert.Empty(backend.MarketDetailRequests);
    }

    [AvaloniaFact]
    public async Task ARateLimitedDetailDisablesTheButtonCountsDownAndIsNeverResentAutomatically()
    {
        var now = At;
        var backend = new SampleBackend { MarketDetailRateLimit = 3 };
        using var model = new MarketPageModel(backend, () => now);
        model.NamesText = "魯特琴";
        await model.QueryAsync();
        model.SelectRowCommand.Execute(model.Groups[0].Rows[0]);
        Assert.True(model.CanQueryDetail);
        await model.QueryDetailAsync();
        Assert.False(model.CanQueryDetail);
        Assert.Contains("3 秒后可用", model.DetailLabel);
        Assert.False(model.HasDetail);
        Assert.Equal("查询太频繁，请稍后再试", model.DetailMessage);

        now = At.AddSeconds(1);
        model.Tick();
        Assert.Contains("2 秒后可用", model.DetailLabel);
        Assert.False(model.CanQueryDetail);
        now = At.AddSeconds(3.2);
        model.Tick();
        Assert.True(model.CanQueryDetail);
        Assert.Equal("查询明细", model.DetailLabel);
        // 倒计时结束后没有自动重发：只有用户再点才查询。
        Assert.Single(backend.MarketDetailRequests);
        await model.QueryDetailAsync();
        Assert.Equal(2, backend.MarketDetailRequests.Count);
        Assert.True(model.HasDetail);
        Assert.True(model.DetailView.HasTiers);
        model.QuantityText = "100";
        Assert.True(model.CostLowerBound);
        Assert.Contains("下限", model.CostText);
        Assert.Equal(2, backend.MarketDetailRequests.Count);
    }

    [AvaloniaFact]
    public void TheMarketEntryIsDisabledWithTheReasonWhenTheCapabilityIsUnavailable()
    {
        var backend = new SampleBackend
        {
            CapabilityNotices = id => id == CapabilityIds.MarketPrices ? "暂不可用：游戏连接器未启用" : null
        };
        using var model = new MarketPageModel(backend);
        Assert.True(model.HasBlocked);
        Assert.False(model.CanQuery);
        Assert.Contains("游戏连接器未启用", model.Blocked);
        backend.Offline = true;
        backend.RaiseChanged();
        Assert.Equal("未连接后端", model.Blocked);
    }

    // ---- 背包估价 ----

    private static GameMarketValuationRow Row(string name, int count, GameMarketValuationState state, double? unit = null, string? reason = null) =>
        new(name, count, state, unit, unit is { } price ? price * count : null, reason);

    [AvaloniaFact]
    public void ValuationCellsAndTheTotalFollowTheMarketStatesAndTheTotalIsALowerBoundWhenSomeItemsHaveNoPrice()
    {
        var rows = new[]
        {
            Row("生命藥水 100", 49, GameMarketValuationState.Priced, 120), Row("染色安瓶", 12, GameMarketValuationState.NoStock),
            Row("金幣袋", 1, GameMarketValuationState.NotTradable), Row("冬夜禮服", 1, GameMarketValuationState.MultipleVariants),
            Row("藥", 3, GameMarketValuationState.NameTooShort), Row("豎琴", 1, GameMarketValuationState.Unread, reason: "gameCli.marketLocked")
        };
        var names = rows.Select(row => row.DisplayName).ToList();
        var view = ValuationMapper.Map(names, rows.ToDictionary(row => row.DisplayName), At, null);
        Assert.Equal("5,880", view.CellFor("生命藥水 100").Text);
        Assert.False(view.CellFor("生命藥水 100").Dim);
        Assert.Contains("120", view.CellFor("生命藥水 100").Tip);
        Assert.Contains("49", view.CellFor("生命藥水 100").Tip);
        Assert.Equal(["无在售", "不可交易", "多品阶", "名称太短", "未读取"],
            new[] { "染色安瓶", "金幣袋", "冬夜禮服", "藥", "豎琴" }.Select(name => view.CellFor(name).Text));
        Assert.All(new[] { "染色安瓶", "金幣袋", "冬夜禮服", "藥", "豎琴" }, name => Assert.True(view.CellFor(name).Dim));
        Assert.Contains("交易所尚未解锁", view.CellFor("豎琴").Tip);
        Assert.Equal("5,880 德卡（下限）", view.Total);
        Assert.Equal("估价：交易所价格，单位德卡，取价于 " + Local(At), view.Status);

        var all = ValuationMapper.Map(["生命藥水 100"], new Dictionary<string, GameMarketValuationRow> { ["生命藥水 100"] = rows[0] }, At, null);
        Assert.Equal("5,880 德卡", all.Total);
    }

    [AvaloniaFact]
    public void WhenNothingWasReadTheValuationShowsUnreadWithTheReasonAndNeverZero()
    {
        var view = ValuationMapper.Map(["原木", "豎琴"], new Dictionary<string, GameMarketValuationRow>(), null, "gameCli.disabled");
        Assert.Equal("估价：未读取：游戏连接器未启用（点左上角“游戏连接器”查看）", view.Status);
        Assert.Equal("未读取", view.Total);
        Assert.All(new[] { "原木", "豎琴" }, name => { Assert.Equal("未读取", view.CellFor(name).Text); Assert.True(view.CellFor(name).Dim); });
        Assert.DoesNotContain("0", view.Total);
        Assert.Equal("未读取", InventoryValuationView.Off.CellFor("原木").Text);
        Assert.False(InventoryValuationView.Off.Requested);
    }

    [AvaloniaFact]
    public void TheValuationColumnAndSurfaceFollowTheSwitchAndThePageVisibility()
    {
        var backend = new SampleBackend();
        InventoryPreference.SetValuationShown(false);
        try
        {
            using var model = new InventoryPageModel(backend, new InventoryNoteStore());
            Assert.DoesNotContain(model.Columns, column => column.Id == "valuation");
            Assert.False(backend.VisibleSurfaces.ContainsKey("inventory.valuation") && backend.VisibleSurfaces["inventory.valuation"] > 0);
            Assert.Contains(model.Groups, group => group.Id == InventoryPreference.ValuationGroup && !group.Shown);

            model.ToggleGroupCommand.Execute(InventoryPreference.ValuationGroup);
            Assert.True(InventoryPreference.ValuationShown);
            Assert.Equal(1, backend.VisibleSurfaces["inventory.valuation"]);
            var column = model.Columns.Single(item => item.Id == "valuation");
            var lute = model.Data.Rows.Single(row => row.RawName == "魯特琴");
            Assert.Equal("18,000", column.TextOf(lute));
            Assert.Equal("无在售", column.TextOf(model.Data.Rows.Single(row => row.RawName == "染色安瓶")));
            Assert.Contains("德卡", model.ValuationTotal);
            Assert.StartsWith("估价：交易所价格，单位德卡，取价于", model.ValuationLine);

            // 页面切走：停止取价；回来后继续。
            model.Deactivate();
            Assert.Equal(0, backend.VisibleSurfaces["inventory.valuation"]);
            model.Activate();
            Assert.Equal(1, backend.VisibleSurfaces["inventory.valuation"]);

            model.ToggleGroupCommand.Execute(InventoryPreference.ValuationGroup);
            Assert.False(InventoryPreference.ValuationShown);
            Assert.Equal(0, backend.VisibleSurfaces["inventory.valuation"]);
            Assert.DoesNotContain(model.Columns, item => item.Id == "valuation");
        }
        finally { InventoryPreference.SetValuationShown(false); }
    }

    [AvaloniaFact]
    public void TheValuationSwitchIsDisabledWithTheReasonWhenTheMarketCapabilityIsUnavailable()
    {
        var backend = new SampleBackend
        {
            CapabilityNotices = id => id == CapabilityIds.MarketPrices ? "暂不可用：游戏连接器未启用" : null
        };
        InventoryPreference.SetValuationShown(false);
        try
        {
            using var model = new InventoryPageModel(backend, new InventoryNoteStore());
            Assert.False(model.ToggleGroupCommand.CanExecute(InventoryPreference.ValuationGroup));
            Assert.True(model.ToggleGroupCommand.CanExecute(InventoryPreference.StorageGroup));
            Assert.Equal("估价：暂不可用：游戏连接器未启用", model.ValuationLine);
            Assert.True(model.HasValuationLine);
        }
        finally { InventoryPreference.SetValuationShown(false); }
    }

    // ---- 读取只在界面可见时进行 ----

    private static async Task<(LiveBackend Backend, CombatStubHost Stub)> ConnectAsync()
    {
        var stub = new CombatStubHost();
        var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "instance.json"),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action))
        {
            Handler = () => stub,
            Bootstrap = _ => Task.FromResult(stub.Bootstrap)
        };
        backend.Start();
        await Until(() => backend.ShownWorkspaceRevision == stub.WorkspaceRevision && backend.GetCombat().Skills.Count > 0, "连接替身后端并读到战斗数据");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        return (backend, stub);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(seconds), "等待超时：" + what);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private const string Query = "POST /api/v1/game/data/query";

    [AvaloniaFact]
    public async Task GameDataIsQueriedOnlyWhileACharacterOrOverlaySurfaceIsShownAndANotFoundHostIsNotProbedAgain()
    {
        var (backend, stub) = await ConnectAsync();
        await using var _ = backend;
        stub.GameData = new GameDataSnapshot("e", new(Fresh(), new("艾薇", "称号", "愛麗沙", 65, "魔法師", [], [], null, [])), null, null, null, null, null);
        stub.ResetCounters();
        // 没有可见使用者：不发查询。
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.Equal(0, stub.Count(Query));
        Assert.Empty(backend.WantedGameDataTopics);

        // 角色页可见：读货币（归背包页）与附近玩家（只供附近演奏字幕）以外的主题，进入页面带 Refresh；结果映射成角色页数据。
        backend.SetSurfaceVisible("character", true);
        Assert.Equal(["environment", "growth-guide", "missions", "profile"], backend.WantedGameDataTopics);
        await Until(() => stub.GameDataQueries.Count >= 1, "角色页可见后发出数据查询");
        Assert.True(stub.Count(Query) >= 1);
        var first = stub.GameDataQueries[0];
        Assert.True(first.Refresh);
        Assert.Equal(4, first.Topics.Count);
        await Until(() => backend.GetCharacter().Read == CharacterRead.Loaded, "读到角色数据");
        Assert.Equal("艾薇", backend.GetCharacter().Overview.Nickname);

        // 背包页只读货币；浮窗元件各自只要自己的主题。
        backend.SetSurfaceVisible("character", false);
        Assert.Empty(backend.WantedGameDataTopics);
        backend.SetSurfaceVisible("inventory", true);
        Assert.Equal(["currencies"], backend.WantedGameDataTopics);
        backend.SetSurfaceVisible("inventory", false);
        Assert.Empty(backend.WantedGameDataTopics);
        backend.SetSurfaceVisible("overlay.environment", true);
        Assert.Equal(["environment"], backend.WantedGameDataTopics);
        backend.SetSurfaceVisible("overlay.nearby", true);
        Assert.Equal(["environment", "nearby"], backend.WantedGameDataTopics);
        backend.SetSurfaceVisible("overlay.environment", false);
        backend.SetSurfaceVisible("overlay.nearby", false);
        Assert.Empty(backend.WantedGameDataTopics);
    }

    [AvaloniaFact]
    public async Task AHostWithoutTheDataInterfaceIsReportedAsUnsupportedAndNotProbedAgainDuringTheConnection()
    {
        var (backend, stub) = await ConnectAsync();
        await using var _ = backend;
        stub.GameData = null;
        stub.ResetCounters();
        backend.SetSurfaceVisible("character", true);
        await Until(() => backend.GetCharacter().Read == CharacterRead.Unsupported, "404 后标为后端没有数据接口");
        var count = stub.Count(Query);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.Equal(count, stub.Count(Query));
        Assert.Equal("后端没有提供角色数据", backend.GetCharacter().Status);
    }

    [AvaloniaFact]
    public void TopicIntervalsFollowTheAgreedCadenceAndMergingKeepsUnchangedTopicObjects()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), LiveBackend.DataInterval(GameDataTopics.Environment));
        Assert.Equal(TimeSpan.FromSeconds(5), LiveBackend.DataInterval(GameDataTopics.Nearby));
        Assert.Equal(TimeSpan.FromSeconds(30), LiveBackend.DataInterval(GameDataTopics.Profile));
        Assert.Equal(TimeSpan.FromSeconds(30), LiveBackend.DataInterval(GameDataTopics.Currencies));
        Assert.Equal(TimeSpan.FromSeconds(30), LiveBackend.DataInterval(GameDataTopics.Missions));
        Assert.Equal(TimeSpan.FromSeconds(60), LiveBackend.DataInterval(GameDataTopics.GrowthGuide));

        var environment = new GameEnvironmentData(Fresh(), new("庫漢", "庫漢", 1, 2, "Sunny", "2968-6-08 13:13", null));
        var old = new GameDataSnapshot("e", null, null, null, null, environment, null);
        var same = new GameDataSnapshot("e", null, null, null, null, environment with { }, null);
        Assert.Same(old, LiveBackend.Merge(old, same));
        var changed = LiveBackend.Merge(old, same with { Environment = environment with { Value = environment.Value! with { Weather = "Rain" } } });
        Assert.NotSame(old, changed);
        Assert.Equal("Rain", changed.Environment!.Value!.Weather);
        // 本次没有请求的主题沿用旧内容。
        var onlyNearby = LiveBackend.Merge(old, new("e", null, null, null, null, null, new(Fresh(), [])));
        Assert.Same(environment, onlyNearby.Environment);
        Assert.NotNull(onlyNearby.Nearby);

        // 读取失败：请求过的主题已有的内容标为旧数据，没有内容的主题仍为空。
        var stale = LiveBackend.MarkStale(old, [GameDataTopics.Environment, GameDataTopics.Profile])!;
        Assert.True(stale.Environment!.Status.Stale);
        Assert.Equal("gameCli.readFailed", stale.Environment.Status.Reason);
        Assert.Null(stale.Profile);
        Assert.Null(LiveBackend.MarkStale(null, [GameDataTopics.Environment]));
    }
}
