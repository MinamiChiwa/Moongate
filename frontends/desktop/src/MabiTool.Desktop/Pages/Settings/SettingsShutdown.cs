using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Settings;

// 设置页“后端”卡片的“退出 Moongate（含后台服务）”：关闭窗口时后端照常在后台运行（抓包、定时测量、浮窗数据），
// 这里是完全退出的入口。先在卡片里确认，后端受理后关闭本前端；下次打开 Moongate 时前端重新启动后端。
public sealed partial class SettingsPageModel
{
    private bool shutdownConfirming;
    private bool shutdownBusy;
    private string shutdownMessage = string.Empty;

    private void InitializeShutdown()
    {
        ShutdownCommand = new RelayCommand(_ =>
        {
            if (!CanShutdown) return;
            shutdownConfirming = true;
            shutdownMessage = string.Empty;
            RaiseShutdown();
        });
        CancelShutdownCommand = new RelayCommand(_ =>
        {
            shutdownConfirming = false;
            RaiseShutdown();
        });
        ConfirmShutdownCommand = new RelayCommand(_ => _ = ShutdownAsync());
    }

    public ICommand ShutdownCommand { get; private set; } = null!;
    public ICommand CancelShutdownCommand { get; private set; } = null!;
    public ICommand ConfirmShutdownCommand { get; private set; } = null!;

    /// <summary>已连接且没有退出请求在进行时可以退出。</summary>
    public bool CanShutdown => Data.Connected && !shutdownBusy;
    public bool ShutdownConfirming => shutdownConfirming && Data.Connected;

    /// <summary>按钮下方的说明：未连接时写明原因，失败时写明后端的回执。</summary>
    public string ShutdownNote => shutdownMessage.Length > 0 ? shutdownMessage
        : Data.Connected ? UiText.Pick("关闭窗口后后端仍在后台运行；要完全退出请按此按钮。", "關閉視窗後後端仍在背景執行；要完全結束請按此按鈕。")
        : UiText.Pick("未连接后端，无需退出后端。", "未連接後端，無需結束後端。");

    private async Task ShutdownAsync()
    {
        if (!CanShutdown) return;
        shutdownBusy = true;
        shutdownConfirming = false;
        RaiseShutdown();
        var reply = await Backend.ShutdownHostAsync(CancellationToken.None);
        shutdownBusy = false;
        shutdownMessage = reply.Ok ? UiText.Pick("后端正在退出，窗口即将关闭。", "後端正在結束，視窗即將關閉。") : reply.Message;
        RaiseShutdown();
        if (reply.Ok && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private void RaiseShutdown()
    {
        Raise(nameof(CanShutdown));
        Raise(nameof(ShutdownConfirming));
        Raise(nameof(ShutdownNote));
    }
}
