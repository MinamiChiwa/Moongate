using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Character;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Market;

/// <summary>价目查询的三种模式：名称、分类、收藏。</summary>
public enum MarketMode { Names, Category, Favorites }

/// <summary>价目查询请求；名称模式的每个名称至少 2 个字，不足时界面不发查询。</summary>
public sealed record MarketPriceRequest(MarketMode Mode, IReadOnlyList<string> Names, string? Category = null, string? SubCategory = null)
{
    public const int MinimumNameLength = 2;

    /// <summary>请求在发出前就能判定的问题（原因码）：名称模式没有名称或有名称不足 2 个字、分类模式没有分类。可以发出时为空。</summary>
    public string? Problem => Mode switch
    {
        MarketMode.Names when Names.Count == 0 || Names.Any(name => name.Replace(" ", string.Empty).Length < MinimumNameLength) => "gameCli.nameTooShort",
        MarketMode.Category when string.IsNullOrWhiteSpace(Category) => "gameCli.categoryNotFound",
        _ => null
    };

    /// <summary>把输入框的文字拆成名称：按空白、逗号、顿号、分号分隔，去重。</summary>
    public static IReadOnlyList<string> SplitNames(string text) => text.Split([' ', '\t', ',', '，', '、', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries |
        StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    public GameMarketPriceQuery ToQuery() => Mode switch
    {
        MarketMode.Names => new(Names: Names),
        MarketMode.Category => new(Category: Category, SubCategory: string.IsNullOrWhiteSpace(SubCategory) ? null : SubCategory),
        _ => new(Favorites: true)
    };
}

/// <summary>一行交易所记录：品阶、星级、赛季用来在明细查询里区分同名记录（<see cref="Rarity"/>、<see cref="Star"/>、<see cref="Season"/>）。</summary>
public sealed record MarketRowView(string Name, string? Rarity, int? Star, string? Season, string Grade, string Stock, string Price,
    string Tip, bool InStock, bool Selected = false)
{
    public bool HasGrade => Grade.Length > 0;
    /// <summary>名称加品阶（有则带）。</summary>
    public string Title => HasGrade ? Name + " · " + Grade : Name;
    public string Key => string.Join('\u0001', Name, Rarity, Star, Season);
}

/// <summary>名称模式下一个名称的结果（或分类、收藏模式的整体结果）：<see cref="Matched"/> 为假表示没有可交易的道具匹配。</summary>
public sealed record MarketGroupView(string Title, bool Matched, IReadOnlyList<MarketRowView> Rows)
{
    public bool HasTitle => Title.Length > 0;
    public string Empty => Pick("交易所里没有匹配的道具", "交易所裡沒有符合的道具");
    public bool ShowEmpty => Rows.Count == 0;
}

/// <summary>
/// 价目查询的结果。<see cref="Reason"/> 为空表示成功；失败时 <see cref="Message"/> 写明原因，不显示价格。
/// <see cref="RetryAfterSeconds"/> 是游戏要求等待的秒数；<see cref="Candidates"/> 是分类不存在时游戏给出的候选名。
/// </summary>
public sealed record MarketPricesView(string Reason, string Message, string Note, IReadOnlyList<string> Candidates,
    IReadOnlyList<MarketGroupView> Groups, int RetryAfterSeconds)
{
    public bool Ok => Reason.Length == 0;
    public bool HasMessage => Message.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasCandidates => Candidates.Count > 0;
    public string CandidatesText => Pick("可选分类：", "可選分類：") + string.Join(Pick("、", "、"), Candidates);
    public static MarketPricesView None { get; } = new(string.Empty, string.Empty, string.Empty, [], [], 0);
}

public sealed record MarketTierLine(string Price, string Count);

/// <summary>
/// 价格明细：最低与最高挂单价（卖空时为“无在售”）、平均成交价、至多三档价位。<see cref="Tiers"/> 是原始档位，购买成本按它与数量就地计算
/// （不再向游戏查询，避免受每秒一次的限制）。失败时 <see cref="Reason"/> 非空，<see cref="Candidates"/> 是多项匹配时的候选记录。
/// </summary>
public sealed record MarketDetailView(string Reason, string Message, string Note, int RetryAfterSeconds, IReadOnlyList<MarketRowView> Candidates,
    string Title, string MinAsk, string MaxAsk, string Average, IReadOnlyList<MarketTierLine> TierLines, IReadOnlyList<GameMarketTier> Tiers)
{
    public bool Ok => Reason.Length == 0;
    public bool HasMessage => Message.Length > 0;
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasTiers => TierLines.Count > 0;
    public static MarketDetailView None { get; } = new(string.Empty, string.Empty, string.Empty, 0, [], string.Empty, string.Empty, string.Empty,
        string.Empty, [], []);
}

/// <summary>明细查询请求：取自价目的一行。</summary>
public sealed record MarketDetailRequest(string Name, string? Rarity, int? Star, string? Season)
{
    public GameMarketDetailQuery ToQuery() => new(Name, Rarity, Star, Season);
}

/// <summary>按数量计算的购买成本：超出三档数量时只给已定价小计并标为下限，其余成本未知。</summary>
public sealed record MarketCost(int Quantity, double PricedSubtotal, int PricedCount, int UnpricedCount)
{
    public bool IsLowerBound => UnpricedCount > 0;
    public bool Known => Quantity > 0 && UnpricedCount == 0;

    /// <summary>从低价档起取够数量；档位总量不足的件数成本未知，不得把单价乘到超出可用数量。</summary>
    public static MarketCost Compute(IReadOnlyList<GameMarketTier> tiers, int quantity)
    {
        var remaining = Math.Max(0, quantity);
        double subtotal = 0;
        var priced = 0;
        foreach (var tier in tiers.OrderBy(tier => tier.UnitPrice))
        {
            if (remaining == 0) break;
            var take = Math.Min(remaining, Math.Max(0, tier.Count));
            subtotal += take * tier.UnitPrice;
            priced += take;
            remaining -= take;
        }
        return new(Math.Max(0, quantity), subtotal, priced, remaining);
    }

    /// <summary>成本的一句话：数量为 0 时为空；成本已知给总价；否则给已定价件数与小计（下限）和未知的件数。</summary>
    public string Text
    {
        get
        {
            if (Quantity <= 0) return string.Empty;
            if (Known) return Pick($"购买 {Quantity} 件的成本 {MarketMapper.Money(PricedSubtotal)} 德卡", $"購買 {Quantity} 件的成本 {MarketMapper.Money(PricedSubtotal)} 德卡");
            if (PricedCount == 0)
                return Pick($"{Quantity} 件的成本未知（市场只报告三档价位以内的数量）", $"{Quantity} 件的成本未知（市場只報告三檔價位以內的數量）");
            return Pick($"已定价 {PricedCount} 件，小计 {MarketMapper.Money(PricedSubtotal)} 德卡（下限）；其余 {UnpricedCount} 件成本未知",
                $"已定價 {PricedCount} 件，小計 {MarketMapper.Money(PricedSubtotal)} 德卡（下限）；其餘 {UnpricedCount} 件成本未知");
        }
    }
}

/// <summary>交易所查价的界面文字：原因（带交易所语境）、价格格式与取价时刻标注。</summary>
public static class MarketText
{
    /// <summary>原因码换成交易所语境下的一句中文；没有交易所专属说法的码交给共用映射（连接器原因后面指向“游戏连接器”胶囊）。</summary>
    public static string Reason(string? code) => code switch
    {
        null or "" => string.Empty,
        "gameCli.nameTooShort" => Pick("名称至少 2 个字", "名稱至少 2 個字"),
        "gameCli.categoryNotFound" => Pick("没有这个分类", "沒有這個分類"),
        "gameCli.marketLocked" => Pick("交易所尚未解锁，不能查价", "交易所尚未解鎖，不能查價"),
        "gameCli.marketUnavailable" => Pick("交易所暂时不可用", "交易所暫時不可用"),
        "gameCli.rateLimited" => Pick("查询太频繁，请稍后再试", "查詢太頻繁，請稍後再試"),
        "gameCli.notFound" => Pick("交易所里没有这个道具", "交易所裡沒有這個道具"),
        "gameCli.ambiguousItem" => Pick("有多条同名记录，请选择具体的品阶", "有多條同名記錄，請選擇具體的品階"),
        "gameCli.invalidBody" => Pick("查询内容无效", "查詢內容無效"),
        "gameCli.busy" => Pick("游戏指令通道正忙，请稍后再试", "遊戲指令通道正忙，請稍後再試"),
        _ => CharacterText.ReasonLine(code)
    };

    /// <summary>每个结果块都标注的一行：“交易所价格，单位德卡”与取价时刻（缓存的结果注明）。</summary>
    public static string Note(DateTimeOffset? readAt, bool cached = false)
    {
        var text = Pick("交易所价格，单位德卡", "交易所價格，單位德卡");
        if (readAt is { } at) text += Pick(" · 取价于 ", " · 取價於 ") + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return cached ? text + Pick("（缓存）", "（快取）") : text;
    }

    public static string NoStock => Pick("无在售", "無在售");
}

/// <summary>契约结果 → 查价页数据的纯映射；游戏的名称、品阶、赛季保持原文。</summary>
public static class MarketMapper
{
    public static string Money(double value) => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);

    public static MarketRowView Row(GameMarketRow row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.Rarity)) parts.Add(row.Rarity!);
        if (row.Star is { } star) parts.Add(star == 0 ? Pick("未刻印", "未刻印") : $"{star}★");
        if (!string.IsNullOrWhiteSpace(row.Season)) parts.Add(row.Season!);
        var tip = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.MainAttributeRange)) tip.Add(Pick("主属性：", "主屬性：") + row.MainAttributeRange);
        if (row.Favorite) tip.Add(Pick("已收藏", "已收藏"));
        var inStock = row.InStock && row.MinUnitPrice is not null;
        return new(row.DisplayName, row.Rarity, row.Star, row.Season, string.Join(" · ", parts),
            inStock ? Pick("在售", "在售") : MarketText.NoStock, inStock ? Money(row.MinUnitPrice!.Value) : "—", string.Join("\n", tip), inStock);
    }

    /// <summary>价目结果：成功给出分组（名称模式每个名称一组，没有匹配的名称标出），失败给出原因与候选。</summary>
    public static MarketPricesView Prices(GameMarketPrices result)
    {
        var candidates = result.Candidates?.ToList() ?? [];
        if (result.Reason is { Length: > 0 } reason)
            return new(reason, MarketText.Reason(reason), string.Empty, candidates, [], result.RetryAfterSeconds ?? 0);
        var groups = new List<MarketGroupView>();
        if (result.Results is { Count: > 0 } named)
            groups.AddRange(named.Select(item => new MarketGroupView(Pick($"名称“{item.Query}”", $"名稱「{item.Query}」"), item.Matched,
                item.Rows.Select(Row).ToList())));
        else groups.Add(new(string.Empty, true, result.Rows?.Select(Row).ToList() ?? []));
        return new(string.Empty, string.Empty, MarketText.Note(result.ReadAt, result.Cached), candidates, groups, 0);
    }

    /// <summary>明细结果：成功给出价位与三档；失败给出原因、等待秒数与候选记录。</summary>
    public static MarketDetailView Detail(GameMarketPriceDetail result)
    {
        var candidates = result.Candidates?.Select(Row).ToList() ?? [];
        if (result.Reason is { Length: > 0 } reason)
            return MarketDetailView.None with { Reason = reason, Message = MarketText.Reason(reason), RetryAfterSeconds = result.RetryAfterSeconds ?? 0, Candidates = candidates };
        var detail = result.Detail;
        if (detail is null) return MarketDetailView.None with { Reason = "gameCli.notFound", Message = MarketText.Reason("gameCli.notFound") };
        var tiers = detail.PriceTiers?.OrderBy(tier => tier.UnitPrice).ToList() ?? [];
        string Price(double? value) => value is { } price ? Money(price) : MarketText.NoStock;
        var shown = Row(new(detail.DisplayName, detail.Rarity, detail.Star, detail.Season, detail.MainAttributeRange, false, true, null));
        return new(string.Empty, string.Empty, MarketText.Note(result.ReadAt), 0, [], shown.HasGrade ? shown.Name + " · " + shown.Grade : shown.Name,
            Price(detail.MinAskUnitPrice), Price(detail.MaxAskUnitPrice), detail.AverageUnitPrice is { } average ? Money(average) : Pick("未读取", "未讀取"),
            tiers.Select(tier => new MarketTierLine(Money(tier.UnitPrice), tier.Count.ToString("N0", CultureInfo.InvariantCulture))).ToList(), tiers);
    }
}

/// <summary>明细查询冷却：游戏要求等待时启动，倒计时到 0 之前停用对应按钮；到时不自动重发。</summary>
public sealed class MarketCooldown(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset until;

    public void Start(int seconds) => until = now() + TimeSpan.FromSeconds(Math.Max(1, seconds));
    public void Clear() => until = default;

    /// <summary>剩余整秒数（向上取整）；没有冷却或已到时为 0。</summary>
    public int Remaining => until == default ? 0 : (int)Math.Max(0, Math.Ceiling((until - now()).TotalSeconds));
    public bool Active => Remaining > 0;
}
