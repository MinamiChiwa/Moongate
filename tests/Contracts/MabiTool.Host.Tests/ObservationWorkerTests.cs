using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Infrastructure.Workers;
using MabiTool.Observation.Worker;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
[Collection("Persistent backend")]
public sealed class ObservationWorkerTests
{
    [Fact]
    public async Task NativeCaptureRejectsMissingOrMismatchingClientFilesBeforeOpeningHandles()
    {
        using var area = new GameObservationApiTests.Area();
        var image = Path.Combine(area.Root, "game.exe");
        var missing = await Assert.ThrowsAsync<WorkerObservationException>(() => CaptureChannels.VerifyClientAsync(image, CancellationToken.None));
        Assert.Equal("gameBuild.incompatible", missing.Code);
        await File.WriteAllBytesAsync(Path.Combine(area.Root, "GameAssembly.dll"), [1, 2, 3, 4]);
        var incompatible = await Assert.ThrowsAsync<WorkerObservationException>(() => CaptureChannels.VerifyClientAsync(image, CancellationToken.None));
        Assert.Equal("gameBuild.incompatible", incompatible.Code);
        Assert.False(File.Exists(Path.Combine(area.Root, "handles")));
    }
    [Fact]
    public async Task ThreeCancelledCapturesReuseTheWorkerAfterTheirHandlesClose()
    {
        using var area = new GameObservationApiTests.Area();
        await using var worker = await WorkerSession.StartAsync(area.Options(), _ => { });
        var pid = worker.ProcessId;
        for (var index = 1; index <= 3; index++)
        {
            var call = await worker.BeginAsync(Request());
            var events = new ConcurrentQueue<WorkerFrame>();
            var drain = Task.Run(async () => { await foreach (var value in call.Events.ReadAllAsync()) events.Enqueue(value); });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!events.Any(value => value.Kind == "event")) await Task.Delay(20, deadline.Token);
            await worker.CancelAsync(call);
            Assert.Equal("cancelled", (await call.Completion).Kind); await drain;
            Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
            Assert.True(File.Exists(Path.Combine(area.Root, "capture-stopped-" + index)));
            Assert.Equal(pid, worker.ProcessId); Assert.True(worker.IsAlive);
            Assert.All(events, value => Assert.Equal(call.Completion.Result.RunId, value.RunId));
        }
        Assert.Single(Directory.GetFiles(area.Root, "process-start-*"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureDiagnosticsAreSentOnlyWhenTheHostDeclaresSupport(bool declared)
    {
        using var area = new GameObservationApiTests.Area();
        var options = area.Options();
        if (declared) options = options with { EnvironmentVariables = new Dictionary<string, string> { [MabiTool.Contracts.ObservationCaptureCounters.EnvironmentVariable] = "1" } };
        await using var worker = await WorkerSession.StartAsync(options, _ => { });
        var call = await worker.BeginAsync(Request());
        var events = new ConcurrentQueue<WorkerFrame>();
        var drain = Task.Run(async () => { await foreach (var value in call.Events.ReadAllAsync()) events.Enqueue(value); });
        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        await worker.CancelAsync(call); await drain;
        var diagnostics = events.Where(value => value.Kind == "event" && value.Payload.TryGetProperty("captureDiagnostics", out _)).ToArray();
        Assert.Contains(events, value => value.Kind == "event" && value.Payload.TryGetProperty("report", out _));
        if (!declared) { Assert.Empty(diagnostics); return; }
        // 计数未变化时不重复发送：合成音乐源不经过共享流，首份之后只有结束前的最终值可能不同。
        Assert.InRange(diagnostics.Length, 1, 3);
        var counters = diagnostics[0].Payload.GetProperty("captureDiagnostics").Deserialize<MabiTool.Contracts.ObservationCaptureCounters>(MabiTool.Contracts.ContractJson.Options)!;
        Assert.Equal(1, counters.SchemaVersion); Assert.Equal(0UL, counters.Frames.Total.Value);
    }
    [Fact]
    public async Task WorkerIdentityMismatchFailsBeforeAnyCaptureRequest()
    {
        using var area = new GameObservationApiTests.Area();
        var failures = new ConcurrentQueue<WorkerFailure>();
        var failure = await Assert.ThrowsAsync<WorkerException>(() => WorkerSession.StartAsync(area.Options() with { WorkerId = "other-worker" }, failures.Enqueue));
        Assert.Equal("worker.handshakeInvalid", failure.Code); Assert.Single(failures);
        Assert.Empty(Directory.GetFiles(area.Root, "capture-start-*"));
    }
    [Fact]
    public async Task CancellationTimeoutInvokesCleanupBeforeKillingTheActualProcess()
    {
        using var area = new GameObservationApiTests.Area();
        var failures = new ConcurrentQueue<(string Code, bool Alive, string? Run)>();
        var pid = 0;
        await using var worker = await WorkerSession.StartAsync(area.Options("stall") with { CancellationGrace = TimeSpan.FromMilliseconds(500) },
            error => failures.Enqueue((error.Code, Alive(pid), error.RunId)));
        pid = worker.ProcessId; var request = Request(); var call = await worker.BeginAsync(request);
        var drain = Task.Run(async () =>
        {
            try { await foreach (var _ in call.Events.ReadAllAsync()) { } }
            catch (WorkerException) { }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(area.Root, "capture-start-1"))) await Task.Delay(20, deadline.Token);
        var failure = await Assert.ThrowsAsync<WorkerException>(() => worker.CancelAsync(call));
        Assert.Equal("worker.cancelTimeout", failure.Code);
        var cleanup = Assert.Single(failures); Assert.True(cleanup.Alive); Assert.Equal(request.RunId, cleanup.Run);
        await Assert.ThrowsAsync<WorkerException>(() => call.Completion); await drain;
        while (Alive(pid)) await Task.Delay(20, deadline.Token);
    }
    [Fact]
    public async Task NativeChannelEnumerationReportsTheDriverCapabilityWithoutCapturingPackets()
    {
        using var area = new GameObservationApiTests.Area();
        var executable = GameObservationApiTests.Area.BuildExecutable("MabiTool.Observation.Worker");
        await using var worker = await WorkerSession.StartAsync(new(executable, Path.GetDirectoryName(executable)!, area.Root, "game-observation"), _ => { });
        var call = await worker.BeginAsync(new("observation.channels", JsonSerializer.SerializeToElement(new { }), Guid.NewGuid().ToString("N"), "0", "0"));
        var result = await call.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await foreach (var _ in call.Events.ReadAllAsync()) { }
        // 系统目录下没有 Npcap 时为 npcapMissing；文件存在但加载失败时为 npcapUnavailable。
        if (result.Kind == "error") Assert.Equal(File.Exists(NpcapInstallation.LibraryPath) ? "observation.npcapUnavailable" : "observation.npcapMissing",
            result.Payload.GetProperty("code").GetString());
        else { Assert.Equal("result", result.Kind); Assert.Equal(JsonValueKind.Array, result.Payload.GetProperty("channels").ValueKind); }
        Assert.Empty(Directory.GetFiles(area.Root, "capture-start-*"));
    }
    private static WorkerRequest Request()
    {
        using var process = Process.GetCurrentProcess();
        return new("observation.capture", JsonSerializer.SerializeToElement(new { gameSessionId = "fixture-game", region = "TW", processId = process.Id,
            processStartTime = new DateTimeOffset(process.StartTime.ToUniversalTime()), channelId = new string('1', 32), profile = "tw-music-v1" }),
            Guid.NewGuid().ToString("N"), "1", "0");
    }
    private static bool Alive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
}
