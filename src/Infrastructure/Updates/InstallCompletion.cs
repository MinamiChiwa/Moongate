namespace MabiTool.Infrastructure.Updates;

internal sealed record InstalledVersionRecord(int SchemaVersion, InstallVersion Version, ActiveVersionPointer Pointer);
internal sealed record CompletedInstallRecord(int SchemaVersion, string OperationId, string Outcome,
    DateTimeOffset CompletedAt, InstallTransactionRecord Transaction, InstalledVersionRecord Active, InstallHealthEvidence? PreviousHealth = null, string? FailureCode = null);

internal sealed partial class InstallTransaction
{
    internal const int MaximumCompletedInstalls = 8;
    internal const long MaximumCompletedBytes = 64L * 1024 * 1024;

    internal Task<InstalledVersionRecord> ReadActiveAsync(CancellationToken token = default) =>
        RunAsync(() => ReadInstalledAsync(Path.GetDirectoryName(stateRoot)!, token), token);

    internal Task<bool> CanAbortAsync(InstallTransactionRecord expected, CancellationToken token = default) => RunAsync(async () =>
    {
        var record = await ReadRecordAsync(token).ConfigureAwait(false);
        if (!Same(record, expected)) throw new UpdatePlanException("update.transactionStale");
        if (record.Phase is not ("prepared" or "dataBackedUp")) return false;
        try
        {
            if (!Same(await ReadPointerAsync(token).ConfigureAwait(false), record.PreviousPointer)) return false;
            await VerifyVersionAsync(record.Previous, token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is FileTreeException or UpdatePlanException or IOException or UnauthorizedAccessException)
        { return false; }
    }, token);

    internal Task<InstallTransactionRecord> RecordRejectedAsync(UpdateInstallIntent intent, CancellationToken token = default) => RunAsync(async () =>
    {
        intent.Validate();
        if (Exists(journalPath)) throw new UpdatePlanException("resource.busy");
        var previous = FreezeVersion(intent.Previous); var candidate = FreezeVersion(new(intent.Plan.ReleaseId,
            intent.Plan.ReleaseSequence, intent.Plan.ManifestSha256, intent.CandidateFiles));
        var pointer = await ReadPointerAsync(token).ConfigureAwait(false);
        if (!Matches(pointer, previous)) throw new UpdatePlanException("update.pointerInvalid");
        await VerifyVersionAsync(previous, token).ConfigureAwait(false);
        var record = new InstallTransactionRecord(1, intent.TransactionId, intent.Plan.PlanId, 1, "prepared", previous,
            candidate, pointer, null, HealthTarget: FreezeHealthTarget(intent.HealthTarget));
        ValidateRecord(record); await PersistAsync(journalPath, record, false, token).ConfigureAwait(false); return record;
    }, token);

    internal static async Task<InstalledVersionRecord> ReadInstalledAsync(string installationRoot, CancellationToken token = default)
    {
        var root = StoragePaths.ValidateLocal(installationRoot);
        var installed = await ReadDocumentAsync<InstalledVersionRecord>(
            UpdatePaths.Local(root, "update-state/installed-version.json"), MaximumDocumentBytes, token).ConfigureAwait(false);
        if (installed.SchemaVersion != 1 || installed.Version is null || installed.Pointer is null)
            throw new UpdatePlanException("update.pointerInvalid");
        var version = FreezeVersion(installed.Version);
        ValidatePointer(installed.Pointer);
        if (!Matches(installed.Pointer, version)) throw new UpdatePlanException("update.pointerInvalid");
        var pointer = await ReadDocumentAsync<ActiveVersionPointer>(
            UpdatePaths.Local(root, "launcher/active-version.json"), 4096, token).ConfigureAwait(false);
        ValidatePointer(pointer);
        if (!Same(pointer, installed.Pointer)) throw new UpdatePlanException("update.pointerInvalid");
        await VerifyTreeAsync(UpdatePaths.Local(root, "versions/" + version.ReleaseId), version.Files, token).ConfigureAwait(false);
        return installed with { Version = version };
    }

    internal static async Task<InstallTransactionRecord> ReadJournalAsync(string installationRoot, CancellationToken token = default)
    {
        var record = await ReadDocumentAsync<InstallTransactionRecord>(
            UpdatePaths.Local(StoragePaths.ValidateLocal(installationRoot), "update-state/install-transaction.json"),
            MaximumDocumentBytes, token).ConfigureAwait(false);
        ValidateRecord(record); return record;
    }

    internal static async Task<CompletedInstallRecord> ReadCompletedAsync(string installationRoot, string transactionId, CancellationToken token = default)
    {
        ValidateId(transactionId);
        var completed = await ReadDocumentAsync<CompletedInstallRecord>(UpdatePaths.Local(StoragePaths.ValidateLocal(installationRoot),
            "update-state/completed-installs/" + transactionId + ".json"), MaximumDocumentBytes, token).ConfigureAwait(false);
        if (completed.SchemaVersion != 1 || !UpdateInstallIntent.OperationIdValid(completed.OperationId) ||
            completed.Transaction is null || completed.Transaction.TransactionId != transactionId ||
            completed.Active is null || completed.Active.SchemaVersion != 1 || completed.Active.Version is null || completed.Active.Pointer is null)
            throw new UpdatePlanException("update.transactionInvalid");
        ValidateRecord(completed.Transaction);
        var version = completed.Outcome switch
        {
            "committed" when completed.Transaction.Phase == "committed" => completed.Transaction.Candidate,
            "rolledBack" when completed.Transaction.Phase == "previousPointerRestored" => completed.Transaction.Previous,
            "aborted" when completed.Transaction.Phase is "prepared" or "dataBackedUp" => completed.Transaction.Previous,
            _ => throw new UpdatePlanException("update.transactionPhaseInvalid")
        };
        var pointer = completed.Outcome == "committed" ? Pointer(version, transactionId) : completed.Transaction.PreviousPointer;
        if (!Same(completed.Active, new InstalledVersionRecord(1, version, pointer)) ||
            completed.Outcome == "rolledBack" && completed.Transaction.HealthTarget is not null && completed.PreviousHealth is null)
            throw new UpdatePlanException("update.transactionInvalid");
        return completed;
    }

    // 候选复核不争用更新器持有的安装锁；只读取已刷盘提交及完整候选树。
    internal static async Task VerifyCommittedAsync(string installationRoot, UpdateCommitNotice notice, CancellationToken token = default)
    {
        var root = StoragePaths.ValidateLocal(installationRoot);
        var record = await ReadJournalAsync(root, token).ConfigureAwait(false);
        if (record.Phase != "committed" || record.Health is null || notice != new UpdateCommitNotice(record.TransactionId,
            record.Health.InstanceId, record.Candidate.ReleaseId, record.Candidate.ManifestSha256, record.Revision))
            throw new UpdatePlanException("update.transactionStale");
        var pointer = await ReadDocumentAsync<ActiveVersionPointer>(UpdatePaths.Local(root, "launcher/active-version.json"), 4096, token).ConfigureAwait(false);
        ValidatePointer(pointer);
        if (!Same(pointer, Pointer(record.Candidate, record.TransactionId))) throw new UpdatePlanException("update.pointerInvalid");
        await VerifyTreeAsync(UpdatePaths.Local(root, "versions/" + record.Candidate.ReleaseId), record.Candidate.Files, token).ConfigureAwait(false);
    }

    internal Task<CompletedInstallRecord> RecordCompletionAsync(InstallTransactionRecord expected, string operationId, string outcome,
        InstallHealthEvidence? previousHealth = null, CancellationToken token = default, string? failureCode = null) =>
        RunAsync(() => RecordCompletionCoreAsync(expected, operationId, outcome, previousHealth, token, failureCode), token);

    private async Task<CompletedInstallRecord> RecordCompletionCoreAsync(InstallTransactionRecord expected, string operationId, string outcome,
        InstallHealthEvidence? previousHealth, CancellationToken token, string? failureCode)
        {
            if (!UpdateInstallIntent.OperationIdValid(operationId)) throw new UpdatePlanException("update.transactionInvalid");
            var frozen = Clone(expected); ValidateRecord(frozen);
            var completionPath = UpdatePaths.Local(stateRoot, "completed-installs/" + frozen.TransactionId + ".json");
            var pointer = await ReadPointerAsync(token).ConfigureAwait(false);
            var version = outcome switch
            {
                "committed" when frozen.Phase == "committed" => frozen.Candidate,
                "rolledBack" when frozen.Phase == "previousPointerRestored" => frozen.Previous,
                "aborted" when frozen.Phase is "prepared" or "dataBackedUp" => frozen.Previous,
                _ => throw new UpdatePlanException("update.transactionPhaseInvalid")
            };
            var expectedPointer = outcome == "committed" ? Pointer(frozen.Candidate, frozen.TransactionId) : frozen.PreviousPointer;
            if (!Same(pointer, expectedPointer)) throw new UpdatePlanException("update.pointerInvalid");
            await VerifyVersionAsync(version, token).ConfigureAwait(false);
            if (outcome == "rolledBack" && frozen.HealthTarget is not null)
            {
                var descriptor = await InstallTargetDescriptor.ReadAsync(UpdatePaths.Local(versionsRoot, version.ReleaseId), "mabitool", token).ConfigureAwait(false);
                var target = descriptor.HealthTarget(frozen.HealthTarget.RootId, frozen.HealthTarget.DatabaseId,
                    frozen.HealthTarget.BackendMode, frozen.HealthTarget.Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal));
                if (previousHealth is null) throw HealthInvalid();
                ValidateHealthEvidence(frozen with { HealthTarget = target }, previousHealth);
            }
            if (Exists(journalPath) && !Same(await ReadRecordAsync(token).ConfigureAwait(false), frozen))
                throw new UpdatePlanException("update.transactionStale");
            CompletedInstallRecord completed;
            if (Exists(completionPath))
            {
                completed = await ReadDocumentAsync<CompletedInstallRecord>(completionPath, MaximumDocumentBytes, token).ConfigureAwait(false);
                if (completed.SchemaVersion != 1 || completed.OperationId != operationId || completed.Outcome != outcome ||
                    !Same(completed.Transaction, frozen) || !Same(completed.Active, new InstalledVersionRecord(1, version, pointer)) ||
                    !Same(completed.PreviousHealth, previousHealth) || completed.FailureCode != failureCode)
                    throw new UpdatePlanException("update.transactionStale");
            }
            else
            {
                if (!Exists(journalPath)) throw new UpdatePlanException("update.transactionStale");
                completed = new(1, operationId, outcome, healthTime.GetUtcNow(), frozen, new(1, version, pointer), previousHealth, failureCode);
                await PersistAsync(completionPath, completed, false, token).ConfigureAwait(false);
                await ObserveAsync(InstallPersistencePoint.CompletionRecorded).ConfigureAwait(false);
            }
            return completed;
        }

    internal Task<CompletedInstallRecord> CompleteAsync(InstallTransactionRecord expected, string operationId, string outcome,
        InstallHealthEvidence? previousHealth = null, CancellationToken token = default, string? failureCode = null) => RunAsync(async () =>
        {
            var completed = await RecordCompletionCoreAsync(expected, operationId, outcome, previousHealth, token, failureCode).ConfigureAwait(false);
            var completionPath = UpdatePaths.Local(stateRoot, "completed-installs/" + completed.Transaction.TransactionId + ".json");
            var installedPath = UpdatePaths.Local(stateRoot, "installed-version.json");
            await PersistAsync(installedPath, completed.Active, Exists(installedPath), CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(InstallPersistencePoint.CompletionIndexed).ConfigureAwait(false);
            if (Exists(journalPath)) File.Delete(journalPath);
            await PruneCompletionsAsync(completionPath).ConfigureAwait(false);
            await ObserveAsync(InstallPersistencePoint.Completed).ConfigureAwait(false);
            return completed;
        }, token);

    internal Task FinalizationPointAsync(InstallPersistencePoint point) => ObserveAsync(point);

    private async Task PruneCompletionsAsync(string retained)
    {
        var directory = Path.GetDirectoryName(retained)!;
        UpdatePaths.RejectLinks(directory);
        var entries = Directory.EnumerateFiles(directory, "*.json").Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
        long bytes = 0; var count = 0;
        foreach (var file in entries.OrderByDescending(file => file.FullName.Equals(retained, StringComparison.OrdinalIgnoreCase)))
        {
            UpdatePaths.RejectLinks(file.FullName);
            if (!file.FullName.Equals(retained, StringComparison.OrdinalIgnoreCase) &&
                (count >= MaximumCompletedInstalls || bytes + file.Length > MaximumCompletedBytes))
            {
                _ = await ReadDocumentAsync<CompletedInstallRecord>(file.FullName, MaximumDocumentBytes, CancellationToken.None).ConfigureAwait(false);
                File.Delete(file.FullName);
            }
            else { count++; bytes += file.Length; }
        }
    }
}
