using System.Buffers.Binary;
using System.Globalization;
using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

/// <summary>怪物、技能与效果名称的客户端表身份；名称文字只在读取侧按当前名称表解析，统计与历史不保存原文。</summary>
public static class CombatNames
{
    public const string Monster = "monster", Skill = "skill", Effect = "effect";
    /// <summary>怪物以外按客户端表取名的自动体种类，与实体 actorType 相同；AiCompanion 没有表。</summary>
    public const string Npc = "npc", NpcCompanion = "npc-companion", Pet = "pet", SummonedAutomaton = "summoned-automaton", Gimmick = "gimmick",
        MemorialSagaMinion = "memorial-saga-minion", AiCompanion = "ai-companion";
    public static IReadOnlySet<string> ActorKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
        { Npc, NpcCompanion, Pet, SummonedAutomaton, Gimmick, MemorialSagaMinion };
    public const string Resolved = "resolved", Unknown = "unknown", Unavailable = "unavailable";
    /// <summary>没有客户端表的实体（AI 同伴）：前端显示固定的本地化文案。</summary>
    public const string Generic = "generic", GenericReason = "combat.nameGeneric";
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
    /// <summary>
    /// 实体的名称占位：怪物取来源的怪物码，其他自动体按 actorType 与表 ID（NPC、同伴、宠物、召唤物、机关），AI 同伴为 generic；
    /// 玩家与类型未知的实体为空。
    /// </summary>
    public static CombatResolvedName? MonsterLabel(CombatEntity? entity) =>
        entity is null ? null : Label(entity.Role, entity.RawName, entity.ActorType, entity.SheetId);
    public static CombatResolvedName? MonsterLabel(CombatEntityObservation? entity) =>
        entity is null ? null : Label(entity.Role, entity.RawName, entity.ActorType, entity.SheetId);
    private static CombatResolvedName? Label(OpenEnum<CombatEntityRole> role, string rawName, string? actorType, UInt64String? sheet)
    {
        if (role.KnownValue == CombatEntityRole.Monster && MonsterSheetId(rawName) is { } monster) return Pending(Monster, monster);
        if (actorType == AiCompanion) return new(AiCompanion, new(0), null, Generic, GenericReason);
        return actorType is { } kind && ActorKinds.Contains(kind) && sheet is { Value: > 0 and <= uint.MaxValue } id ? Pending(kind, (uint)id.Value) : null;
    }
    /// <summary>名称占位是否可按当前名称表解析（怪物、技能、效果与各自动体表）。</summary>
    public static bool Resolvable(CombatResolvedName label) =>
        label.Kind is Monster or Skill or Effect || ActorKinds.Contains(label.Kind);
}
