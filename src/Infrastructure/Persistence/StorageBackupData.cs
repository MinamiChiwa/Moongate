using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

public sealed partial class SqliteStore
{
    internal static Task CheckpointStorageRootAsync(DataRootLease root, DataRootLease.DatabaseOwnership ownership)
    {
        using var use = ownership.Use(root);
        using (var connection = OpenBackupDatabase(root.Paths.Database, SqliteOpenMode.ReadWrite))
        {
            var state = InspectBackupDatabase(connection);
            if (state.Root != root.Paths.RootId || state.Database != root.DatabaseId) throw BackupInvalid("迁移数据库身份不匹配");
            using var checkpoint = connection.CreateCommand(); checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using (var rows = checkpoint.ExecuteReader()) if (!rows.Read() || rows.GetInt32(0) != 0) throw BackupInvalid("迁移数据库不能完成 checkpoint");
            checkpoint.CommandText = "PRAGMA journal_mode=DELETE";
            if (!string.Equals(checkpoint.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase)) throw BackupInvalid("迁移数据库不能退出 WAL");
        }
        RequireNoDatabaseSidecars(root.Paths.Database);
        return Task.CompletedTask;
    }
    internal async Task VerifyStorageContentsAsync(CancellationToken ct)
    {
        foreach (var asset in ReadSnapshotAssets(paths.Database, ct))
            await VerifySnapshotFileAsync(Updates.UpdatePaths.Local(paths.DataRoot, asset.RelativePath), asset, ct);
        _ = await ReadSettingsSnapshotAsync(paths, ct);
    }
    internal async Task<T> ReadBackupDataAsync<T>(DatabaseBackupDescriptor descriptor, Func<SqliteConnection, T> read, CancellationToken ct)
    {
        using var verified = await OpenVerifiedBackupAsync(paths, descriptor.BackupId, descriptor, ct);
        using var connection = OpenBackupDatabase(verified.Database.Name, SqliteOpenMode.ReadOnly);
        return read(connection);
    }
}
