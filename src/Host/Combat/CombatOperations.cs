using System.Collections.Concurrent;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Host.Combat;

public sealed partial class CombatOperations(OperationStore operations, CombatHistoryStore history, ICombatObservationSource source,
    ModuleRuntime modules, ActivityBarrier barrier, GameControlContext controls, LocalAuthService auth, TimeProvider clock,
    DiagnosticTrace? trace = null, StateEventHub? events = null, GameObservationService? observations = null) : BackgroundService
{
    private sealed class Job(string id, string owner, CombatObservationRequest request, IDisposable activity, IAsyncDisposable demand, TimeProvider clock, bool resident)
    {
        public string Id { get; } = id;
        public string Owner { get; } = owner;
        public CombatObservationRequest Request { get; } = request;
        public bool Resident { get; } = resident;
        public IDisposable Activity { get; } = activity;
        public IAsyncDisposable Demand { get; } = demand;
        public readonly CancellationTokenSource Stop = new();
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly CombatFrameBuffer Buffer = new(timeProvider: clock);
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CombatSession? Session;
        public ICombatObservationSubscription? Subscription;
        public bool SubscriptionClosed;
        public Exception? Failure;
        public string StopReason = "observation-stopped";
        public double LastMono;
        public long LastReceived;
        public bool Received;
        /// <summary>上次保存检查点的时刻（TimeProvider 时间戳）。</summary>
        public long LastSaved;
        /// <summary>由常驻自动观测开始的观察。</summary>
        public bool Auto;
        public string? TracedSegment;
        public readonly HashSet<string> TracedEnds = new(StringComparer.Ordinal);
        public readonly Queue<string> TracedOrder = new();
        public readonly Dictionary<string, (ulong Reported, long At)> TracedRejections = new(StringComparer.Ordinal);
    }
    /// <summary>同一分支的帧拒绝至多每分钟记一条 warning；观察结束时补记未记入的次数。</summary>
    internal static readonly TimeSpan RejectionTraceInterval = TimeSpan.FromMinutes(1);
    private void TraceRejections(Job job, bool flush = false)
    {
        if (trace is null || job.Session is null) return;
        foreach (var (branch, total) in job.Session.FrameRejections)
        {
            job.TracedRejections.TryGetValue(branch, out var last);
            if (total <= last.Reported || !flush && last.At != 0 && clock.GetElapsedTime(last.At) < RejectionTraceInterval) continue;
            job.TracedRejections[branch] = (total, clock.GetTimestamp());
            trace.Emit(DiagnosticEvents.CombatFrameRejected, new DiagnosticFields().Value("branch", branch)
                .Count("count", total - last.Reported).Count("total", total), operationId: job.Id);
        }
    }
    /// <summary>已提交检查点中的分段开始与结束记为 info 事件：分段序号与开始/边界原因码。</summary>
    private void TraceSegments(Job job, CombatCheckpoint checkpoint)
    {
        if (trace is null) return;
        foreach (var ended in checkpoint.Encounters.Where(x => x.Summary.Final))
            if (job.TracedEnds.Add(ended.Id))
            {
                job.TracedOrder.Enqueue(ended.Id);
                if (job.TracedOrder.Count > 4096) job.TracedEnds.Remove(job.TracedOrder.Dequeue());
                trace.Emit(DiagnosticEvents.SegmentEnded, new DiagnosticFields().Count("ordinal", ended.Segment?.Ordinal ?? 0)
                    .Value("reason", ended.Summary.BoundaryReason));
            }
        if (checkpoint.Workspace.Encounter is { Summary.Final: false } current && current.Id != job.TracedSegment)
        {
            job.TracedSegment = current.Id;
            trace.Emit(DiagnosticEvents.SegmentStarted, new DiagnosticFields().Count("ordinal", current.Segment?.Ordinal ?? 0)
                .Value("reason", current.Segment?.StartReason));
        }
    }
    /// <summary>
    /// 没有进行中的分段或计时中的测量时，实体与效果等变化至多每隔该间隔保存一次检查点；只有时间推进时不保存。
    /// 读取经最新捕获登记取得当前状态；崩溃时至多丢失这段时间内未保存的实体、效果与缺口变化，不含伤害时间线。
    /// </summary>
    internal static TimeSpan DeferredCheckpointInterval { get; set; } = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(128) { SingleReader = true });
    private readonly ConcurrentDictionary<string, Job> active = new(StringComparer.Ordinal);
    private bool accepting;
    internal (int Active, int Sessions, int Queued, bool Accepting) DiagnosticState() =>
        (active.Count, active.Values.Count(job => job.Session is not null), queue.Reader.Count, accepting);
    public CombatInputUsage InputUsage() => new(active.Values.Sum(job => job.Buffer.Count), active.Values.Sum(job => job.Buffer.Bytes));

    public CombatCapabilities Capabilities(string principal, bool owner)
    {
        var result = source.Capabilities(controls.Bindings.Recent?.Session.Id);
        var workspace = history.Workspace(principal, owner);
        return workspace is null ? result : result with { Capabilities = result.Capabilities.Select(capability => capability with
            { Generation = workspace.Observation.LocalEntity.ObservationGeneration, LastObservedAt = workspace.Observation.LastObservedAt }).ToArray() };
    }
    public Task<OperationSnapshot> SubmitAsync(string principal, CombatObservationRequest request, string? key, CancellationToken ct) =>
        SubmitAsync(principal, request, key, ct, resident: false);

    /// <param name="resident">常驻自动观测：只读观测不登记更新活动，维护期间不开始（由自动观测循环停止），不阻挡更新安装。</param>
    private async Task<OperationSnapshot> SubmitAsync(string principal, CombatObservationRequest request, string? key, CancellationToken ct, bool resident)
    {
        try { CombatSession.ValidateSettings(request); }
        catch (ArgumentException error) { throw new OperationException("request.invalid", error.Message); }
        await admission.WaitAsync(ct);
        try
        {
            if (operations.ExistingCombat(principal, request, key) is { } previous) return previous;
            if (!accepting) throw new OperationException("combat.sourceUnavailable", "战斗执行器正在关闭或等待恢复");
            if (controls.Bindings.Current?.Session.Id != request.GameSessionId) throw new OperationException("input.bindingChanged", "请求的游戏会话与当前绑定不同");
            var capabilities = source.Capabilities(request.GameSessionId);
            var requested = new[] { ("combat.damage.local", request.DamageEnabled), ("combat.effects.self", request.SelfEffectsEnabled), ("combat.effects.monster", request.MonsterEffectsEnabled) };
            foreach (var (id, enabled) in requested)
                if (enabled && capabilities.Capabilities.SingleOrDefault(x => x.Id == id) is not { Status.KnownValue: CapabilityStatus.Available or CapabilityStatus.Degraded })
                    throw new OperationException("combat.sourceUnavailable", "所选战斗能力缺少可用来源", new Dictionary<string, System.Text.Json.JsonElement>
                        { ["capability"] = System.Text.Json.JsonSerializer.SerializeToElement(id) });
            await using var check = await modules.AcquireAsync("combat-observation", "combat-admission", ct);
            IDisposable? activity = !resident ? barrier.EnterActivity(principal, "combat-observation")
                : barrier.Snapshot().MaintenanceOwner is null ? new ResidentActivity()
                : throw new OperationException("resource.busy", "更新维护进行中，稍后自动开始观测");
            OperationAdmission? admitted = null;
            try
            {
                admitted = await operations.AdmitCombatAsync(principal, request, key, queue.Reader.Count < 128, ct);
                if (!admitted.Created) return admitted.Snapshot;
                var demand = await modules.AcquireAsync("combat-observation", admitted.Snapshot.Id);
                var job = new Job(admitted.Snapshot.Id, principal, request, activity, demand, clock, resident); activity = null;
                active[job.Id] = job;
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("战斗执行队列与已受理资源不一致");
                return admitted.Snapshot;
            }
            catch
            {
                if (admitted?.Created == true && !active.ContainsKey(admitted.Snapshot.Id))
                    await operations.FailCombatAsync(admitted.Snapshot.Id, "combat.sourceUnavailable", "战斗来源未能启动");
                throw;
            }
            finally { activity?.Dispose(); }
        }
        finally { admission.Release(); }
    }
    public override async Task StartAsync(CancellationToken ct)
    {
        await history.RecoverAsync(ct); accepting = true; await base.StartAsync(ct); StartAutoObservation();
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            try { await RunAsync(job, stoppingToken); }
            catch (Exception error) when (MaintenanceBlocks(job, error) || job.Resident && job.Stop.IsCancellationRequested &&
                error is OperationCanceledException && barrier.Snapshot().MaintenanceOwner is not null)
            {
                job.StopReason = "update-maintenance"; DeferResidentStop(job);
            }
            catch (Exception error)
            {
                job.Failure = error;
                try
                {
                    if (!await history.StopCommittedAsync(job.Id, "observation-failed", true, OperationState.Interrupted))
                        await operations.FailCombatAsync(job.Id, "combat.sourceUnavailable", "观察未能完成初始化");
                }
                catch (Exception failure) when (MaintenanceBlocks(job, failure))
                { job.StopReason = "update-maintenance"; DeferResidentStop(job); }
                catch { accepting = false; }
            }
            finally
            {
                job.Stop.Cancel(); job.Buffer.Discard();
                try { if (job.Subscription is not null && !job.SubscriptionClosed) await job.Subscription.DisposeAsync(); }
                finally
                {
                    await job.Demand.DisposeAsync(); job.Activity.Dispose(); active.TryRemove(job.Id, out _);
                    try { AutoJobEnded(job); await ReleaseMeasurementAsync(job); } finally { job.Done.TrySetResult(); }
                }
                job.Session = null; job.Subscription = null;
            }
        }
    }
    private static bool MaintenanceBlocks(Job job, Exception error) =>
        job.Resident && error is ActivityBarrierException { Code: "update.maintenanceActive" };

    private async Task ProduceAsync(Job job)
    {
        try { await foreach (var frame in job.Subscription!.ReadAsync(job.Stop.Token)) job.Buffer.TryWrite(frame); }
        catch (OperationCanceledException) when (job.Stop.IsCancellationRequested) { }
        catch (Exception error) { job.Failure = error; }
        finally { job.Buffer.Complete(); }
    }
    private double Now(Job job) => job.Received && job.Subscription is not null
        ? Math.Max(job.Session!.MonotonicMs, job.LastMono + clock.GetElapsedTime(job.LastReceived).TotalMilliseconds * job.Subscription.ClockRate)
        : job.Session?.MonotonicMs ?? 0;
    private async Task RunAsync(Job job, CancellationToken stoppingToken)
    {
        using var registration = stoppingToken.Register(() => { job.StopReason = "backend-stopping"; job.Stop.Cancel(); });
        if (job.Stop.IsCancellationRequested)
        { await operations.FailCombatAsync(job.Id, "combat.sourceUnavailable", "观察启动已取消"); return; }
        job.Subscription = source is SharedCaptureCombatSource shared
            ? await shared.OpenAsync(job.Owner, job.Id, job.Request, job.Stop.Token, resident: job.Resident)
            : await source.OpenAsync(job.Owner, job.Id, job.Request, job.Stop.Token);
        if (!double.IsFinite(job.Subscription.ClockRate) || job.Subscription.ClockRate is <= 0 or > 1000)
            throw new OperationException("combat.buildUnsupported", "观察来源时钟比例无效");
        if (job.Subscription.Source.Region != "TW" || !Version.TryParse(job.Subscription.Source.ProviderVersion, out var version) || version < new Version(1, 0) || version >= new Version(2, 0))
            throw new OperationException("combat.buildUnsupported", "观察来源地区或版本不兼容");
        if (!controls.Simulation && (source is SharedCaptureCombatSource accepting
                ? !accepting.Accepts(controls.Bindings.Current?.Session, job.Subscription.Source)
                : controls.Bindings.Current?.Session.GameBuild != job.Subscription.Source.GameBuild))
            throw new OperationException("combat.buildUnsupported", "观察来源构建与当前游戏绑定不同");
        job.Session = new(job.Owner, job.Id, job.Subscription.Source, job.Subscription.LocalEntity, job.Request, clock.GetUtcNow(),
            healing: job.Subscription.HealingSupported);
        var initial = job.Session.Capture();
        if (!await history.SaveAsync(initial)) { await history.StopCommittedAsync(job.Id, job.StopReason, false, OperationState.Cancelled); return; }
        job.Session.Acknowledge(initial); job.LastSaved = clock.GetTimestamp();
        history.PublishLive(initial.Workspace, job.Session.EntityEffects);
        var producer = ProduceAsync(job);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(job.Stop.Token))
            {
                await job.Gate.WaitAsync(job.Stop.Token);
                try
                {
                    var operation = operations.Get(job.Id, "owner", true);
                    if (operation.State.KnownValue == OperationState.CancelRequested) { job.Stop.Cancel(); break; }
                    if (!auth.HasPermission(job.Owner, ApiPermissions.CombatControl) || controls.Bindings.Current?.Session.Id != job.Request.GameSessionId)
                    { job.StopReason = "binding-or-permission-changed"; job.Failure = new OperationException("combat.observationGap", job.StopReason); job.Stop.Cancel(); break; }
                    for (var drain = 0; drain < 4; drain++)
                    {
                        var batch = job.Buffer.Take();
                        if (batch.DroppedEvents.Value > 0) job.Session.ReportGap("queue-capacity", job.Session.MonotonicMs, job.Session.MonotonicMs, batch.DroppedEvents.Value);
                        for (var i = 0; i < batch.Frames.Count; i++)
                            if (job.Session.Apply(batch.Frames[i]))
                            { job.LastMono = batch.Frames[i].MonotonicMs; job.LastReceived = batch.ReceivedAt[i]; job.Received = true; }
                        if (job.Buffer.Count == 0) break;
                    }
                    await TickMeasurementAsync(job);
                    var ended = job.Buffer.Completed;
                    if (ended)
                        job.Session.Stop(job.Failure is null ? "source-end" : "source-disconnected", clock.GetUtcNow(), job.Session.MonotonicMs, job.Failure is not null);
                    else if (job.Buffer.Count == 0) job.Session.Tick(clock.GetUtcNow(), Now(job));
                    var checkpoint = job.Session.Capture();
                    var terminal = ended ? job.Failure is null ? OperationState.Succeeded : OperationState.Interrupted : (OperationState?)null;
                    if (ended || checkpoint.Urgent || checkpoint.Material && clock.GetElapsedTime(job.LastSaved) >= DeferredCheckpointInterval)
                    {
                        if (!await history.SaveAsync(checkpoint, terminal, job.Failure is null ? null : ApiErrors.Create("combat.sourceUnavailable", "观察源中断", job.Id)))
                        { job.Stop.Cancel(); break; }
                        job.Session.Acknowledge(checkpoint); job.LastSaved = clock.GetTimestamp();
                        TraceSegments(job, checkpoint);
                    }
                    if (!ended) history.PublishLive(checkpoint.Workspace, job.Session.EntityEffects);
                    TraceRejections(job);
                    if (ended) return;
                }
                finally { job.Gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (job.Stop.IsCancellationRequested) { }
        finally
        {
            job.Stop.Cancel(); await producer;
            await FlushDeferredAsync(job);
            TraceRejections(job, flush: true);
            var completion = new CombatGapAccumulator();
            completion.AddRange(await job.Subscription.CloseAsync()); job.SubscriptionClosed = true;
            completion.AddRange(job.Buffer.DrainGaps("combat.uncommittedRecords", job.Session.MonotonicMs));
            if (!SqliteStore.IsTerminal(operations.Get(job.Id, "owner", true).State.KnownValue))
            {
                var (measured, measuredTimeline) = FinishMeasurementOnStop(job, job.Failure is null ? "observation-stopped" : "observation-interrupted");
                try
                {
                    if (job.Failure is null) await operations.CancelAsync(job.Id, "owner", true, job.StopReason);
                    await history.StopCommittedAsync(job.Id, job.StopReason, job.Failure is not null,
                        job.Failure is null ? OperationState.Cancelled : OperationState.Interrupted, completionGaps: completion.Snapshot(),
                        extraEncounters: measured, extraTimeline: measuredTimeline);
                }
                catch (Exception error) when (MaintenanceBlocks(job, error))
                { job.StopReason = "update-maintenance"; DeferResidentStop(job, completion.Snapshot(), measured, measuredTimeline); }
            }
        }
    }
    /// <summary>停止前保存尚未落库的变化，收尾检查点从这里继续；保存失败时沿用上一次已提交的检查点。</summary>
    private async Task FlushDeferredAsync(Job job)
    {
        if (job.Session is not { } session) return;
        await job.Gate.WaitAsync();
        try
        {
            var checkpoint = session.Capture();
            if (!checkpoint.Material) return;
            if (await history.SaveAsync(checkpoint)) session.Acknowledge(checkpoint);
        }
        catch (OperationException) { }
        catch (StorageException) { }
        finally { job.Gate.Release(); }
    }
    public async Task<OperationSnapshot> CancelAsync(string id, string principal, bool owner, string? reason, CancellationToken ct = default)
    {
        var operation = operations.Get(id, principal, owner);
        if (operation.Kind != "combat.observe") throw new OperationException("request.invalid", "操作不是战斗观察");
        active.TryGetValue(id, out var stopping);
        if (stopping is { Resident: true } && barrier.Snapshot().MaintenanceOwner is not null)
        {
            stopping.StopReason = "update-maintenance"; stopping.Stop.Cancel();
            await stopping.Done.Task.WaitAsync(ct);
            return operations.Get(id, principal, owner);
        }
        OperationSnapshot result;
        try { result = await operations.CancelAsync(id, principal, owner, reason, ct); }
        catch (Exception error) when (stopping is not null && MaintenanceBlocks(stopping, error))
        {
            stopping.StopReason = "update-maintenance"; stopping.Stop.Cancel();
            await stopping.Done.Task.WaitAsync(ct);
            return operations.Get(id, principal, owner);
        }
        if (active.TryGetValue(id, out var job))
        {
            job.StopReason = reason ?? "observation-stopped"; job.Stop.Cancel();
            await job.Done.Task.WaitAsync(ct);
            return operations.Get(id, principal, owner);
        }
        return result;
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await autoStop.CancelAsync();
        try { await autoLoop; } catch (OperationCanceledException) { }
        await admission.WaitAsync(ct);
        try { accepting = false; queue.Writer.TryComplete(); }
        finally { admission.Release(); }
        foreach (var job in active.Values)
        {
            job.StopReason = "backend-stopping"; job.Stop.Cancel();
            try { await operations.CancelAsync(job.Id, "owner", true, job.StopReason, ct); }
            catch (Exception error) when (MaintenanceBlocks(job, error)) { }
        }
        await base.StopAsync(ct);
    }
}

public sealed record CombatInputUsage(int QueuedEvents, int QueueBytes);

/// <summary>常驻观测不占用更新活动屏障。</summary>
internal sealed class ResidentActivity : IDisposable
{
    public void Dispose() { }
}
