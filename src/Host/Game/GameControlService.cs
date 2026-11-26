using MabiTool.Contracts;
using MabiTool.Host.Color;
using MabiTool.Host.GameCli;
using MabiTool.Host.Life;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;

namespace MabiTool.Host.Game;

public sealed class GameControlService(GameControlContext controls, SystemInfo info, TimeProvider clock,
    LocalAuthService auth, RoomSessionManager? rooms = null, ColorRunOperations? color = null,
    GameConnectorService? connector = null, PerformanceRunner? performance = null,
    FishingRunOperations? fishing = null)
{
    public const int MaximumReceipts = 256;
    public static readonly TimeSpan MaximumRequestLifetime = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<(string Owner, string Key), Receipt> receipts = [];
    private DateTimeOffset expiredRequestFloor = DateTimeOffset.MinValue;
    private sealed record Receipt(GameEmergencyStopRequest Request, GameEmergencyStopReceipt Result,
        long Created, TimeSpan Lifetime);

    public GameInputOwnershipSnapshot Snapshot()
    {
        var owners = controls.Leases.Snapshot().Where(IsGameControl).Select(lease => new GameInputOwnership(
            lease.OwnerId, lease.OperationId, lease.GameSessionId, lease.BindingRevision,
            lease.Resources, lease.ExpiresAt)).ToArray();
        return new(info.InstanceId, controls.Simulation, clock.GetUtcNow(), Array.AsReadOnly(owners));
    }

    public GameEmergencyStopReceipt EmergencyStop(string principal, GameEmergencyStopRequest request, string? key)
    {
        if (!auth.HasPermission(principal, ApiPermissions.GameControl))
            throw new OperationException("permission.denied", "需要游戏控制权限");
        if (request is null || string.IsNullOrWhiteSpace(request.BackendEpoch) || request.ExpiresAt.Offset != TimeSpan.Zero)
            throw new OperationException("request.invalid", "停止请求需要宿主代次及 UTC 期限");
        if (!string.Equals(request.BackendEpoch, info.InstanceId, StringComparison.Ordinal))
            throw new OperationException("game.controlEpochChanged", "宿主代次已改变，请重新查询当前状态");
        RequireKey(key);
        lock (gate)
        {
            PruneReceipts();
            var now = clock.GetUtcNow();
            if (request.ExpiresAt <= now || request.ExpiresAt <= expiredRequestFloor)
                throw new OperationException("game.stopRequestExpired", "停止请求已过期");
            if (request.ExpiresAt - now > MaximumRequestLifetime)
                throw new OperationException("request.invalid", "停止请求有效期最多五分钟");
            if (receipts.TryGetValue((principal, key!), out var previous))
                return previous.Request == request ? previous.Result
                    : throw new OperationException("idempotency.conflict", "幂等键已用于其他停止请求");
            if (receipts.Count >= MaximumReceipts)
                throw new OperationException("resource.capacityExceeded", "停止回执达到容量，请等待旧请求到期");
            var result = StopLocked();
            receipts.Add((principal, key!), new(request, result, clock.GetTimestamp(), request.ExpiresAt - now));
            return result;
        }
    }

    public GameEmergencyStopReceipt StopLocal()
    {
        lock (gate) return StopLocked();
    }

    private GameEmergencyStopReceipt StopLocked()
    {
        var before = controls.Leases.Snapshot().Where(IsGameControl).ToArray();
        var failures = new List<GameEmergencyStopFailure>();
        EmergencyStopResult? released = null;
        // 合奏先送出本机停止键（游戏内演奏自动循环，不停就一直响），再撤销输入占用；之后不再有本工具发起的输入。
        if (rooms is not null)
        {
            try { rooms.EmergencyStop(); }
            catch { failures.Add(new("rooms", "game.activityCancellationFailed")); }
        }
        // 游戏连接器：演奏队列驱动先取消并撤销租约，再中断命令行进程；游戏动作状态保持未知，随后重查一次演奏状态。
        if (performance is not null)
        {
            try { performance.EmergencyStop(); }
            catch { failures.Add(new("performance", "game.activityCancellationFailed")); }
        }
        if (connector is not null)
        {
            try { connector.EmergencyStop(); }
            catch { failures.Add(new("game-connector", "game.activityCancellationFailed")); }
        }
        try { released = controls.Input.EmergencyStop(); }
        catch { failures.Add(new("input", "input.releaseFailed")); }
        // 后台输入通道与前台输入共用紧急停止：撤销其租约并释放按下的触点与按键。
        try { controls.Background.EmergencyStop(); }
        catch { failures.Add(new("background-input", "input.releaseFailed")); }
        // 活动控制也可能独立于前台输入，撤销本次停止开始时的完整租约。
        foreach (var lease in before) controls.Leases.Release(lease.Id);
        if (color is not null)
        {
            try { color.EmergencyStop(); }
            catch { failures.Add(new("color", "game.activityCancellationFailed")); }
        }
        // 钓鱼：租约与按下的输入已在上面撤销与释放，这里取消运行，识别进程随之结束。
        if (fishing is not null)
        {
            try { fishing.EmergencyStop(); }
            catch { failures.Add(new("life", "game.activityCancellationFailed")); }
        }
        var remaining = controls.Leases.Snapshot().Select(lease => lease.Id).ToHashSet(StringComparer.Ordinal);
        return new(info.InstanceId, controls.Simulation, clock.GetUtcNow(), before.Count(lease => !remaining.Contains(lease.Id)),
            released?.ReleasedControls, released is null ? "failed" : "released", rooms is not null || color is not null || performance is not null || connector is not null || fishing is not null,
            EffectStatus.Unknown, Array.AsReadOnly(failures.ToArray()));
    }

    private void PruneReceipts()
    {
        foreach (var pair in receipts.Where(pair => clock.GetElapsedTime(pair.Value.Created) >= pair.Value.Lifetime).ToArray())
        {
            // 回收后保留过期期限水位，墙上时钟回拨也不能使旧请求复活。
            if (pair.Value.Request.ExpiresAt > expiredRequestFloor) expiredRequestFloor = pair.Value.Request.ExpiresAt;
            receipts.Remove(pair.Key);
        }
    }

    private static bool IsGameControl(ResourceLeaseSnapshot lease) => lease.Resources.Any(resource =>
        resource == "foreground-input" || resource == "activity-control" || resource.StartsWith("activity-control:", StringComparison.Ordinal));

    private static void RequireKey(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "操作需要 Idempotency-Key");
        if (key.Length is < 8 or > 128 || key.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_' or '-')))
            throw new OperationException("request.invalid", "幂等键无效");
    }
}
