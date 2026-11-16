using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class DataMaintenanceTests
{
    [Trait("Category", "Slow")]
    [Fact]
    public async Task RestorePreviewProtectsAnOldAutomaticBackupUntilItsExpiry()
    {
        await using var work = new Workspace(); var backups = new List<DataSnapshotDescriptor>();
        for (var i = 0; i < 2; i++)
        {
            backups.Add(await work.Database.CreateOrdinaryDataSnapshotAsync(work.Assets, DataBackupPurpose.Automatic));
            work.Clock.Advance(TimeSpan.FromHours(1));
        }
        var control = new StorageControlStore(work.Database, work.Paths, [work.Backups], "test", work.Clock);
        var cleanup = await control.PreviewAsync("owner", new(["backups"]), "preview-protection-cleanup"); Assert.Single(cleanup.Candidates);
        await work.Plans.PreviewRestoreAsync("owner", new(backups[0].SnapshotId), "preview-protection-restore");
        Assert.Empty((await control.PreviewAsync("owner", new(["backups"]), "preview-protected-cleanup")).Candidates);
        var kept = await control.ExecuteAsync(cleanup.Id, "owner", "preview-protected-execute", StorageControlStore.ETag(cleanup));
        Assert.Equal("backupRestorePreview", Assert.Single(kept.Items).Reason); Assert.Equal(0UL, kept.ReclaimedBytes.Value);
        await work.Database.VerifyDataSnapshotAsync(backups[0].SnapshotId, backups[0]);
        work.Clock.Advance(TimeSpan.FromMinutes(16));
        var expired = await control.PreviewAsync("owner", new(["backups"]), "preview-expired-cleanup"); Assert.Single(expired.Candidates);
        var removed = await control.ExecuteAsync(expired.Id, "owner", "preview-expired-execute", StorageControlStore.ETag(expired));
        Assert.Equal("removed", Assert.Single(removed.Items).State);
        Assert.False(Directory.Exists(work.Paths.Resolve("backups", "data", backups[0].SnapshotId)));
        foreach (var backup in backups.Skip(1)) await work.Database.VerifyDataSnapshotAsync(backup.SnapshotId, backup);
    }

    [Fact]
    public async Task CancellationBeforeJournalPublicationReleasesItsExclusiveRecoverySnapshot()
    {
        await using var work = new Workspace(); var snapshot = await work.Snapshot();
        var plan = await work.Plans.PreviewRestoreAsync("owner", new(snapshot.SnapshotId), "cancel-recovery-preview");
        var preview = await work.Plans.CheckAsync(plan.Id, "owner", "restore", StorageMaintenancePlanStore.ETag(plan));
        var request = JsonSerializer.SerializeToElement(new { planId = plan.Id, kind = plan.Kind }, ContractJson.Options);
        var operation = (await work.Operations.AdmitStorageAsync("owner", "storage.restore", plan.Id, Workspace.RequestHash(plan), "cancel-recovery-execute", request, true)).Snapshot;
        using var stop = new CancellationTokenSource();
        using (var maintenance = work.Database.Activities.TryEnterMaintenance("cancel-recovery")!)
        {
            var coordinator = new StorageMaintenanceCoordinator(work.Database, work.Lease!, work.Plans, work.Operations, Workspace.Migrations);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.PrepareCoreAsync(preview, operation.Id,
                "cancel-recovery-execute", Workspace.RequestHash(plan), maintenance, stop.Token,
                _ => { stop.Cancel(); return Task.CompletedTask; }));
            await work.Operations.FinishStorageAsync(operation.Id, null, cancelled: true, maintenance: maintenance);
        }
        Assert.Equal(snapshot.SnapshotId, Assert.Single(work.Backups.Records()).Snapshot.SnapshotId);
        Assert.Single(Directory.GetDirectories(work.Paths.Resolve("backups", "data")));
        Assert.Single(Directory.GetDirectories(work.Paths.Resolve("backups", "database")));
        Assert.False(StorageMaintenanceCoordinator.NeedsRecovery(work.Options));
        Assert.Empty(work.Database.Activities.Snapshot().Activities);
        Assert.Equal("initial", work.Value()); await work.Database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot);
    }

    [Fact]
    public async Task LegacyActiveRootKeepsItsIdentityAndUpgradesPermanentBootstrapAuthority()
    {
        await using var work = new Workspace(); var original = work.Paths.DataRoot; var rootId = work.Paths.RootId; var databaseId = work.Lease!.DatabaseId;
        await work.Close();
        var marker = Path.Combine(original, ".mabitool-data-root.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { version = 1, product = "MabiTool", rootId, databaseId }, ContractJson.Options));
        work.Open();
        using var upgraded = JsonDocument.Parse(File.ReadAllText(marker));
        Assert.Equal(2, upgraded.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(work.Options.BootstrapDirectory, upgraded.RootElement.GetProperty("authorityBootstrap").GetString());
        Assert.Equal(rootId, work.Paths.RootId); Assert.Equal(databaseId, work.Lease!.DatabaseId); Assert.Equal("initial", work.Value());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedMaintenanceBlocksOtherBootstrapsAndRecoversThroughPermanentAuthority(bool migrate)
    {
        await using var work = new Workspace(); var original = work.Paths.DataRoot;
        var alias = new StorageOptions(Path.Combine(work.Root, "alias-bootstrap"), true, DataRoot: original);
        await work.Close();
        using (var associated = DataRootLease.Open(alias)) Assert.Equal(work.Options.BootstrapDirectory, associated.Paths.BootstrapDirectory);
        work.Open();
        var plan = migrate ? await work.MigrationPlan() : await work.Plans.PreviewRestoreAsync("owner", new((await work.Snapshot()).SnapshotId), "authority-preview");
        var operation = await work.Prepare(plan); await work.Close();
        Assert.Equal("storage.maintenanceRequired", Assert.Throws<StorageException>(() => DataRootLease.Open(alias)).Code);
        Assert.Equal("storage.maintenanceRequired", Assert.Throws<StorageException>(() => DataRootLease.Open(new(Path.Combine(work.Root, "new-bootstrap"), true, DataRoot: original))).Code);
        Assert.True(StorageMaintenanceCoordinator.NeedsRecovery(alias));
        Assert.Equal(work.Options.BootstrapDirectory, StorageMaintenanceCoordinator.StartupOptions(alias).BootstrapDirectory);
        Assert.True(await StorageMaintenanceCoordinator.RecoverCoreAsync(alias, Workspace.Migrations, Workspace.Definitions, work.Clock, VolumeSpaceReservations.Shared, default));
        using (var authority = DataRootLease.Open(StorageMaintenanceCoordinator.StartupOptions(alias)))
            Assert.Equal(migrate ? work.Target : original, authority.Paths.DataRoot);
        work.Open(); Assert.Equal("initial", work.Value());
        Assert.Equal(OperationState.Succeeded, work.Operations.Get(operation.Id, "owner", false).State.KnownValue);
    }

    [Fact]
    public async Task DataFingerprintIgnoresRuntimeWritesAndTracksBusinessAndEmptySettingsDirectories()
    {
        await using var work = new Workspace();
        var before = await work.Data.CaptureAsync();
        await work.Database.SaveOperationAsync(new("op-noise", "fixture", "owner", [], OperationState.Succeeded, "done", new(1), 1,
            new(work.Clock.GetUtcNow(), null, work.Clock.GetUtcNow()), null, null, new(CancellationState.None, null), EffectStatus.None));
        await using (var temporary = await work.Database.TemporaryFiles.TrackAsync("probe", work.Paths.Resolve("temp", "noise.part"))) { }
        Assert.Equal(before.Digest, (await work.Data.CaptureAsync()).Digest);
        await work.Database.WriteAsync(tx => tx.Execute("UPDATE storage_probe SET value='changed'"));
        Assert.NotEqual(before.Digest, (await work.Data.CaptureAsync()).Digest);
        var business = await work.Data.CaptureAsync();
        Directory.CreateDirectory(work.Paths.Resolve("settings", "empty"));
        Assert.NotEqual(business.Digest, (await work.Data.CaptureAsync()).Digest);
    }

    [Fact]
    public async Task PreviewRejectsChangedDataOwnerEpochRevisionAndExpiryBeforeSwitching()
    {
        await using var work = new Workspace(); var snapshot = await work.Snapshot();
        var plan = await work.Plans.PreviewRestoreAsync("owner", new(snapshot.SnapshotId), "maintenance-preview");
        Assert.Equal(plan.Id, (await work.Plans.PreviewRestoreAsync("owner", new(snapshot.SnapshotId), "maintenance-preview")).Id);
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => work.Plans.Get(plan.Id, "other", "restore")).Code);
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() => work.Plans.CheckAsync(plan.Id, "owner", "restore", "\"old\""))).Code);
        Assert.Equal("storage.planStale", (await Assert.ThrowsAsync<OperationException>(() => work.NewPlans("new").CheckAsync(plan.Id, "owner", "restore", StorageMaintenancePlanStore.ETag(plan)))).Code);
        await work.Database.WriteAsync(tx => tx.Execute("UPDATE storage_probe SET value='new'"));
        Assert.Equal("storage.planStale", (await Assert.ThrowsAsync<OperationException>(() => work.Plans.CheckAsync(plan.Id, "owner", "restore", StorageMaintenancePlanStore.ETag(plan)))).Code);
        work.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("storage.planExpired", (await Assert.ThrowsAsync<OperationException>(() => work.Plans.CheckAsync(plan.Id, "owner", "restore", StorageMaintenancePlanStore.ETag(plan)))).Code);
        Assert.False(StorageMaintenanceCoordinator.NeedsRecovery(work.Options.BootstrapDirectory));
        Assert.Equal("new", work.Value());
    }

    [Fact]
    public async Task IncompatibleSchemaIsRejectedDuringPreviewWithoutReplacingData()
    {
        await using var work = new Workspace(); var snapshot = await work.Snapshot();
        var incompatible = Workspace.Migrations.Select(x => x.ModuleId == "storage-probe" ? x with { Sql = x.Sql + "\n-- changed migration" } : x).ToArray();
        var plans = new StorageMaintenancePlanStore(work.Database, work.Backups, work.Data, "test", incompatible, work.Clock);
        Assert.Equal("storage.backupIncompatible", (await Assert.ThrowsAsync<StorageException>(() => plans.PreviewRestoreAsync("owner", new(snapshot.SnapshotId), "incompatible"))).Code);
        Assert.Equal("initial", work.Value()); Assert.Single(work.Backups.Records());
        Assert.False(StorageMaintenanceCoordinator.NeedsRecovery(work.Options.BootstrapDirectory));
    }

    [Fact]
    public async Task RestorePublishesPersistentReceiptKeepsBackupCatalogAndDoesNotOverwriteLaterData()
    {
        await using var work = new Workspace(); var snapshot = await work.Snapshot();
        await work.Change("after-backup");
        var plan = await work.Plans.PreviewRestoreAsync("owner", new(snapshot.SnapshotId), "restore-preview");
        var operation = await work.Prepare(plan);
        Assert.Equal("storage.maintenanceRequired", Assert.Throws<StorageException>(() => DataRootLease.Open(work.Options)).Code);
        await work.Close(); Assert.True(await work.Recover()); work.Open();
        Assert.Equal("initial", work.Value()); Assert.Equal("initial", File.ReadAllText(work.Paths.Resolve("settings", "value.json")));
        var completed = work.Operations.Get(operation.Id, "owner", false);
        Assert.Equal(OperationState.Succeeded, completed.State.KnownValue);
        var result = completed.Result!.Value.Deserialize<StorageMaintenanceResult>(ContractJson.Options)!;
        Assert.Equal("completed", result.State); Assert.False(result.RolledBack);
        Assert.Contains(work.Backups.Records(), x => x.Snapshot.SnapshotId == snapshot.SnapshotId);
        Assert.Contains(work.Backups.Records(), x => x.Snapshot.SnapshotId == result.BackupId && x.Purpose == DataBackupPurpose.RestoreRecovery && x.Retained);
        Assert.Equal(completed.Id, work.Operations.StorageRetry("owner", "storage.restore", plan.Id, Workspace.RequestHash(plan), "maintenance-execute")!.Id);
        Assert.Equal("completed", work.NewPlans("new").Get(plan.Id, "owner", "restore").State);
        await work.Change("written-after-completion"); await work.Close();
        Assert.False(await work.Recover()); work.Open();
        Assert.Equal("written-after-completion", work.Value());
        Assert.Equal(completed.Revision, work.Operations.Get(operation.Id, "owner", false).Revision);
    }

    [Theory]
    [InlineData("filesPrepared")]
    [InlineData("database.Replaced")]
    [InlineData("files.SettingsRestored")]
    [InlineData("health.checked")]
    [InlineData("healthy")]
    public async Task InterruptedRestoreResumesFromFixedEvidence(string point)
    {
        await using var work = new Workspace(); var snapshot = await work.Snapshot(); await work.Change("new");
        var plan = await work.Plans.PreviewRestoreAsync("owner", new(snapshot.SnapshotId), "restore-preview"); var operation = await work.Prepare(plan);
        await work.Close();
        await Assert.ThrowsAsync<DataMaintenanceInterruptionException>(() => work.Recover(at => at == point ? Task.FromException(new DataMaintenanceInterruptionException()) : Task.CompletedTask));
        Assert.True(StorageMaintenanceCoordinator.NeedsRecovery(work.Options.BootstrapDirectory));
        await work.Recover(); work.Open();
        Assert.Equal("initial", work.Value()); Assert.Equal(OperationState.Succeeded, work.Operations.Get(operation.Id, "owner", false).State.KnownValue);
        Assert.Empty(work.Database.Activities.Snapshot().Activities);
    }

    [Theory]
    [InlineData("copy.file.0")]
    [InlineData("originalRetained")]
    [InlineData("health.checked")]
    public async Task InterruptedMigrationResumesAndRetainedRootCannotBeOpenedThroughAnotherBootstrap(string point)
    {
        await using var work = new Workspace(); var original = work.Paths.DataRoot;
        var plan = await work.MigrationPlan(); var operation = await work.Prepare(plan); await work.Close();
        await Assert.ThrowsAsync<DataMaintenanceInterruptionException>(() => work.Recover(at => at == point ? Task.FromException(new DataMaintenanceInterruptionException()) : Task.CompletedTask));
        await work.Recover(); work.Open();
        Assert.Equal(work.Target, work.Paths.DataRoot); Assert.Equal("initial", work.Value());
        Assert.Equal(OperationState.Succeeded, work.Operations.Get(operation.Id, "owner", false).State.KnownValue);
        Assert.Equal("storage.rootRetained", Assert.Throws<StorageException>(() => DataRootLease.Open(new(Path.Combine(work.Root, "other-bootstrap"), true, DataRoot: original))).Code);
        Assert.Equal("initial", File.ReadAllText(Path.Combine(original, "settings", "value.json")));
        Assert.Equal("initial", File.ReadAllText(work.Paths.Resolve("settings", "value.json")));
        await work.Database.TemporaryFiles.RecoverAsync();
        await work.Change("moved-write"); await work.Close(); Assert.False(await work.Recover()); work.Open(); Assert.Equal("moved-write", work.Value());
    }

    [Theory]
    [InlineData("copyPrepared")]
    [InlineData("pointerSwitched")]
    [InlineData("health.checked")]
    public async Task MissingMigrationTargetRestoresOriginalAuthorityAndKeepsItsData(string point)
    {
        await using var work = new Workspace(); var original = work.Paths.DataRoot; var plan = await work.MigrationPlan(); var operation = await work.Prepare(plan);
        await work.Close();
        await Assert.ThrowsAsync<DataMaintenanceInterruptionException>(() => work.Recover(at => at == point ? Task.FromException(new DataMaintenanceInterruptionException()) : Task.CompletedTask));
        var offline = work.Target + "-offline"; Directory.Move(work.Target, offline);
        await work.Recover(); work.Open();
        Assert.Equal(original, work.Paths.DataRoot); Assert.Equal("initial", work.Value());
        var failed = work.Operations.Get(operation.Id, "owner", false); Assert.Equal(OperationState.Failed, failed.State.KnownValue);
        Assert.True(failed.Result!.Value.Deserialize<StorageMaintenanceResult>(ContractJson.Options)!.RolledBack);
        Assert.True(Directory.Exists(offline));
    }

    [Fact]
    public async Task MissingHealthyTargetKeepsSourceRetainedUntilTargetReturns()
    {
        await using var work = new Workspace(); var original = work.Paths.DataRoot; var plan = await work.MigrationPlan();
        var operation = await work.Prepare(plan); await work.Close();
        await Assert.ThrowsAsync<DataMaintenanceInterruptionException>(() => work.Recover(at => at == "healthy" ? Task.FromException(new DataMaintenanceInterruptionException()) : Task.CompletedTask));
        var offline = work.Target + "-offline"; Directory.Move(work.Target, offline);
        Assert.Equal("storage.rootMissing", (await Assert.ThrowsAsync<StorageException>(() => work.Recover())).Code);
        Assert.True(StorageMaintenanceCoordinator.NeedsRecovery(work.Options));
        Assert.Equal("storage.rootRetained", Assert.Throws<StorageException>(() => DataRootLease.Open(new(Path.Combine(work.Root, "other-bootstrap"), true, DataRoot: original))).Code);
        Directory.Move(offline, work.Target); await work.Recover(); work.Open();
        Assert.Equal(work.Target, work.Paths.DataRoot); Assert.Equal("initial", work.Value());
        Assert.Equal(OperationState.Succeeded, work.Operations.Get(operation.Id, "owner", false).State.KnownValue);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task RestoreHealthFailureRollsBackOnceAndKeepsCompleteReceipt()
    {
        await using var work = new Workspace(); var snapshot = await work.Snapshot(); await work.Change("new");
        var plan = await work.Plans.PreviewRestoreAsync("owner", new(snapshot.SnapshotId), "restore-preview"); var operation = await work.Prepare(plan);
        await work.Close(); var injected = false;
        await work.Recover(at =>
        {
            if (at == "healthChecking" && !injected) { injected = true; throw new StorageException("storage.assetDamaged", "injected health failure"); }
            return Task.CompletedTask;
        });
        work.Open(); Assert.Equal("new", work.Value());
        var failed = work.Operations.Get(operation.Id, "owner", false); Assert.Equal(OperationState.Failed, failed.State.KnownValue);
        Assert.True(failed.Result!.Value.Deserialize<StorageMaintenanceResult>(ContractJson.Options)!.RolledBack);
        await work.Change("after-rollback"); await work.Close(); await work.Recover(); work.Open(); Assert.Equal("after-rollback", work.Value());
    }

    [Trait("Category", "Slow")]
    [CrossVolumeFact]
    public async Task ActualCrossVolumeCopyPreservesBusinessBootstrapAndTrustLocation()
    {
        var tools = CrossVolumeFactAttribute.ToolsRoot!;
        var target = Path.Combine(tools, "storage-cross-volume-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var work = new Workspace(target); var original = work.Paths.DataRoot;
            Directory.CreateDirectory(target);
            Assert.NotEqual(StorageDirectoryEvidence.Capture(original, false).Identity.Split(':')[0], StorageDirectoryEvidence.Capture(target).Identity.Split(':')[0]);
            await work.Change("cross-volume-data");
            var trust = Path.Combine(work.Options.BootstrapDirectory, "trust-location-fixture.dat"); var bytes = RandomNumberGenerator.GetBytes(32); File.WriteAllBytes(trust, bytes);
            var plan = await work.MigrationPlan(); var operation = await work.Prepare(plan); await work.Close(); await work.Recover(); work.Open();
            Assert.Equal(target, work.Paths.DataRoot); Assert.Equal("cross-volume-data", work.Value()); Assert.Equal(bytes, File.ReadAllBytes(trust));
            Assert.Equal(OperationState.Succeeded, work.Operations.Get(operation.Id, "owner", false).State.KnownValue);
            Assert.Equal("cross-volume-data", File.ReadAllText(Path.Combine(original, "settings", "value.json")));
            await work.Database.TemporaryFiles.RecoverAsync();
        }
        finally
        {
            Assert.StartsWith(tools + Path.DirectorySeparatorChar, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData("copy.file.0", true)]
    [InlineData("database.Replaced", false)]
    public async Task TerminatedMaintenanceProcessRecoversWithoutReplayingTheTransaction(string point, bool migrate)
    {
        await using var work = new Workspace(); StorageMaintenancePlan plan;
        if (migrate) plan = await work.MigrationPlan();
        else { var snapshot = await work.Snapshot(); await work.Change("after-backup"); plan = await work.Plans.PreviewRestoreAsync("owner", new(snapshot.SnapshotId), "process-preview"); }
        var original = work.Paths.DataRoot; var operation = await work.Prepare(plan); await work.Close();
        File.WriteAllText(Path.Combine(work.Options.BootstrapDirectory, "data-maintenance-probe.json"),
            JsonSerializer.Serialize(new { migrations = Workspace.Migrations, data = Workspace.Definitions }, ContractJson.Options));
        var marker = Path.Combine(work.Root, "process-reached.txt");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(StorageTestWorkspace.Repository, "artifacts", "build", "bin", "MabiTool.PersistenceProbe", "release", "MabiTool.PersistenceProbe.dll"));
        foreach (var argument in new[] { "data-maintenance-" + point, work.Options.BootstrapDirectory, original, marker }) start.ArgumentList.Add(argument);
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                while (!File.Exists(marker) && !process.HasExited) await Task.Delay(50, timeout.Token);
                Assert.True(File.Exists(marker), process.HasExited ? await error : "维护进程未到达故障注入点");
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await output; await error;
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        await work.Recover(); work.Open(); Assert.Equal("initial", work.Value());
        Assert.Equal(migrate ? work.Target : original, work.Paths.DataRoot);
        Assert.Equal(OperationState.Succeeded, work.Operations.Get(operation.Id, "owner", false).State.KnownValue);
        await work.Change("after-recovery"); await work.Close(); Assert.False(await work.Recover()); work.Open(); Assert.Equal("after-recovery", work.Value());
    }

    internal sealed class Workspace : IAsyncDisposable
    {
        public static IReadOnlyList<DatabaseMigration> Migrations { get; } = [.. AssetStore.Migrations, TemporaryStorageRegistry.Migration,
            StorageControlStore.Migration, DataBackupStore.Migration, StorageMaintenancePlanStore.Migration,
            new("storage-probe", 1, "CREATE TABLE storage_probe(id INTEGER PRIMARY KEY,value TEXT NOT NULL) STRICT; INSERT INTO storage_probe VALUES(1,'initial');")];
        public static IReadOnlyList<StorageDataDefinition> Definitions { get; } = [new("probe", ["storage_probe"]), new("core", ["platform_assets", "core_module_configuration"])];
        public string Root { get; } = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "data-maintenance-" + Guid.NewGuid().ToString("N"));
        public StorageOptions Options { get; }
        private readonly string? externalTarget;
        public string Target => externalTarget ?? Path.Combine(Root, "moved");
        public StorageClock Clock { get; } = new();
        public DataRootLease? Lease { get; private set; }
        public PathService Paths => Lease!.Paths;
        public SqliteStore Database { get; private set; } = null!;
        public AssetStore Assets { get; private set; } = null!;
        public DataBackupStore Backups => new(Database, Paths);
        public StorageDataCatalog Data => new(Database, Paths, Definitions);
        public OperationStore Operations => new(Database, Clock);
        public StorageMaintenancePlanStore Plans => NewPlans("test");
        public StorageMaintenancePlanStore NewPlans(string epoch) => new(Database, Backups, Data, epoch, Migrations, Clock);
        public Workspace(string? target = null)
        {
            externalTarget = target;
            Directory.CreateDirectory(Root); Options = new(Path.Combine(Root, "bootstrap"), true, DataRoot: Path.Combine(Root, "data")); Open();
            File.WriteAllText(Paths.Resolve("settings", "value.json"), "initial");
        }
        public void Open() { Lease = DataRootLease.Open(Options); Database = SqliteStore.Open(Lease, Migrations, clock: Clock); Assets = new(Database, Paths, Clock); }
        public Task<DataSnapshotDescriptor> Snapshot() => Database.CreateOrdinaryDataSnapshotAsync(Assets, DataBackupPurpose.Manual);
        public string Value() => (string)Database.ReadScalar("SELECT value FROM storage_probe")!;
        public async Task Change(string value)
        {
            await Database.WriteAsync(tx => tx.Execute("UPDATE storage_probe SET value=$value", ("$value", value)));
            File.WriteAllText(Paths.Resolve("settings", "value.json"), value);
            await Assets.PutAsync("probe", "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(value)));
        }
        public async Task<StorageMaintenancePlan> MigrationPlan()
        {
            Directory.CreateDirectory(Target); var evidence = StorageDirectoryEvidence.Capture(Target);
            return await Plans.PreviewMigrationAsync("owner", new(Guid.NewGuid().ToString("N")), evidence, "migration-preview");
        }
        public static string RequestHash(StorageMaintenancePlan plan) => StorageMaintenancePlanStore.Hash(JsonSerializer.SerializeToElement(new { planId = plan.Id, kind = plan.Kind }, ContractJson.Options));
        public async Task<OperationSnapshot> Prepare(StorageMaintenancePlan plan)
        {
            var preview = await Plans.CheckAsync(plan.Id, "owner", plan.Kind, StorageMaintenancePlanStore.ETag(plan));
            var request = JsonSerializer.SerializeToElement(new { planId = plan.Id, kind = plan.Kind }, ContractJson.Options);
            var operation = (await Operations.AdmitStorageAsync("owner", "storage." + plan.Kind, plan.Id, RequestHash(plan), "maintenance-execute", request, true)).Snapshot;
            using var maintenance = Database.Activities.TryEnterMaintenance("storage-test")!;
            using var ticket = await new StorageMaintenanceCoordinator(Database, Lease!, Plans, Operations, Migrations).PrepareAsync(preview, operation.Id, "maintenance-execute", RequestHash(plan), maintenance);
            return operation;
        }
        public Task<bool> Recover(Func<string, Task>? persisted = null) => StorageMaintenanceCoordinator.RecoverCoreAsync(Options, Migrations, Definitions, Clock, VolumeSpaceReservations.Shared, default, persisted);
        public async Task Close()
        {
            if (Lease is null) return; await Assets.DisposeAsync(); await Database.DisposeAsync(); Lease.Dispose(); Lease = null;
        }
        public async ValueTask DisposeAsync()
        {
            await Close(); var allowed = Path.GetFullPath(Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(allowed, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase); Directory.Delete(Root, true);
        }
    }
}

/// <summary>跨卷复制需要与仓库不同的本地固定卷：优先 %TEMP%，同卷时读取 MABITOOL_TEST_CROSS_VOLUME_ROOT，仍无可用卷则跳过。</summary>
public sealed class CrossVolumeFactAttribute : FactAttribute
{
    public const string Variable = "MABITOOL_TEST_CROSS_VOLUME_ROOT";
    public static string? ToolsRoot { get; } = Select();

    public CrossVolumeFactAttribute()
    {
        if (ToolsRoot is null)
            Skip = $"%TEMP% 与仓库位于同一卷，且未通过 {Variable} 指定其他本地固定卷上的目录，无法执行真实跨卷复制";
    }

    private static string? Select()
    {
        var repository = Path.GetPathRoot(Path.GetFullPath(StorageTestWorkspace.Repository));
        foreach (var candidate in new[] { Path.Combine(Path.GetTempPath(), "MabiTool", "artifacts"), Environment.GetEnvironmentVariable(Variable) })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
            var full = Path.GetFullPath(candidate); var root = Path.GetPathRoot(full);
            if (root is null || string.Equals(root, repository, StringComparison.OrdinalIgnoreCase)) continue;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.DriveType == DriveType.Fixed) return full;
        }
        return null;
    }
}
