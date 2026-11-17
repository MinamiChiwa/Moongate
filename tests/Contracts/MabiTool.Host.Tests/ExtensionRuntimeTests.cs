using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Extensions;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>
/// 带代码扩展的进程与协议：握手版本不符、未声明或未授予的调用、高风险默认关闭、撤销立即生效、
/// 崩溃退避与上限、挂死、超速、超长消息、迟到响应，以及停用与宿主退出后不留进程。扩展进程由测试用的 ExtensionFixture 扮演。
/// </summary>
public sealed class ExtensionRuntimeTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "moongate-extension-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly ExtensionInstaller installer;
    private readonly ExtensionRuntime runtime;
    private readonly StateEventHub hub = new(null, "test-epoch");
    private readonly ExtensionUiRegistry ui = new();
    private readonly ServiceProvider services;
    private readonly List<int> processes = [];
    private readonly GameControlContext controls;
    private readonly ExtensionFileRequests files = new(TimeProvider.System);
    private readonly LoopbackServer server;

    public ExtensionRuntimeTests()
    {
        ExtensionSession.HandshakeTimeout = TimeSpan.FromSeconds(20);
        ExtensionSession.PingInterval = TimeSpan.FromSeconds(10);
        ExtensionSession.PingTimeout = TimeSpan.FromSeconds(5);
        ExtensionRuntime.Backoff = [TimeSpan.FromMilliseconds(50)];
        installer = new(Path.Combine(root, "extensions"), new(1, 0));
        installer.Initialize();
        installer.SetDevelopmentMode(true, includePaths: false);
        var simulated = GameControlContext.Create(new ResourceLeaseCoordinator(), TimeProvider.System, simulation: true);
        controls = simulated with { Background = new GameBackgroundInputBroker(simulated.Leases, simulated.Bindings, new RecordingBackgroundDevice()) };
        server = new LoopbackServer();
        var client = new HttpClient(new SocketsHttpHandler
        {
            // 测试里的主机名都连到本机回环的临时服务，不访问外网。
            ConnectCallback = async (_, ct) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                await socket.ConnectAsync(System.Net.IPAddress.Loopback, server.Port, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
        });
        services = new ServiceCollection().AddSingleton(hub).AddSingleton(ui).AddSingleton(controls).AddSingleton(files)
            .AddSingleton(new ExtensionNetworkOptions(client, AllowHttp: true, TimeSpan.FromSeconds(1)))
            .AddSingleton<IGameCaptureSource>(new FakeCapture()).BuildServiceProvider();
        var broker = new ExtensionCapabilities(services, installer, TimeProvider.System);
        runtime = new(installer, broker, TimeProvider.System, launch =>
        {
            var process = WindowsExtensionProcess.Start(launch);
            lock (processes) processes.Add(process.Id);
            return process;
        });
        runtime.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task HandshakeWithAnotherProtocolVersionIsRejectedWithoutRestart()
    {
        await StartAsync("test.version", "bad-version");
        var state = await WaitStateAsync("test.version", x => x.State == "failed");
        Assert.Equal("extension.protocolIncompatible", state.ReasonCode);
        Assert.Equal(0, state.Restarts);
        AssertNoProcessLeft();
    }

    [Fact]
    public async Task UndeclaredUngrantedAndUnknownCallsAreRejectedAndHighRiskStartsOff()
    {
        var installed = await StartAsync("test.rogue", "rogue-call", ["network.fetch"], hosts: ["example.com"]);
        Assert.False(Assert.Single(installed.Permissions!).Granted);
        Assert.Equal("high", installed.Permissions![0].Risk);
        await WaitLogAsync("test.rogue", "reply 103");
        var lines = Lines("test.rogue");
        Assert.Contains("reply 101 extension.permissionDenied", lines);
        Assert.Contains("reply 102 extension.permissionDenied", lines);
        Assert.Contains("reply 103 extension.methodUnknown", lines);
        Assert.Equal("running", runtime.State("test.rogue")!.State);
    }

    [Fact]
    public async Task RevokingAPermissionStopsItsEventsImmediately()
    {
        await StartAsync("test.events", "subscribe", ["combat.read"]);
        await WaitLogAsync("test.events", "reply 101 ok");
        await PublishMeasurementAsync(1);
        await WaitLogAsync("test.events", "notify event");

        await installer.SetGrantsAsync("test.events", new Dictionary<string, bool> { ["combat.read"] = false }, false, CancellationToken.None);
        await runtime.ApplyAsync("test.events");
        await WaitLogAsync("test.events", "notify grants/changed");
        var before = Lines("test.events").Count(x => x.StartsWith("notify event", StringComparison.Ordinal));
        await PublishMeasurementAsync(2);
        await Task.Delay(500);
        Assert.Equal(before, Lines("test.events").Count(x => x.StartsWith("notify event", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CrashingExtensionRestartsWithBackoffAndThenStops()
    {
        await StartAsync("test.crash", "crash");
        var state = await WaitStateAsync("test.crash", x => x.State == "failed", TimeSpan.FromSeconds(60));
        Assert.Equal("extension.crashLoop", state.ReasonCode);
        Assert.Equal(ExtensionRuntime.MaximumRestarts + 1, processes.Count);
        AssertNoProcessLeft();
    }

    [Fact]
    public async Task HungExtensionIsKilledAfterTwoMissedHeartbeats()
    {
        ExtensionSession.PingInterval = TimeSpan.FromMilliseconds(300);
        ExtensionSession.PingTimeout = TimeSpan.FromMilliseconds(300);
        ExtensionRuntime.Backoff = [TimeSpan.FromMinutes(5)];
        await StartAsync("test.hang", "hang");
        var state = await WaitStateAsync("test.hang", x => x.State == "restarting");
        Assert.Equal("extension.unresponsive", state.ReasonCode);
        AssertNoProcessLeft();
    }

    [Theory]
    [InlineData("flood", "extension.rateLimited")]
    [InlineData("huge", "extension.messageTooLarge")]
    public async Task ExtensionsExceedingMessageLimitsAreStopped(string mode, string code)
    {
        if (mode == "huge") ExtensionSession.HandshakeTimeout = TimeSpan.FromSeconds(20);
        await StartAsync("test." + mode, mode);
        var state = await WaitStateAsync("test." + mode, x => x.State == "failed");
        Assert.Equal(code, state.ReasonCode);
        AssertNoProcessLeft();
    }

    [Fact]
    public async Task LateResponsesAreIgnoredAndLogged()
    {
        ExtensionSession.PingInterval = TimeSpan.FromSeconds(1);
        ExtensionSession.PingTimeout = TimeSpan.FromMilliseconds(300);
        await StartAsync("test.late", "late");
        await WaitLogAsync("test.late", "没有对应请求的响应");
    }

    [Fact]
    public async Task DisablingAndHostShutdownLeaveNoProcess()
    {
        await StartAsync("test.good", "good");
        await installer.SetEnabledAsync("test.good", false, false, CancellationToken.None);
        await runtime.ApplyAsync("test.good");
        Assert.Equal(("stopped", "extension.disabled"), (runtime.State("test.good")!.State, runtime.State("test.good")!.ReasonCode));
        AssertNoProcessLeft();

        await installer.SetEnabledAsync("test.good", true, false, CancellationToken.None);
        await runtime.ApplyAsync("test.good");
        Assert.Equal("running", runtime.State("test.good")!.State);
        await runtime.StopAsync(CancellationToken.None);
        AssertNoProcessLeft();
    }

    [Fact]
    public async Task PagesAndOverlaysAreRegisteredAndUiEventsReachTheExtension()
    {
        await StartAsync("test.page", "page", ["ui.page", "ui.overlay"]);
        await WaitLogAsync("test.page", "reply 103 ok");
        var (surfaces, states, _, _) = ui.Snapshot();
        Assert.Equal(["overlay:mini", "page:main"], surfaces.Select(x => x.Kind + ":" + x.Id).Order(StringComparer.Ordinal));
        Assert.Equal(7, Assert.Single(states).Values["n"].GetInt32());

        await runtime.Running("test.page")!.NotifyAsync("ui/event", new System.Text.Json.Nodes.JsonObject { ["surface"] = "main", ["node"] = "go", ["kind"] = "click" });
        await WaitLogAsync("test.page", "notify ui/event");

        await installer.SetGrantsAsync("test.page", new Dictionary<string, bool> { ["ui.overlay"] = false }, false, CancellationToken.None);
        await runtime.ApplyAsync("test.page");
        Assert.Equal("page:main", Assert.Single(ui.Snapshot().Surfaces) is var item ? item.Kind + ":" + item.Id : null);

        await installer.SetEnabledAsync("test.page", false, false, CancellationToken.None);
        await runtime.ApplyAsync("test.page");
        Assert.Empty(ui.Snapshot().Surfaces);
    }

    [Fact]
    public async Task RevokingInputReleasesTheExtensionsInputLease()
    {
        await StartAsync("test.input", "input", ["input.selected-window"]);
        await WaitLogAsync("test.input", "reply 101 extension.permissionDenied");
        await installer.SetGrantsAsync("test.input", new Dictionary<string, bool> { ["input.selected-window"] = true }, false, CancellationToken.None);
        await runtime.RestartAsync("test.input");
        await WaitLogAsync("test.input", "reply 101 ok");
        Assert.Contains(controls.Leases.Snapshot(), x => x.OwnerId == "extension:test.input" && x.Resources.Contains(GameBackgroundInputBroker.Resource));

        await installer.SetGrantsAsync("test.input", new Dictionary<string, bool> { ["input.selected-window"] = false }, false, CancellationToken.None);
        await runtime.ApplyAsync("test.input");
        Assert.DoesNotContain(controls.Leases.Snapshot(), x => x.OwnerId == "extension:test.input");
    }

    private sealed class RecordingBackgroundDevice : IGameBackgroundInputDevice
    {
        public string? Unavailable(GameWindowBinding binding) => null;
        public void Gesture(UInt64String window, IReadOnlyList<PhysicalScreenPoint[]> frames, int hold, int release, Action checkpoint) { foreach (var _ in frames) checkpoint(); }
        public void Key(UInt64String window, ushort key, bool down) { }
        public void ReleaseAll() { }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task NetworkReachesOnlyDeclaredHostsWithinSizeAndTimeLimits()
    {
        await StartAsync("test.network", "network", ["network.fetch"], hosts: ["api.example.test"], grantAll: true);
        await WaitLogAsync("test.network", "reply 104 extension.networkTimeout");
        var lines = Lines("test.network");
        Assert.Contains(lines, x => x.StartsWith("reply 101 ok", StringComparison.Ordinal) && x.Contains("\"status\":200") && x.Contains("hello"));
        Assert.Contains("reply 102 extension.hostNotDeclared", lines);
        Assert.Contains("reply 103 extension.resultTooLarge", lines);
        Assert.Contains("reply 104 extension.networkTimeout", lines);
    }

    [Fact]
    public async Task CaptureIsDownscaledAndRateLimited()
    {
        await StartAsync("test.capture", "capture", ["capture.selected-window"], grantAll: true);
        await WaitLogAsync("test.capture", "rgb24");
        await WaitLogAsync("test.capture", "resource.capacityExceeded");
        var lines = Lines("test.capture").Where(x => x.StartsWith("reply 10", StringComparison.Ordinal)).ToArray();
        Assert.Single(lines, x => x.Contains("\"width\":16") && x.Contains("\"height\":9") && x.Contains("rgb24"));
        Assert.Single(lines, x => x.EndsWith("resource.capacityExceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PickedFileReachesTheExtensionOnceWithoutAPathAndRepeatsAreLimited()
    {
        files.DesktopPolled();
        await StartAsync("test.pick", "pick", ["files.read-selected"], grantAll: true);
        var request = await WaitAsync(() => files.Snapshot().SingleOrDefault(), () => string.Join(" | ", Lines("test.pick")));
        Assert.Equal(("test.pick", "选择存档"), (request.ExtensionId, request.Title));
        Assert.True(files.Complete(request.Id, new("save.txt", "abc"u8.ToArray(), "sha")));
        await WaitLogAsync("test.pick", "reply 102");
        var lines = Lines("test.pick");
        Assert.Contains(lines, x => x.StartsWith("reply 101 ok", StringComparison.Ordinal) && x.Contains("\"name\":\"save.txt\"") &&
            x.Contains("\"content\":\"YWJj\"") && !x.Contains(root, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("reply 102 resource.capacityExceeded", lines);
    }

    [Fact]
    public async Task FileRequestsNeedADesktopAndReportCancellation()
    {
        var requests = new ExtensionFileRequests(TimeProvider.System);
        Assert.Equal("extension.noDesktop", (await Assert.ThrowsAsync<ExtensionCallException>(() =>
            requests.RequestAsync("test.x", new Dictionary<string, string>(), "", CancellationToken.None))).Code);
        requests.DesktopPolled();
        var pending = requests.RequestAsync("test.x", new Dictionary<string, string>(), "", CancellationToken.None);
        var request = Assert.Single(requests.Snapshot());
        Assert.Throws<OperationException>(() => requests.Complete(request.Id, new("big", new byte[ExtensionFileRequests.MaximumBytes + 1], "sha")));
        Assert.True(requests.Complete(request.Id, null));
        Assert.Null(await pending);
        Assert.False(requests.Complete(request.Id, null));
    }

    [Fact]
    public async Task ShortcutCommandsReachOnlyARunningGrantedExtension()
    {
        await StartAsync("test.keys", "good", ["ui.shortcut"], commands: true);
        var action = Assert.Single(runtime.ShortcutActions());
        Assert.Equal(("extension.test.keys.reset", null), (action.ActionId, action.UnavailableReason));
        Assert.True(runtime.TriggerShortcut(action.ActionId));
        await WaitLogAsync("test.keys", "notify shortcut/triggered");

        await installer.SetGrantsAsync("test.keys", new Dictionary<string, bool> { ["ui.shortcut"] = false }, false, CancellationToken.None);
        await runtime.ApplyAsync("test.keys");
        Assert.Equal("extension.permissionDenied", Assert.Single(runtime.ShortcutActions()).UnavailableReason);
        Assert.False(runtime.TriggerShortcut(action.ActionId));
    }

    private static async Task<T> WaitAsync<T>(Func<T?> read, Func<string> describe) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (read() is { } value) return value;
            await Task.Delay(50);
        }
        throw new TimeoutException("等待超时：" + describe());
    }

    private sealed class FakeCapture : IGameCaptureSource
    {
        public ValueTask<GameCapture> CaptureAsync(GameWindowBinding binding, CancellationToken token) =>
            ValueTask.FromResult(new GameCapture(160, 90, new byte[160 * 90 * 3]));
    }

    /// <summary>本机回环上的临时 HTTP 服务：/ok 返回 hello，/big 返回 600 KiB，/slow 三秒后才响应。</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public int Port => ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        public LoopbackServer()
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                var reader = new StreamReader(stream);
                                var line = await reader.ReadLineAsync() ?? string.Empty;
                                while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                                if (line.Contains("/slow")) await Task.Delay(3000, stop.Token);
                                var body = line.Contains("/big") ? new string('x', 600 * 1024) : "hello";
                                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                                var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                                await stream.WriteAsync(head);
                                await stream.WriteAsync(bytes);
                            }
                            catch { }
                        }
                    });
                }
            });
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }

    public async Task DisposeAsync()
    {
        server.Dispose();
        await runtime.DisposeAsync();
        await services.DisposeAsync();
        installer.Dispose();
        AssertNoProcessLeft();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private async Task<InstalledExtension> StartAsync(string id, string mode, string[]? permissions = null, string[]? hosts = null, bool commands = false, bool grantAll = false)
    {
        var directory = Path.Combine(root, "dev", id);
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "build", "bin", "MabiTool.ExtensionFixture", new DirectoryInfo(AppContext.BaseDirectory).Name);
        foreach (var file in new[] { "MabiTool.ExtensionFixture.dll", "MabiTool.ExtensionFixture.runtimeconfig.json", "MabiTool.ExtensionFixture.deps.json" })
            File.Copy(Path.Combine(fixture, file), Path.Combine(directory, file));
        File.WriteAllText(Path.Combine(directory, "extension.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["id"] = id, ["version"] = "1.0.0",
            ["hostApi"] = new { minimum = "1.0", maximumExclusive = "2.0" },
            ["displayNames"] = new Dictionary<string, string> { ["zh-Hans"] = "测试扩展" },
            ["runtime"] = new { protocol = 1, kind = "dotnet", entry = "MabiTool.ExtensionFixture.dll", arguments = new[] { mode } },
            ["permissions"] = permissions ?? [],
            ["network"] = hosts is null ? null : new { hosts },
            ["commands"] = commands ? new[] { new { id = "reset", labels = new Dictionary<string, string> { ["zh-Hans"] = "清空" } } } : null
        }.Where(x => x.Value is not null).ToDictionary()));
        await installer.LoadDevelopmentAsync(directory, false, CancellationToken.None);
        if (grantAll) await installer.SetGrantsAsync(id, (permissions ?? []).ToDictionary(x => x, _ => true), false, CancellationToken.None);
        await installer.SetEnabledAsync(id, true, false, CancellationToken.None);
        await runtime.ApplyAsync(id);
        return installer.Get(id, false);
    }

    private Task PublishMeasurementAsync(ulong revision) => hub.PublishVolatileAsync(new StateEvent("measurement-" + revision, "combat-measurement",
        "local", new(revision), "combat.measurement.changed", JsonSerializer.SerializeToElement(new { revision })), DateTimeOffset.UtcNow);

    private string[] Lines(string id) => runtime.Log(id).Read(id, 500).Lines.Select(x => x.Message).ToArray();

    private async Task WaitLogAsync(string id, string prefix, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!Lines(id).Any(x => x.StartsWith(prefix, StringComparison.Ordinal) || x.Contains(prefix, StringComparison.Ordinal)))
        {
            if (watch.Elapsed > (timeout ?? TimeSpan.FromSeconds(30)))
                throw new TimeoutException("扩展日志没有出现 " + prefix + "：" + string.Join(" | ", Lines(id)));
            await Task.Delay(50);
        }
    }

    private async Task<ExtensionRuntimeState> WaitStateAsync(string id, Func<ExtensionRuntimeState, bool> condition, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (runtime.State(id) is { } state && condition(state)) return state;
            if (watch.Elapsed > (timeout ?? TimeSpan.FromSeconds(30)))
                throw new TimeoutException("扩展状态没有达到预期：" + runtime.State(id) + " | " + string.Join(" | ", Lines(id)));
            await Task.Delay(50);
        }
    }

    private void AssertNoProcessLeft()
    {
        int[] ids;
        lock (processes) ids = processes.ToArray();
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                Assert.True(process.WaitForExit(5000), "扩展进程 " + id + " 没有退出");
            }
            catch (ArgumentException) { }
        }
    }
}
