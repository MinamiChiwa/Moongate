using MabiTool.Client;
using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

public sealed partial class LiveBackend
{
    /// <summary>一次刷新中首击读取最多发出的请求数，分段详情与时间线页共用该预算。</summary>
    internal const int FirstAttackPagesPerRefresh = 5;
    internal static readonly TimeSpan FirstAttackRetryInterval = TimeSpan.FromSeconds(5);
    private readonly FirstAttackReader firstAttackReader = new();

    private async Task<IReadOnlyList<CombatDamageEvent>?> FirstAttacksAsync(MabiToolClient api, CombatWorkspace? workspace,
        CombatRangeSummary? range, IReadOnlyList<CombatEncounter> history, CombatSelectionSnapshot chosen, CancellationToken token)
    {
        var (scope, target) = (chosen.Scope, chosen.Target);
        var direct = scope == "segment" && target is null;
        var shown = direct ? workspace?.Encounter : range?.Result;
        var matches = direct || range is not null && range.TargetId == target && (scope switch
        {
            "segment" => range.Scope == "encounter" && range.Result.Id == workspace?.Encounter?.Id,
            "observation" => range.Scope == "observation" && range.ObservationId == workspace?.Observation.Id,
            _ => scope == "measurement:" + range.Result.Id && range.Scope == "encounter"
        });
        if (shown is null || !matches)
        {
            firstAttackReader.Clear();
            return null;
        }
        var sources = direct ? new[] { shown.Id } : range!.EncounterIds;
        var key = scope + "|" + target + "|" + (direct ? shown.Id : range!.ObservationId);
        var request = new FirstAttackRange(key, shown, sources, target,
            direct || range!.Scope == "observation" ? null : range.FromMs,
            direct || range!.Scope == "observation" ? null : range.ToMs);
        var known = workspace?.Encounter is { } current ? history.Prepend(current).ToArray() : history;
        return await firstAttackReader.ReadAsync(request, known,
            (id, ct) => api.GetCombatEncounterAsync(id, ct),
            (id, query, ct) => api.QueryCombatTimelineAsync(id, query, ct), token);
    }

    internal sealed record FirstAttackRange(string Key, CombatEncounter Summary, IReadOnlyList<string> Sources,
        string? TargetId = null, double? FromMs = null, double? ToMs = null);

    /// <summary>
    /// 从较新的分段查起，只保留各来源最近的一条已归属首击及分页进度。范围或目标切换时清空，
    /// 最多缓存 128 个来源的读取进度；按请求预算继续查更早来源，缺少事件时按五秒间隔补读。
    /// </summary>
    internal sealed class FirstAttackReader(TimeProvider? time = null)
    {
        internal const int MaximumSources = 128;
        private readonly TimeProvider clock = time ?? TimeProvider.System;
        private readonly Dictionary<string, Scan> scans = new(StringComparer.Ordinal);
        private string? selectionKey;
        private ulong? previousCount;
        private string? newestSource;
        private int sourceCount, resumeSource;
        internal int CachedSources => scans.Count;

        private sealed record SourceInfo(ulong? Count, bool IncludeOwned, bool Final);
        private sealed class Scan
        {
            public SourceInfo? Info;
            public DateTimeOffset MetadataAt, QueriedAt;
            public ulong? QueriedCount;
            public ulong SeenTo, Found;
            public double ScannedTo, QueryFrom;
            public double? QueryTo;
            public string? Cursor;
            public bool Finished;
            public CombatDamageEvent? Latest;
        }

        public void Clear()
        {
            selectionKey = null; previousCount = null; newestSource = null; sourceCount = resumeSource = 0; scans.Clear();
        }

        public async Task<IReadOnlyList<CombatDamageEvent>?> ReadAsync(FirstAttackRange range,
            IReadOnlyList<CombatEncounter> known, Func<string, CancellationToken, Task<CombatEncounter>> readEncounter,
            Func<string, CombatTimelineQuery, CancellationToken, Task<CombatTimelinePage>> readTimeline, CancellationToken token)
        {
            if (selectionKey != range.Key) { Clear(); selectionKey = range.Key; }
            var sources = range.Sources.Distinct(StringComparer.Ordinal).Reverse().ToArray();
            foreach (var id in scans.Keys.Where(id => !sources.Contains(id, StringComparer.Ordinal)).ToArray()) scans.Remove(id);
            var count = Count(range.Summary, null);
            if (count is null || count == 0)
            {
                scans.Clear(); previousCount = count; resumeSource = 0; return count is null ? null : [];
            }
            if (previousCount != count || sourceCount != sources.Length || newestSource != sources.FirstOrDefault()) resumeSource = 0;
            previousCount = count; sourceCount = sources.Length; newestSource = sources.FirstOrDefault();
            var budget = FirstAttackPagesPerRefresh;
            var now = clock.GetUtcNow();
            for (var position = resumeSource; position < sources.Length; position++)
            {
                resumeSource = position;
                var id = sources[position];
                token.ThrowIfCancellationRequested();
                if (!scans.TryGetValue(id, out var scan))
                {
                    if (scans.Count == MaximumSources)
                        scans.Remove(scans.FirstOrDefault(entry => entry.Value.Latest is null).Key ?? scans.Keys.First());
                    scans[id] = scan = new();
                }
                var value = known.FirstOrDefault(item => item.Id == id);
                // 单记录范围的汇总已按时间和目标筛选，计数与本次查询一致。
                if (sources.Length == 1 && range.Summary.Id == id) value = range.Summary;
                if (value is not null) scan.Info = Info(value, range.TargetId);
                else if ((scan.Info is null || !scan.Info.Final) && now - scan.MetadataAt >= FirstAttackRetryInterval)
                {
                    if (budget-- <= 0) break;
                    try { scan.Info = Info(await readEncounter(id, token), range.TargetId); scan.MetadataAt = now; }
                    // 读取被拒绝（如记录已不存在）也记下时刻，五秒后再试，不按每轮刷新反复请求。
                    catch (MabiToolApiException) { scan.MetadataAt = now; break; }
                }
                if (scan.Info is not { Count: > 0 } info) { resumeSource = position + 1; continue; }
                var expected = info.Count!.Value;
                var due = scan.QueriedCount != expected || scan.Cursor is not null ||
                    scan.Found < expected && now - scan.QueriedAt >= FirstAttackRetryInterval;
                if (!due)
                {
                    if (scan.Latest is { } latest) return [latest];
                    break;
                }
                if (budget <= 0) break;
                if (scan.QueriedCount != expected || scan.Cursor is null)
                {
                    scan.QueryFrom = Math.Max(range.FromMs ?? 0, scan.ScannedTo);
                    scan.QueryTo = range.ToMs;
                    scan.Cursor = null; scan.Finished = false;
                }
                scan.QueriedCount = expected; scan.QueriedAt = now;
                while (budget > 0 && !scan.Finished)
                {
                    budget--;
                    try
                    {
                        var page = await readTimeline(id, new(PageSize: 1000, Cursor: scan.Cursor, FromMs: scan.QueryFrom,
                            ToMs: scan.QueryTo, EntityId: range.TargetId), token);
                        foreach (var item in page.Items)
                        {
                            if (item.Index.Value <= scan.SeenTo) continue;
                            scan.SeenTo = item.Index.Value;
                            if (item.Damage is not { FirstAttack: true } damage || damage.Amount.Value == 0 ||
                                item.Attribution != "local" && !(item.Attribution == "owned" && info.IncludeOwned)) continue;
                            // 新到的首击可能早于已经读取的普通命中；补读从最近首击开始，避免把它跨过去。
                            scan.ScannedTo = Math.Max(scan.ScannedTo, item.OffsetMs);
                            scan.Found++;
                            if (scan.Latest is null || damage.MonotonicMs >= scan.Latest.MonotonicMs) scan.Latest = damage;
                        }
                        scan.Cursor = page.NextCursor;
                        scan.Finished = scan.Cursor is null;
                    }
                    catch (MabiToolApiException) { scan.Cursor = null; scan.Finished = true; }
                }
                if (scan.Latest is { } found) return [found];
                break;
            }
            var current = sources.Select(id => scans.GetValueOrDefault(id)?.Latest).FirstOrDefault(item => item is not null);
            return current is null ? [] : [current];
        }

        private static SourceInfo Info(CombatEncounter encounter, string? target) =>
            new(Count(encounter, target), encounter.IncludeOwnedEntities, encounter.Summary.Final);

        private static ulong? Count(CombatEncounter encounter, string? target)
        {
            var groups = target is null ? encounter.Skills : encounter.Targets.Where(group => group.Key == target).ToArray();
            if (target is not null && groups.Count == 0) return 0;
            var counts = groups.Select(group => group.FirstAttackCount).Where(value => value is not null).ToArray();
            return counts.Length == 0 ? null : counts.Aggregate(0UL, (sum, value) => sum + value!.Value.Value);
        }
    }
}
