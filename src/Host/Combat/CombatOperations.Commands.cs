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
        await job.Gate.WaitAsync(ct);
        try
        {
            if (await history.BoundaryReceiptAsync(principal, route, resource, key, hash, ct) is { } retry) return retry;
            var workspace = history.Workspace(principal, owner, job.Id);
            if (workspace is null || job.Session is null || job.Stop.IsCancellationRequested || !job.Session.Active)
                throw new OperationException("combat.planStale", "观察已经结束");
            var matches = route == "start" ? CombatHistoryStore.WorkspaceETag(workspace) == etag :
                workspace.Encounter is { } encounter && encounter.Id == resource && CombatHistoryStore.ETag(encounter) == etag;
            if (!matches) throw new OperationException("revision.stale", "战斗状态已变化，请读取最新快照");
            var now = clock.GetUtcNow(); var mono = Now(job);
            try
            {
                if (route == "start") job.Session.StartManual(now, mono);
                else if (route == "close") job.Session.Close("manual-end", now, mono);
                else if (route == "reset") job.Session.Reset(now, mono);
                else if (route == "split")
                {
                    if (!job.Session.Split(now, mono)) throw new OperationException("combat.segmentInactive", "当前没有进行中的战斗分段");
                }
                else throw new OperationException("request.invalid", "战斗边界类型无效");
            }
            catch (InvalidOperationException error) { throw new OperationException("resource.busy", error.Message); }
            var checkpoint = job.Session.Capture(); var result = checkpoint.Workspace.Encounter!;
            try
            {
                var receipt = new CombatBoundaryReceipt(principal, route, resource, key!, hash, CombatHistoryStore.WorkspaceETag(workspace), result);
                if (!await history.SaveAsync(checkpoint, boundary: receipt)) throw new OperationException("combat.planStale", "观察已被停止");
                TraceSegments(job, checkpoint);
                job.Session.Acknowledge(checkpoint); job.LastSaved = clock.GetTimestamp();
                history.PublishLive(checkpoint.Workspace, job.Session.EntityEffects); return result;
            }
            catch (Exception error) { job.Failure = error; job.StopReason = "boundary-interrupted"; job.Stop.Cancel(); throw; }
        }
        finally { job.Gate.Release(); }
    }
}
