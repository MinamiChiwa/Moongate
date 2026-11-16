using MabiTool.Contracts;

namespace MabiTool.Host.Game;

internal static class GameObservationEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.GameObservationRuntime.Path, (GameObservationService service) => Results.Ok(service.Runtime()))
            .WithMetadata(ApiContract.GameObservationRuntime);
        app.MapGet(ApiContract.GameObservation.Path, (HttpContext context, GameObservationService service) =>
        {
            var snapshot = service.Snapshot(); context.Response.Headers.ETag = GameObservationService.ETag(snapshot);
            return Results.Ok(snapshot);
        }).WithMetadata(ApiContract.GameObservation);
        app.MapPost(ApiContract.QueryObservationChannels.Path, async (HttpContext context, GameObservationService service) =>
            Results.Ok(await service.ChannelsAsync(LocalApiMiddleware.Session(context).ClientId, context.RequestAborted)))
            .WithMetadata(ApiContract.QueryObservationChannels);
        app.MapPost(ApiContract.StartGameObservation.Path, async (GameObservationStartRequest request, HttpContext context, GameObservationService service) =>
        {
            var value = await service.StartAsync(LocalApiMiddleware.Session(context).ClientId, request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted);
            context.Response.Headers.ETag = GameObservationService.ETag(value.Snapshot);
            return Results.Json(value, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(ApiContract.StartGameObservation);
        app.MapGet(ApiContract.GameCaptureSettings.Path, (HttpContext context, GameCaptureSettingsStore store) =>
        {
            var value = store.Read(); context.Response.Headers.ETag = GameCaptureSettingsStore.ETag(value.Revision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.GameCaptureSettings);
        app.MapPost(ApiContract.SaveGameCaptureSettings.Path, async (GameCaptureSettings request, HttpContext context, GameCaptureSettingsStore store) =>
        {
            var value = await store.UpdateAsync(request, context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted);
            context.Response.Headers.ETag = GameCaptureSettingsStore.ETag(value.Revision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.SaveGameCaptureSettings);
        app.MapPost(ApiContract.StartChannelDetection.Path, async (CaptureChannelDetectionRequest request, HttpContext context, GameObservationService service) =>
            Results.Json(await service.StartDetectionAsync(LocalApiMiddleware.Session(context).ClientId, request,
                context.Request.Headers["Idempotency-Key"], context.RequestAborted), statusCode: StatusCodes.Status201Created))
            .WithMetadata(ApiContract.StartChannelDetection);
        app.MapGet(ApiContract.ChannelDetection.Path, (GameObservationService service) => Results.Ok(service.Detection()))
            .WithMetadata(ApiContract.ChannelDetection);
        app.MapPost(ApiContract.CancelChannelDetection.Path, async (HttpContext context, GameObservationService service) =>
            Results.Ok(await service.CancelDetectionAsync(LocalApiMiddleware.Session(context).ClientId)))
            .WithMetadata(ApiContract.CancelChannelDetection);
        app.MapDelete(ApiContract.ReleaseGameObservation.Path, async (string id, HttpContext context, GameObservationService service) =>
        {
            var principal = LocalApiMiddleware.Session(context);
            return Results.Ok(await service.ReleaseAsync(principal.ClientId, principal.Owner, id, context.RequestAborted));
        }).WithMetadata(ApiContract.ReleaseGameObservation);
    }
}
