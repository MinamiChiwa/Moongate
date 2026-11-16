using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Infrastructure.Updates;

internal sealed record InstallVersion(string ReleaseId, long ReleaseSequence, string ManifestSha256,
    IReadOnlyList<ManifestFile> Files);
internal sealed record ActiveVersionPointer(int SchemaVersion, string ReleaseId, long ReleaseSequence,
    string ManifestSha256, string TreeSha256, string? TransactionId);
internal sealed record InstallTransactionRecord(int SchemaVersion, string TransactionId, string PlanId,
    long Revision, string Phase, InstallVersion Previous, InstallVersion Candidate,
    ActiveVersionPointer PreviousPointer, DatabaseBackupDescriptor? Backup, DatabaseRestoreResult? DatabaseRestore = null,
    DataSnapshotDescriptor? Snapshot = null, DataSnapshotRestorePlan? FileRestorePlan = null, DataSnapshotRestoreResult? FileRestore = null,
    InstallHealthTarget? HealthTarget = null, InstallHealthEvidence? Health = null, int HealthChecks = 0);
internal enum InstallRecoveryAction { Prepared, ResumeSwitch, RestorePreviousAndData, ResumeRollback, PreviousVersionRestored, RecheckCandidateHealth, Committed, RecoveryRequired }
internal sealed record InstallRecoveryAssessment(InstallRecoveryAction Action, InstallTransactionRecord Record,
    bool CandidatePublished, string? Reason);
internal enum InstallPersistencePoint
{
    Prepared, DataBackedUp, VersionPublished, PointerReplaced, PointerSwitched,
    FilesPrepared, RollbackStarted, DatabaseReplaced, DatabaseRestored,
    AssetsRetained, AssetsRestored, SettingsRetained, SettingsRestored, FilesRestored, PreviousPointerReplaced, PreviousPointerRestored,
    Healthy, Committed, CompletionRecorded, OperationRecorded, ConfigurationRecorded, CompletionIndexed, Completed
}

/// <summary>固定完整树、匹配数据恢复与指针的安装日志；调用方负责认证交接及健康确认。</summary>
internal sealed partial class InstallTransaction : IDisposable
{
    private const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { MaxDepth = 24, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string stateRoot;
    private readonly string versionsRoot;
    private readonly string stagingRoot;
    private readonly string pointerPath;
    private readonly string journalPath;
    private readonly FileStream ownership;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<InstallPersistencePoint, Task>? persisted;
    private int disposed;

    public InstallTransaction(string installationRoot, Func<InstallPersistencePoint, Task>? persisted = null)
    {
        var root = StoragePaths.ValidateLocal(installationRoot);
        if (!Directory.Exists(root)) throw new FileTreeException("update.installationMissing");
        stateRoot = Path.Combine(root, "update-state");
        versionsRoot = Path.Combine(root, "versions");
        stagingRoot = Path.Combine(root, "staging");
        pointerPath = Path.Combine(root, "launcher", "active-version.json");
        journalPath = Path.Combine(stateRoot, "install-transaction.json");
        UpdatePaths.RequireDisjoint(stateRoot, versionsRoot, stagingRoot, Path.GetDirectoryName(pointerPath)!);
        this.persisted = persisted;
        Directory.CreateDirectory(stateRoot);
        var lockPath = UpdatePaths.Local(stateRoot, "install.lock");
        try { ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { throw new UpdatePlanException("resource.busy"); }
    }

    // 仅供安装器显式建立基线；普通启动不得补建丢失指针。
    internal Task<ActiveVersionPointer> InitializePointerAsync(InstallVersion installed, CancellationToken token = default) =>
        RunAsync(async () =>
        {
            var version = FreezeVersion(installed);
            if (Exists(pointerPath) || Exists(journalPath)) throw new UpdatePlanException("update.installationAlreadyInitialized");
            await VerifyVersionAsync(version, token).ConfigureAwait(false);
            var pointer = Pointer(version, null);
            await PersistAsync(UpdatePaths.Local(stateRoot, "installed-version.json"), new InstalledVersionRecord(1, version, pointer), false, token).ConfigureAwait(false);
            await PersistAsync(pointerPath, pointer, false, token).ConfigureAwait(false);
            return pointer;
        }, token);

    internal Task<InstallTransactionRecord> PrepareAsync(string transactionId, string planId,
        InstallVersion previous, InstallVersion candidate, CancellationToken token = default) =>
        PrepareCoreAsync(transactionId, planId, previous, candidate, null, token);

    internal Task<InstallTransactionRecord> PrepareForHealthAsync(string transactionId, string planId,
        InstallVersion previous, InstallVersion candidate, InstallHealthTarget healthTarget, CancellationToken token = default) =>
        PrepareCoreAsync(transactionId, planId, previous, candidate, FreezeHealthTarget(healthTarget), token);

    private Task<InstallTransactionRecord> PrepareCoreAsync(string transactionId, string planId,
        InstallVersion previous, InstallVersion candidate, InstallHealthTarget? healthTarget, CancellationToken token) => RunAsync(async () =>
        {
            ValidateId(transactionId);
            ValidateId(planId);
            var before = FreezeVersion(previous);
            var after = FreezeVersion(candidate);
            if (before.ReleaseId.Equals(after.ReleaseId, StringComparison.OrdinalIgnoreCase) || after.ReleaseSequence <= before.ReleaseSequence)
                throw new UpdatePlanException("update.transactionInvalid");
            if (Exists(journalPath)) throw new UpdatePlanException("resource.busy");
            var pointer = await ReadPointerAsync(token).ConfigureAwait(false);
            if (!Matches(pointer, before)) throw new UpdatePlanException("update.baseMismatch");
            await VerifyVersionAsync(before, token).ConfigureAwait(false);
            var stage = UpdatePaths.Local(stagingRoot, planId);
            await VerifyTreeAsync(stage, after.Files, token).ConfigureAwait(false);
            var record = new InstallTransactionRecord(1, transactionId, planId, 1, "prepared", before, after, pointer, null,
                HealthTarget: healthTarget);
            await PersistAsync(journalPath, record, false, token).ConfigureAwait(false);
            await ObserveAsync(InstallPersistencePoint.Prepared).ConfigureAwait(false);
            return record;
        }, token);

    // 备份必须已由存储服务校验；日志保存完整描述，后续恢复按此描述复验。
    internal Task<InstallTransactionRecord> RecordDataBackupAsync(InstallTransactionRecord expected,
        DatabaseBackupDescriptor backup, CancellationToken token = default) => RunAsync(async () =>
        {
            var record = await RequireCurrentAsync(expected, "prepared", token).ConfigureAwait(false);
            if (backup is null) throw new UpdatePlanException("update.transactionInvalid");
            var next = Clone(record with { Revision = 2, Phase = "dataBackedUp", Backup = backup });
            ValidateRecord(next);
            await PersistAsync(journalPath, next, true, token).ConfigureAwait(false);
            await ObserveAsync(InstallPersistencePoint.DataBackedUp).ConfigureAwait(false);
            return next;
        }, token);

    internal Task<InstallTransactionRecord> RecordDataSnapshotAsync(InstallTransactionRecord expected, PathService paths,
        DataSnapshotDescriptor snapshot, CancellationToken token = default) => RunAsync(async () =>
        {
            var record = await RequireCurrentAsync(expected, "prepared", token).ConfigureAwait(false);
            var verified = await SqliteStore.VerifyDataSnapshotAsync(paths, snapshot.SnapshotId, snapshot, token).ConfigureAwait(false);
            var next = Clone(record with { Revision = 2, Phase = "dataBackedUp", Backup = verified.Database, Snapshot = verified });
            ValidateRecord(next);
            await PersistAsync(journalPath, next, true, token).ConfigureAwait(false);
            await ObserveAsync(InstallPersistencePoint.DataBackedUp).ConfigureAwait(false);
            return next;
        }, token);

    /// <summary>切换前重验两版完整树；取消在替换指针前收敛，替换后先持久完成日志。</summary>
    internal Task<InstallTransactionRecord> SwitchPointerAsync(InstallTransactionRecord expected,
        CancellationToken token = default) => RunAsync(async () =>
        {
            var record = await RequireCurrentAsync(expected, "dataBackedUp", token).ConfigureAwait(false);
            var pointer = await ReadPointerAsync(token).ConfigureAwait(false);
            if (!Same(pointer, record.PreviousPointer)) throw new UpdatePlanException("update.baseMismatch");
            await VerifyVersionAsync(record.Previous, token).ConfigureAwait(false);
            var stage = UpdatePaths.Local(stagingRoot, record.PlanId);
            var candidate = UpdatePaths.Local(versionsRoot, record.Candidate.ReleaseId);
            if (Exists(candidate))
                await VerifyTreeAsync(candidate, record.Candidate.Files, token).ConfigureAwait(false);
            else
            {
                await VerifyTreeAsync(stage, record.Candidate.Files, token).ConfigureAwait(false);
                Directory.CreateDirectory(versionsRoot);
                await UpdatePaths.PublishOwnedDirectoryAsync(stage, candidate, token).ConfigureAwait(false);
            }
            await ObserveAsync(InstallPersistencePoint.VersionPublished).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // 目录发布可能发生于上一进程；替换前再次核对真实内容和基线指针。
            await VerifyVersionAsync(record.Candidate, token).ConfigureAwait(false);
            await FlushCandidateAsync(candidate, record.Candidate.Files, token).ConfigureAwait(false);
            if (!Same(await ReadPointerAsync(token).ConfigureAwait(false), record.PreviousPointer))
                throw new UpdatePlanException("update.baseMismatch");
            await PersistAsync(pointerPath, Pointer(record.Candidate, record.TransactionId), true, token).ConfigureAwait(false);
            await ObserveAsync(InstallPersistencePoint.PointerReplaced).ConfigureAwait(false);
            var next = record with { Revision = 3, Phase = "pointerSwitched" };
            await PersistAsync(journalPath, next, true, CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(InstallPersistencePoint.PointerSwitched).ConfigureAwait(false);
            return next;
        }, token);

    internal Task<InstallTransactionRecord> ReadAsync(CancellationToken token = default) => RunAsync(() => ReadRecordAsync(token), token);

    /// <summary>先固定文件意图，再恢复数据库、文件与旧指针；回退意图落盘后延迟取消。</summary>
    internal Task<InstallTransactionRecord> RestorePreviousAsync(InstallTransactionRecord expected, DataRootLease lease,
        CancellationToken token = default) => RunAsync(async () =>
        {
            var frozen = Clone(expected);
            ValidateRecord(frozen);
            var record = await ReadRecordAsync(token).ConfigureAwait(false);
            if (!Same(record, frozen)) throw new UpdatePlanException("update.transactionStale");
            if (record.Backup is null || record.Phase is "prepared" or "committed") throw new UpdatePlanException("update.transactionPhaseInvalid");
            activeHealthChallenge = null;
            activeHealthApproval = null;
            UpdatePaths.RequireDisjoint(stateRoot, versionsRoot, stagingRoot, Path.GetDirectoryName(pointerPath)!,
                lease.Paths.DataRoot, lease.Paths.BootstrapDirectory);
            using var databaseOwnership = lease.AcquireDatabaseOwnership();
            await VerifyVersionAsync(record.Previous, token).ConfigureAwait(false);
            await RequireRollbackPointerAsync(record, token).ConfigureAwait(false);
            if ((record.Phase is "dataBackedUp" or "pointerSwitched" or "healthy") &&
                !Same(await ReadPointerAsync(token).ConfigureAwait(false), Pointer(record.Candidate, record.TransactionId)))
                throw new UpdatePlanException("update.transactionPhaseInvalid");
            var filePlan = record.FileRestorePlan;
            if (record.Snapshot is not null)
                filePlan = await SqliteStore.PrepareDataSnapshotRestoreOwnedAsync(lease, databaseOwnership,
                    record.TransactionId, record.Snapshot, token, filePlan, record.FileRestore,
                    point => point == DataSnapshotRestorePersistencePoint.Prepared ? ObserveAsync(InstallPersistencePoint.FilesPrepared) : Task.CompletedTask).ConfigureAwait(false);
            if (record.Phase is "dataBackedUp" or "pointerSwitched" or "healthy")
            {
                record = record with { Revision = 4 + record.HealthChecks, Phase = "rollbackStarted", FileRestorePlan = filePlan };
                await PersistAsync(journalPath, record, true, token).ConfigureAwait(false);
                await ObserveAsync(InstallPersistencePoint.RollbackStarted).ConfigureAwait(false);
            }
            // 此后不在数据和指针之间响应取消；重试也只允许固定事务及实际已知指针。
            var restored = await SqliteStore.RestoreDatabaseBackupOwnedAsync(lease, databaseOwnership,
                record.TransactionId, record.Backup, CancellationToken.None, point =>
                    point == DatabaseRestorePersistencePoint.Replaced ? ObserveAsync(InstallPersistencePoint.DatabaseReplaced) : Task.CompletedTask,
                record.DatabaseRestore).ConfigureAwait(false);
            if (record.Phase == "rollbackStarted")
            {
                record = record with { Revision = 5 + record.HealthChecks, Phase = "databaseRestored", DatabaseRestore = restored };
                await PersistAsync(journalPath, record, true, CancellationToken.None).ConfigureAwait(false);
                await ObserveAsync(InstallPersistencePoint.DatabaseRestored).ConfigureAwait(false);
            }
            if (record.Snapshot is not null)
            {
                var files = await SqliteStore.RestoreDataSnapshotFilesOwnedAsync(lease, databaseOwnership,
                    record.FileRestorePlan!, record.Snapshot, CancellationToken.None, record.FileRestore, point =>
                        ObserveAsync(point switch
                        {
                            DataSnapshotRestorePersistencePoint.AssetsRetained => InstallPersistencePoint.AssetsRetained,
                            DataSnapshotRestorePersistencePoint.AssetsRestored => InstallPersistencePoint.AssetsRestored,
                            DataSnapshotRestorePersistencePoint.SettingsRetained => InstallPersistencePoint.SettingsRetained,
                            DataSnapshotRestorePersistencePoint.SettingsRestored => InstallPersistencePoint.SettingsRestored,
                            _ => throw new InvalidOperationException()
                        })).ConfigureAwait(false);
                if (record.Phase == "databaseRestored")
                {
                    record = record with { Revision = 6 + record.HealthChecks, Phase = "filesRestored", FileRestore = files };
                    await PersistAsync(journalPath, record, true, CancellationToken.None).ConfigureAwait(false);
                    await ObserveAsync(InstallPersistencePoint.FilesRestored).ConfigureAwait(false);
                }
            }
            await VerifyVersionAsync(record.Previous, CancellationToken.None).ConfigureAwait(false);
            await FlushCandidateAsync(UpdatePaths.Local(versionsRoot, record.Previous.ReleaseId), record.Previous.Files, CancellationToken.None).ConfigureAwait(false);
            await RequireRollbackPointerAsync(record, CancellationToken.None).ConfigureAwait(false);
            if (record.Phase == "previousPointerRestored") return record;
            if (!Same(await ReadPointerAsync(CancellationToken.None).ConfigureAwait(false), record.PreviousPointer))
            {
                await PersistAsync(pointerPath, record.PreviousPointer, true, CancellationToken.None).ConfigureAwait(false);
                await ObserveAsync(InstallPersistencePoint.PreviousPointerReplaced).ConfigureAwait(false);
            }
            record = record with { Revision = (record.Snapshot is null ? 6 : 7) + record.HealthChecks, Phase = "previousPointerRestored" };
            await PersistAsync(journalPath, record, true, CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(InstallPersistencePoint.PreviousPointerRestored).ConfigureAwait(false);
            return record;
        }, token);

    private async Task RequireRollbackPointerAsync(InstallTransactionRecord record, CancellationToken token)
    {
        var pointer = await ReadPointerAsync(token).ConfigureAwait(false);
        if (Same(pointer, record.PreviousPointer)) return;
        if (record.Phase != "previousPointerRestored" && Same(pointer, Pointer(record.Candidate, record.TransactionId))) return;
        throw new UpdatePlanException("update.pointerInvalid");
    }

    /// <summary>只给出恢复方向，不代替匹配数据恢复、健康检查或业务准入。</summary>
    internal Task<InstallRecoveryAssessment> AssessRecoveryAsync(CancellationToken token = default) => RunAsync(async () =>
        {
            var record = await ReadRecordAsync(token).ConfigureAwait(false);
            var candidatePublished = false;
            try
            {
                var candidate = Path.Combine(versionsRoot, record.Candidate.ReleaseId);
                candidatePublished = File.Exists(candidate) || Directory.Exists(candidate);
                var pointer = await ReadPointerAsync(token).ConfigureAwait(false);
                if (record.Phase == "committed")
                {
                    await RequireCandidateAsync(record, token).ConfigureAwait(false);
                    return new(InstallRecoveryAction.Committed, record, candidatePublished, null);
                }
                await VerifyVersionAsync(record.Previous, token).ConfigureAwait(false);
                if (record.Phase is "rollbackStarted" or "databaseRestored" or "filesRestored" or "previousPointerRestored")
                {
                    await RequireRollbackPointerAsync(record, token).ConfigureAwait(false);
                    return new(record.Phase == "previousPointerRestored" ? InstallRecoveryAction.PreviousVersionRestored : InstallRecoveryAction.ResumeRollback,
                        record, candidatePublished, null);
                }
                if (Same(pointer, Pointer(record.Candidate, record.TransactionId)) && record.Backup is not null)
                {
                    if (record.Phase == "healthy")
                    {
                        try { await VerifyVersionAsync(record.Candidate, token).ConfigureAwait(false); }
                        catch (FileTreeException) { return new(InstallRecoveryAction.RestorePreviousAndData, record, candidatePublished, null); }
                        return new(InstallRecoveryAction.RecheckCandidateHealth, record, candidatePublished, null);
                    }
                    return new(InstallRecoveryAction.RestorePreviousAndData, record, candidatePublished, null);
                }
                if (Same(pointer, record.PreviousPointer))
                {
                    if (record.Phase is "pointerSwitched" or "healthy") return Recovery("update.pointerInvalid");
                    if (candidatePublished) await VerifyTreeAsync(candidate, record.Candidate.Files, token).ConfigureAwait(false);
                    else await VerifyTreeAsync(UpdatePaths.Local(stagingRoot, record.PlanId), record.Candidate.Files, token).ConfigureAwait(false);
                    return new(record.Phase == "prepared" ? InstallRecoveryAction.Prepared : InstallRecoveryAction.ResumeSwitch,
                        record, candidatePublished, null);
                }
                return Recovery("update.pointerInvalid");
            }
            catch (FileTreeException error) { return Recovery(error.Code); }
            catch (UpdatePlanException error) { return Recovery(error.Code); }
            catch (IOException) { return Recovery("update.installationUnavailable"); }
            catch (UnauthorizedAccessException) { return Recovery("update.installationUnavailable"); }

            InstallRecoveryAssessment Recovery(string reason) => new(InstallRecoveryAction.RecoveryRequired, record, candidatePublished, reason);
        }, token);

    private async Task<InstallTransactionRecord> RequireCurrentAsync(InstallTransactionRecord expected, string phase, CancellationToken token)
    {
        var frozen = Clone(expected);
        ValidateRecord(frozen);
        var current = await ReadRecordAsync(token).ConfigureAwait(false);
        if (!Same(current, frozen)) throw new UpdatePlanException("update.transactionStale");
        if (current.Phase != phase) throw new UpdatePlanException("update.transactionPhaseInvalid");
        return current;
    }

    private async Task<InstallTransactionRecord> ReadRecordAsync(CancellationToken token)
    {
        var record = await ReadDocumentAsync<InstallTransactionRecord>(journalPath, MaximumDocumentBytes, token).ConfigureAwait(false);
        ValidateRecord(record);
        return record;
    }

    private async Task<ActiveVersionPointer> ReadPointerAsync(CancellationToken token)
    {
        try
        {
            var pointer = await ReadDocumentAsync<ActiveVersionPointer>(pointerPath, 4096, token).ConfigureAwait(false);
            ValidatePointer(pointer);
            return pointer;
        }
        catch (FileNotFoundException) { throw new UpdatePlanException("update.pointerInvalid"); }
        catch (DirectoryNotFoundException) { throw new UpdatePlanException("update.pointerInvalid"); }
    }

    private static async Task<T> ReadDocumentAsync<T>(string path, int limit, CancellationToken token)
    {
        await using var input = await UpdatePaths.OpenPublishedReadAsync(path, token).ConfigureAwait(false);
        if (input.Length is <= 0 || input.Length > limit) throw new UpdatePlanException("update.transactionInvalid");
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            using var document = StrictJson.Parse(bytes, limit);
            return document.RootElement.Deserialize<T>(JsonOptions) ?? throw new UpdatePlanException("update.transactionInvalid");
        }
        catch (JsonException) { throw new UpdatePlanException("update.transactionInvalid"); }
        catch (TrustValidationException) { throw new UpdatePlanException("update.transactionInvalid"); }
    }

    private static async Task PersistAsync<T>(string path, T value, bool replace, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaximumDocumentBytes) throw new UpdatePlanException("update.transactionInvalid");
        UpdatePaths.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            UpdatePaths.RejectLinks(path);
            if (replace) await UpdatePaths.ReplaceOwnedFileAsync(temporary, path, token).ConfigureAwait(false);
            else File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private Task VerifyVersionAsync(InstallVersion version, CancellationToken token) =>
        VerifyTreeAsync(UpdatePaths.Local(versionsRoot, version.ReleaseId), version.Files, token);
    private static Task VerifyTreeAsync(string directory, IReadOnlyList<ManifestFile> files, CancellationToken token)
    {
        UpdatePaths.RejectLinks(directory);
        if (!Directory.Exists(directory)) throw new FileTreeException("update.targetTreeMismatch");
        return FileTreePlanner.VerifyAsync(directory, files, token);
    }

    private static async Task FlushCandidateAsync(string directory, IReadOnlyList<ManifestFile> files, CancellationToken token)
    {
        var budget = new UpdateIoBudget();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var path = UpdatePaths.Local(directory, file.RelativePath);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                UpdateIoBudget.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await budget.CopyAndHashAsync(stream, null, file.SizeBytes, token).ConfigureAwait(false);
            if (actual.Length != file.SizeBytes || !actual.Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new FileTreeException("update.targetTreeMismatch", file.RelativePath);
            stream.Flush(true);
        }
    }
    private Task ObserveAsync(InstallPersistencePoint point) => persisted?.Invoke(point) ?? Task.CompletedTask;
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), JsonOptions)
        ?? throw new UpdatePlanException("update.transactionInvalid");
    private static bool Same<T>(T first, T second) => JsonSerializer.Serialize(first, JsonOptions) == JsonSerializer.Serialize(second, JsonOptions);
    private static bool Exists(string path) { UpdatePaths.RejectLinks(path); return File.Exists(path) || Directory.Exists(path); }
    private static ActiveVersionPointer Pointer(InstallVersion version, string? transactionId) =>
        new(1, version.ReleaseId, version.ReleaseSequence, version.ManifestSha256, FileTreePlanner.TreeHash(version.Files), transactionId);
    private static bool Matches(ActiveVersionPointer pointer, InstallVersion version) => Same(pointer, Pointer(version, pointer.TransactionId));
    private static InstallVersion FreezeVersion(InstallVersion version)
    {
        if (version is null) throw new UpdatePlanException("update.transactionInvalid");
        ValidateVersion(version);
        var copy = Clone(version);
        ValidateVersion(copy);
        return copy;
    }
    private static void ValidateVersion(InstallVersion version)
    {
        if (version is null || !SafeRelease(version.ReleaseId) || version.ReleaseSequence < 0 || !Hash(version.ManifestSha256) ||
            version.Files is not { Count: > 0 and <= 100_000 } || version.Files.Any(file => file is null || file.Sha256 is null))
            throw new UpdatePlanException("update.transactionInvalid");
        FileTreePlanner.ValidateManifest(version.Files);
        if (version.Files.Any(file => file.RelativePath != UpdatePaths.Normalize(file.RelativePath) ||
                !file.RelativePath.StartsWith("components/", StringComparison.Ordinal) ||
                file.RelativePath.Split('/').Length < 3)) throw new UpdatePlanException("update.transactionInvalid");
    }
    private static void ValidateRecord(InstallTransactionRecord record)
    {
        if (record is null || record.SchemaVersion != 1 || record.PreviousPointer is null)
            throw new UpdatePlanException("update.transactionInvalid");
        ValidateId(record.TransactionId); ValidateId(record.PlanId);
        ValidateVersion(record.Previous); ValidateVersion(record.Candidate); ValidatePointer(record.PreviousPointer);
        if (!Matches(record.PreviousPointer, record.Previous) ||
            record.Previous.ReleaseId.Equals(record.Candidate.ReleaseId, StringComparison.OrdinalIgnoreCase) ||
            record.Candidate.ReleaseSequence <= record.Previous.ReleaseSequence ||
            record.Phase is not ("prepared" or "dataBackedUp" or "pointerSwitched" or "healthy" or "committed" or "rollbackStarted" or "databaseRestored" or "filesRestored" or "previousPointerRestored") ||
            record.Revision != (record.Phase switch { "prepared" => 1, "dataBackedUp" => 2, "pointerSwitched" => 3,
                "healthy" => 3 + record.HealthChecks, "committed" => 4 + record.HealthChecks,
                "rollbackStarted" => 4 + record.HealthChecks, "databaseRestored" => 5 + record.HealthChecks,
                "filesRestored" => 6 + record.HealthChecks, _ => (record.Snapshot is null ? 6 : 7) + record.HealthChecks }) ||
            (record.Phase == "prepared") != (record.Backup is null)) throw new UpdatePlanException("update.transactionInvalid");
        ValidateHealthRecord(record);
        if ((record.Phase is "databaseRestored" or "filesRestored" or "previousPointerRestored") != (record.DatabaseRestore is not null) ||
            record.DatabaseRestore is { } restored && (restored.TransactionId != record.TransactionId ||
                restored.BackupId != record.Backup?.BackupId || !Hash(restored.RetainedDatabaseSha256)))
            throw new UpdatePlanException("update.transactionInvalid");
        var restoringFiles = record.Snapshot is not null && (record.Phase is "rollbackStarted" or "databaseRestored" or "filesRestored" or "previousPointerRestored");
        var filesRestored = record.Snapshot is not null && (record.Phase is "filesRestored" or "previousPointerRestored");
        if (restoringFiles != (record.FileRestorePlan is not null) || filesRestored != (record.FileRestore is not null) ||
            record.Phase == "filesRestored" && record.Snapshot is null ||
            record.FileRestorePlan is { } plan && (plan.TransactionId != record.TransactionId || plan.SnapshotId != record.Snapshot?.SnapshotId || !Hash(plan.ManifestSha256)) ||
            record.FileRestore is { } files && files.Plan != record.FileRestorePlan)
            throw new UpdatePlanException("update.transactionInvalid");
        if (record.Snapshot is { } snapshot)
        {
            ValidateId(snapshot.SnapshotId);
            if (record.Phase == "prepared" || snapshot.SchemaVersion != 1 || snapshot.RootId != record.Backup?.RootId ||
                !Same(snapshot.Database, record.Backup) || snapshot.Files is null || snapshot.Directories is null ||
                snapshot.Files.Count + (long)snapshot.Directories.Count > SqliteStore.MaximumSnapshotEntries ||
                snapshot.TotalSizeBytes is <= 0 or > SqliteStore.MaximumSnapshotContentBytes)
                throw new UpdatePlanException("update.transactionInvalid");
        }
        if (record.Backup is { } backup)
        {
            ValidateId(backup.BackupId); ValidateId(backup.RootId); ValidateId(backup.DatabaseId);
            if (backup.SchemaVersion != 1 || backup.BackendMode is not ("live" or "mock") || backup.CreatedAt == default ||
                backup.SizeBytes is <= 0 or > SqliteStore.MaximumBackupDatabaseBytes || !Hash(backup.Sha256) ||
                backup.Migrations is not { Count: > 0 and <= 4096 } || backup.Migrations.Any(stamp => stamp is null ||
                    string.IsNullOrWhiteSpace(stamp.ModuleId) || stamp.Version < 1 || stamp.Checksum is null || !Hash(stamp.Checksum.ToLowerInvariant())))
                throw new UpdatePlanException("update.transactionInvalid");
        }
    }
    private static void ValidatePointer(ActiveVersionPointer pointer)
    {
        if (pointer is null || pointer.SchemaVersion != 1 || !SafeRelease(pointer.ReleaseId) || pointer.ReleaseSequence < 0 ||
            !Hash(pointer.ManifestSha256) || !Hash(pointer.TreeSha256)) throw new UpdatePlanException("update.pointerInvalid");
        if (pointer.TransactionId is not null) ValidateId(pointer.TransactionId);
    }
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateId(string? id)
    {
        if (id is not { Length: 32 } || id.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new UpdatePlanException("update.transactionInvalid");
    }
    private static bool SafeRelease(string? id)
    {
        if (id is not { Length: > 0 and <= 128 } || !char.IsAsciiLetterOrDigit(id[0]) ||
            id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-'))) return false;
        _ = UpdatePaths.Normalize(id);
        return true;
    }
    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return await action().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Wait();
        try { ownership.Dispose(); }
        finally { gate.Release(); }
    }
}
