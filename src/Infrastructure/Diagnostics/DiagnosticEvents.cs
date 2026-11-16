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
        ["state", "revision", "sessionChanged", "minimized"]);
    public static readonly DiagnosticEventDefinition WorkerStarted = new("observation.worker.started", Observation, "observation.worker", DiagnosticLevel.Info, ["pid"]);
    public static readonly DiagnosticEventDefinition WorkerStopped = new("observation.worker.stopped", Observation, "observation.worker", DiagnosticLevel.Info,
        ["end", "exitCode", "terminatedByHost"]);
    public static readonly DiagnosticEventDefinition WorkerFaulted = new("observation.worker.faulted", Observation, "observation.worker", DiagnosticLevel.Info, ["reason"]);
    public static readonly DiagnosticEventDefinition CaptureStarted = new("observation.capture.started", Observation, "observation.capture", DiagnosticLevel.Info, ["profile"]);
    public static readonly DiagnosticEventDefinition CaptureEnded = new("observation.capture.ended", Observation, "observation.capture", DiagnosticLevel.Info, ["reason"]);
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
    public static readonly DiagnosticEventDefinition ColorRunEnded = new("color.run.ended", "color", "color.run", DiagnosticLevel.Info, ["state", "reason"]);
    public static readonly DiagnosticEventDefinition RoomConnection = new("room.connection.changed", "rooms", "rooms.session", DiagnosticLevel.Info, ["state", "roomOwner"]);
    public static readonly DiagnosticEventDefinition DebugCaptureStarted = new("diagnostics.capture.started", "core", "diagnostics.capture", DiagnosticLevel.Info,
        ["level", "modules", "durationSeconds", "includeDetail"]);
    public static readonly DiagnosticEventDefinition DebugCaptureEnded = new("diagnostics.capture.ended", "core", "diagnostics.capture", DiagnosticLevel.Info,
        ["reason", "records", "dropped"]);
    /// <summary>首次以工具根布局启动时从旧位置迁入数据：files 为复制的文件数，bytes 为字节数；旧位置保持原样。</summary>
    public static readonly DiagnosticEventDefinition LegacyDataImported = new("storage.legacy.imported", "core", "storage.legacy", DiagnosticLevel.Info,
        ["files", "bytes"]);
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

    public static IReadOnlyList<DiagnosticEventDefinition> All { get; } = [BindingChanged, WorkerStarted, WorkerStopped, WorkerFaulted, CaptureStarted,
        CaptureEnded, InventorySynced, InventoryFrameState, InventoryFrameRejected, SegmentStarted, SegmentEnded, CombatFrameRejected, HostLog, ColorRunStarted, ColorRunEnded, RoomConnection, DebugCaptureStarted, DebugCaptureEnded, LegacyDataImported,
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
