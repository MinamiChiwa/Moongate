using System.Diagnostics;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace MabiTool.ObservationDebugTool;

/// <summary>
/// 单个场景独占的进程内 Host：启动、绑定、选择网卡、建立共享捕获需求，结束时按固定顺序释放并记录
/// Worker 退出、订阅数与活动数。
/// </summary>
public sealed class ObservationHost : IAsyncDisposable
{
    private readonly IGameEnvironment environment;
    private readonly ScenarioContext context;
    private readonly CancellationTokenSource samplerStop = new();
    private readonly Dictionary<int, Process> processHandles = [];
    private readonly object processGate = new();
    private Action? onChanged;
    private Task sampler = Task.CompletedTask;
    private bool cleaned;
    private MabiToolClient? client;

    private ObservationHost(IGameEnvironment environment, ScenarioContext context, WebApplication app, string token)
    { this.environment = environment; this.context = context; App = app; Token = token; }

    public WebApplication App { get; }
    public string Token { get; }
    public GameObservationService? Observations { get; private set; }
    public string? Owner { get; private set; }
    public BoundGame? Game { get; private set; }
    public string? ChannelId { get; private set; }
    public GameObservationSubscription? Demand { get; private set; }
    public Dictionary<string, double> Phases { get; } = [];
    public MemoryPeaks Memory { get; } = new();
    public string? MemoryTraceDirectory { get; private init; }

    /// <summary>
    /// 报告 memory 字段：外部 250 ms 采样的最大值与最后值（含布局准备窗口），以及 Worker 内存轨迹按窗口拆分的
    /// 布局准备瞬时峰值、运行期峰值、结束值、托管堆与非托管估计；内存预算按 workerTrace.Runtime 判定。
    /// </summary>
    public object MemoryReport() => new
    {
        Memory.HostWorkingBytes, Memory.HostPrivateBytes, Memory.WorkerWorkingBytes, Memory.WorkerPrivateBytes,
        Memory.ProcessTreeWorkingBytes, Memory.ProcessTreePrivateBytes,
        Memory.LastHostPrivateBytes, Memory.LastWorkerPrivateBytes, Memory.LastWorkerWorkingBytes,
        WorkerTrace = MemoryTraceDirectory is null ? null : WorkerMemoryTraceFiles.Summarize(MemoryTraceDirectory),
    };
    public SharedObservationDiagnostics Shared { get; } = new();
    public List<string> CleanupErrors { get; } = [];
    public string Stage { get; set; } = "host.start";
    public int FinalSubscribers { get; private set; } = -1;
    public int FinalActivities { get; private set; } = -1;
    public string FinalState { get; private set; } = "unknown";
    public bool WorkersExited { get; private set; }
    public int WorkersObserved { get; private set; }

    /// <summary>storage 为 true 时在场景私有目录建立便携数据根，库存与 SDK 查询需要持久存储。</summary>
    public static ObservationHost Create(IGameEnvironment environment, ScenarioContext context, bool storage)
    {
        var assets = Path.Combine(context.ScratchDirectory, "worker-assets");
        LocalDiscoverySecurity.EnsureDirectory(context.ScratchDirectory); LocalDiscoverySecurity.EnsureDirectory(assets);
        var token = LocalAuthService.NewSecret();
        StorageOptions? options = storage ? new(Path.Combine(context.ScratchDirectory, "portable", "bootstrap"), true,
            PortableRoot: Path.Combine(context.ScratchDirectory, "portable")) : null;
        var trace = Path.Combine(context.ScratchDirectory, "worker-memory");
        LocalDiscoverySecurity.EnsureDirectory(trace);
        var host = new ObservationHost(environment, context, environment.BuildHost(token, assets, options, trace), token) { MemoryTraceDirectory = trace };
        context.Track(host);
        return host;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await App.StartAsync(ct);
        Phases["hostReadyMs"] = context.ElapsedMs;
        var service = App.Services.GetRequiredService<GameObservationService>();
        Observations = service;
        onChanged = () => Shared.Capture(service.Snapshot(), context.ElapsedMs);
        service.Changed += onChanged; onChanged();
        Owner = App.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session.ClientId;
        Stage = "game.select";
        Game = await environment.BindAsync(App, Owner, ct);
        Phases["bindingReadyMs"] = context.ElapsedMs;
        Stage = "capture.channels";
        ChannelId = await environment.SelectChannelAsync(service, Owner, ct);
        Phases["setupMs"] = context.ElapsedMs;
    }

    /// <summary>建立共享捕获需求（即音乐需求），等待捕获运行后开始资源采样。</summary>
    public async Task StartCaptureAsync(CancellationToken ct, Stopwatch? sample = null)
    {
        var service = Observations ?? throw new InvalidOperationException("host.notStarted");
        Stage = "capture.start";
        Demand = await service.StartAsync(Owner!, new(ChannelId!, Game!.BindingRevision, environment.CaptureProfile),
            Guid.NewGuid().ToString("N"), GameObservationService.ETag(service.Snapshot()), ct);
        Phases["captureStartCompletedMs"] = sample?.Elapsed.TotalMilliseconds ?? context.ElapsedMs;
        Stage = "capture.ready";
        while (service.Runtime().State == "starting") await Task.Delay(50, ct);
        if (service.Runtime().State != "running") throw new ProbeException(service.Runtime().Reason ?? "probe.captureNotRunning");
        Phases["captureRunningMs"] = sample?.Elapsed.TotalMilliseconds ?? context.ElapsedMs;
        StartSampler();
    }

    public void StartSampler()
    {
        if (!sampler.IsCompleted) return;
        sampler = Task.Run(async () =>
        {
            while (!samplerStop.IsCancellationRequested)
            {
                SampleWorker();
                try { await Task.Delay(250, samplerStop.Token); } catch (OperationCanceledException) when (samplerStop.IsCancellationRequested) { }
            }
        });
    }

    public int? WorkerProcessId => Observations?.Runtime().WorkerProcessId;

    private void SampleWorker()
    {
        lock (processGate)
        {
            if (Observations?.Runtime().WorkerProcessId is { } id && !processHandles.ContainsKey(id))
                try { processHandles.Add(id, Process.GetProcessById(id)); context.ObserveProcess(id); } catch (ArgumentException) { }
            Memory.Sample(processHandles.Values);
        }
    }

    /// <summary>库存与核对查询使用的公开 SDK，通过本机回环与 owner 令牌访问同一 Host。</summary>
    public MabiToolClient Client()
    {
        if (client is not null) return client;
        var address = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return client = new MabiToolClient(new Uri(address), _ => ValueTask.FromResult(Token));
    }

    /// <summary>
    /// 公共 API 标记为可重试的失败（如写事务超出预算返回的 storage.unavailable）按同一幂等键重试，
    /// 避免机器瞬时负载使整次实采作废；其余错误直接返回给场景。
    /// </summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> call, CancellationToken ct, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await call(); }
            catch (MabiToolApiException error) when (error.Problem?.Retryable == true && attempt < attempts)
            { await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct); }
        }
    }

    public async Task<OperationSnapshot> WaitOperationAsync(Func<Task<OperationSnapshot>> read, Func<OperationSnapshot, bool> done, CancellationToken ct)
    {
        while (true)
        {
            var value = await RetryAsync(read, ct);
            if (done(value)) return value;
            await Task.Delay(200, ct);
        }
    }

    public async Task CleanupStepAsync(string phase, Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception error) { CleanupErrors.Add(phase + ":" + SafeCodes.Of(error)); }
    }

    /// <summary>冻结共享观察快照、释放捕获需求、停止并释放 Host，随后记录订阅、活动与 Worker 退出。</summary>
    public async Task CleanupAsync(Func<Task>? beforeRelease = null, Action? afterHostStop = null)
    {
        if (cleaned) return;
        cleaned = true;
        var cleanup = Stopwatch.StartNew();
        if (Observations is not null)
        {
            Shared.Capture(Observations.Snapshot(), context.ElapsedMs, freeze: true);
            if (onChanged is not null) Observations.Changed -= onChanged;
        }
        samplerStop.Cancel();
        try { await sampler; } catch (Exception error) { CleanupErrors.Add("sampler.stop:" + SafeCodes.Of(error)); }
        if (beforeRelease is not null) await beforeRelease();
        if (Demand is not null && Observations is not null && Owner is not null)
            await CleanupStepAsync("capture.release", async () => await Observations.ReleaseAsync(Owner, true, Demand.Id, CancellationToken.None));
        client?.Dispose(); client = null;
        await CleanupStepAsync("host.stop", () => App.StopAsync());
        afterHostStop?.Invoke();
        if (Observations is not null)
        {
            var runtime = Observations.Runtime(); FinalSubscribers = runtime.SubscriberCount; FinalState = runtime.State;
        }
        FinalActivities = App.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities.Count;
        await CleanupStepAsync("host.dispose", async () => await App.DisposeAsync());
        Phases["cleanupMs"] = cleanup.Elapsed.TotalMilliseconds;
        lock (processGate)
        {
            WorkersObserved = processHandles.Count;
            WorkersExited = processHandles.Values.All(Exited);
            foreach (var process in processHandles.Values) process.Dispose();
            processHandles.Clear();
        }
        context.Untrack(this);
    }

    private static bool Exited(Process process)
    {
        try { return process.HasExited || process.WaitForExit(TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException) { return true; }
    }

    public bool Released => cleaned && CleanupErrors.Count == 0 && WorkersExited && FinalSubscribers == 0 && FinalActivities == 0;

    public object ReleaseReport() => new
    {
        workersObserved = WorkersObserved, workersExited = WorkersExited, subscribers = FinalSubscribers,
        activities = FinalActivities, state = FinalState, cleanupErrors = CleanupErrors
    };

    /// <summary>场景异常退出时的兜底释放。</summary>
    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        samplerStop.Dispose();
    }
}
