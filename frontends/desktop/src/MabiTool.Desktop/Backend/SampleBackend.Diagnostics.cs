using System.Globalization;
using System.Text;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Pages.Diagnostics;

namespace MabiTool.Desktop.Backend;

// 诊断页样本：一个未知探针与未知键、一个 unavailable 探针、带文字详情的记录，供排版与交互测试。
public sealed partial class SampleBackend
{
    private const string SampleSessionId = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset SampleNow = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    /// <summary>测试用：当前主体是否拥有 diagnostics.debug（调试采集块）。</summary>
    internal bool DiagnosticDebugAllowed { get; set; } = true;

    /// <summary>测试用：当前主体是否拥有 diagnostics.export（导出块）。</summary>
    internal bool DiagnosticExportAllowed { get; set; } = true;

    /// <summary>测试用：“全部会话”导出文档的 containsDetail。</summary>
    internal bool AllSessionsContainDetail { get; set; }

    /// <summary>测试用：读取当前采集时后端以 403 拒绝（握手权限显示有权限，实际被拒绝）。</summary>
    internal bool CaptureForbidden { get; set; }

    /// <summary>测试用：样本记录总数，超过一页时按 nextCursor 续读。</summary>
    internal int SampleRecordTotal { get; set; } = 14;

    /// <summary>测试用：让运行状态读取失败，值是错误码。</summary>
    internal string? RuntimeFailure { get; set; }

    /// <summary>测试用：最近一次采集；默认是一次已手动结束、不含文字详情的采集。</summary>
    internal DiagnosticCaptureView? SampleCapture { get; set; } = new("capture-1", SampleSessionId,
        ["combat-observation", "game-access"], "debug", false, "ended", "stopped", SampleNow.AddMinutes(-9),
        SampleNow.AddMinutes(-8), SampleNow.AddMinutes(-8).AddSeconds(14), 342, 0, 0, 0, 0);

    internal List<DiagnosticRecordFilter> RecordQueries { get; } = [];
    internal List<DebugCaptureSettings> CaptureStarts { get; } = [];
    internal List<(DiagnosticExportScope Scope, string? SessionId)> DiagnosticExports { get; } = [];
    internal int RuntimeReads { get; private set; }

    public event EventHandler<DiagnosticPushedRecord>? DiagnosticRecordReceived;
    public event EventHandler? DiagnosticStreamRestored;

    /// <summary>测试用：模拟一条实时推送。</summary>
    internal void PushDiagnosticRecord(string captureId, DiagnosticRecordView record) =>
        DiagnosticRecordReceived?.Invoke(this, new(captureId, record));

    internal void RestoreDiagnosticStream() => DiagnosticStreamRestored?.Invoke(this, EventArgs.Empty);

    public DiagnosticAccess GetDiagnosticAccess() => new(!Offline, DiagnosticDebugAllowed, DiagnosticExportAllowed);

    public Task<DiagnosticRuntimeReply> ReadDiagnosticRuntimeAsync(CancellationToken cancellationToken)
    {
        RuntimeReads++;
        if (Offline) return Task.FromResult(new DiagnosticRuntimeReply(null, "frontend.notConnected"));
        if (RuntimeFailure is { } failure) return Task.FromResult(new DiagnosticRuntimeReply(null, failure));
        KeyValue[] none = [];
        return Task.FromResult(new DiagnosticRuntimeReply(new RuntimeView(SampleNow, "epoch-7f3a",
        [
            new("host", "core", "ready", null,
                [new("uptimeSeconds", "3725"), new("memoryBytes", "186646528"), new("requests.total", "1842")],
                [new("version", "preview")], 0, 0),
            new("game.binding", "game-access", "ready", null,
                [new("revision", "12"), new("lastChangeAgeMs", "1500")], [new("state", "bound")], 0, 0),
            new("game.input", "game-access", "ready", null, [new("leases.active", "0")], [new("capability.input.status", "available")], 0, 0),
            new("observation.capture", "combat-observation", "ready", null,
                [new("connections.open", "2"), new("packets.received", "48213"), new("queues.depthBytes", "2048"),
                 new("frames.assembleMs", "37"), new("worker.ends.exitCodeNonZero", "1")],
                [new("stateReason", "none"), new("worker.lastEnd", "stopped")], 0, 0),
            new("music", "music-library", "unavailable", "system.unexpectedError", none, none, 0, 0),
            new("rooms", "rooms", "ready", "input.actionMappingUnavailable", [new("members", "3")],
                [new("capabilities.rooms.coordinate.status", "available"), new("capabilities.rooms.coordinate.reason", "none")], 2, 1),
            // 后端以后增加的探针与键：按通用键值表照常显示。
            new("future.probe-with-a-long-identifier", "future-module", "ready", null,
                [new("someNewCounterMs", "90061"), new("anotherBytes", "1572864"), new("unknownNumber", "42")],
                [new("someVeryLongKeyNameThatKeepsGoingAndGoing.withNested.parts", "a-short-code-value-that-is-also-long-enough-to-wrap")], 0, 0)
        ], 1, 2), string.Empty));
    }

    public Task<DiagnosticRecordsReply> QueryDiagnosticRecordsAsync(DiagnosticRecordFilter filter, CancellationToken cancellationToken)
    {
        RecordQueries.Add(filter);
        if (Offline) return Task.FromResult(new DiagnosticRecordsReply(false, "frontend.notConnected", [], null));
        var all = Enumerable.Range(0, SampleRecordTotal).Select(SampleRecord)
            .Where(record => (filter.Modules.Count == 0 || filter.Modules.Contains(record.Module))
                             && (filter.Levels.Count == 0 || filter.Levels.Contains(record.Level))
                             && (filter.From is not { } from || record.OccurredAt >= from)
                             && (filter.SessionId is null || record.SessionId == filter.SessionId)).ToList();
        // 与后端相同：desc 从新到旧，游标以 d- 开头；游标与顺序不匹配时拒绝（request.invalid）。
        var descending = filter.Order == DiagnosticRecordFilter.Descending;
        var prefix = descending ? "d-" : string.Empty;
        if (filter.Cursor is { } given && given.StartsWith("d-", StringComparison.Ordinal) != descending)
            return Task.FromResult(new DiagnosticRecordsReply(false, "request.invalid", [], null));
        if (descending) all.Reverse();
        var start = filter.Cursor is { } cursor ? int.Parse(cursor[prefix.Length..], CultureInfo.InvariantCulture) : 0;
        var page = all.Skip(start).Take(filter.Limit).ToList();
        var next = start + page.Count < all.Count ? prefix + (start + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new DiagnosticRecordsReply(true, string.Empty, page, next));
    }

    private static DiagnosticRecordView SampleRecord(int index)
    {
        var template = index % 7;
        var time = SampleNow.AddSeconds(-600 + index * 3);
        var sequence = (index + 1).ToString(CultureInfo.InvariantCulture);
        return template switch
        {
            0 => new(SampleSessionId, time, "info", "game-access", "game.binding", "game.binding.changed",
                null, [new("state", "bound"), new("revision", "12")], sequence, null),
            1 => new(SampleSessionId, time, "info", "combat-observation", "observation.capture", "observation.capture.started",
                null, [new("connections", "2")], sequence, null),
            2 => new(SampleSessionId, time, "warning", "combat-observation", "observation.worker", "observation.worker.faulted",
                "observation.workerFault", [new("lastFault", "channelClosed"), new("restarts", "1")], sequence,
                "System.IO.IOException: pipe closed\n   at Worker.Channel.Read(<path>/channel.cs:41)\n   at Worker.Run(<install>/worker.dll)\nworker tail: connection reset by peer while reading frame header"),
            3 => new(SampleSessionId, time, "info", "inventory", null, "inventory.sync.completed", null,
                [new("items", "214"), new("durationMs", "820")], sequence, null),
            4 => new(SampleSessionId, time, "debug", "color", "color.engine", "color.run.started", null, [], sequence, null),
            5 => new(SampleSessionId, time, "error", "rooms", "room.connection", "room.connection.changed", "room.connectionLost",
                [new("state", "closed")], sequence, null),
            _ => new(SampleSessionId, time, "info", "core", null, "diagnostics.capture.started", null,
                [new("modules", "2"), new("level", "debug"), new("durationSeconds", "60")], sequence, null)
        };
    }

    public Task<DiagnosticCaptureReply> GetDiagnosticCaptureAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Offline ? new DiagnosticCaptureReply(false, "frontend.notConnected", null)
            : DiagnosticDebugAllowed && !CaptureForbidden ? new(true, string.Empty, SampleCapture) : new(false, "permission.denied", null));

    public Task<DiagnosticCaptureReply> StartDiagnosticCaptureAsync(DebugCaptureSettings settings, CancellationToken cancellationToken)
    {
        CaptureStarts.Add(settings);
        if (SampleCapture is { Active: true }) return Task.FromResult(new DiagnosticCaptureReply(false, "diagnostics.captureActive", null));
        SampleCapture = new("capture-" + (CaptureStarts.Count + 1), "fedcba9876543210fedcba9876543210", settings.Modules, settings.Level,
            settings.IncludeDetail, "active", null, SampleNow, SampleNow.AddSeconds(settings.DurationSeconds), null, 0, 0, 0, 0, 0);
        return Task.FromResult(new DiagnosticCaptureReply(true, string.Empty, SampleCapture));
    }

    public Task<DiagnosticCaptureReply> StopDiagnosticCaptureAsync(string captureId, CancellationToken cancellationToken)
    {
        if (SampleCapture is { } current && current.Id == captureId)
            SampleCapture = current with { State = "ended", EndReason = current.Active ? "stopped" : current.EndReason, EndedAt = SampleNow.AddSeconds(5) };
        return Task.FromResult(new DiagnosticCaptureReply(true, string.Empty, SampleCapture));
    }

    public Task<DiagnosticExportReply> ExportDiagnosticsAsync(DiagnosticExportScope scope, string? captureSessionId,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        DiagnosticExports.Add((scope, captureSessionId));
        progress?.Report(1);
        var detail = scope == DiagnosticExportScope.LatestCapture ? SampleCapture?.IncludeDetail == true : AllSessionsContainDetail;
        var document = $"{{\"schemaVersion\":2,\"containsDetail\":{(detail ? "true" : "false")},\"sessions\":[]}}";
        return Task.FromResult(new DiagnosticExportReply(true, string.Empty, Encoding.UTF8.GetBytes(document), detail,
            "moongate-diagnostics-sample.json"));
    }
}
