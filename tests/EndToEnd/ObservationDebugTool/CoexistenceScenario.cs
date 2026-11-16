using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Application;

namespace MabiTool.ObservationDebugTool;

public sealed record CoexistenceScenarioOptions(int PreparationSeconds = 900, double TimeScale = 1, int SetupSeconds = 120,
    int InventoryStartSeconds = 90);

/// <summary>
/// 11.9 共用观察验收：同一 Host、同一观察 Worker 中依次保持音乐需求、战斗和库存三方订阅，按固定时点由工具
/// 停止/重启库存、关闭战斗、最后释放捕获，核对 Worker 不重启、其余订阅不受影响且最终全部释放。
/// </summary>
public sealed class CoexistenceScenario(IGameEnvironment environment, CoexistenceScenarioOptions? options = null) : IDebugScenario
{
    private readonly CoexistenceScenarioOptions settings = options ?? new();
    private double At(double seconds) => seconds * settings.TimeScale;
    private int RunSeconds => (int)Math.Ceiling(At(150));

    public ScenarioPlan Plan => new("coexistence", "共用观察：战斗 + 库存 + 音乐需求并发启停", "unified-modular-backend 11.9（共用观察无泄漏、停止互不干扰）",
        "native-host-shared-observation-coexistence", "shared-observation-coexistence", settings.PreparationSeconds, RunSeconds,
        [
            $"点击“开始”即开始观察网络。准备阶段最长 {settings.PreparationSeconds / 60} 分钟：工具启动共享捕获（音乐需求）、库存观察和战斗观察。",
            "面板显示“可以切图”后切换一次地图，取得本机战斗身份。",
            $"显示“准备完成”后找好怪物，点击“开始 {RunSeconds} 秒共存采集”。",
            $"0–{At(110):0} 秒持续攻击怪物；工具会在第 {At(40):0} 秒停止库存观察、第 {At(80):0} 秒重新启动库存观察，攻击不要中断。",
            $"第 {At(110):0} 秒工具关闭战斗观察，之后停止攻击、原地自由活动；第 {At(130):0} 秒工具释放全部观察并核对 Worker 退出。",
            "倒计时结束自动停止；任何时候都可以点“停止”。",
        ]);

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var host = ObservationHost.Create(environment, context, storage: true);
        var preparationDeadline = context.DeadlineAfter(settings.PreparationSeconds);
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(context.Stopped);
        preparation.CancelAfter(TimeSpan.FromSeconds(settings.PreparationSeconds));
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(context.Stopped);
        using var readerStop = new CancellationTokenSource();
        var identityPreparation = new CombatIdentityPreparation();
        var windows = new SortedDictionary<int, WindowCounter>();
        var frameAges = new FrameAgeStatistics();
        var currentWindow = -1;
        var checkpoints = new List<CheckpointRecord>();
        var inventoryOperations = new List<InventoryOperationRecord>();
        ICombatObservationSubscription? combat = null;
        Task reader = Task.CompletedTask;
        string? readerFailure = null, failure = null;
        var closingCombat = false;
        var identityReady = false;
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureStartMono = double.NaN;
        CombatCaptureDiagnostics? diagnostics = null;
        InventoryCaptureDiagnostics? inventoryDiagnostics = null;
        var runStartMs = double.NaN;
        string phase = "preparation";

        context.Show(() => phase == "preparation"
            ? new(identityReady ? "准备完成" : host.Stage == "combat.identity" ? "可以切图" : "正在启动",
                identityReady ? "身份已确认。找好怪物后点击开始共存采集；采集期间请持续攻击。"
                    : host.Stage == "combat.identity" ? "请切换一次地图以取得本机身份；库存与音乐需求已在同一 Worker 中运行。"
                    : "正在启动共享捕获、库存观察与战斗观察，请等待。", context.Remaining(preparationDeadline))
            : new("正在停止", "正在释放 Worker、订阅和捕获资源，请稍候。", null));
        context.Offer($"开始 {RunSeconds} 秒共存采集", () => identityReady && phase == "preparation", () => runStarted.TrySetResult());

        async Task Checkpoint(string code)
        {
            var runtime = host.Observations!.Runtime();
            var inventory = inventoryOperations.LastOrDefault();
            string? inventoryState = null;
            if (inventory is not null)
                try { inventoryState = (await ObservationHost.RetryAsync(() => host.Client().GetInventoryObservationAsync(inventory.Id), CancellationToken.None)).State.KnownValue?.ToString(); }
                catch (MabiToolApiException error) { inventoryState = "query:" + SafeCodes.Of(error); }
            checkpoints.Add(new(code, double.IsNaN(runStartMs) ? null : context.ElapsedMs - runStartMs, context.ElapsedMs,
                runtime.WorkerProcessId, runtime.SubscriberCount, runtime.State, SharedObservationDiagnostics.SafeReason(runtime.Reason),
                combat is not null && !closingCombat, inventoryState));
            context.Mark(code);
        }
        async Task StartInventoryAsync(CancellationToken ct)
        {
            var sdk = host.Client();
            var before = host.Observations!.Runtime().SubscriberCount;
            var started = context.ElapsedMs;
            var key = "debug-inventory-" + Guid.NewGuid().ToString("N");
            var operation = await ObservationHost.RetryAsync(() => sdk.StartInventoryObservationAsync(new(host.Game!.Session.Id), key, ct), ct);
            var record = new InventoryOperationRecord(operation.Id, started); inventoryOperations.Add(record);
            var limit = context.Clock.GetTimestamp() + settings.InventoryStartSeconds * context.Clock.TimestampFrequency;
            // 库存兴趣在 Worker 准备布局后才登记为共享需求；订阅数增加即表示已并入同一捕获。
            while (host.Observations.Runtime().SubscriberCount <= before)
            {
                ct.ThrowIfCancellationRequested();
                var state = await ObservationHost.RetryAsync(() => sdk.GetInventoryObservationAsync(operation.Id, ct), ct);
                if (SqliteStore.IsTerminal(state.State.KnownValue))
                {
                    record.FinalState = state.State.KnownValue?.ToString(); record.Problem = state.Problem?.Code; record.StoppedMs = context.ElapsedMs;
                    throw new ProbeException(state.Problem?.Code ?? "inventory.observationEnded");
                }
                if (context.Clock.GetTimestamp() >= limit) throw new ProbeException("inventory.startTimedOut");
                await Task.Delay(200, ct);
            }
            record.ReadyMs = context.ElapsedMs;
        }
        async Task StopInventoryAsync(CancellationToken ct)
        {
            var record = inventoryOperations.LastOrDefault(value => value.StoppedMs is null);
            if (record is null) return;
            var sdk = host.Client();
            if (!SqliteStore.IsTerminal((await ObservationHost.RetryAsync(() => sdk.GetInventoryObservationAsync(record.Id, ct), ct)).State.KnownValue))
                await ObservationHost.RetryAsync(() => sdk.StopInventoryObservationAsync(record.Id, ct), ct);
            var final = await host.WaitOperationAsync(() => sdk.GetInventoryObservationAsync(record.Id, ct),
                value => SqliteStore.IsTerminal(value.State.KnownValue), ct);
            record.StoppedMs = context.ElapsedMs; record.FinalState = final.State.KnownValue?.ToString(); record.Problem = final.Problem?.Code;
        }
        async Task CloseCombatAsync()
        {
            if (combat is null || closingCombat) return;
            closingCombat = true;
            readerStop.Cancel();
            try { await reader; } catch (OperationCanceledException) { }
            foreach (var gap in await combat.CloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)))
                Window(currentWindow).AddGap(gap.Reason, gap.DroppedEvents.Value);
            await combat.DisposeAsync();
        }
        WindowCounter Window(int index) { lock (windows) { if (!windows.TryGetValue(index, out var value)) windows[index] = value = new(); return value; } }

        try
        {
            using (var setup = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token))
            {
                setup.CancelAfter(TimeSpan.FromSeconds(settings.SetupSeconds));
                await host.StartAsync(setup.Token);
            }
            captureStartMono = Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
            await host.StartCaptureAsync(preparation.Token);
            context.Mark("capture.running");
            host.Stage = "inventory.start";
            await StartInventoryAsync(preparation.Token);
            host.Stage = "combat.identity";
            var observations = host.Observations!; var owner = host.Owner!; var session = host.Game!.Session;
            combat = await identityPreparation.OpenAsync(ready => observations.OpenCombatAsync(owner, "debug-coexistence-" + Guid.NewGuid().ToString("N"),
                    new(session.Id), environment.CombatProfile, preparation.Token, ready),
                () => context.Mark("combat.captureReady"), () => observations.Runtime().State == "running", preparation.Token);
            context.Track(combat);
            identityReady = true; host.Stage = "combat.read";
            context.Mark("combat.identityReady");
            var subscription = combat;
            reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in subscription.ReadAsync(readerStop.Token))
                    {
                        Window(Volatile.Read(ref currentWindow)).Accept(frame);
                        frameAges.Add(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency) - frame.MonotonicMs);
                    }
                    if (!readerStop.IsCancellationRequested) throw new ProbeException("probe.streamEndedEarly");
                }
                catch (OperationCanceledException) when (readerStop.IsCancellationRequested) { }
                catch (Exception error)
                {
                    readerFailure = SafeCodes.Of(error); context.Mark("combat.readerFailed", readerFailure);
                    try { abort.Cancel(); } catch (ObjectDisposedException) { }
                }
            });
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token, abort.Token))
                await runStarted.Task.WaitAsync(waiting.Token);
            phase = "run"; runStartMs = context.ElapsedMs; host.Stage = "coexistence.run";
            context.ClearOffer();
            await Checkpoint("run.started");
            Volatile.Write(ref currentWindow, 0);
            await ScenarioTimeline.RunAsync(context,
            [
                new(0, "三方共用观察", "持续攻击怪物。音乐需求、战斗与库存正在共用同一个 Worker。", Code: "window.0"),
                new(At(40), "工具停止库存观察", "继续攻击怪物。工具已停止库存观察，战斗与音乐需求应保持运行。", async ct =>
                {
                    Volatile.Write(ref currentWindow, 1);
                    await StopInventoryAsync(ct); await Checkpoint("inventory.stopped");
                }, "window.1"),
                new(At(80), "工具重新启动库存观察", "继续攻击怪物。工具已重新启动库存观察，战斗应保持连续。", async ct =>
                {
                    Volatile.Write(ref currentWindow, 2);
                    await StartInventoryAsync(ct); await Checkpoint("inventory.restarted");
                }, "window.2"),
                new(At(110), "工具关闭战斗观察", "停止攻击，原地自由活动即可。库存与音乐需求继续运行。", async _ =>
                {
                    Volatile.Write(ref currentWindow, 3);
                    await CloseCombatAsync(); await Checkpoint("combat.closed");
                }, "window.3"),
                new(At(130), "工具释放全部观察", "无需操作。工具正在停止库存、释放共享捕获并核对 Worker 退出。", async ct =>
                {
                    Volatile.Write(ref currentWindow, 4);
                    await StopInventoryAsync(ct);
                    await Checkpoint("inventory.stoppedFinal");
                    if (host.Demand is { } demand)
                        await host.Observations!.ReleaseAsync(host.Owner!, true, demand.Id, ct);
                    var exitWatch = Stopwatch.StartNew();
                    await ScenarioTimeline.WaitUntilAsync(context, () => host.Observations!.Runtime().WorkerProcessId is null, ct);
                    host.Phases["workerExitAfterReleaseMs"] = exitWatch.Elapsed.TotalMilliseconds;
                    await Checkpoint("capture.released");
                }, "window.4"),
            ], RunSeconds, abort.Token);
        }
        catch (OperationCanceledException) when (context.Stopped.IsCancellationRequested) { failure = "probe.interrupted"; }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { failure = readerFailure ?? "coexistence.aborted"; }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested && phase == "preparation")
        { failure = identityReady ? "probe.preparationTimedOut" : host.Stage == "combat.identity" ? "combat.identityNotObserved" : "probe.preparationTimedOut"; }
        catch (Exception error) { failure = SafeCodes.Of(error); }
        finally
        {
            phase = "stopping";
            context.ClearOffer();
            try { await CloseCombatAsync(); } catch (Exception error) { host.CleanupErrors.Add("combat.close:" + SafeCodes.Of(error)); }
            if (combat is not null) context.Untrack(combat);
            try
            {
                using var stopLimit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (host.Observations is not null) await StopInventoryAsync(stopLimit.Token);
            }
            catch (Exception error) { host.CleanupErrors.Add("inventory.stop:" + SafeCodes.Of(error)); }
            await host.CleanupAsync(afterHostStop: () => { diagnostics = host.Observations?.CombatDiagnostics(); inventoryDiagnostics = host.Observations?.InventoryDiagnostics(); });
        }
        var inventoryFrames = InventoryFrameTimeline.Project(inventoryDiagnostics, context);
        double? Relative(double? mono) => mono is { } value && double.IsFinite(captureStartMono) ? value - captureStartMono : null;
        var pids = checkpoints.Select(value => value.WorkerProcessId).ToArray();
        var subscribers = checkpoints.Select(value => value.SubscriberCount).ToArray();
        var expectedSubscribers = new[] { 3, 2, 3, 2, 1, 0 };
        WindowCounter[] runWindows;
        lock (windows) runWindows = Enumerable.Range(0, 3).Select(index => windows.GetValueOrDefault(index) ?? new WindowCounter()).ToArray();
        var unexpectedGaps = runWindows.Sum(value => value.Gaps.Where(gap => !gap.Key.StartsWith("combat.streamGap.connection", StringComparison.Ordinal)).Sum(gap => gap.Value));
        // Worker 运行期私有字节峰值 ≤ 160 MiB（共用观察内存预算）；没有内存轨迹即没有证据，按未通过处理。
        var workerTrace = host.MemoryTraceDirectory is null ? null : WorkerMemoryTraceFiles.Summarize(host.MemoryTraceDirectory);
        var checks = new Dictionary<string, bool>
        {
            ["timelineCompleted"] = failure is null && checkpoints.Count == expectedSubscribers.Length,
            ["workerProcessStable"] = pids.Length >= 5 && pids.Take(5).All(value => value is not null && value == pids[0]),
            ["subscriberCountsExpected"] = subscribers.SequenceEqual(expectedSubscribers),
            ["combatFramesInEachWindow"] = runWindows.All(value => value.Frames > 0),
            ["noUnexpectedCombatGaps"] = unexpectedGaps == 0 && diagnostics?.FrameRejection is null,
            ["inventoryStoppedCleanly"] = inventoryOperations.Count >= 2 && inventoryOperations.All(value => value.FinalState is "Cancelled" or "Succeeded"),
            ["musicDemandHeldUntilRelease"] = checkpoints.Count == expectedSubscribers.Length &&
                checkpoints.Take(checkpoints.Count - 1).All(value => value.CaptureState == "running"),
            ["workerRuntimeMemoryWithinBudget"] = workerTrace is { RuntimeWithinBudget: true },
            ["released"] = host.Released,
        };
        var accepted = checks.Values.All(value => value);
        var report = JsonNodes.From(new
        {
            schemaVersion = 1, evidenceKind = Plan.EvidenceKind, checkedAtUtc = DateTimeOffset.UtcNow,
            captureProfile = environment.CaptureProfile, combatProfile = environment.CombatProfile.Id,
            gameBuild = host.Game?.Session.GameBuild, timeScale = settings.TimeScale, runSeconds = RunSeconds,
            phases = host.Phases, stageAtEnd = host.Stage, interrupted = context.Stopped.IsCancellationRequested, failure, readerFailure,
            identityPreparation, checkpoints,
            combatWindows = windows.Select(pair => new
            {
                window = pair.Key, pair.Value.Frames, pair.Value.DamageEvents, pair.Value.LocalDamageEvents, pair.Value.SelfEffects,
                pair.Value.MonsterEffects, gaps = pair.Value.Gaps, droppedEvents = pair.Value.Dropped.ToString(CultureInfo.InvariantCulture),
            }).ToArray(),
            frameRejection = diagnostics?.FrameRejection is { } rejection ? new { rejection.Branch, rejection.FrameAgeMs, rejection.ArrivalIntervalMs,
                atCaptureMs = Relative(rejection.HostMonotonicMs) } : null,
            // 战斗帧到达工具读取端时的帧龄（Worker 帧单调时点至读取时点，同一系统单调时钟），毫秒。
            frameAge = frameAges.Summary(),
            combatGaps = diagnostics?.Gaps.Select(gap => new { gap.Reason, fromCaptureMs = Relative(gap.FromMs), toCaptureMs = Relative(gap.ToMs),
                droppedEvents = gap.DroppedEvents.ToString(CultureInfo.InvariantCulture) }).ToArray(),
            // 每次库存操作期间 Host 受理的完整同步、条目数、增量数与失效原因（来自库存帧诊断，不含身份与条目内容）。
            inventoryOperations = inventoryOperations.Select(value => new { value.StartedMs, value.ReadyMs, value.StoppedMs, value.FinalState, value.Problem,
                inventory = InventoryFrameTimeline.Window(inventoryFrames, value.StartedMs, value.StoppedMs) }).ToArray(),
            inventoryFrames, inventoryFrameCounts = inventoryDiagnostics?.Counts,
            // 当前台服布局按映射 20344/20054/20590/20608/170415/170416/170703/170704/20272 解码本机身份与演奏；
            // 本场景不演奏，只验收音乐需求对共享捕获的持有及本机身份的识别。
            music = new { demandHeld = host.Demand is not null, identityObserved = host.Shared.MusicIdentityEverObserved,
                contentObserved = host.Shared.MusicEverObserved, projectionSupportedOnCurrentBuild = true },
            sharedObservation = host.Shared, checks, memory = host.MemoryReport(), release = host.ReleaseReport(), totalElapsedMs = context.ElapsedMs,
        });
        report["acceptance"] = new JsonObject { ["accepted"] = accepted, ["released"] = host.Released };
        var outcome = failure switch
        {
            null => ScenarioResult.Completed,
            "probe.interrupted" => ScenarioResult.Cancelled,
            "probe.preparationTimedOut" => ScenarioResult.TimedOut,
            _ => ScenarioResult.Failed
        };
        var failed = checks.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray();
        var summary = accepted ? $"共存验收通过：Worker 未重启，订阅数 {string.Join("→", subscribers)}，三方停止互不干扰"
            : "结果：" + (failure ?? "未通过 " + string.Join("、", failed));
        return new(outcome, accepted, summary, report);
    }

    private sealed record CheckpointRecord(string Code, double? AtRunMs, double AtMs, int? WorkerProcessId, int SubscriberCount,
        string CaptureState, string? CaptureReason, bool CombatOpen, string? InventoryOperationState);

    private sealed class InventoryOperationRecord(string id, double startedMs)
    {
        public string Id { get; } = id;
        public double StartedMs { get; } = startedMs;
        public double? ReadyMs { get; set; }
        public double? StoppedMs { get; set; }
        public string? FinalState { get; set; }
        public string? Problem { get; set; }
    }
}

/// <summary>帧龄样本（最多 100,000 个）及分位数汇总。</summary>
public sealed class FrameAgeStatistics
{
    private readonly object gate = new();
    private readonly List<double> samples = [];
    private double maximum = double.NegativeInfinity;
    private long count;
    public void Add(double ageMs)
    {
        if (!double.IsFinite(ageMs)) return;
        lock (gate) { count++; maximum = Math.Max(maximum, ageMs); if (samples.Count < 100_000) samples.Add(ageMs); }
    }
    public FrameAgeSummary Summary()
    {
        lock (gate)
        {
            if (samples.Count == 0) return new(0, null, null, null, null);
            var sorted = samples.Order().ToArray();
            double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
            return new(count, At(.5), At(.95), At(.99), maximum);
        }
    }
}
public sealed record FrameAgeSummary(long Count, double? P50Ms, double? P95Ms, double? P99Ms, double? MaxMs);

/// <summary>时间窗内的战斗帧计数，用于判断启停其他订阅时战斗是否连续。</summary>
public sealed class WindowCounter
{
    private readonly object gate = new();
    public long Frames { get; private set; }
    public long DamageEvents { get; private set; }
    public long LocalDamageEvents { get; private set; }
    public long SelfEffects { get; private set; }
    public long MonsterEffects { get; private set; }
    public BigInteger Dropped { get; private set; }
    public Dictionary<string, long> Gaps { get { lock (gate) return new(gaps, StringComparer.Ordinal); } }
    private readonly Dictionary<string, long> gaps = new(StringComparer.Ordinal);
    public void Accept(CombatFrame frame)
    {
        lock (gate)
        {
            Frames++; DamageEvents += frame.Damage.Count;
            var local = frame.Entities.FirstOrDefault(entity => entity.Role.KnownValue == CombatEntityRole.LocalPlayer)?.Key;
            var monsters = frame.Entities.Where(entity => entity.Role.KnownValue == CombatEntityRole.Monster).Select(entity => entity.Key).ToHashSet();
            LocalDamageEvents += frame.Damage.Count(value => local is not null && value.SourceEntity == local && value.Chain != true);
            SelfEffects += frame.Effects.Count(effect => local is not null && effect.EntityKey == local);
            MonsterEffects += frame.Effects.Count(effect => monsters.Contains(effect.EntityKey));
            foreach (var gap in frame.Gaps) AddGapCore(gap.Reason, gap.DroppedEvents.Value);
        }
    }
    public void AddGap(string reason, BigInteger dropped) { lock (gate) AddGapCore(reason, dropped); }
    private void AddGapCore(string reason, BigInteger dropped) { gaps[reason] = gaps.GetValueOrDefault(reason) + 1; Dropped += dropped; }
}
