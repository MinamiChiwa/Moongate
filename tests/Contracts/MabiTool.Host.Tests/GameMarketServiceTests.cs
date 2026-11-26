using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.GameCli;
using MabiTool.Infrastructure;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class GameMarketServiceTests
{
    private static readonly string[] Commands = ["get_market_prices", "get_market_price_detail"];

    private static string Names(params string[] results) => "{\"Results\":[" + string.Join(",", results) + "]}";
    private static string Result(string query, bool matched, params string[] rows) =>
        "{\"Query\":\"" + query + "\",\"Matched\":" + (matched ? "true" : "false") + ",\"Rows\":[" + string.Join(",", rows) + "]}";
    private static string Row(string name, bool inStock, double? price = null, string extra = "") =>
        "{\"DisplayName\":\"" + name + "\",\"Favorite\":false,\"InStock\":" + (inStock ? "true" : "false") +
        (price is { } p ? ",\"MinUnitPrice\":" + p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + extra + "}";

    private static async Task<(ConnectedRig Rig, GameMarketService Market)> ConnectedAsync()
    {
        var rig = new ConnectedRig();
        await rig.ConnectAsync(Commands);
        return (rig, new GameMarketService(rig.Service, "epoch"));
    }

    [Fact]
    public async Task InvalidModesAndShortNamesNeverReachTheCommandLine()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            foreach (var query in new GameMarketPriceQuery[] { new(), new(["原木"], "材料"), new(null, null, null, false), new(null, null, "子分類"), new(["原木"], null, null, true) })
                Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => market.PricesAsync(query, default))).Code);
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => market.PricesAsync(new(Enumerable.Repeat("原木", 21).ToArray()), default))).Code);
            var tooShort = await market.PricesAsync(new(["原木", " 木 "]), default);
            Assert.Equal(("gameCli.nameTooShort", "names"), (tooShort.Reason, tooShort.Mode));
            Assert.Empty(tooShort.Results);
            Assert.DoesNotContain("get_market_prices", rig.Runner.Commands);
        }
    }

    [Fact]
    public async Task PriceQueriesAreSentAsExactlyOneModeAndRepeatedQueriesInsideTheCooldownAreServedFromTheCache()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok(Names(Result("原木", true, Row("原木", true, 35), Row("堅硬原木", false))));
            var first = await market.PricesAsync(new(["原木"]), default);
            Assert.Null(first.Reason);
            Assert.False(first.Cached);
            Assert.Equal(("原木", true, 35d), (first.Results[0].Query, first.Results[0].Matched, first.Results[0].Rows[0].MinUnitPrice));
            Assert.Null(first.Results[0].Rows[1].MinUnitPrice);
            var second = await market.PricesAsync(new(["原木"]), default);
            Assert.True(second.Cached);
            Assert.Equal(first.ReadAt, second.ReadAt);
            Assert.Equal(1, rig.Runner.CountOf("get_market_prices"));
            Assert.Equal("{\"names\":[\"原木\"]}", DataRunner.BodyOf(rig.Runner.Calls.Single(x => x[0] == "get_market_prices")));

            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok("{\"Category\":\"材料\",\"SubCategory\":\"木材\",\"Rows\":[" + Row("原木", true, 35) + "]}");
            var category = await market.PricesAsync(new(null, "材料", "木材"), default);
            Assert.Equal(("category", "材料", "木材", 1), (category.Mode, category.Category, category.SubCategory, category.Rows.Count));
            Assert.Equal("{\"category\":\"材料\",\"subCategory\":\"木材\"}", DataRunner.BodyOf(rig.Runner.Calls.Last(x => x[0] == "get_market_prices")));
            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok("{\"Rows\":[]}");
            var favorites = await market.PricesAsync(new(Favorites: true), default);
            Assert.Equal(("favorites", 0), (favorites.Mode, favorites.Rows.Count));
            Assert.Equal("{\"favorites\":true}", DataRunner.BodyOf(rig.Runner.Calls.Last(x => x[0] == "get_market_prices")));
        }
    }

    [Fact]
    public async Task CooldownsComeFromTheCatalogNotesWithDefaultsWhenAbsent()
    {
        using var rig = new ConnectedRig();
        Assert.Equal((5, 1), new GameMarketService(rig.Service, "epoch").Cooldowns());
        rig.Notes["get_market_prices"] = "Market board prices are refreshed from the server at most once every 8 seconds.";
        rig.Notes["get_market_price_detail"] = "Lookups are limited to one per 2 seconds; a call within that window is rejected.";
        await rig.ConnectAsync(Commands);
        Assert.Equal((8, 2), new GameMarketService(rig.Service, "epoch").Cooldowns());
    }

    [Fact]
    public async Task RejectionsAreReturnedAsReasonsWithCandidatesAndNoPrices()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok("""{"status":"rejected","body":{"error":"category_not_found","message":"m","candidates":["材料","裝備"]}}""");
            var missing = await market.PricesAsync(new(null, "不存在"), default);
            Assert.Equal("gameCli.categoryNotFound", missing.Reason);
            Assert.Equal(["材料", "裝備"], missing.Candidates);
            Assert.Empty(missing.Rows);
            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok("""{"status":"rejected","body":{"error":"market_locked","message":"m"}}""");
            Assert.Equal("gameCli.marketLocked", (await market.PricesAsync(new(["原木"]), default)).Reason);
            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok("""{"error":"market_unavailable"}""");
            Assert.Equal("gameCli.marketUnavailable", (await market.PricesAsync(new(null, "材料"), default)).Reason);
            // 失败不进缓存：之后恢复时立即可读。
            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok("{\"Rows\":[]}");
            Assert.Null((await market.PricesAsync(new(null, "材料"), default)).Reason);
        }
    }

    [Fact]
    public void CostFromThreeTiersIsALowerBoundWhenTheQuantityExceedsThem()
    {
        GameMarketTier[] tiers = [new(12, 3), new(10, 2), new(15, 4)];
        var exact = GameMarketService.Cost(tiers, 4);
        Assert.Equal((4, 44d, 4, 0, false), (exact.Quantity, exact.PricedSubtotal, exact.PricedCount, exact.UnpricedCount, exact.IsLowerBound));
        var over = GameMarketService.Cost(tiers, 12);
        // 只给三档共 9 件的小计 2×10+3×12+4×15 = 116；其余 3 件成本未知，不乘以档位单价。
        Assert.Equal((12, 116d, 9, 3, true), (over.Quantity, over.PricedSubtotal, over.PricedCount, over.UnpricedCount, over.IsLowerBound));
        var none = GameMarketService.Cost([], 5);
        Assert.Equal((0d, 0, 5, true), (none.PricedSubtotal, none.PricedCount, none.UnpricedCount, none.IsLowerBound));
    }

    [Fact]
    public async Task DetailReturnsTiersAndCostAndSendsOnlyTheGivenSelectors()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            rig.Runner.Handlers["get_market_price_detail"] = _ => DataRunner.Ok("""
                {"DisplayName":"符文","Rarity":"Rare","Star":1,"MinAskUnitPrice":10,"MaxAskUnitPrice":30,"AverageUnitPrice":14.5,"PriceTiers":[{"UnitPrice":10,"Count":2},{"UnitPrice":12,"Count":3}]}
                """);
            var detail = await market.DetailAsync(new("符文", "Rare", 1, null, 8), default);
            Assert.Null(detail.Reason);
            Assert.Equal((10d, 30d, 2), (detail.Detail!.MinAskUnitPrice, detail.Detail.MaxAskUnitPrice, detail.Detail.PriceTiers.Count));
            Assert.Equal((56d, 5, 3, true), (detail.Cost!.PricedSubtotal, detail.Cost.PricedCount, detail.Cost.UnpricedCount, detail.Cost.IsLowerBound));
            using var body = JsonDocument.Parse(DataRunner.BodyOf(rig.Runner.Calls.Single(x => x[0] == "get_market_price_detail")));
            Assert.Equal(["displayName", "rarity", "star"], body.RootElement.EnumerateObject().Select(x => x.Name));
            var noQuantity = await market.DetailAsync(new("符文"), default);
            Assert.Null(noQuantity.Cost);
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => market.DetailAsync(new(" "), default))).Code);
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => market.DetailAsync(new("符文", Quantity: 0), default))).Code);
        }
    }

    [Fact]
    public async Task RateLimitedDetailReturnsTheWaitAndIsNeverResent()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            rig.Runner.Handlers["get_market_price_detail"] = _ => DataRunner.Ok("""{"status":"rejected","body":{"error":"rate_limited","message":"m","retryAfterSeconds":3}}""");
            var limited = await market.DetailAsync(new("符文"), default);
            Assert.Equal("gameCli.rateLimited", limited.Reason);
            Assert.Equal(3, limited.RetryAfterSeconds);
            Assert.Null(limited.Detail);
            await Task.Delay(200);
            Assert.Equal(1, rig.Runner.CountOf("get_market_price_detail"));
            rig.Runner.Handlers["get_market_price_detail"] = _ => DataRunner.Ok("""{"error":"ambiguous_item","candidates":[{"DisplayName":"符文","Rarity":"Rare","Star":1,"InStock":true,"MinUnitPrice":9},{"DisplayName":"符文","Rarity":"Epic","Star":2,"InStock":false}]}""");
            var ambiguous = await market.DetailAsync(new("符文"), default);
            Assert.Equal(("gameCli.ambiguousItem", 2), (ambiguous.Reason, ambiguous.Candidates.Count));
            Assert.Equal(["Rare", "Epic"], ambiguous.Candidates.Select(x => x.Rarity));
        }
    }

    [Fact]
    public async Task ConsecutiveDetailCallsAreSpacedByTheCooldownInsteadOfBeingRejectedByTheGame()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            rig.Runner.Handlers["get_market_price_detail"] = _ => DataRunner.Ok("""{"DisplayName":"符文","PriceTiers":[]}""");
            var watch = Stopwatch.StartNew();
            await market.DetailAsync(new("符文"), default);
            await market.DetailAsync(new("符文"), default);
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), watch.Elapsed.ToString());
            Assert.Equal(2, rig.Runner.CountOf("get_market_price_detail"));
        }
    }

    [Fact]
    public async Task ValuationPricesOnlyExactSingleRecordsAndExplainsEveryOtherState()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok(Names(
                Result("\\\"原木\\\"", true, Row("原木", true, 35)),
                Result("\\\"堅硬原木\\\"", true, Row("堅硬原木", false)),
                Result("\\\"神秘物品\\\"", false),
                Result("\\\"符文\\\"", true, Row("符文", true, 10, ",\"Rarity\":\"Rare\",\"Star\":1"), Row("符文", true, 40, ",\"Rarity\":\"Epic\",\"Star\":2"))));
            var valuation = await market.ValuationAsync(new([new("原木", 12), new("堅硬原木", 3), new("神秘物品", 1), new("符文", 5), new("木", 4)]), default);
            Assert.Null(valuation.Reason);
            Assert.NotNull(valuation.PricedAt);
            var rows = valuation.Rows.ToDictionary(x => x.DisplayName);
            Assert.Equal((GameMarketValuationState.Priced, 35d, 420d), (rows["原木"].State.KnownValue!.Value, rows["原木"].UnitPrice, rows["原木"].Total));
            Assert.Equal((GameMarketValuationState.NoStock, (double?)null), (rows["堅硬原木"].State.KnownValue!.Value, rows["堅硬原木"].Total));
            Assert.Equal(GameMarketValuationState.NotTradable, rows["神秘物品"].State.KnownValue);
            Assert.Equal((GameMarketValuationState.MultipleVariants, (double?)null), (rows["符文"].State.KnownValue!.Value, rows["符文"].Total));
            Assert.Equal(("gameCli.nameTooShort", GameMarketValuationState.NameTooShort), (rows["木"].Reason, rows["木"].State.KnownValue!.Value));
            // 名称加双引号精确匹配，名称不足 2 个字符的不发给游戏。
            Assert.Equal("{\"names\":[\"\\\"原木\\\"\",\"\\\"堅硬原木\\\"\",\"\\\"神秘物品\\\"\",\"\\\"符文\\\"\"]}", DataRunner.BodyOf(rig.Runner.Calls.Single(x => x[0] == "get_market_prices")));

            // 60 秒内同名取价结果复用，不再调用命令行。
            var again = await market.ValuationAsync(new([new("原木", 20)]), default);
            Assert.Equal(700d, again.Rows[0].Total);
            Assert.Equal(1, rig.Runner.CountOf("get_market_prices"));
        }
    }

    [Fact]
    public async Task ValuationWithoutPricesIsUnreadWithTheReasonNeverZero()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            rig.Runner.Handlers["get_market_prices"] = _ => DataRunner.Ok("""{"status":"rejected","body":{"error":"market_locked","message":"m"}}""");
            var locked = await market.ValuationAsync(new([new("原木", 12)]), default);
            Assert.Equal("gameCli.marketLocked", locked.Reason);
            Assert.Null(locked.PricedAt);
            var row = Assert.Single(locked.Rows);
            Assert.Equal((GameMarketValuationState.Unread, "gameCli.marketLocked", (double?)null), (row.State.KnownValue!.Value, row.Reason, row.Total));
            await rig.DisconnectAsync();
            var expired = await market.ValuationAsync(new([new("堅硬原木", 1)]), default);
            Assert.Equal("gameCli.expired", expired.Reason);
        }
        using var offline = new ConnectedRig();
        await offline.Service.RefreshAsync(true);
        var disabled = await new GameMarketService(offline.Service, "epoch").ValuationAsync(new([new("原木", 1)]), default);
        Assert.Equal(("gameCli.disabled", GameMarketValuationState.Unread), (disabled.Reason, disabled.Rows[0].State.KnownValue!.Value));
        Assert.Empty(offline.Runner.Commands);
    }

    [Fact]
    public async Task ValuationSplitsNamesIntoChunksAndRejectsOversizedRequests()
    {
        var (rig, market) = await ConnectedAsync();
        using (rig)
        {
            rig.Runner.Handlers["get_market_prices"] = args =>
            {
                using var body = JsonDocument.Parse(DataRunner.BodyOf(args));
                return DataRunner.Ok(Names([.. body.RootElement.GetProperty("names").EnumerateArray().Select(x =>
                    Result(x.GetString()!.Replace("\"", "\\\""), true, Row(x.GetString()!.Trim('"'), true, 5)))]));
            };
            var items = Enumerable.Range(0, 65).Select(i => new GameMarketValuationItem("道具" + i, 2)).ToArray();
            var valuation = await market.ValuationAsync(new(items), default);
            Assert.Equal(3, rig.Runner.CountOf("get_market_prices"));
            Assert.All(valuation.Rows, row => Assert.Equal((GameMarketValuationState.Priced, 10d), (row.State.KnownValue!.Value, row.Total)));
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => market.ValuationAsync(new([]), default))).Code);
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() =>
                market.ValuationAsync(new(Enumerable.Range(0, 201).Select(i => new GameMarketValuationItem("道具" + i, 1)).ToArray()), default))).Code);
        }
    }
}
