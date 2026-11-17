using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Color;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Platform.Windows;

namespace MabiTool.Host.Game;

public sealed record GameControlContext(GameSessionBindingTracker Bindings, ResourceLeaseCoordinator Leases,
    GameInputBroker Input, bool Simulation)
{
    private GameBackgroundInputBroker? background;

    /// <summary>
    /// 后台输入通道，与 <see cref="Input"/> 并列并共用租约与紧急停止。Windows 上为移植自 MabiAgent 的后台触控；
    /// 模拟宿主与其他平台不可用并给出原因码。现有模块（合奏、染色）本期仍只用前台输入。
    /// </summary>
    public GameBackgroundInputBroker Background
    {
        get => background ??= new(Leases, Bindings, Simulation || !OperatingSystem.IsWindows()
            ? new UnavailableBackgroundInputDevice(Simulation ? "input.backgroundSimulation" : "input.backgroundUnsupported")
            : CreateWindowsBackground());
        init => background = value;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IGameBackgroundInputDevice CreateWindowsBackground() => new WindowsBackgroundTouchInput();

    public static GameControlContext Create(ResourceLeaseCoordinator leases, TimeProvider time, bool simulation)
    {
        var bindings = new GameSessionBindingTracker(time);
        IGameInputDevice device;
        if (simulation)
        {
            bindings.Update(new("TW", Environment.ProcessId, time.GetUtcNow(), new(1), "mock-room", new(0, 0, 1920, 1080), 96, 96, false));
            device = new ReplayGameInputDevice(new(1));
        }
        else device = OperatingSystem.IsWindows() ? new WindowsGameInputDevice() : new UnavailableGameInputDevice();
        return new(bindings, leases, new(leases, bindings, device, time), simulation);
    }
}

public sealed partial class GameBindingService(GameControlContext controls, IGameWindowSource? source, SystemInfo info, TimeProvider time,
    LocalAuthService? auth = null)
{
    private sealed record Candidate(string Owner, SelectableGameWindow Window, long Created, GameWindowCandidate Snapshot);
    private sealed record Receipt(string Hash, GameBindingSnapshot Snapshot, long Created);
    private readonly object gate = new();
    private readonly Dictionary<string, Candidate> candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Key), Receipt> receipts = [];
    private volatile bool selected;
    /// <summary>查询用快照，按 <see cref="GameSessionBindingTracker.ReadRefreshInterval"/> 限流核对窗口。</summary>
    public GameBindingSnapshot Snapshot() => Snapshot(controls.Bindings.Recent);
    private GameBindingSnapshot Snapshot(GameWindowBinding? current)
    {
        return new(current is null ? "unbound" : current.IsMinimized ? "minimized" : "bound", current,
            controls.Simulation, info.InstanceId, current?.Session.BindingRevision ?? new(0));
    }
    public string ETag(GameBindingSnapshot value) => "\"game-binding-" + info.InstanceId + "-" + (value.Binding?.Session.Id ?? "unbound") + "-" + value.Revision + "\"";
    public ApiList<GameWindowCandidate> Enumerate(string owner)
    {
        lock (gate)
        {
            foreach (var old in candidates.Where(x => time.GetElapsedTime(x.Value.Created) >= TimeSpan.FromMinutes(5)).Select(x => x.Key).ToArray()) candidates.Remove(old);
            var windows = source?.Enumerate() ?? [];
            var values = new List<GameWindowCandidate>();
            foreach (var window in windows.Take(32))
            {
                var existing = candidates.Values.FirstOrDefault(x => x.Owner == owner && x.Window.Observation.WindowIdentity == window.Observation.WindowIdentity &&
                    x.Window.Observation.ProcessStartTime == window.Observation.ProcessStartTime);
                if (existing is not null) { values.Add(existing.Snapshot); continue; }
                if (candidates.Count >= 128) throw new OperationException("resource.capacityExceeded", "游戏窗口候选达到上限");
                var value = new GameWindowCandidate(Guid.NewGuid().ToString("N"), window.Name, window.ProcessName, window.Observation.ProcessId,
                    window.Observation.ProcessStartTime, window.Observation.Region, window.Observation.WindowIdentity, time.GetUtcNow().AddMinutes(5));
                candidates.Add(value.Id, new(owner, window, time.GetTimestamp(), value)); values.Add(value);
            }
            return new(values, null, info.InstanceId);
        }
    }
    private static string RequestHash(GameBindingRequest request, string? key, string? etag)
    {
        if (key is null) throw new OperationException("idempotency.required", "游戏绑定需要 Idempotency-Key");
        if (key.Length is < 8 or > 128 || key.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '-')))
            throw new OperationException("request.invalid", "游戏绑定幂等键无效");
        if (etag is null) throw new OperationException("revision.required", "游戏绑定需要 If-Match");
        return JsonSerializer.Serialize(new { request.CandidateId, etag }, ContractJson.Options);
    }
    public async Task<GameBindingSnapshot> SelectAsync(string owner, GameBindingRequest request, string? key, string? etag, CancellationToken ct)
    {
        var hash = RequestHash(request, key, etag);
        Candidate candidate;
        lock (gate)
        {
            if (receipts.TryGetValue((owner, key!), out var previous) && time.GetElapsedTime(previous.Created) < TimeSpan.FromHours(24))
                return previous.Hash == hash ? previous.Snapshot : throw new OperationException("idempotency.conflict", "幂等键已用于其他游戏绑定");
            if (etag != ETag(Snapshot(controls.Bindings.Current))) throw new OperationException("revision.stale", "游戏绑定已变化");
            if (source is null || !candidates.TryGetValue(request.CandidateId, out candidate!) || candidate.Owner != owner)
                throw new OperationException("resource.notFound", "游戏窗口候选不存在");
            if (time.GetElapsedTime(candidate.Created) >= TimeSpan.FromMinutes(5)) throw new OperationException("game.selectionExpired", "游戏窗口候选已到期");
        }
        var prepared = await source.PrepareAsync(candidate.Window.Observation, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!SameIdentity(prepared, candidate.Window.Observation))
            throw new OperationException("input.bindingChanged", "准备期间候选游戏进程或窗口已变化");
        lock (gate)
        {
            if (auth is not null && !auth.HasPermission(owner, ApiPermissions.RoomsCoordinate))
                throw new OperationException("permission.denied", "游戏绑定授权已撤销");
            var fresh = source.Refresh(candidate.Window.Observation);
            if (!SameIdentity(fresh, candidate.Window.Observation) || fresh!.GameBuild != prepared!.GameBuild)
                throw new OperationException("input.bindingChanged", "准备后的游戏构建已变化");
            return Bind(owner, request, key, etag);
        }
    }
    public GameBindingSnapshot Bind(string owner, GameBindingRequest request, string? key, string? etag)
    {
        var hash = RequestHash(request, key, etag);
        lock (gate)
        {
            foreach (var old in receipts.Where(x => time.GetElapsedTime(x.Value.Created) >= TimeSpan.FromHours(24)).Select(x => x.Key).ToArray()) receipts.Remove(old);
            if (receipts.TryGetValue((owner, key!), out var previous))
                return previous.Hash == hash ? previous.Snapshot : throw new OperationException("idempotency.conflict", "幂等键已用于其他游戏绑定");
            if (etag != ETag(Snapshot(controls.Bindings.Current))) throw new OperationException("revision.stale", "游戏绑定已变化");
            if (source is null || !candidates.TryGetValue(request.CandidateId, out var candidate) || candidate.Owner != owner)
                throw new OperationException("resource.notFound", "游戏窗口候选不存在");
            if (time.GetElapsedTime(candidate.Created) >= TimeSpan.FromMinutes(5)) throw new OperationException("game.selectionExpired", "游戏窗口候选已到期");
            if (!SameIdentity(source.Refresh(candidate.Window.Observation), candidate.Window.Observation)) throw new OperationException("input.bindingChanged", "候选游戏进程或窗口已变化");
            if (receipts.Count >= 1000) throw new OperationException("resource.capacityExceeded", "游戏绑定回执达到上限");
            controls.Input.EmergencyStop();
            if (controls.Bindings.BindSource(() =>
            {
                var refreshed = source.Refresh(candidate.Window.Observation);
                return SameIdentity(refreshed, candidate.Window.Observation) ? refreshed : null;
            }) is null)
                throw new OperationException("input.bindingChanged", "候选游戏进程或窗口已变化");
            selected = true;
            var value = Snapshot(controls.Bindings.LastKnown); receipts.Add((owner, key!), new(hash, value, time.GetTimestamp())); return value;
        }
    }
    public bool TryActivate(GameWindowBinding binding)
    {
        if (!selected) return true;
        return source?.TryActivate(new(binding.Session.Region, binding.Session.ProcessId, binding.Session.ProcessStartTime,
            binding.Session.WindowIdentity, binding.Session.GameBuild, binding.Bounds, binding.DpiX, binding.DpiY, binding.IsMinimized)) == true;
    }
    /// <summary>恢复并前置当前绑定的游戏窗口，不发送游戏输入；其他操作持有前台输入时拒绝。</summary>
    public GameWindowActivationReceipt Activate(string principal)
    {
        if (auth is not null && !auth.HasPermission(principal, ApiPermissions.GameControl))
            throw new OperationException("permission.denied", "需要游戏控制权限");
        var binding = controls.Bindings.Current;
        if (binding is null) throw new OperationException("input.bindingChanged", "没有可用的游戏绑定");
        ResourceLeaseSnapshot lease;
        try
        {
            // 短租约占住前台输入，与染色、合奏的获取互斥，激活期间它们不会开始。
            lease = controls.Leases.Acquire(new("game-window-activation", "activate-" + Guid.NewGuid().ToString("N"),
                binding.Session.Id, binding.Session.BindingRevision, ["foreground-input"], TimeSpan.FromSeconds(5)));
        }
        catch (ResourceLeaseException error) when (error.Code == "resource.busy")
        {
            throw new OperationException("resource.busy", "游戏输入已被占用", new Dictionary<string, JsonElement>
            { ["occupiedBy"] = JsonSerializer.SerializeToElement(error.ConflictingOwner) });
        }
        try
        {
            var observation = new GameWindowObservation(binding.Session.Region, binding.Session.ProcessId, binding.Session.ProcessStartTime,
                binding.Session.WindowIdentity, binding.Session.GameBuild, binding.Bounds, binding.DpiX, binding.DpiY, binding.IsMinimized);
            var result = selected && source is not null ? source.Activate(observation) : new GameWindowActivation(false, !binding.IsMinimized, false);
            if (selected && source is not null && !result.Attempted)
                throw new OperationException("input.bindingChanged", "游戏窗口已变化");
            return new(info.InstanceId, controls.Simulation, binding.Session.Id, binding.Session.BindingRevision,
                result.Attempted, result.Restored, result.Foreground, time.GetUtcNow());
        }
        finally { controls.Leases.Release(lease.Id); }
    }
    private static bool SameIdentity(GameWindowObservation? current, GameWindowObservation selected) => current is not null &&
        current.ProcessId == selected.ProcessId && current.ProcessStartTime == selected.ProcessStartTime &&
        current.WindowIdentity == selected.WindowIdentity && current.Region == selected.Region;
}

internal sealed class UnavailableGameInputDevice : IGameInputDevice
{
    public UInt64String? ForegroundWindowIdentity => null;
    public void Press(InputControl control) => throw new GameInputException("input.bindingUnavailable");
    public void Release(InputControl control) { }
    public void MovePointer(PhysicalScreenPoint point) => throw new GameInputException("input.bindingUnavailable");
    public void ReleaseAll() { }
}

internal sealed class ReplayGameInputDevice(UInt64String window) : IGameInputDevice
{
    public UInt64String? ForegroundWindowIdentity => window;
    public void Press(InputControl control) => throw new GameInputException("room.simulationUnavailable");
    public void Release(InputControl control) { }
    public void MovePointer(PhysicalScreenPoint point) => throw new GameInputException("room.simulationUnavailable");
    public void ReleaseAll() { }
}
