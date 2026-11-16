using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Color;

/// <summary>染色运行与计算 Worker 池状态；不租用或启动 Worker。</summary>
internal sealed class ColorDiagnosticProbe(ColorRunOperations runs, ColorWorkerPool workers) : IDiagnosticProbe
{
    public string Id => "color";
    public string Module => "color";
    public DiagnosticProbeReading Capture()
    {
        var (running, queued, accepting, configured) = runs.DiagnosticState();
        var (pool, starts, uses) = workers.DiagnosticState();
        var reading = new DiagnosticProbeReading(!accepting ? "stopped" : running ? "running" : "idle")
            .Flag("runner.configured", configured).Count("runs.queued", queued)
            .Value("pool.state", pool).Count("pool.starts", starts).Count("pool.uses", uses)
            .Count("pool.maximumParallelism", workers.Options?.MaximumParallelism ?? 0);
        if (workers.ProcessId is { } pid) reading.Count("pool.workerPid", pid);
        return reading;
    }
}
