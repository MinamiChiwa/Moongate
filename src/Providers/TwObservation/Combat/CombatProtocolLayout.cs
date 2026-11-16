namespace MabiTool.Providers.TwObservation.Combat;

/// <summary>共用捕获流的协议布局：MusicOnly 为固定 MabiSync 构建的 tw-music-v1 捕获（只解析身份与音乐，不受理战斗兴趣）；Tw20261110 为当前台服构建，音乐、战斗与库存共用。</summary>
public enum CombatProtocolLayout { MusicOnly, Tw20261110 }
