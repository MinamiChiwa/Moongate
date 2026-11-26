using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.GameCli;

public static class GameConnectorEndpoints
{
    private static string? Header(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1) throw new OperationException("request.invalid", "请求头不能重复：" + name);
        return values.Count == 0 ? null : values[0];
    }

    public static void Map(WebApplication app)
    {
        app.MapGet(GameConnectorApi.State.Path, (HttpContext context, GameConnectorService connector) =>
        {
            var value = connector.Current;
            context.Response.Headers.ETag = GameConnectorService.ETag(value);
            return Results.Ok(value);
        }).WithMetadata(GameConnectorApi.State);

        app.MapPost(GameConnectorApi.Command.Path, async (GameConnectorCommand request, HttpContext context, GameConnectorService connector, CancellationToken ct) =>
        {
            var value = await connector.CommandAsync(LocalApiMiddleware.Session(context).ClientId, request, Header(context, "Idempotency-Key"), ct);
            context.Response.Headers.ETag = GameConnectorService.ETag(value);
            return Results.Ok(value);
        }).WithMetadata(GameConnectorApi.Command);

        app.MapPost(GameConnectorApi.ScoreSupport.Path, async (GameScoreSupportQuery request, GameScoreSupportService support, CancellationToken ct) =>
            Results.Ok(await support.QueryAsync(request, ct))).WithMetadata(GameConnectorApi.ScoreSupport);

        app.MapPost(GameConnectorApi.MusicLibrarySync.Path, async (GameMusicLibrarySyncService sync, ModuleRuntime runtime, CancellationToken ct) =>
        {
            await using var demand = await runtime.AcquireAsync("music-library", "game-music-sync", ct);
            return Results.Ok(await sync.SyncAsync(ct));
        }).WithMetadata(GameConnectorApi.MusicLibrarySync);
    }
}
