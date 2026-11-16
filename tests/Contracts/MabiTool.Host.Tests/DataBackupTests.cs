using System.Text;
using MabiTool.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class DataBackupTests
{
    [Fact]
    public async Task OrdinaryBackupAllowsWritesAndProtectsRemovedContentUntilCopyCompletes()
    {
        await using var work = new StorageTestWorkspace();
        await work.Database.WriteAsync(tx => tx.Execute("CREATE TABLE backup_probe(value TEXT NOT NULL); INSERT INTO backup_probe VALUES('before')"));
        var original = await Put(work.Assets, "original");
        var blob = work.Paths.Resolve("assets", "sha256", original.Sha256[..2], original.Sha256);
        var snapshot = await work.Database.CreateOrdinaryDataSnapshotAsync(work.Assets, default, async point =>
        {
            if (point != DataSnapshotPersistencePoint.DatabaseBackedUp) return;
            Assert.Equal(1L, work.Database.ReadScalar("SELECT count(*) FROM platform_asset_backup_pins"));
            Assert.Null(work.Database.Activities.TryEnterMaintenance("update"));
            await work.Database.WriteAsync(tx => tx.Execute("UPDATE backup_probe SET value='after'"));
            Assert.True(await work.Assets.RemoveAsync(original.Id));
            Assert.Equal(0, await work.Assets.CollectGarbageAsync());
            Assert.True(File.Exists(blob));
            Assert.Contains((await work.Assets.InspectAsync()), x => x.Key == original.Sha256 && x.ProtectionReasons.Contains("backupReference"));
            await Put(work.Assets, "created-after-snapshot");
        });
        await work.Database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot);
        var copy = work.Paths.Resolve("backups", "database", snapshot.Database.BackupId, "main.sqlite");
        Assert.Equal("before", Scalar(copy, "SELECT value FROM backup_probe"));
        Assert.Equal(1L, Scalar(copy, "SELECT count(*) FROM platform_assets"));
        Assert.Equal(0L, Scalar(copy, "SELECT count(*) FROM platform_asset_backup_pins"));
        Assert.Equal(0L, work.Database.ReadScalar("SELECT count(*) FROM platform_asset_backup_pins"));
        Assert.Equal("after", work.Database.ReadScalar("SELECT value FROM backup_probe"));
        Assert.Equal(1, await work.Assets.CollectGarbageAsync()); Assert.False(File.Exists(blob));
        await work.Database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot);
        Assert.Empty(work.Database.Activities.Snapshot().Activities);
    }

    [Theory]
    [InlineData("DatabaseBackedUp")]
    [InlineData("FilesCopied")]
    public async Task CancellationReleasesOrdinaryBackupPinsAndUnpublishedFiles(string at)
    {
        await using var work = new StorageTestWorkspace(); await Put(work.Assets, "retained");
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Database.CreateOrdinaryDataSnapshotAsync(work.Assets, stop.Token, point =>
        {
            if (point.ToString() == at) stop.Cancel(); return Task.CompletedTask;
        }));
        Assert.Equal(0L, work.Database.ReadScalar("SELECT count(*) FROM platform_asset_backup_pins"));
        Assert.Empty(Directory.GetFileSystemEntries(work.Paths.Resolve("backups", "data")));
        Assert.Empty(Directory.GetFileSystemEntries(work.Paths.Resolve("backups", "database")));
        Assert.Empty(new DataBackupStore(work.Database, work.Paths).Records());
        Assert.Empty(work.Database.Activities.Snapshot().Activities);
    }

    [Fact]
    public async Task ShutdownWaitsForBackupAndRestartClearsItsOldProcessPins()
    {
        await using var work = new StorageTestWorkspace(); await Put(work.Assets, "retained");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = work.Database.CreateOrdinaryDataSnapshotAsync(work.Assets, default, async point =>
        {
            if (point == DataSnapshotPersistencePoint.DatabaseBackedUp) { reached.SetResult(); await resume.Task; }
        });
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var closing = work.Database.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
        resume.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await closing;
        await work.RestartAsync(); await work.Assets.RecoverAsync();
        Assert.Equal(0L, work.Database.ReadScalar("SELECT count(*) FROM platform_asset_backup_pins"));
        Assert.Equal(1L, work.Database.ReadScalar("SELECT count(*) FROM platform_assets"));
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task CleanupKeepsOneAutomaticBackupAndAllRecoveryOrManualSnapshots()
    {
        await using var work = new StorageTestWorkspace(); await Put(work.Assets, "saved");
        var manual = await work.Database.CreateOrdinaryDataSnapshotAsync(work.Assets, DataBackupPurpose.Manual); work.Clock.Advance(TimeSpan.FromHours(1));
        var ordinary = new List<DataSnapshotDescriptor>();
        for (var i = 0; i < 3; i++)
        {
            ordinary.Add(await work.Database.CreateOrdinaryDataSnapshotAsync(work.Assets)); work.Clock.Advance(TimeSpan.FromHours(1));
        }
        DataSnapshotDescriptor recovery;
        using (var maintenance = work.Database.Activities.TryEnterMaintenance("update")!)
            recovery = await work.Database.CreateDataSnapshotAsync(maintenance);
        var control = work.Control(); var plan = await control.PreviewAsync("owner", new(["backups"]), "backup-preview");
        Assert.Equal(2, plan.Candidates.Count);
        var result = await control.ExecuteAsync(plan.Id, "owner", "backup-execute", StorageControlStore.ETag(plan));
        Assert.Equal("completed", result.State); Assert.True(result.ReclaimedBytes.Value > 0);
        foreach (var removed in ordinary.Take(2))
        {
            Assert.False(Directory.Exists(work.Paths.Resolve("backups", "data", removed.SnapshotId)));
            Assert.False(Directory.Exists(work.Paths.Resolve("backups", "database", removed.Database.BackupId)));
        }
        foreach (var saved in new[] { manual, ordinary[2], recovery }) await work.Database.VerifyDataSnapshotAsync(saved.SnapshotId, saved);
        var records = new DataBackupStore(work.Database, work.Paths).Records(); Assert.Equal(3, records.Count);
        Assert.True(records.Single(x => x.Snapshot.SnapshotId == recovery.SnapshotId).Retained);
        Assert.Empty((await control.PreviewAsync("owner", new(["backups"]), "backup-preview-again")).Candidates);
    }

    [Fact]
    public async Task RetentionAddedAfterPreviewProtectsTheBackup()
    {
        await using var work = new StorageTestWorkspace(); var ordinary = new List<DataSnapshotDescriptor>();
        for (var i = 0; i < 2; i++)
        { ordinary.Add(await work.Database.CreateOrdinaryDataSnapshotAsync(work.Assets)); work.Clock.Advance(TimeSpan.FromHours(1)); }
        var control = work.Control(); var plan = await control.PreviewAsync("owner", new(["backups"]), "retained-backup-preview");
        Assert.Single(plan.Candidates);
        await work.Database.WriteAsync(tx => tx.Execute("UPDATE platform_data_backups SET retained=1,revision='2' WHERE snapshot_id=$id",
            ("$id", ordinary[0].SnapshotId)));
        var result = await control.ExecuteAsync(plan.Id, "owner", "retained-backup-execute", StorageControlStore.ETag(plan));
        Assert.Equal(0UL, result.ReclaimedBytes.Value); Assert.Equal("changed", Assert.Single(result.Items).Reason);
        await work.Database.VerifyDataSnapshotAsync(ordinary[0].SnapshotId, ordinary[0]);
    }

    [Fact]
    public async Task UnregisteredBackupsAreReportedAsProtected()
    {
        await using var work = new StorageTestWorkspace(); var path = work.Paths.Resolve("backups", "unknown.sqlite");
        await File.WriteAllTextAsync(path, "recovery");
        var usage = await work.Control().UsageAsync();
        var backup = Assert.Single(usage.Categories, x => x.Category == "backups"); Assert.Equal(backup.Bytes, backup.ProtectedBytes);
        Assert.Empty((await work.Control().PreviewAsync("owner", new(["backups"]), "unknown-backup-preview")).Candidates);
        Assert.Equal("recovery", File.ReadAllText(path));
    }

    private static Task<MabiTool.Contracts.AssetSnapshot> Put(AssetStore assets, string text) =>
        assets.PutAsync("music-library", "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(text)));
    private static object? Scalar(string path, string sql)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open(); using var command = database.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
}
