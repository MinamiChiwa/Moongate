using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Life.Application;
using MabiTool.Modules.Life.Domain;

namespace MabiTool.Host.Life;

/// <summary>
/// 钓鱼运行的受理、执行与取消。宿主持有运行：前端刷新或同时打开多个前端不会产生第二个工作流；
/// 取消、停止与紧急停止先撤销输入，再结束识别进程，最后发布终态。
/// </summary>
public sealed class FishingRunOperations(OperationStore operations, ModuleRuntime modules, SystemInfo info, TimeProvider clock,
    GameControlContext controls, IFishingSessionFactory sessions, FishingRunnerOptions? runnerOptions = null) : BackgroundService
{
    public const int MinimumDurationSeconds = 60;
    public const int MaximumDurationSeconds = 24 * 60 * 60;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(1);
    private readonly object gate = new();
    private Job? current;
    private volatile bool accepting;

    private sealed class Job(string id, string runId, string owner)
    {
        public string Id { get; } = id;
        public string RunId { get; } = runId;
        public string Owner { get; } = owner;
        public CancellationTokenSource Stop { get; } = new();
        public FishingRunner? Runner { get; set; }
        public bool GracefulStop { get; set; }
        public string? StopReason { get; set; }
        public bool Finished { get; set; }
    }

    internal (bool Running, bool Accepting) DiagnosticState()
    {
        lock (gate) return (current is { Finished: false }, accepting);
    }

    /// <summary>能力状态与原因码：台服、客户端 1280×960、窗口未最小化、后台输入可用、识别组件存在。</summary>
    public FishingCapability Capability()
    {
        var binding = controls.Bindings.Recent;
        var reason = UnavailableReason(binding);
        return new(controls.Simulation, reason is null ? CapabilityStatus.Available : CapabilityStatus.Unavailable, reason ?? "available",
            ["region:TW", "client:1280x960", "window:not-minimized", "input:background", "component:life-vision"],
            binding?.Session.Id, binding?.Session.GameBuild, clock.GetUtcNow());
    }

    public IEnumerable<CapabilitySnapshot> CapabilitySnapshots(DateTimeOffset checkedAt)
    {
        var capability = Capability();
        yield return new("life.fishing", "life", capability.Status, capability.Status.KnownValue == CapabilityStatus.Available ? LifeVisionProfiles.WorkerId : null,
            capability.Status.KnownValue == CapabilityStatus.Available ? "1.0" : null, capability.GameBuild, capability.GameSessionId,
            capability.ReasonCode, checkedAt, []);
    }

    private string? UnavailableReason(GameWindowBinding? binding)
    {
        if (controls.Simulation) return "input.backgroundSimulation";
        if (!OperatingSystem.IsWindows()) return "platform.unsupported";
        if (sessions.UnavailableReason is { } component) return component;
        if (binding is null) return "input.bindingUnavailable";
        if (binding.Session.Region != "TW") return "region.unsupported";
        if (binding.Bounds.Width != LifeVisionProfiles.ClientWidth || binding.Bounds.Height != LifeVisionProfiles.ClientHeight)
            return LifeFishingErrors.LayoutUnsupported;
        return controls.Background.Availability();
    }

    private static string UnavailableMessage(string reason) => reason switch
    {
        LifeFishingErrors.ComponentMissing => "未找到钓鱼识别组件：程序文件夹里缺少 components\\life-vision",
        LifeFishingErrors.LayoutUnsupported => "钓鱼需要游戏客户区为 1280×960（4:3），请在游戏设置里调整分辨率",
        "region.unsupported" => "钓鱼只支持台服客户端",
        "input.windowMinimized" => "游戏窗口已最小化，请先还原窗口",
        "input.bindingUnavailable" => "请先选择游戏窗口",
        "platform.unsupported" => "钓鱼需要 Windows",
        "input.backgroundSimulation" => "模拟宿主不提供钓鱼",
        _ => "钓鱼当前不可用"
    };

    public async Task<OperationSnapshot> SubmitAsync(string principal, FishingRunRequest request, string? key, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.GameSessionId) || request.GameSessionId.Length > 128 ||
            request.DurationSeconds is { } seconds && (seconds < MinimumDurationSeconds || seconds > MaximumDurationSeconds))
            throw new OperationException("request.invalid", "钓鱼请求需要游戏会话，时长为 60 秒到 24 小时或留空");
        await admission.WaitAsync(ct);
        try
        {
            if (!accepting) throw new OperationException("provider.unavailable", "钓鱼执行器尚未启动或正在关闭");
            var capability = Capability();
            if (capability.Status.KnownValue != CapabilityStatus.Available)
                throw new OperationException("capability.unavailable", UnavailableMessage(capability.ReasonCode),
                    new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement(capability.ReasonCode) });
            bool idle;
            lock (gate) idle = current is null;
            var result = await operations.AdmitFishingAsync(principal, request, key, () => Resolve(request), info.InstanceId, idle, ct);
            if (result.Created)
            {
                var job = new Job(result.Snapshot.Id, result.Snapshot.Result!.Value.GetProperty("id").GetString()!, principal);
                lock (gate) current = job;
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理运行无法进入保留队列");
            }
            return result.Snapshot;
        }
        finally { admission.Release(); }
    }

    /// <summary>核对绑定并查看资源占用：被染色、合奏或其他后台输入占用时返回 409 与占用者。</summary>
    private GameSession Resolve(FishingRunRequest request)
    {
        var binding = controls.Bindings.Current;
        if (binding is null || binding.Session.Id != request.GameSessionId || binding.Session.BindingRevision != request.ExpectedBindingRevision)
            throw new OperationException("input.bindingChanged", "游戏绑定已变化，请重新选择当前会话");
        if (controls.Leases.FindConflict([GameBackgroundInputBroker.Resource, "activity-control:" + binding.Session.Id]) is { } occupied)
            throw new OperationException("resource.busy", "游戏输入已被占用", new Dictionary<string, JsonElement>
                { ["occupiedBy"] = JsonSerializer.SerializeToElement(occupied.OwnerId) });
        return binding.Session;
    }

    public FishingRun Get(string runId, string principal, bool owner) =>
        operations.FishingRunByOperation(operations.FishingOperationId(runId), principal, owner);

    /// <summary>优雅停止：离开钓鱼姿态后正常结束。运行已结束时返回 <c>life.fishing.runInactive</c>。</summary>
    public FishingRun Stop(string runId, string principal, bool owner)
    {
        var run = Get(runId, principal, owner);
        lock (gate)
        {
            if (current is not { Finished: false } job || job.RunId != runId || job.Stop.IsCancellationRequested)
                throw new OperationException(LifeFishingErrors.RunInactive, "钓鱼运行已结束或已取消");
            job.GracefulStop = true;
            job.Runner?.RequestStop();
        }
        return run;
    }

    /// <summary>取消运行：输入权限先撤销，数据库写入不处于停止路径。</summary>
    public async Task<OperationSnapshot> CancelAsync(string id, string principal, bool owner, string? reason, CancellationToken ct)
    {
        var operation = operations.Get(id, principal, owner);
        if (operation.Kind != OperationStore.FishingKind) return await operations.CancelAsync(id, principal, owner, reason, ct);
        if (reason is { Length: > 256 } || reason?.Any(char.IsControl) == true)
            throw new OperationException("request.invalid", "取消原因最多 256 字符且不能包含控制字符");
        lock (gate)
            if (current is { } job && job.Id == id) StopJob(job, reason ?? "cancelled");
        return await operations.CancelAsync(id, principal, owner, reason, ct);
    }

    /// <summary>本机紧急停止：撤销输入租约已由调用方完成，这里取消运行并让识别进程随之结束。</summary>
    public void EmergencyStop()
    {
        lock (gate)
            if (current is { } job) StopJob(job, "emergency-stop");
    }

    private static void StopJob(Job job, string reason)
    {
        job.StopReason ??= reason;
        job.Stop.Cancel();
    }

    public override Task StartAsync(CancellationToken ct)
    {
        accepting = true;
        return base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            using var stop = stoppingToken.Register(() => { lock (gate) StopJob(job, "host-stopped"); });
            try { await ExecuteJobAsync(job); }
            catch (Exception error)
            {
                var code = error switch
                {
                    OperationException e => e.Code, ResourceLeaseException e => e.Code, ModuleRuntimeException e => e.Code,
                    WorkerException => "capture.sourceFailed", _ => "operation.executionFailed"
                };
                try
                {
                    await operations.UpdateFishingAsync(job.Id, run => run with { Stop = new(job.StopReason ?? code, true, FishingPoseState.Unconfirmed, clock.GetUtcNow()) },
                        job.Stop.IsCancellationRequested ? OperationState.Cancelled : OperationState.Interrupted,
                        job.Stop.IsCancellationRequested ? null : Problem(code, "钓鱼运行中断", job.Id));
                }
                catch { accepting = false; queue.Writer.TryComplete(); }
            }
            finally
            {
                lock (gate) { job.Finished = true; if (ReferenceEquals(current, job)) current = null; }
                job.Stop.Dispose();
            }
        }
    }

    private async Task ExecuteJobAsync(Job job)
    {
        var initial = await operations.BeginFishingAsync(job.Id);
        if (initial is null || job.Stop.IsCancellationRequested)
        {
            await operations.UpdateFishingAsync(job.Id, run => run with { Stop = new(job.StopReason ?? "cancelled", true, FishingPoseState.Unconfirmed, clock.GetUtcNow()) },
                OperationState.Cancelled);
            return;
        }
        var run = operations.FishingRunByOperation(job.Id, "owner", true);
        await using var demand = await modules.AcquireAsync("life", job.Id, job.Stop.Token);
        var binding = controls.Bindings.Current;
        if (binding is null || binding.Session != run.BindingSnapshot)
            throw new OperationException("input.bindingChanged", "游戏绑定已变化");
        var lease = controls.Leases.Acquire(new(FishingInputAdapter.Owner, job.Id, binding.Session.Id, binding.Session.BindingRevision,
            [GameBackgroundInputBroker.Resource, "activity-control:" + binding.Session.Id], LeaseDuration));
        IFishingSession? session = null;
        FishingOutcome? outcome = null;
        Exception? failure = null;
        var progress = Channel.CreateBounded<FishingProgress>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        Task consume = Task.CompletedTask;
        try
        {
            session = await sessions.CreateAsync(binding, lease, run.Id, job.Stop.Token);
            var runner = new FishingRunner(new(run.DurationSeconds), session.Vision, session.Input, clock, value => progress.Writer.TryWrite(value), runnerOptions);
            lock (gate)
            {
                job.Runner = runner;
                if (job.GracefulStop) runner.RequestStop();
            }
            consume = ConsumeProgressAsync(job.Id, progress.Reader);
            outcome = await runner.RunAsync(job.Stop.Token);
        }
        catch (OperationCanceledException) when (job.Stop.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            // 先撤销输入并交还租约，再结束识别进程，最后写终态：迟到的识别帧与动作不能在停止之后落地。
            session?.ReleaseInput();
            if (session is null) controls.Background.ReleaseRun(lease.Id);
            progress.Writer.TryComplete();
            try { await consume; } catch (Exception error) when (error is OperationException or StorageException) { }
            if (session is not null) await session.DisposeAsync();
        }
        if (failure is not null && !job.Stop.IsCancellationRequested) throw failure;
        outcome ??= new(FishingEnd.Cancelled, job.StopReason ?? "cancelled", null, FishingPoseState.Unconfirmed, false, false,
            new("cancelled", run.ClockStarted, run.ElapsedSeconds, run.Rounds, run.ConsecutiveRoundsWithoutHook, run.Reason));
        await PublishOutcomeAsync(job, outcome);
    }

    private async Task ConsumeProgressAsync(string id, ChannelReader<FishingProgress> reader)
    {
        await foreach (var value in reader.ReadAllAsync())
            await operations.UpdateFishingAsync(id, run => Apply(run, value));
    }

    private static FishingRun Apply(FishingRun run, FishingProgress progress) => run with
    {
        Phase = progress.Phase, ClockStarted = progress.ClockStarted, ElapsedSeconds = progress.ElapsedSeconds, Rounds = progress.Rounds,
        ConsecutiveRoundsWithoutHook = progress.ConsecutiveRoundsWithoutHook, Reason = progress.Reason
    };

    private async Task PublishOutcomeAsync(Job job, FishingOutcome outcome)
    {
        var cancelled = outcome.End == FishingEnd.Cancelled || job.Stop.IsCancellationRequested;
        var state = cancelled ? OperationState.Cancelled : outcome.End switch
        {
            FishingEnd.Succeeded => OperationState.Succeeded, FishingEnd.Failed => OperationState.Failed, _ => OperationState.Interrupted
        };
        var stopReason = cancelled ? job.StopReason ?? "cancelled" : outcome.StopReason;
        var stop = new FishingStop(stopReason, true, outcome.PoseLeft, clock.GetUtcNow());
        var problem = state is OperationState.Failed or OperationState.Interrupted && outcome.ProblemCode is { } code
            ? Problem(code, state == OperationState.Failed ? "钓鱼运行失败" : "钓鱼运行中断", job.Id) : null;
        var effect = outcome.EffectUnknown ? EffectStatus.Unknown : outcome.InputDispatched ? EffectStatus.InputDispatched : EffectStatus.None;
        await operations.UpdateFishingAsync(job.Id, run => Apply(run, outcome.Final) with { Stop = stop }, state, problem, effect);
    }

    private static ApiProblem Problem(string code, string title, string traceId) =>
        ApiErrors.Create(ApiErrors.Definitions.Any(error => error.Code == code) ? code : "operation.executionFailed", title, traceId);

    public override async Task StopAsync(CancellationToken ct)
    {
        accepting = false;
        lock (gate) if (current is { } active) StopJob(active, "host-stopped");
        await admission.WaitAsync(ct);
        try
        {
            accepting = false; queue.Writer.TryComplete();
            lock (gate) if (current is { } job) StopJob(job, "host-stopped");
        }
        finally { admission.Release(); }
        await base.StopAsync(ct);
    }
}
