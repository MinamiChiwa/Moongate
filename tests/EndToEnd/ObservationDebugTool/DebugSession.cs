using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MabiTool.ObservationDebugTool;

/// <summary>面板展示的场景计划：开始时点固定为“点击开始”，准备上限与采集时长均按单调时钟计。</summary>
public sealed record ScenarioPlan(string Id, string Title, string Tasks, string EvidenceKind, string ReportPrefix,
    int PreparationLimitSeconds, int RunSeconds, IReadOnlyList<string> OperatorActions, string? Unavailable = null)
{
    /// <summary>超出准备与采集预算后由会话强制停止的余量，覆盖资源释放和核对问答。</summary>
    public int CleanupAllowanceSeconds { get; init; } = 900;
}

public sealed record ScenarioDisplay(string Stage, string Instruction, double? RemainingSeconds, string? Next = null);

public sealed record ScenarioResult(string Outcome, bool Accepted, string Summary, JsonObject Report)
{
    public const string Completed = "completed", Failed = "failed", Cancelled = "cancelled", TimedOut = "timedOut";
}

public interface IDebugScenario
{
    ScenarioPlan Plan { get; }
    Task<ScenarioResult> RunAsync(ScenarioContext context);
}

public interface IReportSink
{
    string Write(string prefix, JsonObject report);
}

public sealed record ScenarioListItem(string Id, string Title, string Status);
public sealed record CompletedScenario(string Id, string Title, string Outcome, bool Accepted, string Summary, string? ReportPath, string? ReportError);
public sealed record TimelineEntry(string Code, double AtMs, DateTimeOffset AtUtc, string? Detail);

public sealed record DebugSnapshot(string State, bool Preview, string? SelectedId, ScenarioPlan? SelectedPlan,
    IReadOnlyList<ScenarioListItem> Scenarios, string? RunningId, ScenarioDisplay? Display,
    string? ActionLabel, bool ActionEnabled, string? Question, string? QuestionDetail, IReadOnlyList<string> Options,
    bool CanStart, bool CanStop, bool CanExit, IReadOnlyList<CompletedScenario> Results);

/// <summary>
/// 一次启动内依次执行多个场景：每个场景由操作者点击开始，结束后写一份报告并回到空闲。
/// 停止、超时和场景异常都会先释放场景登记的资源，再写报告。
/// </summary>
public sealed class DebugSession
{
    public const string Idle = "idle", Running = "running", Stopping = "stopping", Exited = "exited";
    private readonly object gate = new();
    private readonly IReadOnlyList<IDebugScenario> scenarios;
    private readonly IReportSink reports;
    private readonly TimeProvider clock;
    private readonly Func<IReadOnlyDictionary<string, object?>> environment;
    private readonly string scratchRoot;
    private readonly bool preview;
    private readonly Channel<IDebugScenario> starts = Channel.CreateBounded<IDebugScenario>(1);
    private readonly List<CompletedScenario> results = [];
    private readonly Dictionary<string, string> statuses = new(StringComparer.Ordinal);
    private string state = Idle;
    private string? selected;
    private ScenarioContext? current;
    private CancellationTokenSource? stop;
    private bool exitRequested;
    private int sequence;

    public DebugSession(IReadOnlyList<IDebugScenario> scenarios, IReportSink reports, TimeProvider clock, string scratchRoot,
        Func<IReadOnlyDictionary<string, object?>> environment, bool preview = false)
    {
        if (scenarios.Count == 0 || scenarios.Select(value => value.Plan.Id).Distinct(StringComparer.Ordinal).Count() != scenarios.Count)
            throw new ArgumentException("scenario.idsInvalid");
        this.scenarios = scenarios; this.reports = reports; this.clock = clock; this.environment = environment;
        this.scratchRoot = scratchRoot; this.preview = preview; selected = scenarios[0].Plan.Id;
    }

    public IReadOnlyList<CompletedScenario> Results { get { lock (gate) return results.ToArray(); } }
    public string State { get { lock (gate) return state; } }

    public bool Select(string id)
    {
        lock (gate)
        {
            if (state != Idle || scenarios.All(value => value.Plan.Id != id)) return false;
            selected = id; return true;
        }
    }

    /// <summary>点击开始即为采集开始时点；同一时刻只运行一个场景，重复点击被忽略。</summary>
    public bool Start()
    {
        lock (gate)
        {
            var scenario = scenarios.FirstOrDefault(value => value.Plan.Id == selected);
            if (preview || exitRequested || state != Idle || scenario is null || scenario.Plan.Unavailable is not null) return false;
            if (!starts.Writer.TryWrite(scenario)) return false;
            state = Running; return true;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? source;
        lock (gate)
        {
            if (state != Running) return;
            state = Stopping; source = stop;
        }
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>空闲时立即退出；运行中先停止当前场景，资源释放与报告完成后退出。</summary>
    public void RequestExit()
    {
        lock (gate)
        {
            exitRequested = true;
            if (state == Idle) { state = Exited; starts.Writer.TryComplete(); return; }
        }
        Stop();
    }

    public void Act() { ScenarioContext? value; lock (gate) value = current; value?.InvokeOffer(); }
    public bool Answer(string option) { ScenarioContext? value; lock (gate) value = current; return value?.Answer(option) == true; }

    public DebugSnapshot Snapshot()
    {
        lock (gate)
        {
            var plan = scenarios.FirstOrDefault(value => value.Plan.Id == selected)?.Plan;
            var list = scenarios.Select(value => new ScenarioListItem(value.Plan.Id, value.Plan.Title,
                value.Plan.Unavailable is not null ? "不可用" : statuses.GetValueOrDefault(value.Plan.Id, "待执行"))).ToArray();
            var running = current;
            var offer = running?.CurrentOffer();
            var question = running?.CurrentQuestion();
            return new(state, preview, selected, plan, list, running?.Plan.Id, running?.Display(),
                offer?.Label, offer?.Enabled == true && state == Running, question?.Text, question?.Detail, question?.Options ?? [],
                !preview && !exitRequested && state == Idle && plan?.Unavailable is null, state == Running, state is Idle or Exited,
                results.ToArray());
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await foreach (var scenario in starts.Reader.ReadAllAsync(ct))
        {
            var outcome = await ExecuteAsync(scenario).ConfigureAwait(false);
            lock (gate)
            {
                results.Add(outcome);
                statuses[scenario.Plan.Id] = outcome.Outcome switch
                {
                    ScenarioResult.Completed when outcome.Accepted => "已通过",
                    ScenarioResult.Completed => "已完成（未达验收）",
                    ScenarioResult.Cancelled => "已停止",
                    ScenarioResult.TimedOut => "已超时",
                    _ => "失败"
                };
                current = null; stop?.Dispose(); stop = null;
                state = exitRequested ? Exited : Idle;
                if (exitRequested) { starts.Writer.TryComplete(); }
            }
        }
    }

    private async Task<CompletedScenario> ExecuteAsync(IDebugScenario scenario)
    {
        var plan = scenario.Plan;
        var number = Interlocked.Increment(ref sequence);
        var scratch = Path.Combine(scratchRoot, plan.Id + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var source = new CancellationTokenSource();
        var context = new ScenarioContext(plan, source.Token, clock, scratch);
        lock (gate) { current = context; stop = source; if (state == Stopping) source.Cancel(); }
        using var load = new LoadSampler(clock, context.ObservedProcesses);
        load.Start();
        // 准备与采集预算之外再留释放余量；超出时按超时停止，防止异常场景无限占用捕获。
        var ceiling = TimeSpan.FromSeconds(plan.PreparationLimitSeconds + plan.RunSeconds + plan.CleanupAllowanceSeconds);
        using var guard = new CancellationTokenSource(ceiling, clock);
        using var guardRegistration = guard.Token.Register(() =>
        {
            context.Mark("tool.scenarioDeadline");
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        });
        context.Mark("scenario.started");
        ScenarioResult result;
        try { result = await scenario.RunAsync(context).ConfigureAwait(false); }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            result = new(guard.IsCancellationRequested ? ScenarioResult.TimedOut : ScenarioResult.Cancelled, false,
                guard.IsCancellationRequested ? "超过场景总期限，已停止" : "操作者已停止", new JsonObject());
        }
        catch (Exception error)
        {
            result = new(ScenarioResult.Failed, false, "场景异常：" + SafeCodes.Of(error),
                new JsonObject { ["failure"] = SafeCodes.Of(error) });
        }
        var cleanupErrors = await context.DisposeTrackedAsync().ConfigureAwait(false);
        context.Mark("scenario.finished", result.Outcome);
        load.Stop();
        var report = result.Report;
        report["scenario"] = new JsonObject
        {
            ["id"] = plan.Id, ["title"] = plan.Title, ["tasks"] = plan.Tasks, ["outcome"] = result.Outcome,
            ["accepted"] = result.Accepted, ["summary"] = result.Summary,
            ["preparationLimitSeconds"] = plan.PreparationLimitSeconds, ["runSeconds"] = plan.RunSeconds,
            ["operatorActions"] = new JsonArray(plan.OperatorActions.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["toolCleanupErrors"] = new JsonArray(cleanupErrors.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        };
        report.TryAdd("evidenceKind", plan.EvidenceKind);
        report.TryAdd("schemaVersion", 1);
        report["operatorTimeline"] = System.Text.Json.JsonSerializer.SerializeToNode(context.Timeline);
        var environmentNode = System.Text.Json.JsonSerializer.SerializeToNode(environment()) as JsonObject ?? [];
        environmentNode["startedAtUtc"] = context.StartedAtUtc;
        environmentNode["finishedAtUtc"] = clock.GetUtcNow();
        environmentNode["elapsedMs"] = context.ElapsedMs;
        environmentNode["load"] = System.Text.Json.JsonSerializer.SerializeToNode(load.Summary());
        report["environment"] = environmentNode;
        string? path = null, reportError = null;
        try { path = reports.Write(plan.ReportPrefix, report); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { reportError = SafeCodes.Of(error); }
        source.Dispose();
        return new(plan.Id, plan.Title, result.Outcome, result.Accepted, result.Summary, path, reportError);
    }
}

public sealed record OfferState(string Label, bool Enabled);
public sealed record QuestionState(string Text, string? Detail, IReadOnlyList<string> Options);

/// <summary>场景运行时与面板之间的唯一通道：显示、操作者按钮、核对问答、时间线和兜底资源释放。</summary>
public sealed class ScenarioContext
{
    private readonly object gate = new();
    private readonly List<TimelineEntry> timeline = [];
    private readonly List<IAsyncDisposable> tracked = [];
    private readonly HashSet<int> processes = [];
    private Func<ScenarioDisplay> display;
    private (string Label, Func<bool> Enabled, Action Click)? offer;
    private (QuestionState Question, TaskCompletionSource<string> Answer)? question;

    internal ScenarioContext(ScenarioPlan plan, CancellationToken stopped, TimeProvider clock, string scratch)
    {
        Plan = plan; Stopped = stopped; Clock = clock; ScratchDirectory = scratch;
        StartedTimestamp = clock.GetTimestamp(); StartedAtUtc = clock.GetUtcNow();
        display = () => new("正在启动", "正在准备场景。", null);
    }

    public ScenarioPlan Plan { get; }
    public CancellationToken Stopped { get; }
    public TimeProvider Clock { get; }
    public string ScratchDirectory { get; }
    public long StartedTimestamp { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public double ElapsedMs => Clock.GetElapsedTime(StartedTimestamp).TotalMilliseconds;
    /// <summary>把 Host/Worker 的系统单调毫秒换算为相对点击开始的毫秒（同一 TimeProvider 时钟）。</summary>
    public double SinceStartMs(double monotonicMs) => monotonicMs - StartedTimestamp * (1000d / Clock.TimestampFrequency);
    public IReadOnlyList<TimelineEntry> Timeline { get { lock (gate) return timeline.ToArray(); } }

    public void Show(string stage, string instruction, long? deadlineTimestamp = null, string? next = null) =>
        Show(() => new(stage, instruction, deadlineTimestamp is { } end ? Remaining(end) : null, next));
    public void Show(Func<ScenarioDisplay> live) { lock (gate) display = live; }
    public double Remaining(long deadlineTimestamp) =>
        Math.Max(0, (deadlineTimestamp - Clock.GetTimestamp()) / (double)Clock.TimestampFrequency);
    public long DeadlineAfter(double seconds) => StartedTimestampNow() + (long)(seconds * Clock.TimestampFrequency);
    private long StartedTimestampNow() => Clock.GetTimestamp();

    internal ScenarioDisplay Display()
    {
        Func<ScenarioDisplay> live; lock (gate) live = display;
        try { return live(); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        { return new("状态读取失败", SafeCodes.Of(error), null); }
    }

    public void Offer(string label, Func<bool> enabled, Action click) { lock (gate) offer = (label, enabled, click); }
    public void ClearOffer() { lock (gate) offer = null; }
    internal OfferState? CurrentOffer()
    {
        (string Label, Func<bool> Enabled, Action Click)? value; lock (gate) value = offer;
        return value is { } item ? new(item.Label, item.Enabled()) : null;
    }
    internal void InvokeOffer()
    {
        (string Label, Func<bool> Enabled, Action Click)? value; lock (gate) value = offer;
        if (value is { } item && item.Enabled()) { Mark("operator.action", item.Label); item.Click(); }
    }

    /// <summary>核对问答；停止或超时后返回 null，不阻塞资源释放。</summary>
    public async Task<string?> AskAsync(string text, string? detail, IReadOnlyList<string> options, TimeSpan limit)
    {
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) question = (new(text, detail, options), answer);
        Mark("question.asked", text);
        try
        {
            using var timeout = new CancellationTokenSource(limit, Clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, Stopped);
            var value = await answer.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            Mark("question.answered", value); return value;
        }
        catch (OperationCanceledException) { Mark("question.unanswered"); return null; }
        finally { lock (gate) question = null; }
    }
    internal QuestionState? CurrentQuestion() { lock (gate) return question?.Question; }
    internal bool Answer(string option)
    {
        (QuestionState Question, TaskCompletionSource<string> Answer)? value; lock (gate) value = question;
        return value is { } item && item.Question.Options.Contains(option) && item.Answer.TrySetResult(option);
    }

    public void Mark(string code, string? detail = null)
    { lock (gate) timeline.Add(new(code, ElapsedMs, Clock.GetUtcNow(), detail)); }

    /// <summary>登记兜底释放的资源；场景正常释放后应 Untrack。</summary>
    public T Track<T>(T resource) where T : IAsyncDisposable { lock (gate) tracked.Add(resource); return resource; }
    public void Untrack(IAsyncDisposable resource) { lock (gate) tracked.Remove(resource); }
    public void ObserveProcess(int processId) { lock (gate) processes.Add(processId); }
    internal IReadOnlyCollection<int> ObservedProcesses() { lock (gate) return processes.ToArray(); }

    internal async Task<IReadOnlyList<string>> DisposeTrackedAsync()
    {
        IAsyncDisposable[] values;
        lock (gate) { values = tracked.AsEnumerable().Reverse().ToArray(); tracked.Clear(); offer = null; }
        List<string> errors = [];
        foreach (var value in values)
        {
            try { await value.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(value.GetType().Name + ":" + SafeCodes.Of(error)); }
        }
        return errors;
    }
}

public sealed class ScenarioTimeoutException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class ProbeException(string code) : Exception(code) { public string Code { get; } = code; }

public static class SafeCodes
{
    public static string Of(Exception error) => error switch
    {
        ProbeException typed => typed.Code,
        ScenarioTimeoutException typed => typed.Code,
        MabiTool.Infrastructure.OperationException typed => typed.Code,
        MabiTool.Infrastructure.Workers.WorkerException typed => typed.Code,
        MabiTool.Client.MabiToolApiException typed => typed.Problem?.Code ?? "api.status" + typed.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        OperationCanceledException => "probe.cancelled",
        TimeoutException => "probe.cleanupTimeout",
        _ => error.GetType().Name
    };
}
