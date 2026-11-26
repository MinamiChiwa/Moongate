using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.GameCli;

/// <summary>
/// 交易所只读查价：价目（名称、分类、收藏）、明细（至多三档价位与按数量的成本下限）、背包批量估价（最低单价乘持有数量）。
/// 冷却取自目录备注（读不到时价目 5 秒、明细 1 秒），游戏返回 <c>rate_limited</c> 时把 <c>retryAfterSeconds</c> 交给界面，不自动重发；
/// 所有调用以数据读取优先级进入通道，不挤占演奏与合奏的指令。不提供任何购买、交易或点击游戏的能力。
/// </summary>
public sealed class GameMarketService(GameConnectorService connector, string backendEpoch, TimeProvider? time = null)
{
    public const int MaximumNames = 20, MaximumValuationItems = 200, ValuationChunk = 30, MaximumNameLength = 64, MinimumNameLength = 2;
    private static readonly TimeSpan ValuationLifetime = TimeSpan.FromSeconds(60);
    private const int DefaultPricesCooldown = 5, DefaultDetailCooldown = 1, MaximumCache = 64;

    private sealed record CachedPrices(long Stamp, GameMarketPrices Value);
    private sealed record CachedValuation(long Stamp, DateTimeOffset At, GameMarketValuationState State, double? UnitPrice);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, CachedPrices> prices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedValuation> valuations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim detailGate = new(1, 1), valuationGate = new(1, 1);
    private long lastDetail;
    private bool hasDetail;

    /// <summary>价目与明细的冷却秒数（目录备注里的数值，读不到用默认值）。</summary>
    public (int Prices, int Detail) Cooldowns()
    {
        var catalog = connector.Cli.CachedCatalog();
        string? Note(string command) => catalog is not null && catalog.Commands.TryGetValue(command, out var entry) ? entry.Note : null;
        return (TwCliReaders.CooldownSeconds(Note("get_market_prices"), DefaultPricesCooldown), TwCliReaders.CooldownSeconds(Note("get_market_price_detail"), DefaultDetailCooldown));
    }

    // ───────────────────────── 价目 ─────────────────────────

    public async Task<GameMarketPrices> PricesAsync(GameMarketPriceQuery query, CancellationToken ct)
    {
        if (query is null) throw new OperationException("request.invalid", "价目查询无效");
        var modes = (query.Names is { Count: > 0 } ? 1 : 0) + (query.Category is { Length: > 0 } ? 1 : 0) + (query.Favorites ? 1 : 0);
        if (modes != 1 || query.SubCategory is { Length: > 0 } && query.Category is not { Length: > 0 })
            throw new OperationException("request.invalid", "价目查询只能使用名称、分类或收藏中的一种");
        if (query.Names is { } names && (names.Count > MaximumNames || names.Any(x => x is null || x.Length > MaximumNameLength || x.Any(char.IsControl))))
            throw new OperationException("request.invalid", "名称最多 20 个，每个不超过 64 个字符");
        if (query.Category is { } category && (category.Length > MaximumNameLength || category.Any(char.IsControl) ||
            query.SubCategory is { } subName && (subName.Length > MaximumNameLength || subName.Any(char.IsControl))))
            throw new OperationException("request.invalid", "分类名称无效");
        var mode = query.Names is { Count: > 0 } ? "names" : query.Category is { Length: > 0 } ? "category" : "favorites";
        GameMarketPrices Failed(string reason, string? message = null, int? retry = null, IReadOnlyList<string>? candidates = null) =>
            new(backendEpoch, mode, reason, message, retry, candidates ?? [], null, false, query.Category, query.SubCategory, [], []);
        if (query.Names is { } given && given.Any(x => x.Trim().Length < MinimumNameLength)) return Failed("gameCli.nameTooShort");
        var ready = GameCliReadiness.Check(connector, ["get_market_prices"]);
        if (!ready.Ready) return Failed(ready.Reason);

        var cooldown = TimeSpan.FromSeconds(Cooldowns().Prices);
        var key = mode + "|" + (query.Names is { } list ? string.Join('\u0001', list.Select(x => x.Trim())) : query.Category + "\u0001" + query.SubCategory);
        lock (sync)
            if (prices.TryGetValue(key, out var cached) && clock.GetElapsedTime(cached.Stamp) < cooldown) return cached.Value with { Cached = true };

        TwCliBody body = mode switch
        {
            "names" => TwCliBody.Json(new { names = query.Names!.Select(x => x.Trim()).ToArray() }),
            "category" when query.SubCategory is { Length: > 0 } sub => TwCliBody.Json(new { category = query.Category, subCategory = sub }),
            "category" => TwCliBody.Json(new { category = query.Category }),
            _ => TwCliBody.Json(new { favorites = true })
        };
        var result = await connector.Cli.ExecuteAsync("get_market_prices", body, ct, TwCliPriority.Background).ConfigureAwait(false);
        switch (result.Kind)
        {
            case TwCliResultKind.Ok:
                var now = clock.GetUtcNow();
                GameMarketPrices value;
                if (mode == "names")
                {
                    if (TwCliReaders.MarketNames(result.Body) is not { } results) return Failed("gameCli.readFailed");
                    value = new(backendEpoch, mode, null, null, null, [], now, false, null, null, results, []);
                }
                else
                {
                    if (TwCliReaders.MarketRows(result.Body) is not { } rows) return Failed("gameCli.readFailed");
                    var echoed = result.Body is { ValueKind: JsonValueKind.Object } root ? root : (JsonElement?)null;
                    value = new(backendEpoch, mode, null, null, null, [], now, false,
                        echoed is { } e ? TwCliReaders.Text(e, "Category") ?? query.Category : query.Category,
                        echoed is { } f ? TwCliReaders.Text(f, "SubCategory") ?? query.SubCategory : query.SubCategory, [], rows);
                }
                lock (sync)
                {
                    if (prices.Count >= MaximumCache) prices.Clear();
                    prices[key] = new(clock.GetTimestamp(), value);
                }
                return value;
            case TwCliResultKind.Rejected:
                return Failed(TwCliCodes.FromGame(result.Error), result.Message, TwCliReaders.RetryAfterSeconds(result.Root), TwCliReaders.Candidates(result.Root));
            case TwCliResultKind.NotConnected: return Failed(result.Reason == "option_off" ? "gameCli.expired" : "gameCli.gameNotRunning");
            case TwCliResultKind.Interrupted: return Failed("gameCli.interrupted");
            case TwCliResultKind.UnknownCommand or TwCliResultKind.Unavailable: return Failed("gameCli.commandMissing");
            default: return Failed("gameCli.readFailed");
        }
    }

    // ───────────────────────── 明细 ─────────────────────────

    public async Task<GameMarketPriceDetail> DetailAsync(GameMarketDetailQuery query, CancellationToken ct)
    {
        if (query is null || string.IsNullOrWhiteSpace(query.DisplayName) || query.DisplayName.Length > 128 || query.DisplayName.Any(char.IsControl) ||
            query.Rarity is { Length: > 64 } || query.Season is { Length: > 64 } || query.Quantity is < 1 or > 1_000_000)
            throw new OperationException("request.invalid", "明细查询需要道具名称，数量须为正整数");
        GameMarketPriceDetail Failed(string reason, string? message = null, int? retry = null, IReadOnlyList<GameMarketRow>? candidates = null) =>
            new(backendEpoch, reason, message, retry, candidates ?? [], null, null, null);
        var ready = GameCliReadiness.Check(connector, ["get_market_price_detail"]);
        if (!ready.Ready) return Failed(ready.Reason);

        // 明细两次调用至少相隔冷却时间：串行并等待剩余时间，不向游戏发出会被拒绝的调用。
        await detailGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cooldown = TimeSpan.FromSeconds(Cooldowns().Detail);
            if (hasDetail && clock.GetElapsedTime(lastDetail) is var elapsed && elapsed < cooldown)
                await Task.Delay(cooldown - elapsed, clock, ct).ConfigureAwait(false);
            var body = new Dictionary<string, object?> { ["displayName"] = query.DisplayName.Trim() };
            if (query.Rarity is { Length: > 0 }) body["rarity"] = query.Rarity;
            if (query.Star is { } star) body["star"] = star;
            if (query.Season is { Length: > 0 }) body["season"] = query.Season;
            var result = await connector.Cli.ExecuteAsync("get_market_price_detail", TwCliBody.Json(body), ct, TwCliPriority.Background).ConfigureAwait(false);
            if (result.Kind != TwCliResultKind.Interrupted) { lastDetail = clock.GetTimestamp(); hasDetail = true; }
            switch (result.Kind)
            {
                case TwCliResultKind.Ok:
                    if (TwCliReaders.MarketDetail(result.Body) is not { } detail) return Failed("gameCli.readFailed");
                    return new(backendEpoch, null, null, null, [], clock.GetUtcNow(), detail, query.Quantity is { } quantity ? Cost(detail.PriceTiers, quantity) : null);
                case TwCliResultKind.Rejected:
                    return Failed(TwCliCodes.FromGame(result.Error), result.Message, TwCliReaders.RetryAfterSeconds(result.Root), TwCliReaders.CandidateRows(result.Root));
                case TwCliResultKind.NotConnected: return Failed(result.Reason == "option_off" ? "gameCli.expired" : "gameCli.gameNotRunning");
                case TwCliResultKind.Interrupted: return Failed("gameCli.interrupted");
                case TwCliResultKind.UnknownCommand or TwCliResultKind.Unavailable: return Failed("gameCli.commandMissing");
                default: return Failed("gameCli.readFailed");
            }
        }
        finally { detailGate.Release(); }
    }

    /// <summary>从低价档起取件：超出三档数量的部分成本未知，小计只是下限，不把单价乘以超出可用数量的件数。</summary>
    public static GameMarketCost Cost(IReadOnlyList<GameMarketTier> tiers, int quantity)
    {
        var remaining = quantity;
        double subtotal = 0;
        foreach (var tier in tiers.OrderBy(x => x.UnitPrice))
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, tier.Count);
            subtotal += take * tier.UnitPrice;
            remaining -= take;
        }
        return new(quantity, subtotal, quantity - remaining, remaining, remaining > 0);
    }

    // ───────────────────────── 估价 ─────────────────────────

    public async Task<GameMarketValuation> ValuationAsync(GameMarketValuationQuery query, CancellationToken ct)
    {
        if (query?.Items is not { Count: > 0 and <= MaximumValuationItems } items ||
            items.Any(x => x is null || string.IsNullOrWhiteSpace(x.DisplayName) || x.DisplayName.Length > 128 || x.DisplayName.Any(char.IsControl) || x.Count < 0))
            throw new OperationException("request.invalid", "估价需要 1–200 个带名称与数量的道具");
        var rows = new GameMarketValuationRow[items.Count];
        var wanted = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var name = items[i].DisplayName.Trim();
            if (name.Length < MinimumNameLength) { rows[i] = new(items[i].DisplayName, items[i].Count, GameMarketValuationState.NameTooShort, null, null, "gameCli.nameTooShort"); continue; }
            if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) wanted.Add(name);
        }
        string? failure = null;
        await valuationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var missing = wanted.Where(name => !Fresh(name)).ToList();
            if (missing.Count > 0)
            {
                var ready = GameCliReadiness.Check(connector, ["get_market_prices"]);
                if (!ready.Ready) failure = ready.Reason;
                else
                    foreach (var chunk in missing.Chunk(ValuationChunk))
                    {
                        failure = await FetchAsync(chunk, ct).ConfigureAwait(false);
                        if (failure is not null) break;
                    }
            }
        }
        finally { valuationGate.Release(); }

        DateTimeOffset? oldest = null;
        for (var i = 0; i < items.Count; i++)
        {
            if (rows[i] is not null) continue;
            var name = items[i].DisplayName.Trim();
            CachedValuation? entry;
            lock (sync) entry = valuations.TryGetValue(name, out var cached) && clock.GetElapsedTime(cached.Stamp) < ValuationLifetime ? cached : null;
            if (entry is null) { rows[i] = new(items[i].DisplayName, items[i].Count, GameMarketValuationState.Unread, null, null, failure ?? "gameCli.readFailed"); continue; }
            oldest = oldest is null || entry.At < oldest ? entry.At : oldest;
            var count = items[i].Count;
            rows[i] = new(items[i].DisplayName, count, entry.State, entry.UnitPrice, entry.State == GameMarketValuationState.Priced && entry.UnitPrice is { } unit ? unit * count : null, null);
        }
        var allUnread = rows.All(x => x.State.KnownValue is GameMarketValuationState.Unread or GameMarketValuationState.NameTooShort);
        return new(backendEpoch, oldest, allUnread && failure is not null ? failure : null, rows);
    }

    private bool Fresh(string name)
    {
        lock (sync) return valuations.TryGetValue(name, out var cached) && clock.GetElapsedTime(cached.Stamp) < ValuationLifetime;
    }

    /// <summary>名称加双引号精确匹配取价；返回整体失败原因（市场未开放、连接器不可用、被中断等），成功为空。</summary>
    private async Task<string?> FetchAsync(string[] names, CancellationToken ct)
    {
        var body = TwCliBody.Json(new { names = names.Select(x => "\"" + x + "\"").ToArray() });
        var result = await connector.Cli.ExecuteAsync("get_market_prices", body, ct, TwCliPriority.Background).ConfigureAwait(false);
        switch (result.Kind)
        {
            case TwCliResultKind.Ok: break;
            case TwCliResultKind.Rejected: return TwCliCodes.FromGame(result.Error);
            case TwCliResultKind.NotConnected: return result.Reason == "option_off" ? "gameCli.expired" : "gameCli.gameNotRunning";
            case TwCliResultKind.Interrupted: return "gameCli.interrupted";
            case TwCliResultKind.UnknownCommand or TwCliResultKind.Unavailable: return "gameCli.commandMissing";
            default: return "gameCli.readFailed";
        }
        if (TwCliReaders.MarketNames(result.Body) is not { } results) return "gameCli.readFailed";
        var byQuery = new Dictionary<string, GameMarketNameResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in results) byQuery.TryAdd(Normal(item.Query), item);
        var now = clock.GetUtcNow();
        for (var i = 0; i < names.Length; i++)
        {
            var found = byQuery.TryGetValue(Normal(names[i]), out var match) ? match : results.Count == names.Length ? results[i] : null;
            if (found is null) continue;
            var (state, unit) = Evaluate(names[i], found);
            lock (sync)
            {
                if (valuations.Count >= 4 * MaximumValuationItems) valuations.Clear();
                valuations[names[i]] = new(clock.GetTimestamp(), now, state, unit);
            }
        }
        return null;
    }

    private static string Normal(string value) => new(value.Where(c => c != '"' && !char.IsWhiteSpace(c)).ToArray());

    /// <summary>同名恰好一条记录才估价：没有匹配为不可交易，没有在售为无在售，多条（品阶、星级、赛季）为多品阶，不猜测品阶。</summary>
    internal static (GameMarketValuationState State, double? Unit) Evaluate(string name, GameMarketNameResult result)
    {
        var exact = result.Rows.Where(x => string.Equals(Normal(x.DisplayName), Normal(name), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!result.Matched || exact.Length == 0) return (GameMarketValuationState.NotTradable, null);
        if (exact.Length > 1) return (GameMarketValuationState.MultipleVariants, null);
        return exact[0] is { InStock: true, MinUnitPrice: { } price } ? (GameMarketValuationState.Priced, price) : (GameMarketValuationState.NoStock, null);
    }
}
