using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Modules.CombatObservation.Application;

/// <summary>按客户端表 ID 查游戏原文名称；查不到时返回 unknown 或 unavailable 并带原因，不抛出。</summary>
public interface ICombatNameResolver
{
    CombatResolvedName Resolve(string kind, uint sheetId);
    /// <summary>效果的极性（客户端 Buff 表 AffectType）：buff、debuff、none；名称表未读取、表里没有该效果或列结构不符时为空。</summary>
    string? EffectPolarity(uint sheetId) => null;
}

/// <summary>
/// 读取侧投影：在返回给客户端的战斗数据上补齐怪物、技能与效果的原文名称，并为旧历史中因不完整而为空的 DPS 按已保存的伤害与 DPS 时长补算。
/// 只改变返回内容，不改写已保存的记录。
/// </summary>
public static partial class CombatNameProjection
{
    public static CombatWorkspace Project(CombatWorkspace value, ICombatNameResolver names) => value with
    {
        Encounter = value.Encounter is { } encounter ? Project(encounter, names) : null,
        Entities = value.Entities.Select(entity => Project(entity, names)).ToArray(),
        SelfEffects = value.SelfEffects is { } effects ? Project(effects, names) : null
    };
    public static CombatEncounterPage Project(CombatEncounterPage value, ICombatNameResolver names) =>
        value with { Items = value.Items.Select(item => Project(item, names)).ToArray() };
    public static CombatEncounter Project(CombatEncounter value, ICombatNameResolver names) => value with
    {
        Summary = LegacyRate(value.Summary),
        Skills = value.Skills.Select(group => SkillGroup(group, names)).ToArray(),
        Targets = value.Targets.Select(group => TargetGroup(group, names)).ToArray()
    };
    public static CombatEntity Project(CombatEntity value, ICombatNameResolver names) =>
        CombatNames.MonsterLabel(value) is { } label ? value with { ResolvedName = Resolve(label, names) } : value with { ResolvedName = null };
    public static CombatEntityEffects Project(CombatEntityEffects value, ICombatNameResolver names) => value with
    {
        Views = value.Views.Select(view => view with
        {
            ResolvedName = Effect(view.Instance.EffectId, names),
            Polarity = view.Instance.EffectId.Value is > 0 and <= uint.MaxValue ? names.EffectPolarity((uint)view.Instance.EffectId.Value) : null
        }).ToArray()
    };
    public static CombatTimelinePage Project(CombatTimelinePage value, ICombatNameResolver names) =>
        value with { Items = value.Items.Select(item => Project(item, names)).ToArray() };
    public static CombatTimelineItem Project(CombatTimelineItem value, ICombatNameResolver names) => value with
    {
        Entity = value.Entity is { } entity ? Project(entity, names) : null,
        SkillName = value.Damage is { } damage ? Skill(damage.SkillId, names) : null,
        EffectName = value.Effect is { } effect ? Effect(effect.EffectId, names) : null
    };
    public static CombatRangeSummary Project(CombatRangeSummary value, ICombatNameResolver names)
    {
        var targets = value.RangeTargets.Select(group => TargetGroup(group, names)).ToArray();
        return value with
        {
            Result = Project(value.Result, names), RangeTargets = targets,
            TargetResolvedName = value.TargetId is { } id ? targets.FirstOrDefault(group => group.Key == id)?.ResolvedName : null
        };
    }

    private static CombatDamageGroup SkillGroup(CombatDamageGroup group, ICombatNameResolver names) =>
        group with { ResolvedName = ulong.TryParse(group.Key, out var id) ? Skill(new(id), names) : null };
    private static CombatDamageGroup TargetGroup(CombatDamageGroup group, ICombatNameResolver names)
    {
        if (group.ResolvedName is { Kind: CombatNames.Monster } label && label.SheetId.Value <= uint.MaxValue)
            return group with { ResolvedName = Resolve(label, names) };
        // 旧历史没有名称占位：目标名为含字母 A–F 的 8 位十六进制码时才是怪物表码；全数字的名称无法与玩家实体 ID 区分，保持为空。
        return group with
        {
            ResolvedName = LegacyMonsterCode().IsMatch(group.Name) && CombatNames.MonsterSheetId(group.Name) is { } sheet
                ? names.Resolve(CombatNames.Monster, sheet) : null
        };
    }
    private static CombatResolvedName Resolve(CombatResolvedName label, ICombatNameResolver names) =>
        names.Resolve(label.Kind, checked((uint)label.SheetId.Value));
    private static CombatResolvedName Skill(UInt64String id, ICombatNameResolver names) =>
        id.Value == 0 ? new(CombatNames.Skill, id, null, CombatNames.Unknown, "combat.skillUnmatched") :
        id.Value > uint.MaxValue ? new(CombatNames.Skill, id, null, CombatNames.Unknown, "combat.nameNotFound") :
        names.Resolve(CombatNames.Skill, (uint)id.Value);
    private static CombatResolvedName Effect(UInt64String id, ICombatNameResolver names) =>
        id.Value is 0 or > uint.MaxValue ? new(CombatNames.Effect, id, null, CombatNames.Unknown, "combat.nameNotFound") :
        names.Resolve(CombatNames.Effect, (uint)id.Value);
    /// <summary>旧版本在分段不完整时不给 DPS：按已保存的伤害与 DPS 时长补算，quality 与 reasons 保持原样。</summary>
    private static CombatSummary LegacyRate(CombatSummary summary)
    {
        if (summary.EncounterDps is not null || summary.Quality.KnownValue != ObservationQuality.Partial ||
            summary.Reasons.Contains("rateOutOfRange") || summary.Reasons.Contains("insufficientDuration")) return summary;
        return summary with { EncounterDps = CombatRate.Calculate(summary.AttributedDamage.Value, summary.DpsDurationMs ?? summary.DurationMs) };
    }
    [GeneratedRegex(@"\A(?=[0-9A-F]*[A-F])[0-9A-F]{8}\z", RegexOptions.CultureInvariant)] private static partial Regex LegacyMonsterCode();
}
