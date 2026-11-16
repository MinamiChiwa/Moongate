namespace MabiTool.Desktop.Backend;

/// <summary>
/// 编辑类意图的回执：<see cref="Ok"/> 表示后端已受理，<see cref="Message"/> 是后端给出的状态或错误码，原样显示；
/// <see cref="Code"/> 是后端拒绝时的稳定错误码，供界面按原因处理（例如 Npcap 缺失时弹出安装指引），其余为空。
/// </summary>
public sealed record BackendReply(bool Ok, string Message, string? Code = null);

/// <summary>
/// 后端冻结的一张屏幕画面：<see cref="X"/>、<see cref="Y"/> 是画面左上角的物理屏幕坐标（可为负），
/// <see cref="Rgb"/> 是逐行排列的 RGB24 像素。画面只在取色期间保留，用完由页面释放。
/// </summary>
public sealed record ScreenCapture(string Id, int X, int Y, int Width, int Height, byte[] Rgb);

/// <summary>捕获屏幕的回执；捕获失败时 <see cref="Capture"/> 为空，<see cref="Message"/> 是后端的错误码。</summary>
public sealed record CaptureReply(ScreenCapture? Capture, string Message);

/// <summary>
/// 后端列出的一个候选游戏窗口。<see cref="Name"/>、<see cref="ProcessName"/> 是系统原文；
/// <see cref="Current"/> 表示它与当前绑定是同一个窗口（进程号、进程启动时间、窗口标识都相同）。
/// </summary>
public sealed record GameWindowChoice(string Id, string Name, string ProcessName, int ProcessId, string Region, bool Current);

/// <summary>读取候选窗口的回执：<see cref="Ok"/> 为假时 <see cref="Message"/> 是后端的错误码，<see cref="Windows"/> 为空。</summary>
public sealed record GameWindowListReply(bool Ok, string Message, IReadOnlyList<GameWindowChoice> Windows);

/// <summary>
/// 后端的更新策略，四个字段与契约一致。界面修改自动检查、检查间隔和限速；<see cref="DownloadConcurrency"/> 不显示，保存时原值带回。
/// <see cref="MaximumBytesPerSecond"/> 的单位是字节每秒，为空表示不限速（后端默认）；设定值在 65,536–67,108,864 之间。
/// </summary>
public sealed record UpdatePolicyView(bool AutomaticChecks, int CheckIntervalHours, int DownloadConcurrency, int? MaximumBytesPerSecond)
{
    public bool Unlimited => MaximumBytesPerSecond is null;
}

/// <summary>
/// 读取合奏本机设置的回执：<see cref="Value"/> 是后端的完整设置（界面不显示的字段保存时原样带回），
/// <see cref="ETag"/> 是读取时的修订标记，保存时作为 If-Match；读取失败时 <see cref="Value"/> 为空，<see cref="Message"/> 是错误码。
/// </summary>
public sealed record RoomPreferencesReply(MabiTool.Contracts.RoomPreferences? Value, string ETag, string Message);

/// <summary>用户按下的组合键。<see cref="Key"/> 用后端快捷键表的键名（例如 F9、A、D1、PageUp）。</summary>
public sealed record KeyChord(string Key, bool Control, bool Alt, bool Shift, bool Windows)
{
    public override string ToString() => string.Join(" + ", new[]
    {
        Control ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, Windows ? "Win" : null,
        Key.Length == 2 && Key[0] == 'D' && char.IsAsciiDigit(Key[1]) ? Key[1..] : Key
    }.Where(part => part is not null));
}
