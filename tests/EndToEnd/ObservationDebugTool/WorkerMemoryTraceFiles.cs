using System.Text.Json;

namespace MabiTool.ObservationDebugTool;

/// <summary>
/// 读取观察 Worker 的可选内存轨迹（workers/game-observation/WorkerMemoryTrace.cs）：布局准备与目录读取窗口内的样本单独汇总，
/// 其余样本为运行期；非托管估计取私有字节减 GC 提交量。
/// </summary>
public static class WorkerMemoryTraceFiles
{
    public const string Variable = "MABITOOL_OBSERVATION_MEMORY_TRACE";
    public const long RuntimeBudgetBytes = 160L * 1024 * 1024;

    public static IReadOnlyDictionary<string, string> Environment(string? directory) =>
        directory is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [Variable] = directory };

    public static WorkerMemoryTraceSummary? Summarize(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var files = Directory.EnumerateFiles(directory, "worker-memory-*.jsonl").Order(StringComparer.Ordinal).Select(SummarizeFile).ToArray();
        return files.Length == 0 ? null : new(files, RuntimeBudgetBytes, files.All(file => file.Runtime.PeakPrivateBytes is null or <= RuntimeBudgetBytes));
    }

    internal static WorkerMemoryFileSummary SummarizeFile(string path)
    {
        var samples = new List<Sample>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            for (var line = reader.ReadLine(); line is not null && samples.Count < 20_000; line = reader.ReadLine())
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    samples.Add(new(root.GetProperty("monotonicMs").GetDouble(),
                        root.GetProperty("marker").ValueKind == JsonValueKind.String ? root.GetProperty("marker").GetString() : null,
                        root.GetProperty("privateBytes").GetInt64(), root.GetProperty("managedHeapBytes").GetInt64(),
                        root.GetProperty("gcCommittedBytes").GetInt64()));
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
            }
        var preparation = new List<Sample>(); var catalog = new List<Sample>(); var runtime = new List<Sample>();
        var windows = new List<object>();
        string? open = null; double openedAt = 0;
        foreach (var sample in samples)
        {
            if (sample.Marker is "layout.prepareStarted" or "catalog.started") { open = sample.Marker; openedAt = sample.MonotonicMs; }
            (open == "layout.prepareStarted" ? preparation : open == "catalog.started" ? catalog : runtime).Add(sample);
            if (sample.Marker is "layout.prepareFinished" or "catalog.finished" && open is not null)
            { windows.Add(new { kind = open == "catalog.started" ? "catalog" : "layout", startedMs = openedAt, finishedMs = sample.MonotonicMs }); open = null; }
        }
        var first = samples.Count == 0 ? (double?)null : samples[0].MonotonicMs;
        var last = samples.LastOrDefault();
        return new(Path.GetFileName(path), samples.Count, first, windows, Peak(preparation), Peak(catalog), Peak(runtime),
            last is null ? null : new(last.PrivateBytes, last.ManagedHeapBytes, last.GcCommittedBytes, Math.Max(0, last.PrivateBytes - last.GcCommittedBytes)));
    }

    private static WorkerMemoryPeak Peak(IReadOnlyList<Sample> values) => values.Count == 0 ? new(0, null, null, null, null)
        : new(values.Count, values.Max(value => value.PrivateBytes), values.Max(value => value.ManagedHeapBytes),
            values.Max(value => value.GcCommittedBytes), values.Max(value => Math.Max(0, value.PrivateBytes - value.GcCommittedBytes)));

    private sealed record Sample(double MonotonicMs, string? Marker, long PrivateBytes, long ManagedHeapBytes, long GcCommittedBytes);
}

/// <param name="UnmanagedEstimateBytes">私有字节减 GC 提交量：运行时自身、原生库（Npcap、Brotli）与线程栈等。</param>
public sealed record WorkerMemoryPeak(int Samples, long? PeakPrivateBytes, long? PeakManagedHeapBytes, long? PeakGcCommittedBytes, long? PeakUnmanagedEstimateBytes);
public sealed record WorkerMemoryValue(long PrivateBytes, long ManagedHeapBytes, long GcCommittedBytes, long UnmanagedEstimateBytes);
public sealed record WorkerMemoryFileSummary(string File, int Samples, double? FirstMonotonicMs, IReadOnlyList<object> Windows,
    WorkerMemoryPeak Preparation, WorkerMemoryPeak Catalog, WorkerMemoryPeak Runtime, WorkerMemoryValue? End);
public sealed record WorkerMemoryTraceSummary(IReadOnlyList<WorkerMemoryFileSummary> Workers, long RuntimeBudgetBytes, bool RuntimeWithinBudget);
