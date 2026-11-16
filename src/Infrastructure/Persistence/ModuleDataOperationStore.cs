using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public void ForgetModuleHistory(DatabaseTransaction tx, IReadOnlyList<string> kinds, IReadOnlyList<string> eventTypes)
    {
        var kindsJson = JsonSerializer.Serialize(kinds, ContractJson.Options);
        var typesJson = JsonSerializer.Serialize(eventTypes, ContractJson.Options);
        const string ids = "SELECT id FROM core_operations WHERE terminal=1 AND json_extract(snapshot,'$.kind') IN (SELECT value FROM json_each($kinds))";
        tx.Execute("DELETE FROM core_state_projection WHERE event_type IN (SELECT value FROM json_each($types)) OR event_type='operation.changed' AND resource_id IN (" + ids + ")",
            ("$types", typesJson), ("$kinds", kindsJson));
        tx.Execute("DELETE FROM core_outbox WHERE event_type IN (SELECT value FROM json_each($types)) OR event_type='operation.changed' AND resource_id IN (" + ids + ")",
            ("$types", typesJson), ("$kinds", kindsJson));
        tx.Execute("DELETE FROM core_idempotency WHERE operation_id IN (" + ids + ")", ("$kinds", kindsJson));
        tx.Execute("DELETE FROM core_operations WHERE id IN (" + ids + ")", ("$kinds", kindsJson));
    }

    internal OperationSnapshot CompleteModuleDataDeletion(DatabaseTransaction tx, string id, StorageModuleDeletionResult result)
    {
        var previous = Read(tx, id);
        if (previous.Kind != "storage.moduleDelete" || previous.State.KnownValue != OperationState.Running &&
            previous.Cancellation.State.KnownValue != CancellationState.Deferred)
            throw new OperationException("operation.notCancellable", "模块数据删除未进入提交阶段");
        var next = previous with { State = OperationState.Succeeded, Phase = "succeeded", Revision = Next(previous), Progress = 1,
            Result = JsonSerializer.SerializeToElement(result, ContractJson.Options), EffectStatus = EffectStatus.None,
            Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            Cancellation = previous.Cancellation.State.KnownValue == CancellationState.None ? previous.Cancellation : new(CancellationState.Completed, previous.Cancellation.Reason) };
        Save(tx, next);
        SqliteStore.AppendEvent(tx, new("module-data-" + result.PlanId, "storage-module-data", result.ModuleId,
            result.DataRevision, "storage.module-data.changed", JsonSerializer.SerializeToElement(new StorageModuleDataChanged(previous.Owner, id, result), ContractJson.Options)));
        return next;
    }
}
