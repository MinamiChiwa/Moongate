using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 让无边框主窗口保留 Windows 系统贴靠。
/// <para>
/// <c>WindowDecorations="None"</c> 时 Avalonia 不给窗口 <c>WS_THICKFRAME</c>，而拖到边缘半屏、拖到顶部最大化和
/// <c>Win+←/→</c> 都要求窗口带这个样式。这里通过样式回调补回 <c>WS_THICKFRAME</c>；Avalonia 对无装饰窗口的
/// <c>WM_NCCALCSIZE</c> 返回整窗为客户区，所以普通状态下不出现系统边框。最大化时系统把带 <c>WS_THICKFRAME</c>
/// 的窗口按边框厚度放大到工作区之外，钩子把客户区按同样厚度收回，内容恰好铺满工作区，不盖住任务栏也不被裁。
/// </para>
/// <para>
/// 最大化按钮标记为 <see cref="WindowDecorationsElementRole.MaximizeButton"/>，并开启
/// <see cref="Window.ExtendClientAreaToDecorationsHint"/>：Avalonia 据此对该按钮区域的 <c>WM_NCHITTEST</c> 返回
/// <c>HTMAXBUTTON</c>（Windows 11 悬停时显示贴靠布局），并把这块区域的非客户区鼠标消息转回按钮，悬停、按下、点击照常。
/// </para>
/// 只在真实的 Win32 窗口上生效；无界面测试和其他平台不做任何改动。
/// </summary>
public static class SnapFrame
{
    private const uint ThickFrame = 0x00040000;
    private const uint Caption = 0x00C00000;
    private const uint NcCalcSize = 0x0083;
    private const int BorderColorAttribute = 34;
    private const uint NoBorderColor = 0xFFFFFFFE;

    public static void Attach(Window window, Control maximizeButton)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND", Handle: var hwnd } || hwnd == IntPtr.Zero)
            return;
        Win32Properties.AddWindowStylesCallback(window, (style, exStyle) => (style | ThickFrame, exStyle));
        Win32Properties.AddWndProcHookCallback(window, Hook);
        // 圆角和外圈由界面自己画：系统圆角与 Windows 11 的 1 像素描边都关掉，透明边角保持原样。
        Win32Properties.SetWindowCornerPreference(window, Win32Properties.WindowCornerPreference.DoNotRound);
        var none = NoBorderColor;
        _ = DwmSetWindowAttribute(hwnd, BorderColorAttribute, ref none, sizeof(uint));
        WindowDecorationProperties.SetElementRole(maximizeButton, WindowDecorationsElementRole.MaximizeButton);
        window.ExtendClientAreaToDecorationsHint = true;
    }

    private static IntPtr Hook(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NcCalcSize || wParam == IntPtr.Zero || !IsZoomed(hwnd)) return IntPtr.Zero;
        var style = (uint)GetWindowLong(hwnd, -16);
        // 与 Avalonia 计算最大化位置时相同：去掉标题栏样式后的边框厚度，按窗口当前 DPI。
        var frame = new Rect32();
        if (!AdjustWindowRectExForDpi(ref frame, style & ~Caption, false, 0, GetDpiForWindow(hwnd))) return IntPtr.Zero;
        var client = Marshal.PtrToStructure<Rect32>(lParam);
        var inset = Inset(new PixelRect(client.Left, client.Top, client.Right - client.Left, client.Bottom - client.Top),
            new Thickness(-frame.Left, -frame.Top, frame.Right, frame.Bottom));
        Marshal.StructureToPtr(new Rect32 { Left = inset.X, Top = inset.Y, Right = inset.Right, Bottom = inset.Bottom }, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>把窗口矩形按四边的边框厚度（物理像素）向内收，得到最大化时的客户区。</summary>
    public static PixelRect Inset(PixelRect window, Thickness frame)
    {
        var left = window.X + (int)frame.Left;
        var top = window.Y + (int)frame.Top;
        var right = window.Right - (int)frame.Right;
        var bottom = window.Bottom - (int)frame.Bottom;
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRectExForDpi(ref Rect32 rect, uint style, bool menu, uint exStyle, uint dpi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
