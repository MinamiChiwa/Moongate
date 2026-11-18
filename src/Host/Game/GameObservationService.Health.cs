using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Game;

/// <summary>
/// 常驻抓包运行后的健康检查：按 Worker 上报的计数写抓包流量摘要与按设备分项（第 10、30 秒，之后每 60 秒且有变化时），
/// 设备打开结果或主来源变化时写 observation.channel.selected；约 20 秒分不出游戏帧或迟迟没有本机身份时在自动抓包状态里给出原因码，供界面提示。
/// </summary>
public sealed partial class GameObservationService
{
    /// <summary>判定“分不出帧”与“等待身份”的时限（自捕获请求发出起）。</summary>
    internal static TimeSpan HealthGrace { get; set; } = TimeSpan.FromSeconds(20);
    internal const string NoGameFrames = "observation.noGameFrames", IdentityPending = "observation.identityPending";
    private static readonly double[] SummaryOffsetsMs = [10_000, 30_000];
    private const double SummaryIntervalMs = 60_000;

    private double? captureStartMono;
    private int summaryIndex;
    private string? lastSummary;
    private string? healthHint;
    private string? lastSelection;
    private string? tracedFailure;
    private int suppressedFailures;

    private void CaptureRequested()
    {
        captureStartMono = Mono(); summaryIndex = 0; lastSummary = null; healthHint = null; lastSelection = null;
    }

    private void CaptureCleared()
    {
        captureStartMono = null; healthHint = null;
    }

    /// <summary>运行中的自动抓包附带的原因码：分不出帧或等待身份；正常时为空。</summary>
    private string? RunningHint { get { lock (gate) return healthHint; } }

    /// <summary>当前主来源设备的类别；Worker 没有上报设备分项或还没有游戏流时为空。</summary>
    private static string? PrimarySourceKind(ObservationCaptureCounters? counters) => counters?.Devices?.FirstOrDefault(value => value.Primary)?.Kind;

    private Task SuperviseCaptureAsync()
    {
        lock (gate)
        {
            if (worker is null || state != "running" || captureStartMono is not { } start) return Task.CompletedTask;
            var now = Mono(); var elapsed = now - start;
            var counters = captureCounters;
            var frames = counters?.Frames.Total.Value ?? 0;
            var packets = counters?.Packets.Received.Value ?? 0; var connections = counters?.Connections.Current.Value ?? 0;
            var identity = latest?.Value.Identity is not null;
            // 分不出帧（含全部设备都没有游戏端口的包）为 noGameFrames；能分帧而没有本机身份为 identityPending。
            var hint = elapsed < HealthGrace.TotalMilliseconds ? null : frames == 0 ? NoGameFrames : identity ? null : IdentityPending;
            if (hint != healthHint) { healthHint = hint; revision++; }
            if (counters?.Devices is { } devices)
            {
                var primary = PrimarySourceKind(counters);
                var selection = string.Join(";", devices.Count(value => value.Opened), devices.Count(value => !value.Opened), primary);
                if (selection != lastSelection)
                {
                    lastSelection = selection; revision++;
                    trace?.Emit(DiagnosticEvents.ChannelSelected, new DiagnosticFields().Value("source", autoChoice?.Source ?? "request")
                        .Count("opened", devices.Count(value => value.Opened)).Count("failed", devices.Count(value => !value.Opened)).Value("primary", primary));
                }
            }
            var due = summaryIndex < SummaryOffsetsMs.Length ? SummaryOffsetsMs[summaryIndex]
                : SummaryOffsetsMs[^1] + (summaryIndex - SummaryOffsetsMs.Length + 1) * SummaryIntervalMs;
            if (elapsed >= due)
            {
                var key = counters is null ? identity.ToString() : string.Join(";", identity, packets, counters.Packets.Filtered.Value, connections,
                    counters.Reassembly.Segments.Value, frames, counters.Frames.Dispatched.Value, counters.Frames.DecompressFailures.Value);
                if (summaryIndex < SummaryOffsetsMs.Length || key != lastSummary)
                {
                    if (trace is not null)
                        foreach (var (definition, fields) in SummaryEvents(elapsed, autoChoice?.Source ?? "request", counters, identity,
                            latest is { } report ? now - report.MonotonicMs : null, lastArrivalMono is { } combat ? now - combat : null,
                            lastInventoryFrame is { } inventory ? now - inventory.HostMonotonicMs : null))
                            trace.Emit(definition, fields);
                }
                lastSummary = key; summaryIndex++;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 一次流量摘要写出的事件：流量计数（observation.capture.summary）、解析与产出状态（observation.capture.parse），
    /// 以及逐设备分项（observation.capture.device）。诊断事件每条至多保留 <see cref="DiagnosticSanitizer.MaximumFields"/> 个字段，按此拆分。
    /// </summary>
    internal static IReadOnlyList<(DiagnosticEventDefinition Definition, DiagnosticFields Fields)> SummaryEvents(double elapsedMs, string source,
        ObservationCaptureCounters? counters, bool identity, double? reportAgeMs, double? combatAgeMs, double? inventoryAgeMs)
    {
        var seconds = (long)(elapsedMs / 1000);
        var traffic = new DiagnosticFields().Count("elapsedSeconds", seconds).Value("source", source);
        var parse = new DiagnosticFields().Count("elapsedSeconds", seconds).Flag("identity", identity);
        if (counters is { } value)
        {
            traffic.Count("packets", value.Packets.Received.Value).Count("bytes", value.Packets.Bytes.Value).Count("filtered", value.Packets.Filtered.Value)
                .Count("truncated", value.Packets.Truncated.Value).Count("driverDropped", value.Packets.DriverDropped.Value)
                .Count("connections", value.Connections.Current.Value).Count("segments", value.Reassembly.Segments.Value);
            parse.Count("frames", value.Frames.Total.Value).Count("dispatched", value.Frames.Dispatched.Value)
                .Count("decompressFailures", value.Frames.DecompressFailures.Value)
                .Count("rejected", value.Frames.Rejected.Values.Aggregate(0UL, (sum, count) => sum + count.Value));
        }
        if (reportAgeMs is { } report) parse.Count("reportAgeMs", (long)Math.Max(0, report));
        if (combatAgeMs is { } combat) parse.Count("combatAgeMs", (long)Math.Max(0, combat));
        if (inventoryAgeMs is { } inventory) parse.Count("inventoryAgeMs", (long)Math.Max(0, inventory));
        var events = new List<(DiagnosticEventDefinition, DiagnosticFields)> { (DiagnosticEvents.CaptureSummary, traffic), (DiagnosticEvents.CaptureParse, parse) };
        var index = 0;
        foreach (var device in counters?.Devices ?? [])
            events.Add((DiagnosticEvents.CaptureDevice, new DiagnosticFields().Count("elapsedSeconds", seconds).Count("index", index++)
                .Value("kind", device.Kind).Count("linkType", device.LinkType).Flag("opened", device.Opened).Value("issue", device.Issue)
                .Flag("primary", device.Primary).Count("packets", device.Packets.Value).Count("portPackets", device.PortPackets.Value)
                .Count("gameStreams", device.GameStreams.Value).Count("frames", device.Frames.Value)
                .Count("candidateDropped", device.CandidateDropped?.Value ?? 0)));
        return events;
    }
    /// <summary>
    /// 自动抓包状态的诊断事件：同一原因的连续失败（例如未安装 Npcap 时每次重试）只记第一次，
    /// 之后的重复在下一条不同的事件里以 repeats 计数写出。
    /// </summary>
    private void TraceAutomation(string next, string? code)
    {
        if (trace is null) return;
        if (next == "starting" && code is null && tracedFailure is not null) return;
        if (next == "failed" && code == tracedFailure) { suppressedFailures++; return; }
        var fields = new DiagnosticFields().Value("state", next).Value("reason", code);
        if (suppressedFailures > 0) fields.Count("repeats", suppressedFailures);
        trace.Emit(DiagnosticEvents.AutomationChanged, fields);
        tracedFailure = next == "failed" ? code : null; suppressedFailures = 0;
    }
}
