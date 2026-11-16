using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DataSnapshotReference(string Id, string Digest);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DataMaintenanceRecord(int Version, string Id, string OriginalRoot, string RootId, string DatabaseId,
    StorageMaintenancePlanRecord Preview, OperationSnapshot Operation, string Key, string RequestHash,
    DataSnapshotReference CurrentBackup, DataSnapshotReference? RestoreBackup, string Phase = "prepared", bool RolledBack = false,
    DataSnapshotRestorePlan? FilePlan = null, DatabaseRestoreResult? DatabaseResult = null, DataSnapshotRestoreResult? FileResult = null,
    DataSnapshotRestorePlan? RollbackFilePlan = null, DatabaseRestoreResult? RollbackDatabaseResult = null,
    DataSnapshotRestoreResult? RollbackFileResult = null, string? TreeDigest = null, ApiProblem? Problem = null);

internal static class DataMaintenanceJournal
{
    internal const string Name = "data-maintenance.json";
    private const int MaximumBytes = 256 * 1024;
    public static bool HasPending(string bootstrap)
    {
        var file = Path.Combine(bootstrap, Name);
        return File.Exists(file) && Read(bootstrap).Phase != "committed";
    }
    public static DataMaintenanceRecord Read(string bootstrap)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var file = StoragePaths.ValidateLocal(Path.Combine(bootstrap, Name)); LocalDiscoverySecurity.ValidateFile(file);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumBytes) throw new StorageException("storage.assetDamaged", "数据维护日志长度无效");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes, MaximumBytes);
        var value = parsed.RootElement.Deserialize<DataMaintenanceRecord>(ContractJson.Options) ?? throw new StorageException("storage.assetDamaged", "数据维护日志为空");
        if (value.Version != 1 || !Guid.TryParseExact(value.Id, "N", out _) || !Guid.TryParseExact(value.RootId, "N", out _) ||
            !Guid.TryParseExact(value.DatabaseId, "N", out _) || value.Preview.Plan.Owner != value.Operation.Owner ||
            value.Operation.Kind != "storage." + value.Preview.Plan.Kind || value.Preview.Plan.Kind is not ("restore" or "migrate") ||
            value.Phase is not ("prepared" or "filesPrepared" or "databaseRestored" or "filesRestored" or "copyPrepared" or "copied" or "originalRetained" or "pointerSwitched" or "healthChecking" or "healthy" or "rollbackStarted" or "rolledBack" or "committed"))
            throw new StorageException("storage.assetDamaged", "数据维护日志身份或阶段无效");
        if (!Path.IsPathFullyQualified(value.OriginalRoot) || value.OriginalRoot.Length > 4096 ||
            value.Preview.Target is { } target && (!Path.IsPathFullyQualified(target.Path) || target.Path.Length > 4096))
            throw new StorageException("storage.pathRejected", "数据维护日志路径无效");
        return value;
    }
    public static void Save(string bootstrap, DataMaintenanceRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, ContractJson.Options);
        if (bytes.Length > MaximumBytes) throw new StorageException("storage.quotaExceeded", "数据维护日志超限");
        var destination = StoragePaths.ValidateLocal(Path.Combine(bootstrap, Name));
        var temporary = Path.Combine(bootstrap, "data-maintenance-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static FileStream Lock(string bootstrap)
    {
        var path = StoragePaths.ValidateLocal(Path.Combine(bootstrap, "data-maintenance.lock"));
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new StorageException("resource.busy", "数据维护或恢复已由另一宿主执行", error); }
    }
}
