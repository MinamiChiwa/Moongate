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
public sealed class InstallTransactionTests
{
    private static readonly DatabaseMigration RestoreInitial = new("install-restore-test", 1,
        "CREATE TABLE install_values (value TEXT NOT NULL) STRICT;");
    private static readonly DatabaseMigration RestoreNext = new("install-restore-test", 2,
        "ALTER TABLE install_values ADD COLUMN migrated INTEGER NOT NULL DEFAULT 1;");

    [Fact]
    public async Task CancelledRollbackBeforeIntentLeavesJournalAndPointerUnchanged()
    {
        using var workspace = new Workspace();
        using var root = workspace.OpenData();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var switched = await PrepareRollbackAsync(workspace, root, transaction);
        var journal = File.ReadAllBytes(workspace.Journal);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.RestorePreviousAsync(switched, root, cancel.Token));
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.Equal("new-data", ReadValue(root.Paths.Database));
    }

    [Theory]
    [InlineData("DatabaseReplaced", "rollbackStarted")]
    [InlineData("DatabaseRestored", "databaseRestored")]
    [InlineData("PreviousPointerReplaced", "databaseRestored")]
    public async Task RollbackRetriesAfterOccupiedJournalOrPointerWithoutReplacingDatabaseAgain(string occupiedAt, string phase)
    {
        using var workspace = new Workspace();
        using var root = workspace.OpenData();
        FileStream? held = null;
        using var transaction = new InstallTransaction(workspace.InstallRoot, point =>
        {
            if (point.ToString() == occupiedAt)
                held = new FileStream(occupiedAt == "DatabaseRestored" ? workspace.Pointer : workspace.Journal,
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        });
        var switched = await PrepareRollbackAsync(workspace, root, transaction);
        try { await Assert.ThrowsAnyAsync<IOException>(() => transaction.RestorePreviousAsync(switched, root)); }
        finally { held?.Dispose(); }
        var record = await transaction.ReadAsync();
        Assert.Equal(phase, record.Phase);
        Assert.Equal("old-data", ReadValue(root.Paths.Database));
        var retained = root.Paths.Resolve("recovery", "database-" + workspace.TransactionId + ".sqlite");
        var retainedHash = SHA256.HashData(File.ReadAllBytes(retained));
        // 重开协调器，避免故障注入回调再次占用路径。
        transaction.Dispose();
        using var recovered = new InstallTransaction(workspace.InstallRoot);
        Assert.Equal("previousPointerRestored", (await recovered.RestorePreviousAsync(record, root)).Phase);
        Assert.Equal(retainedHash, SHA256.HashData(File.ReadAllBytes(retained)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackRestoresFixedDatabaseAndOriginalPointerAndKeepsBothVersions(bool portable)
    {
        using var workspace = new Workspace();
        using var root = portable ? DataRootLease.Open(new(Path.Combine(workspace.InstallRoot, "bootstrap"), true,
            PortableRoot: workspace.InstallRoot)) : workspace.OpenData();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var switched = await PrepareRollbackAsync(workspace, root, transaction);
        var restored = await transaction.RestorePreviousAsync(switched, root);
        Assert.Equal("previousPointerRestored", restored.Phase);
        Assert.Equal(6, restored.Revision);
        Assert.Equal(switched.PreviousPointer, JsonSerializer.Deserialize<ActiveVersionPointer>(File.ReadAllText(workspace.Pointer), WebJson));
        Assert.Equal("old-data", ReadValue(root.Paths.Database));
        Assert.Equal("new-data", ReadValue(root.Paths.Resolve("recovery", "database-" + workspace.TransactionId + ".sqlite")));
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
        Assert.Equal("new-host", File.ReadAllText(workspace.PublishedFile));
        Assert.Equal(restored.DatabaseRestore, (await transaction.RestorePreviousAsync(restored, root)).DatabaseRestore);
        Assert.Equal(InstallRecoveryAction.PreviousVersionRestored, (await transaction.AssessRecoveryAsync()).Action);
        // 确认恢复方法已经释放数据库所有权。
        await using var reopened = SqliteStore.Open(root, [RestoreInitial]);
    }

    [Fact]
    public async Task RollbackRejectsDataInsideManagedInstallationDirectory()
    {
        using var workspace = new Workspace();
        using var root = DataRootLease.Open(new(Path.Combine(workspace.Root, "bootstrap"), true,
            Path.Combine(workspace.InstallRoot, "launcher", "data")));
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var switched = await PrepareRollbackAsync(workspace, root, transaction);
        var journal = File.ReadAllBytes(workspace.Journal);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        Assert.Equal("update.rootOverlap", (await Assert.ThrowsAsync<FileTreeException>(() => transaction.RestorePreviousAsync(switched, root))).Code);
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.Equal("new-data", ReadValue(root.Paths.Database));
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("missing")]
    public async Task DamagedCandidateDoesNotPreventRestoringVerifiedPreviousVersion(string scenario)
    {
        using var workspace = new Workspace();
        using var root = workspace.OpenData();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var switched = await PrepareRollbackAsync(workspace, root, transaction);
        if (scenario == "damaged") File.WriteAllText(workspace.PublishedFile, "broken");
        else File.Delete(workspace.PublishedFile);
        Assert.Equal(InstallRecoveryAction.RestorePreviousAndData, (await transaction.AssessRecoveryAsync()).Action);
        await transaction.RestorePreviousAsync(switched, root);
        Assert.Equal("old-data", ReadValue(root.Paths.Database));
    }

    [Theory]
    [InlineData("host-owns-database")]
    [InlineData("old-tree")]
    [InlineData("unknown-pointer")]
    [InlineData("stale-record")]
    [InlineData("before-switch")]
    public async Task RollbackRejectsUnsafeStateBeforeChangingDatabaseOrJournal(string scenario)
    {
        using var workspace = new Workspace();
        using var root = workspace.OpenData();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var record = await PrepareRollbackAsync(workspace, root, transaction, scenario != "before-switch");
        SqliteStore? database = null;
        if (scenario == "host-owns-database") database = SqliteStore.Open(root, [RestoreInitial, RestoreNext]);
        if (scenario == "old-tree") File.WriteAllText(workspace.PreviousFile, "bad");
        if (scenario == "unknown-pointer")
        {
            var pointer = JsonNode.Parse(File.ReadAllText(workspace.Pointer))!;
            pointer["transactionId"] = Guid.NewGuid().ToString("N");
            File.WriteAllText(workspace.Pointer, pointer.ToJsonString());
        }
        var journal = File.ReadAllBytes(workspace.Journal);
        var pointerBytes = File.ReadAllBytes(workspace.Pointer);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => transaction.RestorePreviousAsync(
                scenario == "stale-record" ? record with { PlanId = Guid.NewGuid().ToString("N") } : record, root));
        }
        finally { if (database is not null) await database.DisposeAsync(); }
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
        Assert.Equal(pointerBytes, File.ReadAllBytes(workspace.Pointer));
        Assert.Equal("new-data", ReadValue(root.Paths.Database));
    }

    [Theory]
    [InlineData("RollbackStarted", false)]
    [InlineData("DatabaseReplaced", false)]
    [InlineData("DatabaseRestored", false)]
    [InlineData("PreviousPointerReplaced", false)]
    [InlineData("PreviousPointerRestored", false)]
    [InlineData("RollbackStarted", true)]
    public async Task RollbackDefersCancellationAfterIntentAndHoldsDatabaseOwnership(string cancelAt, bool disposeRoot)
    {
        using var workspace = new Workspace();
        using var root = workspace.OpenData();
        using var cancel = new CancellationTokenSource();
        using var transaction = new InstallTransaction(workspace.InstallRoot, point =>
        {
            if (point.ToString() != cancelAt) return Task.CompletedTask;
            Assert.Equal("resource.busy", Assert.Throws<StorageException>(() => SqliteStore.Open(root, [RestoreInitial])).Code);
            if (disposeRoot) root.Dispose();
            cancel.Cancel();
            return Task.CompletedTask;
        });
        var switched = await PrepareRollbackAsync(workspace, root, transaction);
        var result = await transaction.RestorePreviousAsync(switched, root, cancel.Token);
        Assert.Equal("previousPointerRestored", result.Phase);
        Assert.Equal("old-data", ReadValue(root.Paths.Database));
    }

    [Theory]
    [InlineData("new-data")]
    [InlineData("changed-retained")]
    [InlineData("missing-retained")]
    [InlineData("changed-backup")]
    public async Task CompletedRollbackRejectsChangedDataOrRecoveryEvidence(string scenario)
    {
        using var workspace = new Workspace();
        using var root = workspace.OpenData();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var restored = await transaction.RestorePreviousAsync(await PrepareRollbackAsync(workspace, root, transaction), root);
        var retained = root.Paths.Resolve("recovery", "database-" + workspace.TransactionId + ".sqlite");
        if (scenario == "new-data")
        {
            await using var database = SqliteStore.Open(root, [RestoreInitial]);
            await database.WriteAsync(tx => tx.Execute("UPDATE install_values SET value='after-restore'"));
        }
        if (scenario == "changed-retained") File.WriteAllText(retained, "changed-evidence");
        if (scenario == "missing-retained") File.Delete(retained);
        if (scenario == "changed-backup") File.WriteAllText(root.Paths.Resolve("backups", "database", restored.Backup!.BackupId, "main.sqlite"), "bad-backup");
        var journal = File.ReadAllBytes(workspace.Journal);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        Assert.Equal("storage.backupInvalid", (await Assert.ThrowsAsync<StorageException>(() => transaction.RestorePreviousAsync(restored, root))).Code);
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.Equal(scenario == "new-data" ? "after-restore" : "old-data", ReadValue(root.Paths.Database));
    }

    [Theory]
    [InlineData("RollbackStarted", "rollbackStarted")]
    [InlineData("DatabaseReplaced", "rollbackStarted")]
    [InlineData("DatabaseRestored", "databaseRestored")]
    [InlineData("PreviousPointerReplaced", "databaseRestored")]
    [InlineData("PreviousPointerRestored", "previousPointerRestored")]
    public async Task KilledRollbackResumesFromDatabaseAndPointerPersistenceBoundaries(string pauseAt, string phase)
    {
        using var workspace = new Workspace();
        using (var root = workspace.OpenData())
        using (var transaction = new InstallTransaction(workspace.InstallRoot))
            await PrepareRollbackAsync(workspace, root, transaction);
        using var child = workspace.StartProbe(pauseAt, "install-rollback");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            // 探针先创建标记再写入内容；读到完整内容才算到达暂停点。期限只防探针挂起，负载下进程启动和落盘较慢。
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var reached = "";
            while (true)
            {
                try { if (File.Exists(workspace.Marker)) reached = File.ReadAllText(workspace.Marker); }
                catch (IOException) { }
                if (reached.Length > 0) break;
                if (child.HasExited) throw new InvalidOperationException(await errors + await output);
                await Task.Delay(20, deadline.Token);
            }
            Assert.Equal(pauseAt, reached);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
        using var recoveredRoot = workspace.OpenData();
        using var recovered = new InstallTransaction(workspace.InstallRoot);
        var record = await recovered.ReadAsync();
        Assert.Equal(phase, record.Phase);
        Assert.Equal(phase == "previousPointerRestored" ? InstallRecoveryAction.PreviousVersionRestored : InstallRecoveryAction.ResumeRollback,
            (await recovered.AssessRecoveryAsync()).Action);
        var result = await recovered.RestorePreviousAsync(record, recoveredRoot);
        Assert.Equal("previousPointerRestored", result.Phase);
        Assert.Equal("old-data", ReadValue(recoveredRoot.Paths.Database));
        Assert.Equal("new-data", ReadValue(recoveredRoot.Paths.Resolve("recovery", "database-" + workspace.TransactionId + ".sqlite")));
        Assert.Equal(record.PreviousPointer, JsonSerializer.Deserialize<ActiveVersionPointer>(File.ReadAllText(workspace.Pointer), WebJson));
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static object? ReadValue(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM install_values";
        return command.ExecuteScalar();
    }

    private static async Task<InstallTransactionRecord> PrepareRollbackAsync(Workspace workspace, DataRootLease root,
        InstallTransaction transaction, bool switchPointer = true)
    {
        DatabaseBackupDescriptor backup;
        await using (var database = SqliteStore.Open(root, [RestoreInitial]))
        {
            await database.WriteAsync(tx => tx.Execute("INSERT INTO install_values VALUES('old-data')"));
            using var maintenance = database.Activities.TryEnterMaintenance("install-restore-fixture")!;
            backup = await database.CreateDatabaseBackupAsync(maintenance);
        }
        await transaction.InitializePointerAsync(workspace.Previous);
        var backedUp = await transaction.RecordDataBackupAsync(await workspace.PrepareAsync(transaction), backup);
        var record = switchPointer ? await transaction.SwitchPointerAsync(backedUp) : backedUp;
        await using (var database = SqliteStore.Open(root, [RestoreInitial, RestoreNext]))
            await database.WriteAsync(tx => tx.Execute("UPDATE install_values SET value='new-data'"));
        return record;
    }

    [Fact]
    public async Task ExplicitInitializationVerifiesTreeAndNeverReplacesExistingPointer()
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        var pointer = await transaction.InitializePointerAsync(workspace.Previous);
        Assert.Equal(workspace.Previous.ReleaseId, pointer.ReleaseId);
        Assert.Equal(FileTreePlanner.TreeHash(workspace.Previous.Files), pointer.TreeSha256);
        var original = File.ReadAllBytes(workspace.Pointer);
        Assert.Equal("update.installationAlreadyInitialized", (await Assert.ThrowsAsync<UpdatePlanException>(() =>
            transaction.InitializePointerAsync(workspace.Candidate))).Code);
        Assert.Equal(original, File.ReadAllBytes(workspace.Pointer));
        Assert.False(File.Exists(workspace.Journal));
    }

    [Fact]
    public async Task InstallationLockIsExclusiveAndReleasedOnDisposal()
    {
        using var workspace = new Workspace();
        var transaction = new InstallTransaction(workspace.InstallRoot);
        Assert.Equal("resource.busy", Assert.Throws<UpdatePlanException>(() => new InstallTransaction(workspace.InstallRoot)).Code);
        transaction.Dispose();
        transaction.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.ReadAsync());
        using var reopened = new InstallTransaction(workspace.InstallRoot);
        await reopened.InitializePointerAsync(workspace.Previous);
    }

    [Fact]
    public async Task PrepareNeedsExistingBaselineAndBothCompleteTrees()
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        Assert.Equal("update.pointerInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => workspace.PrepareAsync(transaction))).Code);
        Assert.False(File.Exists(workspace.Pointer));
        await transaction.InitializePointerAsync(workspace.Previous);
        File.WriteAllText(workspace.CandidateFile, "damaged");
        Assert.Equal("update.targetTreeMismatch", (await Assert.ThrowsAsync<FileTreeException>(() => workspace.PrepareAsync(transaction))).Code);
        Assert.False(File.Exists(workspace.Journal));
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
    }

    [Fact]
    public async Task JournalFreezesMetadataAndRejectsUnfinishedTransactionReplacement()
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var original = await workspace.PrepareAsync(transaction);
        var file = File.ReadAllBytes(workspace.Journal);
        var mutable = Assert.IsAssignableFrom<IList<ManifestFile>>(original.Candidate.Files);
        mutable[0] = mutable[0] with { Executable = !mutable[0].Executable };
        Assert.Equal("update.transactionStale", (await Assert.ThrowsAsync<UpdatePlanException>(() =>
            transaction.RecordDataBackupAsync(original, Workspace.Backup))).Code);
        Assert.Equal(file, File.ReadAllBytes(workspace.Journal));
        Assert.Equal("resource.busy", (await Assert.ThrowsAsync<UpdatePlanException>(() => workspace.PrepareAsync(transaction))).Code);
    }

    [Fact]
    public async Task DataBackupDescriptorAndRevisionArePersistedAndOldCopiesCannotAdvance()
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        Assert.Equal(2, backedUp.Revision);
        Assert.Equal(Workspace.Backup.Sha256, (await transaction.ReadAsync()).Backup!.Sha256);
        Assert.Equal("update.transactionStale", (await Assert.ThrowsAsync<UpdatePlanException>(() =>
            transaction.RecordDataBackupAsync(prepared, Workspace.Backup))).Code);
        Assert.Equal("update.transactionPhaseInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() =>
            transaction.RecordDataBackupAsync(backedUp, Workspace.Backup))).Code);
        Assert.Equal(InstallRecoveryAction.ResumeSwitch, (await transaction.AssessRecoveryAsync()).Action);
    }

    [Fact]
    public async Task SwitchPublishesWholeVersionKeepsPreviousAndRecordsRecoveryDirection()
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        var switched = await transaction.SwitchPointerAsync(backedUp);
        Assert.Equal("pointerSwitched", switched.Phase);
        Assert.Equal(3, switched.Revision);
        Assert.Equal(workspace.Candidate.ReleaseId, JsonNode.Parse(File.ReadAllText(workspace.Pointer))!["releaseId"]!.GetValue<string>());
        Assert.False(Directory.Exists(workspace.Stage));
        Assert.Equal("new-host", File.ReadAllText(workspace.PublishedFile));
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
        var recovery = await transaction.AssessRecoveryAsync();
        Assert.Equal(InstallRecoveryAction.RestorePreviousAndData, recovery.Action);
        Assert.Equal(Workspace.Backup.Sha256, recovery.Record.Backup!.Sha256);
        Assert.True(recovery.CandidatePublished);
        Assert.Equal("new-host", File.ReadAllText(workspace.PublishedFile));
    }

    [Theory]
    [InlineData("previous")]
    [InlineData("stage")]
    [InlineData("published")]
    [InlineData("pointer")]
    public async Task ChangedBaselineOrTargetRefusesSwitchWithoutOverwritingAnyVersion(string changed)
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        if (changed == "previous") File.WriteAllText(workspace.PreviousFile, "damaged-old");
        if (changed == "stage") File.WriteAllText(workspace.CandidateFile, "damaged-stage");
        if (changed == "published") { Directory.CreateDirectory(Path.GetDirectoryName(workspace.PublishedFile)!); File.WriteAllText(workspace.PublishedFile, "foreign-version"); }
        if (changed == "pointer") { var json = JsonNode.Parse(File.ReadAllText(workspace.Pointer))!; json["manifestSha256"] = new string('c', 64); File.WriteAllText(workspace.Pointer, json.ToJsonString()); pointer = File.ReadAllBytes(workspace.Pointer); }
        await Assert.ThrowsAnyAsync<Exception>(() => transaction.SwitchPointerAsync(backedUp));
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.Equal("dataBackedUp", (await transaction.ReadAsync()).Phase);
        Assert.Equal(InstallRecoveryAction.RecoveryRequired, (await transaction.AssessRecoveryAsync()).Action);
        if (changed == "published") Assert.Equal("foreign-version", File.ReadAllText(workspace.PublishedFile));
    }

    [Fact]
    public async Task PublishedIdenticalVersionCanBeReusedWithoutReplacingStageOrPrevious()
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.PublishedFile)!);
        File.Copy(workspace.CandidateFile, workspace.PublishedFile);
        await transaction.SwitchPointerAsync(backedUp);
        Assert.True(File.Exists(workspace.CandidateFile));
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("null-version")]
    [InlineData("bad-phase")]
    [InlineData("bad-revision")]
    [InlineData("invalid-backup")]
    [InlineData("truncated")]
    [InlineData("oversize")]
    public async Task CorruptJournalIsRejectedAndNeverRepairedByGuessing(string changed)
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        var json = JsonNode.Parse(File.ReadAllText(workspace.Journal))!;
        if (changed == "unknown-field") json["injected"] = true;
        if (changed == "null-version") json["candidate"] = null;
        if (changed == "bad-phase") json["phase"] = "committed";
        if (changed == "bad-revision") json["revision"] = -1;
        if (changed == "invalid-backup") json["backup"]!["sha256"] = "bad";
        var value = json.ToJsonString();
        if (changed == "duplicate-field") value = value.Insert(1, "\"phase\":\"prepared\",");
        if (changed == "truncated") value = value[..10];
        File.WriteAllText(workspace.Journal, value);
        if (changed == "oversize") { using var file = new FileStream(workspace.Journal, FileMode.Open, FileAccess.Write); file.SetLength(32 * 1024 * 1024 + 1); }
        var pointer = File.ReadAllBytes(workspace.Pointer);
        Assert.Equal("update.transactionInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => transaction.ReadAsync())).Code);
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
    }

    [Fact]
    public async Task CancellationBeforeSwitchRetainsDurableCandidateAndAfterPointerReplacementCompletesJournal()
    {
        using var workspace = new Workspace();
        using var cancel = new CancellationTokenSource();
        var activeCancel = cancel;
        var point = InstallPersistencePoint.VersionPublished;
        using var transaction = new InstallTransaction(workspace.InstallRoot, actual =>
        {
            if (point == actual) activeCancel.Cancel();
            return Task.CompletedTask;
        });
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.SwitchPointerAsync(backedUp, cancel.Token));
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.True(File.Exists(workspace.PublishedFile));
        Assert.Equal(InstallRecoveryAction.ResumeSwitch, (await transaction.AssessRecoveryAsync()).Action);
        point = InstallPersistencePoint.PointerReplaced;
        using var laterCancel = new CancellationTokenSource();
        activeCancel = laterCancel;
        await transaction.SwitchPointerAsync(backedUp, laterCancel.Token);
        Assert.True(laterCancel.IsCancellationRequested);
        Assert.Equal("pointerSwitched", (await transaction.ReadAsync()).Phase);
    }

    [Theory]
    [InlineData("pointer")]
    [InlineData("journal")]
    public async Task SharingViolationPreservesDurableEvidenceForRecovery(string locked)
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        using (var held = new FileStream(locked == "pointer" ? workspace.Pointer : workspace.Journal,
            FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => transaction.SwitchPointerAsync(backedUp));
        Assert.Equal("dataBackedUp", (await transaction.ReadAsync()).Phase);
        var assessment = await transaction.AssessRecoveryAsync();
        Assert.Equal(locked == "pointer" ? InstallRecoveryAction.ResumeSwitch : InstallRecoveryAction.RestorePreviousAndData, assessment.Action);
        Assert.True(assessment.CandidatePublished);
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
        Assert.Equal("new-host", File.ReadAllText(workspace.PublishedFile));
        if (locked == "pointer") Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("CON")]
    [InlineData("new.")]
    [InlineData("new ")]
    public async Task CandidateReleaseRejectsNonLocalOrReservedNames(string id)
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        await Assert.ThrowsAnyAsync<Exception>(() => transaction.PrepareAsync(workspace.TransactionId, workspace.PlanId,
            workspace.Previous, workspace.Candidate with { ReleaseId = id }));
        Assert.False(File.Exists(workspace.Journal));
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
    }

    [Fact]
    public async Task LinkedCandidateIsRejectedAndExternalFilesAreKept()
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "external");
        var link = Path.Combine(workspace.InstallRoot, "versions", "new");
        using (var junction = Process.Start(new ProcessStartInfo("cmd.exe")
        { Arguments = $"/c mklink /J \"{link}\" \"{outside}\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await junction.WaitForExitAsync();
            Assert.Equal(0, junction.ExitCode);
        }
        try
        {
            Assert.Equal("update.pathRejected", (await Assert.ThrowsAsync<FileTreeException>(() => transaction.SwitchPointerAsync(backedUp))).Code);
            Assert.Equal(InstallRecoveryAction.RecoveryRequired, (await transaction.AssessRecoveryAsync()).Action);
            Assert.Equal("external", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task CandidateMustBeFlushableBeforePointerReplacement()
    {
        using var workspace = new Workspace();
        FileStream? held = null;
        using var transaction = new InstallTransaction(workspace.InstallRoot, point =>
        {
            if (point == InstallPersistencePoint.VersionPublished)
                held = new FileStream(workspace.PublishedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        });
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        var backedUp = await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        var pointer = File.ReadAllBytes(workspace.Pointer);
        try { await Assert.ThrowsAnyAsync<IOException>(() => transaction.SwitchPointerAsync(backedUp)); }
        finally { held?.Dispose(); }
        Assert.Equal(pointer, File.ReadAllBytes(workspace.Pointer));
        Assert.Equal("dataBackedUp", (await transaction.ReadAsync()).Phase);
        Assert.Equal(InstallRecoveryAction.ResumeSwitch, (await transaction.AssessRecoveryAsync()).Action);
        Assert.Equal("new-host", File.ReadAllText(workspace.PublishedFile));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("unknown-transaction")]
    public async Task UnrecognizedPointerRequiresRecoveryWithoutChangingFiles(string changed)
    {
        using var workspace = new Workspace();
        using var transaction = new InstallTransaction(workspace.InstallRoot);
        await transaction.InitializePointerAsync(workspace.Previous);
        var prepared = await workspace.PrepareAsync(transaction);
        await transaction.RecordDataBackupAsync(prepared, Workspace.Backup);
        var json = JsonNode.Parse(File.ReadAllText(workspace.Pointer))!;
        if (changed == "unknown-field") json["untrusted"] = true;
        if (changed == "unknown-transaction") json["transactionId"] = Guid.NewGuid().ToString("N");
        var value = json.ToJsonString();
        if (changed == "duplicate-field") value = value.Insert(1, "\"schemaVersion\":1,");
        File.WriteAllText(workspace.Pointer, changed == "truncated" ? value[..10] : value);
        if (changed == "missing") File.Delete(workspace.Pointer);
        var journal = File.ReadAllBytes(workspace.Journal);
        var assessment = await transaction.AssessRecoveryAsync();
        Assert.Equal(InstallRecoveryAction.RecoveryRequired, assessment.Action);
        Assert.Equal(journal, File.ReadAllBytes(workspace.Journal));
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
        Assert.Equal("new-host", File.ReadAllText(workspace.CandidateFile));
        if (changed == "missing") Assert.False(File.Exists(workspace.Pointer));
    }

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData("Prepared", "prepared", InstallRecoveryAction.Prepared)]
    [InlineData("DataBackedUp", "dataBackedUp", InstallRecoveryAction.ResumeSwitch)]
    [InlineData("VersionPublished", "dataBackedUp", InstallRecoveryAction.ResumeSwitch)]
    [InlineData("PointerReplaced", "dataBackedUp", InstallRecoveryAction.RestorePreviousAndData)]
    [InlineData("PointerSwitched", "pointerSwitched", InstallRecoveryAction.RestorePreviousAndData)]
    internal async Task KilledProcessIsAssessedFromRealPointerAndTrees(string pauseAt, string phase, InstallRecoveryAction action)
    {
        using var workspace = new Workspace();
        DatabaseBackupDescriptor backup;
        using (var lease = DataRootLease.Open(new(Path.Combine(workspace.Root, "bootstrap"), true, Path.Combine(workspace.Root, "data"))))
        await using (var database = SqliteStore.Open(lease))
        {
            using var maintenance = database.Activities.TryEnterMaintenance("probe-backup")!;
            backup = await database.CreateDatabaseBackupAsync(maintenance);
        }
        using (var initial = new InstallTransaction(workspace.InstallRoot)) await initial.InitializePointerAsync(workspace.Previous);
        File.WriteAllText(Path.Combine(workspace.InstallRoot, "probe.json"), JsonSerializer.Serialize(new
        { workspace.TransactionId, workspace.PlanId, workspace.Previous, workspace.Candidate, Backup = backup }));
        using var child = workspace.StartProbe(pauseAt);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(workspace.Marker))
            {
                if (child.HasExited) throw new InvalidOperationException(await errors);
                await Task.Delay(50, deadline.Token);
            }
            Assert.Equal("resource.busy", Assert.Throws<UpdatePlanException>(() => new InstallTransaction(workspace.InstallRoot)).Code);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await Task.WhenAll(output, errors);
        }
        using var recovered = new InstallTransaction(workspace.InstallRoot);
        var record = await recovered.ReadAsync();
        Assert.Equal(phase, record.Phase);
        var assessment = await recovered.AssessRecoveryAsync();
        Assert.Equal(action, assessment.Action);
        Assert.Equal("old-host", File.ReadAllText(workspace.PreviousFile));
        if (pauseAt is "VersionPublished" or "PointerReplaced" or "PointerSwitched") Assert.Equal("new-host", File.ReadAllText(workspace.PublishedFile));
        if (action == InstallRecoveryAction.ResumeSwitch) await recovered.SwitchPointerAsync(record);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "data", "backups", "database", backup.BackupId, "main.sqlite")));
    }

    internal sealed class Workspace : IDisposable
    {
        public static DatabaseBackupDescriptor Backup => new(1, new string('1', 32), new string('2', 32), new string('3', 32),
            "live", new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero), 4096, new string('a', 64), [new("core", 1, new string('B', 64))]);
        public string Root { get; }
        public string Repository { get; }
        public string InstallRoot => Path.Combine(Root, "install");
        public string PlanId { get; } = Guid.NewGuid().ToString("N");
        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public string Stage => Path.Combine(InstallRoot, "staging", PlanId);
        public string PreviousFile => Path.Combine(InstallRoot, "versions", "old", "components", "host", "Host.dll");
        public string CandidateFile => Path.Combine(Stage, "components", "host", "Host.dll");
        public string PublishedFile => Path.Combine(InstallRoot, "versions", "new", "components", "host", "Host.dll");
        public string Pointer => Path.Combine(InstallRoot, "launcher", "active-version.json");
        public string Journal => Path.Combine(InstallRoot, "update-state", "install-transaction.json");
        public string Marker => Path.Combine(Root, "reached.txt");
        public InstallVersion Previous { get; }
        public InstallVersion Candidate { get; }
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            Repository = repository!.FullName;
            Root = Path.Combine(Repository, "artifacts", "tmp", "install-transaction-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(PreviousFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(CandidateFile)!);
            File.WriteAllText(PreviousFile, "old-host");
            File.WriteAllText(CandidateFile, "new-host");
            Previous = Version("old", 1, "old-host");
            Candidate = Version("new", 2, "new-host");
        }
        public Task<InstallTransactionRecord> PrepareAsync(InstallTransaction transaction) => transaction.PrepareAsync(TransactionId, PlanId, Previous, Candidate);
        private static InstallVersion Version(string id, long sequence, string content) => new(id, sequence, new string('a', 64),
            [new("components/host/Host.dll", Encoding.UTF8.GetByteCount(content), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant())]);
        public DataRootLease OpenData() => DataRootLease.Open(new(Path.Combine(Root, "bootstrap"), true, Path.Combine(Root, "data")));
        public Process StartProbe(string pauseAt, string scenario = "install-transaction")
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(Repository, "artifacts", "build", "bin", "MabiTool.PersistenceProbe", configuration, "MabiTool.PersistenceProbe.dll"));
            foreach (var arg in new[] { scenario, InstallRoot, pauseAt, Marker }) start.ArgumentList.Add(arg);
            return Process.Start(start)!;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
