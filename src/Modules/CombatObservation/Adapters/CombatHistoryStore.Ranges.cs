using System.Numerics;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Modules.CombatObservation.Adapters;

/// <summary>
/// 按“时间范围 × 目标”重新统计（口径 observed-range-v1）：用记录保留的时间线事件按与分段相同的归属、分组和治疗规则重放。
/// 整段查询按记录缓存累计结果，进行中的记录只读取新增的时间线页；缓存条数与估算字节都有上限。
/// </summary>
public sealed partial class CombatHistoryStore
{
    public const string RangeCalculationVersion = "observed-range-v1";
    public const int MaximumRangeCacheEntries = 256;
    public const long MaximumRangeCacheBytes = 8L * 1024 * 1024;
    public const int MaximumRangeEncounters = 128;
    private static readonly CombatLimits RangeLimits = new();
    private readonly object rangeGate = new();
    private readonly Dictionary<(string Encounter, string? Target), RangePart> rangeCache = [];
    private long rangeTick;

    /// <summary>一个记录在某个范围内的重放结果；整段结果按记录缓存，进行中的记录从 LastIndex 之后接着读。</summary>
    private sealed class RangePart(EncounterState all, EncounterState? filtered)
    {
        public EncounterState All { get; } = all;
        public EncounterState? Filtered { get; } = filtered;
        public string Revision = "";
        public ulong LastIndex;
        public ulong RetainedEvents, RetainedHealCast, RetainedHealReceived;
        public BigInteger RetainedDamage;
        public double? FirstOffset, LastOffset;
        /// <summary>按时间线重放的破防区间，时点为相对记录开始的偏移。</summary>
        public readonly BreakIntervals Breaks = new();
        public long Used;
        public long Bytes => All.GroupBytes + (Filtered?.GroupBytes ?? 0) + 4096;
    }

    private sealed record RangeSource(CombatEncounter Encounter, double From, double To);

    public CombatRangeSummary QueryRange(string principal, bool owner, CombatRangeQuery query)
    {
        var sources = ResolveRange(principal, owner, query);
        lock (rangeGate) return Summarize(query, sources);
    }

    private IReadOnlyList<RangeSource> ResolveRange(string principal, bool owner, CombatRangeQuery query)
    {
        if (query is null || query.Scope is not ("encounter" or "observation") || query.TargetId?.Length > 128 ||
            query.FromMs is { } from && (!double.IsFinite(from) || from < 0) || query.ToMs is { } to && (!double.IsFinite(to) || to < (query.FromMs ?? 0)))
            throw new OperationException("request.invalid", "范围查询需要 encounter 或 observation 范围与有效的起止时间");
        if (query.Scope == "encounter")
        {
            if (query.EncounterId is not { Length: > 0 and <= 128 } id) throw new OperationException("request.invalid", "范围查询缺少战斗记录");
            var value = Encounter(id, principal, owner);
            var end = Math.Min(query.ToMs ?? value.Summary.DurationMs, value.Summary.DurationMs);
            return [new(value, Math.Min(query.FromMs ?? 0, end), end)];
        }
        if (query.ObservationId is not { Length: > 0 and <= 128 } observation) throw new OperationException("request.invalid", "范围查询缺少观察");
        if (query.FromMs is not null || query.ToMs is not null) throw new OperationException("request.invalid", "整场范围不接受起止时间");
        var segments = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT document FROM combat_encounters WHERE observation_id=$o AND ($owner=1 OR owner=$p) AND json_extract(document,'$.record') IS NULL ORDER BY json_extract(document,'$.startedMonotonicMs'),id";
            command.Parameters.AddWithValue("$o", observation); command.Parameters.AddWithValue("$owner", owner ? 1 : 0); command.Parameters.AddWithValue("$p", principal);
            using var rows = command.ExecuteReader(); var result = new List<CombatEncounter>();
            while (rows.Read()) result.Add(JsonSerializer.Deserialize<CombatEncounter>(rows.GetString(0), ContractJson.Options)!);
            return result;
        });
        if (segments.Count == 0) throw new OperationException("resource.notFound", "这次观察还没有战斗记录");
        // 整场为当前整场序号的分段：到期后、下一场开始前仍是原整场；重设后在下一笔伤害前没有分段。
        // 没有整场序号的旧观察从最近一次重设开始。
        List<CombatEncounter> selected;
        if (Workspace(principal, owner, observation)?.Observation.WholeOrdinal is { } whole)
        {
            selected = segments.Where(x => x.Segment?.Whole == whole).ToList();
            if (selected.Count == 0) throw new OperationException("resource.notFound", "这一场还没有战斗记录");
        }
        else
        {
            var start = segments.FindLastIndex(x => x.Segment?.StartReason == "reset");
            selected = segments.Skip(Math.Max(0, start)).ToList();
        }
        return selected.Select(x => new RangeSource(x, 0, x.Summary.DurationMs)).ToArray();
    }

    private CombatRangeSummary Summarize(CombatRangeQuery query, IReadOnlyList<RangeSource> sources)
    {
        var first = sources[0].Encounter;
        var healing = sources.Any(x => x.Encounter.Healing is not null);
        var all = Replay(first, healing); var filtered = query.TargetId is null ? null : Replay(first, false);
        var reasons = new List<string>();
        var limited = sources.Count > MaximumRangeEncounters;
        if (limited)
        {
            var gap = new CombatGap("range-segment-limit", 0, null, new(0));
            all.Gap(gap); filtered?.Gap(gap);
        }
        double duration = 0, cumulative = 0; double? firstOffset = null, lastOffset = null;
        var breaks = new List<BreakIntervals.Interval>(); var breaksTruncated = false;
        string? targetName = null;
        foreach (var source in limited ? sources.TakeLast(MaximumRangeEncounters) : sources)
        {
            var value = source.Encounter;
            var full = source.From <= 0 && source.To >= value.Summary.DurationMs;
            var part = full ? CachedPart(value, query.TargetId) : ScanPart(value, source.From, source.To, query.TargetId, null);
            all.Merge(part.All);
            breaks.AddRange(BreakIntervals.Clip(part.Breaks.All(), query.TargetId, source.From, source.To, cumulative - source.From));
            breaksTruncated |= part.Breaks.Truncated;
            if (filtered is not null && part.Filtered is not null) filtered.Merge(part.Filtered);
            if (part.FirstOffset is { } a) firstOffset = Math.Min(firstOffset ?? double.MaxValue, cumulative + a - source.From);
            if (part.LastOffset is { } b) lastOffset = Math.Max(lastOffset ?? double.MinValue, cumulative + b - source.From);
            var span = Math.Max(0, source.To - source.From);
            var gaps = value.Gaps.Where(gap => gap.FromMs <= source.To && (gap.ToMs ?? double.MaxValue) >= source.From)
                .Select(gap => gap with { FromMs = cumulative + Math.Max(0, gap.FromMs - source.From),
                    ToMs = gap.ToMs is { } end ? cumulative + Math.Clamp(end - source.From, 0, span) : null }).ToList();
            // 保留的事件与记录汇总不一致：时间线截断或旧记录缺少事件，范围结果不完整。
            var expectedEvents = value.Skills.Aggregate(0UL, (sum, group) => sum + group.EventCount.Value);
            if (part.RetainedEvents != expectedEvents || part.RetainedDamage != value.Summary.AttributedDamage.Value)
                gaps.Add(new("timeline-incomplete", cumulative, cumulative + span, new(0)));
            foreach (var gap in gaps) { all.Gap(gap); filtered?.Gap(gap); }
            if (HealingNotRetained(value, part.RetainedHealCast, part.RetainedHealReceived))
                reasons.Add("healing-not-retained");
            duration += span; cumulative += span;
        }
        var target = filtered ?? all;
        target.EndedMs = duration; all.EndedMs = duration;
        var resources = EmptyResources;
        var allSnapshot = all.Snapshot(new(0), duration, 1, resources);
        var snapshot = target.Snapshot(new(0), duration, 1, resources);
        if (query.TargetId is { } wanted)
        {
            var named = allSnapshot.Targets.FirstOrDefault(x => x.Key == wanted)
                ?? throw new OperationException("combat.entityOutOfScope", "目标不属于该范围");
            targetName = named.Name;
        }
        var last = sources[^1].Encounter;
        var targetSummary = snapshot.Summary;
        if (query.TargetId is not null)
        {
            // 选中目标时 DPS 以对该目标第一击到最后一击的时长为分母（偏移已扣除分段之间的空档）；范围时长仍为 durationMs。
            var span = firstOffset is { } a && lastOffset is { } b ? Math.Max(0, b - a) : 0;
            var targetDps = CombatRate.Calculate(targetSummary.AttributedDamage.Value, span);
            var targetReasons = targetSummary.Reasons.Where(x => x is not ("insufficientDuration" or "rateOutOfRange")).ToList();
            if (span == 0) targetReasons.Add("insufficientDuration");
            else if (targetDps is null) targetReasons.Add("rateOutOfRange");
            targetSummary = targetSummary with { EncounterDps = targetDps, DpsDurationMs = span, Reasons = targetReasons };
        }
        var summaryReasons = targetSummary.Reasons.Concat(reasons).Distinct(StringComparer.Ordinal).ToArray();
        var result = snapshot with
        {
            Id = query.Scope == "encounter" ? first.Id : "observation:" + query.ObservationId,
            Owner = first.Owner, ObservationId = first.ObservationId, GameSessionId = last.GameSessionId, Generation = last.Generation,
            Revision = last.Revision, StartedAt = first.StartedAt.AddMilliseconds(sources[0].From), EndedAt = last.EndedAt,
            StartedMonotonicMs = 0, EndedMonotonicMs = duration, Favorite = query.Scope == "encounter" && first.Favorite,
            BoundaryRevision = null, Segment = query.Scope == "encounter" ? first.Segment : null,
            Record = query.Scope == "encounter" ? first.Record : null,
            Healing = allSnapshot.Healing is not { } heal ? null : reasons.Contains("healing-not-retained")
                ? heal with { EncounterHps = null, RollingHps = null, Reasons = heal.Reasons.Append("healing-not-retained").ToArray() }
                : heal with { RollingHps = null },
            Summary = targetSummary with { EncounterId = query.Scope == "encounter" ? first.Id : "observation:" + query.ObservationId,
                Final = sources.All(x => x.Encounter.Summary.Final), BoundaryReason = last.Summary.BoundaryReason, RollingDps = null,
                Reasons = summaryReasons, Owner = first.Owner, ObservationId = first.ObservationId, Revision = last.Revision,
                Healing = null }
        };
        var totals = target.Attributed.Group("total", "total");
        var breakSummary = BreakIntervals.Summarize(breaks, totals.BreakedHitDamage, totals.BreakedHitCount, duration, breaksTruncated);
        result = result with { Break = breakSummary };
        var complete = !target.Partial && reasons.Count == 0;
        return new(RangeCalculationVersion, query.Scope, first.ObservationId, sources.Select(x => x.Encounter.Id).ToArray(),
            sources.Count == 1 ? sources[0].From : 0, sources.Count == 1 ? sources[0].To : duration, query.TargetId, targetName,
            result, totals, allSnapshot.Targets, allSnapshot.Summary.AttributedDamage, firstOffset, lastOffset,
            breakSummary, complete, summaryReasons);
    }

    private static readonly CombatResourceUsage EmptyResources = new(0, 0, 0, 0, new(0), new(0), new(0), new(0));

    private static bool HealingNotRetained(CombatEncounter value, ulong cast, ulong received) =>
        value.Healing is { } healing && (cast != healing.CastCount.Value || received != healing.ReceivedCount.Value ||
            healing.Reasons.Contains("healing-not-retained", StringComparer.Ordinal)) ||
        value.Gaps.Any(gap => gap.Reason == "healing-not-retained");

    private static EncounterState Replay(CombatEncounter value, bool healing) => new(value.Id, value.Owner, value.ObservationId,
        value.Summary.LocalEntity, value.Summary.DisplayName, value.StartedAt, 0, true, value.IncludeOwnedEntities, RangeLimits, value.Segment,
        healing, trackRolling: false, useCompleteWindowDps: true);

    private RangePart CachedPart(CombatEncounter value, string? target)
    {
        var key = (value.Id, target);
        rangeCache.TryGetValue(key, out var part);
        if (part is not null && part.Revision == value.Revision.ToString() && value.Summary.Final)
        { part.Used = ++rangeTick; return part; }
        part = ScanPart(value, 0, double.MaxValue, target, part);
        part.Revision = value.Revision.ToString(); part.Used = ++rangeTick;
        rangeCache[key] = part;
        while (rangeCache.Count > MaximumRangeCacheEntries || rangeCache.Values.Sum(x => x.Bytes) > MaximumRangeCacheBytes)
        {
            var oldest = rangeCache.Where(x => !ReferenceEquals(x.Value, part)).MinBy(x => x.Value.Used);
            if (oldest.Value is null) break;
            rangeCache.Remove(oldest.Key);
        }
        return part;
    }

    /// <summary>读取记录在 previous.LastIndex 之后的时间线页并重放；previous 为空时从头读取。</summary>
    private RangePart ScanPart(CombatEncounter value, double from, double to, string? target, RangePart? previous)
    {
        var part = previous ?? new RangePart(Replay(value, value.Healing is not null), target is null ? null : Replay(value, false));
        var names = value.Targets.ToDictionary(x => x.Key, x => x.Name, StringComparer.Ordinal);
        var labels = value.Targets.ToDictionary(x => x.Key, x => x.ResolvedName, StringComparer.Ordinal);
        var healTargets = value.Healing?.Targets?.ToDictionary(x => x.Key, StringComparer.Ordinal) ?? [];
        var local = value.Summary.LocalEntity;
        database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT document,bytes FROM combat_timeline_pages WHERE encounter_id=$id AND CAST(last_index AS INTEGER)>$last ORDER BY length(first_index),first_index";
            command.Parameters.AddWithValue("$id", value.Id); command.Parameters.AddWithValue("$last", (long)Math.Min(part.LastIndex, long.MaxValue));
            using var rows = command.ExecuteReader(); var bytes = 0L;
            while (rows.Read())
            {
                bytes += rows.GetInt32(1);
                if (bytes > 17 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "时间线读取超过预算");
                foreach (var item in JsonSerializer.Deserialize<CombatTimelineItem[]>(rows.GetString(0), ContractJson.Options)!)
                {
                    if (item.Index.Value <= part.LastIndex) continue;
                    part.LastIndex = item.Index.Value;
                    var inRange = item.OffsetMs >= from && item.OffsetMs <= to;
                    if (item.Damage is { } damage && damage.Amount.Value > 0 && item.Attribution is "local" or "owned" or "unknown")
                    {
                        if (item.Attribution == "local" || item.Attribution == "owned" && value.IncludeOwnedEntities)
                        { part.RetainedEvents++; part.RetainedDamage += damage.Amount.Value; }
                        if (!inRange) continue;
                        var id = CombatSession.EntityId(damage.TargetEntity);
                        var name = names.GetValueOrDefault(id) ?? damage.TargetEntity.RawInstanceId.ToString();
                        var label = labels.GetValueOrDefault(id);
                        part.All.Add(damage, item.Attribution, id, name, () => true, label);
                        var matches = target is null || id == target;
                        if (matches && part.Filtered is { } only) only.Add(damage, item.Attribution, id, name, () => true, label);
                        var counted = item.Attribution == "local" || item.Attribution == "owned" && value.IncludeOwnedEntities;
                        if (counted) part.Breaks.Damage(id, item.OffsetMs, damage.BreakOccurred, damage.BreakedHit);
                        if (matches && counted)
                        {
                            part.FirstOffset = Math.Min(part.FirstOffset ?? double.MaxValue, item.OffsetMs);
                            part.LastOffset = Math.Max(part.LastOffset ?? double.MinValue, item.OffsetMs);
                        }
                    }
                    else if (item.Entity is { BreakGauge: { Current: 0, Stale: false } } entity && item.Kind is "entity" or "entity-baseline")
                        part.Breaks.GaugeZero(entity.Id, item.OffsetMs);
                    else if (item.Heal is { } heal)
                    {
                        if (heal.Direction.KnownValue == CombatHealDirection.Cast) { part.RetainedHealCast++; if (heal.ReceiptMerged) part.RetainedHealReceived++; }
                        else part.RetainedHealReceived++;
                        if (!inRange || part.All.Healing is not { } accumulator) continue;
                        var targetKey = heal.TargetEntity;
                        var id = targetKey is null ? "" : CombatSession.EntityId(targetKey);
                        var retainedTarget = healTargets.GetValueOrDefault(id);
                        accumulator.Add(heal, targetKey == local || retainedTarget?.Self == true, id,
                            targetKey is null ? "" : retainedTarget?.Name ?? targetKey.RawInstanceId.ToString(), () => true);
                    }
                }
            }
            return true;
        });
        return part;
    }

    /// <summary>
    /// 把一个范围存为新的历史记录：重放范围内保留的伤害、治疗与缺口，时间线重新编号并按范围起点重排偏移；
    /// 记录名称 1–60 字，自动收藏。
    /// </summary>
    public async Task<CombatEncounter> SaveRangeAsync(string principal, bool owner, CombatRangeSaveRequest request, string? key, CancellationToken ct = default)
    {
        if (request?.Range is null || request.Name is null || request.Name.Trim().Length is < 1 or > 60 || request.Name.Any(char.IsControl))
            throw new OperationException("request.invalid", "记录名称为 1–60 个字符");
        var name = request.Name.Trim();
        var hash = JsonSerializer.Serialize(new { request.Range, name }, ContractJson.Options);
        var retry = await database.WriteAsync(tx => Receipt<CombatEncounter>(tx, principal, "save-range", "history", key, hash), ct);
        if (retry is not null) return retry;
        var sources = ResolveRange(principal, owner, request.Range with { TargetId = null });
        var first = sources[0].Encounter;
        var healing = sources.Any(x => x.Encounter.Healing is not null);
        var state = Replay(first, healing);
        if (sources.Count > MaximumRangeEncounters) state.Gap(new("range-segment-limit", 0, null, new(0)));
        var id = "enc-" + Guid.NewGuid().ToString("N");
        var items = new List<CombatTimelineItem>(); long bytes = 0; ulong truncated = 0;
        double cumulative = 0; var healingNotRetained = false;
        foreach (var source in sources.TakeLast(MaximumRangeEncounters))
        {
            var value = source.Encounter;
            var names = value.Targets.ToDictionary(x => x.Key, x => x.Name, StringComparer.Ordinal);
            var labels = value.Targets.ToDictionary(x => x.Key, x => x.ResolvedName, StringComparer.Ordinal);
            var healTargets = value.Healing?.Targets?.ToDictionary(x => x.Key, StringComparer.Ordinal) ?? [];
            var span = Math.Max(0, source.To - source.From);
            var retainedEvents = 0UL; var retainedDamage = BigInteger.Zero;
            ulong retainedHealCast = 0, retainedHealReceived = 0;
            foreach (var item in ReadTimeline(value.Id))
            {
                if (item.Damage is { } counted && counted.Amount.Value > 0 &&
                    (item.Attribution == "local" || item.Attribution == "owned" && value.IncludeOwnedEntities))
                { retainedEvents++; retainedDamage += counted.Amount.Value; }
                if (item.Heal is { } retainedHeal)
                {
                    if (retainedHeal.Direction.KnownValue == CombatHealDirection.Cast)
                    { retainedHealCast++; if (retainedHeal.ReceiptMerged) retainedHealReceived++; }
                    else retainedHealReceived++;
                }
                var inRange = item.Gap is { } gap
                    ? gap.FromMs <= source.To && (gap.ToMs ?? double.MaxValue) >= source.From
                    : item.OffsetMs >= source.From && item.OffsetMs <= source.To && (item.Damage is not null || item.Heal is not null ||
                        item.Entity is { BreakGauge: { Current: 0, Stale: false } } && item.Kind is "entity" or "entity-baseline");
                if (!inRange) continue;
                if (item.Damage is { } damage)
                {
                    if (damage.Amount.Value == 0 || item.Attribution is not ("local" or "owned" or "unknown")) continue;
                    var target = CombatSession.EntityId(damage.TargetEntity);
                    state.Add(damage, item.Attribution, target, names.GetValueOrDefault(target) ?? damage.TargetEntity.RawInstanceId.ToString(), () => true,
                        labels.GetValueOrDefault(target));
                }
                else if (item.Heal is { } heal && state.Healing is { } accumulator)
                {
                    var targetKey = heal.TargetEntity; var target = targetKey is null ? "" : CombatSession.EntityId(targetKey);
                    var retainedTarget = healTargets.GetValueOrDefault(target);
                    accumulator.Add(heal, targetKey == value.Summary.LocalEntity || retainedTarget?.Self == true, target,
                        targetKey is null ? "" : retainedTarget?.Name ?? targetKey.RawInstanceId.ToString(), () => true);
                }
                var copy = item with
                {
                    Index = new((ulong)items.Count + 1), OffsetMs = cumulative + Math.Clamp(item.OffsetMs - source.From, 0, span),
                    Gap = item.Gap is not { } copiedGap ? null : copiedGap with
                    {
                        FromMs = cumulative + Math.Max(0, copiedGap.FromMs - source.From),
                        ToMs = copiedGap.ToMs is { } end ? cumulative + Math.Clamp(end - source.From, 0, span) : null
                    }
                };
                // 破防区间按新记录的时间基准重放：本机伤害的破防标记与目标破防值归零（归零条目随记录一起保存）。
                if (copy.Damage is { } hit && hit.Amount.Value > 0 && (copy.Attribution == "local" || copy.Attribution == "owned" && value.IncludeOwnedEntities))
                    state.Breaks.Damage(CombatSession.EntityId(hit.TargetEntity), copy.OffsetMs, hit.BreakOccurred, hit.BreakedHit);
                else if (copy.Damage is null && copy.Entity is { BreakGauge: { Current: 0 } } zeroed) state.Breaks.GaugeZero(zeroed.Id, copy.OffsetMs);
                var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(copy, ContractJson.Options));
                if (items.Count >= RangeLimits.MaximumTimelineItems || bytes + size > RangeLimits.MaximumTimelineBytes) { truncated++; continue; }
                items.Add(copy); bytes += size;
            }
            foreach (var gap in value.Gaps.Where(gap => gap.FromMs <= source.To && (gap.ToMs ?? double.MaxValue) >= source.From))
                state.Gap(gap with { FromMs = cumulative + Math.Max(0, gap.FromMs - source.From),
                    ToMs = gap.ToMs is { } end ? cumulative + Math.Clamp(end - source.From, 0, span) : null });
            var expected = value.Skills.Aggregate(0UL, (sum, group) => sum + group.EventCount.Value);
            if (retainedEvents != expected || retainedDamage != value.Summary.AttributedDamage.Value)
                state.Gap(new("timeline-incomplete", cumulative, cumulative + span, new(0)));
            if (HealingNotRetained(value, retainedHealCast, retainedHealReceived))
            {
                healingNotRetained = true;
                if (!value.Gaps.Any(gap => gap.Reason == "healing-not-retained"))
                    state.Gap(new("healing-not-retained", cumulative, cumulative + span, new(0)));
            }
            cumulative += span;
        }
        if (truncated > 0) state.Gap(new("timeline-capacity", cumulative, cumulative, new(0)));
        state.EndedMs = cumulative; state.EndedAt = sources[^1].Encounter.EndedAt ?? clock.GetUtcNow(); state.Boundary = "saved-range";
        var keepMeasurement = request.Range.Scope == "encounter" && first.Record?.Measurement is { } measured &&
            sources[0].From <= 0 && sources[0].To >= first.Summary.DurationMs ? measured : null;
        state.Record = new(keepMeasurement is null ? "saved-range" : "timed-measurement", name, keepMeasurement,
            new(request.Range.Scope, sources.Select(x => x.Encounter.Id).ToArray(), request.Range.FromMs, request.Range.ToMs));
        var snapshot = state.Snapshot(new(1), cumulative, first.Summary.RollingWindowMs is > 0 and <= int.MaxValue ? (int)first.Summary.RollingWindowMs : 60000,
            EmptyResources with { TimelineItems = items.Count, TimelineBytes = new((ulong)bytes), TruncatedTimelineItems = new(truncated) });
        var savedHealing = snapshot.Healing;
        if (healingNotRetained && savedHealing is { } retained)
            savedHealing = retained with { EncounterHps = null, RollingHps = null,
                Reasons = retained.Reasons.Append("healing-not-retained").Distinct(StringComparer.Ordinal).ToArray() };
        var stored = snapshot with
        {
            Id = id, Owner = principal, ObservationId = first.ObservationId, GameSessionId = first.GameSessionId, Generation = first.Generation,
            StartedAt = first.StartedAt.AddMilliseconds(sources[0].From), StartedMonotonicMs = 0, EndedMonotonicMs = cumulative, Favorite = true,
            BoundaryRevision = new(1), Segment = null, Healing = savedHealing,
            Summary = snapshot.Summary with { EncounterId = id, Final = true, RollingDps = null,
                Owner = principal, ObservationId = first.ObservationId, Revision = new(1),
                Healing = savedHealing is null ? null : savedHealing with { Targets = null } }
        };
        var json = JsonSerializer.Serialize(stored, ContractJson.Options);
        var pages = EncodePages(items.Select(item => new CombatTimelineRecord(id, item)).ToArray());
        return await database.WriteAsync(tx =>
        {
            var receipt = Receipt<CombatEncounter>(tx, principal, "save-range", "history", key, hash); if (receipt is not null) return receipt;
            tx.Execute("INSERT INTO combat_encounters(id,owner,observation_id,revision,closed,quality,favorite,started_at,document,bytes) VALUES($id,$owner,$observation,'1',1,$quality,1,$at,$json,$bytes)",
                ("$id", id), ("$owner", principal), ("$observation", first.ObservationId), ("$quality", stored.Summary.Quality.ToString()),
                ("$at", stored.StartedAt.ToUnixTimeMilliseconds()), ("$json", json), ("$bytes", Encoding.UTF8.GetByteCount(json)));
            foreach (var page in pages)
            {
                tx.Execute("INSERT INTO combat_timeline_pages VALUES($id,$first,$last,$json,$bytes)",
                    ("$id", page.Encounter), ("$first", page.First), ("$last", page.Last), ("$json", page.Json), ("$bytes", page.Bytes));
                tx.Execute("UPDATE combat_encounters SET timeline_bytes=timeline_bytes+$bytes WHERE id=$id", ("$bytes", page.Bytes), ("$id", id));
            }
            Append(tx, "combat-closed", id, stored.Revision, "combat.encounter.closed",
                new CombatEncounterClosed(stored.Owner, stored.ObservationId, id, stored.Revision, stored.Summary, stored.Summary.BoundaryReason));
            AdvanceHistory(tx); SaveReceipt(tx, principal, "save-range", "history", key!, hash, stored);
            return stored;
        }, ct);
    }

    private IReadOnlyList<CombatTimelineItem> ReadTimeline(string id) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT document,bytes FROM combat_timeline_pages WHERE encounter_id=$id ORDER BY length(first_index),first_index";
        command.Parameters.AddWithValue("$id", id);
        using var rows = command.ExecuteReader(); var result = new List<CombatTimelineItem>(); var bytes = 0L;
        while (rows.Read())
        {
            bytes += rows.GetInt32(1);
            if (bytes > 17 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "时间线读取超过预算");
            result.AddRange(JsonSerializer.Deserialize<CombatTimelineItem[]>(rows.GetString(0), ContractJson.Options)!);
        }
        return (IReadOnlyList<CombatTimelineItem>)result;
    });

}
