using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Modules.Color.Domain;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Color;

/// <summary>
/// 运行依赖由宿主装配，HTTP 请求不能指定进程、输入设备或可执行路径。
/// <paramref name="activate"/> 在取得输入租约后把绑定的游戏窗口切到前台：用户在 Moongate 里按开始，此时前台是 Moongate，
/// 统一输入代理只向前台的绑定窗口发送输入（原工具从游戏内按 F8 开始，不需要这一步）。
/// </summary>
public sealed class ColorRunWorkerFactory(ColorWorkerOptions options, GameSessionBindingTracker bindings,
    ResourceLeaseCoordinator leases, GameInputBroker input, IGameCaptureSource capture, bool allowProcessRead = false,
    string providerId = "color-engine", ColorWorkerPool? workerPool = null, Func<GameWindowBinding, bool>? activate = null) : IAsyncDisposable
{
    public GameSessionBindingTracker Bindings { get; } = bindings;
    public ResourceLeaseCoordinator Leases { get; } = leases;
    public GameInputBroker Input { get; } = input;
    public ColorWorkerPool Workers { get; } = workerPool ?? new(options);
    public EmergencyStopResult EmergencyStop() => Input.EmergencyStop();
    public ValueTask DisposeAsync() => Workers.DisposeAsync();

    /// <summary>
    /// 正式宿主的装配：染色组件已配置且文件齐全时，运行端口复用宿主的游戏绑定、租约与输入代理（与合奏共同仲裁），
    /// 截图取绑定窗口的客户区，精准寻色由宿主授权只读进程读取；否则返回 null，能力表给出 <see cref="UnavailableReason"/>。
    /// </summary>
    public static ColorRunWorkerFactory? ForHost(ColorWorkerPool workers, GameSessionBindingTracker bindings, ResourceLeaseCoordinator leases,
        GameInputBroker input, IGameCaptureSource? capture, Func<GameWindowBinding, bool> activate)
    {
        if (UnavailableReason(workers.Options) is not null || !OperatingSystem.IsWindows()) return null;
        return new(workers.Options!, bindings, leases, input, capture ?? new MabiTool.Platform.Windows.WindowsGameCaptureSource(),
            allowProcessRead: true, workerPool: workers, activate: activate);
    }

    /// <summary>
    /// 染色运行不可用的原因码：provider.unconfigured 为未配置染色组件（解压版里没有 components\color-engine），
    /// component.missing 为配置的解释器或 Worker 包不存在，platform.unsupported 为非 Windows；可用时为 null。
    /// </summary>
    public static string? UnavailableReason(ColorWorkerOptions? options)
    {
        if (options is null) return "provider.unconfigured";
        if (!OperatingSystem.IsWindows()) return "platform.unsupported";
        if (!File.Exists(options.PythonExecutable) || !Directory.Exists(Path.Combine(options.WorkerDirectory, "mabitool_color")))
            return "component.missing";
        return null;
    }

    public static string UnavailableMessage(string reason) => reason switch
    {
        "provider.unconfigured" => "未找到染色组件：程序文件夹里缺少 components\\color-engine",
        "component.missing" => "染色组件不完整：缺少 Python 解释器或染色 Worker",
        "platform.unsupported" => "染色需要 Windows",
        _ => "染色运行端口不可用"
    };
    public GameSession Resolve(ColorRunStartRequest request)
    {
        var binding = Bindings.Current;
        if (binding is null || binding.Session.Id != request.GameSessionId ||
            binding.Session.BindingRevision != request.ExpectedBindingRevision)
            throw new OperationException("input.bindingChanged", "游戏绑定已变化，请重新选择当前会话");
        if (request.Profile.Strategy.KnownValue == ColorStrategy.Precise && !allowProcessRead)
            throw new OperationException("permission.missing", "当前宿主未授权精准进程读取");
        if (Leases.FindConflict(["foreground-input", "activity-control:" + binding.Session.Id]) is { } occupied)
            throw new OperationException("resource.busy", "游戏输入已被占用", new Dictionary<string, JsonElement>
            { ["occupiedBy"] = JsonSerializer.SerializeToElement(occupied.OwnerId) });
        return binding.Session;
    }

    [SupportedOSPlatform("windows")]
    public async Task<ActiveColorWorker> StartAsync(ColorRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var binding = Bindings.Current;
        if (binding is null || binding.Session != run.BindingSnapshot)
            throw new OperationException("input.bindingChanged", "游戏绑定已变化");
        var lease = Leases.Acquire(new("color", run.OperationId, binding.Session.Id, binding.Session.BindingRevision,
            ["foreground-input", "activity-control:" + binding.Session.Id], TimeSpan.FromMinutes(5)));
        if (activate is not null)
        {
            // 前台输入租约已持有，激活期间其他活动不能开始；切换后绑定修订不变才继续。
            var foreground = false;
            try { foreground = activate(binding); }
            catch { Leases.Release(lease.Id); throw; }
            if (!foreground)
            {
                Leases.Release(lease.Id);
                throw new OperationException("input.focusChanged", "无法把游戏窗口切到前台，请点一下游戏窗口后重新开始");
            }
            if (Bindings.Current is not { } current || current.Session != run.BindingSnapshot)
            {
                Leases.Release(lease.Id);
                throw new OperationException("input.bindingChanged", "游戏窗口在切到前台时发生变化，请重新开始");
            }
            binding = current;
        }
        ColorGamePort? port = null;
        GameInteractionSession? interaction = null;
        ColorWorkerSession? worker = null;
        ColorWorkerPool.Rental? rental = null;
        var effects = new ColorInputEvidence();
        try
        {
            interaction = new(Input, Leases, binding, lease, capture, TimeSpan.FromMinutes(5));
            port = new(interaction, run.Id, lease.FencingToken.ToString(), options.AssetRoot,
                allowProcessRead && run.RulesSnapshot.Strategy.KnownValue == ColorStrategy.Precise);
            // 本次运行目录已建好、是最新的一个：连同它只留最近三次。
            ColorRunFolders.Prune(options.AssetRoot);
            var ownedPort = port;
            using var stopInput = ct.Register(ownedPort.AbortInput);
            rental = await Workers.RentAsync(run.Id, _ => ownedPort.AbortInput(), ct);
            worker = rental.Worker;
            var call = await worker.BeginAsync(new(run.RulesSnapshot.Strategy.KnownValue == ColorStrategy.Precise ? "color.native" : "color.screenshot",
                JsonSerializer.SerializeToElement(new { profile = ColorProfiles.EngineProfile(run.RulesSnapshot), interactive = true }),
                run.Id, binding.Session.BindingRevision.ToString(), lease.FencingToken.ToString()), ct, async (frame, token) =>
                {
                    var method = frame.Payload.GetProperty("method").GetString();
                    var writes = method is "game.gesture" or "game.move" or "game.wheel";
                    if (writes) effects.Attempt();
                    var result = await ownedPort.HandleAsync(frame, token);
                    if (writes) effects.Dispatched();
                    return result;
                });
            rental.Call = call;
            return new(worker, call, port, lease.FencingToken,
                new(providerId, "1.0", binding.Session.Region, binding.Session.GameBuild), effects, rental);
        }
        catch
        {
            try { if (port is not null) port.Dispose(); else if (interaction is not null) interaction.Dispose(); else Leases.Release(lease.Id); }
            finally { if (rental is not null) await rental.DisposeAsync(); }
            throw;
        }
    }

    public static void Validate(ColorRunStartRequest request)
    {
        if (!ColorProfiles.IsId(request.GameSessionId)) throw new OperationException("request.invalid", "游戏会话 ID 无效");
        ColorProfiles.Validate(request.Profile);
    }

}

[SupportedOSPlatform("windows")]
public sealed class ActiveColorWorker(ColorWorkerSession worker, WorkerCall call, ColorGamePort port,
    UInt64String generation, ObservationSource source, ColorInputEvidence effects, ColorWorkerPool.Rental? rental = null) : IAsyncDisposable
{
    public ColorWorkerSession Worker { get; } = worker;
    public WorkerCall Call { get; } = call;
    public UInt64String Generation { get; } = generation;
    public ObservationSource Source { get; } = source;
    public EffectStatus InputEffect => effects.Value;
    public void AbortInput() => port.AbortInput();
    public async ValueTask DisposeAsync()
    {
        try { port.Dispose(); }
        finally { if (rental is not null) await rental.DisposeAsync(); else await Worker.DisposeAsync(); }
    }
}

public sealed class ColorInputEvidence
{
    private int value;
    public EffectStatus Value => (EffectStatus)Volatile.Read(ref value);
    public void Attempt() => Volatile.Write(ref value, (int)EffectStatus.Unknown);
    public void Dispatched() => Volatile.Write(ref value, (int)EffectStatus.InputDispatched);
}
