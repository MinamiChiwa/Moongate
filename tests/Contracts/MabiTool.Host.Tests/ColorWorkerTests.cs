using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using MabiTool.Infrastructure.Workers;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class ColorWorkerTests : IDisposable
{
    private readonly string repository;
    private readonly string area;
    private readonly string python;
    private readonly ConcurrentQueue<WorkerFailure> failures = new();

    public ColorWorkerTests()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MabiTool.slnx"))) current = current.Parent;
        repository = current?.FullName ?? throw new InvalidOperationException("无法定位仓库。");
        area = Path.Combine(repository, "artifacts", "tmp", "worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        var executable = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        executable.ArgumentList.Add("-c"); executable.ArgumentList.Add("import sys; print(sys.executable)");
        using var probe = Process.Start(executable)!;
        python = probe.StandardOutput.ReadToEnd().Trim();
        probe.WaitForExit();
        Assert.Equal(0, probe.ExitCode);
    }

    // 默认 700 ms 宽限让“不响应”类用例快速结束；验证 Worker 自行退出的用例传入允许的最大宽限，
    // 负载下解释器退出变慢时仍判定 shutdown 路径，而宽限上限本身由 WorkerIgnoringShutdownIsTerminatedAfterTheGrace 覆盖。
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);

    private ColorWorkerOptions Options(string? mode = null, TimeSpan? grace = null) => new(python,
        Path.Combine(repository, "workers", "color-engine", mode is null ? "" : "tests"), area)
    {
        EntryModule = mode is null ? "mabitool_color.worker" : "fault_peer",
        Arguments = mode is null ? [] : [mode],
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        HeartbeatTimeout = TimeSpan.FromSeconds(2),
        CancellationGrace = grace ?? TimeSpan.FromMilliseconds(700)
    };

    private static JsonElement Arguments() => JsonSerializer.SerializeToElement(new
    {
        profile = new { schema = 1, search_strategy = "screenshot", priority_order = new[] { 2, 0, 1 },
            regions = Enumerable.Range(0, 3).Select(i => new { enabled = true, target = "#202020", alt = "", mode = "精准 HEX", tolerance = 8 }) },
        colors = new[] { "#202020", "#202020" }
    });

    private static WorkerRequest Request(string? run = null) => new("color.evaluate", Arguments(), run ?? Guid.NewGuid().ToString("N"), "18446744073709551615", "7");

    [Fact]
    public async Task RealPythonWorkerEvaluatesOriginalRulesAndRemainsUsableAcrossRequests()
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options(), failures.Enqueue);
        var call = await worker.BeginAsync(Request());
        var result = await call.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        var events = new List<WorkerFrame>();
        await foreach (var frame in call.Events.ReadAllAsync()) events.Add(frame);
        Assert.Equal("result", result.Kind);
        Assert.True(result.Payload.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal(2, result.Payload.GetProperty("regionCount").GetInt32());
        Assert.Equal(2, result.Payload.GetProperty("rules")[0].GetProperty("priority").GetInt32());
        Assert.Equal(["accepted", "event"], events.Select(e => e.Kind));
        Assert.All(events, e => Assert.Equal("18446744073709551615", e.BindingRevision));
        var second = await worker.BeginAsync(Request());
        Assert.Equal("result", (await second.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        Assert.True(worker.IsAlive);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("wrong-version", "worker.protocolIncompatible")]
    [InlineData("wrong-token", "worker.handshakeInvalid")]
    [InlineData("wrong-pid", "worker.peerInvalid")]
    public async Task RejectsIncompatibleHandshake(string mode, string code)
    {
        var error = await Assert.ThrowsAsync<WorkerException>(() => ColorWorkerSession.StartAsync(Options(mode), failures.Enqueue));
        Assert.Equal(code, error.Code);
        Assert.Equal(code, Assert.Single(failures).Code);
    }

    [Theory]
    [InlineData("crash", "worker.disconnected")]
    [InlineData("oversize", "worker.frameTooLarge")]
    [InlineData("wrong-run", "worker.requestIdentityInvalid")]
    [InlineData("duplicate-json", "worker.frameInvalid")]
    [InlineData("wrong-sequence", "worker.sequenceInvalid")]
    [InlineData("duplicate-accepted", "worker.phaseInvalid")]
    [InlineData("flood", "worker.outputBackpressure")]
    public async Task FaultsInterruptRequestAndCleanUp(string mode, string code)
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options(mode), failures.Enqueue);
        var request = Request();
        var call = await worker.BeginAsync(request);
        var error = await Assert.ThrowsAsync<WorkerException>(() => call.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(code, error.Code);
        Assert.Equal(request.RunId, Assert.Single(failures).RunId);
        Assert.False(worker.IsAlive);
    }

    [Fact]
    public async Task HeartbeatTimeoutCleansUpBeforeKillingProcess()
    {
        ColorWorkerSession? session = null;
        var aliveAtCleanup = false;
        await using var worker = await ColorWorkerSession.StartAsync(Options("silent"), failure =>
        {
            if (failure.Code == "worker.heartbeatTimeout")
            {
                using var process = Process.GetProcessById(session!.ProcessId);
                aliveAtCleanup = !process.HasExited;
            }
            failures.Enqueue(failure);
        });
        session = worker;
        // 故障先把会话标为关闭再调用清理回调，IsAlive 变为 false 时回调可能尚未入队，因此等待失败记录本身。
        var until = Stopwatch.StartNew();
        while ((worker.IsAlive || failures.IsEmpty) && until.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
        Assert.Equal("worker.heartbeatTimeout", Assert.Single(failures).Code);
        Assert.True(aliveAtCleanup);
        Assert.False(worker.IsAlive);
    }

    [Fact]
    public async Task CooperativeCancellationAndSingleRequestCapacity()
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("cancel"), failures.Enqueue);
        var call = await worker.BeginAsync(Request());
        var busy = await Assert.ThrowsAsync<WorkerException>(() => worker.BeginAsync(Request()));
        Assert.Equal("worker.busy", busy.Code);
        await worker.CancelAsync(call);
        Assert.Equal("cancelled", (await call.Completion).Kind);
        var next = await worker.BeginAsync(Request());
        await worker.CancelAsync(next);
        Assert.True(worker.IsAlive);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("command-invalid", "worker.commandInvalid")]
    [InlineData("command-wrong-run", "worker.requestIdentityInvalid")]
    [InlineData("command-wait", "worker.commandTimeout")]
    public async Task InvalidOrUnresponsiveInteractionFailsBoundedly(string mode, string code)
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options(mode), failures.Enqueue);
        var call = await worker.BeginAsync(Request());
        await call.Events.ReadAsync();
        var error = await Assert.ThrowsAsync<WorkerException>(() => worker.InteractAsync(call, "retry", "0"));
        Assert.Equal(code, error.Code);
        await Assert.ThrowsAsync<WorkerException>(() => call.Completion);
        Assert.Equal(code, Assert.Single(failures).Code);
        Assert.False(worker.IsAlive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrTerminalReleasesPendingCommand(bool terminal)
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options(terminal ? "command-ended" : "command-wait"), failures.Enqueue);
        var call = await worker.BeginAsync(Request());
        await call.Events.ReadAsync();
        var pending = worker.InteractAsync(call, "retry", "0");
        if (!terminal) await worker.CancelAsync(call);
        var error = await Assert.ThrowsAsync<WorkerException>(() => pending);
        Assert.Equal("color.runInactive", error.Code);
        Assert.Equal(terminal ? "result" : "cancelled", (await call.Completion).Kind);
        Assert.True(worker.IsAlive);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task UnresponsiveCancellationTerminatesWorker()
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("ignore-cancel"), failures.Enqueue);
        var call = await worker.BeginAsync(Request());
        var error = await Assert.ThrowsAsync<WorkerException>(() => worker.CancelAsync(call));
        Assert.Equal("worker.cancelTimeout", error.Code);
        await Assert.ThrowsAsync<WorkerException>(() => call.Completion);
        Assert.Equal("worker.cancelTimeout", Assert.Single(failures).Code);
    }

    [Fact]
    public async Task NormalStopUsesShutdownAndKeepsTheWorkerExitCode()
    {
        var worker = await ColorWorkerSession.StartAsync(Options(grace: ExitGrace), failures.Enqueue);
        var call = await worker.BeginAsync(Request());
        Assert.Equal("result", (await call.Completion.WaitAsync(TimeSpan.FromSeconds(60))).Kind);
        var pid = worker.ProcessId;
        await worker.DisposeAsync();
        Assert.Equal(0, worker.ExitCode);
        Assert.False(worker.TerminatedByHost);
        Assert.False(worker.IsAlive);
        Assert.False(Running(pid));
        var stopped = Assert.Single(failures);
        Assert.Equal(("worker.stopped", (string?)null), (stopped.Code, stopped.RunId));
    }

    [Fact]
    public async Task StopDuringActiveRequestEndsTheRunBeforeTheWorkerExits()
    {
        var worker = await ColorWorkerSession.StartAsync(Options("cancel", ExitGrace), failures.Enqueue);
        var request = Request();
        var call = await worker.BeginAsync(request);
        await worker.DisposeAsync();
        var error = await Assert.ThrowsAsync<WorkerException>(() => call.Completion);
        Assert.Equal("worker.stopped", error.Code);
        Assert.Equal(request.RunId, Assert.Single(failures).RunId);
        await Assert.ThrowsAsync<WorkerException>(async () => { await foreach (var _ in call.Events.ReadAllAsync()) { } });
        Assert.Equal(0, worker.ExitCode);
        Assert.False(worker.TerminatedByHost);
        await Assert.ThrowsAsync<WorkerException>(() => worker.BeginAsync(Request()));
    }

    [Fact]
    public async Task WorkerIgnoringShutdownIsTerminatedAfterTheGrace()
    {
        var worker = await ColorWorkerSession.StartAsync(Options("ignore-shutdown"), failures.Enqueue);
        var pid = worker.ProcessId;
        var elapsed = Stopwatch.StartNew();
        await worker.DisposeAsync();
        elapsed.Stop();
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(5));
        Assert.True(worker.TerminatedByHost);
        Assert.NotEqual(0, worker.ExitCode);
        Assert.False(Running(pid));
        Assert.Equal("worker.stopped", Assert.Single(failures).Code);
    }

    [Fact]
    public async Task NormalStopAlsoReclaimsDescendantsLeftByTheWorker()
    {
        var worker = await ColorWorkerSession.StartAsync(Options("spawn-child", ExitGrace), failures.Enqueue);
        var call = await worker.BeginAsync(Request());
        var child = (await call.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Payload.GetProperty("child").GetInt32();
        Assert.True(Running(child));
        await worker.DisposeAsync();
        Assert.Equal(0, worker.ExitCode);
        Assert.False(worker.TerminatedByHost);
        var until = Stopwatch.StartNew();
        while (Running(child) && until.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
        Assert.False(Running(child));
    }

    private static bool Running(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task LogsAreDrainedAndBoundedSeparatelyFromProtocol()
    {
        await using var worker = await ColorWorkerSession.StartAsync(Options("logs"), failures.Enqueue);
        var call = await worker.BeginAsync(Request());
        Assert.Equal("result", (await call.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        Assert.InRange(worker.DiagnosticTail.Length, 1, 16 * 1024);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task RealWorkerReadsGrantedAssetAndRejectsTamperingAndCrossRun()
    {
        var run = Guid.NewGuid().ToString("N");
        var asset = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(area, run));
        var bytes = Encoding.UTF8.GetBytes(Arguments().GetRawText());
        var path = Path.Combine(area, run, asset + ".blob");
        await File.WriteAllBytesAsync(path, bytes);
        var reference = new WorkerAssetReference(asset, run, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());
        await using var worker = await ColorWorkerSession.StartAsync(Options(), failures.Enqueue);
        var call = await worker.BeginAsync(Request(run) with { ArgumentsAsset = reference });
        Assert.Equal("result", (await call.Completion.WaitAsync(TimeSpan.FromSeconds(60))).Kind);
        await File.WriteAllBytesAsync(path, new byte[bytes.Length]);
        var corrupted = await worker.BeginAsync(Request(run) with { ArgumentsAsset = reference });
        Assert.Equal("worker.assetInvalid", (await corrupted.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Payload.GetProperty("code").GetString());
        var other = await worker.BeginAsync(Request() with { ArgumentsAsset = reference });
        Assert.Equal("worker.assetInvalid", (await other.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Payload.GetProperty("code").GetString());
        Assert.True(worker.IsAlive);
    }

    [Fact]
    public async Task CodecRejectsDuplicateFieldsAndOversizedHeadersBeforeBodyAllocation()
    {
        using var body = new MemoryStream();
        await WorkerProtocol.WriteAsync(body, new(1, "1", null, "ping", null, null, null, JsonSerializer.SerializeToElement(new { })));
        var raw = Encoding.UTF8.GetString(body.ToArray()[4..]).Replace("\"payload\":{}", "\"payload\":{\"a\":1,\"a\":2}");
        var encoded = Encoding.UTF8.GetBytes(raw);
        using var duplicate = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, encoded.Length);
        duplicate.Write(header); duplicate.Write(encoded); duplicate.Position = 0;
        Assert.Equal("worker.frameInvalid", (await Assert.ThrowsAsync<WorkerException>(() => WorkerProtocol.ReadAsync(duplicate))).Code);
        BinaryPrimitives.WriteInt32LittleEndian(header, WorkerProtocol.MaximumFrameBytes + 1);
        Assert.Equal("worker.frameTooLarge", (await Assert.ThrowsAsync<WorkerException>(() => WorkerProtocol.ReadAsync(new MemoryStream(header)))).Code);
    }

    public void Dispose()
    {
        // 测试目录由本夹具直接创建，解析后限定在仓库 artifacts/tmp 内。
        var parent = Path.GetFullPath(Path.Combine(repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(area).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(area, recursive: true);
    }
}
