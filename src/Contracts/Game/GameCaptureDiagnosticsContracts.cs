using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>
/// 观察 Worker v1 的可选事件 <c>captureDiagnostics</c>：一次捕获运行内的累计计数，只含整数、协议号与固定码。
/// Host 以启动环境 <see cref="EnvironmentVariable"/>=1 声明可受理；未声明时 Worker 不发送，旧 Worker 忽略该变量。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObservationCaptureCounters(int SchemaVersion, CaptureConnectionCounters Connections,
    CapturePacketCounters Packets, CaptureQueueCounters Queues, CaptureReassemblyCounters Reassembly, CaptureFrameCounters Frames,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CapturePrearmCounters? Prearm = null)
{
    public const string EnvironmentVariable = "MABITOOL_OBSERVATION_DIAGNOSTICS";
    public const int MaximumFrameTypes = 24;
    /// <summary>重组重置原因的固定码，与 Provider 的流重置原因一一对应。</summary>
    public static IReadOnlySet<string> ResetCauses { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "unknown", "payloadTooLarge", "pendingCapacity", "gapTimeout", "conflictingRetransmission", "bufferCapacity",
        "invalidFrameHeader", "decodeFailure", "connectionRemoved", "captureDrops", "truncatedPacket", "connectionSyn",
        "connectionClosed", "connectionReset"
    };
    /// <summary>帧解析拒绝的固定分支码：按接收该帧的观察器区分。</summary>
    public static IReadOnlySet<string> RejectionBranches { get; } = new HashSet<string>(StringComparer.Ordinal) { "combat", "music", "inventory" };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CaptureConnectionCounters(UInt64String Current, UInt64String Opened, UInt64String Closed);
/// <param name="Received">原生抓包读到的包数；Bytes 为其捕获长度合计。</param>
/// <param name="Filtered">不属于所选连接下行或无法解析为 TCP 的包。</param>
/// <param name="Truncated">捕获长度与线上长度不一致而丢弃的包。</param>
/// <param name="DriverDropped">抓包驱动报告的丢包（内核与网卡丢弃之和）。</param>
/// <param name="ReadLimitHits">单轮读取达到上限、驱动缓冲仍有积压的次数。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CapturePacketCounters(UInt64String Received, UInt64String Bytes, UInt64String Filtered,
    UInt64String Truncated, UInt64String DriverDropped, UInt64String ReadLimitHits);
/// <param name="CombatDepth">当前战斗写出队列深度（批次与容量缺口），CombatPeak 为本次运行的采样高水位。</param>
/// <param name="InventoryLaneRejected">库存解析通道因容量拒绝的工作项。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CaptureQueueCounters(UInt64String CombatDepth, UInt64String CombatPeak, UInt64String InventoryDepth,
    UInt64String InventoryPeak, UInt64String InventoryLaneRejected);
/// <param name="Resets">按固定原因码的重组重置次数，只含非零项。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CaptureReassemblyCounters(UInt64String Segments, UInt64String Bytes, UInt64String OutOfOrder,
    UInt64String Retransmitted, IReadOnlyDictionary<string, UInt64String> Resets);
/// <param name="Total">已分帧的帧数；Dispatched 为交给至少一个观察器的帧，其余为当前兴趣不使用的已知协议号。</param>
/// <param name="Types">按十进制协议号的帧数，至多 24 种；之后新出现协议号的帧计入 OtherFrames。</param>
/// <param name="Rejected">按固定分支码（combat、music、inventory）的解析拒绝数，只含非零项。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CaptureFrameCounters(UInt64String Total, UInt64String Dispatched, UInt64String DecompressFailures,
    IReadOnlyDictionary<string, UInt64String> Rejected, IReadOnlyDictionary<string, UInt64String> Types, UInt64String OtherFrames);
/// <summary>
/// 新连接预缓冲（端点过滤器）的计数，只含包数；可省略（旧 Worker）。
/// </summary>
/// <param name="Buffered">进入有界缓冲的未确认连接下行包。</param>
/// <param name="Delivered">连接表确认属于绑定进程后从缓冲按序交付的包。</param>
/// <param name="Expired">超过保留时长仍未确认而丢弃的包。</param>
/// <param name="Foreign">连接表确认属于其他进程而丢弃的包。</param>
/// <param name="Budget">超出字节或包数预算而丢弃的最旧未确认包。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CapturePrearmCounters(UInt64String Buffered, UInt64String Delivered, UInt64String Expired,
    UInt64String Foreign, UInt64String Budget);
