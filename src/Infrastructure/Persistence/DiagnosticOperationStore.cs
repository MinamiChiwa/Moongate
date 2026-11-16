using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public Task<OperationAdmission> AdmitDiagnosticExportAsync(string principal, bool owner, DiagnosticExportRequest request,
        string? key, bool queueAvailable, CancellationToken ct = default)
    {
        if (request.Sessions is not { Count: >= 1 and <= 10 } ||
            request.Sessions.Any(x => x is null || x.Id is null || !Regex.IsMatch(x.Id, @"\A[0-9a-f]{32}\z")) ||
            request.Sessions.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != request.Sessions.Count)
            throw new OperationException("request.invalid", "诊断导出须指定 1–10 个不同会话及其修订");
        if (key is null) throw new OperationException("idempotency.required", "诊断导出需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "诊断导出幂等键无效");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options)));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            const string match = "principal=$p AND route='/api/v1/diagnostics/exports' AND resource='diagnostics' AND key=$key";
            var args = new (string, object?)[] { ("$p", principal), ("$key", key) };
            if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE " + match, args) is string existing)
            {
                if ((string?)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE " + match, args) != hash)
                    throw new OperationException("idempotency.conflict", "幂等键已用于其他诊断导出");
                return new OperationAdmission(Read(tx, existing), false);
            }
            if (!queueAvailable || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "诊断导出队列或幂等记录已满");
            var id = "op-" + Guid.NewGuid().ToString("N");
            var plan = DiagnosticStore.PinSources(tx, id, principal, owner, request, now);
            var snapshot = new OperationSnapshot(id, "diagnostics.export", principal,
                plan.Sessions.Select(x => new ResourceReference("diagnostic-session", x.Id)).ToArray(),
                OperationState.Queued, "queued", new(1), null, new(now, null, null),
                JsonSerializer.SerializeToElement(plan, ContractJson.Options), null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, snapshot);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,'/api/v1/diagnostics/exports','diagnostics',$key,$hash,$op,$expiry)",
                ("$p", principal), ("$key", key), ("$hash", hash), ("$op", id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds()));
            return new OperationAdmission(snapshot, true);
        }, ct);
    }
    internal Task<OperationSnapshot?> BeginDiagnosticExportAsync(string id, CancellationToken ct = default) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        if (previous.Kind != "diagnostics.export") throw new OperationException("resource.notFound", "诊断导出操作不存在");
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
        if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "诊断导出已被领取");
        var next = previous with { State = OperationState.Running, Phase = "exporting", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        Save(tx, next); return next;
    }, ct);
    internal Task<bool> FinishDiagnosticExportAsync(string id, DiagnosticExportArtifact? artifact, ApiProblem? problem = null,
        bool interrupted = false, bool cancelled = false, CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        var next = previous with { State = interrupted ? OperationState.Interrupted : cancelled ? OperationState.Cancelled : problem is null ? OperationState.Succeeded : OperationState.Failed,
            Phase = interrupted ? "interrupted" : cancelled ? "cancelled" : problem is null ? "succeeded" : "failed", Revision = Next(previous), Problem = problem,
            Result = artifact is null ? null : JsonSerializer.SerializeToElement(artifact, ContractJson.Options),
            Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() }, EffectStatus = EffectStatus.None,
            Cancellation = cancelled ? new(CancellationState.Completed, "cancelled") : previous.Cancellation };
        Save(tx, next); return true;
    }, ct);
}
