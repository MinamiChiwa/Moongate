using MabiTool.ModuleSdk;

namespace MabiTool.Modules.Rooms.Domain;

/// <summary>就绪或开轮准备时为本机成员确保乐器的结果：<see cref="ErrorCode"/> 为空表示已就绪或无需处理（连接器不可用、没有分配乐器、命令确定没发出）。</summary>
public sealed record GameInstrumentPreparation(string? ErrorCode, string? GameCode = null);

/// <summary>
/// 合奏经游戏连接器（台服命令行）控制本机演奏的端口：开奏前换乐器、游戏自带乐谱经 <c>play_music_score</c> 开奏、经 <c>stop_action</c> 停止。
/// 所有执行类调用都带本轮持有的 <c>activity-control</c> 租约证明；连接器不可用时 <see cref="Available"/> 为假，合奏沿用按键。
/// </summary>
public interface IRoomGameMusic
{
    bool Available { get; }
    /// <summary>最近命令行调用耗时的中位数（毫秒），开始命令按它提前发出。</summary>
    double StartLeadMs { get; }
    ValueTask<GameInstrumentPreparation> PrepareInstrumentAsync(GameActivityProof proof, string? instrumentName, CancellationToken ct);
    /// <summary>预热游戏乐谱列表并判断标题能否经连接器演奏（true 可演奏，false 玩家自制，空为未知）。</summary>
    ValueTask<bool?> WarmAsync(string? title, CancellationToken ct);
    /// <summary>只读缓存的可演奏性。</summary>
    bool? TryIsPlayable(string title);
    ValueTask<GameMusicResult> StartAsync(GameActivityProof proof, string title, CancellationToken ct);
    ValueTask<GameMusicResult> StopAsync(GameActivityProof proof, CancellationToken ct);
}
