using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Rooms;

internal static class RoomServerEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.RoomServer.Path, (HttpContext context) =>
        {
            var value = Required(context).Get();
            context.Response.Headers.ETag = OperationStore.RoomServerETag(value); return Results.Ok(value);
        }).WithMetadata(ApiContract.RoomServer);
        app.MapPost(ApiContract.StartRoomServer.Path, async (RoomServerStartRequest request, HttpContext context) =>
        {
            var value = await Required(context).StartServerAsync(LocalApiMiddleware.Session(context), request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted);
            return Results.Accepted(ApiContract.Operation.Path.Replace("{id}", value.Id, StringComparison.Ordinal), value);
        }).WithMetadata(ApiContract.StartRoomServer);
        app.MapPost(ApiContract.StopRoomServer.Path, async (RoomServerStopRequest request, HttpContext context) =>
        {
            var value = await Required(context).StopServerAsync(LocalApiMiddleware.Session(context),
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted);
            return Results.Accepted(ApiContract.Operation.Path.Replace("{id}", value.Id, StringComparison.Ordinal), value);
        }).WithMetadata(ApiContract.StopRoomServer);
    }
    private static RoomServerOperations Required(HttpContext context) => context.RequestServices.GetService<RoomServerOperations>()
        ?? throw new OperationException("storage.unavailable", "建房服务需要持久化宿主");
}
