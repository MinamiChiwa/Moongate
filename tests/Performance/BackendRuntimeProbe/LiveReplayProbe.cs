using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 实采样本驱动的宿主负载：按 tw-20261110 协议把 packets.jsonl 解析成战斗帧，以 Worker 相同的 100 ms 合并规则、按样本原时点送入
/// 进程内 mock 宿主的战斗观察；可选模拟桌面前端（SSE 唤醒 + 每秒例行刷新，与 LiveBackend 的读取组合相同）。
/// 报告宿主进程 CPU、写事务按回调分类的次数与耗时、outbox 事件数、进程 IO 计数与 HTTP 请求数。
/// --speed 按倍率压缩样本时间（加压）；--merge-ms 为 Worker 合批窗口（默认 100 ms）；--serve 连接文件：帧时点对齐本机单调时钟
/// （抓包时点即本机 QPC 毫秒，与实时抓包相同），把宿主地址与令牌写入连接文件，等到“连接文件.ready”出现（桌面
/// <c>DesktopRefreshBenchmark.MeasuresLiveHostLatency</c> 连上后写出）再解析样本、开始观察与回放，结束后删除连接文件。
/// --history 条数 [--history-kb 正文大小]：观察开始后写入这么多条已结束的观察与战斗记录占位（工作区与记录正文各默认 300 KB，
/// 相当于用过一段时间的数据库），
/// 用于量出快照读取的数据库查询随历史规模的开销。
/// </summary>
internal static class LiveReplayProbe
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length < 3 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("参数：repository sampleDirectory report [--client] [--tail 秒] [--limit 秒] [--speed 倍率] [--merge-ms 毫秒] [--serve 连接文件] [--history 条数] [--history-kb 正文 KiB] [--segments 条数 [--segments-same-whole]]，路径须为绝对路径");
        var repository = Path.GetFullPath(args[0]); var sample = Path.GetFullPath(args[1]); var report = Path.GetFullPath(args[2]);
        if (!report.StartsWith(Path.Combine(repository, "artifacts", "reports") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("报告须位于 artifacts/reports");
        var client = args.Contains("--client");
        var tail = Option(args, "--tail", 30); var limit = Option(args, "--limit", 600); var merge = Option(args, "--merge-ms", 100);
        var history = Option(args, "--history", 0); var historyKiB = Option(args, "--history-kb", 300); var segments = Option(args, "--segments", 0); var sameWhole = args.Contains("--segments-same-whole");
        if (history > 100) throw new ArgumentException("--history 至多 100（战斗记录上限）");
        var speedAt = Array.IndexOf(args, "--speed");
        var speed = speedAt < 0 ? 1d : double.Parse(args[speedAt + 1], System.Globalization.CultureInfo.InvariantCulture);
        if (speed is < 0.1 or > 20 || merge is < 1 or > 100) throw new ArgumentException("--speed 超出 0.1–20 或 --merge-ms 超出 1–100");
        var serveAt = Array.IndexOf(args, "--serve");
        var serve = serveAt < 0 ? null : Path.GetFullPath(args[serveAt + 1]);
        if (serve is not null && !serve.StartsWith(Path.Combine(repository, "artifacts", "tmp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("连接文件须位于 artifacts/tmp");
        var packets = Path.Combine(sample, "packets.jsonl");
        if (!File.Exists(packets) || new FileInfo(packets).Length > 64 * 1024 * 1024) throw new ArgumentException("样本缺少 packets.jsonl 或超过 64 MiB");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var temporary = Path.Combine(repository, "artifacts", "tmp", "live-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var secret = LocalAuthService.NewSecret();
            var source = new SampleSource(packets, limit, speed, merge, serve is not null);
            await using var app = BackendApplication.Build(new(0, secret, new HashSet<string>()),
                storage: new(Path.Combine(temporary, "bootstrap"), true, PortableRoot: temporary), mock: true, combatSource: _ => source);
            await app.StartAsync();
            var store = app.Services.GetRequiredService<SqliteStore>();
            var writes = new Dictionary<string, WriteStats>(StringComparer.Ordinal);
            store.WriteTimingObserver = timing =>
            {
                lock (writes)
                {
                    if (!writes.TryGetValue(timing.Callback, out var stats)) writes[timing.Callback] = stats = new();
                    stats.Count++; stats.CallbackMs += timing.CallbackMs; stats.CommitMs += timing.CommitMs; stats.SqlCommands += timing.SqlCommands;
                }
            };
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new(address) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", secret);
            var binding = (await http.GetFromJsonAsync<GameBindingSnapshot>(ApiContract.GameBinding.Path, ContractJson.Options))!;
            if (serve is not null)
            {
                await File.WriteAllTextAsync(serve, JsonSerializer.Serialize(new { address, secret }));
                var ready = Stopwatch.StartNew();
                while (!File.Exists(serve + ".ready"))
                {
                    if (ready.Elapsed > TimeSpan.FromSeconds(180)) throw new TimeoutException("桌面未连接");
                    await Task.Delay(200);
                }
            }
            source.Prepare(binding.Binding!.Session.Id);
            using var start = new HttpRequestMessage(HttpMethod.Post, ApiContract.StartCombatObservation.Path)
            { Content = JsonContent.Create(new CombatObservationRequest(binding.Binding.Session.Id), options: ContractJson.Options) };
            start.Headers.Add("Idempotency-Key", "live-replay-start");
            (await http.SendAsync(start)).EnsureSuccessStatusCode();
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
            // 历史占位在本次观察写入之后加入（更新时刻更早，快照仍选本次观察）。
            if (history > 0) await SeedHistoryAsync(store, history, historyKiB);
            if (segments > 0) await SeedSegmentsAsync(store, segments, sameWhole);
            using var stop = new CancellationTokenSource();
            var emulator = client && serve is null ? new DesktopEmulator(http) : null;
            var clientTask = emulator?.RunAsync(stop.Token) ?? Task.CompletedTask;
            var outboxBefore = MaxSequence(store);
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime; var io = Io.Read(process); var allocated = GC.GetTotalAllocatedBytes();
            var gen2 = GC.CollectionCount(2); var clock = Stopwatch.StartNew();
            await source.Finished.Task;
            var replayMs = clock.Elapsed.TotalMilliseconds; process.Refresh();
            var replayCpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds; var replayIo = Io.Read(process) - io;
            var replayOutbox = MaxSequence(store) - outboxBefore; var replayWrites = Snapshot(writes);
            var replayRequests = emulator?.Requests ?? 0;
            var tailCpuBefore = process.TotalProcessorTime; var tailIo = Io.Read(process); var tailOutboxBefore = MaxSequence(store);
            var tailRequestsBefore = emulator?.Requests ?? 0; lock (writes) writes.Clear();
            var tailClock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(tail));
            process.Refresh();
            var tailMs = tailClock.Elapsed.TotalMilliseconds; var tailCpu = (process.TotalProcessorTime - tailCpuBefore).TotalMilliseconds;
            var tailIoDelta = Io.Read(process) - tailIo; var tailOutbox = MaxSequence(store) - tailOutboxBefore; var tailWrites = Snapshot(writes);
            var totalAllocated = GC.GetTotalAllocatedBytes() - allocated; var totalGen2 = GC.CollectionCount(2) - gen2;
            await stop.CancelAsync();
            try { await clientTask; } catch (OperationCanceledException) { }
            store.WriteTimingObserver = null;
            var output = new
            {
                schemaVersion = 1, evidenceKind = "backend-live-replay", measuredAtUtc = DateTimeOffset.UtcNow,
                environment = new
                {
                    os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
                    processor = OperatingSystem.IsWindows() ? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString() : null,
#if DEBUG
                    configuration = "Debug",
#else
                    configuration = "Release",
#endif
                },
                sample = new { directory = Path.GetFileName(sample), speed, mergeMs = merge, source.InputPackets, source.ParsedFrames, source.Batches, source.Damage, source.Effects, source.Reconciliations,
                    source.EntityObservations, source.SampleSpanMs, parseMs = source.ParseMs },
                protocol = new
                {
                    host = "进程内 mock 宿主（真实 SQLite WAL+FULL、outbox、SSE），战斗来源为样本帧；CPU 为整个探针进程（样本已预解析，发送线程只做定时）",
                    client = client ? "模拟桌面前端：SSE 订阅战斗事件唤醒，每轮至少间隔 250 ms、无事件时每秒一轮，读取组合同 LiveBackend.RefreshAsync 的战斗部分" : "无前端",
                    tailSeconds = tail
                },
                replay = Phase(replayMs, replayCpu, replayIo, replayOutbox, replayWrites, replayRequests),
                tail = Phase(tailMs, tailCpu, tailIoDelta, tailOutbox, tailWrites, (emulator?.Requests ?? 0) - tailRequestsBefore),
                gc = new { allocatedBytes = totalAllocated, gen2Collections = totalGen2 }
            };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(output, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
            Console.WriteLine(report);
            if (serve is not null) { File.Delete(serve); File.Delete(serve + ".ready"); }
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
    /// 写入 <paramref name="count"/> 条已结束的观察（terminal=1，更新时刻早于本次观察）与各自引用它的进行中战斗记录占位（closed=0，
    /// 历史分页与清理不读取），工作区与记录正文各约 300 KB 的合法 JSON：只用于让快照读取的观察选择与存储统计面对真实规模的表。
    /// </summary>
    private static async Task SeedHistoryAsync(SqliteStore store, int count, int kib)
    {
        var padding = "{\"pad\":\"" + new string('x', kib * 1024) + "\"}";
        await store.WriteAsync(tx =>
        {
            for (var i = 0; i < count; i++)
            {
                tx.Execute("INSERT INTO combat_observations(id,owner,game_session,revision,workspace,terminal,updated_at,encounter_id) VALUES($id,'owner','seed','1',$w,1,$at,$e)",
                    ("$id", $"seed-observation-{i}"), ("$w", padding), ("$at", 1_000_000L + i), ("$e", $"seed-encounter-{i}"));
                tx.Execute("INSERT INTO combat_encounters(id,owner,observation_id,revision,closed,quality,favorite,started_at,document,bytes,timeline_bytes) VALUES($id,'owner',$o,'1',0,'confirmed',0,$at,$d,$b,0)",
                    ("$id", $"seed-encounter-{i}"), ("$o", $"seed-observation-{i}"), ("$at", 1_000_000L + i), ("$d", padding), ("$b", (long)padding.Length));
            }
            return 0;
        });
    }

    /// <summary>
    /// 本次观察里再加 <paramref name="count"/> 段已结束的分段：等本次观察的第一段写入后按它复制（新 ID、整场序号 9999、已结束），
    /// 相当于同一次观测里此前打过的许多段（整场范围查询要读出本次观察的全部分段再按整场筛选）。<paramref name="sameWhole"/> 为真时
    /// 复制的分段保留原整场序号并连同时间线页一起复制，即同一整场里此前打过的许多段（整场范围与首击要读它们的时间线）。
    /// </summary>
    private static async Task SeedSegmentsAsync(SqliteStore store, int count, bool sameWhole)
    {
        var wait = Stopwatch.StartNew(); string? source = null;
        while (wait.Elapsed < TimeSpan.FromSeconds(120) && (source = store.ReadScalar(
            "SELECT e.id FROM combat_encounters e JOIN combat_observations o ON o.id=e.observation_id WHERE o.terminal=0 AND e.id NOT LIKE 'seed-%' LIMIT 1") as string) is null)
            await Task.Delay(200);
        if (source is null) throw new TimeoutException("本次观察没有写入分段");
        // 等第一段写入若干时间线页再复制。
        while (sameWhole && wait.Elapsed < TimeSpan.FromSeconds(120) &&
            Convert.ToInt64(store.ReadScalar("SELECT count(*) FROM combat_timeline_pages WHERE encounter_id=$id", ("$id", source))) < 4)
            await Task.Delay(500);
        await store.WriteAsync(tx =>
        {
            for (var i = 0; i < count; i++)
            {
                if (sameWhole)
                {
                    tx.Execute("INSERT INTO combat_encounters(id,owner,observation_id,revision,closed,quality,favorite,started_at,document,bytes,timeline_bytes) " +
                        "SELECT $id,owner,observation_id,revision,1,quality,0,started_at-1000000+$i,json_set(document,'$.id',$id,'$.summary.encounterId',$id,'$.summary.final',json('true'),'$.startedMonotonicMs',-1000000+$i),bytes,timeline_bytes FROM combat_encounters WHERE id=$source",
                        ("$id", $"seed-segment-{i}"), ("$i", (long)i), ("$source", source));
                    tx.Execute("INSERT INTO combat_timeline_pages(encounter_id,first_index,last_index,document,bytes) SELECT $id,first_index,last_index,document,bytes FROM combat_timeline_pages WHERE encounter_id=$source",
                        ("$id", $"seed-segment-{i}"), ("$source", source));
                    continue;
                }
                tx.Execute("INSERT INTO combat_encounters(id,owner,observation_id,revision,closed,quality,favorite,started_at,document,bytes,timeline_bytes) " +
                    "SELECT $id,owner,observation_id,revision,1,quality,0,started_at-1000000+$i,json_set(document,'$.id',$id,'$.summary.encounterId',$id,'$.segment.whole',9999,'$.summary.final',json('true')),bytes,0 FROM combat_encounters WHERE id=$source",
                    ("$id", $"seed-segment-{i}"), ("$i", (long)i), ("$source", source));
            }
            return 0;
        });
    }

    private static int Option(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return fallback;
        var value = int.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture);
        if (value is < 0 or > 3600) throw new ArgumentException(name + " 超出 0–3600");
        return value;
    }
    private static long MaxSequence(SqliteStore store) => Convert.ToInt64(store.ReadScalar("SELECT coalesce(max(sequence),0) FROM core_outbox"));
    private static object Phase(double ms, double cpuMs, Io io, long outbox, IReadOnlyDictionary<string, WriteStats> writes, long requests)
    {
        var seconds = ms / 1000;
        return new
        {
            elapsedMs = ms, cpuMs, coresAverage = cpuMs / ms, wholeMachinePercent = cpuMs / ms / Environment.ProcessorCount * 100,
            transactionsPerSecond = writes.Values.Sum(x => x.Count) / seconds, outboxEventsPerSecond = outbox / seconds,
            httpRequestsPerSecond = requests / seconds,
            io = new { readOpsPerSecond = io.ReadOps / seconds, writeOpsPerSecond = io.WriteOps / seconds, otherOpsPerSecond = io.OtherOps / seconds,
                readBytesPerSecond = io.ReadBytes / seconds, writeBytesPerSecond = io.WriteBytes / seconds },
            transactions = writes.OrderByDescending(x => x.Value.Count).Select(x => new
            {
                callback = x.Key, count = x.Value.Count, perSecond = x.Value.Count / seconds, callbackMsAverage = x.Value.CallbackMs / x.Value.Count,
                commitMsAverage = x.Value.CommitMs / x.Value.Count, sqlCommandsAverage = (double)x.Value.SqlCommands / x.Value.Count
            }).ToArray()
        };
    }
    private static Dictionary<string, WriteStats> Snapshot(Dictionary<string, WriteStats> writes)
    {
        lock (writes) return writes.ToDictionary(x => x.Key, x => new WriteStats { Count = x.Value.Count, CallbackMs = x.Value.CallbackMs, CommitMs = x.Value.CommitMs, SqlCommands = x.Value.SqlCommands });
    }

    private sealed class WriteStats { public long Count, SqlCommands; public double CallbackMs, CommitMs; }

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

    /// <summary>样本帧来源：按 Worker 规则（同代次、相距 100 ms 内、至多 256 条）合并成批，按样本时点送出一次后结束。</summary>
    private sealed class SampleSource(string packets, int limitSeconds, double speed, int mergeMs, bool aligned) : ICombatObservationSource
    {
        private List<CombatFrame> batches = [];
        private CombatEntityObservation? local;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long InputPackets, ParsedFrames, Damage, Effects, Reconciliations, EntityObservations;
        public int Batches => batches.Count;
        public double SampleSpanMs, ParseMs;
        private DateTimeOffset origin;
        /// <summary>帧时点的零点：对齐本机单调时钟时为 Prepare 时的 QPC 毫秒加 15 秒（与 <see cref="origin"/> 同一时刻），否则为 0。</summary>
        private double zero;
        private static double QpcMs => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
        private bool Aligned => aligned;

        public void Prepare(string gameSession)
        {
            var timer = Stopwatch.StartNew();
            origin = DateTimeOffset.UtcNow.AddSeconds(15);
            zero = aligned ? QpcMs + 15_000 : 0;
            var frames = new List<CombatFrame>(); var owners = new List<string>();
            ulong generation = 0;
            var streams = new Dictionary<string, (TwCombatObserver Observer, GameTcpStream Stream, double[] Now, uint?[] End)>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(packets))
            {
                InputPackets++;
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                var id = row.GetProperty("connectionId").GetString()!;
                var sampleNow = row.GetProperty("monotonicMs").GetDouble();
                if (sampleNow > limitSeconds * 1000d) break;
                var now = zero + sampleNow / speed;
                if (!streams.TryGetValue(id, out var connection))
                {
                    if (streams.Count >= 64) continue;
                    var observer = new TwCombatObserver(gameSession, Tw20261110CombatProtocol.CombatProfile.Source, frame => { frames.Add(frame); owners.Add(id); }, () => ++generation);
                    observer.Configure(new(true, true, true, true, true, true));
                    var clock = new double[1]; GameTcpStream? stream = null;
                    stream = new((code, body) =>
                    {
                        if (Tw20261110CombatProtocol.ProtocolsV4.Contains(code))
                            observer.Accept(code, body, stream!.FrameStreamOffset, stream.FrameWireLength, clock[0], origin.AddMilliseconds(clock[0] - zero), stream.FrameEncoding);
                    }, reason => observer.Reset("combat.streamGap", clock[0], origin.AddMilliseconds(clock[0] - zero)), Tw20261110CombatProtocol.StreamProfile);
                    streams[id] = connection = (observer, stream, clock, new uint?[1]);
                }
                if (now < connection.Now[0]) now = connection.Now[0];
                connection.Now[0] = now;
                var bytes = Convert.FromBase64String(row.GetProperty("payloadBase64").GetString()!);
                var sequence = row.GetProperty("tcpSequence").GetUInt32();
                var flags = row.GetProperty("tcpFlags").GetByte();
                if ((flags & 2) != 0) { connection.Stream.Restart("tcp.syn"); sequence = unchecked(sequence + 1); connection.End[0] = null; }
                if ((flags & 16) != 0 && bytes.Length == 1 && connection.End[0] is { } end && sequence == unchecked(end - 1)) continue;
                var next = unchecked(sequence + (uint)bytes.Length);
                if (connection.End[0] is null || unchecked((int)(next - connection.End[0]!.Value)) > 0) connection.End[0] = next;
                for (var offset = 0; offset < bytes.Length; offset += 65536)
                    connection.Stream.Feed(unchecked(sequence + (uint)offset), bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)), now, now);
                connection.Observer.Tick(now, origin.AddMilliseconds(now - zero));
                if ((flags & 5) != 0) connection.Stream.Restart("tcp.closed");
            }
            foreach (var connection in streams.Values) connection.Observer.Finish(connection.Now[0] + 1501, origin.AddMilliseconds(connection.Now[0] + 1501 - zero));
            ParsedFrames = frames.Count;
            // 与 Worker 相同只送战斗主连接（伤害最多的连接）的帧；与宿主相同只跟随不降低的代次；从第一份含已确认本机身份的帧开始送出。
            var primary = frames.Select((frame, index) => (frame, owner: owners[index])).GroupBy(x => x.owner)
                .MaxBy(group => group.Sum(x => x.frame.Damage.Count))?.Key;
            var ordered = frames.Where((_, index) => owners[index] == primary).OrderBy(x => x.MonotonicMs).ToList();
            var first = ordered.FindIndex(x => x.Entities.Any(e => e.Role.KnownValue == CombatEntityRole.LocalPlayer && e.Quality.KnownValue == ObservationQuality.Confirmed));
            if (first < 0) throw new InvalidDataException("样本中没有已确认的本机身份");
            local = ordered[first].Entities.First(e => e.Role.KnownValue == CombatEntityRole.LocalPlayer && e.Quality.KnownValue == ObservationQuality.Confirmed);
            ulong current = 0;
            foreach (var frame in ordered.Skip(first))
            {
                if (frame.Generation.Value < current) continue;
                current = frame.Generation.Value;
                Damage += frame.Damage.Count; Effects += frame.Effects.Count; Reconciliations += frame.Reconciliations.Count; EntityObservations += frame.Entities.Count;
                if (batches.Count > 0 && batches[^1] is var previous && previous.Generation == frame.Generation && frame.Sequence.Value > previous.Sequence.Value &&
                    frame.MonotonicMs - batchStart <= mergeMs && Records(previous) + Records(frame) <= 256)
                {
                    batches[^1] = frame with
                    {
                        Damage = [.. previous.Damage, .. frame.Damage], Effects = [.. previous.Effects, .. frame.Effects],
                        Reconciliations = [.. previous.Reconciliations, .. frame.Reconciliations], Gaps = [.. previous.Gaps, .. frame.Gaps],
                        Healing = previous.Healing is null && frame.Healing is null ? null : [.. previous.Healing ?? [], .. frame.Healing ?? []],
                        CombatStates = previous.CombatStates is null && frame.CombatStates is null ? null : [.. previous.CombatStates ?? [], .. frame.CombatStates ?? []]
                    };
                }
                else { batches.Add(frame); batchStart = frame.MonotonicMs; }
            }
            SampleSpanMs = batches[^1].MonotonicMs - batches[0].MonotonicMs;
            ParseMs = timer.Elapsed.TotalMilliseconds;
        }
        private double batchStart;
        private static int Records(CombatFrame value) => Math.Max(1, value.Entities.Count + value.Damage.Count + value.Effects.Count + value.Gaps.Count +
            (value.Healing?.Count ?? 0) + (value.CombatStates?.Count ?? 0) + value.Reconciliations.Sum(x => Math.Max(1, x.Effects.Count)));

        public CombatCapabilities Capabilities(string? session) => new(!aligned,
            new[] { "combat.damage.local", "combat.effects.self", "combat.effects.monster" }.Select(id =>
                new CombatCapability(id, CapabilityStatus.Available, Tw20261110CombatProtocol.CombatProfile.Source, null, null, null)).ToArray());
        public ValueTask<ICombatObservationSubscription> OpenAsync(string id, CombatObservationRequest request, CancellationToken ct) =>
            ValueTask.FromResult<ICombatObservationSubscription>(new Subscription(this));

        private sealed class Subscription(SampleSource owner) : ICombatObservationSubscription
        {
            public ObservationSource Source => Tw20261110CombatProtocol.CombatProfile.Source;
            public CombatEntityObservation LocalEntity => owner.local ?? throw new InvalidOperationException("样本未准备");
            public double ClockRate => 1;
            public bool HealingSupported => true;
            public async IAsyncEnumerable<CombatFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
            {
                var wait = owner.Aligned ? TimeSpan.FromMilliseconds(owner.batches[0].MonotonicMs - QpcMs)
                    : owner.origin.AddMilliseconds(owner.batches[0].MonotonicMs) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                owner.Started.TrySetResult();
                var clock = Stopwatch.StartNew(); var first = owner.batches[0].MonotonicMs;
                try
                {
                    foreach (var batch in owner.batches)
                    {
                        var due = batch.MonotonicMs - first - clock.Elapsed.TotalMilliseconds;
                        if (due > 1) await Task.Delay(TimeSpan.FromMilliseconds(due), ct);
                        yield return batch;
                    }
                    owner.Finished.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally { owner.Finished.TrySetResult(); }
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// 桌面前端读取的近似：SSE 订阅与 LiveBackend 相同的战斗事件主题，事件唤醒刷新（两轮至少相隔 250 ms），无事件时每秒一轮；
    /// 每轮并行读取绑定、输入占用、战斗快照与历史，再按快照读取当前分段时间线首页与第一个怪物的效果。
    /// </summary>
    private sealed class DesktopEmulator(HttpClient http)
    {
        private long requests;
        public long Requests => Interlocked.Read(ref requests);
        private readonly SemaphoreSlim wake = new(0, 1);
        public async Task RunAsync(CancellationToken ct)
        {
            var events = ListenAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var round = Stopwatch.StartNew();
                await RefreshAsync(ct);
                try { await wake.WaitAsync(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { break; }
                if (round.Elapsed < TimeSpan.FromMilliseconds(250))
                    try { await Task.Delay(TimeSpan.FromMilliseconds(250) - round.Elapsed, ct); } catch (OperationCanceledException) { break; }
            }
            try { await events; } catch (Exception) when (ct.IsCancellationRequested) { }
        }
        private async Task ListenAsync(CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiContract.Events.Path);
            request.Headers.Add("X-Event-Topics", "combat.summary.changed,combat.effects.changed,combat.encounter.closed,combat.observation.degraded,combat.measurement.changed,shortcut.triggered");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
                if (line.StartsWith("event: combat", StringComparison.Ordinal))
                    try { wake.Release(); } catch (SemaphoreFullException) { }
        }
        private async Task<T?> Get<T>(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref requests);
            using var response = await http.GetAsync(path, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, ct) : default;
        }
        private async Task<T?> Post<T>(string path, object body, CancellationToken ct)
        {
            Interlocked.Increment(ref requests);
            using var response = await http.PostAsJsonAsync(path, body, ContractJson.Options, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, ct) : default;
        }
        private async Task RefreshAsync(CancellationToken ct)
        {
            try
            {
                var binding = Get<GameBindingSnapshot>(ApiContract.GameBinding.Path, ct);
                var ownership = Get<GameInputOwnershipSnapshot>(ApiContract.GameInputOwnership.Path, ct);
                var snapshot = Get<CombatSnapshot>(ApiContract.CombatSnapshot.Path, ct);
                var history = Get<CombatEncounterPage>(ApiContract.CombatEncounters.Path, ct);
                await Task.WhenAll(binding, ownership, snapshot, history);
                var workspace = snapshot.Result?.Data;
                if (workspace?.Encounter is { } encounter)
                    await Post<CombatTimelinePage>(ApiContract.QueryCombatTimeline.Path.Replace("{id}", encounter.Id), new CombatTimelineQuery(PageSize: 1), ct);
                await Get<CombatMeasurementState>(ApiContract.CombatMeasurement.Path, ct);
                if (workspace?.Entities.FirstOrDefault(x => x.Role.KnownValue == CombatEntityRole.Monster && x.InScope) is { } monster)
                    await Get<CombatEntityEffects>(ApiContract.CombatEntityEffects.Path.Replace("{id}", monster.Id), ct);
            }
            catch (HttpRequestException) { }
            catch (JsonException) { }
        }
    }
}
