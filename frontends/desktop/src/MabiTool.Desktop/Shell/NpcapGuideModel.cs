using System.Diagnostics;
using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 缺少 Npcap 时的安装指引弹出层。Npcap 不随工具发行，由用户从官方下载页自行安装；
/// “打开下载页”只交给系统默认浏览器打开地址，程序不下载、不安装任何东西。
/// 自动开始的常驻抓包因 Npcap 缺失失败时同一次运行只弹一次（<see cref="Observe"/>）；用户手动开始时每次都弹（<see cref="Show"/>）。
/// </summary>
public sealed class NpcapGuideModel : ObservableObject
{
    public const string DownloadUrl = "https://npcap.com/#download";

    private bool isOpen;
    private bool shown;
    private string notice = string.Empty;

    public NpcapGuideModel()
    {
        OpenDownloadCommand = new RelayCommand(_ => OpenDownload());
        CopyAddressCommand = new RelayCommand(_ => _ = CopyAddressAsync());
        CloseCommand = new RelayCommand(_ => Close());
    }

    public ICommand OpenDownloadCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public ICommand CloseCommand { get; }

    public string Url => DownloadUrl;
    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }

    /// <summary>打开下载页或复制地址的结果；关闭时清空。</summary>
    public string Notice { get => notice; private set { if (Set(ref notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => notice.Length > 0;

    /// <summary>用系统默认浏览器打开地址；测试替换为记录。</summary>
    public Action<string> OpenUrl { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    /// <summary>写入剪贴板；由主窗口提供，没有时“复制地址”提示复制失败。</summary>
    public Func<string, Task>? CopyText { get; set; }

    /// <summary>用户手动开始抓包而 Npcap 缺失：每次都弹出。</summary>
    public void Show()
    {
        shown = true;
        Notice = string.Empty;
        IsOpen = true;
    }

    /// <summary>
    /// 随后端状态观察常驻抓包：未抓包的原因是 Npcap 缺失时本次运行只弹一次（已因手动操作弹过也算），
    /// 之后的重试失败只在抓包状态里写原因。其他原因与未知状态不弹。
    /// </summary>
    public void Observe(CaptureStatusView status)
    {
        if (shown || !status.NpcapMissingReason) return;
        Show();
    }

    public void Close()
    {
        IsOpen = false;
        Notice = string.Empty;
    }

    private void OpenDownload()
    {
        try
        {
            OpenUrl(DownloadUrl);
            Notice = UiText.Pick("已在默认浏览器中打开下载页", "已在預設瀏覽器中開啟下載頁");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            Notice = UiText.Pick("无法打开浏览器，请复制地址后在浏览器中打开", "無法開啟瀏覽器，請複製地址後在瀏覽器中開啟");
        }
    }

    private async Task CopyAddressAsync()
    {
        try
        {
            if (CopyText is null) throw new InvalidOperationException("clipboard.unavailable");
            await CopyText(DownloadUrl);
            Notice = UiText.Pick("已复制下载页地址", "已複製下載頁地址");
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Notice = UiText.Pick("复制失败，请手动输入地址", "複製失敗，請手動輸入地址");
        }
    }
}
