using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Storage;

internal static class StorageEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.StorageUsage.Path, (Func<HttpContext, Task<IResult>>)(async context => Results.Ok(
            await Store(context).UsageAsync(context.RequestAborted)))).WithMetadata(ApiContract.StorageUsage);
        app.MapGet(ApiContract.StorageModuleData.Path, async (string id, HttpContext context) =>
        {
            RequireOwner(context);
            return Results.Ok(await Required<StorageModuleDataStore>(context).DescribeAsync(id, context.RequestAborted));
        }).WithMetadata(ApiContract.StorageModuleData);
        app.MapPost(ApiContract.PreviewStorageModuleDeletion.Path, async (StorageModuleDeletionRequest request, HttpContext context) =>
        {
            RequireOwner(context);
            var plan = await Required<StorageModuleDataStore>(context).PreviewAsync(Owner(context), request, Header(context, "Idempotency-Key"), context.RequestAborted);
            context.Response.Headers.ETag = StorageModuleDataStore.ETag(plan); return Results.Json(plan, statusCode: 201);
        }).WithMetadata(ApiContract.PreviewStorageModuleDeletion);
        app.MapGet(ApiContract.StorageModuleDeletionPlan.Path, (string id, HttpContext context) =>
        {
            RequireOwner(context);
            var plan = Required<StorageModuleDataStore>(context).Get(id, Owner(context));
            context.Response.Headers.ETag = StorageModuleDataStore.ETag(plan); return Results.Ok(plan);
        }).WithMetadata(ApiContract.StorageModuleDeletionPlan);
        app.MapPost(ApiContract.ExecuteStorageModuleDeletion.Path, async (string id, HttpContext context) =>
        {
            RequireOwner(context);
            var operation = await Required<StorageModuleDeletionOperations>(context).SubmitAsync(id, Owner(context), Header(context, "Idempotency-Key"), Header(context, "If-Match"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(ApiContract.ExecuteStorageModuleDeletion);
        app.MapPost(ApiContract.PreviewStorageCleanup.Path, async (StorageCleanupRequest request, HttpContext context) =>
        {
            var plan = await Store(context).PreviewAsync(Owner(context), request, Header(context, "Idempotency-Key"), context.RequestAborted);
            context.Response.Headers.ETag = StorageControlStore.ETag(plan); return Results.Json(plan, statusCode: 201);
        }).WithMetadata(ApiContract.PreviewStorageCleanup);
        app.MapGet(ApiContract.StorageCleanupPlan.Path, (string id, HttpContext context) =>
        {
            var plan = Store(context).GetPlan(id, Owner(context));
            context.Response.Headers.ETag = StorageControlStore.ETag(plan); return Results.Ok(plan);
        }).WithMetadata(ApiContract.StorageCleanupPlan);
        app.MapPost(ApiContract.ExecuteStorageCleanup.Path, async (string id, HttpContext context) => Results.Ok(
            await Store(context).ExecuteAsync(id, Owner(context), Header(context, "Idempotency-Key"), Header(context, "If-Match"), context.RequestAborted))).WithMetadata(ApiContract.ExecuteStorageCleanup);
        app.MapGet(ApiContract.StorageCleanupResult.Path, (string id, HttpContext context) => Results.Ok(
            Store(context).Result(id, Owner(context)))).WithMetadata(ApiContract.StorageCleanupResult);
        app.MapGet(ApiContract.StorageBackups.Path, (HttpContext context) =>
        {
            RequireOwner(context);
            return Results.Ok(new ApiList<StorageBackupSummary>(Required<DataBackupStore>(context).Records().Select(DataBackupStore.Summary).ToArray(), null,
                context.RequestServices.GetRequiredService<SystemInfo>().InstanceId + ":backups"));
        }).WithMetadata(ApiContract.StorageBackups);
        app.MapGet(ApiContract.StorageBackup.Path, (string id, HttpContext context) =>
        {
            RequireOwner(context);
            var backup = DataBackupStore.Summary(Required<DataBackupStore>(context).Records().SingleOrDefault(x => x.Snapshot.SnapshotId == id)
                ?? throw new OperationException("resource.notFound", "备份不存在"));
            context.Response.Headers.ETag = "\"backup-" + backup.Id + "-" + backup.Revision + "\"";
            return Results.Ok(backup);
        }).WithMetadata(ApiContract.StorageBackup);
        app.MapPost(ApiContract.CreateStorageBackup.Path, async (StorageBackupRequest request, HttpContext context) =>
        {
            RequireOwner(context);
            var operation = await Required<StorageBackupOperations>(context).SubmitAsync(Owner(context), Header(context, "Idempotency-Key"), ct: context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(ApiContract.CreateStorageBackup);
        app.MapGet(ApiContract.StorageDirectorySelection.Path, (string id, HttpContext context) =>
            Results.Ok(Required<StorageDirectorySelectionRegistry>(context).Get(id, LocalApiMiddleware.Session(context)))).WithMetadata(ApiContract.StorageDirectorySelection);
        app.MapPost(ApiContract.PreviewStorageRestore.Path, async (StorageRestoreRequest request, HttpContext context) =>
        {
            RequireOwner(context);
            var plan = await Required<StorageMaintenancePlanStore>(context).PreviewRestoreAsync(Owner(context), request, Header(context, "Idempotency-Key"), context.RequestAborted);
            context.Response.Headers.ETag = StorageMaintenancePlanStore.ETag(plan); return Results.Json(plan, statusCode: 201);
        }).WithMetadata(ApiContract.PreviewStorageRestore);
        app.MapPost(ApiContract.PreviewStorageMigration.Path, async (StorageMigrationRequest request, HttpContext context) =>
        {
            RequireOwner(context);
            var target = Required<StorageDirectorySelectionRegistry>(context).Resolve(request.DirectorySelectionId, LocalApiMiddleware.Session(context));
            var plan = await Required<StorageMaintenancePlanStore>(context).PreviewMigrationAsync(Owner(context), request, target, Header(context, "Idempotency-Key"), context.RequestAborted);
            context.Response.Headers.ETag = StorageMaintenancePlanStore.ETag(plan); return Results.Json(plan, statusCode: 201);
        }).WithMetadata(ApiContract.PreviewStorageMigration);
        MapMaintenance(app, "restore", ApiContract.StorageRestorePlan, ApiContract.ExecuteStorageRestore);
        MapMaintenance(app, "migrate", ApiContract.StorageMigrationPlan, ApiContract.ExecuteStorageMigration);
    }
    private static void MapMaintenance(WebApplication app, string kind, EndpointDefinition query, EndpointDefinition execute)
    {
        app.MapGet(query.Path, (string id, HttpContext context) =>
        {
            RequireOwner(context); var plan = Required<StorageMaintenancePlanStore>(context).Get(id, Owner(context), kind);
            context.Response.Headers.ETag = StorageMaintenancePlanStore.ETag(plan); return Results.Ok(plan);
        }).WithMetadata(query);
        app.MapPost(execute.Path, async (string id, HttpContext context) =>
        {
            RequireOwner(context);
            var operation = await Required<StorageMaintenanceOperations>(context).SubmitAsync(id, Owner(context), kind,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + operation.Id, operation);
        }).WithMetadata(execute);
    }
    private static T Required<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置存储管理");
    private static void RequireOwner(HttpContext context)
    {
        if (!LocalApiMiddleware.Session(context).Owner) throw new OperationException("storage.scopeDenied", "完整数据备份及维护需要宿主所有者");
    }
    private static StorageControlStore Store(HttpContext context) => context.RequestServices.GetService<StorageControlStore>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置存储管理");
    private static string Owner(HttpContext context) => LocalApiMiddleware.Session(context).ClientId;
    private static string? Header(HttpContext context, string name)
    {
        var value = context.Request.Headers[name];
        if (value.Count > 1) throw new OperationException("request.invalid", "存储控制请求头不能重复");
        return value.Count == 0 ? null : value[0];
    }
}

internal sealed class StorageRetentionLifetime(StorageControlStore store, ActivityBarrier barrier,
    TimeProvider clock, ILogger<StorageRetentionLifetime> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken ct)
    {
        await store.RecoverAsync(ct); await base.StartAsync(ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (barrier.Snapshot() is { MaintenanceOwner: not null } or { Activities.Count: > 0 }) continue;
            try
            {
                if (!await store.CleanupNeededAsync(stoppingToken)) continue;
                var key = "automatic-" + Guid.NewGuid().ToString("N");
                var plan = await store.PreviewAsync("owner", new(), key, stoppingToken);
                await store.ExecuteAsync(plan.Id, "owner", key, StorageControlStore.ETag(plan), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationException error) when (error.Code == "resource.busy") { }
            catch (Exception error) when (error is IOException or OperationException or ActivityBarrierException)
            { logger.LogWarning("{Code}", error is StorageException storage ? storage.Code : error is OperationException operation ? operation.Code : "storage.unavailable"); }
            // 其他未预期的异常（例如临时目录条目超过扫描上限）只记警告，下一周期继续；不让后台服务退出而停掉宿主。
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            { logger.LogWarning("storage.cleanupFailed {Type}", error.GetType().Name); }
        }
    }
}
