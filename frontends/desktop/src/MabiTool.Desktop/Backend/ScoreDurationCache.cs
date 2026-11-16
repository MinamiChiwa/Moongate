namespace MabiTool.Desktop.Backend;

/// <summary>
/// 乐谱时长的按需查询与缓存。页面只登记当前显示的乐谱（乐谱库页的一页、迷你窗的一页）；每一轮最多取出 perPass 张（默认 8）交给后端查询，
/// 两轮之间至少隔 interval（默认 1 秒），所以翻页或搜索不会对整个乐谱库造成突发请求。
/// 查询结果（包括查不到的“未知”）只查一次，在本次运行内缓存。
/// </summary>
public sealed class ScoreDurationCache(int perPass = 8, TimeSpan? interval = null, Func<DateTimeOffset>? clock = null)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, double?> known = new(StringComparer.Ordinal);
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    private readonly TimeSpan interval = interval ?? TimeSpan.FromSeconds(1);
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private List<string> wanted = [];
    private DateTimeOffset lastPass = DateTimeOffset.MinValue;

    /// <summary>
    /// 登记一组乐谱（某个页面当前显示的那些），排在待查询的最前面。待查询最多保留 <see cref="Capacity"/> 张，
    /// 早先翻过去的页面被挤掉后不再查询。返回真表示这一组里有尚未查过的乐谱，调用方应唤醒查询。
    /// </summary>
    public bool Want(IEnumerable<string> scoreIds)
    {
        lock (gate)
        {
            var fresh = scoreIds.Distinct(StringComparer.Ordinal).Where(id => !known.ContainsKey(id)).ToList();
            wanted = fresh.Concat(wanted).Distinct(StringComparer.Ordinal).Take(Capacity).ToList();
            return fresh.Count > 0;
        }
    }

    /// <summary>待查询的上限：够两个窗口各一页。</summary>
    public const int Capacity = 64;

    /// <summary>已查过时返回真；<paramref name="milliseconds"/> 为空表示后端也不知道时长。</summary>
    public bool TryGet(string scoreId, out double? milliseconds)
    {
        lock (gate) return known.TryGetValue(scoreId, out milliseconds);
    }

    public bool Contains(string scoreId)
    {
        lock (gate) return known.ContainsKey(scoreId) || inFlight.Contains(scoreId);
    }

    /// <summary>取出本轮要查询的乐谱；离上一轮不到间隔时返回空。</summary>
    public IReadOnlyList<string> NextBatch()
    {
        lock (gate)
        {
            var now = clock();
            if (now - lastPass < interval) return [];
            var batch = wanted.Where(id => !known.ContainsKey(id) && !inFlight.Contains(id)).Take(perPass).ToList();
            if (batch.Count == 0) return batch;
            lastPass = now;
            foreach (var id in batch) inFlight.Add(id);
            wanted.RemoveAll(batch.Contains);
            return batch;
        }
    }

    /// <summary>登记一次查询（含查不到的）结果。</summary>
    public void Record(string scoreId, double? milliseconds)
    {
        lock (gate)
        {
            inFlight.Remove(scoreId);
            known[scoreId] = milliseconds;
        }
    }
}
