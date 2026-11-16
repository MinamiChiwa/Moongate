using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

public static class CombatTimelineProjection
{
    private readonly record struct Interval(double From, double To);
    private sealed record ActiveEffect(string Entity, UInt64String Effect, double From, double? Expiry);
    public static IReadOnlyList<CombatDamageBucket> DamageBuckets(CombatEncounter encounter, IReadOnlyList<CombatTimelineItem> items,
        double from, double to, int width, string? entityId = null)
    {
        var count = (int)Math.Ceiling((to - from) / width);
        if (count > 1000) throw new ArgumentException("时间窗口最多返回 1000 个桶，请增大桶宽");
        if (count <= 0) return [];
        var buckets = Enumerable.Range(0, count).Select(_ => new DamageAccumulator()).ToArray();
        var counts = new ulong[count];
        foreach (var item in items)
        {
            if (item.Damage is not { } damage || item.OffsetMs < from || item.OffsetMs > to ||
                item.Attribution != "local" && !(item.Attribution == "owned" && encounter.IncludeOwnedEntities) ||
                entityId is not null && CombatSession.EntityId(damage.TargetEntity) != entityId && CombatSession.EntityId(damage.SourceEntity) != entityId) continue;
            var index = Math.Min(count - 1, (int)((item.OffsetMs - from) / width)); buckets[index].Add(damage); counts[index]++;
        }
        return buckets.Select((bucket, index) => new CombatDamageBucket(from + index * width, Math.Min(to, from + (index + 1) * width), bucket.Snapshot(), new(counts[index]))).ToArray();
    }
    public static IReadOnlyList<CombatEffectCoverage> Coverage(CombatEncounter encounter, IReadOnlyList<CombatTimelineItem> items,
        double from, double to, string? entityId = null)
    {
        var intervals = new Dictionary<(string Entity, UInt64String Effect), List<Interval>>();
        var active = new Dictionary<string, ActiveEffect>(StringComparer.Ordinal);
        var scopeChanges = new Dictionary<string, List<(double At, bool InScope)>>(StringComparer.Ordinal);
        foreach (var item in items.OrderBy(x => x.OffsetMs).ThenBy(x => x.Index.Value))
        {
            if (item.Entity is { } entity)
            {
                if (!scopeChanges.TryGetValue(entity.Id, out var changes)) scopeChanges.Add(entity.Id, changes = []);
                changes.Add((item.OffsetMs, entity.InScope && entity.Quality.KnownValue == ObservationQuality.Confirmed));
            }
            if (item.Effect is not { } effect) continue;
            var entityKey = CombatSession.EntityId(effect.EntityKey);
            if (entityId is not null && entityKey != entityId) continue;
            var key = entityKey + ":" + effect.EffectId + ":" + effect.InstanceId + ":" + (effect.CasterKey is { } caster ? CombatSession.EntityId(caster) : "aggregate");
            if (!intervals.ContainsKey((entityKey, effect.EffectId))) intervals.Add((entityKey, effect.EffectId), []);
            if (effect.Kind.KnownValue == CombatEffectChangeKind.StackChanged) continue;
            if (active.Remove(key, out var previous)) End(previous, item.OffsetMs);
            if (effect.Kind.KnownValue != CombatEffectChangeKind.Remove)
                active.Add(key, new(entityKey, effect.EffectId, item.OffsetMs,
                    effect.DurationKind.KnownValue == EffectDurationKind.Finite && effect.DurationMs is > 0 ? item.OffsetMs + effect.DurationMs : null));
        }
        foreach (var remaining in active.Values) End(remaining, to);
        var gaps = encounter.Gaps.Select(gap => new Interval(gap.FromMs, gap.ToMs ?? to)).ToArray();
        var result = new List<CombatEffectCoverage>();
        foreach (var (key, spans) in intervals.OrderBy(x => x.Key.Entity, StringComparer.Ordinal).ThenBy(x => x.Key.Effect.Value))
        {
            var scope = new List<Interval>(); double? open = null;
            if (scopeChanges.TryGetValue(key.Entity, out var changes))
            {
                foreach (var change in changes.OrderBy(x => x.At))
                    if (change.InScope) open ??= change.At;
                    else if (open is { } start) { scope.Add(new(start, change.At)); open = null; }
                if (open is { } endStart) scope.Add(new(endStart, to));
            }
            else scope.Add(new(0, to));
            var observable = Subtract(Merge(scope, from, to), Merge(gaps, from, to));
            var coverage = Intersect(Merge(spans, from, to), observable);
            var denominator = observable.Sum(x => x.To - x.From); var covered = coverage.Sum(x => x.To - x.From);
            result.Add(new(key.Entity, key.Effect, from, to, covered, denominator, denominator > 0 ? covered / denominator : null, encounter.Gaps));
        }
        return result;
        void End(ActiveEffect effect, double end) => intervals[(effect.Entity, effect.Effect)].Add(new(effect.From, Math.Min(end, effect.Expiry ?? end)));
    }
    private static IReadOnlyList<Interval> Merge(IEnumerable<Interval> source, double from, double to)
    {
        var result = new List<Interval>();
        foreach (var span in source.Select(x => new Interval(Math.Max(from, x.From), Math.Min(to, x.To))).Where(x => x.To > x.From).OrderBy(x => x.From))
            if (result.Count > 0 && span.From <= result[^1].To) result[^1] = result[^1] with { To = Math.Max(result[^1].To, span.To) };
            else result.Add(span);
        return result;
    }
    private static IReadOnlyList<Interval> Intersect(IReadOnlyList<Interval> first, IReadOnlyList<Interval> second)
    {
        var result = new List<Interval>(); var i = 0; var j = 0;
        while (i < first.Count && j < second.Count)
        {
            var from = Math.Max(first[i].From, second[j].From); var to = Math.Min(first[i].To, second[j].To);
            if (to > from) result.Add(new(from, to));
            if (first[i].To <= second[j].To) i++; else j++;
        }
        return result;
    }
    private static IReadOnlyList<Interval> Subtract(IReadOnlyList<Interval> spans, IReadOnlyList<Interval> gaps)
    {
        var result = new List<Interval>();
        foreach (var span in spans)
        {
            var cursor = span.From;
            foreach (var gap in gaps)
            {
                if (gap.To <= cursor || gap.From >= span.To) continue;
                if (gap.From > cursor) result.Add(new(cursor, gap.From)); cursor = Math.Max(cursor, gap.To);
            }
            if (cursor < span.To) result.Add(new(cursor, span.To));
        }
        return result;
    }
}
