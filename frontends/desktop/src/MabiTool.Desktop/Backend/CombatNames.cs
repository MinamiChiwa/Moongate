using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>名称种类：决定名称缺失时显示“未知目标／未知技能／未知效果／未知玩家”。</summary>
public enum CombatNameKind { Target, Skill, Effect, Player }

/// <summary>界面上的一个游戏名称：<see cref="Text"/> 是显示文字，<see cref="Tip"/> 是悬停提示。</summary>
public readonly record struct CombatName(string Text, string Tip);

/// <summary>
/// 战斗里的游戏名称（目标、技能、效果、玩家）。后端给出 <c>resolvedName</c> 时以它为准：<c>resolved</c> 显示客户端原文（不做简繁转换）；
/// <c>unknown</c> 与 <c>unavailable</c> 显示“未知…”，名称表正在读取（<c>combat.nameCatalogLoading</c>）时显示“名称读取中”，
/// 悬停提示写出表 ID 与原因。没有 <c>resolvedName</c>（玩家、旧后端）时沿用名称字段：为空或是纯数字的原始编号都按缺失处理。
/// 未解析的名称一律在后面带稳定编号（“未知技能 #20101”），同名的未知项因此分得开：技能与效果用客户端表 ID，
/// 玩家与目标用实体 ID（调用方给出 <c>number</c> 时以它为准）；已解析的名称不加编号。
/// </summary>
public static class CombatNames
{
    public const string Resolved = "resolved";
    public const string LoadingReason = "combat.nameCatalogLoading";
    /// <summary>没有名称表的实体（AI 同伴）：显示按种类给出的固定文案。</summary>
    public const string Generic = "generic";

    /// <summary>名称缺失：为空，或是后端沿用的纯数字原始编号。</summary>
    public static bool Missing(string? name) => string.IsNullOrWhiteSpace(name) || name.All(char.IsAsciiDigit);

    public static string UnknownTarget => Pick("未知目标", "未知目標");
    public static string UnknownSkill => Pick("未知技能", "未知技能");
    public static string UnknownEffect => Pick("未知效果", "未知效果");
    public static string UnknownPlayer => Pick("未知玩家", "未知玩家");
    public static string Loading => Pick("名称读取中", "名稱讀取中");

    public static string Unknown(CombatNameKind kind) => kind switch
    {
        CombatNameKind.Skill => UnknownSkill,
        CombatNameKind.Effect => UnknownEffect,
        CombatNameKind.Player => UnknownPlayer,
        _ => UnknownTarget
    };

    /// <summary>
    /// 显示名称与悬停提示。<paramref name="name"/> 与 <paramref name="id"/> 只在后端没有给出 <paramref name="resolved"/> 时使用：
    /// 名称有效时直接显示，否则显示“未知…”并在提示里写出编号（名称是纯数字时写名称本身，否则写 <paramref name="id"/>）。
    /// </summary>
    /// <param name="number">显示在“#”后的稳定编号；为空时取表 ID（有 <paramref name="resolved"/> 时）或名称里的原始编号、<paramref name="id"/>。</param>
    public static CombatName Of(CombatResolvedName? resolved, CombatNameKind kind, string? name = null, string? id = null, string? number = null)
    {
        if (resolved is { } value)
        {
            if (value.State == Resolved && !string.IsNullOrWhiteSpace(value.Text)) return new(value.Text, value.Text);
            // 通用名称是一类实体的固定文案，不是缺失；同类可能有多个，同样带编号区分。
            if (value.State == Generic) return new(Numbered(GenericName(value.Kind), number), GenericName(value.Kind));
            var label = value.Reason == LoadingReason ? Loading : Unknown(kind);
            var sheet = value.SheetId.ToString();
            return new(Numbered(label, number ?? (value.SheetId.Value == 0 ? null : sheet)), Tip(label, sheet, ReasonText(value.Reason)));
        }
        if (!Missing(name)) return new(name!, name!);
        var unknown = Unknown(kind);
        var raw = string.IsNullOrWhiteSpace(name) ? id : name;
        return new(Numbered(unknown, number ?? Readable(raw)), Tip(unknown, raw, null));
    }

    /// <summary>没有名称表的实体种类的固定文案；目前只有 AI 同伴，其他种类按未知目标显示。</summary>
    public static string GenericName(string? kind) => kind switch
    {
        "ai-companion" => Pick("AI 同伴", "AI 同伴"),
        _ => UnknownTarget
    };

    /// <summary>“未知技能 #20101”；没有编号时只写名称。</summary>
    public static string Numbered(string label, string? number) => string.IsNullOrEmpty(number) ? label : $"{label} #{number}";

    /// <summary>编号的可读形式：后端实体标识（ce- 加 32 位十六进制）取前 8 位，其余原样。</summary>
    public static string? Readable(string? id) => id is { Length: > 11 } && id.StartsWith("ce-", StringComparison.Ordinal) ? id[3..11] : id;

    /// <summary>名称原因码的说明；未登记的原因码原样写出。</summary>
    public static string? ReasonText(string? reason) => reason switch
    {
        null or "" => null,
        "combat.nameNotFound" => Pick("客户端名称表里没有这一项", "用戶端名稱表裡沒有這一項"),
        "combat.skillUnmatched" => Pick("这笔伤害没有对应到技能", "這筆傷害沒有對應到技能"),
        LoadingReason => Pick("正在读取客户端名称表", "正在讀取用戶端名稱表"),
        "combat.nameCatalogUnavailable" => Pick("无法读取客户端名称表", "無法讀取用戶端名稱表"),
        "combat.nameLayoutUnsupported" => Pick("客户端名称表结构与已核对的不符", "用戶端名稱表結構與已核對的不符"),
        "combat.nameTableMissing" => Pick("客户端缺少这张名称表", "用戶端缺少這張名稱表"),
        "combat.nameEmpty" =>Pick("客户端名称为空", "用戶端名稱為空"),
        "combat.nameGeneric" => Pick("没有名称表，显示通用名称", "沒有名稱表，顯示通用名稱"),
        "game.bindingRequired" => Pick("尚未绑定游戏窗口，无法读取名称表", "尚未綁定遊戲視窗，無法讀取名稱表"),
        var other => other
    };

    // “未知技能（编号 20101，客户端名称表里没有这一项）”；没有编号与原因时只写名称。
    private static string Tip(string label, string? id, string? reason)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(id)) parts.Add(Pick($"编号 {id}", $"編號 {id}"));
        if (!string.IsNullOrEmpty(reason)) parts.Add(reason);
        return parts.Count == 0 ? label : $"{label}（{string.Join(Pick("，", "，"), parts)}）";
    }
}
