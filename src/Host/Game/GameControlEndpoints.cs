using MabiTool.Contracts;

namespace MabiTool.Host.Game;

internal static class GameControlEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.GameInputOwnership.Path, (GameControlService service) =>
            Results.Ok(service.Snapshot())).WithMetadata(ApiContract.GameInputOwnership);
        app.MapPost(ApiContract.ActivateGameWindow.Path, (HttpContext context, GameBindingService service) =>
            Results.Ok(service.Activate(LocalApiMiddleware.Session(context).ClientId))).WithMetadata(ApiContract.ActivateGameWindow);
        app.MapPost(ApiContract.GameEmergencyStop.Path,
            (GameEmergencyStopRequest request, HttpContext context, GameControlService service) =>
                Results.Ok(service.EmergencyStop(LocalApiMiddleware.Session(context).ClientId, request,
                    context.Request.Headers["Idempotency-Key"]))).WithMetadata(ApiContract.GameEmergencyStop);
    }
}
