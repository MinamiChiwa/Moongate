using System.Diagnostics;
using Avalonia.Threading;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>
/// 效果条目的顺序与倒计时总时长，主页面、明细窗口与浮窗共用一份。按“分组（自身／目标）+ 实体范围 + 效果编号”记住首次出现的顺序：
/// 收藏的在前，组内按首次出现先后；行出现后不再移动，这一份数据里不再出现的效果被忘记，再出现时排到末尾。
/// 总时长取首次观测到的剩余时间；按本地时钟推算的剩余比新观测值小 1 秒以上（续期）时以新值重置。
/// </summary>
public static class CombatEffectOrder
{
    /// <summary>判定续期的容差：新观测值比推算值大出这么多才重置总时长。</summary>
    public const double RenewalToleranceMs = 1000;
    private sealed class Entry(long order) { public long Order = order; public double? Total, Remaining; public long At; }
    private static readonly object gate = new();
    private static readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private static long sequence;

    /// <summary>测试用：清空记住的顺序。</summary>
    internal static void Reset() { lock (gate) { entries.Clear(); sequence = 0; } }

    public static IReadOnlyList<EffectRow> Arrange(IReadOnlyList<EffectRow> rows, string group)
    {
        lock (gate)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<EffectRow>(rows.Count);
            foreach (var row in rows)
            {
                var key = $"{group}|{row.Scope}|{row.EffectId ?? row.Name}";
                if (!present.Add(key)) continue;
                if (!entries.TryGetValue(key, out var entry))
                {
                    entries[key] = entry = new(++sequence) { Total = row.RemainingMs, Remaining = row.RemainingMs, At = row.ObservedAt };
                }
                else if (row.RemainingMs is { } remaining)
                {
                    var projected = entry.Remaining is { } previous ? previous - Elapsed(entry.At, row.ObservedAt) : (double?)null;
                    if (entry.Total is null || projected is null || remaining > projected.Value + RenewalToleranceMs) entry.Total = remaining;
                    entry.Remaining = remaining; entry.At = row.ObservedAt;
                }
                else { entry.Remaining = null; entry.At = row.ObservedAt; }
                result.Add(row with { Order = entry.Order, TotalMs = entry.Total, Favorite = CombatEffectFavorites.IsFavorite(row.EffectId) });
            }
            var prefix = group + "|";
            foreach (var key in entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && !present.Contains(key)).ToList())
                entries.Remove(key);
            return result.OrderByDescending(row => row.Favorite).ThenBy(row => row.Order).ToList();
        }
    }

    private static double Elapsed(long from, long to) => from == 0 || to <= from ? 0 : (to - from) * 1000d / Stopwatch.Frequency;

    /// <summary>按本地时钟推算的剩余时间；常驻或未知时为空。</summary>
    public static double? Remaining(EffectRow row, long now) =>
        row.Infinite || row.RemainingMs is not { } ms ? null : ms - Elapsed(row.ObservedAt, now);

    /// <summary>倒计时进度（剩余 ÷ 总时长，0–1）；常驻或未知时为空。</summary>
    public static double? Progress(EffectRow row, long now) =>
        Remaining(row, now) is { } remaining && row.TotalMs is { } total and > 0 ? Math.Clamp(remaining / total, 0, 1) : null;

    /// <summary>推算已归零、应从列表移除的效果。</summary>
    public static bool Expired(EffectRow row, long now) => Remaining(row, now) is <= 0;
}

/// <summary>效果倒计时的共用节拍（200 ms）：有订阅时运行，挂在界面上的条目只重绘自己，页面模型移除推算归零的行。</summary>
public static class EffectTicker
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    private static DispatcherTimer? timer;
    private static EventHandler? tick;

    public static event EventHandler? Tick
    {
        add
        {
            tick += value;
            if (timer is null) { timer = new DispatcherTimer { Interval = Interval }; timer.Tick += (_, _) => tick?.Invoke(null, EventArgs.Empty); }
            if (!timer.IsEnabled) timer.Start();
        }
        remove
        {
            tick -= value;
            if (tick is null) timer?.Stop();
        }
    }
}
