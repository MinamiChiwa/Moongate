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

    /// <summary>授予或撤销扩展的一项权限；撤销在后端立即生效。</summary>
    Task<BackendReply> SetExtensionPermissionAsync(string id, string permission, bool granted, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    /// <summary>重启带代码的扩展，清除失败与重启计数。</summary>
    Task<BackendReply> RestartExtensionAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    /// <summary>扩展日志的最后若干行；读取失败时为空。</summary>
    Task<ExtensionLogPage?> GetExtensionLogsAsync(string id, CancellationToken cancellationToken) => Task.FromResult<ExtensionLogPage?>(null);

    /// <summary>运行中扩展提供的页面、浮窗元件、状态、设置表单与命令；后端没有该接口或没有运行中的扩展时为空。</summary>
    ExtensionSurfaceCatalog? GetExtensionSurfaces() => null;

    /// <summary>把扩展界面上的点击或修改交给后端，由后端转给扩展。</summary>
    Task<BackendReply> SendExtensionUiEventAsync(string id, ExtensionUiEvent value, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    /// <summary>保存扩展的设置值；后端核对类型与范围。</summary>
    Task<BackendReply> SaveExtensionSettingsAsync(string id, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values,
        CancellationToken cancellationToken) => Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    /// <summary>开启或关闭开发模式加载。</summary>
    Task<BackendReply> SetExtensionDevelopmentModeAsync(bool enabled, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    /// <summary>以开发模式加载一个本机目录（根目录有 extension.json）。</summary>
    Task<BackendReply> LoadDevelopmentExtensionAsync(string directory, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    /// <summary>
    /// 回复扩展的文件请求：<paramref name="filePath"/> 是用户在文件选择框里选定的本机文件（前端经上传接口送出），为空表示取消。
    /// </summary>
    Task<BackendReply> AnswerExtensionFileRequestAsync(string extensionId, string requestId, string? filePath, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, ExtensionsUnavailable));

    static string ExtensionsUnavailable => Pick("后端尚未提供扩展模块安装，暂不可用", "後端尚未提供擴充模組安裝，暫不可用");
}
