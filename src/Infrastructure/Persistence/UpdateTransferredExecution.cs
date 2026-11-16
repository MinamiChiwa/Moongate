using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    internal ActivityBarrier.MaintenanceLease RequireUpdateMaintenance(string id) =>
        updateMaintenance is { } lease && lease.Owner == id ? lease : throw new ActivityBarrierException("update.maintenanceLeaseInvalid");

    internal Task<OperationSnapshot> ReportTransferredUpdateAsync(UpdateInstallIntent intent, ActivityBarrier.MaintenanceLease maintenance,
        string phase, CancellationToken token = default)
    {
        var work = TransferredWork(intent);
        var progress = JsonSerializer.SerializeToElement(new { planId = intent.Plan.PlanId, transactionId = intent.TransactionId, phase }, ContractJson.Options);
        var observed = Get(intent.OperationId, "", true);
        var prepared = PrepareUpdate(observed with { Revision = Next(observed), Result = progress });
        return store.WriteMaintenanceAsync(maintenance, tx =>
        {
            var previous = RequireTransferred(tx, intent, work);
            var next = previous with { Revision = Next(previous), Result = progress };
            if (previous.Revision == observed.Revision) SavePrepared(tx, prepared); else Save(tx, next);
            return next;
        }, token);
    }

    internal Task<OperationSnapshot> FinishTransferredUpdateAsync(UpdateInstallIntent intent, ActivityBarrier.MaintenanceLease maintenance,
        UpdateInstallResult result, ApiProblem? problem = null, CancellationToken token = default)
    {
        var work = TransferredWork(intent);
        var resultJson = JsonSerializer.SerializeToElement(result, ContractJson.Options);
        var observed = Get(intent.OperationId, "", true);
        OperationSnapshot Finish(OperationSnapshot previous) => previous with
        {
            State = problem is null ? OperationState.Succeeded : OperationState.Failed,
            Phase = result.Outcome, Revision = Next(previous), Result = resultJson,
            Problem = problem, Progress = problem is null ? 1 : null, Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            Cancellation = previous.Cancellation.State.KnownValue == CancellationState.Deferred
                ? new(CancellationState.Completed, "safe-update-completed") : previous.Cancellation
        };
        var prepared = SqliteStore.IsTerminal(observed.State.KnownValue) ? null : PrepareUpdate(Finish(observed));
        return store.WriteMaintenanceAsync(maintenance, tx =>
        {
            var previous = Read(tx, intent.OperationId);
            if (SqliteStore.IsTerminal(previous.State.KnownValue))
            {
                if (previous.Kind != "update.apply" || previous.Result is null ||
                    previous.Result.Value.GetRawText() != resultJson.GetRawText() || previous.Problem?.Code != problem?.Code)
                    throw new OperationException("update.recoveryRequired", "原安装操作终态与恢复结果不一致");
                return previous;
            }
            previous = RequireTransferred(tx, intent, work);
            var next = prepared is not null && previous.Revision == observed.Revision ? prepared.Snapshot : Finish(previous);
            if (prepared is not null && previous.Revision == observed.Revision) SavePrepared(tx, prepared); else Save(tx, next);
            return next;
        }, token);
    }

    private static string TransferredWork(UpdateInstallIntent intent) => JsonSerializer.Serialize(
        new UpdateWorkRequest(UpdateAction.Apply, intent.Plan, UpdatePlanETag(intent.Plan)), ContractJson.Options);

    private static OperationSnapshot RequireTransferred(DatabaseTransaction tx, UpdateInstallIntent intent, string expectedWork)
    {
        var previous = Read(tx, intent.OperationId);
        if (previous.Kind != "update.apply" || previous.State.KnownValue is not (OperationState.Running or OperationState.CancelRequested) || previous.Phase != "applying")
            throw new OperationException("update.recoveryRequired", "原安装操作未持有可接管状态");
        var payload = tx.Scalar("SELECT payload FROM platform_update_requests WHERE operation_id=$id", ("$id", intent.OperationId)) as string;
        if (payload != expectedWork)
            throw new OperationException("update.installIntentInvalid", "安装意图与原始请求不一致");
        return previous;
    }

    private static PreparedOperationRecord PrepareUpdate(OperationSnapshot snapshot) =>
        SqliteStore.PrepareOperation(snapshot, [UpdateProgressEvent(snapshot)]);

    private static void SavePrepared(DatabaseTransaction tx, PreparedOperationRecord prepared)
    {
        SqliteStore.SavePreparedOperation(tx, prepared);
        if (SqliteStore.IsTerminal(prepared.Snapshot.State.KnownValue))
            tx.Execute("UPDATE core_module_configuration SET pending_operation=NULL WHERE pending_operation=$id", ("$id", prepared.Snapshot.Id));
    }

    private static void SaveUpdateProgress(DatabaseTransaction tx, OperationSnapshot snapshot) =>
        SqliteStore.AppendEvent(tx, UpdateProgressEvent(snapshot));

    private static StateEvent UpdateProgressEvent(OperationSnapshot snapshot)
    {
        var plan = snapshot.ResourceRefs.Single().Id;
        UInt64String bytes = new(0); UInt64String? total = null; string? component = null; OpenEnum<UpdateSourceKind>? source = null;
        var phase = snapshot.Phase;
        var reason = snapshot.Problem?.Code;
        if (snapshot.Result is { } result && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("phase", out var detail) && detail.ValueKind == JsonValueKind.String) phase = detail.GetString()!;
            if (result.TryGetProperty("verifiedBytes", out var verified) && verified.ValueKind == JsonValueKind.String) bytes = UInt64String.Parse(verified.GetString()!);
            if (result.TryGetProperty("totalBytes", out var length) && length.ValueKind == JsonValueKind.String) total = UInt64String.Parse(length.GetString()!);
            if (result.TryGetProperty("source", out var selected) && selected.ValueKind == JsonValueKind.String) source = new(selected.GetString()!);
            if (result.TryGetProperty("componentId", out var item) && item.ValueKind == JsonValueKind.String) component = item.GetString();
            if (result.TryGetProperty("failureCode", out var failure) && failure.ValueKind == JsonValueKind.String) reason = failure.GetString();
        }
        var progress = new UpdateProgress(plan, phase, source, bytes, total, component, reason, snapshot.Id, snapshot.Revision);
        return new("update-progress-" + snapshot.Id + "-" + snapshot.Revision, "update-progress", snapshot.Id,
            snapshot.Revision, "update.progress", JsonSerializer.SerializeToElement(progress, ContractJson.Options));
    }
}
