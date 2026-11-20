using System.Diagnostics;
using System.Text.Json.Nodes;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class StorageTests
{
    [Fact]
    public async Task SameRootLeaseCannotOpenTwoDatabasesOrRecoveryOwners()
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        var database = SqliteStore.Open(lease);
        try
        {
            Assert.Equal("resource.busy", Assert.Throws<StorageException>(() => SqliteStore.Open(lease)).Code);
            Assert.Equal("resource.busy", Assert.Throws<StorageException>(() => lease.AcquireDatabaseOwnership()).Code);
            Assert.Equal("ok", database.ReadScalar("PRAGMA quick_check"));
        }
        finally { await database.DisposeAsync(); }
        using (lease.AcquireDatabaseOwnership())
            Assert.Equal("resource.busy", Assert.Throws<StorageException>(() => SqliteStore.Open(lease)).Code);
        await using var reopened = SqliteStore.Open(lease);
        Assert.Equal("ok", reopened.ReadScalar("PRAGMA quick_check"));
    }

    [Fact]
    public async Task DatabaseOpenFailureReleasesOwnershipForNextAttempt()
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        Assert.Throws<StorageException>(() => SqliteStore.Open(lease,
            [new DatabaseMigration("invalid", 1, "INVALID SQL")]));
        await using var database = SqliteStore.Open(lease);
        Assert.Equal("ok", database.ReadScalar("PRAGMA quick_check"));
    }

    [Fact]
    public async Task DisposedRootKeepsWriterLockUntilDatabaseCloses()
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        var database = SqliteStore.Open(lease);
        try
        {
            lease.Dispose();
            Assert.Equal("storage.writerBusy", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)).Code);
            Assert.Throws<ObjectDisposedException>(() => SqliteStore.Open(lease));
            Assert.Throws<ObjectDisposedException>(() => lease.AcquireDatabaseOwnership());
            Assert.Equal("ok", database.ReadScalar("PRAGMA quick_check"));
        }
        finally { await database.DisposeAsync(); }
        using var reopened = DataRootLease.Open(workspace.Options);
        Assert.Equal(lease.DatabaseId, reopened.DatabaseId);
    }

    [Fact]
    public void RecoveryOwnershipAlsoKeepsDisposedRootLockedAndReleasesOnlyOnce()
    {
        using var workspace = new Workspace();
        var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        using var ownership = lease.AcquireDatabaseOwnership();
        lease.Dispose();
        Assert.Equal("storage.writerBusy", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)).Code);
        ownership.Dispose();
        ownership.Dispose();
        lease.Dispose();
        using var reopened = DataRootLease.Open(workspace.Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDatabaseDisposalsWaitForActiveReaderAndKeepOwnership(bool failingRead)
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        var database = SqliteStore.Open(lease);
        using var resume = new ManualResetEventSlim();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = Task.Run(() => database.Read(connection =>
        {
            reached.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            if (failingRead) throw new InvalidOperationException("reader failed");
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            return command.ExecuteScalar();
        }));
        Task? first = null;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first = database.DisposeAsync().AsTask();
            var second = database.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => database.ReadScalar("SELECT 1"));
            Assert.Equal("resource.busy", Assert.Throws<StorageException>(() => lease.AcquireDatabaseOwnership()).Code);
        }
        finally
        {
            resume.Set();
            if (failingRead) await Assert.ThrowsAsync<InvalidOperationException>(() => reading);
            else Assert.Equal("ok", await reading);
            await database.DisposeAsync();
        }
        Assert.True(first!.IsCompletedSuccessfully);
        using var ownership = lease.AcquireDatabaseOwnership();
    }

    [Theory]
    [InlineData("Journaled")]
    [InlineData("BlobPublished")]
    public async Task AssetMutationBlocksMaintenanceAcrossFileAndDatabasePhases(string pauseAt)
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        await using var database = SqliteStore.Open(lease, AssetStore.Migrations);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var assets = new AssetStore(database, lease.Paths, null, async phase =>
        {
            if (phase.ToString() != pauseAt) return;
            reached.TrySetResult();
            await resume.Task;
        });
        var upload = assets.PutAsync("music-library", "scope", "text/plain", new MemoryStream([1, 2, 3]));
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(database.Activities.TryEnterMaintenance("updater"));
            using var cancel = new CancellationTokenSource();
            var queued = assets.CollectGarbageAsync(cancel.Token);
            Assert.Equal(2, database.Activities.Snapshot().Activities.Count);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Single(database.Activities.Snapshot().Activities);
        }
        finally { resume.TrySetResult(); }
        var asset = await upload;
        using (var maintenance = database.Activities.TryEnterMaintenance("updater"))
        {
            Assert.NotNull(maintenance);
            Assert.NotNull(assets.Get(asset.Id));
            await Assert.ThrowsAsync<ActivityBarrierException>(() => assets.PutAsync("music-library", "scope", "text/plain", new MemoryStream([4])));
            await Assert.ThrowsAsync<ActivityBarrierException>(() => assets.PinAsync(asset.Id, "test"));
            await Assert.ThrowsAsync<ActivityBarrierException>(() => assets.UnpinAsync("missing"));
            await Assert.ThrowsAsync<ActivityBarrierException>(() => assets.RemoveAsync(asset.Id));
            await Assert.ThrowsAsync<ActivityBarrierException>(() => assets.CollectGarbageAsync());
            await Assert.ThrowsAsync<ActivityBarrierException>(() => assets.RecoverAsync());
        }
        Assert.True(await assets.RemoveAsync(asset.Id));
        Assert.Equal(1, await assets.CollectGarbageAsync());
        Assert.Empty(database.Activities.Snapshot().Activities);
    }

    [Fact]
    public async Task AssetsShareImmutableBlobsAndPinsProtectTheLastReference()
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        await using var database = SqliteStore.Open(lease, AssetStore.Migrations);
        await using var assets = new AssetStore(database, lease.Paths);
        var bytes = System.Text.Encoding.UTF8.GetBytes("same-content");
        var first = await assets.PutAsync("music-library", "tw:character-1", "text/plain", new MemoryStream(bytes));
        var second = await assets.PutAsync("music-library", "tw:character-1", "text/plain", new MemoryStream(bytes));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM platform_asset_blobs"));
        var pin = await assets.PinAsync(first.Id, "export:1");
        Assert.Equal("storage.assetInUse", (await Assert.ThrowsAsync<StorageException>(() => assets.RemoveAsync(first.Id))).Code);
        Assert.True(await assets.UnpinAsync(pin));
        Assert.True(await assets.RemoveAsync(first.Id));
        Assert.Equal(0, await assets.CollectGarbageAsync());
        Assert.NotNull(assets.Get(second.Id));
        Assert.True(await assets.RemoveAsync(second.Id));
        Assert.Equal(1, await assets.CollectGarbageAsync());
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM platform_asset_blobs"));
    }

    [Fact]
    public async Task AssetRecoveryDistinguishesJournalAndPublishedBlobCrashes()
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        await using var database = SqliteStore.Open(lease, AssetStore.Migrations);
        var content = System.Text.Encoding.UTF8.GetBytes("recovery-content");
        await using (var beforePublish = new AssetStore(database, lease.Paths, null, phase =>
                     phase == AssetCommitPhase.Journaled ? Task.FromException(new IOException("crash")) : Task.CompletedTask))
        {
            await Assert.ThrowsAsync<IOException>(() => beforePublish.PutAsync("music-library", "scope", "text/plain", new MemoryStream(content)));
        }
        await using (var recovery = new AssetStore(database, lease.Paths)) await recovery.RecoverAsync();
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM platform_assets"));
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM platform_asset_commits WHERE state='failed'"));

        await using (var afterPublish = new AssetStore(database, lease.Paths, null, phase =>
                     phase == AssetCommitPhase.BlobPublished ? Task.FromException(new IOException("crash")) : Task.CompletedTask))
        {
            await Assert.ThrowsAsync<IOException>(() => afterPublish.PutAsync("music-library", "scope", "text/plain", new MemoryStream(content)));
        }
        await using (var recovery = new AssetStore(database, lease.Paths)) await recovery.RecoverAsync();
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM platform_assets"));
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM platform_asset_commits WHERE state='committed'"));
    }

    [Fact]
    public async Task AssetReadDetectsMissingOrModifiedBlobAndConcurrentGcPreservesNewReference()
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        await using var database = SqliteStore.Open(lease, AssetStore.Migrations);
        await using var assets = new AssetStore(database, lease.Paths);
        var bytes = System.Text.Encoding.UTF8.GetBytes("protected-content");
        var asset = await assets.PutAsync("music-library", "scope", "text/plain", new MemoryStream(bytes));
        await Task.WhenAll(assets.CollectGarbageAsync(), assets.PutAsync("music-library", "scope", "text/plain", new MemoryStream(bytes)));
        Assert.NotNull(assets.Get(asset.Id));
        var path = lease.Paths.Resolve("assets", "sha256", asset.Sha256[..2], asset.Sha256);
        File.WriteAllText(path, "tampered");
        Assert.Equal("storage.assetDamaged", Assert.Throws<StorageException>(() => assets.Get(asset.Id)).Code);
        File.Delete(path);
        Assert.Equal("storage.assetDamaged", Assert.Throws<StorageException>(() => assets.Get(asset.Id)).Code);
    }

    [Fact]
    public void FirstStartRequiresInitializationAndStableConfigurationRetainsIdentity()
    {
        using var workspace = new Workspace();
        var options = workspace.Options;
        Assert.Equal("storage.notInitialized", Assert.Throws<StorageException>(() => DataRootLease.Open(options)).Code);
        Assert.False(Directory.Exists(options.BootstrapDirectory));
        Assert.False(Directory.Exists(options.DataRoot));
        Directory.CreateDirectory(options.DataRoot!);
        string identity;
        using (var first = DataRootLease.Open(options with { Initialize = true }))
        {
            identity = first.Paths.RootId;
            Assert.Equal(options.DataRoot, first.Paths.DataRoot);
            Assert.Equal(Path.Combine(options.DataRoot!, "store", "main.sqlite"), first.Paths.Database);
            Assert.True(Directory.Exists(first.Paths.Assets));
            Assert.True(Directory.Exists(first.Paths.Backups));
            Assert.False(first.Paths.Portable);
            File.WriteAllText(Path.Combine(first.Paths.Settings, "keep.json"), "user-content");
        }
        using var second = DataRootLease.Open(options with { DataRoot = null });
        Assert.Equal(identity, second.Paths.RootId);
        Assert.Equal("user-content", File.ReadAllText(second.Paths.Resolve("settings", "keep.json")));
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(options.BootstrapDirectory, "data-root.json")))!;
        Assert.Equal(options.DataRoot, config["dataRoot"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(options.DataRoot!, "data-root.json")));
    }

    [Fact]
    public void MissingOrReplacedRootNeverCreatesAnEmptyReplacementEvenWithInitializeFlag()
    {
        using var workspace = new Workspace();
        using (DataRootLease.Open(workspace.Options with { Initialize = true })) { }
        var config = File.ReadAllText(workspace.Configuration);
        var moved = workspace.Root + "-original";
        Directory.Move(workspace.Root, moved);
        foreach (var initialize in new[] { false, true })
        {
            Assert.Equal("storage.rootMissing", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options with { Initialize = initialize })).Code);
            Assert.False(Directory.Exists(workspace.Root));
            Assert.Equal(config, File.ReadAllText(workspace.Configuration));
        }
        Directory.CreateDirectory(workspace.Root);
        Assert.Equal("storage.identityMismatch", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)).Code);
        Directory.Delete(workspace.Root);
        Directory.Move(moved, workspace.Root);
        var markerPath = Path.Combine(workspace.Root, ".mabitool-data-root.json");
        var marker = JsonNode.Parse(File.ReadAllText(markerPath))!;
        marker["rootId"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(markerPath, marker.ToJsonString());
        Assert.Equal("storage.identityMismatch", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)).Code);
        Assert.Equal(config, File.ReadAllText(workspace.Configuration));
    }

    [Fact]
    public void WriterLockCoversDifferentBootstrapConfigurationsAndReleasesOnDispose()
    {
        using var workspace = new Workspace();
        using (var first = DataRootLease.Open(workspace.Options with { Initialize = true }))
        {
            Assert.Equal("storage.writerBusy", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)).Code);
            var alternate = workspace.Options with { BootstrapDirectory = Path.Combine(workspace.Directory, "other-bootstrap"), Initialize = true };
            Assert.Equal("storage.writerBusy", Assert.Throws<StorageException>(() => DataRootLease.Open(alternate)).Code);
            Assert.False(File.Exists(Path.Combine(alternate.BootstrapDirectory, "data-root.json")));
        }
        using var reopened = DataRootLease.Open(workspace.Options);
        Assert.Equal(workspace.Root, reopened.Paths.DataRoot);
    }

    [Fact]
    public void PortableDataRemainsOutsideVersionDirectoriesAndRootChangesAreRejected()
    {
        using var workspace = new Workspace();
        var portable = Path.Combine(workspace.Directory, "portable");
        var options = new StorageOptions(Path.Combine(portable, "bootstrap"), true, PortableRoot: portable);
        string identity;
        using (var first = DataRootLease.Open(options))
        {
            identity = first.Paths.RootId;
            Assert.Equal(Path.Combine(portable, "data"), first.Paths.DataRoot);
            Assert.True(first.Paths.Portable);
            Assert.DoesNotContain("versions", first.Paths.Database);
        }
        using (var second = DataRootLease.Open(options with { Initialize = false })) Assert.Equal(identity, second.Paths.RootId);
        var changed = options with { PortableRoot = null, DataRoot = Path.Combine(workspace.Directory, "other-data") };
        Assert.Equal("storage.configurationConflict", Assert.Throws<StorageException>(() => DataRootLease.Open(changed)).Code);
        Assert.False(Directory.Exists(changed.DataRoot));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("D:\\outside")]
    [InlineData("a:b")]
    [InlineData("con.txt")]
    [InlineData("trailing.")]
    [InlineData("")]
    public void LogicalPathsRejectTraversalDeviceNamesAndAlternateStreams(string segment)
    {
        using var workspace = new Workspace();
        using var lease = DataRootLease.Open(workspace.Options with { Initialize = true });
        Assert.Equal("storage.pathInvalid", Assert.Throws<StorageException>(() => lease.Paths.Resolve("temp", segment)).Code);
    }

    [Fact]
    public void NonLocalPathsNonemptyDirectoriesAndCorruptConfigurationAreRejected()
    {
        using var workspace = new Workspace();
        foreach (var path in new[] { @"\\server\share\data", @"\\?\D:\data", "relative/data", workspace.Root + @"\..\other" })
            Assert.Equal("storage.pathInvalid", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options with { Initialize = true, DataRoot = path })).Code);
        Directory.CreateDirectory(workspace.Root);
        var userFile = Path.Combine(workspace.Root, "keep.txt");
        File.WriteAllText(userFile, "keep");
        var foreign = Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options with { Initialize = true }));
        Assert.Equal("storage.rootNotEmpty", foreign.Code);
        Assert.Contains("keep.txt", foreign.Message);
        Assert.Equal("keep", File.ReadAllText(userFile));
        File.Delete(userFile);
        using (DataRootLease.Open(workspace.Options with { Initialize = true })) { }
        File.WriteAllText(workspace.Configuration, "{broken");
        Assert.Equal("storage.unavailable", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)).Code);
        Assert.Equal("{broken", File.ReadAllText(workspace.Configuration));
    }

    /// <summary>
    /// 首次初始化在取得写锁后、发布身份标记前中断（实机：所有者校验失败）：数据根只剩写锁、发布标记的临时文件与空的标准子目录，
    /// 引导目录只有 bootstrap.lock。再次以初始化方式打开时续做完成，得到有效身份标记与引导配置；有用户数据的子目录仍被拒绝并报出该项。
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void InitializationInterruptedBeforeTheMarkerResumesAndForeignContentIsNamed()
    {
        using var workspace = new Workspace();
        // 与初始化相同：目录由宿主建为当前用户独占，锁文件继承该权限。
        LocalDiscoverySecurity.EnsureDirectory(Path.GetDirectoryName(workspace.Configuration)!);
        LocalDiscoverySecurity.EnsureDirectory(workspace.Root);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(workspace.Configuration)!, "bootstrap.lock"), []);
        File.WriteAllBytes(Path.Combine(workspace.Root, "writer.lock"), []);
        var temporary = Path.Combine(workspace.Root, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temporary, "{\"version\":2}");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "store"));
        Assert.Equal("storage.notInitialized", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)).Code);
        string rootId;
        using (var lease = DataRootLease.Open(workspace.Options with { Initialize = true })) rootId = lease.Paths.RootId;
        Assert.True(File.Exists(Path.Combine(workspace.Root, ".mabitool-data-root.json")));
        Assert.False(File.Exists(temporary));
        Assert.Equal(rootId, JsonNode.Parse(File.ReadAllText(workspace.Configuration))!["rootId"]!.GetValue<string>());
        using (var reopened = DataRootLease.Open(workspace.Options)) Assert.Equal(rootId, reopened.Paths.RootId);

        using var other = new Workspace();
        LocalDiscoverySecurity.EnsureDirectory(other.Root);
        Directory.CreateDirectory(Path.Combine(other.Root, "store"));
        File.WriteAllBytes(Path.Combine(other.Root, "writer.lock"), []);
        File.WriteAllText(Path.Combine(other.Root, "store", "main.sqlite"), "data");
        var error = Assert.Throws<StorageException>(() => DataRootLease.Open(other.Options with { Initialize = true }));
        Assert.Equal("storage.rootNotEmpty", error.Code);
        Assert.Contains("store" + Path.DirectorySeparatorChar, error.Message);
        Assert.Equal("data", File.ReadAllText(Path.Combine(other.Root, "store", "main.sqlite")));
        Assert.False(File.Exists(other.Configuration));
    }

    /// <summary>身份标记已发布、引导配置尚未写出时中断，随后整个文件夹被搬走：在新位置续做初始化，权威引导目录改为当前位置。</summary>
    [Fact]
    public void InitializationInterruptedAfterTheMarkerResumesAfterTheFolderMoved()
    {
        using var original = new Workspace();
        using (DataRootLease.Open(original.Options with { Initialize = true })) { }
        File.Delete(original.Configuration);
        using var moved = new Workspace();
        Directory.Move(Path.GetDirectoryName(original.Configuration)!, Path.GetDirectoryName(moved.Configuration)!);
        Directory.Move(original.Root, moved.Root);
        string rootId;
        using (var lease = DataRootLease.Open(moved.Options with { Initialize = true })) rootId = lease.Paths.RootId;
        var marker = JsonNode.Parse(File.ReadAllText(Path.Combine(moved.Root, ".mabitool-data-root.json")))!;
        Assert.Equal(Path.GetDirectoryName(moved.Configuration), marker["authorityBootstrap"]!.GetValue<string>());
        Assert.Equal(rootId, JsonNode.Parse(File.ReadAllText(moved.Configuration))!["rootId"]!.GetValue<string>());
        using (DataRootLease.Open(moved.Options)) { }
    }

    [Fact]
    public void ReadOnlyLockAndDirectoryLinksFailWithoutChangingConfiguration()
    {
        using var workspace = new Workspace();
        using (DataRootLease.Open(workspace.Options with { Initialize = true })) { }
        var original = File.ReadAllText(workspace.Configuration);
        var writer = Path.Combine(workspace.Root, "writer.lock");
        File.SetAttributes(writer, FileAttributes.ReadOnly);
        try { Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)); }
        finally { File.SetAttributes(writer, FileAttributes.Normal); }
        Assert.Equal(original, File.ReadAllText(workspace.Configuration));
        var target = Path.Combine(workspace.Directory, "outside");
        Directory.CreateDirectory(target);
        var link = Path.Combine(workspace.Root, "cache", "linked");
        using var junction = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        junction.WaitForExit();
        Assert.Equal(0, junction.ExitCode);
        try
        {
            using var lease = DataRootLease.Open(workspace.Options);
            var error = Assert.Throws<LinkedPathException>(() => lease.Paths.Resolve("cache", "linked", "file.json"));
            Assert.Equal((link, false), (error.LinkedPath, error.CloudPlaceholder));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally { Directory.Delete(link); }
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task ActualHostRetainsDataIdentityAcrossWorkingDirectoriesAndForcedTermination()
    {
        using var workspace = new Workspace();
        using (DataRootLease.Open(workspace.Options with { Initialize = true })) { }
        var original = File.ReadAllText(workspace.Configuration);
        for (var i = 0; i < 2; i++)
        {
            using var child = workspace.StartHost(i == 0 ? workspace.Directory : workspace.Root);
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                var discovery = Path.Combine(workspace.Directory, "runtime", "instance.json");
                var connected = false;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (child.HasExited) throw new InvalidOperationException(await errors);
                    if (File.Exists(discovery))
                    {
                        try
                        {
                            var published = System.Text.Json.JsonSerializer.Deserialize<MabiTool.Contracts.LocalDiscoveryInfo>(
                                File.ReadAllText(discovery), MabiTool.Contracts.ContractJson.Options)!;
                            if (published.ProcessId == child.Id)
                            {
                                var response = await LocalBootstrapClient.ConnectAsync(discovery, "storage-test");
                                Assert.Equal(child.Id, System.Text.Json.JsonSerializer.Deserialize<MabiTool.Contracts.LocalDiscoveryInfo>(
                                    File.ReadAllText(discovery), MabiTool.Contracts.ContractJson.Options)!.ProcessId);
                                Assert.True(response.Grant.Session.Owner);
                                connected = true;
                                break;
                            }
                        }
                        catch (IOException) { }
                    }
                    await Task.Delay(100);
                }
                if (!connected) throw new TimeoutException("测试宿主未发布受保护发现信息");
                Assert.Equal("storage.writerBusy", Assert.Throws<StorageException>(() => DataRootLease.Open(workspace.Options)).Code);
                using var duplicate = workspace.StartHost(workspace.Directory);
                var duplicateOutput = duplicate.StandardOutput.ReadToEndAsync();
                var duplicateError = duplicate.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await duplicate.WaitForExitAsync(deadline.Token);
                Assert.Equal(3, duplicate.ExitCode);
                Assert.Contains("storage.writerBusy", await duplicateError);
                Assert.Empty(await duplicateOutput);
                Assert.Equal(original, File.ReadAllText(workspace.Configuration));
            }
            catch (TimeoutException error)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                throw new TimeoutException($"{error.Message}; stdout: {await output}; stderr: {await errors}", error);
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                await Task.WhenAll(output, errors);
            }
            using var released = DataRootLease.Open(workspace.Options);
            Assert.Equal(workspace.Root, released.Paths.DataRoot);
        }
    }

    [Fact]
    public async Task ActualHostMissingRootExitsWithoutRecreatingItOrPublishingDiscovery()
    {
        using var workspace = new Workspace();
        using (DataRootLease.Open(workspace.Options with { Initialize = true })) { }
        var original = File.ReadAllText(workspace.Configuration);
        var moved = Path.Combine(workspace.Directory, "original-data");
        Directory.Move(workspace.Root, moved);
        using var child = workspace.StartHost(workspace.Directory);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await child.WaitForExitAsync(deadline.Token);
        Assert.Equal(3, child.ExitCode);
        Assert.Contains("storage.rootMissing", await errors);
        Assert.Empty(await output);
        Assert.False(Directory.Exists(workspace.Root));
        Assert.False(File.Exists(Path.Combine(workspace.Directory, "runtime", "instance.json")));
        Assert.Equal(original, File.ReadAllText(workspace.Configuration));
    }

    [Fact]
    public void StartupArgumentsRequireExplicitUnambiguousMode()
    {
        var portable = StorageOptions.FromArguments(["--portable-root", @"D:\portable", "--initialize-data"]);
        Assert.True(portable.Initialize);
        Assert.Equal(@"D:\portable\bootstrap", portable.BootstrapDirectory);
        Assert.Null(portable.DataRoot);
        Assert.Throws<ArgumentException>(() => StorageOptions.FromArguments(["--data-root"]));
        Assert.Throws<ArgumentException>(() => StorageOptions.FromArguments(["--initialize-data", "--initialize-data"]));
        Assert.Throws<ArgumentException>(() => StorageOptions.FromArguments(["--portable-root", @"D:\p", "--data-root", @"D:\d"]));
    }

    [Fact]
    public void DefaultLayoutKeepsHostAndDesktopFilesUnderTheProgramFolder()
    {
        var root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "tool-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            var package = Path.Combine(root, "package");
            Directory.CreateDirectory(Path.Combine(package, "components", "core"));
            Directory.CreateDirectory(Path.Combine(package, "components", "desktop"));
            File.WriteAllBytes(Path.Combine(package, "components", "core", "MabiTool.Host.dll"), []);
            var flat = Path.Combine(root, "flat");
            Directory.CreateDirectory(flat);

            // 目录包：运行文件归入包根下的 userdata，包根只留入口、组件与许可。
            var state = Path.Combine(package, ToolRoot.PackageStateName);
            Assert.Equal(state, ToolRoot.FromProgramDirectory(Path.Combine(package, "components", "core") + Path.DirectorySeparatorChar));
            Assert.Equal(state, ToolRoot.FromProgramDirectory(Path.Combine(package, "components", "desktop")));
            Assert.Equal(package, ToolRoot.PackageRoot(Path.Combine(package, "components", "desktop")));
            Assert.Null(ToolRoot.PackageRoot(flat));
            Assert.Equal(flat, ToolRoot.FromProgramDirectory(flat));

            // 不带参数：引导、数据、实例发现都在工具根；前端按同一规则得到同一个发现文件。
            var storage = StorageOptions.FromArguments([], Path.Combine(package, "components", "core"));
            Assert.True(storage.UsesToolRootDefaults);
            Assert.Equal(Path.Combine(state, "bootstrap"), storage.BootstrapDirectory);
            Assert.Equal(Path.Combine(state, "data"), StorageOptions.DefaultDataRoot(storage.BootstrapDirectory));
            var host = LocalApiOptions.FromEnvironment(ToolRoot.FromProgramDirectory(Path.Combine(package, "components", "core")));
            var desktop = ToolRoot.DiscoveryFile(ToolRoot.FromArguments([], Path.Combine(package, "components", "desktop")));
            Assert.Equal(Path.Combine(host.RuntimeDirectory!, "instance.json"), desktop);
            Assert.Equal(Path.Combine(state, "runtime", "instance.json"), desktop);
            Assert.Equal(Path.Combine(flat, "runtime", "instance.json"), ToolRoot.DiscoveryFile(ToolRoot.FromArguments([ToolRoot.Argument, flat], package)));
            Assert.False(StorageOptions.FromArguments([ToolRoot.Argument, flat]).UsesToolRootDefaults);

            // 旧布局（包根已有 bootstrap 等目录）继续以包根为工具根，已有数据照常打开。
            Directory.CreateDirectory(Path.Combine(package, "bootstrap"));
            Assert.Equal(package, ToolRoot.FromProgramDirectory(Path.Combine(package, "components", "core")));
            Assert.Equal(Path.Combine(package, "bootstrap"), StorageOptions.FromArguments([], Path.Combine(package, "components", "core")).BootstrapDirectory);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void UnwritableToolRootStopsStartupWithoutUsingAnotherFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "tool-root-" + Guid.NewGuid().ToString("N"));
        var locked = new DirectoryInfo(Path.Combine(root, "program"));
        locked.Create();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(user,
            System.Security.AccessControl.FileSystemRights.CreateDirectories | System.Security.AccessControl.FileSystemRights.CreateFiles,
            System.Security.AccessControl.AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var error = Assert.Throws<StorageException>(() =>
                DataRootLease.Open(StorageOptions.FromArguments(["--initialize-data"], locked.FullName)));
            Assert.Equal("storage.permissionDenied", error.Code);
            Assert.Contains("不可写", error.Message);
            Assert.Empty(locked.EnumerateFileSystemInfos());
            Assert.Equal(locked.FullName, Assert.Single(Directory.EnumerateFileSystemEntries(root)));
        }
        finally
        {
            security = locked.GetAccessControl();
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void FileGrantedToAnotherPrincipalInsideAPrivateDirectoryIsRepairedBeforeUse()
    {
        // 数据目录本身是当前用户独占，其中的锁文件被安全软件等加上了其他主体的访问规则：先改回当前用户独占再使用，不当作被占用。
        var root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "private-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            LocalDiscoverySecurity.EnsureDirectory(root);
            var file = new FileInfo(Path.Combine(root, "writer.lock"));
            File.WriteAllBytes(file.FullName, []);
            var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
            var security = file.GetAccessControl();
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users,
                System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
            file.SetAccessControl(security);
            Assert.False(LocalDiscoverySecurity.IsPrivate(root));
            LocalDiscoverySecurity.ValidateFile(file.FullName);
            Assert.True(LocalDiscoverySecurity.IsPrivate(root));

            // 目录本身不是独占时不修复单个文件，原因写明对象与多出的主体。
            var shared = new DirectoryInfo(Path.Combine(root, "shared"));
            shared.Create();
            var other = Path.Combine(shared.FullName, "bootstrap.lock");
            File.WriteAllBytes(other, []);
            var directorySecurity = shared.GetAccessControl();
            directorySecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users, System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.InheritanceFlags.ObjectInherit, System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            shared.SetAccessControl(directorySecurity);
            var fileSecurity = new FileInfo(other).GetAccessControl();
            fileSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users,
                System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(other).SetAccessControl(fileSecurity);
            var error = Assert.Throws<IOException>(() => LocalDiscoverySecurity.ValidateFile(other));
            Assert.Contains("bootstrap.lock", error.Message);
            Assert.Contains("还授予了", error.Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FirstToolRootStartImportsTheLegacyDataRootAndLeavesItUntouched()
    {
        var work = new StorageTestWorkspace();
        var open = true;
        try
        {
            await work.Assets.PutAsync("music-library", "saved", "text/plain", new MemoryStream("legacy"u8.ToArray()));
            var part = work.Paths.Resolve("temp", "legacy.part");
            await using (await work.Database.TemporaryFiles.TrackAsync("files", part)) await File.WriteAllTextAsync(part, "part");
            var legacyBootstrap = Path.Combine(work.Root, "bootstrap");
            var tool = Path.Combine(work.Root, "tool");
            var options = new StorageOptions(Path.Combine(tool, "bootstrap"));

            // 旧宿主仍持有数据根：拒绝迁入，工具根不出现数据目录。
            Assert.Equal("storage.writerBusy", Assert.Throws<StorageException>(() => DataRootLease.ImportLegacyLayout(options, legacyBootstrap)).Code);
            Assert.False(Directory.Exists(Path.Combine(tool, "data")));
            await work.Assets.DisposeAsync(); await work.Database.DisposeAsync(); work.Lease.Dispose(); open = false;

            var legacyBefore = Hashes(legacyBootstrap).Concat(Hashes(Path.Combine(work.Root, "data"))).ToArray();
            var result = DataRootLease.ImportLegacyLayout(options, legacyBootstrap);
            Assert.NotNull(result);
            Assert.Equal(Path.Combine(work.Root, "data"), result.SourceDataRoot);
            Assert.True(result.Files > 0);
            Assert.Equal(legacyBefore, Hashes(legacyBootstrap).Concat(Hashes(Path.Combine(work.Root, "data"))).ToArray());
            Assert.Equal(Hashes(Path.Combine(work.Root, "data", "assets")), Hashes(Path.Combine(tool, "data", "assets")));
            Assert.False(Directory.Exists(Path.Combine(tool, "data.import")));
            Assert.Null(DataRootLease.ImportLegacyLayout(options, legacyBootstrap));

            // 迁入后不带 --initialize-data 即可打开工具根数据，临时文件登记改到新数据根。
            using var lease = DataRootLease.Open(options);
            Assert.Equal(Path.Combine(tool, "data"), lease.Paths.DataRoot);
            await using var database = SqliteStore.Open(lease,
                [.. AssetStore.Migrations, TemporaryStorageRegistry.Migration, StorageControlStore.Migration, DataBackupStore.Migration]);
            await database.TemporaryFiles.RecoverAsync();
            Assert.Equal(lease.Paths.Resolve("temp", "legacy.part"), database.ReadScalar("SELECT path FROM platform_temporary_files"));
        }
        finally
        {
            if (open) { await work.Assets.DisposeAsync(); await work.Database.DisposeAsync(); work.Lease.Dispose(); }
            Directory.Delete(work.Root, true);
        }

        static IEnumerable<(string, string)> Hashes(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is not ("writer.lock" or "database.lock" or "bootstrap.lock"))
            .Select(path => (Path.GetRelativePath(directory, path), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))))
            .OrderBy(item => item.Item1, StringComparer.Ordinal);
    }

    [Fact]
    public async Task MovedRenamedOrCopiedToolRootOpensItsOwnData()
    {
        if (!OperatingSystem.IsWindows()) return;
        var work = new StorageTestWorkspace();
        var moved = work.Root + "-moved";
        var copied = work.Root + "-copied";
        var open = true;
        try
        {
            var part = work.Paths.Resolve("temp", "moved.part");
            await using (await work.Database.TemporaryFiles.TrackAsync("files", part)) await File.WriteAllTextAsync(part, "part");
            var rootId = work.Paths.RootId;
            await work.Assets.DisposeAsync(); await work.Database.DisposeAsync(); work.Lease.Dispose(); open = false;

            // 整体改名并移到别处：核对身份后改写引导配置、身份标记与临时文件登记，不带参数即可打开原有数据。
            Directory.Move(work.Root, moved);
            var options = StorageOptions.FromArguments([], moved);
            var adopted = DataRootLease.AdoptToolRootLayout(options);
            Assert.NotNull(adopted);
            Assert.Equal(Path.Combine(work.Root, "data"), adopted.PreviousDataRoot);
            Assert.False(adopted.AccessRepaired);
            await AssertOpens(options, Path.Combine(moved, "data"));
            Assert.Null(DataRootLease.AdoptToolRootLayout(options));

            // 复制到新位置：复制得到的目录继承新位置的权限，原有 ACL 检查拒绝打开；接管后改为当前用户独占并使用复制得到的 data。
            Copy(moved, copied);
            var copiedOptions = StorageOptions.FromArguments([], copied);
            Assert.Equal("storage.unavailable", Assert.Throws<StorageException>(() => DataRootLease.Open(copiedOptions)).Code);
            var copiedAdoption = DataRootLease.AdoptToolRootLayout(copiedOptions);
            Assert.NotNull(copiedAdoption);
            Assert.True(copiedAdoption.AccessRepaired);
            Assert.Equal(Path.Combine(moved, "data"), copiedAdoption.PreviousDataRoot);
            foreach (var name in new[] { "bootstrap", "data" }) Assert.True(LocalDiscoverySecurity.IsPrivate(Path.Combine(copied, name)));
            await AssertOpens(copiedOptions, Path.Combine(copied, "data"));
            await AssertOpens(options, Path.Combine(moved, "data"));

            async Task AssertOpens(StorageOptions start, string data)
            {
                using var lease = DataRootLease.Open(start);
                Assert.Equal(data, lease.Paths.DataRoot);
                Assert.Equal(rootId, lease.Paths.RootId);
                await using var database = SqliteStore.Open(lease,
                    [.. AssetStore.Migrations, TemporaryStorageRegistry.Migration, StorageControlStore.Migration, DataBackupStore.Migration]);
                Assert.Equal(lease.Paths.Resolve("temp", "moved.part"), database.ReadScalar("SELECT path FROM platform_temporary_files"));
            }
        }
        finally
        {
            if (open) { await work.Assets.DisposeAsync(); await work.Database.DisposeAsync(); work.Lease.Dispose(); }
            foreach (var directory in new[] { work.Root, moved, copied })
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        static void Copy(string source, string target)
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    [Fact]
    public async Task ForeignDataInMovedToolRootIsRejectedWithoutRewriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = new StorageTestWorkspace();
        var second = new StorageTestWorkspace();
        var moved = first.Root + "-moved";
        try
        {
            foreach (var work in new[] { first, second }) { await work.Assets.DisposeAsync(); await work.Database.DisposeAsync(); work.Lease.Dispose(); }
            // 搬走后原数据根已不存在，工具根下的 data 却换成了另一份数据：不接管，也不落到空库。
            Directory.Move(first.Root, moved);
            Directory.Delete(Path.Combine(moved, "data"), true);
            Directory.Move(Path.Combine(second.Root, "data"), Path.Combine(moved, "data"));
            var configuration = File.ReadAllText(Path.Combine(moved, "bootstrap", "data-root.json"));
            var marker = File.ReadAllText(Path.Combine(moved, "data", ".mabitool-data-root.json"));

            var options = StorageOptions.FromArguments([], moved);
            Assert.Equal("storage.identityMismatch", Assert.Throws<StorageException>(() => DataRootLease.AdoptToolRootLayout(options)).Code);
            Assert.Equal(configuration, File.ReadAllText(Path.Combine(moved, "bootstrap", "data-root.json")));
            Assert.Equal(marker, File.ReadAllText(Path.Combine(moved, "data", ".mabitool-data-root.json")));
        }
        finally
        {
            foreach (var directory in new[] { first.Root, moved, second.Root })
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ActualHostRejectsKnownCloudRootBeforeOpeningStorage()
    {
        using var workspace = new Workspace();
        using (DataRootLease.Open(workspace.Options with { Initialize = true })) { }
        var original = File.ReadAllText(workspace.Configuration);
        using var child = workspace.StartHost(workspace.Directory, workspace.Root);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await child.WaitForExitAsync(deadline.Token);
        Assert.Equal(3, child.ExitCode);
        Assert.Contains("storage.unsupportedVolume", await errors);
        Assert.Empty(await output);
        Assert.Equal(original, File.ReadAllText(workspace.Configuration));
        using var available = DataRootLease.Open(workspace.Options);
        Assert.Equal(workspace.Root, available.Paths.DataRoot);
    }

    private sealed class Workspace : IDisposable
    {
        public string Directory { get; }
        public string Root => Path.Combine(Directory, "data");
        public string Configuration => Path.Combine(Directory, "bootstrap", "data-root.json");
        public StorageOptions Options => new(Path.Combine(Directory, "bootstrap"), DataRoot: Root);
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            Directory = Path.GetFullPath(Path.Combine(repository!.FullName, "artifacts/tmp/storage-tests", Guid.NewGuid().ToString("N")));
            System.IO.Directory.CreateDirectory(Directory);
        }

        public Process StartHost(string workingDirectory, string? cloudRoot = null)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(BackendApplication).Assembly.Location);
            start.Environment["MABITOOL_API_PORT"] = "0";
            start.Environment["MABITOOL_ALLOWED_ORIGINS"] = "";
            start.Environment.Remove("MABITOOL_API_TOKEN");
            start.Environment["MABITOOL_BOOTSTRAP_DIRECTORY"] = Options.BootstrapDirectory;
            start.Environment["MABITOOL_RUNTIME_DIRECTORY"] = Path.Combine(Directory, "runtime");
            if (cloudRoot is not null) start.Environment["OneDrive"] = cloudRoot;
            return Process.Start(start)!;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
