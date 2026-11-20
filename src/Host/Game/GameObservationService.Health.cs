using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Game;

/// <summary>
/// 常驻抓包运行后的健康检查：按 Worker 上报的计数写抓包流量摘要与按设备分项（第 10、30 秒，之后每 60 秒且有变化时；设备分项只写有变化的设备，
/// 停止时为计数有变化的设备补写），设备打开结果、主来源或读取方式变化时写 observation.channel.selected；约 20 秒分不出游戏帧或迟迟没有本机身份时在自动抓包状态里给出原因码，供界面提示。
/// </summary>
public sealed partial class GameObservationService
{
    /// <summary>判定“分不出帧”与“等待身份”的时限（自捕获请求发出起）。</summary>
    internal static TimeSpan HealthGrace { get; set; } = TimeSpan.FromSeconds(20);
    internal const string NoGameFrames = "observation.noGameFrames", IdentityPending = "observation.identityPending",
        IdentityUnconfirmed = "observation.identityUnconfirmed";
    /// <summary>本次捕获随请求下发了同一进程先前确认的身份（中途接入沿用）；此后仍没有身份时原因为 <see cref="IdentityUnconfirmed"/>。</summary>
    private bool carriedIdentitySent;
    private static readonly double[] SummaryOffsetsMs = [10_000, 30_000];
    private const double SummaryIntervalMs = 60_000;

    private double? captureStartMono;
    private int summaryIndex;
    private string? lastSummary;
    private string? healthHint;
    private string? lastSelection;
    /// <summary>本次捕获每张设备（按设备 ID）最近写出的分项状态：打开结果、链路类型、主来源与各计数。</summary>
    private readonly Dictionary<string, string> writtenDevices = new(StringComparer.Ordinal);
    private string? tracedFailure;
    private int suppressedFailures;

    private void CaptureRequested()
    {
        captureStartMono = Mono(); summaryIndex = 0; lastSummary = null; healthHint = null; lastSelection = null; writtenDevices.Clear();
    }

    private void CaptureCleared()
    {
        captureStartMono = null; healthHint = null; carriedIdentitySent = false;
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
            // 分不出帧（含全部设备都没有游戏端口的包）为 noGameFrames；能分帧而没有本机身份为 identityPending，
            // 下发过沿用身份却没有得到印证（Worker 已撤回）为 identityUnconfirmed。
            var hint = elapsed < HealthGrace.TotalMilliseconds ? null : frames == 0 ? NoGameFrames : identity ? null
                : carriedIdentitySent ? IdentityUnconfirmed : IdentityPending;
            if (hint != healthHint) { healthHint = hint; revision++; }
            if (counters?.Devices is { } devices)
            {
                var primary = PrimarySourceKind(counters);
                var read = counters.Read;
                var selection = string.Join(";", devices.Count(value => value.Opened), devices.Count(value => !value.Opened), primary, read);
                if (selection != lastSelection)
                {
                    lastSelection = selection; revision++;
                    trace?.Emit(DiagnosticEvents.ChannelSelected, SelectionFields(autoChoice?.Source ?? "request", devices, primary, read));
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
                            lastInventoryFrame is { } inventory ? now - inventory.HostMonotonicMs : null,
                            inventoryFrameCounts.GetValueOrDefault("Invalidated:inventory.streamGap"), writtenDevices))
                            trace.Emit(definition, fields);
                }
                lastSummary = key; summaryIndex++;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>observation.channel.selected 的字段：来源、打开与失败的设备数、主来源类别，以及 Worker 上报的实际读取方式（旧 Worker 不上报时省略）。</summary>
    internal static DiagnosticFields SelectionFields(string source, IReadOnlyList<CaptureDeviceCounters> devices, string? primary, CaptureReadCounters? read)
    {
        var fields = new DiagnosticFields().Value("source", source)
            .Count("opened", devices.Count(value => value.Opened)).Count("failed", devices.Count(value => !value.Opened)).Value("primary", primary);
        if (read is not null)
            fields.Value("readMode", read.Mode).Flag("readFallback", read.Fallback).Count("coalesceMs", read.CoalesceMs).Count("kernelBufferKiB", read.BufferBytes / 1024);
        return fields;
    }

    /// <summary>
    /// 停止抓包时为自上次写出后计数或状态有变化的设备补写分项（含从未写过的设备），导出中每张设备的最后一条即其最终状态。
    /// 调用方在 Worker 的最终计数到达之后、清空捕获状态之前调用。
    /// </summary>
    private void TraceFinalDevices()
    {
        if (trace is null) return;
        IReadOnlyList<(DiagnosticEventDefinition Definition, DiagnosticFields Fields)> events;
        lock (gate)
        {
            if (captureStartMono is not { } start || captureCounters is not { } counters) return;
            events = DeviceEvents((long)((Mono() - start) / 1000), counters, writtenDevices);
        }
        foreach (var (definition, fields) in events) trace.Emit(definition, fields);
    }

    /// <summary>
    /// 一次流量摘要写出的事件：流量计数（observation.capture.summary）、解析与产出状态（observation.capture.parse），
    /// 以及逐设备分项（observation.capture.device）。诊断事件每条至多保留 <see cref="DiagnosticSanitizer.MaximumFields"/> 个字段，按此拆分。
    /// 给出 <paramref name="writtenDevices"/> 时只写首次出现、或打开结果、链路类型、主来源、计数自上次写出后变化的设备，并记下写出的状态。
    /// </summary>
    internal static IReadOnlyList<(DiagnosticEventDefinition Definition, DiagnosticFields Fields)> SummaryEvents(double elapsedMs, string source,
        ObservationCaptureCounters? counters, bool identity, double? reportAgeMs, double? combatAgeMs, double? inventoryAgeMs,
        long inventoryGapInvalidations = 0, IDictionary<string, string>? writtenDevices = null)
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
        if (counters?.Reassembly is { } reassembly)
        {
            ulong Reset(string name) => reassembly.Resets.TryGetValue(name, out var value) ? value.Value : 0;
            string[] named = ["connectionSyn", "connectionClosed", "connectionReset", "connectionRemoved", "gapTimeout", "captureDrops", "conflictingRetransmission", "invalidFrameHeader"];
            var other = reassembly.Resets.Where(pair => !named.Contains(pair.Key)).Aggregate(0UL, (sum, pair) => sum + pair.Value.Value);
            events.Add((DiagnosticEvents.CaptureReassembly, new DiagnosticFields().Count("elapsedSeconds", seconds)
                .Count("gapSkippedBytes", reassembly.GapSkippedBytes?.Value ?? 0).Count("connectionSyn", Reset("connectionSyn"))
                .Count("connectionEnded", Reset("connectionClosed") + Reset("connectionReset")).Count("connectionRemoved", Reset("connectionRemoved"))
                .Count("gapTimeout", Reset("gapTimeout")).Count("captureDrops", Reset("captureDrops"))
                .Count("conflictingRetransmission", Reset("conflictingRetransmission")).Count("invalidFrameHeader", Reset("invalidFrameHeader"))
                .Count("other", other).Count("combatGapResets", reassembly.GapSceneResets?.Value ?? 0)
                .Count("inventoryGapInvalidations", (ulong)Math.Max(0, inventoryGapInvalidations))));
        }
        events.AddRange(DeviceEvents(seconds, counters, writtenDevices));
        return events;
    }

    /// <summary>按设备分项；给出 <paramref name="written"/> 时只含状态或计数自上次写出后变化的设备。</summary>
    internal static List<(DiagnosticEventDefinition Definition, DiagnosticFields Fields)> DeviceEvents(long seconds, ObservationCaptureCounters? counters,
        IDictionary<string, string>? written)
    {
        var events = new List<(DiagnosticEventDefinition, DiagnosticFields)>();
        var index = 0;
        foreach (var device in counters?.Devices ?? [])
        {
            var position = index++;
            var state = string.Join(";", device.Kind, device.LinkType, device.Opened, device.Issue, device.Primary, device.Packets.Value,
                device.PortPackets.Value, device.GameStreams.Value, device.Frames.Value, device.CandidateDropped?.Value ?? 0);
            if (written is not null)
            {
                if (written.TryGetValue(device.ChannelId, out var previous) && previous == state) continue;
                written[device.ChannelId] = state;
            }
            events.Add((DiagnosticEvents.CaptureDevice, new DiagnosticFields().Count("elapsedSeconds", seconds).Count("index", position)
                .Value("kind", device.Kind).Count("linkType", device.LinkType).Flag("opened", device.Opened).Value("issue", device.Issue)
                .Flag("primary", device.Primary).Count("packets", device.Packets.Value).Count("portPackets", device.PortPackets.Value)
                .Count("gameStreams", device.GameStreams.Value).Count("frames", device.Frames.Value)
                .Count("candidateDropped", device.CandidateDropped?.Value ?? 0)));
        }
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
