using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Combat;

namespace MabiTool.Desktop.Backend;

// DPS 明细窗口的时间线分桶：只在窗口打开时读取；同一记录与目标在修订未变时不重读，至多每 3 秒一次。
public sealed partial class LiveBackend
{
    internal static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(3);
    private bool combatDetailOpen;
    private (string Key, DateTimeOffset At)? timelineRead;
    private CombatTimelinePage? timelinePage;
    private string? timelineProblem;

    public void SetCombatDetailOpen(bool open)
    {
        lock (gate)
        {
            combatDetailOpen = open;
            if (!open) { timelineRead = null; timelinePage = null; timelineProblem = null; }
        }
        if (open) Signal();
    }

    private string TimelineSignature()
    {
        lock (gate) return $"{combatDetailOpen}:{timelineRead?.Key}:{timelineRead?.At.Ticks}:{timelineProblem}";
    }

    public CombatTimelineView? GetCombatTimeline()
    {
        lock (gate) return combatDetailOpen ? CombatMapper.TimelineView(timelinePage, timelineProblem) : CombatTimelineView.Pending;
    }

    /// <summary>范围对应的单个战斗记录：当前分段或所选的测量；整场跨多个分段，返回空并给出原因。</summary>
    internal static (string? EncounterId, double DurationMs, string? Problem) TimelineSource(CombatWorkspace? workspace, string scope,
        CombatRangeSummary? range)
    {
        if (scope == "segment")
            return workspace?.Encounter is { } encounter ? (encounter.Id, encounter.Summary.DurationMs, null) : (null, 0, "no-record");
        if (scope.StartsWith("measurement:", StringComparison.Ordinal))
            return (scope["measurement:".Length..], range?.Result.Summary.DurationMs ?? 60000, null);
        return (null, 0, scope.StartsWith("measurement", StringComparison.Ordinal) ? "no-record" : "observation-scope");
    }

    private async Task RefreshTimelineAsync(MabiToolClient api, CombatWorkspace? workspace, CombatRangeSummary? range, CombatSelectionSnapshot chosen,
        CancellationToken token)
    {
        var (scope, target) = (chosen.Scope, chosen.Target);
        bool open;
        lock (gate) open = combatDetailOpen;
        if (!open) return;
        var (id, duration, problem) = TimelineSource(workspace, scope, range);
        if (id is null)
        {
            lock (gate) { timelinePage = null; timelineProblem = problem; }
            return;
        }
        var revision = scope == "segment" ? workspace?.Encounter?.Revision.ToString() : range?.Result.Revision.ToString();
        var key = string.Join('|', id, target, revision);
        lock (gate)
            if (timelineRead is { } last && (last.Key == key || DateTimeOffset.UtcNow - last.At < TimelineInterval)) return;
        try
        {
            var page = await api.QueryCombatTimelineAsync(id, new(PageSize: 1, BucketWidthMs: CombatMapper.BucketWidth(duration), EntityId: target), token);
            lock (gate) { timelinePage = page; timelineProblem = null; timelineRead = (key, DateTimeOffset.UtcNow); }
        }
        catch (MabiToolApiException error)
        {
            lock (gate) { timelinePage = null; timelineProblem = error.Problem?.Code ?? $"http.{error.Status}"; timelineRead = (key, DateTimeOffset.UtcNow); }
        }
    }
}
