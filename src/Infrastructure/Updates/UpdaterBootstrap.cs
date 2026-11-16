using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

internal sealed record UpdaterBootstrapRecord(int SchemaVersion, string TransactionId, string IntentSha256,
    string Generation, string Version, IReadOnlyList<ManifestFile> Files, UpdatePeerIdentity Predecessor,
    UpdatePeerIdentity? Launcher, bool LaunchHost, string Phase, string? FailureCode = null);

/// <summary>独立副本和引导记录不修改业务指针，也不覆盖运行中的更新器。</summary>
internal static class UpdaterBootstrap
{
    internal const string RecordName = "updater-bootstrap.json";
    internal const int MaximumAttempts = 4;
    internal static string StableRoot(UpdateInstallIntent intent) => UpdatePaths.Local(intent.InstallationRoot, "launcher/stable");
    internal static string VersionRoot(UpdateInstallIntent intent, UpdaterBootstrapRecord record) =>
        UpdatePaths.Local(intent.StateRoot, "updaters/" + intent.TransactionId + "-" + record.Generation);

    internal static IReadOnlyList<ManifestFile> Files(UpdateInstallIntent intent) =>
        intent.CandidateFiles.Where(file => file.RelativePath.StartsWith("components/updater/", StringComparison.Ordinal)).ToArray();

    [SupportedOSPlatform("windows")]
    internal static async Task<UpdaterBootstrapRecord> PrepareAsync(UpdateInstallIntent intent, string hash, bool launch,
        CancellationToken token = default)
    {
        var release = await VerifyPlanAsync(intent, token).ConfigureAwait(false);
        if (intent.Plan.RequiredUpdaterVersion != release.MinimumUpdaterVersion ||
            intent.Plan.RequiredUpdaterVersion is null || Files(intent).Count == 0) throw new UpdatePlanException("update.updaterRequired");
        await FileTreePlanner.VerifyAsync(intent.StageRoot, intent.CandidateFiles, token).ConfigureAwait(false);
        await VerifyStableAsync(intent, token).ConfigureAwait(false);
        using var current = Process.GetCurrentProcess();
        var existing = await ReadAsync(intent, hash, token).ConfigureAwait(false);
        if (existing is not null)
        {
            try
            {
                await VerifyVersionAsync(intent, existing, token).ConfigureAwait(false);
                var reused = existing with { Predecessor = UpdatePeerIdentity.Capture(current), Launcher = null,
                    LaunchHost = launch, Phase = "staged", FailureCode = null };
                await SaveAsync(intent, reused, token).ConfigureAwait(false); return reused;
            }
            catch (Exception error) when (error is FileTreeException or IOException) { }
        }
        var directory = UpdatePaths.Local(intent.StateRoot, "updaters"); Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory, intent.TransactionId + "-*").Take(MaximumAttempts).Count() >= MaximumAttempts)
            throw new UpdatePlanException("update.recoveryRequired");
        var record = new UpdaterBootstrapRecord(1, intent.TransactionId, hash, Guid.NewGuid().ToString("N"),
            intent.Plan.Components.Single(item => item.ComponentId == "updater").Version.ToString(), Files(intent),
            UpdatePeerIdentity.Capture(current), null, launch, "staged");
        var destination = VersionRoot(intent, record); var work = destination + ".work-" + Guid.NewGuid().ToString("N");
        var bytes = record.Files.Sum(file => file.SizeBytes);
        using var reservation = VolumeSpaceReservations.Shared.Reserve("updater-bootstrap:" + intent.TransactionId,
            new VolumeSpaceRequirement(directory, checked((long)Math.Ceiling(bytes * 1.2d)), new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace));
        try
        {
            Directory.CreateDirectory(work);
            foreach (var file in record.Files)
            {
                token.ThrowIfCancellationRequested(); var outputPath = UpdatePaths.Local(work, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = new FileStream(UpdatePaths.Local(intent.StageRoot, file.RelativePath), FileMode.Open,
                    FileAccess.Read, FileShare.Read, UpdateIoBudget.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    UpdateIoBudget.ChunkSize, FileOptions.Asynchronous);
                var copied = await new UpdateIoBudget().CopyAndHashAsync(input, output, file.SizeBytes, token).ConfigureAwait(false);
                if (copied.Length != file.SizeBytes || copied.Hash != file.Sha256) throw new FileTreeException("update.targetTreeMismatch");
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            await FileTreePlanner.VerifyAsync(work, record.Files, token).ConfigureAwait(false);
            await UpdatePaths.PublishOwnedDirectoryAsync(work, destination, token).ConfigureAwait(false);
            await SaveAsync(intent, record, token).ConfigureAwait(false); return record;
        }
        finally { UpdatePaths.DeleteOwnedTree(work); }
    }

    internal static async Task<UpdaterBootstrapRecord?> ReadAsync(UpdateInstallIntent intent, string hash, CancellationToken token = default)
    {
        var path = UpdatePaths.Local(intent.StateRoot, RecordName); if (!File.Exists(path)) return null;
        FileStream opened;
        try { opened = await UpdatePaths.OpenPublishedReadAsync(path, token).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using var input = opened;
        if (input.Length is <= 0 or > 4 * 1024 * 1024) throw new UpdatePlanException("update.installIntentInvalid");
        var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            using var json = StrictJson.Parse(bytes, 4 * 1024 * 1024);
            var record = json.RootElement.Deserialize<UpdaterBootstrapRecord>(UpdateHandoffProtocol.Json) ?? throw new JsonException();
            Require(intent, hash, record); return record;
        }
        catch (Exception error) when (error is JsonException or TrustValidationException or ArgumentException)
        { throw new UpdatePlanException("update.installIntentInvalid"); }
    }

    internal static Task VerifyVersionAsync(UpdateInstallIntent intent, UpdaterBootstrapRecord record, CancellationToken token = default)
    { Require(intent, record.IntentSha256, record); return FileTreePlanner.VerifyAsync(VersionRoot(intent, record), record.Files, token); }

    internal static async Task<VerifiedReleaseManifest> VerifyPlanAsync(UpdateInstallIntent intent, CancellationToken token = default)
    {
        var (plan, release) = await UpdateSignedPlanVerifier.ReadAsync(intent.StateRoot, intent.Validation, intent.Plan.PlanId,
            verifyAvailableSpace: false, token: token).ConfigureAwait(false);
        if (JsonSerializer.Serialize(plan, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(intent.Plan, UpdateHandoffProtocol.Json) ||
            JsonSerializer.Serialize(UpdateSignedPlanVerifier.TargetFiles(plan, release), UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(intent.CandidateFiles, UpdateHandoffProtocol.Json))
            throw new UpdatePlanException("update.planStale");
        return release;
    }

    internal static async Task VerifyStableAsync(UpdateInstallIntent intent, CancellationToken token = default)
    {
        var previous = intent.Previous.Files.Where(file => file.RelativePath.StartsWith("components/launcher/", StringComparison.Ordinal)).ToArray();
        var next = intent.CandidateFiles.Where(file => file.RelativePath.StartsWith("components/launcher/", StringComparison.Ordinal)).ToArray();
        if (!previous.Any(file => file.RelativePath == "components/launcher/MabiTool.Launcher.dll") ||
            FileTreePlanner.TreeHash(previous) != FileTreePlanner.TreeHash(next))
            throw new UpdatePlanException("update.stableLauncherRequired");
        await FileTreePlanner.VerifyAsync(StableRoot(intent), previous, token).ConfigureAwait(false);
    }

    internal static async Task SaveAsync(UpdateInstallIntent intent, UpdaterBootstrapRecord record, CancellationToken token = default)
    {
        Require(intent, record.IntentSha256, record);
        var path = UpdatePaths.Local(intent.StateRoot, RecordName); var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, record, UpdateHandoffProtocol.Json, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            if (File.Exists(path)) await UpdatePaths.ReplaceOwnedFileAsync(temporary, path, token).ConfigureAwait(false);
            else File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task WaitForCompletionAsync(UpdateInstallIntent intent, string hash, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        while (File.Exists(intent.PathValue))
        {
            var record = await ReadAsync(intent, hash, deadline.Token).ConfigureAwait(false);
            if (record is null)
            {
                if (!File.Exists(intent.PathValue)) return;
                var completion = UpdatePaths.Local(intent.StateRoot, "completed-installs/" + intent.TransactionId + ".json");
                if (!File.Exists(completion)) throw new UpdatePlanException("update.recoveryRequired");
                var completed = await InstallTransaction.ReadCompletedAsync(intent.InstallationRoot, intent.TransactionId, deadline.Token).ConfigureAwait(false);
                if (completed.OperationId != intent.OperationId || completed.Transaction.PlanId != intent.Plan.PlanId)
                    throw new UpdatePlanException("update.recoveryRequired");
            }
            if (record?.Phase == "failed") throw new UpdatePlanException(record.FailureCode ?? "update.recoveryRequired");
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
    }

    internal static async Task RemoveAsync(UpdateInstallIntent intent, string hash, CancellationToken token = default)
    {
        if (await ReadAsync(intent, hash, token).ConfigureAwait(false) is not null)
            File.Delete(UpdatePaths.Local(intent.StateRoot, RecordName));
    }

    internal static async Task ClearCompletedRecordAsync(string installationRoot, CancellationToken token = default)
    {
        var path = UpdatePaths.Local(installationRoot, "update-state/" + RecordName); if (!File.Exists(path)) return;
        await using var input = await UpdatePaths.OpenPublishedReadAsync(path, token).ConfigureAwait(false);
        if (input.Length is <= 0 or > 4 * 1024 * 1024) throw new UpdatePlanException("update.installIntentInvalid");
        var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using var json = StrictJson.Parse(bytes, 4 * 1024 * 1024);
        var record = json.RootElement.Deserialize<UpdaterBootstrapRecord>(UpdateHandoffProtocol.Json) ?? throw new UpdatePlanException("update.installIntentInvalid");
        if (record.SchemaVersion != 1 || !UpdateHandoffProtocol.Id(record.TransactionId)) throw new UpdatePlanException("update.installIntentInvalid");
        _ = await InstallTransaction.ReadCompletedAsync(installationRoot, record.TransactionId, token).ConfigureAwait(false);
        input.Dispose(); File.Delete(path);
    }

    private static void Require(UpdateInstallIntent intent, string hash, UpdaterBootstrapRecord record)
    {
        if (record.SchemaVersion != 1 || record.TransactionId != intent.TransactionId || record.IntentSha256 != hash ||
            !UpdateHandoffProtocol.Id(record.Generation) || record.Phase is not ("staged" or "activated" or "failed") ||
            intent.Plan.RequiredUpdaterVersion is null || !Version.TryParse(record.Version, out var version) ||
            version < intent.Plan.RequiredUpdaterVersion || version != intent.Plan.Components.Single(item => item.ComponentId == "updater").Version ||
            record.Files is null || JsonSerializer.Serialize(record.Files, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(Files(intent), UpdateHandoffProtocol.Json) ||
            record.Predecessor is null || record.Predecessor.ProcessId <= 0 || record.Predecessor.StartTimeUtcTicks <= 0)
            throw new UpdatePlanException("update.installIntentInvalid");
        StoragePaths.ValidateLocal(record.Predecessor.ExecutablePath);
        if (record.Launcher is not null) StoragePaths.ValidateLocal(record.Launcher.ExecutablePath);
    }
}
