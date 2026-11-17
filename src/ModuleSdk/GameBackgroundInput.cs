using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

/// <summary>后台输入的一个动作：坐标是绑定客户区内的物理像素（每显示器 DPI 感知 v2），与前台输入的坐标语义一致。</summary>
public enum BackgroundActionKind { Tap, LongPress, Drag, Swipe, Key }

/// <param name="Points">Tap、LongPress 为一个点；Drag、Swipe 为起点与终点。</param>
/// <param name="HoldMilliseconds">LongPress 的按住时长（100–5000）；Key 为按下到松开的间隔（0–2000）。</param>
/// <param name="VirtualKey">Key 的虚拟键码，只接受 <see cref="GameBackgroundInputBroker.SupportedKeys"/>。</param>
public sealed record GameBackgroundAction(BackgroundActionKind Kind, IReadOnlyList<PhysicalScreenPoint> Points,
    int HoldMilliseconds = 0, int Steps = 6, ushort VirtualKey = 0);

/// <summary>
/// 一批后台动作：<see cref="IntervalMilliseconds"/> 是相邻动作的间隔，<see cref="JitterMilliseconds"/> 与 <see cref="JitterPixels"/>
/// 是间隔与坐标的随机抖动上限（抖动后的坐标仍在客户区内）。每批最多 32 个动作，计划总时长不超过 15 秒。
/// </summary>
public sealed record GameBackgroundRequest(string OwnerId, string LeaseId, UInt64String FencingToken, string GameSessionId,
    UInt64String BindingRevision, UInt64String WindowIdentity, IReadOnlyList<GameBackgroundAction> Actions,
    int IntervalMilliseconds = 120, int JitterMilliseconds = 0, int JitterPixels = 0);

/// <summary>
/// 后台输入设备：不前置、不激活游戏窗口，也不移动用户的鼠标。坐标为物理屏幕像素。
/// <paramref name="checkpoint"/> 在每一帧投递前调用，抛出异常即中止手势并释放已按下的触点。
/// </summary>
public interface IGameBackgroundInputDevice
{
    /// <summary>当前窗口与系统条件是否支持后台输入；不支持时返回原因码（不回退为抢前台）。</summary>
    string? Unavailable(GameWindowBinding binding);
    void Gesture(UInt64String window, IReadOnlyList<PhysicalScreenPoint[]> frames, int holdMilliseconds, int releaseDelayMilliseconds, Action checkpoint);
    void Key(UInt64String window, ushort virtualKey, bool down);
    /// <summary>释放本设备按下的全部触点与按键；紧急停止与撤销租约时调用。</summary>
    void ReleaseAll();
}

/// <summary>没有后台输入能力的平台或模拟宿主。</summary>
public sealed class UnavailableBackgroundInputDevice(string reason = "input.backgroundUnavailable") : IGameBackgroundInputDevice
{
    public string? Unavailable(GameWindowBinding binding) => reason;
    public void Gesture(UInt64String window, IReadOnlyList<PhysicalScreenPoint[]> frames, int holdMilliseconds, int releaseDelayMilliseconds, Action checkpoint) =>
        throw new GameInputException(reason);
    public void Key(UInt64String window, ushort virtualKey, bool down) => throw new GameInputException(reason);
    public void ReleaseAll() { }
}

/// <summary>
/// 后台输入通道：与前台输入（<see cref="GameInputBroker"/>）并列，接入同一套租约、绑定核对与紧急停止。
/// 资源键为 <c>background-input</c>；调用方同时持有 <c>activity-control:&lt;游戏会话&gt;</c>，与染色、合奏等活动互斥。
/// 每个动作、每一帧投递前核对租约代次、游戏会话、绑定修订与窗口身份，任何一项失效即中止并释放已按下的触点与按键；
/// 坐标从客户区换算为屏幕物理像素时按绑定的客户区原点（可为负，左侧或上方的显示器）。
/// </summary>
public sealed class GameBackgroundInputBroker(ResourceLeaseCoordinator leases, GameSessionBindingTracker bindings,
    IGameBackgroundInputDevice device, TimeProvider? timeProvider = null, Func<int, int>? random = null)
{
    public const string Resource = "background-input";
    public const int MaximumActions = 32;
    public static readonly TimeSpan MaximumPlan = TimeSpan.FromSeconds(15);
    /// <summary>后台按键接受的虚拟键：字母、数字、F1–F12、回车、Esc、空格、退格、Tab 与方向键。</summary>
    public static bool SupportedKeys(ushort key) => key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7B or 0x0D or 0x1B or 0x20 or 0x08 or 0x09 or >= 0x25 and <= 0x28;

    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<int, int> next = random ?? Random.Shared.Next;
    private readonly Dictionary<string, HashSet<ushort>> heldKeys = new(StringComparer.Ordinal);
    private ulong emergencyStops;
    public ulong EmergencyStops { get { lock (gate) return emergencyStops; } }

    /// <summary>当前绑定是否可以后台输入；不可用时给出原因码。</summary>
    public string? Availability()
    {
        var binding = bindings.Current;
        if (binding is null) return "input.bindingUnavailable";
        if (binding.IsMinimized) return "input.windowMinimized";
        return device.Unavailable(binding);
    }

    /// <summary>
    /// 执行一批动作。输入执行期间持有内部锁，紧急停止会等待当前一帧结束后撤销租约，下一帧核对即失败并释放。
    /// 返回实际投递的动作数；中途失效时抛出 <see cref="GameInputException"/>，已投递的动作不重放。
    /// </summary>
    public int Execute(GameBackgroundRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var delivered = 0;
        foreach (var action in request.Actions)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                var binding = Check(request);
                if (device.Unavailable(binding) is { } reason) throw new GameInputException(reason);
                void Checkpoint()
                {
                    ct.ThrowIfCancellationRequested();
                    Check(request);
                }
                try
                {
                    if (action.Kind == BackgroundActionKind.Key) Key(request, action, Checkpoint);
                    else device.Gesture(request.WindowIdentity, Frames(action, binding.Bounds, request.JitterPixels), Hold(action),
                        action.Kind is BackgroundActionKind.Drag or BackgroundActionKind.Swipe ? 160 : 0, Checkpoint);
                }
                catch (OperationCanceledException) { ReleaseLeaseKeys(request.LeaseId); throw; }
                catch (GameInputException) { ReleaseLeaseKeys(request.LeaseId); throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    ReleaseLeaseKeys(request.LeaseId);
                    throw new GameInputException("input.deviceFailure");
                }
            }
            delivered++;
            if (delivered < request.Actions.Count)
            {
                var delay = request.IntervalMilliseconds + (request.JitterMilliseconds > 0 ? next(request.JitterMilliseconds + 1) : 0);
                if (delay > 0) Task.Delay(TimeSpan.FromMilliseconds(delay), clock, ct).GetAwaiter().GetResult();
            }
        }
        return delivered;
    }

    /// <summary>紧急停止：撤销全部后台输入租约，释放按下的触点与按键。</summary>
    public EmergencyStopResult EmergencyStop()
    {
        var revoked = leases.RevokeResource(Resource);
        lock (gate)
        {
            emergencyStops++;
            var released = heldKeys.Values.Sum(x => x.Count);
            try { device.ReleaseAll(); }
            finally { heldKeys.Clear(); }
            return new(revoked, released);
        }
    }

    /// <summary>交还一个租约：先释放它按下的按键，再释放租约。</summary>
    public void ReleaseRun(string leaseId)
    {
        lock (gate)
        {
            try { ReleaseLeaseKeys(leaseId); }
            finally { leases.Release(leaseId); }
        }
    }

    /// <summary>把客户区坐标换算为屏幕物理像素；越出客户区为 <c>input.pointOutsideWindow</c>。</summary>
    public static PhysicalScreenPoint ToScreen(PhysicalScreenPoint client, PhysicalWindowRect bounds)
    {
        if (client.X < 0 || client.Y < 0 || client.X >= bounds.Width || client.Y >= bounds.Height)
            throw new GameInputException("input.pointOutsideWindow");
        return new(checked(bounds.X + client.X), checked(bounds.Y + client.Y));
    }

    private GameWindowBinding Check(GameBackgroundRequest request)
    {
        var current = bindings.Current;
        if (!leases.Validate(request.LeaseId, Resource, request.FencingToken, request.GameSessionId, request.BindingRevision, request.OwnerId))
            throw new GameInputException("input.leaseInvalid");
        if (current is null || current.Session.Id != request.GameSessionId || current.Session.BindingRevision != request.BindingRevision ||
            current.Session.WindowIdentity != request.WindowIdentity)
            throw new GameInputException("input.bindingChanged");
        if (current.IsMinimized) throw new GameInputException("input.windowMinimized");
        return current;
    }

    private void Key(GameBackgroundRequest request, GameBackgroundAction action, Action checkpoint)
    {
        checkpoint();
        if (!heldKeys.TryGetValue(request.LeaseId, out var keys)) heldKeys[request.LeaseId] = keys = [];
        keys.Add(action.VirtualKey);
        device.Key(request.WindowIdentity, action.VirtualKey, true);
        if (action.HoldMilliseconds > 0) Thread.Sleep(action.HoldMilliseconds);
        checkpoint();
        device.Key(request.WindowIdentity, action.VirtualKey, false);
        keys.Remove(action.VirtualKey);
    }

    private void ReleaseLeaseKeys(string leaseId)
    {
        if (!heldKeys.Remove(leaseId, out var keys) || keys.Count == 0) return;
        try { device.ReleaseAll(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private static int Hold(GameBackgroundAction action) => action.Kind switch
    {
        BackgroundActionKind.Tap => 40,
        BackgroundActionKind.LongPress => action.HoldMilliseconds,
        _ => 0
    };

    /// <summary>
    /// 手势帧（屏幕坐标）：点按与长按为一帧；拖动按 MabiAgent 的缓出曲线（末段减速后停留再松开，避免惯性）
    /// 生成 max(4, min(24, 步数×2)) 段；滑动同拖动、固定 24 段。
    /// </summary>
    internal IReadOnlyList<PhysicalScreenPoint[]> Frames(GameBackgroundAction action, PhysicalWindowRect bounds, int jitter)
    {
        PhysicalScreenPoint Jitter(PhysicalScreenPoint point) => jitter <= 0 ? point : new(
            Math.Clamp(point.X + next(jitter * 2 + 1) - jitter, 0, bounds.Width - 1),
            Math.Clamp(point.Y + next(jitter * 2 + 1) - jitter, 0, bounds.Height - 1));
        var points = action.Points.Select(p => { ToScreen(p, bounds); return Jitter(p); }).ToArray();
        if (action.Kind is BackgroundActionKind.Tap or BackgroundActionKind.LongPress) return [[ToScreen(points[0], bounds)]];
        var (from, to) = (points[0], points[1]);
        var count = action.Kind == BackgroundActionKind.Swipe ? 24 : Math.Clamp(action.Steps * 2, 4, 24);
        return Enumerable.Range(0, count + 1).Select(i =>
        {
            var progress = 1 - Math.Pow(1 - i / (double)count, 3);
            return new[] { ToScreen(new((int)Math.Round(from.X + (to.X - from.X) * progress), (int)Math.Round(from.Y + (to.Y - from.Y) * progress)), bounds) };
        }).ToArray();
    }

    private static void Validate(GameBackgroundRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OwnerId) || string.IsNullOrWhiteSpace(request.LeaseId) || request.WindowIdentity.Value == 0 ||
            request.Actions.Count is 0 or > MaximumActions || request.IntervalMilliseconds is < 0 or > 2000 ||
            request.JitterMilliseconds is < 0 or > 500 || request.JitterPixels is < 0 or > 8)
            throw new GameInputException("input.actionInvalid");
        var planned = 0L;
        foreach (var action in request.Actions)
        {
            var valid = action.Kind switch
            {
                BackgroundActionKind.Tap => action.Points.Count == 1,
                BackgroundActionKind.LongPress => action.Points.Count == 1 && action.HoldMilliseconds is >= 100 and <= 5000,
                BackgroundActionKind.Drag => action.Points.Count == 2 && action.Steps is >= 1 and <= 12,
                BackgroundActionKind.Swipe => action.Points.Count == 2,
                BackgroundActionKind.Key => action.Points.Count == 0 && SupportedKeys(action.VirtualKey) && action.HoldMilliseconds is >= 0 and <= 2000,
                _ => false
            };
            if (!valid) throw new GameInputException("input.actionInvalid");
            planned += action.Kind switch
            {
                BackgroundActionKind.LongPress or BackgroundActionKind.Key => action.HoldMilliseconds,
                BackgroundActionKind.Drag or BackgroundActionKind.Swipe => 25 * 20 + 160,
                _ => 60
            } + request.IntervalMilliseconds + request.JitterMilliseconds;
        }
        if (planned > MaximumPlan.TotalMilliseconds) throw new GameInputException("input.planTooLong");
    }
}
