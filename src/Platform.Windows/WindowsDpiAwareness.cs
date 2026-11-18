using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MabiTool.Platform.Windows;

/// <summary>
/// 游戏窗口的位置与尺寸、截图区域、鼠标绝对坐标统一使用物理像素。
/// 没有声明 DPI 感知的进程拿到的是按显示器缩放换算后的逻辑坐标：游戏在 125%、150% 的显示器上时，
/// 绑定尺寸、截图与鼠标落点会和游戏实际像素错开；主副屏缩放不同时，逻辑坐标与物理位置不再按同一比例对应。
/// 宿主进程在启动最早处声明每显示器 DPI 感知 v2；窗口、输入与截图调用另在线程上进入同一上下文，
/// 进程声明失败（已由宿主外部设定）或在测试等其他进程里调用时坐标仍是物理像素。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDpiAwareness
{
    private static readonly nint PerMonitorAwareV2 = new(-4);

    /// <summary>声明进程为每显示器 DPI 感知 v2；进程已经处于该感知时同样返回真。</summary>
    public static bool EnablePerMonitorV2()
    {
        // 低于 Windows 10 1703 的系统没有这组函数；包根启动器已拦截，直接启动宿主时不在这里崩溃。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063)) return false;
        if (SetProcessDpiAwarenessContext(PerMonitorAwareV2)) return true;
        var current = GetThreadDpiAwarenessContext();
        return current != 0 && AreDpiAwarenessContextsEqual(current, PerMonitorAwareV2);
    }

    /// <summary>在当前线程进入物理像素上下文，释放时恢复原上下文；无法进入时 <see cref="Physical"/> 为假。</summary>
    public static Scope Enter() => new(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063) ? SetThreadDpiAwarenessContext(PerMonitorAwareV2) : 0);

    public readonly struct Scope(nint previous) : IDisposable
    {
        public bool Physical => previous != 0;
        public void Dispose() { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
