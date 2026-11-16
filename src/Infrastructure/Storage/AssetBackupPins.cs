using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed partial class AssetStore
{
    internal bool BelongsTo(SqliteStore store) => ReferenceEquals(database, store);

    internal async Task<AssetBackupPin> FreezeBackupAsync(
        Func<CancellationToken, Task<(DatabaseBackupDescriptor Backup, IReadOnlyList<DataSnapshotFile> Files)>> capture,
        CancellationToken ct)
    {
        await mutationGate.WaitAsync(ct).ConfigureAwait(false);
        string? backupId = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var snapshot = await capture(ct).ConfigureAwait(false);
            backupId = snapshot.Backup.BackupId;
            if (snapshot.Files.Count > SqliteStore.MaximumSnapshotEntries)
                throw new StorageException("resource.capacityExceeded", "备份内容保护超过条目预算");
            var created = time.GetUtcNow().ToString("O");
            foreach (var batch in snapshot.Files.Chunk(100))
                await database.WriteAsync(tx =>
                {
                    foreach (var file in batch)
                    {
                        var count = tx.Execute("""
                            INSERT INTO platform_asset_backup_pins
                            SELECT $backup,sha256,$runtime,$at FROM platform_asset_blobs
                            WHERE sha256=$hash AND size_bytes=$size AND state='ready'
                            """, ("$backup", backupId), ("$runtime", database.RuntimeId), ("$at", created),
                            ("$hash", file.Sha256), ("$size", file.SizeBytes));
                        if (count != 1) throw new StorageException("storage.backupInvalid", "固定备份的资产内容已不可用");
                    }
                    return 0;
                }, ct).ConfigureAwait(false);
            return new(snapshot.Backup, snapshot.Files, () => ReleaseBackupAsync(snapshot.Backup.BackupId));
        }
        catch
        {
            if (backupId is not null) await ReleaseBackupAsync(backupId).ConfigureAwait(false);
            throw;
        }
        finally { mutationGate.Release(); }
    }

    private async ValueTask ReleaseBackupAsync(string backupId)
    {
        if (database.IsStopping) return;
        try
        {
            await database.WriteAsync(tx => tx.Execute(
                "DELETE FROM platform_asset_backup_pins WHERE backup_id=$id AND runtime_id=$runtime",
                ("$id", backupId), ("$runtime", database.RuntimeId))).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (database.IsStopping) { }
        catch (OperationCanceledException) when (database.IsStopping) { }
    }

    internal sealed class AssetBackupPin(DatabaseBackupDescriptor backup, IReadOnlyList<DataSnapshotFile> files,
        Func<ValueTask> release) : IAsyncDisposable
    {
        private Func<ValueTask>? cleanup = release;
        public DatabaseBackupDescriptor Backup { get; } = backup;
        public IReadOnlyList<DataSnapshotFile> Files { get; } = files;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref cleanup, null)?.Invoke() ?? ValueTask.CompletedTask;
    }
}
