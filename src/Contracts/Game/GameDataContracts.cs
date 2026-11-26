using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>数据主题名（<see cref="GameDataQuery.Topics"/>）。</summary>
public static class GameDataTopics
{
    public const string Profile = "profile";
    public const string Currencies = "currencies";
    public const string Missions = "missions";
    public const string GrowthGuide = "growth-guide";
    public const string Environment = "environment";
    public const string Nearby = "nearby";
    public static readonly IReadOnlyList<string> All = [Profile, Currencies, Missions, GrowthGuide, Environment, Nearby];
}

/// <summary>某项数据当前内容的来源：游戏连接器（台服命令行）、抓包，或没有来源（读不到，内容为空）。</summary>
public enum GameDataSource { None, Connector, Capture }

/// <summary>命令行与抓包对同一字段给出了不同的值（字段名、命令行的值、抓包的值）；主值仍取命令行。</summary>
public sealed record GameDataConflict(string Field, string? ConnectorValue, string? CaptureValue);

/// <summary>
/// 一个数据主题（或任务列表）的读取状态。<see cref="Source"/> 为 None 表示没有任何来源，内容为空，界面显示未读取；
/// <see cref="Reason"/> 是来源不是命令行、内容为旧数据、被游戏拒绝或没有来源时的原因码（连接器状态原因码、<c>gameCli.busy</c> 通道占用、
/// <c>gameCli.emptyHeld</c> 空列表保留旧数据、<c>gameCli.readFailed</c>、游戏拒绝码如 <c>gameCli.guideLocked</c>），其余为空；
/// <see cref="ReadAt"/> 是内容的读取时刻；<see cref="Stale"/> 为真表示内容是上次读取的旧数据。
/// </summary>
public sealed record GameDataStatus(OpenEnum<GameDataSource> Source, string? Reason, DateTimeOffset? ReadAt, bool Stale,
    IReadOnlyList<GameDataConflict> Conflicts);

/// <summary>
/// 数据查询：<see cref="Topics"/> 取 <see cref="GameDataTopics"/> 里的名称（至少一个）；查询本身就是读取需求，后端只在这时按需读取命令行，
/// 并按主题的最小间隔缓存（环境与附近 5 秒，属性、货币、任务 30 秒，成长指南 60 秒）。<see cref="Refresh"/> 在进入页面或用户点刷新时设置，
/// 可绕过间隔，但同一主题两次实际读取至少相隔 3 秒。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameDataQuery(IReadOnlyList<string> Topics, bool Refresh = false);

/// <summary>一项属性：<see cref="Key"/> 是游戏返回的字段名（STR、CombatScore 等），名称取 <see cref="DisplayName"/> 原文。</summary>
public sealed record GameStat(string Key, string DisplayName, double? Value);

/// <summary>当前状态；任何字段读不到时为空，界面显示未读取。</summary>
public sealed record GameVitals(double? HealthCurrent, double? HealthMax, double? ShieldAmount, double? ShieldMax, double? SatietyValue,
    double? SatietyMax, double? SatietyRatio, double? InventoryWeightCurrent, double? InventoryWeightMax, int? ActiveBuffCount);

/// <summary>生活技能；满级时没有经验（<see cref="Exp"/>、<see cref="MaxExp"/> 为空）。</summary>
public sealed record GameLivingSkill(string DisplayName, int? Level, int? Exp, int? MaxExp);

/// <summary>
/// 角色信息。<see cref="Nickname"/> 是角色昵称，只来自抓包识别的本机身份（命令行 <c>get_my_info</c> 没有昵称字段）；
/// <see cref="Title"/> 是称号；<see cref="ServerName"/> 是服务器名（命令行 <c>RealmName</c>，游戏原文），都不是昵称。
/// <see cref="Stats"/> 是顶层形如 <c>{DisplayName, Value}</c> 的全部属性，<see cref="PaladinStats"/> 是圣职属性。
/// 回落抓包时只有昵称，其余为空。
/// </summary>
public sealed record GameProfile(string? Nickname, string? Title, string? ServerName, int? Level, string? CombatJob, IReadOnlyList<GameStat> Stats,
    IReadOnlyList<GameStat> PaladinStats, GameVitals? Vitals, IReadOnlyList<GameLivingSkill> LivingSkills);

public sealed record GameProfileData(GameDataStatus Status, GameProfile? Profile);

/// <summary>一项货币余额；<see cref="Amount"/> 读不到时为空（不是 0）。</summary>
public sealed record GameCurrency(string DisplayName, double? Amount);

/// <summary><see cref="Items"/> 为空表示未读取，空数组表示读取成功但没有货币。</summary>
public sealed record GameCurrenciesData(GameDataStatus Status, IReadOnlyList<GameCurrency>? Items);

public sealed record GameMission(string Title, string? Description, int? CurrentCount, int? GoalCount, bool IsCompleted,
    bool IsRewardReceived, bool HasShortcut);

/// <summary>
/// 一类任务（日常或周常）。切换频道时命令行可能短暂返回空列表：上次有任务时保留上次内容并标旧数据，空列表持续至少 20 秒、
/// 至少两次读取才确认已清空。<see cref="Items"/> 为空表示未读取。
/// </summary>
public sealed record GameMissionList(GameDataStatus Status, IReadOnlyList<GameMission>? Items);

public sealed record GameMissionsData(GameMissionList Daily, GameMissionList Weekly);

/// <summary>成长指南的一项内容；<see cref="DayType"/> 为 Daily 或 Weekly，未解锁的内容没有完成状态与剩余次数。</summary>
public sealed record GameGrowthContent(string Target, string DayType, bool IsUnlocked, bool? IsCompleted, int? RemainingCount, string? UnlockHint);

/// <summary>成长指南；游戏内的内容指南未解锁时 <see cref="Locked"/> 为真，<see cref="UnlockHint"/> 是游戏给出的解锁条件原文。</summary>
public sealed record GameGrowthGuideData(GameDataStatus Status, bool Locked, string? UnlockHint, IReadOnlyList<GameGrowthContent>? Items);

public sealed record GameHousing(bool? IsInHousing, bool? IsOwnedHousing, bool? CanEnterHousing, string? CannotEnterHousingReason, bool? CanExitHousing);

/// <summary>
/// 环境：<see cref="Channel"/>、<see cref="Area"/> 为游戏显示名原文；<see cref="ErinnTime"/> 是游戏内历法时间原文（不是现实时间）；
/// <see cref="Weather"/> 是游戏返回的天气代码，由界面映射成文字。回落抓包时只有区域（地图名）。
/// </summary>
public sealed record GameEnvironment(string? Channel, string? Area, double? X, double? Y, string? Weather, string? ErinnTime, GameHousing? Housing);

public sealed record GameEnvironmentData(GameDataStatus Status, GameEnvironment? Value);

/// <summary>附近玩家的演奏状态；曲目与时长只在演奏中才有。</summary>
public sealed record GameNearbyPerformance(bool IsPlaying, string? MusicTitle, bool? IsCopyingAllowed, bool? IsLoop,
    double? TotalDurationSeconds, double? ElapsedSeconds, double? RemainingSeconds, int? ChannelCount);

/// <summary>
/// 半径 30 内的玩家。命令行没有昵称字段：<see cref="ServerName"/> 是服务器名（<c>RealmName</c>），<see cref="Title"/> 是称号，
/// 界面以称号作身份，称号为空时写“附近玩家”。
/// </summary>
public sealed record GameNearbyPlayer(string? ServerName, string? Title, double? Distance, int? Level, string? CombatJob, bool? IsFriend,
    bool? IsInParty, bool? HasGuild, bool? IsSameGuild, bool? IsInCombat, GameNearbyPerformance? Performance);

/// <summary><see cref="Players"/> 为空表示未读取，空数组表示读取成功且附近没有玩家。</summary>
public sealed record GameNearbyData(GameDataStatus Status, IReadOnlyList<GameNearbyPlayer>? Players);

/// <summary>数据查询的结果：只包含请求的主题，其余为空。</summary>
public sealed record GameDataSnapshot(string BackendEpoch, GameProfileData? Profile, GameCurrenciesData? Currencies, GameMissionsData? Missions,
    GameGrowthGuideData? GrowthGuide, GameEnvironmentData? Environment, GameNearbyData? Nearby);
