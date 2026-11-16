using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

/// <summary>治疗与伤害共用分段：分段内计入、分段外与切图等待期间不计入、不开始或延续分段，以及容量上限与旧历史。</summary>
public sealed class CombatHealingSessionTests
{
    private static readonly EntityKey Ally = Self with { RawInstanceId = new(77) };

    private static CombatSession Healing(CombatLimits? limits = null, int idleTimeoutMs = 300000) => new("client-a", "obs-test", Source,
        Entity(Self, CombatEntityRole.LocalPlayer, "測試角色"), new(Self.GameSessionId, IdleTimeoutMs: idleTimeoutMs), At, limits, healing: true);
    private static CombatHealEvent Cast(string id, double mono, ulong amount, EntityKey? target = null, bool merged = false,
        ulong? effective = null) => new(id, CombatHealDirection.Cast, target ?? Self, new(0x2000), new(amount),
            effective is { } e ? new(e) : null, effective is { } known ? new(amount - known) : null, merged,
            At.AddMilliseconds(mono), mono, null, ObservationQuality.Confirmed, merged && effective is null ? "hp-unpaired" : null);
    private static CombatHealEvent Received(string id, double mono, ulong amount, ulong? effective = null, string? reason = "hp-unpaired") =>
        new(id, CombatHealDirection.Received, null, new(0x2000), new(amount), effective is { } e ? new(e) : null,
            effective is { } known ? new(amount - known) : null, false, At.AddMilliseconds(mono), mono, null, ObservationQuality.Confirmed,
            effective is null ? reason : null);
    private static CombatFrame Frame(ulong sequence, double mono, IReadOnlyList<CombatHealEvent> healing,
        IReadOnlyList<CombatDamageEvent>? damage = null, IReadOnlyList<CombatGapInput>? gaps = null,
        IReadOnlyList<CombatEntityObservation>? entities = null) =>
        CombatSessionTests.Frame(sequence, mono, damage, entities: entities ?? [Entity(Monster, CombatEntityRole.Monster),
            Entity(Ally, CombatEntityRole.Player, "芙蕾雅")], gaps: gaps) with { Healing = healing };

    [Fact]
    public void HealingInsideTheSegmentIsSplitBySelfAndOthersWithEffectiveTotals()
    {
        var session = Healing();
        session.Apply(Frame(1, 0, [], [Damage("first", 0)]));
        session.Apply(Frame(2, 1000, [Cast("self", 1000, 320, merged: true, effective: 300),
            Cast("ally", 1000, 500, Ally), Received("other", 1000, 1232, effective: 68)], [Damage("second", 1000)]));
        var healing = session.Capture().Workspace.Encounter!.Healing!;
        Assert.Equal(("observed-healing-v1", "820", 2UL), (healing.CalculationVersion, healing.CastAmount.ToString(), healing.CastCount.Value));
        Assert.Equal(("320", "500"), (healing.CastSelfAmount.ToString(), healing.CastOtherAmount.ToString()));
        Assert.Equal(("1552", 2UL), (healing.ReceivedAmount.ToString(), healing.ReceivedCount.Value));
        Assert.Equal(("320", "1232"), (healing.ReceivedSelfAmount.ToString(), healing.ReceivedOtherAmount.ToString()));
        Assert.Equal(("368", "1184", 2UL), (healing.ReceivedEffective!.ToString(), healing.ReceivedOverheal!.ToString(), healing.PairedReceivedCount.Value));
        Assert.Null(healing.EffectiveReason);
        Assert.Equal(820, healing.EncounterHps!.Value, 6);
        var targets = healing.Targets!;
        Assert.Equal(2, targets.Count);
        Assert.True(targets.Single(x => x.Self).Amount.ToString() == "320");
        Assert.Equal("芙蕾雅", targets.Single(x => !x.Self).Name);
        // 摘要事件不带目标分组。
        Assert.Null(session.Capture().Workspace.Encounter!.Summary.Healing!.Targets);

        // 停手后的治疗继续累计并把 HPS 截止点推到最后一次施放；伤害分母保持最后伤害时点。
        session.Apply(Frame(3, 2000, [Cast("after-damage", 2000, 180, Ally)]));
        var afterHealing = session.Capture().Workspace.Encounter!;
        Assert.Equal(2000, afterHealing.Summary.DurationMs); Assert.Equal(1000, afterHealing.Summary.DpsDurationMs);
        Assert.Equal(200, afterHealing.Summary.EncounterDps); Assert.Equal(200, afterHealing.Summary.RollingDps);
        Assert.Equal(500, afterHealing.Healing!.EncounterHps!.Value, 6); Assert.Equal(500, afterHealing.Healing.RollingHps!.Value, 6);
        Assert.Equal(2000, afterHealing.Healing.HpsDurationMs);
        // 停手期间 HPS 与 DPS 一样冻结：分母、近期窗口与数值都停在最后一次有效伤害或施放治疗。
        session.Tick(At.AddMilliseconds(63000), 63000);
        var paused = session.Capture().Workspace.Encounter!;
        Assert.Equal(63000, paused.Summary.DurationMs); Assert.Equal(1000, paused.Summary.DpsDurationMs);
        Assert.Equal(afterHealing.Summary.EncounterDps, paused.Summary.EncounterDps);
        Assert.Equal(afterHealing.Summary.RollingDps, paused.Summary.RollingDps);
        Assert.Equal(2000, paused.Healing!.HpsDurationMs);
        Assert.Equal(500, paused.Healing.EncounterHps!.Value, 6);
        Assert.Equal(500, paused.Healing.RollingHps!.Value, 6);
    }

    [Fact]
    public void UnpairedReceiptsLeaveEffectiveAndOverhealUnknown()
    {
        var session = Healing();
        session.Apply(Frame(1, 0, [Received("paired", 0, 503, effective: 503), Received("full", 0, 419)], [Damage("first", 0)]));
        var healing = session.Capture().Workspace.Encounter!.Healing!;
        Assert.Equal("922", healing.ReceivedAmount.ToString());
        Assert.Null(healing.ReceivedEffective); Assert.Null(healing.ReceivedOverheal);
        Assert.Equal(("hp-unpaired", 1UL), (healing.EffectiveReason, healing.PairedReceivedCount.Value));
    }

    [Fact]
    public void HealingOutsideSegmentsNeitherCountsNorStartsOrExtendsOne()
    {
        var session = Healing(idleTimeoutMs: 1000);
        session.Apply(Frame(1, 0, [Cast("before", 0, 100)]));
        Assert.Null(session.Capture().Workspace.Encounter);
        session.Apply(Frame(2, 100, [], [Damage("first", 100)]));
        session.Apply(Frame(3, 600, [Cast("inside", 600, 40)]));
        // 空闲阈值后的治疗不计入，也不推迟空闲结束。
        session.Apply(Frame(4, 1200, [Cast("idle", 1200, 900)]));
        var encounter = session.Capture().Workspace.Encounter!;
        Assert.Equal("40", encounter.Healing!.CastAmount.ToString());
        session.Tick(At.AddMilliseconds(1300), 1300);
        var closed = session.Capture().Workspace.Encounter!;
        Assert.Equal("idle-timeout", closed.Summary.BoundaryReason);
        Assert.Equal(100, closed.EndedMonotonicMs);
        session.Apply(Frame(5, 1400, [Cast("after", 1400, 7)]));
        Assert.Equal("40", session.Capture().Workspace.Encounter!.Healing!.CastAmount.ToString());
    }

    [Fact]
    public void ConnectionChangeEndsHealingWithTheSegmentAndTheNextMapStartsFromZero()
    {
        var session = Healing();
        session.Apply(Frame(1, 0, [Cast("old", 0, 100)], [Damage("first", 0)]));
        var first = session.Capture().Workspace.Encounter!.Id;
        session.Apply(Frame(2, 100, [Cast("late", 100, 50)], gaps: [new("combat.streamGap.connectionClosed", 100, 100, new(0))]));
        var next = Self with { ObservationGeneration = new(2) };
        var monster = Monster with { ObservationGeneration = new(2) };
        var resumed = new CombatFrame(1, Source, Self.GameSessionId, new(2), new(1), At.AddMilliseconds(500), 500,
            [Entity(next, CombatEntityRole.LocalPlayer, "測試角色"), Entity(monster, CombatEntityRole.Monster)], [], [], [], [],
            Healing: [new("waiting", CombatHealDirection.Cast, next, new(0x2000), new(999), null, null, false, At.AddMilliseconds(500), 500, null,
                ObservationQuality.Confirmed)]);
        session.Apply(resumed);
        var checkpoint = session.Capture();
        Assert.Equal("150", checkpoint.Encounters.Single(x => x.Id == first).Healing!.CastAmount.ToString());
        Assert.Equal("connection-changed", checkpoint.Encounters.Single(x => x.Id == first).Summary.BoundaryReason);
        session.Apply(new CombatFrame(1, Source, Self.GameSessionId, new(2), new(2), At.AddMilliseconds(700), 700,
            [Entity(monster, CombatEntityRole.Monster)], [Damage("new-map", 700, from: next, target: monster) with { Sequence = new(2) }], [], [], [],
            Healing: [new("new", CombatHealDirection.Cast, next, new(0x2000), new(5), null, null, false, At.AddMilliseconds(700), 700, null,
                ObservationQuality.Confirmed)]));
        var current = session.Capture().Workspace.Encounter!;
        Assert.NotEqual(first, current.Id);
        Assert.Equal("map-change", current.Segment!.StartReason);
        Assert.Equal("5", current.Healing!.CastAmount.ToString());
    }

    [Fact]
    public void RollingAndTargetCapacityAreBoundedWithReasons()
    {
        var session = Healing(new CombatLimits(MaximumDeduplicationEntries: 10));
        session.Apply(Frame(1, 0, [], [Damage("first", 0)]));
        // 每 4 秒一次、持续 60 秒：去重身份 30 秒过期，滚动缓存保留 60 秒，第 11 条起挤出最旧条目。
        for (var i = 1; i <= 15; i++)
            session.Apply(Frame((ulong)i + 1, i * 4000, [Cast("h" + i, i * 4000, 10)]));
        var healing = session.Capture().Workspace.Encounter!.Healing!;
        Assert.Equal("150", healing.CastAmount.ToString());
        Assert.Null(healing.RollingHps);
        Assert.Contains("healing-rolling-capacity", healing.Reasons);
        Assert.NotNull(healing.EncounterHps);

        var wide = Healing();
        wide.Apply(Frame(1, 0, [], [Damage("first", 0)]));
        var allies = Enumerable.Range(0, 70).Select(i => Self with { RawInstanceId = new(1000 + (ulong)i) }).ToArray();
        wide.Apply(Frame(2, 10, allies.Select((ally, i) => Cast("t" + i, 10, 1, ally)).ToArray(),
            entities: [Entity(Monster, CombatEntityRole.Monster), .. allies.Select(ally => Entity(ally, CombatEntityRole.Player))]));
        var grouped = wide.Capture().Workspace.Encounter!.Healing!;
        Assert.Equal(64, grouped.Targets!.Count);
        Assert.Equal(7UL, grouped.Targets.Single(x => x.Key == "other-targets").Count.Value);
        Assert.Equal("70", grouped.CastAmount.ToString());
        Assert.Contains("healing-group-capacity", grouped.Reasons);
    }

    [Fact]
    public void DuplicateHealIsCountedOnceAndPartialSegmentsKeepRates()
    {
        var session = Healing();
        session.Apply(Frame(1, 0, [], [Damage("first", 0)]));
        session.Apply(Frame(2, 1000, [Cast("same", 1000, 100)]));
        session.Apply(Frame(3, 1100, [Cast("same", 1000, 100) with { MonotonicMs = 1100 }]));
        session.Apply(Frame(4, 1200, [], gaps: [new("combat.captureDrops", 1200, 1200, new(1))]));
        var healing = session.Capture().Workspace.Encounter!.Healing!;
        Assert.Equal(("100", 1UL), (healing.CastAmount.ToString(), healing.CastCount.Value));
        // 分段不完整时每秒治疗量仍按已接受的施放量给出，分段 quality 标注不完整。
        Assert.Equal(100 * 1000d / 1000, healing.EncounterHps); Assert.Equal(100 * 1000d / 1000, healing.RollingHps);
    }

    [Fact]
    public void SourcesWithoutHealingAndLegacyDocumentsReturnNoHealingBlock()
    {
        var plain = Session();
        plain.Apply(Frame(1, 0, [Cast("ignored", 0, 100)], [Damage("first", 0)]));
        var encounter = plain.Capture().Workspace.Encounter!;
        Assert.Null(encounter.Healing); Assert.Null(encounter.Summary.Healing);
        Assert.DoesNotContain("healing", JsonSerializer.Serialize(encounter, ContractJson.Options));

        var session = Healing();
        session.Apply(Frame(1, 0, [Cast("kept", 0, 100)], [Damage("first", 0)]));
        var legacy = JsonSerializer.SerializeToNode(session.Capture().Workspace.Encounter!, ContractJson.Options)!.AsObject();
        legacy.Remove("healing"); legacy["summary"]!.AsObject().Remove("healing");
        var restored = JsonSerializer.Deserialize<CombatEncounter>(legacy.ToJsonString(), ContractJson.Options)!;
        Assert.Null(restored.Healing); Assert.Null(restored.Summary.Healing);
        Assert.Equal("100", restored.Summary.AttributedDamage.ToString());
    }
}
