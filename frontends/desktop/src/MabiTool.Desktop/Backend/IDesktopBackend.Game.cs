namespace MabiTool.Desktop.Backend;

/// <summary>
/// 激活绑定的游戏窗口的结果（后端 <c>POST /game/window-activation</c>）。
/// <see cref="Bound"/> 为假表示没有绑定游戏窗口、没有发出请求；<see cref="Foreground"/> 是请求后读到的窗口实际是否在前台，
/// 以它判断是否已切到游戏；<see cref="ErrorCode"/> 是后端拒绝时的错误码（如 <c>input.bindingChanged</c>、<c>resource.busy</c>）。
/// <see cref="Message"/> 是未切到前台时给界面的说明，切到前台或没有绑定时为空。
/// </summary>
public sealed record GameWindowActivation(bool Bound, bool Attempted, bool Restored, bool Foreground, string? ErrorCode, string Message)
{
    /// <summary>没有绑定游戏窗口：不发请求。</summary>
    public static GameWindowActivation Unbound { get; } = new(false, false, false, false, null, string.Empty);

    /// <summary>有绑定但没有切到前台（被拒绝、没有窗口来源、系统不允许前置或请求失败）。</summary>
    public bool Missed => Bound && !Foreground;
}

// 游戏窗口的共用意图：恢复并前置当前绑定的游戏窗口，不发送任何游戏输入。供吸管取色、合奏点选按钮位置等需要游戏在前台的操作使用。
public partial interface IDesktopBackend
{
    /// <summary>
    /// 恢复并前置当前绑定的游戏窗口。没有绑定时直接返回 <see cref="GameWindowActivation.Unbound"/>，不发请求。
    /// 调用时 Moongate 的窗口应仍在前台：请求发出前允许后端进程前置窗口（Windows 限制后台进程抢占前台）。
    /// </summary>
    Task<GameWindowActivation> ActivateGameWindowAsync(CancellationToken cancellationToken);
}
