using System.Text.Json.Nodes;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.ObservationDebugTool;

public sealed record ReloginSampleOptions(int PreparationSeconds = 300, int SetupSeconds = 120, int InventoryStartSeconds = 90,
    int CaptureSeconds = 180, double TimeScale = 1);

/// <summary>
/// 重新登录样本（mabiagent-inventory-module 5.3 取证）：库存观察与原始下行采集同时运行，操作者按面板依次站定、退回角色选择、
/// 重新进入同一角色、站定 30 秒、切一次图。采集跟随游戏进程全部 TCP 连接（不限 1111 端口），新连接在被连接表发现前到达的
/// 下行包经内存预缓冲补写。结束后工具离线重组样本，报告各连接的端口与生命周期、已知协议号直方图、登录/身份关联/库存增量帧，
/// 以及 Host 实时库存帧诊断和离线重放的库存观察器状态与失效原因。
/// </summary>
public sealed class ReloginSampleScenario(IGameEnvironment environment, Func<CancellationToken, ClientCaptureLayout?> layoutSource,
    ReloginSampleOptions? options = null) : IDebugScenario
{
    private readonly ReloginSampleOptions settings = options ?? new();
    private double At(double seconds) => seconds * settings.TimeScale;
    private int TimelineSeconds => (int)Math.Ceiling(At(175));

    public IReadOnlyList<TimelineStep> Steps =>
    [
        new(0, "站定", "站在安全区域原地不动，不要打开任何界面。", Code: "relogin.stand"),
        new(At(10), "退回角色选择", "打开菜单选择“返回角色选择”（不要退出游戏），停在角色选择画面。", Code: "relogin.characterSelect"),
        new(At(35), "重新进入同一角色", "选择刚才的同一角色进入游戏，等待加载完成。", Code: "relogin.enter"),
        new(At(70), "站定 30 秒", "进入后原地站定，不要打开界面。", Code: "relogin.standAfterEnter"),
        new(At(105), "切一次图", "移动或传送到相邻地图（切换一次地图），加载完成后原地站定。", Code: "relogin.mapChange"),
        new(At(140), "站定至结束", "原地站定直到倒计时结束。", Code: "relogin.standAfterMap"),
    ];

    public ScenarioPlan Plan => new("relogin-sample", "样本：重新登录与切图（库存登录数据去向）", "mabiagent-inventory-module 5.3",
        "native-relogin-sample", "relogin-sample", settings.PreparationSeconds, settings.CaptureSeconds,
        [
            $"点击“开始”即开始观察网络。工具先启动共享捕获和库存观察（读取客户端布局约 5 秒），准备阶段最长 {settings.PreparationSeconds / 60} 分钟。",
            $"面板显示“站定”后开始原始下行采集，持续 {settings.CaptureSeconds} 秒；采集跟随游戏进程的全部 TCP 连接，不限端口。",
            .. Steps.Select(step => $"第 {step.AtSeconds:0} 秒：{step.Stage}——{step.Instruction}"),
            "倒计时结束后工具停止库存观察、释放捕获，并离线分析样本（约 10 秒）；任何时候都可以点“停止”。",
            "原始样本只写入 artifacts/tmp 下的私有目录；报告只记录协议号、长度、时点、计数与库存状态，不含载荷与身份。",
        ],
        environment.Samples is null ? "需要 --python 参数指定 Python 3.12 解释器" : environment.Samples.Unavailable) { CleanupAllowanceSeconds = 180 };

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var capture = environment.Samples ?? throw new ProbeException("sample.pythonNotConfigured");
        var host = ObservationHost.Create(environment, context, storage: true);
        var output = Path.Combine(context.ScratchDirectory, "capture");
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(context.Stopped);
        preparation.CancelAfter(TimeSpan.FromSeconds(settings.PreparationSeconds));
        var preparationDeadline = context.DeadlineAfter(settings.PreparationSeconds);
        string? failure = null, operationId = null, finalState = null;
        int? exitCode = null; JsonNode? captureResult = null; var stopped = false;
        double? sampleStartedMs = null;
        var snapshotStates = new List<object>();
        InventoryCaptureDiagnostics? inventoryDiagnostics = null;
        IRawSampleRun? run = null;
        context.Show("正在启动", "正在启动 Host、共享捕获与库存观察，请保持游戏在角色内。", preparationDeadline);
        try
        {
            using (var setup = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token))
            {
                setup.CancelAfter(TimeSpan.FromSeconds(settings.SetupSeconds));
                await host.StartAsync(setup.Token);
            }
            await host.StartCaptureAsync(preparation.Token);
            context.Mark("capture.running");
            var sdk = host.Client();
            var before = host.Observations!.Runtime().SubscriberCount;
            host.Stage = "inventory.start";
            var started = await ObservationHost.RetryAsync(() => sdk.StartInventoryObservationAsync(new(host.Game!.Session.Id),
                "debug-relogin-" + Guid.NewGuid().ToString("N"), preparation.Token), preparation.Token);
            operationId = started.Id;
            var limit = context.Clock.GetTimestamp() + settings.InventoryStartSeconds * context.Clock.TimestampFrequency;
            while (host.Observations.Runtime().SubscriberCount <= before)
            {
                var state = await ObservationHost.RetryAsync(() => sdk.GetInventoryObservationAsync(operationId, preparation.Token), preparation.Token);
                if (SqliteStore.IsTerminal(state.State.KnownValue)) throw new ProbeException(state.Problem?.Code ?? "inventory.observationEnded");
                if (context.Clock.GetTimestamp() >= limit) throw new ProbeException("inventory.startTimedOut");
                await Task.Delay(200, preparation.Token);
            }
            context.Mark("inventory.observing");
            host.Stage = "sample.capture";
            run = context.Track(await capture.StartAsync(output, settings.CaptureSeconds, context.Stopped, trackProcessConnections: true));
            context.ObserveProcess(run.ProcessId);
            sampleStartedMs = context.ElapsedMs; context.Mark("sample.started");
            using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.Stopped);
            var timeline = ScenarioTimeline.RunAsync(context, Steps, TimelineSeconds, ended.Token);
            var watcher = Task.Run(async () =>
            {
                bool? last = null;
                while (!ended.IsCancellationRequested && !timeline.IsCompleted)
                {
                    try
                    {
                        var data = (await sdk.GetInventorySnapshotAsync(ended.Token)).Data;
                        var current = data?.Current == true;
                        if (current != last)
                        {
                            last = current;
                            lock (snapshotStates) snapshotStates.Add(new { atMs = Math.Round(context.ElapsedMs, 1), current,
                                quality = data?.Quality.KnownValue?.ToString(), items = data?.Items.Count });
                        }
                        await Task.Delay(1000, ended.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (MabiTool.Client.MabiToolApiException) { await Task.Delay(1000, CancellationToken.None); }
                }
            });
            var finished = await Task.WhenAny(timeline, run.Completion);
            if (finished == run.Completion && !timeline.IsCompleted)
            {
                ended.Cancel();
                try { await timeline; } catch (OperationCanceledException) { }
                context.Mark("sample.endedBeforeTimeline");
            }
            else await timeline;
            ended.Cancel();
            await watcher;
            context.Show("等待采集结束", "采集进程正在写入样本摘要，请保持原地。", null);
            exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(settings.CaptureSeconds + 30), context.Stopped);
        }
        catch (OperationCanceledException) when (context.Stopped.IsCancellationRequested) { failure = "probe.interrupted"; stopped = true; }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested && sampleStartedMs is null) { failure = "probe.preparationTimedOut"; }
        catch (Exception error) { failure = SafeCodes.Of(error); }
        finally
        {
            context.Show("正在停止", "正在结束采集、停止库存观察并释放捕获。", null);
            if (run is not null) { await run.DisposeAsync(); context.Untrack(run); }
            if (operationId is not null && host.Observations is not null)
                try
                {
                    using var stopLimit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var sdk = host.Client(); var id = operationId;
                    if (!SqliteStore.IsTerminal((await ObservationHost.RetryAsync(() => sdk.GetInventoryObservationAsync(id, stopLimit.Token), stopLimit.Token)).State.KnownValue))
                        await ObservationHost.RetryAsync(() => sdk.StopInventoryObservationAsync(id, stopLimit.Token), stopLimit.Token);
                    finalState = (await host.WaitOperationAsync(() => sdk.GetInventoryObservationAsync(id, stopLimit.Token),
                        value => SqliteStore.IsTerminal(value.State.KnownValue), stopLimit.Token)).State.KnownValue?.ToString();
                }
                catch (Exception error) { host.CleanupErrors.Add("inventory.stop:" + SafeCodes.Of(error)); }
            await host.CleanupAsync(afterHostStop: () => inventoryDiagnostics = host.Observations?.InventoryDiagnostics());
        }
        if (run?.Result is { } line)
            try { captureResult = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { captureResult = null; }
        var reason = captureResult?["reason"]?.GetValue<string>();
        var summaryPath = Path.Combine(output, "summary.json");
        var sampleComplete = exitCode == 0 && reason is "capture.durationComplete" or "capture.byteBudget" or "capture.packetBudget" && File.Exists(summaryPath);
        ReloginSampleAnalysis? analysis = null; string? analysisFailure = null;
        if (Directory.Exists(output) && File.Exists(Path.Combine(output, "packets.jsonl")))
        {
            context.Show("正在分析样本", "正在读取客户端布局并离线重组样本，约 10 秒。", null);
            try
            {
                using var analysisLimit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var layout = layoutSource(analysisLimit.Token);
                analysis = ReloginSampleAnalyzer.Analyze(output, ReloginAnalysisProtocol.From(layout), analysisLimit.Token);
                context.Mark("sample.analyzed");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
                FormatException or OperationCanceledException or UnauthorizedAccessException or KeyNotFoundException)
            { analysisFailure = SafeCodes.Of(error); }
        }
        var frames = InventoryFrameTimeline.Project(inventoryDiagnostics, context);
        failure ??= !sampleComplete ? reason ?? "sample.captureFailed" : null;
        var accepted = failure is null && analysis is not null && host.Released;
        var report = JsonNodes.From(new
        {
            schemaVersion = 1, evidenceKind = Plan.EvidenceKind, checkedAtUtc = DateTimeOffset.UtcNow,
            captureProfile = environment.CaptureProfile, gameBuild = host.Game?.Session.GameBuild, timeScale = settings.TimeScale,
            phases = host.Phases, stageAtEnd = host.Stage, failure, stoppedByOperator = stopped,
            sample = new
            {
                exitCode, captureResult, outputDirectory = output, summaryPath = File.Exists(summaryPath) ? summaryPath : null,
                startedAtMs = sampleStartedMs,
                alignment = "sampleAnalysis 的时点相对采集进程启动（约在 operatorTimeline 的 sample.started 之后数百毫秒）；inventoryFrames 与 operatorTimeline 相对点击开始",
            },
            steps = Steps.Select(step => new { atSeconds = step.AtSeconds, step.Stage, step.Instruction }).ToArray(),
            inventory = new
            {
                finalState, snapshotStates,
                fullSync = InventoryFrameTimeline.Window(frames, 0, null),
                frames, counts = inventoryDiagnostics?.Counts,
            },
            sampleAnalysis = analysis, analysisFailure,
            sharedObservation = host.Shared, memory = host.MemoryReport(), release = host.ReleaseReport(), totalElapsedMs = context.ElapsedMs,
        });
        report["acceptance"] = new JsonObject { ["accepted"] = accepted, ["released"] = host.Released };
        var outcome = stopped ? ScenarioResult.Cancelled : failure is "probe.preparationTimedOut" ? ScenarioResult.TimedOut
            : failure is null ? ScenarioResult.Completed : ScenarioResult.Failed;
        var summary = accepted
            ? $"样本已分析：{analysis!.Connections.Count} 条连接，登录数据 {analysis.Events.Count(value => value.Kind == "login")} 次，身份关联 {analysis.Events.Count(value => value.Kind == "profile")} 次"
            : "结果：" + (failure ?? analysisFailure ?? "样本未完整");
        return new(outcome, accepted, summary, report);
    }

    /// <summary>实机分析使用的客户端布局：整读 DLL 与 metadata 后立即压缩回收，与 Worker 布局准备一致。</summary>
    public static ClientCaptureLayout ReadClientLayout(string clientRoot, CancellationToken ct)
    {
        var layout = ClientLayoutReader.Read(clientRoot, ct);
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return layout;
    }
}
