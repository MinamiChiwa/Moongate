using Avalonia.Controls;
using MabiTool.Desktop.Backend;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 迷你窗的打开与关闭。整个应用只有一个迷你窗，与主窗口同时存在、读同一个后端；
/// 主窗口关闭时迷你窗一并关闭，迷你窗里可以回到主窗口。
/// </summary>
public sealed class MiniWindowController(IDesktopBackend backend)
{
    private MiniWindow? window;

    /// <summary>主窗口；迷你窗的“主窗口”按钮把它还原并带到前面。</summary>
    public Window? Main { get; set; }

    public MiniWindow? Window => window;
    public bool IsShown => window is not null;

    public void Show()
    {
        if (window is not null)
        {
            window.Activate();
            return;
        }
        window = new MiniWindow { DataContext = new MiniWindowModel(backend, ShowMain) };
        // 迷你窗打开期间页面状态（乐谱、活动）照常读取，主窗口最小化也不停。
        backend.SetSurfaceVisible("mini", true);
        window.Closed += (_, _) =>
        {
            backend.SetSurfaceVisible("mini", false);
            (window?.DataContext as IDisposable)?.Dispose();
            window = null;
        };
        window.Show();
    }

    public void Hide() => window?.Close();

    private void ShowMain()
    {
        if (Main is not { } main) return;
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }
}
