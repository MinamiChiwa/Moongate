using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

internal sealed class EffectTracker(CombatLimits limits, Func<long> otherCacheBytes, Action<string> overflow)
{
    private readonly record struct InstanceKey(UInt64String Effect, UInt64String Instance, EntityKey? Caster);
    private sealed class State(CombatEffectObservation value)
    {
        public CombatEffectObservation Value = value;
        public DateTimeOffset AppliedAt = value.ObservedAt;
        public double? ExpiresMs;
        public DateTimeOffset? ExpiresAt;
        public EffectDurationKind Duration = EffectDurationKind.Unknown;
        public EffectPresence Presence = EffectPresence.Observed;
        public bool Stale;
        public ulong Revision = 1;
    }
    private sealed class EntityState
    {
        public readonly Dictionary<InstanceKey, State> Instances = [];
        public bool Complete;
        public bool InScope = true;
        public DateTimeOffset LastObserved;
        public ulong Revision;
    }
    private readonly Dictionary<EntityKey, EntityState> entities = [];
    public int Count => entities.Values.Sum(entity => entity.Instances.Count);
    public long Bytes => Count * 1024L + entities.Count * 128L;
    public void Clear() => entities.Clear();
    /// <summary>实体被淘汰时移除其全部效果实例。</summary>
    public void Remove(EntityKey key) => entities.Remove(key);
    private static InstanceKey Key(CombatEffectObservation value) => new(value.EffectId, value.InstanceId, value.CasterKey);
    private EntityState Entity(EntityKey key)
    {
        if (!entities.TryGetValue(key, out var state)) entities.Add(key, state = new());
        return state;
    }

    public bool Apply(CombatEffectObservation value)
    {
        var entity = Entity(value.EntityKey); var key = Key(value);
        if (value.Kind.KnownValue is null || value.Stacks is < 0 || value.Category?.Length > 128)
        { overflow("effect-invalid-or-late"); return false; }
        if (!entity.Instances.TryGetValue(key, out var state))
        {
            foreach (var retired in entity.Instances.Where(x => x.Value.Presence == EffectPresence.ConfirmedRemoved)
                .OrderBy(x => x.Value.Value.MonotonicMs).Select(x => x.Key).ToArray())
            {
                if (entity.Instances.Count < limits.MaximumEffectsPerEntity && otherCacheBytes() + Bytes + 1024 <= limits.MaximumCacheBytes) break;
                entity.Instances.Remove(retired);
            }
            if (entity.Instances.Count >= limits.MaximumEffectsPerEntity || otherCacheBytes() + Bytes + 1024 > limits.MaximumCacheBytes)
            { entity.Complete = false; overflow("effect-capacity"); return false; }
            state = new(value); entity.Instances.Add(key, state);
        }
        else if (value.MonotonicMs < state.Value.MonotonicMs) { overflow("effect-invalid-or-late"); return false; }
        var previous = state.Value;
        if (value.Kind.KnownValue == CombatEffectChangeKind.Remove)
        {
            state.Presence = EffectPresence.ConfirmedRemoved;
            state.Value = value with { DurationKind = state.Duration, Category = value.Category ?? previous.Category };
        }
        else
        {
            if (state.Presence == EffectPresence.ConfirmedRemoved) state.AppliedAt = value.ObservedAt;
            state.Value = value with { Stacks = value.Stacks ?? previous.Stacks, Category = value.Category ?? previous.Category };
            state.Presence = EffectPresence.Observed;
            if (value.Kind.KnownValue != CombatEffectChangeKind.StackChanged)
            {
                state.Duration = value.DurationKind.KnownValue == EffectDurationKind.Infinite ? EffectDurationKind.Infinite :
                    value.DurationKind.KnownValue == EffectDurationKind.Finite && value.DurationMs is > 0 && double.IsFinite(value.DurationMs.Value)
                        ? EffectDurationKind.Finite : EffectDurationKind.Unknown;
                state.ExpiresMs = state.Duration == EffectDurationKind.Finite ? value.MonotonicMs + value.DurationMs : null;
                state.ExpiresAt = state.Duration == EffectDurationKind.Finite ? value.ObservedAt.AddMilliseconds(value.DurationMs!.Value) : null;
            }
        }
        state.Stale = value.Quality.KnownValue != ObservationQuality.Confirmed;
        state.Revision = checked(state.Revision + 1); entity.Revision = checked(entity.Revision + 1);
        entity.LastObserved = value.ObservedAt; return true;
    }

    public IReadOnlyList<CombatEffectObservation> Reconcile(CombatEffectReconciliation reconciliation, DateTimeOffset at, double mono)
    {
        var entity = Entity(reconciliation.EntityKey);
        var applied = new List<CombatEffectObservation>(); var successful = true;
        foreach (var value in reconciliation.Effects)
        {
            if (value.EntityKey != reconciliation.EntityKey || value.Kind.KnownValue == CombatEffectChangeKind.Remove)
            { overflow("effect-invalid-or-late"); successful = false; continue; }
            if (!Apply(value))
            { successful = false; continue; }
            applied.Add(value);
        }
        if (reconciliation.Complete && successful)
        {
            var present = reconciliation.Effects.Select(Key).ToHashSet();
            foreach (var missing in entity.Instances.Where(x => !present.Contains(x.Key) && x.Value.Presence != EffectPresence.ConfirmedRemoved).ToArray())
            {
                var remove = missing.Value.Value with { Kind = CombatEffectChangeKind.Remove, ObservedAt = at, MonotonicMs = mono, Quality = ObservationQuality.Confirmed };
                if (Apply(remove)) applied.Add(remove);
            }
            entity.Complete = reconciliation.Effects.All(x => x.Quality.KnownValue == ObservationQuality.Confirmed);
            entity.InScope = true;
        }
        entity.LastObserved = at; entity.Revision = checked(entity.Revision + 1); return applied;
    }
    public void Gap()
    {
        foreach (var entity in entities.Values)
        {
            entity.Complete = false; entity.Revision = checked(entity.Revision + 1);
            foreach (var state in entity.Instances.Values) state.Stale = true;
        }
    }
    public void Scope(EntityKey key, bool inScope)
    {
        var entity = Entity(key);
        if (entity.InScope == inScope) return;
        entity.InScope = inScope; entity.Complete = false; entity.Revision = checked(entity.Revision + 1);
    }
    public IReadOnlyList<EntityKey> Tick(double mono)
    {
        var changed = new List<EntityKey>();
        foreach (var (key, entity) in entities)
            foreach (var state in entity.Instances.Values)
                if (state.Presence == EffectPresence.Observed && state.ExpiresMs is { } expiry && expiry <= mono)
                {
                    state.Presence = EffectPresence.PredictedExpired; state.Revision = checked(state.Revision + 1);
                    entity.Revision = checked(entity.Revision + 1); changed.Add(key);
                }
        return changed.Distinct().ToArray();
    }
    public CombatEntityEffects Snapshot(EntityKey key, string owner, string observation, double mono, UInt64String revision)
    {
        var entity = Entity(key);
        var views = entity.Instances.Values.OrderBy(x => x.Value.EffectId.Value).ThenBy(x => x.Value.InstanceId.Value)
            .ThenBy(x => x.Value.CasterKey?.RawInstanceId.Value).Select(state =>
            {
                var quality = state.Stale ? ObservationQuality.Stale : state.Value.Quality;
                var presence = state.Presence == EffectPresence.ConfirmedRemoved ? state.Presence : !entity.InScope ? EffectPresence.OutOfScope : state.Presence;
                var instance = new EffectInstance(key, state.Value.EffectId, state.Value.InstanceId, state.Value.CasterKey,
                    state.AppliedAt, state.ExpiresAt, state.Duration, state.Value.Stacks, state.Value.Category, presence, quality, new(state.Revision));
                return new CombatEffectView(instance, state.ExpiresMs is { } expiry ? Math.Max(0, expiry - mono) : null,
                    state.Value.ObservedAt, state.Value.Aggregated, state.Stale);
            }).ToArray();
        var snapshot = new EffectsSnapshot(key, entity.Complete && entity.InScope, views.Select(x => x.Instance).ToArray(),
            entity.LastObserved, views.Any(x => x.Stale) || !entity.InScope ? ObservationQuality.Stale : entity.Complete ? ObservationQuality.Confirmed : ObservationQuality.Unknown,
            owner, observation, CombatSession.EntityId(key), revision);
        return new(snapshot.EntityId!, owner, observation, revision, snapshot, views);
    }
    public IReadOnlyList<CombatEffectObservation> Baseline(DateTimeOffset at, double mono) => entities.SelectMany(pair => pair.Value.Instances.Values
        .Where(x => pair.Value.InScope && !x.Stale && x.Presence == EffectPresence.Observed)
        .Select(x => x.Value with { Kind = CombatEffectChangeKind.Apply, ObservedAt = at, MonotonicMs = mono,
            DurationKind = x.Duration, DurationMs = x.ExpiresMs is { } expiry ? Math.Max(0, expiry - mono) : null })).ToArray();
}
