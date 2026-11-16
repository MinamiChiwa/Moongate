using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.Modules.MusicLibrary.Persistence;
using Xunit;

namespace MabiTool.Host.Tests;

[Trait("Category", "Slow")]
[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class ManagedInstallTests(ManagedInstallImages images) : IClassFixture<ManagedInstallImages>
{
    [Fact]
    public async Task ActualCandidateCodeCommitsTwoUpgradesAndPreservesBusinessDataAndOriginalOperations()
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        var firstAsset = await SaveBusinessData(fixture, "before-first");
        await fixture.StageAsync("0.2.0"); var first = await fixture.TransferIntentAsync();
        var completed = await new UpdateInstallEngine(first.Intent, first.Hash, new(0, 1, 0)).RunAsync(false);
        Assert.True(completed.Outcome == "committed", $"outcome={completed.Outcome}, failure={completed.FailureCode}, phase={completed.Transaction.Phase}");
        var firstSnapshot = completed.Transaction.Snapshot!.SnapshotId;
        Assert.Equal("committed", completed.Outcome); await VerifyBusinessData(fixture, "before-first", firstAsset);
        await AssertTerminal(fixture, first.Intent, "committed"); await fixture.ReloadOptionsAsync();
        Assert.Equal("release-0.2.0", fixture.Options.InstalledReleaseId);
        var secondAsset = await SaveBusinessData(fixture, "between-upgrades");
        await fixture.StageAsync("0.3.0"); var second = await fixture.TransferIntentAsync();
        completed = await new UpdateInstallEngine(second.Intent, second.Hash, new(0, 1, 0)).RunAsync(false);
        Assert.Equal("committed", completed.Outcome); await VerifyBusinessData(fixture, "between-upgrades", secondAsset);
        await AssertTerminal(fixture, second.Intent, "committed"); await AssertTerminal(fixture, first.Intent, "committed");
        await fixture.ReloadOptionsAsync(); Assert.Equal("release-0.3.0", fixture.Options.InstalledReleaseId);
        using var root = DataRootLease.Open(fixture.Storage); await using var database = SqliteStore.Open(root, BackendApplication.DatabaseMigrations);
        var backups = new DataBackupStore(database, root.Paths).Records();
        Assert.Equal(2, backups.Count);
        Assert.Equal(DataBackupPurpose.Automatic, backups.Single(item => item.Snapshot.SnapshotId == firstSnapshot).Purpose);
        Assert.False(backups.Single(item => item.Snapshot.SnapshotId == firstSnapshot).Retained);
        Assert.True(backups.Single(item => item.Snapshot.SnapshotId == completed.Transaction.Snapshot!.SnapshotId).Retained);
        await using var assets = new AssetStore(database, root.Paths);
        Assert.Equal("before-first", Encoding.UTF8.GetString(await assets.ReadRangeAsync(firstAsset, 0, 12, 256)));
    }

    [Theory]
    [InlineData("Prepared", "committed")]
    [InlineData("DataBackedUp", "committed")]
    [InlineData("VersionPublished", "committed")]
    [InlineData("PointerReplaced", "rolledBack")]
    [InlineData("PointerSwitched", "rolledBack")]
    [InlineData("Healthy", "committed")]
    [InlineData("Committed", "committed")]
    [InlineData("CompletionRecorded", "committed")]
    [InlineData("OperationRecorded", "committed")]
    [InlineData("ConfigurationRecorded", "committed")]
    [InlineData("CompletionIndexed", "committed")]
    [InlineData("Completed", "committed")]
    public async Task ForcedProcessTerminationRecoversRealInstallAndReleasesAllCandidateResources(string phase, string expected)
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        var asset = await SaveBusinessData(fixture, "kept-data"); await fixture.StageAsync("0.2.0");
        var transfer = await fixture.TransferIntentAsync(); var probe = fixture.StartProbe(phase);
        await WaitForMarker(fixture, probe); await probe.KillAsync();
        var result = await new UpdateInstallEngine(transfer.Intent, transfer.Hash, new(0, 1, 0)).RunAsync(true);
        Assert.True(expected == result.Outcome, $"expected={expected}, outcome={result.Outcome}, failure={result.FailureCode}, phase={result.Transaction.Phase}");
        await VerifyBusinessData(fixture, "kept-data", asset);
        await AssertTerminal(fixture, transfer.Intent, expected);
        var active = await InstallTransaction.ReadInstalledAsync(fixture.Installation);
        Assert.Equal(expected == "committed" ? "release-0.2.0" : "release-0.1.0", active.Version.ReleaseId);
        if (phase == "Healthy") Assert.Equal(2, result.Transaction.HealthChecks);
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("DataBackedUp")]
    public async Task CorruptedCandidateBeforeSwitchAbortsAndRetainsCompleteOldVersion(string phase)
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        var asset = await SaveBusinessData(fixture, "old-data"); await fixture.StageAsync("0.2.0");
        var transfer = await fixture.TransferIntentAsync();
        var result = await new UpdateInstallEngine(transfer.Intent, transfer.Hash, new(0, 1, 0), async point =>
        {
            if (point.ToString() == phase) await File.WriteAllTextAsync(Path.Combine(transfer.Intent.StageRoot, InstallTargetDescriptor.RelativePath), "corrupted");
        }).RunAsync(false);
        Assert.Equal("aborted", result.Outcome); await VerifyBusinessData(fixture, "old-data", asset);
        await AssertTerminal(fixture, transfer.Intent, "aborted");
        Assert.Equal("release-0.1.0", (await InstallTransaction.ReadInstalledAsync(fixture.Installation)).Version.ReleaseId);
    }

    [Theory]
    [InlineData("PreviousPointerRestored")]
    [InlineData("CompletionRecorded")]
    [InlineData("OperationRecorded")]
    [InlineData("ConfigurationRecorded")]
    [InlineData("CompletionIndexed")]
    [InlineData("Completed")]
    public async Task FailedCandidateRestoresDatabaseSettingsAndFixedRollbackCompletionAfterProcessTermination(string phase)
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        var asset = await SaveBusinessData(fixture, "old-settings"); await SaveEnvironmentConfiguration(fixture);
        await fixture.StageAsync("0.2.0"); var transfer = await fixture.TransferIntentAsync();
        var probe = fixture.StartProbe("rollback/" + phase); await WaitForMarker(fixture, probe); await probe.KillAsync();
        var result = await new UpdateInstallEngine(transfer.Intent, transfer.Hash, new(0, 1, 0)).RunAsync(true);
        Assert.True(result.Outcome == "rolledBack", $"outcome={result.Outcome}, failure={result.FailureCode}, phase={result.Transaction.Phase}");
        Assert.NotNull(result.PreviousHealth);
        await VerifyBusinessData(fixture, "old-settings", asset); await AssertTerminal(fixture, transfer.Intent, "rolledBack");
        using var root = DataRootLease.Open(fixture.Storage); await using var database = SqliteStore.Open(root, BackendApplication.DatabaseMigrations);
        Assert.True(new OperationStore(database).Configuration("environment", false).Enabled);
        Assert.True(File.Exists(root.Paths.Resolve("recovery", "database-" + transfer.Intent.TransactionId + ".sqlite")));
    }

    [Fact]
    public async Task CommittedRecoveryUsesNewUpdaterAndKeepsNewDataWhenOldHostIsDamaged()
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        await fixture.StageAsync("0.2.0"); var transfer = await fixture.TransferIntentAsync();
        var probe = fixture.StartProbe("Committed"); await WaitForMarker(fixture, probe); await probe.KillAsync();
        using (var root = DataRootLease.OpenForUpdate(fixture.Storage, transfer.Intent.TransactionId, transfer.Hash))
        {
            await using var database = SqliteStore.OpenUpdateControl(root, transfer.Intent.CandidateTarget.Migrations, "live");
            var music = new MusicLibraryStore(database, "test");
            await music.EditAsync("test-owner", new([new("after-commit", null, "new-business-data", "0")], [], [], [], null), "after-commit-data", MusicLibraryStore.ETag(music.Read().Revision));
        }
        await File.WriteAllTextAsync(Path.Combine(transfer.Intent.PreviousRoot, "components", "core", "MabiTool.Host.dll"), "damaged-old-host");
        Assert.Equal(transfer.Intent.CandidateRoot, await UpdateLaunchSupport.RecoveryUpdaterRootAsync(transfer.Intent));
        await UpdateLaunchSupport.RecoverAsync(fixture.Installation);
        await AssertTerminal(fixture, transfer.Intent, "committed");
        using var reopened = DataRootLease.Open(fixture.Storage); await using var store = SqliteStore.Open(reopened, BackendApplication.DatabaseMigrations);
        Assert.Equal("new-business-data", Assert.Single(new MusicLibraryStore(store, "test").Read().Folders).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicApplySharesOriginalOperationAndReconnectsBothClientsToNewEpoch(bool bootstrapUpdater)
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        await fixture.StageAsync("0.2.0", bootstrap: bootstrapUpdater); var host = fixture.StartHost();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var ct = timeout.Token;
        var bootstrap = await fixture.ConnectAsync(host, token: ct);
        using var owner = Client(bootstrap);
        var firstGrant = await Pair(owner, "first", ct); var secondGrant = await Pair(owner, "second", ct);
        using var first = new MabiToolClient(new(bootstrap.ApiBaseUri), _ => ValueTask.FromResult(firstGrant.Token));
        using var second = new MabiToolClient(new(bootstrap.ApiBaseUri), _ => ValueTask.FromResult(secondGrant.Token));
        string? etag = null; await first.GetUpdatePlanAsync(fixture.Plan.PlanId, ct, metadata => etag = metadata.ETag);
        var results = await Task.WhenAll(first.ApplyUpdatePlanAsync(fixture.Plan.PlanId, "first-public-apply", etag!, ct),
            second.ApplyUpdatePlanAsync(fixture.Plan.PlanId, "second-public-apply", etag!, ct));
        Assert.Equal(results[0].Id, results[1].Id);
        await host.Process.WaitForExitAsync(ct); Assert.Equal(0, host.Process.ExitCode);
        var next = await fixture.ConnectAsync(host, bootstrap.InstanceId, ct); Assert.NotEqual(bootstrap.InstanceId, next.InstanceId);
        using var staleFirst = new MabiToolClient(new(next.ApiBaseUri), _ => ValueTask.FromResult(firstGrant.Token));
        using var staleSecond = new MabiToolClient(new(next.ApiBaseUri), _ => ValueTask.FromResult(secondGrant.Token));
        Assert.Equal(401, (await Assert.ThrowsAsync<MabiToolApiException>(() => staleFirst.GetOperationAsync(results[0].Id, ct))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<MabiToolApiException>(() => staleSecond.GetOperationAsync(results[0].Id, ct))).Status);
        using var nextOwner = Client(next);
        var nextFirstGrant = await Pair(nextOwner, "first", ct); var nextSecondGrant = await Pair(nextOwner, "second", ct);
        using var firstReconnected = new MabiToolClient(new(next.ApiBaseUri), _ => ValueTask.FromResult(nextFirstGrant.Token));
        using var secondReconnected = new MabiToolClient(new(next.ApiBaseUri), _ => ValueTask.FromResult(nextSecondGrant.Token));
        var terminal = await firstReconnected.GetOperationAsync(results[0].Id, ct);
        Assert.Equal(OperationState.Succeeded, terminal.State.KnownValue);
        Assert.Equal("committed", terminal.Result!.Value.Deserialize<UpdateInstallResult>(ContractJson.Options)!.Outcome);
        Assert.Equal(JsonSerializer.Serialize(terminal, ContractJson.Options), JsonSerializer.Serialize(await secondReconnected.GetOperationAsync(terminal.Id, ct), ContractJson.Options));
        Assert.Equal(terminal.Id, (await firstReconnected.ApplyUpdatePlanAsync(fixture.Plan.PlanId, "first-public-apply", etag!, ct)).Id);
        Assert.Equal(terminal.Id, (await secondReconnected.ApplyUpdatePlanAsync(fixture.Plan.PlanId, "second-public-apply", etag!, ct)).Id);
        Assert.Equal("release-0.2.0", (await secondReconnected.GetUpdateStateAsync(ct)).InstalledReleaseId);
        var discovery = JsonSerializer.Deserialize<LocalDiscoveryInfo>(await File.ReadAllTextAsync(fixture.Discovery, ct), ContractJson.Options)!;
        Assert.Equal(next.InstanceId, discovery.InstanceId);
        using var processDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        processDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { while (!host.HasOnlyHostAndConsole(discovery.ProcessId)) await Task.Delay(100, processDeadline.Token); }
        catch (OperationCanceledException error) { throw new TimeoutException(host.DescribeActiveProcesses(), error); }
        var remaining = host.ActiveProcessList();
        Assert.Equal((uint)remaining.Count, host.ActiveProcesses);
        Assert.Single(remaining, item => item.Id == discovery.ProcessId);
        await fixture.ReloadOptionsAsync();
        Assert.Equal(bootstrapUpdater ? new Version(0, 2, 0) : new Version(0, 1, 0), fixture.Options.Runtime.UpdaterVersion);
    }

    private static MabiToolClient Client(BootstrapResponse value) => new(new(value.ApiBaseUri), _ => ValueTask.FromResult(value.Grant.Token));

    [Fact]
    public async Task OldUpdaterHandsSignedPlanToStableLauncherAndNewUpdaterCompletesOriginalOperation()
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        var asset = await SaveBusinessData(fixture, "bootstrap-data");
        await fixture.StageAsync("0.2.0", bootstrap: true); var transfer = await fixture.TransferIntentAsync();
        Assert.Equal(new Version(0, 2, 0), fixture.Plan.RequiredUpdaterVersion);
        var updater = fixture.StartUpdaterRecovery();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await updater.Process.WaitForExitAsync(timeout.Token);
        Assert.True(updater.Process.ExitCode == 0, updater.Process.ExitCode == 0 ? "" : await updater.Errors.WaitAsync(timeout.Token));
        await UpdaterBootstrap.WaitForCompletionAsync(transfer.Intent, transfer.Hash, timeout.Token);
        await AssertTerminal(fixture, transfer.Intent, "committed"); await VerifyBusinessData(fixture, "bootstrap-data", asset);
        await fixture.ReloadOptionsAsync(); Assert.Equal(new Version(0, 2, 0), fixture.Options.Runtime.UpdaterVersion);
        await FileTreePlanner.VerifyAsync(transfer.Intent.PreviousRoot, transfer.Intent.Previous.Files);
        Assert.Null(await UpdaterBootstrap.ReadAsync(transfer.Intent, transfer.Hash));
    }

    [Theory]
    [InlineData("staged", false)]
    [InlineData("activated", false)]
    [InlineData("staged", true)]
    public async Task TerminatedBootstrapResumesVerifiedNewUpdaterAndRepairsDamagedIndependentCopy(string phase, bool damaged)
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        var asset = await SaveBusinessData(fixture, "bootstrap-kept"); await fixture.StageAsync("0.2.0", bootstrap: true);
        var transfer = await fixture.TransferIntentAsync(); var probe = fixture.StartBootstrapProbe(phase);
        await WaitForMarker(fixture, probe); await probe.KillAsync();
        var record = (await UpdaterBootstrap.ReadAsync(transfer.Intent, transfer.Hash))!;
        Assert.Equal(phase, record.Phase);
        if (damaged) await File.WriteAllTextAsync(Path.Combine(UpdaterBootstrap.VersionRoot(transfer.Intent, record), "components", "updater", "MabiTool.Updater.dll"), "damaged-copy");
        try { await UpdateLaunchSupport.RecoverAsync(fixture.Installation); }
        catch (UpdatePlanException error)
        {
            var diagnostics = Path.Combine(fixture.State, UpdateFailureLog.FileName);
            throw new Xunit.Sdk.XunitException($"{error.Code}: {error.Data["updaterFailure"]}\n{(File.Exists(diagnostics) ? await File.ReadAllTextAsync(diagnostics) : "")}");
        }
        await AssertTerminal(fixture, transfer.Intent, "committed"); await VerifyBusinessData(fixture, "bootstrap-kept", asset);
        await fixture.ReloadOptionsAsync(); Assert.Equal(new Version(0, 2, 0), fixture.Options.Runtime.UpdaterVersion);
        if (damaged) Assert.Equal("damaged-copy", await File.ReadAllTextAsync(Path.Combine(UpdaterBootstrap.VersionRoot(transfer.Intent, record), "components", "updater", "MabiTool.Updater.dll")));
    }

    [Fact]
    public async Task UnavailableStableLauncherFailsOriginalOperationAndKeepsOldVersionUsable()
    {
        await using var fixture = new ManagedInstallFixture(images); await fixture.InitializeAsync();
        await fixture.StageAsync("0.2.0", bootstrap: true); var transfer = await fixture.TransferIntentAsync();
        await File.WriteAllTextAsync(Path.Combine(UpdaterBootstrap.StableRoot(transfer.Intent), "components", "launcher", "MabiTool.Launcher.dll"), "damaged-launcher");
        var updater = fixture.StartUpdaterRecovery(); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await updater.Process.WaitForExitAsync(timeout.Token); Assert.Equal(4, updater.Process.ExitCode);
        await AssertTerminal(fixture, transfer.Intent, "aborted");
        Assert.Equal("release-0.1.0", (await InstallTransaction.ReadInstalledAsync(fixture.Installation)).Version.ReleaseId);
        Assert.True(Directory.Exists(transfer.Intent.StageRoot));
    }
    private static async Task<AccessGrant> Pair(MabiToolClient owner, string name, CancellationToken ct)
    {
        var challenge = await owner.CreatePairingChallengeAsync(new(name, null, [ApiPermissions.SystemRead, ApiPermissions.UpdatesManage]), ct);
        return await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code), ct);
    }

    private static async Task WaitForMarker(ManagedInstallFixture fixture, ManagedInstallProcess probe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        while (!File.Exists(Path.Combine(fixture.Root, "pause")))
        {
            if (probe.Process.HasExited) throw new InvalidOperationException($"安装探针退出 {probe.Process.ExitCode}: {await probe.Output}\n{await probe.Errors}");
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task<string> SaveBusinessData(ManagedInstallFixture fixture, string value)
    {
        using var root = DataRootLease.Open(fixture.Storage); await using var database = SqliteStore.Open(root, BackendApplication.DatabaseMigrations);
        var music = new MusicLibraryStore(database, "test");
        await music.EditAsync("test-owner", new([new("kept-folder", null, value, "0")], [], [], [], null), "data-" + value, MusicLibraryStore.ETag(music.Read().Revision));
        await using var assets = new AssetStore(database, root.Paths); using var content = new MemoryStream(Encoding.UTF8.GetBytes(value));
        var asset = await assets.PutAsync("core", "managed-install", "text/plain", content);
        await File.WriteAllTextAsync(root.Paths.Resolve("settings", "install-value.json"), value); return asset.Id;
    }

    private static async Task VerifyBusinessData(ManagedInstallFixture fixture, string value, string assetId)
    {
        using var root = DataRootLease.Open(fixture.Storage); await using var database = SqliteStore.Open(root, BackendApplication.DatabaseMigrations);
        Assert.Equal(value, Assert.Single(new MusicLibraryStore(database, "test").Read().Folders).Name);
        Assert.Equal(value, await File.ReadAllTextAsync(root.Paths.Resolve("settings", "install-value.json")));
        await using var assets = new AssetStore(database, root.Paths);
        Assert.Equal(value, Encoding.UTF8.GetString(await assets.ReadRangeAsync(assetId, 0, Encoding.UTF8.GetByteCount(value), 256)));
    }

    private static async Task SaveEnvironmentConfiguration(ManagedInstallFixture fixture)
    {
        using var root = DataRootLease.Open(fixture.Storage); await using var database = SqliteStore.Open(root, BackendApplication.DatabaseMigrations);
        var operations = new OperationStore(database); var current = operations.Configuration("environment", false);
        var result = await operations.AdmitConfigurationAsync("test-owner", "environment", false, new(true), "environment-enabled", OperationStore.ConfigurationETag(current));
        await operations.BeginConfigurationAsync(result.Snapshot.Id); await operations.FinishConfigurationAsync(result.Snapshot.Id, true);
    }

    private static async Task AssertTerminal(ManagedInstallFixture fixture, UpdateInstallIntent intent, string outcome)
    {
        using var root = DataRootLease.Open(fixture.Storage); await using var database = SqliteStore.Open(root, BackendApplication.DatabaseMigrations);
        var operation = new OperationStore(database).Get(intent.OperationId, "test-owner", false);
        Assert.Equal(outcome == "committed" ? OperationState.Succeeded : OperationState.Failed, operation.State.KnownValue);
        Assert.Equal(outcome, operation.Result!.Value.Deserialize<UpdateInstallResult>(ContractJson.Options)!.Outcome);
        Assert.False(File.Exists(Path.Combine(fixture.State, "install-transaction.json")));
        Assert.False(File.Exists(intent.PathValue)); Assert.Null(UpdateInstallGate.Read(root.Paths.DataRoot));
        Assert.Equal(outcome, (await InstallTransaction.ReadCompletedAsync(fixture.Installation, intent.TransactionId)).Outcome);
    }
}
