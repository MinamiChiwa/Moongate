using MabiTool.Contracts;

namespace MabiTool.Host.GameCli;

/// <summary>游戏数据读取与交易所查价的只读接口：查询本身就是读取需求，没有查询时不调用命令行。</summary>
public static class GameDataEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost(GameDataApi.Query.Path, async (GameDataQuery request, GameDataService data, CancellationToken ct) =>
            Results.Ok(await data.QueryAsync(request, ct))).WithMetadata(GameDataApi.Query);
        app.MapPost(GameDataApi.MarketPrices.Path, async (GameMarketPriceQuery request, GameMarketService market, CancellationToken ct) =>
            Results.Ok(await market.PricesAsync(request, ct))).WithMetadata(GameDataApi.MarketPrices);
        app.MapPost(GameDataApi.MarketPriceDetail.Path, async (GameMarketDetailQuery request, GameMarketService market, CancellationToken ct) =>
            Results.Ok(await market.DetailAsync(request, ct))).WithMetadata(GameDataApi.MarketPriceDetail);
        app.MapPost(GameDataApi.MarketValuation.Path, async (GameMarketValuationQuery request, GameMarketService market, CancellationToken ct) =>
            Results.Ok(await market.ValuationAsync(request, ct))).WithMetadata(GameDataApi.MarketValuation);
    }
}
