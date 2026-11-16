using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MabiTool.Infrastructure.Updates;

internal sealed record InstallHealthModule(string Id, string Version);
internal sealed record InstallHealthTarget(string HostVersion, Version MinimumHostApi, Version MaximumHostApiExclusive,
    string RootId, string DatabaseId, string BackendMode, IReadOnlyList<DatabaseMigrationStamp> Migrations,
    IReadOnlyList<InstallHealthModule> Modules);
internal sealed record InstallHealthEvidence(string TransactionId, string Challenge, string InstanceId,
    string HostVersion, string ApiVersion, DatabaseHealthResult Database, IReadOnlyList<InstallHealthModule> Modules);

internal sealed class InstallHealthChallenge(InstallTransactionRecord record, string nonce, long startedAt)
{
    private readonly InstallTransactionRecord record = InstallTransaction.CopyHealthRecord(record);
    internal InstallTransactionRecord Record => InstallTransaction.CopyHealthRecord(record);
    internal string Nonce { get; } = nonce;
    internal long StartedAt { get; } = startedAt;
}

internal sealed class InstallHealthApproval(InstallTransactionRecord record, long startedAt)
{
    private readonly InstallTransactionRecord record = InstallTransaction.CopyHealthRecord(record);
    internal InstallTransactionRecord Record => InstallTransaction.CopyHealthRecord(record);
    internal long StartedAt { get; } = startedAt;
}

internal sealed partial class InstallTransaction
{
    private readonly TimeProvider healthTime = TimeProvider.System;
    private InstallHealthChallenge? activeHealthChallenge;
    private InstallHealthApproval? activeHealthApproval;

    internal InstallTransaction(string installationRoot, TimeProvider time,
        Func<InstallPersistencePoint, Task>? persisted = null) : this(installationRoot, persisted) => healthTime = time;

    // 授权绑定此协调器实例及当前日志修订，重开进程必须发起新检查。
    internal Task<InstallHealthChallenge> BeginHealthCheckAsync(InstallTransactionRecord expected,
        CancellationToken token = default, UpdateHandoffProgress? progress = null) => RunAsync(async () =>
        {
            activeHealthChallenge = null;
            activeHealthApproval = null;
            var record = await RequireHealthCurrentAsync(expected, token).ConfigureAwait(false);
            progress?.SetStage("challenge.candidateTree");
            await RequireCandidateAsync(record, token).ConfigureAwait(false);
            progress?.SetStage("challenge.previousTree");
            await VerifyVersionAsync(record.Previous, token).ConfigureAwait(false);
            return activeHealthChallenge = new(record, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), healthTime.GetTimestamp());
        }, token);

    // 调用方须先完成对宿主进程及响应通道的认证；此步骤复核固定目标和实际安装内容。
    internal Task<InstallHealthApproval> RecordHealthyAsync(InstallHealthChallenge challenge, InstallHealthEvidence evidence,
        CancellationToken token = default, UpdateHandoffProgress? progress = null) => RunAsync(async () =>
        {
            if (challenge is null || !ReferenceEquals(challenge, activeHealthChallenge)) throw HealthInvalid();
            RequireHealthTime(challenge.StartedAt);
            var record = await RequireHealthCurrentAsync(challenge.Record, token).ConfigureAwait(false);
            var fixedEvidence = Clone(evidence ?? throw HealthInvalid());
            if (fixedEvidence.Challenge != challenge.Nonce) throw HealthInvalid();
            ValidateHealthEvidence(record, fixedEvidence);
            progress?.SetStage("recordHealthy.candidateTree");
            await RequireCandidateAsync(record, token).ConfigureAwait(false);
            progress?.SetStage("recordHealthy.previousTree");
            await VerifyVersionAsync(record.Previous, token).ConfigureAwait(false);
            RequireHealthTime(challenge.StartedAt);
            if (record.HealthChecks >= 4096) throw HealthInvalid();
            var next = record with { Revision = record.Revision + 1, Phase = "healthy", Health = fixedEvidence, HealthChecks = record.HealthChecks + 1 };
            ValidateRecord(next);
            progress?.SetStage("recordHealthy.persist");
            await PersistAsync(journalPath, next, true, token).ConfigureAwait(false);
            activeHealthChallenge = null;
            activeHealthApproval = null;
            await ObserveAsync(InstallPersistencePoint.Healthy).ConfigureAwait(false);
            return activeHealthApproval = new(next, challenge.StartedAt);
        }, token);

    internal Task<InstallTransactionRecord> CommitAsync(InstallHealthApproval approval,
        CancellationToken token = default, UpdateHandoffProgress? progress = null) => RunAsync(async () =>
        {
            if (approval is null || !ReferenceEquals(approval, activeHealthApproval)) throw HealthInvalid();
            var healthy = approval.Record;
            var committed = healthy with { Phase = "committed", Revision = healthy.Revision + 1 };
            var current = await ReadRecordAsync(token).ConfigureAwait(false);
            if (!Same(current, healthy) && !Same(current, committed)) throw new UpdatePlanException("update.transactionStale");
            progress?.SetStage("commit.candidateTree");
            await RequireCandidateAsync(current, token).ConfigureAwait(false);
            if (current.Phase == "committed") return current;
            RequireHealthTime(approval.StartedAt);
            progress?.SetStage("commit.previousTree");
            await VerifyVersionAsync(current.Previous, token).ConfigureAwait(false);
            RequireHealthTime(approval.StartedAt);
            progress?.SetStage("commit.persist");
            await PersistAsync(journalPath, committed, true, token).ConfigureAwait(false);
            // 原子替换完成后，先报告已提交；迟到取消不回退已经持久提交的业务版本。
            await ObserveAsync(InstallPersistencePoint.Committed).ConfigureAwait(false);
            return committed;
        }, token);

    private async Task<InstallTransactionRecord> RequireHealthCurrentAsync(InstallTransactionRecord expected, CancellationToken token)
    {
        var frozen = Clone(expected);
        ValidateRecord(frozen);
        var current = await ReadRecordAsync(token).ConfigureAwait(false);
        if (!Same(current, frozen)) throw new UpdatePlanException("update.transactionStale");
        if (current.HealthTarget is null || current.Phase is not ("pointerSwitched" or "healthy"))
            throw new UpdatePlanException("update.transactionPhaseInvalid");
        return current;
    }

    private async Task RequireCandidateAsync(InstallTransactionRecord record, CancellationToken token)
    {
        if (!Same(await ReadPointerAsync(token).ConfigureAwait(false), Pointer(record.Candidate, record.TransactionId)))
            throw new UpdatePlanException("update.pointerInvalid");
        await VerifyVersionAsync(record.Candidate, token).ConfigureAwait(false);
    }

    private void RequireHealthTime(long startedAt)
    {
        var elapsed = healthTime.GetElapsedTime(startedAt);
        if (elapsed < TimeSpan.Zero || elapsed >= UpdateHandoffProtocol.Timeout) throw new UpdatePlanException("update.healthExpired");
    }

    internal static InstallTransactionRecord CopyHealthRecord(InstallTransactionRecord record) => Clone(record);

    private static InstallHealthTarget FreezeHealthTarget(InstallHealthTarget target)
    {
        if (target is null || !Version.TryParse(target.HostVersion, out _) || target.MinimumHostApi is null ||
            target.MaximumHostApiExclusive is null || target.MinimumHostApi >= target.MaximumHostApiExclusive ||
            target.BackendMode is not ("live" or "mock") || target.Migrations is not { Count: >= 4 and <= 4096 } ||
            target.Modules is not { Count: > 0 and <= 128 }) throw HealthInvalid();
        ValidateId(target.RootId); ValidateId(target.DatabaseId);
        var frozen = Clone(target);
        var migrations = frozen.Migrations.OrderBy(item => item?.ModuleId, StringComparer.Ordinal).ThenBy(item => item?.Version).ToArray();
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stamp in migrations)
        {
            if (stamp is null || !ModuleId(stamp.ModuleId) || stamp.Version != versions.GetValueOrDefault(stamp.ModuleId) + 1 ||
                stamp.Checksum is not { Length: 64 } || !stamp.Checksum.All(Uri.IsHexDigit)) throw HealthInvalid();
            versions[stamp.ModuleId] = stamp.Version;
        }
        if (versions.GetValueOrDefault("core") < 4 || frozen.Modules.Any(item => item is null || !ModuleId(item.Id) || !Version.TryParse(item.Version, out _)) ||
            frozen.Modules.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != frozen.Modules.Count ||
            frozen.Modules.All(item => item.Id != "core")) throw HealthInvalid();
        return frozen with { Migrations = Array.AsReadOnly(migrations), Modules = Array.AsReadOnly(frozen.Modules.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray()) };
    }

    private static void ValidateHealthEvidence(InstallTransactionRecord record, InstallHealthEvidence evidence)
    {
        var target = record.HealthTarget ?? throw HealthInvalid();
        if (evidence is null || evidence.TransactionId != record.TransactionId || !Hash(evidence.Challenge) ||
            evidence.HostVersion != target.HostVersion || !Version.TryParse(evidence.ApiVersion, out var api) ||
            api < target.MinimumHostApi || api >= target.MaximumHostApiExclusive || evidence.Database is null ||
            evidence.Database.RootId != target.RootId || evidence.Database.DatabaseId != target.DatabaseId || evidence.Database.BackendMode != target.BackendMode ||
            evidence.Database.Migrations is null || !evidence.Database.Migrations.SequenceEqual(target.Migrations) ||
            evidence.Modules is null || !evidence.Modules.SequenceEqual(target.Modules)) throw HealthInvalid();
        ValidateId(evidence.InstanceId);
    }

    private static void ValidateHealthRecord(InstallTransactionRecord record)
    {
        if (record.HealthChecks is < 0 or > 4096 || (record.Health is not null) != (record.HealthChecks > 0) ||
            record.Phase is "healthy" or "committed" && record.Health is null ||
            record.Phase is "prepared" or "dataBackedUp" or "pointerSwitched" && record.HealthChecks != 0) throw HealthInvalid();
        if (record.HealthTarget is { } target)
        {
            if (!Same(target, FreezeHealthTarget(target)) || record.Backup is { } backup &&
                (target.RootId != backup.RootId || target.DatabaseId != backup.DatabaseId || target.BackendMode != backup.BackendMode)) throw HealthInvalid();
        }
        if (record.Health is { } evidence) ValidateHealthEvidence(record, evidence);
    }

    private static bool ModuleId(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,63}\z");
    private static UpdatePlanException HealthInvalid() => new("update.healthInvalid");
}
