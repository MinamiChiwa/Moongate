using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public enum InputControl { Space, Enter, Escape, MouseLeft, MouseRight, MouseMiddle }
public static class InputControls
{
    public static InputControl VirtualKey(ushort key) => key switch
    {
        32 => InputControl.Space, 13 => InputControl.Enter, 27 => InputControl.Escape,
        > 0 and < 255 => (InputControl)(256 + key),
        _ => throw new GameInputException("input.actionInvalid")
    };
    public static bool IsValid(InputControl control) => Enum.IsDefined(control) || (int)control is > 256 and < 511;
    public static ushort? VirtualKeyOf(InputControl control) => control switch
    {
        InputControl.Space => 32, InputControl.Enter => 13, InputControl.Escape => 27,
        _ when (int)control is > 256 and < 511 => (ushort)((int)control - 256), _ => null
    };
}
public enum InputActionKind { Press, Release, MovePointer, MovePointerRelative, Wheel }
public enum StopKeyBehavior { PressOnce, ToggleOnlyWhenPlaying }
public sealed record PhysicalScreenPoint(int X, int Y);
public sealed record GamePlaybackEvidence(string GameSessionId, bool IsPlaying,
    OpenEnum<ObservationQuality> Quality, DateTimeOffset ObservedAt);
public sealed record GameInputAction(string OwnerId, string LeaseId, UInt64String FencingToken,
    string GameSessionId, UInt64String BindingRevision, UInt64String WindowIdentity,
    InputActionKind Kind, InputControl? Control, PhysicalScreenPoint? Point = null,
    PhysicalScreenPoint? RelativeDelta = null, int? WheelDelta = null);
public sealed record GameStopRequest(GameInputAction Action, StopKeyBehavior Behavior,
    GamePlaybackEvidence? Evidence, TimeSpan MaximumEvidenceAge);
public sealed record EmergencyStopResult(int RevokedLeases, int ReleasedControls);

public interface IGameInputDevice
{
    UInt64String? ForegroundWindowIdentity { get; }
    void Press(InputControl control);
    void Release(InputControl control);
    void MovePointer(PhysicalScreenPoint point);
    void MovePointerRelative(PhysicalScreenPoint delta) => throw new NotSupportedException("设备未实现相对位移。");
    void Wheel(int delta) => throw new NotSupportedException("设备未实现滚轮。");
    void ReleaseAll();
}

public sealed class GameInputException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class GameInputBroker(ResourceLeaseCoordinator leases, GameSessionBindingTracker bindings,
    IGameInputDevice device, TimeProvider? timeProvider = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, HashSet<InputControl>> heldByLease = new(StringComparer.Ordinal);
    private ulong emergencyStops;
    /// <summary>当前按下未释放的控件数与本进程紧急停止累计次数。</summary>
    public int HeldControls { get { lock (gate) return heldByLease.Values.Sum(value => value.Count); } }
    public ulong EmergencyStops { get { lock (gate) return emergencyStops; } }

    public void Execute(GameInputAction action)
    {
        ValidateActionShape(action);
        lock (gate)
        {
            ValidateBindingForAction(action);
            if (!leases.Validate(action.LeaseId, "foreground-input", action.FencingToken,
                    action.GameSessionId, action.BindingRevision, action.OwnerId))
            {
                ReleaseLeaseInputs(action.LeaseId);
                throw new GameInputException("input.leaseInvalid");
            }
            ValidateForeground(action.WindowIdentity);
            ValidatePointerBounds(action);
            try { ExecuteDeviceAction(action); }
            catch (GameInputException) { throw; }
            catch
            {
                StopLocked();
                throw new GameInputException("input.deviceFailure");
            }
            ValidateBindingForAction(action);
            ValidateForeground(action.WindowIdentity);
        }
    }

    public void DispatchStop(GameStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Action is null || !Enum.IsDefined(request.Behavior) ||
            request.Action.Kind != InputActionKind.Press ||
            request.Action.Control is not (InputControl.Space or InputControl.Enter or InputControl.Escape) ||
            request.Action.Point is not null || request.MaximumEvidenceAge <= TimeSpan.Zero || request.MaximumEvidenceAge > TimeSpan.FromMinutes(1))
            throw new GameInputException("input.stopRequestInvalid");
        ValidateActionShape(request.Action);

        lock (gate)
        {
            ValidateBindingForAction(request.Action);
            if (!leases.Validate(request.Action.LeaseId, "foreground-input", request.Action.FencingToken,
                    request.Action.GameSessionId, request.Action.BindingRevision, request.Action.OwnerId))
            {
                ReleaseLeaseInputs(request.Action.LeaseId);
                throw new GameInputException("input.leaseInvalid");
            }
            ValidateForeground(request.Action.WindowIdentity);
            if (request.Behavior == StopKeyBehavior.ToggleOnlyWhenPlaying)
            {
                var evidence = request.Evidence;
                var age = evidence is null ? TimeSpan.MaxValue : clock.GetUtcNow() - evidence.ObservedAt;
                if (evidence is null || !evidence.IsPlaying ||
                    evidence.Quality.KnownValue != ObservationQuality.Confirmed ||
                    !string.Equals(evidence.GameSessionId, request.Action.GameSessionId, StringComparison.Ordinal) ||
                    evidence.ObservedAt.Offset != TimeSpan.Zero || age < TimeSpan.Zero || age > request.MaximumEvidenceAge)
                    throw new GameInputException("input.stopEvidenceRequired");
            }
            var control = request.Action.Control!.Value;
            try { device.Press(control); }
            catch
            {
                StopLocked();
                throw new GameInputException("input.deviceFailure");
            }
            try { ValidateBinding(request.Action); }
            catch (GameInputException)
            {
                StopLocked();
                throw;
            }
            ValidateForeground(request.Action.WindowIdentity);
            try { device.Release(control); }
            catch
            {
                StopLocked();
                throw new GameInputException("input.deviceFailure");
            }
            ValidateForeground(request.Action.WindowIdentity);
        }
    }

    public EmergencyStopResult EmergencyStop()
    {
        lock (gate) { emergencyStops++; return StopLocked(); }
    }

    public void ReleaseRun(string leaseId)
    {
        lock (gate)
        {
            try { ReleaseLeaseInputs(leaseId); }
            finally { leases.Release(leaseId); }
        }
    }

    public void ReleaseHeldInputs(string leaseId)
    {
        lock (gate) ReleaseLeaseInputs(leaseId);
    }

    public void ValidateAccess(GameInputAction action)
    {
        ValidateActionShape(action);
        lock (gate)
        {
            ValidateBindingForAction(action);
            if (!leases.Validate(action.LeaseId, "foreground-input", action.FencingToken,
                action.GameSessionId, action.BindingRevision, action.OwnerId))
            {
                ReleaseLeaseInputs(action.LeaseId);
                throw new GameInputException("input.leaseInvalid");
            }
            ValidateForeground(action.WindowIdentity);
        }
    }

    private EmergencyStopResult StopLocked()
    {
        var released = heldByLease.Values.Sum(controls => controls.Count);
        var revoked = leases.RevokeResource("foreground-input");
        try { device.ReleaseAll(); }
        finally { heldByLease.Clear(); }
        return new(revoked, released);
    }

    private void ValidateBinding(GameInputAction action)
    {
        var current = bindings.Current;
        if (current is null || !current.CanAcceptInput || current.Session.Id != action.GameSessionId ||
            current.Session.BindingRevision != action.BindingRevision ||
            current.Session.WindowIdentity != action.WindowIdentity)
            throw new GameInputException("input.bindingChanged");
    }

    private void ValidateBindingForAction(GameInputAction action)
    {
        try { ValidateBinding(action); }
        catch (GameInputException)
        {
            ReleaseLeaseInputs(action.LeaseId);
            leases.Release(action.LeaseId);
            throw;
        }
    }

    private void ValidateForeground(UInt64String expectedWindow)
    {
        if (device.ForegroundWindowIdentity != expectedWindow)
        {
            leases.RevokeResource("foreground-input");
            try { device.ReleaseAll(); }
            finally { heldByLease.Clear(); }
            throw new GameInputException("input.focusChanged");
        }
    }

    private void ReleaseLeaseInputs(string leaseId)
    {
        if (!heldByLease.Remove(leaseId, out var controls)) return;
        foreach (var control in controls) device.Release(control);
    }

    private void ValidatePointerBounds(GameInputAction action)
    {
        if (action.Kind is not (InputActionKind.MovePointer or InputActionKind.MovePointerRelative)) return;
        var window = bindings.Current!;
        if (action.Point!.X < window.Bounds.X || action.Point.Y < window.Bounds.Y ||
            (long)action.Point.X >= (long)window.Bounds.X + window.Bounds.Width ||
            (long)action.Point.Y >= (long)window.Bounds.Y + window.Bounds.Height)
            throw new GameInputException("input.pointOutsideWindow");
    }

    private void ExecuteDeviceAction(GameInputAction action)
    {
        switch (action.Kind)
        {
            case InputActionKind.Press:
                var control = action.Control!.Value;
                if (!heldByLease.TryGetValue(action.LeaseId, out var controls)) heldByLease[action.LeaseId] = controls = [];
                if (controls.Add(control)) device.Press(control);
                break;
            case InputActionKind.Release:
                if (heldByLease.TryGetValue(action.LeaseId, out var heldControls) && heldControls.Remove(action.Control!.Value))
                {
                    device.Release(action.Control.Value);
                    if (heldControls.Count == 0) heldByLease.Remove(action.LeaseId);
                }
                break;
            case InputActionKind.MovePointer:
                device.MovePointer(action.Point!);
                break;
            case InputActionKind.MovePointerRelative:
                device.MovePointerRelative(action.RelativeDelta!);
                break;
            case InputActionKind.Wheel:
                device.Wheel(action.WheelDelta!.Value);
                break;
            default:
                throw new GameInputException("input.actionUnsupported");
        }
    }

    private void ValidateActionShape(GameInputAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrWhiteSpace(action.OwnerId) || string.IsNullOrWhiteSpace(action.LeaseId) ||
            string.IsNullOrWhiteSpace(action.GameSessionId) || action.WindowIdentity.Value == 0 ||
            !Enum.IsDefined(action.Kind))
            throw new GameInputException("input.actionInvalid");
        if (action.Kind is InputActionKind.MovePointer or InputActionKind.MovePointerRelative)
        {
            if (action.Control is not null || action.Point is null || action.WheelDelta is not null ||
                (action.Kind == InputActionKind.MovePointer && action.RelativeDelta is not null) ||
                (action.Kind == InputActionKind.MovePointerRelative && (action.RelativeDelta is null ||
                    Math.Abs((long)action.RelativeDelta.X) > 65536 || Math.Abs((long)action.RelativeDelta.Y) > 65536)))
                throw new GameInputException("input.actionInvalid");
        }
        else if (action.Kind == InputActionKind.Wheel)
        {
            if (action.Control is not null || action.Point is not null || action.RelativeDelta is not null ||
                action.WheelDelta is null or 0 or > 120 or < -120)
                throw new GameInputException("input.actionInvalid");
        }
        else if (action.Kind is not (InputActionKind.Press or InputActionKind.Release) ||
                 action.Control is null || !InputControls.IsValid(action.Control.Value) || action.Point is not null ||
                 action.RelativeDelta is not null || action.WheelDelta is not null)
            throw new GameInputException("input.actionInvalid");
    }

}
