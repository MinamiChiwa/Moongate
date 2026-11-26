using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Life;

public static class LifeEndpoints
{
    private static readonly string[] Topics = ["life.fishing.changed"];

    private static string? Header(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1) throw new OperationException("request.invalid", "请求头不能重复：" + name);
        return values.Count == 0 ? null : values[0];
    }

    private static IResult Answer(HttpContext context, LifeSnapshot value)
    {
        context.Response.Headers.ETag = LifeService.ETag(value);
        return Results.Ok(value);
    }

    // 没有持久存储的宿主（模拟宿主、测试宿主）不装配钓鱼运行：钓鱼端点返回“能力不可用”问题，不是 500。
    private static T Required<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("capability.unavailable", "当前宿主未配置钓鱼所需的持久存储",
            new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement("storage.unavailable") });

    public static string RunETag(FishingRun run) => $"\"life-fishing-{run.Id}-{run.Revision}\"";
    public static string SnapshotETag(string epoch, UInt64String revision) => $"\"life-fishing-{epoch}-{revision}\"";

    public static void Map(WebApplication app)
    {
        // 采集、加工、制作（经游戏连接器）。
        app.MapGet(LifeApi.State.Path, (HttpContext context, LifeService life) => Answer(context, life.Current())).WithMetadata(LifeApi.State);

        app.MapPost(LifeApi.Refresh.Path, async (LifeRefreshRequest request, HttpContext context, LifeService life, ModuleRuntime runtime, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("life", "life-refresh", ct);
            return Answer(context, await life.RefreshAsync(request, ct));
        }).WithMetadata(LifeApi.Refresh);

        app.MapPost(LifeApi.Execute.Path, async (LifeActionRequest request, HttpContext context, LifeService life, ModuleRuntime runtime, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("life", "life-start", ct);
            return Answer(context, await life.StartAsync(LocalApiMiddleware.Session(context).ClientId, request, Header(context, "Idempotency-Key"), ct));
        }).WithMetadata(LifeApi.Execute);

        app.MapPost(LifeApi.Stop.Path, (HttpContext context, LifeService life) => Answer(context, life.Stop())).WithMetadata(LifeApi.Stop);

        // 总队列：编辑与命令都只改宿主里的队列；开始或继续时才取得活动锁并发出命令。
        app.MapPost(LifeApi.AddTask.Path, async (LifeTaskAddRequest request, HttpContext context, LifeService life, CancellationToken ct) =>
            Answer(context, await life.AddTaskAsync(LocalApiMiddleware.Session(context).ClientId, request, Header(context, "Idempotency-Key"), ct)))
            .WithMetadata(LifeApi.AddTask);

        app.MapPut(LifeApi.UpdateTask.Path, (string id, LifeTaskUpdateRequest request, HttpContext context, LifeService life) =>
            Answer(context, life.UpdateTask(id, request))).WithMetadata(LifeApi.UpdateTask);

        app.MapDelete(LifeApi.RemoveTask.Path, (string id, HttpContext context, LifeService life) => Answer(context, life.RemoveTask(id))).WithMetadata(LifeApi.RemoveTask);

        app.MapPost(LifeApi.QueueCommand.Path, async (LifeQueueCommand request, HttpContext context, LifeService life, ModuleRuntime runtime, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("life", "life-queue", ct);
            return Answer(context, await life.CommandAsync(request, ct));
        }).WithMetadata(LifeApi.QueueCommand);

        // 原地专注钓鱼。
        app.MapGet(LifeApi.Fishing.Path, async (HttpContext context, CancellationToken ct) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var fishing = Required<FishingRunOperations>(context);
            var operations = Required<OperationStore>(context);
            var value = await Required<StateEventHub>(context).CaptureAsync(session, EventFilter.Create(Topics), (epoch, cursor) =>
            {
                var run = operations.LatestFishing(session.ClientId, session.Owner);
                return new FishingSnapshot(epoch, cursor, run?.Revision ?? new(0), fishing.Capability(), run);
            }, ct);
            context.Response.Headers.ETag = SnapshotETag(value.BackendEpoch, value.ResourceRevision);
            return Results.Ok(value);
        }).WithMetadata(LifeApi.Fishing);

        app.MapPost(LifeApi.StartFishing.Path, async (FishingRunRequest request, HttpContext context, CancellationToken ct) =>
        {
            var key = Header(context, "Idempotency-Key");
            var operation = await Required<FishingRunOperations>(context).SubmitAsync(LocalApiMiddleware.Session(context).ClientId, request, key, ct);
            context.Response.Headers.ETag = OperationStore.OperationETag(operation);
            return Results.Accepted(ApiContract.Operation.Path.Replace("{id}", operation.Id), operation);
        }).WithMetadata(LifeApi.StartFishing);

        app.MapGet(LifeApi.FishingRun.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var run = Required<FishingRunOperations>(context).Get(id, session.ClientId, session.Owner);
            context.Response.Headers.ETag = RunETag(run);
            return Results.Ok(run);
        }).WithMetadata(LifeApi.FishingRun);

        app.MapPost(LifeApi.StopFishing.Path, (string id, HttpContext context) =>
        {
            var session = LocalApiMiddleware.Session(context);
            var run = Required<FishingRunOperations>(context).Stop(id, session.ClientId, session.Owner);
            context.Response.Headers.ETag = RunETag(run);
            return Results.Ok(run);
        }).WithMetadata(LifeApi.StopFishing);
    }
}
