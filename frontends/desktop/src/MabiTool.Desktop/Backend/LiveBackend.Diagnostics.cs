using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Pages.Diagnostics;

namespace MabiTool.Desktop.Backend;

// 诊断页的后端读写。读取只在页面内显示，不写活动胶囊；错误以错误码交给页面，由页面映射文字。
public sealed partial class LiveBackend
{
    private const string NotConnected = "frontend.notConnected";

    private DiagnosticAccess diagnosticAccess = new(false, false, false);

    public event EventHandler<DiagnosticPushedRecord>? DiagnosticRecordReceived;
    public event EventHandler? DiagnosticStreamRestored;

    public DiagnosticAccess GetDiagnosticAccess()
    {
        lock (gate) return diagnosticAccess;
    }

    internal static DiagnosticAccess DiagnosticAccessOf(AuthSession session) => new(true,
        session.Permissions.Contains(ApiPermissions.DiagnosticsDebug, StringComparer.Ordinal),
        session.Permissions.Contains(ApiPermissions.DiagnosticsExport, StringComparer.Ordinal));

    // 有 diagnostics.debug 时才订阅实时记录；该事件不唤醒例行刷新。
    private EventStreamOptions StreamOptions()
    {
        bool debug;
        lock (gate) debug = diagnosticAccess.CanDebug;
        return debug ? DesktopEvents with { Topics = [.. DesktopEvents.Topics, "diagnostics.record"] } : DesktopEvents;
    }

    private void NotifyDiagnosticStream() => dispatch(() => DiagnosticStreamRestored?.Invoke(this, EventArgs.Empty));

    private void ReceiveDiagnosticRecord(StreamMessage message)
    {
        if (!GetDiagnosticAccess().CanDebug) return;
        EventEnvelope<DiagnosticRecord>? envelope;
        try { envelope = message.Data.Deserialize<EventEnvelope<DiagnosticRecord>>(ContractJson.Options); }
        catch (JsonException) { return; }
        if (envelope is not { Type: "diagnostics.record", Data: { } record }) return;
        var pushed = new DiagnosticPushedRecord(envelope.ResourceId, MapRecord(string.Empty, record));
        dispatch(() => DiagnosticRecordReceived?.Invoke(this, pushed));
    }

    // 页面读取：未连接或失败时返回错误码，不触碰活动胶囊。
    private async Task<(T? Value, string Message)> DiagnosticCallAsync<T>(Func<MabiToolClient, Task<T>> call) where T : class
    {
        try
        {
            var api = client;
            if (api is null || !Read().State.Connected) return (null, NotConnected);
            return (await call(api), string.Empty);
        }
        catch (Exception error) when (error is MabiToolApiException or HttpRequestException or IOException
                                          or OperationCanceledException or InvalidDataException or JsonException or DiagnosticFailure or FormatException)
        {
            return (null, ErrorCode(error));
        }
    }

    private sealed class DiagnosticFailure(string code) : Exception(code);

    private static string ErrorCode(Exception error) =>
        error is MabiToolApiException api ? api.Problem?.Code ?? $"http.{api.Status}"
        : error is HttpRequestException or IOException ? "frontend.notConnected"
        : error is TimeoutException or OperationCanceledException ? "frontend.timeout" : "frontend.requestFailed";

    public async Task<DiagnosticRuntimeReply> ReadDiagnosticRuntimeAsync(CancellationToken cancellationToken)
    {
        var (value, message) = await DiagnosticCallAsync(api => api.GetDiagnosticRuntimeAsync(cancellationToken));
        return value is null ? new(null, message) : new(MapRuntime(value), string.Empty);
    }

    public async Task<DiagnosticRecordsReply> QueryDiagnosticRecordsAsync(DiagnosticRecordFilter filter, CancellationToken cancellationToken)
    {
        var (value, message) = await DiagnosticCallAsync(api => api.QueryDiagnosticRecordsAsync(new(filter.SessionId,
            filter.Modules.Count == 0 ? null : filter.Modules, filter.Levels.Count == 0 ? null : filter.Levels,
            null, null, null, filter.From, null, filter.Cursor, filter.Limit, filter.Order), cancellationToken));
        return value is null
            ? new(false, message, [], null)
            : new(true, string.Empty, [.. value.Items.Select(item => MapRecord(item.SessionId, item.Record))], value.NextCursor);
    }

    public async Task<DiagnosticCaptureReply> GetDiagnosticCaptureAsync(CancellationToken cancellationToken)
    {
        var (value, message) = await DiagnosticCallAsync(api => api.GetDiagnosticCaptureAsync(cancellationToken));
        return value is null ? new(false, message, null) : new(true, string.Empty, value.Capture is { } capture ? MapCapture(capture) : null);
    }

    public async Task<DiagnosticCaptureReply> StartDiagnosticCaptureAsync(DebugCaptureSettings settings, CancellationToken cancellationToken)
    {
        var (value, message) = await DiagnosticCallAsync(api => WithStorageRetryAsync(
            () => api.StartDiagnosticCaptureAsync(new(settings.Modules, settings.Level, settings.DurationSeconds, settings.IncludeDetail),
                IdempotencyKey(), cancellationToken), cancellationToken));
        return value is null ? new(false, message, null) : new(true, string.Empty, MapCapture(value));
    }

    public async Task<DiagnosticCaptureReply> StopDiagnosticCaptureAsync(string captureId, CancellationToken cancellationToken)
    {
        var (value, message) = await DiagnosticCallAsync(api => api.StopDiagnosticCaptureAsync(captureId, cancellationToken));
        return value is null ? new(false, message, null) : new(true, string.Empty, MapCapture(value));
    }

    // 导出：先取会话与修订，提交后轮询 Operation，完成后读取导出资产的全部分段。文档只在内存里，写盘由页面完成。
    public async Task<DiagnosticExportReply> ExportDiagnosticsAsync(DiagnosticExportScope scope, string? captureSessionId,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (bytes, message) = await DiagnosticCallAsync(async api =>
        {
            var summary = await api.GetDiagnosticsAsync(cancellationToken);
            var sessions = summary.Sessions
                .Where(session => scope == DiagnosticExportScope.AllSessions || session.Id == captureSessionId)
                .Select(session => new DiagnosticSessionVersion(session.Id, session.Revision)).ToList();
            if (sessions.Count == 0) throw new DiagnosticFailure("resource.notFound");
            var operation = await WithStorageRetryAsync(
                () => api.ExportDiagnosticsAsync(new(sessions), IdempotencyKey(), cancellationToken), cancellationToken);
            progress?.Report(0.02);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (operation.State.KnownValue is OperationState.Queued or OperationState.Running or OperationState.CancelRequested or OperationState.WaitingUser)
            {
                if (DateTimeOffset.UtcNow > deadline) throw new DiagnosticFailure("diagnostics.exportTimeout");
                await Task.Delay(500, cancellationToken);
                operation = await api.GetOperationAsync(operation.Id, cancellationToken);
                progress?.Report(0.02 + 0.38 * Math.Clamp(operation.Progress ?? 0, 0, 1));
            }
            if (operation.State.KnownValue != OperationState.Succeeded || operation.Result is not { } result)
                throw new DiagnosticFailure(operation.Problem?.Code ?? "system.unexpectedError");
            var artifact = result.Deserialize<DiagnosticExportArtifact>(ContractJson.Options)
                ?? throw new DiagnosticFailure("diagnostics.exportInvalid");
            var export = await api.GetDiagnosticExportAsync(artifact.Id, cancellationToken);
            var total = (long)export.Asset.SizeBytes.Value;
            using var content = new MemoryStream();
            while (content.Length < total)
            {
                var chunk = await api.ReadDiagnosticExportAsync(export.Id, new((int)content.Length, CaptureChunkBytes), cancellationToken);
                var part = Convert.FromBase64String(chunk.ContentBase64);
                if (part.Length == 0) throw new DiagnosticFailure("diagnostics.exportTruncated");
                content.Write(part);
                progress?.Report(0.4 + 0.6 * content.Length / Math.Max(1, total));
            }
            return content.ToArray();
        });
        if (bytes is null) return new(false, message, null, false, string.Empty);
        return new(true, string.Empty, bytes, ContainsDetail(bytes),
            $"moongate-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    private static bool ContainsDetail(byte[] document)
    {
        try
        {
            using var parsed = JsonDocument.Parse(document);
            return parsed.RootElement.TryGetProperty("containsDetail", out var flag) && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    internal static RuntimeView MapRuntime(DiagnosticRuntimeSnapshot snapshot) => new(snapshot.CapturedAt, snapshot.BackendEpoch,
        [.. snapshot.Probes.Select(probe => new ProbeView(probe.Id, probe.Module, probe.State, probe.Reason,
            [.. probe.Counters.Select(item => new KeyValue(item.Key, item.Value.ToString()))],
            [.. probe.Values.Select(item => new KeyValue(item.Key, item.Value))],
            probe.TruncatedEntries, probe.RejectedEntries))],
        snapshot.TruncatedProbes, snapshot.RejectedProbes);

    internal static DiagnosticRecordView MapRecord(string sessionId, DiagnosticRecord record) => new(sessionId, record.OccurredAt,
        record.Level, record.Module, record.Component, record.Event, record.Code,
        [.. (record.Fields ?? new Dictionary<string, string>()).Select(item => new KeyValue(item.Key, item.Value))],
        record.Sequence?.ToString(), record.Detail);

    internal static DiagnosticCaptureView MapCapture(DiagnosticCaptureSnapshot capture) => new(capture.Id, capture.SessionId,
        capture.Modules, capture.Level, capture.IncludeDetail, capture.State, capture.EndReason, capture.StartedAt,
        capture.ExpiresAt, capture.EndedAt, capture.Records.Value, capture.DroppedRecords.Value, capture.PushDropped.Value,
        capture.DetailBytes.Value, capture.DetailTruncated.Value);
}
