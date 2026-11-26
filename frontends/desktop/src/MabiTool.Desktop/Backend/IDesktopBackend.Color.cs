using Avalonia;
using MabiTool.Desktop.Pages.Dyeing;

namespace MabiTool.Desktop.Backend;

// 染色页的结构化意图：草稿、命名方案、目标色集合、屏幕取色与寻色记录。结果原样交回页面，是否接受由后端判定。
public partial interface IDesktopBackend
{
    /// <summary>把区域卡的当前设置写入后端草稿；带读取时的修订与新的幂等键发送一次。</summary>
    Task<BackendReply> SaveColorDraftAsync(ColorDraft draft, CancellationToken cancellationToken);

    /// <summary>把 <paramref name="draft"/> 存为名为 <paramref name="name"/> 的新方案。</summary>
    Task<BackendReply> SaveColorProfileAsync(string name, ColorDraft draft, CancellationToken cancellationToken);

    /// <summary>以命名方案覆盖后端草稿；成功后页面数据里的草稿就是该方案的设置。</summary>
    Task<BackendReply> LoadColorProfileAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>删除一个命名方案。</summary>
    Task<BackendReply> DeleteColorProfileAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>
    /// 按目标色、颜色要求（<see cref="ColorCodes.ExactHex"/> 或 <see cref="ColorCodes.Similar"/>）与容差取目标色集合的一张图与颜色数：
    /// <paramref name="full"/> 为假时是卡片缩略图，为真时是集合浮层的全集图。请求同步等到出图；参数变化时调用方取消旧请求。
    /// 没有取到时 <see cref="ColorAtlasReply.Atlas"/> 为空，消息说明原因。
    /// </summary>
    Task<ColorAtlasReply> GetColorAtlasAsync(string targetHex, string matchMode, double tolerance, bool full, CancellationToken cancellationToken);

    /// <summary>请后端冻结一块显示器的物理像素区域 <paramref name="region"/>（可为负原点），读回 RGB24 画面供吸管取色。</summary>
    Task<CaptureReply> CaptureScreenAsync(PixelRect region, CancellationToken cancellationToken);

    /// <summary>释放冻结画面。</summary>
    Task ReleaseCaptureAsync(string captureId, CancellationToken cancellationToken);

    /// <summary>读取后端保留的寻色记录（最新 30 条，从新到旧）；<paramref name="beforeOperationId"/> 是分页标记，页面只读第一页。</summary>
    Task<ColorHistoryReply> QueryColorHistoryAsync(string? beforeOperationId, CancellationToken cancellationToken);
}
