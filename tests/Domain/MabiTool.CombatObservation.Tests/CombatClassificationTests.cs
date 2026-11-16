// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using MabiTool.Contracts;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

public sealed class CombatClassificationTests
{
    private static CombatDamageEvent Classified(string id, ulong amount, CombatDamageClassification classification) =>
        Damage(id, 0, amount) with { Classification = classification, Hit = true, Critical = true,
            PowerfulHit = true, RapidHit = true, DoubleHit = true };

    [Fact]
    public void MixedSkillUsesMatchingEligibilityForNumeratorsAndDenominators()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Classified("direct", 10, CombatDamageClassification.Direct),
            Classified("indirect", 20, CombatDamageClassification.Indirect),
            Classified("dot", 30, CombatDamageClassification.DamageOverTime)]));
        foreach (var group in session.Capture().Workspace.Encounter!.Skills.Concat(session.Capture().Workspace.Encounter!.Targets))
        {
            Assert.Equal(new CombatDamageClassificationCounts(new(1), new(1), new(1), new(0)), group.ClassificationCounts);
            Assert.Equal(new UInt64String(2), group.CriticalCount);
            Assert.Equal(new UInt64String(2), group.CriticalEligibleCount);
            Assert.Equal(new UInt64String(1), group.MainHitEligibleCount);
            Assert.Equal(new UInt64String(1), group.PowerfulHitCount);
            Assert.Equal(new UInt64String(1), group.RapidHitCount);
            Assert.Equal(new UInt64String(1), group.DoubleHitCount);
            Assert.Equal(new UInt64String(3), group.HitCount);
            Assert.Equal("60", group.AttributedDamage.ToString());
            Assert.Equal(new UInt64String(10), group.MinimumDamage);
            Assert.Equal(new UInt64String(30), group.MaximumDamage);
        }
    }

    [Fact]
    public void DamageOverTimeWithUnknownTraitsHasKnownZeroCoverageCounts()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Classified("dot", 30, CombatDamageClassification.DamageOverTime) with
            { Critical = null, PowerfulHit = null, RapidHit = null, DoubleHit = null }]));
        var group = Assert.Single(session.Capture().Workspace.Encounter!.Skills);
        Assert.Equal(new CombatDamageClassificationCounts(new(0), new(0), new(1), new(0)), group.ClassificationCounts);
        Assert.Equal(new UInt64String(0), group.CriticalCount);
        Assert.Equal(new UInt64String(0), group.CriticalEligibleCount);
        Assert.Equal(new UInt64String(0), group.MainHitEligibleCount);
        Assert.Equal(new UInt64String(0), group.PowerfulHitCount);
        Assert.Equal(new UInt64String(0), group.RapidHitCount);
        Assert.Equal(new UInt64String(0), group.DoubleHitCount);
    }

    [Fact]
    public void UnknownEligibleTraitPreservesKnownDenominatorsAndOtherNumerators()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Classified("direct", 30, CombatDamageClassification.Direct) with
            { Critical = null, PowerfulHit = null, RapidHit = false }]));
        var group = Assert.Single(session.Capture().Workspace.Encounter!.Skills);
        Assert.Null(group.CriticalCount); Assert.Null(group.PowerfulHitCount);
        Assert.Equal(new UInt64String(1), group.CriticalEligibleCount);
        Assert.Equal(new UInt64String(1), group.MainHitEligibleCount);
        Assert.Equal(new UInt64String(0), group.RapidHitCount);
        Assert.Equal(new UInt64String(1), group.DoubleHitCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrFutureClassificationKeepsAffectedCoverageUnknown(bool future)
    {
        var session = Session();
        var unknown = Classified("unknown", 20, CombatDamageClassification.Unknown) with
            { Classification = future ? new OpenEnum<CombatDamageClassification>("future-classification") : null };
        session.Apply(Frame(1, 0, [Classified("direct", 10, CombatDamageClassification.Direct), unknown]));
        var checkpoint = session.Capture();
        var group = Assert.Single(checkpoint.Workspace.Encounter!.Skills);
        Assert.Equal(new CombatDamageClassificationCounts(new(1), new(0), new(0), new(1)), group.ClassificationCounts);
        Assert.Null(group.CriticalEligibleCount); Assert.Null(group.MainHitEligibleCount);
        Assert.Null(group.CriticalCount); Assert.Null(group.PowerfulHitCount);
        Assert.Null(group.RapidHitCount); Assert.Null(group.DoubleHitCount);
        Assert.Equal(new UInt64String(2), group.HitCount);
        Assert.Equal("30", group.AttributedDamage.ToString());
        var normalized = Assert.Single(checkpoint.Timeline, value => value.Item.Damage?.EventId == "unknown").Item.Damage!;
        Assert.Equal(future ? "future-classification" : "unknown", normalized.Classification!.Value.RawValue);
    }

    [Fact]
    public void ClassificationParticipatesInDuplicateConflictIdentity()
    {
        var session = Session();
        var direct = Classified("one", 10, CombatDamageClassification.Direct);
        session.Apply(Frame(1, 0, [direct, direct]));
        session.Apply(Frame(2, 100, [direct with { Classification = CombatDamageClassification.Indirect }]));
        var encounter = session.Capture().Workspace.Encounter!;
        var group = Assert.Single(encounter.Skills);
        Assert.Equal(1UL, group.EventCount.Value);
        Assert.Equal(new CombatDamageClassificationCounts(new(1), new(0), new(0), new(0)), group.ClassificationCounts);
        Assert.Contains("damage-conflict", encounter.Summary.Reasons);
    }

    [Fact]
    public void SameNamedTargetsKeepIndependentClassificationAndCoverage()
    {
        var session = Session();
        var other = Monster with { RawInstanceId = new(Monster.RawInstanceId.Value + 1) };
        session.Apply(Frame(1, 0, [Classified("direct", 10, CombatDamageClassification.Direct),
            Classified("dot", 20, CombatDamageClassification.DamageOverTime) with { TargetEntity = other }],
            entities: [Entity(Monster, CombatEntityRole.Monster), Entity(other, CombatEntityRole.Monster)]));
        var encounter = session.Capture().Workspace.Encounter!;
        Assert.Equal(2, encounter.Targets.Count);
        Assert.Single(encounter.Targets.Select(value => value.Name).Distinct());
        var direct = Assert.Single(encounter.Targets, value => value.ClassificationCounts!.Direct.Value == 1);
        var dot = Assert.Single(encounter.Targets, value => value.ClassificationCounts!.DamageOverTime.Value == 1);
        Assert.NotEqual(direct.Key, dot.Key);
        Assert.Equal(new UInt64String(1), direct.MainHitEligibleCount);
        Assert.Equal(new UInt64String(0), dot.MainHitEligibleCount);
        Assert.Equal(new CombatDamageClassificationCounts(new(1), new(0), new(1), new(0)), Assert.Single(encounter.Skills).ClassificationCounts);
    }
}
