using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class ColorGamePortTests : IDisposable, IGameInputDevice, IGameCaptureSource
{
    private readonly string repository;
    private readonly string area;
    private readonly string python;
    private readonly GameSessionBindingTracker bindings = new();
    private readonly ResourceLeaseCoordinator leases = new();
    private readonly GameWindowBinding binding;
    private readonly ResourceLeaseSnapshot lease;
    private readonly GameInteractionSession interaction;
    private readonly ColorGamePort port;
    private readonly ConcurrentQueue<WorkerFailure> failures = new();
    private readonly ConcurrentQueue<string> trace = new();
    private readonly TaskCompletionSource pressed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string run = Guid.NewGuid().ToString("N");
    private bool blockCapture;
    private int captures;

    public ColorGamePortTests()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MabiTool.slnx"))) current = current.Parent;
        repository = current?.FullName ?? throw new InvalidOperationException();
        area = Path.Combine(repository, "artifacts", "tmp", "game-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        var probeStart = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        probeStart.ArgumentList.Add("-c"); probeStart.ArgumentList.Add("import sys; print(sys.executable)");
        using var probe = Process.Start(probeStart)!;
        python = probe.StandardOutput.ReadToEnd().Trim();
        probe.WaitForExit();
        Assert.Equal(0, probe.ExitCode);
        binding = bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), "offline",
            new(100, 200, 200, 120), 96, 96, false));
        lease = leases.Acquire(new("color", "op", binding.Session.Id, binding.Session.BindingRevision,
            ["foreground-input", "activity-control:" + binding.Session.Id], TimeSpan.FromMinutes(2)));
        interaction = new(new(leases, bindings, this), leases, binding, lease, this, TimeSpan.FromMinutes(2));
        port = new(interaction, run, lease.FencingToken.ToString(), area);
    }

    private ColorWorkerOptions Options(string entry, params string[] arguments) => new(python,
        Path.Combine(repository, "workers", "color-engine", "tests"), area)
    {
        EntryModule = entry, Arguments = arguments,
        HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(2),
        CancellationGrace = TimeSpan.FromSeconds(2)
    };

    private WorkerRequest Request() => new("color.screenshot", JsonSerializer.SerializeToElement(new
    {
        profile = new { schema = 1, search_strategy = "screenshot", priority_order = new[] { 0, 1, 2 },
            regions = Enumerable.Range(0, 3).Select(_ => new { enabled = true, target = "#202020", alt = "", mode = "精准 HEX", tolerance = 8 }) }
    }), run, binding.Session.BindingRevision.ToString(), lease.FencingToken.ToString());

    private void Cleanup(WorkerFailure failure) { failures.Enqueue(failure); port.Dispose(); }

    private static async Task<JsonElement> WaitForEvent(WorkerCall call, string kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var frame = await call.Events.ReadAsync(timeout.Token);
            if (frame.Kind == "event" && frame.Payload.TryGetProperty("kind", out var name) && name.GetString() == kind)
                return frame.Payload.GetProperty("data").Clone();
        }
    }

    [Fact]
    public async Task ScopedSelectionRetryAndLateCommandsUseRealWorkerPipe()
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("interaction_peer"), Cleanup);
        var call = await worker.BeginAsync(Request());
        var ready = await WaitForEvent(call, "native_candidate_ready");
        var available = await WaitForEvent(call, "native_retry_available");
        var revision = available.GetProperty("interaction").GetProperty("revision").GetString()!;
        var wrong = await worker.InteractAsync(call, "select", revision, "expired", "choice");
        Assert.False(wrong.GetProperty("accepted").GetBoolean());
        Assert.Equal("color.candidateUnavailable", wrong.GetProperty("code").GetString());
        var selected = await worker.InteractAsync(call, "select", revision, "board-0", "choice");
        Assert.True(selected.GetProperty("accepted").GetBoolean());
        var measured = await WaitForEvent(call, "native_candidate_selected");
        Assert.True(measured.GetProperty("verified").GetBoolean());
        await WaitForEvent(call, "native_candidate_ready");
        available = await WaitForEvent(call, "native_retry_available");
        var stale = await worker.InteractAsync(call, "retry", revision);
        Assert.Equal("color.interactionConflict", stale.GetProperty("code").GetString());
        revision = available.GetProperty("interaction").GetProperty("revision").GetString()!;
        var retry = await worker.InteractAsync(call, "retry", revision);
        Assert.True(retry.GetProperty("accepted").GetBoolean());
        Assert.Null(retry.GetProperty("interaction").GetProperty("batchId").GetString());
        await WaitForEvent(call, "native_retry_reset");
        var next = await WaitForEvent(call, "native_candidate_ready");
        Assert.Equal("board-1", next.GetProperty("batch_id").GetString());
        Assert.Equal(ready.GetProperty("effective_deadline").GetDouble(), next.GetProperty("effective_deadline").GetDouble());
        available = await WaitForEvent(call, "native_retry_available");
        revision = available.GetProperty("interaction").GetProperty("revision").GetString()!;
        var old = await worker.InteractAsync(call, "select", revision, "board-0", "choice");
        Assert.Equal("color.candidateUnavailable", old.GetProperty("code").GetString());
        await worker.CancelAsync(call);
        Assert.Equal("cancelled", (await call.Completion).Kind);
        var ended = await Assert.ThrowsAsync<WorkerException>(() => worker.InteractAsync(call, "retry", revision));
        Assert.Equal("color.runInactive", ended.Code);
        var another = await worker.BeginAsync(Request());
        await WaitForEvent(another, "native_retry_available");
        await Assert.ThrowsAsync<WorkerException>(() => worker.InteractAsync(call, "retry", revision));
        await worker.CancelAsync(another);
        Assert.True(worker.IsAlive);
        Assert.Empty(failures);
        Assert.Empty(trace);
    }

    [Fact]
    public async Task OriginalScreenshotControllerWaitsForExplicitInteractionAndRemainsCancellable()
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("screenshot_peer", "2"), Cleanup);
        var request = Request();
        request = request with { Arguments = JsonSerializer.SerializeToElement(new
        {
            profile = request.Arguments.GetProperty("profile"), interactive = true
        }) };
        var call = await worker.BeginAsync(request, portHandler: port.HandleAsync);
        JsonElement available;
        do { available = await WaitForEvent(call, "native_retry_available"); }
        while (!available.TryGetProperty("waiting_for_retry", out var waiting) || !waiting.GetBoolean());
        Assert.False(call.Completion.IsCompleted);
        Assert.True(available.GetProperty("verified").GetBoolean());
        var result = await worker.InteractAsync(call, "select",
            available.GetProperty("interaction").GetProperty("revision").GetString()!, "expired", "choice");
        Assert.Equal("color.candidateUnavailable", result.GetProperty("code").GetString());
        await worker.CancelAsync(call);
        Assert.Equal("cancelled", (await call.Completion).Kind);
        Assert.Empty(trace);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task NativePermissionFailureKeepsScreenshotAvailableOnSameWorker()
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("native_peer", "passive"), Cleanup);
        var screenshot = Request();
        var profile = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            screenshot.Arguments.GetProperty("profile").GetRawText())!;
        profile["search_strategy"] = JsonSerializer.SerializeToElement("native");
        var native = screenshot with { Method = "color.native", Arguments = JsonSerializer.SerializeToElement(new { profile }) };
        var denied = await worker.BeginAsync(native, portHandler: port.HandleAsync);
        var events = Drain(denied);
        var result = await denied.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await events;
        Assert.Equal("result", result.Kind);
        Assert.Equal("permission.missing", result.Payload.GetProperty("unavailable_reason").GetString());
        Assert.Equal(0, result.Payload.GetProperty("actual_input_attempts").GetInt32());
        Assert.Equal(0, captures);
        Assert.Empty(trace);
        var next = await worker.BeginAsync(screenshot, portHandler: port.HandleAsync);
        var nextEvents = Drain(next);
        Assert.True((await next.Completion.WaitAsync(TimeSpan.FromSeconds(30))).Payload.GetProperty("accepted").GetBoolean());
        await nextEvents;
        Assert.True(worker.IsAlive);
        Assert.Empty(failures);
    }

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData("unsupported", "unavailable", "gameBuild.incompatible")]
    [InlineData("passive", "no_active_session", null)]
    public async Task NativeGrantedSourceUsesBoundCreationAndReportsIndependentFailure(string mode, string stop, string? reason)
    {
        using var allowed = new ColorGamePort(interaction, run, lease.FencingToken.ToString(), area, processReadAllowed: true);
        await using var worker = await ColorWorkerSession.StartAsync(Options("native_peer", mode), Cleanup);
        var profile = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Request().Arguments.GetProperty("profile").GetRawText())!;
        profile["search_strategy"] = JsonSerializer.SerializeToElement("native");
        var request = Request() with { Method = "color.native", Arguments = JsonSerializer.SerializeToElement(new { profile }) };
        var call = await worker.BeginAsync(request, portHandler: allowed.HandleAsync);
        var events = Drain(call);
        var result = await call.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await events;
        Assert.Equal("result", result.Kind);
        Assert.Equal(stop, result.Payload.GetProperty("stop_reason").GetString());
        if (reason is not null) Assert.Equal(reason, result.Payload.GetProperty("unavailable_reason").GetString());
        Assert.Empty(trace);
        Assert.Equal(0, captures);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task NativeGrantContainsOnlyBoundIdentityAndRejectsCallerPid()
    {
        using var allowed = new ColorGamePort(interaction, run, lease.FencingToken.ToString(), area, processReadAllowed: true);
        WorkerFrame Frame(object arguments) => new(1, "1", Guid.NewGuid().ToString("N"), "portRequest", run,
            binding.Session.BindingRevision.ToString(), lease.FencingToken.ToString(),
            JsonSerializer.SerializeToElement(new { callId = "1", method = "game.nativeAccess", arguments }));
        var value = await allowed.HandleAsync(Frame(new { }), CancellationToken.None);
        Assert.Equal(binding.Session.ProcessId, value.GetProperty("pid").GetInt32());
        Assert.Equal(binding.Session.WindowIdentity.ToString(), value.GetProperty("hwnd").GetString());
        Assert.Equal("tw", value.GetProperty("server").GetString());
        Assert.Equal(binding.Session.ProcessStartTime.UtcDateTime.ToFileTimeUtc().ToString(), value.GetProperty("processCreationTime").GetString());
        Assert.Equal("worker.portInvalid", (await Assert.ThrowsAsync<WorkerException>(() =>
            allowed.HandleAsync(Frame(new { pid = 999 }), CancellationToken.None))).Code);
        Assert.Empty(trace);
    }

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RealWorkerUsesHostRgbAssetsAndOriginalControllerConfirmsColors(int count)
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("screenshot_peer", count.ToString()), Cleanup);
        var call = await worker.BeginAsync(Request(), portHandler: port.HandleAsync);
        var events = Drain(call);
        var result = await call.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        await events;
        Assert.Equal("result", result.Kind);
        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.True(result.Payload.GetProperty("verified").GetBoolean());
        Assert.Equal(count, result.Payload.GetProperty("region_count").GetInt32());
        Assert.Equal(Enumerable.Repeat("#202020", count), result.Payload.GetProperty("actual_colors").EnumerateArray().Select(x => x.GetString()));
        Assert.True(result.Payload.GetProperty("no_game_memory").GetBoolean());
        var profile = ColorWorkerResultsTests.Profile();
        var projected = ColorWorkerResults.ReadResult(result, profile, new("offline", "1", "TW", null), new(7));
        Assert.Equal(count, projected.RegionCount);
        Assert.Equal(count, projected.CurrentVerified!.HexByRegion.Count);
        Assert.Equal("#202020", projected.CurrentVerified.HexByRegion["cloth"]);
        Assert.True(projected.CurrentVerified.TargetExact);
        Assert.NotNull(projected.BestMeasured);
        Assert.NotNull(projected.LastMeasured);
        Assert.True(captures >= 2);
        Assert.Empty(trace);
        Assert.Single(Directory.GetFiles(Path.Combine(area, run), "*.blob"));
        Assert.Empty(failures);
        port.Dispose();
        Assert.Empty(Directory.GetFiles(Path.Combine(area, run), "*.blob"));
        Assert.False(LeaseValid());
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task SlowHostCaptureKeepsHeartbeatAndCancellationReleasesRun()
    {
        blockCapture = true;
        await using var worker = await ColorWorkerSession.StartAsync(Options("screenshot_peer", "2"), Cleanup);
        var call = await worker.BeginAsync(Request(), portHandler: port.HandleAsync);
        var events = Drain(call);
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.True(worker.IsAlive);
        await worker.CancelAsync(call);
        Assert.Equal("cancelled", (await call.Completion).Kind);
        await events;
        Assert.False(LeaseValid());
        Assert.Empty(Directory.GetFiles(Path.Combine(area, run), "*.blob"));
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrCrashWhileHeldReleasesMouseAndRejectsOldInput(bool crash)
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("fault_peer", crash ? "port-crash" : "port-gesture"), Cleanup);
        var call = await worker.BeginAsync(Request(), portHandler: port.HandleAsync);
        await pressed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (crash)
            Assert.Equal("worker.disconnected", (await Assert.ThrowsAsync<WorkerException>(() => call.Completion.WaitAsync(TimeSpan.FromSeconds(10)))).Code);
        else
        {
            await worker.CancelAsync(call);
            Assert.Equal("cancelled", (await call.Completion).Kind);
            Assert.Empty(failures);
        }
        Assert.Equal(["absolute:110,210", "down:MouseLeft", "up:MouseLeft"], trace);
        Assert.False(LeaseValid());
        var next = leases.Acquire(new("music", "next", binding.Session.Id, binding.Session.BindingRevision,
            ["foreground-input"], TimeSpan.FromSeconds(10)));
        Assert.Equal("input.runStopped", Assert.Throws<GameInputException>(() => interaction.MovePointer(new(1, 2))).Code);
        Assert.True(leases.Validate(next.Id, "foreground-input", next.FencingToken, binding.Session.Id, binding.Session.BindingRevision));
    }

    [Fact]
    public async Task LatePortCompletionIsDroppedBeforeNextRequest()
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("fault_peer", "port-late"), Cleanup);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = await worker.BeginAsync(Request(), portHandler: async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return JsonSerializer.SerializeToElement(new { old = true });
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await worker.CancelAsync(old);
            var next = await worker.BeginAsync(Request() with { RunId = Guid.NewGuid().ToString("N") },
                portHandler: (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { next = true })));
            release.TrySetResult();
            var result = await next.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Payload.GetProperty("result").GetProperty("next").GetBoolean());
            Assert.True(worker.IsAlive);
            Assert.Empty(failures);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void PortRejectsFenceUnrelatedToInteractionLease()
    {
        Assert.Throws<ArgumentException>(() => new ColorGamePort(interaction, run, "0", area));
        Assert.True(LeaseValid());
    }

    [Fact]
    public async Task SnapshotBindingFailureKeepsTypedErrorAndWorkerCanAcceptNewRequest()
    {
        bindings.Clear();
        await using var worker = await ColorWorkerSession.StartAsync(Options("screenshot_peer", "2"), Cleanup);
        for (var i = 0; i < 2; i++)
        {
            var call = await worker.BeginAsync(Request(), portHandler: port.HandleAsync);
            var events = Drain(call);
            var result = await call.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            await events;
            Assert.Equal("error", result.Kind);
            Assert.Equal("input.bindingChanged", result.Payload.GetProperty("code").GetString());
        }
        Assert.False(LeaseValid());
        Assert.True(worker.IsAlive);
        Assert.Empty(trace);
        Assert.Empty(failures);
    }

    private static async Task Drain(WorkerCall call) { await foreach (var _ in call.Events.ReadAllAsync()) { } }
    private bool LeaseValid() => leases.Validate(lease.Id, "foreground-input", lease.FencingToken, binding.Session.Id, binding.Session.BindingRevision);
    UInt64String? IGameInputDevice.ForegroundWindowIdentity => new(500);
    void IGameInputDevice.Press(InputControl control) { trace.Enqueue("down:" + control); pressed.TrySetResult(); }
    void IGameInputDevice.Release(InputControl control) => trace.Enqueue("up:" + control);
    void IGameInputDevice.MovePointer(PhysicalScreenPoint point) => trace.Enqueue($"absolute:{point.X},{point.Y}");
    void IGameInputDevice.MovePointerRelative(PhysicalScreenPoint delta) => trace.Enqueue($"relative:{delta.X},{delta.Y}");
    void IGameInputDevice.Wheel(int delta) => trace.Enqueue("wheel:" + delta);
    void IGameInputDevice.ReleaseAll() => trace.Enqueue("all-up");
    async ValueTask<GameCapture> IGameCaptureSource.CaptureAsync(GameWindowBinding selected, CancellationToken token)
    {
        Interlocked.Increment(ref captures);
        captured.TrySetResult();
        if (blockCapture) await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new(200, 120, Enumerable.Repeat((byte)255, 200 * 120 * 3).ToArray());
    }
    public void Dispose()
    {
        port.Dispose();
        var parent = Path.GetFullPath(Path.Combine(repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(area).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(area, recursive: true);
    }
}
