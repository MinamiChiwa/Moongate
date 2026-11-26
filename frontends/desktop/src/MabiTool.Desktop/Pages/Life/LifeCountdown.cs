using MabiTool.Contracts;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>一个设施的串行加工进度：加工中与排队未开始的项数、加工中一项的剩余、全部完成还需（秒）；读不到时为空。</summary>
public readonly record struct LifeFacilityTimeline(int Working, int Queued, double? Current, double? Total);

/// <summary>
/// 加工队列的本地倒计时与设施内的串行累计。同一设施里的加工按队列顺序逐项进行：
/// 只有加工中（既非已完成也非排队未开始 <c>NotStarted</c>）的项目倒计时，排队项目给出的剩余秒数是它自己的加工时长，不随时间减少。
/// 加工中项目的截止时刻 = 宿主读取加工队列的时刻（快照 <c>Works.ReadAt</c>）+ 读取时的剩余秒数，剩余 = 截止时刻 − 现在；
/// 同一次读取无论宿主重复返回多少次、页面重建多少次，截止时刻都相同，倒计时不会回到读取时的数值。
/// 新读取算出的截止时刻比原来晚不超过 <see cref="Tolerance"/> 秒时保留原截止时刻（读取往返与取整造成的抖动，避免数字倒退）；
/// 快照没有读取时刻时退回按收到快照的时刻起算，宿主重复返回同一剩余秒数时保留原截止时刻。
/// 没有给出剩余时间（未知）的项目保持未知，不当作 0。同一设施里同名的加工按出现顺序区分。
/// </summary>
public sealed class LifeCountdown
{
    /// <summary>新读取的截止时刻比原来晚不超过这个秒数时保留原截止时刻。</summary>
    public const double Tolerance = 3;

    // 两次服务端值相差小于这个秒数视为同一次读取的结果（只在没有读取时刻时使用）。
    private const double SameServerValue = 0.001;

    private readonly record struct Entry(double Deadline, double Server, bool FromRead);

    // 一个设施里尚未完成的项目，按队列顺序；排队项目的 Duration 是它自己的加工时长。
    private readonly record struct Pending(string Key, bool Working, double? Duration);

    private readonly Func<DateTimeOffset> now;
    private Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private Dictionary<string, List<Pending>> facilities = new(StringComparer.Ordinal);

    /// <param name="now">当前时刻（UTC）；测试注入替身。</param>
    public LifeCountdown(Func<DateTimeOffset>? now = null) => this.now = now ?? (() => DateTimeOffset.UtcNow);

    public static string Key(string facility, string name, int ordinal) => facility + "\u0001" + name + "\u0001" + ordinal;

    private static double Seconds(DateTimeOffset time) => (time - DateTimeOffset.UnixEpoch).TotalSeconds;

    /// <summary>用一次快照里的加工队列与它的读取时刻更新倒计时；队列里已不存在的项目被清除。</summary>
    public void Update(IReadOnlyList<LifeAlterWork> works, DateTimeOffset? readAt = null)
    {
        var start = Seconds(readAt ?? now());
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var lines = new Dictionary<string, List<Pending>>(StringComparer.Ordinal);
        foreach (var work in works)
        {
            var prefix = work.Facility + "\u0001" + work.Name;
            var ordinal = seen.TryGetValue(prefix, out var count) ? count : 0;
            seen[prefix] = ordinal + 1;
            if (work.Completed == true) continue;
            var key = Key(work.Facility, work.Name, ordinal);
            if (!lines.TryGetValue(work.Facility, out var line)) lines[work.Facility] = line = [];
            if (work.State == "NotStarted")
            {
                line.Add(new(key, false, work.RemainingSeconds is { } duration ? Math.Max(0, duration) : null));
                continue;
            }
            line.Add(new(key, true, null));
            if (work.RemainingSeconds is not { } server) continue;
            var deadline = start + Math.Max(0, server);
            if (entries.TryGetValue(key, out var previous))
            {
                var repeated = readAt is null && !previous.FromRead && Math.Abs(server - previous.Server) < SameServerValue;
                var later = deadline - previous.Deadline;
                if (repeated || later > 0 && later <= Tolerance) deadline = previous.Deadline;
            }
            next[key] = new(deadline, server, readAt is not null);
        }
        entries = next;
        facilities = lines;
    }

    private double? Left(string key, double at) => entries.TryGetValue(key, out var entry) ? Math.Max(0, entry.Deadline - at) : null;

    private static double? Add(double? a, double? b) => a is { } x && b is { } y ? x + y : null;

    /// <summary>
    /// 项目完成还需的秒数（不小于 0）：加工中的项目是它的剩余时间；排队未开始的项目是加工中项目的剩余加上它及其前面排队项目的时长。
    /// 已完成、不在队列里或所需时间有未知时为空。
    /// </summary>
    public double? Remaining(string facility, string name, int ordinal)
    {
        if (!facilities.TryGetValue(facility, out var line)) return null;
        var key = Key(facility, name, ordinal);
        var at = Seconds(now());
        double? sum = 0;
        foreach (var item in line.Where(x => x.Working))
        {
            if (item.Key == key) return Left(key, at);
            sum = Add(sum, Left(item.Key, at));
        }
        foreach (var item in line.Where(x => !x.Working))
        {
            sum = Add(sum, item.Duration);
            if (item.Key == key) return sum;
        }
        return null;
    }

    /// <summary>设施的串行进度：加工中一项的剩余（没有加工中的项目时为空），全部完成还需（加工中的剩余加全部排队项目的时长）。</summary>
    public LifeFacilityTimeline Facility(string facility)
    {
        if (!facilities.TryGetValue(facility, out var line)) return new(0, 0, null, null);
        var at = Seconds(now());
        var working = line.Where(x => x.Working).ToList();
        double? current = 0;
        foreach (var item in working) current = Add(current, Left(item.Key, at));
        var total = current;
        foreach (var item in line.Where(x => !x.Working)) total = Add(total, item.Duration);
        return new(working.Count, line.Count - working.Count, working.Count > 0 ? current : null, total);
    }

    /// <summary>有加工中的项目本地剩余时间已到 0（排队项目不会到点）。</summary>
    public bool AnyDue
    {
        get
        {
            var at = Seconds(now());
            return entries.Values.Any(entry => entry.Deadline <= at);
        }
    }
}
