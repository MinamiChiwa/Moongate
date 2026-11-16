using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure.Updates;

[SupportedOSPlatform("windows")]
internal sealed class UpdateInstallEngine(UpdateInstallIntent intent, string intentHash, Version updaterVersion,
    Func<InstallPersistencePoint, Task>? persisted = null)
{
    internal async Task<CompletedInstallRecord> RunAsync(bool recovering, CancellationToken token = default, string? initialFailure = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        intent.Validate();
        var stored = await UpdateInstallIntent.ReadAsync(intent.InstallationRoot, intentHash, token).ConfigureAwait(false);
        if (JsonSerializer.Serialize(stored, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(intent, UpdateHandoffProtocol.Json)) throw UpdateHandoffProtocol.Invalid();
        using var installation = new InstallTransaction(intent.InstallationRoot, persisted);
        var journal = UpdatePaths.Local(intent.StateRoot, "install-transaction.json");
        InstallTransactionRecord? record = File.Exists(journal) ? await installation.ReadAsync(token).ConfigureAwait(false) : null;
        InstallHealthEvidence? previousHealth = null; var outcome = "committed"; string? failureCode = null;
        if (File.Exists(UpdatePaths.Local(intent.StateRoot, "completed-installs/" + intent.TransactionId + ".json")))
        {
            var completed = await InstallTransaction.ReadCompletedAsync(intent.InstallationRoot, intent.TransactionId, token).ConfigureAwait(false);
            RequireIntentRecord(completed.Transaction);
            if (completed.OperationId != intent.OperationId || record is not null &&
                JsonSerializer.Serialize(record, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(completed.Transaction, UpdateHandoffProtocol.Json))
                throw new UpdatePlanException("update.recoveryRequired");
            return await FinalizeAsync(installation, completed.Transaction, completed.Outcome, completed.PreviousHealth, completed.FailureCode, token).ConfigureAwait(false);
        }
        if (record is not null) RequireIntentRecord(record);
        try
        {
            if (initialFailure is not null && record is null) throw new UpdatePlanException(initialFailure);
            if (record is null)
            {
                var release = await RevalidateAsync(token).ConfigureAwait(false);
                var active = await installation.ReadActiveAsync(token).ConfigureAwait(false);
                if (JsonSerializer.Serialize(active.Version, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(intent.Previous, UpdateHandoffProtocol.Json))
                    throw new UpdatePlanException("update.planStale");
                await FileTreePlanner.VerifyAsync(intent.StageRoot, UpdateSignedPlanVerifier.TargetFiles(intent.Plan, release), token).ConfigureAwait(false);
                record = await installation.PrepareForHealthAsync(intent.TransactionId, intent.Plan.PlanId, intent.Previous,
                    new(intent.Plan.ReleaseId, intent.Plan.ReleaseSequence, intent.Plan.ManifestSha256, intent.CandidateFiles), intent.HealthTarget, token).ConfigureAwait(false);
            }
            var assessment = await installation.AssessRecoveryAsync(token).ConfigureAwait(false);
            if (assessment.Action == InstallRecoveryAction.RecoveryRequired) throw new UpdatePlanException("update.recoveryRequired");
            if (record.Phase == "committed") return await FinalizeAsync(installation, record, outcome, null, null, token).ConfigureAwait(false);
            if (assessment.Action is InstallRecoveryAction.RestorePreviousAndData or InstallRecoveryAction.ResumeRollback or InstallRecoveryAction.PreviousVersionRestored)
            {
                record = await RollbackAsync(installation, record, token).ConfigureAwait(false);
                previousHealth = await PreviousHealthAsync(record, token).ConfigureAwait(false); outcome = "rolledBack";
            }
            else
            {
                if (record.Phase == "prepared") record = await BackupAsync(installation, record, token).ConfigureAwait(false);
                if (record.Phase == "dataBackedUp")
                {
                    await PreflightAsync(record, token).ConfigureAwait(false);
                    _ = await RevalidateAsync(token).ConfigureAwait(false);
                    record = await installation.SwitchPointerAsync(record, token).ConfigureAwait(false);
                }
                if (record.Phase is "pointerSwitched" or "healthy")
                {
                    // 已切换但尚未健康的恢复由前述评估回退；healthy 重新取得本进程授权。
                    record = await CandidateAsync(record, installation, token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is UpdatePlanException or TrustValidationException or FileTreeException or StorageException or OperationException or
            ActivityBarrierException or IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            await UpdateFailureLog.RecordAsync(intent, error).ConfigureAwait(false);
            record = File.Exists(journal) ? await installation.ReadAsync(CancellationToken.None).ConfigureAwait(false) :
                await installation.RecordRejectedAsync(intent, CancellationToken.None).ConfigureAwait(false);
            RequireIntentRecord(record);
            var assessment = await installation.AssessRecoveryAsync(CancellationToken.None).ConfigureAwait(false);
            if (record.Phase == "committed") outcome = "committed";
            else if (await installation.CanAbortAsync(record, CancellationToken.None).ConfigureAwait(false))
                outcome = "aborted";
            else if (assessment.Action is InstallRecoveryAction.RestorePreviousAndData or InstallRecoveryAction.ResumeRollback or InstallRecoveryAction.PreviousVersionRestored or InstallRecoveryAction.RecheckCandidateHealth)
            {
                record = await RollbackAsync(installation, record, CancellationToken.None).ConfigureAwait(false);
                previousHealth = await PreviousHealthAsync(record, CancellationToken.None).ConfigureAwait(false); outcome = "rolledBack";
            }
            else throw new UpdatePlanException("update.recoveryRequired");
            if (outcome != "committed") failureCode = error switch
            {
                UpdatePlanException e => e.Code, TrustValidationException e => e.Code, FileTreeException e => e.Code,
                StorageException e => e.Code, OperationException e => e.Code, ActivityBarrierException e => e.Code,
                OperationCanceledException => "operation.cancelled", TimeoutException => "update.handoffTimeout", _ => "update.storageUnavailable"
            };
        }
        if (record is null) throw new UpdatePlanException("update.recoveryRequired");
        return await FinalizeAsync(installation, record, outcome, previousHealth, failureCode, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<VerifiedReleaseManifest> RevalidateAsync(CancellationToken token)
    {
        if (updaterVersion < intent.Validation.Runtime.UpdaterVersion) throw new UpdatePlanException("update.updaterRequired");
        var (plan, release) = await UpdateSignedPlanVerifier.ReadAsync(intent.StateRoot, intent.Validation, intent.Plan.PlanId,
            verifyAvailableSpace: false, token: token).ConfigureAwait(false);
        if (updaterVersion < release.MinimumUpdaterVersion) throw new UpdatePlanException("update.updaterRequired");
        if (JsonSerializer.Serialize(plan, ContractJson.Options) != JsonSerializer.Serialize(intent.Plan, ContractJson.Options) ||
            JsonSerializer.Serialize(UpdateSignedPlanVerifier.TargetFiles(plan, release), UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(intent.CandidateFiles, UpdateHandoffProtocol.Json))
            throw new UpdatePlanException("update.planStale");
        return release;
    }

    private async Task<InstallTransactionRecord> BackupAsync(InstallTransaction installation, InstallTransactionRecord record, CancellationToken token)
    {
        using var root = DataRootLease.OpenForUpdate(intent.Storage, intent.TransactionId, intentHash); RequireRoot(root);
        await using var database = SqliteStore.OpenUpdateControl(root, intent.PreviousMigrations, intent.BackendMode);
        using var maintenance = database.Activities.TryEnterMaintenance(intent.OperationId) ?? throw new UpdatePlanException("resource.busy");
        var operations = new OperationStore(database);
        await operations.ReportTransferredUpdateAsync(intent, maintenance, "dataBackup", token).ConfigureAwait(false);
        var snapshot = await database.CreateDataSnapshotAsync(maintenance, token).ConfigureAwait(false);
        return await installation.RecordDataSnapshotAsync(record, root.Paths, snapshot, token).ConfigureAwait(false);
    }

    private async Task PreflightAsync(InstallTransactionRecord record, CancellationToken token)
    {
        if (record.Snapshot is null) throw new UpdatePlanException("update.migrationInvalid");
        await ChildAsync(intent.PreflightRoot, "--update-preflight", async (connection, ct, progress) =>
        {
            await connection.SendAsync("preflight", new UpdatePreflightRequest(record.Snapshot.SnapshotId, record.Revision), ct).ConfigureAwait(false);
            var result = await connection.ReceiveAsync<DatabaseMigrationPreflight>("preflightResult", ct).ConfigureAwait(false);
            if (result.BackupId != record.Snapshot.Database.BackupId || !result.Migrations.SequenceEqual(intent.CandidateTarget.Migrations))
                throw new UpdatePlanException("update.migrationInvalid");
            await connection.SendAsync("preflightCompleted", new UpdateIntentTransfer(intent.TransactionId, intentHash), ct).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private Task<InstallTransactionRecord> CandidateAsync(InstallTransactionRecord record, InstallTransaction installation, CancellationToken token) =>
        ChildAsync(intent.CandidateRoot, "--update-candidate", (connection, ct, progress) => UpdateInstallHealthExchange.RunUpdaterAsync(connection, installation, record, ct, progress), token);

    private async Task<InstallTransactionRecord> RollbackAsync(InstallTransaction installation, InstallTransactionRecord record, CancellationToken token)
    {
        using var root = DataRootLease.OpenForUpdate(intent.Storage, intent.TransactionId, intentHash); RequireRoot(root);
        return await installation.RestorePreviousAsync(record, root, token).ConfigureAwait(false);
    }

    private async Task<InstallHealthEvidence> PreviousHealthAsync(InstallTransactionRecord record, CancellationToken token)
    {
        var target = intent.PreviousTarget.HealthTarget(intent.RootId, intent.DatabaseId, intent.BackendMode,
            intent.HealthTarget.Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal));
        var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return await ChildAsync(intent.PreviousRoot, "--update-rollback-health", async (connection, ct, progress) =>
        {
            await connection.SendAsync("rollbackHealth", new UpdateRollbackHealthRequest(challenge, target), ct).ConfigureAwait(false);
            var result = await connection.ReceiveAsync<InstallHealthEvidence>("rollbackHealthResult", ct).ConfigureAwait(false);
            if (result.TransactionId != record.TransactionId || result.Challenge != challenge || result.HostVersion != target.HostVersion ||
                !Version.TryParse(result.ApiVersion, out var api) || api < target.MinimumHostApi || api >= target.MaximumHostApiExclusive ||
                result.Database.RootId != target.RootId || result.Database.DatabaseId != target.DatabaseId || result.Database.BackendMode != target.BackendMode ||
                !result.Database.Migrations.SequenceEqual(target.Migrations) || !result.Modules.SequenceEqual(target.Modules))
                throw new UpdatePlanException("update.recoveryRequired");
            await connection.SendAsync("rollbackHealthCompleted", new UpdateIntentTransfer(intent.TransactionId, intentHash), ct).ConfigureAwait(false);
            return result;
        }, token).ConfigureAwait(false);
    }

    private async Task<T> ChildAsync<T>(string versionRoot, string mode, Func<UpdateHandoffConnection, CancellationToken, UpdateHandoffProgress, Task<T>> exchange, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await FileTreePlanner.VerifyAsync(versionRoot, mode == "--update-rollback-health" ? intent.Previous.Files : intent.CandidateFiles, token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(UpdateHandoffProtocol.Timeout);
        var progress = new UpdateHandoffProgress();
        using var progressRegistration = deadline.Token.Register(() => progress.Freeze());
        using var listener = new UpdateHandoffListener(intent.TransactionId);
        using var current = Process.GetCurrentProcess();
        using var child = UpdateProcess.Start(versionRoot, "core", "MabiTool.Host", intent.Validation.Runtime.RuntimeMode,
            [mode, intent.InstallationRoot, intent.TransactionId, intentHash, listener.PipeName,
                JsonSerializer.Serialize(UpdatePeerIdentity.Capture(current), UpdateHandoffProtocol.Json)], captureOutput: true);
        var output = UpdateProcess.ReadBoundedAsync(child.StandardOutput); var errors = UpdateProcess.ReadBoundedAsync(child.StandardError);
        UpdateHandoffConnection? connection = null; Task<T>? exchangeTask = null;
        T result = default!; Exception? failure = null; Exception? exchangeFailure = null; string diagnostics; var timedOut = false;
        try
        {
            progress.SetStage("connect");
            connection = await listener.AcceptAsync(UpdatePeerIdentity.Capture(child), deadline.Token).ConfigureAwait(false);
            progress.SetStage("exchange");
            exchangeTask = exchange(connection, deadline.Token, progress);
            result = await exchangeTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            progress.SetStage("candidateExit");
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (child.ExitCode != 0) throw new UpdatePlanException("update.installationFailed");
            progress.Freeze();
        }
        catch (Exception error)
        {
            failure = error; timedOut = deadline.IsCancellationRequested && !token.IsCancellationRequested;
            progress.Freeze();
            // 对端关闭通道后，留出有界时间让退出路径写完实际故障；不能先杀进程丢掉错误原因。
            if (error is EndOfStreamException && !deadline.IsCancellationRequested && !child.HasExited)
            {
                using var exitGrace = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                exitGrace.CancelAfter(TimeSpan.FromSeconds(1));
                try { await child.WaitForExitAsync(exitGrace.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (exitGrace.IsCancellationRequested) { }
            }
        }
        finally
        {
            deadline.Cancel(); connection?.Dispose();
            UpdateProcess.Terminate(child); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // 先收敛原交换任务，避免超时包装结束后仍有迟到日志写入；原失败由上面的 await 传播。
            if (exchangeTask is not null) try { _ = await exchangeTask.ConfigureAwait(false); } catch (Exception error) { exchangeFailure = error; }
            _ = await output.ConfigureAwait(false); diagnostics = await errors.ConfigureAwait(false);
        }
        if (failure is not null)
        {
            if (exchangeFailure is not null) UpdateHandoffProgress.MergeFailureDetails(failure, exchangeFailure);
            var reported = diagnostics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2)[0]).FirstOrDefault(code => code.Length <= 96 &&
                    code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '.') && code.StartsWith("update.", StringComparison.Ordinal));
            // 清理关闭通道后子进程的 EOF 不能覆盖原超时；保留原交换阶段。
            var code = timedOut ? "update.handoffTimeout" : reported;
            if (code is not null)
            {
                var original = failure; failure = new UpdatePlanException(code);
                foreach (var key in new[] { "handoffStage", "handoffMessage", "peerCheck", "peerOpenError", "transactionCallback", "transactionElapsedMs" })
                    if (original.Data.Contains(key)) failure.Data[key] = original.Data[key];
            }
            failure.Data["updatePhase"] = mode;
            progress.Freeze().Apply(failure);
            await UpdateFailureLog.RecordAsync(intent, failure).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        await UpdateFailureLog.RecordHandoffAsync(intent.StateRoot, mode, progress.Freeze(), intent.TransactionId).ConfigureAwait(false);
        return result;
    }

    private async Task<CompletedInstallRecord> FinalizeAsync(InstallTransaction installation, InstallTransactionRecord record,
        string outcome, InstallHealthEvidence? previousHealth, string? failureCode, CancellationToken token)
    {
        var target = outcome == "committed" ? intent.CandidateTarget : intent.PreviousTarget;
        using var root = DataRootLease.OpenForUpdate(intent.Storage, intent.TransactionId, intentHash); RequireRoot(root);
        await using var database = SqliteStore.OpenUpdateControl(root, target.Migrations, intent.BackendMode);
        using var maintenance = database.Activities.TryEnterMaintenance(intent.OperationId) ?? throw new UpdatePlanException("resource.busy");
        var operations = new OperationStore(database);
        var completed = await installation.RecordCompletionAsync(record, intent.OperationId, outcome, previousHealth, token, failureCode).ConfigureAwait(false);
        var result = new UpdateInstallResult(intent.Plan.PlanId, intent.TransactionId,
            outcome == "committed" ? intent.Plan.ReleaseId : intent.Previous.ReleaseId, outcome, record.Snapshot?.SnapshotId, failureCode);
        var problem = outcome == "committed" ? null : ApiErrors.Create(outcome == "rolledBack" ? "update.installationRolledBack" : "update.installationFailed", "安装已结束", intent.OperationId);
        await operations.FinishTransferredUpdateAsync(intent, maintenance, result, problem, token).ConfigureAwait(false);
        await installation.FinalizationPointAsync(InstallPersistencePoint.OperationRecorded).ConfigureAwait(false);
        if (record.Snapshot is not null)
        {
            var superseded = new HashSet<string>(StringComparer.Ordinal);
            var directory = UpdatePaths.Local(intent.StateRoot, "completed-installs");
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(InstallTransaction.MaximumCompletedInstalls + 1))
                {
                    var previous = await InstallTransaction.ReadCompletedAsync(intent.InstallationRoot, Path.GetFileNameWithoutExtension(path), token).ConfigureAwait(false);
                    if (previous.Transaction.Snapshot is { } snapshot && snapshot.RootId == intent.RootId) superseded.Add(snapshot.SnapshotId);
                }
            await new DataBackupStore(database, root.Paths).CompleteUpdateRetentionAsync(record.Snapshot, superseded, maintenance, token).ConfigureAwait(false);
        }
        await UpdateInstalledConfiguration.SaveAsync(intent, outcome, token).ConfigureAwait(false);
        await installation.FinalizationPointAsync(InstallPersistencePoint.ConfigurationRecorded).ConfigureAwait(false);
        completed = await installation.CompleteAsync(record, intent.OperationId, outcome, previousHealth, token, failureCode).ConfigureAwait(false);
        // 完成标记在数据库和数据根所有权释放后撤销，恢复等待者可立即读取原操作。
        maintenance.Dispose();
        await database.DisposeAsync().ConfigureAwait(false);
        root.Dispose();
        var gate = UpdateInstallGate.Read(root.Paths.DataRoot);
        if (gate is not null)
        {
            if (gate != new UpdateInstallGate(1, intent.TransactionId, intentHash, intent.InstallationRoot, intent.RootId)) throw new UpdatePlanException("update.installIntentInvalid");
            gate.Remove(root.Paths);
        }
        await UpdaterBootstrap.RemoveAsync(intent, intentHash, token).ConfigureAwait(false);
        if (File.Exists(intent.PathValue)) File.Delete(intent.PathValue);
        return completed;
    }

    private void RequireRoot(DataRootLease root)
    {
        if (root.Paths.RootId != intent.RootId || root.DatabaseId != intent.DatabaseId) throw new UpdatePlanException("update.installIntentInvalid");
        UpdatePaths.RequireDisjoint(intent.StateRoot, Path.Combine(intent.InstallationRoot, "versions"), Path.Combine(intent.InstallationRoot, "staging"),
            Path.Combine(intent.InstallationRoot, "launcher"), root.Paths.DataRoot, root.Paths.BootstrapDirectory);
    }

    private void RequireIntentRecord(InstallTransactionRecord record)
    {
        var candidate = new InstallVersion(intent.Plan.ReleaseId, intent.Plan.ReleaseSequence, intent.Plan.ManifestSha256, intent.CandidateFiles);
        if (record.TransactionId != intent.TransactionId || record.PlanId != intent.Plan.PlanId ||
            JsonSerializer.Serialize(record.Previous, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(intent.Previous, UpdateHandoffProtocol.Json) ||
            JsonSerializer.Serialize(record.Candidate, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(candidate, UpdateHandoffProtocol.Json) ||
            JsonSerializer.Serialize(record.HealthTarget, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(intent.HealthTarget, UpdateHandoffProtocol.Json))
            throw new UpdatePlanException("update.recoveryRequired");
    }
}
