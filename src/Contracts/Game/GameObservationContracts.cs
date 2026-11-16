using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <param name="Loopback">Npcap 回环设备；旧观察组件不报告时为空。</param>
/// <param name="GameConnections">查询时已绑定游戏进程的数据连接中本机地址落在该网卡上的条数；未绑定或旧组件为空。</param>
public sealed record CaptureChannelOption(string Id, string Description, IReadOnlyList<string> Addresses,
    bool? Loopback = null, int? GameConnections = null);
public sealed record ObservedGameIdentity(UInt64String CharacterId, string RealmId, string Nickname, UInt64String ActorId);
/// <summary>本机所在合奏的成员角色候选，发起者在前，只用于为房间成员匹配游戏角色。</summary>
/// <param name="OrderedCharacterIds">已停用的加入顺序字段：当前 Worker 不再填写；旧 Worker 填写时须与 <paramref name="Members"/> 的角色 ID 逐项一致。</param>
public sealed record ObservedConcert(UInt64String Id, UInt64String HostCharacterId, IReadOnlyList<UInt64String>? OrderedCharacterIds,
    IReadOnlyList<ObservedGameIdentity> Members);
public sealed record ObservedMusic(UInt64String ConcertId, UInt64String PerformerCharacterId, UInt64String HostCharacterId,
    string Title, bool IsLoop, IReadOnlyList<int> SelectedChannels, string? ScoreSha256,
    double? WholeScoreDurationSeconds, double? SelectedDurationSeconds, double? EstimatedStartMonotonicMs,
    string TimingSource, bool PlaybackTimingVerified, string? Issue);
/// <summary>当前地图与频道：当前构建进图通知（20344）的 ChannelSheetId 经客户端 Channel → GameSpace 表解析。</summary>
/// <param name="ChannelSheetId">Channel 表主键（有符号 Int32），即进图通知 ChannelId 的表 ID。</param>
/// <param name="MapName">GameSpace.DisplayName 游戏原文，不做转换；名称表读取中、读取失败或没有对应行时为空。</param>
/// <param name="MapSheetId">GameSpace 表主键；同名地图以此区分。</param>
/// <param name="MapReason">地图名为空的原因：location.mapCatalogPending、location.mapCatalogUnavailable、location.mapUnknown。</param>
/// <param name="PermanentChannel">ChannelRule.IsPermanentChannel；名称表不可用时为空。</param>
/// <param name="PermanentChannelIndex">永久频道按客户端规则（实例编号 = realmId × 10000 + 序号）推出的序号；与游戏界面频道号的对应尚未实机核对。</param>
/// <param name="ChannelReason">没有可显示频道号的原因：location.channelDisplayUnverified、location.channelNotPermanent、location.channelRuleUnknown、location.channelIndexUnresolved。</param>
public sealed record ObservedLocation(int ChannelSheetId, string? MapName, int? MapSheetId, string? MapReason,
    bool? PermanentChannel, int? PermanentChannelIndex, string? ChannelReason);
/// <param name="Location">当前地图与频道；不依赖模块兴趣，进图时更新，连接重置或换绑定时清空；旧 Worker 为空。</param>
public sealed record GameObservationValue(string CaptureGeneration, ObservedGameIdentity? Identity,
    ObservedConcert? Concert, ObservedMusic? Music, UInt64String Packets, string Detail, ObservedLocation? Location = null);
public sealed record GameCaptureReport(GameObservationValue Value, UInt64String Sequence, bool Running,
    double MonotonicMs, DateTimeOffset ObservedAt, string? Reason, ObservationSource? Source = null);
/// <param name="Npcap">Npcap 是否已安装：installed、missing（系统目录下没有 <c>Npcap\wpcap.dll</c>）；宿主只检查文件、不加载 DLL，无法判定时为空。</param>
public sealed record GameObservationRuntime(bool Installed, string State, int SubscriberCount, int? WorkerProcessId,
    int MaximumSubscribers, int MaximumConnections, string? Reason, string? Npcap = null);
public sealed record GameObservationSnapshot(string BackendEpoch, UInt64String Revision, string? CaptureId, string State,
    string? GameSessionId, UInt64String? BindingRevision, string? ChannelId, string? Profile,
    int SubscriberCount, Observation<GameObservationValue>? Latest, string? Reason, GameCaptureAutomation? Automation = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameObservationStartRequest(string? ChannelId, UInt64String BindingRevision, string? Profile = null);
public sealed record GameObservationSubscription(string Id, string CaptureId, GameObservationSnapshot Snapshot);
public sealed record GameObservationRelease(string Id, bool Released, GameObservationSnapshot Snapshot);

/// <summary>全局抓包设置：自动抓包、网卡选择方式与已保存网卡、自动选择游戏窗口。由后端持久化，修订与幂等规则同房间偏好。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameCaptureSettings(UInt64String Revision, bool AutomaticCapture, string ChannelMode,
    string? ChannelId, string? ChannelDescription, bool AutomaticWindowBinding)
{
    public const string AutomaticChannel = "automatic";
    public const string ManualChannel = "manual";
    public static GameCaptureSettings Default => new(new(0), true, AutomaticChannel, null, null, true);
}
/// <summary>常驻抓包的自动启动状态。State 为 off/waiting/starting/running/failed；Reason 为固定错误码。</summary>
public sealed record GameCaptureAutomation(bool Enabled, string State, string? Reason, string? ChannelId,
    string? ChannelDescription, string? ChannelSource, DateTimeOffset? NextAttemptAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CaptureChannelDetectionRequest(UInt64String BindingRevision, int DurationSeconds = 20);
public sealed record CaptureChannelDetectionEntry(string ChannelId, string Description, bool Opened,
    UInt64String Packets, UInt64String PayloadBytes, string? Issue);
/// <summary>手动调试的网卡检测：候选网卡同时短时监听游戏连接下行流量。State 为 idle/running/completed/failed/cancelled。</summary>
public sealed record CaptureChannelDetection(string BackendEpoch, string? Id, string State, UInt64String? BindingRevision,
    int DurationSeconds, double ElapsedSeconds, IReadOnlyList<CaptureChannelDetectionEntry> Channels,
    string? WinnerChannelId, string? Reason, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);
