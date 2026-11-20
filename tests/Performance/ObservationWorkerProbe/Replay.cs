using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.ObservationWorkerProbe;

/// <summary>样本回放的捕获来源：只提供当前构建的战斗配置，捕获由回放直接驱动。</summary>
internal sealed class ReplaySource(Func<ObservationCaptureContext, CancellationToken, Task>? capture = null) : IObservationCaptureSource
{
    public IReadOnlyList<CaptureChannelOption> Channels() => [];
    public Task CaptureAsync(ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct) => throw new NotSupportedException();
    public IReadOnlyList<ObservationCombatProfile> CombatProfiles => [Tw20261110CombatProtocol.CombatProfile];
    public Task CaptureSharedAsync(ObservationCaptureRequest request, ObservationCaptureContext context, CancellationToken ct) =>
        capture?.Invoke(context, ct) ?? Task.CompletedTask;
}

/// <summary>写出目标：与 WorkerPeerConnection 相同地把正文编成 Worker 帧并写入空流，只统计条数与字节。</summary>
internal sealed class NullPeer : IObservationPeer
{
    private ulong sequence;
    public long Messages, Bytes;
    /// <summary>最近一条消息的正文（核对编码时使用）。</summary>
    public byte[]? Last;
    public bool Keep;
    /// <summary>每条消息的写出时刻（单调毫秒），用于统计任一 1 秒窗口内的消息数。</summary>
    public readonly List<double> Times = [];
    public readonly Dictionary<string, long> Kinds = new(StringComparer.Ordinal);
    private readonly CountingStream sink = new();
    /// <summary>写出分解（--write-breakdown）：Peer 为整个 SendAsync（不含下面单独计的序列化），Frame 为 WorkerProtocol.WriteAsync（校验与帧序列化），
    /// Serialize 为单独再做一次的帧序列化；Frame − Serialize 约为写出前的帧校验（含重复键检查）。</summary>
    public static bool Breakdown;
    public static readonly Phase Peer = new(), Frame = new(), Serialize = new();
    public async Task SendAsync(string kind, object payload, WorkerFrame? scope, CancellationToken ct)
    {
        var (at, allocated) = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
        var element = payload is JsonElement json ? json : JsonSerializer.SerializeToElement(payload, WorkerProtocol.Json);
        var frame = new WorkerFrame(1, (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture), scope?.RequestId, kind,
            scope?.RunId, scope?.BindingRevision, scope?.FencingToken, element);
        var before = sink.Length;
        if (Breakdown)
        {
            var (serializeAt, serializeAllocated) = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
            Serialize.Measure(() => JsonSerializer.SerializeToUtf8Bytes(frame, WorkerProtocol.Json));
            at += Stopwatch.GetTimestamp() - serializeAt; allocated += GC.GetAllocatedBytesForCurrentThread() - serializeAllocated;
            var (frameAt, frameAllocated) = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
            await WorkerProtocol.WriteAsync(sink, frame, ct).ConfigureAwait(false);
            Frame.Ms += Stopwatch.GetElapsedTime(frameAt).TotalMilliseconds; Frame.Allocated += GC.GetAllocatedBytesForCurrentThread() - frameAllocated; Frame.Calls++;
        }
        else await WorkerProtocol.WriteAsync(sink, frame, ct).ConfigureAwait(false);
        if (Breakdown) { Peer.Ms += Stopwatch.GetElapsedTime(at).TotalMilliseconds; Peer.Allocated += GC.GetAllocatedBytesForCurrentThread() - allocated; Peer.Calls++; }
        Messages++; Bytes += sink.Length - before;
        lock (Times) Times.Add(CapturePipeline.Mono());
        if (Keep) Last = JsonSerializer.SerializeToUtf8Bytes(element, WorkerProtocol.Json);
        var name = element.EnumerateObject().FirstOrDefault().Name ?? kind;
        Kinds[name] = Kinds.GetValueOrDefault(name) + 1;
    }
    public async Task SendEncodedAsync(string kind, ReadOnlyMemory<byte> payload, WorkerFrame? scope, CancellationToken ct)
    {
        var (at, allocated) = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
        var envelope = new WorkerFrame(1, (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture), scope?.RequestId, kind,
            scope?.RunId, scope?.BindingRevision, scope?.FencingToken, default);
        var before = sink.Length;
        await WorkerProtocol.WriteAsync(sink, envelope, payload, ct).ConfigureAwait(false);
        if (Breakdown)
        {
            var spent = (Stopwatch.GetElapsedTime(at).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
            Frame.Ms += spent.Item1; Frame.Allocated += spent.Item2; Frame.Calls++;
            Peer.Ms += spent.Item1; Peer.Allocated += spent.Item2; Peer.Calls++;
        }
        Messages++; Bytes += sink.Length - before;
        lock (Times) Times.Add(CapturePipeline.Mono());
        if (Keep) Last = payload.ToArray();
        var reader = new Utf8JsonReader(payload.Span);
        reader.Read(); reader.Read();
        var name = reader.GetString() ?? kind;
        Kinds[name] = Kinds.GetValueOrDefault(name) + 1;
    }
    private sealed class CountingStream : Stream
    {
        public override long Length => length;
        private long length;
        public override void Write(byte[] buffer, int offset, int count) => length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => length += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { length += buffer.Length; return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>回放时钟：单调毫秒以开始时的真实单调时钟为原点、按样本时刻推进；UTC 同步推进。</summary>
internal sealed class ReplayClock(double monoOrigin, DateTimeOffset utcOrigin)
{
    public double Elapsed;
    public double Mono() => monoOrigin + Elapsed;
    public DateTimeOffset Utc() => utcOrigin.AddMilliseconds(Elapsed);
}

/// <summary>各缓存的占用峰值（字节）。</summary>
internal sealed class Occupancy
{
    public long Candidate, Prearm, ReassemblyBuffer, ReassemblyPending, ReassemblyHistory, CombatQueue, InventoryQueue, ManagedHeap, Streams;
    public void Max(Occupancy value)
    {
        Candidate = Math.Max(Candidate, value.Candidate); Prearm = Math.Max(Prearm, value.Prearm);
        ReassemblyBuffer = Math.Max(ReassemblyBuffer, value.ReassemblyBuffer); ReassemblyPending = Math.Max(ReassemblyPending, value.ReassemblyPending);
        ReassemblyHistory = Math.Max(ReassemblyHistory, value.ReassemblyHistory); CombatQueue = Math.Max(CombatQueue, value.CombatQueue);
        InventoryQueue = Math.Max(InventoryQueue, value.InventoryQueue); ManagedHeap = Math.Max(ManagedHeap, value.ManagedHeap); Streams = Math.Max(Streams, value.Streams);
    }
    public object Json() => new { candidateBytes = Candidate, prearmBytes = Prearm, reassemblyBufferBytes = ReassemblyBuffer, reassemblyPendingBytes = ReassemblyPending,
        reassemblyHistoryBytes = ReassemblyHistory, combatQueueBytes = CombatQueue, inventoryQueueBytes = InventoryQueue, managedHeapBytes = ManagedHeap, streams = Streams };
}

internal sealed record ThroughputResult(string Sample, int Packets, long PayloadBytes, double SampleSeconds, double WallMs, double CpuMs,
    long AllocatedBytes, int Gen0, int Gen1, int Gen2, Occupancy Peak, long Messages, long MessageBytes, IReadOnlyDictionary<string, long> Kinds,
    ObservationCaptureCounters Counters, IReadOnlyDictionary<string, Phase> Phases, long RejectedStreams);

/// <summary>回放线程上一类调用的累计耗时与分配（含同步完成的写出）。</summary>
internal sealed class Phase
{
    public double Ms; public long Allocated; public long Calls;
    public T Measure<T>(Func<T> action)
    {
        var (at, allocated) = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
        try { return action(); }
        finally { Ms += Stopwatch.GetElapsedTime(at).TotalMilliseconds; Allocated += GC.GetAllocatedBytesForCurrentThread() - allocated; Calls++; }
    }
    public void Measure(Action action) => Measure(() => { action(); return 0; });
}

internal static class Replay
{
    /// <summary>读取循环一轮的样本时长：Windows 默认计时精度下 Task.Delay(10) 的实际间隔。</summary>
    public const double RoundMs = 15.625;
    public const int ProcessId = 4242;
    private static readonly WorkerFrame Scope = new(1, "1", new string('a', 32), "request", new string('b', 32), "7", "1",
        JsonSerializer.SerializeToElement(new { }));

    public static ObservationCaptureRun CreateRun(IObservationPeer peer, IObservationCaptureSource source, string gameSession)
    {
        var request = new ObservationCaptureRequest(gameSession, "TW", ProcessId, DateTimeOffset.UnixEpoch, null, Tw20261110CombatProtocol.CaptureProfile);
        var run = new ObservationCaptureRun(peer, source, request, Scope, null, reportDiagnostics: true);
        run.Initialize();
        run.Context.Configure(new(true, new(new string('c', 32), Tw20261110CombatProtocol.Profile, true, true, true)));
        return run;
    }

    /// <summary>
    /// 尽快回放一份样本：按 15.625 ms 一轮推进样本时刻，每轮读入抓包时刻不晚于本轮的包、推进解析；每 500 ms 读一次（模拟的）连接表并更新过滤器，
    /// 每 500 ms 发状态报告，每 100 ms 执行一轮写出。过滤器按当前端口集合模拟 BPF：不放行的包不进入管线。
    /// </summary>
    public static async Task<ThroughputResult> ThroughputAsync(Sample sample, EncodingCheck? check = null)
    {
        var peer = new NullPeer { Keep = check is not null };
        var run = CreateRun(peer, new ReplaySource(), "probe-" + sample.Name);
        if (check is not null) run.Sent = value => check.Compare(value, peer.Last);
        var context = run.Context;
        var clock = new ReplayClock(CapturePipeline.Mono(), DateTimeOffset.UtcNow);
        sample.Materialize(clock.Utc());
        var signature = Tw20261110CombatProtocol.StreamProfile.KnownProtocols.Where(code => code != 0).ToHashSet();
        var pipeline = new CapturePipeline(1, signature, "probe-" + sample.Name, context, CombatProtocolLayout.Tw20261110, null,
            new LocalIdentityMemory(), null, clock.Mono, clock.Utc);
        var prearm = new PrearmPool().Create(context.Diagnostics);
        PrearmBuffer[] prearms = [prearm];
        var peak = new Occupancy();
        var phases = new Dictionary<string, Phase>(StringComparer.Ordinal) { ["packet"] = new(), ["refresh"] = new(), ["tick"] = new(), ["report"] = new(), ["write"] = new() };
        HashSet<ushort> passLocal = [], passRemote = [];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes(true);
        var (g0, g1, g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var wall = Stopwatch.StartNew();
        double lastPoll = -1000, lastReport = -1000, lastWrite = 0;
        var next = 0; var end = sample.DurationMs + 2000;
        for (double t = 0; t <= end || next < sample.Packets.Count; t += RoundMs)
        {
            clock.Elapsed = t;
            if (t - lastPoll >= 500)
            {
                var table = sample.Connections.Where(row => row.ListedFrom <= t && t <= row.ListedTo)
                    .Select(row => (new CaptureConnection(row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort), ProcessId, 5)).ToList();
                phases["refresh"].Measure(() => pipeline.Refresh(table, ProcessId, prearms));
                passLocal = pipeline.GamePorts.Concat(pipeline.Router.LocalPorts).ToHashSet(); passRemote = pipeline.Router.RemotePorts.ToHashSet();
                lastPoll = t;
            }
            while (next < sample.Packets.Count && sample.Packets[next].Ms <= t)
            {
                var connection = sample.Connections[sample.Packets[next].Connection];
                var index = next;
                if (passLocal.Contains(connection.LocalPort) || passRemote.Contains(connection.RemotePort))
                    phases["packet"].Measure(() => pipeline.Packet(0, sample.Packets[index].Link, prearm, sample.Header(index), sample.Data(index)));
                next++;
            }
            phases["tick"].Measure(pipeline.Tick);
            if (t - lastReport >= 500) { phases["report"].Measure(pipeline.Report); lastReport = t; }
            if (t - lastWrite >= 100)
            {
                peak.Max(Sample(pipeline, prearm, run));
                var write = phases["write"].Measure(() => Task.WhenAll(run.WriteOnceAsync(CancellationToken.None), run.WriteInventoryOnceAsync(CancellationToken.None)));
                await write; lastWrite = t;
                peak.Max(Sample(pipeline, prearm, run));
            }
        }
        pipeline.Dispose();
        for (var i = 0; i < 64; i++) { await run.WriteOnceAsync(CancellationToken.None); await run.WriteInventoryOnceAsync(CancellationToken.None); }
        wall.Stop(); process.Refresh();
        var result = new ThroughputResult(sample.Name, sample.Packets.Count, sample.PayloadBytes, sample.DurationMs / 1000, wall.Elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpu).TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, peak, peer.Messages, peer.Bytes, peer.Kinds,
            context.Diagnostics.Snapshot(0, 0), phases, pipeline.Router.RejectedStreams);
        prearm.Clear();
        return result;
    }

    private static Occupancy Sample(CapturePipeline pipeline, PrearmBuffer prearm, ObservationCaptureRun run)
    {
        var value = new Occupancy { Candidate = pipeline.Router.CandidateBufferedBytes, Prearm = prearm.Bytes, Streams = pipeline.Router.Count,
            ManagedHeap = GC.GetTotalMemory(false) };
        foreach (var parser in pipeline.Router.Parsers)
        {
            var (buffer, pending, history) = parser.Stream.BufferedBytes;
            value.ReassemblyBuffer += buffer; value.ReassemblyPending += pending; value.ReassemblyHistory += history;
        }
        (value.CombatQueue, value.InventoryQueue) = ((long)run.QueuedBytes.Combat, (long)run.QueuedBytes.Inventory);
        return value;
    }

    /// <summary>
    /// 实时回放：按样本时刻原速送包（真实时钟），读取循环同生产实现（每轮读入已到的包、推进解析、Task.Delay(10)），写出用生产的写出循环；
    /// 统计每条记录从入队到写出的延迟。
    /// </summary>
    public static async Task<(Dictionary<string, List<double>> Latencies, NullPeer Peer)> LatencyAsync(Sample sample, double limitMs)
    {
        var latencies = new Dictionary<string, List<double>>(StringComparer.Ordinal) { ["music"] = [], ["combat"] = [], ["inventory"] = [] };
        var gate = new object();
        var music = new Dictionary<object, double>(ReferenceEqualityComparer.Instance);
        var combat = new Dictionary<(string, ulong), Queue<(ulong Sequence, double At)>>();
        static double Now() => CapturePipeline.Mono();
        var source = new ReplaySource(async (context, ct) =>
        {
            var utcOrigin = DateTimeOffset.UtcNow; var monoOrigin = Now();
            sample.Materialize(utcOrigin);
            var signature = Tw20261110CombatProtocol.StreamProfile.KnownProtocols.Where(code => code != 0).ToHashSet();
            using var pipeline = new CapturePipeline(1, signature, "probe-latency", context, CombatProtocolLayout.Tw20261110, null, new LocalIdentityMemory(), null);
            var prearm = new PrearmBuffer(context.Diagnostics);
            HashSet<ushort> passLocal = [], passRemote = [];
            double lastPoll = -1000, lastReport = -1000; var next = 0;
            while (!ct.IsCancellationRequested)
            {
                var t = Now() - monoOrigin;
                if (t > limitMs || next >= sample.Packets.Count && t > sample.DurationMs + 2000) break;
                if (t - lastPoll >= 500)
                {
                    var table = sample.Connections.Where(row => row.ListedFrom <= t && t <= row.ListedTo)
                        .Select(row => (new CaptureConnection(row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort), ProcessId, 5)).ToList();
                    pipeline.Refresh(table, ProcessId, [prearm]);
                    passLocal = pipeline.GamePorts.Concat(pipeline.Router.LocalPorts).ToHashSet(); passRemote = pipeline.Router.RemotePorts.ToHashSet();
                    lastPoll = t;
                }
                while (next < sample.Packets.Count && sample.Packets[next].Ms <= t)
                {
                    var connection = sample.Connections[sample.Packets[next].Connection];
                    if (passLocal.Contains(connection.LocalPort) || passRemote.Contains(connection.RemotePort))
                        pipeline.Packet(0, sample.Packets[next].Link, prearm, sample.Header(next), sample.Data(next));
                    next++;
                }
                pipeline.Tick();
                if (t - lastReport >= 500) { pipeline.Report(); lastReport = t; }
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            prearm.Clear();
        });
        var peer = new NullPeer();
        var run = CreateRun(peer, source, "probe-latency");
        run.Enqueued = report =>
        {
            lock (gate)
                switch (report)
                {
                    case GameCaptureReport value: music[value] = Now(); break;
                    case CombatCaptureReport value:
                        var key = (value.InterestId, value.Frame.Generation.Value);
                        if (!combat.TryGetValue(key, out var queue)) combat[key] = queue = new();
                        queue.Enqueue((value.Frame.Sequence.Value, Now())); break;
                }
        };
        run.Sent = report =>
        {
            var now = Now();
            lock (gate)
                switch (report)
                {
                    case GameCaptureReport value when music.Remove(value, out var at): latencies["music"].Add(now - at); break;
                    case CombatCaptureBatch { Head: var value } when combat.TryGetValue((value.InterestId, value.Frame.Generation.Value), out var queue):
                        while (queue.TryPeek(out var head) && head.Sequence <= value.Frame.Sequence.Value) { queue.Dequeue(); latencies["combat"].Add(now - head.At); }
                        break;
                }
        };
        using var stop = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        await run.RunAsync(stop, lifetime.Token);
        return (latencies, peer);
    }
}

/// <summary>
/// 核对写出的事件正文：同一批记录按此前的方式（合并后的报告对象经 WorkerProtocol 选项编为 {"combat":…} 正文）写出，与实际写出的正文比较字节与 JSON 语义。
/// </summary>
internal sealed class EncodingCheck
{
    public long Compared, ByteEqual, ObjectByteEqual, SemanticEqual;
    public void Compare(object sent, byte[]? actual)
    {
        if (sent is not CombatCaptureBatch batch || actual is null) return;
        // 此前的写出：报告对象经 WorkerPeerConnection 编为 JsonElement，再随 Worker 帧写出（与 NullPeer 记录正文的方式相同）。
        var expected = JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.SerializeToElement(new { combat = batch.Report }, WorkerProtocol.Json), WorkerProtocol.Json);
        Compared++;
        if (expected.AsSpan().SequenceEqual(actual)) ByteEqual++;
        // 报告对象直接编码（不经 JsonElement 转写；转写会把字符串里的 + 等字符改写为六个字符的 Unicode 转义）。
        if (JsonSerializer.SerializeToUtf8Bytes(new { combat = batch.Report }, WorkerProtocol.Json).AsSpan().SequenceEqual(actual)) ObjectByteEqual++;
        else if (Environment.GetEnvironmentVariable("PROBE_DUMP") is { } dump && !File.Exists(dump + ".expected.json"))
        { File.WriteAllBytes(dump + ".expected.json", expected); File.WriteAllBytes(dump + ".actual.json", actual); }
        if (System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(actual))) SemanticEqual++;
    }
}
