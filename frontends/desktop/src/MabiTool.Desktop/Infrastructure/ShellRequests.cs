namespace MabiTool.Desktop.Infrastructure;

/// <summary>页面请求外壳切换页面：例如合奏页、DPS 检测页在未抓包时打开设置页的抓包区。由主窗口订阅。</summary>
public static class ShellRequests
{
    public static event Action? CaptureSettingsRequested;

    /// <summary>页面里手动开始抓包而 Npcap 缺失时请求弹出安装指引；由外壳模型订阅。</summary>
    public static event Action? NpcapGuideRequested;

    public static void OpenCaptureSettings() => CaptureSettingsRequested?.Invoke();

    public static void ShowNpcapGuide() => NpcapGuideRequested?.Invoke();
}
