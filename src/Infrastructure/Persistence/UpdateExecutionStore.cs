using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    private readonly SemaphoreSlim updateExecutionGate = new(1, 1);
    private ActivityBarrier.MaintenanceLease? updateMaintenance;

    // 本次进程的执行队列领取任务；恢复操作不会重新进入执行队列。
    internal Task<OperationSnapshot?> BeginUpdateAsync(string id, CancellationToken token = default) =>
        store.WriteAsync<OperationSnapshot?>(tx =>
        {
            var previous = Read(tx, id);
            RequireUpdate(previous);
            if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
            if (previous.State.KnownValue != OperationState.Queued)
                throw new OperationException("resource.busy", "更新操作已被领取");
            var phase = previous.Kind switch
            {
                "update.check" => "checking", "update.download" => "preparing", _ => "waitingForIdle"
            };
            var next = previous with
            {
                State = OperationState.Running, Phase = phase, Revision = Next(previous),
                Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() }
            };
            Save(tx, next);
            return next;
        }, token);

    internal Task<bool> ReportUpdateDownloadAsync(string id, string phase, JsonElement status, CancellationToken token = default)
    {
        if (phase is not ("preparing" or "downloading" or "verifying")) throw new ArgumentOutOfRangeException(nameof(phase));
        return store.WriteAsync(tx =>
        {
            var previous = Read(tx, id);
            if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
            if (previous.Kind != "update.download" || previous.State.KnownValue != OperationState.Running)
                throw new OperationException("resource.busy", "下载尚未进入执行阶段");
            Save(tx, previous with { Phase = phase, Result = status, Revision = Next(previous), Progress = null });
            return true;
        }, token);
    }

    // 由执行器在每次尝试前清理到期资源租约；空闲竞争失败时不占用维护权。
    internal async Task<bool> TryBeginUpdateMaintenanceAsync(string id, ResourceLeaseCoordinator leases,
        CancellationToken token = default)
    {
        await updateExecutionGate.WaitAsync(token);
        try
        {
            var previous = store.GetOperation(id) ?? throw new OperationException(ApiErrors.ResourceNotFound, "操作不存在");
            RequireUpdate(previous);
            if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
            if (previous.Kind != "update.apply" || previous.Phase != "waitingForIdle")
                throw new OperationException("resource.busy", "更新操作未进入等待安装阶段");
            leases.SweepExpired();
            var maintenance = store.Activities.TryEnterMaintenance(id);
            if (maintenance is null) return false;
            try
            {
                await store.WriteMaintenanceAsync(maintenance, tx =>
                {
                    var current = Read(tx, id);
                    var work = JsonSerializer.Deserialize<UpdateWorkRequest>((string)tx.Scalar(
                        "SELECT payload FROM platform_update_requests WHERE operation_id=$id", ("$id", id))!, ContractJson.Options)!;
                    if (work.Plan is null || work.Plan.ExpiresAt <= clock.GetUtcNow())
                        throw new OperationException("update.planStale", "等待期间更新计划已过期，须重新规划");
                    var applying = current with { Phase = "applying", Revision = Next(current) };
                    Save(tx, applying);
                    return true;
                }, token);
                updateMaintenance = maintenance;
                return true;
            }
            catch { maintenance.Dispose(); throw; }
        }
        finally { updateExecutionGate.Release(); }
    }

    private async Task<OperationSnapshot> CancelUpdateAsync(string id, string principal, bool owner, string? reason, CancellationToken token)
    {
        await updateExecutionGate.WaitAsync(token);
        try { return await WriteUpdateAsync(id, tx => Cancel(tx, id, principal, owner, reason), token); }
        finally { updateExecutionGate.Release(); }
    }

    // 安装执行器仅在安全切换完成或恢复完整旧版后调用；失败恢复交由持久日志继续持有维护。
    internal async Task<OperationSnapshot> FinishUpdateAsync(string id, JsonElement? result = null, ApiProblem? problem = null,
        CancellationToken token = default)
    {
        await updateExecutionGate.WaitAsync(token);
        try
        {
            var finished = await WriteUpdateAsync(id, tx =>
            {
                var previous = Read(tx, id);
                RequireUpdate(previous);
                if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
                var canFinishApply = previous.Phase == "applying" ? updateMaintenance?.Owner == id :
                    previous.Phase == "waitingForIdle" && problem is not null;
                if (previous.State.KnownValue is not (OperationState.Running or OperationState.CancelRequested) ||
                    previous.Kind == "update.apply" && !canFinishApply)
                    throw new OperationException("resource.busy", "更新尚未进入可完成阶段");
                var state = problem is null ? OperationState.Succeeded : OperationState.Failed;
                var next = previous with
                {
                    State = state, Phase = problem is null ? previous.Kind switch
                    { "update.apply" => "committed", "update.download" => "staged", _ => "completed" } : "failed",
                    Revision = Next(previous), Result = result ?? (previous.Kind == "update.download" ? previous.Result : null),
                    Problem = problem, Progress = problem is null ? 1 : null,
                    Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
                    Cancellation = previous.Cancellation.State.KnownValue == CancellationState.Deferred
                        ? new(CancellationState.Completed, "safe-update-completed") : previous.Cancellation
                };
                Save(tx, next);
                return next;
            }, token);
            if (updateMaintenance?.Owner == id)
            {
                updateMaintenance.Dispose();
                updateMaintenance = null;
            }
            return finished;
        }
        finally { updateExecutionGate.Release(); }
    }

    private Task<T> WriteUpdateAsync<T>(string id, Func<DatabaseTransaction, T> action, CancellationToken token) =>
        updateMaintenance is { } maintenance && maintenance.Owner == id
            ? store.WriteMaintenanceAsync(maintenance, action, token) : store.WriteAsync(action, token);

    private static void RequireUpdate(OperationSnapshot operation)
    {
        if (!IsUpdateOperation(operation.Kind)) throw new OperationException("request.invalid", "操作不属于更新任务");
    }
}
