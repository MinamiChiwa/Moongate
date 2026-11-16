namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>
/// 合奏页的前端偏好：昵称来源，由前端偏好文件保存。卡片的折叠状态在 <see cref="Infrastructure.CardStateStore"/>（页面标识 <see cref="Page"/>）。
/// </summary>
public static class RoomsPreference
{
    /// <summary>合奏页的页面标识与底部状态卡的卡片标识（状态卡自己排版，模型直接读写卡片状态）。</summary>
    public const string Page = "rooms";
    public const string Status = "status";

    public static event EventHandler? Changed;

    /// <summary>读取偏好时恢复，不触发保存。</summary>
    internal static void Restore(string? nicknameSource = null) =>
        NicknameSource = nicknameSource == CustomNickname ? CustomNickname : CaptureNickname;

    /// <summary>昵称来源：抓包识别的本机身份（缺省），或自定义昵称（值保存在后端本机设置的 Nickname）。</summary>
    public const string CaptureNickname = "capture";
    public const string CustomNickname = "custom";

    public static string NicknameSource { get; private set; } = CaptureNickname;

    public static void SetNicknameSource(string source)
    {
        var next = source == CustomNickname ? CustomNickname : CaptureNickname;
        if (next == NicknameSource) return;
        NicknameSource = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
