using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

internal sealed class DataMaintenanceInterruptionException : Exception { }

public sealed partial class StorageMaintenanceCoordinator
{
    public static Task<bool> RecoverAsync(StorageOptions options, IReadOnlyList<DatabaseMigration> migrations,
        IReadOnlyList<StorageDataDefinition> data, TimeProvider? clock = null, VolumeSpaceReservations? space = null, CancellationToken ct = default)
        => RecoverCoreAsync(options, migrations, data, clock ?? TimeProvider.System, space ?? VolumeSpaceReservations.Shared, ct);

    internal static async Task<bool> RecoverCoreAsync(StorageOptions options, IReadOnlyList<DatabaseMigration> migrations,
        IReadOnlyList<StorageDataDefinition> definitions, TimeProvider clock, VolumeSpaceReservations space, CancellationToken ct,
        Func<string, Task>? persisted = null)
    {
        ct.ThrowIfCancellationRequested();
        options = StartupOptions(options);
        if (!File.Exists(Path.Combine(options.BootstrapDirectory, DataMaintenanceJournal.Name))) return false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var bootstrap = StoragePaths.ValidateLocal(options.BootstrapDirectory);
        using var guard = DataMaintenanceJournal.Lock(bootstrap);
        var record = DataMaintenanceJournal.Read(bootstrap);
        var wasPending = record.Phase != "committed";
        async Task Save(DataMaintenanceRecord next)
        {
            record = next; DataMaintenanceJournal.Save(bootstrap, record);
            if (persisted is not null) await persisted(record.Phase);
        }
        if (record.Phase == "committed")
        {
            var activeRoot = record.RolledBack || record.Problem is not null ? record.OriginalRoot : record.Preview.Target?.Path ?? record.OriginalRoot;
            using var active = DataRootLease.OpenForMaintenance(bootstrap, activeRoot, record.RootId, record.DatabaseId, record.Id);
            await using var database = SqliteStore.Open(active, migrations, spaceReservations: space, clock: clock, mock: await BackendModeAsync(active.Paths.Database));
            await new OperationStore(database, clock).RecoverStorageReceiptAsync(record, Result(record));
            return false;
        }

        if (record.Preview.Plan.Kind == "restore")
        {
            using var source = DataRootLease.OpenForMaintenance(bootstrap, record.OriginalRoot, record.RootId, record.DatabaseId, record.Id);
            if (record.Phase is not ("healthy" or "healthChecking" or "rolledBack"))
            {
                using var ownership = source.AcquireDatabaseOwnership();
                try
                {
                    if (record.Phase == "rollbackStarted")
                        await RollbackRestore(source, ownership);
                    else
                    {
                        var snapshot = await SnapshotAsync(source.Paths, record.RestoreBackup ?? throw new StorageException("storage.backupInvalid", "恢复意图缺少固定备份"));
                        var filePlan = await SqliteStore.PrepareDataSnapshotRestoreOwnedAsync(source, ownership, record.Id, snapshot,
                            expected: record.FilePlan, completed: record.FileResult);
                        if (record.FilePlan is null) await Save(record with { FilePlan = filePlan, Phase = "filesPrepared" });
                        var restored = await SqliteStore.RestoreDatabaseBackupOwnedAsync(source, ownership, record.Id, snapshot.Database,
                            completed: record.DatabaseResult, persisted: async point => { if (persisted is not null) await persisted("database." + point); });
                        if (record.DatabaseResult is null) await Save(record with { DatabaseResult = restored, Phase = "databaseRestored" });
                        var files = await SqliteStore.RestoreDataSnapshotFilesOwnedAsync(source, ownership, filePlan, snapshot, completed: record.FileResult,
                            persisted: async point => { if (persisted is not null) await persisted("files." + point); });
                        if (record.FileResult is null) await Save(record with { FileResult = files, Phase = "filesRestored" });
                    }
                }
                catch (Exception error) when (Recoverable(error))
                {
                    if (record.Phase == "rollbackStarted") throw;
                    var changed = record.DatabaseResult is not null || File.Exists(source.Paths.Resolve("recovery", "database-" + record.Id + ".sqlite"));
                    await Save(record with { Problem = Problem(error, record.Operation.Id), RolledBack = changed, Phase = changed ? "rollbackStarted" : "rolledBack" });
                    if (changed) await RollbackRestore(source, ownership);
                }
            }
            try
            {
                if (record.Phase is not ("healthy" or "healthChecking")) await Save(record with { Phase = "healthChecking" });
                await HealthAsync(source, record, migrations, definitions, clock, space, preserveCatalog: true);
                if (persisted is not null) await persisted("health.checked");
                if (record.Phase != "healthy") await Save(record with { Phase = "healthy" });
            }
            catch (Exception error) when (Recoverable(error) && !record.RolledBack && record.Phase != "healthy")
            {
                await Save(record with { Problem = Problem(error, record.Operation.Id), RolledBack = true, Phase = "rollbackStarted" });
                using (var ownership = source.AcquireDatabaseOwnership()) await RollbackRestore(source, ownership);
                await HealthAsync(source, record, migrations, definitions, clock, space, preserveCatalog: true);
                await Save(record with { Phase = "healthy" });
            }
            await CommitAsync(source);
        }
        else
        {
            if (record.Phase == "healthy")
            {
                var activePath = record.RolledBack || record.Problem is not null ? record.OriginalRoot : record.Preview.Target!.Path;
                using var active = DataRootLease.OpenForMaintenance(bootstrap, activePath, record.RootId, record.DatabaseId, record.Id);
                await CommitAsync(active);
            }
            else
            {
                using var source = DataRootLease.OpenForMaintenance(bootstrap, record.OriginalRoot, record.RootId, record.DatabaseId, record.Id);
                DataRootLease? target = null;
                try
                {
                    if (record.Phase is "rollbackStarted" or "rolledBack")
                        await RollbackMigration(source, null);
                    else
                    {
                        var evidence = record.Preview.Target ?? throw new StorageException("storage.pathRejected", "迁移意图缺少目标目录");
                        if (StoragePaths.IsWithin(evidence.Path, source.Paths.DataRoot) || StoragePaths.IsWithin(source.Paths.DataRoot, evidence.Path) ||
                            StoragePaths.IsWithin(evidence.Path, bootstrap) || StoragePaths.IsWithin(bootstrap, evidence.Path))
                            throw new StorageException("storage.pathRejected", "迁移根须与原根及引导目录独立");
                        evidence.Verify(requireEmpty: false);
                        target = DataRootLease.OpenForMaintenance(bootstrap, evidence.Path, record.RootId, record.DatabaseId, record.Id, evidence);
                        if (record.Phase is "prepared" or "copyPrepared")
                        {
                            using var ownership = source.AcquireDatabaseOwnership();
                            if (record.Phase == "prepared")
                            {
                                await SqliteStore.CheckpointStorageRootAsync(source, ownership);
                                var tree = await StorageRootCopy.CaptureAsync(source.Paths.DataRoot, CancellationToken.None);
                                var digest = StorageRootCopy.Publish(bootstrap, record.Id, tree);
                                await Save(record with { TreeDigest = digest, Phase = "copyPrepared" });
                            }
                            var treeToCopy = StorageRootCopy.Load(bootstrap, record.Id, record.TreeDigest!);
                            using var reservation = space.Reserve("root-migration:" + record.Id,
                                new VolumeSpaceRequirement(target.Paths.DataRoot, checked(treeToCopy.Files.Sum(x => x.SizeBytes) * 6 / 5 + 64L * 1024 * 1024)));
                            await StorageRootCopy.CopyAsync(source.Paths.DataRoot, target.Paths.DataRoot, record.Id, treeToCopy, CancellationToken.None, persisted);
                            await Save(record with { Phase = "copied" });
                        }
                        var manifest = StorageRootCopy.Load(bootstrap, record.Id, record.TreeDigest!);
                        if (record.Phase == "copied")
                        {
                            await StorageRootCopy.VerifyAsync(target.Paths.DataRoot, manifest, false, CancellationToken.None);
                            source.SetAuthority("retained", record.Id);
                            await Save(record with { Phase = "originalRetained" });
                        }
                        if (record.Phase == "originalRetained")
                        {
                            DataRootLease.SwitchAuthorityPointer(bootstrap, record.RootId, record.OriginalRoot, target.Paths.DataRoot);
                            await Save(record with { Phase = "pointerSwitched" });
                        }
                        await StorageRootCopy.VerifyAsync(target.Paths.DataRoot, manifest, true, CancellationToken.None);
                        if (record.Phase != "healthChecking") await Save(record with { Phase = "healthChecking" });
                        await HealthAsync(target, record, migrations, definitions, clock, space, relocateFrom: source.Paths.DataRoot);
                        if (persisted is not null) await persisted("health.checked");
                        await Save(record with { Phase = "healthy" });
                        await CommitAsync(target);
                    }
                }
                catch (Exception error) when (Recoverable(error) && record.Phase is not ("healthy" or "committed"))
                {
                    if (record.Phase == "rollbackStarted") throw;
                    await Save(record with { Problem = Problem(error, record.Operation.Id), RolledBack = true, Phase = "rollbackStarted" });
                    await RollbackMigration(source, target);
                }
                finally { target?.Dispose(); }
                if (record.Phase == "rolledBack")
                {
                    await HealthAsync(source, record, migrations, definitions, clock, space);
                    await Save(record with { Phase = "healthy" });
                    await CommitAsync(source);
                }
            }
        }
        return wasPending;

        async Task RollbackRestore(DataRootLease lease, DataRootLease.DatabaseOwnership ownership)
        {
            var snapshot = await SnapshotAsync(lease.Paths, record.CurrentBackup);
            var id = RollbackId(record.Id);
            var plan = await SqliteStore.PrepareDataSnapshotRestoreOwnedAsync(lease, ownership, id, snapshot,
                expected: record.RollbackFilePlan, completed: record.RollbackFileResult);
            if (record.RollbackFilePlan is null) await Save(record with { RollbackFilePlan = plan, Phase = "rollbackStarted" });
            var restored = await SqliteStore.RestoreDatabaseBackupOwnedAsync(lease, ownership, id, snapshot.Database, completed: record.RollbackDatabaseResult,
                persisted: async point => { if (persisted is not null) await persisted("rollback.database." + point); });
            if (record.RollbackDatabaseResult is null) await Save(record with { RollbackDatabaseResult = restored });
            var files = await SqliteStore.RestoreDataSnapshotFilesOwnedAsync(lease, ownership, plan, snapshot, completed: record.RollbackFileResult,
                persisted: async point => { if (persisted is not null) await persisted("rollback.files." + point); });
            await Save(record with { RollbackFileResult = files, Phase = "rolledBack", RolledBack = true });
        }
        async Task RollbackMigration(DataRootLease source, DataRootLease? target)
        {
            try { target?.SetAuthority("retained", record.Id); }
            catch (Exception error) when (error is IOException || error is StorageException storage && storage.Code is "storage.rootMissing" or "storage.unsupportedVolume") { }
            source.SetAuthority("active", record.Id);
            DataRootLease.SwitchAuthorityPointer(bootstrap, record.RootId, record.Preview.Target!.Path, record.OriginalRoot);
            await Save(record with { Phase = "rolledBack", RolledBack = true });
        }
        async Task CommitAsync(DataRootLease lease)
        {
            await using var database = SqliteStore.Open(lease, migrations, mock: await BackendModeAsync(lease.Paths.Database), spaceReservations: space, clock: clock);
            await new OperationStore(database, clock).RecoverStorageReceiptAsync(record, Result(record));
            lease.SetAuthority("active", record.Id);
            await Save(record with { Phase = "committed" });
        }
    }

    private static async Task<DataSnapshotDescriptor> SnapshotAsync(PathService paths, DataSnapshotReference reference)
    {
        var snapshot = await SqliteStore.VerifyDataSnapshotAsync(paths, reference.Id, null, CancellationToken.None);
        if (StorageMaintenancePlanStore.Hash(snapshot) != reference.Digest) throw new StorageException("storage.backupInvalid", "数据维护固定快照被替换");
        return snapshot;
    }
    private static async Task HealthAsync(DataRootLease root, DataMaintenanceRecord record, IReadOnlyList<DatabaseMigration> migrations,
        IReadOnlyList<StorageDataDefinition> definitions, TimeProvider clock, VolumeSpaceReservations space, bool preserveCatalog = false, string? relocateFrom = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var mock = await BackendModeAsync(root.Paths.Database);
        await using var database = SqliteStore.Open(root, migrations, mock, spaceReservations: space, clock: clock);
        var expected = new[] { CoreMigrations.Initial, CoreMigrations.Operations, CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode }.Concat(migrations)
            .Select(x => new DatabaseMigrationStamp(x.ModuleId, x.Version, x.Checksum)).OrderBy(x => x.ModuleId, StringComparer.Ordinal).ThenBy(x => x.Version).ToArray();
        using (var maintenance = database.Activities.TryEnterMaintenance("data-health") ?? throw new StorageException("resource.busy", "离线健康检查无法取得维护权"))
            await database.VerifyDatabaseHealthAsync(maintenance, record.DatabaseId, mock ? "mock" : "live", expected);
        await database.VerifyStorageContentsAsync(CancellationToken.None);
        if (relocateFrom is not null)
        {
            var current = await new StorageDataCatalog(database, root.Paths, definitions).CaptureAsync();
            if (current.Digest != record.Preview.DataDigest) throw new StorageException("storage.planStale", "迁移后的业务数据与固定预览不匹配");
            await database.TemporaryFiles.RelocateAsync(relocateFrom, CancellationToken.None);
        }
        if (preserveCatalog) await new DataBackupStore(database, root.Paths).RecoverCatalogAsync(await SnapshotAsync(root.Paths, record.CurrentBackup));
    }
    private static Task<bool> BackendModeAsync(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT mode FROM core_backend_mode WHERE singleton=1";
        var mode = command.ExecuteScalar() as string;
        if (mode is not ("live" or "mock")) throw new StorageException("storage.databaseInvalid", "维护数据库缺少后端模式");
        return Task.FromResult(mode == "mock");
    }
    private static StorageMaintenanceResult Result(DataMaintenanceRecord record) => new(record.Preview.Plan.Id, record.Preview.Plan.Kind,
        record.Problem is null ? "completed" : record.RolledBack ? "rolledBack" : "failed", record.RootId, record.CurrentBackup.Id, record.RolledBack);
    private static bool Recoverable(Exception error) => error is StorageException or OperationException or IOException or UnauthorizedAccessException or SqliteException;
    private static ApiProblem Problem(Exception error, string id)
    {
        var code = error switch { StorageException e => e.Code, OperationException e => e.Code, _ => "storage.unavailable" };
        if (!ApiErrors.Definitions.Any(x => x.Code == code)) code = "storage.unavailable";
        return ApiErrors.Create(code, "数据维护未完成，已保留恢复证据", id);
    }
    private static string RollbackId(string id) => StorageMaintenancePlanStore.Hash("rollback:" + id)[..32];
}
