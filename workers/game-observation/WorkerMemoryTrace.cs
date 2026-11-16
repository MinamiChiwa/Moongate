using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 可选内存轨迹：仅当启动环境给出 MABITOOL_OBSERVATION_MEMORY_TRACE（已存在的绝对目录）时启用，写入该目录下 worker-memory-&lt;pid&gt;.jsonl，供实机调试工具
/// 区分布局准备的瞬时峰值与运行期占用。每 250 ms 写一行私有字节、托管堆与 GC 提交量，标记行记录布局准备的起止；
/// 只写数字与固定标记，最多 14,400 行（1 小时）。未设置时不创建线程或文件。
/// </summary>
internal static class WorkerMemoryTrace
{
    internal const string Variable = "MABITOOL_OBSERVATION_MEMORY_TRACE";
    internal const int MaximumLines = 14_400;
    private static readonly object gate = new();
    private static StreamWriter? writer;
    private static int lines;

    internal static void StartFromEnvironment()
    {
        var directory = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, null);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return;
        try
        {
            directory = Path.GetFullPath(directory);
            if (!Directory.Exists(directory)) return;
            MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(directory);
            var path = Path.Combine(directory, FileName(Environment.ProcessId));
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            lock (gate) writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { return; }
        var thread = new Thread(() =>
        {
            while (Active) { Mark(null); Thread.Sleep(250); }
        }) { IsBackground = true, Name = "memory-trace" };
        thread.Start();
    }

    internal static string FileName(int processId) => "worker-memory-" + processId.ToString(CultureInfo.InvariantCulture) + ".jsonl";
    private static bool Active { get { lock (gate) return writer is not null; } }

    /// <summary>写一行样本；marker 为 null 时是周期样本，否则为固定标记（如 layout.prepareStarted）。</summary>
    internal static void Mark(string? marker)
    {
        lock (gate)
        {
            if (writer is null) return;
            if (lines >= MaximumLines) { writer.Dispose(); writer = null; return; }
            var info = GC.GetGCMemoryInfo();
            using var self = Process.GetCurrentProcess();
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{{\"monotonicMs\":{Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency):F1},\"marker\":{(marker is null ? "null" : "\"" + marker + "\"")}," +
                $"\"privateBytes\":{self.PrivateMemorySize64},\"workingBytes\":{self.WorkingSet64},\"managedHeapBytes\":{info.HeapSizeBytes}," +
                $"\"gcCommittedBytes\":{info.TotalCommittedBytes},\"allocatedBytes\":{GC.GetTotalAllocatedBytes()},\"gen2Collections\":{GC.CollectionCount(2)}}}");
            try { writer.WriteLine(line); lines++; }
            catch (IOException) { writer.Dispose(); writer = null; }
        }
    }
}
