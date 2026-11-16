namespace MabiTool.ModuleSdk;

/// <summary>
/// 组件自行报告的运行状态探针。Capture 只读取组件已持有的内存状态，不得启动 Worker、触发采集或扫描磁盘；
/// 平台按白名单校验并截断结果，状态、键与值只使用稳定的短码。
/// </summary>
public interface IDiagnosticProbe
{
    string Id { get; }
    string Module { get; }
    DiagnosticProbeReading Capture();
}

/// <summary>按写入顺序保存计数与短值；超出平台上限的条目从末尾截断。</summary>
public sealed class DiagnosticProbeReading(string state, string? reason = null)
{
    private readonly List<KeyValuePair<string, ulong>> counters = [];
    private readonly List<KeyValuePair<string, string>> values = [];
    public string State { get; } = state;
    public string? Reason { get; } = reason;
    public IReadOnlyList<KeyValuePair<string, ulong>> Counters => counters;
    public IReadOnlyList<KeyValuePair<string, string>> Values => values;

    public DiagnosticProbeReading Count(string key, ulong value) { counters.Add(new(key, value)); return this; }
    public DiagnosticProbeReading Count(string key, long value) => value < 0 ? this : Count(key, (ulong)value);
    /// <summary>毫秒时长按整数报告；未知、非有限或负值不写入。</summary>
    public DiagnosticProbeReading Milliseconds(string key, double? value) =>
        value is { } ms && double.IsFinite(ms) && ms >= 0 && ms < ulong.MaxValue ? Count(key, (ulong)Math.Round(ms)) : this;
    public DiagnosticProbeReading Value(string key, string? value) { if (value is not null) values.Add(new(key, value)); return this; }
    public DiagnosticProbeReading Flag(string key, bool value) => Value(key, value ? "true" : "false");
}
