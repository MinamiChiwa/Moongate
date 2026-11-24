// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Numerics;
using MabiTool.Contracts;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

public sealed class CombatDetailMetricsTests
{
    private static CombatDamageEvent Marked(string id, ulong amount, bool? powerful = false,
        bool? rapid = false, bool? followUp = false, int sub = 0) => Damage(id, 0, amount, sub: sub) with
        { Hit = true, Critical = false, PowerfulHit = powerful, RapidHit = rapid, DoubleHit = followUp,
          Classification = CombatDamageClassification.Direct };

    [Fact]
    public void OverlappingTraitsAreCountedIndependentlyInSkillAndTargetGroups()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Marked("all", 40, true, true, true),
            Marked("powerful", 10, true), Marked("rapid", 20, rapid: true), Marked("follow-up", 30, followUp: true)]));
        var encounter = session.Capture().Workspace.Encounter!;
        foreach (var group in encounter.Skills.Concat(encounter.Targets))
        {
            Assert.Equal(4UL, group.EventCount.Value);
            Assert.Equal(2UL, group.PowerfulHitCount!.Value.Value);
            Assert.Equal(2UL, group.RapidHitCount!.Value.Value);
            Assert.Equal(2UL, group.DoubleHitCount!.Value.Value);
            Assert.Equal(10UL, group.MinimumDamage!.Value.Value);
            Assert.Equal(40UL, group.MaximumDamage!.Value.Value);
        }
    }

    [Fact]
    public void UnknownTraitOnlyMakesItsOwnCountUnknownAndKnownZeroStaysZero()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Marked("known", 20, true, followUp: true),
            Marked("unknown-powerful", 10, powerful: null)]));
        foreach (var group in session.Capture().Workspace.Encounter!.Skills)
        {
            Assert.Null(group.PowerfulHitCount);
            Assert.Equal(0UL, group.RapidHitCount!.Value.Value);
            Assert.Equal(1UL, group.DoubleHitCount!.Value.Value);
            Assert.Equal(10UL, group.MinimumDamage!.Value.Value);
            Assert.Equal(20UL, group.MaximumDamage!.Value.Value);
        }
    }

    [Fact]
    public void DamageRangeKeepsFullUInt64AndUsesAllAttributedAmountKinds()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Marked("maximum", ulong.MaxValue),
            Marked("shield", 50) with { AmountKind = CombatDamageKind.Shield },
            Marked("overkill", 1) with { AmountKind = CombatDamageKind.Overkill },
            Marked("unclassified", 9) with { AmountKind = CombatDamageKind.Unclassified }]));
        var group = Assert.Single(session.Capture().Workspace.Encounter!.Skills);
        Assert.Equal(1UL, group.MinimumDamage!.Value.Value);
        Assert.Equal(ulong.MaxValue.ToString(), group.MaximumDamage!.Value.ToString());
        Assert.Equal(new BigInteger(ulong.MaxValue) + 60, group.AttributedDamage.Value);
        Assert.Equal("50", group.Amounts.Shield.ToString());
        Assert.Equal("1", group.Amounts.Overkill.ToString());
        Assert.Equal("9", group.Amounts.Unclassified.ToString());
    }

    [Fact]
    public void DuplicateSubeventsDoNotChangeCountsOrRangeAndTraitConflictsAreReported()
    {
        var session = Session();
        var first = Marked("multi", 10, true, true, true);
        var second = Marked("multi", 20, sub: 1);
        session.Apply(Frame(1, 0, [first, second, second]));
        session.Apply(Frame(2, 100, [second with { MonotonicMs = 100, PowerfulHit = true }]));
        var encounter = session.Capture().Workspace.Encounter!;
        var group = Assert.Single(encounter.Skills);
        Assert.Equal(2UL, group.EventCount.Value);
        Assert.Equal(1UL, group.PowerfulHitCount!.Value.Value);
        Assert.Equal(1UL, group.RapidHitCount!.Value.Value);
        Assert.Equal(1UL, group.DoubleHitCount!.Value.Value);
        Assert.Equal(10UL, group.MinimumDamage!.Value.Value);
        Assert.Equal(20UL, group.MaximumDamage!.Value.Value);
        Assert.Contains("damage-conflict", encounter.Summary.Reasons);
        Assert.Equal(ObservationQuality.Partial, encounter.Summary.Quality.KnownValue);
    }

    [Fact]
    public void RangeAndTraitsExcludeZeroUnknownAndOwnedDamageAndKeepTeammateSkillsInTheirOwnGroup()
    {
        var session = Session(owned: false);
        var pet = Self with { RawInstanceId = new(42) };
        var unknown = Self with { RawInstanceId = new(43) };
        var caster = Self with { RawInstanceId = new(44) };
        session.Apply(Frame(1, 0, [Marked("local", 30), Marked("zero", 0, true, true, true),
            Marked("owned", 1, true, true, true) with { SourceEntity = pet },
            Marked("unknown", 1000, true, true, true) with { SourceEntity = unknown },
            Marked("chain", 10000, true, true, true) with { SkillCasterKey = caster, Chain = true }],
            entities: [Entity(Monster, CombatEntityRole.Monster), Entity(pet, CombatEntityRole.Owned, owner: Self, confirmed: true)]));
        var encounter = session.Capture().Workspace.Encounter!;
        // 本机攻击者配到他人施放的表现（连携）计入本机，单列为队友技能分组；本机自己的技能分组不含它。
        Assert.Equal("10030", encounter.Summary.AttributedDamage.ToString());
        var group = Assert.Single(encounter.Skills, item => item.Teammate is null);
        Assert.Equal(1UL, group.EventCount.Value);
        Assert.Equal("30", group.AttributedDamage.ToString());
        Assert.Equal(0UL, group.PowerfulHitCount!.Value.Value);
        Assert.Equal(0UL, group.RapidHitCount!.Value.Value);
        Assert.Equal(0UL, group.DoubleHitCount!.Value.Value);
        Assert.Equal(30UL, group.MinimumDamage!.Value.Value);
        Assert.Equal(30UL, group.MaximumDamage!.Value.Value);
        var teammate = Assert.Single(encounter.Skills, item => item.Teammate == true);
        Assert.Equal(("1000:teammate", "1000", 1UL, "10000"), (teammate.Key, teammate.Name, teammate.EventCount.Value, teammate.AttributedDamage.ToString()));
        Assert.Equal((1UL, 1UL, 1UL), (teammate.PowerfulHitCount!.Value.Value, teammate.RapidHitCount!.Value.Value, teammate.DoubleHitCount!.Value.Value));
    }

    [Fact]
    public void SameNamedMonstersKeepIndependentGaugeValuesAndGaugeChangesEnterTheTimeline()
    {
        var session = Session();
        var other = Monster with { RawInstanceId = new(Monster.RawInstanceId.Value + 1) };
        var first = Entity(Monster, CombatEntityRole.Monster) with { BreakGauge = new(20.5, new(100), At, ObservationQuality.Confirmed) };
        var second = Entity(other, CombatEntityRole.Monster) with { BreakGauge = new(60, new(200), At, ObservationQuality.Confirmed) };
        session.Apply(Frame(1, 0, [Marked("start", 10)], entities: [first, second]));
        session.Apply(Frame(2, 1000, entities: [first with { BreakGauge = first.BreakGauge! with { Current = 30, ObservedAt = At.AddMilliseconds(1000) } },
            Entity(other, CombatEntityRole.Monster)]));
        var checkpoint = session.Capture();
        var entities = checkpoint.Workspace.Entities.ToDictionary(entity => entity.Key);
        Assert.Equal(30, entities[Monster].BreakGauge!.Current);
        Assert.Equal(100UL, entities[Monster].BreakGauge!.Maximum!.Value.Value);
        Assert.Equal(60, entities[other].BreakGauge!.Current);
        Assert.Equal(200UL, entities[other].BreakGauge!.Maximum!.Value.Value);
        Assert.Contains(checkpoint.Timeline, record => record.Item.OffsetMs == 1000 && record.Item.Entity is { } entity && entity.Key == Monster &&
            entity.BreakGauge?.Current == 30);
    }

    [Fact]
    public void GaugeGapsRequireFreshObservationsAndGenerationChangesDiscardOldValues()
    {
        var session = Session();
        var baseline = Entity(Monster, CombatEntityRole.Monster) with { BreakGauge = new(20, new(100), At, ObservationQuality.Confirmed) };
        session.Apply(Frame(1, 0, [Marked("start", 10)], entities: [baseline]));
        session.ReportGap("capture-gap", 0, null, 0);
        session.Apply(Frame(2, 1000, entities: [baseline]));
        Assert.True(session.Capture().Workspace.Entities.Single(entity => entity.Key == Monster).BreakGauge!.Stale);
        var fresh = baseline with { BreakGauge = baseline.BreakGauge! with { Current = 25, ObservedAt = At.AddMilliseconds(1200) } };
        session.Apply(Frame(3, 1200, entities: [fresh]));
        Assert.False(session.Capture().Workspace.Entities.Single(entity => entity.Key == Monster).BreakGauge!.Stale);
        session.Apply(Frame(4, 1400, entities: [Entity(Monster, CombatEntityRole.Monster) with { InScope = false }]));
        Assert.True(session.Capture().Workspace.Entities.Single(entity => entity.Key == Monster).BreakGauge!.Stale);
        var local = Self with { ObservationGeneration = new(2) };
        var target = Monster with { ObservationGeneration = new(2) };
        session.Apply(Frame(1, 1600, entities: [Entity(local, CombatEntityRole.LocalPlayer), Entity(target, CombatEntityRole.Monster)]) with { Generation = new(2) });
        Assert.All(session.Capture().Workspace.Entities, entity => Assert.Null(entity.BreakGauge));
    }

    [Fact]
    public void StoppingObservationKeepsLastGaugeAsStale()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Marked("start", 10)], entities: [Entity(Monster, CombatEntityRole.Monster) with
            { BreakGauge = new(20, new(100), At, ObservationQuality.Confirmed) }]));
        session.Stop("user-stopped", At.AddMilliseconds(100), 100);
        var gauge = session.Capture().Workspace.Entities.Single(entity => entity.Key == Monster).BreakGauge!;
        Assert.Equal(20, gauge.Current);
        Assert.True(gauge.Stale);
        Assert.Equal("user-stopped", gauge.Reason);
    }

    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(-1d, false)]
    [InlineData(10d, true)]
    public void InvalidGaugeFieldsStayUnknownWithoutDiscardingOrdinaryDamage(double current, bool future)
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Marked("ordinary", 10)],
            entities: [Entity(Monster, CombatEntityRole.Monster) with
                { BreakGauge = new(current, new(100), future ? At.AddSeconds(1) : At, ObservationQuality.Confirmed) }]));
        var value = session.Capture();
        var gauge = value.Workspace.Entities.Single(entity => entity.Key == Monster).BreakGauge!;
        Assert.Null(gauge.Current);
        Assert.Null(gauge.Maximum);
        Assert.True(gauge.Stale);
        Assert.Equal(ObservationQuality.Unknown, gauge.Quality.KnownValue);
        Assert.Equal("break-gauge-invalid", gauge.Reason);
        Assert.Equal("10", value.Workspace.Encounter!.Summary.AttributedDamage.ToString());
        var normalized = Assert.Single(value.Timeline, record => record.Item.Damage is not null).Item.Damage!;
        Assert.Equal(10UL, normalized.Amount.Value);
    }

    [Fact]
    public void OversizedGaugeReasonsAreNormalizedAsUnknownFields()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Marked("ordinary", 10)],
            entities: [Entity(Monster, CombatEntityRole.Monster) with
                { BreakGauge = new(20, new(100), At, ObservationQuality.Confirmed, Reason: new string('x', 129)) }]));
        var checkpoint = session.Capture();
        Assert.Equal("10", checkpoint.Workspace.Encounter!.Summary.AttributedDamage.ToString());
        Assert.Equal("break-gauge-invalid", checkpoint.Workspace.Entities.Single(entity => entity.Key == Monster).BreakGauge!.Reason);
    }
}
