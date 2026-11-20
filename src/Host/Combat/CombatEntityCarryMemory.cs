using MabiTool.Contracts;

namespace MabiTool.Host.Combat;

/// <summary>
/// 观察 Worker 上报的实体延续记录（自动体类型、表 ID、最大 HP、首领标志与 HP 解码信息），按游戏进程与连接只保留最新一份。
/// 捕获重启时随捕获请求回传给同一游戏进程的下一次捕获，Worker 只在同一连接的流上补足；进程不同或记录不合规时不下发。
/// 解码信息只在内存中，不进入快照、历史、诊断与日志。
/// </summary>
public sealed class CombatEntityCarryMemory
{
    private readonly object gate = new();
    private (int ProcessId, DateTimeOffset StartedAt, CombatEntityCarry Carry)? last;

    /// <summary>登记一份上报；字段不合规的整份忽略。</summary>
    public void Record(GameSession? game, CombatEntityCarry? carry)
    {
        if (game is null || carry is null || !Valid(carry)) return;
        lock (gate) last = (game.ProcessId, game.ProcessStartTime, carry);
    }

    /// <summary>同一游戏进程最近的延续记录；没有或进程不同时为空。</summary>
    public CombatEntityCarry? For(GameSession game)
    {
        lock (gate) return last is { } value && value.ProcessId == game.ProcessId && value.StartedAt == game.ProcessStartTime ? value.Carry : null;
    }

    public void Clear() { lock (gate) last = null; }

    internal static bool Valid(CombatEntityCarry carry) =>
        carry.Connection is { LocalPort: > 0 and <= 65535, RemotePort: > 0 and <= 65535, RemoteAddress: { Length: <= 64 } address } &&
        System.Net.IPAddress.TryParse(address, out _) && carry.Entities is { Count: <= 64 } entities &&
        entities.All(entity => entity is not null && entity.Id.Value != 0 && entity.ActorType is >= 1 and <= 8 &&
            (entity.Iv is null || entity.Iv.Length == 24 && entity.KeyId is not null) && entity.SheetId is null or { Value: <= uint.MaxValue } &&
            entity.HealthMaximum is null or { Value: <= long.MaxValue });
}
