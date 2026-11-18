using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MabiTool.Desktop.Overlay;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 寻色运行期间让盖在游戏客户区上的前端窗口不进入屏幕截图：宿主按屏幕区域截取游戏客户区，
/// 盖在游戏上的 Moongate 窗口会被一起截进去。只排除与游戏客户区（后端绑定的物理像素矩形）相交的窗口，
/// 其余窗口保持原样，基于屏幕采集的远程桌面与录屏里照常可见。
/// 每次 <see cref="Refresh"/> 重新判断：窗口移入游戏区域时设 WDA_EXCLUDEFROMCAPTURE，移出时恢复原设置；
/// 拿不到游戏客户区时不排除任何窗口（遮挡交给染色引擎检测与提示）。<see cref="Dispose"/> 恢复全部窗口，
/// 浮窗按版面的“录屏时隐藏”恢复。Windows 10 2004（19041）之前不设置。所有窗口操作在界面线程执行。
/// </summary>
internal sealed class CaptureExclusion : IDisposable
{
    private const uint ExcludeFromCapture = 0x11;
    private readonly Func<PixelRect?> gameBounds;
    private readonly Dictionary<IntPtr, (Window Window, uint Affinity)> original = new();
    private bool disposed;

    private CaptureExclusion(Func<PixelRect?> gameBounds) => this.gameBounds = gameBounds;

    public static bool Supported => OverlayWindow.CaptureExclusionSupported;

    /// <param name="gameBounds">游戏客户区的物理像素矩形；游戏未绑定或已最小化时为空。</param>
    public static CaptureExclusion Begin(Func<PixelRect?> gameBounds)
    {
        var exclusion = new CaptureExclusion(gameBounds);
        exclusion.Refresh();
        return exclusion;
    }

    public void Refresh()
    {
        if (!Supported) return;
        PixelRect? game;
        try { game = gameBounds(); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { game = null; }
        Dispatcher.UIThread.Post(() => Apply(game));
    }

    /// <summary>两个物理像素矩形是否有面积相交（相邻不算）；可跨显示器、可为负坐标。</summary>
    internal static bool Overlaps(PixelRect window, PixelRect game) =>
        window.Width > 0 && window.Height > 0 && game.Width > 0 && game.Height > 0 &&
        window.X < game.X + game.Width && game.X < window.X + window.Width &&
        window.Y < game.Y + game.Height && game.Y < window.Y + window.Height;

    private void Apply(PixelRect? game)
    {
        if (disposed || !OperatingSystem.IsWindows()) return;
        foreach (var (window, handle) in Windows())
        {
            var covers = game is { } area && CapturedRect(handle) is { } rect && Overlaps(rect, area);
            if (!covers)
            {
                if (original.Remove(handle, out var saved)) Restore(handle, saved.Window, saved.Affinity);
                continue;
            }
            if (!GetWindowDisplayAffinity(handle, out var affinity)) continue;
            // 窗口自己重设过属性（如浮窗换显示器后）时补回排除，恢复值仍取进入游戏区域时的设置。
            if (!original.ContainsKey(handle)) original[handle] = (window, affinity);
            if (affinity != ExcludeFromCapture) SetWindowDisplayAffinity(handle, ExcludeFromCapture);
        }
    }

    public void Dispose()
    {
        if (!Supported) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed) return;
            disposed = true;
            foreach (var (handle, (window, affinity)) in original) Restore(handle, window, affinity);
            original.Clear();
        });
    }

    private static void Restore(IntPtr handle, Window window, uint affinity)
    {
        if (!IsWindow(handle)) return;
        if (window is OverlayWindow overlay) overlay.RestoreCaptureSetting();
        else SetWindowDisplayAffinity(handle, affinity);
    }

    /// <summary>窗口在截图里占的物理像素矩形：有窗口区域（浮窗只画元件）时取区域外框，否则取窗口矩形；不可见时为空。</summary>
    private static PixelRect? CapturedRect(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || IsIconic(handle) || !GetWindowRect(handle, out var rect)) return null;
        var box = GetWindowRgnBox(handle, out var region) is SimpleRegion or ComplexRegion
            ? new Rect32(rect.Left + region.Left, rect.Top + region.Top, rect.Left + region.Right, rect.Top + region.Bottom)
            : rect;
        return new PixelRect(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top);
    }

    private static IEnumerable<(Window, IntPtr)> Windows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) yield break;
        foreach (var window in desktop.Windows.ToList())
            if (window.TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: var handle } && handle != IntPtr.Zero)
                yield return (window, handle);
    }

    private const int SimpleRegion = 2, ComplexRegion = 3;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect32(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgnBox(IntPtr window, out Rect32 rect);
}
