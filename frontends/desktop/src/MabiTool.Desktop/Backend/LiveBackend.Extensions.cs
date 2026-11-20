using System.Security.Cryptography;
using MabiTool.Client;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 扩展模块：列表随页面状态一起读取（每五秒，发出意图后立即重读，内容没变时沿用原对象）；安装先上传本机 .zip 再请后端生成预览，确认后安装。
public sealed partial class LiveBackend
{
    private ExtensionCatalog? extensions;

    public ExtensionCatalog? GetExtensions()
    {
        lock (gate) return extensions;
    }

    private byte[]? extensionsContent;

    // 旧宿主没有扩展接口（404）或读取被拒绝时为空，页面据此停用安装入口。返回内容是否有变化。
    private async Task<bool> RefreshExtensionsAsync(MabiToolClient api, CancellationToken token)
    {
        var value = await Try(() => api.GetExtensionsAsync(token));
        var content = value is null ? null : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        lock (gate)
        {
            if (content is not null && extensionsContent is not null && content.AsSpan().SequenceEqual(extensionsContent)) return false;
            if (value is null && extensions is null) return false;
            (extensions, extensionsContent) = (value, content);
            return true;
        }
    }

    public async Task<ExtensionPreviewReply> PreviewExtensionAsync(string? filePath, string? inboxName, CancellationToken cancellationToken)
    {
        ExtensionInstallPreview? preview = null;
        var reply = await ExtensionReplyAsync(async (api, token) =>
        {
            ExtensionPreviewRequest request;
            if (filePath is not null)
            {
                await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
                file.Position = 0;
                string? tag = null;
                var key = IdempotencyKey();
                var upload = await api.CreateFileUploadAsync(new("extension.zip", new((ulong)file.Length), sha, "application/zip"), key, token,
                    metadata => tag = metadata.ETag);
                upload = await api.UploadFileContentAsync(upload.Id, file, IdempotencyKey(), tag ?? string.Empty, token);
                request = new(upload.Id, null);
            }
            else request = new(null, inboxName);
            preview = await api.PreviewExtensionInstallAsync(request, token);
            return string.Empty;
        }, cancellationToken, remember: false);
        return new(reply.Ok ? preview : null, reply.Message);
    }

    public Task<BackendReply> InstallExtensionAsync(string previewId, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            var installed = await api.InstallExtensionAsync(previewId, token);
            return Pick($"已安装扩展 {installed.Id} {installed.Version}", $"已安裝擴充 {installed.Id} {installed.Version}");
        }, cancellationToken);

    public Task<BackendReply> SetExtensionEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            await api.SetExtensionEnabledAsync(id, new(enabled), token);
            return enabled ? Pick($"已启用扩展 {id}", $"已啟用擴充 {id}") : Pick($"已停用扩展 {id}", $"已停用擴充 {id}");
        }, cancellationToken);

    public Task<BackendReply> UninstallExtensionAsync(string id, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            await api.UninstallExtensionAsync(id, token);
            return Pick($"已卸载扩展 {id}", $"已解除安裝擴充 {id}");
        }, cancellationToken);

    public Task<BackendReply> SetExtensionPermissionAsync(string id, string permission, bool granted, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            await api.SetExtensionPermissionsAsync(id, new(new Dictionary<string, bool> { [permission] = granted }), token);
            return granted ? Pick("已授予权限", "已授予權限") : Pick("已撤销权限，立即生效", "已撤銷權限，立即生效");
        }, cancellationToken);

    public Task<BackendReply> RestartExtensionAsync(string id, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            await api.RestartExtensionAsync(id, token);
            return Pick($"已重启扩展 {id}", $"已重新啟動擴充 {id}");
        }, cancellationToken);

    private ExtensionSurfaceCatalog? extensionSurfaces;

    /// <summary>
    /// 活动胶囊里的扩展状态：取最近更新的一条，写成“扩展名：状态”，状态最多 16 字；没有运行中扩展的状态时为空。
    /// </summary>
    internal (string Title, string Detail)? ExtensionActivity()
    {
        ExtensionSurfaceCatalog? surfaces;
        ExtensionCatalog? catalog;
        lock (gate) { surfaces = extensionSurfaces; catalog = extensions; }
        if (surfaces?.Activities.OrderByDescending(x => x.Revision.Value).FirstOrDefault() is not { } activity) return null;
        var name = Pages.Modules.ModuleText.Pick(catalog?.Installed.FirstOrDefault(x => x.Id == activity.ExtensionId)?.Package?.DisplayNames) ?? activity.ExtensionId;
        var text = activity.Text.Length > 16 ? activity.Text[..16] : activity.Text;
        return (Pick($"扩展 {name}：{text}", $"擴充 {name}：{text}"), Pick($"来自扩展 {name} 的状态", $"來自擴充 {name} 的狀態"));
    }

    public ExtensionSurfaceCatalog? GetExtensionSurfaces()
    {
        lock (gate) return extensionSurfaces;
    }

    // 调用方持有 gate。只在有运行中的带代码扩展、或上一次还有界面时读取扩展界面状态。
    private bool SurfacesWanted() => extensions?.Installed.Any(item => item.Runtime?.State is "running" or "starting") == true ||
        extensionSurfaces is { Surfaces.Count: > 0 } || extensionSurfaces is { Settings.Count: > 0 } ||
        extensionSurfaces is { Activities.Count: > 0 } || extensionSurfaces?.FileRequests is { Count: > 0 };

    // 扩展界面的状态由扩展推送（例如计时），需要时每秒读取一次，修订不变时不通知界面。
    private async Task RefreshExtensionSurfacesAsync(MabiToolClient api, CancellationToken token)
    {
        lock (gate)
        {
            if (!SurfacesWanted() || !Elapsed(lastSurfacesRead, StatusInterval)) return;
            lastSurfacesRead = Now;
        }
        var value = await Try(() => api.GetExtensionSurfacesAsync(token));
        bool changed;
        lock (gate)
        {
            changed = value?.Revision != extensionSurfaces?.Revision;
            extensionSurfaces = value;
        }
        if (changed) Notify();
    }

    public Task<BackendReply> SendExtensionUiEventAsync(string id, ExtensionUiEvent value, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            var catalog = await api.SendExtensionUiEventAsync(id, value, token);
            lock (gate) extensionSurfaces = catalog;
            return string.Empty;
        }, cancellationToken, remember: false);

    public Task<BackendReply> SaveExtensionSettingsAsync(string id, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values,
        CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            await api.SaveExtensionSettingsAsync(id, new(values), token);
            return Pick("扩展设置已保存", "擴充設定已儲存");
        }, cancellationToken);

    // 文件只经上传接口交给宿主，扩展得到内容而不是路径；超过上限的文件在本机就拒绝，扩展收到取消。
    public Task<BackendReply> AnswerExtensionFileRequestAsync(string extensionId, string requestId, string? filePath, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            if (filePath is not null && new FileInfo(filePath) is { Exists: true } info && info.Length <= 512 * 1024)
            {
                await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
                file.Position = 0;
                string? tag = null;
                var upload = await api.CreateFileUploadAsync(new(Path.GetFileName(filePath), new((ulong)file.Length), sha, "application/octet-stream"),
                    IdempotencyKey(), token, metadata => tag = metadata.ETag);
                upload = await api.UploadFileContentAsync(upload.Id, file, IdempotencyKey(), tag ?? string.Empty, token);
                await api.AnswerExtensionFileRequestAsync(requestId, new(upload.Id), token);
                return Pick("已把所选文件交给扩展", "已把所選檔案交給擴充");
            }
            await api.AnswerExtensionFileRequestAsync(requestId, new(null, true), token);
            return filePath is null ? string.Empty : Pick("所选文件超过 512 KiB，未交给扩展", "所選檔案超過 512 KiB，未交給擴充");
        }, cancellationToken);

    public Task<BackendReply> SetExtensionDevelopmentModeAsync(bool enabled, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            await api.SetExtensionDevelopmentModeAsync(new(enabled), token);
            return enabled ? Pick("已开启开发模式", "已開啟開發模式") : Pick("已关闭开发模式，开发中的扩展已停止", "已關閉開發模式，開發中的擴充已停止");
        }, cancellationToken);

    public Task<BackendReply> LoadDevelopmentExtensionAsync(string directory, CancellationToken cancellationToken) =>
        ExtensionReplyAsync(async (api, token) =>
        {
            var installed = await api.LoadDevelopmentExtensionAsync(new(directory), token);
            return Pick($"已加载开发中的扩展 {installed.Id}，启用后运行", $"已載入開發中的擴充 {installed.Id}，啟用後執行");
        }, cancellationToken);

    public async Task<ExtensionLogPage?> GetExtensionLogsAsync(string id, CancellationToken cancellationToken)
    {
        MabiToolClient? api;
        lock (gate) api = client;
        if (api is null) return null;
        return await Try(() => api.GetExtensionLogsAsync(id, cancellationToken));
    }

    // 与其他编辑意图相同的回执与重读；扩展的拒绝原因以后端的中文说明加原因码显示，用户能看懂为什么不能安装。
    private async Task<BackendReply> ExtensionReplyAsync(Func<MabiToolClient, CancellationToken, Task<string>> send,
        CancellationToken cancellationToken, bool remember = true)
    {
        try
        {
            return await ReplyAsync(async (api, token) =>
            {
                try { return await send(api, token); }
                catch (MabiToolApiException error) when (error.Problem is { } problem)
                {
                    throw new ExtensionRejectedException(Localization.CodeText.Readable(problem.Title) ?? Localization.CodeText.Reason(problem.Code));
                }
            }, cancellationToken, remember);
        }
        catch (ExtensionRejectedException error)
        {
            if (remember) Remember(error.Message);
            lock (gate) pagesDue = true;
            Signal();
            return new(false, error.Message);
        }
    }

    private sealed class ExtensionRejectedException(string message) : Exception(message);
}
