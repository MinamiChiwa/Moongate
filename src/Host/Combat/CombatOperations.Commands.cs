using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Adapters;

namespace MabiTool.Host.Combat;

public sealed partial class CombatOperations
{
    public async Task<CombatEncounter> BoundaryAsync(string principal, bool owner, string route, string resource,
        CombatBoundaryRequest request, string? key, string? etag, CancellationToken ct)
    {
        if (request.ObservationId is null || request.ObservationId.Length > 128 || etag is null)
            throw new OperationException(etag is null ? "revision.required" : "request.invalid", "战斗边界需要观察身份和 If-Match");
        var hash = JsonSerializer.Serialize(new { request.ObservationId, etag }, ContractJson.Options);
        if (await history.BoundaryReceiptAsync(principal, route, resource, key, hash, ct) is { } previous) return previous;
        if (!active.TryGetValue(request.ObservationId, out var job) || !owner && job.Owner != principal)
            throw new OperationException("combat.planStale", "观察已结束或不属于当前主体");
        CombatEncounter? result = null;
        await job.Gate.WaitAsync(ct);
        try
        {
            // 边界回执按已提交的工作区核对修订：先等进行中的后台保存，再把已发布但未保存的状态写入，已提交状态与最新捕获一致后再核对。
            await job.SaveGate.WaitAsync(ct);
            try { await BoundaryCoreAsync(); }
            finally { job.SaveGate.Release(); }
        }
        finally { job.Gate.Release(); }
        return result!;

        async Task BoundaryCoreAsync()
        {
            if (await history.BoundaryReceiptAsync(principal, route, resource, key, hash, ct) is { } retry) { result = retry; return; }
            if (job.Session is { Active: true } live && !job.Stop.IsCancellationRequested)
            {
                if (job.Saving is { } pending) { job.Saving = null; await pending; }
                var current = live.Capture();
                if (current.Material || current.Urgent)
                {
                    if (!await history.SaveAsync(current)) throw new OperationException("combat.planStale", "观察已被停止");
                    live.Acknowledge(current); job.LastSaved = clock.GetTimestamp(); job.SavedTick = Interlocked.Read(ref job.Ticks); TraceSegments(job, current);
                }
                history.PublishLive(current.Workspace, live.EntityEffects);
            }
            var workspace = history.Workspace(principal, owner, job.Id);
            if (workspace is null || job.Session is null || job.Stop.IsCancellationRequested || !job.Session.Active)
                throw new OperationException("combat.planStale", "观察已经结束");
            var matches = route == "start" ? CombatHistoryStore.WorkspaceETag(workspace) == etag :
                workspace.Encounter is { } encounter && encounter.Id == resource && CombatHistoryStore.ETag(encounter) == etag;
            if (!matches) throw new OperationException("revision.stale", "战斗状态已变化，请读取最新快照");
            var now = clock.GetUtcNow(); var mono = Now(job);
            CombatEncounter? closed = null;
            try
            {
                if (route == "start") job.Session.StartManual(now, mono);
                else if (route == "close") job.Session.Close("manual-end", now, mono);
                else if (route == "reset")
                {
                    // 重设清空整场、当前分段与定时测量：进行中的测量取消且不保存，最近一次测量结果一并清除。
                    closed = job.Session.Reset(now, mono) ?? workspace.Encounter;
                    await ResetMeasurementAsync(job);
                }
                else if (route == "split")
                {
                    if (!job.Session.Split(now, mono)) throw new OperationException("combat.segmentInactive", "当前没有进行中的战斗分段");
                }
                else throw new OperationException("request.invalid", "战斗边界类型无效");
            }
            catch (InvalidOperationException error) { throw new OperationException("resource.busy", error.Message); }
            var checkpoint = job.Session.Capture();
            var outcome = closed is null ? checkpoint.Workspace.Encounter! : checkpoint.Encounters.FirstOrDefault(x => x.Id == closed.Id) ?? closed;
            try
            {
                var receipt = new CombatBoundaryReceipt(principal, route, resource, key!, hash, CombatHistoryStore.WorkspaceETag(workspace), outcome);
                if (!await history.SaveAsync(checkpoint, boundary: receipt)) throw new OperationException("combat.planStale", "观察已被停止");
                TraceSegments(job, checkpoint);
                job.Session.Acknowledge(checkpoint); job.LastSaved = clock.GetTimestamp(); job.SavedTick = Interlocked.Read(ref job.Ticks);
                history.PublishLive(checkpoint.Workspace, job.Session.EntityEffects); result = outcome; return;
            }
            catch (Exception error) { job.Failure = error; job.StopReason = "boundary-interrupted"; job.Stop.Cancel(); throw; }
        }
    }
}
