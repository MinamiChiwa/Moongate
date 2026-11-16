using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Extensions;

/// <summary>扩展列表、安装预览、安装、启停与卸载的公开接口；本机路径只返回给原生桌面客户端（没有 Origin 的会话）。</summary>
internal static class ExtensionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.Extensions.Path, (HttpContext context) =>
            Results.Ok(Installer(context).Catalog(Native(context)))).WithMetadata(ApiContract.Extensions);
        app.MapPost(ApiContract.PreviewExtensionInstall.Path, async (ExtensionPreviewRequest request, HttpContext context, CancellationToken ct) =>
        {
            var installer = Installer(context);
            ExtensionInstallPreview preview;
            if (request is { UploadId: { } uploadId, InboxName: null })
            {
                var session = LocalApiMiddleware.Session(context);
                var upload = Required<FileTransferStore>(context).Get(uploadId, session.ClientId);
                if (upload.State != "ready" || upload.Asset is null)
                    throw new OperationException("storage.assetInvalid", "扩展包尚未完整上传");
                await using var lease = await Required<AssetStore>(context).OpenReadAsync(upload.Asset.Id,
                    "extension-read:" + Guid.NewGuid().ToString("N"), ExtensionPackageInspector.MaximumPackageBytes, ct);
                preview = await installer.PreviewAsync(lease.Content, "upload", null, ct);
            }
            else if (request is { UploadId: null, InboxName: { } name }) preview = await installer.PreviewInboxAsync(name, ct);
            else throw new OperationException("request.invalid", "安装预览须提供上传编号或投放位置里的文件名之一");
            return Results.Json(preview, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(ApiContract.PreviewExtensionInstall);
        app.MapPost(ApiContract.InstallExtension.Path, async (string id, HttpContext context, CancellationToken ct) =>
            Results.Ok(await Installer(context).InstallAsync(id, Native(context), ct))).WithMetadata(ApiContract.InstallExtension);
        app.MapPost(ApiContract.SetExtensionEnabled.Path, async (string id, ExtensionEnableRequest request, HttpContext context, CancellationToken ct) =>
            Results.Ok(await Installer(context).SetEnabledAsync(id, request.Enabled, Native(context), ct))).WithMetadata(ApiContract.SetExtensionEnabled);
        app.MapPost(ApiContract.UninstallExtension.Path, async (string id, HttpContext context, CancellationToken ct) =>
            Results.Ok(await Installer(context).UninstallAsync(id, ct))).WithMetadata(ApiContract.UninstallExtension);
    }

    private static bool Native(HttpContext context) => LocalApiMiddleware.Session(context).Origin is null;
    private static ExtensionInstaller Installer(HttpContext context) => Required<ExtensionInstaller>(context);
    private static T Required<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置持久存储");
}

/// <summary>宿主启动时整理 extensions/；整理失败只记录日志，宿主照常启动。</summary>
internal sealed class ExtensionStartup(ExtensionInstaller installer, ILogger<ExtensionStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { installer.Initialize(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "扩展目录整理失败：{Message}", error.Message);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
