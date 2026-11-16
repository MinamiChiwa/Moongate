using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

/// <summary>宿主停机前固定维护意图；离线协调器先处理日志，再允许业务以新代次启动。</summary>
public sealed partial class StorageMaintenanceCoordinator(SqliteStore database, DataRootLease root,
    StorageMaintenancePlanStore plans, OperationStore operations, IReadOnlyList<DatabaseMigration> migrations)
{
    public static StorageOptions StartupOptions(StorageOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return options with { BootstrapDirectory = DataRootLease.ResolveAuthorityBootstrap(options) };
    }

    public static bool NeedsRecovery(string bootstrapDirectory) => NeedsRecovery(new StorageOptions(bootstrapDirectory));
    public static bool NeedsRecovery(StorageOptions options) => DataMaintenanceJournal.HasPending(StartupOptions(options).BootstrapDirectory);

    public Task<StorageMaintenanceTicket> PrepareAsync(StorageMaintenancePlanRecord preview, string operationId,
        string key, string requestHash, ActivityBarrier.MaintenanceLease maintenance, CancellationToken ct = default)
        => PrepareCoreAsync(preview, operationId, key, requestHash, maintenance, ct);

    internal async Task<StorageMaintenanceTicket> PrepareCoreAsync(StorageMaintenancePlanRecord preview, string operationId,
        string key, string requestHash, ActivityBarrier.MaintenanceLease maintenance, CancellationToken ct,
        Func<string, Task>? persisted = null)
    {
        FileTransferStore.Key(key);
        var bootstrap = root.Paths.BootstrapDirectory;
        var guard = DataMaintenanceJournal.Lock(bootstrap);
        DataSnapshotDescriptor? current = null;
        var journaled = false;
        try
        {
            if (DataMaintenanceJournal.HasPending(bootstrap)) throw new OperationException("resource.busy", "已有尚未完成的数据维护事务");
            await plans.VerifyCurrentAsync(preview, ct);
            DataSnapshotDescriptor? restore = preview.Plan.Kind == "restore" ? plans.BackedUpSnapshot(preview) : null;
            if (restore is not null) await database.PreflightDatabaseMigrationsAsync(restore.Database, migrations, maintenance, ct);
            current = await database.CreateRecoveryDataSnapshotAsync(maintenance, ct);
            if (persisted is not null) await persisted("currentBackedUp");
            await plans.VerifyCurrentAsync(preview, ct);
            ct.ThrowIfCancellationRequested();
            var operation = await operations.BeginStorageAsync(operationId, "applying", maintenance)
                ?? throw new OperationCanceledException("数据维护在固定意图前已取消", ct);
            var record = new DataMaintenanceRecord(1, Guid.NewGuid().ToString("N"), root.Paths.DataRoot, root.Paths.RootId,
                root.DatabaseId ?? throw new StorageException("storage.databaseInvalid", "数据维护缺少数据库身份"), preview, operation, key, requestHash,
                new(current.SnapshotId, StorageMaintenancePlanStore.Hash(current)),
                restore is null ? null : new(restore.SnapshotId, StorageMaintenancePlanStore.Hash(restore)));
            DataMaintenanceJournal.Save(bootstrap, record);
            journaled = true;
            return new(bootstrap, record, guard);
        }
        catch
        {
            try
            {
                if (!journaled && current is not null)
                    await new DataBackupStore(database, root.Paths).DiscardUnclaimedRecoveryAsync(current, maintenance);
            }
            finally { guard.Dispose(); }
            throw;
        }
    }
}

public sealed class StorageMaintenanceTicket : IDisposable
{
    private readonly string bootstrap;
    private readonly FileStream guard;
    private readonly object gate = new();
    private DataMaintenanceRecord record;
    private bool disposed;
    internal StorageMaintenanceTicket(string bootstrap, DataMaintenanceRecord record, FileStream guard)
    { this.bootstrap = bootstrap; this.record = record; this.guard = guard; }
    public void UpdateOperation(OperationSnapshot operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (operation.Id != record.Operation.Id || operation.Kind != record.Operation.Kind || operation.Owner != record.Operation.Owner)
                throw new ArgumentException("维护回执须属于已固定的同一操作");
            if (operation.Revision.Value < record.Operation.Revision.Value) return;
            record = record with { Operation = operation }; DataMaintenanceJournal.Save(bootstrap, record);
        }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; guard.Dispose(); } }
}
