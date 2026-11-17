using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Combat;

namespace MabiTool.Desktop.Backend;

// DPS 明细窗口：窗口有自己的范围与目标（打开时取主页面当时的选择），按它读取范围汇总与时间线分桶，与主页面互不影响。
// 窗口内切换只发出窗口自己的读取并通知窗口（CombatDetailChanged），不唤醒整轮刷新、不通知主页面与浮窗；
// 例行刷新时随主页面同一轮更新窗口的数据。时间线只在窗口打开时读取，同一记录与目标在修订未变时不重读，至多每 3 秒一次。
public sealed partial class LiveBackend
{
    internal static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(3);
    /// <summary>整场曲线每轮至多新读这么多个分段的时间线；已结束的分段读一次后不再重读。</summary>
    internal const int WholeTimelineReadsPerRound = 8;
    private bool combatDetailOpen;
    private readonly CombatSelection detailSelection = new();
    private CombatRangeSummary? detailRange;
    private string? detailRangeProblem, detailRangeKey;
    private (string Key, DateTimeOffset At)? timelineRead;
    private CombatTimelinePage? timelinePage;
    private CombatTimelineView? wholeTimeline;
    private string? timelineProblem;
    // 整场曲线的分段分桶：记录 ID → (修订, 开始时刻, 分桶)；按目标筛选不同时整份换掉。
    private readonly Dictionary<string, (string Revision, DateTimeOffset Start, IReadOnlyList<CombatDamageBucket> Buckets)> segmentBuckets =
        new(StringComparer.Ordinal);
    private string? segmentBucketsTarget;
    private int detailReads;

    public event EventHandler? CombatDetailChanged;

    public void SetCombatDetailOpen(bool open)
    {
        lock (gate)
        {
            combatDetailOpen = open;
            timelineRead = null; timelinePage = null; wholeTimeline = null; timelineProblem = null;
            detailRange = null; detailRangeProblem = null; detailRangeKey = null;
            segmentBuckets.Clear(); segmentBucketsTarget = null;
        }
        if (!open) return;
        var chosen = selection.Snapshot();
        detailSelection.Assign(chosen.Scope, chosen.Target);
        _ = RefreshDetailNowAsync();
    }

    /// <summary>窗口自己的范围与目标意图：只改窗口的选择，立即通知窗口（只移动选中标记），再单独读取新选择的结果。</summary>
    private Task SelectDetailAsync(string intent, string? argument)
    {
        switch (intent)
        {
            case "combat.detail.scope": detailSelection.SelectScope(argument); break;
            case "combat.detail.scope.live": detailSelection.BackToLive(); break;
            case "combat.detail.target": detailSelection.SelectTarget(argument); break;
        }
        NotifyDetail();
        return RefreshDetailNowAsync();
    }

    private void NotifyDetail() => dispatch(() => CombatDetailChanged?.Invoke(this, EventArgs.Empty));

    // 窗口切换后单独读取：范围汇总与时间线，完成后只通知窗口。
    private async Task RefreshDetailNowAsync()
    {
        if (client is not { } api) return;
        var (current, _) = Read();
        if (!current.Connected) return;
        Interlocked.Increment(ref detailReads);
        try
        {
            await RefreshDetailAsync(api, current.Workspace, current.History, current.Range, current.RangeProblem, current.RangeKey, lifetime.Token);
        }
        catch (Exception error) when (error is MabiToolApiException or HttpRequestException or IOException or OperationCanceledException or InvalidOperationException) { }
        NotifyDetail();
    }

    /// <summary>测试用：窗口切换触发的单独读取次数。</summary>
    internal int DetailReads => Volatile.Read(ref detailReads);

    /// <summary>窗口的数据：窗口自己的范围与目标；结果还没到时保留上一次的显示（Pending）。主页面同一选择的结果直接沿用。</summary>
    public CombatPageData GetCombatDetail()
    {
        var (current, _) = Read();
        var chosen = detailSelection.Snapshot();
        CombatRangeSummary? range; string? problem, key;
        lock (gate) { range = detailRange; problem = detailRangeProblem; key = detailRangeKey; }
        if (current.RangeKey == chosen.Key) (range, problem, key) = (current.Range, current.RangeProblem, current.RangeKey);
        var direct = chosen.Scope == "segment" && chosen.Target is null;
        var pending = !direct && key != chosen.Key;
        var data = CombatMapper.Map(new(current.Connected, current.Binding?.Binding is not null, current.Workspace,
            current.History, current.TargetEffects, current.EffectProblem, current.Coverage, null, chosen.Scope, chosen.Target,
            pending ? null : range, pending ? null : problem, current.Measurement, current.MeasurementAt, current.EffectsAt))
            with { SelectedScope = chosen.Scope, SelectedTarget = chosen.Target, Pending = pending };
        return CombatEffectFavorites.Apply(data);
    }

    private string TimelineSignature()
    {
        lock (gate) return $"{combatDetailOpen}:{timelineRead?.Key}:{timelineRead?.At.Ticks}:{timelineProblem}:{detailRangeKey}:{detailRange?.Result.Revision}";
    }

    public CombatTimelineView? GetCombatTimeline()
    {
        lock (gate)
            return !combatDetailOpen ? CombatTimelineView.Pending
                : wholeTimeline is not null && timelineProblem is null ? wholeTimeline
                : CombatMapper.TimelineView(timelinePage, timelineProblem);
    }

    /// <summary>范围对应的单个战斗记录：当前分段或所选的测量；整场由各分段合并（返回空），没有记录时给出原因。</summary>
    internal static (string? EncounterId, double DurationMs, string? Problem) TimelineSource(CombatWorkspace? workspace, string scope,
        CombatRangeSummary? range)
    {
        if (scope == "segment")
            return workspace?.Encounter is { } encounter ? (encounter.Id, encounter.Summary.DurationMs, null) : (null, 0, "no-record");
        if (scope.StartsWith("measurement:", StringComparison.Ordinal))
            return (scope["measurement:".Length..], range?.Result.Summary.DurationMs ?? 60000, null);
        return (null, 0, scope.StartsWith("measurement", StringComparison.Ordinal) ? "no-record" : null);
    }

    /// <summary>
    /// 窗口的范围汇总与时间线：选择与主页面这一轮相同时直接沿用主页面的结果，否则按窗口的选择另读一次。
    /// 窗口没打开时什么都不读。
    /// </summary>
    private async Task RefreshDetailAsync(MabiToolClient api, CombatWorkspace? workspace, IReadOnlyList<CombatEncounter> history,
        CombatRangeSummary? mainRange, string? mainProblem, string? mainKey, CancellationToken token)
    {
        bool open;
        lock (gate) open = combatDetailOpen;
        if (!open) return;
        var chosen = detailSelection.Snapshot();
        CombatRangeSummary? range = null; string? problem = null;
        if (chosen.Key == mainKey) (range, problem) = (mainRange, mainProblem);
        else
        {
            var state = Read().State with { Workspace = workspace, History = history };
            var query = RangeQuery(state, chosen.Scope, chosen.Target, forDisplay: true);
            if (query is not null && !(query.Scope == "observation" && !HasSegments(workspace, history)))
                try { range = await api.QueryCombatRangeAsync(query, token); }
                catch (MabiToolApiException error) when (error.Problem?.Code == "combat.entityOutOfScope" && chosen.Target is not null)
                {
                    // 所选目标已不在范围内：窗口回到“全部”，下一次读取按新选择。
                    detailSelection.DropTarget(chosen.Target);
                    return;
                }
                catch (MabiToolApiException error)
                {
                    // 整场在这一场还没有分段时为空（例如重设后、下一次伤害前），不算错误。
                    problem = error.Status == 404 && query.Scope == "observation" ? null : error.Problem?.Code ?? $"http.{error.Status}";
                }
        }
        lock (gate)
        {
            if (detailSelection.Snapshot().Key != chosen.Key) return;
            (detailRange, detailRangeProblem, detailRangeKey) = (range, problem, chosen.Key);
        }
        await RefreshTimelineAsync(api, workspace, history, range, chosen, token);
    }

    private async Task RefreshTimelineAsync(MabiToolClient api, CombatWorkspace? workspace, IReadOnlyList<CombatEncounter> history,
        CombatRangeSummary? range, CombatSelectionSnapshot chosen, CancellationToken token)
    {
        var (scope, target) = (chosen.Scope, chosen.Target);
        if (scope == "observation")
        {
            await RefreshWholeTimelineAsync(api, workspace, history, range, target, token);
            return;
        }
        var (id, duration, problem) = TimelineSource(workspace, scope, range);
        if (id is null)
        {
            lock (gate) { timelinePage = null; wholeTimeline = null; timelineProblem = problem; }
            return;
        }
        var revision = scope == "segment" ? workspace?.Encounter?.Revision.ToString() : range?.Result.Revision.ToString();
        var key = string.Join('|', id, target, revision);
        lock (gate)
            if (timelineRead is { } last && (last.Key == key || DateTimeOffset.UtcNow - last.At < TimelineInterval)) return;
        try
        {
            var page = await api.QueryCombatTimelineAsync(id, new(PageSize: 1, BucketWidthMs: CombatMapper.BucketWidth(duration), EntityId: target), token);
            lock (gate) { timelinePage = page; wholeTimeline = null; timelineProblem = null; timelineRead = (key, DateTimeOffset.UtcNow); }
        }
        catch (MabiToolApiException error)
        {
            lock (gate) { timelinePage = null; wholeTimeline = null; timelineProblem = error.Problem?.Code ?? $"http.{error.Status}"; timelineRead = (key, DateTimeOffset.UtcNow); }
        }
    }

    /// <summary>
    /// 整场曲线：按范围汇总里的分段读取各自的时间线分桶（桶宽取各分段自己的时长），按本机时钟合并。已结束的分段修订不再变化，
    /// 读一次后沿用；进行中的分段随修订重读，整体至多每 3 秒一轮、每轮至多读 <see cref="WholeTimelineReadsPerRound"/> 个分段。
    /// </summary>
    private async Task RefreshWholeTimelineAsync(MabiToolClient api, CombatWorkspace? workspace, IReadOnlyList<CombatEncounter> history,
        CombatRangeSummary? range, string? target, CancellationToken token)
    {
        if (range is null)
        {
            lock (gate) { timelinePage = null; wholeTimeline = null; timelineProblem = "no-record"; }
            return;
        }
        var ids = range.EncounterIds;
        var known = history.Concat(workspace?.Encounter is { } live ? [live] : []).GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var key = string.Join('|', target, range.Result.Revision, string.Join(',', ids.Select(id => known.TryGetValue(id, out var item) ? id + ":" + item.Revision : id)));
        lock (gate)
        {
            if (timelineRead is { } last && (last.Key == key || DateTimeOffset.UtcNow - last.At < TimelineInterval)) return;
            if (segmentBucketsTarget != target) { segmentBuckets.Clear(); segmentBucketsTarget = target; }
        }
        var reads = 0;
        try
        {
            foreach (var id in ids)
            {
                (string Revision, DateTimeOffset Start, IReadOnlyList<CombatDamageBucket> Buckets) cached;
                bool have;
                lock (gate) have = segmentBuckets.TryGetValue(id, out cached);
                var encounter = known.GetValueOrDefault(id);
                if (have && (encounter is null || cached.Revision == encounter.Revision.ToString())) continue;
                if (reads >= WholeTimelineReadsPerRound) break;
                encounter ??= await api.GetCombatEncounterAsync(id, token);
                reads++;
                var page = await api.QueryCombatTimelineAsync(id, new(PageSize: 1, BucketWidthMs: CombatMapper.BucketWidth(encounter.Summary.DurationMs),
                    EntityId: target), token);
                lock (gate) segmentBuckets[id] = (encounter.Revision.ToString(), encounter.StartedAt, page.DamageBuckets);
            }
        }
        catch (MabiToolApiException error) when (error.Status == 404) { }
        lock (gate)
        {
            var parts = ids.Where(segmentBuckets.ContainsKey).Select(id => (segmentBuckets[id].Start, segmentBuckets[id].Buckets)).ToList();
            wholeTimeline = parts.Count == 0 ? CombatTimelineView.Pending : CombatMapper.WholeTimelineView(parts);
            timelinePage = null; timelineProblem = null;
            // 本轮没读完的分段下一轮接着读（键不变也不跳过）。
            timelineRead = (reads >= WholeTimelineReadsPerRound ? key + "|more" : key, DateTimeOffset.UtcNow);
        }
    }
}
