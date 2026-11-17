using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Extensions;

/// <summary>扩展列表、安装预览、安装、启停与卸载的公开接口；本机路径只返回给原生桌面客户端（没有 Origin 的会话）。</summary>
internal static partial class ExtensionEndpoints
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
        {
            var installed = await Installer(context).InstallAsync(id, Native(context), ct);
            return Results.Ok(await AppliedAsync(context, installed.Id));
        }).WithMetadata(ApiContract.InstallExtension);
        app.MapPost(ApiContract.SetExtensionEnabled.Path, async (string id, ExtensionEnableRequest request, HttpContext context, CancellationToken ct) =>
        {
            await Installer(context).SetEnabledAsync(id, request.Enabled, Native(context), ct);
            return Results.Ok(await AppliedAsync(context, id));
        }).WithMetadata(ApiContract.SetExtensionEnabled);
        app.MapPost(ApiContract.UninstallExtension.Path, async (string id, HttpContext context, CancellationToken ct) =>
        {
            var receipt = await Installer(context).UninstallAsync(id, ct);
            if (context.RequestServices.GetService<ExtensionRuntime>() is { } runtime) await runtime.ApplyAsync(id);
            return Results.Ok(receipt);
        }).WithMetadata(ApiContract.UninstallExtension);
        app.MapPost(ApiContract.SetExtensionPermissions.Path, async (string id, ExtensionGrantRequest request, HttpContext context, CancellationToken ct) =>
        {
            await Installer(context).SetGrantsAsync(id, request.Grants, Native(context), ct);
            return Results.Ok(await AppliedAsync(context, id));
        }).WithMetadata(ApiContract.SetExtensionPermissions);
        app.MapPost(ApiContract.RestartExtension.Path, async (string id, HttpContext context) =>
        {
            var installer = Installer(context);
            var current = installer.Get(id, Native(context));
            if (current.Package?.Runtime is null) throw new OperationException("request.invalid", "这个扩展没有程序，不需要重启");
            if (!current.Enabled) throw new OperationException("extension.notRunning", "扩展已停用，请先启用");
            await Runtime(context).RestartAsync(id);
            return Results.Ok(installer.Get(id, Native(context)));
        }).WithMetadata(ApiContract.RestartExtension);
        app.MapGet(ApiContract.ExtensionLogs.Path, (string id, HttpContext context) =>
        {
            Installer(context).Get(id, includePaths: false);
            return Results.Ok(Runtime(context).Log(id).Read(id));
        }).WithMetadata(ApiContract.ExtensionLogs);
        app.MapPost(ApiContract.SetExtensionDevelopmentMode.Path, async (ExtensionDevelopmentModeRequest request, HttpContext context) =>
        {
            var installer = Installer(context);
            installer.SetDevelopmentMode(request.Enabled, Native(context));
            if (context.RequestServices.GetService<ExtensionRuntime>() is { } runtime)
                foreach (var item in installer.Catalog(false).Installed.Where(x => x.Development)) await runtime.ApplyAsync(item.Id);
            return Results.Ok(installer.Catalog(Native(context)));
        }).WithMetadata(ApiContract.SetExtensionDevelopmentMode);
        app.MapPost(ApiContract.LoadDevelopmentExtension.Path, async (ExtensionDevelopmentLoadRequest request, HttpContext context, CancellationToken ct) =>
        {
            // 开发目录是本机路径，只接受原生桌面客户端提交。
            if (!Native(context)) throw new OperationException("permission.denied", "开发模式加载只接受本机桌面客户端");
            var installed = await Installer(context).LoadDevelopmentAsync(request.Directory, true, ct);
            return Results.Ok(await AppliedAsync(context, installed.Id));
        }).WithMetadata(ApiContract.LoadDevelopmentExtension);
        MapSurfaces(app);
    }

    /// <summary>等运行时按新的安装记录完成启停与授权对齐后，返回含运行状态的扩展描述。</summary>
    private static async Task<InstalledExtension> AppliedAsync(HttpContext context, string id)
    {
        if (context.RequestServices.GetService<ExtensionRuntime>() is { } runtime) await runtime.ApplyAsync(id);
        return Installer(context).Get(id, Native(context));
    }

    private static ExtensionRuntime Runtime(HttpContext context) => Required<ExtensionRuntime>(context);

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
