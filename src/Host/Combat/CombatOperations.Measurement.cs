using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Host.Combat;

/// <summary>
/// 定时测量：受理后进入“等待首次伤害”，从本机第一次有效伤害起按设定时长计时，到时结束，与前端是否打开无关。
/// 同一时刻只有一个测量，需要一个已收到观测帧的 DPS 观察；状态变化以 <c>combat.measurement.changed</c> 推送。
/// 预备倒计时已停用：请求里的 countdownMs 仍按 0–60,000 校验以兼容旧前端，但不再生效，设定与结果一律为 0。
/// </summary>
public sealed partial class CombatOperations
{
    public const int MinimumMeasurementMs = 5000, MaximumMeasurementMs = 3_600_000, MaximumCountdownMs = 60000;
    /// <summary>窗口结束后最多再等这么久让迟到的观测帧进入窗口，再保存结果。</summary>
    public const double MeasurementSettleMs = 1500;
    /// <summary>进行中的测量；<see cref="Running"/> 为假时在等待首次伤害，<see cref="StartMs"/>、<see cref="EndMs"/> 在首击到达后由会话给出。</summary>
    private sealed class MeasurementRun(Job job, string id, long anchor, double anchorMono, double rate, string owner)
    {
        public Job Job { get; } = job;
        public string Id { get; } = id;
        public long Anchor { get; } = anchor;
        public double AnchorMono { get; } = anchorMono;
        public double StartMs = double.NaN;
        public double EndMs = double.NaN;
        public double Rate { get; } = rate;
        public string Owner { get; } = owner;
        public bool Running;
    }
    private readonly SemaphoreSlim measurementLock = new(1, 1);
    private MeasurementRun? measurementRun;
    private CombatMeasurementResult? lastMeasurement;
    private string? lastMeasurementId;
    private ulong measurementRevision;

    private double MeasurementNow(MeasurementRun run) => run.AnchorMono + clock.GetElapsedTime(run.Anchor).TotalMilliseconds * run.Rate;

    private Job? MeasurementJob(string principal, bool owner) => active.Values
        .Where(job => (owner || job.Owner == principal) && job.Session is { Active: true } && !job.Stop.IsCancellationRequested)
        .OrderByDescending(job => job.Received).FirstOrDefault();

    public CombatMeasurementState MeasurementState(string principal, bool owner) => MeasurementSnapshot(principal, owner, history.MeasurementPreferences());

    private CombatMeasurementState MeasurementSnapshot(string principal, bool owner, CombatMeasurementPreferences preferences) =>
        MeasurementSnapshot(principal, owner, preferences, measurementRun, new(Interlocked.Read(ref measurementRevision)));

    private CombatMeasurementState MeasurementSnapshot(string principal, bool owner, CombatMeasurementPreferences preferences,
        MeasurementRun? run, UInt64String revision)
    {
        var settings = preferences.Settings with { CountdownMs = 0 };
        if (run is not null && (owner || run.Owner == principal))
        {
            var progress = run.Job.Session?.MeasurementProgress();
            // 等待首次伤害：没有剩余时间与累计，前端显示“等待首次伤害”。
            if (progress is null or { Waiting: true } || !double.IsFinite(progress.EndMs))
                return new("waiting", settings, revision, false, "combat.measurementActive", run.Job.Id, run.Id, null, null, null, null, null,
                    [], lastMeasurement, lastMeasurementId, preferences.Revision);
            var now = MeasurementNow(run);
            var elapsed = Math.Clamp(now - progress.StartMs, 0, progress.EndMs - progress.StartMs);
            var damage = progress.Damage;
            double? dps = elapsed <= 0 ? null : (double)damage * 1000 / elapsed;
            if (dps is { } rate && !double.IsFinite(rate)) dps = null;
            return new("running", settings, revision, false, "combat.measurementActive", run.Job.Id, run.Id,
                Math.Max(0, progress.EndMs - now), elapsed, new DamageAmountString(damage), dps, !progress.Partial,
                progress.Reasons, lastMeasurement, lastMeasurementId, preferences.Revision);
        }
        var job = MeasurementJob(principal, owner);
        var reason = run is not null ? "combat.measurementActive" : job is null ? "combat.observationInactive"
            : !job.Request.DamageEnabled ? "combat.damageDisabled" : !job.Received ? "combat.observationStarting" : null;
        return new("idle", settings, revision, reason is null, reason, job?.Id, null, null, null, null, null, null, [], lastMeasurement, lastMeasurementId, preferences.Revision);
    }

    public static void ValidateMeasurement(int durationMs, int countdownMs)
    {
        if (durationMs is < MinimumMeasurementMs or > MaximumMeasurementMs || countdownMs is < 0 or > MaximumCountdownMs)
            throw new OperationException("request.invalid", "测量时长为 5 秒至 60 分钟，预备倒计时为 0–60 秒");
    }

    public async Task<CombatMeasurementState> StartMeasurementAsync(string principal, bool owner, CombatMeasurementStartRequest request,
        string? key, CancellationToken ct = default)
    {
        if (request is null || request.ObservationId is not { Length: > 0 and <= 128 }) throw new OperationException("request.invalid", "定时测量需要观察身份");
        ValidateMeasurement(request.DurationMs, request.CountdownMs);
        await measurementLock.WaitAsync(ct);
        try
        {
            if (history.MeasurementStartReceipt(principal, request, key) is { } receipt) return receipt;
            if (measurementRun is not null) throw new OperationException("combat.measurementActive", "已有定时测量在进行");
        }
        finally { measurementLock.Release(); }
        if (!active.TryGetValue(request.ObservationId, out var job) || !owner && job.Owner != principal)
            throw new OperationException("combat.observationInactive", "定时测量需要正在进行的 DPS 观测");

        // 与观察循环保持同一顺序，等待检查点时不占住测量锁。
        await job.Gate.WaitAsync(ct);
        CombatMeasurementState result;
        try
        {
            await measurementLock.WaitAsync(ct);
            try
            {
                var now = clock.GetUtcNow();
                if (history.MeasurementStartReceipt(principal, request, key) is { } receipt) return receipt;
                if (measurementRun is not null) throw new OperationException("combat.measurementActive", "已有定时测量在进行");
                var operationState = operations.Get(job.Id, principal, owner).State.KnownValue;
                if (!active.TryGetValue(request.ObservationId, out var current) || current != job || job.Session is not { Active: true } session ||
                    job.Stop.IsCancellationRequested || operationState == OperationState.CancelRequested || SqliteStore.IsTerminal(operationState))
                    throw new OperationException("combat.observationInactive", "定时测量需要正在进行的 DPS 观测");
                if (!job.Request.DamageEnabled) throw new OperationException("combat.observationInactive", "这次观测没有统计伤害");
                if (!job.Received) throw new OperationException("combat.observationInactive", "观测尚未收到数据，请稍候再开始");
                var anchorMono = Now(job);
                CombatSession.MeasurementStartCandidate candidate;
                try { candidate = session.BeginMeasurementCandidate(now, anchorMono, request.DurationMs, 0); }
                catch (InvalidOperationException error) { throw new OperationException("combat.measurementActive", error.Message); }
                var pending = new MeasurementRun(job, candidate.Id, clock.GetTimestamp(), anchorMono, job.Subscription!.ClockRate, job.Owner);
                (CombatMeasurementState Value, bool Applied) committed;
                try
                {
                    var revision = new UInt64String(checked(Interlocked.Read(ref measurementRevision) + 1));
                    committed = await history.CommitMeasurementStartAsync(principal, request, key,
                        preferences => MeasurementSnapshot(principal, owner, preferences, pending, revision), ct);
                }
                catch { session.AbortMeasurementStart(candidate); throw; }
                if (!committed.Applied) { session.AbortMeasurementStart(candidate); return committed.Value; }
                measurementRun = pending; Interlocked.Increment(ref measurementRevision);
                result = committed.Value;
            }
            finally { measurementLock.Release(); }
        }
        finally { job.Gate.Release(); }
        await PublishMeasurementAsync();
        return result;
    }

    public async Task<CombatMeasurementState> CancelMeasurementAsync(string principal, bool owner, CombatMeasurementCancelRequest request,
        string? key, CancellationToken ct = default)
    {
        if (request is null || request.MeasurementId is not { Length: > 0 and <= 128 })
            throw new OperationException("request.invalid", "取消测量需要明确的测量身份");
        await measurementLock.WaitAsync(ct);
        CombatMeasurementState result;
        try
        {
            if (history.MeasurementCancelReceipt(principal, request, key) is { } receipt) return receipt;
            if (measurementRun is not { } run || run.Id != request.MeasurementId || !owner && run.Owner != principal)
                throw new OperationException("combat.measurementInactive", "对应的定时测量未在进行");
            var revision = new UInt64String(checked(Interlocked.Read(ref measurementRevision) + 1));
            var snapshot = MeasurementSnapshot(principal, owner, history.MeasurementPreferences(), null, revision);
            var committed = await history.CommitMeasurementCancelAsync(principal, request, key, snapshot, ct);
            if (!committed.Applied) return committed.Value;
            run.Job.Session?.CancelMeasurement();
            measurementRun = null; Interlocked.Increment(ref measurementRevision);
            result = committed.Value;
        }
        finally { measurementLock.Release(); }
        await PublishMeasurementAsync();
        return result;
    }

    public async Task<CombatMeasurementState> SaveMeasurementSettingsAsync(string principal, bool owner, CombatMeasurementSettings settings,
        string? key, string? etag, CancellationToken ct = default)
    {
        if (settings is null) throw new OperationException("request.invalid", "测量设定无效");
        ValidateMeasurement(settings.DurationMs, settings.CountdownMs);
        settings = settings with { CountdownMs = 0 };
        await measurementLock.WaitAsync(ct);
        CombatMeasurementState result;
        try
        {
            var revision = new UInt64String(checked(Interlocked.Read(ref measurementRevision) + 1));
            var committed = await history.SaveMeasurementSettingsAsync(principal, settings, key, etag,
                preferences => MeasurementSnapshot(principal, owner, preferences, measurementRun, revision), ct);
            if (!committed.Applied) return committed.Value;
            Interlocked.Increment(ref measurementRevision);
            result = committed.Value;
        }
        finally { measurementLock.Release(); }
        await PublishMeasurementAsync();
        return result;
    }

    /// <summary>观察循环每轮调用：首次伤害到达后进入计时，窗口结束（并等迟到帧进入窗口）后转入历史。</summary>
    private async Task TickMeasurementAsync(Job job)
    {
        if (measurementRun is not { } candidate || candidate.Job != job) return;
        var changed = false;
        await measurementLock.WaitAsync();
        try
        {
            if (measurementRun is not { } run || run != candidate) return;
            if (!run.Running && job.Session?.MeasurementProgress() is { Waiting: false } progress && double.IsFinite(progress.EndMs))
            { run.StartMs = progress.StartMs; run.EndMs = progress.EndMs; run.Running = true; changed = true; }
            var now = MeasurementNow(run);
            if (run.Running && now >= run.EndMs && (job.LastMono > run.EndMs || now >= run.EndMs + MeasurementSettleMs))
            {
                var finished = job.Session!.FinishMeasurement(clock.GetUtcNow());
                lastMeasurement = finished?.Record?.Measurement; lastMeasurementId = finished?.Id;
                measurementRun = null; changed = true;
            }
            if (changed) Interlocked.Increment(ref measurementRevision);
        }
        finally { measurementLock.Release(); }
        if (changed) await PublishMeasurementAsync();
    }

    /// <summary>
    /// 重设：会话已取消本观察进行中的测量，这里释放测量状态并清除最近一次结果（历史里保存的测量记录不变）。
    /// 调用方持有任务锁，与开始、帧更新一样先任务锁再测量锁。
    /// </summary>
    private async Task ResetMeasurementAsync(Job job)
    {
        await measurementLock.WaitAsync();
        try
        {
            if (measurementRun?.Job == job) measurementRun = null;
            lastMeasurement = null; lastMeasurementId = null;
            Interlocked.Increment(ref measurementRevision);
        }
        finally { measurementLock.Release(); }
        await PublishMeasurementAsync();
    }

    /// <summary>观察结束时收尾：已开始的测量以停止时点结束并标为不完整，返回需要随收尾检查点保存的记录与时间线。</summary>
    private (CombatEncounter[] Encounters, CombatTimelineRecord[] Timeline) FinishMeasurementOnStop(Job job, string reason)
    {
        if (measurementRun?.Job != job || job.Session is not { } session) return ([], []);
        if (session.FinishMeasurement(clock.GetUtcNow(), reason) is not { } finished) return ([], []);
        var checkpoint = session.Capture();
        return (checkpoint.Encounters.Where(x => x.Id == finished.Id).ToArray(), checkpoint.Timeline.Where(x => x.EncounterId == finished.Id).ToArray());
    }

    /// <summary>观察结束后清除测量状态；保存了结果的测量成为最近一次结果。</summary>
    private async Task ReleaseMeasurementAsync(Job job)
    {
        if (measurementRun?.Job != job) return;
        await measurementLock.WaitAsync();
        try
        {
            if (measurementRun is not { } run || run.Job != job) return;
            try
            {
                var saved = history.Encounter(run.Id, "owner", true);
                if (saved.Summary.Final && saved.Record?.Measurement is { } result) { lastMeasurement = result; lastMeasurementId = saved.Id; }
            }
            catch (OperationException) { }
            measurementRun = null; Interlocked.Increment(ref measurementRevision);
        }
        finally { measurementLock.Release(); }
        await PublishMeasurementAsync();
    }

    private async Task PublishMeasurementAsync()
    {
        if (events is null) return;
        try
        {
            var state = MeasurementSnapshot("owner", true, history.MeasurementPreferences());
            await events.PublishVolatileAsync(new("combat-measurement-" + state.Revision, "combat-measurement", "combat-measurement",
                state.Revision, "combat.measurement.changed", JsonSerializer.SerializeToElement(state, ContractJson.Options)), clock.GetUtcNow());
        }
        catch (OperationException) { }
    }
}
