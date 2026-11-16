using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.Host.Updates;

internal sealed class UpdateApiService(UpdateInstallationOptions? options, IUpdateDownloadService downloads,
    OperationStore operations, UpdatePolicyStore policy, TimeProvider clock)
{
    public UpdateStateSnapshot State()
    {
        var operation = operations.LatestUpdate(); var check = operations.LatestUpdate("update.check");
        var result = check?.State.KnownValue == OperationState.Succeeded && check.Result is { } saved
            ? saved.Deserialize<UpdateCheckResult>(ContractJson.Options) : null;
        var available = result is { UpdateAvailable: true } && result.PlanExpiresAt > clock.GetUtcNow()
            ? new UpdateAvailable(result.ReleaseId, result.ManifestSha256, result.PlanId, result.PlanExpiresAt, result.Source) : null;
        var phase = operation is not null && !SqliteStore.IsTerminal(operation.State.KnownValue) ? operation.Phase :
            operation?.Phase == "staged" ? "staged" : available is not null ? "available" : "idle";
        return new(options is not null, phase, options?.InstalledReleaseId, policy.Get(), operation, available);
    }
    public async Task<UpdatePlanSnapshot> Plan(string id, CancellationToken ct)
    {
        var plan = await downloads.ReadPlanAsync(id, ct);
        return new(plan.PlanId, new(1), plan.ReleaseId, plan.ManifestSha256, plan.CreatedAt, plan.ExpiresAt, plan.ExpiresAt <= clock.GetUtcNow(),
            plan.Components.Select(x => new UpdateComponentSnapshot(x.ComponentId, x.Version.ToString(), new((ulong)x.DownloadBytes), new((ulong)x.InstalledBytes),
                x.IsOptional, x.Dependencies.Select(d => d.ComponentId).ToArray())).ToArray(), plan.Removed.Select(x => x.ComponentId).ToArray(), new((ulong)plan.DownloadBytes), new((ulong)plan.PeakBytes), plan.RequiredUpdaterVersion?.ToString());
    }
}

internal static class UpdateEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.UpdateState.Path, (HttpContext context) => Results.Ok(Required<UpdateApiService>(context).State())).WithMetadata(ApiContract.UpdateState);
        app.MapGet(ApiContract.UpdatePolicy.Path, (HttpContext context) =>
        {
            var policy = Required<UpdatePolicyStore>(context).Get(); context.Response.Headers.ETag = UpdatePolicyStore.ETag(policy); return Results.Ok(policy);
        }).WithMetadata(ApiContract.UpdatePolicy);
        app.MapPost(ApiContract.SaveUpdatePolicy.Path, async (UpdatePolicy request, HttpContext context) =>
        {
            var value = await Required<UpdatePolicyStore>(context).SaveIdempotentAsync(LocalApiMiddleware.Session(context).ClientId, request,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), context.RequestAborted);
            context.Response.Headers.ETag = UpdatePolicyStore.ETag(value); return Results.Ok(value);
        }).WithMetadata(ApiContract.SaveUpdatePolicy);
        app.MapPost(ApiContract.CreateUpdateCheck.Path, async (UpdateCheckRequest request, HttpContext context) =>
        {
            var operation = await Required<UpdateCheckOperations>(context).SubmitAsync(LocalApiMiddleware.Session(context).ClientId, Header(context, "Idempotency-Key"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(ApiContract.CreateUpdateCheck);
        app.MapGet(ApiContract.UpdatePlan.Path, async (string id, HttpContext context) =>
        {
            var plan = await Required<UpdateApiService>(context).Plan(id, context.RequestAborted);
            context.Response.Headers.ETag = "\"update-plan-" + plan.Id + "-" + plan.Revision + "\""; return Results.Ok(plan);
        }).WithMetadata(ApiContract.UpdatePlan);
        app.MapPost(ApiContract.DownloadUpdatePlan.Path, async (string id, HttpContext context) =>
        {
            var operation = await Required<UpdateCheckOperations>(context).SubmitDownloadAsync(LocalApiMiddleware.Session(context).ClientId, id,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(ApiContract.DownloadUpdatePlan);
        app.MapPost(ApiContract.ApplyUpdatePlan.Path, async (string id, HttpContext context) =>
        {
            var operation = await Required<UpdateCheckOperations>(context).SubmitApplyAsync(LocalApiMiddleware.Session(context).ClientId, id,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(ApiContract.ApplyUpdatePlan);
    }
    private static T Required<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置更新存储");
    private static string? Header(HttpContext context, string name)
    {
        var value = context.Request.Headers[name];
        if (value.Count > 1) throw new OperationException("request.invalid", "更新请求头不能重复");
        return value.Count == 0 ? null : value[0];
    }
}
