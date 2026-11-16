using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.ModuleSdk;

namespace MabiTool.ObservationDebugTool;

/// <summary>报告写入 artifacts/reports，文件名沿用“前缀-本地时间戳”，同秒冲突时追加序号且绝不覆盖。</summary>
public sealed class ReportDirectory(string directory, TimeProvider clock) : IReportSink
{
    public string Directory { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    public string Write(string prefix, JsonObject report)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("report.prefixInvalid");
        System.IO.Directory.CreateDirectory(Directory);
        LocalDiscoverySecurity.RejectLinks(Directory);
        var stamp = clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var name = prefix + "-" + stamp + (attempt == 1 ? "" : "-" + attempt.ToString(CultureInfo.InvariantCulture)) + ".json";
            var path = Path.Combine(Directory, name);
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes); file.Flush(true);
                return path;
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("report.nameExhausted");
    }
}

/// <summary>运行期负载：系统 CPU、内存负载，以及本工具进程和登记的 Worker/子进程 CPU。</summary>
public sealed class LoadSampler(TimeProvider clock, Func<IReadOnlyCollection<int>> processes) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<int, (Process Process, TimeSpan First, TimeSpan Last)> children = [];
    private Task loop = Task.CompletedTask;
    private long started, ended;
    private TimeSpan hostFirst, hostLast;
    private int samples;
    private double systemCpuSum, systemCpuMax, memoryLoadMax;
    private (ulong Idle, ulong Kernel, ulong User)? lastSystem;

    public void Start()
    {
        started = clock.GetTimestamp();
        using (var self = Process.GetCurrentProcess()) hostFirst = hostLast = self.TotalProcessorTime;
        loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Sample();
                try { await Task.Delay(TimeSpan.FromSeconds(1), clock, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Stop()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        try { loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        Sample(); ended = clock.GetTimestamp();
    }

    private void Sample()
    {
        lock (gate)
        {
            using (var self = Process.GetCurrentProcess()) hostLast = self.TotalProcessorTime;
            foreach (var id in processes())
            {
                if (children.ContainsKey(id)) continue;
                try { var process = Process.GetProcessById(id); var cpu = process.TotalProcessorTime; children[id] = (process, cpu, cpu); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            foreach (var id in children.Keys.ToArray())
            {
                var value = children[id];
                try { value.Process.Refresh(); if (!value.Process.HasExited) children[id] = value with { Last = value.Process.TotalProcessorTime }; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            if (!OperatingSystem.IsWindows()) return;
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var now = (Idle: idle, Kernel: kernel, User: user);
                if (lastSystem is { } previous)
                {
                    var total = (now.Kernel - previous.Kernel) + (now.User - previous.User);
                    var busy = total - (now.Idle - previous.Idle);
                    if (total > 0) { var percent = 100d * busy / total; systemCpuSum += percent; systemCpuMax = Math.Max(systemCpuMax, percent); samples++; }
                }
                lastSystem = now;
            }
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory)) memoryLoadMax = Math.Max(memoryLoadMax, memory.MemoryLoad);
        }
    }

    public object Summary()
    {
        lock (gate)
        {
            var seconds = Math.Max(0.001, ((ended == 0 ? clock.GetTimestamp() : ended) - started) / (double)clock.TimestampFrequency);
            double Percent(TimeSpan cpu) => 100d * cpu.TotalSeconds / seconds / Environment.ProcessorCount;
            var child = children.Values.Aggregate(TimeSpan.Zero, (sum, value) => sum + (value.Last - value.First));
            return new
            {
                logicalProcessors = Environment.ProcessorCount, sampleSeconds = seconds, systemSamples = samples,
                systemCpuPercentAverage = samples == 0 ? (double?)null : systemCpuSum / samples,
                systemCpuPercentMaximum = samples == 0 ? (double?)null : systemCpuMax,
                memoryLoadPercentMaximum = memoryLoadMax,
                toolProcessCpuPercent = Percent(hostLast - hostFirst),
                childProcessesObserved = children.Count, childProcessCpuPercent = Percent(child),
                processTreeCpuPercent = Percent(hostLast - hostFirst + child),
            };
        }
    }

    public void Dispose()
    {
        Stop();
        lock (gate) foreach (var value in children.Values) value.Process.Dispose();
        stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}

/// <summary>Host、Worker 与进程树的内存峰值（每 250 ms 外部采样的最大值，含布局准备窗口），沿用战斗探针的报告字段。</summary>
public sealed class MemoryPeaks
{
    public long HostWorkingBytes { get; private set; }
    public long HostPrivateBytes { get; private set; }
    public long WorkerWorkingBytes { get; private set; }
    public long WorkerPrivateBytes { get; private set; }
    public long ProcessTreeWorkingBytes { get; private set; }
    public long ProcessTreePrivateBytes { get; private set; }
    /// <summary>最后一次采样值；Worker 取其退出前最后一次采样，与上方最大值分开记录。</summary>
    public long LastHostPrivateBytes { get; private set; }
    public long LastWorkerPrivateBytes { get; private set; }
    public long LastWorkerWorkingBytes { get; private set; }
    public void Sample(IEnumerable<Process> workers)
    {
        using var host = Process.GetCurrentProcess(); host.Refresh();
        long workerWorking = 0, workerPrivate = 0;
        foreach (var worker in workers)
            try { worker.Refresh(); if (!worker.HasExited) { workerWorking += worker.WorkingSet64; workerPrivate += worker.PrivateMemorySize64; } }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        LastHostPrivateBytes = host.PrivateMemorySize64;
        if (workerPrivate > 0) { LastWorkerPrivateBytes = workerPrivate; LastWorkerWorkingBytes = workerWorking; }
        HostWorkingBytes = Math.Max(HostWorkingBytes, host.WorkingSet64); HostPrivateBytes = Math.Max(HostPrivateBytes, host.PrivateMemorySize64);
        WorkerWorkingBytes = Math.Max(WorkerWorkingBytes, workerWorking); WorkerPrivateBytes = Math.Max(WorkerPrivateBytes, workerPrivate);
        ProcessTreeWorkingBytes = Math.Max(ProcessTreeWorkingBytes, host.WorkingSet64 + workerWorking);
        ProcessTreePrivateBytes = Math.Max(ProcessTreePrivateBytes, host.PrivateMemorySize64 + workerPrivate);
    }
}

public static class JsonNodes
{
    public static JsonObject From(object value) =>
        JsonSerializer.SerializeToNode(value) as JsonObject ?? throw new InvalidOperationException("report.notObject");
}
