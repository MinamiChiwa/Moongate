using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Platform.Windows;
using MabiTool.Providers.Tunnels;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class QuickTunnelTests
{
    [Theory]
    [InlineData(true, "room.tunnelComponentInvalid", 0)]
    [InlineData(false, "room.tunnelVersionMismatch", 1)]
    public async Task ComponentIdentityIsVerifiedBeforeTunnelStart(bool wrongHash, string expectedCode, int expectedStarts)
    {
        using var area = new Area(); var processes = new ControlledTunnelProcessFactory { Version = "2025.1.0" };
        var options = wrongHash ? area.Component with { Sha256 = new string('0', 64) } : area.Component;
        await using var tunnel = new QuickTunnelFactory(options, processes).Open(3132, Guid.NewGuid().ToString(), _ => Task.CompletedTask);
        var result = await tunnel.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoomTunnelState.Failed, result.State.KnownValue); Assert.Equal(expectedCode, result.Problem!.Code);
        Assert.Equal(expectedStarts, processes.Arguments.Count); Assert.Null(result.Endpoint);
    }

    [Fact]
    public async Task SplitOutputRequiresMatchingProbeAndThreeHealthFailuresWithdrawInvitation()
    {
        using var area = new Area(); var processes = new ControlledTunnelProcessFactory();
        var changes = new ConcurrentQueue<RoomTunnelSnapshot>(); var calls = 0; var epoch = Guid.NewGuid().ToString();
        var probe = new ControlledTunnelProbe((endpoint, expected, _) =>
        {
            Assert.Equal(epoch, expected); Assert.Equal("wss://valid-room.trycloudflare.com/ws", endpoint.AbsoluteUri);
            return Task.FromResult(Interlocked.Increment(ref calls) == 1);
        });
        await using var tunnel = new QuickTunnelFactory(area.Component, processes, probe,
            budgets: new(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(30))).Open(4141, epoch,
            value => { changes.Enqueue(value); return Task.CompletedTask; });
        processes.Tunnel.Output.Write("https://valid-room.trycloudflare.com.evil.invalid\n");
        processes.Tunnel.Error.Write("url https://valid-");
        processes.Tunnel.Error.Write("room.trycloudflare.com |\n");
        var ready = await tunnel.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoomTunnelState.Available, ready.State.KnownValue);
        Assert.Equal(new[] { "tunnel", "--url", "http://127.0.0.1:4141", "--no-autoupdate", "--protocol", "http2" },
            processes.Arguments.Last());
        await tunnel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, calls); Assert.Equal("room.tunnelHealthFailed", changes.Last().Problem!.Code);
        Assert.Null(changes.Last().Endpoint); Assert.True(processes.Tunnel.Terminated); Assert.True(processes.Tunnel.Disposed);
    }

    [Theory]
    [InlineData(false, "room.tunnelUnavailable")]
    [InlineData(true, "room.tunnelOutputExceeded")]
    public async Task MissingEndpointAndOversizedFragmentsReleaseProcess(bool oversized, string expectedCode)
    {
        using var area = new Area(); var processes = new ControlledTunnelProcessFactory();
        await using var tunnel = new QuickTunnelFactory(area.Component, processes,
            budgets: new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(50)))
            .Open(4141, Guid.NewGuid().ToString(), _ => Task.CompletedTask);
        if (oversized) processes.Tunnel.Output.Write(new string('x', 8193));
        var result = await tunnel.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedCode, result.Problem!.Code); Assert.True(processes.Tunnel.Disposed);
        Assert.Null(result.Endpoint);
    }

    [Fact]
    public async Task NewlyPublishedAddressRetriesUntilMatchingRoomBecomesReachable()
    {
        using var area = new Area(); var processes = new ControlledTunnelProcessFactory(); var calls = 0;
        var probe = new ControlledTunnelProbe((_, _, _) => Task.FromResult(Interlocked.Increment(ref calls) > 1));
        await using var tunnel = new QuickTunnelFactory(area.Component, processes, probe,
            budgets: new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)))
            .Open(4141, Guid.NewGuid().ToString(), _ => Task.CompletedTask);
        processes.Tunnel.Output.Write("https://new-room.trycloudflare.com\n");
        var result = await tunnel.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoomTunnelState.Available, result.State.KnownValue); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationWaitsForLateProbeAndCannotPublishItsInvitation()
    {
        using var area = new Area(); var processes = new ControlledTunnelProcessFactory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new ConcurrentQueue<RoomTunnelSnapshot>();
        var probe = new ControlledTunnelProbe(async (_, _, _) => { entered.TrySetResult(); await late.Task; return true; });
        var tunnel = new QuickTunnelFactory(area.Component, processes, probe).Open(4141, Guid.NewGuid().ToString(),
            value => { changes.Enqueue(value); return Task.CompletedTask; });
        processes.Tunnel.Output.Write("https://late-room.trycloudflare.com\n");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = tunnel.DisposeAsync().AsTask();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!processes.Tunnel.Disposed) await Task.Delay(10, deadline.Token);
            Assert.False(closing.IsCompleted);
        }
        finally { late.TrySetResult(); }
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tunnel.Ready);
        Assert.DoesNotContain(changes, value => value.State.KnownValue == RoomTunnelState.Available);
    }

    [Fact]
    public async Task ProcessReleaseFailuresSettleReadyAndAttemptAllCleanup()
    {
        using var area = new Area(); var processes = new ControlledTunnelProcessFactory();
        processes.Tunnel.FailTermination = true; processes.Tunnel.FailDisposal = true;
        var changes = new ConcurrentQueue<RoomTunnelSnapshot>();
        var tunnel = new QuickTunnelFactory(area.Component, processes,
            budgets: new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(50)))
            .Open(4141, Guid.NewGuid().ToString(), value => { changes.Enqueue(value); return Task.CompletedTask; });
        var failed = await tunnel.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoomTunnelState.Failed, failed.State.KnownValue); Assert.Null(failed.Endpoint);
        Assert.True(processes.Tunnel.Terminated); Assert.True(processes.Tunnel.Disposed);
        Assert.Equal(RoomTunnelState.Failed, changes.Last().State.KnownValue);
        await Assert.ThrowsAsync<AggregateException>(() => tunnel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        using var reopened = new FileStream(area.Component.ExecutablePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FailedStatusCallbackStillSettlesReady()
    {
        var tunnel = new QuickTunnelFactory(null).Open(4141, Guid.NewGuid().ToString(),
            _ => throw new InvalidOperationException("测试状态订阅失败"));
        Assert.Equal("room.tunnelNotConfigured", (await tunnel.Ready.WaitAsync(TimeSpan.FromSeconds(5))).Problem!.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tunnel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LateProbeFailureCannotLeaveCancellationWaitingForReady()
    {
        using var area = new Area(); var processes = new ControlledTunnelProcessFactory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ControlledTunnelProbe(async (_, _, _) =>
        { entered.TrySetResult(); await late.Task; throw new InvalidOperationException("测试迟到探测失败"); });
        var tunnel = new QuickTunnelFactory(area.Component, processes, probe).Open(4141, Guid.NewGuid().ToString(), _ => Task.CompletedTask);
        processes.Tunnel.Output.Write("https://late-failure.trycloudflare.com\n");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = tunnel.DisposeAsync().AsTask(); late.TrySetResult();
        Assert.Equal(RoomTunnelState.Failed, (await tunnel.Ready.WaitAsync(TimeSpan.FromSeconds(5))).State.KnownValue);
        await Assert.ThrowsAsync<AggregateException>(() => closing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(processes.Tunnel.Disposed);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task PrivateWindowsJobCapturesVersionAndReleasesLongRunningProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../..", "dotnet.exe"));
        await using (var version = WindowsOwnedChildProcess.Start(dotnet, ["--version"]))
        {
            var output = await version.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await version.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Matches(@"\d+\.\d+\.\d+", output);
        }
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var child = WindowsOwnedChildProcess.Start(shell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.WriteLine('ready'); while ($true) { Start-Sleep -Milliseconds 100 }"]);
        Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await child.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(child.HasExited);
    }

    internal sealed class Area : IDisposable
    {
        public string Root { get; } = Path.Combine(FindRoot(), "artifacts", "tmp", "tunnel-tests", Guid.NewGuid().ToString("N"));
        public TunnelComponentOptions Component { get; }
        public Area()
        {
            Directory.CreateDirectory(Root); var path = Path.Combine(Root, "component.bin");
            var bytes = Encoding.UTF8.GetBytes("已固定的测试组件"); File.WriteAllBytes(path, bytes);
            Component = new(path, Convert.ToHexString(SHA256.HashData(bytes)), "2026.10.0");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        internal static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("测试目录缺少项目根");
        }
    }
}
