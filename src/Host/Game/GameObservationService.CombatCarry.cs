using MabiTool.Host.Combat;

namespace MabiTool.Host.Game;

public sealed partial class GameObservationService
{
    /// <summary>战斗报告带来的实体延续记录；捕获重启时随捕获请求下发给同一游戏进程（见 <see cref="CombatEntityCarryMemory"/>）。</summary>
    internal CombatEntityCarryMemory CombatCarry { get; } = new();
}
