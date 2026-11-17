using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

/// <summary>
/// 后台输入通道：客户区到屏幕的坐标换算（含负坐标的显示器与抖动夹紧）、租约与取消释放、紧急停止中途撤销、
/// 条件不满足时的原因码。设备是记录调用的替身，不向真实窗口发送输入。
/// </summary>
public sealed class GameBackgroundInputBrokerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeDevice : IGameBackgroundInputDevice
    {
        public string? Reason;
        public Action? DuringGesture;
        public List<PhysicalScreenPoint[]> Frames { get; } = [];
        public List<(ushort Key, bool Down)> Keys { get; } = [];
        public int ReleaseAllCalls;
        public string? Unavailable(GameWindowBinding binding) => Reason;
        public void Gesture(UInt64String window, IReadOnlyList<PhysicalScreenPoint[]> frames, int hold, int release, Action checkpoint)
        {
            foreach (var frame in frames)
            {
                DuringGesture?.Invoke();
                checkpoint();
                Frames.Add(frame);
            }
        }
        public void Key(UInt64String window, ushort key, bool down) => Keys.Add((key, down));
        public void ReleaseAll() => ReleaseAllCalls++;
    }

    private static (GameSessionBindingTracker Tracker, ResourceLeaseCoordinator Leases, FakeDevice Device, GameBackgroundInputBroker Broker,
        ResourceLeaseSnapshot Lease, GameWindowBinding Binding) Create(PhysicalWindowRect? bounds = null, bool minimized = false)
    {
        var tracker = new GameSessionBindingTracker();
        var binding = tracker.Update(new("TW", 100, Now, new UInt64String(500), "build-1", bounds ?? new PhysicalWindowRect(-1920, -200, 800, 600), 144, 144, minimized));
        var leases = new ResourceLeaseCoordinator(new FixedClock(Now));
        var lease = leases.Acquire(new("extension:test.input", "op-1", binding.Session.Id, binding.Session.BindingRevision,
            [GameBackgroundInputBroker.Resource, "activity-control:" + binding.Session.Id], TimeSpan.FromSeconds(30)));
        var device = new FakeDevice();
        return (tracker, leases, device, new GameBackgroundInputBroker(leases, tracker, device, new FixedClock(Now), max => max - 1), lease, binding);
    }

    private static GameBackgroundRequest Request(ResourceLeaseSnapshot lease, GameWindowBinding binding, int jitter = 0, params GameBackgroundAction[] actions) =>
        new(lease.OwnerId, lease.Id, lease.FencingToken, binding.Session.Id, binding.Session.BindingRevision, binding.Session.WindowIdentity,
            actions, IntervalMilliseconds: 0, JitterPixels: jitter);

    [Fact]
    public void ClientPointsMapToPhysicalScreenOnNegativeMonitorsAndJitterStaysInside()
    {
        var (_, _, device, broker, lease, binding) = Create();
        broker.Execute(Request(lease, binding, 0,
            new GameBackgroundAction(BackgroundActionKind.Tap, [new(10, 20)]),
            new GameBackgroundAction(BackgroundActionKind.Drag, [new(0, 0), new(799, 599)], Steps: 2)));

        Assert.Equal(new PhysicalScreenPoint(-1910, -180), Assert.Single(device.Frames[0]));
        // 拖动 2 步 → 4 段（5 帧），终点为客户区右下角。
        Assert.Equal(1 + 5, device.Frames.Count);
        Assert.Equal(new PhysicalScreenPoint(-1121, 399), device.Frames[^1][0]);

        // 抖动后仍在客户区内：随机数替身取最大值，即向正方向偏移满额，贴边的点被夹紧。
        Assert.Equal("input.pointOutsideWindow", Assert.Throws<GameInputException>(() =>
            GameBackgroundInputBroker.ToScreen(new(800, 10), binding.Bounds)).Code);
        broker.Execute(Request(lease, binding, 4, new GameBackgroundAction(BackgroundActionKind.Tap, [new(799, 599)])));
        Assert.Equal(new PhysicalScreenPoint(-1121, 399), device.Frames[^1][0]);
    }

    [Fact]
    public void EmergencyStopDuringAGestureAbortsTheNextFrameAndReleases()
    {
        var (_, leases, device, broker, lease, binding) = Create();
        var frames = 0;
        device.DuringGesture = () => { if (++frames == 3) Assert.Equal(1, broker.EmergencyStop().RevokedLeases); };

        var error = Assert.Throws<GameInputException>(() =>
            broker.Execute(Request(lease, binding, 0, new GameBackgroundAction(BackgroundActionKind.Swipe, [new(100, 100), new(100, 400)]))));

        Assert.Equal("input.leaseInvalid", error.Code);
        Assert.Equal(2, device.Frames.Count);
        Assert.True(device.ReleaseAllCalls >= 1);
        Assert.Empty(leases.Snapshot());
    }

    [Fact]
    public void ReleasedOrCancelledRunsDeliverNothingFurther()
    {
        var (_, leases, device, broker, lease, binding) = Create();
        broker.Execute(Request(lease, binding, 0, new GameBackgroundAction(BackgroundActionKind.Key, [], 0, VirtualKey: 0x41)));
        Assert.Equal([((ushort)0x41, true), ((ushort)0x41, false)], device.Keys);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => broker.Execute(Request(lease, binding, 0, new GameBackgroundAction(BackgroundActionKind.Tap, [new(1, 1)])), cancelled.Token));

        broker.ReleaseRun(lease.Id);
        Assert.Empty(leases.Snapshot());
        Assert.Equal("input.leaseInvalid", Assert.Throws<GameInputException>(() =>
            broker.Execute(Request(lease, binding, 0, new GameBackgroundAction(BackgroundActionKind.Tap, [new(1, 1)])))).Code);
        Assert.Empty(device.Frames);
    }

    [Fact]
    public void UnmetConditionsReturnReasonCodesWithoutInput()
    {
        var (_, _, device, broker, lease, binding) = Create(minimized: true);
        Assert.Equal("input.windowMinimized", broker.Availability());
        Assert.Equal("input.windowMinimized", Assert.Throws<GameInputException>(() =>
            broker.Execute(Request(lease, binding, 0, new GameBackgroundAction(BackgroundActionKind.Tap, [new(1, 1)])))).Code);

        var (tracker, _, device2, broker2, lease2, binding2) = Create();
        device2.Reason = "input.backgroundUnsupported";
        Assert.Equal("input.backgroundUnsupported", broker2.Availability());
        Assert.Equal("input.backgroundUnsupported", Assert.Throws<GameInputException>(() =>
            broker2.Execute(Request(lease2, binding2, 0, new GameBackgroundAction(BackgroundActionKind.Tap, [new(1, 1)])))).Code);
        tracker.Clear();
        Assert.Equal("input.bindingUnavailable", broker2.Availability());
        Assert.Equal("input.actionInvalid", Assert.Throws<GameInputException>(() =>
            broker2.Execute(Request(lease2, binding2, 0, new GameBackgroundAction(BackgroundActionKind.Key, [], 0, VirtualKey: 0x5B)))).Code);
        Assert.Empty(device.Frames);
        Assert.Empty(device2.Frames);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
