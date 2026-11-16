using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

/// <summary>
/// 按目标划分的破防区间（2026-11-15 六份台服实采核对）：本机命中带 BreakOccurred（“破防”）时开始，
/// 该目标的破防值（170204）归零时结束；之前没有开始事件的“无防备”命中以该命中为估计起点，区间结束前一直没有归零时以最后一次无防备命中为估计终点。
/// 破防值在同一时刻已归零（击杀的一击造成破防）时记为零时长区间，其后 1 秒内的无防备命中延长该区间。时点为相对记录开始的毫秒。
/// </summary>
internal sealed class BreakIntervals
{
    public sealed record Interval(string Target, double Start, double End, bool EstimatedStart, bool EstimatedEnd);
    public const int MaximumIntervals = 1024;
    /// <summary>破防值归零与造成破防的命中视为同一时刻的容差（同一批到达的帧）。</summary>
    public const double SameInstantMs = 250;
    /// <summary>零时长区间之后仍算在该区间内的无防备命中（持续伤害等）的窗口。</summary>
    public const double TrailingMs = 1000;
    private readonly Dictionary<string, (double Start, double Last, bool EstimatedStart)> open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> lastZero = new(StringComparer.Ordinal);
    private readonly List<Interval> closed = [];
    public bool Truncated { get; private set; }

    public void Damage(string target, double at, bool? occurred, bool? breaked)
    {
        if (occurred != true && breaked != true || !double.IsFinite(at)) return;
        if (open.TryGetValue(target, out var current)) { open[target] = current with { Last = Math.Max(current.Last, at) }; return; }
        if (lastZero.TryGetValue(target, out var zero) && at >= zero)
        {
            var index = closed.FindLastIndex(item => item.Target == target);
            if (occurred == true && at - zero <= SameInstantMs) { Add(new(target, at, at, false, false)); return; }
            if (occurred != true && index >= 0 && closed[index].End >= zero - SameInstantMs && at - zero <= TrailingMs)
            { closed[index] = closed[index] with { End = Math.Max(closed[index].End, at) }; return; }
        }
        open[target] = (at, at, occurred != true);
    }

    public void GaugeZero(string target, double at)
    {
        if (!double.IsFinite(at)) return;
        lastZero[target] = at;
        if (!open.Remove(target, out var current)) return;
        Add(new(target, current.Start, Math.Max(at, current.Start), current.EstimatedStart, false));
    }

    private void Add(Interval value)
    {
        if (closed.Count >= MaximumIntervals) { closed.RemoveAt(0); Truncated = true; }
        closed.Add(value);
    }

    /// <summary>全部区间；进行中的区间以 <paramref name="openEnd"/> 截止（为空时取最后一次无防备命中并标为估计终点）。</summary>
    public IReadOnlyList<Interval> All(double? openEnd = null) =>
        [.. closed, .. open.Select(pair => new Interval(pair.Key, pair.Value.Start,
            openEnd is { } end ? Math.Max(pair.Value.Start, end) : pair.Value.Last, pair.Value.EstimatedStart, openEnd is null))];

    /// <summary>
    /// 破防期间汇总：持续时间为各区间的并集长度，区间次数为区间个数，时间占比为并集长度 ÷ 范围时长，平均 DPS 为无防备伤害 ÷ 并集长度。
    /// 无防备标记未知时只返回已知的累计项。
    /// </summary>
    public static CombatBreakSummary Summarize(IReadOnlyList<Interval> intervals, DamageAmountString? damage, UInt64String? count,
        double rangeMs, bool truncated = false)
    {
        var average = count is { Value: > 0 } hits && damage is { } amount ? new DamageAmountString(amount.Value / hits.Value) : (DamageAmountString?)null;
        if (damage is null || count is null) return new(damage, count, average, null, null, null, null, ["break-flags-unknown"]);
        var reasons = new List<string>();
        if (intervals.Any(item => item.EstimatedStart)) reasons.Add("break-start-estimated");
        if (intervals.Any(item => item.EstimatedEnd)) reasons.Add("break-end-estimated");
        if (truncated) reasons.Add("break-interval-capacity");
        double union = 0, coveredTo = double.NegativeInfinity;
        foreach (var item in intervals.OrderBy(item => item.Start))
        {
            var start = Math.Max(item.Start, coveredTo);
            if (item.End > start) union += item.End - start;
            coveredTo = Math.Max(coveredTo, item.End);
        }
        double? dps = union > 0 && (double)damage.Value.Value * 1000 / union is var rate && double.IsFinite(rate) ? rate : null;
        double? share = rangeMs > 0 ? Math.Clamp(union / rangeMs, 0, 1) : null;
        return new(damage, count, average, union, new((ulong)intervals.Count), dps, share, reasons);
    }

    /// <summary>按目标筛选并裁剪到 [<paramref name="from"/>, <paramref name="to"/>]，再平移 <paramref name="shift"/>。</summary>
    public static IEnumerable<Interval> Clip(IEnumerable<Interval> intervals, string? target, double from, double to, double shift) =>
        intervals.Where(item => (target is null || item.Target == target) && item.End >= from && item.Start <= to)
            .Select(item => item with { Start = Math.Max(from, item.Start) + shift, End = Math.Min(to, item.End) + shift });
}
