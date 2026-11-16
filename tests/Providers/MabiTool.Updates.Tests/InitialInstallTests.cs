using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MabiTool.Updates.Tests;

[SupportedOSPlatform("windows")]
public sealed class InitialInstallTests
{
    [Fact]
    public async Task ToolRootLayoutKeepsBootstrapAndDataInsideTheInstallation()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        var installation = fixture.Request.InstallationRoot;
        await fixture.SaveRequestAsync(fixture.Request with
            { BootstrapDirectory = Path.Combine(installation, "bootstrap"), DataRoot = Path.Combine(installation, "data") });
        var record = await fixture.Engine().RunAsync(fixture.RequestPath);
        Assert.Equal("complete", record.Phase);
        var top = Directory.EnumerateFileSystemEntries(installation).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(new HashSet<string?>(["bootstrap", "data", "launcher", "staging", "update-state", "versions"], StringComparer.Ordinal), top);
        Assert.Contains("bootstrap", top); Assert.Contains("data", top);
        using var lease = DataRootLease.Open(record.Request.Storage);
        Assert.Equal(Path.Combine(installation, "data"), lease.Paths.DataRoot);
        Assert.Equal(record.DatabaseId, lease.DatabaseId);
    }

    [Fact]
    public async Task RequestInsideTheInstallationIsAcceptedAndResumes()
    {
        await using (var fixture = await InitialFixture.CreateAsync())
        {
            var state = Path.Combine(fixture.Request.InstallationRoot, "update-state");
            Directory.CreateDirectory(state);
            var inside = Path.Combine(state, "initial-install-request.json");
            await File.WriteAllTextAsync(inside, JsonSerializer.Serialize(fixture.Request, UpdateHandoffProtocol.Json));
            await File.WriteAllTextAsync(inside + ".tmp-" + Guid.NewGuid().ToString("N"), "partial");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Engine(point =>
                point == InitialInstallPersistencePoint.FilesPublished ? throw new OperationCanceledException() : Task.CompletedTask).RunAsync(inside));
            var record = await fixture.Engine().RunAsync(inside);
            Assert.Equal("complete", record.Phase);
            Assert.True(File.Exists(inside));
            Assert.Equal(["installation"], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(fixture.Request.InstallationRoot)!)
                .Select(Path.GetFileName).Where(name => name!.StartsWith("installation", StringComparison.Ordinal)));
        }

        // 更新状态目录里不是本次执行的请求文件仍按未知内容阻止安装。
        await using var other = await InitialFixture.CreateAsync();
        var foreign = Path.Combine(other.Request.InstallationRoot, "update-state", "initial-install-request.json");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        await File.WriteAllTextAsync(foreign, "{}");
        Assert.Equal("update.initialInstallNotEmpty", (await Assert.ThrowsAsync<UpdatePlanException>(() => other.Engine().RunAsync(other.RequestPath))).Code);
        Assert.Equal("{}", await File.ReadAllTextAsync(foreign));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaseAndExplicitOptionalInstallPublishVerifiedLayoutAndFixedData(bool optional)
    {
        await using var fixture = await InitialFixture.CreateAsync(optional);
        var record = await fixture.Engine().RunAsync(fixture.RequestPath);
        Assert.Equal("complete", record.Phase);
        Assert.Equal(record.RootId, record.Health!.Database.RootId);
        Assert.Equal(record.DatabaseId, record.Health.Database.DatabaseId);
        Assert.True(InitialInstallFiles.Same(record.Version, (await InstallTransaction.ReadInstalledAsync(record.Request.InstallationRoot)).Version));
        await FileTreePlanner.VerifyAsync(record.VersionRoot, record.Version!.Files);
        await FileTreePlanner.VerifyAsync(record.StableRoot, record.StableFiles!);
        Assert.Equal(optional, Directory.Exists(Path.Combine(record.VersionRoot, "components", "color-engine")));
        Assert.False(Directory.Exists(Path.Combine(record.VersionRoot, "components", "observation-worker")));
        Assert.Null(UpdateInstallGate.Read(record.Request.DataRoot));
        using var lease = DataRootLease.Open(record.Request.Storage);
        Assert.Equal(record.DatabaseId, lease.DatabaseId);
    }

    [Theory]
    [InlineData("captured")]
    [InlineData("prepared")]
    [InlineData("file")]
    [InlineData("version")]
    [InlineData("stable")]
    [InlineData("files")]
    [InlineData("data")]
    [InlineData("installed")]
    [InlineData("pointer")]
    [InlineData("configuration")]
    [InlineData("gate")]
    public async Task CancellationAtPersistenceBoundaryResumesTheSameInput(string boundary)
    {
        await using var fixture = await InitialFixture.CreateAsync();
        var point = boundary switch
        {
            "captured" => InitialInstallPersistencePoint.Captured, "prepared" => InitialInstallPersistencePoint.Prepared,
            "file" => InitialInstallPersistencePoint.FileCopied, "version" => InitialInstallPersistencePoint.VersionPublished,
            "stable" => InitialInstallPersistencePoint.StablePublished, "files" => InitialInstallPersistencePoint.FilesPublished,
            "data" => InitialInstallPersistencePoint.DataInitialized, "installed" => InitialInstallPersistencePoint.InstalledRecordPublished,
            "pointer" => InitialInstallPersistencePoint.PointerPublished, "gate" => InitialInstallPersistencePoint.MaintenanceGateRemoved,
            _ => InitialInstallPersistencePoint.ConfigurationPublished
        };
        var interrupted = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Engine(observed: current =>
        {
            if (current == point && Interlocked.Exchange(ref interrupted, 1) == 0) throw new OperationCanceledException();
            return Task.CompletedTask;
        }).RunAsync(fixture.RequestPath));
        var before = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        var completed = await fixture.Engine().RunAsync(fixture.RequestPath);
        Assert.Equal("complete", completed.Phase);
        Assert.Equal(before.InputSha256, completed.InputSha256);
        Assert.Equal(before.DatabaseId, completed.DatabaseId);
        Assert.Equal(before.RootId, completed.RootId);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("manifest")]
    [InlineData("file")]
    [InlineData("optional")]
    public async Task UntrustedOrIncompatibleInputCannotInitializeData(string change)
    {
        await using var fixture = await InitialFixture.CreateAsync();
        if (change == "index") fixture.Clock.UtcNow = fixture.Signing.Now.AddHours(2);
        if (change == "manifest") await File.AppendAllTextAsync(fixture.Request.ReleaseManifestPath, " ");
        if (change == "file") await File.AppendAllTextAsync(Path.Combine(fixture.Request.PackageRoot, "components", "core", "MabiTool.Host.dll"), "changed");
        if (change == "optional") await fixture.SaveRequestAsync(fixture.Request with { OptionalComponents = ["missing-engine"] });
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Engine().RunAsync(fixture.RequestPath));
        Assert.False(Directory.Exists(fixture.Request.DataRoot));
        Assert.False(File.Exists(UpdateInstalledConfiguration.PathFor(fixture.Request.InstallationRoot)));
        Assert.False(File.Exists(Path.Combine(fixture.Request.InstallationRoot, "launcher", "active-version.json")));
    }

    [Theory]
    [InlineData("installation")]
    [InlineData("bootstrap")]
    [InlineData("data")]
    public async Task ExistingUnknownContentIsPreserved(string where)
    {
        await using var fixture = await InitialFixture.CreateAsync();
        var root = where == "installation" ? fixture.Request.InstallationRoot : where == "bootstrap" ? fixture.Request.BootstrapDirectory : fixture.Request.DataRoot;
        Directory.CreateDirectory(root); var sentinel = Path.Combine(root, "unknown.txt"); await File.WriteAllTextAsync(sentinel, "preserve");
        Assert.Equal("update.initialInstallNotEmpty", (await Assert.ThrowsAsync<UpdatePlanException>(() => fixture.Engine().RunAsync(fixture.RequestPath))).Code);
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task ChangedFixedInputAndOccupiedInstallCannotResume()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.Prepared);
        using (var ownership = new InstallTransaction(fixture.Request.InstallationRoot))
            Assert.Equal("resource.busy", (await Assert.ThrowsAsync<UpdatePlanException>(() => fixture.Engine().RunAsync(fixture.RequestPath))).Code);
        await fixture.SaveRequestAsync(fixture.Request with { OptionalComponents = ["color-engine"] });
        Assert.Equal("update.initialInstallInputMismatch", (await Assert.ThrowsAsync<UpdatePlanException>(() => fixture.Engine().RunAsync(fixture.RequestPath))).Code);
    }

    [Theory]
    [InlineData("pointer")]
    [InlineData("configuration")]
    [InlineData("data")]
    [InlineData("identity")]
    [InlineData("journal")]
    public async Task ChangedPublishedStateIsPreservedAndCannotComplete(string change)
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.ConfigurationPublished);
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        string path;
        if (change == "pointer") path = Path.Combine(record.Request.InstallationRoot, "launcher", "active-version.json");
        else if (change == "configuration") path = UpdateInstalledConfiguration.PathFor(record.Request.InstallationRoot);
        else if (change == "data") path = Path.Combine(record.Request.DataRoot, "assets", "unknown.txt");
        else if (change == "identity") path = Path.Combine(record.Request.DataRoot, ".mabitool-data-root.json");
        else path = record.Request.JournalPath;
        var replacement = change is "data" or "pointer" or "configuration" ? "changed" :
            (await File.ReadAllTextAsync(path)).Replace(record.InputSha256, new string('a', 64), StringComparison.Ordinal)
                .Replace(record.RootId, new string('b', 32), StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, replacement);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Engine().RunAsync(fixture.RequestPath));
        Assert.Equal(replacement, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CommittedDatabaseWithoutSealedMarkerResumesItsFixedIdentity()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.FilesPublished);
        var authorization = await InitialInstallAuthorization.ReadAsync(fixture.Request.InstallationRoot,
            (await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot)).InputSha256, fixture.Clock);
        using (var lease = DataRootLease.OpenForInitialInstall(authorization))
        { await using var database = SqliteStore.Open(lease); Assert.Equal(authorization.Record.DatabaseId, lease.DatabaseId); }
        var markerPath = Path.Combine(fixture.Request.DataRoot, ".mabitool-data-root.json");
        var marker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!; marker["databaseId"] = null;
        await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
        var completed = await fixture.Engine().RunAsync(fixture.RequestPath);
        Assert.Equal(authorization.Record.DatabaseId, completed.Health!.Database.DatabaseId);
        Assert.Equal(authorization.Record.RootId, completed.Health.Database.RootId);
    }

    [Fact]
    public async Task DatabaseIdentityChangedBeforeMarkerSealIsRejected()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.FilesPublished);
        var authorization = await InitialInstallAuthorization.ReadAsync(fixture.Request.InstallationRoot,
            (await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot)).InputSha256, fixture.Clock);
        using (var lease = DataRootLease.OpenForInitialInstall(authorization))
        {
            await using var database = SqliteStore.Open(lease);
            await database.WriteAsync(context => { context.Execute("UPDATE core_database_identity SET database_id=$id", ("$id", new string('f', 32))); return true; });
        }
        var markerPath = Path.Combine(fixture.Request.DataRoot, ".mabitool-data-root.json");
        var marker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!; marker["databaseId"] = null;
        await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
        Assert.Equal("storage.databaseIdentityMismatch", (await Assert.ThrowsAsync<StorageException>(() => fixture.Engine().RunAsync(fixture.RequestPath))).Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnsealedEmptyDatabaseResumesOnlyWithoutBusinessSchema(bool businessSchema, bool sealedMarker)
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.FilesPublished);
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        var authorization = await InitialInstallAuthorization.ReadAsync(fixture.Request.InstallationRoot, record.InputSha256, fixture.Clock);
        string databasePath;
        using (var lease = DataRootLease.OpenForInitialInstall(authorization)) databasePath = lease.Paths.Database;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using (var transaction = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "CREATE TABLE interrupted_initial_write(value TEXT)"; command.ExecuteNonQuery(); transaction.Rollback();
            }
            using var vacuum = connection.CreateCommand(); vacuum.CommandText = "VACUUM"; vacuum.ExecuteNonQuery();
            if (businessSchema)
            {
                vacuum.CommandText = "CREATE TABLE user_content(value TEXT); INSERT INTO user_content VALUES('preserve')";
                vacuum.ExecuteNonQuery();
            }
        }
        Assert.True(new FileInfo(databasePath).Length > 0);
        if (sealedMarker)
        {
            var markerPath = Path.Combine(fixture.Request.DataRoot, ".mabitool-data-root.json");
            var marker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!; marker["databaseId"] = record.DatabaseId;
            await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
        }
        if (businessSchema || sealedMarker)
        {
            Assert.Equal("storage.databaseInvalid", (await Assert.ThrowsAsync<StorageException>(() => fixture.Engine().RunAsync(fixture.RequestPath))).Code);
            if (businessSchema)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM user_content";
                Assert.Equal("preserve", command.ExecuteScalar());
            }
        }
        else
        {
            var completed = await fixture.Engine().RunAsync(fixture.RequestPath);
            Assert.Equal("complete", completed.Phase); Assert.Equal(record.DatabaseId, completed.Health!.Database.DatabaseId);
        }
    }

    [Fact]
    public async Task SealedInitialLeaseCannotReinitializeAnEmptyReplacement()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.FilesPublished);
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        var authorization = await InitialInstallAuthorization.ReadAsync(fixture.Request.InstallationRoot, record.InputSha256, fixture.Clock);
        using var lease = DataRootLease.OpenForInitialInstall(authorization);
        await using (var database = SqliteStore.Open(lease)) { }
        var empty = fixture.Signing.Path("empty-after-seal.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = empty, Pooling = false }.ToString()))
        {
            connection.Open();
            using var vacuum = connection.CreateCommand(); vacuum.CommandText = "VACUUM"; vacuum.ExecuteNonQuery();
        }
        var replacement = await File.ReadAllBytesAsync(empty);
        Assert.NotEmpty(replacement); File.Copy(empty, lease.Paths.Database, true);
        var failure = await Assert.ThrowsAsync<StorageException>(async () => { await using var database = SqliteStore.Open(lease); });
        Assert.Equal("storage.databaseInvalid", failure.Code);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(lease.Paths.Database));
    }

    [Theory]
    [InlineData("journal", false)]
    [InlineData("trust", false)]
    [InlineData("installed", false)]
    [InlineData("pointer", false)]
    [InlineData("configuration", false)]
    [InlineData("bootstrap", false)]
    [InlineData("reference", false)]
    [InlineData("marker", false)]
    [InlineData("gate", false)]
    [InlineData("journal", true)]
    [InlineData("trust", true)]
    [InlineData("installed", true)]
    [InlineData("pointer", true)]
    [InlineData("configuration", true)]
    [InlineData("bootstrap", true)]
    [InlineData("reference", true)]
    [InlineData("marker", true)]
    [InlineData("gate", true)]
    public async Task InterruptedMetadataResumesFixedInputAndRejectsChangedTemporaryContent(string target, bool changed)
    {
        await using var fixture = await InitialFixture.CreateAsync();
        var boundary = target is "journal" or "trust" ? InitialInstallPersistencePoint.Prepared :
            target is "installed" or "pointer" or "configuration" ? InitialInstallPersistencePoint.ConfigurationPublished : InitialInstallPersistencePoint.FilesPublished;
        await fixture.InterruptAsync(boundary);
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        if (boundary == InitialInstallPersistencePoint.FilesPublished)
        {
            var authorization = await InitialInstallAuthorization.ReadAsync(fixture.Request.InstallationRoot, record.InputSha256, fixture.Clock);
            using var lease = DataRootLease.OpenForInitialInstall(authorization);
        }
        var source = target switch
        {
            "journal" => fixture.Request.JournalPath, "trust" => Path.Combine(fixture.Request.StateRoot, "trust.json"),
            "installed" => Path.Combine(fixture.Request.StateRoot, "installed-version.json"),
            "pointer" => Path.Combine(fixture.Request.InstallationRoot, "launcher", "active-version.json"),
            "configuration" => UpdateInstalledConfiguration.PathFor(fixture.Request.InstallationRoot),
            "bootstrap" => Path.Combine(fixture.Request.BootstrapDirectory, "data-root.json"),
            "reference" => Path.Combine(fixture.Request.BootstrapDirectory, InitialInstallReference.FileName),
            "marker" => Path.Combine(fixture.Request.DataRoot, ".mabitool-data-root.json"),
            _ => Path.Combine(fixture.Request.DataRoot, UpdateInstallGate.RelativePath)
        };
        var temporary = target is "bootstrap" or "reference" or "marker" ? Path.Combine(Path.GetDirectoryName(source)!, Guid.NewGuid().ToString("N") + ".tmp") :
            source + ".tmp-" + Guid.NewGuid().ToString("N");
        var original = await File.ReadAllBytesAsync(source);
        var content = changed ? System.Text.Encoding.UTF8.GetBytes("changed-temporary-content") : original[..Math.Max(1, original.Length / 2)];
        await File.WriteAllBytesAsync(temporary, content);
        if (changed)
            Assert.Equal("update.initialInstallStateInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => fixture.Engine().RunAsync(fixture.RequestPath))).Code);
        else
        {
            var completed = await fixture.Engine().RunAsync(fixture.RequestPath);
            Assert.Equal("complete", completed.Phase); Assert.Equal(record.InputSha256, completed.InputSha256);
            Assert.Equal(record.RootId, completed.RootId); Assert.Equal(record.DatabaseId, completed.DatabaseId);
        }
        Assert.Equal(content, await File.ReadAllBytesAsync(temporary));
    }

    [Theory]
    [InlineData("partial-unclaimed")]
    [InlineData("empty-claimed")]
    [InlineData("changed-temporary")]
    [InlineData("changed-input")]
    public async Task FirstJournalPublicationResumesOnlyTheFixedInput(string scenario)
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.Captured);
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        var original = await File.ReadAllBytesAsync(fixture.Request.JournalPath);
        File.Move(fixture.Request.JournalPath, fixture.Signing.Path("first-journal-retained.json"));
        var claim = Path.Combine(fixture.Request.StateRoot, "initial-install-input-" + record.InputSha256 + ".claim");
        if (scenario == "partial-unclaimed" && File.Exists(claim)) File.Move(claim, fixture.Signing.Path("first-claim-retained"));
        var temporary = fixture.Request.JournalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        byte[] content = scenario == "empty-claimed" ? [] : scenario == "changed-temporary" ? System.Text.Encoding.UTF8.GetBytes("changed-first-journal") :
            original[..Math.Max(1, original.Length / 2)];
        await File.WriteAllBytesAsync(temporary, content);
        if (scenario == "changed-input") await fixture.SaveRequestAsync(fixture.Request with { OptionalComponents = ["color-engine"] });
        if (scenario is "changed-input" or "changed-temporary")
        {
            var failure = await Assert.ThrowsAsync<UpdatePlanException>(() => fixture.Engine().RunAsync(fixture.RequestPath));
            Assert.Equal(scenario == "changed-input" ? "update.initialInstallInputMismatch" : "update.initialInstallStateInvalid", failure.Code);
            Assert.False(File.Exists(fixture.Request.JournalPath)); Assert.False(Directory.Exists(fixture.Request.DataRoot));
        }
        else
        {
            var completed = await fixture.Engine().RunAsync(fixture.RequestPath);
            Assert.Equal("complete", completed.Phase); Assert.Equal(record.InputSha256, completed.InputSha256);
            Assert.Equal(record.RootId, completed.RootId); Assert.Equal(record.DatabaseId, completed.DatabaseId);
        }
        Assert.Equal(content, await File.ReadAllBytesAsync(temporary));
    }

    [Theory]
    [InlineData("reference", false)]
    [InlineData("marker", false)]
    [InlineData("reference", true)]
    [InlineData("marker", true)]
    public async Task FirstDataMetadataPublicationRequiresTheFixedPartialDocument(string target, bool changed)
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.FilesPublished);
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        var authorization = await InitialInstallAuthorization.ReadAsync(fixture.Request.InstallationRoot, record.InputSha256, fixture.Clock);
        using (var lease = DataRootLease.OpenForInitialInstall(authorization)) { }
        var reference = await File.ReadAllBytesAsync(Path.Combine(fixture.Request.BootstrapDirectory, InitialInstallReference.FileName));
        var marker = await File.ReadAllBytesAsync(Path.Combine(fixture.Request.DataRoot, ".mabitool-data-root.json"));
        var scope = Path.GetFullPath(fixture.Signing.Path(""));
        foreach (var directory in new[] { fixture.Request.BootstrapDirectory, fixture.Request.DataRoot })
        {
            var source = Path.GetFullPath(directory); var destination = fixture.Signing.Path("before-first-publication/" + Path.GetFileName(source));
            if (!StoragePaths.IsWithin(source, scope) || !StoragePaths.IsWithin(destination, scope) || source == scope || destination == scope)
                throw new InvalidOperationException();
            UpdatePaths.RejectLinks(source); UpdatePaths.RejectLinks(destination); _ = UpdatePaths.EnumerateFiles(source, default);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); Directory.Move(source, destination);
        }
        LocalDiscoverySecurity.EnsureDirectory(fixture.Request.BootstrapDirectory);
        if (target == "marker")
        {
            await File.WriteAllBytesAsync(Path.Combine(fixture.Request.BootstrapDirectory, InitialInstallReference.FileName), reference);
            LocalDiscoverySecurity.EnsureDirectory(fixture.Request.DataRoot);
        }
        var temporary = Path.Combine(target == "reference" ? fixture.Request.BootstrapDirectory : fixture.Request.DataRoot, Guid.NewGuid().ToString("N") + ".tmp");
        var document = target == "reference" ? reference : marker;
        var content = changed ? System.Text.Encoding.UTF8.GetBytes("changed-first-document") : document[..Math.Max(1, document.Length / 2)];
        await File.WriteAllBytesAsync(temporary, content);
        if (changed)
            Assert.Equal("update.initialInstallStateInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => fixture.Engine().RunAsync(fixture.RequestPath))).Code);
        else
        {
            var completed = await fixture.Engine().RunAsync(fixture.RequestPath);
            Assert.Equal("complete", completed.Phase); Assert.Equal(record.RootId, completed.RootId); Assert.Equal(record.DatabaseId, completed.DatabaseId);
        }
        Assert.Equal(content, await File.ReadAllBytesAsync(temporary));
    }

    [Fact]
    public async Task RemovedMaintenanceGateAndInterruptedRepublishResumeBeforeCompletion()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        await fixture.InterruptAsync(InitialInstallPersistencePoint.MaintenanceGateRemoved);
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        Assert.Equal("configured", record.Phase); Assert.Null(UpdateInstallGate.Read(fixture.Request.DataRoot));
        var gate = new UpdateInstallGate(SchemaVersion: 1, TransactionId: record.InstallationId, IntentSha256: record.InputSha256,
            InstallationRoot: record.Request.InstallationRoot, RootId: record.RootId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(gate, UpdateHandoffProtocol.Json);
        var temporary = Path.Combine(fixture.Request.DataRoot, UpdateInstallGate.RelativePath) + ".tmp-" + Guid.NewGuid().ToString("N");
        var content = bytes[..(bytes.Length / 2)]; await File.WriteAllBytesAsync(temporary, content);
        var completed = await fixture.Engine().RunAsync(fixture.RequestPath);
        Assert.Equal("complete", completed.Phase); Assert.Equal(record.RootId, completed.RootId); Assert.Equal(record.DatabaseId, completed.DatabaseId);
        Assert.Null(UpdateInstallGate.Read(fixture.Request.DataRoot)); Assert.Equal(content, await File.ReadAllBytesAsync(temporary));
    }

    [Fact]
    public async Task OrdinaryDatabaseCreationKeepsIndependentRandomIdentities()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        using var first = DataRootLease.Open(new(fixture.Signing.Path("ordinary/bootstrap-first"), true, fixture.Signing.Path("ordinary/data-first")));
        using var second = DataRootLease.Open(new(fixture.Signing.Path("ordinary/bootstrap-second"), true, fixture.Signing.Path("ordinary/data-second")));
        await using (var database = SqliteStore.Open(first)) { }
        await using (var database = SqliteStore.Open(second)) { }
        Assert.NotNull(first.DatabaseId); Assert.NotNull(second.DatabaseId);
        Assert.NotEqual(first.Paths.RootId, second.Paths.RootId); Assert.NotEqual(first.DatabaseId, second.DatabaseId);
        await using var reopened = SqliteStore.Open(first);
        Assert.Equal(first.DatabaseId, reopened.ReadScalar("SELECT database_id FROM core_database_identity WHERE singleton=1"));
    }

    [Fact]
    public async Task OrdinarySealedDatabaseRejectsMissingEmptyAndForeignContent()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        using var first = DataRootLease.Open(new(fixture.Signing.Path("ordinary/bootstrap-first"), true, fixture.Signing.Path("ordinary/data-first")));
        using var second = DataRootLease.Open(new(fixture.Signing.Path("ordinary/bootstrap-second"), true, fixture.Signing.Path("ordinary/data-second")));
        await using (var database = SqliteStore.Open(first)) { }
        await using (var database = SqliteStore.Open(second)) { }
        var original = await File.ReadAllBytesAsync(first.Paths.Database);
        File.Move(first.Paths.Database, fixture.Signing.Path("ordinary/retained-database.sqlite"));
        Assert.Equal("storage.databaseMissing", Assert.Throws<StorageException>(() => SqliteStore.Open(first)).Code);
        Assert.False(File.Exists(first.Paths.Database));
        await File.WriteAllBytesAsync(first.Paths.Database, []);
        Assert.Equal("storage.databaseIdentityMismatch", Assert.Throws<StorageException>(() => SqliteStore.Open(first)).Code);
        File.Copy(second.Paths.Database, first.Paths.Database, true);
        Assert.Equal("storage.databaseIdentityMismatch", Assert.Throws<StorageException>(() => SqliteStore.Open(first)).Code);
        await File.WriteAllBytesAsync(first.Paths.Database, original);
        await using var reopened = SqliteStore.Open(first);
        Assert.Equal("ok", reopened.ReadScalar("PRAGMA quick_check"));
        Assert.Equal(first.DatabaseId, reopened.ReadScalar("SELECT database_id FROM core_database_identity WHERE singleton=1"));
    }

    [Fact]
    public async Task OrdinaryMigrationsKeepChecksumValidationAndTransactionalRollback()
    {
        await using var fixture = await InitialFixture.CreateAsync();
        using var lease = DataRootLease.Open(new(fixture.Signing.Path("ordinary/bootstrap"), true, fixture.Signing.Path("ordinary/data")));
        var first = new DatabaseMigration("ordinary-regression", 1, "CREATE TABLE ordinary_values(id TEXT PRIMARY KEY,value INTEGER) STRICT;");
        var failing = new DatabaseMigration("ordinary-regression", 2, "INSERT INTO ordinary_values VALUES('rollback',2); INSERT INTO absent_table VALUES(1);");
        await using (var database = SqliteStore.Open(lease, [first]))
            await database.WriteAsync(tx => tx.Execute("INSERT INTO ordinary_values VALUES('preserve',1)"));
        Assert.Equal("storage.databaseInvalid", Assert.Throws<StorageException>(() => SqliteStore.Open(lease, [first, failing])).Code);
        await using (var reopened = SqliteStore.Open(lease, [first]))
        {
            Assert.Equal(1L, reopened.ReadScalar("SELECT count(*) FROM ordinary_values"));
            Assert.Equal(1L, reopened.ReadScalar("SELECT value FROM ordinary_values WHERE id='preserve'"));
            Assert.Equal(1L, reopened.ReadScalar("SELECT max(version) FROM core_migrations WHERE module_id='ordinary-regression'"));
        }
        Assert.Equal("storage.migrationIncompatible", Assert.Throws<StorageException>(() => SqliteStore.Open(lease, [first with { Sql = first.Sql + " -- changed" }])).Code);
    }

    [SupportedOSPlatform("windows")]
    private sealed class InitialFixture : IAsyncDisposable
    {
        internal SignedReleaseFixture Signing { get; } = new();
        internal InitialClock Clock { get; } = new();
        internal InitialInstallRequest Request { get; private set; } = null!;
        internal string RequestPath => Signing.Path("initial-request.json");
        internal InitialInstallEngine Engine(Func<InitialInstallPersistencePoint, Task>? observed = null) => new(new(1, 0, 0),
            Path.Combine(Request.PackageRoot, "components", "updater", "MabiTool.Updater.dll"), new InitialHealth(), Clock, observed);

        internal static async Task<InitialFixture> CreateAsync(bool optional = false)
        {
            var fixture = new InitialFixture(); fixture.Clock.UtcNow = fixture.Signing.Now;
            var package = fixture.Signing.Path("image"); var payload = fixture.Signing.Payload(); var template = payload["platforms"]![0]!["components"]![0]!;
            var components = new JsonArray();
            foreach (var id in InitialInstallValidation.BaseComponents.Concat(["color-engine", "observation-worker"]))
            {
                var directory = Path.Combine(package, "components", id); Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, id switch { "core" => "MabiTool.Host.dll", "updater" => "MabiTool.Updater.dll",
                    "launcher" => "MabiTool.Launcher.dll", "dotnet-runtime" => "dotnet.exe", _ => "component.txt" }), id);
                if (id == "core") await File.WriteAllTextAsync(Path.Combine(package, InstallTargetDescriptor.RelativePath), JsonSerializer.Serialize(
                    new InstallTargetDescriptor(1, "mabitool", "1.0.0", "1.0", 4, "framework-dependent", InstallTargetDescriptor.Entry("framework-dependent"),
                        SqliteStore.MigrationTargets([]), [new("core", "0.1.0")]), UpdateHandoffProtocol.Json));
                var files = (await FileTreePlanner.ReadCurrentAsync(directory, new(), default)).Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
                var component = template.DeepClone(); component["componentId"] = id; component["optional"] = !InitialInstallValidation.BaseComponents.Contains(id);
                component["installedSize"] = files.Sum(file => file.SizeBytes); component["targetTreeHash"] = FileTreePlanner.TreeHash(files);
                component["files"] = JsonSerializer.SerializeToNode(files.Select(file => new { path = file.RelativePath, size = file.SizeBytes, sha256 = file.Sha256,
                    componentId = id, executable = file.RelativePath.EndsWith(".dll", StringComparison.Ordinal) || file.RelativePath.EndsWith(".exe", StringComparison.Ordinal), encoding = "identity",
                    asset = new { assetName = file.Sha256 + ".blob", length = file.SizeBytes, sha256 = file.Sha256 } }), SignedReleaseFixture.Options);
                components.Add(component);
            }
            payload["platforms"]![0]!["components"] = components; payload["platforms"]![0]!["runtimeMode"] = "framework-dependent";
            payload["minUpdaterVersion"] = "1.0.0"; payload["data"] = new JsonArray();
            var manifest = fixture.Signing.Release(payload);
            await File.WriteAllBytesAsync(fixture.Signing.Path("release-manifest.json"), manifest);
            await File.WriteAllTextAsync(fixture.Signing.Path("root.json"), JsonSerializer.Serialize(fixture.Signing.Root, SignedReleaseFixture.Options));
            await File.WriteAllTextAsync(fixture.Signing.Path("index.json"), JsonSerializer.Serialize(fixture.Signing.Index(manifest), SignedReleaseFixture.Options));
            await fixture.SaveRequestAsync(new(1, package, fixture.Signing.Path("release-manifest.json"), fixture.Signing.Path("index.json"), fixture.Signing.Path("root.json"),
                fixture.Signing.Path("installation"), fixture.Signing.Path("bootstrap"), fixture.Signing.Path("data"), "initial/tests", "stable", optional ? ["color-engine"] : []));
            return fixture;
        }

        internal async Task SaveRequestAsync(InitialInstallRequest request)
        { Request = request; await File.WriteAllTextAsync(RequestPath, JsonSerializer.Serialize(request, UpdateHandoffProtocol.Json)); }
        internal Task InterruptAsync(InitialInstallPersistencePoint point) => Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(current =>
        { if (current == point) throw new OperationCanceledException(); return Task.CompletedTask; }).RunAsync(RequestPath));
        public ValueTask DisposeAsync()
        {
            Signing.RootSigner.Dispose(); Signing.IndexSigner.Dispose(); Signing.ReleaseSigner.Dispose();
            var source = Path.GetFullPath(Signing.Path("")); var directory = new DirectoryInfo(source);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            var repository = directory?.FullName ?? throw new InvalidOperationException();
            var pending = Path.Combine(repository, "artifacts", "pending-cleanup", "initial-install-tests-" + Guid.NewGuid().ToString("N"));
            if (!StoragePaths.IsWithin(source, Path.Combine(repository, "artifacts", "tmp", "update-tests"))) throw new InvalidOperationException();
            UpdatePaths.RejectLinks(source); UpdatePaths.RejectLinks(pending); _ = UpdatePaths.EnumerateFiles(source, default);
            Directory.CreateDirectory(Path.GetDirectoryName(pending)!); Directory.Move(source, pending);
            File.WriteAllText(Path.Combine(pending, "cleanup-manifest.json"), JsonSerializer.Serialize(new { originalPath = source, movedAtUtc = DateTimeOffset.UtcNow }, SignedReleaseFixture.Options));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InitialClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [SupportedOSPlatform("windows")]
    private sealed class InitialHealth : IInitialInstallHealthExchange
    {
        public async Task<InstallHealthEvidence> VerifyAsync(InitialInstallAuthorization authorization, CancellationToken token)
        {
            var record = authorization.Record;
            using var lease = DataRootLease.OpenForInitialInstall(authorization);
            await using var database = SqliteStore.Open(lease);
            using var maintenance = database.Activities.TryEnterMaintenance(record.InstallationId)!;
            var result = await database.VerifyDatabaseHealthAsync(maintenance, record.DatabaseId, "live", record.Target!.Migrations, token);
            return new(record.InstallationId, new string('c', 64), Guid.NewGuid().ToString("N"), record.Target.HostVersion,
                record.Target.HostApiVersion, result, record.Target.Modules);
        }
    }
}
