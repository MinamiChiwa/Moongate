using System.Numerics;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;

namespace MabiTool.CombatObservation.Tests;

public sealed class CombatSessionTests
{
    internal static readonly ObservationSource Source = new("normalized-test", "1.0", "TW", "fixture-v1");
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
    internal static readonly EntityKey Self = new("game-test", new(1), new(9007199254740993), new(1));
    internal static readonly EntityKey Monster = Self with { RawInstanceId = new(9007199254740995) };
    internal static CombatEntityObservation Entity(EntityKey key, CombatEntityRole role, string name = "同名目标", EntityKey? owner = null, bool confirmed = false) =>
        new(key, name, name, role, owner, confirmed, true, ObservationQuality.Confirmed);
    internal static CombatSession Session(bool owned = true, CombatLimits? limits = null, int rollingWindowMs = 60000) => new("client-a", "obs-test", Source,
        Entity(Self, CombatEntityRole.LocalPlayer, "測試角色"), new(Self.GameSessionId, IncludeOwnedEntities: owned, RollingWindowMs: rollingWindowMs), At, limits);
    internal static CombatDamageEvent Damage(string id, double mono, ulong amount = 100, EntityKey? from = null,
        EntityKey? target = null, int sub = 0, CombatDamageKind kind = CombatDamageKind.ActualHp) =>
        new(id, Source, from ?? Self, target ?? Monster, new(1000), new(amount), kind, At.AddMilliseconds(mono), mono, new(1), sub, null, null, ObservationQuality.Confirmed);
    internal static CombatEffectObservation Effect(EntityKey key, double mono, CombatEffectChangeKind kind = CombatEffectChangeKind.Apply,
        ulong instance = 200, EntityKey? caster = null, EffectDurationKind duration = EffectDurationKind.Finite, double? ms = 1000, int? stacks = 1) =>
        new(kind, key, new(100), new(instance), caster, duration, ms, stacks, "buff", caster is null, At.AddMilliseconds(mono), mono, ObservationQuality.Confirmed);
    internal static CombatFrame Frame(ulong sequence, double mono, IReadOnlyList<CombatDamageEvent>? damage = null,
        IReadOnlyList<CombatEffectObservation>? effects = null, IReadOnlyList<CombatEntityObservation>? entities = null,
        IReadOnlyList<CombatEffectReconciliation>? reconcile = null, IReadOnlyList<CombatGapInput>? gaps = null) =>
        new(1, Source, Self.GameSessionId, Self.ObservationGeneration, new(sequence), At.AddMilliseconds(mono), mono,
            entities ?? [Entity(Monster, CombatEntityRole.Monster)], damage ?? [], effects ?? [], reconcile ?? [], gaps ?? []);

    // 无人操作时来源每批重报相同实体：只有观测时刻推进，检查点不需要保存、效果文档不变脏；新实体或效果变化才需要保存，分段进行中每次都保存。
    [Fact]
    public void ReportedEntitiesWithOnlyNewObservationTimesDoNotRequireACheckpoint()
    {
        var session = Session();
        session.Apply(Frame(1, 0));
        var first = session.Capture(); Assert.True(first.Material); Assert.False(first.Urgent); session.Acknowledge(first);
        session.Apply(Frame(2, 200)); session.Tick(At.AddMilliseconds(400), 400);
        var repeated = session.Capture();
        Assert.False(repeated.Material); Assert.Empty(repeated.Effects);
        Assert.Equal(400, repeated.Workspace.Observation.MonotonicMs);
        Assert.Equal(At.AddMilliseconds(200), repeated.Workspace.Entities.Single(x => x.Key == Monster).LastObservedAt);
        var other = Monster with { RawInstanceId = new(Monster.RawInstanceId.Value + 2) };
        session.Apply(Frame(3, 600, entities: [Entity(Monster, CombatEntityRole.Monster), Entity(other, CombatEntityRole.Monster)]));
        var appeared = session.Capture(); Assert.True(appeared.Material); Assert.False(appeared.Urgent); session.Acknowledge(appeared);
        session.Apply(Frame(4, 800, effects: [Effect(Monster, 800)]));
        var effect = session.Capture(); Assert.True(effect.Material); Assert.Equal([CombatSession.EntityId(Monster)], effect.Effects.Select(x => x.Id)); session.Acknowledge(effect);
        session.Apply(Frame(5, 1000, [Damage("hit", 1000)]));
        var fighting = session.Capture(); Assert.True(fighting.Urgent); session.Acknowledge(fighting);
        session.Tick(At.AddMilliseconds(1200), 1200);
        Assert.True(session.Capture().Urgent);
    }

    [Theory]
    [InlineData(true, "140")]
    [InlineData(false, "100")]
    public void CompleteIdentityAndConfirmedOwnershipDetermineLocalContribution(bool includeOwned, string total)
    {
        var session = Session(includeOwned); var other = Monster with { RawInstanceId = new(Monster.RawInstanceId.Value + (1UL << 32)) };
        var pet = Self with { RawInstanceId = new(42) }; var unknown = Self with { RawInstanceId = new(43) };
        session.Apply(Frame(1, 0, [Damage("direct", 0), Damage("pet", 0, 40, pet, other, kind: CombatDamageKind.Shield), Damage("unknown", 0, 1000, unknown)],
            entities: [Entity(Monster, CombatEntityRole.Monster), Entity(other, CombatEntityRole.Monster),
                Entity(pet, CombatEntityRole.Owned, owner: Self, confirmed: true), Entity(unknown, CombatEntityRole.Owned)]));
        var value = session.Capture(); var encounter = value.Workspace.Encounter!;
        Assert.Equal(total, encounter.Summary.AttributedDamage.ToString()); Assert.Equal("40", encounter.OwnedDamage.Shield.ToString());
        Assert.Equal("1000", encounter.UnknownDamage.ActualHp.ToString());
        Assert.Equal(2, value.Workspace.Entities.Count(x => x.Role.KnownValue == CombatEntityRole.Monster));
        Assert.NotEqual(CombatSession.EntityId(Monster), CombatSession.EntityId(other));
        Assert.NotEqual(CombatSession.EntityId(Monster), CombatSession.EntityId(Monster with { SpawnGeneration = new(2) }));
        Assert.All(encounter.Skills, x => { Assert.Null(x.CriticalCount); Assert.Null(x.HitCount); });
    }
    [Fact]
    public void DuplicateIdentityPreservesLegitimateSameValueSubeventsAndFlagsConflicts()
    {
        var session = Session();
        session.Apply(Frame(1, 0, [Damage("multi", 0, sub: 0), Damage("multi", 0, sub: 1), Damage("multi", 0, sub: 1)]));
        session.Apply(Frame(2, 200, [Damage("multi", 200, sub: 0)]));
        Assert.Equal("200", session.Capture().Workspace.Encounter!.Summary.AttributedDamage.ToString());
        session.Apply(Frame(3, 300, [Damage("multi", 300, 999, sub: 0)]));
        var value = session.Capture().Workspace.Encounter!;
        Assert.Equal("200", value.Summary.AttributedDamage.ToString()); Assert.Equal(ObservationQuality.Partial, value.Summary.Quality.KnownValue);
        Assert.Contains("damage-conflict", value.Summary.Reasons);
    }
    [Fact]
    public void DamageTotalsRemainExactBeyondUInt64AndKindsStaySeparate()
    {
        var session = Session(); session.Apply(Frame(1, 0, [Damage("hp", 0, ulong.MaxValue), Damage("unclassified", 0, ulong.MaxValue, kind: CombatDamageKind.Unclassified)]));
        var value = session.Capture().Workspace.Encounter!;
        Assert.Equal(new BigInteger(ulong.MaxValue) * 2, value.Summary.AttributedDamage.Value);
        Assert.Equal(ulong.MaxValue.ToString(), value.Amounts.ActualHp.ToString()); Assert.Equal(ulong.MaxValue.ToString(), value.Amounts.Unclassified.ToString());
    }
    [Fact]
    public void LiveFinalAndRollingDpsUseTheirActualMonotonicDenominators()
    {
        // 本机保持 in-combat：整场不会到期，分段只按空闲阈值结束。
        var session = Session(rollingWindowMs: 5000);
        session.Apply(Frame(1, 0, [Damage("first", 0)]) with { CombatStates = [new(CombatLocalState.InCombat, At, 0, null)] });
        var first = session.Capture().Workspace.Encounter!;
        Assert.Null(first.Summary.EncounterDps); Assert.Null(first.Summary.RollingDps); Assert.Equal(0, first.Summary.DpsDurationMs);
        Assert.Contains("insufficientDuration", first.Summary.Reasons); Assert.Equal("observed-damage-v2", first.Summary.CalculationVersion);
        session.Tick(At.AddMilliseconds(500), 500);
        var firstPaused = session.Capture().Workspace.Encounter!.Summary;
        Assert.Equal(500, firstPaused.DurationMs); Assert.Equal(0, firstPaused.DpsDurationMs); Assert.Null(firstPaused.EncounterDps);
        session.Apply(Frame(2, 1000, [Damage("second", 1000, 200)])); session.Tick(At.AddMilliseconds(6000), 6000);
        var live = session.Capture().Workspace.Encounter!;
        Assert.Equal(6000, live.Summary.DurationMs); Assert.Equal(1000, live.Summary.DpsDurationMs);
        Assert.Equal(300, live.Summary.EncounterDps); Assert.Equal(300, live.Summary.RollingDps); Assert.False(live.Summary.Final);
        session.Tick(At.AddMilliseconds(7000), 7000);
        var paused = session.Capture().Workspace.Encounter!.Summary;
        Assert.Equal(live.Summary.AttributedDamage, paused.AttributedDamage); Assert.Equal(1000, paused.DpsDurationMs);
        Assert.Equal(live.Summary.EncounterDps, paused.EncounterDps); Assert.Equal(live.Summary.RollingDps, paused.RollingDps);

        // 续击接入空档，同时把窗口外的旧伤害移出近期窗口。
        session.Apply(Frame(3, 8000, [Damage("resumed", 8000)]));
        var resumed = session.Capture().Workspace.Encounter!;
        Assert.Equal(live.Id, resumed.Id); Assert.Equal("400", resumed.Summary.AttributedDamage.ToString());
        Assert.Equal(8000, resumed.Summary.DpsDurationMs); Assert.Equal(50, resumed.Summary.EncounterDps); Assert.Equal(20, resumed.Summary.RollingDps);
        session.Tick(At.AddMilliseconds(307999), 307999);
        Assert.False(session.Capture().Workspace.Encounter!.Summary.Final);
        session.Tick(At.AddMilliseconds(308000), 308000); var final = session.Capture().Workspace.Encounter!;
        Assert.True(final.Summary.Final); Assert.Equal(8000, final.Summary.DurationMs); Assert.Equal(8000, final.Summary.DpsDurationMs);
        Assert.Equal(resumed.Summary.EncounterDps, final.Summary.EncounterDps); Assert.Equal(resumed.Summary.RollingDps, final.Summary.RollingDps);
        Assert.Equal("idle-timeout", final.Summary.BoundaryReason);
        session.Apply(Frame(4, 309000, [Damage("new", 309000)]));
        var next = session.Capture().Workspace.Encounter!; Assert.NotEqual(final.Id, next.Id);
        // 新伤害恰好达到空闲阈值时同样结束旧段。
        session.Apply(Frame(5, 609000, [Damage("at-threshold", 609000)]));
        var threshold = session.Capture();
        Assert.Contains(threshold.Encounters, value => value.Id == next.Id && value.Summary.Final && value.Summary.BoundaryReason == "idle-timeout");
        Assert.NotEqual(next.Id, threshold.Workspace.Encounter!.Id);

        var manual = Session(rollingWindowMs: 5000); manual.StartManual(At.AddMilliseconds(1000), 1000);
        manual.Tick(At.AddMilliseconds(4000), 4000);
        var empty = manual.Capture().Workspace.Encounter!.Summary;
        Assert.Equal(3000, empty.DurationMs); Assert.Equal(0, empty.DpsDurationMs); Assert.Null(empty.EncounterDps);
        manual.Apply(Frame(1, 5000, [Damage("manual", 5000)]));
        manual.Tick(At.AddMilliseconds(405000), 405000);
        var manualPaused = manual.Capture().Workspace.Encounter!;
        Assert.False(manualPaused.Summary.Final); Assert.Equal(4000, manualPaused.Summary.DpsDurationMs);
        Assert.Equal(25, manualPaused.Summary.EncounterDps); Assert.Equal(25, manualPaused.Summary.RollingDps);
        manual.Close("manual-end", At.AddMilliseconds(406000), 406000);
        var manualFinal = manual.Capture().Workspace.Encounter!;
        Assert.True(manualFinal.Summary.Final); Assert.Equal("manual-end", manualFinal.Summary.BoundaryReason);
        Assert.Equal(1000, manualFinal.StartedMonotonicMs); Assert.Equal(406000, manualFinal.EndedMonotonicMs);
        Assert.Equal(405000, manualFinal.Summary.DurationMs); Assert.Equal(4000, manualFinal.Summary.DpsDurationMs);
        Assert.Equal(manualPaused.Summary.EncounterDps, manualFinal.Summary.EncounterDps);
        Assert.Equal(manualPaused.Summary.RollingDps, manualFinal.Summary.RollingDps);
    }
    internal static CombatFrame StateFrame(ulong sequence, double mono, CombatLocalState state) =>
        Frame(sequence, mono) with { CombatStates = [new(state, At.AddMilliseconds(mono), mono, null)] };

    [Fact]
    public void LeavingCombatKeepsTheSegmentForTenSecondsAndThenEndsAtTheLastDamage()
    {
        var session = Session();
        session.Apply(StateFrame(1, 900, CombatLocalState.InCombat));
        session.Apply(Frame(2, 1000, [Damage("a", 1000)]));
        session.Apply(StateFrame(3, 3000, CombatLocalState.NonCombat));
        // 离开战斗 5 秒后回到战斗并续击：仍是同一分段，空档按停手口径计入分母。
        session.Apply(StateFrame(4, 8000, CombatLocalState.InCombat));
        session.Apply(Frame(5, 9000, [Damage("b", 9000)]));
        session.Apply(StateFrame(6, 10000, CombatLocalState.CombatEnd));
        session.Tick(At.AddMilliseconds(20000), 20000);
        Assert.Equal("active", session.Capture().Workspace.Encounter!.Summary.BoundaryReason);
        session.Tick(At.AddMilliseconds(20001), 20001);
        var first = session.Capture().Workspace.Encounter!;
        Assert.Equal(("combat-left", "200"), (first.Summary.BoundaryReason, first.Summary.AttributedDamage.ToString()));
        Assert.Equal(8000, first.Summary.DpsDurationMs ?? first.Summary.DurationMs);
        session.Apply(Frame(7, 25000, [Damage("c", 25000)]));
        var next = session.Capture().Workspace.Encounter!;
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal("combat-entered", next.Segment!.StartReason);
    }

    [Fact]
    public void WholeExpiresThreeMinutesAfterTheLastCombatActionAndRenewsOnTheNextDamage()
    {
        var session = Session();
        session.Apply(Frame(1, 1000, [Damage("a", 1000)]));
        // 受到伤害也是战斗动作：3 分钟从这一下重新计算。
        session.Apply(Frame(2, 60000, [Damage("taken", 60000, from: Monster, target: Self)]));
        session.Tick(At.AddMilliseconds(239999), 239999);
        Assert.Null(session.Capture().Workspace.Observation.WholeExpired);
        session.Tick(At.AddMilliseconds(240000), 240000);
        var expired = session.Capture().Workspace;
        Assert.Equal("idle-out-of-combat", expired.Observation.WholeExpired);
        // 到期只作标记：上一场的分段（截止最后有效伤害）照常保留。
        Assert.Equal(1, expired.Observation.WholeOrdinal);
        Assert.Equal(1, expired.Encounter!.Segment!.Whole);
        Assert.Equal("100", expired.Encounter.Summary.AttributedDamage.ToString());
        session.Apply(Frame(3, 250000, [Damage("b", 250000)]));
        var renewed = session.Capture().Workspace;
        Assert.Equal(2, renewed.Encounter!.Segment!.Whole);
        Assert.Equal(2, renewed.Observation.WholeOrdinal);
        Assert.Null(renewed.Observation.WholeExpired);
    }

    [Fact]
    public void ResetClearsTheSegmentTheWholeAndTheMeasurementButKeepsHistory()
    {
        var session = Session();
        session.Apply(Frame(1, 1000, [Damage("a", 1000)]));
        var measurement = session.BeginMeasurement(At.AddMilliseconds(1000), 1000, 60000, 0);
        session.Apply(Frame(2, 2000, [Damage("b", 2000)]));
        Assert.NotNull(session.MeasurementProgress());
        var closed = session.Reset(At.AddMilliseconds(3000), 3000);
        var checkpoint = session.Capture();
        Assert.Contains(checkpoint.Encounters, x => x.Id == closed!.Id && x.Summary.Final && x.Summary.BoundaryReason == "reset");
        Assert.Null(checkpoint.Workspace.Encounter);
        Assert.Null(session.MeasurementProgress());
        Assert.Contains(measurement, checkpoint.DeletedEncounters);
        Assert.Equal(2, checkpoint.Workspace.Observation.WholeOrdinal);
        session.Apply(Frame(3, 4000, [Damage("c", 4000)]));
        var segment = session.Capture().Workspace.Encounter!.Segment!;
        Assert.Equal("reset", segment.StartReason);
        Assert.Equal(2, segment.Whole);
    }

    [Fact]
    public void ManualResetAndLateFramesPreserveSeparateHistoryBoundaries()
    {
        var session = Session(); session.StartManual(At, 0); session.Apply(Frame(1, 1000, [Damage("first", 1000)]));
        var old = session.Capture().Workspace.Encounter!.Id;
        session.Reset(At.AddMilliseconds(2000), 2000); var next = session.Capture();
        Assert.Contains(next.Encounters, x => x.Id == old && x.Summary.Final && x.Summary.BoundaryReason == "reset");
        Assert.Null(next.Workspace.Encounter);
        session.Apply(Frame(2, 2500, [Damage("late", 1000)]));
        Assert.Null(session.Capture().Workspace.Encounter);
        session.Stop("observation-stopped", At, 2500); Assert.False(session.Apply(Frame(3, 3000, [Damage("after-stop", 3000)])));
    }
    [Fact]
    public void CaptureGenerationsFenceOldIdentityAndFinishTheOldEncounter()
    {
        var session = Session(); session.Apply(Frame(1, 0, [Damage("old", 0)]));
        var fresh = Self with { ObservationGeneration = new(2) }; var target = Monster with { ObservationGeneration = new(2) };
        var damage = Damage("fresh", 1000) with { SourceEntity = fresh, TargetEntity = target };
        session.Apply(Frame(1, 1000, [damage], entities: [Entity(fresh, CombatEntityRole.LocalPlayer), Entity(target, CombatEntityRole.Monster)]) with { Generation = new(2) });
        var checkpoint = session.Capture(); Assert.Equal(new UInt64String(2), checkpoint.Workspace.Observation.LocalEntity.ObservationGeneration);
        Assert.Contains(checkpoint.Encounters, x => x.Generation.Value == 1 && x.Summary.Final && x.Summary.Quality.KnownValue == ObservationQuality.Partial);
        Assert.False(session.Apply(Frame(2, 1200, [Damage("stale", 1200)])));
        Assert.Equal("100", session.Capture().Workspace.Encounter!.Summary.AttributedDamage.ToString());
    }
    [Fact]
    public void EntityEffectAndDeduplicationLimitsKeepQualityAndMemoryExplicit()
    {
        var limits = new CombatLimits(MaximumEntities: 2, MaximumEffectsPerEntity: 1, MaximumDeduplicationEntries: 2, MaximumTimelineItems: 2);
        var session = Session(limits: limits);
        session.Apply(Frame(1, 0, [Damage("one", 0), Damage("two", 0), Damage("three", 0)],
            [Effect(Self, 0), Effect(Self, 0, instance: 201)], [Entity(Monster, CombatEntityRole.Monster), Entity(Self with { RawInstanceId = new(50) }, CombatEntityRole.Unknown)]));
        var checkpoint = session.Capture(); var resources = checkpoint.Workspace.Encounter!.Resources;
        Assert.True(resources.Entities <= 2); Assert.True(resources.Effects <= 1); Assert.True(resources.DeduplicationEntries <= 2); Assert.True(resources.TimelineItems <= 2);
        Assert.True(resources.DroppedEvents.Value > 0); Assert.True(resources.TruncatedTimelineItems.Value > 0);
        Assert.Equal(ObservationQuality.Partial, checkpoint.Workspace.Encounter.Summary.Quality.KnownValue);
    }
    [Fact]
    public void StatisticsAdvanceWithoutInvalidatingTheCurrentBoundaryRevision()
    {
        var session = Session(); session.StartManual(At, 0);
        var initial = session.Capture().Workspace;
        session.Apply(Frame(1, 1000, [Damage("current-hit", 1000)]));
        var changed = session.Capture().Workspace;
        Assert.True(changed.Revision.Value > initial.Revision.Value); Assert.Equal(initial.BoundaryRevision, changed.BoundaryRevision);
        Assert.Equal(initial.Encounter!.BoundaryRevision, changed.Encounter!.BoundaryRevision);
        session.Close("manual-end", At.AddMilliseconds(2000), 2000);
        var closed = session.Capture().Workspace;
        Assert.True(closed.BoundaryRevision!.Value.Value > changed.BoundaryRevision!.Value.Value);
        Assert.True(closed.Encounter!.BoundaryRevision!.Value.Value > changed.Encounter.BoundaryRevision!.Value.Value);
    }
    [Fact]
    public void EachRejectedEntityAndEffectCountsExactlyOnce()
    {
        var session = Session(limits: new(MaximumEntities: 1, MaximumEffectsPerEntity: 1));
        Assert.True(session.Apply(Frame(1, 0, effects: [Effect(Self, 0), Effect(Self, 0, instance: 201)],
            entities: [Entity(Monster, CombatEntityRole.Monster)])));
        var result = session.Capture().Workspace;
        Assert.Equal(new UInt64String(2), result.Observation.DroppedEvents);
        Assert.Equal(2, result.Observation.Reasons.Count);
        Assert.Single(result.Entities); Assert.Single(result.SelfEffects!.Views);
    }
    [Fact]
    public void ExtremelyShortDurationKeepsExactDamageAndSerializableUnknownRates()
    {
        var session = Session(); session.StartManual(At, 0);
        session.Apply(Frame(1, double.Epsilon, [Damage("tiny-time", double.Epsilon, ulong.MaxValue)]));
        var summary = session.Capture().Workspace.Encounter!.Summary;
        Assert.Equal(ulong.MaxValue.ToString(), summary.AttributedDamage.ToString());
        Assert.Equal(double.Epsilon, summary.DurationMs); Assert.Null(summary.EncounterDps); Assert.Null(summary.RollingDps);
        Assert.Contains("rateOutOfRange", summary.Reasons);
        Assert.NotEmpty(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(summary, ContractJson.Options));
    }
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public void BufferNormalizesInvalidDurationsAndSurvivesDamagedFrames(double duration)
    {
        var buffer = new CombatFrameBuffer();
        Assert.False(buffer.TryWrite(Frame(1, 0) with { Damage = null! }));
        Assert.False(buffer.TryWrite(Frame(2, double.NaN)));
        Assert.True(buffer.TryWrite(Frame(3, 0, effects: [Effect(Self, 0, ms: duration)])));
        var batch = buffer.Take(); Assert.Equal(new UInt64String(2), batch.DroppedEvents);
        var effect = Assert.Single(Assert.Single(batch.Frames).Effects);
        Assert.Equal(EffectDurationKind.Unknown, effect.DurationKind.KnownValue); Assert.Null(effect.DurationMs);
        var session = Session(); Assert.True(session.Apply(batch.Frames[0]));
        Assert.Equal(EffectDurationKind.Unknown, Assert.Single(session.Capture().Workspace.SelfEffects!.Views).Instance.DurationKind.KnownValue);
    }
    [Fact]
    public void InputQueueBoundsBothEventsAndBytesAndFreezesCallerData()
    {
        var buffer = new CombatFrameBuffer(new(MaximumQueueEvents: 3, MaximumQueueBytes: 4096));
        var damage = new List<CombatDamageEvent> { Damage("one", 0) };
        Assert.True(buffer.TryWrite(Frame(1, 0, damage))); damage.Clear();
        Assert.False(buffer.TryWrite(Frame(2, 0, [Damage("two", 0), Damage("three", 0)])));
        var batch = buffer.Take(); Assert.Single(batch.Frames[0].Damage); Assert.Equal(new UInt64String(3), batch.DroppedEvents);
        Assert.Equal(0, buffer.Count); Assert.Equal(0, buffer.Bytes);
        var byteLimited = new CombatFrameBuffer(new(MaximumQueueBytes: 1024)); Assert.False(byteLimited.TryWrite(Frame(1, 0, [Damage("large", 0)])));
        buffer.Complete(); Assert.False(buffer.TryWrite(Frame(3, 0))); Assert.True(buffer.Completed);
    }

    [Fact]
    public void FrameDelayedPastTheHostTickIsAcceptedAndOutOfOrderFramesStillPartialWithDps()
    {
        // 实采分段 2 中 82763.60 ms 的缺口：上一帧伤害在 82211 ms，宿主按收帧流逝把时钟推到 82763.6 ms，
        // 随后到达的 Worker 帧（配对等待后发布）来源时点 82480 ms，旧规则因早于时钟 250 ms 以上整帧丢弃。
        var session = Session();
        Assert.True(session.Apply(Frame(1, 82211, [Damage("before", 82211)])));
        session.Tick(At.AddMilliseconds(82763.6), 82763.6);
        Assert.True(session.Apply(Frame(2, 82480, [Damage("delayed", 82480)])));
        var accepted = session.Capture().Workspace.Encounter!.Summary;
        Assert.Equal(("200", ObservationQuality.Confirmed), (accepted.AttributedDamage.ToString(), accepted.Quality.KnownValue));
        Assert.Empty(session.FrameRejections);

        // 来源时点早于已接受帧 250 ms 以上的帧仍按 frame.late 拒绝；分段标为不完整，DPS 按已接受伤害给出。
        Assert.False(session.Apply(Frame(3, 82100, [Damage("out-of-order", 82100)])));
        Assert.Equal(1UL, session.FrameRejections["frame.late"]);
        var partial = session.Capture().Workspace.Encounter!.Summary;
        Assert.Equal(ObservationQuality.Partial, partial.Quality.KnownValue); Assert.Contains("frame-invalid", partial.Reasons);
        Assert.Equal("200", partial.AttributedDamage.ToString());
        Assert.Equal(269, partial.DpsDurationMs); Assert.Equal(200 * 1000d / 269, partial.EncounterDps);
    }
}
