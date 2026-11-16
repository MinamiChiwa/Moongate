using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

public sealed partial class SqliteStore
{
    public static IReadOnlyList<DatabaseMigrationStamp> MigrationTargets(IReadOnlyList<DatabaseMigration> moduleMigrations)
    {
        var migrations = new[] { CoreMigrations.Initial, CoreMigrations.Operations, CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode }
            .Concat(moduleMigrations).ToArray();
        _ = MigrationCatalog(migrations);
        return Array.AsReadOnly(migrations.OrderBy(item => item.ModuleId, StringComparer.Ordinal).ThenBy(item => item.Version)
            .Select(item => new DatabaseMigrationStamp(item.ModuleId, item.Version, item.Checksum)).ToArray());
    }

    // 更新器只写平台回执或预检登记；此入口复核现有历史，始终不应用候选业务迁移。
    internal static SqliteStore OpenUpdateControl(DataRootLease lease, IReadOnlyList<DatabaseMigrationStamp> expectedMigrations,
        string expectedMode, TimeProvider? clock = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var ownership = lease.AcquireDatabaseOwnership(); SqliteConnection? connection = null;
        try
        {
            if (lease.DatabaseId is null || expectedMigrations is null || expectedMode is not ("live" or "mock"))
                throw BackupInvalid("更新回执需要已初始化数据库及固定迁移历史");
            var expected = expectedMigrations.OrderBy(item => item.ModuleId, StringComparer.Ordinal).ThenBy(item => item.Version).ToArray();
            var core = expected.Where(item => item.ModuleId == "core").ToArray();
            if (!core.SequenceEqual(MigrationTargets([])))
                throw new StorageException("storage.migrationIncompatible", "当前更新器不支持目标平台数据库协议");
            var path = lease.Paths.Database;
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) LocalDiscoverySecurity.RejectLinks(path + suffix);
            connection = OpenBackupDatabase(path, SqliteOpenMode.ReadWrite);
            ExecuteBackupSql(connection, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
            var actual = InspectBackupDatabase(connection);
            if (actual.Root != lease.Paths.RootId || actual.Database != lease.DatabaseId || actual.Mode != expectedMode || !actual.Migrations.SequenceEqual(expected))
                throw BackupInvalid("更新控制数据库与固定身份或迁移历史不匹配");
            ExecuteBackupSql(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=1000;");
            return new(lease.Paths, connection, new ActivityBarrier(), ownership, VolumeSpaceReservations.Shared, clock ?? TimeProvider.System);
        }
        catch { connection?.Dispose(); ownership.Dispose(); throw; }
    }
}
