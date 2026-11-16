using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Files;

internal static class FileTransferEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiContract.FileCapabilities.Path, (HttpContext context) => Results.Ok(new FileTransferCapabilities(
            new((ulong)FilePackageReader.MaximumUploadBytes), new((ulong)FilePackageReader.MaximumExpandedBytes),
            FilePackageReader.MaximumEntries, Required<FileSelectionRegistry>(context).Available))).WithMetadata(ApiContract.FileCapabilities);
        app.MapPost(ApiContract.CreateUpload.Path, async (FileUploadRequest request, HttpContext context) =>
        {
            var value = await Required<FileTransferStore>(context).CreateAsync(Session(context).ClientId, request, Header(context, "Idempotency-Key"), context.RequestAborted);
            context.Response.Headers.ETag = FileTransferStore.ETag(value);
            return Results.Json(value, statusCode: 201);
        }).WithMetadata(ApiContract.CreateUpload);
        app.MapGet(ApiContract.Upload.Path, (string id, HttpContext context) =>
        {
            var value = Required<FileTransferStore>(context).Get(id, Session(context).ClientId);
            context.Response.Headers.ETag = FileTransferStore.ETag(value); return Results.Ok(value);
        }).WithMetadata(ApiContract.Upload);
        app.MapPost(ApiContract.UploadContent.Path, async (string id, HttpContext context) =>
        {
            var value = await Required<FileTransferService>(context).UploadAsync(id, Session(context), context.Request.Body,
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), context.RequestAborted);
            context.Response.Headers.ETag = FileTransferStore.ETag(value); return Results.Ok(value);
        }).WithMetadata(ApiContract.UploadContent);
        app.MapGet(ApiContract.FileSelection.Path, (string id, HttpContext context) =>
            Results.Ok(Required<FileSelectionRegistry>(context).Get(id, Session(context)))).WithMetadata(ApiContract.FileSelection);
        app.MapPost(ApiContract.UploadSelection.Path, async (FileSelectionUploadRequest request, HttpContext context) =>
        {
            var value = await Required<FileTransferService>(context).UploadSelectionAsync(Session(context), request,
                Header(context, "Idempotency-Key"), context.RequestAborted);
            context.Response.Headers.ETag = FileTransferStore.ETag(value); return Results.Ok(value);
        }).WithMetadata(ApiContract.UploadSelection);
        app.MapGet(ApiContract.Asset.Path, (string id, HttpContext context) =>
        {
            var value = Required<FileTransferService>(context).Asset(id, Session(context));
            context.Response.Headers.ETag = FileTransferStore.AssetETag(value); return Results.Ok(value);
        }).WithMetadata(ApiContract.Asset);
        app.MapGet(ApiContract.AssetContent.Path, (string id, HttpContext context) =>
            Required<FileTransferService>(context).DownloadAsync(id, Session(context), context)).WithMetadata(ApiContract.AssetContent);
        app.MapPost(ApiContract.CreateFileImport.Path, async (FileImportRequest request, HttpContext context) =>
            Results.Json(await Required<FileTransferService>(context).PreviewAsync(Session(context), request,
                Header(context, "Idempotency-Key"), context.RequestAborted), statusCode: 201)).WithMetadata(ApiContract.CreateFileImport);
        app.MapGet(ApiContract.FileImport.Path, (string id, HttpContext context) =>
            Results.Ok(Required<FileTransferService>(context).GetImport(id, Session(context)))).WithMetadata(ApiContract.FileImport);
        app.MapPost(ApiContract.CommitFileImport.Path, async (string id, HttpContext context) =>
        {
            var value = await Required<FileTransferService>(context).CommitImportAsync(id, Session(context),
                Header(context, "Idempotency-Key"), Header(context, "If-Match"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + value.Id, value);
        }).WithMetadata(ApiContract.CommitFileImport);
        app.MapPost(ApiContract.CreateFileExport.Path, async (FileExportRequest request, HttpContext context) =>
        {
            var value = await Required<FileExportOperations>(context).SubmitAsync(Session(context), request,
                Header(context, "Idempotency-Key"), context.RequestAborted);
            return Results.Accepted("/api/v1/operations/" + value.Id, value);
        }).WithMetadata(ApiContract.CreateFileExport);
        app.MapGet(ApiContract.FileExport.Path, (string id, HttpContext context) =>
            Results.Ok(Required<FileTransferStore>(context).Export(id, Session(context).ClientId, Session(context).Owner))).WithMetadata(ApiContract.FileExport);
        app.MapPost(ApiContract.SaveFileExport.Path, async (string id, FileExportSaveRequest request, HttpContext context) =>
            Results.Ok(await Required<FileTransferService>(context).SaveExportAsync(id, Session(context), request,
                Header(context, "Idempotency-Key"), context.RequestAborted))).WithMetadata(ApiContract.SaveFileExport);
    }
    private static AuthSession Session(HttpContext context) => LocalApiMiddleware.Session(context);
    private static T Required<T>(HttpContext context) where T : class => context.RequestServices.GetService<T>()
        ?? throw new OperationException("storage.unavailable", "当前宿主未配置文件存储");
    private static string? Header(HttpContext context, string name)
    {
        var value = context.Request.Headers[name];
        if (value.Count > 1) throw new OperationException("request.invalid", "文件操作请求头不能重复");
        return value.Count == 0 ? null : value[0];
    }
}

internal sealed class FileTransferLifetime(FileTransferStore files, FileTransferService service, ILogger<FileTransferLifetime> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken ct) { await files.RecoverAsync(ct); await base.StartAsync(ct); }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await files.CleanupAsync(ct); }
                catch (ActivityBarrierException error) when (error.Code == "update.maintenanceActive") { }
                catch (Exception error) when (error is StorageException or OperationException or ActivityBarrierException or IOException or UnauthorizedAccessException)
                {
                    var code = error switch { StorageException e => e.Code, OperationException e => e.Code, ActivityBarrierException e => e.Code, _ => "storage.unavailable" };
                    logger.LogWarning("{code}", code);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public override Task StopAsync(CancellationToken ct) { service.Stop(); return base.StopAsync(ct); }
}
