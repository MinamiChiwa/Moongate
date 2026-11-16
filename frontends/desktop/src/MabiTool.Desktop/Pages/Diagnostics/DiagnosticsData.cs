using MabiTool.Desktop.Infrastructure;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>
/// 当前主体对诊断功能的权限，取自握手时的授权信息。<see cref="CanDebug"/> 对应 diagnostics.debug（调试采集与实时记录），
/// <see cref="CanExport"/> 对应 diagnostics.export（导出排查包）。
/// </summary>
public sealed record DiagnosticAccess(bool Connected, bool CanDebug, bool CanExport);

/// <summary>
/// 运行状态总览里的一个探针。<see cref="Counters"/> 的值是字符串数值，键以 Ms、Bytes、Seconds 结尾时分别是毫秒、字节、秒；
/// <see cref="Values"/> 是短码。键与探针都随后端版本增加，页面按通用键值表显示，不写死全集。
/// </summary>
public sealed record ProbeView(string Id, string Module, string State, string? Reason,
    IReadOnlyList<KeyValue> Counters, IReadOnlyList<KeyValue> Values, int TruncatedEntries, int RejectedEntries);

public sealed record RuntimeView(DateTimeOffset CapturedAt, string BackendEpoch, IReadOnlyList<ProbeView> Probes,
    int TruncatedProbes, int RejectedProbes);

/// <summary>读取运行状态的回执：失败时 <see cref="Value"/> 为空，<see cref="Message"/> 是错误码。</summary>
public sealed record DiagnosticRuntimeReply(RuntimeView? Value, string Message);

/// <summary>一条诊断记录。<see cref="Fields"/> 是白名单键到受限短值；<see cref="Detail"/> 只在包含文字详情的调试采集里出现。</summary>
public sealed record DiagnosticRecordView(string SessionId, DateTimeOffset OccurredAt, string Level, string Module,
    string? Component, string? Event, string? Code, IReadOnlyList<KeyValue> Fields, string? Sequence, string? Detail);

/// <summary>
/// 记录查询条件：列表内为“或”，空列表表示不限；每页 <see cref="Limit"/> 条，<see cref="Cursor"/> 是上一页的 nextCursor，
/// <see cref="From"/> 只取该时刻及之后的记录。<see cref="Order"/> 为 asc（从旧到新，默认）或 desc（从新到旧）；
/// 续页带同一个顺序，desc 的游标以 d- 开头，游标与顺序不匹配时后端返回 400 request.invalid。
/// </summary>
public sealed record DiagnosticRecordFilter(string? SessionId, IReadOnlyList<string> Modules, IReadOnlyList<string> Levels,
    string? Cursor, int Limit = 100, DateTimeOffset? From = null, string Order = DiagnosticRecordFilter.Ascending)
{
    public const string Ascending = "asc";
    public const string Descending = "desc";
}

public sealed record DiagnosticRecordsReply(bool Ok, string Message, IReadOnlyList<DiagnosticRecordView> Items, string? NextCursor);

/// <summary>实时推送的一条记录；<see cref="CaptureId"/> 是事件的资源标识，即调试采集的编号。</summary>
public sealed record DiagnosticPushedRecord(string CaptureId, DiagnosticRecordView Record);

/// <summary>开始调试采集的参数，与后端请求一致：1–7 个模块、级别 debug 或 trace、1–600 秒。</summary>
public sealed record DebugCaptureSettings(IReadOnlyList<string> Modules, string Level, int DurationSeconds, bool IncludeDetail);

/// <summary>调试采集的快照。计数字段后端以十进制字符串传输，这里已解析。</summary>
public sealed record DiagnosticCaptureView(string Id, string SessionId, IReadOnlyList<string> Modules, string Level,
    bool IncludeDetail, string State, string? EndReason, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt, ulong Records, ulong DroppedRecords, ulong PushDropped, ulong DetailBytes, ulong DetailTruncated)
{
    public bool Active => State == "active";
}

/// <summary>
/// 采集相关请求的回执。<see cref="Ok"/> 为假时 <see cref="Message"/> 是错误码（例如 diagnostics.captureActive、permission.denied）；
/// 成功时 <see cref="Capture"/> 是最新快照，读取当前采集时从未采集过为空。
/// </summary>
public sealed record DiagnosticCaptureReply(bool Ok, string Message, DiagnosticCaptureView? Capture);

/// <summary>导出范围：最近一次调试采集的会话，或调用者可见的全部会话。</summary>
public enum DiagnosticExportScope { LatestCapture, AllSessions }

/// <summary>
/// 导出的结果：<see cref="Content"/> 是完整的导出文档（UTF-8 JSON）；<see cref="ContainsDetail"/> 取自文档的 containsDetail。
/// 失败时 <see cref="Content"/> 为空，<see cref="Message"/> 是错误码。
/// </summary>
public sealed record DiagnosticExportReply(bool Ok, string Message, byte[]? Content, bool ContainsDetail, string FileName);
