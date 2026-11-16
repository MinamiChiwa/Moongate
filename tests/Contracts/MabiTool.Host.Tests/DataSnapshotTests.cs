using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class DataSnapshotTests
{
    [Fact]
    public async Task SnapshotKeepsMatchingWalAssetsAndConfigurationAfterOriginalsChangeAndGc()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, AssetStore.Migrations);
        await using var assets = new AssetStore(database, root.Paths);
        var first = await PutAsync(assets);
        var second = await PutAsync(assets);
        Assert.True(new FileInfo(root.Paths.Database + "-wal").Length > 32);
        Directory.CreateDirectory(Path.Combine(root.Paths.Settings, "空目录", "nested"));
        File.WriteAllText(Path.Combine(root.Paths.Settings, "繁體設定 file.json"), "old-settings");
        File.WriteAllText(root.Paths.Resolve("cache", "excluded.txt"), "cache");
        File.WriteAllText(Path.Combine(root.Paths.BootstrapDirectory, "credential.json"), "outside");
        DataSnapshotDescriptor snapshot;
        using (var maintenance = database.Activities.TryEnterMaintenance("snapshot")!)
        {
            snapshot = await database.CreateDataSnapshotAsync(maintenance);
            Assert.Single(snapshot.Files, file => file.RelativePath.StartsWith("assets/"));
            Assert.Contains("settings/空目录/nested", snapshot.Directories);
            Assert.DoesNotContain(snapshot.Files, file => file.RelativePath.Contains("excluded") || file.RelativePath.Contains("credential"));
            Assert.Equal(snapshot.Database.SizeBytes + snapshot.Files.Sum(file => file.SizeBytes), snapshot.TotalSizeBytes);
        }
        await assets.RemoveAsync(first.Id);
        await assets.RemoveAsync(second.Id);
        Assert.Equal(1, await assets.CollectGarbageAsync());
        File.WriteAllText(Path.Combine(root.Paths.Settings, "繁體設定 file.json"), "new-settings");
        File.WriteAllText(Path.Combine(root.Paths.Settings, "new.json"), "after-snapshot");
        var verified = await database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot);
        Assert.Equal(snapshot.SnapshotId, verified.SnapshotId);
        Assert.Equal("old-settings", File.ReadAllText(Path.Combine(workspace.Snapshot(root, snapshot), "settings", "繁體設定 file.json")));
        var savedDatabase = Path.Combine(root.Paths.Backups, "database", snapshot.Database.BackupId, "main.sqlite");
        Assert.Equal(2L, ReadScalar(savedDatabase, "SELECT count(*) FROM platform_assets"));
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM platform_assets"));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(root.Paths.BootstrapDirectory, "credential.json")));
    }

    [Fact]
    public async Task SnapshotSupportsCoreOnlyDatabaseAndEmptySettings()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root);
        using var maintenance = database.Activities.TryEnterMaintenance("snapshot")!;
        var snapshot = await database.CreateDataSnapshotAsync(maintenance);
        Assert.Empty(snapshot.Files);
        Assert.Equal(new[] { "assets", "settings" }, snapshot.Directories);
        await database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot);
    }

    [Fact]
    public async Task EmptyAssetIsPreservedAndSnapshotQuotaRejectsBeforeDatabaseBackup()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, AssetStore.Migrations);
        await using var assets = new AssetStore(database, root.Paths);
        var empty = await assets.PutAsync("music", "import", "application/octet-stream", new MemoryStream());
        using var maintenance = database.Activities.TryEnterMaintenance("snapshot")!;
        var snapshot = await database.CreateDataSnapshotAsync(maintenance);
        Assert.Equal(0, Assert.Single(snapshot.Files).SizeBytes);
        Assert.Equal(empty.Sha256, snapshot.Files[0].Sha256);
        await database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot);
        var snapshots = root.Paths.Resolve("backups", "data");
        for (var i = 1; i < SqliteStore.MaximumDataSnapshots; i++) Directory.CreateDirectory(Path.Combine(snapshots, i.ToString("x32")));
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<StorageException>(() => database.CreateDataSnapshotAsync(maintenance))).Code);
        Assert.Single(Directory.GetDirectories(root.Paths.Resolve("backups", "database")));
        await database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("damaged")]
    [InlineData("pending")]
    [InlineData("deleting")]
    [InlineData("length")]
    public async Task InvalidSourceNeverPublishesSnapshotAndCleansItsDatabaseCopy(string scenario)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, AssetStore.Migrations);
        await using var assets = new AssetStore(database, root.Paths);
        var asset = await PutAsync(assets);
        var blob = root.Paths.Resolve("assets", "sha256", asset.Sha256[..2], asset.Sha256);
        if (scenario == "missing") File.Delete(blob);
        if (scenario == "damaged") File.WriteAllText(blob, "tampered");
        if (scenario == "pending")
            await database.WriteAsync(tx => tx.Execute("UPDATE platform_asset_commits SET state='pending'"));
        if (scenario == "deleting")
            await database.WriteAsync(tx => tx.Execute("UPDATE platform_asset_blobs SET state='deleting'"));
        if (scenario == "length")
            await database.WriteAsync(tx => tx.Execute("UPDATE platform_assets SET size_bytes=size_bytes+1"));
        using var maintenance = database.Activities.TryEnterMaintenance("snapshot")!;
        await Assert.ThrowsAnyAsync<Exception>(() => database.CreateDataSnapshotAsync(maintenance));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Resolve("backups", "data")));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Resolve("backups", "database")));
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM platform_assets"));
        Assert.Empty(database.Activities.Snapshot().Activities);
    }

    [Theory]
    [InlineData("DatabaseBackedUp")]
    [InlineData("FilesCopied")]
    [InlineData("Published")]
    public async Task CancellationBeforePublicationCleansWorkAndAfterPublicationReturnsComplete(string cancelAt)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, AssetStore.Migrations);
        await using var assets = new AssetStore(database, root.Paths);
        await PutAsync(assets);
        using var maintenance = database.Activities.TryEnterMaintenance("snapshot")!;
        using var cancel = new CancellationTokenSource();
        var pending = database.CreateDataSnapshotAsync(maintenance, cancel.Token, point =>
        {
            if (point.ToString() == cancelAt) cancel.Cancel();
            return Task.CompletedTask;
        });
        if (cancelAt == "Published")
        {
            var completed = await pending;
            await database.VerifyDataSnapshotAsync(completed.SnapshotId, completed);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Resolve("backups", "data")));
            Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Resolve("backups", "database")));
        }
        Assert.Empty(database.Activities.Snapshot().Activities);
    }

    [Fact]
    public async Task ConfigurationChangeDuringCopyRejectsSnapshot()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root);
        var settings = Path.Combine(root.Paths.Settings, "settings.json");
        File.WriteAllText(settings, "before");
        using var maintenance = database.Activities.TryEnterMaintenance("snapshot")!;
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => database.CreateDataSnapshotAsync(
            maintenance, default, point =>
            {
                if (point == DataSnapshotPersistencePoint.FilesCopied) File.WriteAllText(settings, "after");
                return Task.CompletedTask;
            }))).Code);
        Assert.Equal("after", File.ReadAllText(settings));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Resolve("backups", "data")));
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("settings")]
    [InlineData("missing")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    [InlineData("database")]
    [InlineData("manifest-unknown")]
    [InlineData("manifest-duplicate")]
    [InlineData("manifest-case-field")]
    [InlineData("manifest-escape")]
    [InlineData("manifest-case")]
    [InlineData("manifest-no-assets")]
    [InlineData("manifest-total")]
    [InlineData("manifest-null")]
    [InlineData("manifest-directory")]
    public async Task VerificationRejectsChangedContentAndMetadata(string scenario)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root, AssetStore.Migrations);
        await using var assets = new AssetStore(database, root.Paths);
        await PutAsync(assets);
        File.WriteAllText(Path.Combine(root.Paths.Settings, "test.json"), "settings");
        DataSnapshotDescriptor snapshot;
        using (var maintenance = database.Activities.TryEnterMaintenance("snapshot")!)
            snapshot = await database.CreateDataSnapshotAsync(maintenance);
        var directory = workspace.Snapshot(root, snapshot);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (scenario == "asset") File.WriteAllText(Path.Combine(directory, snapshot.Files[0].RelativePath), "changed");
        if (scenario == "settings") File.WriteAllText(Path.Combine(directory, "settings", "test.json"), "changed");
        if (scenario == "missing") File.Delete(Path.Combine(directory, snapshot.Files[0].RelativePath));
        if (scenario == "extra-file") File.WriteAllText(Path.Combine(directory, "surprise.txt"), "extra");
        if (scenario == "extra-directory") Directory.CreateDirectory(Path.Combine(directory, "empty-surprise"));
        if (scenario == "database")
            File.WriteAllBytes(Path.Combine(root.Paths.Backups, "database", snapshot.Database.BackupId, "main.sqlite"), [1, 2, 3]);
        if (scenario.StartsWith("manifest-"))
        {
            var node = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            if (scenario == "manifest-unknown") node["extra"] = true;
            if (scenario == "manifest-case-field") node["RootId"] = snapshot.RootId;
            if (scenario == "manifest-escape") node["files"]![0]!["relativePath"] = "settings/../../outside";
            if (scenario == "manifest-case") node["files"]![0]!["relativePath"] = snapshot.Files[0].RelativePath.ToUpperInvariant();
            if (scenario == "manifest-no-assets")
            {
                node["files"]!.AsArray().RemoveAt(0);
                node["totalSizeBytes"] = snapshot.TotalSizeBytes - snapshot.Files[0].SizeBytes;
                node["directories"] = JsonSerializer.SerializeToNode(new[] { "assets", "settings" });
            }
            if (scenario == "manifest-total") node["totalSizeBytes"] = snapshot.TotalSizeBytes + 1;
            if (scenario == "manifest-null") node["files"]![0] = null;
            if (scenario == "manifest-directory") node["directories"]!.AsArray().Add("settings/../../outside");
            var text = node.ToJsonString();
            if (scenario == "manifest-duplicate") text = text.Insert(1, "\"schemaVersion\":1,");
            File.WriteAllText(manifestPath, text);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => database.VerifyDataSnapshotAsync(snapshot.SnapshotId));
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM platform_assets"));
    }

    [Fact]
    public async Task VerificationRequiresFixedDescriptorAndCurrentRootIdentity()
    {
        using var workspace = new Workspace();
        using var otherWorkspace = new Workspace();
        using var root = workspace.Open();
        using var other = otherWorkspace.Open();
        await using var database = SqliteStore.Open(root);
        DataSnapshotDescriptor snapshot;
        using (var maintenance = database.Activities.TryEnterMaintenance("snapshot")!)
            snapshot = await database.CreateDataSnapshotAsync(maintenance);
        await Assert.ThrowsAsync<StorageException>(() => database.VerifyDataSnapshotAsync(snapshot.SnapshotId,
            snapshot with { CreatedAt = snapshot.CreatedAt.AddSeconds(1) }));
        var otherSnapshot = other.Paths.Resolve("backups", "data", snapshot.SnapshotId);
        Directory.CreateDirectory(otherSnapshot);
        File.Copy(Path.Combine(workspace.Snapshot(root, snapshot), "manifest.json"), Path.Combine(otherSnapshot, "manifest.json"));
        await Assert.ThrowsAsync<StorageException>(() => SqliteStore.VerifyDataSnapshotAsync(other.Paths, snapshot.SnapshotId, null, default));
    }

    [Fact]
    public async Task MaintenanceSubactivityAndDatabaseOwnershipLastUntilSnapshotEnds()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        var database = SqliteStore.Open(root);
        var maintenance = database.Activities.TryEnterMaintenance("snapshot")!;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = database.CreateDataSnapshotAsync(maintenance, default, async point =>
        {
            if (point != DataSnapshotPersistencePoint.DatabaseBackedUp) return;
            reached.SetResult();
            await resume.Task;
        });
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            maintenance.Dispose();
            Assert.Equal("update.maintenanceActive", Assert.Throws<ActivityBarrierException>(() => database.Activities.EnterActivity("test", "write")).Code);
            Assert.Equal("resource.busy", Assert.Throws<StorageException>(() => root.AcquireDatabaseOwnership()).Code);
            var stopping = database.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            resume.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot);
            await stopping;
            Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
            using var released = root.AcquireDatabaseOwnership();
        }
        finally
        {
            resume.TrySetResult();
            maintenance.Dispose();
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task SnapshotRejectsForeignDisposedAndMissingMaintenanceRights()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root);
        using var foreign = new ActivityBarrier().TryEnterMaintenance("foreign")!;
        await Assert.ThrowsAsync<ActivityBarrierException>(() => database.CreateDataSnapshotAsync(foreign));
        await Assert.ThrowsAsync<ActivityBarrierException>(() => database.CreateDataSnapshotAsync(null!));
        var disposed = database.Activities.TryEnterMaintenance("disposed")!;
        disposed.Dispose();
        await Assert.ThrowsAsync<ActivityBarrierException>(() => database.CreateDataSnapshotAsync(disposed));
        Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Backups));
        using var maintenance = database.Activities.TryEnterMaintenance("valid")!;
        var backup = await database.CreateDatabaseBackupAsync(maintenance);
        await Assert.ThrowsAsync<ActivityBarrierException>(() => database.PreflightDatabaseMigrationsAsync(backup, [], null!));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("C:\\outside")]
    public async Task SnapshotLookupRejectsNonGeneratedIdentity(string id)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root);
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => database.VerifyDataSnapshotAsync(id))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotRejectsDirectoryJunctionsWithoutReadingOrChangingTarget(bool published)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        await using var database = SqliteStore.Open(root);
        DataSnapshotDescriptor? snapshot = null;
        if (published)
        {
            using var prepare = database.Activities.TryEnterMaintenance("snapshot")!;
            snapshot = await database.CreateDataSnapshotAsync(prepare);
        }
        var outside = Path.Combine(workspace.Directory, "outside");
        Directory.CreateDirectory(outside);
        var original = Path.Combine(outside, "original.txt");
        File.WriteAllText(original, "preserved");
        var link = Path.Combine(published ? workspace.Snapshot(root, snapshot!) : root.Paths.Settings, "linked");
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", link, outside }
        };
        using (var junction = Process.Start(start)!)
        {
            await junction.WaitForExitAsync();
            Assert.Equal(0, junction.ExitCode);
        }
        try
        {
            if (published) await Assert.ThrowsAnyAsync<Exception>(() => database.VerifyDataSnapshotAsync(snapshot!.SnapshotId));
            else
            {
                using var maintenance = database.Activities.TryEnterMaintenance("snapshot")!;
                await Assert.ThrowsAnyAsync<Exception>(() => database.CreateDataSnapshotAsync(maintenance));
                Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Resolve("backups", "data")));
            }
            Assert.Equal("preserved", File.ReadAllText(original));
            Assert.Single(Directory.GetFiles(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("DatabaseBackedUp")]
    [InlineData("FilesCopied")]
    [InlineData("Published")]
    public async Task KilledSnapshotIsEitherPrivateWorkOrCompleteAndCanBeRetried(string stopAt)
    {
        using var workspace = new Workspace();
        using (var root = workspace.Open())
        {
            await using var database = SqliteStore.Open(root, AssetStore.Migrations);
            await using var assets = new AssetStore(database, root.Paths);
            await PutAsync(assets);
            File.WriteAllText(Path.Combine(root.Paths.Settings, "source.json"), "saved-config");
        }
        var marker = Path.Combine(workspace.Directory, "marker.txt");
        var probe = Path.Combine(workspace.Repository, "artifacts", "build", "bin", "MabiTool.PersistenceProbe", "release", "MabiTool.PersistenceProbe.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { probe, "data-snapshot-" + stopAt, workspace.Bootstrap, workspace.Data, marker }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!File.Exists(marker))
            {
                if (child.HasExited) throw new InvalidOperationException($"snapshot probe exited {child.ExitCode}: {await errors}; {await output}");
                await Task.Delay(25, deadline.Token);
            }
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await Task.WhenAll(output, errors);
        }
        using var reopened = workspace.Open();
        await using var store = SqliteStore.Open(reopened, AssetStore.Migrations);
        var previous = Assert.Single(Directory.GetDirectories(reopened.Paths.Resolve("backups", "data")));
        var previousId = Path.GetFileName(previous);
        if (stopAt == "Published") await store.VerifyDataSnapshotAsync(previousId);
        else
        {
            Assert.Contains(".work-", previousId);
            await Assert.ThrowsAsync<StorageException>(() => store.VerifyDataSnapshotAsync(previousId));
        }
        using var maintenance = store.Activities.TryEnterMaintenance("retry")!;
        var retried = await store.CreateDataSnapshotAsync(maintenance);
        Assert.NotEqual(previousId, retried.SnapshotId);
        await store.VerifyDataSnapshotAsync(retried.SnapshotId, retried);
        Assert.Equal(1L, store.ReadScalar("SELECT count(*) FROM platform_assets"));
        Assert.Equal("saved-config", File.ReadAllText(Path.Combine(reopened.Paths.Settings, "source.json")));
    }

    private static Task<MabiTool.Contracts.AssetSnapshot> PutAsync(AssetStore assets) =>
        assets.PutAsync("color", "profile", "application/octet-stream", new MemoryStream(Encoding.UTF8.GetBytes("shared-content")));

    private static object? ReadScalar(string path, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class Workspace : IDisposable
    {
        public string Repository { get; }
        public string Directory { get; }
        public string Bootstrap => Path.Combine(Directory, "bootstrap");
        public string Data => Path.Combine(Directory, "data");
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            Repository = repository!.FullName;
            Directory = Path.Combine(Repository, "artifacts", "tmp", "data-snapshot-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
        }
        public DataRootLease Open() => DataRootLease.Open(new(Bootstrap, true, Data));
        public string Snapshot(DataRootLease root, DataSnapshotDescriptor snapshot) => root.Paths.Resolve("backups", "data", snapshot.SnapshotId);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
