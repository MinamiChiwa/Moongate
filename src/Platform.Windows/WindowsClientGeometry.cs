using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Contracts;

namespace MabiTool.Platform.Windows;

/// <summary>窗口的 DPI 感知方式（<c>GetAwarenessFromDpiAwarenessContext</c> 的取值）。</summary>
public enum WindowDpiAwareness { Unaware = 0, System = 1, PerMonitor = 2 }

/// <summary>客户区的物理屏幕像素矩形与所在显示器的有效 DPI。</summary>
public readonly record struct PhysicalClientArea(PhysicalWindowRect Bounds, uint Dpi);

/// <summary>
/// 游戏窗口客户区的物理像素口径。未声明 DPI 感知或系统级感知的窗口由系统按显示器缩放虚拟化：
/// 在每显示器感知线程里直接 <c>GetClientRect</c> 得到的仍是窗口自己的逻辑尺寸，而 <c>ClientToScreen</c> 给出的原点已是物理坐标，两者口径不一。
/// 与染色引擎读取窗口的方式相同：进入目标窗口的 DPI 上下文取客户区，四角经 <c>ClientToScreen</c> 与
/// <c>LogicalToPhysicalPointForPerMonitorDPI</c> 换成物理像素；DPI 取窗口所在显示器的有效 DPI（虚拟化窗口的
/// <c>GetDpiForWindow</c> 是 96 或系统 DPI，不是显示器实际缩放）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsClientGeometry
{
    /// <summary>读取客户区物理矩形；窗口无效或系统换算失败时为空。最小化窗口返回尺寸为 0 的矩形。</summary>
    public static PhysicalClientArea? Read(nint window)
    {
        using var physical = WindowsDpiAwareness.Enter();
        if (!physical.Physical || !IsWindow(window)) return null;
        var context = GetWindowDpiAwarenessContext(window);
        if (context == 0) return null;
        var awareness = GetAwarenessFromDpiAwarenessContext(context);
        if (awareness is < 0 or > 2) return null;
        var windowDpi = GetDpiForWindow(window);
        var monitorDpi = MonitorDpi(window);
        if (windowDpi == 0 || monitorDpi == 0) return null;
        var previous = SetThreadDpiAwarenessContext(context);
        if (previous == 0) return null;
        Rect client; Point topLeft = default, bottomRight;
        try
        {
            if (!GetClientRect(window, out client) || client.Left != 0 || client.Top != 0) return null;
            bottomRight = new() { X = client.Right, Y = client.Bottom };
            if (!ClientToScreen(window, ref topLeft) || !LogicalToPhysicalPointForPerMonitorDPI(window, ref topLeft)) return null;
            if (client.Right > 0 && client.Bottom > 0 &&
                (!ClientToScreen(window, ref bottomRight) || !LogicalToPhysicalPointForPerMonitorDPI(window, ref bottomRight))) return null;
        }
        finally { SetThreadDpiAwarenessContext(previous); }
        return Resolve((WindowDpiAwareness)awareness, client.Right, client.Bottom, (topLeft.X, topLeft.Y), (bottomRight.X, bottomRight.Y), windowDpi, monitorDpi);
    }

    /// <summary>窗口是否被系统按显示器缩放虚拟化（窗口坐标系与物理像素不是一比一）。</summary>
    public static bool? IsVirtualized(nint window)
    {
        using var physical = WindowsDpiAwareness.Enter();
        if (!physical.Physical || !IsWindow(window)) return null;
        var context = GetWindowDpiAwarenessContext(window);
        if (context == 0) return null;
        var awareness = GetAwarenessFromDpiAwarenessContext(context);
        var windowDpi = GetDpiForWindow(window); var monitorDpi = MonitorDpi(window);
        if (awareness is < 0 or > 2 || windowDpi == 0 || monitorDpi == 0) return null;
        return (WindowDpiAwareness)awareness != WindowDpiAwareness.PerMonitor && windowDpi != monitorDpi;
    }

    /// <summary>
    /// 由目标窗口坐标系的客户区尺寸与换算后的物理四角得出物理客户区。四角给出的尺寸须与按 DPI 比例换算的尺寸一致
    /// （每显示器感知：与逻辑尺寸相同；未感知或系统级感知：逻辑尺寸 × 显示器 DPI ÷ 窗口 DPI，允许 1 像素舍入差），否则视为换算失败。
    /// </summary>
    public static PhysicalClientArea? Resolve(WindowDpiAwareness awareness, int logicalWidth, int logicalHeight,
        (int X, int Y) physicalTopLeft, (int X, int Y) physicalBottomRight, uint windowDpi, uint monitorDpi)
    {
        if (windowDpi == 0 || monitorDpi == 0 || logicalWidth < 0 || logicalHeight < 0) return null;
        if (logicalWidth == 0 || logicalHeight == 0)
            return new(new(physicalTopLeft.X, physicalTopLeft.Y, 0, 0), monitorDpi);
        long width = (long)physicalBottomRight.X - physicalTopLeft.X, height = (long)physicalBottomRight.Y - physicalTopLeft.Y;
        if (width is <= 0 or > 65536 || height is <= 0 or > 65536) return null;
        var scale = awareness == WindowDpiAwareness.PerMonitor ? (1L, 1L) : (monitorDpi, (long)windowDpi);
        long expectedWidth = (logicalWidth * scale.Item1 + scale.Item2 / 2) / scale.Item2, expectedHeight = (logicalHeight * scale.Item1 + scale.Item2 / 2) / scale.Item2;
        if (Math.Abs(width - expectedWidth) > 1 || Math.Abs(height - expectedHeight) > 1) return null;
        return new(new(physicalTopLeft.X, physicalTopLeft.Y, (int)width, (int)height), monitorDpi);
    }

    private static uint MonitorDpi(nint window)
    {
        var monitor = MonitorFromWindow(window, 2);
        return monitor != 0 && GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LogicalToPhysicalPointForPerMonitorDPI(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}
