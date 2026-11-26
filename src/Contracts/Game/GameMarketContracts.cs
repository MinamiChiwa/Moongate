using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>
/// 价目查询：名称（每个至少 2 个字符，可多个）、分类（可带子分类）或收藏三种模式只能用一种。
/// 价格单位为德卡；查价只读，不提供购买、交易或点击游戏的能力。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameMarketPriceQuery(IReadOnlyList<string>? Names = null, string? Category = null, string? SubCategory = null, bool Favorites = false);

/// <summary>一条交易所记录；<see cref="MinUnitPrice"/> 只在 <see cref="InStock"/> 为真时有值。</summary>
public sealed record GameMarketRow(string DisplayName, string? Rarity, int? Star, string? Season, string? MainAttributeRange, bool Favorite,
    bool InStock, double? MinUnitPrice);

/// <summary>名称模式下一个名称的结果；<see cref="Matched"/> 为假表示没有可交易的道具匹配该名称。</summary>
public sealed record GameMarketNameResult(string Query, bool Matched, IReadOnlyList<GameMarketRow> Rows);

/// <summary>
/// 价目查询结果。<see cref="Mode"/> 为 names、category 或 favorites；名称模式的结果在 <see cref="Results"/>，分类与收藏模式的记录在 <see cref="Rows"/>。
/// <see cref="Reason"/> 为空表示查询成功，否则是原因码（连接器原因码，或 <c>gameCli.nameTooShort</c>、<c>gameCli.categoryNotFound</c>、
/// <c>gameCli.marketLocked</c>、<c>gameCli.marketUnavailable</c>、<c>gameCli.rateLimited</c>），此时没有价格；分类不存在时 <see cref="Candidates"/> 是游戏给出的候选名。
/// <see cref="Cached"/> 为真表示 5 秒内的相同请求直接返回了缓存，<see cref="ReadAt"/> 是取价时刻。
/// </summary>
public sealed record GameMarketPrices(string BackendEpoch, string? Mode, string? Reason, string? Message, int? RetryAfterSeconds,
    IReadOnlyList<string> Candidates, DateTimeOffset? ReadAt, bool Cached, string? Category, string? SubCategory,
    IReadOnlyList<GameMarketNameResult> Results, IReadOnlyList<GameMarketRow> Rows);

/// <summary>明细查询：<see cref="DisplayName"/> 取自价目的一行，品阶、星级、赛季用来区分同名记录；<see cref="Quantity"/> 给出时计算购买成本。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameMarketDetailQuery(string DisplayName, string? Rarity = null, int? Star = null, string? Season = null, int? Quantity = null);

/// <summary>一档价位：该单价只适用于 <see cref="Count"/> 件。</summary>
public sealed record GameMarketTier(double UnitPrice, int Count);

/// <summary>
/// 按数量计算的购买成本：从低价档起取 <see cref="PricedCount"/> 件，小计 <see cref="PricedSubtotal"/>；超出三档数量的 <see cref="UnpricedCount"/> 件成本未知，
/// 此时 <see cref="IsLowerBound"/> 为真，小计只是下限，不得把单价乘以超出可用数量的件数。
/// </summary>
public sealed record GameMarketCost(int Quantity, double PricedSubtotal, int PricedCount, int UnpricedCount, bool IsLowerBound);

/// <summary>价格明细：最低与最高挂单价在卖空时为空，<see cref="PriceTiers"/> 至多三档（市场不报告更多）。</summary>
public sealed record GameMarketDetail(string DisplayName, string? Rarity, int? Star, string? Season, string? MainAttributeRange,
    double? MinAskUnitPrice, double? MaxAskUnitPrice, double? AverageUnitPrice, IReadOnlyList<GameMarketTier> PriceTiers);

/// <summary>
/// 明细查询结果。<see cref="Reason"/> 为空表示成功；<c>gameCli.rateLimited</c> 时 <see cref="RetryAfterSeconds"/> 是游戏要求等待的秒数（后端不自动重发）；
/// <c>gameCli.ambiguousItem</c> 时 <see cref="Candidates"/> 是候选记录。
/// </summary>
public sealed record GameMarketPriceDetail(string BackendEpoch, string? Reason, string? Message, int? RetryAfterSeconds,
    IReadOnlyList<GameMarketRow> Candidates, DateTimeOffset? ReadAt, GameMarketDetail? Detail, GameMarketCost? Cost);

/// <summary>要估价的道具：名称取背包里的道具名称原文，<see cref="Count"/> 是持有数量。</summary>
public sealed record GameMarketValuationItem(string DisplayName, int Count);

/// <summary>批量估价，一次最多 200 项。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameMarketValuationQuery(IReadOnlyList<GameMarketValuationItem> Items);

/// <summary>
/// 估价状态：Priced 已估价；NoStock 交易所有该道具但没有在售；NotTradable 交易所没有该名称；
/// MultipleVariants 同名对应多个品阶或星级的记录，不猜测品阶；NameTooShort 名称不足 2 个字符；Unread 没有读到（原因见 <see cref="GameMarketValuationRow.Reason"/>）。
/// </summary>
public enum GameMarketValuationState { Priced, NoStock, NotTradable, MultipleVariants, NameTooShort, Unread }

public sealed record GameMarketValuationRow(string DisplayName, int Count, OpenEnum<GameMarketValuationState> State, double? UnitPrice,
    double? Total, string? Reason);

/// <summary>估价结果：<see cref="PricedAt"/> 是最早的取价时刻（缓存项取其读取时刻），全部项未读取时为空；<see cref="Reason"/> 是整体读不到的原因。</summary>
public sealed record GameMarketValuation(string BackendEpoch, DateTimeOffset? PricedAt, string? Reason, IReadOnlyList<GameMarketValuationRow> Rows);
