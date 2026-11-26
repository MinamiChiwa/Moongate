using System.Globalization;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwCli;

/// <summary>
/// 把读取类指令的响应映射为契约：字段区分大小写，缺省字段为空（界面显示未读取，不当 0），形状不符时整体返回空。
/// 响应根可以是对象或数组，带 <c>status</c> 与 <c>body</c> 包装时调用方先取 <see cref="TwCliResult.Body"/>。
/// </summary>
public static class TwCliReaders
{
    private static readonly HashSet<string> ProfileScalars = new(StringComparer.Ordinal)
    { "Title", "RealmName", "Level", "EnabledCombatJobDisplayName", "PaladinStats", "Vitals", "LivingSkills" };

    // ───────────────────────── 角色 ─────────────────────────

    /// <summary><c>get_my_info</c>：<c>RealmName</c> 是服务器名、<c>Title</c> 是称号，响应里没有昵称（昵称由抓包回填）；属性取顶层所有形如 <c>{DisplayName, Value}</c> 的字段（不写死清单）。</summary>
    public static GameProfile? Profile(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root) return null;
        var stats = new List<GameStat>();
        foreach (var property in root.EnumerateObject())
            if (!ProfileScalars.Contains(property.Name) && Stat(property) is { } stat) stats.Add(stat);
        var paladin = new List<GameStat>();
        if (root.TryGetProperty("PaladinStats", out var group) && group.ValueKind == JsonValueKind.Object)
            foreach (var property in group.EnumerateObject())
                if (Stat(property) is { } stat) paladin.Add(stat);
        var skills = new List<GameLivingSkill>();
        if (root.TryGetProperty("LivingSkills", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var row in list.EnumerateArray())
                if (Text(row, "DisplayName") is { Length: > 0 } name) skills.Add(new(name, Int(row, "Level"), Int(row, "Exp"), Int(row, "MaxExp")));
        GameVitals? vitals = null;
        if (root.TryGetProperty("Vitals", out var v) && v.ValueKind == JsonValueKind.Object)
            vitals = new(Number(v, "HealthCurrent"), Number(v, "HealthMax"), Number(v, "ShieldAmount"), Number(v, "ShieldMax"), Number(v, "SatietyValue"),
                Number(v, "SatietyMax"), Number(v, "SatietyRatio"), Number(v, "InventoryWeightCurrent"), Number(v, "InventoryWeightMax"), Int(v, "ActiveBuffCount"));
        return new(null, Text(root, "Title"), Text(root, "RealmName"), Int(root, "Level"), Text(root, "EnabledCombatJobDisplayName"), stats, paladin, vitals, skills);
    }

    private static GameStat? Stat(JsonProperty property) =>
        property.Value.ValueKind == JsonValueKind.Object && Text(property.Value, "DisplayName") is { Length: > 0 } name
            ? new(property.Name, name, Number(property.Value, "Value")) : null;

    /// <summary><c>get_currencies</c>：<c>{DisplayName, Amount}</c> 数组。</summary>
    public static IReadOnlyList<GameCurrency>? Currencies(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Array } rows) return null;
        var list = new List<GameCurrency>();
        foreach (var row in rows.EnumerateArray())
            if (Text(row, "DisplayName") is { Length: > 0 } name) list.Add(new(name, Number(row, "Amount")));
        return list;
    }

    /// <summary><c>get_daily_missions</c> 与 <c>get_weekly_missions</c> 的任务数组。</summary>
    public static IReadOnlyList<GameMission>? Missions(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Array } rows) return null;
        var list = new List<GameMission>();
        foreach (var row in rows.EnumerateArray())
            if (Text(row, "Title") is { Length: > 0 } title)
                list.Add(new(title, Text(row, "Description"), Int(row, "CurrentCount"), Int(row, "GoalCount"), Flag(row, "IsCompleted") == true,
                    Flag(row, "IsRewardReceived") == true, Flag(row, "HasShortcut") == true));
        return list;
    }

    /// <summary><c>get_growth_guide</c>：<c>{Contents:[…]}</c>；未解锁的内容没有完成状态与剩余次数。</summary>
    public static IReadOnlyList<GameGrowthContent>? GrowthGuide(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("Contents", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
        var list = new List<GameGrowthContent>();
        foreach (var row in rows.EnumerateArray())
            if (Text(row, "Target") is { Length: > 0 } target)
                list.Add(new(target, Text(row, "DayType") ?? string.Empty, Flag(row, "IsUnlocked") == true, Flag(row, "IsCompleted"),
                    Int(row, "RemainingCount"), Text(row, "UnlockHint")));
        return list;
    }

    // ───────────────────────── 环境与附近 ─────────────────────────

    /// <summary><c>get_current_environment</c>：<c>ErinnNow</c> 是游戏内历法时间原文，不转换。</summary>
    public static GameEnvironment? Environment(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root) return null;
        double? x = null, y = null;
        if (root.TryGetProperty("WorldPosition", out var position) && position.ValueKind == JsonValueKind.Object) (x, y) = (Number(position, "X"), Number(position, "Y"));
        GameHousing? housing = null;
        if (root.TryGetProperty("Housing", out var h) && h.ValueKind == JsonValueKind.Object)
            housing = new(Flag(h, "IsInHousing"), Flag(h, "IsOwnedHousing"), Flag(h, "CanEnterHousing"), Text(h, "CannotEnterHousingReason"), Flag(h, "CanExitHousing"));
        return new(Text(root, "ChannelDisplayName"), Text(root, "GameSpaceDisplayName"), x, y, Text(root, "Weather"), Text(root, "ErinnNow"), housing);
    }

    /// <summary><c>get_near_pcs</c>：<c>RealmName</c> 是服务器名、<c>Title</c> 是称号，没有昵称字段；空数组是正常结果。</summary>
    public static IReadOnlyList<GameNearbyPlayer>? Nearby(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Array } rows) return null;
        var list = new List<GameNearbyPlayer>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            GameNearbyPerformance? performance = null;
            if (row.TryGetProperty("Performance", out var p) && p.ValueKind == JsonValueKind.Object)
                performance = new(Flag(p, "IsPlaying") == true, Text(p, "MusicTitle"), Flag(p, "IsCopyingAllowed"), Flag(p, "IsLoop"),
                    Number(p, "TotalDurationSeconds"), Number(p, "ElapsedSeconds"), Number(p, "RemainingSeconds"), Int(p, "ChannelCount"));
            list.Add(new(Text(row, "RealmName"), Text(row, "Title"), Number(row, "Distance"), Int(row, "Level"), Text(row, "EnabledCombatJobDisplayName"),
                Flag(row, "IsFriend"), Flag(row, "IsInParty"), Flag(row, "HasGuild"), Flag(row, "IsSameGuild"), Flag(row, "IsInCombat"), performance));
        }
        return list;
    }

    // ───────────────────────── 交易所 ─────────────────────────

    /// <summary><c>get_market_prices</c> 名称模式：<c>{Results:[{Query, Matched, Rows}]}</c>。</summary>
    public static IReadOnlyList<GameMarketNameResult>? MarketNames(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        var list = new List<GameMarketNameResult>();
        foreach (var item in results.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
                list.Add(new(Text(item, "Query") ?? string.Empty, Flag(item, "Matched") == true, MarketRows(item) ?? []));
        return list;
    }

    /// <summary>价目的 <c>Rows</c> 数组（分类与收藏模式的根，名称模式每个结果里也有）。</summary>
    public static IReadOnlyList<GameMarketRow>? MarketRows(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("Rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
        var list = new List<GameMarketRow>();
        foreach (var row in rows.EnumerateArray())
            if (MarketRow(row) is { } value) list.Add(value);
        return list;
    }

    public static GameMarketRow? MarketRow(JsonElement row) => row.ValueKind == JsonValueKind.Object && Text(row, "DisplayName") is { Length: > 0 } name
        ? new(name, Loose(row, "Rarity"), Int(row, "Star"), Loose(row, "Season"), Loose(row, "MainAttributeRange"), Flag(row, "Favorite") == true,
            Flag(row, "InStock") == true, Number(row, "MinUnitPrice")) : null;

    /// <summary><c>get_market_price_detail</c>：最多三档价位，按单价从低到高排列。</summary>
    public static GameMarketDetail? MarketDetail(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root || Text(root, "DisplayName") is not { Length: > 0 } name) return null;
        var tiers = new List<GameMarketTier>();
        if (root.TryGetProperty("PriceTiers", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var tier in list.EnumerateArray())
                if (Number(tier, "UnitPrice") is { } price && Int(tier, "Count") is { } count and >= 0) tiers.Add(new(price, count));
        return new(name, Loose(root, "Rarity"), Int(root, "Star"), Loose(root, "Season"), Loose(root, "MainAttributeRange"),
            Number(root, "MinAskUnitPrice"), Number(root, "MaxAskUnitPrice"), Number(root, "AverageUnitPrice"), [.. tiers.OrderBy(x => x.UnitPrice)]);
    }

    /// <summary>
    /// 被拒绝响应里的候选：<c>candidates</c> 可以是字符串数组（分类候选）或记录数组（多项匹配），字符串按只有名称的记录处理；
    /// 在根与 <c>body</c> 里都找。
    /// </summary>
    public static IReadOnlyList<string> Candidates(JsonElement? root)
    {
        if (Find(root, "candidates") is not { ValueKind: JsonValueKind.Array } list) return [];
        var result = new List<string>();
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) result.Add(text);
            else if (item.ValueKind == JsonValueKind.Object && Text(item, "DisplayName") is { Length: > 0 } name) result.Add(name);
        return result;
    }

    /// <summary>被拒绝响应里的多项匹配候选记录。</summary>
    public static IReadOnlyList<GameMarketRow> CandidateRows(JsonElement? root)
    {
        if (Find(root, "candidates") is not { ValueKind: JsonValueKind.Array } list) return [];
        var result = new List<GameMarketRow>();
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) result.Add(new(text, null, null, null, null, false, false, null));
            else if (MarketRow(item) is { } row) result.Add(row);
        return result;
    }

    /// <summary><c>rate_limited</c> 的 <c>retryAfterSeconds</c>（向上取整为整数秒，至少 1）。</summary>
    public static int? RetryAfterSeconds(JsonElement? root) =>
        Find(root, "retryAfterSeconds") is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var seconds) && seconds > 0
            ? (int)Math.Min(int.MaxValue, Math.Ceiling(seconds)) : null;

    /// <summary>被拒绝响应里的 <c>unlockHint</c>（成长指南未解锁时游戏给出的解锁条件原文）。</summary>
    public static string? UnlockHint(JsonElement? root) => Find(root, "unlockHint") is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    // ───────────────────────── 目录里的冷却 ─────────────────────────

    /// <summary>
    /// 从指令备注读出冷却秒数：形如“at most once every 5 seconds”“limited to one per 1 seconds”的第一个整数；读不到时返回默认值。
    /// </summary>
    public static int CooldownSeconds(string? note, int fallback)
    {
        if (string.IsNullOrEmpty(note)) return fallback;
        foreach (var pattern in new[] { @"once every\s+(\d+)\s*second", @"one per\s+(\d+)\s*second", @"every\s+(\d+)\s*second" })
            if (System.Text.RegularExpressions.Regex.Match(note, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } match &&
                int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 600) return seconds;
        return fallback;
    }

    // ───────────────────────── 基础读取 ─────────────────────────

    private static JsonElement? Find(JsonElement? root, string name)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value) return null;
        if (value.TryGetProperty(name, out var direct)) return direct;
        return value.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var nested) ? nested : null;
    }

    internal static string? Text(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>字符串或数字（品阶、赛季等取值形状未经实测，数字按文本保留）。</summary>
    private static string? Loose(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null
        };
    }

    internal static double? Number(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
        double.IsFinite(number) ? number : null;

    internal static int? Int(JsonElement row, string name)
    {
        if (Number(row, name) is not { } number) return null;
        var rounded = Math.Round(number);
        return rounded is >= int.MinValue and <= int.MaxValue ? (int)rounded : null;
    }

    internal static bool? Flag(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
