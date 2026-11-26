using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.LifeVision.Worker;
using Xunit;

namespace MabiTool.LifeVision.Tests;

/// <summary>
/// Worker 协议（进程内）：测试自建命名管道服务端充当宿主，按真实握手连接 <see cref="WorkerPeerConnection"/>，
/// 假截图源按序返回夹具帧、假时钟推进虚拟时间；不启动进程、不接触窗口。
/// 环境变量是进程级状态，本类的测试顺序执行，其他测试类不使用它们。
/// </summary>
public sealed class LifeVisionApplicationTests
{
    private const string Request = "0123456789abcdef0123456789abcdef", Run = "fedcba9876543210fedcba9876543210";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private sealed class VirtualClock : IVisionClock
    {
        private readonly object gate = new();
        private double now;
        private readonly List<double> delays = [];
        public double[] Delays() { lock (gate) return [.. delays]; }
        public double NowMilliseconds { get { lock (gate) return now; } }
        public void Advance(double milliseconds) { lock (gate) now += milliseconds; }
        public async Task DelayAsync(double milliseconds, CancellationToken cancellationToken)
        {
            lock (gate) delays.Add(milliseconds);
            // 真实地让出一小段时间，避免虚拟时钟下的循环空转占满 CPU。
            await Task.Delay(1, cancellationToken);
            Advance(milliseconds);
        }
    }

    private sealed class FakeSource(VirtualClock clock, RgbImage[] frames, double captureMilliseconds = 12) : IFrameSource
    {
        public int Calls;
        public Func<int, Exception?> Failure { get; init; } = _ => null;
        public TaskCompletionSource? Stuck { get; init; }
        public LifeVisionWatchArguments? Binding;

        public async ValueTask<RgbImage> CaptureAsync(LifeVisionWatchArguments binding, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref Calls);
            Binding = binding;
            if (Stuck is not null) await Stuck.Task.WaitAsync(cancellationToken);
            if (Failure(call) is { } error) throw error;
            clock.Advance(captureMilliseconds);
            return frames[(call - 1) % frames.Length];
        }

        public void Dispose() { }
    }

    /// <summary>测试里的宿主端：服务端管道、握手与帧读写。</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly NamedPipeServerStream server;
        private ulong sent;

        private Host(NamedPipeServerStream server) => this.server = server;

        public WorkerPeerConnection Peer { get; private set; } = null!;

        public static async Task<Host> StartAsync()
        {
            var name = "mabitool-worker-" + Guid.NewGuid().ToString("N");
            var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            using var self = Process.GetCurrentProcess();
            Environment.SetEnvironmentVariable("MABITOOL_WORKER_PIPE", name);
            Environment.SetEnvironmentVariable("MABITOOL_WORKER_TOKEN", token);
            Environment.SetEnvironmentVariable("MABITOOL_WORKER_HOST_PID", self.Id.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("MABITOOL_WORKER_HOST_START", self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var connecting = WorkerPeerConnection.ConnectFromEnvironmentAsync(LifeVisionProfiles.WorkerId);
            using var deadline = new CancellationTokenSource(Wait);
            await server.WaitForConnectionAsync(deadline.Token);
            var hello = await WorkerProtocol.ReadAsync(server, deadline.Token);
            Assert.Equal("hello", hello.Kind);
            Assert.Equal(LifeVisionProfiles.WorkerId, hello.Payload.GetProperty("workerId").GetString());
            Assert.Equal(token, hello.Payload.GetProperty("token").GetString());
            // ready 先于 Worker 一侧的握手完成：ConnectFromEnvironmentAsync 在收到 ready 后才返回。
            var host = new Host(server);
            await host.SendAsync("ready", new { workerId = LifeVisionProfiles.WorkerId });
            host.Peer = await connecting.WaitAsync(Wait);
            return host;
        }

        public Task SendAsync(string kind, object payload, bool scoped = false)
        {
            var element = JsonSerializer.SerializeToElement(payload, WorkerProtocol.Json);
            var frame = new WorkerFrame(1, (++sent).ToString(CultureInfo.InvariantCulture), scoped ? Request : null, kind,
                scoped ? Run : null, scoped ? "7" : null, scoped ? "1" : null, element);
            return WorkerProtocol.WriteAsync(server, frame);
        }

        public Task SendRequestAsync(LifeVisionWatchArguments arguments) =>
            SendAsync("request", new { method = LifeVisionProfiles.WatchMethod, arguments }, scoped: true);

        public async Task<WorkerFrame> ReadAsync()
        {
            using var deadline = new CancellationTokenSource(Wait);
            return await WorkerProtocol.ReadAsync(server, deadline.Token);
        }

        /// <summary>发 ping 并读到 pong，返回 pong 之前收到的其他帧（观察循环已结束时为空）。</summary>
        public async Task<List<WorkerFrame>> PingAsync()
        {
            await SendAsync("ping", new { });
            var frames = new List<WorkerFrame>();
            while (true)
            {
                var frame = await ReadAsync();
                if (frame.Kind == "pong") return frames;
                frames.Add(frame);
            }
        }

        /// <summary>只关闭宿主端的管道，模拟宿主退出。</summary>
        public ValueTask CloseAsync() => server.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            await Peer.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static LifeVisionWatchArguments Arguments(int clientWidth = 1280, int clientHeight = 960, int period = 100, int processId = 1234,
        string profile = LifeVisionProfiles.TwFishing1280x960) =>
        new(new UInt64String(0x1A2B3C), processId, new DateTimeOffset(2026, 11, 25, 1, 2, 3, TimeSpan.Zero), new PhysicalWindowRect(10, 20, clientWidth, clientHeight),
            96, period, profile);

    private static RgbImage[] FixtureFrames() =>
        [TestAssets.Fixture("fishing-mode-confirm-user.png"), TestAssets.Fixture("fishing-mounted-current.png")];

    private static FishingObservation Observation(WorkerFrame frame)
    {
        Assert.Equal("event", frame.Kind);
        return frame.Payload.GetProperty("observation").Deserialize<FishingObservation>(ContractJson.Options)!;
    }

    private static async Task<(Host Host, Task Application)> StartAsync(IFrameSource source, VirtualClock clock, string? assetRoot = null)
    {
        var host = await Host.StartAsync();
        return (host, LifeVisionApplication.RunAsync(source, host.Peer, clock, assetRoot));
    }

    private static async Task FinishAsync(Host host, Task application)
    {
        await host.SendAsync("shutdown", new { });
        await application.WaitAsync(Wait);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task WatchStreamsObservationsAtThePeriodMinusCaptureAndRecognitionTime()
    {
        var clock = new VirtualClock();
        var source = new FakeSource(clock, FixtureFrames());
        var (host, application) = await StartAsync(source, clock);
        await host.SendRequestAsync(Arguments());
        var accepted = await host.ReadAsync();
        Assert.Equal("accepted", accepted.Kind);
        Assert.Equal((Request, Run, "7", "1"), (accepted.RequestId, accepted.RunId, accepted.BindingRevision, accepted.FencingToken));
        var observations = new List<FishingObservation>();
        for (var index = 0; index < 4; index++)
        {
            var frame = await host.ReadAsync();
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(frame.Payload).Length < 2048, "事件正文只含类型化观察，不含帧像素");
            Assert.Equal((Request, Run), (frame.RequestId, frame.RunId));
            observations.Add(Observation(frame));
        }
        Assert.Equal(["1", "2", "3", "4"], observations.Select(item => item.FrameId.ToString()));
        // 第 1、3 帧是弹窗夹具，第 2、4 帧是骑乘夹具。
        Assert.All(observations.Where((_, index) => index % 2 == 0), item => { Assert.Equal("focused", item.Dialog!.Kind); Assert.False(item.DismountVisible); });
        Assert.All(observations.Where((_, index) => index % 2 == 1), item => { Assert.True(item.DismountVisible); Assert.Null(item.Dialog); });
        // 每帧截图耗时 12 毫秒（假源推进时钟），识别耗时在虚拟时钟下为 0；周期 100 毫秒里扣除后等待 88。
        Assert.All(observations, item => { Assert.Equal(12, item.CaptureMilliseconds); Assert.Equal(0, item.RecognizeMilliseconds); });
        Assert.Equal([12, 112, 212, 312], observations.Select(item => item.CapturedAtMilliseconds));
        Assert.All(clock.Delays().Take(3), delay => Assert.Equal(88, delay));
        Assert.Equal(Arguments(), source.Binding);
        await FinishAsync(host, application);
    }

    [Fact]
    public async Task CancelStopsTheLoopAndAnswersCancelledWithNoLaterObservation()
    {
        var clock = new VirtualClock();
        var (host, application) = await StartAsync(new FakeSource(clock, FixtureFrames()), clock);
        await host.SendRequestAsync(Arguments());
        Assert.Equal("accepted", (await host.ReadAsync()).Kind);
        Assert.Equal("event", (await host.ReadAsync()).Kind);
        await host.SendAsync("cancel", new { }, scoped: true);
        WorkerFrame frame;
        do frame = await host.ReadAsync(); while (frame.Kind == "event");
        Assert.Equal("cancelled", frame.Kind);
        Assert.Equal((Request, Run), (frame.RequestId, frame.RunId));
        // cancelled 之后循环已结束：下一帧就是 pong，中间没有迟到的观察。
        Assert.Empty(await host.PingAsync());
        // 取消后可以再受理新的观察。
        await host.SendRequestAsync(Arguments());
        Assert.Equal("accepted", (await host.ReadAsync()).Kind);
        await FinishAsync(host, application);
    }

    [Theory]
    [InlineData("capture.sourceFailed")]
    [InlineData("input.bindingChanged")]
    [InlineData("capture.frameInvalid")]
    public async Task SourceFailureEndsTheRunWithTheContractCodeAndNoMoreEvents(string code)
    {
        var clock = new VirtualClock();
        var source = new FakeSource(clock, FixtureFrames()) { Failure = call => call == 3 ? new WorkerException(code) : null };
        var (host, application) = await StartAsync(source, clock);
        await host.SendRequestAsync(Arguments());
        Assert.Equal("accepted", (await host.ReadAsync()).Kind);
        Assert.Equal("event", (await host.ReadAsync()).Kind);
        Assert.Equal("event", (await host.ReadAsync()).Kind);
        var failure = await host.ReadAsync();
        Assert.Equal("error", failure.Kind);
        Assert.Equal(code, failure.Payload.GetProperty("code").GetString());
        Assert.Empty(await host.PingAsync());
        Assert.Equal(3, source.Calls);
        await FinishAsync(host, application);
    }

    [Fact]
    public async Task UnexpectedSourceExceptionIsReportedAsCaptureSourceFailed()
    {
        var clock = new VirtualClock();
        var source = new FakeSource(clock, FixtureFrames()) { Failure = call => call == 1 ? new InvalidOperationException("boom") : null };
        var (host, application) = await StartAsync(source, clock);
        await host.SendRequestAsync(Arguments());
        Assert.Equal("accepted", (await host.ReadAsync()).Kind);
        var failure = await host.ReadAsync();
        Assert.Equal("error", failure.Kind);
        Assert.Equal("capture.sourceFailed", failure.Payload.GetProperty("code").GetString());
        await FinishAsync(host, application);
    }

    [Theory]
    [InlineData(1920, 1080, 100, 1234, LifeVisionProfiles.TwFishing1280x960, "life.fishing.layoutUnsupported")]
    [InlineData(1280, 960, 49, 1234, LifeVisionProfiles.TwFishing1280x960, "worker.frameInvalid")]
    [InlineData(1280, 960, 501, 1234, LifeVisionProfiles.TwFishing1280x960, "worker.frameInvalid")]
    [InlineData(1280, 960, 100, 0, LifeVisionProfiles.TwFishing1280x960, "worker.frameInvalid")]
    [InlineData(1280, 960, 100, 1234, "other-profile", "worker.frameInvalid")]
    public async Task InvalidArgumentsAreRejectedBeforeAcceptingAndTheWorkerStaysUsable(int width, int height, int period, int processId, string profile, string code)
    {
        var clock = new VirtualClock();
        var source = new FakeSource(clock, FixtureFrames());
        var (host, application) = await StartAsync(source, clock);
        await host.SendRequestAsync(Arguments(width, height, period, processId, profile));
        var failure = await host.ReadAsync();
        Assert.Equal("error", failure.Kind);
        Assert.Equal(code, failure.Payload.GetProperty("code").GetString());
        Assert.Equal(0, source.Calls);
        // 校验失败不占用 Worker：随后的有效请求照常受理。
        await host.SendRequestAsync(Arguments());
        Assert.Equal("accepted", (await host.ReadAsync()).Kind);
        await FinishAsync(host, application);
    }

    [Fact]
    public async Task OnlyOneWatchIsAcceptedAtATime()
    {
        var clock = new VirtualClock();
        var (host, application) = await StartAsync(new FakeSource(clock, FixtureFrames()), clock);
        await host.SendRequestAsync(Arguments());
        Assert.Equal("accepted", (await host.ReadAsync()).Kind);
        await host.SendRequestAsync(Arguments());
        WorkerFrame frame;
        do frame = await host.ReadAsync(); while (frame.Kind == "event");
        Assert.Equal("error", frame.Kind);
        Assert.Equal("worker.frameInvalid", frame.Payload.GetProperty("code").GetString());
        await FinishAsync(host, application);
    }

    [Fact]
    public async Task MissingTemplatesAnswerComponentMissingBeforeAccepting()
    {
        var clock = new VirtualClock();
        var source = new FakeSource(clock, FixtureFrames());
        var (host, application) = await StartAsync(source, clock, Path.Combine(AppContext.BaseDirectory, "no-such-assets"));
        await host.SendRequestAsync(Arguments());
        var failure = await host.ReadAsync();
        Assert.Equal("error", failure.Kind);
        Assert.Equal("life.fishing.componentMissing", failure.Payload.GetProperty("code").GetString());
        Assert.Equal(0, source.Calls);
        await FinishAsync(host, application);
    }

    [Fact]
    public async Task PingAndCancelAreAnsweredWhileTheCaptureIsStuck()
    {
        var clock = new VirtualClock();
        var source = new FakeSource(clock, FixtureFrames()) { Stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (host, application) = await StartAsync(source, clock);
        await host.SendRequestAsync(Arguments());
        Assert.Equal("accepted", (await host.ReadAsync()).Kind);
        while (Volatile.Read(ref source.Calls) == 0) await Task.Delay(5);
        // 截图卡住：没有任何观察，但心跳照常回应，取消也能结束运行。
        Assert.Empty(await host.PingAsync());
        await host.SendAsync("cancel", new { }, scoped: true);
        Assert.Equal("cancelled", (await host.ReadAsync()).Kind);
        Assert.Empty(await host.PingAsync());
        await FinishAsync(host, application);
    }

    [Fact]
    public async Task ClosingThePipeEndsTheApplicationAndItsRun()
    {
        var clock = new VirtualClock();
        var (host, application) = await StartAsync(new FakeSource(clock, FixtureFrames()), clock);
        await host.SendRequestAsync(Arguments());
        Assert.Equal("accepted", (await host.ReadAsync()).Kind);
        Assert.Equal("event", (await host.ReadAsync()).Kind);
        // 宿主关闭管道：Worker 自行退出，不留下观察循环。
        await host.CloseAsync();
        await application.WaitAsync(Wait);
        await host.DisposeAsync();
    }
}
