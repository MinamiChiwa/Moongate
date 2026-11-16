using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record PointerGestureTiming(double BeforeDown, double AfterDown, double PointInterval,
    double BeforeUp, double AfterWheel);
public sealed record PointerGesture(string Kind, IReadOnlyList<PhysicalScreenPoint> Points,
    bool Right, bool Absolute, int WheelSteps, PointerGestureTiming Timing);
public sealed record GameCapture(int Width, int Height, byte[] Rgb);

public interface IGameCaptureSource
{
    ValueTask<GameCapture> CaptureAsync(GameWindowBinding binding, CancellationToken token);
}

/// <summary>运行固定绑定及租约；客户区坐标只在宿主转换为物理屏幕坐标。</summary>
public sealed class GameInteractionSession : IDisposable
{
    private readonly GameInputBroker broker;
    private readonly GameWindowBinding binding;
    private readonly GameInputAction identity;
    private readonly IGameCaptureSource capture;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stopped = new();
    private readonly object gate = new();
    private readonly long started;
    private TimeSpan maximumDuration;
    private TimeSpan actionDeadline = TimeSpan.MaxValue;
    private int busy;
    private bool closed;

    public GameInteractionSession(GameInputBroker broker, ResourceLeaseCoordinator leases,
        GameWindowBinding binding, ResourceLeaseSnapshot lease, IGameCaptureSource capture,
        TimeSpan maximumDuration, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(capture);
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromMinutes(5) ||
            lease.GameSessionId != binding.Session.Id || lease.BindingRevision != binding.Session.BindingRevision ||
            !leases.Validate(lease.Id, "activity-control:" + binding.Session.Id, lease.FencingToken,
                binding.Session.Id, binding.Session.BindingRevision, lease.OwnerId))
            throw new GameInputException("input.activityLeaseRequired");
        this.broker = broker; this.binding = binding; this.capture = capture;
        this.clock = clock ?? TimeProvider.System;
        this.maximumDuration = maximumDuration;
        started = this.clock.GetTimestamp();
        identity = new(lease.OwnerId, lease.Id, lease.FencingToken, lease.GameSessionId,
            lease.BindingRevision, binding.Session.WindowIdentity, InputActionKind.Release, InputControl.MouseLeft);
        broker.ValidateAccess(identity);
    }

    public GameWindowBinding Binding => binding;
    public UInt64String FencingToken => identity.FencingToken;

    public void LimitRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero || remaining > TimeSpan.FromMinutes(5)) throw new GameInputException("input.deadlineInvalid");
        lock (gate)
        {
            var proposed = clock.GetElapsedTime(started) + remaining;
            if (proposed < maximumDuration) maximumDuration = proposed;
        }
    }

    public void Check()
    {
        lock (gate)
        {
            if (closed) throw new GameInputException("input.runStopped");
            if (clock.GetElapsedTime(started) >= (maximumDuration < actionDeadline ? maximumDuration : actionDeadline))
            {
                Close();
                throw new GameInputException("input.deadlineExceeded");
            }
            broker.ValidateAccess(identity);
        }
    }

    public async ValueTask<GameCapture> CaptureAsync(CancellationToken token = default)
    {
        Enter();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopped.Token);
            Check();
            var frame = await capture.CaptureAsync(binding, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            Check();
            if (frame.Width != binding.Bounds.Width || frame.Height != binding.Bounds.Height ||
                frame.Width > 16384 || frame.Height > 16384 ||
                (long)frame.Width * frame.Height * 3 != frame.Rgb.LongLength || frame.Rgb.LongLength > 64 * 1024 * 1024)
                throw new GameInputException("capture.frameInvalid");
            return frame;
        }
        catch { Close(); throw; }
        finally { Volatile.Write(ref busy, 0); }
    }

    public async Task<bool> ExecuteAsync(PointerGesture gesture, CancellationToken token = default, double? remainingSeconds = null)
    {
        // 先复制并完整校验描述，拒绝半途发现越界后留下部分输入。
        ArgumentNullException.ThrowIfNull(gesture);
        var points = gesture.Points?.ToArray() ?? throw new GameInputException("input.gestureInvalid");
        Validate(gesture, points);
        if (remainingSeconds is { } seconds && (!double.IsFinite(seconds) || seconds < 0 || seconds > 300))
            throw new GameInputException("input.deadlineInvalid");
        Enter();
        lock (gate) actionDeadline = remainingSeconds is { } limit ? clock.GetElapsedTime(started) + TimeSpan.FromSeconds(limit) : TimeSpan.MaxValue;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopped.Token);
        using var registration = token.Register(Close);
        try
        {
            linked.Token.ThrowIfCancellationRequested(); Check();
            var effect = gesture.Kind == "wheel" ? gesture.WheelSteps != 0 : points.Distinct().Skip(1).Any();
            if (!effect) return false;
            Execute(InputActionKind.MovePointer, point: Screen(points[0]));
            var timing = gesture.Timing;
            if (gesture.Kind == "wheel")
            {
                for (var i = 0; i < Math.Abs(gesture.WheelSteps); i++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    Execute(InputActionKind.Wheel, wheel: Math.Sign(gesture.WheelSteps) * 120);
                    await Wait(timing.PointInterval, linked.Token).ConfigureAwait(false);
                }
                await Wait(timing.AfterWheel, linked.Token).ConfigureAwait(false);
                return true;
            }
            var control = gesture.Right ? InputControl.MouseRight : InputControl.MouseLeft;
            await Wait(timing.BeforeDown, linked.Token).ConfigureAwait(false);
            Execute(InputActionKind.Press, control);
            await Wait(timing.AfterDown, linked.Token).ConfigureAwait(false);
            for (var i = 1; i < points.Length; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var delta = new PhysicalScreenPoint(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y);
                if (delta.X != 0 || delta.Y != 0)
                    Execute(gesture.Absolute ? InputActionKind.MovePointer : InputActionKind.MovePointerRelative,
                        point: Screen(points[i]), relative: gesture.Absolute ? null : delta);
                await Wait(timing.PointInterval, linked.Token).ConfigureAwait(false);
            }
            await Wait(timing.BeforeUp, linked.Token).ConfigureAwait(false);
            Execute(InputActionKind.Release, control);
            return true;
        }
        catch { Close(); throw; }
        finally { lock (gate) actionDeadline = TimeSpan.MaxValue; Volatile.Write(ref busy, 0); }
    }

    private async Task Wait(double seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Check();
        TimeSpan remaining;
        lock (gate) remaining = (maximumDuration < actionDeadline ? maximumDuration : actionDeadline) - clock.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            Close();
            throw new GameInputException("input.deadlineExceeded");
        }
        var duration = TimeSpan.FromSeconds(seconds);
        await Task.Delay(duration < remaining ? duration : remaining, clock, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); Check();
    }

    private void Execute(InputActionKind kind, InputControl? control = null, PhysicalScreenPoint? point = null,
        PhysicalScreenPoint? relative = null, int? wheel = null)
    {
        lock (gate)
        {
            Check();
            broker.Execute(identity with { Kind = kind, Control = control, Point = point, RelativeDelta = relative, WheelDelta = wheel });
        }
    }

    private PhysicalScreenPoint Screen(PhysicalScreenPoint point) => new(
        checked(binding.Bounds.X + point.X), checked(binding.Bounds.Y + point.Y));

    public void MovePointer(PhysicalScreenPoint point)
    {
        Enter();
        try { Execute(InputActionKind.MovePointer, point: Screen(point)); }
        catch { Close(); throw; }
        finally { Volatile.Write(ref busy, 0); }
    }

    public void Wheel(int delta)
    {
        Enter();
        try { Execute(InputActionKind.Wheel, wheel: delta); }
        catch { Close(); throw; }
        finally { Volatile.Write(ref busy, 0); }
    }

    public void ReleaseHeldInputs() => broker.ReleaseHeldInputs(identity.LeaseId);

    private void Validate(PointerGesture gesture, PhysicalScreenPoint[] points)
    {
        var t = gesture.Timing;
        if (gesture.Kind is not ("drag" or "rotate" or "path" or "wheel") || points.Length is < 1 or > 512 || t is null ||
            new[] { t.BeforeDown, t.AfterDown, t.PointInterval, t.BeforeUp, t.AfterWheel }.Any(v => !double.IsFinite(v) || v < 0 || v > 1) ||
            Math.Abs((long)gesture.WheelSteps) > 2048 || (gesture.Kind != "wheel" && gesture.WheelSteps != 0) ||
            points.Any(p => p is null || p.X < 0 || p.Y < 0 || p.X >= binding.Bounds.Width || p.Y >= binding.Bounds.Height))
            throw new GameInputException("input.gestureInvalid");
    }

    private void Enter()
    {
        lock (gate)
        {
            if (closed) throw new GameInputException("input.runStopped");
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new GameInputException("input.runBusy");
        }
    }

    private void Close()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            try { stopped.Cancel(); }
            finally { broker.ReleaseRun(identity.LeaseId); }
        }
    }

    public void Dispose() => Close();
}
