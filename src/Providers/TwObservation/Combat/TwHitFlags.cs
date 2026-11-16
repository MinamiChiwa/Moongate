using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.Combat;

/// <summary>
/// 伤害飘字与技能表现携带的 <c>Shared.GameLogic.Hits.HitFlags</c>（Int64）。命名标记按 tw-20261110 客户端静态枚举核对：
/// Critical=0x1、BreakedHit（无防备）=0x4、BreakOccurred（破防）=0x8、FirstAttack=0x40、DefaultAttack=0x80、MultiHit=0x100、
/// PowerfulHit=0x200、RapidHit=0x400、AdditionalHit=0x800、DoubleHit=0x8000000，八种元素自 0x10000000 起依次左移一位。
/// 持续与间接伤害的分类位由实采归纳：0x1000000000 为持续伤害；非持续伤害同时带 0x800、0x8000、0x10000 时为间接伤害。
/// </summary>
internal readonly record struct TwHitFlags(ulong Bits)
{
    private const ulong CriticalBit = 0x1, BreakedHitBit = 0x4, BreakOccurredBit = 0x8, FirstAttackBit = 0x40,
        DefaultAttackBit = 0x80, MultiHitBit = 0x100, PowerfulHitBit = 0x200, RapidHitBit = 0x400, AdditionalHitBit = 0x800,
        DoubleHitBit = 0x8000000, FirstElementBit = 0x10000000;
    private const ulong DamageOverTimeBit = 0x1000000000, IndirectMask = 0x800 | 0x8000 | 0x10000;
    private const int ElementCount = 8;

    public bool Critical => Has(CriticalBit);
    public bool BreakedHit => Has(BreakedHitBit);
    public bool BreakOccurred => Has(BreakOccurredBit);
    public bool FirstAttack => Has(FirstAttackBit);
    public bool DefaultAttack => Has(DefaultAttackBit);
    public bool MultiHit => Has(MultiHitBit);
    public bool PowerfulHit => Has(PowerfulHitBit);
    public bool RapidHit => Has(RapidHitBit);
    public bool AdditionalHit => Has(AdditionalHitBit);
    public bool DoubleHit => Has(DoubleHitBit);
    public CombatDamageClassification Classification =>
        Has(DamageOverTimeBit) ? CombatDamageClassification.DamageOverTime :
        (Bits & IndirectMask) == IndirectMask ? CombatDamageClassification.Indirect : CombatDamageClassification.Direct;

    /// <summary>元素按 <see cref="CombatDamageElement"/> 声明顺序（bleed、dark、fire、holy、ice、lightning、poison、soul）列出。</summary>
    public List<OpenEnum<CombatDamageElement>> Elements()
    {
        var result = new List<OpenEnum<CombatDamageElement>>();
        for (var index = 0; index < ElementCount; index++)
            if (Has(FirstElementBit << index)) result.Add((CombatDamageElement)index);
        return result;
    }

    private bool Has(ulong bit) => (Bits & bit) != 0;
}

/// <summary>
/// Buff EndAt 在线上只携带 <c>ReplicatedTime.offsetInSeconds</c>，即发送时点的剩余秒数，Add 与 Refresh 相同。
/// 2026-11-10 留存样本中 403 组“最后一次 Add/Refresh → Expire”的有限值配对：140 组在该值到期后 0.05 s 内收到 Expire，
/// 其余均为提前移除，没有一组晚于该值 1 s 以上。常驻效果使用绝对值不小于 9999 的值（实测 -94608474112 与 99999）。
/// 零、大于 -9999 的负值及非有限值没有样本支撑，保持未知。
/// </summary>
internal static class TwBuffRemaining
{
    public const float PermanentThresholdSeconds = 9999;

    public static (EffectDurationKind Kind, double? DurationMs) Classify(float seconds)
    {
        if (!float.IsFinite(seconds)) return (EffectDurationKind.Unknown, null);
        if (Math.Abs(seconds) >= PermanentThresholdSeconds) return (EffectDurationKind.Infinite, null);
        return seconds > 0 ? (EffectDurationKind.Finite, seconds * 1000d) : (EffectDurationKind.Unknown, null);
    }
}
