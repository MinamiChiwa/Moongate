using System.Security.Cryptography;
using MabiTool.Client;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 扩展模块：列表随页面状态一起读取（每五秒，发出意图后立即重读）；安装先上传本机 .zip 再请后端生成预览，确认后安装。
public sealed partial class LiveBackend
{
    private ExtensionCatalog? extensions;

    public ExtensionCatalog? GetExtensions()
    {
        lock (gate) return extensions;
    }

    // 旧宿主没有扩展接口（404）或读取被拒绝时为空，页面据此停用安装入口。
    private async Task RefreshExtensionsAsync(MabiToolClient api, CancellationToken token)
    {
        var value = await Try(() => api.GetExtensionsAsync(token));
        lock (gate) extensions = value;
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
                    throw new ExtensionRejectedException(problem.Title is { Length: > 0 } title ? title : Localization.CodeText.Reason(problem.Code));
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
