using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.PortableUpdate;
using MabiTool.Providers.GitHubReleases;

namespace MabiTool.Host.Updates;

internal sealed class UpdateApiService(PortableInstallation? installation, IUpdateDownloadService downloads,
    OperationStore operations, UpdatePolicyStore policy, TimeProvider clock)
{
    public UpdateStateSnapshot State()
    {
        var operation = operations.LatestUpdate();
        var succeeded = operations.LatestUpdate("update.check", "succeeded");
        var finished = operations.LatestUpdate("update.check", "succeeded", "failed");
        var result = succeeded?.Result is { } saved ? saved.Deserialize<UpdateCheckResult>(ContractJson.Options) : null;
        var installed = installation is not null && ReleaseVersion.TryParse(installation.Identity.DisplayVersion, out var parsed) ? parsed : null;
        // 只把高于当前运行版本、未过期且计划仍在的发行报为可用：更新成功或手动升级后，旧检查结果里的版本不再显示。
        UpdatePlan? plan = null;
        if (installation is not null && installed is not null && result is { UpdateAvailable: true, PlanId: { } planId } &&
            result.PlanExpiresAt > clock.GetUtcNow() && ReleaseVersion.TryParse(result.ReleaseId, out var offered) && offered.CompareTo(installed) > 0)
        {
            try { plan = downloads.ReadPlanAsync(planId).GetAwaiter().GetResult(); }
            catch (Exception error) when (error is OperationException or UpdatePackageException or IOException) { }
        }
        var resumable = plan is null ? null : UpdateRetention.ResumableBytes(installation!.ToolRoot, plan);
        // 下载方式与大小：后台比对的结果优先；还没算出且计划能分段时两者都为空，界面不写大小。
        var estimate = plan is null ? null : UpdatePlanStore.ReadEstimate(installation!.ToolRoot, plan.PlanId);
        var kind = estimate?.DownloadKind ?? (plan?.Ranges is null ? result?.DownloadKind ?? "full" : null);
        var downloadBytes = estimate?.DownloadBytes ?? (plan?.Ranges is null ? result?.DownloadBytes ?? plan?.AssetLength : null);
        var available = plan is not null
            ? new UpdateAvailable(result!.ReleaseId, result.ManifestSha256, result.PlanId, result.PlanExpiresAt, result.Source,
                result.SizeBytes is { } size ? new((ulong)size) : null, kind,
                downloadBytes is { } download ? new((ulong)download) : null, resumable is { } part ? new((ulong)part) : null) : null;
        var last = installation is null ? null : UpdateResult.Read(installation.ToolRoot);
        var lastInstall = last is null ? null : new UpdateInstallResult(last.TransactionId, last.TransactionId, last.ToVersion, last.Outcome,
            null, last.Reason, last.FromVersion, new DateTimeOffset(DateTime.SpecifyKind(last.At, DateTimeKind.Utc)));
        var running = operation is not null && !SqliteStore.IsTerminal(operation.State.KnownValue);
        // 下载完成且计划仍可用时为 ready（可安装）：取这份计划最近一次下载的结果，之后的检查沿用同一计划时不受影响。
        var lastDownload = available is null ? null : operations.LatestUpdate("update.download");
        var staged = lastDownload is { Phase: "staged" } && lastDownload.ResourceRefs.Any(item => item.Id == available!.PlanId);
        var phase = running ? operation!.Phase : staged ? "ready" :
            operation?.Phase == "handedOff" && last?.OperationId != operation.Id ? "handedOff" : available is not null ? "available" : "idle";
        return new(installation is not null, phase, installation?.Identity.DisplayVersion, policy.Get(), operation, available,
            installation?.Identity.Channel, lastInstall, installation is null ? "update.sourceNotConfigured" : null,
            LastCheck(finished, installation?.Identity.DisplayVersion, available is not null));
    }

    /// <summary>最近一次完成的检查。成功的检查只在结论针对当前运行版本时给出：没有新版本时结果里的版本等于当前版本，有新版本时须仍为可用。</summary>
    private static UpdateCheckSummary? LastCheck(OperationSnapshot? check, string? installed, bool available)
    {
        if (check is null || installed is null) return null;
        var at = check.Timestamps.CompletedAt ?? check.Timestamps.CreatedAt;
        if (check.State.KnownValue == OperationState.Failed) return new(at, "failed", check.Problem?.Code);
        var result = check.Result?.Deserialize<UpdateCheckResult>(ContractJson.Options);
        return result switch
        {
            { UpdateAvailable: false } when ReleaseVersion.TryParse(result.ReleaseId, out var checkedVersion) &&
                ReleaseVersion.TryParse(installed, out var running) && checkedVersion.Equals(running) => new(at, "current"),
            { UpdateAvailable: true } when available => new(at, "available"),
            _ => null
        };
    }

    public async Task<UpdatePlanSnapshot> Plan(string id, CancellationToken ct)
    {
        var plan = await downloads.ReadPlanAsync(id, ct);
        var bytes = new UInt64String((ulong)(UpdatePlanStore.ReadEstimate(installation!.ToolRoot, plan.PlanId)?.DownloadBytes ?? plan.AssetLength));
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
