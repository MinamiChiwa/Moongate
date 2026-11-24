using System.Diagnostics;
using MabiTool.Client;
using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

// 战斗数据的显示延迟：最新捕获事件给出每次变化所含数据最新一条的抓包时点（观察单调时点，实时抓包时与本机单调时钟同一时间轴），
// 界面每次显示新的战斗数据时记下“显示时刻 − 抓包时点”，最近 256 次滚动统计中位数与 p95，战斗中至多每 30 秒报告给后端写入诊断。
public sealed partial class LiveBackend
{
    /// <summary>滚动统计的样本数。</summary>
    internal const int LatencyWindow = 256;
    /// <summary>向后端报告的最短间隔（后端另按每客户端 10 秒限流）。</summary>
    internal static readonly TimeSpan LatencyReportInterval = TimeSpan.FromSeconds(30);
    /// <summary>采信的样本上限：超出（时钟不在同一时间轴，例如回放来源）不计入。</summary>
    private const double MaximumLatencyMs = 10_000;

    /// <summary>最近收到的最新捕获通知（观察、工作区修订、抓包时点），至多 64 条。调用方持有 gate。</summary>
    private readonly Queue<(string ObservationId, ulong Revision, double? Captured)> liveMarks = new();
    /// <summary>已采用、等待界面显示的抓包时点。调用方持有 gate。</summary>
    private double? displayCaptured;
    private readonly double[] latencySamples = new double[LatencyWindow];
    private int latencyCount, latencyNext;
    private long latencyTotal, latencyReported;
    private long latencyReportedAt;
    private int latencyReporting;

    private static double MonotonicMs => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);

    /// <summary>记下一条最新捕获通知。</summary>
    private void RememberLive(string observationId, ulong revision, double? captured)
    {
        lock (gate)
        {
            if (liveMarks.Count >= 64) liveMarks.Dequeue();
            liveMarks.Enqueue((observationId, revision, captured));
        }
    }

    /// <summary>
    /// 调用方持有 gate。界面将要显示 <paramref name="workspace"/>：取修订不新于它的通知中最新的抓包时点，等界面显示后记为一个样本；
    /// 模拟来源的时点不在本机时钟上，不统计。
    /// </summary>
    private void AdoptLive(CombatWorkspace? workspace, bool simulation)
    {
        if (workspace is null) return;
        double? captured = null;
        while (liveMarks.TryPeek(out var mark) && (mark.ObservationId != workspace.Observation.Id || mark.Revision <= workspace.Revision.Value))
        {
            liveMarks.Dequeue();
            if (mark.ObservationId == workspace.Observation.Id && mark.Captured is { } value && (captured is null || value > captured)) captured = value;
        }
        if (!simulation && captured is not null) displayCaptured = captured;
    }

    /// <summary>测试用：每个显示延迟样本（毫秒）在记下后回调一次。</summary>
    internal Action<double>? LatencyObserved { get; init; }

    /// <summary>界面线程：按通知重读并显示之后记下显示延迟样本。</summary>
    private void RecordDisplayed()
    {
        double latency;
        lock (gate)
        {
            if (displayCaptured is not { } captured) return;
            displayCaptured = null;
            latency = MonotonicMs - captured;
            if (latency is < 0 or > MaximumLatencyMs) return;
            latencySamples[latencyNext] = latency;
            latencyNext = (latencyNext + 1) % LatencyWindow;
            latencyCount = Math.Min(LatencyWindow, latencyCount + 1);
            latencyTotal++;
        }
        LatencyObserved?.Invoke(latency);
    }

    /// <summary>最近窗口的显示延迟统计（样本数、累计样本数、中位数、p95、最大值）；没有样本时为空。</summary>
    internal CombatDisplayLatencyReport? DisplayLatency()
    {
        double[] values; long total;
        lock (gate)
        {
            if (latencyCount == 0) return null;
            values = latencySamples.AsSpan(0, latencyCount).ToArray(); total = latencyTotal;
        }
        Array.Sort(values);
        double At(double q) => values[Math.Min(values.Length - 1, (int)Math.Ceiling(q * values.Length) - 1)];
        return new(values.Length, (int)Math.Min(int.MaxValue, total), Math.Round(At(0.5), 1), Math.Round(At(0.95), 1), Math.Round(values[^1], 1));
    }

    /// <summary>
    /// 有新样本且距上次报告满 <see cref="LatencyReportInterval"/> 时向后端报告一次；在后台发出，失败（后端不支持、权限不足、连接中断）不影响刷新。
    /// </summary>
    private void ReportDisplayLatency(MabiToolClient api)
    {
        lock (gate)
        {
            if (latencyTotal == latencyReported || latencyReportedAt != 0 && !Elapsed(latencyReportedAt, LatencyReportInterval)) return;
            latencyReportedAt = Now; latencyReported = latencyTotal;
        }
        if (DisplayLatency() is not { } report || Interlocked.Exchange(ref latencyReporting, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await api.ReportCombatDisplayLatencyAsync(report, lifetime.Token); }
            catch (Exception error) when (error is MabiToolApiException or HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
            finally { Volatile.Write(ref latencyReporting, 0); }
        });
    }
}
