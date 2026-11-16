using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Diagnostics;

/// <summary>宿主版本、运行时长、内存与事件总线订阅；只读取进程计数器与内存状态。</summary>
internal sealed class HostDiagnosticProbe(SystemInfo info, StateEventHub events) : IDiagnosticProbe
{
    public string Id => "host";
    public string Module => "core";
    public DiagnosticProbeReading Capture()
    {
        // StartedAt 由系统时钟记录，运行时长按同一时钟计算。
        var uptime = DateTimeOffset.UtcNow - info.StartedAt;
        return new DiagnosticProbeReading("running")
            .Value("version", info.Version).Value("apiVersion", info.ApiVersion).Value("runtime", Environment.Version.ToString())
            .Count("uptimeSeconds", (long)Math.Max(0, uptime.TotalSeconds))
            .Count("workingSetBytes", Environment.WorkingSet).Count("managedHeapBytes", GC.GetTotalMemory(false))
            .Count("gc.gen0", GC.CollectionCount(0)).Count("gc.gen1", GC.CollectionCount(1)).Count("gc.gen2", GC.CollectionCount(2))
            .Count("threads", ThreadPool.ThreadCount)
            .Count("events.subscriptions", events.SubscriptionCount).Count("events.resyncedSubscriptions", events.ResyncedSubscriptions);
    }
}

/// <summary>诊断记录器自身的丢弃、排队与最后错误；日志字节取最近一次写入后的内存值。</summary>
internal sealed class DiagnosticsDiagnosticProbe(DiagnosticRecorder recorder, DiagnosticStore store) : IDiagnosticProbe
{
    public string Id => "diagnostics";
    public string Module => "core";
    public DiagnosticProbeReading Capture()
    {
        var lastError = recorder.LastError ?? store.Logs.LastError;
        var reading = new DiagnosticProbeReading(lastError is null ? "recording" : "degraded", lastError)
            .Count("droppedRecords", recorder.DroppedRecords + store.Logs.DroppedRecords)
            .Count("recorder.droppedRecords", recorder.DroppedRecords).Count("log.droppedRecords", store.Logs.DroppedRecords)
            .Count("queuedRecords", recorder.QueuedRecords).Value("lastError", lastError);
        if (store.Logs.LastKnownBytes is { } bytes) reading.Count("log.bytes", bytes);
        return reading;
    }
}
