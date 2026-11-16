using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Host.Combat;

/// <summary>
/// 常驻 DPS 观测：自动观测开启（后端偏好，默认开启）、共享抓包运行且绑定了游戏窗口时，宿主为本机主体保持一个战斗观测；
/// 本机身份未出现时观测处于等待，身份出现后开始统计。抓包关闭、绑定变化或观测结束后自动停止或重新开始，失败后退避重试。
/// </summary>
public sealed partial class CombatOperations
{
    public const string AutoPrincipal = "owner";
    public static readonly TimeSpan AutoInterval = TimeSpan.FromSeconds(2), AutoRetry = TimeSpan.FromSeconds(10);
    private Task autoLoop = Task.CompletedTask;
    private readonly CancellationTokenSource autoStop = new();
    private volatile CombatAutoObservationState autoState = new(true, "off", null, null, new(0));
    private long autoRetryAt;
    private readonly SemaphoreSlim autoGate = new(1, 1);
    private readonly object deferredStopLock = new();
    private DeferredResidentStop? deferredResidentStop;
    private sealed class DeferredResidentStop(string id)
    {
        public string Id { get; } = id;
        public IReadOnlyList<CombatGapInput>? Gaps;
        public IReadOnlyList<CombatEncounter>? Encounters;
        public IReadOnlyList<CombatTimelineRecord>? Timeline;
    }

    // 维护期间先释放观察资源；唯一常驻观测的终态在维护结束后提交，退出时交给既有恢复流程。
    private void DeferResidentStop(Job job, IReadOnlyList<CombatGapInput>? completionGaps = null,
        IReadOnlyList<CombatEncounter>? measuredEncounters = null, IReadOnlyList<CombatTimelineRecord>? measuredTimeline = null)
    {
        job.StopReason = "update-maintenance";
        lock (deferredStopLock)
        {
            var pending = deferredResidentStop ??= new(job.Id);
            if (pending.Id != job.Id) throw new InvalidOperationException("只能延期结算一个常驻战斗观测");
            if (pending.Gaps is not { Count: > 0 }) pending.Gaps = completionGaps;
            if (pending.Encounters is not { Count: > 0 }) pending.Encounters = measuredEncounters;
            if (pending.Timeline is not { Count: > 0 }) pending.Timeline = measuredTimeline;
        }
    }

    private async Task<bool> CompleteDeferredResidentStopAsync(CancellationToken ct)
    {
        DeferredResidentStop? pending;
        IReadOnlyList<CombatGapInput>? gaps;
        IReadOnlyList<CombatEncounter>? encounters;
        IReadOnlyList<CombatTimelineRecord>? timeline;
        lock (deferredStopLock)
        {
            pending = deferredResidentStop;
            gaps = pending?.Gaps; encounters = pending?.Encounters; timeline = pending?.Timeline;
        }
        if (pending is null) return true;
        if (active.ContainsKey(pending.Id)) return false;
        if (barrier.Snapshot().MaintenanceOwner is not null) return false;
        try
        {
            if (!await history.StopCommittedAsync(pending.Id, "update-maintenance", true, OperationState.Interrupted, ct,
                completionGaps: gaps, extraEncounters: encounters, extraTimeline: timeline))
                await operations.FailCombatAsync(pending.Id, "update.maintenanceActive", "常驻观测在更新维护期间停止", ct);
            if (encounters?.LastOrDefault(value => value.Record?.Measurement is not null) is { } measured)
            {
                await measurementLock.WaitAsync(ct);
                try
                {
                    lastMeasurement = measured.Record!.Measurement;
                    lastMeasurementId = measured.Id;
                    Interlocked.Increment(ref measurementRevision);
                }
                finally { measurementLock.Release(); }
                await PublishMeasurementAsync();
            }
        }
        catch (ActivityBarrierException error) when (error.Code == "update.maintenanceActive") { return false; }
        lock (deferredStopLock) if (ReferenceEquals(deferredResidentStop, pending)) deferredResidentStop = null;
        return true;
    }

    public CombatAutoObservationState AutoObservation()
    {
        var preference = history.AutoObservationPreferences();
        return autoState with { Enabled = preference.Enabled, Revision = preference.Revision };
    }

    public async Task<CombatAutoObservationState> SetAutoObservationAsync(string principal, CombatAutoObservationRequest request,
        string? key, string? etag, CancellationToken ct = default)
    {
        if (request is null) throw new OperationException("request.invalid", "自动观测设置无效");
        var committed = await history.SaveAutoObservationAsync(principal, request, key, etag, preference =>
        {
            var state = autoState;
            return preference.Enabled ? state with { Enabled = true, Revision = preference.Revision,
                Reason = state.Reason == "combat.autoDisabled" ? null : state.Reason }
                : new(false, "off", "combat.autoDisabled", null, preference.Revision);
        }, ct);
        if (!committed.Applied) return committed.Value;
        Interlocked.Exchange(ref autoRetryAt, 0);
        await AutoTickAsync(CancellationToken.None);
        return committed.Value;
    }

    private void StartAutoObservation() => autoLoop = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(AutoInterval, clock);
        try
        {
            do
            {
                try { await AutoTickAsync(autoStop.Token); }
                catch (Exception error) when (error is not OperationCanceledException) { autoState = autoState with { State = "failed", Reason = Code(error) }; }
            }
            while (await timer.WaitForNextTickAsync(autoStop.Token));
        }
        catch (OperationCanceledException) when (autoStop.IsCancellationRequested) { }
    });

    private static string Code(Exception error) => error switch
    {
        OperationException operation => operation.Code,
        ModuleRuntimeException module => module.Code,
        ActivityBarrierException barrier => barrier.Code,
        _ => "combat.sourceUnavailable"
    };

    private async Task AutoTickAsync(CancellationToken ct)
    {
        await autoGate.WaitAsync(ct);
        try { await AutoTickCoreAsync(ct); }
        finally { autoGate.Release(); }
    }

    private async Task AutoTickCoreAsync(CancellationToken ct)
    {
        if (!accepting) return;
        if (!await CompleteDeferredResidentStopAsync(ct))
        {
            autoState = new(history.AutoObservationEnabled(), "off", "update.maintenance", null, new(0));
            return;
        }
        var enabled = history.AutoObservationEnabled();
        var binding = controls.Bindings.Current?.Session;
        var capture = observations?.Snapshot();
        var running = capture?.State == "running";
        var job = active.Values.FirstOrDefault(value => value.Resident && !value.Stop.IsCancellationRequested);
        var reason = !enabled ? "combat.autoDisabled" : observations is null ? "observation.unavailable"
            : !running ? capture?.Reason ?? "observation.captureStopped" : binding is null ? "game.bindingRequired"
            : barrier.Snapshot().MaintenanceOwner is not null ? "update.maintenance" : null;
        if (reason is not null)
        {
            if (job is not null) await CancelAsync(job.Id, AutoPrincipal, true, "auto-observation-stopped", ct);
            autoState = new(enabled, "off", reason, null, new(0));
            return;
        }
        if (job is not null && job.Request.GameSessionId != binding!.Id)
        {
            await CancelAsync(job.Id, AutoPrincipal, true, "binding-or-permission-changed", ct);
            job = null;
        }
        if (job is not null)
        {
            autoState = new(true, job.Session is { Active: true } ? "observing" : "waiting", null, job.Id, new(0));
            return;
        }
        // 已有手动开始的观测时不另开一个。
        if (active.Values.FirstOrDefault(value => !value.Stop.IsCancellationRequested && value.Request.GameSessionId == binding!.Id) is { } manual)
        {
            autoState = new(true, manual.Session is { Active: true } ? "observing" : "waiting", null, manual.Id, new(0));
            return;
        }
        if (clock.GetTimestamp() < Interlocked.Read(ref autoRetryAt)) return;
        try
        {
            var snapshot = await SubmitAsync(AutoPrincipal, new(binding!.Id), "auto-" + Guid.NewGuid().ToString("N"), ct, resident: true);
            if (active.TryGetValue(snapshot.Id, out var started)) started.Auto = true;
            autoState = new(true, "waiting", null, snapshot.Id, new(0));
        }
        catch (Exception error) when (error is OperationException or ModuleRuntimeException)
        {
            Interlocked.Exchange(ref autoRetryAt, clock.GetTimestamp() + (long)(AutoRetry.TotalSeconds * clock.TimestampFrequency));
            autoState = new(true, "failed", Code(error), null, new(0));
        }
    }

    /// <summary>观察结束时（含身份等待失败），自动观测在下一轮按退避重新开始。</summary>
    private void AutoJobEnded(Job job)
    {
        if (!job.Resident) return;
        if (job.StopReason == "update-maintenance")
        {
            Interlocked.Exchange(ref autoRetryAt, 0);
            autoState = new(history.AutoObservationEnabled(), "off", "update.maintenance", null, new(0));
            return;
        }
        if (job.Failure is not null)
        {
            Interlocked.Exchange(ref autoRetryAt, clock.GetTimestamp() + (long)(AutoRetry.TotalSeconds * clock.TimestampFrequency));
            autoState = autoState with { State = "failed", Reason = Code(job.Failure), ObservationId = null };
        }
    }
}
