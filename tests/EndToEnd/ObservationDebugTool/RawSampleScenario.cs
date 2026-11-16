using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Platform.Windows;

namespace MabiTool.ObservationDebugTool;

/// <summary>原始下行 TCP 样本采集，用于尚无 Provider 的协议研究；样本只写入 artifacts/tmp 下私有目录。</summary>
public interface IRawSampleCapture
{
    string? Unavailable { get; }
    /// <param name="trackProcessConnections">true 时跟随游戏进程全部 TCP 连接（不限端口）并写 connections.json，供重新登录样本使用。</param>
    Task<IRawSampleRun> StartAsync(string outputDirectory, int durationSeconds, CancellationToken ct, bool trackProcessConnections = false);
}

public interface IRawSampleRun : IAsyncDisposable
{
    int ProcessId { get; }
    Task<int> Completion { get; }
    /// <summary>采集脚本结束时输出的最后一行 JSON 摘要；被停止时为 null。</summary>
    string? Result { get; }
}

/// <summary>调用 scripts/maintenance/capture-combat-evidence.py：固定游戏 PID 与创建身份，跟随换图/换频道连接。</summary>
public sealed class PythonRawSampleCapture(string python, string script, string clientRoot) : IRawSampleCapture
{
    public string? Unavailable => !File.Exists(python) ? "sample.pythonMissing" : !File.Exists(script) ? "sample.scriptMissing" : null;

    public async Task<IRawSampleRun> StartAsync(string outputDirectory, int durationSeconds, CancellationToken ct, bool trackProcessConnections = false)
    {
        var executable = Path.Combine(clientRoot, "MabinogiMobile.exe");
        var games = Process.GetProcessesByName("MabinogiMobile").Where(process =>
        {
            try { return string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }).ToArray();
        try
        {
            if (games.Length != 1) throw new ProbeException("probe.gameSelectionAmbiguous");
            ct.ThrowIfCancellationRequested();
            var child = WindowsOwnedChildProcess.Start(python, ["-I", "-B", script, "--pid", games[0].Id.ToString(CultureInfo.InvariantCulture),
                "--game-exe", executable, "--output-dir", outputDirectory, "--duration-seconds", durationSeconds.ToString(CultureInfo.InvariantCulture),
                trackProcessConnections ? "--track-process-connections" : "--follow-connections"]);
            await Task.CompletedTask;
            return new Run(child);
        }
        finally { foreach (var game in games) game.Dispose(); }
    }

    private sealed class Run : IRawSampleRun
    {
        private readonly WindowsOwnedChildProcess child;
        private readonly Task<string> output;
        private readonly Task<string> errors;
        public Run(WindowsOwnedChildProcess child)
        {
            this.child = child;
            output = child.StandardOutput.ReadToEndAsync();
            errors = child.StandardError.ReadToEndAsync();
            Completion = Complete();
        }
        public int ProcessId => child.Id;
        public Task<int> Completion { get; }
        public string? Result { get; private set; }
        private async Task<int> Complete()
        {
            var code = await child.WaitForExitAsync();
            var text = await output;
            _ = await errors;
            Result = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return code;
        }
        public ValueTask DisposeAsync() => child.DisposeAsync();
    }
}

public sealed record RawSampleDefinition(string Id, string Title, string Tasks, string Purpose, int DurationSeconds,
    IReadOnlyList<(double AtSeconds, string Stage, string Instruction)> Steps);

/// <summary>按时间线提示操作者执行游戏动作，同时记录每步相对采集开始的时点，供离线对齐协议帧。</summary>
public sealed class RawSampleScenario(RawSampleDefinition definition, IRawSampleCapture? capture) : IDebugScenario
{
    public static IReadOnlyList<RawSampleDefinition> Definitions { get; } =
    [
        new("sample-music-solo", "样本：独奏选曲、换乐器、播放与停止", "unified-modular-backend 4.6",
            "当前台服构建的乐谱/乐器/播放协议字段；2026-11-14 样本已核对 170415/170416/170703/170704 映射，复采用于构建更新后的复核", 150,
            [
                (0, "站定", "站在安全区域原地不动，不要打开任何界面。"),
                (15, "打开演奏界面", "打开演奏（乐谱）界面，浏览后选中一首已保存的乐谱，不要开始演奏。"),
                (35, "更换乐器", "关闭演奏界面，在背包中把当前乐器换成另一件乐器（装备另一把乐器）。"),
                (55, "开始独奏", "重新打开演奏界面，选择同一首乐谱并开始独奏，让它持续播放。"),
                (95, "手动停止", "在播放中途手动停止演奏。"),
                (110, "换曲并播放", "选择另一首乐谱（如有循环选项请开启）开始独奏。"),
                (135, "停止并站定", "停止演奏，关闭界面，原地站定直到倒计时结束。"),
            ]),
        new("sample-ensemble", "样本：附近合奏与手动停止", "unified-modular-backend 4.6、4.7",
            "合奏成员（20608）、开始与手动停止的当前构建协议；演奏自动循环，一轮结束按开始时间与乐谱时长推算。需要一名队友或附近正在合奏的玩家", 180,
            [
                (0, "靠近合奏对象", "与队友会合，或走到附近正在合奏的玩家旁边，原地站定。"),
                (20, "发起或加入合奏", "由一方发起合奏邀请，另一方加入；若观察他人合奏，保持站在附近。"),
                (45, "开始合奏", "开始合奏并完整演奏，期间不要移动。"),
                (130, "停止合奏", "演奏会自动循环，在此手动停止，不要开始下一首。"),
                (155, "解散并站定", "退出合奏状态，原地站定直到倒计时结束。"),
            ]),
        new("sample-environment", "样本：时间、天气、频道与区域", "unified-modular-backend 4.7",
            "游戏内时间、天气、频道与区域变化的当前构建协议编号与字段", 150,
            [
                (0, "站定", "在野外区域原地站定，记下当前频道号和游戏内时间。"),
                (15, "查看地图与天气", "打开大地图，停留约 10 秒查看区域名、时间和天气，然后关闭。"),
                (35, "更换频道", "通过频道选择切换到另一个频道，进入后原地站定。"),
                (75, "切换区域", "移动或传送到另一个区域（进入新地图），进入后原地站定。"),
                (115, "等待变化", "原地站定；如游戏内时间或天气正好变化，记下变化时点。"),
            ]),
    ];

    public ScenarioPlan Plan => new(definition.Id, definition.Title, definition.Tasks, "native-raw-sample-capture", "raw-" + definition.Id,
        30, definition.DurationSeconds,
        [$"点击“开始”后约 5 秒内开始采集原始下行 TCP，持续 {definition.DurationSeconds} 秒；用途：{definition.Purpose}。",
            .. definition.Steps.Select(step => $"第 {step.AtSeconds:0} 秒：{step.Stage}——{step.Instruction}"),
            "倒计时结束自动停止；任何时候都可以点“停止”（被停止的样本缺少摘要文件）。",
            "原始样本只写入 artifacts/tmp 下的私有目录，不入库；报告只记录样本目录与哈希摘要。"],
        capture is null ? "需要 --python 参数指定 Python 3.12 解释器" : capture.Unavailable) { CleanupAllowanceSeconds = 120 };

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        if (capture is null) throw new ProbeException("sample.pythonNotConfigured");
        var output = Path.Combine(context.ScratchDirectory, "capture");
        Directory.CreateDirectory(context.ScratchDirectory);
        string? failure = null; int? exitCode = null; JsonNode? result = null;
        var stopped = false;
        context.Show("正在启动采集", "正在启动原始样本采集，请保持游戏窗口运行。", null);
        IRawSampleRun? run = null;
        var steps = definition.Steps.Select(step => new TimelineStep(step.AtSeconds, step.Stage, step.Instruction)).ToArray();
        try
        {
            // 游戏会同时开关多条连接（含多条 1111），跟随模式的范围变更预算撑不过整段采样，改为持续跟踪进程全部连接。
            run = context.Track(await capture.StartAsync(output, definition.DurationSeconds, context.Stopped, trackProcessConnections: true));
            context.ObserveProcess(run.ProcessId);
            context.Mark("sample.started");
            using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.Stopped);
            var timeline = ScenarioTimeline.RunAsync(context, steps, definition.DurationSeconds, ended.Token);
            var finished = await Task.WhenAny(timeline, run.Completion);
            if (finished == run.Completion && !timeline.IsCompleted)
            {
                // 采集脚本先结束（预算、连接变化或错误）：停止提示，按脚本结果记录。
                ended.Cancel();
                try { await timeline; } catch (OperationCanceledException) { }
                context.Mark("sample.endedBeforeTimeline");
            }
            else await timeline;
            exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30), context.Stopped);
        }
        catch (OperationCanceledException) when (context.Stopped.IsCancellationRequested) { failure = "probe.interrupted"; stopped = true; }
        catch (Exception error) { failure = SafeCodes.Of(error); }
        finally
        {
            context.Show("正在停止", "正在结束采集进程。", null);
            if (run is not null) { await run.DisposeAsync(); context.Untrack(run); }
        }
        if (run?.Result is { } line)
            try { result = JsonNode.Parse(line); } catch (JsonException) { result = null; }
        var reason = result?["reason"]?.GetValue<string>();
        var summaryPath = Path.Combine(output, "summary.json");
        var completedReasons = new[] { "capture.durationComplete", "capture.byteBudget", "capture.packetBudget" };
        var accepted = failure is null && exitCode == 0 && reason is not null && completedReasons.Contains(reason) && File.Exists(summaryPath);
        failure ??= exitCode is not 0 ? reason ?? "sample.captureFailed" : null;
        var report = JsonNodes.From(new
        {
            schemaVersion = 1, evidenceKind = Plan.EvidenceKind, checkedAtUtc = DateTimeOffset.UtcNow, sampleId = definition.Id,
            purpose = definition.Purpose, requestedDurationSeconds = definition.DurationSeconds, failure, stoppedByOperator = stopped,
            exitCode, captureResult = result, outputDirectory = output, summaryPath = File.Exists(summaryPath) ? summaryPath : null,
            steps = definition.Steps.Select(step => new { atSeconds = step.AtSeconds, step.Stage, step.Instruction }).ToArray(),
            alignment = "operatorTimeline 的 atMs 相对点击开始；采集进程在 sample.started 之后启动，原始包时点见样本目录 summary.json",
            totalElapsedMs = context.ElapsedMs,
        });
        report["acceptance"] = new JsonObject { ["accepted"] = accepted };
        var outcome = stopped ? ScenarioResult.Cancelled : failure is null ? ScenarioResult.Completed : ScenarioResult.Failed;
        var summary = accepted ? $"样本已保存：{output}" : "结果：" + (failure ?? reason ?? "样本未完整");
        return new(outcome, accepted, summary, report);
    }
}
