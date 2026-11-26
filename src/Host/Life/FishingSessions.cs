using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Life.Application;
using MabiTool.Modules.Life.Domain;

namespace MabiTool.Host.Life;

/// <summary>一次钓鱼运行的识别与输入两端；释放时先结束识别进程，输入租约由 <see cref="ReleaseInput"/> 单独交还。</summary>
public interface IFishingSession : IAsyncDisposable
{
    IFishingVision Vision { get; }
    IFishingInput Input { get; }
    /// <summary>释放本运行按下的键与触点并交还租约；可重复调用。</summary>
    void ReleaseInput();
}

/// <summary>
/// 运行依赖由宿主装配，HTTP 请求不能指定进程路径、窗口或输入设备。<see cref="UnavailableReason"/> 非空时钓鱼能力不可用。
/// </summary>
public interface IFishingSessionFactory
{
    string? UnavailableReason { get; }
    Task<IFishingSession> CreateAsync(GameWindowBinding binding, ResourceLeaseSnapshot lease, string runId, CancellationToken cancellationToken);
}

/// <summary>正式宿主的装配：识别由 <c>life-vision</c> Worker 进程提供，输入走后台输入通道（W/S/空格与触控 Tap）。</summary>
public sealed class WindowsFishingSessionFactory(GameControlContext controls, Func<WorkerProcessOptions?> workerOptions, TimeProvider clock)
    : IFishingSessionFactory
{
    public const string ExecutableVariable = "MABITOOL_LIFE_VISION_EXECUTABLE";
    /// <summary>首帧期限覆盖 Worker 启动、模板加载与第一次截图。</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    public string? UnavailableReason => !OperatingSystem.IsWindows() ? "platform.unsupported"
        : workerOptions() is null ? LifeFishingErrors.ComponentMissing : null;

    /// <summary>由环境变量给出的 Worker 可执行文件解析启动选项；变量未设置或文件不存在时为空，能力表写明组件缺失。</summary>
    public static Func<WorkerProcessOptions?> FromEnvironment(Func<string> assetRoot)
    {
        return () =>
        {
            if (!OperatingSystem.IsWindows()) return null;
            var executable = Environment.GetEnvironmentVariable(ExecutableVariable);
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable)) return null;
            LocalDiscoverySecurity.RejectLinks(executable);
            var assets = assetRoot();
            Directory.CreateDirectory(assets);
            return new(executable, Path.GetDirectoryName(executable)!, assets, LifeVisionProfiles.WorkerId) { StartupTimeout = StartupTimeout };
        };
    }

    [SupportedOSPlatform("windows")]
    public async Task<IFishingSession> CreateAsync(GameWindowBinding binding, ResourceLeaseSnapshot lease, string runId, CancellationToken cancellationToken)
    {
        var options = workerOptions() ?? throw new OperationException("capability.unavailable", "未找到钓鱼识别组件");
        var input = new FishingInputAdapter(controls.Background, controls.Leases, lease, binding.Session, clock);
        WorkerSession? worker = null;
        try
        {
            // Worker 故障清理先于进程终止：先撤销本运行的输入，迟到的动作不能落地。
            worker = await WorkerSession.StartAsync(options, _ => input.Release(), cancellationToken);
            var arguments = new LifeVisionWatchArguments(binding.Session.WindowIdentity, binding.Session.ProcessId, binding.Session.ProcessStartTime,
                binding.Bounds, (int)Math.Round(binding.DpiX), 100, LifeVisionProfiles.TwFishing1280x960);
            var call = await worker.BeginAsync(new(LifeVisionProfiles.WatchMethod,
                JsonSerializer.SerializeToElement(arguments, ContractJson.Options), runId, binding.Session.BindingRevision.ToString(),
                lease.FencingToken.ToString()), cancellationToken);
            return new FishingSession(new FishingVisionSession(worker, call), input);
        }
        catch
        {
            if (worker is not null) await worker.DisposeAsync();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class FishingSession(FishingVisionSession vision, FishingInputAdapter input) : IFishingSession
    {
        public IFishingVision Vision => vision;
        public IFishingInput Input => input;
        public void ReleaseInput() => input.Release();
        public ValueTask DisposeAsync() => vision.DisposeAsync();
    }
}

/// <summary>把 Worker 的事件流读成观察；超过期限没有帧即判定截图来源失效，Worker 终态或进程退出同样结束。</summary>
[SupportedOSPlatform("windows")]
public sealed class FishingVisionSession(WorkerSession worker, WorkerCall call) : IFishingVision, IAsyncDisposable
{
    public async ValueTask<FishingObservation> NextAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (await call.Events.WaitToReadAsync(deadline.Token).ConfigureAwait(false))
                while (call.Events.TryRead(out var frame))
                    if (frame.Kind == "event" && frame.Payload.TryGetProperty("observation", out var value))
                    {
                        try { return value.Deserialize<FishingObservation>(ContractJson.Options) ?? throw new JsonException(); }
                        catch (JsonException error) { throw new FishingVisionException("capture.frameInvalid", error); }
                    }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new FishingVisionException("capture.sourceFailed"); }
        throw new FishingVisionException(await TerminalCodeAsync().ConfigureAwait(false));
    }

    /// <summary>事件流结束：Worker 回了错误帧就用它的错误码，被取消、断连或崩溃都是截图来源失效。</summary>
    private async Task<string> TerminalCodeAsync()
    {
        try
        {
            var terminal = await call.Completion.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (terminal.Kind == "error" && terminal.Payload.TryGetProperty("code", out var code) && code.GetString() is { Length: <= 64 } text &&
                ApiErrors.Definitions.Any(error => error.Code == text)) return text;
        }
        catch (Exception error) when (error is TimeoutException or WorkerException or OperationCanceledException) { }
        return "capture.sourceFailed";
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!call.Completion.IsCompleted) await worker.CancelAsync(call).ConfigureAwait(false); }
        catch (Exception error) when (error is WorkerException or OperationCanceledException or TimeoutException) { }
        finally { await worker.DisposeAsync().ConfigureAwait(false); }
    }
}

/// <summary>
/// 钓鱼输入：抛竿 W/S 60 毫秒、等待 300 毫秒、空格 80 毫秒分两次分发，使“第二步没有发出”与“抛竿已发出”可以区分，
/// 取消发生在等待期间时空格不会发出；收竿空格 80 毫秒、离开 W 120 毫秒；弹窗与下坐骑为触控点按。
/// </summary>
public sealed class FishingInputAdapter(GameBackgroundInputBroker broker, ResourceLeaseCoordinator leases, ResourceLeaseSnapshot lease,
    GameSession session, TimeProvider clock) : IFishingInput
{
    public const string Owner = "life";
    private const ushort KeyW = 0x57, KeyS = 0x53, KeySpace = 0x20;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task PerformAsync(FishingAction action, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case FishingActionKind.Cast:
                if (action.Move != FishingKey.None)
                {
                    await ExecuteAsync(Key(action.Move == FishingKey.W ? KeyW : KeyS, 60), cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(300), clock, cancellationToken).ConfigureAwait(false);
                }
                await ExecuteAsync(Key(KeySpace, 80), cancellationToken).ConfigureAwait(false);
                break;
            case FishingActionKind.Reel: await ExecuteAsync(Key(KeySpace, 80), cancellationToken).ConfigureAwait(false); break;
            case FishingActionKind.Leave: await ExecuteAsync(Key(KeyW, 120), cancellationToken).ConfigureAwait(false); break;
            default:
                var point = action.Point ?? throw new FishingInputException("input.actionInvalid", FishingInputFailure.Rejected);
                await ExecuteAsync(new(BackgroundActionKind.Tap, [new PhysicalScreenPoint(point.X, point.Y)]), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public void RenewLease()
    {
        try { leases.Renew(lease.Id, lease.FencingToken, LeaseDuration); }
        catch (ResourceLeaseException) { throw new FishingInputException("input.leaseInvalid", FishingInputFailure.Fatal); }
    }

    /// <summary>释放键与触点并交还租约；先释放输入再交还，租约已不存在时也不抛出。</summary>
    public void Release()
    {
        try { broker.ReleaseRun(lease.Id); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private static GameBackgroundAction Key(ushort key, int hold) => new(BackgroundActionKind.Key, [], hold, VirtualKey: key);

    private async Task ExecuteAsync(GameBackgroundAction action, CancellationToken cancellationToken)
    {
        var request = new GameBackgroundRequest(Owner, lease.Id, lease.FencingToken, session.Id, session.BindingRevision,
            session.WindowIdentity, [action], IntervalMilliseconds: 0);
        try { await Task.Run(() => broker.Execute(request, cancellationToken), cancellationToken).ConfigureAwait(false); }
        catch (GameInputException error) { throw new FishingInputException(error.Code, Classify(error.Code), error); }
    }

    /// <summary>分发前条件不满足的拒绝可以重试；设备在分发过程中失败时效果未知；其余（租约、绑定、窗口、设备能力）使本次运行无法继续。</summary>
    internal static FishingInputFailure Classify(string code) => code switch
    {
        "input.backgroundRejected" or "input.pointOutsideWindow" or "input.actionInvalid" or "input.planTooLong" => FishingInputFailure.Rejected,
        "input.deviceFailure" => FishingInputFailure.Unknown,
        _ => FishingInputFailure.Fatal
    };
}
