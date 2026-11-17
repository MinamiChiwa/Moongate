using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    internal ActivityBarrier.MaintenanceLease RequireUpdateMaintenance(string id) =>
        updateMaintenance is { } lease && lease.Owner == id ? lease : throw new ActivityBarrierException("update.maintenanceLeaseInvalid");

    /// <summary>
    /// 安装已交给替换器：在维护屏障内把安装操作记为成功结束（阶段 <c>handedOff</c>），宿主随后退出。
    /// 替换、健康确认与回退的结果由替换器写入工具根的结果文件，重启后的宿主据此显示。
    /// </summary>
    internal async Task<OperationSnapshot> FinishUpdateHandoffAsync(string id, JsonElement result, CancellationToken token = default)
    {
        await updateExecutionGate.WaitAsync(token);
        try
        {
            return await WriteUpdateAsync(id, tx =>
            {
                var previous = Read(tx, id);
                RequireUpdate(previous);
                if (previous.Kind != "update.apply" || previous.Phase != "applying" || updateMaintenance?.Owner != id)
                    throw new OperationException("resource.busy", "安装尚未进入可交接阶段");
                var next = previous with
                {
                    State = OperationState.Succeeded, Phase = "handedOff", Revision = Next(previous), Result = result, Progress = 1,
                    Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() }
                };
                Save(tx, next);
                return next;
            }, token);
        }
        finally { updateExecutionGate.Release(); }
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
