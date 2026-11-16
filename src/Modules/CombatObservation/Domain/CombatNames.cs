using System.Buffers.Binary;
using System.Globalization;
using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

/// <summary>怪物、技能与效果名称的客户端表身份；名称文字只在读取侧按当前名称表解析，统计与历史不保存原文。</summary>
public static class CombatNames
{
    public const string Monster = "monster", Skill = "skill", Effect = "effect";
    public const string Resolved = "resolved", Unknown = "unknown", Unavailable = "unavailable";
    /// <summary>领域内的占位原因：分组已知是哪个表的哪一行，名称等待读取侧解析。</summary>
    public const string PendingReason = "combat.nameCatalogPending";

    /// <summary>
    /// 来源把怪物的 MonsterSheetId 按小端字节写成 8 位十六进制码（例如 9EE0110C 对应表 ID 0x0C11E09E）。
    /// 不是 8 位十六进制或为零时返回空。
    /// </summary>
    public static uint? MonsterSheetId(string? code)
    {
        if (code is not { Length: 8 } || !uint.TryParse(code, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ||
            code.Any(char.IsLower)) return null;
        var id = BinaryPrimitives.ReverseEndianness(value);
        return id is 0 or uint.MaxValue ? null : id;
    }
    public static CombatResolvedName Pending(string kind, uint id) => new(kind, new(id), null, Unavailable, PendingReason);
    /// <summary>怪物实体的名称占位；玩家、宠物与未知实体为空。</summary>
    public static CombatResolvedName? MonsterLabel(CombatEntity? entity) =>
        entity is { Role.KnownValue: CombatEntityRole.Monster } && MonsterSheetId(entity.RawName) is { } id ? Pending(Monster, id) : null;
    public static CombatResolvedName? MonsterLabel(CombatEntityObservation? entity) =>
        entity is { Role.KnownValue: CombatEntityRole.Monster } && MonsterSheetId(entity.RawName) is { } id ? Pending(Monster, id) : null;
}
