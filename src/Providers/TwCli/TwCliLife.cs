using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiTool.Providers.TwCli;

public sealed record TwCliMaterial(string Name, long? Required, long? Owned);
public sealed record TwCliGatherable(string Name, bool? ToolOk);
public sealed record TwCliAlterable(string Name, bool? Alterable, long? ProducedPerWork, string? Reason, IReadOnlyList<TwCliMaterial> Missing);
public sealed record TwCliAlterWork(string Name, string Facility, string? State, bool? Completed, double? RemainingSeconds);
public sealed record TwCliAlterQueue(int? CompletedCount, IReadOnlyList<TwCliAlterWork> Works);
public sealed record TwCliCraftable(string Name, int? Variant, bool? Craftable, long? ProducedPerCraft, double? SuccessRate, double? CriticalSuccessRate,
    string? Reason, IReadOnlyList<TwCliMaterial> Ingredients);
public sealed record TwCliCraftList(bool? Unlocked, IReadOnlyList<TwCliCraftable> Items);

/// <summary>
/// 生活生产读取类指令的响应解析：缺省字段保持为空（界面显示“未读取”，不当作 0），不是对象的条目跳过，
/// 没有名称的条目没有身份，同样跳过。字段名区分大小写，与运行时目录公布的输出一致。
/// </summary>
public static class TwCliLifeParser
{
    public static IReadOnlyList<TwCliGatherable>? Gatherables(JsonElement? body) =>
        Items(body, row => Text(row, "DisplayName") is { Length: > 0 } name ? new TwCliGatherable(name, Flag(row, "ToolOk")) : null);

    public static IReadOnlyList<TwCliAlterable>? Alterables(JsonElement? body) =>
        Items(body, row => Text(row, "DisplayName") is { Length: > 0 } name
            ? new TwCliAlterable(name, Flag(row, "Alterable"), Whole(row, "ProducedPerWork"), Text(row, "Reason"), Materials(row, "MissingIngredients")) : null);

    public static TwCliAlterQueue? Works(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root) return null;
        var works = Items(body, row => Text(row, "DisplayName") is { Length: > 0 } name && Text(row, "FacilityName") is { Length: > 0 } facility
            ? new TwCliAlterWork(name, facility, Text(row, "State"), Flag(row, "IsCompleted"), Number(row, "RemainingSeconds")) : null, "works");
        return works is null ? null : new(root.TryGetProperty("completedCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value) ? value : null, works);
    }

    public static TwCliCraftList? Craftables(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } root) return null;
        var items = Items(body, row => Text(row, "DisplayName") is { Length: > 0 } name
            ? new TwCliCraftable(name, row.TryGetProperty("Variant", out var variant) && variant.ValueKind == JsonValueKind.Number && variant.TryGetInt32(out var number) ? number : null,
                Flag(row, "Craftable"), Whole(row, "ProducedPerCraft"), Number(row, "SuccessRate"), Number(row, "CriticalSuccessRate"), Text(row, "Reason"),
                Materials(row, "Ingredients")) : null);
        return items is null ? null : new(root.TryGetProperty("craftingUnlocked", out var unlocked) && unlocked.ValueKind is JsonValueKind.True or JsonValueKind.False ? unlocked.GetBoolean() : null, items);
    }

    private static IReadOnlyList<T>? Items<T>(JsonElement? body, Func<JsonElement, T?> read, string property = "items") where T : class
    {
        if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var result = new List<T>();
        foreach (var row in list.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && read(row) is { } item) result.Add(item);
        return result;
    }

    private static IReadOnlyList<TwCliMaterial> Materials(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<TwCliMaterial>();
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && Text(item, "DisplayName") is { Length: > 0 } name) result.Add(new(name, Whole(item, "Required"), Whole(item, "Owned")));
        return result;
    }

    internal static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static bool? Flag(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    internal static double? Number(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    internal static long? Whole(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out var whole) ? whole : (long)Math.Round(value.GetDouble()) : null;
}

/// <summary>运行时目录备注里的单次花费（例如 5 精靈之翼）；<see cref="Text"/> 是界面显示的文字。</summary>
public sealed record TwCliCost(long Amount, string Currency)
{
    public string Text => Amount.ToString(CultureInfo.InvariantCulture) + " " + Currency;
}

/// <summary>执行成功后客户端返回的实际支出句子（<c>精靈之翼 5 spent, 11530 left.</c>）；解析不了时界面按原文显示。</summary>
public sealed record TwCliSpent(string Currency, long Spent, long Left);

public static partial class TwCliLifeCost
{
    /// <summary>从指令备注读出“consumes N 货币”；备注没有写花费或写法不同时为空，界面如实写明目录没有给出花费。</summary>
    public static TwCliCost? FromNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var match = NoteCost().Match(note);
        return match.Success && long.TryParse(match.Groups["n"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            ? new(amount, match.Groups["cur"].Value) : null;
    }

    public static TwCliSpent? FromResult(string? cost)
    {
        if (string.IsNullOrWhiteSpace(cost)) return null;
        var match = ResultCost().Match(cost);
        return match.Success
            && long.TryParse(match.Groups["spent"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var spent)
            && long.TryParse(match.Groups["left"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var left)
            ? new(match.Groups["cur"].Value, spent, left) : null;
    }

    [GeneratedRegex(@"consumes\s+(?<n>[\d,]+)\s+`?(?<cur>[^\s.,;`']+)`?", RegexOptions.CultureInvariant)]
    private static partial Regex NoteCost();

    [GeneratedRegex(@"^\s*(?<cur>\S+)\s+(?<spent>[\d,]+)\s+spent,\s*(?<left>[\d,]+)\s+left\.?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultCost();
}

public enum TwCliLifeKind
{
    /// <summary>采集或制作完成。</summary>
    Completed,
    /// <summary>加工已排队，或命令行已开启自动钓鱼。</summary>
    Started,
    /// <summary>领取完成。</summary>
    Collected,
    /// <summary>游戏里提前结束（<c>result=stopped</c>）。</summary>
    Stopped,
    /// <summary>用户在游戏里停止了前往设施的路程或制作（<c>stopped_by_user</c>）。</summary>
    StoppedByUser,
    /// <summary>游戏拒绝或在开始后被打断，<see cref="TwCliLifeOutcome.Code"/> 是游戏给出的原因。</summary>
    Rejected,
    /// <summary>命令行出错或响应无法解析，游戏是否已执行未知。</summary>
    Failed,
    /// <summary>调用方取消或紧急停止中断了命令行进程，游戏动作状态未知。</summary>
    Interrupted,
    /// <summary>请求确定没有发出（连接器不可用、目录没有该指令、凭证无效）。</summary>
    Unavailable
}

public sealed record TwCliReward(string Name, long? Amount);

/// <summary>
/// 一次生活执行指令的结果。<see cref="Code"/> 统一为 <c>gameCli.xxx</c> 形式（游戏的 snake_case 原因经 <see cref="TwCliCodes.FromGame"/>）；
/// <see cref="BlockedKind"/> 是 <c>blocked</c> 时游戏给出的界面或状态种类原文；<see cref="Cost"/> 是客户端返回的花费句子原文。
/// </summary>
public sealed record TwCliLifeOutcome(TwCliLifeKind Kind, string? Code = null, string? Message = null, string? BlockedKind = null,
    long? Gained = null, long? Target = null, string? Cost = null, long? Collected = null, IReadOnlyList<TwCliReward>? Rewards = null,
    IReadOnlyList<TwCliReward>? CriticalRewards = null, long? CraftCount = null, long? MaxCount = null)
{
    /// <summary>请求确定没有到达游戏；其他结果可能已经产生游戏效果。</summary>
    public bool NotSent => Kind == TwCliLifeKind.Unavailable;

    public static TwCliLifeOutcome From(TwCliResult result)
    {
        switch (result.Kind)
        {
            case TwCliResultKind.Interrupted: return new(TwCliLifeKind.Interrupted, "gameCli.interrupted");
            case TwCliResultKind.NotConnected: return new(TwCliLifeKind.Unavailable, "gameCli.notConnected");
            case TwCliResultKind.UnknownCommand or TwCliResultKind.Unavailable:
                return new(TwCliLifeKind.Unavailable, result.Error ?? "gameCli.failed");
            case TwCliResultKind.Rejected:
            {
                var fields = Fields(result.Root);
                return new(TwCliLifeKind.Rejected, TwCliCodes.FromGame(result.Error), result.Message ?? Text(fields, "message"), Text(fields, "kind"),
                    Whole(fields, "gained"), Whole(fields, "target"), Text(fields, "cost"), CraftCount: Whole(fields, "craftCount"), MaxCount: Whole(fields, "maxCount"));
            }
            case TwCliResultKind.Ok:
            {
                var fields = Fields(result.Root);
                if (fields is null) return new(TwCliLifeKind.Failed, "gameCli.unparseable");
                var cost = Text(fields, "cost");
                var message = Text(fields, "message");
                var kind = Text(fields, "result") switch
                {
                    "completed" => TwCliLifeKind.Completed,
                    "started" => TwCliLifeKind.Started,
                    "stopped" => TwCliLifeKind.Stopped,
                    "stopped_by_user" => TwCliLifeKind.StoppedByUser,
                    _ => Whole(fields, "collected") is not null ? TwCliLifeKind.Collected : (TwCliLifeKind?)null
                };
                if (kind is null) return new(TwCliLifeKind.Failed, "gameCli.unparseable", message);
                return new(kind.Value, null, message, null, Whole(fields, "gained"), Whole(fields, "target"), cost, Whole(fields, "collected"),
                    RewardList(fields, "rewards"), RewardList(fields, "criticalRewards"), Whole(fields, "craftCount"));
            }
            default:
                return new(TwCliLifeKind.Failed, result.Error ?? "gameCli.failed", result.Message);
        }
    }

    // 响应可能带 status/body 包装，也可能就是数据对象：先取 body，没有就取根。
    private static JsonElement? Fields(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value) return null;
        return value.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object ? Merge(value, body) : value;
    }

    // 外层的 kind、message 等在 body 里没有时也要读到：返回 body，读取时回退到外层由调用方分别取。
    private static JsonElement Merge(JsonElement outer, JsonElement body) => body.TryGetProperty("error", out _) || body.TryGetProperty("result", out _)
        || body.TryGetProperty("collected", out _) ? body : outer;

    private static string? Text(JsonElement? fields, string name) => fields is { } row ? TwCliLifeParser.Text(row, name) : null;
    private static long? Whole(JsonElement? fields, string name) => fields is { } row ? TwCliLifeParser.Whole(row, name) : null;

    private static IReadOnlyList<TwCliReward>? RewardList(JsonElement? fields, string name)
    {
        if (fields is not { } row || !row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var result = new List<TwCliReward>();
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && (TwCliLifeParser.Text(item, "Name") ?? TwCliLifeParser.Text(item, "DisplayName")) is { Length: > 0 } reward)
                result.Add(new(reward, TwCliLifeParser.Whole(item, "Amount") ?? TwCliLifeParser.Whole(item, "Count")));
        return result;
    }
}
