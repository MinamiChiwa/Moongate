using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.PortableUpdate;

namespace MabiTool.Host.Updates;

internal sealed class UpdateApiService(PortableInstallation? installation, IUpdateDownloadService downloads,
    OperationStore operations, UpdatePolicyStore policy, TimeProvider clock)
{
    public UpdateStateSnapshot State()
    {
        var operation = operations.LatestUpdate(); var check = operations.LatestUpdate("update.check");
        var result = check?.State.KnownValue == OperationState.Succeeded && check.Result is { } saved
            ? saved.Deserialize<UpdateCheckResult>(ContractJson.Options) : null;
        var available = installation is not null && result is { UpdateAvailable: true } && result.PlanExpiresAt > clock.GetUtcNow()
            ? new UpdateAvailable(result.ReleaseId, result.ManifestSha256, result.PlanId, result.PlanExpiresAt, result.Source,
                result.SizeBytes is { } size ? new((ulong)size) : null, result.DownloadKind,
                result.DownloadBytes is { } download ? new((ulong)download) : null) : null;
        var last = installation is null ? null : UpdateResult.Read(installation.ToolRoot);
        var lastInstall = last is null ? null : new UpdateInstallResult(last.TransactionId, last.TransactionId, last.ToVersion, last.Outcome,
            null, last.Reason, last.FromVersion, new DateTimeOffset(DateTime.SpecifyKind(last.At, DateTimeKind.Utc)));
        var running = operation is not null && !SqliteStore.IsTerminal(operation.State.KnownValue);
        // 下载完成且计划仍可用时为 ready（可安装）；已安装的发行不再显示为可用。
        var staged = operation is { Kind: "update.download", Phase: "staged" } && available is not null &&
            operation.ResourceRefs.Any(item => item.Id == available.PlanId);
        var phase = running ? operation!.Phase : staged ? "ready" :
            operation?.Phase == "handedOff" && last?.OperationId != operation.Id ? "handedOff" : available is not null ? "available" : "idle";
        return new(installation is not null, phase, installation?.Identity.DisplayVersion, policy.Get(), operation, available,
            installation?.Identity.Channel, lastInstall, installation is null ? "update.sourceNotConfigured" : null);
    }

    public async Task<UpdatePlanSnapshot> Plan(string id, CancellationToken ct)
    {
        var plan = await downloads.ReadPlanAsync(id, ct);
        var bytes = new UInt64String((ulong)(plan.Delta?.AssetLength ?? plan.AssetLength));
        return new(plan.PlanId, new(1), plan.DisplayVersion, plan.Sha256, plan.CreatedAt, plan.ExpiresAt, plan.ExpiresAt <= clock.GetUtcNow(),
            [], [], bytes, new((ulong)plan.AssetLength * 3));
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
