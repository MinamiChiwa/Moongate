using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>安装预览的回执：<see cref="Preview"/> 为空时 <see cref="Message"/> 是后端给出的拒绝原因。</summary>
public sealed record ExtensionPreviewReply(ExtensionInstallPreview? Preview, string Message);

// 扩展模块的列表、安装预览、安装、启停与卸载；后端没有扩展接口时列表为空，操作返回停用说明。
public partial interface IDesktopBackend
{
    /// <summary>后端的扩展列表；未连接或后端没有扩展接口时为空。</summary>
    ExtensionCatalog? GetExtensions() => null;

    /// <summary>
    /// 请后端为一个扩展包生成安装预览：<paramref name="filePath"/> 是用户选定的本机 .zip（前端经上传接口送出），
    /// <paramref name="inboxName"/> 是投放位置里的文件名，二选一。
    /// </summary>
    Task<ExtensionPreviewReply> PreviewExtensionAsync(string? filePath, string? inboxName, CancellationToken cancellationToken) =>
        Task.FromResult(new ExtensionPreviewReply(null, ExtensionsUnavailable));

    Task<BackendReply> InstallExtensionAsync(string previewId, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    Task<BackendReply> SetExtensionEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    Task<BackendReply> UninstallExtensionAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    static string ExtensionsUnavailable => Pick("后端尚未提供扩展模块安装，暂不可用", "後端尚未提供擴充模組安裝，暫不可用");
}
