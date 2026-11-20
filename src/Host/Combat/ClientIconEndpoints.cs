using MabiTool.Contracts;

namespace MabiTool.Host.Combat;

/// <summary>
/// 客户端属性图标接口：目录（带强 ETag）与单个图标的 PNG。PNG 的 ETag 与 X-Content-Sha256 为内容 SHA-256（即目录中的 version），
/// 请求带相同 If-None-Match 时返回 304；鉴权、来源限制与其他本机接口相同。
/// </summary>
public static class ClientIconEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ClientIconApi.Catalog.Path, (HttpContext context) =>
        {
            var catalog = context.RequestServices.GetRequiredService<ClientIconService>().Catalog();
            context.Response.Headers.ETag = ClientIconService.ETag(catalog);
            return Results.Ok(catalog);
        }).WithMetadata(ClientIconApi.Catalog);
        app.MapGet(ClientIconApi.Content.Path, (string id, HttpContext context) =>
        {
            var icon = context.RequestServices.GetRequiredService<ClientIconService>().Content(id);
            var etag = "\"" + icon.Sha256 + "\"";
            context.Response.Headers.ETag = etag;
            context.Response.Headers["X-Content-Sha256"] = icon.Sha256;
            context.Response.Headers.CacheControl = "private, no-cache";
            if (context.Request.Headers.IfNoneMatch.Any(value => value == etag)) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(icon.Png!, "image/png");
        }).WithMetadata(ClientIconApi.Content);
    }
}
