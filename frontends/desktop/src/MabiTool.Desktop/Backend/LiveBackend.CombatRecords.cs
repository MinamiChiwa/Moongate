using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Combat;

namespace MabiTool.Desktop.Backend;

// 记录明细窗口：每个打开的记录各有自己的目标筛选，按记录 ID 读取范围汇总（scope=encounter）与时间线分桶，
// 与查看定时测量结果走同一接口。记录已结束，打开时读一次、换目标时重读；读取失败于连接问题时由例行刷新补读。
// 结果到达只发 CombatRecordChanged，主页面、实时明细窗口与浮窗不受影响。
public sealed partial class LiveBackend
{
    private sealed class RecordDetail
    {
        public string? Target;
        public CombatEncounter? Encounter;
        public CombatRangeSummary? Range;
        public string? Problem;
        public CombatTimelinePage? Timeline;
        public string? TimelineProblem;
        public bool Loaded, Loading;
        public int Version;
    }

    private readonly Dictionary<string, RecordDetail> recordDetails = new(StringComparer.Ordinal);

    public event EventHandler? CombatRecordChanged;

    public void SetCombatRecordOpen(string id, bool open)
    {
        lock (gate)
        {
            if (!open) { recordDetails.Remove(id); return; }
            recordDetails.TryAdd(id, new RecordDetail());
        }
        _ = LoadRecordAsync(id);
    }

    public CombatPageData GetCombatRecord(string id)
    {
        var (current, _) = Read();
        CombatRecordState value;
        lock (gate)
        {
            recordDetails.TryGetValue(id, out var view);
            var stored = view?.Encounter ?? current.History.FirstOrDefault(item => item.Id == id);
            value = new(current.Connected, id, stored, view?.Target, view?.Range, view?.Problem, view?.Loaded == true);
        }
        return CombatMapper.MapRecord(value);
    }

    public CombatTimelineView? GetCombatRecordTimeline(string id)
    {
        lock (gate)
            return recordDetails.TryGetValue(id, out var view) && view.Loaded
                ? CombatMapper.TimelineView(view.Timeline, view.TimelineProblem ?? view.Problem)
                : CombatTimelineView.Pending;
    }

    /// <summary>窗口里的目标筛选：参数为“记录 ID|目标 ID”（目标为空表示全部），立即通知窗口移动选中标记，再按新目标重读。</summary>
    private Task SelectRecordTargetAsync(string? argument)
    {
        var split = argument?.IndexOf('|') ?? -1;
        if (split <= 0) return Task.CompletedTask;
        var id = argument![..split];
        var target = argument[(split + 1)..] is { Length: > 0 } chosen ? chosen : null;
        lock (gate)
        {
            if (!recordDetails.TryGetValue(id, out var view) || view.Target == target) return Task.CompletedTask;
            view.Target = target;
        }
        NotifyRecord();
        return LoadRecordAsync(id);
    }

    private void NotifyRecord() => dispatch(() => CombatRecordChanged?.Invoke(this, EventArgs.Empty));

    /// <summary>例行刷新：补读打开后还没读到的记录（例如打开时未连接或请求中断）。</summary>
    private async Task RefreshRecordsAsync()
    {
        string[] pending;
        lock (gate) pending = recordDetails.Where(item => !item.Value.Loaded && !item.Value.Loading).Select(item => item.Key).ToArray();
        foreach (var id in pending) await LoadRecordAsync(id);
    }

    private async Task LoadRecordAsync(string id)
    {
        if (client is not { } api) return;
        var (current, _) = Read();
        if (!current.Connected) return;
        string? target; int version;
        lock (gate)
        {
            if (!recordDetails.TryGetValue(id, out var view)) return;
            target = view.Target; version = ++view.Version; view.Loading = true;
        }
        var token = lifetime.Token;
        var encounter = current.History.FirstOrDefault(item => item.Id == id);
        CombatRangeSummary? range = null; CombatTimelinePage? page = null;
        string? problem = null, timelineProblem = null;
        var dropTarget = false; var done = true;
        try
        {
            encounter ??= await api.GetCombatEncounterAsync(id, token);
            if (!CombatMapper.SummaryOnly(encounter))
            {
                try { range = await api.QueryCombatRangeAsync(new("encounter", id, TargetId: target), token); }
                // 所选目标不在这条记录里：回到“全部”再读一次。
                catch (MabiToolApiException error) when (error.Problem?.Code == "combat.entityOutOfScope" && target is not null) { dropTarget = true; }
                catch (MabiToolApiException error) { problem = error.Problem?.Code ?? $"http.{error.Status}"; }
                if (range is not null)
                    try
                    {
                        page = await api.QueryCombatTimelineAsync(id, new(PageSize: 1,
                            BucketWidthMs: CombatMapper.BucketWidth(range.Result.Summary.DurationMs), EntityId: target), token);
                    }
                    catch (MabiToolApiException error) { timelineProblem = error.Problem?.Code ?? $"http.{error.Status}"; }
            }
        }
        catch (MabiToolApiException error) { problem = error.Problem?.Code ?? $"http.{error.Status}"; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException) { done = false; }
        lock (gate)
        {
            if (!recordDetails.TryGetValue(id, out var view) || view.Version != version) return;
            view.Loading = false;
            if (!done) return;
            if (dropTarget) view.Target = null;
            else
            {
                view.Encounter = encounter; view.Range = range; view.Problem = problem;
                view.Timeline = page; view.TimelineProblem = timelineProblem; view.Loaded = true;
            }
        }
        NotifyRecord();
        if (dropTarget) await LoadRecordAsync(id);
    }
}
