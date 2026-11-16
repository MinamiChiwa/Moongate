using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class DatabaseBackupTests
{
    private static readonly DatabaseMigration Initial = new("backup-test", 1,
        "CREATE TABLE backup_values (id INTEGER PRIMARY KEY, value TEXT NOT NULL) STRICT;");
    private static readonly DatabaseMigration Next = new("backup-test", 2,
        "ALTER TABLE backup_values ADD COLUMN migrated INTEGER NOT NULL DEFAULT 1;");

    [Fact]
    public async Task OwnedRestoreRejectsForeignOrReleasedOwnership()
    {
        using var workspace = new Workspace();
        using var other = new Workspace();
        using var root = workspace.Open();
        using var foreign = other.Open();
        var backup = await PrepareRestoreAsync(root);
        using (var ownership = foreign.AcquireDatabaseOwnership())
            await Assert.ThrowsAsync<ArgumentException>(() => SqliteStore.RestoreDatabaseBackupOwnedAsync(root, ownership,
                Guid.NewGuid().ToString("N"), backup));
        var released = root.AcquireDatabaseOwnership();
        released.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => SqliteStore.RestoreDatabaseBackupOwnedAsync(root, released,
            Guid.NewGuid().ToString("N"), backup));
        Assert.Equal("new-data", Read(root.Paths.Database, "SELECT value FROM backup_values"));
    }

    [Fact]
    public async Task DisposingOwnershipDuringOwnedRestoreDefersLockReleaseUntilStepEnds()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        using var ownership = root.AcquireDatabaseOwnership();
        await SqliteStore.RestoreDatabaseBackupOwnedAsync(root, ownership, Guid.NewGuid().ToString("N"), backup, persisted: point =>
        {
            if (point != DatabaseRestorePersistencePoint.CandidateVerified) return Task.CompletedTask;
            ownership.Dispose();
            Assert.Equal("resource.busy", Assert.Throws<StorageException>(() => root.AcquireDatabaseOwnership()).Code);
            Assert.Throws<ObjectDisposedException>(() => ownership.Use(root));
            return Task.CompletedTask;
        });
        await using var reopened = SqliteStore.Open(root, [Initial]);
        Assert.Equal("old-data", reopened.ReadScalar("SELECT value FROM backup_values"));
    }

    [Fact]
    public async Task SameOwnershipRejectsConcurrentRecoverySteps()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        using var ownership = root.AcquireDatabaseOwnership();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = SqliteStore.RestoreDatabaseBackupOwnedAsync(root, ownership, Guid.NewGuid().ToString("N"), backup, persisted: async point =>
        {
            if (point != DatabaseRestorePersistencePoint.CandidateVerified) return;
            reached.SetResult();
            await resume.Task;
        });
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("resource.busy", (await Assert.ThrowsAsync<StorageException>(() =>
                SqliteStore.RestoreDatabaseBackupOwnedAsync(root, ownership, Guid.NewGuid().ToString("N"), backup))).Code);
        }
        finally { resume.TrySetResult(); await first; }
        Assert.Equal("old-data", Read(root.Paths.Database, "SELECT value FROM backup_values"));
    }

    [Fact]
    public async Task OfflineRestoreReplacesMigratedDatabaseAndRetainsIndependentPreviousDatabase()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        var id = Guid.NewGuid().ToString("N");
        var result = await SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup);
        var retained = root.Paths.Resolve("recovery", "database-" + id + ".sqlite");
        Assert.Equal("new-data", Read(retained, "SELECT value FROM backup_values WHERE id=1"));
        Assert.Equal(2L, Read(retained, "SELECT max(version) FROM core_migrations WHERE module_id='backup-test'"));
        Assert.Equal("delete", Read(retained, "PRAGMA journal_mode"));
        Assert.Equal(Hash(retained), result.RetainedDatabaseSha256);
        Assert.Equal(backup.Sha256, Hash(root.Paths.Database));
        Assert.Equal(result, await SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup));
        await using var restored = SqliteStore.Open(root, [Initial]);
        Assert.Equal("old-data", restored.ReadScalar("SELECT value FROM backup_values WHERE id=1"));
        Assert.Equal(1L, restored.ReadScalar("SELECT max(version) FROM core_migrations WHERE module_id='backup-test'"));
        Assert.Equal("ok", restored.ReadScalar("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task OfflineRestoreCannotEnterWhileHostOwnsDatabase()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        await using var database = SqliteStore.Open(root, [Initial, Next]);
        Assert.Equal("resource.busy", (await Assert.ThrowsAsync<StorageException>(() =>
            SqliteStore.RestoreDatabaseBackupOfflineAsync(root, Guid.NewGuid().ToString("N"), backup))).Code);
        Assert.Equal("new-data", database.ReadScalar("SELECT value FROM backup_values WHERE id=1"));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Recovery));
    }

    [Fact]
    public async Task RepeatedRestoreRejectsDataWrittenAfterSuccessfulRestore()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        var id = Guid.NewGuid().ToString("N");
        await SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup);
        await using (var database = SqliteStore.Open(root, [Initial]))
            await database.WriteAsync(tx => tx.Execute("UPDATE backup_values SET value='after-restore'"));
        var before = File.ReadAllBytes(root.Paths.Database);
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() =>
            SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup))).Code);
        Assert.Equal(before, File.ReadAllBytes(root.Paths.Database));
        Assert.Equal("after-restore", Read(root.Paths.Database, "SELECT value FROM backup_values WHERE id=1"));
        Assert.Equal("new-data", Read(root.Paths.Resolve("recovery", "database-" + id + ".sqlite"), "SELECT value FROM backup_values WHERE id=1"));
    }

    [Theory]
    [InlineData("CandidateVerified")]
    [InlineData("SourceCheckpointed")]
    [InlineData("Replaced")]
    public async Task OfflineRestoreCancellationPreservesLogicalDataOrReturnsCompletedReplacement(string cancelAt)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        var id = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        var restoring = SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup, cancellation.Token, point =>
        {
            if (point.ToString() == cancelAt) cancellation.Cancel();
            return Task.CompletedTask;
        });
        if (cancelAt == "Replaced")
        {
            Assert.Equal(id, (await restoring).TransactionId);
            Assert.Equal(backup.Sha256, Hash(root.Paths.Database));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restoring);
            Assert.Equal("new-data", Read(root.Paths.Database, "SELECT value FROM backup_values WHERE id=1"));
            Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Recovery));
        }
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(root.Paths.Database)!, "restore-*"));
        await SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup);
        Assert.Equal(backup.Sha256, Hash(root.Paths.Database));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("database-id")]
    [InlineData("root-id")]
    [InlineData("mode")]
    [InlineData("transaction-id")]
    public async Task OfflineRestoreRejectsChangedFixedIdentityBeforeReplacement(string scenario)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        var before = File.ReadAllBytes(root.Paths.Database);
        var expected = scenario switch
        {
            "hash" => backup with { Sha256 = new string('0', 64) },
            "database-id" => backup with { DatabaseId = Guid.NewGuid().ToString("N") },
            "root-id" => backup with { RootId = Guid.NewGuid().ToString("N") },
            "mode" => backup with { BackendMode = "mock" },
            _ => backup
        };
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() =>
            SqliteStore.RestoreDatabaseBackupOfflineAsync(root, scenario == "transaction-id" ? "../escape" : Guid.NewGuid().ToString("N"), expected))).Code);
        Assert.Equal(before, File.ReadAllBytes(root.Paths.Database));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Recovery));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(root.Paths.Database)!, "restore-*"));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Theory]
    [InlineData("root")]
    [InlineData("database")]
    [InlineData("mode")]
    public async Task OfflineRestoreRejectsForeignCurrentDatabase(string changed)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        Execute(root.Paths.Database, changed switch
        {
            "root" => "UPDATE core_database_identity SET root_id='" + Guid.NewGuid().ToString("N") + "'",
            "database" => "UPDATE core_database_identity SET database_id='" + Guid.NewGuid().ToString("N") + "'",
            _ => "UPDATE core_backend_mode SET mode='mock'"
        });
        var before = File.ReadAllBytes(root.Paths.Database);
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() =>
            SqliteStore.RestoreDatabaseBackupOfflineAsync(root, Guid.NewGuid().ToString("N"), backup))).Code);
        Assert.Equal(before, File.ReadAllBytes(root.Paths.Database));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Recovery));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(root.Paths.Database)!, "restore-*"));
    }

    [Fact]
    public async Task OfflineRestorePreservesOccupiedMainFileAndCanRetryAfterRelease()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        var id = Guid.NewGuid().ToString("N");
        using (File.Open(root.Paths.Database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await Assert.ThrowsAnyAsync<IOException>(() => SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup));
        Assert.Equal("new-data", Read(root.Paths.Database, "SELECT value FROM backup_values WHERE id=1"));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Recovery));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(root.Paths.Database)!, "restore-*"));
        await SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup);
        Assert.Equal(backup.Sha256, Hash(root.Paths.Database));
    }

    [Fact]
    public async Task RepeatedRestoreRejectsEmptyRetainedEvidence()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var backup = await PrepareRestoreAsync(root);
        var id = Guid.NewGuid().ToString("N");
        await SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup);
        File.WriteAllBytes(root.Paths.Resolve("recovery", "database-" + id + ".sqlite"), []);
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() =>
            SqliteStore.RestoreDatabaseBackupOfflineAsync(root, id, backup))).Code);
        Assert.Equal(backup.Sha256, Hash(root.Paths.Database));
    }

    [Theory]
    [InlineData("CandidateVerified")]
    [InlineData("SourceCheckpointed")]
    [InlineData("Replaced")]
    [InlineData("SourceWal")]
    public async Task KilledOfflineRestorePreservesOriginalOrVerifiedReplacementAndCanResume(string stopAt)
    {
        using var workspace = new Workspace();
        DatabaseBackupDescriptor backup;
        string rootPath, bootstrap;
        using (var root = workspace.Open())
        {
            backup = await PrepareRestoreAsync(root);
            rootPath = root.Paths.DataRoot;
            bootstrap = root.Paths.BootstrapDirectory;
            File.WriteAllText(root.Paths.Resolve("recovery", "restore-probe.json"),
                JsonSerializer.Serialize(backup, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        var probe = Path.Combine(repository!.FullName, "artifacts", "build", "bin", "MabiTool.PersistenceProbe",
            new DirectoryInfo(AppContext.BaseDirectory).Name, "MabiTool.PersistenceProbe.dll");
        var marker = Path.Combine(rootPath, "recovery", "restore.marker");
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { probe, "restore-database-" + stopAt, bootstrap, rootPath, marker })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(marker))
            {
                if (child.HasExited) throw new InvalidOperationException(await errors);
                await Task.Delay(50, deadline.Token);
            }
            Assert.Equal("storage.writerBusy", Assert.Throws<StorageException>(() => workspace.Open()).Code);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await Task.WhenAll(output, errors);
        }
        using var recovered = workspace.Open();
        if (stopAt == "SourceWal") Assert.True(new FileInfo(recovered.Paths.Database + "-wal").Length > 0);
        else Assert.Equal(stopAt == "Replaced" ? "old-data" : "new-data",
            Read(recovered.Paths.Database, "SELECT value FROM backup_values WHERE id=1"));
        var retained = recovered.Paths.Resolve("recovery", "database-" + new string('1', 32) + ".sqlite");
        Assert.Equal(stopAt == "Replaced", File.Exists(retained));
        await SqliteStore.RestoreDatabaseBackupOfflineAsync(recovered, new string('1', 32), backup);
        Assert.Equal(backup.Sha256, Hash(recovered.Paths.Database));
        Assert.Equal(stopAt == "SourceWal" ? "new-in-wal" : "new-data", Read(retained, "SELECT value FROM backup_values WHERE id=1"));
        Assert.Equal(backup.Sha256, Hash(workspace.BackupFile(recovered, backup)));
    }

    private static async Task<DatabaseBackupDescriptor> PrepareRestoreAsync(DataRootLease root)
    {
        DatabaseBackupDescriptor backup;
        await using (var database = SqliteStore.Open(root, [Initial]))
        {
            await database.WriteAsync(tx => tx.Execute("INSERT INTO backup_values VALUES(1,'old-data')"));
            using var maintenance = database.Activities.TryEnterMaintenance("restore-fixture")!;
            backup = await database.CreateDatabaseBackupAsync(maintenance);
        }
        await using (var database = SqliteStore.Open(root, [Initial, Next]))
            await database.WriteAsync(tx => tx.Execute("UPDATE backup_values SET value='new-data'"));
        return backup;
    }

    [Fact]
    public async Task BackupIncludesCommittedWalAndPublishesStandaloneVerifiedDatabase()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        await database.WriteAsync(tx => tx.Execute("INSERT INTO backup_values VALUES(1,'committed-in-wal')"));
        Assert.True(new FileInfo(root.Paths.Database + "-wal").Length > 0);
        using var maintenance = database.Activities.TryEnterMaintenance("backup");
        Assert.NotNull(maintenance);
        var backup = await database.CreateDatabaseBackupAsync(maintenance);
        Assert.Equal(root.Paths.RootId, backup.RootId);
        Assert.Equal(root.DatabaseId, backup.DatabaseId);
        Assert.Equal("live", backup.BackendMode);
        Assert.Contains(backup.Migrations, m => m.ModuleId == Initial.ModuleId && m.Version == 1 && m.Checksum == Initial.Checksum);
        Assert.Equal(backup, await database.VerifyDatabaseBackupAsync(backup.BackupId, backup) with { Migrations = backup.Migrations });
        var file = workspace.BackupFile(root, backup);
        Assert.Equal("committed-in-wal", Read(file, "SELECT value FROM backup_values WHERE id=1"));
        Assert.Equal("delete", Read(file, "PRAGMA journal_mode"));
        Assert.Equal(new[] { "main.sqlite", "manifest.json" }, Directory.GetFiles(Path.GetDirectoryName(file)!).Select(Path.GetFileName).Order());
        await Assert.ThrowsAsync<ActivityBarrierException>(() => database.WriteAsync(tx => tx.Execute("DELETE FROM backup_values")));
        Assert.Equal("committed-in-wal", database.ReadScalar("SELECT value FROM backup_values WHERE id=1"));
    }

    [Fact]
    public async Task BackupRequiresCurrentMaintenanceLeaseFromSameBarrier()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using (database.Activities.EnterActivity("test", "operation"))
            Assert.Null(database.Activities.TryEnterMaintenance("backup"));
        using var other = new ActivityBarrier().TryEnterMaintenance("backup");
        await Assert.ThrowsAsync<ActivityBarrierException>(() => database.CreateDatabaseBackupAsync(other!));
        var expired = database.Activities.TryEnterMaintenance("backup")!;
        expired.Dispose();
        await Assert.ThrowsAsync<ActivityBarrierException>(() => database.CreateDatabaseBackupAsync(expired));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Backups));
    }

    [Fact]
    public async Task MigrationPreflightUsesIsolatedCopyAndPreservesSourceAndBackup()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using var maintenance = database.Activities.TryEnterMaintenance("backup")!;
        var backup = await database.CreateDatabaseBackupAsync(maintenance);
        var before = File.ReadAllBytes(workspace.BackupFile(root, backup));
        var result = await database.PreflightDatabaseMigrationsAsync(backup, [Initial, Next], maintenance);
        Assert.Equal(backup.BackupId, result.BackupId);
        Assert.True(result.SizeBytes > 0);
        Assert.Contains(result.Migrations, m => m.ModuleId == Next.ModuleId && m.Version == 2 && m.Checksum == Next.Checksum);
        Assert.Equal(1L, database.ReadScalar("SELECT max(version) FROM core_migrations WHERE module_id='backup-test'"));
        Assert.Equal(before, File.ReadAllBytes(workspace.BackupFile(root, backup)));
        Assert.Equal(1L, Read(workspace.BackupFile(root, backup), "SELECT max(version) FROM core_migrations WHERE module_id='backup-test'"));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Temporary));
    }

    [Theory]
    [InlineData("lf")]
    [InlineData("crlf")]
    public async Task LegacyLineEndingBackupStampsVerifyPreflightAndRestore(string storedForm)
    {
        const string sql = "CREATE TABLE eol_backup (\n    id INTEGER PRIMARY KEY\n) STRICT;";
        var lf = new DatabaseMigration("eol-backup", 1, sql);
        var crlf = new DatabaseMigration("eol-backup", 1, sql.Replace("\n", "\r\n"));
        var legacy = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(storedForm == "lf" ? sql : sql.Replace("\n", "\r\n"))));
        using var workspace = new Workspace();
        using var root = workspace.Open();
        DatabaseBackupDescriptor backup;
        await using (var database = SqliteStore.Open(root, [lf]))
        {
            // 模拟旧版按源文件换行写入的记录及其备份印记。
            await database.WriteAsync(tx => tx.Execute("UPDATE core_migrations SET checksum=$checksum WHERE module_id='eol-backup'", ("$checksum", legacy)));
            using var maintenance = database.Activities.TryEnterMaintenance("backup")!;
            backup = await database.CreateDatabaseBackupAsync(maintenance);
            Assert.Contains(backup.Migrations, m => m.ModuleId == "eol-backup" && m.Checksum == legacy);
            Assert.Equal(backup.Migrations, (await database.VerifyDatabaseBackupAsync(backup.BackupId, backup)).Migrations);
            var expected = SqliteStore.MigrationTargets([crlf, Initial]);
            var preflight = await database.PreflightDatabaseMigrationsAsync(backup, [crlf, Initial], maintenance);
            Assert.Equal(expected, preflight.Migrations);
        }
        await using (var current = SqliteStore.Open(root, [crlf]))
            Assert.Equal(lf.Checksum, current.ReadScalar("SELECT checksum FROM core_migrations WHERE module_id='eol-backup'"));
        await SqliteStore.RestoreDatabaseBackupOfflineAsync(root, Guid.NewGuid().ToString("N"), backup);
        await using var restored = SqliteStore.Open(root, [crlf]);
        Assert.Equal(lf.Checksum, restored.ReadScalar("SELECT checksum FROM core_migrations WHERE module_id='eol-backup'"));
        using var health = restored.Activities.TryEnterMaintenance("health")!;
        await restored.VerifyDatabaseHealthAsync(health, root.DatabaseId!, "live", SqliteStore.MigrationTargets([lf]));
    }

    [Theory]
    [InlineData("invalid-sql", "storage.migrationInvalid")]
    [InlineData("missing-module", "storage.migrationIncompatible")]
    [InlineData("changed-history", "storage.migrationIncompatible")]
    [InlineData("version-gap", "storage.migrationInvalid")]
    [InlineData("identity-change", "storage.backupInvalid")]
    public async Task InvalidMigrationPreflightLeavesOriginalAndPublishedBackupIntact(string scenario, string code)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using var maintenance = database.Activities.TryEnterMaintenance("backup")!;
        var backup = await database.CreateDatabaseBackupAsync(maintenance);
        DatabaseMigration[] migrations = scenario switch
        {
            "invalid-sql" => [Initial, Next with { Sql = "THIS IS INVALID SQL" }],
            "missing-module" => [],
            "changed-history" => [Initial with { Sql = Initial.Sql + "\n-- changed" }],
            "version-gap" => [Initial, Next with { Version = 3 }],
            _ => [Initial, Next with { Sql = "UPDATE core_database_identity SET database_id='00000000000000000000000000000000'" }]
        };
        var error = await Assert.ThrowsAsync<StorageException>(() => database.PreflightDatabaseMigrationsAsync(backup, migrations, maintenance));
        Assert.Equal(code, error.Code);
        await database.VerifyDatabaseBackupAsync(backup.BackupId, backup);
        Assert.Equal(root.DatabaseId, database.ReadScalar("SELECT database_id FROM core_database_identity"));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Temporary));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    [InlineData("unknown-field")]
    [InlineData("missing-file")]
    [InlineData("root-identity")]
    [InlineData("migration-history")]
    [InlineData("database-identity")]
    public async Task TamperedBackupIsRejected(string scenario)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using var maintenance = database.Activities.TryEnterMaintenance("backup")!;
        var backup = await database.CreateDatabaseBackupAsync(maintenance);
        var file = workspace.BackupFile(root, backup);
        var manifestPath = Path.Combine(Path.GetDirectoryName(file)!, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        switch (scenario)
        {
            case "database": File.WriteAllBytes(file, [1, 2, 3]); break;
            case "extra-file": File.WriteAllText(file + "-wal", "untrusted"); break;
            case "extra-directory": Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(file)!, "extra")); break;
            case "missing-file": File.Delete(file); break;
            case "unknown-field": manifest["other"] = true; break;
            case "root-identity": manifest["rootId"] = Guid.NewGuid().ToString("N"); break;
            case "migration-history": manifest["migrations"]![0]!["checksum"] = new string('a', 64); break;
            case "database-identity": manifest["databaseId"] = Guid.NewGuid().ToString("N"); break;
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => database.VerifyDatabaseBackupAsync(backup.BackupId))).Code);
    }

    [Fact]
    public async Task FrozenDescriptorRejectsCoordinatedManifestAndDatabaseReplacement()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using var maintenance = database.Activities.TryEnterMaintenance("backup")!;
        var backup = await database.CreateDatabaseBackupAsync(maintenance);
        var file = workspace.BackupFile(root, backup);
        Execute(file, "INSERT INTO backup_values VALUES(2,'replaced')");
        var altered = backup with { SizeBytes = new FileInfo(file).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, "manifest.json"),
            JsonSerializer.Serialize(altered, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(altered.Sha256, (await database.VerifyDatabaseBackupAsync(backup.BackupId)).Sha256);
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => database.VerifyDatabaseBackupAsync(backup.BackupId, backup))).Code);
        await Assert.ThrowsAsync<StorageException>(() => database.PreflightDatabaseMigrationsAsync(backup, [Initial, Next], maintenance));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Temporary));
    }

    [Fact]
    public async Task ForeignKeysAreCheckedBeforeBackupIsPublished()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        await database.WriteAsync(tx => tx.Execute("CREATE TABLE backup_parent(id INTEGER PRIMARY KEY); CREATE TABLE backup_child(parent INTEGER REFERENCES backup_parent(id));"));
        Execute(root.Paths.Database, "PRAGMA foreign_keys=OFF; INSERT INTO backup_child VALUES(100)");
        using var maintenance = database.Activities.TryEnterMaintenance("backup")!;
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => database.CreateDatabaseBackupAsync(maintenance))).Code);
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Resolve("backups", "database")));
    }

    [Theory]
    [InlineData("../main")]
    [InlineData("0000000000000000000000000000000G")]
    [InlineData("C:\\outside")]
    public async Task BackupLookupRejectsNonGeneratedIds(string id)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => database.VerifyDatabaseBackupAsync(id))).Code);
    }

    [Fact]
    public async Task CancelledRequestsPublishNothingAndDoNotChangeBackup()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, [Initial]);
        using var maintenance = database.Activities.TryEnterMaintenance("backup")!;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.CreateDatabaseBackupAsync(maintenance, cancelled.Token));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Backups));
        var backup = await database.CreateDatabaseBackupAsync(maintenance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.PreflightDatabaseMigrationsAsync(backup, [Initial, Next], maintenance, cancelled.Token));
        await database.VerifyDatabaseBackupAsync(backup.BackupId, backup);
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Temporary));
        Assert.Empty(database.Activities.Snapshot().Activities);
    }

    private static object? Read(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string directory;
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            directory = Path.Combine(repository!.FullName, "artifacts", "tmp", "database-backup-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }
        public DataRootLease Open() => DataRootLease.Open(new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
        public string BackupFile(DataRootLease root, DatabaseBackupDescriptor backup) => Path.Combine(root.Paths.Backups, "database", backup.BackupId, "main.sqlite");
        public void Dispose() => Directory.Delete(directory, true);
    }
}
