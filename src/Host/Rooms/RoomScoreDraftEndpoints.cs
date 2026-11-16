using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Rooms;

internal static class RoomScoreDraftEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.RoomScoreDraft.Path, (HttpContext context, RoomSessionManager rooms) =>
        {
            var snapshot = rooms.GetScoreDraft(LocalApiMiddleware.Session(context).ClientId);
            context.Response.Headers.ETag = RoomSessionManager.ScoreDraftETag(snapshot); return Results.Ok(snapshot);
        }).WithMetadata(ApiContract.RoomScoreDraft);
        app.MapPost(ApiContract.PublishRoomScoreDraft.Path, async (RoomScoreDraftRequest request, HttpContext context) =>
            Accepted(context, await Required(context).PublishAsync(LocalApiMiddleware.Session(context), request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted)))
            .WithMetadata(ApiContract.PublishRoomScoreDraft);
        app.MapPost(ApiContract.MapRoomScoreDraft.Path, async (RoomScoreMappingRequest request, HttpContext context) =>
            Accepted(context, await Required(context).MapAsync(LocalApiMiddleware.Session(context), request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted)))
            .WithMetadata(ApiContract.MapRoomScoreDraft);
        app.MapPost(ApiContract.ClearRoomScoreDraft.Path, (Func<HttpContext, Task<IResult>>)(async context =>
            Accepted(context, await Required(context).ClearAsync(LocalApiMiddleware.Session(context),
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted))))
            .WithMetadata(ApiContract.ClearRoomScoreDraft);
    }
    private static IResult Accepted(HttpContext context, OperationSnapshot snapshot)
    {
        context.Response.Headers.ETag = OperationStore.OperationETag(snapshot);
        return Results.Accepted(ApiContract.Operation.Path.Replace("{id}", snapshot.Id, StringComparison.Ordinal), snapshot);
    }
    private static RoomScoreDraftOperations Required(HttpContext context) => context.RequestServices.GetService<RoomScoreDraftOperations>()
        ?? throw new OperationException("storage.unavailable", "乐谱草稿需要持久化宿主");
}
