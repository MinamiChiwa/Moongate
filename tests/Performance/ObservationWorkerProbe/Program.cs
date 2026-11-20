using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.ObservationWorkerProbe;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;

// 观察 Worker 离线回放基准：实采样本 → 设备读到的包 → 流路由、重组、分帧、解析 → 写出队列 → 编码写入空目标。
// 不抓包、不连宿主、不启动游戏；样本只读。各参数形式见下方分支。
if (args is ["--prepare-phase", var preparePhase, var prepareClient, var prepareCache]) { await PreparationProbe.RunPhaseAsync(preparePhase, prepareClient, prepareCache); return; }
if (args is ["--prepare", var client, var cacheRoot, var prepareReport])
{
    // Worker 启动准备阶段（只读本机客户端文件）：client 为客户端目录，cacheRoot 为本次使用的临时缓存目录（结束后删除）。
    var preparation = new JsonObject { ["schemaVersion"] = 1, ["evidenceKind"] = "observation-worker-preparation", ["measuredAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        ["preparation"] = await PreparationProbe.RunAsync(client, cacheRoot) };
    File.WriteAllText(prepareReport, preparation.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(prepareReport);
    return;
}
var options = Args.Parse(args);
if (options.LoopSeconds is { } loopSeconds)
{
    using var loopSample = Sample.Load(options.Samples[0], false);
    var experiment = new JsonObject { ["schemaVersion"] = 1, ["evidenceKind"] = "observation-capture-read-loop", ["measuredAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        ["loop"] = LoopExperiment.Run(loopSample, loopSeconds) };
    File.WriteAllText(options.Report, experiment.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine(options.Report);
    return;
}
if (options.LoopbackSeconds is { } loopback)
{
    var experiment = new JsonObject { ["schemaVersion"] = 1, ["evidenceKind"] = "observation-capture-read-mode", ["measuredAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        ["loopback"] = LoopbackExperiment.Run(loopback) };
    File.WriteAllText(options.Report, experiment.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine(options.Report);
    return;
}
var samples = options.Samples.Select(directory => Sample.Load(directory, options.Noise)).ToList();
var output = new JsonObject
{
    ["schemaVersion"] = 1,
    ["evidenceKind"] = "observation-worker-replay",
    ["label"] = options.Label,
    ["measuredAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
    ["environment"] = JsonSerializer.SerializeToNode(new
    {
        os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
        processor = OperatingSystem.IsWindows() ? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString() : null,
        gc = System.Runtime.GCSettings.IsServerGC ? "server" : "workstation",
#if DEBUG
        configuration = "Debug",
#else
        configuration = "Release",
#endif
    }),
    ["protocol"] = JsonSerializer.SerializeToNode(new
    {
        input = "采集器 packets.jsonl 的下行段按 DLT_NULL/以太网 + IPv4 + TCP 重建为原始帧，连同 Npcap 包头放在非托管内存，按抓包时刻经 CapturePipeline.Packet 进入管线（与读取 Npcap 缓冲同一入口）",
        rounds = "样本时刻每 15.625 ms 一轮：读入本轮已到的包、推进解析；每 500 ms 模拟读连接表与 BPF 过滤器、发状态报告；每 100 ms 一轮写出",
        interests = "音乐兴趣开启，战斗 v3 兴趣（伤害、自身与怪物效果）开启；库存未开启（离线没有客户端布局）",
        writer = "ObservationCaptureRun 的写出路径，正文按 WorkerPeerConnection 的方式编成 Worker 帧写入计数空流",
        noise = options.Noise ? "另加一条游戏端口上的 TLS 连接：每 20 ms 一个 1,400 字节伪随机应用数据记录" : "无",
        repeats = options.Repeat,
        cpu = "进程 TotalProcessorTime 增量（回放线程为主，含 GC 线程）；取各次重复的中位数"
    }),
};
var throughput = new JsonArray();
NullPeer.Breakdown = args.Contains("--write-breakdown");
var check = options.VerifyEncoding ? new EncodingCheck() : null;
double totalCpu = 0, totalPayload = 0, totalAllocated = 0, totalWall = 0; int gen0 = 0, gen1 = 0, gen2 = 0;
var peak = new Occupancy();
foreach (var sample in samples)
{
    var runs = new List<ThroughputResult>();
    for (var i = 0; i < options.Repeat + 1; i++)
    {
        var result = await Replay.ThroughputAsync(sample, i == 0 ? check : null);
        if (i > 0 || options.Repeat == 0) runs.Add(result);
    }
    var median = runs.OrderBy(value => value.CpuMs).ElementAt(runs.Count / 2);
    totalCpu += median.CpuMs; totalPayload += median.PayloadBytes; totalAllocated += median.AllocatedBytes; totalWall += median.WallMs;
    gen0 += median.Gen0; gen1 += median.Gen1; gen2 += median.Gen2; peak.Max(median.Peak);
    var mib = median.PayloadBytes / 1048576d;
    throughput.Add(JsonSerializer.SerializeToNode(new
    {
        sample = median.Sample, packets = median.Packets, payloadBytes = median.PayloadBytes, sampleSeconds = median.SampleSeconds,
        wallMs = median.WallMs, cpuMs = median.CpuMs, cpuMsRuns = runs.Select(value => Math.Round(value.CpuMs, 1)).ToArray(),
        cpuMsPerMiB = median.CpuMs / mib, allocatedBytes = median.AllocatedBytes, allocatedBytesPerMiB = median.AllocatedBytes / mib,
        gen0 = median.Gen0, gen1 = median.Gen1, gen2 = median.Gen2, peak = median.Peak.Json(),
        written = new { messages = median.Messages, bytes = median.MessageBytes, kinds = median.Kinds },
        phases = median.Phases.ToDictionary(pair => pair.Key, pair => new { ms = Math.Round(pair.Value.Ms, 1), allocatedBytes = pair.Value.Allocated, calls = pair.Value.Calls }),
        counters = new
        {
            packets = median.Counters.Packets.Received.Value, filtered = median.Counters.Packets.Filtered.Value,
            frames = median.Counters.Frames.Total.Value, dispatched = median.Counters.Frames.Dispatched.Value,
            segments = median.Counters.Reassembly.Segments.Value, rejectedStreams = median.RejectedStreams, resets = median.Counters.Reassembly.Resets.ToDictionary(pair => pair.Key, pair => pair.Value.Value),
            prearm = median.Counters.Prearm is { } prearm ? new { buffered = prearm.Buffered.Value, delivered = prearm.Delivered.Value, budget = prearm.Budget.Value } : null,
            rejected = median.Counters.Frames.Rejected.ToDictionary(pair => pair.Key, pair => pair.Value.Value)
        }
    }, ContractJson.Options));
}
output["throughput"] = throughput;
var totalMiB = totalPayload / 1048576d;
output["totals"] = JsonSerializer.SerializeToNode(new
{
    payloadBytes = totalPayload, cpuMs = totalCpu, wallMs = totalWall, cpuMsPerMiB = totalCpu / totalMiB, allocatedBytes = totalAllocated,
    allocatedBytesPerMiB = totalAllocated / totalMiB, gen0, gen1, gen2, peak = peak.Json()
});
if (options.LatencySample is { } latencySample)
{
    using var sample = Sample.Load(latencySample, options.Noise);
    var (latencies, peer) = await Replay.LatencyAsync(sample, options.LatencySeconds * 1000d);
    var times = peer.Times.Order().ToArray(); var busiest = 0;
    for (int i = 0, j = 0; i < times.Length; i++) { while (times[i] - times[j] >= 1000) j++; busiest = Math.Max(busiest, i - j + 1); }
    output["latency"] = JsonSerializer.SerializeToNode(new
    {
        sample = sample.Name, seconds = options.LatencySeconds,
        queues = latencies.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => Percentiles(pair.Value)),
        messages = peer.Messages, messagesPerSecond = Math.Round(peer.Messages / (double)options.LatencySeconds, 1), busiestSecondMessages = busiest, kinds = peer.Kinds
    });
}
if (NullPeer.Breakdown)
    output["writeBreakdown"] = JsonSerializer.SerializeToNode(new[] { ("peer", NullPeer.Peer), ("frame", NullPeer.Frame), ("serialize", NullPeer.Serialize) }
        .ToDictionary(pair => pair.Item1, pair => new { ms = Math.Round(pair.Item2.Ms, 1), allocatedBytes = pair.Item2.Allocated, calls = pair.Item2.Calls }));
if (check is not null) output["encodingCheck"] = JsonSerializer.SerializeToNode(new { combatBatches = check.Compared, byteEqual = check.ByteEqual, objectByteEqual = check.ObjectByteEqual, semanticEqual = check.SemanticEqual });
if (options.ConfirmAnalysis) output["confirmation"] = ConfirmationAnalysis.Run(options.Samples);
if (options.Baseline is { } baseline) output["baseline"] = JsonNode.Parse(File.ReadAllText(baseline));
Directory.CreateDirectory(Path.GetDirectoryName(options.Report)!);
File.WriteAllText(options.Report, output.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
foreach (var sample in samples) sample.Dispose();
Console.WriteLine(options.Report);

static object Percentiles(List<double> values)
{
    var sorted = values.Order().ToArray();
    double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * (sorted.Length - 1)))];
    return new { count = sorted.Length, p50 = Math.Round(At(0.5), 2), p95 = Math.Round(At(0.95), 2), p99 = Math.Round(At(0.99), 2), max = Math.Round(sorted[^1], 2) };
}

internal sealed record Args(IReadOnlyList<string> Samples, string Report, string Label, bool Noise, int Repeat, string? LatencySample, int LatencySeconds,
    bool ConfirmAnalysis, string? Baseline, bool VerifyEncoding, int? LoopbackSeconds, int? LoopSeconds)
{
    public static Args Parse(string[] args)
    {
        string? Value(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        int? loopback = Value("--loopback-experiment") is { } seconds ? int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture) : null;
        var samples = (Value("--samples") ?? (loopback is null ? throw new ArgumentException("--samples 目录1;目录2（绝对路径）") : "")).Split(';', StringSplitOptions.RemoveEmptyEntries);
        var report = Value("--report") ?? throw new ArgumentException("--report 报告绝对路径");
        if (!Path.IsPathFullyQualified(report) || samples.Any(value => !Path.IsPathFullyQualified(value))) throw new ArgumentException("路径须为绝对路径");
        return new(samples, report, Value("--label") ?? "run", args.Contains("--noise"), int.Parse(Value("--repeat") ?? "3", System.Globalization.CultureInfo.InvariantCulture),
            Value("--latency-sample"), int.Parse(Value("--latency-seconds") ?? "60", System.Globalization.CultureInfo.InvariantCulture),
            args.Contains("--confirm-analysis"), Value("--baseline"), args.Contains("--verify-encoding"), loopback,
            Value("--loop-experiment") is { } loopSeconds ? int.Parse(loopSeconds, System.Globalization.CultureInfo.InvariantCulture) : null);
    }
}

/// <summary>
/// 未确认流的确认距离：对每条样本游戏连接的每个起点（模拟从该段中途接入），统计到第一个满足帧头特征的段之前已缓冲的负载字节与段数。
/// </summary>
internal static class ConfirmationAnalysis
{
    public static JsonNode Run(IReadOnlyList<string> directories)
    {
        var signature = Tw20261110CombatProtocol.StreamProfile.KnownProtocols.Where(code => code != 0).ToHashSet();
        var before = new List<long>(); var segments = new List<int>();
        long worstBytes = 0; int worstSegments = 0; string? worstAt = null; int unconfirmedStarts = 0; long unconfirmedTail = 0; long largestPayload = 0;
        foreach (var directory in directories)
        {
            using var sample = Sample.Load(directory, false);
            foreach (var group in sample.Packets.Where(packet => packet.Payload.Length > 0).GroupBy(packet => packet.Connection))
            {
                var payloads = group.Select(packet => packet.Payload).ToArray();
                largestPayload = Math.Max(largestPayload, payloads.Max(value => (long)value.Length));
                var prefix = new long[payloads.Length + 1];
                for (var i = 0; i < payloads.Length; i++) prefix[i + 1] = prefix[i] + payloads[i].Length;
                var next = new int[payloads.Length + 1]; next[payloads.Length] = -1;
                for (var i = payloads.Length - 1; i >= 0; i--) next[i] = GameFrameSignature.Matches(payloads[i], signature) ? i : next[i + 1];
                for (var i = 0; i < payloads.Length; i++)
                {
                    if (next[i] < 0) { unconfirmedStarts++; unconfirmedTail = Math.Max(unconfirmedTail, prefix[^1] - prefix[i]); continue; }
                    var bytes = prefix[next[i]] - prefix[i]; var count = next[i] - i;
                    before.Add(bytes); segments.Add(count);
                    if (bytes > worstBytes) { worstBytes = bytes; worstSegments = count; worstAt = sample.Name + "/" + sample.Connections[group.Key].Id[..8] + "#" + i; }
                }
            }
        }
        long Quantile(List<long> values, double q) { var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted[(int)Math.Floor(q * (sorted.Length - 1))]; }
        return JsonSerializer.SerializeToNode(new
        {
            starts = before.Count, bytesBeforeMatch = new { p50 = Quantile(before, 0.5), p99 = Quantile(before, 0.99), p999 = Quantile(before, 0.999), max = worstBytes },
            segmentsBeforeMatch = new { p50 = Quantile(segments.Select(value => (long)value).ToList(), 0.5), p99 = Quantile(segments.Select(value => (long)value).ToList(), 0.99), max = segments.DefaultIfEmpty().Max() },
            worst = new { at = worstAt, bytes = worstBytes, segments = worstSegments },
            unconfirmedTailStarts = unconfirmedStarts, unconfirmedTailMaxBytes = unconfirmedTail, largestSegmentBytes = largestPayload
        })!;
    }
}
