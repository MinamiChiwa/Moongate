using System.Diagnostics;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

internal static class UpdateLaunchSupport
{
    internal static StorageOptions StorageFromConfiguration(string installationRoot, bool initialize = false)
    {
        var path = UpdateInstalledConfiguration.PathFor(installationRoot);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 262144) throw new UpdatePlanException("update.installIntentInvalid");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        var bootstrap = root.TryGetProperty("bootstrapDirectory", out var directory) && directory.ValueKind == JsonValueKind.String
            ? directory.GetString()! : Environment.GetEnvironmentVariable("MABITOOL_BOOTSTRAP_DIRECTORY") ??
                MabiTool.ModuleSdk.ToolRoot.Bootstrap(installationRoot);
        return new(StoragePaths.ValidateLocal(bootstrap), initialize);
    }

    internal static async Task WaitForParentExitAsync(UpdatePeerIdentity parent, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Process process;
        try { process = Process.GetProcessById(parent.ProcessId); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited) return;
            UpdatePeerIdentity actual;
            try { actual = UpdatePeerIdentity.Capture(process); }
            catch (UpdatePlanException) when (process.HasExited) { return; }
            if (actual.StartTimeUtcTicks != parent.StartTimeUtcTicks || !actual.ExecutablePath.Equals(parent.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    internal static async Task RecoverAsync(string installationRoot, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var state = UpdatePaths.Local(installationRoot, "update-state");
        if (!File.Exists(UpdatePaths.Local(state, UpdateInstallIntent.FileName)))
        {
            if (File.Exists(UpdatePaths.Local(state, "install-transaction.json"))) throw new UpdatePlanException("update.recoveryRequired");
            await UpdaterBootstrap.ClearCompletedRecordAsync(installationRoot, token).ConfigureAwait(false);
            return;
        }
        var intent = await UpdateInstallIntent.ReadAsync(installationRoot, token: token).ConfigureAwait(false);
        await WaitForParentExitAsync(intent.Parent, token).ConfigureAwait(false);
        var updaterRoot = await RecoveryUpdaterRootAsync(intent, token).ConfigureAwait(false);
        using var process = UpdateProcess.Start(updaterRoot, "updater", "MabiTool.Updater", intent.Validation.Runtime.RuntimeMode,
            ["--recover", installationRoot, "--no-launch"], captureOutput: true);
        var output = UpdateProcess.ReadBoundedAsync(process.StandardOutput); var errors = UpdateProcess.ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var failure = new UpdatePlanException("update.recoveryRequired");
                failure.Data["updaterFailure"] = await errors.ConfigureAwait(false);
                throw failure;
            }
            if (File.Exists(intent.PathValue) && intent.Plan.RequiredUpdaterVersion is not null)
                await UpdaterBootstrap.WaitForCompletionAsync(intent,
                    UpdateInstallIntent.Hash(JsonSerializer.SerializeToUtf8Bytes(intent, UpdateHandoffProtocol.Json)), token).ConfigureAwait(false);
        }
        finally
        {
            UpdateProcess.Terminate(process); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await output.ConfigureAwait(false); _ = await errors.ConfigureAwait(false);
        }
    }

    internal static async Task<string> RecoveryUpdaterRootAsync(UpdateInstallIntent intent, CancellationToken token = default)
    {
        var journal = UpdatePaths.Local(intent.StateRoot, "install-transaction.json");
        var completion = UpdatePaths.Local(intent.StateRoot, "completed-installs/" + intent.TransactionId + ".json");
        var record = File.Exists(completion) ? (await InstallTransaction.ReadCompletedAsync(intent.InstallationRoot, intent.TransactionId, token).ConfigureAwait(false)).Transaction :
            File.Exists(journal) ? await InstallTransaction.ReadJournalAsync(intent.InstallationRoot, token).ConfigureAwait(false) : null;
        if (record is not null && (record.TransactionId != intent.TransactionId || record.PlanId != intent.Plan.PlanId))
            throw new UpdatePlanException("update.recoveryRequired");
        if (record?.Phase == "committed")
        {
            await FileTreePlanner.VerifyAsync(intent.CandidateRoot, intent.CandidateFiles, token).ConfigureAwait(false);
            if (intent.CandidateFiles.Any(item => item.RelativePath.StartsWith("components/updater/", StringComparison.Ordinal)))
            {
                await VerifyUpdaterAsync(intent.CandidateRoot, intent.CandidateFiles, token).ConfigureAwait(false);
                return intent.CandidateRoot;
            }
        }
        if (intent.Plan.RequiredUpdaterVersion is not null)
        {
            var bootstrap = await UpdaterBootstrap.ReadAsync(intent,
                UpdateInstallIntent.Hash(JsonSerializer.SerializeToUtf8Bytes(intent, UpdateHandoffProtocol.Json)), token).ConfigureAwait(false);
            if (bootstrap is not null)
            {
                try
                {
                    _ = await UpdaterBootstrap.VerifyPlanAsync(intent, token).ConfigureAwait(false);
                    await UpdaterBootstrap.VerifyVersionAsync(intent, bootstrap, token).ConfigureAwait(false);
                    return UpdaterBootstrap.VersionRoot(intent, bootstrap);
                }
                catch (Exception error) when (error is FileTreeException or IOException) { }
            }
        }
        await VerifyUpdaterAsync(intent.PreviousRoot, intent.Previous.Files, token).ConfigureAwait(false);
        return intent.PreviousRoot;
    }

    internal static Task VerifyUpdaterAsync(string versionRoot, IReadOnlyList<ManifestFile> inventory, CancellationToken token = default)
    {
        const string prefix = "components/updater/";
        var files = inventory.Where(item => item.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => item with { RelativePath = item.RelativePath[prefix.Length..] }).ToArray();
        if (!files.Any(item => item.RelativePath == "MabiTool.Updater.dll")) throw new UpdatePlanException("update.updaterUnavailable");
        return FileTreePlanner.VerifyAsync(UpdatePaths.Local(versionRoot, "components/updater"), files, token);
    }

    internal static async Task<Process> StartHostAsync(string installationRoot, StorageOptions storage, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var installed = await InstallTransaction.ReadInstalledAsync(installationRoot, token).ConfigureAwait(false);
        var versionRoot = UpdatePaths.Local(installationRoot, "versions/" + installed.Version.ReleaseId);
        var target = await InstallTargetDescriptor.ReadAsync(versionRoot, "mabitool", token).ConfigureAwait(false);
        var arguments = storage.Initialize ? new[] { "--initialize-data" } : [];
        return UpdateProcess.Start(versionRoot, "core", "MabiTool.Host", target.RuntimeMode, arguments,
            environment: new Dictionary<string, string>
            {
                ["MABITOOL_BOOTSTRAP_DIRECTORY"] = storage.BootstrapDirectory,
                ["MABITOOL_UPDATE_CONFIGURATION"] = UpdateInstalledConfiguration.PathFor(installationRoot)
            });
    }
}
