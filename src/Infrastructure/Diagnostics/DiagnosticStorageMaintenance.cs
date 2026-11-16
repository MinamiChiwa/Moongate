using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed partial class DiagnosticStore : IStorageMaintenanceParticipant
{
    string IStorageMaintenanceParticipant.Id => "diagnostics";
    IReadOnlyList<string> IStorageMaintenanceParticipant.VolumeRoots => [paths.Diagnostics, paths.Logs];
    public Task<IReadOnlyList<StorageManagedItem>> InspectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var policy = Policy().Policy; var all = Sessions();
        var protectedSources = database.Read(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT DISTINCT session_id FROM platform_diagnostic_sources";
            using var rows = command.ExecuteReader(); var ids = new HashSet<string>(StringComparer.Ordinal);
            while (rows.Read()) ids.Add(rows.GetString(0)); return ids;
        });
        var result = new List<StorageManagedItem>(); var completed = all.Count(x => x.State != "active");
        var remaining = all.Sum(x => (long)x.EventBytes.Value);
        foreach (var session in all.OrderBy(x => x.LastWrittenAt).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var protection = session.State == "active" ? "activeTask" : protectedSources.Contains(session.Id) ? "businessReference" :
                time.GetUtcNow() - session.LastWrittenAt < TimeSpan.FromSeconds(policy.RecentProtectionSeconds) ? "recentWrite" : null;
            var eligible = protection is null && (session.State == "deleting" || completed > policy.CompletedSessions || remaining > (long)policy.SessionBudgetBytes!.Value.Value);
            if (eligible) { completed--; remaining -= (long)session.EventBytes.Value; }
            result.Add(new("session/" + session.Id, "diagnostics", session.Module, (long)session.EventBytes.Value,
                session.Revision.ToString() + ":" + session.State, session.LastWrittenAt, eligible, "diagnosticRetention",
                protection is null ? [] : [protection], session.EventSha256));
        }
        result.AddRange(Logs.StorageItems(policy));
        return Task.FromResult<IReadOnlyList<StorageManagedItem>>(result);
    }
    public Task<StorageManagedItem> FreezeAsync(StorageManagedItem item, CancellationToken ct = default) =>
        item.Key.StartsWith("log/", StringComparison.Ordinal) ? Logs.FreezeStorageAsync(item, ct) : Task.FromResult(item);

    public async Task<StorageDeletion> DeleteAsync(StorageManagedItem expected, ActivityBarrier.MaintenanceLease maintenance, CancellationToken ct = default)
    {
        using var activity = database.Activities.EnterMaintenanceActivity(maintenance, "diagnostic.storageCleanup");
        if (expected.Key.StartsWith("log/", StringComparison.Ordinal)) return await Logs.DeleteStorageAsync(expected, Policy().Policy, ct);
        if (!expected.Key.StartsWith("session/", StringComparison.Ordinal)) throw new StorageException("storage.pathRejected", "诊断清理身份无效");
        await gate.WaitAsync(ct);
        try
        {
            var current = (await InspectAsync(ct)).SingleOrDefault(x => x.Key == expected.Key);
            if (current is null) return new(false, 0, "missing");
            if (current.Revision != expected.Revision) return new(false, 0, "changed");
            if (!current.Eligible) return new(false, 0, current.ProtectionReasons.FirstOrDefault() ?? "retentionProtected");
            var id = expected.Key[8..]; var session = Sessions().Single(x => x.Id == id);
            var encoded = JsonSerializer.Serialize(session with { State = "deleting" }, ContractJson.Options);
            await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute(
                "UPDATE platform_diagnostic_sessions SET snapshot=$json WHERE id=$id", ("$json", encoded), ("$id", id)), ct);
            var path = EventPath(session); StorageDeletion result;
            try { result = await StorageFileDeletion.DeleteAsync(path, expected.Bytes, StorageFileDeletion.FileRevision(path, expected.Bytes), expected.ContentHash!, ct); }
            catch
            {
                var previous = JsonSerializer.Serialize(session, ContractJson.Options);
                await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute("UPDATE platform_diagnostic_sessions SET snapshot=$json WHERE id=$id", ("$json", previous), ("$id", id)));
                throw;
            }
            var restored = JsonSerializer.Serialize(session, ContractJson.Options);
            await database.WriteMaintenanceAsync(maintenance, tx => result.Deleted || result.Reason == "missing"
                ? tx.Execute("DELETE FROM platform_diagnostic_sessions WHERE id=$id", ("$id", id))
                : tx.Execute("UPDATE platform_diagnostic_sessions SET snapshot=$json WHERE id=$id", ("$json", restored), ("$id", id)));
            var directory = Path.GetDirectoryName(path)!;
            if (result.Deleted && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            return result;
        }
        finally { gate.Release(); }
    }
}

public sealed partial class RollingDiagnosticLog
{
    internal IReadOnlyList<StorageManagedItem> StorageItems(DiagnosticRetentionPolicy policy) => Segments().Select(path =>
    {
        var file = new FileInfo(path); var protection = path == current ? "activeSegment" :
            time.GetUtcNow().UtcDateTime - file.LastWriteTimeUtc < TimeSpan.FromSeconds(policy.RecentProtectionSeconds) ? "recentWrite" : null;
        return new StorageManagedItem("log/" + file.Name, "logs", "core", file.Length, StorageFileDeletion.FileRevision(path, file.Length),
            new(file.LastWriteTimeUtc, TimeSpan.Zero), protection is null, "logRetention", protection is null ? [] : [protection]);
    }).ToArray();
    internal async Task<StorageManagedItem> FreezeStorageAsync(StorageManagedItem item, CancellationToken ct) =>
        item with { ContentHash = await StorageFileDeletion.HashAsync(LogStoragePath(item.Key), ct) };
    internal async Task<StorageDeletion> DeleteStorageAsync(StorageManagedItem expected, DiagnosticRetentionPolicy policy, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var live = StorageItems(policy).SingleOrDefault(x => x.Key == expected.Key);
            if (live is null) return new(false, 0, "missing");
            if (!live.Eligible) return new(false, 0, live.ProtectionReasons.FirstOrDefault());
            try { return await StorageFileDeletion.DeleteAsync(LogStoragePath(expected.Key), expected.Bytes, expected.Revision, expected.ContentHash!, ct); }
            finally { Remeasure(); }
        }
        finally { gate.Release(); }
    }
    private string LogStoragePath(string key)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"\Alog/log-[0-9a-f]{32}\.jsonl\z"))
            throw new StorageException("storage.pathRejected", "日志清理身份无效");
        return Path.Combine(Root, key[4..]);
    }
}
