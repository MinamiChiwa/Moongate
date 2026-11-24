using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Host.Combat;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 合成 Worker 战斗报告流驱动宿主消费路径：报告按 Worker 的 JSON 编码、经与 Worker 帧相同的受理函数（AcceptCombatEvent：
/// 反序列化、校验、订阅分发）进入共享战斗订阅，再由战斗执行器应用、发布最新捕获与保存检查点。进程内 mock 宿主（真实 SQLite、outbox、SSE）。
/// 阶段：quiet（无帧）→ traffic-idle（只有周围实体与效果、无伤害、无前端，即游戏开着无人操作时的真实空闲条件）→ boss（每秒 300 条伤害，在去重容量 10,000 条/30 秒之内，SSE 订阅战斗摘要）
/// → burst（每秒约 1,500 条伤害，超过去重容量后伤害按 deduplication-capacity 丢弃）→ tail（分段进行中、无帧）。
/// 报告每阶段的宿主 CPU（已扣除探针生成与编码报告的开销）、托管分配、GC 次数、帧到达至快照可查询与至摘要事件送达的延迟、写事务与写入字节。
/// --pipe：战斗报告先按 Worker 帧写入内存流、再由宿主帧读取函数读出后受理（Worker 侧写帧计入校准、从宿主 CPU 中扣除）；
/// 每 500 ms 的状态心跳另读一份状态报告帧，每秒另读一份捕获诊断计数帧，即 Worker 管道的宿主读取开销。
/// </summary>
internal static class ConsumptionProbe
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length < 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("参数：repository report [--idle 秒] [--boss 秒] [--tail 秒] [--rate 每秒报告数] [--label 名称] [--pipe]");
        var repository = Path.GetFullPath(args[0]); var report = Path.GetFullPath(args[1]);
        if (!report.StartsWith(Path.Combine(repository, "artifacts", "reports") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("报告须位于 artifacts/reports");
        var idleSeconds = Option(args, "--idle", 30); var bossSeconds = Option(args, "--boss", 60); var tailSeconds = Option(args, "--tail", 15);
        var rate = Option(args, "--rate", 10);
        var labelIndex = Array.IndexOf(args, "--label"); var label = labelIndex >= 0 ? args[labelIndex + 1] : "current";
        Pipe = args.Contains("--pipe");
        using var process = Process.GetCurrentProcess();

        // 校准：探针自身生成与编码报告的 CPU 与分配（Worker 侧开销，不计入宿主）。
        var calibration = new Generator("calibration-session", "0123456789abcdef0123456789abcdef");
        var calibrationFrames = 400;
        MarkFinalizer();
        // 预热：同一批受理与消费函数先在进程内跑过，测量阶段不含首次编译与分层重编译。
        Stages(400);
        var idleCost = Calibrate(process, () => WorkerSide(calibration.Encode(calibration.Next(boss: false))), calibrationFrames);
        calibration.DamagePerFrame = 30;
        var bossCost = Calibrate(process, () => WorkerSide(calibration.Encode(calibration.Next(boss: true))), calibrationFrames);
        calibration.DamagePerFrame = 150;
        var burstCost = Calibrate(process, () => WorkerSide(calibration.Encode(calibration.Next(boss: true))), calibrationFrames);
        calibration.DamagePerFrame = 1; calibration.Crowd = true;
        var crowdCost = Calibrate(process, () => WorkerSide(calibration.Encode(calibration.Next(boss: true))), calibrationFrames);
        calibration.Crowd = false;

        var temporary = Path.Combine(repository, "artifacts", "tmp", "consumption-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var secret = LocalAuthService.NewSecret();
            ProbeSource? source = null;
            await using var app = BackendApplication.Build(new(0, secret, new HashSet<string>()),
                storage: new(Path.Combine(temporary, "bootstrap"), true, PortableRoot: temporary), mock: true,
                combatSource: sp => source = new ProbeSource(sp));
            await app.StartAsync();
            var store = app.Services.GetRequiredService<SqliteStore>();
            var observations = app.Services.GetRequiredService<GameObservationService>();
            var operations = app.Services.GetRequiredService<CombatOperations>();
            var writes = new Dictionary<string, (long Count, double CallbackMs, double CommitMs)>(StringComparer.Ordinal);
            store.WriteTimingObserver = timing =>
            {
                lock (writes)
                {
                    var current = writes.GetValueOrDefault(timing.Callback);
                    writes[timing.Callback] = (current.Count + 1, current.CallbackMs + timing.CallbackMs, current.CommitMs + timing.CommitMs);
                }
            };
            var tracker = new LatencyTracker();
            operations.LiveObserver = tracker.Published;
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new(address) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", secret);
            var binding = (await http.GetFromJsonAsync<GameBindingSnapshot>(ApiContract.GameBinding.Path, ContractJson.Options))!;
            var session = binding.Binding!.Session.Id;
            using var start = new HttpRequestMessage(HttpMethod.Post, ApiContract.StartCombatObservation.Path)
            { Content = JsonContent.Create(new CombatObservationRequest(session), options: ContractJson.Options) };
            start.Headers.Add("Idempotency-Key", "consumption-start");
            (await http.SendAsync(start)).EnsureSuccessStatusCode();
            var feeder = await source!.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(2000);

            var phases = new List<object>();
            using var stop = new CancellationTokenSource();
            var heartbeat = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                var tick = 0;
                try
                {
                    while (await timer.WaitForNextTickAsync(stop.Token))
                    {
                        if (Pipe)
                        {
                            await WorkerProtocol.ReadAsync(new MemoryStream(StatusFrame));
                            if (tick++ % 2 == 0) await WorkerProtocol.ReadAsync(new MemoryStream(DiagnosticsFrame));
                        }
                        observations.NotifyForProbe();
                    }
                }
                catch (OperationCanceledException) { }
            });
            // 预热 20 秒的周围实体流量（不计入），之后的阶段不含宿主各路径的首次编译。
            await MeasureAsync("warm-up", 20, rate, boss: false, feeder, tracker, process, store, writes, idleCost, null);
            phases.Add(await MeasureAsync("quiet", Math.Min(10, idleSeconds), 0, boss: false, feeder, tracker, process, store, writes, default, null));
            phases.Add(await MeasureAsync("traffic-idle", idleSeconds, rate, boss: false, feeder, tracker, process, store, writes, idleCost, null));
            var events = new EventListener(http, tracker);
            var listening = events.RunAsync(stop.Token);
            await events.Connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            feeder.Generator.DamagePerFrame = 30;
            phases.Add(await MeasureAsync("boss", bossSeconds, rate, boss: true, feeder, tracker, process, store, writes, bossCost, events));
            feeder.Generator.DamagePerFrame = 150;
            phases.Add(await MeasureAsync("burst", Math.Min(10, bossSeconds), rate, boss: true, feeder, tracker, process, store, writes, burstCost, events));
            // 千人同屏量级：每秒 80 份报告（Worker 战斗写出的速率上限），每份 128 个实体、112 条效果、1 条本机伤害与 1 条治疗。
            feeder.Generator.DamagePerFrame = 1; feeder.Generator.Crowd = true;
            phases.Add(await MeasureAsync("crowd", Math.Max(10, bossSeconds), 80, boss: true, feeder, tracker, process, store, writes, crowdCost, events));
            feeder.Generator.Crowd = false;
            // 零星单发：每秒 2 份报告、每份 1 条本机伤害（停顿间的单次命中），报告到唤醒事件的延迟不受密集合并影响。
            feeder.Generator.DamagePerFrame = 1;
            phases.Add(await MeasureAsync("sparse", Math.Min(10, bossSeconds), 2, boss: true, feeder, tracker, process, store, writes, bossCost, events));
            // 只有血量变化：主目标为首领、每份报告首领 HP 下降，没有伤害（他人攻击首领时本机看到的情形）。
            feeder.Generator.HealthOnly = true;
            phases.Add(await MeasureAsync("health-only", Math.Min(10, bossSeconds), 5, boss: false, feeder, tracker, process, store, writes, idleCost, events));
            feeder.Generator.HealthOnly = false;
            phases.Add(await MeasureAsync("tail", tailSeconds, 0, boss: false, feeder, tracker, process, store, writes, default, events));
            await stop.CancelAsync();
            try { await listening; } catch (Exception) when (stop.IsCancellationRequested) { }
            await heartbeat;
            store.WriteTimingObserver = null; operations.LiveObserver = null;
            var output = new
            {
                schemaVersion = 1, evidenceKind = "backend-consumption", label, measuredAtUtc = DateTimeOffset.UtcNow,
                environment = new
                {
                    os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
                    processor = OperatingSystem.IsWindows() ? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString() : null,
#if DEBUG
                    configuration = "Debug",
#else
                    configuration = "Release",
#endif
                    serverGc = System.Runtime.GCSettings.IsServerGC
                },
                protocol = new
                {
                    host = "进程内 mock 宿主（真实 SQLite WAL+FULL、outbox、SSE）；报告经 GameObservationService 的 Worker 战斗帧受理函数进入共享订阅，与常驻观测相同的战斗执行器消费",
                    pipe = Pipe ? "战斗报告经 Worker 帧写入与宿主帧读取（含重复键检查与信封校验）后受理；心跳另读状态报告帧（每 500 ms）与诊断计数帧（每秒）" : "不经 Worker 帧编解码",
                    reportsPerSecond = rate,
                    idleFrame = "64 个实体（本机、2 名队友、61 个怪物），每帧 2 条怪物效果刷新，每 2 秒替换 1 个怪物，无伤害",
                    bossFrame = "40 个实体，每帧 30 条伤害（本机 20、队友 10）、6 条效果、1 条治疗；10 帧/秒即 300 条伤害/秒",
                    burstFrame = "同上但每帧 150 条伤害（本机 100、队友 50），约 1,500 条伤害/秒，10 秒",
                    heartbeat = "每 500 ms 一次观察快照变化通知（模拟 Worker 状态心跳）",
                    events = "boss、burst、sparse、health-only 与 tail 阶段有一个 SSE 订阅（战斗主题，宿主提供时另含 combat.live.changed），traffic-idle 阶段没有订阅者",
                    sparseFrame = "每秒 2 份报告、每份 1 条本机伤害，10 秒",
                    crowdFrame = "千人同屏量级：每秒 80 份报告（Worker 战斗写出速率上限），每份 128 个实体、112 条效果、1 条本机伤害、1 条治疗，即每秒约 9,000 条效果",
                    healthOnlyFrame = "每秒 5 份报告、没有伤害，首领为本机主目标且每份报告 HP 下降，10 秒",
                    liveEvents = EventListener.LiveEvents,
                    cpu = "进程 CPU 减去校准得到的探针生成与编码报告开销（每帧 idle/boss 分别校准）",
                    calibration = new { idleFrameCpuMs = idleCost.CpuMs, idleFrameAllocatedBytes = idleCost.Allocated, idleFrameBytes = idleCost.Bytes,
                        bossFrameCpuMs = bossCost.CpuMs, bossFrameAllocatedBytes = bossCost.Allocated, bossFrameBytes = bossCost.Bytes,
                        burstFrameCpuMs = burstCost.CpuMs, burstFrameAllocatedBytes = burstCost.Allocated, burstFrameBytes = burstCost.Bytes,
                        crowdFrameCpuMs = crowdCost.CpuMs, crowdFrameAllocatedBytes = crowdCost.Allocated, crowdFrameBytes = crowdCost.Bytes }
                },
                phases
            };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(output, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
            Console.WriteLine(report);
            await app.StopAsync();
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(temporary).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                for (var attempt = 0; attempt < 5 && Directory.Exists(temporary); attempt++)
                    try { Directory.Delete(temporary, true); } catch (IOException) { await Task.Delay(200); }
        }
    }

    /// <summary>
    /// 单线程分段计时：同一批合成报告依次经过宿主受理与消费的各步骤（与运行路径相同的函数），每步记录累计耗时与分配，
    /// 用于定位每份报告的开销来源。raw-size 与 fingerprint 两步按宿主当前做法（原始 UTF-8 长度与其 SHA-256）计时。
    /// </summary>
    public static void RunStages(string[] args)
    {
        var report = Path.GetFullPath(args[1]); var count = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 300;
        var results = Stages(count);
        File.WriteAllText(report, JsonSerializer.Serialize(new { evidenceKind = "backend-consumption-stages", reports = count, results }, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
        Console.WriteLine(report);
    }

    private static Dictionary<string, object> Stages(int count)
    {
        var results = new Dictionary<string, object>();
        foreach (var boss in new[] { false, true })
        {
            const string session = "stage-session";
            var generator = new Generator(session, "0123456789abcdef0123456789abcdef");
            var encoded = Enumerable.Range(0, count + 50).Select(_ => generator.Encode(generator.Next(boss))).ToArray();
            var source = Tw20261110CombatProtocol.CombatProfile.Source;
            var local = generator.Next(boss).LocalEntity!;
            var request = new CombatObservationRequest(session);
            var timings = new Dictionary<string, (double Ms, long Bytes)>(StringComparer.Ordinal);
            T Step<T>(string name, Func<T> action)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                var value = action();
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var current = timings.GetValueOrDefault(name); timings[name] = (current.Ms + elapsed, current.Bytes + bytes);
                return value;
            }
            for (var round = 0; round < 2; round++)
            {
                timings.Clear();
                var session1 = new MabiTool.Modules.CombatObservation.Domain.CombatSession("owner", "observation", source, local, request, DateTimeOffset.UtcNow, healing: true);
                var buffer1 = new MabiTool.Modules.CombatObservation.Domain.CombatFrameBuffer();
                var buffer2 = new MabiTool.Modules.CombatObservation.Domain.CombatFrameBuffer();
                var pipe = new MemoryStream();
                foreach (var bytes in round == 0 ? encoded.Take(50) : encoded.Skip(50))
                {
                    // Worker 管道两端：Worker 把事件正文编成帧写出（校验、重复键检查、帧序列化），宿主读帧（重复键检查、解析信封、校验）。
                    using (var incoming = JsonDocument.Parse(bytes))
                    {
                        pipe.SetLength(0);
                        var outgoing = new WorkerFrame(1, "7", "0123456789abcdef0123456789abcdef", "event", "fedcba9876543210fedcba9876543210", "1", "0", incoming.RootElement);
                        Step("worker-frame-write", () => { WorkerProtocol.WriteAsync(pipe, outgoing).GetAwaiter().GetResult(); return 0; });
                        // Worker 写出已编码事件的路径：正文原样拼入帧。
                        pipe.SetLength(0);
                        Step("worker-frame-write-encoded", () => { WorkerProtocol.WriteAsync(pipe, outgoing with { Payload = default }, bytes).GetAwaiter().GetResult(); return 0; });
                    }
                    pipe.Position = 0;
                    Step("host-frame-read", () => WorkerProtocol.ReadAsync(pipe).GetAwaiter().GetResult());
                    using var document = Step("parse-envelope", () => JsonDocument.Parse(bytes));
                    var payload = document.RootElement.GetProperty("combat");
                    Step("raw-size", () => System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(payload).Length);
                    var value = Step("deserialize", () => payload.Deserialize<CombatCaptureReport>(CaptureCombatValidation.Json)!);
                    Step("validate", () => { CaptureCombatValidation.Validate(value, source, session); return 0; });
                    Step("fingerprint", () => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(payload))));
                    var filtered = Step("filter-effects", () => value.Frame with { Effects = value.Frame.Effects.Where(item => item.EntityKey == value.LocalEntity?.Key || true).ToArray() });
                    Step("subscription-buffer", () => buffer1.TryWrite(filtered));
                    var first = buffer1.Take(1).Frames[0];
                    Step("job-buffer", () => buffer2.TryWrite(first));
                    var second = buffer2.Take(1).Frames[0];
                    Step("session-apply", () => session1.Apply(second));
                    var checkpoint = Step("session-capture", () => session1.Capture());
                    Step("workspace-json", () => JsonSerializer.SerializeToUtf8Bytes(checkpoint.Workspace, ContractJson.Options).Length);
                    session1.Acknowledge(checkpoint);
                }
            }
            results[boss ? "boss" : "idle"] = timings.OrderByDescending(value => value.Value.Ms).ToDictionary(value => value.Key,
                value => (object)new { msPerReport = value.Value.Ms / count, kbPerReport = value.Value.Bytes / 1024d / count });
        }
        return results;
    }

    private static bool Pipe;
    /// <summary>Worker 侧：--pipe 时把事件正文编成 Worker 帧（校准时计入 Worker 开销）。</summary>
    private static byte[] WorkerSide(byte[] payload)
    {
        if (!Pipe) return payload;
        using var document = JsonDocument.Parse(payload);
        var stream = new MemoryStream();
        WorkerProtocol.WriteAsync(stream, new WorkerFrame(1, "7", ProbeRequest, "event", ProbeRun, "1", "0", document.RootElement)).GetAwaiter().GetResult();
        return stream.ToArray();
    }
    private const string ProbeRequest = "0123456789abcdef0123456789abcdef", ProbeRun = "fedcba9876543210fedcba9876543210";
    /// <summary>约 1.6 KB 的状态报告帧（身份、地图与来源字段的典型长度）。</summary>
    private static readonly byte[] StatusFrame = Frame(new { report = new { schemaVersion = 1, sequence = "12345", running = true, monotonicMs = 123456.789,
        observedAt = DateTimeOffset.UtcNow, reason = (string?)null, source = new { provider = "tw-observation", providerVersion = "1.0.0", region = "TW", gameBuild = "tw-20261110" },
        identityConnection = new { localPort = 51234, remoteAddress = "43.212.230.170", remotePort = 16000 },
        value = new { captureGeneration = "0123456789abcdef0123456789abcdef", detail = "已识别本机角色。", packets = "123456",
            identity = new { characterId = "1234567890123", realmId = "4", nickname = "合成角色", actorId = "4987654" },
            location = new { channelSheetId = 1201, mapName = "提爾·柯內爾", mapReason = (string?)null, channelReason = "channel.unverified", permanentChannelIndex = 3 },
            concert = (object?)null, music = (object?)null, filler = string.Join(",", Enumerable.Range(0, 60).Select(i => "field" + i)) } } });
    /// <summary>8 张设备分项的捕获诊断计数帧（约 3 KB）。</summary>
    private static readonly byte[] DiagnosticsFrame = Frame(new { captureDiagnostics = new { schemaVersion = 1,
        connections = new { current = "3", opened = "9", closed = "6" },
        packets = new { received = "184000", bytes = "221650400", filtered = "3", truncated = "0", driverDropped = "0", readLimitHits = "1" },
        queues = new { combatDepth = "0", combatPeak = "2", inventoryDepth = "0", inventoryPeak = "1", inventoryLaneRejected = "0" },
        reassembly = new { segments = "183700", bytes = "220112000", outOfOrder = "4", retransmitted = "2", resets = new { connectionSyn = "3", gapTimeout = "1" } },
        frames = new { total = "91200", dispatched = "64000", decompressFailures = "0", rejected = new { }, types = Enumerable.Range(0, 24).ToDictionary(i => (20000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), i => (i * 37).ToString(System.Globalization.CultureInfo.InvariantCulture)), otherFrames = "0" },
        devices = Enumerable.Range(0, 8).Select(i => new { channelId = new string((char)('a' + i), 32), kind = "physical", linkType = 1, opened = true, issue = (string?)null,
            primary = i == 0, packets = "1000", portPackets = "900", gameStreams = "1", frames = "500", candidateDropped = "0" }).ToArray(),
        read = new { mode = "immediate", fallback = false, coalesceMs = 10, pollMs = 0, bufferBytes = 1048576 } } });
    private static byte[] Frame(object payload)
    {
        var stream = new MemoryStream();
        WorkerProtocol.WriteAsync(stream, new WorkerFrame(1, "7", ProbeRequest, "event", ProbeRun, "1", "0",
            JsonSerializer.SerializeToElement(payload, ContractJson.Options))).GetAwaiter().GetResult();
        return stream.ToArray();
    }
    private readonly record struct Cost(double CpuMs, double Allocated, double Bytes);
    private static Cost Calibrate(Process process, Func<byte[]> produce, int count)
    {
        for (var i = 0; i < 50; i++) produce();
        GC.Collect(); process.Refresh();
        var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes(true); long bytes = 0;
        for (var i = 0; i < count; i++) bytes += produce().Length;
        process.Refresh();
        return new((process.TotalProcessorTime - cpu).TotalMilliseconds / count, (GC.GetTotalAllocatedBytes(true) - allocated) / (double)count, bytes / (double)count);
    }

    private static async Task<object> MeasureAsync(string name, int seconds, int rate, bool boss, Feeder feeder, LatencyTracker tracker, Process process,
        SqliteStore store, Dictionary<string, (long Count, double CallbackMs, double CommitMs)> writes, Cost cost, EventListener? events)
    {
        lock (writes) writes.Clear();
        tracker.Reset(); events?.Reset();
        process.Refresh();
        var cpu = process.TotalProcessorTime; var io = Io.Read(process); var allocated = GC.GetTotalAllocatedBytes(false);
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var outbox = MaxSequence(store); var clock = Stopwatch.StartNew(); var frames = 0; var bytes = 0L;
        var threadsBefore = ThreadTimes(process);
        if (rate > 0)
        {
            var interval = 1000d / rate;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var due = frames * interval - clock.Elapsed.TotalMilliseconds;
                if (due > 1) await Task.Delay(TimeSpan.FromMilliseconds(due));
                bytes += feeder.Send(boss, tracker);
                frames++;
            }
        }
        else await Task.Delay(TimeSpan.FromSeconds(seconds));
        await Task.Delay(500);
        var elapsed = clock.Elapsed.TotalMilliseconds; process.Refresh();
        var threadsAfter = ThreadTimes(process);
        var threadCpu = threadsAfter.Select(value => (Id: value.Key, Ms: value.Value - threadsBefore.GetValueOrDefault(value.Key)))
            .OrderByDescending(value => value.Ms).Take(6).Select(value => new { thread = value.Id == FinalizerThread ? "finalizer" : value.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), cpuMs = value.Ms }).ToArray();
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds - frames * cost.CpuMs;
        var ioDelta = Io.Read(process) - io; var outboxDelta = MaxSequence(store) - outbox;
        var allocatedDelta = GC.GetTotalAllocatedBytes(false) - allocated - frames * cost.Allocated;
        Dictionary<string, (long Count, double CallbackMs, double CommitMs)> snapshot;
        lock (writes) snapshot = new(writes, StringComparer.Ordinal);
        var perSecond = elapsed / 1000;
        return new
        {
            phase = name, elapsedMs = elapsed, reports = frames, reportBytes = bytes, topThreads = threadCpu,
            hostCpuMs = cpuMs, hostCores = cpuMs / elapsed, wholeMachinePercent = cpuMs / elapsed / Environment.ProcessorCount * 100,
            hostCpuMsPerReport = frames == 0 ? (double?)null : cpuMs / frames,
            allocatedBytes = allocatedDelta, allocatedBytesPerReport = frames == 0 ? (double?)null : allocatedDelta / frames,
            gc = new { gen0 = GC.CollectionCount(0) - gen0, gen1 = GC.CollectionCount(1) - gen1, gen2 = GC.CollectionCount(2) - gen2 },
            latency = new { reportToSnapshotMs = tracker.Snapshot(), reportToSnapshotByWindowMs = tracker.Windows(), events = events?.Latencies() },
            transactionsPerSecond = snapshot.Values.Sum(value => value.Count) / perSecond,
            transactions = snapshot.OrderByDescending(value => value.Value.Count).Select(value => new
                { callback = value.Key, count = value.Value.Count, perSecond = value.Value.Count / perSecond,
                  callbackMsAverage = value.Value.CallbackMs / value.Value.Count, commitMsAverage = value.Value.CommitMs / value.Value.Count }).ToArray(),
            outboxEventsPerSecond = outboxDelta / perSecond, outboxByType = OutboxTypes(store, outbox),
            io = new { writeOpsPerSecond = ioDelta.WriteOps / perSecond, writeBytesPerSecond = ioDelta.WriteBytes / perSecond,
                readOpsPerSecond = ioDelta.ReadOps / perSecond, otherOpsPerSecond = ioDelta.OtherOps / perSecond }
        };
    }

    private static Dictionary<int, double> ThreadTimes(Process process)
    {
        process.Refresh(); var result = new Dictionary<int, double>();
        foreach (ProcessThread thread in process.Threads)
            try { result[thread.Id] = thread.TotalProcessorTime.TotalMilliseconds; } catch (InvalidOperationException) { }
        return result;
    }
    private static int FinalizerThread;
    private sealed class FinalizerMarker { ~FinalizerMarker() => FinalizerThread = GetCurrentThreadId(); }
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
    internal static void MarkFinalizer() { _ = new FinalizerMarker(); GC.Collect(); GC.WaitForPendingFinalizers(); }

    private static int Option(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return fallback;
        var value = int.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture);
        if (value is < 0 or > 3600) throw new ArgumentException(name + " 超出 0–3600");
        return value;
    }
    private static Dictionary<string, long> OutboxTypes(SqliteStore store, long after) => store.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type,count(*) FROM core_outbox WHERE sequence>$s GROUP BY event_type";
        command.Parameters.AddWithValue("$s", after);
        using var rows = command.ExecuteReader(); var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (rows.Read()) result[rows.GetString(0)] = rows.GetInt64(1);
        return result;
    });
    private static long MaxSequence(SqliteStore store) => Convert.ToInt64(store.ReadScalar("SELECT coalesce(max(sequence),0) FROM core_outbox"));

    private sealed class ProbeSource(IServiceProvider services) : ICombatObservationSource
    {
        public TaskCompletionSource<Feeder> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CombatCapabilities Capabilities(string? session) => new(true,
            new[] { "combat.damage.local", "combat.effects.self", "combat.effects.monster", "combat.healing.local" }.Select(id =>
                new CombatCapability(id, CapabilityStatus.Available, Tw20261110CombatProtocol.CombatProfile.Source, null, null, null)).ToArray());
        public ValueTask<ICombatObservationSubscription> OpenAsync(string id, CombatObservationRequest request, CancellationToken ct) =>
            OpenAsync("owner", id, request, ct);
        public async ValueTask<ICombatObservationSubscription> OpenAsync(string principal, string id, CombatObservationRequest request, CancellationToken ct)
        {
            var observations = services.GetRequiredService<GameObservationService>();
            var subscription = observations.AttachCombatForProbe(principal, id, request, Tw20261110CombatProtocol.CombatProfile);
            var feeder = new Feeder(observations, new Generator(request.GameSessionId, subscription.InterestId));
            feeder.Send(boss: false, null);
            await subscription.Ready.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Opened.TrySetResult(feeder);
            return subscription;
        }
    }

    /// <summary>编码报告并按 Worker 帧受理；返回报告字节数。</summary>
    private sealed class Feeder(GameObservationService observations, Generator generator)
    {
        public Generator Generator => generator;
        private readonly object gate = new();
        public long Send(bool boss, LatencyTracker? tracker)
        {
            lock (gate)
            {
                var report = generator.Next(boss);
                var bytes = generator.Encode(report);
                if (Pipe)
                {
                    // Worker 侧写帧（已在校准中扣除），宿主读帧后受理。
                    var framed = WorkerSide(bytes);
                    tracker?.Sent(report.Frame.MonotonicMs);
                    observations.AcceptCombatFrameForProbe(WorkerProtocol.ReadAsync(new MemoryStream(framed)).GetAwaiter().GetResult());
                    return bytes.Length;
                }
                using var document = JsonDocument.Parse(bytes);
                tracker?.Sent(report.Frame.MonotonicMs);
                observations.AcceptCombatFrameForProbe(new WorkerFrame(1, Guid.NewGuid().ToString("N"), null, "event", "run", null, null, document.RootElement));
                return bytes.Length;
            }
        }
    }

    /// <summary>合成报告：同一代次、序号递增；时点取宿主同一单调时钟。</summary>
    private sealed class Generator(string session, string interest)
    {
        /// <summary>首领战每帧伤害条数；三分之二来自本机，其余来自队友。</summary>
        public int DamagePerFrame { get; set; } = 150;
        /// <summary>只有血量变化的帧：首领为本机主目标，每帧首领 HP 下降，没有伤害。</summary>
        public bool HealthOnly { get; set; }
        /// <summary>千人同屏量级的帧：128 个实体（本机、2 名队友、125 个怪物），112 条效果轮流施加与刷新，1 条治疗。</summary>
        public bool Crowd { get; set; }
        private readonly List<ulong> crowd = Enumerable.Range(0, 125).Select(index => (ulong)(5000 + index)).ToList();
        private ulong bossHealth = 10_000_000;
        private static readonly ObservationSource Source = Tw20261110CombatProtocol.CombatProfile.Source;
        private ulong sequence, stream = 1000, effectCount;
        private int frame;
        private ulong nextMonster = 1061;
        private readonly List<ulong> monsters = Enumerable.Range(0, 61).Select(index => (ulong)(1000 + index)).ToList();
        private EntityKey Key(ulong raw) => new(session, new(1), new(raw), null);
        private CombatEntityObservation Local => new(Key(1), "本机", "本机", CombatEntityRole.LocalPlayer, null, false, true, ObservationQuality.Confirmed);
        public CombatCaptureReport Next(bool boss)
        {
            frame++;
            var mono = Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency); var at = DateTimeOffset.UtcNow;
            if (!boss && !HealthOnly && frame % 20 == 0) { monsters.RemoveAt(0); monsters.Add(nextMonster++); }
            var entities = new List<CombatEntityObservation> { Local,
                new(Key(2), "隊友甲", "隊友甲", CombatEntityRole.Player, null, false, true, ObservationQuality.Confirmed),
                new(Key(3), "隊友乙", "隊友乙", CombatEntityRole.Player, null, false, true, ObservationQuality.Confirmed) };
            var count = boss ? 37 : 61;
            if (Crowd)
            {
                entities.AddRange(crowd.Select(raw => new CombatEntityObservation(Key(raw), "怪物" + raw, "怪物" + raw, CombatEntityRole.Monster, null, false, true, ObservationQuality.Confirmed)));
                var crowdDamage = Enumerable.Range(0, DamagePerFrame).Select(i => new CombatDamageEvent($"d{frame}-{i}", Source, Key(1), Key(crowd[0]),
                    new(20000), new((ulong)(1000 + i * 7)), CombatDamageKind.ActualHp, at, mono - 80 + i * 0.5, new(stream += 64), 0, false, true,
                    ObservationQuality.Confirmed, SkillCasterKey: Key(1), SkillResolved: true)).ToList();
                var crowdEffects = Enumerable.Range(0, 112).Select(_ =>
                {
                    var slot = effectCount++ % 500; var first = effectCount <= 500;
                    return new CombatEffectObservation(first ? CombatEffectChangeKind.Apply : CombatEffectChangeKind.Refresh, Key(crowd[(int)(slot % 125)]),
                        new(300 + slot / 125), new(slot + 1), Key(slot % 3 == 0 ? 1UL : 2UL), EffectDurationKind.Finite, 10000, 1, null, false, at, mono - 10, ObservationQuality.Confirmed);
                }).ToList();
                var crowdHealing = new List<CombatHealEvent> { new($"h{frame}", CombatHealDirection.Cast, Key(2), new(0), new(500), null, null, false, at, mono - 5,
                    new(stream += 64), ObservationQuality.Confirmed) };
                return new(1, interest, Local, new(1, Source, session, new(1), new(++sequence), at, mono, entities, crowdDamage, crowdEffects, [], [], Healing: crowdHealing));
            }
            if (HealthOnly) bossHealth -= 1234;
            entities.AddRange(monsters.Take(count).Select((raw, index) => new CombatEntityObservation(Key(raw), "怪物" + raw, "怪物" + raw,
                CombatEntityRole.Monster, null, false, true, ObservationQuality.Confirmed,
                index == 0 ? new CombatBreakGauge(50, new(100), at, ObservationQuality.Confirmed) : null,
                index == 0 ? new CombatEntityHealth(new(bossHealth), new(10_000_000), at, "read") : null, index == 0 ? true : null)));
            var damage = new List<CombatDamageEvent>();
            if (boss)
                for (var i = 0; i < DamagePerFrame; i++)
                {
                    var source = i < DamagePerFrame * 2 / 3 ? Key(1) : Key(i % 2 == 0 ? 2UL : 3UL);
                    damage.Add(new($"d{frame}-{i}", Source, source, Key(monsters[0]), new((ulong)(20000 + i % 24)), new((ulong)(1000 + i * 7)),
                        CombatDamageKind.ActualHp, at, mono - 80 + i * 0.5, new(stream += 64), 0, i % 5 == 0, true, ObservationQuality.Confirmed,
                        SkillCasterKey: source, SkillResolved: true));
                }
            // 本机与一名队友身上 12 个效果实例轮流刷新（首次出现为施加）。
            var effects = Enumerable.Range(0, boss ? 6 : 2).Select(_ =>
            {
                var slot = effectCount % 12; var first = effectCount++ < 12;
                return new CombatEffectObservation(first ? CombatEffectChangeKind.Apply : CombatEffectChangeKind.Refresh, Key(slot % 2 == 0 ? 1UL : 2UL),
                    new(300 + slot), new(slot + 1), Key(1), EffectDurationKind.Finite, 10000, 1, null, false, at, mono - 10, ObservationQuality.Confirmed);
            }).ToList();
            var healing = boss ? new List<CombatHealEvent> { new($"h{frame}", CombatHealDirection.Cast, Key(2), new(0), new(500), null, null, false, at, mono - 5,
                new(stream += 64), ObservationQuality.Confirmed) } : null;
            return new(1, interest, Local, new(1, Source, session, new(1), new(++sequence), at, mono, entities, damage, effects, [], [], Healing: healing,
                MainTarget: HealthOnly ? new CombatMainTarget(Key(monsters[0]), at, mono) : null));
        }
        public byte[] Encode(CombatCaptureReport report) => JsonSerializer.SerializeToUtf8Bytes(new { combat = report }, ContractJson.Options);
    }

    /// <summary>
    /// 帧送出时点与发布时点配对：发布回调给出已应用的最后一帧单调时点，之前送出的帧都已可查询。
    /// 唤醒事件（摘要事件，以及宿主提供时的最新捕获事件）按事件所指的工作区修订找到发布时已应用的帧，记录送出到送达的延迟；
    /// 送出后 2.5 秒仍没有任何唤醒事件的帧记为未通知（桌面只能等兜底读取）。
    /// </summary>
    private sealed class LatencyTracker
    {
        public const double NotifyLimitMs = 2500;
        private readonly object gate = new();
        private readonly Queue<(double Mono, long Sent)> pending = new();
        private readonly Dictionary<string, Queue<(double Mono, long Sent)>> waiting = new(StringComparer.Ordinal) { ["summary"] = new(), ["live"] = new(), ["wake"] = new() };
        private readonly Dictionary<string, List<double>> delivered = new(StringComparer.Ordinal) { ["summary"] = [], ["live"] = [], ["wake"] = [] };
        private readonly Dictionary<string, long> unnotified = new(StringComparer.Ordinal) { ["summary"] = 0, ["live"] = 0, ["wake"] = 0 };
        private readonly List<double> published = [];
        private readonly List<long> publishedAt = [];
        private readonly Dictionary<ulong, (double Mono, long At)> revisions = [];
        /// <summary>唤醒事件先于最新捕获发布送达时（检查点先保存后发布），等该修订发布登记后再配对。</summary>
        private readonly Dictionary<(string Kind, ulong Revision), long> unresolved = [];
        public void Reset()
        {
            lock (gate)
            {
                pending.Clear(); published.Clear(); publishedAt.Clear(); unresolved.Clear();
                foreach (var key in waiting.Keys) { waiting[key].Clear(); delivered[key].Clear(); unnotified[key] = 0; }
            }
        }
        public void Sent(double mono)
        {
            lock (gate)
            {
                var now = Stopwatch.GetTimestamp(); pending.Enqueue((mono, now));
                foreach (var queue in waiting.Values) queue.Enqueue((mono, now));
            }
        }
        /// <summary>唤醒事件送达：kind 为 summary 或 live；同一帧的唤醒以先到的为准。</summary>
        public void Delivered(string kind, ulong revision, long now)
        {
            lock (gate)
            {
                if (!revisions.TryGetValue(revision, out var value)) { if (unresolved.Count < 4096) unresolved[(kind, revision)] = now; return; }
                Pair(kind, value.Mono, now);
            }
        }
        private void Pair(string kind, double mono, long now)
        {
            foreach (var name in new[] { kind, "wake" })
            {
                var queue = waiting[name];
                while (queue.TryPeek(out var item) && item.Mono <= mono)
                {
                    queue.Dequeue();
                    var elapsed = Stopwatch.GetElapsedTime(item.Sent, now).TotalMilliseconds;
                    if (elapsed > NotifyLimitMs) unnotified[name]++; else delivered[name].Add(elapsed);
                }
            }
        }
        private void Expire()
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var (name, queue) in waiting)
                while (queue.TryPeek(out var item) && Stopwatch.GetElapsedTime(item.Sent, now).TotalMilliseconds > NotifyLimitMs) { queue.Dequeue(); unnotified[name]++; }
        }
        public object EventSnapshot()
        {
            lock (gate)
            {
                Expire();
                return new
                {
                    reportToSummaryEventMs = Distribution(delivered["summary"]), reportToLiveEventMs = Distribution(delivered["live"]),
                    reportToWakeEventMs = Distribution(delivered["wake"]),
                    unnotifiedReports = new { summary = unnotified["summary"], live = unnotified["live"], wake = unnotified["wake"] }
                };
            }
        }
        public void Published(UInt64String revision, double mono)
        {
            var now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                while (pending.TryPeek(out var item) && item.Mono <= mono) { pending.Dequeue(); published.Add(Stopwatch.GetElapsedTime(item.Sent, now).TotalMilliseconds); publishedAt.Add(now); }
                revisions[revision.Value] = (mono, now);
                foreach (var key in unresolved.Keys.Where(key => key.Revision == revision.Value).ToArray())
                { Pair(key.Kind, mono, unresolved[key]); unresolved.Remove(key); }
                if (revisions.Count > 4096) foreach (var key in revisions.Keys.Order().Take(2048).ToArray()) revisions.Remove(key);
            }
        }
        /// <summary>修订对应的发布时点；修订未登记时取不超过它的最近一次。</summary>
        public long? PublishedAt(ulong revision)
        {
            lock (gate) return revisions.TryGetValue(revision, out var value) ? value.At : null;
        }
        public object? Snapshot() { lock (gate) return Distribution(published); }
        /// <summary>按 10 秒窗口的报告到快照可查询延迟（检查延迟是否随时间累积）。</summary>
        public object Windows()
        {
            lock (gate)
            {
                if (publishedAt.Count == 0) return Array.Empty<object>();
                var origin = publishedAt[0];
                return published.Select((value, index) => (Window: (int)(Stopwatch.GetElapsedTime(origin, publishedAt[index]).TotalSeconds / 10), Value: value))
                    .GroupBy(item => item.Window).Select(group => new { fromSecond = group.Key * 10, latency = Distribution(group.Select(item => item.Value).ToList()) }).ToArray();
            }
        }
    }

    internal static object? Distribution(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        double At(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new { count = sorted.Length, p50 = At(0.5), p95 = At(0.95), p99 = At(0.99), maximum = sorted[^1] };
    }

    /// <summary>
    /// SSE 订阅战斗主题；摘要事件按修订、最新捕获事件（宿主提供 combat.live.changed 时）按负载里的工作区修订找到对应的最新捕获发布时点，
    /// 记录发布到送达的延迟与送达总数。
    /// </summary>
    private sealed class EventListener(HttpClient http, LatencyTracker tracker)
    {
        /// <summary>宿主契约是否登记了最新捕获事件（改动前的宿主没有）。</summary>
        public static readonly bool LiveEvents = ContractEvents.Definitions.Any(definition => definition.Type == "combat.live.changed");
        private readonly object gate = new();
        private readonly List<double> latencies = [], liveLatencies = [];
        private long delivered, live;
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { lock (gate) { latencies.Clear(); liveLatencies.Clear(); delivered = 0; live = 0; } }
        public object Latencies()
        {
            lock (gate) return new { deliveredEvents = delivered, liveEvents = live, snapshotToSummaryEventMs = Distribution(latencies),
                snapshotToLiveEventMs = Distribution(liveLatencies), reports = tracker.EventSnapshot() };
        }
        public async Task RunAsync(CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiContract.Events.Path);
            request.Headers.Add("X-Event-Topics", "combat.summary.changed,combat.effects.changed,combat.encounter.closed,combat.observation.degraded,combat.measurement.changed" +
                (LiveEvents ? ",combat.live.changed" : ""));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Connected.TrySetResult();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            string? type = null;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal) && type is not null)
                {
                    var now = Stopwatch.GetTimestamp();
                    lock (gate) delivered++;
                    if (type is "combat.summary.changed" or "combat.live.changed")
                    {
                        using var document = JsonDocument.Parse(line[6..]);
                        var root = document.RootElement;
                        var live = type == "combat.live.changed";
                        var revisionText = live ? root.GetProperty("data").GetProperty("workspaceRevision").GetString()
                            : root.TryGetProperty("resourceRevision", out var value) ? value.GetString() : null;
                        if (ulong.TryParse(revisionText, out var revision))
                        {
                            if (tracker.PublishedAt(revision) is { } at)
                                lock (gate) (live ? liveLatencies : latencies).Add(Stopwatch.GetElapsedTime(at, now).TotalMilliseconds);
                            if (live) lock (gate) this.live++;
                            tracker.Delivered(live ? "live" : "summary", revision, now);
                        }
                    }
                }
                else if (line.Length == 0) type = null;
            }
        }
    }

    private readonly record struct Io(ulong ReadOps, ulong WriteOps, ulong OtherOps, ulong ReadBytes, ulong WriteBytes)
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Counters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out Counters counters);
        public static Io Read(Process process) => GetProcessIoCounters(process.Handle, out var value)
            ? new(value.ReadOperationCount, value.WriteOperationCount, value.OtherOperationCount, value.ReadTransferCount, value.WriteTransferCount) : default;
        public static Io operator -(Io a, Io b) => new(a.ReadOps - b.ReadOps, a.WriteOps - b.WriteOps, a.OtherOps - b.OtherOps, a.ReadBytes - b.ReadBytes, a.WriteBytes - b.WriteBytes);
    }
}
