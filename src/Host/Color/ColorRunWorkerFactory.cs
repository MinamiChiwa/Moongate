using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Modules.Color.Domain;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Color;

/// <summary>运行依赖由宿主装配，HTTP 请求不能指定进程、输入设备或可执行路径。</summary>
public sealed class ColorRunWorkerFactory(ColorWorkerOptions options, GameSessionBindingTracker bindings,
    ResourceLeaseCoordinator leases, GameInputBroker input, IGameCaptureSource capture, bool allowProcessRead = false,
    string providerId = "color-engine", ColorWorkerPool? workerPool = null) : IAsyncDisposable
{
    public GameSessionBindingTracker Bindings { get; } = bindings;
    public ResourceLeaseCoordinator Leases { get; } = leases;
    public GameInputBroker Input { get; } = input;
    public ColorWorkerPool Workers { get; } = workerPool ?? new(options);
    public EmergencyStopResult EmergencyStop() => Input.EmergencyStop();
    public ValueTask DisposeAsync() => Workers.DisposeAsync();
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
