// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using MabiTool.Contracts;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

public sealed class CombatHitFlagTraitTests
{
    private static CombatDamageEvent Flagged(string id, ulong amount, CombatDamageClassification classification,
        params CombatDamageElement[] elements) =>
        Damage(id, 0, amount) with { Classification = classification, Hit = true, Critical = true,
            PowerfulHit = true, RapidHit = true, DoubleHit = true, BreakedHit = true, BreakOccurred = true,
            FirstAttack = true, DefaultAttack = true, MultiHit = true,
            Elements = elements.Select(value => (OpenEnum<CombatDamageElement>)value).ToArray() };
    private static CombatDamageElementCounts Elements(ulong bleed = 0, ulong dark = 0, ulong fire = 0, ulong holy = 0,
        ulong ice = 0, ulong lightning = 0, ulong poison = 0, ulong soul = 0) =>
        new(new(bleed), new(dark), new(fire), new(holy), new(ice), new(lightning), new(poison), new(soul));

    [Fact]
    public void AttackTraitsAndElementsCountEveryAttributedHitWhileMainHitStatsStayDirectOnly()
    {
        var session = Session();
        var direct = Flagged("direct", 10, CombatDamageClassification.Direct, CombatDamageElement.Fire, CombatDamageElement.Ice);
        session.Apply(Frame(1, 0, [direct, direct,
            Flagged("indirect", 20, CombatDamageClassification.Indirect, CombatDamageElement.Lightning),
            Flagged("dot", 30, CombatDamageClassification.DamageOverTime, CombatDamageElement.Lightning)]));
        var encounter = session.Capture().Workspace.Encounter!;
        foreach (var group in encounter.Skills.Concat(encounter.Targets))
        {
            Assert.Equal(new UInt64String(3), group.EventCount);
            Assert.Equal(new UInt64String(1), group.MainHitEligibleCount);
            foreach (var count in new[] { group.BreakedHitCount, group.BreakOccurredCount, group.FirstAttackCount,
                group.DefaultAttackCount, group.MultiHitCount })
                Assert.Equal(new UInt64String(3), count);
            foreach (var count in new[] { group.PowerfulHitCount, group.RapidHitCount, group.DoubleHitCount })
                Assert.Equal(new UInt64String(1), count);
            Assert.Equal(new UInt64String(2), group.CriticalCount);
            Assert.Equal(new UInt64String(2), group.CriticalEligibleCount);
            Assert.Equal(Elements(fire: 1, ice: 1, lightning: 2), group.ElementCounts);
            Assert.Equal("60", group.AttributedDamage.ToString());
        }
    }

    [Fact]
    public void UnknownTraitOrElementOnlyClearsThatCount()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Flagged("known", 10, CombatDamageClassification.Direct, CombatDamageElement.Dark),
            Flagged("unknown", 20, CombatDamageClassification.Direct) with { BreakedHit = null, MultiHit = null, Elements = null }]));
        var group = Assert.Single(session.Capture().Workspace.Encounter!.Skills);
        Assert.Null(group.BreakedHitCount); Assert.Null(group.MultiHitCount); Assert.Null(group.ElementCounts);
        Assert.Equal(new UInt64String(2), group.BreakOccurredCount);
        Assert.Equal(new UInt64String(2), group.FirstAttackCount);
        Assert.Equal(new UInt64String(2), group.MainHitEligibleCount);
        Assert.Equal(new UInt64String(2), group.PowerfulHitCount);
    }

    [Fact]
    public void EmptyElementListIsKnownZeroAndUnknownClassificationKeepsElementsKnown()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Flagged("none", 10, CombatDamageClassification.Direct),
            Flagged("unclassified", 20, CombatDamageClassification.Unknown, CombatDamageElement.Holy, CombatDamageElement.Soul)]));
        var group = Assert.Single(session.Capture().Workspace.Encounter!.Skills);
        Assert.Equal(Elements(holy: 1, soul: 1), group.ElementCounts);
        Assert.Null(group.MainHitEligibleCount); Assert.Null(group.PowerfulHitCount);
        Assert.Equal(new UInt64String(2), group.BreakedHitCount); Assert.Equal(new UInt64String(2), group.FirstAttackCount);
    }

    [Fact]
    public void AttackTraitsOfDamageOverTimeAndIndirectHitsAreCountedPerFlag()
    {
        var session = Session();
        var none = Flagged("direct", 10, CombatDamageClassification.Direct) with
            { BreakedHit = false, BreakOccurred = false, FirstAttack = false, DefaultAttack = false, MultiHit = false };
        session.Apply(Frame(1, 0, [none,
            none with { EventId = "dot", Classification = CombatDamageClassification.DamageOverTime, BreakOccurred = true, MultiHit = true },
            none with { EventId = "indirect", Classification = CombatDamageClassification.Indirect, DefaultAttack = true }]));
        var group = Assert.Single(session.Capture().Workspace.Encounter!.Skills);
        Assert.Equal(new UInt64String(3), group.EventCount); Assert.Equal(new UInt64String(1), group.MainHitEligibleCount);
        Assert.Equal(new UInt64String(0), group.BreakedHitCount); Assert.Equal(new UInt64String(1), group.BreakOccurredCount);
        Assert.Equal(new UInt64String(0), group.FirstAttackCount); Assert.Equal(new UInt64String(1), group.DefaultAttackCount);
        Assert.Equal(new UInt64String(1), group.MultiHitCount);
    }

    [Fact]
    public void FutureOrOverlongElementListsStayUnknownWithoutGuessing()
    {
        var session = Session();
        var future = Flagged("future", 10, CombatDamageClassification.Direct) with
            { Elements = [new OpenEnum<CombatDamageElement>("future-element")] };
        session.Apply(Frame(1, 0, [future]));
        var checkpoint = session.Capture();
        Assert.Null(Assert.Single(checkpoint.Workspace.Encounter!.Skills).ElementCounts);
        var stored = Assert.Single(checkpoint.Timeline, value => value.Item.Damage?.EventId == "future").Item.Damage!;
        Assert.Equal("future-element", Assert.Single(stored.Elements!).RawValue);

        var overlong = Session();
        overlong.Apply(Frame(1, 0, [Flagged("overlong", 10, CombatDamageClassification.Direct) with
            { Elements = Enumerable.Repeat((OpenEnum<CombatDamageElement>)CombatDamageElement.Ice, 9).ToArray() }]));
        var normalized = overlong.Capture();
        Assert.Null(Assert.Single(normalized.Workspace.Encounter!.Skills).ElementCounts);
        Assert.Null(Assert.Single(normalized.Timeline, value => value.Item.Damage?.EventId == "overlong").Item.Damage!.Elements);
    }

    [Fact]
    public void ChangedTraitsOrElementsOnARetransmittedEventAreAConflict()
    {
        var session = Session();
        var hit = Flagged("one", 10, CombatDamageClassification.Direct, CombatDamageElement.Fire);
        session.Apply(Frame(1, 0, [hit]));
        session.Apply(Frame(2, 100, [hit with { Elements = [CombatDamageElement.Ice] }]));
        session.Apply(Frame(3, 100, [hit with { FirstAttack = false }]));
        var encounter = session.Capture().Workspace.Encounter!;
        var group = Assert.Single(encounter.Skills);
        Assert.Equal(new UInt64String(1), group.EventCount);
        Assert.Equal(Elements(fire: 1), group.ElementCounts);
        Assert.Equal(new UInt64String(1), group.FirstAttackCount);
        Assert.Contains("damage-conflict", encounter.Summary.Reasons);
    }

    [Fact]
    public void SameNamedTargetsKeepIndependentTraitAndElementCounts()
    {
        var session = Session();
        var other = Monster with { RawInstanceId = new(Monster.RawInstanceId.Value + 1) };
        session.Apply(Frame(1, 0, [Flagged("fire", 10, CombatDamageClassification.Direct, CombatDamageElement.Fire),
            Flagged("poison", 20, CombatDamageClassification.DamageOverTime, CombatDamageElement.Poison) with { TargetEntity = other }],
            entities: [Entity(Monster, CombatEntityRole.Monster), Entity(other, CombatEntityRole.Monster)]));
        var encounter = session.Capture().Workspace.Encounter!;
        Assert.Equal(2, encounter.Targets.Count);
        Assert.Single(encounter.Targets.Select(value => value.Name).Distinct());
        var fire = Assert.Single(encounter.Targets, value => value.ElementCounts!.Fire.Value == 1);
        var poison = Assert.Single(encounter.Targets, value => value.ElementCounts!.Poison.Value == 1);
        Assert.Equal(new UInt64String(1), fire.BreakedHitCount);
        Assert.Equal(new UInt64String(1), poison.BreakedHitCount);
        Assert.Equal(Elements(fire: 1, poison: 1), Assert.Single(encounter.Skills).ElementCounts);
    }
}
