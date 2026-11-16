using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class DatabaseHealthCheckTests
{
    private static readonly DatabaseMigration Initial = new("health-test", 1,
        "CREATE TABLE health_parent(id INTEGER PRIMARY KEY); CREATE TABLE health_child(parent INTEGER REFERENCES health_parent(id));");
    private static DatabaseMigrationStamp[] Target => new[]
    {
        CoreMigrations.Initial, CoreMigrations.Operations, CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode, Initial
    }.Select(migration => new DatabaseMigrationStamp(migration.ModuleId, migration.Version, migration.Checksum)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChecksCommittedWalIdentityAndExactMigrationsWithoutChangingData(bool mock)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial], mock: mock);
        Execute(root.Paths.Database, "PRAGMA foreign_keys=ON; INSERT INTO health_parent VALUES(7); INSERT INTO health_child VALUES(7);");
        Assert.True(new FileInfo(root.Paths.Database + "-wal").Length > 0);
        using var maintenance = database.Activities.TryEnterMaintenance("health")!;
        var before = ReadSharedBytes(root.Paths.Database);
        var wal = ReadSharedBytes(root.Paths.Database + "-wal");
        var result = await database.VerifyDatabaseHealthAsync(maintenance, root.DatabaseId!, mock ? "mock" : "live", Target);
        Assert.Equal(root.Paths.RootId, result.RootId);
        Assert.Equal(root.DatabaseId, result.DatabaseId);
        Assert.Equal(mock ? "mock" : "live", result.BackendMode);
        Assert.Equal(Target.OrderBy(stamp => stamp.ModuleId, StringComparer.Ordinal).ThenBy(stamp => stamp.Version), result.Migrations);
        Assert.Equal(7L, database.ReadScalar("SELECT parent FROM health_child"));
        Assert.Equal(before, ReadSharedBytes(root.Paths.Database));
        Assert.Equal(wal, ReadSharedBytes(root.Paths.Database + "-wal"));
        Assert.Empty(database.Activities.Snapshot().Activities);
        Assert.Equal("health", database.Activities.Snapshot().MaintenanceOwner);
        Assert.Equal("update.maintenanceActive", Assert.Throws<ActivityBarrierException>(() => database.Activities.EnterActivity("business", "write")).Code);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("database")]
    [InlineData("mode")]
    [InlineData("checksum")]
    [InlineData("missing-migration")]
    [InlineData("extra-migration")]
    [InlineData("missing-table")]
    [InlineData("foreign-key")]
    public async Task ChangedActiveDatabaseFailsHealthWithoutRepairingEvidence(string change)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        var sql = change switch
        {
            "root" => "UPDATE core_database_identity SET root_id='" + Guid.NewGuid().ToString("N") + "'",
            "database" => "UPDATE core_database_identity SET database_id='" + Guid.NewGuid().ToString("N") + "'",
            "mode" => "UPDATE core_backend_mode SET mode='mock'",
            "checksum" => "UPDATE core_migrations SET checksum='" + new string('a', 64) + "' WHERE module_id='health-test'",
            "missing-migration" => "DELETE FROM core_migrations WHERE module_id='health-test'",
            "extra-migration" => "INSERT INTO core_migrations VALUES('extra',1,'" + new string('a', 64) + "')",
            "missing-table" => "DROP TABLE core_database_identity",
            "foreign-key" => "PRAGMA foreign_keys=OFF; INSERT INTO health_child VALUES(99)",
            _ => throw new ArgumentException(change)
        };
        Execute(root.Paths.Database, sql);
        using var maintenance = database.Activities.TryEnterMaintenance("health")!;
        var before = ReadSharedBytes(root.Paths.Database);
        var wal = ReadSharedBytes(root.Paths.Database + "-wal");
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() =>
            database.VerifyDatabaseHealthAsync(maintenance, root.DatabaseId!, "live", Target))).Code);
        Assert.Equal(before, ReadSharedBytes(root.Paths.Database));
        Assert.Equal(wal, ReadSharedBytes(root.Paths.Database + "-wal"));
        Assert.Empty(database.Activities.Snapshot().Activities);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("mode")]
    [InlineData("checksum")]
    [InlineData("missing-module")]
    [InlineData("future-migration")]
    [InlineData("duplicate")]
    [InlineData("null-stamp")]
    public async Task WrongOrMalformedTargetCannotProduceHealthResult(string change)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using var maintenance = database.Activities.TryEnterMaintenance("health")!;
        var target = Target;
        if (change == "checksum") target[^1] = target[^1] with { Checksum = new string('a', 64) };
        if (change == "missing-module") target = target[..^1];
        if (change == "future-migration") target = [.. target, target[^1] with { Version = 2 }];
        if (change == "duplicate") target = [.. target, target[^1]];
        if (change == "null-stamp") target[^1] = null!;
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => database.VerifyDatabaseHealthAsync(
            maintenance, change == "database" ? Guid.NewGuid().ToString("N") : root.DatabaseId!, change == "mode" ? "mock" : "live", target))).Code);
        Assert.Equal(5L, database.ReadScalar("SELECT count(*) FROM core_migrations"));
        Assert.Empty(database.Activities.Snapshot().Activities);
    }

    [Fact]
    public async Task ForeignOrReleasedMaintenanceDoesNotAuthorizeHealthCheck()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using var foreign = new ActivityBarrier().TryEnterMaintenance("other")!;
        Assert.Equal("update.maintenanceLeaseInvalid", (await Assert.ThrowsAsync<ActivityBarrierException>(() =>
            database.VerifyDatabaseHealthAsync(foreign, root.DatabaseId!, "live", Target))).Code);
        var released = database.Activities.TryEnterMaintenance("health")!;
        released.Dispose();
        Assert.Equal("update.maintenanceLeaseInvalid", (await Assert.ThrowsAsync<ActivityBarrierException>(() =>
            database.VerifyDatabaseHealthAsync(released, root.DatabaseId!, "live", Target))).Code);
        Assert.Empty(database.Activities.Snapshot().Activities);
    }

    [Fact]
    public async Task CancelledHealthCheckRetainsMaintenanceAndDatabaseOwnership()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using var maintenance = database.Activities.TryEnterMaintenance("health")!;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            database.VerifyDatabaseHealthAsync(maintenance, root.DatabaseId!, "live", Target, cancel.Token));
        Assert.Equal("resource.busy", Assert.Throws<StorageException>(() => root.AcquireDatabaseOwnership()).Code);
        Assert.Equal("health", database.Activities.Snapshot().MaintenanceOwner);
        Assert.Empty(database.Activities.Snapshot().Activities);
        await database.VerifyDatabaseHealthAsync(maintenance, root.DatabaseId!, "live", Target);
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string directory;
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            directory = Path.Combine(repository!.FullName, "artifacts", "tmp", "database-health-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }
        public DataRootLease Open() => DataRootLease.Open(new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
        public void Dispose() => Directory.Delete(directory, true);
    }
}
