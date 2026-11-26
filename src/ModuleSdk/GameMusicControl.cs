using System.Text;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

/// <summary>
/// 游戏内演奏动作的活动凭证：调用方持有的 <c>activity-control:{游戏会话}</c> 租约。执行类命令由实现方按租约、会话与绑定修订核对后才发出。
/// </summary>
public sealed record GameActivityProof(string LeaseId, UInt64String FencingToken, string OwnerId, string GameSessionId,
    UInt64String BindingRevision);

public enum GameMusicOutcome
{
    /// <summary>游戏已受理（命令行工具返回成功）。</summary>
    Accepted,
    /// <summary>游戏拒绝了请求，<see cref="GameMusicResult.Code"/> 是游戏给出的原因（如 <c>user_composed_not_allowed</c>）。</summary>
    Rejected,
    /// <summary>请求没有发出：连接器未连接、目录不含该指令、凭证无效或活动被占用。</summary>
    Unavailable,
    /// <summary>命令行工具返回了错误或无法解析，游戏是否已执行未知。</summary>
    Failed,
    /// <summary>调用方取消或紧急停止中断了命令行进程，游戏动作状态未知。</summary>
    Interrupted
}

public sealed record GameMusicResult(GameMusicOutcome Outcome, string? Code = null, string? Detail = null)
{
    public bool Accepted => Outcome == GameMusicOutcome.Accepted;
    /// <summary>请求确定没有到达游戏；其他结果可能已经产生游戏效果。</summary>
    public bool NotSent => Outcome == GameMusicOutcome.Unavailable;
}

public sealed record GameInstrument(string Name, bool Equipped, double? Durability);

/// <summary>游戏提供的乐谱（<c>get_music_scores</c>）：标题是游戏原文；列表不含玩家自制或复制的乐谱。</summary>
public sealed record GameProvidedScore(string DisplayTitle, string Location, bool IsLocked);

public sealed record GameScoreListing(bool Available, string? Reason, IReadOnlyList<GameProvidedScore> Scores);

/// <summary>游戏里读到的演奏状态（<c>get_activity.Performance</c>）。</summary>
public sealed record GamePerformanceActivity(bool IsPlaying, string? InstrumentName, string? MusicTitle, bool IsLoop,
    double? TotalSeconds, double? ElapsedSeconds, double? RemainingSeconds, int ChannelCount, bool IsDead, bool IsInCombat);

public sealed record GameMusicAvailability(bool Available, string Reason);

/// <summary>
/// 经台服游戏连接器控制演奏的端口：乐谱库队列与合奏共用。实现方保证命令行单通道串行、执行类命令必须带有效活动凭证、
/// 不自动重发，取消只中断命令行进程（游戏动作状态保持未知）。
/// </summary>
public interface IGameMusicControl
{
    GameMusicAvailability Availability { get; }
    /// <summary>读取游戏提供的乐谱；<paramref name="maxAge"/> 内的缓存直接返回。</summary>
    ValueTask<GameScoreListing> ListScoresAsync(TimeSpan maxAge, CancellationToken ct);
    /// <summary>只读缓存：标题是否在最近一次取得的游戏乐谱列表里；列表还没有取得时为空。</summary>
    bool? TryIsGameProvided(string title);
    ValueTask<IReadOnlyList<GameInstrument>?> ListInstrumentsAsync(CancellationToken ct);
    ValueTask<GameMusicResult> ChangeInstrumentAsync(GameActivityProof proof, string name, CancellationToken ct);
    ValueTask<GameMusicResult> PlayScoreAsync(GameActivityProof proof, string title, CancellationToken ct);
    ValueTask<GameMusicResult> StopAsync(GameActivityProof proof, CancellationToken ct);
    ValueTask<GamePerformanceActivity?> ReadActivityAsync(CancellationToken ct);
    /// <summary>最近几次命令行调用的往返耗时中位数（毫秒），没有样本时为 0；合奏用它提前发出开始命令。</summary>
    double RecentCallLatencyMs { get; }
}

/// <summary>乐谱标题与乐器名称在乐谱库、游戏列表之间比较时使用的规范化。</summary>
public static partial class GameMusicNames
{
    [GeneratedRegex("<[^>]{1,64}>")]
    private static partial Regex RichText();

    /// <summary>去掉富文本标记与首尾空白，用于核对乐谱库标题与 <c>get_music_scores</c> 的 <c>DisplayTitle</c>。</summary>
    public static string Title(string value) => RichText().Replace(value ?? string.Empty, string.Empty).Trim();

    /// <summary>比较乐器名称：NFKC、去空白、忽略大小写。</summary>
    public static string InstrumentKey(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized) if (!char.IsWhiteSpace(ch)) builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString();
    }

    /// <summary>
    /// 在持有乐器里找房间/清单指定的乐器：先按规范化全名，再按“唯一一个名称包含指定名称”；多个候选时不选择。
    /// </summary>
    public static GameInstrument? MatchInstrument(IReadOnlyList<GameInstrument> owned, string wanted, out bool ambiguous)
    {
        ambiguous = false;
        var key = InstrumentKey(wanted);
        if (key.Length == 0) return null;
        var exact = owned.Where(x => InstrumentKey(x.Name) == key).ToArray();
        if (exact.Length > 0) return exact.FirstOrDefault(x => x.Equipped) ?? exact[0];
        var contains = owned.Where(x => InstrumentKey(x.Name).Contains(key, StringComparison.Ordinal)).ToArray();
        if (contains.Length == 1) return contains[0];
        ambiguous = contains.Length > 1;
        return null;
    }
}
