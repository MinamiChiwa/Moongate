namespace MabiTool.Contracts;

/// <summary>乐谱库的来源同步接口：背包抓包读到的乐谱同步为“库存”来源（游戏列表来源的同步在 <see cref="GameConnectorApi"/>）。</summary>
public static class MusicLibraryApi
{
    /// <summary>把库存读模型里的玩家自制与复制乐谱同步为当前角色的抓包来源；不要求游戏连接器。</summary>
    public static EndpointDefinition SyncOwnedScores { get; } = new("/api/v1/music/scores/owned/sync", "syncOwnedMusicScores", typeof(MusicOwnedSync),
        Method: "POST", Permission: ApiPermissions.MusicEdit);

    public static IReadOnlyList<EndpointDefinition> Endpoints { get; } = Array.AsReadOnly<EndpointDefinition>([SyncOwnedScores]);
}
