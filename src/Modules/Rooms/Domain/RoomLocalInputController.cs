using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Modules.Rooms.Domain;

public sealed record RoomInputPorts(GameSessionBindingTracker Bindings, ResourceLeaseCoordinator Leases,
    GameInputBroker Input, Func<GameWindowBinding, bool>? Activate = null,
    Func<GamePlaybackEvidence?>? Playback = null, bool Simulation = false);
public sealed class RoomInputException(string code, string? occupiedBy = null) : Exception(code)
{ public string Code { get; } = code; public string? OccupiedBy { get; } = occupiedBy; }
public sealed record LocalRoomCapabilities(bool Simulation, bool CanStart, bool CanStop, double RoundTripMs, int ActivationMs);

/// <summary>固定本轮本地配置，准备时预留资源，调度时只执行已准备的动作。</summary>
public sealed class RoomLocalInputController : IInputExecutor
{
    private sealed record LocalAction(ScheduledAction Action, IReadOnlyList<InputControl> Controls, PhysicalScreenPoint? Point, bool Toggle);
    private readonly object gate = new();
    private readonly string owner;
    private readonly RoomInputPorts ports;
    private readonly IMonotonicClock clock;
    private readonly RoomClock roomClock;
    private readonly TimeProvider time;
    private readonly Func<RoomPreferences> preferences;
    private readonly Func<bool> authorized;
    private readonly Func<InputBindingPreference, bool> conflict;
    private readonly Func<bool> operateGame;
    private readonly Dictionary<string, LocalAction> actions = new(StringComparer.Ordinal);
    private readonly RoomClockInputTimingQuality timing = new();
    private ResourceLeaseSnapshot? lease;
    private GameWindowBinding? binding;
    private RoomPreferences? frozen;
    private ModeInputPreferences? modeBindings;
    private string? id, mode, sessionId, reason;
    private int offset;
    private bool canStart, canStop, chatConfirmed, started;
    // detached：“操作游戏”关闭时的模拟准备，不需要游戏绑定，不持有租约，不发送输入。
    private bool simulated, detached, detachedArmed;
    private ulong revision;
    private string state = "unprepared";
    private EffectStatus effect = EffectStatus.None;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    /// <summary>按键与点击按下后保持的毫秒数（原工具 MabiSync GameInput.Execute 的 <c>WaitOne(40)</c>）。</summary>
    public const int KeyHoldMs = 40;

    public RoomLocalInputController(string owner, RoomInputPorts ports, IMonotonicClock clock, RoomClock roomClock,
        Func<RoomPreferences> preferences, Func<bool>? authorized = null, TimeProvider? time = null,
        Func<InputBindingPreference, bool>? conflictsWithShortcut = null, Func<bool>? operateGame = null)
    {
        this.operateGame = operateGame ?? (() => true);
        this.owner = owner; this.ports = ports; this.clock = clock; this.roomClock = roomClock;
        this.preferences = preferences; this.authorized = authorized ?? (() => true); this.time = time ?? TimeProvider.System;
        conflict = conflictsWithShortcut ?? (value => value.Kind.KnownValue == LocalBindingKind.Key && value.Key == 0x78 && !value.Control && !value.Alt && !value.Shift);
    }

    public RoomLocalPreparation Snapshot
    {
        get { lock (gate) return new(id, state, binding?.Session.Id, binding?.Session.BindingRevision, frozen?.Revision,
            mode, offset, lease is not null || detachedArmed ? simulated : ports.Simulation || !operateGame(), canStart, canStop, sessionId, effect, reason, new(revision)); }
    }

    public LocalRoomCapabilities Arm(RoomReadinessRequest request, string selectedMode)
    {
        lock (gate)
        {
            if (!authorized()) throw new RoomInputException("permission.denied");
            if (request.TimingOffsetMs is < -2000 or > 2000) throw new RoomInputException("room.timingOffsetInvalid");
            var detach = !operateGame();
            if (request.Simulation != (ports.Simulation || detach)) throw new RoomInputException("room.simulationUnavailable");
            var value = JsonSerializer.Deserialize<RoomPreferences>(JsonSerializer.Serialize(preferences(), ContractJson.Options), ContractJson.Options)!;
            if (detach) return ArmDetached(request, selectedMode, value);
            var current = ports.Bindings.Current ?? throw new RoomInputException("input.bindingUnavailable");
            if (request.ExpectedBindingRevision is { } expected && expected != current.Session.BindingRevision ||
                request.ExpectedPreferencesRevision is { } expectedPreferences && expectedPreferences != value.Revision)
                throw new RoomInputException("revision.stale");
            if (selectedMode is not ("emote" or "independent" or "ensemble")) throw new RoomInputException("request.invalid");
            if (timing.ValidateForPreparation("readiness", roomClock, request.TimingOffsetMs) is { } quality) throw new RoomInputException(quality);
            if (started) throw new RoomInputException("room.activityActive");
            if (lease is not null && binding == current && frozen?.Revision == value.Revision && mode == selectedMode &&
                offset == request.TimingOffsetMs && chatConfirmed == request.ChatDraftConfirmed)
            {
                ValidateCurrent(true);
                return new(ports.Simulation, canStart, canStop, roomClock.Estimate!.RoundTripMs, 0);
            }
            ReleaseCore("readiness-replaced");
            Freeze(request, selectedMode, value, false);
            try
            {
                lease = ports.Leases.Acquire(new(owner, "room-" + id, current.Session.Id, current.Session.BindingRevision,
                    ["foreground-input", "activity-control:" + current.Session.Id], LeaseDuration));
                var before = clock.NowMs;
                if (ports.Activate is not null && !ports.Activate(current)) throw new RoomInputException("input.focusChanged");
                var restored = ports.Bindings.Current;
                if (restored is null || !restored.CanAcceptInput || restored.Session.Id != current.Session.Id)
                    throw new RoomInputException("input.bindingChanged");
                lease = ports.Leases.RebindPrepared(lease.Id, lease.FencingToken, restored.Session.BindingRevision);
                binding = restored;
                if (canStart) _ = Map(new("validate-start", "validation", "start", 0, "validation") { Mode = mode! });
                if (canStop) _ = Map(new("validate-stop", "validation", "stop", 0, "validation") { Mode = mode! });
                if (!ports.Simulation) ports.Input.ValidateAccess(Authority(InputActionKind.Press, InputControl.Space));
                state = "prepared"; revision++;
                return new(ports.Simulation, canStart, canStop, roomClock.Estimate!.RoundTripMs,
                    (int)Math.Clamp(clock.NowMs - before, 0, 5000));
            }
            catch (ResourceLeaseException error) { ReleaseCore(error.Code); throw new RoomInputException(error.Code, error.ConflictingOwner); }
            catch (GameInputException error) { ReleaseCore(error.Code); throw new RoomInputException(error.Code); }
            catch { ReleaseCore("preparation-failed"); throw; }
        }
    }

    private void Freeze(RoomReadinessRequest request, string selectedMode, RoomPreferences value, bool detach)
    {
        id = Guid.NewGuid().ToString("N"); frozen = value; mode = selectedMode; offset = request.TimingOffsetMs;
        detached = detach; simulated = ports.Simulation || detach;
        chatConfirmed = request.ChatDraftConfirmed; effect = EffectStatus.None; sessionId = null; reason = null;
        if (!value.Bindings.TryGetValue(mode, out modeBindings)) throw new RoomInputException("input.actionMappingUnavailable");
        if (simulated && modeBindings.Start is null)
            modeBindings = new(new(LocalBindingKind.Key, 32), mode == "emote" ? null : new(LocalBindingKind.Key, 27), false);
        canStart = !(mode == "ensemble" && request.MusicRole == "joiner");
        canStop = mode != "emote";
    }

    private LocalRoomCapabilities ArmDetached(RoomReadinessRequest request, string selectedMode, RoomPreferences value)
    {
        if (request.ExpectedPreferencesRevision is { } expectedPreferences && expectedPreferences != value.Revision) throw new RoomInputException("revision.stale");
        if (selectedMode is not ("emote" or "independent" or "ensemble")) throw new RoomInputException("request.invalid");
        if (timing.ValidateForPreparation("readiness", roomClock, request.TimingOffsetMs) is { } quality) throw new RoomInputException(quality);
        if (started) throw new RoomInputException("room.activityActive");
        ReleaseCore("readiness-replaced");
        try
        {
            Freeze(request, selectedMode, value, true);
            if (canStart) _ = Map(new("validate-start", "validation", "start", 0, "validation") { Mode = mode! });
            if (canStop) _ = Map(new("validate-stop", "validation", "stop", 0, "validation") { Mode = mode! });
            detachedArmed = true; state = "prepared"; revision++;
            return new(true, canStart, canStop, roomClock.Estimate!.RoundTripMs, 0);
        }
        catch { ReleaseCore("preparation-failed"); throw; }
    }

    public bool IsSimulation(ScheduledAction action) => simulated;
    public InputPreparation Prepare(ScheduledAction action)
    {
        lock (gate)
        {
            try
            {
                ValidateCurrent(action.Kind == "start");
                if (action.Mode != mode || sessionId is not null && sessionId != action.SessionId) throw new RoomInputException("room.planStale");
                if (actions.TryGetValue(action.Id, out var existing)) return new(existing.Action == action, existing.Action == action ? null : "room.planStale");
                if (actions.Count >= 32) throw new RoomInputException("room.preparationCapacityExceeded");
                // 校时质量只约束开始：停止（含立即停止与一轮后停止）晚到也必须送达，否则游戏内演奏会一直循环。
                if (action.Kind == "start" && timing.ValidateForPreparation(action.SessionId, roomClock, offset) is { } error) throw new RoomInputException(error);
                if (!simulated && ports.Activate is not null && !ports.Activate(binding!)) throw new RoomInputException("input.focusChanged");
                ValidateCurrent(action.Kind == "start");
                if (!simulated) ports.Input.ValidateAccess(Authority(InputActionKind.Press, InputControl.Space));
                actions.Add(action.Id, Map(action)); sessionId = action.SessionId;
                if (mode == "ensemble" && !canStart && action.Kind == "stop") started = true;
                state = started ? "performing" : "armed"; revision++;
                return new(true, null);
            }
            catch (RoomInputException error) { return Rejected(action, error.Code); }
            catch (GameInputException error) { return Rejected(action, error.Code); }
        }
    }

    // 立即停止（本机停止、全员停止、紧急停止，目标时刻为 0）没能准备时本轮在本机已无法继续：释放准备，下一轮开始不再被 room.activityActive 挡住。
    private InputPreparation Rejected(ScheduledAction action, string code)
    {
        if (action.Kind == "stop" && action.TargetServerMs == 0 && started) ReleaseCore(code);
        return new(false, code);
    }

    /// <summary>
    /// 到点前 <see cref="ActionScheduler.PrimeLeadMs"/>：先把游戏窗口切到前台并等前台确认（最多约 200 ms），到点时停止键直接发出。
    /// 到点时的再次激活在游戏已是前台时立即返回。只切窗口，不发送游戏输入。
    /// </summary>
    public void Prime(ScheduledAction action)
    {
        lock (gate)
        {
            if (simulated || binding is null || lease is null || !actions.TryGetValue(action.Id, out var plan) || plan.Action != action) return;
            ports.Activate?.Invoke(binding);
        }
    }

    public InputOutcome Execute(ScheduledAction action, CancellationToken ct)
    {
        lock (gate)
        {
            if (!actions.TryGetValue(action.Id, out var plan) || plan.Action != action) return new("rejected", null, simulated, "room.planStale");
            var dispatched = false;
            double? pressedAt = null;
            try
            {
                ct.ThrowIfCancellationRequested(); ValidateCurrent(action.Kind == "start");
                if (plan.Toggle && !simulated)
                {
                    var observed = ports.Playback?.Invoke();
                    var age = observed is null ? TimeSpan.MaxValue : time.GetUtcNow() - observed.ObservedAt;
                    if (observed is null || !observed.IsPlaying || observed.GameSessionId != binding!.Session.Id ||
                        observed.Quality.KnownValue != ObservationQuality.Confirmed || observed.ObservedAt.Offset != TimeSpan.Zero ||
                        age < TimeSpan.Zero || age > TimeSpan.FromSeconds(2)) throw new RoomInputException("input.stopEvidenceRequired");
                }
                // 停止可能在游戏被遮挡或用户切到其他窗口时到点（原工具 GameInput.Execute）：先恢复并前置游戏窗口再发送。
                if (!simulated && action.Kind == "stop" && ports.Activate is not null && !ports.Activate(binding!))
                    throw new RoomInputException("input.focusChanged");
                if (!simulated)
                {
                    if (plan.Point is { } point) { ct.ThrowIfCancellationRequested(); ports.Input.Execute(Authority(InputActionKind.MovePointer, null) with { Point = point }); }
                    foreach (var control in plan.Controls)
                    {
                        ct.ThrowIfCancellationRequested(); effect = EffectStatus.Unknown;
                        ports.Input.Execute(Authority(InputActionKind.Press, control)); pressedAt ??= clock.NowMs; dispatched = true; effect = EffectStatus.InputDispatched;
                    }
                    // 与原工具 GameInput.Execute 相同：按下在到点时刻，保持 KeyHoldMs 后再抬起，游戏才能可靠识别这次按键或点击。
                    if (ct.WaitHandle.WaitOne(KeyHoldMs)) ct.ThrowIfCancellationRequested();
                    foreach (var control in plan.Controls.Reverse()) { ct.ThrowIfCancellationRequested(); ports.Input.Execute(Authority(InputActionKind.Release, control)); }
                }
                started = action.Kind == "start" && mode != "emote"; state = started ? "performing" : "released";
                if (action.Kind == "start" && mode == "emote" || action.Kind == "stop") ReleaseCore("action-finished");
                revision++; return new("sent", pressedAt ?? clock.NowMs, simulated, null);
            }
            catch (RoomInputException error)
            {
                if (dispatched) effect = EffectStatus.Unknown;
                // 停止没有送达时本轮在本机已结束，释放准备；否则下一轮开始会被 room.activityActive 拒绝，要再点一次才恢复。
                // 切换式停止缺少演奏证据时保留准备，可以在拿到证据后再停一次。
                if (action.Kind == "stop" && error.Code != "input.stopEvidenceRequired") ReleaseCore(error.Code);
                else { reason = error.Code; revision++; }
                return new("rejected", null, simulated, error.Code);
            }
            catch (GameInputException error) { if (dispatched) effect = EffectStatus.Unknown; ReleaseCore(error.Code); return new("rejected", null, simulated, error.Code); }
            catch (OperationCanceledException) { if (dispatched) effect = EffectStatus.Unknown; ReleaseCore("cancelled"); throw; }
            finally
            {
                actions.Remove(action.Id);
                if (lease is not null) ports.Input.ReleaseHeldInputs(lease.Id);
            }
        }
    }

    private LocalAction Map(ScheduledAction action)
    {
        var stop = action.Kind == "stop";
        if (stop && !canStop || !stop && !canStart) throw new RoomInputException("input.actionMappingUnavailable");
        var value = stop ? modeBindings?.Stop : modeBindings?.Start;
        if (value is null) throw new RoomInputException("input.actionMappingUnavailable");
        var chat = !stop && (value.Kind.KnownValue == LocalBindingKind.ChatDraft || frozen!.StartInputKinds.GetValueOrDefault(mode!) == 2);
        if (chat)
        {
            if (mode != "emote" || !chatConfirmed) throw new RoomInputException("room.chatDraftUnconfirmed");
            return new(action, [InputControl.Enter], null, false);
        }
        if (conflict(value)) throw new RoomInputException("input.shortcutConflict");
        var toggle = stop && modeBindings!.StopIsToggle;
        if (toggle && !simulated && ports.Playback is null) throw new RoomInputException("input.stopEvidenceRequired");
        if (value.Kind.KnownValue == LocalBindingKind.Key)
        {
            if (value.Key is null or 0 or >= 255) throw new RoomInputException("input.actionMappingUnavailable");
            var controls = new List<InputControl>();
            if (value.Control) controls.Add(InputControls.VirtualKey(0x11));
            if (value.Alt) controls.Add(InputControls.VirtualKey(0x12));
            if (value.Shift) controls.Add(InputControls.VirtualKey(0x10));
            controls.Add(InputControls.VirtualKey(value.Key.Value));
            return new(action, controls.Distinct().ToArray(), null, toggle);
        }
        if (value.Kind.KnownValue == LocalBindingKind.Button && detached) return new(action, [InputControl.MouseLeft], null, toggle);
        if (value.Kind.KnownValue == LocalBindingKind.Button)
        {
            if (value.X is null or < 0 || value.Y is null or < 0 || value.ClientWidth is null or <= 0 || value.ClientHeight is null or <= 0 ||
                value.X >= value.ClientWidth || value.Y >= value.ClientHeight || value.ClientWidth != binding!.Bounds.Width ||
                value.ClientHeight != binding.Bounds.Height || value.Dpi is null || value.Dpi != (uint)binding.DpiX || binding.DpiX != binding.DpiY)
                throw new RoomInputException("input.bindingGeometryMismatch");
            return new(action, [InputControl.MouseLeft], new(checked(binding.Bounds.X + value.X.Value), checked(binding.Bounds.Y + value.Y.Value)), toggle);
        }
        throw new RoomInputException("input.actionMappingUnavailable");
    }

    private GameInputAction Authority(InputActionKind kind, InputControl? control) => new(owner, lease!.Id, lease.FencingToken,
        binding!.Session.Id, binding.Session.BindingRevision, binding.Session.WindowIdentity, kind, control);
    private void ValidateCurrent(bool checkPreferences)
    {
        if (!authorized()) { ReleaseCore("permission.denied"); throw new RoomInputException("permission.denied"); }
        if (detached)
        {
            if (!detachedArmed) throw new RoomInputException("room.planStale");
            if (checkPreferences && preferences().Revision != frozen!.Revision) { ReleaseCore("room.planStale"); throw new RoomInputException("room.planStale"); }
            return;
        }
        if (lease is null || binding is null) throw new RoomInputException("room.planStale");
        if (ports.Bindings.Current != binding) { ReleaseCore("input.bindingChanged"); throw new RoomInputException("input.bindingChanged"); }
        if (checkPreferences && preferences().Revision != frozen!.Revision) { ReleaseCore("room.planStale"); throw new RoomInputException("room.planStale"); }
        if (!ports.Leases.Validate(lease.Id, "foreground-input", lease.FencingToken, binding.Session.Id, binding.Session.BindingRevision, owner))
        { ReleaseCore("resource.leaseExpired"); throw new RoomInputException("resource.leaseExpired"); }
    }
    public void KeepAlive()
    {
        lock (gate)
        {
            if (lease is null) return;
            try { ValidateCurrent(!started); lease = ports.Leases.Renew(lease.Id, lease.FencingToken, LeaseDuration); }
            catch (RoomInputException) { }
            catch (ResourceLeaseException error) { ReleaseCore(error.Code); }
        }
    }
    public void WithdrawReadiness() { lock (gate) if ((lease is not null || detachedArmed) && !started && sessionId is null && actions.Count == 0) ReleaseCore("unready"); }
    public void Release(string preparationId)
    { lock (gate) { if (id != preparationId) throw new RoomInputException("room.planStale"); ReleaseCore("user-released"); } }
    public void Forget(string actionId) { lock (gate) actions.Remove(actionId); }
    public void ReleaseSession(string actionSessionId) { lock (gate) if (sessionId == actionSessionId) ReleaseCore("session-cancelled"); }
    public void ReleaseAll() { lock (gate) if (lease is not null || detachedArmed || actions.Count > 0) ReleaseCore("interrupted"); }
    public void CancelPendingForStop(string actionSessionId)
    {
        lock (gate)
        {
            if (sessionId != actionSessionId) return;
            actions.Clear();
            if (lease is not null) ports.Input.ReleaseHeldInputs(lease.Id);
        }
    }
    private void ReleaseCore(string why)
    {
        var previous = lease; lease = null; detachedArmed = false; actions.Clear(); canStart = false; canStop = false;
        if (started) effect = EffectStatus.Unknown;
        started = false; sessionId = null; state = id is null ? "unprepared" : "released"; reason = why; revision++;
        if (previous is not null) ports.Input.ReleaseRun(previous.Id);
    }
}
