using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

/// <summary>同一地图内的空闲拆段（默认 300 秒）与手动拆段。</summary>
public sealed class CombatIdleSplitTests
{
    private ulong sequence;
    private bool Hit(CombatSession session, string id, double ms, ulong amount = 100) =>
        session.Apply(Frame(++sequence, ms, [Damage(id, ms, amount)]));
    /// <summary>本机进入 in-combat 后整场不会到期，停手期间只有空闲阈值会结束分段。</summary>
    private CombatSession InCombat()
    {
        var session = Session();
        session.Apply(StateFrame(++sequence, 0, CombatLocalState.InCombat));
        return session;
    }
    private static IReadOnlyList<CombatEncounter> Segments(CombatSession session) =>
        session.Capture().Encounters.OrderBy(x => x.Segment!.Ordinal).ToArray();

    [Fact]
    public void DefaultIdleThresholdIsFiveMinutesAndLongerValuesAreRejected()
    {
        var request = new CombatObservationRequest("game-test");
        Assert.Equal(300000, request.IdleTimeoutMs);
        CombatSession.ValidateSettings(request);
        Assert.Throws<ArgumentException>(() => CombatSession.ValidateSettings(request with { IdleTimeoutMs = 300001 }));
        Assert.Throws<ArgumentException>(() => CombatSession.ValidateSettings(request with { IdleTimeoutMs = 999 }));
    }

    [Fact]
    public void RollingWindowDefaultsToSixtySecondsAndKeepsDamageForTheWholeWindow()
    {
        var request = new CombatObservationRequest("game-test");
        Assert.Equal(60000, request.RollingWindowMs);
        CombatSession.ValidateSettings(request with { RollingWindowMs = 60000 });
        Assert.Throws<ArgumentException>(() => CombatSession.ValidateSettings(request with { RollingWindowMs = 60001 }));
        var session = Session();
        Assert.True(Hit(session, "a", 0)); Assert.True(Hit(session, "b", 50000));
        var summary = session.Capture().Workspace.Encounter!.Summary;
        Assert.Equal(60000, summary.RollingWindowMs);
        Assert.Equal(200 * 1000d / 50000, summary.RollingDps);
    }

    [Fact]
    public void PauseOf299SecondsStaysInOneSegmentAndCountsTowardTheDenominator()
    {
        var session = InCombat();
        Assert.True(Hit(session, "a", 0));
        session.Tick(At.AddMilliseconds(299000), 299000);
        Assert.False(session.Capture().Workspace.Encounter!.Summary.Final);
        Assert.True(Hit(session, "b", 299000));
        session.Stop("observation-stopped", At.AddMilliseconds(299000), 299000);

        var only = Assert.Single(session.Capture().Encounters);
        Assert.Equal(1, only.Segment!.Ordinal);
        Assert.Equal("200", only.Summary.AttributedDamage.ToString());
        Assert.Equal(299000, only.Summary.DurationMs);
        Assert.Equal(200 * 1000d / 299000, only.Summary.EncounterDps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PauseOfThreeHundredSecondsEndsTheSegmentAtTheLastDamage(bool tick)
    {
        var session = InCombat();
        Assert.True(Hit(session, "a", 0)); Assert.True(Hit(session, "b", 1000));
        if (tick)
        {
            session.Tick(At.AddMilliseconds(300999), 300999);
            Assert.False(session.Capture().Workspace.Encounter!.Summary.Final);
            session.Tick(At.AddMilliseconds(301000), 301000);
            Assert.True(session.Capture().Workspace.Encounter!.Summary.Final);
        }
        Assert.True(Hit(session, "c", 301500, 300));

        var segments = Segments(session);
        Assert.Equal(2, segments.Count);
        Assert.Equal("idle-timeout", segments[0].Summary.BoundaryReason);
        Assert.Equal(1000, segments[0].Summary.DurationMs); Assert.Equal(200, segments[0].Summary.EncounterDps);
        Assert.Equal(1000, segments[0].EndedMonotonicMs);
        Assert.Equal((2, "first-damage"), (segments[1].Segment!.Ordinal, segments[1].Segment!.StartReason));
        Assert.Equal("300", segments[1].Summary.AttributedDamage.ToString());
    }

    [Fact]
    public void ManualSplitEndsAutoSegmentAtLastDamageAndMarksTheNextSegment()
    {
        var session = Session();
        Assert.True(Hit(session, "a", 0)); Assert.True(Hit(session, "b", 1000));
        session.Tick(At.AddMilliseconds(61000), 61000);
        var before = session.Capture().Workspace.BoundaryRevision!.Value.Value;

        Assert.True(session.Split(At.AddMilliseconds(61000), 61000));
        var split = session.Capture();
        var ended = split.Workspace.Encounter!;
        Assert.True(ended.Summary.Final); Assert.Equal("manual-split", ended.Summary.BoundaryReason);
        Assert.Equal(1000, ended.Summary.DurationMs); Assert.Equal(200, ended.Summary.EncounterDps);
        Assert.True(split.Workspace.BoundaryRevision!.Value.Value > before);

        Assert.True(Hit(session, "c", 62000, 500)); Assert.True(Hit(session, "d", 63000, 500));
        var next = session.Capture().Workspace.Encounter!;
        Assert.NotEqual(ended.Id, next.Id);
        Assert.Equal((2, "manual-split"), (next.Segment!.Ordinal, next.Segment!.StartReason));
        Assert.Equal("1000", next.Summary.AttributedDamage.ToString());
        Assert.Equal(62000, next.StartedMonotonicMs);

        // 拆段产生的分段继续按空闲阈值自动结束，其后的分段恢复 first-damage。
        Assert.True(Hit(session, "e", 363000));
        var segments = Segments(session);
        Assert.Equal(["manual-split", "idle-timeout", "active"], segments.Select(x => x.Summary.BoundaryReason));
        Assert.Equal(["first-damage", "manual-split", "first-damage"], segments.Select(x => x.Segment!.StartReason));
    }

    [Fact]
    public void ManualSplitEndsManualSegmentAtTheRequestTime()
    {
        var session = Session();
        session.StartManual(At, 0); Assert.True(Hit(session, "a", 500));
        Assert.True(session.Split(At.AddMilliseconds(2000), 2000));
        var ended = session.Capture().Workspace.Encounter!;
        Assert.Equal(("manual-start", "manual-split", 2000d), (ended.Segment!.StartReason, ended.Summary.BoundaryReason, ended.Summary.DurationMs));
        Assert.True(Hit(session, "b", 3000));
        Assert.Equal("manual-split", session.Capture().Workspace.Encounter!.Segment!.StartReason);
    }

    [Fact]
    public void SplitWithoutActiveSegmentChangesNothing()
    {
        var session = Session();
        Assert.False(session.Split(At, 0));
        Assert.Null(session.Capture().Workspace.Encounter);
        Assert.True(Hit(session, "a", 0)); session.Tick(At.AddMilliseconds(300000), 300000);
        var idle = session.Capture().Workspace;
        Assert.False(session.Split(At.AddMilliseconds(300100), 300100));
        var after = session.Capture().Workspace;
        Assert.Equal(idle.BoundaryRevision, after.BoundaryRevision);
        Assert.Equal("idle-timeout", after.Encounter!.Summary.BoundaryReason);
        // 无效拆段不标记下一分段。
        Assert.True(Hit(session, "b", 300200));
        Assert.Equal("first-damage", session.Capture().Workspace.Encounter!.Segment!.StartReason);
        session.Stop("observation-stopped", At.AddMilliseconds(300300), 300300);
        Assert.False(session.Split(At.AddMilliseconds(300400), 300400));
    }

    [Fact]
    public void ExplicitStartAfterSplitKeepsItsOwnReason()
    {
        var session = Session();
        Assert.True(Hit(session, "a", 0)); Assert.True(session.Split(At.AddMilliseconds(100), 100));
        session.StartManual(At.AddMilliseconds(200), 200);
        Assert.Equal("manual-start", session.Capture().Workspace.Encounter!.Segment!.StartReason);
    }
}
