using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public enum DataBackupPurpose { Automatic, Manual, UpdateRecovery, RestoreRecovery }
public sealed record DataBackupRecord(DataSnapshotDescriptor Snapshot, DataBackupPurpose Purpose, bool Retained,
    UInt64String Revision, string State = "complete");

public sealed partial class DataBackupStore(SqliteStore database, PathService paths) : IStorageMaintenanceParticipant
{
    public static DatabaseMigration Migration { get; } = new("platform-backups", 1, """
        CREATE TABLE platform_data_backups(snapshot_id TEXT PRIMARY KEY,database_backup_id TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,purpose TEXT NOT NULL,retained INTEGER NOT NULL CHECK(retained IN (0,1)),
            revision TEXT NOT NULL,state TEXT NOT NULL CHECK(state IN ('complete','deleting')),descriptor TEXT NOT NULL) STRICT;
        """);
    public const int AutomaticRetentionCount = 1;
    public string Id => "backups";
    public IReadOnlyList<string> VolumeRoots => [paths.Backups];
    private bool Available => (long)database.ReadScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='platform_data_backups'")! == 1;
    public static StorageBackupSummary Summary(DataBackupRecord value) => new(value.Snapshot.SnapshotId,
        value.Purpose switch { DataBackupPurpose.Automatic => "automatic", DataBackupPurpose.Manual => "manual", DataBackupPurpose.UpdateRecovery => "updateRecovery", _ => "restoreRecovery" },
        value.State, value.Snapshot.CreatedAt, new((ulong)value.Snapshot.TotalSizeBytes),
        value.Snapshot.Database.Migrations.GroupBy(x => x.ModuleId, StringComparer.Ordinal).Select(x => new StorageSchemaVersion(x.Key, x.Max(s => s.Version))).ToArray(),
        value.Retained, value.Revision);

    internal async Task RegisterAsync(DataSnapshotDescriptor snapshot, DataBackupPurpose purpose,
        ActivityBarrier.MaintenanceLease? maintenance = null, CancellationToken ct = default)
    {
        if (!Available) return;
        var json = JsonSerializer.Serialize(snapshot, ContractJson.Options);
        Func<DatabaseTransaction, int> save = tx => tx.Execute("""
            INSERT INTO platform_data_backups VALUES($id,$database,$at,$purpose,$retained,'1','complete',$descriptor)
            """, ("$id", snapshot.SnapshotId), ("$database", snapshot.Database.BackupId), ("$at", snapshot.CreatedAt.ToString("O")),
            ("$purpose", purpose.ToString()), ("$retained", purpose == DataBackupPurpose.Automatic ? 0 : 1), ("$descriptor", json));
        if (maintenance is null) await database.WriteAsync(save, ct);
        else await database.WriteMaintenanceAsync(maintenance, save, ct);
    }

    public IReadOnlyList<DataBackupRecord> Records() => !Available ? [] : database.Read(ReadRecords);

    internal async Task CompleteUpdateRetentionAsync(DataSnapshotDescriptor current, IReadOnlySet<string> superseded,
        ActivityBarrier.MaintenanceLease maintenance, CancellationToken token = default)
    {
        if (!Available) return;
        var record = Records().SingleOrDefault(item => item.Snapshot.SnapshotId == current.SnapshotId);
        if (record is null) await RegisterAsync(current, DataBackupPurpose.UpdateRecovery, maintenance, token);
        else if (record.Purpose != DataBackupPurpose.UpdateRecovery || record.State != "complete" ||
            JsonSerializer.Serialize(record.Snapshot, ContractJson.Options) != JsonSerializer.Serialize(current, ContractJson.Options))
            throw new StorageException("storage.backupInvalid", "更新恢复快照登记与固定事务不一致");
        var released = Records().Where(item => item.Purpose == DataBackupPurpose.UpdateRecovery && superseded.Contains(item.Snapshot.SnapshotId) &&
            item.Snapshot.SnapshotId != current.SnapshotId && item.State == "complete")
            .Select(item => (Record: item, Revision: new UInt64String(checked(item.Revision.Value + 1)))).ToArray();
        await database.WriteMaintenanceAsync(maintenance, tx =>
        {
            foreach (var item in released)
                tx.Execute("UPDATE platform_data_backups SET purpose='Automatic',retained=0,revision=$next WHERE snapshot_id=$id AND purpose='UpdateRecovery' AND state='complete' AND revision=$revision",
                    ("$id", item.Record.Snapshot.SnapshotId), ("$next", item.Revision.ToString()), ("$revision", item.Record.Revision.ToString()));
            return 0;
        }, token);
    }

    private static IReadOnlyList<DataBackupRecord> ReadRecords(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT purpose,retained,revision,state,descriptor FROM platform_data_backups ORDER BY created_at DESC,snapshot_id DESC LIMIT 129";
        using var rows = command.ExecuteReader(); var result = new List<DataBackupRecord>();
        while (rows.Read())
        {
            if (result.Count >= SqliteStore.MaximumDataSnapshots || !Enum.TryParse<DataBackupPurpose>(rows.GetString(0), out var purpose))
                throw new StorageException("storage.backupInvalid", "备份登记数量或用途无效");
            var snapshot = JsonSerializer.Deserialize<DataSnapshotDescriptor>(rows.GetString(4), ContractJson.Options)
                ?? throw new StorageException("storage.backupInvalid", "备份登记缺少描述");
            result.Add(new(snapshot, purpose, rows.GetBoolean(1), UInt64String.Parse(rows.GetString(2)), rows.GetString(3)));
        }
        return result;
    }

    internal async Task RecoverCatalogAsync(DataSnapshotDescriptor currentBackup, CancellationToken ct = default)
    {
        var records = await database.ReadBackupDataAsync(currentBackup.Database, ReadRecords, ct);
        foreach (var record in records.Append(new(currentBackup, DataBackupPurpose.RestoreRecovery, true, new(1))))
            await database.WriteAsync(tx =>
            {
                tx.Execute("""
                    INSERT INTO platform_data_backups VALUES($id,$database,$at,$purpose,$retained,$revision,$state,$descriptor)
                    ON CONFLICT(snapshot_id) DO UPDATE SET purpose=excluded.purpose,retained=excluded.retained,
                        revision=excluded.revision,state=excluded.state,descriptor=excluded.descriptor
                    """, ("$id", record.Snapshot.SnapshotId), ("$database", record.Snapshot.Database.BackupId),
                    ("$at", record.Snapshot.CreatedAt.ToString("O")), ("$purpose", record.Purpose.ToString()), ("$retained", record.Retained ? 1 : 0),
                    ("$revision", record.Revision.ToString()), ("$state", record.State), ("$descriptor", JsonSerializer.Serialize(record.Snapshot, ContractJson.Options)));
                return 0;
            }, ct);
    }

    internal async Task DiscardUnclaimedRecoveryAsync(DataSnapshotDescriptor snapshot, ActivityBarrier.MaintenanceLease maintenance)
    {
        using var activity = database.Activities.EnterMaintenanceActivity(maintenance, "storage.discardRecovery");
        var record = Records().SingleOrDefault(x => x.Snapshot.SnapshotId == snapshot.SnapshotId);
        if (record is null) return;
        if (record.Purpose != DataBackupPurpose.RestoreRecovery || record.State != "complete" ||
            StorageMaintenancePlanStore.Hash(record.Snapshot) != StorageMaintenancePlanStore.Hash(snapshot))
            throw new StorageException("storage.backupInvalid", "待释放快照不属于本次独占恢复备份");
        if (File.Exists(Path.Combine(paths.BootstrapDirectory, DataMaintenanceJournal.Name)))
        {
            var journal = DataMaintenanceJournal.Read(paths.BootstrapDirectory);
            if (journal.CurrentBackup.Id == snapshot.SnapshotId || journal.RestoreBackup?.Id == snapshot.SnapshotId)
                throw new StorageException("resource.busy", "恢复快照已由持久维护事务接管");
        }
        await database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot);
        var proof = await HashTreeAsync(Tree(Roots(snapshot), CancellationToken.None), CancellationToken.None);
        var claimed = await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute(
            "UPDATE platform_data_backups SET state='deleting' WHERE snapshot_id=$id AND state='complete' AND purpose='RestoreRecovery' AND revision=$revision",
            ("$id", snapshot.SnapshotId), ("$revision", record.Revision.ToString())));
        if (claimed != 1) throw new StorageException("storage.backupInvalid", "恢复快照登记在释放前改变");
        foreach (var file in proof.Files)
            if (!(await StorageFileDeletion.DeleteAsync(file.Path, file.Bytes, file.Revision, file.Hash!, CancellationToken.None)).Deleted)
                throw new StorageException("storage.unavailable", "恢复快照文件在释放前改变或被占用");
        foreach (var directory in proof.Directories.OrderByDescending(x => x.Path.Length))
        {
            StoragePaths.ValidateLocal(directory.Path); Directory.Delete(directory.Path, false);
        }
        await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute(
            "DELETE FROM platform_data_backups WHERE snapshot_id=$id AND state='deleting' AND purpose='RestoreRecovery'", ("$id", snapshot.SnapshotId)));
    }

    public Task<IReadOnlyList<StorageManagedItem>> InspectAsync(CancellationToken ct = default)
    {
        var records = Records();
        var protectedIds = records.Where(x => x.State == "complete" && x.Purpose == DataBackupPurpose.Automatic)
            .Take(AutomaticRetentionCount).Select(x => x.Snapshot.SnapshotId).ToHashSet(StringComparer.Ordinal);
        if (records.FirstOrDefault(x => x.State == "complete") is { } newest) protectedIds.Add(newest.Snapshot.SnapshotId);
        var previews = (long)database.ReadScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='platform_storage_maintenance_plans'")! == 0 ? [] : database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json_extract(record,'$.plan.backup.id') FROM platform_storage_maintenance_plans WHERE expires>$now AND json_extract(record,'$.plan.state')='planned'";
            command.Parameters.AddWithValue("$now", database.StorageClock.GetUtcNow().ToUnixTimeMilliseconds());
            using var rows = command.ExecuteReader(); var result = new List<string>(); while (rows.Read()) if (!rows.IsDBNull(0)) result.Add(rows.GetString(0)); return result;
        });
        var result = new List<StorageManagedItem>(); var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            var roots = Roots(record.Snapshot); foreach (var root in roots) represented.Add(root);
            var proof = Tree(roots, ct);
            var protection = record.State != "complete" ? "incompleteBackupCleanup" :
                record.Retained ? "backupRecovery" : previews.Contains(record.Snapshot.SnapshotId) ? "backupRestorePreview" : protectedIds.Contains(record.Snapshot.SnapshotId) ? "backupRetention" :
                roots.Any(root => !Directory.Exists(root)) ? "backupMissing" : null;
            result.Add(new(record.Snapshot.SnapshotId, "backups", "core", proof.Files.Sum(x => x.Bytes),
                record.Revision + ":" + Digest(proof), record.Snapshot.CreatedAt, protection is null,
                "automaticBackupRetention", protection is null ? [] : [protection]));
        }
        if (Directory.Exists(paths.Backups))
            foreach (var entry in Tree([paths.Backups], ct).Files)
            {
                if (represented.Any(root => StoragePaths.IsWithin(entry.Path, root))) continue;
                if (result.Count >= 100000) throw new StorageException("resource.capacityExceeded", "备份用量超过巡检预算");
                result.Add(new("unknown/" + entry.Relative, "backups", "core", entry.Bytes, entry.Revision,
                    new DateTimeOffset(File.GetLastWriteTimeUtc(entry.Path), TimeSpan.Zero), false,
                    "unregisteredBackup", ["backupRecovery"]));
            }
        return Task.FromResult<IReadOnlyList<StorageManagedItem>>(result);
    }

    public async Task<StorageManagedItem> FreezeAsync(StorageManagedItem item, CancellationToken ct = default)
    {
        var record = Records().SingleOrDefault(x => x.Snapshot.SnapshotId == item.Key)
            ?? throw new StorageException("storage.backupInvalid", "清理备份登记不存在");
        await database.VerifyDataSnapshotAsync(record.Snapshot.SnapshotId, record.Snapshot, ct);
        return item with { ContentHash = Digest(await HashTreeAsync(Tree(Roots(record.Snapshot), ct), ct)) };
    }

    public async Task<StorageDeletion> DeleteAsync(StorageManagedItem expected, ActivityBarrier.MaintenanceLease maintenance, CancellationToken ct = default)
    {
        using var activity = database.Activities.EnterMaintenanceActivity(maintenance, "storage.backupCleanup");
        var item = (await InspectAsync(ct)).SingleOrDefault(x => x.Key == expected.Key);
        if (item is null) return new(false, 0, "missing");
        if (!item.Eligible) return new(false, 0, item.ProtectionReasons.FirstOrDefault() ?? "backupRetention");
        if (item.Revision != expected.Revision) return new(false, 0, "changed");
        var record = Records().Single(x => x.Snapshot.SnapshotId == expected.Key);
        await database.VerifyDataSnapshotAsync(record.Snapshot.SnapshotId, record.Snapshot, ct);
        var roots = Roots(record.Snapshot); var proof = await HashTreeAsync(Tree(roots, ct), ct);
        if (Digest(proof) != expected.ContentHash) return new(false, 0, "changed");
        var claimed = await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute(
            "UPDATE platform_data_backups SET state='deleting' WHERE snapshot_id=$id AND state='complete' AND retained=0 AND purpose='Automatic' AND revision=$revision",
            ("$id", expected.Key), ("$revision", record.Revision.ToString())), ct);
        if (claimed != 1) return new(false, 0, "backupRecovery");
        long reclaimed = 0;
        foreach (var file in proof.Files)
        {
            var removed = await StorageFileDeletion.DeleteAsync(file.Path, file.Bytes, file.Revision, file.Hash!, ct);
            reclaimed += removed.ReclaimedBytes;
            if (!removed.Deleted) return new(false, reclaimed, removed.Reason ?? "incompleteBackupCleanup");
        }
        try
        {
            foreach (var directory in proof.Directories.OrderByDescending(x => x.Path.Length))
            {
                StoragePaths.ValidateLocal(directory.Path);
                Directory.Delete(directory.Path, recursive: false);
            }
        }
        catch (IOException) { return new(false, reclaimed, "incompleteBackupCleanup"); }
        await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute("DELETE FROM platform_data_backups WHERE snapshot_id=$id AND state='deleting'",
            ("$id", expected.Key)));
        return new(true, reclaimed);
    }

    private string[] Roots(DataSnapshotDescriptor snapshot) =>
        [paths.Resolve("backups", "data", snapshot.SnapshotId), paths.Resolve("backups", "database", snapshot.Database.BackupId)];
    private sealed record ProofFile(string Relative, string Path, long Bytes, string Revision, string? Hash = null);
    private sealed record ProofDirectory(string Relative, string Path);
    private sealed record TreeProof(IReadOnlyList<ProofFile> Files, IReadOnlyList<ProofDirectory> Directories);

    private static TreeProof Tree(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var files = new List<ProofFile>(); var directories = new List<ProofDirectory>();
        for (var index = 0; index < roots.Count; index++)
        {
            var root = StoragePaths.ValidateLocal(roots[index]);
            if (!Directory.Exists(root)) continue;
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                ct.ThrowIfCancellationRequested(); StoragePaths.ValidateLocal(directory);
                directories.Add(new(index + "/" + Path.GetRelativePath(root, directory).Replace('\\', '/'), directory));
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested(); StoragePaths.ValidateLocal(path);
                    if (directories.Count + files.Count + pending.Count >= SqliteStore.MaximumSnapshotEntries + 10)
                        throw new StorageException("resource.capacityExceeded", "备份目录超过巡检预算");
                    if (Directory.Exists(path)) pending.Push(path);
                    else
                    {
                        var bytes = new FileInfo(path).Length;
                        files.Add(new(index + "/" + Path.GetRelativePath(root, path).Replace('\\', '/'), path, bytes, StorageFileDeletion.FileRevision(path, bytes)));
                    }
                }
            }
        }
        return new(files.OrderBy(x => x.Relative, StringComparer.Ordinal).ToArray(), directories.OrderBy(x => x.Relative, StringComparer.Ordinal).ToArray());
    }
    private static async Task<TreeProof> HashTreeAsync(TreeProof proof, CancellationToken ct)
    {
        var files = new List<ProofFile>();
        foreach (var file in proof.Files) files.Add(file with { Hash = await StorageFileDeletion.HashAsync(file.Path, ct) });
        return proof with { Files = files };
    }
    private static string Digest(TreeProof proof) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Files = proof.Files.Select(x => new { x.Relative, x.Bytes, x.Revision, x.Hash }), Directories = proof.Directories.Select(x => x.Relative)
    }, ContractJson.Options))));
}
