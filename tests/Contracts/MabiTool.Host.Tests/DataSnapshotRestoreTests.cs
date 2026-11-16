using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class DataSnapshotRestoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyCandidateCanRestoreFullSnapshotWithIncreasingRevision(bool empty)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using var transaction = new InstallTransaction(workspace.Install);
        var healthy = await workspace.PrepareAsync(root, transaction, empty, health: true);
        Assert.Equal("healthy", healthy.Phase);
        var restored = await transaction.RestorePreviousAsync(healthy, root);
        Assert.Equal("previousPointerRestored", restored.Phase);
        Assert.Equal(8, restored.Revision);
        Assert.Equal(1, restored.HealthChecks);
        Assert.NotNull(restored.Health);
        Assert.NotNull(restored.FileRestore);
        workspace.AssertRestored(root, empty);
        Assert.Equal(restored.Revision, (await transaction.RestorePreviousAsync(restored, root)).Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoresMatchingDatabaseAssetsSettingsAndOriginalPointerAndRetainsNewContents(bool empty)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using var transaction = new InstallTransaction(workspace.Install);
        var switched = await workspace.PrepareAsync(root, transaction, empty);
        var restored = await transaction.RestorePreviousAsync(switched, root);
        Assert.Equal("previousPointerRestored", restored.Phase);
        Assert.Equal(7, restored.Revision);
        Assert.NotNull(restored.FileRestore);
        Assert.Equal(restored.FileRestorePlan, restored.FileRestore.Plan);
        Assert.Equal(switched.PreviousPointer, JsonSerializer.Deserialize<ActiveVersionPointer>(File.ReadAllText(workspace.Pointer), WebJson));
        Assert.Equal("previous", JsonNode.Parse(File.ReadAllText(workspace.Pointer))!["releaseId"]!.GetValue<string>());
        workspace.AssertRestored(root, empty);
        Assert.Equal(restored.FileRestore, (await transaction.RestorePreviousAsync(restored, root)).FileRestore);
        Assert.Equal(InstallRecoveryAction.PreviousVersionRestored, (await transaction.AssessRecoveryAsync()).Action);
        Assert.Equal("settings-new", File.ReadAllText(workspace.Recovery(root, "retained/settings/config.json")));
        Assert.True(File.Exists(workspace.Recovery(root, "retained/assets/new-only.txt")));
        Assert.Equal("cached", File.ReadAllText(root.Paths.Resolve("cache", "keep")));
        Assert.Equal("credential", File.ReadAllText(Path.Combine(root.Paths.BootstrapDirectory, "keep")));
        Assert.Equal("trust-state", File.ReadAllText(Path.Combine(workspace.Install, "update-state", "trust.txt")));
        root.Dispose();
        using var reopened = workspace.Open();
        workspace.AssertRestored(reopened, empty);
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("settings")]
    [InlineData("database")]
    [InlineData("extra")]
    public async Task InvalidSnapshotStopsBeforeDatabaseOrFileReplacement(string corrupt)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using var transaction = new InstallTransaction(workspace.Install);
        var switched = await workspace.PrepareAsync(root, transaction);
        var snapshot = switched.Snapshot!;
        var saved = root.Paths.Resolve("backups", "data", snapshot.SnapshotId);
        if (corrupt == "asset") File.WriteAllText(Path.Combine(saved, snapshot.Files.First(file => file.RelativePath.StartsWith("assets/")).RelativePath), "changed");
        if (corrupt == "settings") File.WriteAllText(Path.Combine(saved, "settings", "config.json"), "changed");
        if (corrupt == "database") File.WriteAllText(root.Paths.Resolve("backups", "database", snapshot.Database.BackupId, "main.sqlite"), "changed");
        if (corrupt == "extra") Directory.CreateDirectory(Path.Combine(saved, "unknown"));
        var database = File.ReadAllBytes(root.Paths.Database);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        await Assert.ThrowsAnyAsync<IOException>(() => transaction.RestorePreviousAsync(switched, root));
        Assert.Equal(database, File.ReadAllBytes(root.Paths.Database));
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.Equal("settings-new", File.ReadAllText(Path.Combine(root.Paths.Settings, "config.json")));
        Assert.False(File.Exists(root.Paths.Resolve("recovery", "database-" + workspace.TransactionId + ".sqlite")));
        Assert.Equal("pointerSwitched", (await transaction.ReadAsync()).Phase);
    }

    [Theory]
    [InlineData("active-settings")]
    [InlineData("active-assets")]
    [InlineData("retained-settings")]
    [InlineData("retained-assets")]
    [InlineData("missing-retained")]
    [InlineData("manifest")]
    [InlineData("missing-manifest")]
    [InlineData("extra-private")]
    public async Task CompletedRetryRejectsNewContentAndChangedEvidenceWithoutOverwriting(string corrupt)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using var transaction = new InstallTransaction(workspace.Install);
        var switched = await workspace.PrepareAsync(root, transaction);
        var restored = await transaction.RestorePreviousAsync(switched, root);
        if (corrupt == "active-settings") File.WriteAllText(Path.Combine(root.Paths.Settings, "config.json"), "new-after-restore");
        if (corrupt == "active-assets") File.WriteAllText(Path.Combine(root.Paths.Assets, "new-after-restore"), "new");
        if (corrupt == "retained-settings") File.WriteAllText(workspace.Recovery(root, "retained/settings/config.json"), "changed-evidence");
        if (corrupt == "retained-assets") File.WriteAllText(workspace.Recovery(root, "retained/assets/new-only.txt"), "changed-evidence");
        if (corrupt == "missing-retained") Directory.Delete(workspace.Recovery(root, "retained/settings"), true);
        if (corrupt == "manifest") File.AppendAllText(workspace.Recovery(root, "manifest.json"), " ");
        if (corrupt == "missing-manifest") File.Delete(workspace.Recovery(root, "manifest.json"));
        if (corrupt == "extra-private") Directory.CreateDirectory(workspace.Recovery(root, "stage/unknown"));
        var database = File.ReadAllBytes(root.Paths.Database);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        await Assert.ThrowsAnyAsync<IOException>(() => transaction.RestorePreviousAsync(restored, root));
        Assert.Equal(database, File.ReadAllBytes(root.Paths.Database));
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        if (corrupt == "active-settings") Assert.Equal("new-after-restore", File.ReadAllText(Path.Combine(root.Paths.Settings, "config.json")));
        if (corrupt == "active-assets") Assert.Equal("new", File.ReadAllText(Path.Combine(root.Paths.Assets, "new-after-restore")));
    }

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData("FilesPrepared", "pointerSwitched")]
    [InlineData("RollbackStarted", "rollbackStarted")]
    [InlineData("DatabaseReplaced", "rollbackStarted")]
    [InlineData("DatabaseRestored", "databaseRestored")]
    [InlineData("AssetsRetained", "databaseRestored")]
    [InlineData("AssetsRestored", "databaseRestored")]
    [InlineData("SettingsRetained", "databaseRestored")]
    [InlineData("SettingsRestored", "databaseRestored")]
    [InlineData("FilesRestored", "filesRestored")]
    [InlineData("PreviousPointerReplaced", "filesRestored")]
    [InlineData("PreviousPointerRestored", "previousPointerRestored")]
    public async Task KilledRollbackResumesFromActualTreesAndRecreatedEmptyDirectories(string stopAt, string phase)
    {
        using var workspace = new Workspace();
        using (var root = workspace.Open())
        using (var transaction = new InstallTransaction(workspace.Install))
            await workspace.PrepareAsync(root, transaction);
        await workspace.KillProbeAsync(stopAt);
        using var reopened = workspace.Open();
        Assert.True(Directory.Exists(reopened.Paths.Assets));
        Assert.True(Directory.Exists(reopened.Paths.Settings));
        using var recovered = new InstallTransaction(workspace.Install);
        var record = await recovered.ReadAsync();
        Assert.Equal(phase, record.Phase);
        var restored = await recovered.RestorePreviousAsync(record, reopened);
        Assert.Equal("previousPointerRestored", restored.Phase);
        workspace.AssertRestored(reopened);
        Assert.Equal("settings-new", File.ReadAllText(workspace.Recovery(reopened, "retained/settings/config.json")));
        Assert.Equal(restored.FileRestore, (await recovered.RestorePreviousAsync(restored, reopened)).FileRestore);
    }

    [Theory]
    [InlineData("AssetsRetained", "assets")]
    [InlineData("SettingsRetained", "settings")]
    public async Task UnknownNonemptyPlaceholderAfterRestartStopsWithoutDiscardingContents(string stopAt, string name)
    {
        using var workspace = new Workspace();
        using (var root = workspace.Open())
        using (var transaction = new InstallTransaction(workspace.Install))
            await workspace.PrepareAsync(root, transaction);
        await workspace.KillProbeAsync(stopAt);
        using var reopened = workspace.Open();
        var unknown = reopened.Paths.Resolve(name, "new-after-crash.txt");
        File.WriteAllText(unknown, "keep-this");
        using var recovered = new InstallTransaction(workspace.Install);
        var record = await recovered.ReadAsync();
        var pointer = File.ReadAllBytes(workspace.Pointer);
        await Assert.ThrowsAsync<StorageException>(() => recovered.RestorePreviousAsync(record, reopened));
        Assert.Equal("keep-this", File.ReadAllText(unknown));
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.True(Directory.Exists(workspace.Recovery(reopened, "stage/" + name)));
        Assert.True(Directory.Exists(workspace.Recovery(reopened, "retained/" + name)));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("stage")]
    [InlineData("original")]
    [InlineData("extra")]
    public async Task FixedPreparationRejectsChangedIntentOrTreesBeforeDatabaseReplacement(string corrupt)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using (var transaction = new InstallTransaction(workspace.Install, point => point == InstallPersistencePoint.RollbackStarted
            ? Task.FromException(new IOException("injected stop")) : Task.CompletedTask))
        {
            var switched = await workspace.PrepareAsync(root, transaction);
            await Assert.ThrowsAsync<IOException>(() => transaction.RestorePreviousAsync(switched, root));
        }
        if (corrupt == "intent") File.AppendAllText(workspace.Recovery(root, "manifest.json"), " ");
        if (corrupt == "stage") File.WriteAllText(workspace.Recovery(root, "stage/settings/config.json"), "changed");
        if (corrupt == "original") File.WriteAllText(Path.Combine(root.Paths.Settings, "config.json"), "new-writing");
        if (corrupt == "extra") File.WriteAllText(workspace.Recovery(root, "extra.txt"), "extra");
        var database = File.ReadAllBytes(root.Paths.Database);
        using var recovered = new InstallTransaction(workspace.Install);
        var record = await recovered.ReadAsync();
        await Assert.ThrowsAsync<StorageException>(() => recovered.RestorePreviousAsync(record, root));
        Assert.Equal(database, File.ReadAllBytes(root.Paths.Database));
        Assert.False(File.Exists(root.Paths.Resolve("recovery", "database-" + workspace.TransactionId + ".sqlite")));
    }

    [Theory]
    [InlineData("FilesPrepared", false)]
    [InlineData("RollbackStarted", true)]
    [InlineData("AssetsRetained", true)]
    [InlineData("FilesRestored", true)]
    public async Task CancellationBeforeIntentPreservesActiveDataAndAfterIntentCompletes(string cancelAt, bool completes)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using var cancel = new CancellationTokenSource();
        using var transaction = new InstallTransaction(workspace.Install, point =>
        {
            if (point.ToString() == cancelAt) cancel.Cancel();
            return Task.CompletedTask;
        });
        var switched = await workspace.PrepareAsync(root, transaction);
        var pending = transaction.RestorePreviousAsync(switched, root, cancel.Token);
        if (completes)
        {
            Assert.Equal("previousPointerRestored", (await pending).Phase);
            workspace.AssertRestored(root);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal("settings-new", File.ReadAllText(Path.Combine(root.Paths.Settings, "config.json")));
            Assert.Equal("pointerSwitched", (await transaction.ReadAsync()).Phase);
            Assert.False(File.Exists(root.Paths.Resolve("recovery", "database-" + workspace.TransactionId + ".sqlite")));
            var retried = await transaction.RestorePreviousAsync(switched, root);
            Assert.Equal("previousPointerRestored", retried.Phase);
        }
    }

    [Fact]
    public async Task OwnedStepRejectsActiveDatabaseAndWrongOwnershipAndKeepsDataRootLocked()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using var transaction = new InstallTransaction(workspace.Install);
        var switched = await workspace.PrepareAsync(root, transaction);
        await using (var database = SqliteStore.Open(root, AssetStore.Migrations))
            Assert.Equal("resource.busy", (await Assert.ThrowsAsync<StorageException>(() => transaction.RestorePreviousAsync(switched, root))).Code);
        using var other = new Workspace();
        using var otherRoot = other.Open();
        using var ownership = otherRoot.AcquireDatabaseOwnership();
        await Assert.ThrowsAsync<ArgumentException>(() => SqliteStore.PrepareDataSnapshotRestoreOwnedAsync(root, ownership, workspace.TransactionId, switched.Snapshot!));
        using var own = root.AcquireDatabaseOwnership();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = SqliteStore.PrepareDataSnapshotRestoreOwnedAsync(root, own, workspace.TransactionId, switched.Snapshot!, persisted: async point =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        root.Dispose();
        own.Dispose();
        try { Assert.Equal("storage.writerBusy", Assert.Throws<StorageException>(() => workspace.Open()).Code); }
        finally { release.TrySetResult(); }
        await pending;
        using var reopened = workspace.Open();
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Trait("Category", "Slow")]
    [Fact]
    public async Task OccupiedStageLeavesResumableTreesAndDoesNotRestorePointerEarly()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        FileStream? held = null;
        using (var transaction = new InstallTransaction(workspace.Install, point =>
        {
            if (point == InstallPersistencePoint.DatabaseRestored)
                held = new FileStream(workspace.Recovery(root, "stage/settings/config.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        }))
        {
            var switched = await workspace.PrepareAsync(root, transaction);
            try { await Assert.ThrowsAnyAsync<IOException>(() => transaction.RestorePreviousAsync(switched, root)); }
            finally { held?.Dispose(); }
            Assert.Equal("databaseRestored", (await transaction.ReadAsync()).Phase);
            Assert.Equal("candidate", JsonNode.Parse(File.ReadAllText(workspace.Pointer))!["releaseId"]!.GetValue<string>());
        }
        using var recovered = new InstallTransaction(workspace.Install);
        var record = await recovered.ReadAsync();
        Assert.Equal("previousPointerRestored", (await recovered.RestorePreviousAsync(record, root)).Phase);
        workspace.AssertRestored(root);
    }

    [Fact]
    public async Task UnboundIntentCannotAdoptMovedDirectoriesBeforeRollbackIsLogged()
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using (var transaction = new InstallTransaction(workspace.Install, point => point == InstallPersistencePoint.FilesPrepared
            ? Task.FromException(new IOException("injected stop")) : Task.CompletedTask))
        {
            var switched = await workspace.PrepareAsync(root, transaction);
            await Assert.ThrowsAsync<IOException>(() => transaction.RestorePreviousAsync(switched, root));
        }
        Directory.Move(root.Paths.Assets, workspace.Recovery(root, "retained/assets"));
        Directory.Move(workspace.Recovery(root, "stage/assets"), root.Paths.Assets);
        var database = File.ReadAllBytes(root.Paths.Database);
        using var recovered = new InstallTransaction(workspace.Install);
        var record = await recovered.ReadAsync();
        Assert.Null(record.FileRestorePlan);
        await Assert.ThrowsAsync<StorageException>(() => recovered.RestorePreviousAsync(record, root));
        Assert.Equal(database, File.ReadAllBytes(root.Paths.Database));
        Assert.True(Directory.Exists(workspace.Recovery(root, "retained/assets")));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("case")]
    public async Task UnboundIntentStillRequiresStrictMetadata(string corrupt)
    {
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using (var transaction = new InstallTransaction(workspace.Install, point => point == InstallPersistencePoint.FilesPrepared
            ? Task.FromException(new IOException("injected stop")) : Task.CompletedTask))
        {
            var switched = await workspace.PrepareAsync(root, transaction);
            await Assert.ThrowsAsync<IOException>(() => transaction.RestorePreviousAsync(switched, root));
        }
        var path = workspace.Recovery(root, "manifest.json");
        var text = File.ReadAllText(path);
        if (corrupt == "unknown") text = text.Insert(1, "\"unknown\":true,");
        if (corrupt == "duplicate") text = text.Insert(1, "\"schemaVersion\":1,");
        if (corrupt == "case") text = text.Replace("\"schemaVersion\":1", "\"SchemaVersion\":1", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        var database = File.ReadAllBytes(root.Paths.Database);
        using var recovered = new InstallTransaction(workspace.Install);
        var record = await recovered.ReadAsync();
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => recovered.RestorePreviousAsync(record, root))).Code);
        Assert.Equal(database, File.ReadAllBytes(root.Paths.Database));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("stage")]
    [InlineData("retained")]
    public async Task DirectoryJunctionStopsRecoveryAndPreservesExternalContents(string location)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var workspace = new Workspace();
        using var root = workspace.Open();
        using (var transaction = new InstallTransaction(workspace.Install, point => point == InstallPersistencePoint.RollbackStarted
            ? Task.FromException(new IOException("injected stop")) : Task.CompletedTask))
        {
            var switched = await workspace.PrepareAsync(root, transaction);
            await Assert.ThrowsAsync<IOException>(() => transaction.RestorePreviousAsync(switched, root));
        }
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep"), "external");
        var link = location == "active" ? Path.Combine(root.Paths.Settings, "link") : workspace.Recovery(root, location + "/settings/link");
        LocalDiscoverySecurity.EnsureDirectory(Path.GetDirectoryName(link)!);
        using (var junction = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", link, outside }
        })!)
        {
            await junction.WaitForExitAsync();
            Assert.Equal(0, junction.ExitCode);
        }
        try
        {
            using var recovered = new InstallTransaction(workspace.Install);
            var record = await recovered.ReadAsync();
            Assert.Equal("update.pathRejected", (await Assert.ThrowsAsync<FileTreeException>(() => recovered.RestorePreviousAsync(record, root))).Code);
            Assert.Equal("external", File.ReadAllText(Path.Combine(outside, "keep")));
            Assert.False(File.Exists(root.Paths.Resolve("recovery", "database-" + workspace.TransactionId + ".sqlite")));
        }
        finally { Directory.Delete(link); }
    }

    private sealed class Workspace : IDisposable
    {
        public string Repository { get; }
        public string Root { get; }
        public string Install => Path.Combine(Root, "install");
        public string Pointer => Path.Combine(Install, "launcher", "active-version.json");
        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        private string PlanId { get; } = Guid.NewGuid().ToString("N");
        private string Marker => Path.Combine(Root, "marker.txt");
        public Workspace()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            Repository = directory!.FullName;
            Root = Path.Combine(Repository, "artifacts", "tmp", "data-snapshot-restore-tests", Guid.NewGuid().ToString("N"));
            Write(Path.Combine(Install, "versions", "previous", "components", "host", "Host.dll"), "old-host");
            Write(Path.Combine(Install, "staging", PlanId, "components", "host", "Host.dll"), "new-host");
        }
        public DataRootLease Open() => DataRootLease.Open(new(Path.Combine(Root, "bootstrap"), true, Path.Combine(Root, "data")));
        public string Recovery(DataRootLease root, string relative) => UpdatePaths.Local(root.Paths.Resolve("recovery", "files-" + TransactionId), relative);
        public async Task<InstallTransactionRecord> PrepareAsync(DataRootLease root, InstallTransaction transaction, bool empty = false, bool health = false)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            DataSnapshotDescriptor snapshot;
            await using (var database = SqliteStore.Open(root, AssetStore.Migrations))
            await using (var assets = new AssetStore(database, root.Paths))
            {
                if (!empty)
                {
                    var old = await assets.PutAsync("music", "score", "application/octet-stream", new MemoryStream(Encoding.UTF8.GetBytes("old-asset")));
                    File.WriteAllText(Path.Combine(root.Paths.Settings, "config.json"), "settings-old");
                    Directory.CreateDirectory(Path.Combine(root.Paths.Settings, "繁體 空目录", "nested"));
                    using (var maintenance = database.Activities.TryEnterMaintenance("snapshot")!) snapshot = await database.CreateDataSnapshotAsync(maintenance);
                    await assets.RemoveAsync(old.Id);
                    await assets.CollectGarbageAsync();
                }
                else
                {
                    using var maintenance = database.Activities.TryEnterMaintenance("snapshot")!;
                    snapshot = await database.CreateDataSnapshotAsync(maintenance);
                }
                await assets.PutAsync("music", "score", "application/octet-stream", new MemoryStream(Encoding.UTF8.GetBytes("new-asset")));
                Directory.Delete(root.Paths.Settings, true);
                LocalDiscoverySecurity.EnsureDirectory(root.Paths.Settings);
                File.WriteAllText(Path.Combine(root.Paths.Settings, "config.json"), "settings-new");
                File.WriteAllText(Path.Combine(root.Paths.Assets, "new-only.txt"), "new-unreferenced");
            }
            File.WriteAllText(root.Paths.Resolve("cache", "keep"), "cached");
            File.WriteAllText(Path.Combine(root.Paths.BootstrapDirectory, "keep"), "credential");
            await transaction.InitializePointerAsync(Version("previous", 1, "old-host"));
            var prepared = health
                ? await transaction.PrepareForHealthAsync(TransactionId, PlanId, Version("previous", 1, "old-host"), Version("candidate", 2, "new-host"),
                    new("0.1.0", new(1, 0), new(2, 0), snapshot.RootId, snapshot.Database.DatabaseId, "live",
                        snapshot.Database.Migrations, [new("core", "0.1.0")]))
                : await transaction.PrepareAsync(TransactionId, PlanId, Version("previous", 1, "old-host"), Version("candidate", 2, "new-host"));
            var backed = await transaction.RecordDataSnapshotAsync(prepared, root.Paths, snapshot);
            File.WriteAllText(Path.Combine(Install, "update-state", "trust.txt"), "trust-state");
            var switched = await transaction.SwitchPointerAsync(backed);
            if (!health) return switched;
            var challenge = await transaction.BeginHealthCheckAsync(switched);
            var target = challenge.Record.HealthTarget!;
            var approval = await transaction.RecordHealthyAsync(challenge, new(TransactionId, challenge.Nonce, Guid.NewGuid().ToString("N"),
                target.HostVersion, "1.0", new(target.RootId, target.DatabaseId, target.BackendMode, target.Migrations), target.Modules));
            return approval.Record;
        }
        public void AssertRestored(DataRootLease root, bool empty = false)
        {
            if (empty)
            {
                Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Assets));
                Assert.Empty(Directory.GetFileSystemEntries(root.Paths.Settings));
            }
            else
            {
                Assert.Equal("settings-old", File.ReadAllText(Path.Combine(root.Paths.Settings, "config.json")));
                Assert.True(Directory.Exists(Path.Combine(root.Paths.Settings, "繁體 空目录", "nested")));
                var blob = Assert.Single(Directory.GetFiles(root.Paths.Assets, "*", SearchOption.AllDirectories));
                Assert.Equal("old-asset", File.ReadAllText(blob));
            }
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = root.Paths.Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "SELECT count(*) FROM platform_assets";
            Assert.Equal(empty ? 0L : 1L, command.ExecuteScalar());
        }
        public async Task KillProbeAsync(string stopAt)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { Path.Combine(Repository, "artifacts", "build", "bin", "MabiTool.PersistenceProbe", configuration, "MabiTool.PersistenceProbe.dll"), "install-rollback", Install, stopAt, Marker }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!File.Exists(Marker))
                {
                    if (child.HasExited) throw new InvalidOperationException($"probe exited {child.ExitCode}: {await errors}; {await output}");
                    await Task.Delay(25, deadline.Token);
                }
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                await Task.WhenAll(output, errors);
            }
        }
        private static InstallVersion Version(string id, long sequence, string content) => new(id, sequence, new string('a', 64),
            [new("components/host/Host.dll", Encoding.UTF8.GetByteCount(content), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant())]);
        private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
