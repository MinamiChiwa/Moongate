using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;

namespace MabiTool.Host.Performance;

public static class PerformanceEndpoints
{
    private static T Required<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置音乐库持久存储");

    private static string? Header(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1) throw new OperationException("request.invalid", "请求头不能重复：" + name);
        return values.Count == 0 ? null : values[0];
    }

    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.PerformanceSessions.Path, async (HttpContext context, ModuleRuntime runtime, SystemInfo info, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "performance-list", ct);
            var owner = LocalApiMiddleware.Session(context).ClientId;
            return Results.Ok(new ApiList<PerformanceQueueState>(Required<PerformanceSessionService>(context).List(owner), null,
                info.InstanceId + ":performance"));
        }).WithMetadata(ApiContract.PerformanceSessions);

        app.MapPost(ApiContract.CreatePerformanceSession.Path, async (PerformanceSessionCreateRequest request, HttpContext context,
            ModuleRuntime runtime, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "performance-create", ct);
            var owner = LocalApiMiddleware.Session(context).ClientId;
            var value = await Required<PerformanceSessionService>(context).CreateAsync(owner, request, Header(context, "Idempotency-Key"), ct);
            context.Response.Headers.ETag = PerformanceSessionService.ETag(value.Generation);
            return Results.Created(ApiContract.PerformanceSession.Path.Replace("{id}", value.Id), value);
        }).WithMetadata(ApiContract.CreatePerformanceSession);

        app.MapGet(ApiContract.PerformanceSession.Path, async (string id, HttpContext context, ModuleRuntime runtime, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "performance-get", ct);
            var value = Required<PerformanceSessionService>(context).Get(LocalApiMiddleware.Session(context).ClientId, id);
            context.Response.Headers.ETag = PerformanceSessionService.ETag(value.Generation);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.PerformanceSession);

        app.MapPost(ApiContract.CommandPerformanceSession.Path, async (string id, PerformanceSessionCommand request,
            HttpContext context, ModuleRuntime runtime, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "performance-command", ct);
            var value = await Required<PerformanceSessionService>(context).CommandAsync(LocalApiMiddleware.Session(context).ClientId,
                id, request, Header(context, "If-Match"), ct);
            context.Response.Headers.ETag = PerformanceSessionService.ETag(value.Generation);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.CommandPerformanceSession);
    }
}
