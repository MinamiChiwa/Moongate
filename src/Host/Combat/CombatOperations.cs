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
        /// <summary>新帧写入缓冲或后台保存完成时唤醒执行循环（容量 1，多次写入合并）。</summary>
        public readonly Channel<bool> Wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        /// <summary>检查点保存串行化：后台保存、边界命令与收尾保存按修订顺序依次提交。</summary>
        public readonly SemaphoreSlim SaveGate = new(1, 1);
        /// <summary>进行中的后台保存；完成后由执行循环取回其结果。</summary>
        public Task? Saving;
        /// <summary>需要保存时后台保存仍在进行：完成后重新捕获并判定。</summary>
        public int Reevaluate;
        /// <summary>
        /// 例行检查计数（由 TimeProvider 计时器每 <see cref="HousekeepingInterval"/> 推进），以及上次捕获、上次保存、上次核对操作状态时的计数；
        /// 保存与刷新节奏按计数判定，与帧到达无关。
        /// </summary>
        public long Ticks, CaptureTick, SavedTick, OperationTick;
        /// <summary>例行检查到期（计时器置位，执行循环取走）。</summary>
        public int HousekeepingDue;
        /// <summary>上次捕获的时刻（TimeProvider 时间戳），用于帧合并窗口。</summary>
        public long LastCapture;
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
    private static long TicksOf(TimeSpan value) => Math.Max(1, (long)Math.Round(value / HousekeepingInterval));
    /// <summary>分段进行中或测量计时中有变化时，两次保存至少相隔该间隔（同一时段内的变化合并到下一次保存）。</summary>
    internal static TimeSpan ActiveCheckpointInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    /// <summary>分段进行中或测量计时中只有时间推进时的保存间隔；停止观察时另行保存，收尾时长不受影响。</summary>
    internal static TimeSpan TimeOnlyCheckpointInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>例行检查（授权与绑定、空闲时的分段与效果到期、测量计时）的节奏，与帧到达无关。</summary>
    internal static TimeSpan HousekeepingInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    /// <summary>核对操作是否被请求取消的节奏（读取数据库）；取消命令另经任务的停止信号即时生效。</summary>
    internal static TimeSpan OperationCheckInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>没有新帧也没有待保存变化时，最新捕获（观测时点、效果剩余时间）的刷新间隔。</summary>
    internal static TimeSpan IdleCaptureInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>连续到达的帧至多在该窗口内合并为一次捕获与发布。</summary>
    internal static TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromMilliseconds(10);
    /// <summary>离线性能探针：每次发布最新捕获后以（工作区修订、已应用的最后一帧单调时点）回调；未设置时不调用。</summary>
    internal Action<UInt64String, double>? LiveObserver { get; set; }
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
        try { await foreach (var frame in job.Subscription!.ReadAsync(job.Stop.Token)) { job.Buffer.TryWrite(frame); job.Wake.Writer.TryWrite(true); } }
        catch (OperationCanceledException) when (job.Stop.IsCancellationRequested) { }
        catch (Exception error) { job.Failure = error; }
        finally { job.Buffer.Complete(); job.Wake.Writer.TryWrite(true); }
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
        using var housekeepingTimer = new PeriodicTimer(HousekeepingInterval, clock);
        var housekeepingLoop = HousekeepingAsync(job, housekeepingTimer);
        try
        {
            while (true)
            {
                await WaitForWorkAsync(job);
                // 后台保存的失败（数据库错误）与同步保存时一样结束本次观察。
                if (job.Saving is { IsCompleted: true } saved) { job.Saving = null; await saved; }
                await job.Gate.WaitAsync(job.Stop.Token);
                try
                {
                    var housekeeping = Interlocked.Exchange(ref job.HousekeepingDue, 0) == 1;
                    var ticks = Interlocked.Read(ref job.Ticks);
                    if (housekeeping)
                    {
                        if (ticks - job.OperationTick >= TicksOf(OperationCheckInterval))
                        {
                            job.OperationTick = ticks;
                            if (operations.Get(job.Id, "owner", true).State.KnownValue == OperationState.CancelRequested) { job.Stop.Cancel(); break; }
                        }
                        if (!auth.HasPermission(job.Owner, ApiPermissions.CombatControl) || controls.Bindings.Current?.Session.Id != job.Request.GameSessionId)
                        { job.StopReason = "binding-or-permission-changed"; job.Failure = new OperationException("combat.observationGap", job.StopReason); job.Stop.Cancel(); break; }
                    }
                    var applied = false;
                    for (var drain = 0; drain < 4; drain++)
                    {
                        var batch = job.Buffer.Take();
                        if (batch.DroppedEvents.Value > 0)
                        { job.Session.ReportGap("queue-capacity", job.Session.MonotonicMs, job.Session.MonotonicMs, batch.DroppedEvents.Value); applied = true; }
                        for (var i = 0; i < batch.Frames.Count; i++)
                            if (job.Session.Apply(batch.Frames[i]))
                            { job.LastMono = batch.Frames[i].MonotonicMs; job.LastReceived = batch.ReceivedAt[i]; job.Received = true; applied = true; }
                        if (job.Buffer.Count == 0) break;
                    }
                    if (applied || housekeeping) await TickMeasurementAsync(job);
                    if (job.Buffer.Completed)
                    {
                        job.Session.Stop(job.Failure is null ? "source-end" : "source-disconnected", clock.GetUtcNow(), job.Session.MonotonicMs, job.Failure is not null);
                        await job.SaveGate.WaitAsync();
                        try
                        {
                            if (job.Saving is { } pending) { job.Saving = null; await pending; }
                            var final = job.Session.Capture();
                            if (!await history.SaveAsync(final, job.Failure is null ? OperationState.Succeeded : OperationState.Interrupted,
                                    job.Failure is null ? null : ApiErrors.Create("combat.sourceUnavailable", "观察源中断", job.Id)))
                            { job.Stop.Cancel(); break; }
                            job.Session.Acknowledge(final); job.LastSaved = clock.GetTimestamp(); job.SavedTick = job.Ticks;
                            TraceSegments(job, final);
                        }
                        finally { job.SaveGate.Release(); }
                        TraceRejections(job);
                        return;
                    }
                    if (housekeeping && job.Buffer.Count == 0) job.Session.Tick(clock.GetUtcNow(), Now(job));
                    var reevaluate = Interlocked.Exchange(ref job.Reevaluate, 0) == 1;
                    // 没有新帧时，例行检查只在会话有待保存的变化或距上次捕获满 IdleCaptureInterval 时捕获（快照的观测时点随之推进）。
                    var idleCapture = housekeeping && (job.Session.PendingChanges || ticks - job.CaptureTick >= TicksOf(IdleCaptureInterval));
                    if (!applied && !idleCapture && !reevaluate) continue;
                    // 快照先发布（读取立即可见），检查点按变化与时间阈值在后台保存，发布不等待写盘。
                    var checkpoint = job.Session.Capture(); job.LastCapture = clock.GetTimestamp(); job.CaptureTick = ticks;
                    history.PublishLive(checkpoint.Workspace, job.Session.EntityEffects); LiveObserver?.Invoke(checkpoint.Workspace.Revision, job.LastMono);
                    if (SaveDue(job, checkpoint))
                    {
                        if (job.Saving is null && job.SaveGate.Wait(0)) job.Saving = SaveInBackgroundAsync(job, checkpoint);
                        else Volatile.Write(ref job.Reevaluate, 1);
                    }
                    TraceRejections(job);
                }
                finally { job.Gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (job.Stop.IsCancellationRequested) { }
        finally
        {
            job.Stop.Cancel(); await producer; await housekeepingLoop;
            // 后台保存的结果在收尾前取回；失败时按观察中断收尾。
            if (job.Saving is { } inflight)
            {
                job.Saving = null;
                try { await inflight; }
                catch (Exception error) when (MaintenanceBlocks(job, error)) { }
                catch (Exception error) when (error is OperationException or StorageException)
                { job.Failure ??= error; job.StopReason = "observation-failed"; }
            }
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
    /// <summary>
    /// 停止前保存尚未落库的变化与进行中分段的当前时点，收尾检查点从这里继续（分段时长截止到停止时）；
    /// 保存失败时沿用上一次已提交的检查点。
    /// </summary>
    private async Task FlushDeferredAsync(Job job)
    {
        if (job.Session is not { } session) return;
        await job.Gate.WaitAsync();
        await job.SaveGate.WaitAsync();
        try
        {
            var checkpoint = session.Capture();
            if (!checkpoint.Material && !checkpoint.Urgent) return;
            if (await history.SaveAsync(checkpoint)) session.Acknowledge(checkpoint);
        }
        catch (OperationException) { }
        catch (StorageException) { }
        // 更新维护期间数据库拒绝写入：沿用上一次已提交的检查点，收尾由维护结束后的延后停止完成。
        catch (ActivityBarrierException) { }
        finally { job.SaveGate.Release(); job.Gate.Release(); }
    }

    /// <summary>
    /// 请求取消或停止前，把已发布但尚未保存的状态写入检查点：取消请求提交后不再接受非终态检查点，收尾以已提交状态为准，
    /// 因此停止前读取方已看到的统计都进入收尾结果。
    /// </summary>
    private async Task PersistLatestAsync(Job job, CancellationToken ct)
    {
        await job.Gate.WaitAsync(ct);
        try
        {
            await job.SaveGate.WaitAsync(ct);
            try
            {
                if (job.Session is not { Active: true } session || job.Stop.IsCancellationRequested) return;
                if (job.Saving is { } pending) { job.Saving = null; await pending; }
                var checkpoint = session.Capture();
                if (!checkpoint.Material && !checkpoint.Urgent) return;
                if (!await history.SaveAsync(checkpoint, ct: ct)) return;
                session.Acknowledge(checkpoint); job.SavedTick = Interlocked.Read(ref job.Ticks); TraceSegments(job, checkpoint);
                history.PublishLive(checkpoint.Workspace, session.EntityEffects);
            }
            catch (Exception error) when (error is OperationException or StorageException or ActivityBarrierException) { }
            finally { job.SaveGate.Release(); }
        }
        finally { job.Gate.Release(); }
    }

    /// <summary>
    /// 等待新帧、例行检查时刻或后台保存完成。新帧到达时若距上次捕获不足合并窗口，等到窗口结束再处理，期间到达的帧一并应用。
    /// </summary>
    private async Task WaitForWorkAsync(Job job)
    {
        // 待重新判定的保存只在后台保存结束后处理（保存完成时会唤醒循环），保存进行中照常等待。
        if (job.Buffer.Count == 0 && !job.Buffer.Completed && Volatile.Read(ref job.HousekeepingDue) == 0 && job.Saving is not { IsCompleted: true } &&
            (Volatile.Read(ref job.Reevaluate) == 0 || job.Saving is { IsCompleted: false }))
            await job.Wake.Reader.WaitToReadAsync(job.Stop.Token);
        job.Stop.Token.ThrowIfCancellationRequested();
        job.Wake.Reader.TryRead(out _);
        var sinceCapture = clock.GetElapsedTime(job.LastCapture);
        if (job.Buffer.Count > 0 && sinceCapture < CoalesceWindow) await Task.Delay(CoalesceWindow - sinceCapture, clock, job.Stop.Token);
    }

    /// <summary>例行检查计时器：每个周期推进计数并唤醒执行循环；任务停止时结束。</summary>
    private static async Task HousekeepingAsync(Job job, PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(job.Stop.Token))
            { Interlocked.Increment(ref job.Ticks); Volatile.Write(ref job.HousekeepingDue, 1); job.Wake.Writer.TryWrite(true); }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 是否保存本次捕获：分段进行中、测量计时中或有待保存的已结束分段与删除时，有变化至多每 <see cref="ActiveCheckpointInterval"/>
    /// 一次、只有时间推进时每 <see cref="TimeOnlyCheckpointInterval"/> 一次；其余有变化时至多每 <see cref="DeferredCheckpointInterval"/>
    /// 一次，只有时间推进时不保存。
    /// </summary>
    private bool SaveDue(Job job, CombatCheckpoint checkpoint)
    {
        var since = Interlocked.Read(ref job.Ticks) - Interlocked.Read(ref job.SavedTick);
        return checkpoint.Material ? since >= TicksOf(checkpoint.Urgent ? ActiveCheckpointInterval : DeferredCheckpointInterval)
            : checkpoint.Urgent && since >= TicksOf(TimeOnlyCheckpointInterval);
    }

    /// <summary>
    /// 后台保存检查点：持有 <see cref="Job.SaveGate"/> 直到提交与确认完成，完成后唤醒执行循环。
    /// 观察已终止（保存被拒）时停止任务；数据库错误由执行循环取回后按观察失败处理。
    /// </summary>
    private async Task SaveInBackgroundAsync(Job job, CombatCheckpoint checkpoint)
    {
        try
        {
            await Task.Yield();
            if (!await history.SaveAsync(checkpoint)) { job.Stop.Cancel(); return; }
            job.Session?.Acknowledge(checkpoint); Interlocked.Exchange(ref job.LastSaved, clock.GetTimestamp()); Interlocked.Exchange(ref job.SavedTick, Interlocked.Read(ref job.Ticks));
            TraceSegments(job, checkpoint);
        }
        finally { job.SaveGate.Release(); job.Wake.Writer.TryWrite(true); }
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
        if (stopping is not null) await PersistLatestAsync(stopping, ct);
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
            await PersistLatestAsync(job, ct);
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
