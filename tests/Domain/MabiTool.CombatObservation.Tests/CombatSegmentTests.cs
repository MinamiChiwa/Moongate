using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

/// <summary>切图分段：按 Worker 实际顺序构造帧——旧代次发布连接缺口，重置后的代次没有本机身份，重新识别后进入新代次。</summary>
public sealed class CombatSegmentTests
{
    private const string Closed = "combat.streamGap.connectionClosed", Removed = "combat.streamGap.connectionRemoved";

    private sealed class Script(CombatSession session)
    {
        public ulong Generation = 1;
        private ulong sequence;
        private bool identified = true;
        public EntityKey Local => Self with { ObservationGeneration = new(Generation) };
        public EntityKey Target => Monster with { ObservationGeneration = new(Generation) };
        public bool Hit(string id, double ms, ulong amount) =>
            session.Apply(Next(ms, damage: [Damage(id, ms, amount) with { SourceEntity = Local, TargetEntity = Target }]));
        public bool Gap(double ms, params CombatGapInput[] gaps) => session.Apply(Next(ms, gaps: gaps, local: identified));
        public bool Start(double ms) => session.Apply(Next(ms));
        /// <summary>流重置：缺口在当前代次发布，随后代次递增且尚无本机身份。</summary>
        public void Reset(double ms, string reason, ulong dropped = 0) => ResetMany(ms, dropped, reason);
        public void ResetMany(double ms, ulong dropped, params string[] reasons)
        { Gap(ms, reasons.Select(x => new CombatGapInput(x, ms, ms, new(dropped))).ToArray()); Generation++; sequence = 0; identified = false; }
        /// <summary>身份尚未取得时的新代次帧，只包含怪物。</summary>
        public bool Unresolved(double ms, params CombatGapInput[] gaps) => session.Apply(Next(ms, gaps: gaps, local: false));
        /// <summary>重新取得本机身份：代次再次递增。</summary>
        public bool Identify(double ms) { Generation++; sequence = 0; identified = true; return session.Apply(Next(ms)); }
        private CombatFrame Next(double ms, IReadOnlyList<CombatDamageEvent>? damage = null, IReadOnlyList<CombatGapInput>? gaps = null, bool local = true) =>
            Frame(++sequence, ms, damage, entities: local
                ? [Entity(Local, CombatEntityRole.LocalPlayer, "測試角色"), Entity(Target, CombatEntityRole.Monster)]
                : [Entity(Target, CombatEntityRole.Monster)], gaps: gaps) with { Generation = new(Generation) };
    }

    private static IReadOnlyList<CombatEncounter> Segments(CombatSession session) =>
        session.Capture().Encounters.OrderBy(x => x.Segment!.Ordinal).ToArray();

    /// <summary>一张地图内两次伤害：分母 1,000 ms。</summary>
    private static void Fight(Script script, double start, ulong amount)
    {
        Assert.True(script.Hit($"g{script.Generation}-a", start, amount));
        Assert.True(script.Hit($"g{script.Generation}-b", start + 1000, amount));
    }

    [Fact]
    public void ThreeMapChangesProduceFourIndependentCompleteSegments()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0);
        Fight(script, 100, 100);
        // 第一次切图：FIN 后约 0.23 s 又移除连接，均在重新识别前。
        script.Reset(1500, Closed); script.Unresolved(1734); script.Reset(1734, Removed);
        script.Unresolved(1800); script.Identify(5000);
        Fight(script, 6000, 200);
        // 第二次切图：只有连接移除。
        script.Reset(8000, Removed); script.Identify(9000);
        Fight(script, 10000, 300);
        script.Reset(12000, Closed); script.Unresolved(12240); script.Reset(12240, Removed); script.Identify(14000);
        Fight(script, 15000, 400);
        session.Stop("observation-stopped", At.AddMilliseconds(17000), 17000);

        var segments = Segments(session);
        Assert.Equal([1, 2, 3, 4], segments.Select(x => x.Segment!.Ordinal));
        Assert.Equal(["first-damage", "map-change", "map-change", "map-change"], segments.Select(x => x.Segment!.StartReason));
        Assert.Equal(["connection-changed", "connection-changed", "connection-changed", "observation-stopped"], segments.Select(x => x.Summary.BoundaryReason));
        Assert.Equal(["200", "400", "600", "800"], segments.Select(x => x.Summary.AttributedDamage.ToString()));
        Assert.All(segments, segment =>
        {
            Assert.Equal(ObservationQuality.Confirmed, segment.Summary.Quality.KnownValue);
            Assert.Empty(segment.Gaps);
            Assert.True(segment.Summary.Final);
        });
        // 自动分段截止最后有效伤害，加载时间不进入任何分母。
        Assert.Equal([1000d, 1000d, 1000d], segments.Take(3).Select(x => x.Summary.DurationMs));
        Assert.Equal([200d, 400d, 600d], segments.Take(3).Select(x => x.Summary.EncounterDps!.Value));
        Assert.Equal([100d, 6000d, 10000d, 15000d], segments.Select(x => x.StartedMonotonicMs));
        Assert.Equal(4, segments.Select(x => x.Generation).Distinct().Count());
        var status = session.Capture().Workspace.Observation;
        Assert.Equal(ObservationQuality.Confirmed, status.Quality.KnownValue);
        Assert.Empty(status.Reasons);
        // 连接缺口仍完整保留在观察诊断中。
        var gaps = session.Capture().Gaps;
        Assert.Equal(2, gaps.Count(x => x.Reason == Closed));
        Assert.Equal(3, gaps.Count(x => x.Reason == Removed));
    }

    [Theory]
    [InlineData("combat.captureLag", 0UL)]
    [InlineData("combat.streamGap.captureDrops", 3UL)]
    [InlineData("combat.streamGap.gapTimeout", 0UL)]
    public void CaptureGapInsideOneSegmentOnlyDegradesThatSegment(string reason, ulong dropped)
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); Fight(script, 100, 100);
        script.Reset(1500, Removed); script.Identify(3000);
        Assert.True(script.Hit("lag-a", 4000, 200));
        if (reason.StartsWith("combat.streamGap.", StringComparison.Ordinal))
        {
            // 捕获/重组类重置同样会换代次；它落在分段内，使本段降级。
            script.Reset(4500, reason, dropped); script.Identify(4600);
        }
        else Assert.True(script.Gap(4500, new CombatGapInput(reason, 4300, 4500, new(dropped))));
        Assert.True(script.Hit("lag-b", 5000, 200));
        script.Reset(6000, Closed); script.Identify(7000);
        Fight(script, 8000, 300);
        session.Stop("observation-stopped", At.AddMilliseconds(9500), 9500);

        var segments = Segments(session);
        Assert.Equal(ObservationQuality.Confirmed, segments[0].Summary.Quality.KnownValue);
        Assert.Equal(200, segments[0].Summary.EncounterDps);
        Assert.Equal(ObservationQuality.Confirmed, segments[^1].Summary.Quality.KnownValue);
        Assert.Equal(1500, segments[^1].Summary.DurationMs); Assert.Equal(1000, segments[^1].Summary.DpsDurationMs);
        Assert.Equal(600, segments[^1].Summary.EncounterDps); // 实际时长保留停止时点，DPS 截止最后伤害。
        var degraded = Assert.Single(segments, x => x.Summary.Quality.KnownValue != ObservationQuality.Confirmed);
        Assert.Equal(2, degraded.Segment!.Ordinal);
        Assert.Equal(ObservationQuality.Partial, degraded.Summary.Quality.KnownValue);
        // 不完整的分段仍按已接受的伤害给出 DPS，并以 quality 与 reasons 标注。
        Assert.Contains(reason, degraded.Summary.Reasons);
        Assert.Equal((double?)(degraded.Summary.DpsDurationMs is > 0 ? (double)degraded.Summary.AttributedDamage.Value * 1000 / degraded.Summary.DpsDurationMs.Value : null), degraded.Summary.EncounterDps);
        Assert.Contains(reason, session.Capture().Workspace.Observation.Reasons);
    }

    [Fact]
    public void DisconnectWithoutIdentityEndsSegmentWithoutOpeningAnother()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); Fight(script, 100, 100);
        script.Reset(1500, Closed);
        Assert.False(script.Unresolved(1700)); Assert.False(script.Unresolved(2000));
        script.Reset(2200, Removed);
        Assert.False(script.Unresolved(2500));
        var waiting = session.Capture().Workspace;
        Assert.Equal(ObservationQuality.Partial, waiting.Observation.Quality.KnownValue);
        Assert.Contains(Closed, waiting.Observation.Reasons); Assert.Contains("identity-unresolved", waiting.Observation.Reasons);
        session.Stop("observation-stopped", At.AddMilliseconds(5000), 5000);

        var checkpoint = session.Capture();
        var only = Assert.Single(checkpoint.Encounters);
        Assert.Equal("connection-changed", only.Summary.BoundaryReason);
        Assert.Equal(ObservationQuality.Confirmed, only.Summary.Quality.KnownValue);
        Assert.Equal(200, only.Summary.EncounterDps);
        Assert.Equal(only.Id, checkpoint.Workspace.Encounter!.Id);
        Assert.Equal(ObservationQuality.Partial, checkpoint.Workspace.Observation.Quality.KnownValue);
        Assert.Contains("identity-unresolved", checkpoint.Workspace.Observation.Reasons);
        // 每个等待代次只记一次，不随帧数增长。
        Assert.Equal(2, checkpoint.Gaps.Count(x => x.Reason == "identity-unresolved"));
    }

    [Fact]
    public void PairedConnectionGapsCreateOneBoundaryEvenInTheSameFrame()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); Fight(script, 100, 100);
        script.ResetMany(1500, 0, Closed, Removed); script.Identify(4000);
        Fight(script, 5000, 200);
        var segments = Segments(session);
        Assert.Equal(2, segments.Count);
        Assert.Single(segments, x => x.Summary.BoundaryReason == "connection-changed");
        Assert.Equal("map-change", segments[1].Segment!.StartReason);
        Assert.Equal(2, segments[1].Segment!.Ordinal);
        Assert.All(segments, x => Assert.Equal(ObservationQuality.Confirmed, x.Summary.Quality.KnownValue));
    }

    [Fact]
    public void ManualSegmentEndsAtGapAndDroppedEventsStillDegradeTheSegment()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); session.StartManual(At.AddMilliseconds(50), 50);
        Assert.True(script.Hit("manual", 100, 100));
        script.Reset(2000, Closed); script.Identify(3000);
        Assert.True(script.Hit("auto-a", 4000, 100)); Assert.True(script.Hit("auto-b", 5000, 100));
        script.Reset(5500, Removed, dropped: 2);
        var segments = Segments(session);
        Assert.Equal("manual-start", segments[0].Segment!.StartReason);
        Assert.Equal(1950, segments[0].Summary.DurationMs);
        Assert.Equal(ObservationQuality.Confirmed, segments[0].Summary.Quality.KnownValue);
        Assert.Equal(ObservationQuality.Partial, segments[1].Summary.Quality.KnownValue);
        Assert.Contains(Removed, segments[1].Summary.Reasons);
        Assert.Equal("connection-changed", segments[1].Summary.BoundaryReason);
        Assert.Equal(new UInt64String(2), session.Capture().Workspace.Observation.DroppedEvents);
    }

    [Fact]
    public void MapChangeAfterManualSplitStillMarksTheNewMap()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); Fight(script, 100, 100);
        Assert.True(session.Split(At.AddMilliseconds(1500), 1500));
        script.Reset(2000, Closed); script.Identify(3000); Fight(script, 4000, 100);
        var segments = Segments(session);
        Assert.Equal(["manual-split", "active"], segments.Select(x => x.Summary.BoundaryReason));
        Assert.Equal(["first-damage", "map-change"], segments.Select(x => x.Segment!.StartReason));
        Assert.Equal(1000, segments[0].Summary.DurationMs);
    }

    [Fact]
    public void LongPauseWithinOneMapDoesNotDelayMapChangeBoundaries()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); Fight(script, 100, 100);
        Assert.True(script.Hit("late-in-map", 200000, 100));
        script.Reset(200500, Closed); script.Identify(201000); Fight(script, 202000, 100);
        var segments = Segments(session);
        Assert.Equal(["connection-changed", "active"], segments.Select(x => x.Summary.BoundaryReason));
        Assert.Equal(199900, segments[0].Summary.DurationMs);
        Assert.Equal("300", segments[0].Summary.AttributedDamage.ToString());
        Assert.Equal("map-change", segments[1].Segment!.StartReason);
    }

    [Fact]
    public void UnexplainedGenerationChangeKeepsTheReconnectSemantics()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); Fight(script, 100, 100);
        script.Identify(2000); Fight(script, 3000, 100);
        var segments = Segments(session);
        Assert.Equal("observation-reconnected", segments[0].Summary.BoundaryReason);
        Assert.Equal(ObservationQuality.Partial, segments[0].Summary.Quality.KnownValue);
        Assert.Equal("first-damage", segments[1].Segment!.StartReason);
    }

    [Fact]
    public void StreamResetWithinOneConnectionIsReportedApartFromConnectionChanges()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); Fight(script, 100, 100);
        // 同一连接内重组超时：Worker 换代次，旧段以 stream-reset 结束并标为不完整，不当作切图。
        script.Reset(1500, "combat.streamGap.gapTimeout"); script.Identify(2000); Fight(script, 3000, 100);
        var segments = Segments(session);
        Assert.Equal("stream-reset", segments[0].Summary.BoundaryReason);
        Assert.Equal(ObservationQuality.Partial, segments[0].Summary.Quality.KnownValue);
        Assert.Equal("first-damage", segments[1].Segment!.StartReason);
    }

    [Fact]
    public void SegmentIsAnOptionalContractFieldForFrontendsAndOldHistory()
    {
        var session = Session(); var script = new Script(session);
        script.Start(0); Fight(script, 100, 100);
        script.Reset(1500, Closed); script.Identify(3000); Fight(script, 4000, 100);
        var encounter = session.Capture().Workspace.Encounter!;
        var json = JsonSerializer.SerializeToNode(encounter, ContractJson.Options)!.AsObject();
        Assert.Equal(2, (int)json["segment"]!["ordinal"]!);
        Assert.Equal("map-change", (string)json["segment"]!["startReason"]!);
        Assert.Equal("active", (string)json["summary"]!["boundaryReason"]!);
        json.Remove("segment");
        var legacy = json.Deserialize<CombatEncounter>(ContractJson.Options)!;
        Assert.Null(legacy.Segment);
        Assert.False(JsonSerializer.SerializeToNode(legacy, ContractJson.Options)!.AsObject().ContainsKey("segment"));
    }
}
