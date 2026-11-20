namespace MabiTool.Infrastructure;

public enum DiagnosticLevel { Trace, Debug, Info, Warning, Error }

/// <param name="Module">事件所属模块；为空时由调用方给出（Worker 通道按所属服务记入模块）。</param>
/// <param name="Fields">允许出现的 fields 键；其他键在写入前丢弃。</param>
public sealed record DiagnosticEventDefinition(string Code, string? Module, string Component, DiagnosticLevel Level, IReadOnlyList<string> Fields);

/// <summary>诊断事件码的集中登记表。info 事件默认记录，debug/trace 事件只在调试采集选中对应模块与等级时记录。</summary>
public static class DiagnosticEvents
{
    private const string Observation = "game-access", Combat = "combat-observation";
    public static readonly DiagnosticEventDefinition BindingChanged = new("game.binding.changed", Observation, "game.binding", DiagnosticLevel.Info,
        ["state", "revision", "sessionChanged", "minimized", "changed", "merged"]);
    /// <summary>绑定客户端的构建指纹摘要（每个游戏会话一次）：build 为 registered/adaptive/unrecognized，三文件哈希前 12 位，
    /// 能否抓包及身份、音乐、战斗各项的核对结果（enabled 或停用原因码）；不含路径。</summary>
    public static readonly DiagnosticEventDefinition GameBuildChecked = new("game.build.checked", Observation, "game.build", DiagnosticLevel.Info,
        ["build", "registeredName", "assembly", "metadata", "unity", "captureUsable", "identity", "music", "combat"]);
    /// <summary>自动抓包状态变化：state 为 off/waiting/starting/running/failed，reason 为等待或失败原因码。</summary>
    public static readonly DiagnosticEventDefinition AutomationChanged = new("observation.automation.changed", Observation, "observation.capture", DiagnosticLevel.Info,
        ["state", "reason", "repeats"]);
    /// <summary>抓包设备的打开结果：source 为 automatic（全部设备）/saved/request，opened 与 failed 为打开成功与失败（含跳过）的设备数，
    /// primary 为当前主来源类别；指定网卡不可用时 reason 为原因码。readMode 为实际读取方式（immediate/poll），readFallback 为是否因 Npcap
    /// 不支持读事件或即时模式而回退轮询，coalesceMs 为合批间隔，kernelBufferKiB 为每句柄内核缓冲（旧 Worker 不上报时省略这四项）。
    /// 打开结果、主来源或读取方式变化时记录。</summary>
    public static readonly DiagnosticEventDefinition ChannelSelected = new("observation.channel.selected", Observation, "observation.capture", DiagnosticLevel.Info,
        ["source", "reason", "opened", "failed", "primary", "readMode", "readFallback", "coalesceMs", "kernelBufferKiB"]);
    /// <summary>抓包流量摘要（流量计数）：捕获开始后第 10、30 秒，之后每 60 秒且有变化时一条，同一时点另有 <see cref="CaptureParse"/>。
    /// 不含地址、端口与载荷。packets/bytes 为收到的包与字节，filtered 为不属于游戏连接的包，truncated 与 driverDropped 为截断与驱动丢包，
    /// connections 为当前接收连接数，segments 为交给重组的数据段。</summary>
    public static readonly DiagnosticEventDefinition CaptureSummary = new("observation.capture.summary", Observation, "observation.capture", DiagnosticLevel.Info,
        ["elapsedSeconds", "source", "packets", "bytes", "filtered", "truncated", "driverDropped", "connections", "segments"]);
    /// <summary>抓包流量摘要（解析与产出状态），与 <see cref="CaptureSummary"/> 同时写出：frames 为分帧成功数，dispatched 为分发数，
    /// decompressFailures 与 rejected 为解码失败与拒绝数，identity 为是否已取得本机身份，各 *AgeMs 为对应产出距今毫秒（没有产出时省略）。</summary>
    public static readonly DiagnosticEventDefinition CaptureParse = new("observation.capture.parse", Observation, "observation.capture", DiagnosticLevel.Info,
        ["elapsedSeconds", "frames", "dispatched", "decompressFailures", "rejected", "identity", "reportAgeMs", "combatAgeMs", "inventoryAgeMs"]);
    /// <summary>抓包流量摘要（重组重置分项），与 <see cref="CaptureSummary"/> 同时写出：各原因的累计重置次数（新连接 connectionSyn、
    /// 结束或复位 connectionEnded、移除 connectionRemoved、缺口超时 gapTimeout、驱动丢包 captureDrops、重传不一致 conflictingRetransmission、
    /// 帧头失步 invalidFrameHeader，其余原因合计 other），gapSkippedBytes 为缺口超时跳过的字节数，combatGapResets 为同一连接内的重置使战斗解析
    /// 整体重置（对应分段以 stream-reset 结束）的次数，inventoryGapInvalidations 为宿主收到的因重组缺口失效的库存帧数。</summary>
    public static readonly DiagnosticEventDefinition CaptureReassembly = new("observation.capture.reassembly", Observation, "observation.capture", DiagnosticLevel.Info,
        ["elapsedSeconds", "gapSkippedBytes", "connectionSyn", "connectionEnded", "connectionRemoved", "gapTimeout",
            "captureDrops", "conflictingRetransmission", "invalidFrameHeader", "other", "combatGapResets", "inventoryGapInvalidations"]);
    /// <summary>流量摘要的按设备分项：每张设备只在首次摘要、打开结果／链路类型／主来源变化或计数自上次写出后变化时写一条，停止抓包时为计数有变化的设备补写；index 为设备序号，kind 为 physical/wintun/tap/hyperv/virtual/loopback，
    /// linkType 为链路类型值（未打开为 -1），opened 与 issue 为打开结果，primary 为是否主来源，packets、portPackets、gameStreams、frames
    /// 为收包、游戏端口包、确认的游戏流与分帧数，candidateDropped 为确认前超出候选缓冲而丢弃的字节。不含设备名与地址。</summary>
    public static readonly DiagnosticEventDefinition CaptureDevice = new("observation.capture.device", Observation, "observation.capture", DiagnosticLevel.Info,
        ["elapsedSeconds", "index", "kind", "linkType", "opened", "issue", "primary", "packets", "portPackets", "gameStreams", "frames", "candidateDropped"]);
    public static readonly DiagnosticEventDefinition WorkerStarted = new("observation.worker.started", Observation, "observation.worker", DiagnosticLevel.Info, ["pid"]);
    public static readonly DiagnosticEventDefinition WorkerStopped = new("observation.worker.stopped", Observation, "observation.worker", DiagnosticLevel.Info,
        ["end", "exitCode", "terminatedByHost"]);
    /// <summary>捕获故障：reason 为原因码；kind 为 observation.captureFailed 的细分原因（capture.*、writer.* 来自 Worker，host.* 来自宿主），其他原因为空。</summary>
    public static readonly DiagnosticEventDefinition WorkerFaulted = new("observation.worker.faulted", Observation, "observation.worker", DiagnosticLevel.Info, ["reason", "kind"]);
    public static readonly DiagnosticEventDefinition CaptureStarted = new("observation.capture.started", Observation, "observation.capture", DiagnosticLevel.Info, ["profile"]);
    /// <summary>捕获结束：reason 为结束原因；kind 同 <see cref="WorkerFaulted"/> 的细分原因。</summary>
    public static readonly DiagnosticEventDefinition CaptureEnded = new("observation.capture.ended", Observation, "observation.capture", DiagnosticLevel.Info, ["reason", "kind"]);
    public static readonly DiagnosticEventDefinition InventorySynced = new("inventory.sync.completed", "inventory", "inventory.sync", DiagnosticLevel.Info,
        ["items", "generation"]);
    /// <summary>Worker 送达的非增量库存帧（登录数据到达即等待身份、完整同步、撤销及原因），附带端点预挂缓冲的累计计数；不含身份与条目内容。</summary>
    public static readonly DiagnosticEventDefinition InventoryFrameState = new("inventory.frame.state", "inventory", "inventory.sync", DiagnosticLevel.Info,
        ["kind", "reason", "items", "identity", "prearmBuffered", "prearmDelivered", "prearmExpired"]);
    /// <summary>库存读模型拒绝写入的帧：code 为问题码，branch 为固定校验分支（如 itemIdentity）。</summary>
    public static readonly DiagnosticEventDefinition InventoryFrameRejected = new("inventory.frame.rejected", "inventory", "inventory.sync", DiagnosticLevel.Warning,
        ["kind", "code", "branch", "items", "changes"]);
    public static readonly DiagnosticEventDefinition SegmentStarted = new("combat.segment.started", Combat, "combat.segment", DiagnosticLevel.Info, ["ordinal", "reason"]);
    public static readonly DiagnosticEventDefinition SegmentEnded = new("combat.segment.ended", Combat, "combat.segment", DiagnosticLevel.Info, ["ordinal", "reason"]);
    /// <summary>战斗领域按固定分支拒绝的帧（受限频率）：count 为本条覆盖的新增次数，total 为本次观察累计。</summary>
    public static readonly DiagnosticEventDefinition CombatFrameRejected = new("combat.frame.rejected", Combat, "combat.session", DiagnosticLevel.Warning,
        ["branch", "count", "total"]);
    /// <summary>宿主日志的 warning/error：category 为日志类别，eventId 为日志事件号；未登记错误码的日志据此定位来源。</summary>
    public static readonly DiagnosticEventDefinition HostLog = new("host.log.entry", null, "host.log", DiagnosticLevel.Warning, ["category", "eventId"]);
    public static readonly DiagnosticEventDefinition ColorRunStarted = new("color.run.started", "color", "color.run", DiagnosticLevel.Info, []);
    public static readonly DiagnosticEventDefinition ColorRunEnded = new("color.run.ended", "color", "color.run", DiagnosticLevel.Info, ["state", "reason", "detail", "stage", "problem"]);
    public static readonly DiagnosticEventDefinition RoomConnection = new("room.connection.changed", "rooms", "rooms.session", DiagnosticLevel.Info, ["state", "roomOwner"]);
    public static readonly DiagnosticEventDefinition DebugCaptureStarted = new("diagnostics.capture.started", "core", "diagnostics.capture", DiagnosticLevel.Info,
        ["level", "modules", "durationSeconds", "includeDetail"]);
    public static readonly DiagnosticEventDefinition DebugCaptureEnded = new("diagnostics.capture.ended", "core", "diagnostics.capture", DiagnosticLevel.Info,
        ["reason", "records", "dropped"]);
    /// <summary>首次以工具根布局启动时从旧位置迁入数据：files 为复制的文件数，bytes 为字节数；旧位置保持原样。</summary>
    public static readonly DiagnosticEventDefinition LegacyDataImported = new("storage.legacy.imported", "core", "storage.legacy", DiagnosticLevel.Info,
        ["files", "bytes"]);
    /// <summary>未指定端口时默认端口不可用、宿主改用系统分配的端口：reason 为 occupied（其他程序占用）或 refused（系统拒绝监听），
    /// requestedPort 为默认端口，port 为实际监听端口。</summary>
    public static readonly DiagnosticEventDefinition HostPortFallback = new("host.port.fallback", "core", "host.port", DiagnosticLevel.Info,
        ["reason", "requestedPort", "port"]);
    public static readonly DiagnosticEventDefinition FrameRejected = new("observation.frame.rejected", Combat, "observation.capture", DiagnosticLevel.Debug, ["branch"]);
    public static readonly DiagnosticEventDefinition CaptureGap = new("observation.capture.gap", Combat, "observation.capture", DiagnosticLevel.Debug,
        ["reason", "droppedEvents"]);
    public static readonly DiagnosticEventDefinition InterestsChanged = new("observation.interests.changed", Observation, "observation.capture", DiagnosticLevel.Debug,
        ["music", "combat", "inventory"]);
    public static readonly DiagnosticEventDefinition SubscriptionChanged = new("observation.subscription.changed", Observation, "observation.capture", DiagnosticLevel.Debug,
        ["kind", "action", "count"]);
    public static readonly DiagnosticEventDefinition ConnectionChanged = new("observation.connection.changed", Observation, "observation.capture", DiagnosticLevel.Debug,
        ["current", "opened", "closed"]);
    public static readonly DiagnosticEventDefinition InventoryFrame = new("inventory.frame.received", "inventory", "observation.capture", DiagnosticLevel.Trace,
        ["kind", "reason", "items", "changes"]);
    public static readonly DiagnosticEventDefinition ChannelConnected = new("worker.channel.connected", null, "worker.channel", DiagnosticLevel.Debug, ["pid"]);
    public static readonly DiagnosticEventDefinition ChannelRequest = new("worker.channel.request", null, "worker.channel", DiagnosticLevel.Trace, ["method"]);
    public static readonly DiagnosticEventDefinition ChannelFault = new("worker.channel.fault", null, "worker.channel", DiagnosticLevel.Debug, ["reason"]);
    public static readonly DiagnosticEventDefinition ChannelClosed = new("worker.channel.closed", null, "worker.channel", DiagnosticLevel.Debug,
        ["exitCode", "terminatedByHost"]);

    public static IReadOnlyList<DiagnosticEventDefinition> All { get; } = [BindingChanged, GameBuildChecked, AutomationChanged, ChannelSelected, CaptureSummary, CaptureParse, CaptureReassembly, CaptureDevice, WorkerStarted, WorkerStopped, WorkerFaulted, CaptureStarted,
        CaptureEnded, InventorySynced, InventoryFrameState, InventoryFrameRejected, SegmentStarted, SegmentEnded, CombatFrameRejected, HostLog, ColorRunStarted, ColorRunEnded, RoomConnection, DebugCaptureStarted, DebugCaptureEnded, LegacyDataImported, HostPortFallback,
        FrameRejected, CaptureGap, InterestsChanged, SubscriptionChanged, ConnectionChanged, InventoryFrame,
        ChannelConnected, ChannelRequest, ChannelFault, ChannelClosed];
    private static readonly Dictionary<string, DiagnosticEventDefinition> ByCode = All.ToDictionary(x => x.Code, StringComparer.Ordinal);
    public static DiagnosticEventDefinition? Find(string code) => ByCode.GetValueOrDefault(code);
    public static string LevelName(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Trace => "trace", DiagnosticLevel.Debug => "debug", DiagnosticLevel.Info => "info",
        DiagnosticLevel.Warning => "warning", _ => "error"
    };
    public static DiagnosticLevel Level(string name) => name switch
    {
        "trace" => DiagnosticLevel.Trace, "debug" => DiagnosticLevel.Debug, "info" => DiagnosticLevel.Info,
        "warning" => DiagnosticLevel.Warning, _ => DiagnosticLevel.Error
    };
}

/// <summary>fields 构造器：整数按十进制、布尔为 true/false、短码原样，写入时再按白名单校验。</summary>
public sealed class DiagnosticFields
{
    internal readonly Dictionary<string, string> Items = new(StringComparer.Ordinal);
    public DiagnosticFields Count(string key, long value) { Items[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }
    public DiagnosticFields Count(string key, ulong value) { Items[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture); return this; }
    public DiagnosticFields Flag(string key, bool value) { Items[key] = value ? "true" : "false"; return this; }
    public DiagnosticFields Value(string key, string? value) { if (value is not null) Items[key] = value; return this; }
}
