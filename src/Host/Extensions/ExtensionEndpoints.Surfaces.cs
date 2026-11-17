using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Host.Files;

namespace MabiTool.Host.Extensions;

/// <summary>扩展界面的读取、交互事件回传与设置保存；前端按目录修订决定是否重读。</summary>
internal static partial class ExtensionEndpoints
{
    private static void MapSurfaces(WebApplication app)
    {
        MapFileRequests(app);
        app.MapGet(ApiContract.ExtensionSurfaces.Path, (HttpContext context) =>
        {
            if (Native(context)) Required<ExtensionFileRequests>(context).DesktopPolled();
            var catalog = SurfaceCatalog(context);
            context.Response.Headers.ETag = "\"extension-surfaces-" + catalog.Revision + "\"";
            return Results.Ok(catalog);
        }).WithMetadata(ApiContract.ExtensionSurfaces);
        app.MapPost(ApiContract.SendExtensionUiEvent.Path, async (string id, ExtensionUiEvent request, HttpContext context) =>
        {
            if (request.Kind is not ("click" or "change") || request.Node.Length is 0 or > 64 || request.Surface.Length is 0 or > 32 ||
                request.Value is { } value && JsonSerializer.SerializeToUtf8Bytes(value).Length > 4096)
                throw new OperationException("request.invalid", "界面事件无效");
            var registry = Required<ExtensionUiRegistry>(context);
            if (registry.Surface(id, request.Surface) is null) throw new OperationException("resource.notFound", "扩展没有这个界面");
            var session = Runtime(context).Running(id) ?? throw new OperationException("extension.notRunning", "扩展没有在运行");
            await session.NotifyAsync("ui/event", new JsonObject
            {
                ["surface"] = request.Surface, ["node"] = request.Node, ["kind"] = request.Kind,
                ["value"] = request.Value is { } v ? JsonNode.Parse(v.GetRawText()) : null
            });
            return Results.Ok(SurfaceCatalog(context));
        }).WithMetadata(ApiContract.SendExtensionUiEvent);
        app.MapPost(ApiContract.SaveExtensionSettings.Path, async (string id, ExtensionSettingsUpdate request, HttpContext context) =>
        {
            var installer = Installer(context);
            var target = installer.Runnable().FirstOrDefault(x => x.Id == id && x.Grants.Contains("ui.settings"))
                ?? throw new OperationException("extension.notRunning", "扩展未启用或没有授予设置项权限");
            var broker = Required<ExtensionCapabilities>(context);
            var form = await broker.SaveSettingsAsync(target, request.Values);
            if (Runtime(context).Running(id) is { } session)
                await session.NotifyAsync("settings/changed", new JsonObject { ["values"] = broker.SettingsNode(session) });
            Required<ExtensionUiRegistry>(context).Touch();
            return Results.Ok(form);
        }).WithMetadata(ApiContract.SaveExtensionSettings);
    }

    private static void MapFileRequests(WebApplication app) =>
        app.MapPost(ApiContract.AnswerExtensionFileRequest.Path, async (string id, ExtensionFileResponse request, HttpContext context, CancellationToken ct) =>
        {
            // 文件来自用户在本机文件选择框里的选择，只接受原生桌面客户端的回复。
            if (!Native(context)) throw new OperationException("permission.denied", "文件请求只接受本机桌面客户端回复");
            ExtensionPickedFile? file = null;
            if (!request.Cancelled)
            {
                if (request.UploadId is not { } uploadId) throw new OperationException("request.invalid", "须提供上传编号或取消");
                var session = LocalApiMiddleware.Session(context);
                var upload = Required<FileTransferStore>(context).Get(uploadId, session.ClientId);
                if (upload.State != "ready" || upload.Asset is null) throw new OperationException("storage.assetInvalid", "文件尚未完整上传");
                if (upload.Asset.SizeBytes.Value > ExtensionFileRequests.MaximumBytes) throw new OperationException("storage.quotaExceeded", "所选文件超过 512 KiB");
                await using var lease = await Required<AssetStore>(context).OpenReadAsync(upload.Asset.Id, "extension-file:" + Guid.NewGuid().ToString("N"),
                    ExtensionFileRequests.MaximumBytes, ct);
                using var buffer = new MemoryStream();
                await lease.Content.CopyToAsync(buffer, ct);
                file = new(upload.Name, buffer.ToArray(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())));
            }
            if (!Required<ExtensionFileRequests>(context).Complete(id, file))
                throw new OperationException("resource.notFound", "文件请求不存在或已结束");
            return Results.Ok(SurfaceCatalog(context));
        }).WithMetadata(ApiContract.AnswerExtensionFileRequest);

    private static ExtensionSurfaceCatalog SurfaceCatalog(HttpContext context)
    {
        var registry = Required<ExtensionUiRegistry>(context);
        var broker = Required<ExtensionCapabilities>(context);
        var runnable = Installer(context).Runnable();
        var (surfaces, states, activities, revision) = registry.Snapshot();
        var settings = runnable.Where(x => x.Grants.Contains("ui.settings") && x.Manifest.SettingItems.Count > 0).Select(broker.Form).ToArray();
        var commands = runnable.Where(x => x.Grants.Contains("ui.shortcut"))
            .SelectMany(x => x.Manifest.CommandItems.Select(c => new ExtensionCommand(x.Id, c.Id, ExtensionShortcuts.ActionId(x.Id, c.Id), c.Labels))).ToArray();
        return new(surfaces, states, settings, commands, activities, new(revision),
            Native(context) ? Required<ExtensionFileRequests>(context).Snapshot() : []);
    }
}
