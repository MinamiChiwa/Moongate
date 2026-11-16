using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Diagnostics;

/// <summary>汇总各组件探针；平台只做白名单校验、故障隔离和条目截断，不读取组件私有状态。</summary>
public sealed class DiagnosticRuntimeService(IEnumerable<IDiagnosticProbe> probes, SystemInfo info, TimeProvider clock)
{
    public const int MaximumProbes = 64;
    public const int MaximumCounters = 128;
    public const int MaximumValues = 32;
    private const string Unavailable = "unavailable";
    private const string ProbeFailure = ApiErrors.UnexpectedError;
    private readonly IReadOnlyList<IDiagnosticProbe> registered = probes.ToArray();

    public DiagnosticRuntimeSnapshot Capture()
    {
        var results = new List<DiagnosticProbeSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int truncated = 0, rejected = 0;
        foreach (var probe in registered)
        {
            string id, module;
            try { id = probe.Id; module = probe.Module; }
            catch (Exception) { rejected++; continue; }
            if (!DiagnosticSanitizer.IsProbeId(id) || !DiagnosticSanitizer.IsProbeModule(module) || !seen.Add(id)) { rejected++; continue; }
            if (results.Count >= MaximumProbes) { truncated++; continue; }
            results.Add(Read(probe, id, module));
        }
        return new(1, clock.GetUtcNow(), info.InstanceId, results, truncated, rejected);
    }

    private static DiagnosticProbeSnapshot Read(IDiagnosticProbe probe, string id, string module)
    {
        DiagnosticProbeReading reading;
        // 探针异常只影响自身；异常消息与类型不进入响应。
        try { reading = probe.Capture() ?? throw new InvalidOperationException(); }
        catch (Exception) { return Failed(id, module); }
        if (!DiagnosticSanitizer.IsProbeState(reading.State)) return Failed(id, module);
        int truncated = 0, rejected = 0;
        var counters = new SortedDictionary<string, UInt64String>(StringComparer.Ordinal);
        foreach (var (key, value) in reading.Counters)
        {
            if (!DiagnosticSanitizer.IsProbeKey(key) || counters.ContainsKey(key)) { rejected++; continue; }
            if (counters.Count >= MaximumCounters) { truncated++; continue; }
            counters.Add(key, new(value));
        }
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in reading.Values)
        {
            if (!DiagnosticSanitizer.IsProbeKey(key) || !DiagnosticSanitizer.IsProbeValue(value) || values.ContainsKey(key)) { rejected++; continue; }
            if (values.Count >= MaximumValues) { truncated++; continue; }
            values.Add(key, value);
        }
        return new(id, module, reading.State, DiagnosticSanitizer.ProbeReason(reading.Reason), counters, values, truncated, rejected);
    }

    private static DiagnosticProbeSnapshot Failed(string id, string module) => new(id, module, Unavailable, ProbeFailure,
        new Dictionary<string, UInt64String>(), new Dictionary<string, string>(), 0, 0);
}
