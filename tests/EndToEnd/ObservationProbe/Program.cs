using System.Diagnostics;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.Observation.Worker;

[assembly: SupportedOSPlatform("windows")]

if (args is ["--child"]) { await Task.Delay(Timeout.InfiniteTimeSpan); return; }
var root = Path.GetFullPath(args[0]);
var mode = args.ElementAtOrDefault(1) ?? "normal";
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "process-start-" + Environment.ProcessId), "started");
// legacy 模拟不认识诊断声明的旧 Worker：启动前移除 Host 设置的环境变量。
if (mode == "current-combat-legacy") { Environment.SetEnvironmentVariable(ObservationCaptureCounters.EnvironmentVariable, null); mode = "current-combat-stream"; }
await ObservationWorkerApplication.RunAsync(mode is "current-combat" or "current-combat-stream" or "current-combat-inventory" or "current-combat-resident"
        or "current-combat-resident-inventory"
    ? new CurrentCombatFixtureSource(root, mode != "current-combat", mode is "current-combat-inventory" or "current-combat-resident-inventory" ? 4000 : 0,
        residentIdentity: mode is "current-combat-resident" or "current-combat-resident-inventory") : mode.StartsWith("combat", StringComparison.Ordinal)
    ? new CombatFixtureSource(root, mode) : new FixtureSource(root, mode));

sealed class FixtureSource(string root, string mode) : IObservationCaptureSource
{
    private int runs;
    public IReadOnlyList<CaptureChannelOption> Channels()
    {
        if (mode == "missing-npcap") throw new DllNotFoundException();
        return [new(new string('1', 32), "合成捕获通道", ["127.0.0.1"])];
    }
    /// <summary>auto 系列模式按游戏进程报告各网卡承载的连接数：auto 唯一、auto-none 无、auto-many 两张。</summary>
    public IReadOnlyList<CaptureChannelOption> Channels(CaptureChannelQuery? query)
    {
        if (query is null || !mode.StartsWith("auto", StringComparison.Ordinal)) return Channels();
        File.WriteAllText(Path.Combine(root, "channel-query-" + Environment.ProcessId), query.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return [new(new string('1', 32), "合成游戏网卡", ["10.0.0.2"], false, mode == "auto-none" ? 0 : 1),
            new(new string('2', 32), "合成回环", [], true, mode == "auto-many" ? 1 : 0)];
    }
    /// <summary>detect：第一张网卡收到持续的游戏下行负载，第二张只有零星包；detect-hold：保持到取消。句柄状态写入 detect-handles。</summary>
    public async Task<ChannelDetectionProgress> DetectChannelsAsync(ChannelDetectionRequest request, Func<ChannelDetectionProgress, Task> progress, CancellationToken ct)
    {
        var detector = new ChannelTrafficDetector([(new string('1', 32), "合成游戏网卡"), (new string('2', 32), "合成回环")]);
        detector.Opened(0); detector.Opened(1);
        File.WriteAllText(Path.Combine(root, "detect-handles"), "2");
        try
        {
            var timer = Stopwatch.StartNew();
            for (var tick = 0; ; tick++)
            {
                ct.ThrowIfCancellationRequested();
                if (mode != "detect-hold" && tick >= 10) break;
                for (var i = 0; i < 30; i++) detector.Record(0, 1200);
                if (tick % 5 == 0) detector.Record(1, 60);
                await progress(detector.Progress(timer.Elapsed.TotalSeconds, false));
                await Task.Delay(100, ct);
            }
            return detector.Progress(timer.Elapsed.TotalSeconds, true);
        }
        finally { File.WriteAllText(Path.Combine(root, "detect-handles"), "0"); }
    }
    public async Task CaptureAsync(ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct)
    {
        var run = Interlocked.Increment(ref runs);
        File.WriteAllText(Path.Combine(root, "capture-start-" + run), request.GameSessionId);
        File.WriteAllText(Path.Combine(root, "handles"), "1");
        Process? child = null;
        try
        {
            if (mode == "missing-npcap") throw new DllNotFoundException();
            if (mode == "fail-start") throw new InvalidDataException();
            if (mode == "tree")
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--child"); child = Process.Start(start)!;
                File.WriteAllText(Path.Combine(root, "child-pid"), child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            var generation = Guid.NewGuid().ToString("N");
            var originalGeneration = generation;
            ulong sequence = 0;
            GameCaptureReport Report(bool reset = false)
            {
                var id = new ObservedGameIdentity(new(9007199254740993), "3", "Fixture玩家", new(11));
                var value = new GameObservationValue(generation, reset ? null : id,
                    reset ? null : new(new(7), id.CharacterId, null, [id]),
                    reset ? null : new(new(7), id.CharacterId, id.CharacterId, "同名合成乐谱", true, [0], new string('a', 64),
                        1, .5, Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency) + 1500, "tw-receive-offset-v1", false, null),
                    new(++sequence), "合成被动观测");
                var report = new GameCaptureReport(value, new(sequence), true, Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency) - (mode == "stale" ? 10000 : 0),
                    DateTimeOffset.UtcNow, null, new("observation-fixture", "1.0.0", "TW", "synthetic-v1"));
                return mode switch
                {
                    "null-value" => report with { Value = null! },
                    "bad-realm" => report with { Value = value with { Identity = id with { RealmId = "foreign-realm" } } },
                    "bad-members" => report with { Value = value with { Concert = value.Concert! with { Members = [id with { RealmId = "4" }] } } },
                    "bad-timing" => report with { Value = value with { Music = value.Music! with { SelectedDurationSeconds = 2 } } },
                    "regression" when sequence >= 3 => report with { Sequence = new(1) },
                    _ => report
                };
            }
            if (mode == "hold-build") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            while (!ct.IsCancellationRequested || mode == "stall")
            {
                if (mode == "generation" && sequence == 3) generation = Guid.NewGuid().ToString("N");
                if (mode == "retired" && sequence == 2) generation = Guid.NewGuid().ToString("N");
                if (mode == "retired" && sequence == 4) generation = originalGeneration;
                publish(Report(mode == "generation" && sequence >= 3));
                if (mode == "silent") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (mode == "exit") { await Task.Delay(300); Environment.Exit(12); }
                await Task.Delay(100, mode == "stall" ? CancellationToken.None : ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (mode == "late")
            {
                var value = new GameObservationValue(Guid.NewGuid().ToString("N"), null, null, null, new(1), "取消后迟到的合成摘要");
                publish(new(value, new(1), true, Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency), DateTimeOffset.UtcNow, null));
                await Task.Delay(50);
            }
            throw;
        }
        finally
        {
            child?.Dispose();
            File.WriteAllText(Path.Combine(root, "handles"), "0");
            File.WriteAllText(Path.Combine(root, "capture-stopped-" + run), "closed");
        }
    }
}
