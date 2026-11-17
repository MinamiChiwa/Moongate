using System.Text.Json;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.CombatObservation.Application;

namespace MabiTool.Host.Game;

public sealed partial class GameObservationService
{
    private readonly Dictionary<string, CaptureCombatSubscription> combatDemands = new(StringComparer.Ordinal);
    private ObservationCaptureInterests appliedInterests = new(true, null);
    private ObservationCaptureInterests? requestedInterests;
    private TaskCompletionSource? interestAcknowledgement;
    /// <summary>等待本机身份时核对授权与绑定的间隔；首次未就绪即公开 combat.identityPending。</summary>
    internal static TimeSpan IdentityPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public async ValueTask<ICombatObservationSubscription> OpenCombatAsync(string principal, string observationId,
        CombatObservationRequest request, ObservationCombatProfile configured, CancellationToken ct,
        Action? onInterestsReady = null, Func<Task>? onIdentityPending = null, bool residentDemand = false)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("observation.workerUnavailable", "观察组件需要 Windows");
        Authorize(principal);
        if (!auth.HasPermission(principal, ApiPermissions.CombatControl)) throw new OperationException("permission.denied", "需要战斗控制权限");
        CaptureCombatSubscription? subscription = null;
        try
        {
            await lifecycle.WaitAsync(ct);
            try
            {
                lock (gate)
                {
                    if (worker is null || state is not ("running" or "starting"))
                        throw new OperationException("observation.channelUnavailable", "请先选择网卡并启动共享游戏观察");
                    if (game?.Id != request.GameSessionId || !SameGameSession(controls.Bindings.Current?.Session, game))
                        throw new OperationException("input.bindingChanged", "共享观察的游戏绑定已变化");
                    if (!controls.Simulation && game.GameBuild != configured.Source.GameBuild)
                        throw new OperationException("combat.buildUnsupported", "战斗来源与当前构建不匹配");
                    if (!configured.Simulation && profile != MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol.CaptureProfile)
                        throw new OperationException("combat.buildUnsupported", "当前战斗兴趣需要匹配的捕获 profile");
                    if (TotalDemands >= 8) throw new OperationException("resource.capacityExceeded", "共享观察需求已达到上限");
                    if (combatDemands.ContainsKey(observationId)) throw new OperationException("resource.busy", "该战斗观察已有共享订阅");
                    if (combatDemands.Values.Any(value => value.Profile != configured))
                        throw new OperationException("resource.busy", "共享捕获正在使用其他战斗协议");
                    if (residentDemand)
                    {
                        if (barrier.Snapshot().MaintenanceOwner is not null)
                            throw new OperationException("update.maintenance", "更新维护进行中，稍后自动开始观测");
                    }
                    else HoldActivity(principal);
                    var id = appliedInterests.Combat?.Id ?? Guid.NewGuid().ToString("N");
                    subscription = new(this, principal, observationId, id, request, configured, residentDemand);
                    combatDemands.Add(observationId, subscription); revision++;
                }
                TraceSubscription("combat", "added");
                await ConfigureInterestsAsync(ct);
            }
            finally { lifecycle.Release(); }
            Authorize(principal);
            if (!auth.HasPermission(principal, ApiPermissions.CombatControl))
                throw new OperationException("permission.denied", "战斗控制授权已撤销");
            if (!SameGameSession(controls.Bindings.Current?.Session, game))
                throw new OperationException("input.bindingChanged", "等待战斗身份时游戏绑定已变化");
            ct.ThrowIfCancellationRequested();
            onInterestsReady?.Invoke();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
            // 本机身份只在登录或切图时出现：未知时不设时限，保持等待并公开等待原因，直到身份出现、取消、授权撤销或绑定变化。
            var pendingReported = false;
            while (!subscription.Ready.IsCompleted)
            {
                try { await subscription.Ready.WaitAsync(IdentityPollInterval, lifetime.Token); }
                catch (TimeoutException) { }
                if (subscription.Ready.IsCompleted) break;
                Authorize(principal);
                if (!auth.HasPermission(principal, ApiPermissions.CombatControl)) throw new OperationException("permission.denied", "战斗控制授权已撤销");
                if (!SameGameSession(controls.Bindings.Current?.Session, game)) throw new OperationException("input.bindingChanged", "等待战斗身份时游戏绑定已变化");
                if (!pendingReported && onIdentityPending is not null) { pendingReported = true; await onIdentityPending(); }
            }
            await subscription.Ready;
            Authorize(principal);
            if (!auth.HasPermission(principal, ApiPermissions.CombatControl)) throw new OperationException("permission.denied", "战斗控制授权已撤销");
            if (!SameGameSession(controls.Bindings.Current?.Session, game)) throw new OperationException("input.bindingChanged", "等待战斗身份时游戏绑定已变化");
            Notify(); return subscription;
        }
        catch
        {
            if (subscription is not null)
            {
                await ReleaseCombatAsync(subscription);
            }
            throw;
        }
    }
    internal async Task ReleaseCombatAsync(CaptureCombatSubscription subscription)
    {
        await lifecycle.WaitAsync(CancellationToken.None);
        try { await ReleaseCombatCoreAsync(subscription, null); }
        finally { lifecycle.Release(); }
    }
    private async Task ReleaseCombatCoreAsync(CaptureCombatSubscription subscription, Exception? failure)
    {
        lock (gate)
        {
            if (!combatDemands.ContainsKey(subscription.Id)) { subscription.Complete(failure); return; }
            subscription.Closing = true;
        }
        if (worker is not null && state is "running" or "starting")
            try { await ConfigureInterestsAsync(CancellationToken.None); }
            catch (Exception error) when (error is WorkerException or OperationException or OperationCanceledException)
            { failure ??= new OperationException("combat.observationGap", "共享捕获停止交接未完成"); OnFault(captureId!, "observation.captureFailed"); }
        lock (gate) { combatDemands.Remove(subscription.Id); revision++; }
        TraceSubscription("combat", "removed");
        subscription.Complete(failure);
        if (TotalDemands == 0 && worker is not null && (!resident || failure is not null)) await StopCoreAsync(failure is null ? null : "observation.captureFailed");
        else ReleaseIdleActivity();
        Notify();
    }
    private async Task ReleaseUnauthorizedCombatAsync()
    {
        CaptureCombatSubscription[] revoked;
        lock (gate) revoked = combatDemands.Values.Where(value => !auth.HasPermission(value.Principal, ApiPermissions.GameObserve) ||
            !auth.HasPermission(value.Principal, ApiPermissions.CombatControl)).ToArray();
        foreach (var value in revoked) await ReleaseCombatCoreAsync(value, new OperationException("permission.denied", "观察授权已撤销"));
    }
    private async Task ConfigureInterestsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("observation.workerUnavailable", "观察组件需要 Windows");
        ObservationCaptureInterests next; WorkerSession? session; WorkerCall? current;
        lock (gate)
        {
            var active = combatDemands.Values.Where(value => !value.Closing).ToArray();
            var combat = active.Length == 0 ? null : new ObservationCombatInterest(active[0].InterestId, active[0].Profile.Id,
                active.Any(value => value.Request.DamageEnabled), active.Any(value => value.Request.SelfEffectsEnabled), active.Any(value => value.Request.MonsterEffectsEnabled));
            var inventories = inventoryDemands.Values.Where(value => !value.Closing).ToArray();
            var inventory = inventories.Length == 0 ? null : new ObservationInventoryInterest(inventories[0].InterestId, inventories[0].Profile.Id);
            // 身份与状态心跳始终发送；只有音乐需求开启合奏和演奏解码。
            next = new(demands.Count > 0, combat, inventory); session = worker; current = call;
            if (session is null || current is null || next == appliedInterests) return;
            requestedInterests = next; interestAcknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            var acknowledgement = interestAcknowledgement;
            // 命令已发送后完成有界交接，避免订阅取消触发整个共享捕获的运行级取消。
            var result = await session.CommandAsync(current, id => new { commandId = id, method = "observation.interests", arguments = next }, payload =>
            {
                if (payload.EnumerateObject().Count() != 4 || !payload.TryGetProperty("accepted", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !payload.TryGetProperty("code", out var code) || !payload.TryGetProperty("interests", out var interests) ||
                    interests.Deserialize<ObservationCaptureInterests>(ContractJson.Options) is not { } actual ||
                    accepted.GetBoolean() && (code.ValueKind != JsonValueKind.Null || actual != next) ||
                    !accepted.GetBoolean() && (code.ValueKind != JsonValueKind.String || code.GetString() is not ("combat.buildUnsupported" or "inventory.buildUnsupported")))
                    throw new WorkerException("worker.commandInvalid");
            }, CancellationToken.None);
            if (!result.GetProperty("accepted").GetBoolean()) throw new OperationException(result.GetProperty("code").GetString()!, "当前观察源未登记所选协议");
            try { await acknowledgement!.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { throw new WorkerException("worker.commandTimeout"); }
            lock (gate)
            {
                appliedInterests = next;
                if (!next.MusicEnabled && latest is { } observed)
                    latest = observed with { Value = observed.Value with { Concert = null, Music = null } };
            }
            if (trace?.IsEnabled(DiagnosticEvents.InterestsChanged) == true)
                trace.Emit(DiagnosticEvents.InterestsChanged, new DiagnosticFields().Flag("music", next.MusicEnabled)
                    .Flag("combat", next.Combat is not null).Flag("inventory", next.Inventory is not null));
        }
        finally { lock (gate) { requestedInterests = null; interestAcknowledgement = null; } }
    }
    private bool AcceptCombatEvent(WorkerFrame frame)
    {
        if (frame.Payload.TryGetProperty("interestsApplied", out var marker))
        {
            if (frame.Payload.EnumerateObject().Count() != 1 || marker.Deserialize<ObservationCaptureInterests>(ContractJson.Options) is not { } value)
                throw new WorkerException("worker.commandInvalid");
            lock (gate)
            {
                if (value != requestedInterests) throw new WorkerException("worker.commandInvalid");
                interestAcknowledgement?.TrySetResult();
            }
            return true;
        }
        if (!frame.Payload.TryGetProperty("combat", out var payload)) return false;
        double? age = null;
        try
        {
            if (frame.Payload.EnumerateObject().Count() != 1) throw CaptureCombatValidation.Invalid("envelope.members");
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("interestId", out var interest) ||
                interest.ValueKind != JsonValueKind.String || !WorkerProtocol.IsId(interest.GetString()))
                throw CaptureCombatValidation.Invalid("envelope.interest");
            CaptureCombatSubscription[] targets;
            lock (gate) targets = combatDemands.Values.Where(value => value.InterestId == interest.GetString()).ToArray();
            if (targets.Length == 0) return true;
            if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > 256 * 1024) throw CaptureCombatValidation.Invalid("payload.size");
            var report = payload.Deserialize<CombatCaptureReport>(CaptureCombatValidation.Json) ?? throw CaptureCombatValidation.Invalid("payload.null");
            if (report.Frame is { } value && double.IsFinite(value.MonotonicMs)) age = Mono() - value.MonotonicMs;
            CaptureCombatValidation.Validate(report, targets[0].Source, targets[0].Request.GameSessionId);
            if (report.Frame.MonotonicMs > Mono() + 200) throw CaptureCombatValidation.Invalid("frame.ahead");
            if (Mono() - report.Frame.MonotonicMs > 2000) throw CaptureCombatValidation.Invalid("frame.stale");
            RecordCombatGaps(report.Frame.Gaps);
            foreach (var target in targets) target.Publish(report);
            return true;
        }
        catch (WorkerException error) when (CaptureCombatValidation.Branch(error) is { } branch)
        {
            lock (gate)
            {
                frameRejection ??= new(branch, age, arrivalIntervalMs, Mono());
                frameRejections++;
                var key = frameRejectionBranches.ContainsKey(branch) || frameRejectionBranches.Count < MaximumRejectionBranches ? branch : "other";
                frameRejectionBranches[key] = frameRejectionBranches.GetValueOrDefault(key) + 1;
            }
            if (trace?.IsEnabled(DiagnosticEvents.FrameRejected) == true)
                trace.Emit(DiagnosticEvents.FrameRejected, new DiagnosticFields().Value("branch", branch), exception: error);
            throw;
        }
    }
    /// <summary>进程内诊断：首个战斗帧拒绝分支及已接受帧中的缺口时点（Worker 单调毫秒），供实机探针定位。</summary>
    public CombatCaptureDiagnostics CombatDiagnostics()
    {
        lock (gate) return new(frameRejection, combatGaps.ToArray(), combatGapsTruncated);
    }
    private void RecordCombatGaps(IReadOnlyList<CombatGapInput> gaps)
    {
        if (gaps.Count > 0 && trace?.IsEnabled(DiagnosticEvents.CaptureGap) == true)
            foreach (var gap in gaps)
                trace.Emit(DiagnosticEvents.CaptureGap, new DiagnosticFields().Value("reason", gap.Reason).Count("droppedEvents", gap.DroppedEvents.Value));
        lock (gate)
            foreach (var gap in gaps)
            {
                if (combatGaps.Count >= 64) { combatGapsTruncated++; continue; }
                combatGaps.Add(new(gap.Reason, gap.FromMs, gap.ToMs, gap.DroppedEvents.Value));
            }
    }
    private void ResetCombatDiagnostics()
    {
        lock (gate)
        {
            frameRejection = null; frameRejections = 0; frameRejectionBranches.Clear();
            combatGaps.Clear(); combatGapsTruncated = 0; arrivalIntervalMs = null; lastArrivalMono = null;
        }
    }
}

/// <param name="Branch">拒绝分支，例如 frame.stale、damage.time。</param>
/// <param name="FrameAgeMs">Host 收到时与帧 MonotonicMs 的差值；帧时间不可读时为空。</param>
/// <param name="ArrivalIntervalMs">该帧与上一条 Worker 事件的到达间隔。</param>
/// <param name="HostMonotonicMs">拒绝时的 Host 单调毫秒，与 Worker 帧时间同一时钟。</param>
public sealed record CombatFrameRejection(string Branch, double? FrameAgeMs, double? ArrivalIntervalMs, double HostMonotonicMs);
public sealed record CombatGapDiagnostic(string Reason, double FromMs, double? ToMs, ulong DroppedEvents);
public sealed record CombatCaptureDiagnostics(CombatFrameRejection? FrameRejection, IReadOnlyList<CombatGapDiagnostic> Gaps, long GapsTruncated);

public sealed partial class GameObservationService
{
    private CombatFrameRejection? frameRejection;
    /// <summary>拒绝分支为固定码（现有 18 种）；分布最多保留 24 种，之后的新分支计入 other。</summary>
    private const int MaximumRejectionBranches = 24;
    private ulong frameRejections;
    private readonly Dictionary<string, ulong> frameRejectionBranches = new(StringComparer.Ordinal);
    private readonly List<CombatGapDiagnostic> combatGaps = [];
    private long combatGapsTruncated;
    private double? arrivalIntervalMs, lastArrivalMono;
    private void TraceSubscription(string kind, string action)
    {
        if (trace?.IsEnabled(DiagnosticEvents.SubscriptionChanged) != true) return;
        int count; lock (gate) count = TotalDemands;
        trace.Emit(DiagnosticEvents.SubscriptionChanged, new DiagnosticFields().Value("kind", kind).Value("action", action).Count("count", count));
    }
    private void RecordArrival()
    {
        var now = Mono();
        lock (gate) { arrivalIntervalMs = lastArrivalMono is { } last ? now - last : null; lastArrivalMono = now; }
    }
}
