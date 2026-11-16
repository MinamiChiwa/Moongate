using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Shortcuts;

internal static class ShortcutSettingsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.ShortcutSettings.Path, async (HttpContext context, ShortcutSettingsService shortcuts) =>
        {
            var value = await shortcuts.GetAsync(context.RequestAborted);
            context.Response.Headers.ETag = ShortcutSettingsStore.ETag(value.Revision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.ShortcutSettings);
        app.MapPost(ApiContract.ChangeShortcutSettings.Path, async (ShortcutSettingsRequest request, HttpContext context, ShortcutSettingsService shortcuts) =>
        {
            var value = await shortcuts.UpdateAsync(LocalApiMiddleware.Session(context).ClientId, request,
                context.Request.Headers["Idempotency-Key"], context.Request.Headers.IfMatch, context.RequestAborted);
            context.Response.Headers.ETag = ShortcutSettingsStore.ETag(value.Revision);
            return Results.Ok(value);
        }).WithMetadata(ApiContract.ChangeShortcutSettings);
    }
}
