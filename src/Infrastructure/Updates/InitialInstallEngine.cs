using System.Text.Json;
using System.Runtime.Versioning;

namespace MabiTool.Infrastructure.Updates;

internal interface IInitialInstallHealthExchange
{
    Task<InstallHealthEvidence> VerifyAsync(InitialInstallAuthorization authorization, CancellationToken token);
}

[SupportedOSPlatform("windows")]
internal sealed class InitialInstallEngine(Version updaterVersion, string updaterAssembly,
    IInitialInstallHealthExchange? health = null, TimeProvider? time = null,
    Func<InitialInstallPersistencePoint, Task>? observed = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private Task ObserveAsync(InitialInstallPersistencePoint point) => observed?.Invoke(point) ?? Task.CompletedTask;

    internal async Task<InitialInstallRecord> RunAsync(string requestPath, CancellationToken token = default)
    {
        requestPath = StoragePaths.ValidateLocal(requestPath);
        var request = (await InitialInstallFiles.ReadJsonAsync<InitialInstallRequest>(requestPath, 262144, token).ConfigureAwait(false)).Normalize();
        var rootBytes = await InitialInstallFiles.ReadAsync(request.TrustRootPath, 1048576, token).ConfigureAwait(false);
        var indexBytes = await InitialInstallFiles.ReadAsync(request.ChannelIndexPath, 1048576, token).ConfigureAwait(false);
        var manifestBytes = await InitialInstallFiles.ReadAsync(request.ReleaseManifestPath, ReleaseManifestParser.MaximumEnvelopeBytes, token).ConfigureAwait(false);
        var hash = InitialInstallRecord.InputHash(request, rootBytes, indexBytes, manifestBytes);
        var captured = new InitialInstallRecord(1, request, hash, rootBytes, indexBytes, manifestBytes, "captured");
        var existing = File.Exists(request.JournalPath);
        if (!existing)
        {
            InitialInstallMetadata.RequireCaptureLayout(captured, token, requestPath);
            InitialInstallFiles.RequireEmpty(request.BootstrapDirectory);
            InitialInstallFiles.RequireEmpty(request.DataRoot);
        }
        Directory.CreateDirectory(request.InstallationRoot);
        using var installation = new InstallTransaction(request.InstallationRoot);
        existing = File.Exists(request.JournalPath);
        InitialInstallRecord record;
        if (existing)
        {
            record = await InitialInstallRecord.ReadAsync(request.InstallationRoot, token: token).ConfigureAwait(false);
            if (record.InputSha256 != hash || !InitialInstallFiles.Same(record.Request, request))
                throw new UpdatePlanException("update.initialInstallInputMismatch");
            if (record.Phase == "complete") throw new UpdatePlanException("update.installationAlreadyInitialized");
        }
        else
        {
            InitialInstallMetadata.RequireCaptureLayout(captured, token, requestPath);
            record = captured;
            InitialInstallMetadata.PublishCapture(record);
            await SaveAsync(record, false, token).ConfigureAwait(false);
            await ObserveAsync(InitialInstallPersistencePoint.Captured).ConfigureAwait(false);
        }
        try
        {
            RequireKnownLayout(record, token, requestPath);
            var selected = await InitialInstallValidation.SelectAsync(record, updaterVersion, clock, token).ConfigureAwait(false);
            if (!UpdatePaths.SameLocalFile(updaterAssembly, UpdatePaths.Local(request.PackageRoot, "components/updater/MabiTool.Updater.dll")))
                throw new UpdatePlanException("update.updaterUnavailable");
            if (record.Step == 0)
            {
                if (selected.PeakBytes > new DriveInfo(Path.GetPathRoot(request.InstallationRoot)!).AvailableFreeSpace)
                    throw new UpdatePlanException("update.diskSpaceInsufficient");
                record = record with { Phase = "prepared", Target = selected.Target, Version = selected.Version,
                    StableFiles = selected.StableFiles, Configuration = selected.Configuration, FailureCode = null };
                await SaveAsync(record, true, token).ConfigureAwait(false);
                await ObserveAsync(InitialInstallPersistencePoint.Prepared).ConfigureAwait(false);
            }
            else InitialInstallValidation.RequireSelection(record, selected);
            if (record.Step < 2)
            {
                await PublishTreeAsync(record, record.VersionRoot, "staging/initial-" + record.InstallationId,
                    record.Version!.Files, InitialInstallPersistencePoint.VersionPublished, token).ConfigureAwait(false);
                await PublishTreeAsync(record, record.StableRoot, "staging/initial-launcher-" + record.InstallationId,
                    record.StableFiles!, InitialInstallPersistencePoint.StablePublished, token).ConfigureAwait(false);
                record = record with { Phase = "filesPublished", FailureCode = null };
                await SaveAsync(record, true, token).ConfigureAwait(false);
                await ObserveAsync(InitialInstallPersistencePoint.FilesPublished).ConfigureAwait(false);
            }
            var authorization = await InitialInstallAuthorization.ReadAsync(request.InstallationRoot, hash, clock, token).ConfigureAwait(false);
            var result = await (health ?? new InitialInstallHealthExchange()).VerifyAsync(authorization, token).ConfigureAwait(false);
            RequireHealth(record, result);
            var data = await InitialInstallValidation.SnapshotAsync(request.DataRoot, false, token).ConfigureAwait(false);
            var bootstrap = await InitialInstallValidation.SnapshotAsync(request.BootstrapDirectory, true, token).ConfigureAwait(false);
            if (record.Step >= 3 && (!InitialInstallFiles.Same(data, record.DataFiles) || !InitialInstallFiles.Same(bootstrap, record.BootstrapFiles)))
                throw new UpdatePlanException("update.initialInstallDataChanged");
            record = record with { Phase = record.Step < 3 ? "dataInitialized" : record.Phase,
                Health = result, DataFiles = data, BootstrapFiles = bootstrap, FailureCode = null };
            await SaveAsync(record, true, token).ConfigureAwait(false);
            await ObserveAsync(InitialInstallPersistencePoint.DataInitialized).ConfigureAwait(false);
            _ = await installation.InitializeOrResumeInitialPointerAsync(record.Version!, observed, token).ConfigureAwait(false);
            if (record.Step < 4)
            {
                record = record with { Phase = "pointerPublished" }; await SaveAsync(record, true, token).ConfigureAwait(false);
            }
            await InitialInstallFiles.WriteExactAsync(UpdateInstalledConfiguration.PathFor(request.InstallationRoot), record.Configuration!, token).ConfigureAwait(false);
            await ObserveAsync(InitialInstallPersistencePoint.ConfigurationPublished).ConfigureAwait(false);
            record = record with { Phase = "configured" }; await SaveAsync(record, true, token).ConfigureAwait(false);
            authorization = await InitialInstallAuthorization.ReadAsync(request.InstallationRoot, hash, clock, token).ConfigureAwait(false);
            using (var lease = DataRootLease.OpenForInitialInstall(authorization))
            {
                var gate = UpdateInstallGate.Read(lease.Paths.DataRoot) ?? throw InitialInstallFiles.Invalid();
                gate.Remove(lease.Paths);
                await ObserveAsync(InitialInstallPersistencePoint.MaintenanceGateRemoved).ConfigureAwait(false);
                record = record with { Phase = "complete" }; await SaveAsync(record, true, token).ConfigureAwait(false);
            }
            await ObserveAsync(InitialInstallPersistencePoint.Completed).ConfigureAwait(false);
            return record;
        }
        catch (Exception error)
        {
            error.Data["initialInstallPhase"] = record.Phase;
            var failure = error switch { UpdatePlanException e => e.Code, TrustValidationException e => e.Code,
                FileTreeException e => e.Code, StorageException e => e.Code, OperationCanceledException => "update.initialInstallCancelled", _ => "update.initialInstallIncomplete" };
            // 失败正文只保存稳定错误码，保留已发布的原始输入与最后成功步骤。
            try { await SaveAsync(record with { FailureCode = failure }, true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception secondary) when (secondary is IOException or UnauthorizedAccessException or UpdatePlanException) { }
            throw;
        }
    }

    private async Task PublishTreeAsync(InitialInstallRecord record, string destination, string stageRelative,
        IReadOnlyList<ManifestFile> files, InitialInstallPersistencePoint point, CancellationToken token)
    {
        if (Directory.Exists(destination))
        { InitialInstallCopy.RequireDirectories(destination, files); await FileTreePlanner.VerifyAsync(destination, files, token).ConfigureAwait(false); }
        else
        {
            var work = UpdatePaths.Local(record.Request.InstallationRoot, stageRelative);
            await InitialInstallCopy.CopyAsync(record.Request.PackageRoot, work, files, observed, token).ConfigureAwait(false);
            await FileTreePlanner.VerifyAsync(work, files, token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await UpdatePaths.PublishOwnedDirectoryAsync(work, destination, token).ConfigureAwait(false);
        }
        await ObserveAsync(point).ConfigureAwait(false);
    }

    private static void RequireKnownLayout(InitialInstallRecord record, CancellationToken token, string requestPath)
    {
        var request = record.Request;
        var expectedTop = new HashSet<string>(["update-state", "versions", "staging", "launcher"], StringComparer.OrdinalIgnoreCase);
        // 工具根布局下引导与数据目录的内容由初始化快照单独核对。
        if (request.UsesToolRootLayout) expectedTop.UnionWith([ModuleSdk.ToolRoot.BootstrapName, ModuleSdk.ToolRoot.DataName]);
        foreach (var entry in Directory.EnumerateFileSystemEntries(request.InstallationRoot))
        {
            UpdatePaths.RejectLinks(entry);
            if (!Directory.Exists(entry) || !expectedTop.Contains(Path.GetFileName(entry))) throw new UpdatePlanException("update.initialInstallNotEmpty");
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(request.StateRoot))
        {
            token.ThrowIfCancellationRequested();
            UpdatePaths.RejectLinks(entry);
            var name = Path.GetFileName(entry);
            if (!File.Exists(entry)) throw new UpdatePlanException("update.initialInstallNotEmpty");
            if (InitialInstallMetadata.IsCurrentRequest(request, entry, requestPath)) continue;
            if (name == InitialInstallMetadata.CaptureName(record.InputSha256)) InitialInstallMetadata.RequireCaptureIdentifier(entry);
            else if (InitialInstallMetadata.IsTemporary(name, InitialInstallRecord.FileName)) InitialInstallMetadata.RequireJournal(entry, record, token);
            else if (InitialInstallMetadata.IsTemporary(name, "trust.json")) InitialInstallMetadata.RequireTrust(entry, record, token);
            else if (record.Step >= 3 && InitialInstallMetadata.IsTemporary(name, "installed-version.json"))
                InitialInstallMetadata.RequirePrefix(entry, [InstallTransaction.SerializeInitialInstalledRecord(record.Version!)], token: token);
            else if (name is not ("install.lock" or "initial-install.json" or "trust.json" or "trust.json.lock" or "installed-version.json"))
                throw new UpdatePlanException("update.initialInstallNotEmpty");
            if (record.Step < 3 && name == "installed-version.json") throw new UpdatePlanException("update.initialInstallNotEmpty");
        }
        foreach (var (relative, names) in new[]
        {
            ("versions", new[] { record.Version?.ReleaseId ?? "" }),
            ("staging", new[] { "initial-" + record.InstallationId, "initial-launcher-" + record.InstallationId }),
            ("launcher", new[] { "stable", "active-version.json", "install-configuration.json" })
        })
        {
            var directory = UpdatePaths.Local(request.InstallationRoot, relative);
            if (Directory.Exists(directory)) foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                UpdatePaths.RejectLinks(entry);
                var name = Path.GetFileName(entry);
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (relative == "launcher" && record.Step >= 3 && InitialInstallMetadata.IsTemporary(name, "active-version.json"))
                        InitialInstallMetadata.RequirePrefix(entry, [InstallTransaction.SerializeInitialPointer(record.Version!)], token: token);
                    else if (relative == "launcher" && record.Step >= 3 && InitialInstallMetadata.IsTemporary(name, "install-configuration.json"))
                        InitialInstallMetadata.RequirePrefix(entry, [record.Configuration!], token: token);
                    else throw new UpdatePlanException("update.initialInstallNotEmpty");
                }
                if (record.Step < 3 && relative == "launcher" && File.Exists(entry)) throw new UpdatePlanException("update.initialInstallNotEmpty");
            }
        }
    }

    private static void RequireHealth(InitialInstallRecord record, InstallHealthEvidence result)
    {
        var target = record.Target!;
        if (result.TransactionId != record.InstallationId || result.Challenge is not { Length: 64 } || !result.Challenge.All(Uri.IsHexDigit) ||
            !UpdateHandoffProtocol.Id(result.InstanceId) || result.HostVersion != target.HostVersion || result.ApiVersion != target.HostApiVersion ||
            result.Database.RootId != record.RootId || result.Database.DatabaseId != record.DatabaseId || result.Database.BackendMode != "live" ||
            !result.Database.Migrations.SequenceEqual(target.Migrations) || result.Modules.All(module => module.Id != "core") ||
            result.Modules.Any(module => !target.Modules.Contains(module))) throw new UpdatePlanException("update.initialInstallHealthMismatch");
    }

    private static Task SaveAsync(InitialInstallRecord record, bool replace, CancellationToken token)
    {
        record.Validate(); return InitialInstallFiles.WriteAsync(record.Request.JournalPath,
            JsonSerializer.SerializeToUtf8Bytes(record, UpdateHandoffProtocol.Json), replace, token);
    }
}
