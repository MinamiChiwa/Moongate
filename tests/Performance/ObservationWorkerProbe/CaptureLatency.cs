using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.ObservationWorkerProbe;

/// <summary>
/// 抓包到写出队列的延迟（样本时间、虚拟时钟）：实采样本按抓包时刻送入 <see cref="CapturePipeline"/>，读取循环的唤醒与推进规则按版本在虚拟时间里重现，
/// 统计每条伤害从抓包到所在报告入队的延迟（含技能关联等待与推进节拍）、只有血量变化的报告从 HP 包到入队的延迟，以及有游戏流时不由包唤醒的
/// 唤醒次数（quiet：两包之间的全部非包唤醒，含血量与保活发布；idle：距下一个包至少 1 秒的空闲时段）。
/// <list type="bullet">
/// <item>before：改动前的 ReadLoop。有包由读事件立即唤醒；推进距上次满 20 ms 才执行；等待超时按系统计时器精度（15.625 ms 网格）取整；
/// 持续有包（距上次唤醒不足 10 ms）时先等 10 ms（同样取整）。</item>
/// <item>after：改动后的 ReadLoop。推进按解析器给出的下一截止时间（无截止时至多 100 ms）由高精度计时器唤醒（取 0.5 ms 唤醒误差）；
/// 持续有包时合批等待 4 ms（高精度）。</item>
/// </list>
/// </summary>
internal static class CaptureLatency
{
    private const double TimerGridMs = 15.625, HighResolutionSlackMs = 0.5;

    public static JsonNode Run(IReadOnlyList<string> directories)
    {
        var policies = new List<string> { "before" };
        if (typeof(CapturePipeline).GetProperty("NextDueMs") is not null) policies.Add("after");
        var result = new JsonObject { ["policies"] = new JsonArray(policies.Select(item => (JsonNode)item).ToArray()) };
        var samples = new JsonArray();
        foreach (var directory in directories)
        {
            using var sample = Sample.Load(directory, false);
            var entry = new JsonObject { ["sample"] = sample.Name, ["packets"] = sample.Packets.Count, ["seconds"] = Math.Round(sample.DurationMs / 1000, 1) };
            foreach (var policy in policies) entry[policy] = ReplayOnce(sample, policy);
            samples.Add(entry);
        }
        result["samples"] = samples;
        return result;
    }

    private static JsonNode ReplayOnce(Sample sample, string policy)
    {
        var clock = new ReplayClock(CapturePipeline.Mono(), DateTimeOffset.UtcNow);
        sample.Materialize(clock.Utc());
        var damage = new List<double>(); var health = new List<double>();
        var lastHealth = new Dictionary<string, ulong?>(StringComparer.Ordinal);
        var run = MabiTool.ObservationWorkerProbe.Replay.CreateRun(new NullPeer(), new ReplaySource(), "probe-" + sample.Name);
        run.Enqueued = report =>
        {
            if (report is not CombatCaptureReport value) return;
            var now = clock.Mono();
            foreach (var hit in value.Frame.Damage) damage.Add(now - hit.MonotonicMs);
            var records = value.Frame.Damage.Count + value.Frame.Effects.Count + (value.Frame.Healing?.Count ?? 0) + (value.Frame.CombatStates?.Count ?? 0);
            foreach (var entity in value.Frame.Entities)
            {
                if (entity.Health is not { Current: { } current, ObservedAt: { } observed }) continue;
                var key = entity.Key.RawInstanceId.ToString();
                var changed = lastHealth.TryGetValue(key, out var previous) && previous != current.Value;
                lastHealth[key] = current.Value;
                if (changed && records == 0) health.Add((clock.Utc() - observed).TotalMilliseconds);
            }
        };
        var context = run.Context;
        var signature = Tw20261110CombatProtocol.StreamProfile.KnownProtocols.Where(code => code != 0).ToHashSet();
        var pipeline = new CapturePipeline(1, signature, "probe-" + sample.Name, context, CombatProtocolLayout.Tw20261110, null,
            new LocalIdentityMemory(), null, clock.Mono, clock.Utc);
        var due = typeof(CapturePipeline).GetProperty("NextDueMs");
        var prearm = new PrearmPool().Create(context.Diagnostics);
        PrearmBuffer[] prearms = [prearm];
        HashSet<ushort> passLocal = [], passRemote = [];
        double t = 0, lastHousekeeping = double.NegativeInfinity, lastReport = double.NegativeInfinity, lastTick = double.NegativeInfinity, lastWake = double.NegativeInfinity;
        var next = 0; var end = sample.DurationMs + 2000;
        // 有游戏流而没有包的时段里读取循环醒来的次数（不含包唤醒）。
        double quietMs = 0; long quietWakes = 0;
        // 距下一个包至少 1 秒的空闲时段里的唤醒（游戏开着、没有战斗数据时的常驻开销）。
        double idleMs = 0; long idleWakes = 0;
        var phase = Random.Shared.NextDouble() * TimerGridMs;
        double Grid(double at) => Math.Max(at, Math.Ceiling((at - phase) / TimerGridMs) * TimerGridMs + phase);
        while (t <= end)
        {
            clock.Elapsed = t;
            if (t - lastHousekeeping >= 500)
            {
                var table = sample.Connections.Where(row => row.ListedFrom <= t && t <= row.ListedTo)
                    .Select(row => (new CaptureConnection(row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort), MabiTool.ObservationWorkerProbe.Replay.ProcessId, 5)).ToList();
                pipeline.Refresh(table, MabiTool.ObservationWorkerProbe.Replay.ProcessId, prearms);
                passLocal = pipeline.GamePorts.Concat(pipeline.Router.LocalPorts).ToHashSet(); passRemote = pipeline.Router.RemotePorts.ToHashSet();
                lastHousekeeping = t;
            }
            var confirmed = pipeline.Router.ConfirmedCount > 0;
            double tickDue = double.PositiveInfinity;
            if (confirmed)
            {
                if (policy == "before") tickDue = lastTick + 20;
                else tickDue = Math.Min(due!.GetValue(pipeline) is double value ? value - clock.Mono() + t : double.PositiveInfinity, lastTick + 100);
            }
            if (confirmed && t >= tickDue) { pipeline.Tick(); lastTick = t; tickDue = policy == "before" ? t + 20 : Math.Min(due!.GetValue(pipeline) is double value ? value - clock.Mono() + t : double.PositiveInfinity, t + 100); }
            if (t - lastReport >= 500) { pipeline.Report(); lastReport = t; }
            // 等待：包到达立即唤醒；超时按版本取整。
            var coarse = Math.Min(lastHousekeeping, lastReport) + 500;
            double timeout;
            if (policy == "before") timeout = Grid(t + Math.Ceiling(Math.Max(1e-6, Math.Min(coarse, tickDue) - t)));
            else timeout = Math.Min(Grid(t + Math.Ceiling(Math.Max(1e-6, coarse - t))), double.IsPositiveInfinity(tickDue) ? double.PositiveInfinity : Math.Max(t, tickDue) + HighResolutionSlackMs);
            var packetAt = next < sample.Packets.Count ? sample.Packets[next].Ms : double.PositiveInfinity;
            var wakeAt = Math.Max(t, Math.Min(timeout, packetAt));
            if (confirmed && packetAt > timeout) { quietWakes++; quietMs += wakeAt - t; }
            if (confirmed && packetAt - t >= 1000 && packetAt > timeout) { idleWakes++; idleMs += wakeAt - t; }
            t = wakeAt; clock.Elapsed = t;
            var read = 0;
            while (next < sample.Packets.Count && sample.Packets[next].Ms <= t)
            {
                var connection = sample.Connections[sample.Packets[next].Connection];
                if (passLocal.Contains(connection.LocalPort) || passRemote.Contains(connection.RemotePort))
                    pipeline.Packet(0, sample.Packets[next].Link, prearm, sample.Header(next), sample.Data(next));
                next++; read++;
            }
            var wake = t;
            var coalesce = policy == "before" ? 10 : 4;
            if (read > 0 && wake - lastWake < coalesce) t = policy == "before" ? Grid(t + coalesce) : t + coalesce;
            if (read > 0) lastWake = wake;
            if (next >= sample.Packets.Count && t > sample.DurationMs + 2000) break;
        }
        pipeline.Dispose();
        prearm.Clear();
        return JsonSerializer.SerializeToNode(new
        {
            damageCaptureToEnqueueMs = WriteLatency.Percentiles(damage),
            healthOnlyCaptureToEnqueueMs = WriteLatency.Percentiles(health),
            quietWakesPerSecond = quietMs <= 0 ? (double?)null : Math.Round(quietWakes / (quietMs / 1000), 1),
            idleWakesPerSecond = idleMs <= 0 ? (double?)null : Math.Round(idleWakes / (idleMs / 1000), 1), idleSeconds = Math.Round(idleMs / 1000, 1)
        }, ContractJson.Options)!;
    }

}
