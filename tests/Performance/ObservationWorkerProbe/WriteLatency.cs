using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.ObservationWorkerProbe;

/// <summary>
/// 战斗写出的延迟与高流量压力：合成战斗报告按真实时间送入 <see cref="ObservationCaptureRun"/> 的捕获上下文（与解析器发布报告同一入口），
/// 经写出队列、合批与速率上限写到计数目标。目标按宿主 WorkerSession 的读取规则（每个 1 秒窗口至多 256 条消息）核对，
/// 记录每份报告入队到写出的延迟（整段与每 10 秒窗口）、每秒消息数、合批记录数，以及因写出容量产生的 combat.transportCapacity 缺口。
/// </summary>
internal static class WriteLatency
{
    internal sealed record Scenario(string Name, string Description, int ReportsPerSecond, int Damage, int Effects, int Healing, int Entities, int Seconds);

    internal static readonly Scenario[] Default =
    [
        new("single", "零星单发：每秒 2 份报告、每份 1 条伤害，16 个实体", 2, 1, 0, 0, 16, 10),
        new("boss", "首领战：每秒 50 份报告、每份 6 条伤害与 2 条效果，40 个实体", 50, 6, 2, 0, 40, 20),
        new("crowd-70", "千人同屏量级（写出容量约七成）：每秒 780 份报告、每份 2 条伤害、6 条效果、1 条治疗（每秒约 7,000 条记录），128 个实体", 780, 2, 6, 1, 128, 120),
        new("crowd", "千人同屏量级（接近写出容量）：每秒 1,000 份报告、组成同上（每秒 9,000 条记录），128 个实体", 1000, 2, 6, 1, 128, 120),
        new("overload", "超出写出容量：每秒 2,000 份报告、组成同上（每秒 18,000 条记录），128 个实体", 2000, 2, 6, 1, 128, 30)
    ];

    public static async Task<JsonNode> RunAsync(IReadOnlyList<Scenario> scenarios)
    {
        var results = new JsonArray();
        foreach (var scenario in scenarios) results.Add(await RunAsync(scenario));
        return results;
    }

    private static double Now() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);

    private static async Task<JsonNode> RunAsync(Scenario scenario)
    {
        var gate = new object();
        var enqueued = new Queue<(ulong Sequence, double At)>();
        var latencies = new List<(double At, double Ms)>();
        long published = 0, publishedRecords = 0, sentRecords = 0, lostRecords = 0, lossBatches = 0, mergedReports = 0, batches = 0;
        var peer = new RatePeer();
        var generator = new Generator(scenario);
        var source = new ReplaySource(async (context, ct) =>
        {
            var interval = 1000d / scenario.ReportsPerSecond;
            var start = Now(); var next = 0L; var total = (long)scenario.ReportsPerSecond * scenario.Seconds;
            while (!ct.IsCancellationRequested && next < total)
            {
                var due = start + next * interval;
                var now = Now();
                if (due > now) { await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, due - now)), ct).ConfigureAwait(false); continue; }
                // 已到期的报告一并发布（与读取循环一次读出多个包相同）。
                while (next < total && start + next * interval <= Now())
                {
                    var report = generator.Next();
                    Interlocked.Increment(ref published); Interlocked.Add(ref publishedRecords, generator.EventsPerReport);
                    context.Publish(report);
                    next++;
                }
            }
            // 写出排空：捕获结束后写出循环写完剩余队列才返回。
        });
        var run = Replay.CreateRun(peer, source, "probe-write");
        run.Enqueued = report =>
        {
            if (report is CombatCaptureReport value) lock (gate) enqueued.Enqueue((value.Frame.Sequence.Value, Now()));
        };
        run.Sent = report =>
        {
            if (report is not CombatCaptureBatch batch) return;
            var now = Now();
            lock (gate)
            {
                batches++;
                if (!batch.Encoded)
                {
                    lossBatches++;
                    lostRecords += batch.Head.Frame.Gaps.Where(gap => gap.Reason == "combat.transportCapacity").Sum(gap => (long)gap.DroppedEvents.Value);
                    return;
                }
                mergedReports += batch.Parts.Count; sentRecords += batch.Events;
                var head = batch.Head.Frame.Sequence.Value;
                while (enqueued.TryPeek(out var item) && item.Sequence <= head) { enqueued.Dequeue(); latencies.Add((now, now - item.At)); }
            }
        };
        using var stop = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; var wall = Stopwatch.StartNew();
        await run.RunAsync(stop, lifetime.Token);
        process.Refresh();
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds; var wallMs = wall.Elapsed.TotalMilliseconds;
        var origin = latencies.Count > 0 ? latencies[0].At : 0;
        var windows = latencies.GroupBy(item => (int)((item.At - origin) / 10_000))
            .Select(group => new { fromSecond = group.Key * 10, latencyMs = Percentiles(group.Select(item => item.Ms).ToList()) }).ToArray();
        var perSecond = peer.PerSecond();
        return JsonSerializer.SerializeToNode(new
        {
            scenario = scenario.Name, description = scenario.Description, seconds = scenario.Seconds, wallMs = Math.Round(wallMs),
            reports = published, reportRecords = publishedRecords, recordsPerSecond = scenario.ReportsPerSecond * generator.EventsPerReport,
            sent = new { messages = peer.Messages, combatBatches = batches, mergedReports, records = sentRecords, reportsPerBatch = batches - lossBatches == 0 ? 0 : Math.Round(mergedReports / (double)(batches - lossBatches), 2) },
            transportCapacity = new { lossBatches, droppedRecords = lostRecords },
            unsentReports = enqueued.Count,
            messagesPerSecond = new { average = Math.Round(peer.Messages / (wallMs / 1000), 1), busiestSecond = perSecond.Max, hostWindowViolations = peer.Violations },
            enqueueToWriteMs = Percentiles(latencies.Select(item => item.Ms).ToList()),
            enqueueToWriteByWindowMs = windows,
            processCpuMs = Math.Round(cpuMs), processCores = Math.Round(cpuMs / wallMs, 3)
        }, ContractJson.Options)!;
    }

    internal static object? Percentiles(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * (sorted.Length - 1)))];
        return new { count = sorted.Length, p50 = Math.Round(At(0.5), 2), p95 = Math.Round(At(0.95), 2), p99 = Math.Round(At(0.99), 2), max = Math.Round(sorted[^1], 2) };
    }

    /// <summary>合成报告：同一兴趣与代次、序号递增，事件时点在帧时点前 50 ms 内；实体快照含本机与怪物（第一个怪物带 HP）。</summary>
    private sealed class Generator(Scenario scenario)
    {
        private const string Session = "probe-write";
        private static readonly ObservationSource Source = Tw20261110CombatProtocol.CombatProfile.Source;
        private ulong sequence, stream = 1000, effects, health = 10_000_000;
        public int EventsPerReport => scenario.Damage + scenario.Effects + scenario.Healing;
        private static EntityKey Key(ulong raw) => new(Session, new(1), new(raw), null);
        private static readonly CombatEntityObservation Local = new(Key(1), "本机", "本机", CombatEntityRole.LocalPlayer, null, false, true, ObservationQuality.Confirmed);
        public CombatCaptureReport Next()
        {
            var mono = Now(); var at = DateTimeOffset.UtcNow; sequence++;
            health -= 100;
            var entities = new List<CombatEntityObservation>(scenario.Entities) { Local };
            for (var i = 1; i < scenario.Entities; i++)
                entities.Add(new(Key(1000 + (ulong)i), "怪物" + i, "怪物" + i, CombatEntityRole.Monster, null, false, true, ObservationQuality.Confirmed,
                    Health: i == 1 ? new CombatEntityHealth(new(health), new(10_000_000), at, "read") : null));
            var damage = Enumerable.Range(0, scenario.Damage).Select(i => new CombatDamageEvent($"d{sequence}-{i}", Source, Key(1), Key(1001),
                new(20000), new((ulong)(1000 + i)), CombatDamageKind.ActualHp, at, mono - 40 + i, new(stream += 64), 0, false, true,
                ObservationQuality.Confirmed, SkillCasterKey: Key(1), SkillResolved: true)).ToList();
            var effectList = Enumerable.Range(0, scenario.Effects).Select(_ =>
            {
                var slot = effects++ % 500;
                return new CombatEffectObservation(CombatEffectChangeKind.Refresh, Key(1000 + 1 + slot % (ulong)Math.Max(1, scenario.Entities - 1)),
                    new(300 + slot / 125), new(slot + 1), Key(1), EffectDurationKind.Finite, 10000, 1, null, false, at, mono - 10, ObservationQuality.Confirmed);
            }).ToList();
            var healing = scenario.Healing == 0 ? null : Enumerable.Range(0, scenario.Healing).Select(i => new CombatHealEvent($"h{sequence}-{i}", CombatHealDirection.Cast,
                Key(1), new(0), new(500), null, null, false, at, mono - 5, new(stream += 64), ObservationQuality.Confirmed)).ToList();
            return new(1, new string('c', 32), Local, new(1, Source, Session, new(1), new(sequence), at, mono, entities, damage, effectList, [], [], Healing: healing));
        }
    }

    /// <summary>写出目标：按宿主 WorkerSession 的读取规则计数（窗口从上次重置起满 1 秒重置，窗口内第 257 条即违规），并记录每条消息的时刻。</summary>
    private sealed class RatePeer : IObservationPeer
    {
        private readonly object gate = new();
        private readonly List<double> times = [];
        private double window = double.NegativeInfinity;
        private int count;
        public long Messages, Violations;
        private void Count()
        {
            lock (gate)
            {
                var now = Now();
                if (now - window >= 1000) { window = now; count = 0; }
                if (++count > 256) Violations++;
                times.Add(now); Messages++;
            }
        }
        public Task SendAsync(string kind, object payload, WorkerFrame? scope, CancellationToken ct)
        {
            _ = JsonSerializer.SerializeToUtf8Bytes(payload, WorkerProtocol.Json);
            Count(); return Task.CompletedTask;
        }
        public Task SendEncodedAsync(string kind, ReadOnlyMemory<byte> payload, WorkerFrame? scope, CancellationToken ct) { Count(); return Task.CompletedTask; }
        public (int Max, int Count) PerSecond()
        {
            lock (gate)
            {
                var busiest = 0;
                for (int i = 0, j = 0; i < times.Count; i++) { while (times[i] - times[j] >= 1000) j++; busiest = Math.Max(busiest, i - j + 1); }
                return (busiest, times.Count);
            }
        }
    }
}
