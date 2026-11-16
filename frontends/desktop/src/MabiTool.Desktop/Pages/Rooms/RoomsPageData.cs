using MabiTool.Contracts;

using MabiTool.Desktop.Backend;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>本机房间服务的状态（高级连接层）。</summary>
public sealed record RoomServerView(string StateText, bool Running, string? Mode, string? LocalEndpoint,
    IReadOnlyList<string> LanEndpoints, string TunnelText, string? TunnelEndpoint)
{
    public static RoomServerView Unknown { get; } = new(string.Empty, false, null, null, [], string.Empty, null);
}

/// <summary>绑定的游戏窗口客户区（屏幕物理像素）与 DPI，点选按钮位置时据此换算坐标。</summary>
public sealed record RoomGameWindow(int X, int Y, int Width, int Height, uint Dpi);

/// <summary>
/// 抓包识别的只读状态：全局抓包的状态，以及已识别的本机昵称与本机正在演奏的乐谱。
/// 抓包由设置页统一管理；合奏在房间内时自动登记音乐识别。
/// </summary>
public sealed record RoomCapture(CaptureStatusView Status, string? Nickname, ObservedMusic? Music)
{
    public static RoomCapture Unknown { get; } = new(CaptureStatusView.Offline, null, null);
    public bool Running => Status.State == "running";
}

/// <summary>
/// 合奏页的后端状态。房间内容来自房间协议原样的 JSON（<see cref="Room"/>，未入房为空）；
/// 本机动作与房主的本轮设置都在 <see cref="Preferences"/>，保存时以 <see cref="PreferencesETag"/> 为 If-Match。
/// <see cref="OperateGame"/> 是本机的“操作游戏”开关（房间会话的 <c>operateGame</c>）；<see cref="Simulation"/> 为真表示就绪与本轮只模拟
/// （关闭了“操作游戏”，或宿主本身是模拟输入）；
/// <see cref="StopKey"/> 是实际注册的紧急停止键，没有注册时为空。
/// <see cref="Feedback"/> 是最近一次合奏操作的结果；<see cref="FallbackInvitation"/> 在剪贴板不可用时给出完整邀请。
/// <see cref="ExitReason"/> 是房间服务让本机退出的原因（<c>kicked</c> 被房主移出、<c>roomClosed</c> 房主关闭房间），重新建房或加入后为空。
/// <see cref="Timing"/> 是读取时的时间基准（没有校时样本时为空），之后按本机单调时钟递减；更新的值由 <c>IDesktopBackend.GetRoomTiming</c> 给出。
/// <see cref="Capture"/> 是抓包识别的状态；<see cref="RoundBusy"/> 表示开始或就绪请求正在等待后端执行结果，期间不重复发送。
/// </summary>
public sealed record RoomsPageData(bool Connected, string SessionState, string? SelfId, RoomState? Room,
    RoomServerView Server, RoomPreferences? Preferences, string PreferencesETag, bool Simulation,
    RoomGameWindow? GameWindow, string? GameWindowName, string? StopKey, RoomFeedback? Feedback,
    string? LocalPreparationId = null, string? ObservedNickname = null, IReadOnlyList<string>? Instruments = null,
    string? FallbackInvitation = null, bool InvitationCopied = false, RoomTiming? Timing = null, bool OperateGame = true, string? ExitReason = null,
    RoomCapture? Capture = null, bool RoundBusy = false)
{
    public bool InRoom => Room is not null;
    public bool IsHost => Room is { HostId: { } host } && host == SelfId;
    public bool RoomConnected => SessionState == "connected";
    public RoomMember? Self => Room?.Members.FirstOrDefault(member => member.Id == SelfId);

    public static RoomsPageData Offline { get; } = new(false, "disconnected", null, null, RoomServerView.Unknown, null,
        string.Empty, false, null, null, null, null);
}
