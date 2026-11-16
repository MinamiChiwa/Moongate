using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

/// <summary>调试采集登记与已落盘记录查询。</summary>
public sealed partial class DiagnosticStore
{
    public Task<int> SaveCaptureAsync(DiagnosticCaptureSnapshot capture, DiagnosticRuntimeSnapshot? startRuntime, DiagnosticRuntimeSnapshot? endRuntime,
        CancellationToken ct = default, ActivityBarrier.MaintenanceLease? maintenance = null)
    {
        int Write(DatabaseTransaction tx) => tx.Execute("""
            INSERT INTO platform_diagnostic_captures VALUES($id,$session,$started,$json,$start,$end)
            ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot,start_runtime=coalesce(excluded.start_runtime,start_runtime),
                end_runtime=coalesce(excluded.end_runtime,end_runtime)
            """, ("$id", capture.Id), ("$session", capture.SessionId), ("$started", capture.StartedAt.ToUnixTimeMilliseconds()),
            ("$json", JsonSerializer.Serialize(capture, ContractJson.Options)),
            ("$start", startRuntime is null ? null : JsonSerializer.Serialize(startRuntime, ContractJson.Options)),
            ("$end", endRuntime is null ? null : JsonSerializer.Serialize(endRuntime, ContractJson.Options)));
        return maintenance is null ? database.WriteAsync(Write, ct) : database.WriteMaintenanceAsync(maintenance, Write, ct);
    }
    public DiagnosticCaptureSnapshot? Capture(string id) =>
        database.ReadScalar("SELECT snapshot FROM platform_diagnostic_captures WHERE id=$id", ("$id", id)) is string json
            ? JsonSerializer.Deserialize<DiagnosticCaptureSnapshot>(json, ContractJson.Options) : null;
    public DiagnosticCaptureSnapshot? LatestCapture() =>
        database.ReadScalar("SELECT snapshot FROM platform_diagnostic_captures ORDER BY started DESC, id DESC LIMIT 1") is string json
            ? JsonSerializer.Deserialize<DiagnosticCaptureSnapshot>(json, ContractJson.Options) : null;
    private DiagnosticCaptureExport? CaptureForSession(string sessionId) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot,start_runtime,end_runtime FROM platform_diagnostic_captures WHERE session_id=$id";
        command.Parameters.AddWithValue("$id", sessionId);
        using var rows = command.ExecuteReader();
        if (!rows.Read()) return null;
        DiagnosticRuntimeSnapshot? Runtime(int index) => rows.IsDBNull(index) ? null
            : JsonSerializer.Deserialize<DiagnosticRuntimeSnapshot>(rows.GetString(index), ContractJson.Options);
        return new DiagnosticCaptureExport(JsonSerializer.Deserialize<DiagnosticCaptureSnapshot>(rows.GetString(0), ContractJson.Options)!, Runtime(1), Runtime(2));
    });
    /// <summary>宿主重启时仍登记为进行中的采集按中断收尾；采集会话本身由会话恢复标为 interrupted。</summary>
    private async Task RecoverCapturesAsync(CancellationToken ct)
    {
        var active = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT snapshot FROM platform_diagnostic_captures WHERE json_extract(snapshot,'$.state')='active'";
            using var rows = command.ExecuteReader(); var result = new List<DiagnosticCaptureSnapshot>();
            while (rows.Read()) result.Add(JsonSerializer.Deserialize<DiagnosticCaptureSnapshot>(rows.GetString(0), ContractJson.Options)!);
            return result;
        });
        foreach (var capture in active)
            await SaveCaptureAsync(capture with { State = "interrupted", EndReason = "restarted" }, null, null, ct);
    }

    public async Task<ApiList<DiagnosticRecordEntry>> QueryAsync(string principal, bool owner, bool debug, DiagnosticRecordQuery query,
        string revision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        static bool Codes(IReadOnlyList<string>? values, Func<string, bool> valid) => values is null || values.Count <= 32 && values.All(valid);
        (long Ticks, string Id, int Index)? cursor = null;
        if (query.Order is not ("asc" or "desc")) throw new OperationException("request.invalid", "诊断记录顺序无效");
        var descending = query.Order == "desc";
        if (query.Cursor is not null)
        {
            var match = Regex.Match(query.Cursor, @"\A(d-)?([0-9]{1,19})-([0-9a-f]{32})-([0-9]{1,9})\z");
            if (!match.Success || match.Groups[1].Success != descending || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                throw new OperationException("request.invalid", "诊断记录游标无效");
            cursor = (ticks, match.Groups[3].Value, int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
        }
        if (query.Limit is < 1 or > MaximumQueryRecords || query.SessionId is not null && !Regex.IsMatch(query.SessionId, @"\A[0-9a-f]{32}\z") ||
            !Codes(query.Modules, DiagnosticSanitizer.IsProbeModule) || !Codes(query.Levels, DiagnosticSanitizer.Levels.Contains) ||
            !Codes(query.Events, x => DiagnosticEvents.Find(x) is not null) ||
            query.TraceId is not null && !DiagnosticSanitizer.IsTraceId(query.TraceId) ||
            query.OperationId is not null && !DiagnosticSanitizer.IsOperationId(query.OperationId) ||
            query.From is { } from && query.To is { } to && from > to)
            throw new OperationException("request.invalid", "诊断记录查询条件无效");
        // 采集会话可能带文字详情，只对具备 diagnostics.debug 的主体可见。
        var sessions = Sessions().Where(x => x.State is not ("deleting" or "damaged") && (owner || x.Owner == principal) && (!IsCapture(x) || debug))
            .OrderBy(x => x.StartedAt.UtcTicks).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        if (descending) sessions.Reverse();
        if (query.SessionId is not null)
        {
            sessions.RemoveAll(x => x.Id != query.SessionId);
            if (sessions.Count == 0) throw new OperationException("resource.notFound", "诊断会话不存在");
        }
        var items = new List<DiagnosticRecordEntry>(); var bytes = 0; string? next = null;
        foreach (var session in sessions)
        {
            if (cursor is { } position && (session.StartedAt.UtcTicks, session.Id).CompareTo((position.Ticks, position.Id)) is var order &&
                (descending ? order > 0 : order < 0)) continue;
            var atCursor = cursor is { } at && at.Ticks == session.StartedAt.UtcTicks && at.Id == session.Id;
            IReadOnlyList<DiagnosticRecord> records;
            try { records = await ReadRecordsAsync(session, ct); }
            catch (StorageException error) { throw new OperationException(error.Code, error.Message); }
            var first = atCursor ? cursor!.Value.Index : descending ? records.Count - 1 : 0;
            if (descending) first = Math.Min(first, records.Count - 1);
            for (var index = first; descending ? index >= 0 : index < records.Count; index += descending ? -1 : 1)
            {
                var record = records[index];
                if (query.Modules is { } modules && !modules.Contains(record.Module) || query.Levels is { } levels && !levels.Contains(record.Level) ||
                    query.Events is { } events && (record.Event is null || !events.Contains(record.Event)) ||
                    query.TraceId is not null && record.TraceId != query.TraceId || query.OperationId is not null && record.OperationId != query.OperationId ||
                    query.From is { } lower && record.OccurredAt < lower || query.To is { } upper && record.OccurredAt > upper) continue;
                var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(record, ContractJson.Options)) + 64;
                if (items.Count >= query.Limit || items.Count > 0 && bytes + size > MaximumQueryBytes)
                { next = (descending ? "d-" : "") + session.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "-" + session.Id + "-" + index.ToString(CultureInfo.InvariantCulture); break; }
                items.Add(new(session.Id, record)); bytes += size;
            }
            if (next is not null) break;
        }
        return new(items, next, revision);
    }
}
