using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

/// <summary>
/// 设置页“抓包”的数据：后端保存的设置及其修订、当前状态、网卡检测（未开始过为空）。
/// <see cref="Channels"/> 是最近一次列出的网卡，未列出时为空。
/// </summary>
public sealed record CaptureSettingsView(GameCaptureSettings Settings, string ETag, CaptureStatusView Status,
    IReadOnlyList<CaptureChannelOption>? Channels, CaptureChannelDetection? Detection);

/// <summary>列出网卡的结果；失败时 <see cref="Channels"/> 为空列表，原因写在 <see cref="Message"/>，后端错误码在 <see cref="Code"/>。</summary>
public sealed record CaptureChannelsReply(bool Ok, string Message, IReadOnlyList<CaptureChannelOption> Channels, string? Code = null);

// 全局抓包：状态供各页显示，设置与网卡检测只在设置页操作。
public partial interface IDesktopBackend
{
    /// <summary>抓包的当前状态（未连接时为 <see cref="CaptureStatusView.Offline"/>）。</summary>
    CaptureStatusView GetCaptureStatus() => CaptureStatusView.Offline;

    /// <summary>设置页的抓包数据；未连接或后端没有提供设置时为空。</summary>
    CaptureSettingsView? GetCaptureSettings() => null;

    /// <summary>保存完整的抓包设置，以读取时的 <paramref name="etag"/> 为 If-Match 发送一次。</summary>
    Task<BackendReply> SaveCaptureSettingsAsync(GameCaptureSettings settings, string etag, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>列出网卡；绑定游戏时每张网卡带有游戏连接数。</summary>
    Task<CaptureChannelsReply> ListCaptureChannelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new CaptureChannelsReply(false, string.Empty, []));

    /// <summary>开始手动调试的网卡检测（固定 20 秒）；进度在 <see cref="GetCaptureSettings"/> 里读取。</summary>
    Task<BackendReply> StartChannelDetectionAsync(CancellationToken cancellationToken) => Task.FromResult(new BackendReply(false, string.Empty));

    Task<BackendReply> CancelChannelDetectionAsync(CancellationToken cancellationToken) => Task.FromResult(new BackendReply(false, string.Empty));
}
