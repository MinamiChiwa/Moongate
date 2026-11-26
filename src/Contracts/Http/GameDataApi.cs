namespace MabiTool.Contracts;

/// <summary>游戏连接器的数据读取与交易所查价接口（只读）；查询本身就是读取需求，后端不在没有查询时调用命令行。</summary>
public static class GameDataApi
{
    public static EndpointDefinition Query { get; } = new("/api/v1/game/data/query", "queryGameData", typeof(GameDataSnapshot),
        Method: "POST", RequestType: typeof(GameDataQuery), Permission: ApiPermissions.GameObserve);
    public static EndpointDefinition MarketPrices { get; } = new("/api/v1/game/market/prices", "queryGameMarketPrices", typeof(GameMarketPrices),
        Method: "POST", RequestType: typeof(GameMarketPriceQuery), Permission: ApiPermissions.GameObserve);
    public static EndpointDefinition MarketPriceDetail { get; } = new("/api/v1/game/market/price-detail", "queryGameMarketPriceDetail", typeof(GameMarketPriceDetail),
        Method: "POST", RequestType: typeof(GameMarketDetailQuery), Permission: ApiPermissions.GameObserve);
    public static EndpointDefinition MarketValuation { get; } = new("/api/v1/game/market/valuation", "queryGameMarketValuation", typeof(GameMarketValuation),
        Method: "POST", RequestType: typeof(GameMarketValuationQuery), Permission: ApiPermissions.GameObserve, MaximumRequestBytes: 256 * 1024);

    public static IReadOnlyList<EndpointDefinition> Endpoints { get; } = Array.AsReadOnly<EndpointDefinition>([Query, MarketPrices, MarketPriceDetail, MarketValuation]);
}
