using System.Runtime.InteropServices;
using Avalonia;

namespace MabiTool.Desktop.Overlay;

/// <summary>系统光标位置（虚拟桌面物理像素）与主按键是否按住；浮窗拖动按它定位与判定松开。</summary>
public interface IOverlayCursor
{
    /// <summary>光标位置；读不到时为空。</summary>
    PixelPoint? Position { get; }

    /// <summary>主按键（交换左右键后为右键）当前按住。</summary>
    bool PrimaryButtonDown { get; }
}

/// <summary>
/// Windows 的系统光标：GetCursorPos 返回物理像素（进程按每显示器 DPI 感知 v2 运行，Avalonia 默认设置），
/// GetAsyncKeyState 读全局按键状态，与哪个窗口在前台、谁持有鼠标捕获无关。
/// </summary>
public sealed class Win32OverlayCursor : IOverlayCursor
{
    public static Win32OverlayCursor Instance { get; } = new();

    public PixelPoint? Position => GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;

    public bool PrimaryButtonDown => (GetAsyncKeyState(GetSystemMetrics(SwapButtons) != 0 ? RightButton : LeftButton) & 0x8000) != 0;

    private const int LeftButton = 0x01, RightButton = 0x02, SwapButtons = 23;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
