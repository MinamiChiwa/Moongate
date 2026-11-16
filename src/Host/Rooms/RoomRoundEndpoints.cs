using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Rooms;

internal static class RoomRoundEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.CurrentRoomRound.Path, (HttpContext context, RoomSessionManager rooms) =>
        {
            var result = rooms.GetRound(LocalApiMiddleware.Session(context).ClientId);
            context.Response.Headers.ETag = RoomSessionManager.RoundETag(result);
            return Results.Ok(result);
        }).WithMetadata(ApiContract.CurrentRoomRound);
        app.MapPost(ApiContract.PrepareRoomRound.Path, async (RoomRoundRequest request, HttpContext context) =>
        {
            var result = await Required(context).PrepareAsync(LocalApiMiddleware.Session(context), request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted);
            context.Response.Headers.ETag = OperationStore.OperationETag(result);
            return Results.Accepted(ApiContract.Operation.Path.Replace("{id}", result.Id, StringComparison.Ordinal), result);
        }).WithMetadata(ApiContract.PrepareRoomRound);
        app.MapPost(ApiContract.ControlRoomRound.Path, async (string id, RoomRoundControlRequest request, HttpContext context) =>
        {
            var result = await Required(context).ControlAsync(LocalApiMiddleware.Session(context), id, request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted);
            context.Response.Headers.ETag = OperationStore.OperationETag(result);
            return Results.Accepted(ApiContract.Operation.Path.Replace("{id}", result.Id, StringComparison.Ordinal), result);
        }).WithMetadata(ApiContract.ControlRoomRound);
    }

    private static RoomRoundOperations Required(HttpContext context) => context.RequestServices.GetService<RoomRoundOperations>()
        ?? throw new OperationException("storage.unavailable", "轮次控制需要持久化宿主");
}
