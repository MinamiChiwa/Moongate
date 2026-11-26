namespace MabiTool.Desktop.Infrastructure;

/// <summary>页面请求外壳切换页面：例如合奏页、DPS 检测页在未抓包时打开设置页的抓包区。由主窗口订阅。</summary>
public static class ShellRequests
{
    public static event Action? CaptureSettingsRequested;

    /// <summary>页面里手动开始抓包而 Npcap 缺失时请求弹出安装指引；由外壳模型订阅。</summary>
    public static event Action? NpcapGuideRequested;

    /// <summary>
    /// 设置页的游戏连接器卡片首次启用时请求弹出风险确认窗口；窗口只在主窗口里有一个，由外壳模型订阅并显示。
    /// 参数是用户确认后由请求方执行的动作（发送启用请求），取消时不执行。
    /// </summary>
    public static event Action<Action>? ConnectorRiskRequested;
    /// <summary>设置页点“下载更新”：请求外壳弹出确认窗口（版本、下载方式与大小、更新说明），确认后才开始下载；由外壳模型订阅。</summary>
    public static event Action? UpdateDownloadRequested;

    public static void OpenCaptureSettings() => CaptureSettingsRequested?.Invoke();

    public static void ShowNpcapGuide() => NpcapGuideRequested?.Invoke();

    public static void ShowConnectorRisk(Action confirmed) => ConnectorRiskRequested?.Invoke(confirmed);
    public static void RequestUpdateDownload() => UpdateDownloadRequested?.Invoke();
}
