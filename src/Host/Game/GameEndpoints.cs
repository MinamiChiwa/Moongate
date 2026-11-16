using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Rooms.Persistence;
using MabiTool.Host.Rooms;

namespace MabiTool.Host.Game;

internal static class GameEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.GameWindows.Path, (HttpContext context, GameBindingService service) =>
            Results.Ok(service.Enumerate(LocalApiMiddleware.Session(context).ClientId))).WithMetadata(ApiContract.GameWindows);
        app.MapGet(ApiContract.GameBinding.Path, (HttpContext context, GameBindingService service) =>
        {
            var snapshot = service.Snapshot(); context.Response.Headers.ETag = service.ETag(snapshot);
            return Results.Ok(snapshot);
        }).WithMetadata(ApiContract.GameBinding);
        app.MapPost(ApiContract.SelectGameBinding.Path, async (GameBindingRequest request, HttpContext context, GameBindingService service, CancellationToken ct) =>
        {
            var snapshot = await service.SelectAsync(LocalApiMiddleware.Session(context).ClientId, request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, ct);
            context.Response.Headers.ETag = service.ETag(snapshot); return Results.Ok(snapshot);
        }).WithMetadata(ApiContract.SelectGameBinding);
        app.MapPost(ApiContract.EditRoomPreferences.Path, async (RoomPreferences request, HttpContext context, CancellationToken ct) =>
        {
            var store = context.RequestServices.GetService<RoomPreferenceStore>()
                ?? throw new OperationException("capability.unavailable", "保存房间偏好需要持久存储");
            var value = await store.UpdateAsync(LocalApiMiddleware.Session(context).ClientId, request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, ct);
            context.Response.Headers.ETag = RoomPreferenceStore.ETag(value.Revision); return Results.Ok(value);
        }).WithMetadata(ApiContract.EditRoomPreferences);
        app.MapPost(ApiContract.ReleaseRoomInput.Path, async (string id, HttpContext context, RoomSessionManager rooms) =>
            Results.Ok(await rooms.ReleaseInputAsync(LocalApiMiddleware.Session(context).ClientId, id,
                context.Request.Headers["Idempotency-Key"], context.RequestAborted))).WithMetadata(ApiContract.ReleaseRoomInput);
    }
}
