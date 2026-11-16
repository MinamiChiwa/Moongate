using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiagnosticRetentionPolicy(int CompletedSessions = 10, UInt64String? SessionBudgetBytes = null,
    UInt64String? LogBudgetBytes = null, int RecentProtectionSeconds = 300);
public sealed record DiagnosticPolicySnapshot(DiagnosticRetentionPolicy Policy, UInt64String Revision);
/// <param name="Component">比模块更细的固定短码，例如 observation.capture、worker.channel。</param>
/// <param name="Event">登记过的稳定事件码；未登记的事件码不进入记录。</param>
/// <param name="Fields">白名单键到受限短值（整数、true/false 或固定短码）的映射，最多 12 条。</param>
/// <param name="Sequence">宿主会话内单调递增的记录序号。</param>
/// <param name="Detail">清洗后的文字详情；只出现在用户开启且勾选“包含文字详情”的调试采集记录中。</param>
/// <param name="HttpMethod">HTTP 问题响应对应请求的方法（GET、POST、PUT、PATCH、DELETE、HEAD、OPTIONS）。</param>
/// <param name="Route">HTTP 问题响应对应请求匹配到的路由模板，例如 <c>/api/v1/operations/{id}</c>；不含实际参数值与查询串，未匹配路由时为空。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiagnosticRecord(DateTimeOffset OccurredAt, string Level, string Module, string? Code,
    string? TraceId, string? OperationId, string? ProviderVersion, string? ExceptionType,
    string? Component = null, string? Event = null, IReadOnlyDictionary<string, string>? Fields = null,
    UInt64String? Sequence = null, string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HttpMethod = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Route = null);
public sealed record DiagnosticSessionSnapshot(string Id, string Owner, string Module, string State,
    UInt64String Revision, DateTimeOffset StartedAt, DateTimeOffset LastWrittenAt, UInt64String EventBytes,
    UInt64String Records, UInt64String DroppedRecords, string EventSha256);
public sealed record DiagnosticSummary(DiagnosticPolicySnapshot Retention, IReadOnlyList<DiagnosticSessionSnapshot> Sessions,
    UInt64String LogBytes, UInt64String DroppedRecords, string? LastError);
public sealed record DiagnosticSessionVersion(string Id, UInt64String Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiagnosticExportRequest(IReadOnlyList<DiagnosticSessionVersion> Sessions);
public sealed record DiagnosticExportArtifact(string Id, string Owner, AssetSnapshot Asset, DateTimeOffset ExpiresAt);
public sealed record DiagnosticExportSession(DiagnosticSessionSnapshot Session, IReadOnlyList<DiagnosticRecord> Records);
/// <param name="SchemaVersion">当前为 2；v1 文档没有 captures 与 containsDetail。</param>
/// <param name="Captures">所选会话中调试采集会话的采集信息及开始、结束时的运行状态总览。</param>
/// <param name="ContainsDetail">任一所选采集包含文字详情时为 true。</param>
public sealed record DiagnosticExportDocument(int SchemaVersion, DateTimeOffset CreatedAt,
    IReadOnlyList<DiagnosticExportSession> Sessions, IReadOnlyList<DiagnosticCaptureExport>? Captures = null,
    bool? ContainsDetail = null);
/// <param name="State">稳定小写状态码；探针故障时为 unavailable。</param>
/// <param name="Reason">已登记错误码，或为空。</param>
/// <param name="TruncatedEntries">超出每探针计数或短值上限而截断的条目数。</param>
/// <param name="RejectedEntries">键或值不符合白名单而丢弃的条目数。</param>
public sealed record DiagnosticProbeSnapshot(string Id, string Module, string State, string? Reason,
    IReadOnlyDictionary<string, UInt64String> Counters, IReadOnlyDictionary<string, string> Values,
    int TruncatedEntries, int RejectedEntries);
/// <param name="TruncatedProbes">超出探针数量上限而省略的探针数。</param>
/// <param name="RejectedProbes">标识无效或重复而省略的探针数。</param>
public sealed record DiagnosticRuntimeSnapshot(int SchemaVersion, DateTimeOffset CapturedAt, string BackendEpoch,
    IReadOnlyList<DiagnosticProbeSnapshot> Probes, int TruncatedProbes, int RejectedProbes);

/// <summary>已落盘诊断记录查询；条件之间为“与”，列表内为“或”。</summary>
/// <param name="Cursor">上一页返回的 nextCursor。</param>
/// <param name="Limit">1–500，默认 100。</param>
/// <param name="Order">asc（默认，从旧到新）或 desc（从新到旧）；游标只能用于生成它的顺序，desc 游标带 d- 前缀。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiagnosticRecordQuery(string? SessionId = null, IReadOnlyList<string>? Modules = null,
    IReadOnlyList<string>? Levels = null, IReadOnlyList<string>? Events = null, string? TraceId = null,
    string? OperationId = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Cursor = null, int Limit = 100,
    string Order = "asc");
public sealed record DiagnosticRecordEntry(string SessionId, DiagnosticRecord Record);

/// <param name="Modules">被选模块，1–7 个：core、game-access、combat-observation、inventory、music-library、color、rooms。</param>
/// <param name="Level">debug 或 trace。</param>
/// <param name="DurationSeconds">1–600 秒，到期自动结束。</param>
/// <param name="IncludeDetail">为 true 时采集记录可带清洗后的异常消息、堆栈与 Worker 输出尾部。</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiagnosticCaptureRequest(IReadOnlyList<string> Modules, string Level, int DurationSeconds, bool IncludeDetail = false);
/// <param name="State">active、ended 或 interrupted（宿主重启时仍在进行）。</param>
/// <param name="EndReason">expired、stopped、hostStopping 或 restarted；进行中为空。</param>
/// <param name="DroppedRecords">环形缓冲覆盖与文件预算不足而丢弃的记录数。</param>
/// <param name="PushDropped">超过每秒 50 条推送限速而未实时推送的记录数（仍写入采集文件）。</param>
/// <param name="DetailTruncated">因单条或单次采集文字总量上限而截断或省略详情的记录数。</param>
public sealed record DiagnosticCaptureSnapshot(string Id, string Owner, string SessionId, IReadOnlyList<string> Modules,
    string Level, bool IncludeDetail, string State, string? EndReason, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt, UInt64String Records, UInt64String DroppedRecords, UInt64String PushDropped,
    UInt64String DetailBytes, UInt64String DetailTruncated);
/// <param name="Capture">进行中的采集，或最近一次已结束的采集；从未采集时为空。</param>
public sealed record DiagnosticCaptureCurrent(DiagnosticCaptureSnapshot? Capture);
public sealed record DiagnosticCaptureExport(DiagnosticCaptureSnapshot Capture, DiagnosticRuntimeSnapshot? StartRuntime,
    DiagnosticRuntimeSnapshot? EndRuntime);
