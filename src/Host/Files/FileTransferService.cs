using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Color;
using MabiTool.Host.DataImports;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;

namespace MabiTool.Host.Files;

public sealed class FileTransferService(FileTransferStore files, AssetStore assets, DiagnosticStore diagnostics,
    ColorHistoryService color, FileSelectionRegistry selections, LocalAuthService auth,
    MusicImportStore music, SettingsImportCoordinator settings, MusicImportOperations musicOperations,
    SettingsImportOperations settingsOperations) : IDisposable
{
    private readonly SemaphoreSlim parsing = new(1, 1);
    private readonly SemaphoreSlim selectionsUploading = new(1, 1);
    private readonly SemaphoreSlim downloads = new(4, 4);
    private readonly CancellationTokenSource stopped = new();
    internal bool Authorized(AuthSession session, string permission = ApiPermissions.FilesTransfer) =>
        !stopped.IsCancellationRequested && auth.HasPermission(session.ClientId, permission);
    internal FileAssetAccess Resolve(string id, AuthSession session)
    {
        FileTransferStore.Id(id);
        if (!Authorized(session, ApiPermissions.SystemRead)) throw new OperationException("permission.denied", "文件读取授权已结束");
        if (files.OwnedAsset(id, session.ClientId, session.Owner) is { } own) return own;
        if (diagnostics.FindExportForAsset(id, session.ClientId, session.Owner) is { } diagnostic)
        {
            if (!Authorized(session, ApiPermissions.DiagnosticsExport)) throw new OperationException("permission.denied", "诊断资产读取需要诊断导出权限");
            return new(diagnostic.Asset, "diagnostics-" + diagnostic.Id + ".json.gz", "diagnostics");
        }
        var metadata = assets.GetMetadata(id);
        if (metadata is { ModuleId: "color", Scope: var scope } && scope.StartsWith("history:", StringComparison.Ordinal))
            return new(color.Asset(id, session), "color-history-" + id + ".json", "color-history");
        // 目标色集合图只由公开参数计算，不含用户记录；已授权读取的客户端均可下载。
        if (metadata is { ModuleId: "color", Scope: var palette } && palette.StartsWith("palette:", StringComparison.Ordinal))
            return new(metadata, "color-palette-" + id + ".png", "color-palette");
        throw new OperationException("resource.notFound", "已授权文件资产不存在");
    }
    public AssetSnapshot Asset(string id, AuthSession session)
    { var value = Resolve(id, session); return assets.Get(value.Asset.Id) ?? throw new OperationException("storage.assetDamaged", "已登记资产缺失"); }
    public async Task<FileUploadSnapshot> UploadAsync(string id, AuthSession session, Stream input, string? key, string? etag, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        return await files.UploadAsync(id, session.ClientId, input, key, etag, () => Authorized(session), deadline.Token);
    }
    public async Task<FileUploadSnapshot> UploadSelectionAsync(AuthSession session, FileSelectionUploadRequest request, string? key, CancellationToken ct)
    {
        FileTransferStore.Key(key); var hash = FileTransferStore.Hash(request);
        if (files.LookupKey<FileUploadSnapshot>(session.ClientId, "selection", key!, hash) is { } previous) return previous;
        if (!await selectionsUploading.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一个文件选择正在上传");
        try
        {
            using var deadline = Deadline(ct);
            return await selections.ReadAsync(request.SelectionId, session, async (selection, input) =>
            {
                var internalKey = "selected-" + FileTransferStore.Hash(new { key, request.SelectionId });
                var media = Path.GetExtension(selection.Name).ToLowerInvariant() switch { ".zip" => "application/zip", ".json" => "application/json",
                    ".gz" => "application/gzip", _ => "application/octet-stream" };
                var created = await files.CreateAsync(session.ClientId, new(selection.Name, selection.SizeBytes!.Value, selection.Sha256!, media), internalKey, deadline.Token);
                var result = await files.UploadAsync(created.Id, session.ClientId, input, internalKey, FileTransferStore.ETag(created),
                    () => Authorized(session) && selections.ReadAuthorized(selection.Id, session), deadline.Token);
                await files.RecordKeyAsync(session.ClientId, "selection", key!, hash, result, deadline.Token);
                return result;
            }, deadline.Token);
        }
        finally { selectionsUploading.Release(); }
    }
    public async Task<FileImportPreview> PreviewAsync(AuthSession session, FileImportRequest request, string? key, CancellationToken ct)
    {
        FileTransferStore.Key(key); Permission(session, request.Project);
        request = JsonSerializer.Deserialize<FileImportRequest>(JsonSerializer.Serialize(request, ContractJson.Options), ContractJson.Options)!;
        var hash = FileTransferStore.Hash(request);
        if (files.LookupKey<FileImportLink>(session.ClientId, "import", key!, hash) is { } previous) return GetImport(previous.Id, session);
        if (!await parsing.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一个文件导入正在解析");
        try
        {
            using var deadline = Deadline(ct);
            if (request.UploadIds is null || request.UploadIds.Any(x => x is null) ||
                request.UploadIds.Distinct(StringComparer.Ordinal).Count() != request.UploadIds.Count ||
                (request.ArchiveUploadId is null ? request.UploadIds.Count is < 1 or > 7 : request.UploadIds.Count != 0))
                throw new OperationException("request.invalid", "导入须选择原文件或单个 ZIP");
            var inputs = new List<ImportSourceFile>(); var leases = new List<AssetReadLease>();
            try
            {
                if (request.ArchiveUploadId is { } archiveId)
                {
                    var upload = files.Get(archiveId, session.ClientId);
                    if (upload.State != "ready" || upload.Asset is null || upload.MediaType != "application/zip")
                        throw new OperationException("storage.assetInvalid", "导入包须为完整上传的 ZIP");
                    var lease = await assets.OpenReadAsync(upload.Asset.Id, "file-read:" + Guid.NewGuid().ToString("N"), FilePackageReader.MaximumUploadBytes, deadline.Token);
                    leases.Add(lease);
                    inputs.AddRange(await FilePackageReader.ReadAsync(lease.Content, MobiFolioImport.MaximumSourceBytes, deadline.Token));
                }
                else
                {
                    ulong total = 0;
                    foreach (var id in request.UploadIds)
                    {
                        var upload = files.Get(id, session.ClientId);
                        if (upload.State != "ready" || upload.Asset is null) throw new OperationException("storage.assetInvalid", "导入源尚未完整上传");
                        total += upload.SizeBytes.Value;
                        if (total > MobiFolioImport.MaximumSourceBytes) throw new OperationException("request.bodyTooLarge", "已选原文件超过模块解析预算");
                        var lease = await assets.OpenReadAsync(upload.Asset.Id, "file-read:" + Guid.NewGuid().ToString("N"), MobiFolioImport.MaximumSourceBytes, deadline.Token);
                        leases.Add(lease);
                        using var memory = new MemoryStream(); await lease.Content.CopyToAsync(memory, 65536, deadline.Token);
                        inputs.Add(new(upload.Name, Convert.ToBase64String(memory.ToArray())));
                    }
                }
                FileImportPreview preview;
                var targetKey = "files-preview-" + FileTransferStore.Hash(key);
                if (request.Project == "mobifolio")
                {
                    var scope = request.Scope ?? throw new OperationException("request.invalid", "音乐导入需要地区、服务器与角色身份");
                    var value = await music.PreviewAsync(session.ClientId, new(request.SourceId, request.SourceLabel, scope.Region, scope.RealmId,
                        scope.CharacterId, inputs, request.ArtistMappings), targetKey, deadline.Token);
                    preview = Wrap(value);
                }
                else
                {
                    var value = await settings.PreviewAsync(request.Project, session.ClientId, new(request.SourceId, request.SourceLabel, inputs,
                        request.BindingOverrides, request.ProfileNames), targetKey, deadline.Token);
                    preview = Wrap(value);
                }
                await files.RegisterImportAsync(preview, session.ClientId, key!, hash, deadline.Token);
                return preview;
            }
            finally { foreach (var lease in leases) await lease.DisposeAsync(); }
        }
        finally { parsing.Release(); }
    }
    public FileImportPreview GetImport(string id, AuthSession session)
    {
        var project = files.ImportProject(id, session.ClientId); Permission(session, project);
        return project == "mobifolio" ? Wrap(music.Get(session.ClientId, id)) : Wrap(settings.Get(project, session.ClientId, id));
    }
    public Task<OperationSnapshot> CommitImportAsync(string id, AuthSession session, string? key, string? etag, CancellationToken ct)
    {
        FileTransferStore.Key(key); var project = files.ImportProject(id, session.ClientId); Permission(session, project);
        var targetKey = "files-commit-" + FileTransferStore.Hash(key);
        return project == "mobifolio" ? musicOperations.SubmitAsync(session.ClientId, id, targetKey, etag, ct)
            : settingsOperations.SubmitAsync(project, session.ClientId, id, targetKey, etag, ct);
    }
    private void Permission(AuthSession session, string project)
    {
        var permission = project switch { "mobifolio" => ApiPermissions.MusicEdit, "mabisync" => ApiPermissions.RoomsCoordinate,
            "color-studio" => ApiPermissions.ColorEdit, _ => throw new OperationException("request.invalid", "文件导入项目无效") };
        if (!Authorized(session, permission)) throw new OperationException("permission.denied", "文件导入需要所属模块权限");
    }
    private static FileImportPreview Wrap(MusicImportPreview value) => new(value.Id, "mobifolio", "mobifolio-1",
        "\"music-library-" + value.ExpectedLibraryRevision + "\"", value.ExpiresAt, value, null);
    private static FileImportPreview Wrap(SettingsImportPreview value) => new(value.Id, value.Project, value.Project + "-1",
        value.TargetETag, value.ExpiresAt, null, value);
    public async Task DownloadAsync(string id, AuthSession session, HttpContext context)
    {
        if (!await downloads.WaitAsync(0, context.RequestAborted)) throw new OperationException("resource.capacityExceeded", "同时下载数量达到上限");
        try
        {
            var access = Resolve(id, session);
            using var deadline = Deadline(context.RequestAborted);
            await using var lease = await assets.OpenReadAsync(access.Asset.Id, "file-read:" + Guid.NewGuid().ToString("N"), FilePackageReader.MaximumUploadBytes, deadline.Token);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = (long)lease.Snapshot.SizeBytes.Value;
            context.Response.Headers.ETag = FileTransferStore.AssetETag(lease.Snapshot);
            context.Response.Headers["X-Content-Sha256"] = lease.Snapshot.Sha256;
            context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = access.Name }.ToString();
            var block = new byte[65536]; int count;
            while ((count = await lease.Content.ReadAsync(block, deadline.Token)) != 0)
            {
                if (!Authorized(session, ApiPermissions.SystemRead) || access.Category == "diagnostics" && !Authorized(session, ApiPermissions.DiagnosticsExport))
                    throw new OperationException("permission.denied", "文件读取授权已结束");
                await context.Response.Body.WriteAsync(block.AsMemory(0, count), deadline.Token);
            }
        }
        finally { downloads.Release(); }
    }
    public async Task<FileSaveReceipt> SaveExportAsync(string id, AuthSession session, FileExportSaveRequest request, string? key, CancellationToken ct)
    {
        return await files.SaveAsync(session.ClientId, id, request.SelectionId, key, async () =>
        {
            var artifact = files.Export(id, session.ClientId, session.Owner);
            using var deadline = Deadline(ct);
            await using var lease = await assets.OpenReadAsync(artifact.Asset.Id, "file-read:" + Guid.NewGuid().ToString("N"), FilePackageReader.MaximumUploadBytes, deadline.Token);
            return await selections.SaveAsync(request.SelectionId, session, artifact, lease.Content, deadline.Token);
        }, ct);
    }
    private CancellationTokenSource Deadline(CancellationToken ct)
    { var source = CancellationTokenSource.CreateLinkedTokenSource(ct, stopped.Token); source.CancelAfter(TimeSpan.FromMinutes(5)); return source; }
    public void Stop() => stopped.Cancel();
    public void Dispose() { stopped.Cancel(); stopped.Dispose(); }
}
