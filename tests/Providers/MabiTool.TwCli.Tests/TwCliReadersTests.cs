using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

/// <summary>读取类指令的响应映射。样例按 2026-11-25 实测响应的字段形状编写（数值与名称为虚构），不是在线数据。</summary>
public sealed class TwCliReadersTests
{
    private static JsonElement? Json(string text) => TwCliResponse.TryParse(text);

    private const string MyInfo = """
        {"Title":"測試稱號","RealmName":"測試伺服器","Level":60,"EnabledCombatJobDisplayName":"魔法師",
         "CombatScore":{"DisplayName":"戰鬥力","Value":30000},"STR":{"DisplayName":"力量","Value":2000},
         "NewStat":{"DisplayName":"新屬性","Value":7},
         "PaladinStats":{"JusticePower":{"DisplayName":"正義","Value":0}},
         "Vitals":{"HealthCurrent":24000,"HealthMax":24086,"ShieldAmount":0,"ShieldMax":0,"SatietyValue":0,"SatietyMax":100,"SatietyRatio":0.0,"InventoryWeightCurrent":696.387634,"InventoryWeightMax":1110.0,"ActiveBuffCount":1},
         "LivingSkills":[{"DisplayName":"伐木","Level":28,"Exp":19378,"MaxExp":21040},{"DisplayName":"鍊金術","Level":10}]}
        """;

    [Fact]
    public void ProfileMapsRealmNameToTheServerKeepsTheTitleAndHasNoNicknameAndReadsEveryStatObject()
    {
        var profile = TwCliReaders.Profile(Json(MyInfo))!;
        Assert.Equal(("測試稱號", "測試伺服器", 60, "魔法師"), (profile.Title, profile.ServerName, profile.Level, profile.CombatJob));
        Assert.Null(profile.Nickname);
        Assert.Equal(["CombatScore", "STR", "NewStat"], profile.Stats.Select(x => x.Key));
        Assert.Equal(("力量", 2000d), (profile.Stats[1].DisplayName, profile.Stats[1].Value));
        Assert.Equal("正義", Assert.Single(profile.PaladinStats).DisplayName);
        Assert.Equal((24000d, 696.387634, 1), (profile.Vitals!.HealthCurrent, profile.Vitals.InventoryWeightCurrent, profile.Vitals.ActiveBuffCount));
        // 满级的生活技能没有经验字段：为空，不是 0。
        var maxed = profile.LivingSkills[1];
        Assert.Equal(("鍊金術", 10, (int?)null, (int?)null), (maxed.DisplayName, maxed.Level, maxed.Exp, maxed.MaxExp));
    }

    [Fact]
    public void MissingFieldsStayEmptyAndWrongShapesAreRejected()
    {
        var sparse = TwCliReaders.Profile(Json("""{"RealmName":"A","Vitals":{"HealthMax":5}}"""))!;
        Assert.Null(sparse.Title);
        Assert.Null(sparse.Level);
        Assert.Null(sparse.Vitals!.HealthCurrent);
        Assert.Empty(sparse.LivingSkills);
        Assert.Null(TwCliReaders.Profile(Json("[]")));
        Assert.Null(TwCliReaders.Missions(Json("""{"x":1}""")));
        Assert.Null(TwCliReaders.Nearby(Json("""{"x":1}""")));
        Assert.Empty(TwCliReaders.Nearby(Json("[]"))!);
    }

    [Fact]
    public void EnvironmentKeepsTheInGameTimeAsText()
    {
        var environment = TwCliReaders.Environment(Json("""
            {"ChannelDisplayName":"頻道一","GameSpaceDisplayName":"庫漢","WorldPosition":{"X":12.5,"Y":52.9},"Weather":"Sunny","ErinnNow":"2968-6-08 13:13",
             "Housing":{"IsInHousing":false,"IsOwnedHousing":false,"CanEnterHousing":true,"CannotEnterHousingReason":"","CanExitHousing":false}}
            """))!;
        Assert.Equal(("頻道一", "庫漢", "Sunny", "2968-6-08 13:13"), (environment.Channel, environment.Area, environment.Weather, environment.ErinnTime));
        Assert.Equal((12.5, 52.9, true), (environment.X, environment.Y, environment.Housing!.CanEnterHousing));
    }

    [Fact]
    public void NearbyPlayersCarryPerformanceOnlyWhenTheGameReportsIt()
    {
        var players = TwCliReaders.Nearby(Json("""
            [{"RealmName":"甲","Title":"稱號甲","Distance":12.5,"Level":50,"EnabledCombatJobDisplayName":"弓箭手","IsFriend":true,"IsInParty":false,
              "Performance":{"IsPlaying":true,"MusicTitle":"少年冒險家","IsCopyingAllowed":false,"IsLoop":true,"TotalDurationSeconds":120,"ElapsedSeconds":30,"RemainingSeconds":90,"ChannelCount":3}},
             {"RealmName":"乙","Performance":{"IsPlaying":false}},{"RealmName":"丙"}]
            """))!;
        Assert.Equal(3, players.Count);
        Assert.Equal(("少年冒險家", 3, true), (players[0].Performance!.MusicTitle, players[0].Performance!.ChannelCount, players[0].Performance!.IsPlaying));
        Assert.False(players[1].Performance!.IsPlaying);
        Assert.Null(players[1].Performance!.MusicTitle);
        Assert.Null(players[2].Performance);
        Assert.Equal(("稱號甲", "甲"), (players[0].Title, players[0].ServerName));
    }

    [Fact]
    public void MissionsReadThroughTheAcceptedBodyWrapperAndRejectionsAreNotData()
    {
        var accepted = TwCliResponse.Classify("get_daily_missions", new(0, """
            {"status":"accepted","body":[{"Title":"討伐","Description":"d","CurrentCount":2,"GoalCount":5,"IsCompleted":false,"IsRewardReceived":false,"HasShortcut":true},
              {"Title":"採集","CurrentCount":3,"GoalCount":3,"IsCompleted":true,"IsRewardReceived":false,"HasShortcut":false}]}
            """, "", false, TimeSpan.Zero));
        var missions = TwCliReaders.Missions(accepted.Body)!;
        Assert.Equal(2, missions.Count);
        Assert.Equal((2, 5, false, true), (missions[0].CurrentCount, missions[0].GoalCount, missions[0].IsCompleted, missions[0].HasShortcut));
        Assert.True(missions[1].IsCompleted);
        Assert.False(missions[1].IsRewardReceived);

        var rejected = TwCliResponse.Classify("get_daily_missions", new(0, """{"status":"rejected","body":{"error":"not_ready","message":"m"}}""", "", false, TimeSpan.Zero));
        Assert.Equal((TwCliResultKind.Rejected, "not_ready"), (rejected.Kind, rejected.Error));
    }

    [Fact]
    public void GrowthGuideLockedRejectionCarriesTheUnlockHint()
    {
        var locked = TwCliResponse.Classify("get_growth_guide", new(0,
            """{"status":"rejected","body":{"error":"guide_locked","message":"locked","unlockHint":"等級 30 解鎖"}}""", "", false, TimeSpan.Zero));
        Assert.Equal("guide_locked", locked.Error);
        Assert.Equal("等級 30 解鎖", TwCliReaders.UnlockHint(locked.Root));
        var open = TwCliReaders.GrowthGuide(Json("""
            {"Contents":[{"Target":"每日地城","DayType":"Daily","IsUnlocked":true,"IsCompleted":false,"RemainingCount":1},
                         {"Target":"深淵","DayType":"Weekly","IsUnlocked":false,"UnlockHint":"等級 50"}]}
            """))!;
        Assert.Equal((true, false, 1), (open[0].IsUnlocked, open[0].IsCompleted, open[0].RemainingCount));
        Assert.Equal((false, (bool?)null, (int?)null, "等級 50"), (open[1].IsUnlocked, open[1].IsCompleted, open[1].RemainingCount, open[1].UnlockHint));
    }

    [Fact]
    public void CurrenciesKeepTheGameNamesAndMissingAmountsStayEmpty()
    {
        var currencies = TwCliReaders.Currencies(Json("""[{"DisplayName":"金幣","Amount":12345},{"DisplayName":"銀幣"}]"""))!;
        Assert.Equal(("金幣", 12345d), (currencies[0].DisplayName, currencies[0].Amount));
        Assert.Null(currencies[1].Amount);
    }

    [Fact]
    public void MarketPricesReadNameResultsRowsAndDetailTiersInPriceOrder()
    {
        var names = TwCliReaders.MarketNames(Json("""
            {"Results":[{"Query":"原木","Matched":true,"Rows":[{"DisplayName":"原木","Favorite":false,"InStock":true,"MinUnitPrice":35},{"DisplayName":"堅硬原木","InStock":false}]},
                        {"Query":"不存在","Matched":false,"Rows":[]}]}
            """))!;
        Assert.Equal((true, 2, 35d), (names[0].Matched, names[0].Rows.Count, names[0].Rows[0].MinUnitPrice));
        Assert.Null(names[0].Rows[1].MinUnitPrice);
        Assert.False(names[1].Matched);
        var rows = TwCliReaders.MarketRows(Json("""{"Category":"材料","SubCategory":"木材","Rows":[{"DisplayName":"符文","Rarity":"Rare","Star":2,"Season":3,"InStock":true,"MinUnitPrice":100}]}"""))!;
        Assert.Equal(("Rare", 2, "3"), (rows[0].Rarity, rows[0].Star, rows[0].Season));
        var detail = TwCliReaders.MarketDetail(Json("""
            {"DisplayName":"原木","MinAskUnitPrice":35,"MaxAskUnitPrice":60,"AverageUnitPrice":40.5,"PriceTiers":[{"UnitPrice":40,"Count":5},{"UnitPrice":35,"Count":3}]}
            """))!;
        Assert.Equal([35d, 40d], detail.PriceTiers.Select(x => x.UnitPrice));
        Assert.Equal((35d, 60d, 40.5), (detail.MinAskUnitPrice, detail.MaxAskUnitPrice, detail.AverageUnitPrice));
        Assert.Null(TwCliReaders.MarketDetail(Json("""{"MinAskUnitPrice":1}""")));
    }

    [Fact]
    public void RejectedMarketResponsesExposeRetryAfterAndCandidates()
    {
        var limited = TwCliResponse.Classify("get_market_price_detail", new(0,
            """{"status":"rejected","body":{"error":"rate_limited","message":"slow","retryAfterSeconds":2.4}}""", "", false, TimeSpan.Zero));
        Assert.Equal(("rate_limited", 3), (limited.Error, TwCliReaders.RetryAfterSeconds(limited.Root)));
        var ambiguous = TwCliResponse.Classify("get_market_price_detail", new(0,
            """{"error":"ambiguous_item","candidates":[{"DisplayName":"符文","Rarity":"Rare","Star":1,"InStock":true,"MinUnitPrice":9},"符文石"]}""", "", false, TimeSpan.Zero));
        Assert.Equal(["符文", "符文石"], TwCliReaders.Candidates(ambiguous.Root));
        Assert.Equal(("Rare", 1), (TwCliReaders.CandidateRows(ambiguous.Root)[0].Rarity, TwCliReaders.CandidateRows(ambiguous.Root)[0].Star));
        Assert.Null(TwCliReaders.RetryAfterSeconds(ambiguous.Root));
        Assert.Equal("gameCli.rateLimited", TwCliCodes.FromGame(limited.Error));
    }

    [Theory]
    [InlineData("Market board prices are refreshed from the server at most once every 5 seconds and served from that snapshot", 1, 5)]
    [InlineData("Lookups are limited to one per 3 seconds; a call within that window is rejected", 1, 3)]
    [InlineData("no cooldown mentioned", 7, 7)]
    [InlineData(null, 2, 2)]
    public void CooldownIsReadFromTheCatalogNoteOrFallsBack(string? note, int fallback, int expected) =>
        Assert.Equal(expected, TwCliReaders.CooldownSeconds(note, fallback));
}
