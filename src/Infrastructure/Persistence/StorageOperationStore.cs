using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public Task<OperationSnapshot> CancelStorageAsync(string id, string principal, bool owner, string? reason,
        ActivityBarrier.MaintenanceLease? maintenance, CancellationToken ct = default)
    {
        if (reason is { Length: > 256 } || reason?.Any(char.IsControl) == true) throw new OperationException("request.invalid", "取消原因最多 256 字符且不能包含控制字符");
        return maintenance is null ? store.WriteAsync(tx => Cancel(tx, id, principal, owner, reason), ct) :
            store.WriteMaintenanceAsync(maintenance, tx => Cancel(tx, id, principal, owner, reason), ct);
    }
    public OperationSnapshot? StorageRetry(string principal, string kind, string resource, string hash, string? key)
    {
        FileTransferStore.Key(key);
        var args = new (string, object?)[] { ("$p", principal), ("$route", StorageRoute(kind)), ("$resource", resource), ("$key", key), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()) };
        var id = store.ReadScalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$resource AND key=$key AND expires_at>$now", args) as string;
        if (id is null) return null;
        if ((string?)store.ReadScalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$resource AND key=$key", args) != hash)
            throw new OperationException("idempotency.conflict", "幂等键已用于其他存储请求");
        return Get(id, principal, false);
    }

    public Task<OperationAdmission> AdmitStorageAsync(string principal, string kind, string resource, string hash, string? key,
        JsonElement request, bool available, CancellationToken ct = default)
    {
        FileTransferStore.Key(key); var route = StorageRoute(kind);
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var args = new (string, object?)[] { ("$p", principal), ("$route", route), ("$resource", resource), ("$key", key) };
            if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$resource AND key=$key", args) is string id)
            {
                if ((string?)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$resource AND key=$key", args) != hash)
                    throw new OperationException("idempotency.conflict", "幂等键已用于其他存储请求");
                return new OperationAdmission(Read(tx, id), false);
            }
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0 AND json_extract(snapshot,'$.kind') LIKE 'storage.%'")! > 0)
                throw new OperationException("resource.busy", "已有存储操作正在执行");
            if (!available || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "存储操作队列或幂等记录已满");
            var operation = new OperationSnapshot("op-" + Guid.NewGuid().ToString("N"), kind, principal, [new("storage", resource)],
                OperationState.Queued, "queued", new(1), null, new(now, null, null), request.Clone(), null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, operation);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,$resource,$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", route), ("$resource", resource), ("$key", key), ("$hash", hash), ("$op", operation.Id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds()));
            return new OperationAdmission(operation, true);
        }, ct);
    }

    public Task<OperationSnapshot?> BeginStorageAsync(string id, string phase, ActivityBarrier.MaintenanceLease? maintenance = null)
    {
        Func<DatabaseTransaction, OperationSnapshot?> save = tx =>
        {
            var previous = Read(tx, id);
            if (SqliteStore.IsTerminal(previous.State.KnownValue) || previous.Cancellation.State.KnownValue != CancellationState.None) return null;
            if (previous.Kind is not ("storage.backup" or "storage.restore" or "storage.migrate" or "storage.moduleDelete"))
                throw new OperationException("request.invalid", "操作不是存储任务");
            var next = previous with { State = OperationState.Running, Phase = phase, Revision = Next(previous),
                Timestamps = previous.Timestamps with { StartedAt = previous.Timestamps.StartedAt ?? clock.GetUtcNow() } };
            Save(tx, next); return next;
        };
        return maintenance is null ? store.WriteAsync(save) : store.WriteMaintenanceAsync(maintenance, save);
    }

    public Task<OperationSnapshot> FinishStorageAsync(string id, JsonElement? result, ApiProblem? problem = null,
        bool cancelled = false, ActivityBarrier.MaintenanceLease? maintenance = null, StorageBackupSummary? backup = null)
    {
        Func<DatabaseTransaction, OperationSnapshot> save = tx =>
        {
            var previous = Read(tx, id);
            if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
            var next = previous with { State = cancelled ? OperationState.Cancelled : problem is null ? OperationState.Succeeded : OperationState.Failed,
                Phase = cancelled ? "cancelled" : problem is null ? "succeeded" : "failed", Revision = Next(previous),
                Result = result?.Clone(), Problem = problem, Progress = problem is null && !cancelled ? 1 : null,
                Cancellation = previous.Cancellation.State.KnownValue == CancellationState.None ? previous.Cancellation : new(CancellationState.Completed, previous.Cancellation.Reason),
                Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() } };
            Save(tx, next);
            if (backup is not null) SqliteStore.AppendEvent(tx, new("backup-" + backup.Id + "-" + backup.Revision, "storage-backup", backup.Id,
                backup.Revision, "storage.backup.changed", JsonSerializer.SerializeToElement(new StorageBackupChanged(previous.Owner, backup), ContractJson.Options)));
            return next;
        };
        return maintenance is null ? store.WriteAsync(save) : store.WriteMaintenanceAsync(maintenance, save);
    }

    internal static string StorageRoute(string kind) => kind switch
    {
        "storage.backup" => "/api/v1/storage/backups", "storage.restore" => "/api/v1/storage/restore-plans/{id}/execute",
        "storage.migrate" => "/api/v1/storage/migration-plans/{id}/execute",
        "storage.moduleDelete" => "/api/v1/storage/module-deletion-plans/{id}/execute", _ => throw new OperationException("request.invalid", "存储操作类型无效")
    };

    internal Task<OperationSnapshot> RecoverStorageReceiptAsync(DataMaintenanceRecord record, StorageMaintenanceResult result)
        => store.WriteAsync(tx =>
        {
            var json = tx.Scalar("SELECT snapshot FROM core_operations WHERE id=$id", ("$id", record.Operation.Id)) as string;
            var previous = json is null ? null : JsonSerializer.Deserialize<OperationSnapshot>(json, ContractJson.Options)!;
            var data = JsonSerializer.SerializeToElement(result, ContractJson.Options);
            if (previous is not null && SqliteStore.IsTerminal(previous.State.KnownValue))
            {
                if (previous.Kind != record.Operation.Kind || previous.Owner != record.Operation.Owner || previous.Result is null ||
                    !JsonElement.DeepEquals(previous.Result.Value, data)) throw new StorageException("storage.assetDamaged", "数据维护回执与固定事务不一致");
                return previous;
            }
            var operation = record.Operation with { State = record.Problem is null ? OperationState.Succeeded : OperationState.Failed,
                Phase = record.Problem is null ? "succeeded" : "failed", Progress = record.Problem is null ? 1 : null,
                Revision = previous is null ? new(1) : Next(previous), Result = data, Problem = record.Problem,
                Timestamps = record.Operation.Timestamps with { CompletedAt = clock.GetUtcNow() },
                Cancellation = record.Operation.Cancellation.State.KnownValue == CancellationState.None ? record.Operation.Cancellation :
                    new(CancellationState.Completed, record.Operation.Cancellation.Reason) };
            Save(tx, operation);
            var route = StorageRoute(operation.Kind); var resource = record.Preview.Plan.Id;
            if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE principal=$owner AND route=$route AND resource=$resource AND key=$key",
                ("$owner", operation.Owner), ("$route", route), ("$resource", resource), ("$key", record.Key)) is string prior && prior != operation.Id)
                throw new StorageException("storage.assetDamaged", "数据维护幂等身份已被替换");
            tx.Execute("INSERT INTO core_idempotency VALUES($owner,$route,$resource,$key,$hash,$id,$expires) ON CONFLICT DO NOTHING",
                ("$owner", operation.Owner), ("$route", route), ("$resource", resource), ("$key", record.Key), ("$hash", record.RequestHash),
                ("$id", operation.Id), ("$expires", (clock.GetUtcNow() + KeyRetention).ToUnixTimeMilliseconds()));
            var plan = record.Preview with { Plan = record.Preview.Plan with { State = result.State, Revision = new(2) } };
            tx.Execute("INSERT INTO platform_storage_maintenance_plans VALUES($id,$owner,$kind,$expires,$record) ON CONFLICT(id) DO UPDATE SET record=excluded.record",
                ("$id", plan.Plan.Id), ("$owner", plan.Plan.Owner), ("$kind", plan.Plan.Kind), ("$expires", (clock.GetUtcNow() + KeyRetention).ToUnixTimeMilliseconds()),
                ("$record", JsonSerializer.Serialize(plan, ContractJson.Options)));
            SqliteStore.AppendEvent(tx, new("maintenance-" + record.Id, "storage-maintenance", resource, new(2), "storage.maintenance.changed",
                JsonSerializer.SerializeToElement(new StorageMaintenanceChanged(operation.Owner, operation.Id, result), ContractJson.Options)));
            return operation;
        });
}
