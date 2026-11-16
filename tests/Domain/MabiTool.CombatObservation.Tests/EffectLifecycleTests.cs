using MabiTool.Contracts;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

public sealed class EffectLifecycleTests
{
    [Fact]
    public void MultipleCastersStacksAndRefreshKeepIndependentInstancesAndHistory()
    {
        var session = Session(); var other = Self with { RawInstanceId = new(80) };
        session.Apply(Frame(1, 0, [Damage("begin", 0)], [Effect(Self, 0, caster: Self), Effect(Self, 0, caster: other)]));
        session.Apply(Frame(2, 500, effects: [Effect(Self, 500, CombatEffectChangeKind.Refresh, caster: Self, ms: 2000, stacks: 3)]));
        session.Tick(At.AddMilliseconds(1500), 1500); var checkpoint = session.Capture();
        var views = checkpoint.Workspace.SelfEffects!.Views; Assert.Equal(2, views.Count);
        var refreshed = Assert.Single(views, x => x.Instance.CasterKey == Self); Assert.Equal(3, refreshed.Instance.Stacks); Assert.Equal(1000, refreshed.RemainingMs);
        Assert.Equal(EffectPresence.Observed, refreshed.Instance.Presence.KnownValue);
        Assert.Equal(EffectPresence.PredictedExpired, Assert.Single(views, x => x.Instance.CasterKey == other).Instance.Presence.KnownValue);
        Assert.Contains(checkpoint.Timeline, x => x.Item.Effect?.Kind.KnownValue == CombatEffectChangeKind.Refresh);
        session.Apply(Frame(3, 1600, effects: [Effect(Self, 1600, CombatEffectChangeKind.Remove, caster: Self)]));
        Assert.Equal(EffectPresence.ConfirmedRemoved, Assert.Single(session.Capture().Workspace.SelfEffects!.Views, x => x.Instance.CasterKey == Self).Instance.Presence.KnownValue);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDurationsRemainUnknownWithoutInventingInfiniteEvidence(double ms)
    {
        var session = Session(); session.Apply(Frame(1, 0, effects: [Effect(Self, 0, ms: ms)]));
        var view = Assert.Single(session.Capture().Workspace.SelfEffects!.Views);
        Assert.Equal(EffectDurationKind.Unknown, view.Instance.DurationKind.KnownValue); Assert.Null(view.RemainingMs); Assert.Null(view.Instance.ExpiresAt);
    }
    [Fact]
    public void InfiniteEvidenceAndPartialFullReconciliationHaveDistinctSemantics()
    {
        var session = Session(); var infinite = Effect(Self, 0, duration: EffectDurationKind.Infinite, ms: null);
        session.Apply(Frame(1, 0, effects: [infinite, Effect(Self, 0, instance: 201)]));
        session.Apply(Frame(2, 100, reconcile: [new(Self, false, [infinite with { MonotonicMs = 100, ObservedAt = At.AddMilliseconds(100) }])]));
        Assert.Equal(2, session.Capture().Workspace.SelfEffects!.Views.Count); Assert.False(session.Capture().Workspace.SelfEffects!.Snapshot.Complete);
        session.Apply(Frame(3, 200, reconcile: [new(Self, true, [infinite with { MonotonicMs = 200, ObservedAt = At.AddMilliseconds(200) }])]));
        var snapshot = session.Capture().Workspace.SelfEffects!; Assert.True(snapshot.Snapshot.Complete);
        Assert.Equal(EffectDurationKind.Infinite, Assert.Single(snapshot.Views, x => x.Instance.InstanceId.Value == 200).Instance.DurationKind.KnownValue);
        Assert.Equal(EffectPresence.ConfirmedRemoved, Assert.Single(snapshot.Views, x => x.Instance.InstanceId.Value == 201).Instance.Presence.KnownValue);
    }
    [Fact]
    public void GapExpirationAndTargetScopeRetainEvidenceUntilFullReconciliation()
    {
        var session = Session(); session.Apply(Frame(1, 0, [Damage("begin", 0)], [Effect(Self, 0), Effect(Monster, 0)]));
        session.Apply(Frame(2, 500, gaps: [new("connection-lost", 500, null, new(0))])); session.Tick(At.AddMilliseconds(2000), 2000);
        var stale = Assert.Single(session.Capture().Workspace.SelfEffects!.Views);
        Assert.True(stale.Stale); Assert.Equal(EffectPresence.PredictedExpired, stale.Instance.Presence.KnownValue);
        session.Apply(Frame(3, 2100, entities: [Entity(Monster, CombatEntityRole.Monster) with { InScope = false }],
            reconcile: [new(Self, true, [Effect(Self, 2100, CombatEffectChangeKind.Refresh, ms: 2000)])]) with { ObservationResumed = true });
        var checkpoint = session.Capture(); Assert.False(Assert.Single(checkpoint.Workspace.SelfEffects!.Views).Stale);
        Assert.Equal(EffectPresence.OutOfScope, Assert.Single(Assert.Single(checkpoint.Effects, x => x.Snapshot.EntityKey == Monster).Views).Instance.Presence.KnownValue);
        Assert.Equal(ObservationQuality.Partial, checkpoint.Workspace.Encounter!.Summary.Quality.KnownValue);
        Assert.Equal(2100, checkpoint.Workspace.Encounter.Gaps[0].ToMs);
    }
}
