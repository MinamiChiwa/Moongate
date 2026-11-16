using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class GameInteractionTests
{
    private static readonly PointerGestureTiming Timing = new(.005, .005, .005, .005, .005);
    private sealed class Fixture : IDisposable, IGameInputDevice, IGameCaptureSource
    {
        public GameSessionBindingTracker Bindings { get; } = new();
        public ResourceLeaseCoordinator Leases { get; } = new();
        public GameWindowBinding Binding { get; }
        public ResourceLeaseSnapshot Lease { get; }
        public GameInputBroker Broker { get; }
        public GameInteractionSession Session { get; }
        public UInt64String? ForegroundWindowIdentity { get; set; } = new(500);
        public List<string> Trace { get; } = [];
        public Action? OnPress { get; set; }
        public Action? OnCapture { get; set; }
        public GameCapture? Frame { get; set; }

        public Fixture(TimeProvider? clock = null)
        {
            Binding = Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), "test",
                new(100, 200, 800, 600), 96, 96, false));
            Lease = Leases.Acquire(new("color", "op", Binding.Session.Id, Binding.Session.BindingRevision,
                ["foreground-input", "activity-control:" + Binding.Session.Id], TimeSpan.FromMinutes(2)));
            Broker = new(Leases, Bindings, this);
            Session = new(Broker, Leases, Binding, Lease, this, TimeSpan.FromMinutes(2), clock);
        }
        public void Press(InputControl control) { Trace.Add("down:" + control); OnPress?.Invoke(); }
        public void Release(InputControl control) => Trace.Add("up:" + control);
        public void MovePointer(PhysicalScreenPoint point) => Trace.Add($"absolute:{point.X},{point.Y}");
        public void MovePointerRelative(PhysicalScreenPoint delta) => Trace.Add($"relative:{delta.X},{delta.Y}");
        public void Wheel(int delta) => Trace.Add("wheel:" + delta);
        public void ReleaseAll() => Trace.Add("all-up");
        public ValueTask<GameCapture> CaptureAsync(GameWindowBinding binding, CancellationToken token)
        {
            OnCapture?.Invoke();
            return ValueTask.FromResult(Frame ?? new GameCapture(800, 600, new byte[800 * 600 * 3]));
        }
        public void Dispose() => Session.Dispose();
    }

    private sealed class ReplayClock : TimeProvider
    {
        private long timestamp;
        public List<TimeSpan> Waits { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Waits.Add(dueTime);
            return new Timer(value =>
            {
                Interlocked.Add(ref timestamp, dueTime.Ticks);
                callback(value);
            }, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    [Fact]
    public async Task HostReplaysOriginalPythonGesturesWithIdenticalInputAndWaits()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var start = new ProcessStartInfo("python")
        {
            WorkingDirectory = repository.FullName, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-B", "workers/color-engine/tests/test_game_proxy.py", "--replay-json" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await errors);
        using var document = JsonDocument.Parse(await output);
        Assert.Equal(8, document.RootElement.GetArrayLength());
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var clock = new ReplayClock();
            using var f = new Fixture(clock);
            var gesture = row.GetProperty("gesture").Deserialize<PointerGesture>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.True(await f.Session.ExecuteAsync(gesture));
            Assert.Equal(row.GetProperty("trace").EnumerateArray().Select(x => x.GetString()), f.Trace);
            Assert.Equal(row.GetProperty("waits").EnumerateArray().Select(x => TimeSpan.FromSeconds(x.GetDouble())), clock.Waits);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GestureKeepsHeldButtonAndOriginalAbsoluteOrRelativeCoordinates(bool absolute)
    {
        using var f = new Fixture();
        var gesture = new PointerGesture("path", [new(10, 20), new(10, 20), new(13, 18), new(15, 21)], true, absolute, 0, Timing);
        Assert.True(await f.Session.ExecuteAsync(gesture));
        Assert.Equal(new[] { "absolute:110,220", "down:MouseRight",
            absolute ? "absolute:113,218" : "relative:3,-2",
            absolute ? "absolute:115,221" : "relative:2,3", "up:MouseRight" }, f.Trace);
        Assert.True(f.Leases.Validate(f.Lease.Id, "foreground-input", f.Lease.FencingToken,
            f.Binding.Session.Id, f.Binding.Session.BindingRevision));
    }

    [Fact]
    public async Task WheelPreservesNotchesAndNoOpDoesNotMovePointer()
    {
        using var f = new Fixture();
        Assert.True(await f.Session.ExecuteAsync(new("wheel", [new(40, 50)], false, true, -3, Timing)));
        Assert.Equal(["absolute:140,250", "wheel:-120", "wheel:-120", "wheel:-120"], f.Trace);
        f.Trace.Clear();
        Assert.False(await f.Session.ExecuteAsync(new("path", [new(10, 10), new(10, 10)], false, false, 0, Timing)));
        Assert.Empty(f.Trace);
    }

    [Fact]
    public async Task CancelWhileHeldReleasesOnlyThisRunAndRejectsLateInput()
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        f.OnPress = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Session.ExecuteAsync(
            new("drag", [new(10, 10), new(20, 20)], false, true, 0, Timing), cancel.Token));
        Assert.Equal(["absolute:110,210", "down:MouseLeft", "up:MouseLeft"], f.Trace);
        var next = f.Leases.Acquire(new("music", "next", f.Binding.Session.Id, f.Binding.Session.BindingRevision,
            ["foreground-input"], TimeSpan.FromSeconds(10)));
        Assert.Equal("input.runStopped", Assert.Throws<GameInputException>(() => f.Session.MovePointer(new(1, 1))).Code);
        Assert.True(f.Leases.Validate(next.Id, "foreground-input", next.FencingToken,
            f.Binding.Session.Id, f.Binding.Session.BindingRevision));
    }

    [Fact]
    public async Task DeadlineDuringHeldWaitReleasesAndPreventsFurtherMovement()
    {
        using var f = new Fixture();
        var error = await Assert.ThrowsAsync<GameInputException>(() => f.Session.ExecuteAsync(
            new("drag", [new(10, 10), new(20, 20)], false, true, 0, new(0, .5, .01, 0, 0)),
            remainingSeconds: .08));
        Assert.Equal("input.deadlineExceeded", error.Code);
        Assert.Equal(["absolute:110,210", "down:MouseLeft", "up:MouseLeft"], f.Trace);
    }

    [Fact]
    public async Task ClosedRunRejectsLateInputWhilePreviousActionIsStillUnwinding()
    {
        using var f = new Fixture();
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnPress = () => pressed.TrySetResult();
        var running = f.Session.ExecuteAsync(new("drag", [new(10, 10), new(20, 20)], false, true, 0,
            new(0, 1, .01, 0, 0)));
        await pressed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Session.Dispose();
        Assert.Equal("input.runStopped", Assert.Throws<GameInputException>(() => f.Session.MovePointer(new(1, 1))).Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(["absolute:110,210", "down:MouseLeft", "up:MouseLeft"], f.Trace);
    }

    [Fact]
    public async Task InvalidDescriptorIsRejectedBeforeAnyInput()
    {
        using var f = new Fixture();
        foreach (var points in new PhysicalScreenPoint[][] { [], [new(-1, 0)], [new(1, 1), new(800, 0)] })
            Assert.Equal("input.gestureInvalid", (await Assert.ThrowsAsync<GameInputException>(() => f.Session.ExecuteAsync(
                new("path", points, false, true, 0, Timing)))).Code);
        Assert.Empty(f.Trace);
    }

    [Fact]
    public async Task NativeRetryWheelPreservesFullRouteAndRejectsExcessBeforeInput()
    {
        var clock = new ReplayClock();
        using var f = new Fixture(clock);
        Assert.True(await f.Session.ExecuteAsync(new("wheel", [new(100, 60)], false, true, -2048, Timing),
            remainingSeconds: 60));
        Assert.Equal(new[] { "absolute:200,260" }.Concat(Enumerable.Repeat("wheel:-120", 2048)), f.Trace);
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(Timing.PointInterval), 2048)
            .Append(TimeSpan.FromSeconds(Timing.AfterWheel)), clock.Waits);
        f.Trace.Clear();
        Assert.Equal("input.gestureInvalid", (await Assert.ThrowsAsync<GameInputException>(() => f.Session.ExecuteAsync(
            new("wheel", [new(100, 60)], false, true, -2049, Timing)))).Code);
        Assert.Empty(f.Trace);
    }

    [Fact]
    public async Task CaptureRejectsBindingChangeAndWrongRgbLength()
    {
        using (var f = new Fixture())
        {
            f.OnCapture = () => f.Bindings.Update(new("TW", 100, f.Binding.Session.ProcessStartTime,
                new(500), "test", new(101, 200, 800, 600), 96, 96, false));
            Assert.Equal("input.bindingChanged", (await Assert.ThrowsAsync<GameInputException>(async () => await f.Session.CaptureAsync())).Code);
        }
        using (var f = new Fixture())
        {
            f.Frame = new(800, 600, [1, 2, 3]);
            Assert.Equal("capture.frameInvalid", (await Assert.ThrowsAsync<GameInputException>(async () => await f.Session.CaptureAsync())).Code);
        }
    }
}
