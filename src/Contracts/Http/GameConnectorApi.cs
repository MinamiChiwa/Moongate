namespace MabiTool.Contracts;

/// <summary>游戏连接器（台服命令行工具）接口：状态、写操作、乐谱可演奏性查询与乐谱库的游戏乐谱同步。</summary>
public static class GameConnectorApi
{
    public static EndpointDefinition State { get; } = new("/api/v1/game/connector", "getGameConnector", typeof(GameConnectorSnapshot), ETag: true);
    public static EndpointDefinition Command { get; } = new("/api/v1/game/connector/commands", "commandGameConnector", typeof(GameConnectorSnapshot),
        Method: "POST", RequestType: typeof(GameConnectorCommand), Permission: ApiPermissions.GameControl, Idempotent: true);
    public static EndpointDefinition ScoreSupport { get; } = new("/api/v1/game/connector/score-support/query", "queryGameScoreSupport", typeof(GameScoreSupportPage),
        Method: "POST", RequestType: typeof(GameScoreSupportQuery), Permission: ApiPermissions.GameControl);
    /// <summary>把游戏提供的乐谱列表同步为乐谱库的“游戏列表”来源；读取 <c>get_music_scores</c>，不占活动锁。</summary>
    public static EndpointDefinition MusicLibrarySync { get; } = new("/api/v1/game/music-library/sync", "syncGameMusicLibrary", typeof(GameMusicLibrarySync),
        Method: "POST", Permission: ApiPermissions.MusicEdit);

    public static IReadOnlyList<EndpointDefinition> Endpoints { get; } = Array.AsReadOnly<EndpointDefinition>([State, Command, ScoreSupport, MusicLibrarySync]);
}
