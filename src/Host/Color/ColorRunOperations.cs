using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Color;

/// <summary>宿主持有运行及取消；交互续约与持久化执行分别管理。</summary>
public sealed class ColorRunOperations(OperationStore operations, ModuleRuntime modules, SystemInfo info,
    TimeProvider clock, ColorRunWorkerFactory? factory = null, LocalAuthService? auth = null, DiagnosticTrace? trace = null,
    ColorWorkerPool? workers = null) : BackgroundService
{
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(1);
    private readonly object gate = new();
    private readonly CancellationTokenSource presenceStop = new();
    private Task? presenceMonitor;
    private Job? current;
    private volatile bool accepting;
    private double MonotonicMs => clock.GetTimestamp() * 1000d / clock.TimestampFrequency;

    private sealed class Job(string id, string runId, string owner, TimeProvider clock)
    {
        public string Id { get; } = id;
        public string RunId { get; } = runId;
        public string Owner { get; } = owner;
        public ColorInteractionPresenceTracker Presence { get; } = new(clock);
        public CancellationTokenSource Stop { get; } = new();
        public Action? Abort { get; set; }
        public object? Active { get; set; }
        public string? StopReason { get; set; }
        public bool Finished { get; set; }
    }

    internal (bool Running, int Queued, bool Accepting, bool Configured) DiagnosticState()
    {
        lock (gate) return (current is { Finished: false }, queue.Reader.Count, accepting, factory is not null);
    }

    public async Task<OperationSnapshot> SubmitAsync(string principal, ColorRunStartRequest request, string? key, CancellationToken ct)
    {
        ColorRunWorkerFactory.Validate(request);
        // 深拷贝调用方集合，保证嵌入式调用和 HTTP 调用具有相同快照语义。
        request = JsonSerializer.Deserialize<ColorRunStartRequest>(JsonSerializer.Serialize(request, ContractJson.Options), ContractJson.Options)!;
        await admission.WaitAsync(ct);
        try
        {
            if (!accepting) throw new OperationException("provider.unavailable", "染色执行器尚未启动或正在关闭");
            bool available;
            lock (gate) available = current is null && factory?.Workers.IsBusy != true;
            var result = await operations.AdmitColorAsync(principal, request, key,
                () => factory?.Resolve(request) ?? throw new OperationException("capability.unavailable",
                    ColorRunWorkerFactory.UnavailableMessage(ColorRunWorkerFactory.UnavailableReason(workers?.Options) ?? "unknown")),
                info.InstanceId, MonotonicMs + TimeSpan.FromMinutes(5).TotalMilliseconds, available, ct);
            if (result.Created)
            {
                var job = new Job(result.Snapshot.Id, result.Snapshot.Result!.Value.GetProperty("id").GetString()!, principal, clock);
                job.Presence.Begin(principal);
                lock (gate) current = job;
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理运行无法进入保留队列");
            }
            return result.Snapshot;
        }
        finally { admission.Release(); }
    }

    public ColorRun Get(string runId, string principal, bool owner) => operations.ColorRun(operations.ColorOperationId(runId), principal, owner);
    private ColorRun OperationRun(string id) => operations.ColorRun(id, "owner", true);

    private bool CanInteract(string principal) => auth?.HasPermission(principal, ApiPermissions.ColorControl) ?? true;

    public ColorInteractionPresence Presence(string runId, string principal, bool owner)
    {
        lock (gate)
        {
            if (current is not { } job || job.RunId != runId || job.Stop.IsCancellationRequested || job.Finished)
                throw new OperationException("color.runInactive", "染色运行已结束或已取消");
            if (!owner && job.Owner != principal) throw new OperationException("resource.notFound", "染色运行不存在");
            if (!CanInteract(principal)) throw new OperationException("permission.denied", "需要染色交互权限");
            if (!job.Presence.HasLiveClient(CanInteract))
            {
                StopJob(job, "frontend-lost");
                throw new OperationException("color.runInactive", "交互租约已过期");
            }
            try { job.Presence.Renew(principal, CanInteract); }
            catch (OperationException error) when (error.Code == "color.runInactive")
            { StopJob(job, "frontend-lost"); throw; }
            return new(runId, info.InstanceId, ColorInteractionPresenceTracker.LifetimeMs);
        }
    }

    public void EmergencyStop()
    {
        try { factory?.EmergencyStop(); }
        finally { lock (gate) if (current is { } job) StopJob(job, "emergency-stop"); }
    }

    private static void StopJob(Job job, string reason)
    {
        job.StopReason ??= reason;
        try { job.Abort?.Invoke(); }
        finally { job.Stop.Cancel(); }
    }

    private async Task MonitorPresenceAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(presenceStop.Token))
                lock (gate)
                    if (current is { } job && !job.Finished && !job.Stop.IsCancellationRequested && !job.Presence.HasLiveClient(CanInteract))
                    {
                        // 取消已保证执行；端口释放异常交由执行路径收尾，监控继续服务后续运行。
                        try { StopJob(job, "frontend-lost"); }
                        catch { }
                    }
        }
        catch (OperationCanceledException) when (presenceStop.IsCancellationRequested) { }
    }

    public async Task<OperationSnapshot> CancelAsync(string id, string principal, bool owner, string? reason, CancellationToken ct)
    {
        var operation = operations.Get(id, principal, owner);
        if (operation.Kind != "color.search") return await operations.CancelAsync(id, principal, owner, reason, ct);
        if (reason is { Length: > 256 } || reason?.Any(char.IsControl) == true)
            throw new OperationException("request.invalid", "取消原因最多 256 字符且不能包含控制字符");
        // 输入权限先撤销，数据库写入不处于紧急停止路径。
        lock (gate)
            if (current is { } job && job.Id == id) StopJob(job, reason ?? "cancelled");
        return await operations.CancelAsync(id, principal, owner, reason, ct);
    }

    public Task<ColorInteractionReceipt> SelectAsync(string id, string principal, bool owner,
        ColorCandidateSelectionRequest request, CancellationToken ct) =>
        InteractAsync(id, principal, owner, "select", request.ExpectedRevision, request.BatchId, request.CandidateId, ct);

    public Task<ColorInteractionReceipt> RetryAsync(string id, string principal, bool owner,
        ColorRetryRequest request, CancellationToken ct) => InteractAsync(id, principal, owner, "retry", request.ExpectedRevision, null, null, ct);

    private async Task<ColorInteractionReceipt> InteractAsync(string id, string principal, bool owner, string action,
        UInt64String revision, string? batch, string? candidate, CancellationToken ct)
    {
        id = Get(id, principal, owner).OperationId;
        if (action == "select" && (batch is not { Length: >= 1 and <= 256 } || candidate is not { Length: >= 1 and <= 256 }))
            throw new OperationException("request.invalid", "需要有效批次及候选 ID");
        await commands.WaitAsync(ct);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new OperationException("capability.unavailable", "染色 Worker 需要 Windows");
            ActiveColorWorker active;
            lock (gate) active = current is { } job && job.Id == id && !job.Stop.IsCancellationRequested && job.Active is ActiveColorWorker value
                ? value : throw new OperationException("color.runInactive", "染色运行已结束或尚未就绪");
            var response = await active.Worker.InteractAsync(active.Call, action, revision.ToString(), batch, candidate, ct);
            if (!response.GetProperty("accepted").GetBoolean())
                throw new OperationException(response.GetProperty("code").GetString()!, "染色交互状态已变化，请读取当前运行");
            var state = ColorWorkerResults.ReadInteraction(response.GetProperty("interaction"));
            await operations.UpdateColorAsync(id, run => ApplyInteractionReceipt(run, state));
            return new(true, state);
        }
        catch (WorkerException error) { throw new OperationException(PublicCode(error.Code), "染色交互未完成，请查询运行状态"); }
        finally { commands.Release(); }
    }

    public override Task StartAsync(CancellationToken ct)
    {
        accepting = true;
        presenceMonitor = MonitorPresenceAsync();
        return base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            using var stop = stoppingToken.Register(() => { lock (gate) StopJob(job, "host-stopped"); });
            trace?.Emit(DiagnosticEvents.ColorRunStarted, operationId: job.Id);
            try
            {
                if (!OperatingSystem.IsWindows()) throw new OperationException("capability.unavailable", "染色 Worker 需要 Windows");
                await ExecuteJobAsync(job);
            }
            catch (Exception error)
            {
                lock (gate) job.Abort?.Invoke();
                var code = error switch { OperationException e => e.Code, WorkerException e => PublicCode(e.Code),
                    ResourceLeaseException e => e.Code, ModuleRuntimeException e => e.Code, _ => "operation.executionFailed" };
                try { await operations.UpdateColorAsync(job.Id, run => run with { StopReason = job.StopReason ?? code },
                    job.Stop.IsCancellationRequested ? OperationState.Cancelled : OperationState.Interrupted,
                    job.Stop.IsCancellationRequested ? null : ApiErrors.Create(code, "染色运行中断", job.Id)); }
                catch { accepting = false; queue.Writer.TryComplete(); }
            }
            finally
            {
                if (trace is not null)
                {
                    // 终态、停止原因、细分原因、精准准备停在的步骤与错误码，都是原因码，不含路径或游戏数据。
                    string? state = null, reason = job.StopReason, detail = null, stage = null, problem = null;
                    try
                    {
                        var operation = operations.Get(job.Id, "owner", true);
                        var ended = OperationStore.ReadColor(operation);
                        state = operation.State.RawValue; problem = operation.Problem?.Code;
                        reason ??= ended.StopReason ?? ended.Result?.StopReason;
                        detail = ended.Result?.StopDetail; stage = ended.Result?.FailedStage;
                    }
                    catch (Exception error) when (error is OperationException or StorageException) { }
                    trace.Emit(DiagnosticEvents.ColorRunEnded, new DiagnosticFields().Value("state", state).Value("reason", reason)
                        .Value("detail", detail).Value("stage", stage).Value("problem", problem), operationId: job.Id);
                }
                lock (gate) { if (ReferenceEquals(current, job)) current = null; }
                stop.Dispose();
                job.Stop.Dispose();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task ExecuteJobAsync(Job job)
    {
        var initial = await operations.BeginColorAsync(job.Id);
        if (initial is null || job.Stop.IsCancellationRequested)
        {
            await operations.UpdateColorAsync(job.Id, run => run with { StopReason = job.StopReason ?? "cancelled" }, OperationState.Cancelled);
            return;
        }
        var run = OperationRun(job.Id);
        await using var demand = await modules.AcquireAsync("color", job.Id, job.Stop.Token);
        ActiveColorWorker? active = null;
        WorkerFrame? terminal = null;
        Exception? failure = null;
        try
        {
            active = await factory!.StartAsync(run, job.Stop.Token);
            lock (gate)
            {
                job.Active = active; job.Abort = active.AbortInput;
                if (job.Stop.IsCancellationRequested) active.AbortInput();
            }
            var consume = ConsumeAsync(job.Id, active, run.DeadlineMonotonicMs);
            // 也观察通过内部 OperationStore 发起的取消；不依赖前端轮询或 SSE。
            while (!active.Call.Completion.IsCompleted && !consume.IsFaulted)
            {
                var operation = operations.Get(job.Id, "owner", true);
                if (operation.State.KnownValue == OperationState.CancelRequested ||
                    MonotonicMs >= OperationRun(job.Id).DeadlineMonotonicMs) job.Stop.Cancel();
                if (job.Stop.IsCancellationRequested)
                {
                    active.AbortInput();
                    await active.Worker.CancelAsync(active.Call);
                    break;
                }
                await Task.WhenAny(active.Call.Completion, consume, Task.Delay(TimeSpan.FromMilliseconds(100), clock));
            }
            await consume;
            terminal = await active.Call.Completion;
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (active is not null) await active.DisposeAsync();
            lock (gate) { job.Active = null; job.Abort = null; job.Finished = true; }
        }
        if (active is not null) await operations.UpdateColorAsync(job.Id, value => value, effect: active.InputEffect);
        // 终态发布前释放运行端口与游戏输入租约；健康计算通道可继续保温。
        if (failure is not null && !job.Stop.IsCancellationRequested) throw failure;
        if (job.Stop.IsCancellationRequested || terminal?.Kind == "cancelled")
        {
            await operations.UpdateColorAsync(job.Id, value => value with { StopReason = job.StopReason ?? value.StopReason ?? "cancelled" }, OperationState.Cancelled);
            return;
        }
        if (terminal?.Kind != "result")
        {
            // Worker 在一局开始前就停止时只回错误码（失焦、窗口变化、时限、端口失败）；原码保留为停止原因供界面说明。
            var raw = terminal?.Payload is { } payload && payload.TryGetProperty("code", out var value) ? value.GetString() : null;
            var code = raw is null ? "operation.executionFailed" : PublicCode(raw);
            var reason = raw is { Length: <= 64 } known && known.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? known : code;
            await operations.UpdateColorAsync(job.Id, value => value with { StopReason = reason },
                code is "input.focusChanged" or "input.bindingChanged" ? OperationState.Interrupted : OperationState.Failed,
                ApiErrors.Create(code, "染色引擎未完成运行", job.Id));
            return;
        }
        var result = ColorWorkerResults.ReadResult(terminal, run.RulesSnapshot, active!.Source, active.Generation);
        var (state, problem) = ColorRunTermination.Classify(result.StopReason, result.StopDetail,
            result.CurrentVerified is not null || result.BestMeasured is not null);
        await operations.UpdateColorAsync(job.Id, value => WithResult(value, result), state,
            problem is null ? null : ApiErrors.Create(problem, "染色运行未完成", job.Id, new Dictionary<string, JsonElement>
            {
                ["stopReason"] = JsonSerializer.SerializeToElement(result.StopReason),
                ["detail"] = JsonSerializer.SerializeToElement(result.StopDetail),
                ["stage"] = JsonSerializer.SerializeToElement(result.FailedStage)
            }));
    }

    [SupportedOSPlatform("windows")]
    private async Task ConsumeAsync(string id, ActiveColorWorker active, double maximumDeadline)
    {
        await foreach (var frame in active.Call.Events.ReadAllAsync())
        {
            if (frame.Kind != "event") continue;
            await operations.UpdateColorAsync(id, run => Project(run, frame, active, maximumDeadline), effect: active.InputEffect);
        }
    }

    [SupportedOSPlatform("windows")]
    private ColorRun Project(ColorRun run, WorkerFrame frame, ActiveColorWorker active, double maximumDeadline)
    {
        var kind = frame.Payload.GetProperty("kind").GetString();
        var data = frame.Payload.GetProperty("data");
        // 运行中的一局结果只更新读数；截图寻色出结果后仍在等待重试，运行停止原因到终态才写入。
        if (kind is "screenshot_result" or "native_result" && data.TryGetProperty("measurements", out _))
            return WithResult(run, ColorWorkerResults.ReadResult(frame with { Kind = "result", Payload = data },
                run.RulesSnapshot, active.Source, active.Generation)) with { StopReason = run.StopReason };
        if (kind == "finished" && data.TryGetProperty("result", out var raw))
            return WithResult(run, ColorWorkerResults.ReadResult(frame with { Kind = "result", Payload = raw },
                run.RulesSnapshot, active.Source, active.Generation)) with { StopReason = run.StopReason };
        if (kind == "native_candidate_ready")
        {
            var batch = ColorWorkerResults.ReadCandidates(frame, run.RulesSnapshot, clock.GetUtcNow());
            run = run with { Candidates = batch, CandidateBatchId = batch.Id, Phase = "waiting-user" };
        }
        if (data.TryGetProperty("interaction", out var interaction))
            run = MergeInteraction(run, ColorWorkerResults.ReadInteraction(interaction));
        if (kind is "native_retry_reset" or "native_candidate_selected") run = InvalidateCurrent(run);
        if (kind == "native_retry_available" && data.TryGetProperty("waiting_for_retry", out var waiting) && waiting.GetBoolean())
            run = InvalidateCurrent(run) with { Phase = "waiting-user" };
        if (kind == "screenshot_progress" && data.TryGetProperty("stage", out var stage))
        {
            var phase = stage.GetString();
            if (phase is "waiting" or "capture" or "search" or "refinement" or "positioning")
                run = run.Phase == phase ? run : run with { Phase = phase };
        }
        if (kind == "region_layout" && data.TryGetProperty("region_count", out var layout))
            run = WithRegionCount(run, layout.GetInt32());
        run = ProjectProgress(run, kind, data, frame.Payload.GetProperty("at_monotonic").GetDouble(), maximumDeadline, MonotonicMs, clock.GetUtcNow());
        // Worker 的绝对单调时钟不跨进程解释；只转换同一帧里的剩余预算。
        if (data.TryGetProperty("effective_deadline", out var end))
        {
            var seconds = end.GetDouble() - frame.Payload.GetProperty("at_monotonic").GetDouble();
            if (!double.IsFinite(seconds) || seconds > 300) throw new WorkerException("worker.resultInvalid");
            var deadline = Math.Min(maximumDeadline, MonotonicMs + Math.Max(0, seconds) * 1000);
            if (deadline < run.DeadlineMonotonicMs || kind == "native_deadline_corrected")
                run = run with { DeadlineMonotonicMs = deadline };
        }
        return run;
    }

    /// <summary>
    /// 阶段与步骤计数取原 screenshot/native_progress 事件；剩余时间按原浮窗 update_activity 的字段顺序，
    /// 依次取 effective_deadline、session_deadline_monotonic、remaining(+captured_monotonic)，最后取截图进度的 seconds。
    /// </summary>
    internal static ColorRun ProjectProgress(ColorRun run, string? kind, JsonElement data, double frameAt, double maximumDeadline,
        double now, DateTimeOffset at)
    {
        if (data.ValueKind != JsonValueKind.Object) return run;
        var progress = run.Progress;
        var changed = false;
        if (kind is "screenshot_progress" or "native_progress" && data.TryGetProperty("stage", out var raw) &&
            raw.ValueKind == JsonValueKind.String && raw.GetString() is { Length: >= 1 and <= 64 } stage)
        {
            var same = progress?.Stage == stage;
            int? total = Count(data, "total") is > 0 and var value ? value : null;
            int? current = total is null ? null : Count(data, "current") ?? 0;
            if (current > total) throw new WorkerException("worker.resultInvalid");
            // 每条阶段事件带本阶段的完整说明（原 ui_progress.progress_text 只看当前事件），没有说明时界面按阶段给默认说明。
            var status = Text(data, "refinement_status", 32);
            var verified = Flag(data, "verified") && Flag(data, "screenshot_verified");
            progress = new ColorRunProgress(stage, total is null && same ? progress!.Current : current, total ?? (same ? progress!.Total : null),
                same ? progress!.StageStartedMonotonicMs : now, same ? progress!.StageStartedAt : at,
                progress?.DeadlineMonotonicMs, now, at)
            {
                Message = Text(data, "message", 512), Status = status,
                Reading = verified ? Reading(run, data, "current_colors", "current_deltas") : null,
                Retained = status is "trial" or "restoring" or "recovering" ? Reading(run, data, "retained_colors", "retained_deltas") : null
            };
            changed = true;
        }
        else if (kind == "native_colors" && Reading(run, data, "colors", null) is { } observed)
        {
            // 精准寻色的实际回读：只更新读数，阶段与说明不变。
            progress = (progress ?? new(null, null, null, now, at, null, now, at)) with { Reading = observed, UpdatedMonotonicMs = now, UpdatedAt = at };
            changed = true;
        }
        else if (kind == "native_retry_available" && Flag(data, "waiting_for_retry"))
        {
            // 本轮已出结果、等待重试（原工具“本轮寻色已完成”）；不可重试时带原因码。
            var verified = Flag(data, "verified") && Flag(data, "screenshot_verified");
            progress = (progress ?? new(null, null, null, now, at, null, now, at)) with
            {
                Status = "retry-waiting", Reason = Flag(data, "enabled") ? null : Text(data, "reason", 64), Message = null,
                Reading = verified ? Reading(run, data, "current_colors", "current_deltas") : progress?.Reading, Retained = null,
                UpdatedMonotonicMs = now, UpdatedAt = at
            };
            changed = true;
        }
        else if (kind == "native_retry_reset" && Text(data, "status", 32) is { } reset && reset is "zooming" or "searching" or "rejected")
        {
            progress = (progress ?? new(null, null, null, now, at, null, now, at)) with
            {
                Status = "retry-" + reset, Reason = Text(data, "reason", 64), Message = null, Retained = null,
                UpdatedMonotonicMs = now, UpdatedAt = at
            };
            changed = true;
        }
        var budget = data.TryGetProperty("budget", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : default;
        double? seconds = null;
        if ((Finite(data, "effective_deadline") ?? Finite(budget, "effective_deadline") ?? Finite(data, "session_deadline_monotonic")) is { } end)
            seconds = end - frameAt;
        else if ((Finite(data, "remaining") ?? Finite(data, "remaining_seconds") ?? Finite(budget, "remaining_seconds")) is { } left)
            seconds = (Finite(data, "captured_monotonic") ?? Finite(data, "at_monotonic") ?? Finite(budget, "at_monotonic") ?? frameAt) + left - frameAt;
        else if (kind == "screenshot_progress" && Finite(data, "seconds") is { } remaining)
            seconds = remaining;
        if (seconds is { } span)
        {
            var deadline = Math.Min(maximumDeadline, now + Math.Max(0, span) * 1000);
            progress = (progress ?? new(null, null, null, now, at, null, now, at)) with { DeadlineMonotonicMs = deadline, UpdatedMonotonicMs = now, UpdatedAt = at };
            changed = true;
        }
        return changed ? run with { Progress = progress } : run;
    }

    private static double? Finite(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.GetDouble() is var number && double.IsFinite(number) ? number : null;

    private static string? Text(JsonElement row, string key, int maximum) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text &&
        text.Length <= maximum && !text.Any(char.IsControl) ? text : null;

    private static bool Flag(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// 按规则区域顺序读一组色码与色差，只用于显示：色码统一为大写 #RRGGBB，不是色码的项为空，多出区域数的项忽略；
    /// 读数不影响运行，格式不符时不拒绝整帧。
    /// </summary>
    private static ColorRunReading? Reading(ColorRun run, JsonElement row, string colorsKey, string? deltasKey)
    {
        if (!row.TryGetProperty(colorsKey, out var colors) || colors.ValueKind != JsonValueKind.Array) return null;
        var regions = run.RulesSnapshot.Regions;
        var count = Math.Min(colors.GetArrayLength(), regions.Count);
        if (count == 0) return null;
        JsonElement? deltas = deltasKey is not null && row.TryGetProperty(deltasKey, out var found) && found.ValueKind == JsonValueKind.Array ? found : null;
        var hex = new Dictionary<string, string?>(StringComparer.Ordinal);
        var delta = new Dictionary<string, double?>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var color = colors[i].ValueKind == JsonValueKind.String ? colors[i].GetString()?.ToUpperInvariant() : null;
            if (color is not null && !System.Text.RegularExpressions.Regex.IsMatch(color, @"\A#[0-9A-F]{6}\z")) color = null;
            double? value = deltas is { } values && i < values.GetArrayLength() && values[i].ValueKind == JsonValueKind.Number &&
                values[i].GetDouble() is var number && double.IsFinite(number) && number >= 0 ? number : null;
            hex[regions[i].Id] = color;
            delta[regions[i].Id] = value;
        }
        return new(hex, delta);
    }

    private static int? Count(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : null;

    internal static ColorRun WithRegionCount(ColorRun run, int count)
    {
        if (count is not (2 or 3) || run.RulesSnapshot.Regions.Count < count) throw new WorkerException("worker.resultInvalid");
        return run with { RegionCount = count, AvailableByRegion = run.RulesSnapshot.Regions
            .Select((region, index) => (region.Id, Available: index < count)).ToDictionary(x => x.Id, x => x.Available, StringComparer.Ordinal) };
    }

    internal static ColorRun ApplyInteractionReceipt(ColorRun run, ColorInteractionState state) =>
        run.Interaction is { } previous && previous.Revision.Value >= state.Revision.Value
            ? run : MergeInteraction(InvalidateCurrent(run), state);

    private static ColorRun InvalidateCurrent(ColorRun run) => run with
    {
        CurrentVerified = null,
        Result = run.Result is { } result ? result with { CurrentVerified = null,
            Outcome = result.Outcome is "matched" or "compromise" ? "unverified" : result.Outcome } : null
    };

    private static ColorRun MergeInteraction(ColorRun run, ColorInteractionState state)
    {
        if (run.Interaction is { } previous && previous.Revision.Value >= state.Revision.Value) return run;
        return run with { Interaction = state, CandidateBatchId = state.BatchId,
            Candidates = run.Candidates is { } batch && state.BatchId == batch.Id ? batch with { Interaction = state,
                Candidates = batch.Candidates.Select(row => row with { Available = row.Available && state.SelectionEnabled }).ToArray() } : null };
    }

    private static ColorRun WithResult(ColorRun run, ColorRunResult result) => (result.RegionCount is 2 or 3 ? WithRegionCount(run, result.RegionCount) : run) with
    { Result = result, BestMeasured = result.BestMeasured, LastMeasured = result.LastMeasured,
        CurrentVerified = result.CurrentVerified, StopReason = result.StopReason };

    private static string PublicCode(string code) => code == "worker.busy" ? "resource.busy" :
        ApiErrors.Definitions.Any(error => error.Code == code) ? code : "operation.executionFailed";

    public override async Task StopAsync(CancellationToken ct)
    {
        accepting = false;
        lock (gate) if (current is { } active) StopJob(active, "host-stopped");
        await admission.WaitAsync(ct);
        try
        {
            accepting = false; queue.Writer.TryComplete();
            lock (gate) if (current is { } job) StopJob(job, "host-stopped");
        }
        finally { admission.Release(); }
        presenceStop.Cancel();
        if (presenceMonitor is not null) await presenceMonitor.WaitAsync(ct);
        await base.StopAsync(ct);
    }

    public override void Dispose()
    {
        presenceStop.Cancel();
        base.Dispose();
    }
}
