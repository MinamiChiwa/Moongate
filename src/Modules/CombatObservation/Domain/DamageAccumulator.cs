using System.Numerics;
using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

internal sealed class DamageAccumulator
{
    private const int ElementCount = 8;
    private readonly BigInteger[] amounts = new BigInteger[4];
    private readonly ulong[] classifications = new ulong[4];
    private readonly ulong[] elements = new ulong[ElementCount];
    // 带各元素与各标签的伤害合计：一条伤害可同时带多个标签，各项独立累计、可重叠。
    private readonly BigInteger[] elementDamage = new BigInteger[ElementCount];
    private BigInteger criticalDamage, powerfulDamage, rapidDamage, doubleDamage, breakOccurredDamage, firstAttackDamage, defaultAttackDamage, multiHitDamage, additionalHitDamage;
    private ulong events, hits, criticals, powerfulHits, rapidHits, doubleHits;
    private ulong breakedHits, breakOccurrences, firstAttacks, defaultAttacks, multiHits, additionalHits;
    private ulong criticalEligible, mainHitEligible;
    private bool allClassificationsKnown = true;
    private bool allHitsKnown = true, allCriticalsKnown = true;
    private bool allPowerfulHitsKnown = true, allRapidHitsKnown = true, allDoubleHitsKnown = true;
    private bool allBreakedHitsKnown = true, allBreakOccurrencesKnown = true, allFirstAttacksKnown = true,
        allDefaultAttacksKnown = true, allMultiHitsKnown = true, allElementsKnown = true, allAdditionalHitsKnown = true;
    private ulong? minimumDamage, maximumDamage;
    private BigInteger breakedDamage;
    // 本组第一次与最近一次计入伤害的观测时刻（目标分组即目标首次出现与最近受击的时刻）。
    private DateTimeOffset? firstAt, lastAt;
    public BigInteger Total => amounts.Aggregate(BigInteger.Zero, (sum, amount) => sum + amount);
    public void Add(CombatDamageEvent value)
    {
        var kind = value.AmountKind.KnownValue ?? CombatDamageKind.Unclassified;
        amounts[(int)kind] += value.Amount.Value;
        events = checked(events + 1);
        if (value.Hit is { } hit) { if (hit) hits = checked(hits + 1); } else allHitsKnown = false;
        var classification = value.Classification?.KnownValue ?? CombatDamageClassification.Unknown;
        classifications[(int)classification] = checked(classifications[(int)classification] + 1);
        switch (classification)
        {
            case CombatDamageClassification.Direct:
                mainHitEligible = checked(mainHitEligible + 1);
                Count(value.PowerfulHit, ref powerfulHits, ref allPowerfulHitsKnown, ref powerfulDamage, value.Amount.Value);
                Count(value.RapidHit, ref rapidHits, ref allRapidHitsKnown, ref rapidDamage, value.Amount.Value);
                Count(value.DoubleHit, ref doubleHits, ref allDoubleHitsKnown, ref doubleDamage, value.Amount.Value);
                goto case CombatDamageClassification.Indirect;
            case CombatDamageClassification.Indirect:
                criticalEligible = checked(criticalEligible + 1);
                Count(value.Critical, ref criticals, ref allCriticalsKnown, ref criticalDamage, value.Amount.Value);
                break;
            case CombatDamageClassification.Unknown:
                allClassificationsKnown = false;
                break;
        }
        // 无防备、破防、首击、普通攻击、多段与元素属性描述伤害本身，统计全部已归属伤害（直接、间接、持续）；
        // 分母为事件数，任一事件该标记未知时对应次数为空。同一元素在一条事件中只计一次。
        Count(value.BreakedHit, ref breakedHits, ref allBreakedHitsKnown);
        if (value.BreakedHit == true) breakedDamage += value.Amount.Value;
        Count(value.BreakOccurred, ref breakOccurrences, ref allBreakOccurrencesKnown, ref breakOccurredDamage, value.Amount.Value);
        Count(value.FirstAttack, ref firstAttacks, ref allFirstAttacksKnown, ref firstAttackDamage, value.Amount.Value);
        Count(value.DefaultAttack, ref defaultAttacks, ref allDefaultAttacksKnown, ref defaultAttackDamage, value.Amount.Value);
        Count(value.MultiHit, ref multiHits, ref allMultiHitsKnown, ref multiHitDamage, value.Amount.Value);
        Count(value.AdditionalHit, ref additionalHits, ref allAdditionalHitsKnown, ref additionalHitDamage, value.Amount.Value);
        if (value.Elements is { } present && present.All(element => element.IsKnown))
            foreach (var element in present.Select(element => (int)element.KnownValue!.Value).Distinct())
            {
                elements[element] = checked(elements[element] + 1);
                elementDamage[element] += value.Amount.Value;
            }
        else allElementsKnown = false;
        minimumDamage = minimumDamage is { } minimum ? Math.Min(minimum, value.Amount.Value) : value.Amount.Value;
        maximumDamage = maximumDamage is { } maximum ? Math.Max(maximum, value.Amount.Value) : value.Amount.Value;
        if (firstAt is null || value.ObservedAt < firstAt) firstAt = value.ObservedAt;
        if (lastAt is null || value.ObservedAt > lastAt) lastAt = value.ObservedAt;
    }
    private static void Count(bool? value, ref ulong count, ref bool known)
    {
        if (value is { } present) { if (present) count = checked(count + 1); }
        else known = false;
    }
    private static void Count(bool? value, ref ulong count, ref bool known, ref BigInteger damage, ulong amount)
    {
        Count(value, ref count, ref known);
        if (value == true) damage += amount;
    }
    private static DamageAmountString? Amount(bool known, BigInteger value) => known ? new(value) : null;
    private CombatDamageTagAmounts Tags() => new(
        Amount(allClassificationsKnown && allCriticalsKnown, criticalDamage),
        Amount(allClassificationsKnown && allPowerfulHitsKnown, powerfulDamage), Amount(allClassificationsKnown && allRapidHitsKnown, rapidDamage),
        Amount(allClassificationsKnown && allDoubleHitsKnown, doubleDamage), Amount(allBreakOccurrencesKnown, breakOccurredDamage),
        Amount(allFirstAttacksKnown, firstAttackDamage), Amount(allDefaultAttacksKnown, defaultAttackDamage), Amount(allMultiHitsKnown, multiHitDamage),
        allElementsKnown ? new(new(elementDamage[0]), new(elementDamage[1]), new(elementDamage[2]), new(elementDamage[3]),
            new(elementDamage[4]), new(elementDamage[5]), new(elementDamage[6]), new(elementDamage[7])) : null,
        Amount(allAdditionalHitsKnown, additionalHitDamage));
    private UInt64String? MainHit(bool known, ulong count) => allClassificationsKnown && known ? new(count) : null;
    public CombatDamageTotals Snapshot() => new(new(amounts[0]), new(amounts[1]), new(amounts[2]), new(amounts[3]));
    public CombatDamageGroup Group(string key, string name) => new(key, name, Snapshot(), new(Total), new(events),
        allHitsKnown ? new(hits) : null, allClassificationsKnown && allCriticalsKnown ? new(criticals) : null,
        MainHit(allPowerfulHitsKnown, powerfulHits), MainHit(allRapidHitsKnown, rapidHits), MainHit(allDoubleHitsKnown, doubleHits),
        minimumDamage is { } minimum ? new(minimum) : null, maximumDamage is { } maximum ? new(maximum) : null,
        new(new(classifications[0]), new(classifications[1]), new(classifications[2]), new(classifications[3])),
        allClassificationsKnown ? new(criticalEligible) : null, allClassificationsKnown ? new(mainHitEligible) : null,
        allBreakedHitsKnown ? new(breakedHits) : null, allBreakOccurrencesKnown ? new(breakOccurrences) : null,
        allFirstAttacksKnown ? new(firstAttacks) : null, allDefaultAttacksKnown ? new(defaultAttacks) : null,
        allMultiHitsKnown ? new(multiHits) : null,
        allElementsKnown ? new(new(elements[0]), new(elements[1]), new(elements[2]), new(elements[3]),
            new(elements[4]), new(elements[5]), new(elements[6]), new(elements[7])) : null,
        allBreakedHitsKnown ? new(breakedDamage) : null, FirstDamageAt: firstAt, LastDamageAt: lastAt, TagDamage: events == 0 ? null : Tags(),
        AdditionalHitCount: allAdditionalHitsKnown ? new(additionalHits) : null);
    /// <summary>并入另一组同口径的累计；任一侧未知的标记合并后仍为未知。</summary>
    public void Merge(DamageAccumulator other)
    {
        for (var i = 0; i < amounts.Length; i++) amounts[i] += other.amounts[i];
        for (var i = 0; i < classifications.Length; i++) classifications[i] = checked(classifications[i] + other.classifications[i]);
        for (var i = 0; i < elements.Length; i++) elements[i] = checked(elements[i] + other.elements[i]);
        events = checked(events + other.events); hits = checked(hits + other.hits); criticals = checked(criticals + other.criticals);
        powerfulHits = checked(powerfulHits + other.powerfulHits); rapidHits = checked(rapidHits + other.rapidHits);
        doubleHits = checked(doubleHits + other.doubleHits); breakedHits = checked(breakedHits + other.breakedHits);
        breakOccurrences = checked(breakOccurrences + other.breakOccurrences); firstAttacks = checked(firstAttacks + other.firstAttacks);
        defaultAttacks = checked(defaultAttacks + other.defaultAttacks); multiHits = checked(multiHits + other.multiHits);
        criticalEligible = checked(criticalEligible + other.criticalEligible); mainHitEligible = checked(mainHitEligible + other.mainHitEligible);
        breakedDamage += other.breakedDamage;
        for (var i = 0; i < elementDamage.Length; i++) elementDamage[i] += other.elementDamage[i];
        criticalDamage += other.criticalDamage; powerfulDamage += other.powerfulDamage; rapidDamage += other.rapidDamage; doubleDamage += other.doubleDamage;
        breakOccurredDamage += other.breakOccurredDamage; firstAttackDamage += other.firstAttackDamage;
        defaultAttackDamage += other.defaultAttackDamage; multiHitDamage += other.multiHitDamage;
        additionalHits = checked(additionalHits + other.additionalHits); additionalHitDamage += other.additionalHitDamage;
        allAdditionalHitsKnown &= other.allAdditionalHitsKnown;
        allClassificationsKnown &= other.allClassificationsKnown; allHitsKnown &= other.allHitsKnown; allCriticalsKnown &= other.allCriticalsKnown;
        allPowerfulHitsKnown &= other.allPowerfulHitsKnown; allRapidHitsKnown &= other.allRapidHitsKnown; allDoubleHitsKnown &= other.allDoubleHitsKnown;
        allBreakedHitsKnown &= other.allBreakedHitsKnown; allBreakOccurrencesKnown &= other.allBreakOccurrencesKnown;
        allFirstAttacksKnown &= other.allFirstAttacksKnown; allDefaultAttacksKnown &= other.allDefaultAttacksKnown;
        allMultiHitsKnown &= other.allMultiHitsKnown; allElementsKnown &= other.allElementsKnown;
        if (other.minimumDamage is { } minimum) minimumDamage = minimumDamage is { } current ? Math.Min(current, minimum) : minimum;
        if (other.maximumDamage is { } maximum) maximumDamage = maximumDamage is { } current ? Math.Max(current, maximum) : maximum;
        if (other.firstAt is { } first && (firstAt is null || first < firstAt)) firstAt = first;
        if (other.lastAt is { } last && (lastAt is null || last > lastAt)) lastAt = last;
    }
    public ulong Events => events;
    public static BigInteger TotalOf(CombatDamageTotals value) => value.ActualHp.Value + value.Shield.Value + value.Overkill.Value + value.Unclassified.Value;
}
