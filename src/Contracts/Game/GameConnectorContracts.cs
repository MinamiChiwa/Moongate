using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>游戏连接器（台服游戏客户端自带的命令行工具）的连接状态。</summary>
public enum GameConnectorStatus
{
    /// <summary>刚启动或刚切换，尚未取得结果。</summary>
    Checking,
    /// <summary>无法启用：命令行工具文件缺失或哈希不符、没有数据根，原因见 <c>Reason</c>。</summary>
    Unavailable,
    /// <summary>台服客户端的连接器偏好为关闭。</summary>
    Disabled,
    /// <summary>偏好已启用，但游戏没有运行或还没有响应。</summary>
    GameNotRunning,
    /// <summary>命令行工具已连上正在运行的游戏。</summary>
    Connected,
    /// <summary>游戏在运行，但连接器已关闭或已过期（需要重新启用并从启动器重启游戏）。</summary>
    Expired
}

public enum GameConnectorCliSource { None, Package, LocalKorean }
public enum GameConnectorCliFileState { Missing, Verified, HashMismatch }
public enum GameConnectorDirectorySource { None, Bound, Remembered, Default, Manual }

/// <summary>命令行工具文件：来源（随包或本机韩服目录）、哈希核对结果，以及是否已部署到台服目录。</summary>
public sealed record GameConnectorCliFile(OpenEnum<GameConnectorCliSource> Source, OpenEnum<GameConnectorCliFileState> State,
    string? Version, bool Deployed, bool DeployedDiffers);

/// <summary><c>Running</c> 为空表示进程状态未读到；<c>Directory</c> 是台服游戏目录（含 <c>MabinogiMobile.exe</c> 的文件夹）。</summary>
public sealed record GameConnectorGameInfo(bool? Running, string? Directory, OpenEnum<GameConnectorDirectorySource> DirectorySource);

/// <summary>三个写操作各自能否执行及被拒绝的原因码（可执行时为空）。</summary>
public sealed record GameConnectorActions(bool CanEnable, string? EnableBlocked, bool CanDisable, string? DisableBlocked,
    bool CanRestore, string? RestoreBlocked);

/// <summary>指令目录：<c>Loaded</c> 为真时 <c>CommandCount</c> 是当前游戏会话取得的指令数；<c>Loading</c> 表示客户端报告目录尚未就绪。</summary>
public sealed record GameConnectorCatalogInfo(bool Loaded, bool Loading, int CommandCount);

public sealed record GameConnectorSnapshot(string BackendEpoch, UInt64String Revision, OpenEnum<GameConnectorStatus> Status,
    string Reason, bool? PreferenceEnabled, bool RiskAcknowledged, GameConnectorCliFile Cli, GameConnectorGameInfo Game,
    GameConnectorActions Actions, GameConnectorCatalogInfo Catalog, DateTimeOffset CheckedAt, DateTimeOffset? LastConnectedAt,
    DateTimeOffset? PreferenceLastUsedAt);

/// <summary>
/// 连接器写操作。<c>Action</c> 为 <c>enable</c>、<c>disable</c>、<c>restore</c>、<c>refresh</c> 或 <c>set-directory</c>；
/// <c>enable</c> 必须带 <c>RiskAcknowledged=true</c>（首次开启的风险说明已由用户确认，后端记住该确认）；
/// <c>set-directory</c> 与 <c>enable</c> 可带 <c>GameDirectory</c> 指定台服游戏目录。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameConnectorCommand(string Action, bool RiskAcknowledged = false, string? GameDirectory = null);

public enum GameScoreSupportState { Supported, UserComposed, Unknown }

/// <summary>某张乐谱能否经连接器在游戏里演奏；<c>UserComposed</c> 表示不在游戏提供的乐谱列表里（玩家自制或复制）。</summary>
public sealed record GameScoreSupport(string ScoreId, OpenEnum<GameScoreSupportState> State, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameScoreSupportQuery(IReadOnlyList<string> ScoreIds);

public sealed record GameScoreSupportPage(string BackendEpoch, OpenEnum<GameConnectorStatus> Connector,
    IReadOnlyList<GameScoreSupport> Items);

/// <summary>乐谱库游戏乐谱同步的结果：<c>Synced</c> 已用游戏当前的乐谱列表刷新来源；<c>Unavailable</c> 连接器或存储不可用；<c>Failed</c> 读取失败。</summary>
public enum GameMusicSyncStatus { Synced, Unavailable, Failed }

/// <summary>
/// 把游戏提供的乐谱列表（<c>get_music_scores</c>）同步为乐谱库来源的结果。<see cref="Reason"/> 是不可用或失败的原因码（连接器原因码、
/// <c>storage.unavailable</c>、<c>gameCli.failed</c> 等），已同步时为空；<see cref="ScoreCount"/> 是来源里当前在游戏中的乐谱数；
/// 不可用与失败时来源保持上次内容，<see cref="SyncedAt"/> 是上次成功同步的时刻（没有时为空）。
/// </summary>
public sealed record GameMusicLibrarySync(string BackendEpoch, OpenEnum<GameMusicSyncStatus> Status, string? Reason, int ScoreCount,
    DateTimeOffset? SyncedAt);
