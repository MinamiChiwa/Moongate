using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public OperationSnapshot? FileExportRetry(string principal, FileExportRequest request, string? key)
    {
        FileTransferStore.Key(key);
        var row = store.ReadScalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route='/api/v1/exports' AND resource='files' AND key=$key AND expires_at>$now",
            ("$p", principal), ("$key", key), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        if (row is null) return null;
        var hash = store.ReadScalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route='/api/v1/exports' AND resource='files' AND key=$key",
            ("$p", principal), ("$key", key)) as string;
        if (hash != FileTransferStore.Hash(request)) throw new OperationException("idempotency.conflict", "幂等键已用于其他文件导出");
        return Get(row, principal, false);
    }
    internal Task<OperationAdmission> AdmitFileExportAsync(string principal, FileExportRequest request, FileExportPlan plan,
        string key, bool available, CancellationToken ct) => store.WriteAsync(tx =>
    {
        var now = clock.GetUtcNow();
        tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
        const string match = "principal=$p AND route='/api/v1/exports' AND resource='files' AND key=$key";
        var args = new (string, object?)[] { ("$p", principal), ("$key", key) };
        var hash = FileTransferStore.Hash(request);
        if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE " + match, args) is string existing)
        {
            if ((string?)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE " + match, args) != hash)
                throw new OperationException("idempotency.conflict", "幂等键已用于其他文件导出");
            return new OperationAdmission(Read(tx, existing), false);
        }
        if (!available || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
            (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
            throw new OperationException("resource.capacityExceeded", "文件导出队列或幂等记录已满");
        var id = "op-" + Guid.NewGuid().ToString("N");
        foreach (var source in plan.Sources)
        {
            var asset = source.Asset;
            var found = tx.Scalar("""
                SELECT a.sha256 FROM platform_assets a JOIN platform_asset_blobs b ON a.sha256=b.sha256
                WHERE a.id=$id AND a.revision=$revision AND a.size_bytes=$size AND a.module_id=$module AND a.scope=$scope AND b.state='ready'
                """, ("$id", asset.Id), ("$revision", asset.Revision.ToString()), ("$size", (long)asset.SizeBytes.Value),
                ("$module", asset.ModuleId), ("$scope", asset.Scope)) as string;
            if (found != asset.Sha256) throw new OperationException("storage.assetDamaged", "源资产在导出受理前已变化或被删除");
            var pin = Guid.NewGuid().ToString("N");
            tx.Execute("INSERT INTO platform_asset_pins VALUES($pin,$asset,$owner,$created)", ("$pin", pin), ("$asset", asset.Id),
                ("$owner", "file-export-source:" + id), ("$created", now.ToString("O")));
            tx.Execute("INSERT INTO platform_file_export_sources VALUES($op,$asset,$pin)", ("$op", id), ("$asset", asset.Id), ("$pin", pin));
        }
        plan = plan with { OperationId = id };
        var snapshot = new OperationSnapshot(id, "files.export", principal, plan.Sources.Select(x => new ResourceReference("asset", x.Asset.Id)).ToArray(),
            OperationState.Queued, "queued", new(1), null, new(now, null, null), JsonSerializer.SerializeToElement(plan, ContractJson.Options),
            null, new(CancellationState.None, null), EffectStatus.None);
        Save(tx, snapshot);
        tx.Execute("INSERT INTO core_idempotency VALUES($p,'/api/v1/exports','files',$key,$hash,$op,$expiry)",
            ("$p", principal), ("$key", key), ("$hash", hash), ("$op", id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds()));
        return new OperationAdmission(snapshot, true);
    }, ct);
    internal Task<OperationSnapshot?> BeginFileExportAsync(string id) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
        if (previous.Kind != "files.export" || previous.State.KnownValue != OperationState.Queued)
            throw new OperationException("resource.busy", "文件导出已被领取");
        var next = previous with { State = OperationState.Running, Phase = "packing", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        Save(tx, next); return next;
    });
    internal Task<bool> FinishFileExportAsync(string id, FileExportArtifact? artifact, ApiProblem? problem = null,
        bool interrupted = false, bool cancelled = false) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        var next = previous with { State = interrupted ? OperationState.Interrupted : cancelled ? OperationState.Cancelled :
                problem is null ? OperationState.Succeeded : OperationState.Failed,
            Phase = interrupted ? "interrupted" : cancelled ? "cancelled" : problem is null ? "succeeded" : "failed",
            Revision = Next(previous), Result = artifact is null ? null : JsonSerializer.SerializeToElement(artifact, ContractJson.Options), Problem = problem,
            EffectStatus = EffectStatus.None, Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            Cancellation = cancelled ? new(CancellationState.Completed, "cancelled") : previous.Cancellation };
        Save(tx, next); return true;
    });
}
