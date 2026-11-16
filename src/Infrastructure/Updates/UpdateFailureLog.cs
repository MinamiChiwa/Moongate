using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

internal sealed record UpdateFailureEntry(string Code, string? Phase, string? PeerCheck, string? TransactionCallback,
    long? TransactionElapsedMs, int? PeerOpenError = null, string? ExceptionType = null, int? HResult = null,
    string? HandoffStage = null, string? HandoffMessage = null, long? HandoffElapsedMs = null,
    IReadOnlyList<UpdateHandoffTiming>? HandoffTimings = null);

internal sealed record UpdateHandoffEntry(string Phase, UpdateHandoffSnapshot Progress, string? TransactionId = null);

// 安装进程退出前保存固定字段，候选和恢复失败共用一份有界诊断。
internal static class UpdateFailureLog
{
    internal const string FileName = "install-failures.json";
    internal const string HandoffFileName = "install-handoffs.json";
    internal const int MaximumBytes = 65_536;
    internal const int MaximumEntries = 8;
    internal static string Code(Exception error) => error switch
    {
        UpdatePlanException e => e.Code, StorageException e => e.Code, OperationException e => e.Code,
        FileTreeException e => e.Code, TrustValidationException e => e.Code,
        MabiTool.ModuleSdk.ActivityBarrierException e => e.Code,
        OperationCanceledException => "operation.cancelled", TimeoutException => "update.handoffTimeout",
        _ => "update.recoveryRequired"
    };

    internal static Task RecordAsync(UpdateInstallIntent intent, Exception error, string? phase = null, string? code = null) =>
        RecordAsync(intent.StateRoot, error, phase, code);

    internal static async Task RecordAsync(string stateRoot, Exception error, string? phase = null, string? code = null)
    {
        try
        {
            var path = UpdatePaths.Local(stateRoot, FileName);
            var entries = new List<UpdateFailureEntry>();
            if (File.Exists(path) && new FileInfo(path).Length <= MaximumBytes)
                entries.AddRange(JsonSerializer.Deserialize<UpdateFailureEntry[]>(await File.ReadAllBytesAsync(path), UpdateHandoffProtocol.Json) ?? []);
            entries.Add(new(code is "update.hostHealthTargetInvalid" or "update.hostHealthMismatch" or
                "update.moduleHealthMismatch" or "update.hostHealthTimeout" ? code : Code(error), phase ?? error.Data["updatePhase"] as string,
                error.Data["peerCheck"] as string, error.Data["transactionCallback"] as string,
                error.Data["transactionElapsedMs"] is long elapsed ? elapsed : null,
                error.Data["peerOpenError"] is int native ? native : null, ExceptionType(error), error.HResult,
                Stage(error.Data["handoffStage"] as string), Message(error.Data["handoffMessage"] as string),
                error.Data["handoffElapsedMs"] is long handoffElapsed && handoffElapsed >= 0 ? handoffElapsed : null,
                Timings(error.Data["handoffTimings"] as IReadOnlyList<UpdateHandoffTiming>)));
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(entries.TakeLast(MaximumEntries), UpdateHandoffProtocol.Json);
                if (bytes.Length > MaximumBytes) return;
                await File.WriteAllBytesAsync(temporary, bytes);
                if (File.Exists(path)) await UpdatePaths.ReplaceOwnedFileAsync(temporary, path, CancellationToken.None); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    internal static async Task RecordHandoffAsync(string stateRoot, string phase, UpdateHandoffSnapshot progress, string? transactionId = null)
    {
        if (phase is not ("--update-candidate" or "--update-preflight" or "--update-rollback-health")) return;
        if (transactionId is not null && !UpdateHandoffProtocol.Id(transactionId)) return;
        try
        {
            var path = UpdatePaths.Local(stateRoot, HandoffFileName);
            var entries = new List<UpdateHandoffEntry>();
            if (File.Exists(path) && new FileInfo(path).Length <= MaximumBytes)
                entries.AddRange(JsonSerializer.Deserialize<UpdateHandoffEntry[]>(await File.ReadAllBytesAsync(path), UpdateHandoffProtocol.Json) ?? []);
            var timings = Timings(progress.Timings);
            if (Stage(progress.Stage) is null || progress.ElapsedMs < 0 || timings is null) return;
            entries.Add(new(phase, progress with { Timings = timings }, transactionId));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries.TakeLast(MaximumEntries), UpdateHandoffProtocol.Json);
            if (bytes.Length > MaximumBytes) return;
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes);
                if (File.Exists(path)) await UpdatePaths.ReplaceOwnedFileAsync(temporary, path, CancellationToken.None); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private static string? Stage(string? value) => UpdateHandoffProgress.IsStage(value) ? value : null;
    private static IReadOnlyList<UpdateHandoffTiming>? Timings(IReadOnlyList<UpdateHandoffTiming>? value) =>
        value is { Count: > 0 and <= UpdateHandoffProgress.MaximumTimings } &&
        value.All(item => item is not null && Stage(item.Stage) is not null && item.ElapsedMs >= 0)
            ? Array.AsReadOnly(value.ToArray()) : null;

    private static string? Message(string? value)
    {
        if (value is null) return null;
        var parts = value.Split(':');
        return parts.Length == 2 && (parts[0] is "send" or "receive") && UpdateHandoffProtocol.Kind(parts[1])
            ? value : null;
    }

    private static string ExceptionType(Exception error) => error.GetType().Name switch
    {
        "UpdatePlanException" or "StorageException" or "OperationException" or "FileTreeException" or
        "TrustValidationException" or "ActivityBarrierException" or "UpdateHostHealthException" or
        "IOException" or "EndOfStreamException" or "UnauthorizedAccessException" or "Win32Exception" or
        "InvalidOperationException" or "ArgumentException" or "JsonException" or "TimeoutException" or
        "OperationCanceledException" or "TaskCanceledException" or "SqliteException" => error.GetType().Name,
        _ => "Exception"
    };
}
