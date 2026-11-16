using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ReleaseBuilder;
using MabiTool.Modules.MusicLibrary.Persistence;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class PackagedReleaseFactAttribute : FactAttribute
{
    public PackagedReleaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MABITOOL_PACKAGE_UPGRADE_INPUT")))
            Skip = "通过 scripts/build/package-upgrade.ps1 指定实际目录包后执行";
    }
}

public sealed class PackagedPublicApplyFactAttribute : FactAttribute
{
    public PackagedPublicApplyFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MABITOOL_PACKAGE_PUBLIC_APPLY_REPORT")))
            Skip = "通过 scripts/build/package-upgrade.ps1 -PublicApply 指定冻结旧包和候选包后执行";
    }
}

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class PackagedReleaseInstallTests
{
    [Fact]
    public void IncompleteBundledRuntimeRejectsMachineRuntimeFallback()
    {
        var root = Path.Combine(ManagedInstallImages.Repository, "artifacts", "tmp", "incomplete-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "components", "core"));
            Directory.CreateDirectory(Path.Combine(root, "components", "dotnet-runtime"));
            File.WriteAllBytes(Path.Combine(root, "components", "core", "MabiTool.Host.dll"), []);
            var error = Assert.Throws<UpdatePlanException>(() => UpdateProcess.Start(root, "core", "MabiTool.Host", "framework-dependent", []));
            Assert.Equal("update.updaterUnavailable", error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PackagedStageFollowsInstalledOptionalComponentsAndRejectsMissingFiles(bool optionalInstalled, bool omitOptional)
    {
        using var package = new MabiTool.Updates.Tests.SignedReleaseFixture();
        var baseline = package.Path("baseline");
        var required = new[] { "core", "dotnet-runtime", "launcher", "updater" };
        var installed = optionalInstalled ? required.Append("color-engine").ToArray() : required;
        foreach (var id in installed)
        {
            var directory = Path.Combine(baseline, "components", id);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "installed.txt"), id);
        }
        var target = new InstallTargetDescriptor(1, "mabitool", "0.1.0", "1.0", 1, "framework-dependent",
            InstallTargetDescriptor.Entry("framework-dependent"), Enumerable.Range(1, 4)
                .Select(version => new DatabaseMigrationStamp("core", version, new string('a', 64))).ToArray(), [new("core", "0.1.0")]);
        await File.WriteAllTextAsync(Path.Combine(baseline, InstallTargetDescriptor.RelativePath), JsonSerializer.Serialize(target, UpdateHandoffProtocol.Json));
        await File.WriteAllTextAsync(Path.Combine(baseline, "release-input.json"), JsonSerializer.Serialize(new
        {
            version = "0.1.0", productId = "mabitool", releaseId = "baseline", releaseSequence = 1, channel = "stable",
            components = installed.Select(id => new { componentId = id, version = "0.1.0" })
        }));
        var images = new ManagedInstallImages(new Dictionary<string, string> { ["0.1.0"] = baseline });
        await using var fixture = new ManagedInstallFixture(images);
        await fixture.InitializeAsync();
        var payload = fixture.Signing.Payload();
        var platform = payload["platforms"]![0]!;
        var template = platform["components"]![0]!;
        var components = new JsonArray();
        foreach (var id in required.Concat(["color-engine", "observation-worker"]))
        {
            var component = template.DeepClone();
            component["componentId"] = id; component["version"] = "0.2.0";
            component["optional"] = !required.Contains(id, StringComparer.Ordinal);
            foreach (var file in component["files"]!.AsArray()) file!["componentId"] = id;
            components.Add(component);
        }
        platform["components"] = components; platform["runtimeMode"] = "framework-dependent";
        payload["releaseId"] = "candidate"; payload["version"] = "0.2.0";
        payload["releaseSequence"] = 2; payload["publishedAt"] = JsonValue.Create(DateTimeOffset.UtcNow); payload["data"] = new JsonArray();
        var plan = await fixture.PlanPackagedReleaseAsync(fixture.Signing.Release(payload), 2);
        Assert.Equal(installed.OrderBy(id => id, StringComparer.Ordinal), plan.Components.Select(item => item.ComponentId));
        Assert.DoesNotContain(plan.Components, item => item.ComponentId == "observation-worker");
        var stage = Path.Combine(fixture.Root, "candidate");
        Directory.CreateDirectory(stage);
        foreach (var component in plan.Components.Where(item => !omitOptional || !item.IsOptional))
            ManagedInstallImages.CopyTree(fixture.Signing.Path("release"), Path.Combine(stage, "components", component.ComponentId));
        if (omitOptional)
            Assert.Equal("update.targetTreeMismatch", (await Assert.ThrowsAsync<FileTreeException>(() => fixture.AcceptPackagedStageAsync(stage))).Code);
        else await fixture.AcceptPackagedStageAsync(stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackagedDatabaseInitializationPreservesImageMigrationHistory(bool includeShortcuts)
    {
        using var package = new MabiTool.Updates.Tests.SignedReleaseFixture();
        var target = BackendApplication.InstallationTarget("framework-dependent");
        if (!includeShortcuts)
            target = target with { Migrations = target.Migrations.Where(item => item.ModuleId != "platform-shortcuts").ToArray() };
        var images = await MigrationImagesAsync(package, target);
        await using var fixture = new ManagedInstallFixture(images, usePackageMigrationHistory: true);
        await fixture.InitializeAsync();
        using var root = DataRootLease.Open(fixture.Storage);
        await using var database = SqliteStore.OpenUpdateControl(root, target.Migrations, "live");
        Assert.Equal(includeShortcuts ? 1L : 0L, database.ReadScalar(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='platform_shortcut_settings'"));
    }

    [Theory]
    [InlineData("core")]
    [InlineData("music-library")]
    [InlineData("unknown-module")]
    public async Task PackagedDatabaseInitializationRejectsUnsupportedMigrationHistory(string module)
    {
        using var package = new MabiTool.Updates.Tests.SignedReleaseFixture();
        var target = BackendApplication.InstallationTarget("framework-dependent");
        var migrations = target.Migrations.ToList();
        var existing = migrations.FindIndex(item => item.ModuleId == module);
        if (existing < 0) migrations.Add(new(module, 1, new string('f', 64)));
        else migrations[existing] = migrations[existing] with { Checksum = new string('f', 64) };
        target = target with { Migrations = migrations.OrderBy(item => item.ModuleId, StringComparer.Ordinal).ThenBy(item => item.Version).ToArray() };
        var images = await MigrationImagesAsync(package, target);
        await using var fixture = new ManagedInstallFixture(images, usePackageMigrationHistory: true);
        await Assert.ThrowsAsync<Xunit.Sdk.EqualException>(() => fixture.InitializeAsync());
        Assert.False(Directory.Exists(fixture.Storage.DataRoot));
    }

    private static async Task<ManagedInstallImages> MigrationImagesAsync(MabiTool.Updates.Tests.SignedReleaseFixture package,
        InstallTargetDescriptor target)
    {
        var baseline = package.Path("baseline");
        var components = new[] { "core", "updater", "launcher" };
        foreach (var id in components)
        {
            var directory = Path.Combine(baseline, "components", id);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "installed.txt"), id);
        }
        await File.WriteAllTextAsync(Path.Combine(baseline, InstallTargetDescriptor.RelativePath), JsonSerializer.Serialize(target, UpdateHandoffProtocol.Json));
        await File.WriteAllTextAsync(Path.Combine(baseline, "release-input.json"), JsonSerializer.Serialize(new
        {
            version = target.HostVersion, productId = "mabitool", releaseId = "baseline", releaseSequence = 1, channel = "stable",
            components = components.Select(id => new { componentId = id, version = target.HostVersion })
        }));
        return new(new Dictionary<string, string> { [target.HostVersion] = baseline }, target.HostVersion);
    }

    [Fact]
    public async Task InstallFailureEvidenceReadsUpdateStateWithoutChangingDatabaseOrExportingPrivateFields()
    {
        using var package = new MabiTool.Updates.Tests.SignedReleaseFixture();
        using var root = DataRootLease.Open(new(package.Path("bootstrap"), true, package.Path("data")));
        await using var database = SqliteStore.Open(root, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var accepted = await operations.AdmitUpdateAsync("private-evidence-owner", UpdateAction.Check, null, "evidence-check-key");
        await operations.BeginUpdateAsync(accepted.Snapshot.Id);
        await operations.FinishUpdateAsync(accepted.Snapshot.Id,
            JsonSerializer.SerializeToElement(new { phase = "probe", token = "private-evidence-token" }));
        var before = database.ReadScalar("SELECT snapshot FROM core_operations WHERE id=$id", ("$id", accepted.Snapshot.Id));
        var count = database.ReadScalar("SELECT count(*) FROM core_operations");
        var logs = Path.Combine(root.Paths.DataRoot, "logs", "backend");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "log-" + Guid.NewGuid().ToString("N") + ".jsonl"),
            "{\"level\":\"error\",\"module\":\"core\",\"code\":\"storage.unavailable\",\"exceptionType\":\"InvalidOperationException\",\"message\":\"private-evidence-message\"}\n");
        var evidence = package.Path("evidence");
        Assert.True(await ManagedInstallEvidence.TrySaveAsync(package.Path("installation"), evidence, "probe", dataRoot: root.Paths.DataRoot));
        var text = await File.ReadAllTextAsync(Path.Combine(evidence, "probe.install-state.json"));
        using var document = JsonDocument.Parse(text);
        var saved = document.RootElement.GetProperty("database");
        Assert.Equal("present", saved.GetProperty("status").GetString());
        var operation = Assert.Single(saved.GetProperty("operations").EnumerateArray());
        Assert.Equal(accepted.Snapshot.Id, operation.GetProperty("id").GetString());
        Assert.Equal("succeeded", operation.GetProperty("state").GetString());
        Assert.Equal("completed", operation.GetProperty("phase").GetString());
        Assert.Equal("probe", operation.GetProperty("result").GetProperty("phase").GetString());
        Assert.Equal("InvalidOperationException", Assert.Single(document.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("private-evidence-", text, StringComparison.Ordinal);
        Assert.Equal(before, database.ReadScalar("SELECT snapshot FROM core_operations WHERE id=$id", ("$id", accepted.Snapshot.Id)));
        Assert.Equal(count, database.ReadScalar("SELECT count(*) FROM core_operations"));
    }

    [Trait("Category", "Slow")]
    [PackagedReleaseFact]
    public async Task FrozenPackagedHostStartsWithRecordedMigrationHistory()
    {
        var input = Environment.GetEnvironmentVariable("MABITOOL_PACKAGE_UPGRADE_INPUT")!;
        var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(input))!;
        var package = paths.TryGetValue("baseline", out var selected) ? selected : paths["0.1.0"];
        var baseline = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(package, "release-input.json"));
        var images = new ManagedInstallImages(new Dictionary<string, string> { [baseline.Version] = package }, baseline.Version);
        await using var fixture = new ManagedInstallFixture(images, usePackageMigrationHistory: true);
        await fixture.InitializeAsync();
        await SaveBusinessDataAsync(fixture);
        var host = fixture.StartHost();
        var bootstrap = await fixture.ConnectAsync(host);
        using var client = new MabiToolClient(new(bootstrap.ApiBaseUri), _ => ValueTask.FromResult(bootstrap.Grant.Token));
        Assert.Equal(baseline.ReleaseId, (await client.GetUpdateStateAsync()).InstalledReleaseId);
        await host.KillAsync();
        await VerifyBusinessDataAsync(fixture);
    }

    [Trait("Category", "Slow")]
    [PackagedReleaseFact]
    public async Task SignedPackagedReleasesUpgradeWithMatchingDeltaAndFullTrees()
    {
        var inputPath = Environment.GetEnvironmentVariable("MABITOOL_PACKAGE_UPGRADE_INPUT")!;
        var (paths, baselineVersion, upgradeVersions) = await ReadPackageInputsAsync(inputPath);
        var baselineInput = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(paths[baselineVersion], "release-input.json"));
        var images = new ManagedInstallImages(paths, baselineVersion);
        await using var fixture = new ManagedInstallFixture(images, usePackageMigrationHistory: true);
        fixture.EvidenceDirectory = Path.GetDirectoryName(inputPath)!;
        fixture.EvidencePrefix = "normal-upgrade";
        await fixture.InitializeAsync();
        await VerifyBaselineRuntimeAsync(fixture, baselineInput);
        await SaveBusinessDataAsync(fixture);
        using (var root = DataRootLease.Open(fixture.Storage))
            await File.WriteAllTextAsync(root.Paths.Resolve("settings", "packaged-upgrade-kept.txt"), "before-upgrades");
        var measurements = new List<object>();
        foreach (var version in upgradeVersions)
        {
            var package = paths[version];
            var input = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(package, "release-input.json"));
            var release = Path.Combine(fixture.Root, "signed-" + version);
            var builder = new ReleaseBuilderService();
            await builder.BuildAsync(input, package, release, [new("release", fixture.Signing.ReleaseSigner)], fixture.Signing.Root);
            var verified = await builder.VerifyAsync(release, fixture.Signing.Root);
            var plan = await fixture.PlanPackagedReleaseAsync(await File.ReadAllBytesAsync(Path.Combine(release, ReleaseBuilderService.ManifestName)), input.ReleaseSequence);
            var current = Path.Combine(fixture.Installation, "versions", fixture.Options.InstalledReleaseId!);
            var delta = Path.Combine(fixture.Root, "delta-" + version);
            var full = Path.Combine(fixture.Root, "full-" + version);
            Directory.CreateDirectory(delta); Directory.CreateDirectory(full);
            using var deltaHandler = new PackageAssets(release);
            using var fullHandler = new PackageAssets(release);
            using var deltaDownloader = new ResumableAssetDownloader(deltaHandler);
            using var fullDownloader = new ResumableAssetDownloader(fullHandler);
            long fullBytes = 0; var reused = 0;
            foreach (var selected in plan.Components)
            {
                var component = verified.Manifest.Components[selected.ComponentId];
                var id = component.Metadata.ComponentId;
                var artifacts = component.Artifacts;
                var staged = await new FileTreeStager(deltaDownloader, new()).StageAsync(Path.Combine(current, "components", id),
                    Path.Combine(delta, "components", id), Path.Combine(fixture.Root, "cache-delta-" + version + "-" + id), artifacts,
                    file => Sources(file.DownloadSha256));
                reused += staged.ReusedFiles;
                await new FileTreeStager(fullDownloader, new()).StagePackageAsync(Path.Combine(fixture.Root, "absent"),
                    Path.Combine(full, "components", id), Path.Combine(fixture.Root, "cache-full-" + version + "-" + id), artifacts,
                    asset => Sources(asset.DownloadSha256));
                File.Delete(Path.Combine(delta, "components", id) + ".lock");
                File.Delete(Path.Combine(full, "components", id) + ".lock");
                fullBytes += artifacts.FullPackage!.DownloadLength;
                await FileTreePlanner.VerifyAsync(Path.Combine(full, "components", id), artifacts.Files.Select(file => file.File).ToArray());
            }
            Assert.True(reused > 0);
            Assert.True(deltaHandler.Bytes < fullBytes);
            Assert.Equal(fullBytes, fullHandler.Bytes);
            await fixture.AcceptPackagedStageAsync(delta);
            Assert.Equal(plan.PlanId, fixture.Plan.PlanId);
            var transfer = await fixture.TransferIntentAsync();
            using (var runtimeProbe = UpdateProcess.Start(transfer.Intent.StageRoot, "core", "MabiTool.Host", "framework-dependent",
                ["--print-install-target", "framework-dependent"], captureOutput: true))
            {
                var output = runtimeProbe.StandardOutput.ReadToEndAsync();
                var errors = runtimeProbe.StandardError.ReadToEndAsync();
                Assert.Equal(Path.Combine(transfer.Intent.StageRoot, "components", "dotnet-runtime", "dotnet.exe"), runtimeProbe.StartInfo.FileName, StringComparer.OrdinalIgnoreCase);
                await runtimeProbe.WaitForExitAsync();
                Assert.True(runtimeProbe.ExitCode == 0, await errors);
                Assert.Equal(version, JsonSerializer.Deserialize<InstallTargetDescriptor>(await output, UpdateHandoffProtocol.Json)!.HostVersion);
            }
            var stopwatch = Stopwatch.StartNew();
            using var sampling = new CancellationTokenSource();
            var memory = Task.Run(() => SampleInstallationMemoryAsync(fixture.Installation, sampling.Token));
            var testUpdaterVersion = plan.RequiredUpdaterVersion is null ? fixture.Options.Runtime.UpdaterVersion
                : plan.Components.Single(component => component.ComponentId == "updater").Version;
            CompletedInstallRecord result;
            try { result = await new UpdateInstallEngine(transfer.Intent, transfer.Hash, testUpdaterVersion).RunAsync(false); }
            finally { sampling.Cancel(); }
            var installationPeak = await memory;
            var handoffDiagnostics = await ExportHandoffDiagnosticsAsync(fixture.State, Path.GetDirectoryName(inputPath)!, version);
            var failures = Path.Combine(fixture.State, UpdateFailureLog.FileName);
            var diagnostics = File.Exists(failures) ? await File.ReadAllTextAsync(failures) : "";
            if (result.Outcome != "committed")
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(inputPath)!, version + ".install-failures.json"), diagnostics);
            Assert.True(result.Outcome == "committed", $"version={version}, outcome={result.Outcome}, failure={result.FailureCode}, phase={result.Transaction.Phase}, diagnostics={diagnostics}");
            Assert.True(installationPeak > 0, "没有观测到安装目录内的私有运行时候选进程");
            await fixture.ReloadOptionsAsync();
            Assert.Equal(input.ReleaseId, fixture.Options.InstalledReleaseId);
            await VerifyBusinessDataAsync(fixture);
            using (var root = DataRootLease.Open(fixture.Storage))
                Assert.Equal("before-upgrades", await File.ReadAllTextAsync(root.Paths.Resolve("settings", "packaged-upgrade-kept.txt")));
            measurements.Add(new { version, manifestSha256 = verified.Receipt.Manifest.Sha256, deltaBytes = deltaHandler.Bytes, fullBytes, savedBytes = fullBytes - deltaHandler.Bytes,
                reusedFiles = reused, upgradeMilliseconds = stopwatch.ElapsedMilliseconds, testProcessPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                executionEntry = "current-test-engine", testUpdaterVersion = testUpdaterVersion.ToString(),
                handoffDiagnostics, transactionId = result.Transaction.TransactionId,
                selectedComponents = plan.Components.Select(component => component.ComponentId).ToArray(),
                retainedOptionalComponents = plan.Components.Where(component => component.IsOptional).Select(component => component.ComponentId).ToArray() });
            measurements.Add(new { version, installationProcessTreePeakWorkingSetBytes = installationPeak, sampleIntervalMilliseconds = 50 });
        }
        var report = Environment.GetEnvironmentVariable("MABITOOL_PACKAGE_UPGRADE_REPORT")!;
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            osVersion = Environment.OSVersion.VersionString,
            testRuntime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            inputPackages = paths,
            inputReleases = paths.Select(item => PackageIdentity(item.Value)).ToArray(),
            baselineReleaseId = baselineInput.ReleaseId,
            baselineVersion,
            upgradeVersions,
            installationLayout = "versions/<releaseId>/components; launcher/stable; launcher/active-version.json; update-state",
            requestedOptionalComponents = Array.Empty<string>(),
            measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Trait("Category", "Slow")]
    [PackagedReleaseFact]
    public async Task SignedPackagedReleaseHealthFailureRestoresPreviousProgramAndData()
    {
        var inputPath = Environment.GetEnvironmentVariable("MABITOOL_PACKAGE_UPGRADE_INPUT")!;
        var (paths, baselineVersion, upgradeVersions) = await ReadPackageInputsAsync(inputPath);
        var baselineInput = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(paths[baselineVersion], "release-input.json"));
        var images = new ManagedInstallImages(paths, baselineVersion);
        await using var rollback = new ManagedInstallFixture(images, usePackageMigrationHistory: true);
        rollback.EvidenceDirectory = Path.GetDirectoryName(inputPath)!;
        rollback.EvidencePrefix = "health-failure";
        await rollback.InitializeAsync();
        await SaveBusinessDataAsync(rollback);
        using (var root = DataRootLease.Open(rollback.Storage))
            await File.WriteAllTextAsync(root.Paths.Resolve("settings", "packaged-kept.txt"), "kept");
        var signed = Path.Combine(rollback.Root, "rollback-release");
        var rollbackPackage = paths[upgradeVersions[0]];
        var secondInput = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(rollbackPackage, "release-input.json"));
        await new ReleaseBuilderService().BuildAsync(secondInput, rollbackPackage, signed, [new("release", rollback.Signing.ReleaseSigner)], rollback.Signing.Root);
        var rollbackPlan = await rollback.PlanPackagedReleaseAsync(await File.ReadAllBytesAsync(Path.Combine(signed, ReleaseBuilderService.ManifestName)), secondInput.ReleaseSequence);
        var candidate = Path.Combine(rollback.Root, "rollback-stage");
        foreach (var component in rollbackPlan.Components)
            ManagedInstallImages.CopyTree(Path.Combine(rollbackPackage, "components", component.ComponentId), Path.Combine(candidate, "components", component.ComponentId));
        await rollback.AcceptPackagedStageAsync(candidate);
        var failed = await rollback.TransferIntentAsync();
        var rollbackUpdaterVersion = rollbackPlan.RequiredUpdaterVersion is null ? rollback.Options.Runtime.UpdaterVersion
            : rollbackPlan.Components.Single(component => component.ComponentId == "updater").Version;
        var watch = Stopwatch.StartNew();
        var faultInjected = false;
        var recovered = await new UpdateInstallEngine(failed.Intent, failed.Hash, rollbackUpdaterVersion, async point =>
        {
            if (point == InstallPersistencePoint.RollbackStarted) { watch.Restart(); return; }
            if (point != InstallPersistencePoint.PointerSwitched) return;
            using var root = DataRootLease.OpenForUpdate(failed.Intent.Storage, failed.Intent.TransactionId, failed.Hash);
            await using var database = SqliteStore.OpenUpdateControl(root, failed.Intent.PreviousMigrations, "live");
            await database.WriteAsync(tx => { tx.Execute("UPDATE core_migrations SET checksum=$checksum WHERE module_id='music-library' AND version=1", ("$checksum", new string('f', 64))); return 0; });
            await File.WriteAllTextAsync(root.Paths.Resolve("settings", "packaged-kept.txt"), "changed");
            faultInjected = true;
        }).RunAsync(false);
        var rollbackHandoffDiagnostics = await ExportHandoffDiagnosticsAsync(rollback.State, Path.GetDirectoryName(inputPath)!, secondInput.Version + ".rollback");
        Assert.True(faultInjected, "未执行候选指针切换后的健康失败注入");
        Assert.Equal("rolledBack", recovered.Outcome); Assert.NotNull(recovered.PreviousHealth);
        await VerifyBusinessDataAsync(rollback);
        Assert.Equal(baselineInput.ReleaseId, (await InstallTransaction.ReadInstalledAsync(rollback.Installation)).Version.ReleaseId);
        using (var root = DataRootLease.Open(rollback.Storage))
            Assert.Equal("kept", await File.ReadAllTextAsync(root.Paths.Resolve("settings", "packaged-kept.txt")));
        var report = Path.Combine(Path.GetDirectoryName(inputPath)!, "package-rollback-metrics.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            outcome = recovered.Outcome,
            executionEntry = "current-test-engine",
            osVersion = Environment.OSVersion.VersionString,
            testRuntime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            inputPackages = paths,
            inputReleases = paths.Select(item => PackageIdentity(item.Value)).ToArray(),
            baselineReleaseId = baselineInput.ReleaseId,
            baselineVersion,
            candidateVersion = secondInput.Version,
            candidateReleaseId = secondInput.ReleaseId,
            testUpdaterVersion = rollbackUpdaterVersion.ToString(),
            installationLayout = "versions/<releaseId>/components; launcher/stable; launcher/active-version.json; update-state",
            faultInjected,
            businessDataPreserved = true,
            settingsRestored = true,
            previousHostHealthy = true,
            rollbackTimingScope = "RollbackStarted through data restoration, previous-host health and completion",
            rollbackMilliseconds = watch.ElapsedMilliseconds,
            recovered.FailureCode,
            handoffDiagnostics = rollbackHandoffDiagnostics,
            transactionId = recovered.Transaction.TransactionId
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<(Dictionary<string, string> Paths, string BaselineVersion, string[] UpgradeVersions)> ReadPackageInputsAsync(string inputPath)
    {
        var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(inputPath))!;
        if (!paths.ContainsKey("baseline")) return (paths, "0.1.0", ["0.2.0", "0.3.0"]);
        Assert.Equal(2, paths.Count);
        var baselinePath = paths["baseline"];
        var candidatePath = paths["candidate"];
        var baseline = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(baselinePath, "release-input.json"));
        var candidate = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(candidatePath, "release-input.json"));
        Assert.True(Version.Parse(candidate.Version) > Version.Parse(baseline.Version));
        Assert.True(candidate.ReleaseSequence > baseline.ReleaseSequence);
        Assert.NotEqual(baseline.ReleaseId, candidate.ReleaseId);
        Assert.Equal(baseline.ProductId, candidate.ProductId);
        Assert.Equal(baseline.Channel, candidate.Channel);
        return (new() { [baseline.Version] = baselinePath, [candidate.Version] = candidatePath }, baseline.Version, [candidate.Version]);
    }

    internal static async Task<string?> ExportHandoffDiagnosticsAsync(string stateRoot, string evidenceDirectory, string prefix)
    {
        var sourcePath = Path.Combine(stateRoot, UpdateFailureLog.HandoffFileName);
        if (!File.Exists(sourcePath)) return null;
        if (Path.GetFileName(prefix) != prefix) throw new ArgumentException("Invalid evidence prefix", nameof(prefix));
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > UpdateFailureLog.MaximumBytes) throw new InvalidDataException("Handoff diagnostics exceed the byte limit");
        var bytes = new byte[(int)source.Length];
        await source.ReadExactlyAsync(bytes);
        if (source.ReadByte() != -1) throw new InvalidDataException("Handoff diagnostics exceed the fixed length");
        var name = prefix + ".install-handoffs.json";
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(Path.Combine(evidenceDirectory, name), bytes);
        return name;
    }

    private static AssetDownloadSource[] Sources(string hash) => [new("actual-package", new Uri("https://package.invalid/" + hash + ".blob"))];

    private static object PackageIdentity(string package)
    {
        var inputPath = Path.Combine(package, "release-input.json");
        var input = ReleaseJson.Read<ReleaseBuildInput>(inputPath);
        var sourcesPath = Path.Combine(package, "components", "core", "source-inventory.json");
        var sources = new List<object>();
        string? sourceInventorySha256 = null;
        if (File.Exists(sourcesPath))
        {
            var bytes = File.ReadAllBytes(sourcesPath);
            sourceInventorySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var document = JsonDocument.Parse(bytes);
            foreach (var source in document.RootElement.GetProperty("sources").EnumerateArray())
                sources.Add(new { id = source.GetProperty("id").GetString(), repository = source.GetProperty("repository").GetString(),
                    commit = source.GetProperty("commit").GetString(),
                    workingTree = source.TryGetProperty("workingTree", out var tree) ? (JsonElement?)tree.Clone() : null });
        }
        return new { packagePath = package, version = input.Version, releaseId = input.ReleaseId, releaseSequence = input.ReleaseSequence,
            releaseInputSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(inputPath))), sourceInventorySha256, sourceRefs = sources };
    }

    private static async Task VerifyBaselineRuntimeAsync(ManagedInstallFixture fixture, ReleaseBuildInput input)
    {
        var versionRoot = Path.Combine(fixture.Installation, "versions", fixture.Options.InstalledReleaseId!);
        foreach (var component in new[] { "core", "updater", "launcher" })
        {
            var root = versionRoot;
            var assembly = component == "core" ? "MabiTool.Host" : component == "updater" ? "MabiTool.Updater" : "MabiTool.Launcher";
            string[] arguments = component == "core" ? ["--print-install-target", "framework-dependent"] :
                component == "updater" ? ["--version"] : ["--recover", fixture.Installation];
            using var probe = UpdateProcess.Start(root, component, assembly, "framework-dependent", arguments, captureOutput: true);
            var output = UpdateProcess.ReadBoundedAsync(probe.StandardOutput);
            var errors = UpdateProcess.ReadBoundedAsync(probe.StandardError);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await probe.WaitForExitAsync(timeout.Token); }
            finally { UpdateProcess.Terminate(probe); await probe.WaitForExitAsync(); }
            Assert.Equal(Path.Combine(root, "components", "dotnet-runtime", "dotnet.exe"), probe.StartInfo.FileName, StringComparer.OrdinalIgnoreCase);
            Assert.True(probe.ExitCode == 0, await errors);
            var text = (await output).Trim();
            if (component == "core") Assert.Equal(input.Version, JsonSerializer.Deserialize<InstallTargetDescriptor>(text, UpdateHandoffProtocol.Json)!.HostVersion);
            else if (component == "updater") Assert.Equal(input.Components.Single(item => item.ComponentId == "updater").Version, text);
            else Assert.Equal("recovered", text);
        }
    }
    private static async Task SaveBusinessDataAsync(ManagedInstallFixture fixture)
    {
        using var root = DataRootLease.Open(fixture.Storage);
        await using var database = SqliteStore.Open(root, fixture.DatabaseMigrations);
        var music = new MusicLibraryStore(database, "test");
        await music.EditAsync("package-test", new([new("kept-folder", null, "packaged-business-data", "0")], [], [], [], null),
            "packaged-data", MusicLibraryStore.ETag(music.Read().Revision));
    }
    private static async Task VerifyBusinessDataAsync(ManagedInstallFixture fixture)
    {
        using var root = DataRootLease.Open(fixture.Storage);
        await using var database = SqliteStore.Open(root, fixture.DatabaseMigrations);
        Assert.Equal("packaged-business-data", Assert.Single(new MusicLibraryStore(database, "test").Read().Folders).Name);
    }
    private static async Task<long> SampleInstallationMemoryAsync(string installation, CancellationToken token)
    {
        long peak = 0;
        while (!token.IsCancellationRequested)
        {
            long total = 0;
            foreach (var process in Process.GetProcessesByName("dotnet"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName.StartsWith(installation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true)
                            total += process.WorkingSet64;
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            peak = Math.Max(peak, total);
            try { await Task.Delay(50, token); } catch (OperationCanceledException) { }
        }
        return peak;
    }
    private sealed class PackageAssets(string release) : HttpMessageHandler
    {
        internal long Bytes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = Path.Combine(release, "assets", request.RequestUri!.Segments[^1]);
            var stream = File.OpenRead(path); Bytes += stream.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }
}

[Trait("Category", "Slow")]
[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class PackagedUpdaterBootstrapTests
{
    [PackagedPublicApplyFact]
    public async Task FrozenHostPublicApplyBootstrapsUpdaterAndBothClientsReadOriginalCompletion()
    {
        var inputPath = Environment.GetEnvironmentVariable("MABITOOL_PACKAGE_UPGRADE_INPUT")!;
        var reportPath = Environment.GetEnvironmentVariable("MABITOOL_PACKAGE_PUBLIC_APPLY_REPORT")!;
        var evidence = Path.GetDirectoryName(inputPath)!;
        var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(inputPath))!;
        Assert.Equal(new[] { "baseline", "candidate" }, paths.Keys.OrderBy(key => key, StringComparer.Ordinal));
        var baseline = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(paths["baseline"], "release-input.json"));
        var candidate = ReleaseJson.Read<ReleaseBuildInput>(Path.Combine(paths["candidate"], "release-input.json"));
        var images = new ManagedInstallImages(new Dictionary<string, string>
        { [baseline.Version] = paths["baseline"], [candidate.Version] = paths["candidate"] }, baseline.Version);
        await using var fixture = new ManagedInstallFixture(images, usePackageMigrationHistory: true);
        fixture.EvidenceDirectory = evidence;
        fixture.EvidencePrefix = "public-apply";
        await fixture.InitializeAsync();
        await SaveBusinessDataAsync(fixture);
        var previousUpdater = fixture.Options.Runtime.UpdaterVersion;
        var signed = Path.Combine(fixture.Root, "signed-public-apply");
        var builder = new ReleaseBuilderService();
        await builder.BuildAsync(candidate, paths["candidate"], signed,
            [new("release", fixture.Signing.ReleaseSigner)], fixture.Signing.Root);
        var verified = await builder.VerifyAsync(signed, fixture.Signing.Root);
        var plan = await fixture.PlanPackagedReleaseAsync(
            await File.ReadAllBytesAsync(Path.Combine(signed, ReleaseBuilderService.ManifestName)), candidate.ReleaseSequence);
        Assert.NotNull(plan.RequiredUpdaterVersion);
        Assert.True(plan.RequiredUpdaterVersion > previousUpdater);
        var expectedUpdater = plan.Components.Single(component => component.ComponentId == "updater").Version;
        Assert.True(expectedUpdater >= plan.RequiredUpdaterVersion);
        var files = UpdateSignedPlanVerifier.TargetFiles(plan, verified.Manifest);
        var stage = Path.Combine(fixture.Root, "public-stage");
        foreach (var component in plan.Components)
            ManagedInstallImages.CopyTree(Path.Combine(paths["candidate"], "components", component.ComponentId),
                Path.Combine(stage, "components", component.ComponentId));
        await fixture.AcceptPackagedStageAsync(stage);
        var stableRoot = Path.Combine(fixture.Installation, "launcher", "stable");
        await FileTreePlanner.VerifyAsync(stableRoot,
            files.Where(file => file.RelativePath.StartsWith("components/launcher/", StringComparison.Ordinal)).ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = timeout.Token;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var host = fixture.StartHost();
            var oldRuntime = Path.Combine(fixture.Installation, "versions", baseline.ReleaseId, "components", "dotnet-runtime", "dotnet.exe");
            Assert.Equal(oldRuntime, host.Process.StartInfo.FileName, StringComparer.OrdinalIgnoreCase);
            var bootstrap = await fixture.ConnectAsync(host, token: ct);
            using var owner = Client(bootstrap);
            var firstGrant = await PairAsync(owner, "first", ct);
            var secondGrant = await PairAsync(owner, "second", ct);
            using var first = Client(bootstrap.ApiBaseUri, firstGrant);
            using var second = Client(bootstrap.ApiBaseUri, secondGrant);
            string? etag = null;
            await first.GetUpdatePlanAsync(plan.PlanId, ct, metadata => etag = metadata.ETag);
            var accepted = await Task.WhenAll(
                first.ApplyUpdatePlanAsync(plan.PlanId, "frozen-first-apply", etag!, ct),
                second.ApplyUpdatePlanAsync(plan.PlanId, "frozen-second-apply", etag!, ct));
            Assert.Equal(accepted[0].Id, accepted[1].Id);
            await host.Process.WaitForExitAsync(ct);
            Assert.Equal(0, host.Process.ExitCode);
            var next = await fixture.ConnectAsync(host, bootstrap.InstanceId, ct, TimeSpan.FromMinutes(12));
            Assert.NotEqual(bootstrap.InstanceId, next.InstanceId);
            using var staleFirst = Client(next.ApiBaseUri, firstGrant);
            using var staleSecond = Client(next.ApiBaseUri, secondGrant);
            Assert.Equal(401, (await Assert.ThrowsAsync<MabiToolApiException>(() => staleFirst.GetOperationAsync(accepted[0].Id, ct))).Status);
            Assert.Equal(401, (await Assert.ThrowsAsync<MabiToolApiException>(() => staleSecond.GetOperationAsync(accepted[0].Id, ct))).Status);
            using var nextOwner = Client(next);
            using var nextFirst = Client(next.ApiBaseUri, await PairAsync(nextOwner, "first", ct));
            using var nextSecond = Client(next.ApiBaseUri, await PairAsync(nextOwner, "second", ct));
            var terminal = await nextFirst.GetOperationAsync(accepted[0].Id, ct);
            Assert.Equal(OperationState.Succeeded, terminal.State.KnownValue);
            Assert.Equal("committed", terminal.Result!.Value.Deserialize<UpdateInstallResult>(ContractJson.Options)!.Outcome);
            Assert.Equal(JsonSerializer.Serialize(terminal, ContractJson.Options),
                JsonSerializer.Serialize(await nextSecond.GetOperationAsync(terminal.Id, ct), ContractJson.Options));
            Assert.Equal(terminal.Id, (await nextFirst.ApplyUpdatePlanAsync(plan.PlanId, "frozen-first-apply", etag!, ct)).Id);
            Assert.Equal(terminal.Id, (await nextSecond.ApplyUpdatePlanAsync(plan.PlanId, "frozen-second-apply", etag!, ct)).Id);
            Assert.Equal(candidate.ReleaseId, (await nextSecond.GetUpdateStateAsync(ct)).InstalledReleaseId);
            await fixture.ReloadOptionsAsync();
            Assert.Equal(expectedUpdater, fixture.Options.Runtime.UpdaterVersion);
            Assert.Equal(candidate.ReleaseId, fixture.Options.InstalledReleaseId);
            var completionFile = Assert.Single(Directory.GetFiles(Path.Combine(fixture.State, "completed-installs"), "*.json"));
            var completion = JsonSerializer.Deserialize<CompletedInstallRecord>(await File.ReadAllTextAsync(completionFile, ct), UpdateHandoffProtocol.Json)!;
            Assert.Equal(terminal.Id, completion.OperationId);
            Assert.Equal("committed", completion.Outcome);
            Assert.Equal(candidate.ReleaseId, (await InstallTransaction.ReadInstalledAsync(fixture.Installation, ct)).Version.ReleaseId);
            Assert.False(File.Exists(Path.Combine(fixture.State, UpdaterBootstrap.RecordName)));
            Assert.False(File.Exists(Path.Combine(fixture.State, UpdateInstallIntent.FileName)));
            var independent = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.State, "updaters"), completion.Transaction.TransactionId + "-*"));
            await FileTreePlanner.VerifyAsync(independent,
                files.Where(file => file.RelativePath.StartsWith("components/updater/", StringComparison.Ordinal)).ToArray(), ct);
            await FileTreePlanner.VerifyAsync(Path.Combine(fixture.Installation, "versions", candidate.ReleaseId), files, ct);
            var discovery = JsonSerializer.Deserialize<LocalDiscoveryInfo>(await File.ReadAllTextAsync(fixture.Discovery, ct), ContractJson.Options)!;
            using var processDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            processDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (!host.HasOnlyHostAndConsole(discovery.ProcessId)) await Task.Delay(100, processDeadline.Token);
            var remaining = host.ActiveProcessList();
            Assert.Equal((uint)remaining.Count, host.ActiveProcesses);
            var finalHost = Assert.Single(remaining, process => process.Id == discovery.ProcessId);
            var newRuntime = Path.Combine(fixture.Installation, "versions", candidate.ReleaseId, "components", "dotnet-runtime", "dotnet.exe");
            Assert.Equal(newRuntime, finalHost.Executable, StringComparer.OrdinalIgnoreCase);
            using var runningHost = Process.GetProcessById(discovery.ProcessId);
            await host.KillAsync();
            await runningHost.WaitForExitAsync(ct);
            await VerifyBusinessDataAsync(fixture);
            await PackagedReleaseInstallTests.ExportHandoffDiagnosticsAsync(fixture.State, evidence, "public-apply");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                outcome = "committed", executionEntry = "frozen-host-public-apply", baselinePackage = paths["baseline"], candidatePackage = paths["candidate"],
                baselineVersion = baseline.Version, candidateVersion = candidate.Version, baselineReleaseId = baseline.ReleaseId,
                candidateReleaseId = candidate.ReleaseId, oldRuntime, newRuntime, independentUpdater = independent,
                previousUpdaterVersion = previousUpdater.ToString(), requiredUpdaterVersion = plan.RequiredUpdaterVersion.ToString(),
                actualUpdaterVersion = fixture.Options.Runtime.UpdaterVersion.ToString(), operationId = terminal.Id,
                transactionId = completion.Transaction.TransactionId, previousEpoch = bootstrap.InstanceId, newEpoch = next.InstanceId,
                bothClientsReadSameCompletion = true, oldGrantsRejected = true, retryReturnedOriginalOperation = true,
                stableTreeVerified = true, independentUpdaterTreeVerified = true, installedTreeVerified = true,
                businessDataPreserved = true, finalProcessesBeforeRelease = remaining, finalHostExited = runningHost.HasExited,
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds, productionHandoffTimeoutSeconds = UpdateHandoffProtocol.Timeout.TotalSeconds,
                productionTransactionBudgetMilliseconds = 250, osVersion = Environment.OSVersion.VersionString,
                manifestSha256 = verified.Receipt.Manifest.Sha256
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await fixture.SaveEvidenceAsync();
            try { await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            { outcome = "failed", exceptionType = error.GetType().Name, elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                baselinePackage = paths["baseline"], candidatePackage = paths["candidate"], productionHandoffTimeoutSeconds = UpdateHandoffProtocol.Timeout.TotalSeconds,
                productionTransactionBudgetMilliseconds = 250 }, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception) { /* Preserve the original packaged-install exception. */ }
            throw;
        }
    }

    private static MabiToolClient Client(BootstrapResponse value) => Client(value.ApiBaseUri, value.Grant);
    private static MabiToolClient Client(string uri, AccessGrant grant) => new(new(uri), _ => ValueTask.FromResult(grant.Token));
    private static async Task<AccessGrant> PairAsync(MabiToolClient owner, string name, CancellationToken ct)
    {
        var challenge = await owner.CreatePairingChallengeAsync(new(name, null, [ApiPermissions.SystemRead, ApiPermissions.UpdatesManage]), ct);
        return await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code), ct);
    }
    private static async Task SaveBusinessDataAsync(ManagedInstallFixture fixture)
    {
        using var root = DataRootLease.Open(fixture.Storage);
        await using var database = SqliteStore.Open(root, fixture.DatabaseMigrations);
        var music = new MusicLibraryStore(database, "test");
        await music.EditAsync("package-public", new([new("public-kept", null, "frozen-public-business", "0")], [], [], [], null),
            "public-business", MusicLibraryStore.ETag(music.Read().Revision));
        await File.WriteAllTextAsync(root.Paths.Resolve("settings", "public-kept.txt"), "frozen-public-settings");
    }
    private static async Task VerifyBusinessDataAsync(ManagedInstallFixture fixture)
    {
        using var root = DataRootLease.Open(fixture.Storage);
        await using var database = SqliteStore.Open(root, fixture.DatabaseMigrations);
        Assert.Equal("frozen-public-business", Assert.Single(new MusicLibraryStore(database, "test").Read().Folders).Name);
        Assert.Equal("frozen-public-settings", await File.ReadAllTextAsync(root.Paths.Resolve("settings", "public-kept.txt")));
    }
}
