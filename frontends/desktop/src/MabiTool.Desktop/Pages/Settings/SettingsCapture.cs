using System.Globalization;
using System.Windows.Input;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Settings;

/// <summary>抓包网卡列表的一项：描述、地址与该网卡承载的游戏连接数。</summary>
public sealed record CaptureChannelRow(string Id, string Description, string Label, bool Current);

/// <summary>网卡检测的一行：网卡、收到的游戏下行包数与字节，是否为判定结果。</summary>
public sealed record DetectionRow(string Name, string Traffic, bool Winner);

// 设置页“抓包”：自动抓包、网卡选择方式、手动调试检测与自动选择游戏窗口。改动即保存，修订由后端判定。
public sealed partial class SettingsPageModel
{
    private CaptureSettingsView? captureView;
    private bool captureLoaded;
    private bool captureSaving;
    private string captureMessage = string.Empty;
    private IReadOnlyList<CaptureChannelOption>? listedChannels;
    private bool channelsExpanded;
    private string detectionMessage = string.Empty;
    private bool detectionFailed;

    private void InitializeCapture()
    {
        CaptureCommand = new RelayCommand(value => SaveCapture(settings => settings with { AutomaticCapture = value as string == "on" },
            value as string == "on"));
        AutoWindowCommand = new RelayCommand(value => SaveCapture(settings => settings with { AutomaticWindowBinding = value as string == "on" }));
        ChannelModeCommand = new RelayCommand(value =>
        {
            if (value as string == "automatic") SaveCapture(settings => settings with { ChannelMode = GameCaptureSettings.AutomaticChannel });
            else if (CaptureSettings is { ChannelId: not null }) SaveCapture(settings => settings with { ChannelMode = GameCaptureSettings.ManualChannel });
            else _ = ListChannelsAsync();
        });
        // 列出网卡展开后再次点击收起，与其他折叠区一致。
        ListChannelsCommand = new RelayCommand(_ =>
        {
            if (channelsExpanded) { channelsExpanded = false; Raise(nameof(ChannelsExpanded)); Raise(nameof(HasCaptureChannels)); }
            else _ = ListChannelsAsync();
        });
        ChooseChannelCommand = new RelayCommand(value =>
        {
            if (value is CaptureChannelRow row) SaveCapture(settings => settings with
            { ChannelMode = GameCaptureSettings.ManualChannel, ChannelId = row.Id, ChannelDescription = row.Description });
        });
        StartDetectionCommand = new RelayCommand(_ => { if (!GuideIfNpcapMissing()) _ = RunAsync(Backend.StartChannelDetectionAsync); });
        NpcapGuideCommand = new RelayCommand(_ => ShellRequests.ShowNpcapGuide());
        CancelDetectionCommand = new RelayCommand(_ => _ = RunAsync(Backend.CancelChannelDetectionAsync));
        ConfirmDetectionCommand = new RelayCommand(_ =>
        {
            if (Detection is { State: "completed", WinnerChannelId: { } winner } detection && Capture is { } view && !captureSaving)
            {
                var name = detection.Channels.FirstOrDefault(entry => entry.ChannelId == winner)?.Description ?? winner;
                var next = view.Settings with { ChannelMode = GameCaptureSettings.ManualChannel, ChannelId = winner, ChannelDescription = name };
                _ = ConfirmDetectionAsync(next, view.ETag, name);
            }
        });
    }

    public ICommand CaptureCommand { get; private set; } = null!;
    public ICommand AutoWindowCommand { get; private set; } = null!;
    public ICommand ChannelModeCommand { get; private set; } = null!;
    public ICommand ListChannelsCommand { get; private set; } = null!;
    public ICommand ChooseChannelCommand { get; private set; } = null!;
    public ICommand StartDetectionCommand { get; private set; } = null!;
    public ICommand CancelDetectionCommand { get; private set; } = null!;
    public ICommand ConfirmDetectionCommand { get; private set; } = null!;
    public ICommand NpcapGuideCommand { get; private set; } = null!;

    /// <summary>未抓包的原因是 Npcap 缺失：状态旁显示“查看指引”。</summary>
    public bool CaptureNpcapMissing => (Capture?.Status ?? Backend.GetCaptureStatus()).NpcapMissingReason;

    private CaptureSettingsView? Capture
    {
        get
        {
            if (!captureLoaded) { captureView = Backend.GetCaptureSettings(); captureLoaded = true; }
            return captureView;
        }
    }
    private GameCaptureSettings? CaptureSettings => Capture?.Settings;
    private CaptureChannelDetection? Detection => Capture?.Detection;

    /// <summary>后端提供了抓包设置时各项可以操作；保存进行中停用。</summary>
    public bool HasCapture => Capture is not null;
    public bool CaptureUnavailable => Capture is null;
    public bool CanEditCapture => HasCapture && !captureSaving;
    public string CaptureStatus => CaptureText.Status(Capture?.Status ?? Backend.GetCaptureStatus());
    public bool CaptureRunning => Capture?.Status.Running == true;
    public bool CaptureOn => CaptureSettings?.AutomaticCapture == true;
    public bool CaptureOff => CaptureSettings is { AutomaticCapture: false };
    public bool AutoWindowOn => CaptureSettings?.AutomaticWindowBinding == true;
    public bool AutoWindowOff => CaptureSettings is { AutomaticWindowBinding: false };
    public bool ChannelAutomatic => CaptureSettings?.ChannelMode == GameCaptureSettings.AutomaticChannel;
    public bool ChannelManual => CaptureSettings?.ChannelMode == GameCaptureSettings.ManualChannel;
    public string SavedChannel => CaptureSettings?.ChannelDescription is { Length: > 0 } name
        ? UiText.Pick("指定网卡：", "指定網卡：") + name : string.Empty;
    public bool HasSavedChannel => ChannelManual && SavedChannel.Length > 0;
    public string CaptureMessage => captureMessage;
    public bool HasCaptureMessage => captureMessage.Length > 0;

    /// <summary>最近一次列出的网卡；带游戏连接数的网卡写明连接数，当前指定的网卡标为当前。</summary>
    public IReadOnlyList<CaptureChannelRow> CaptureChannels => (listedChannels ?? Capture?.Channels ?? []).Select(channel =>
    {
        var parts = new List<string> { channel.Description };
        if (channel.Addresses.Count > 0) parts.Add(string.Join("、", channel.Addresses));
        if (channel.Loopback == true) parts.Add(UiText.Pick("回环", "迴環"));
        if (channel.GameConnections is > 0 and var count) parts.Add(UiText.Pick($"游戏连接 {count} 条", $"遊戲連線 {count} 條"));
        return new CaptureChannelRow(channel.Id, channel.Description, string.Join(" · ", parts),
            ChannelManual && channel.Id == CaptureSettings?.ChannelId);
    }).ToList();
    public bool HasCaptureChannels => channelsExpanded && CaptureChannels.Count > 0;
    /// <summary>网卡列表已展开；按钮文字随之在“列出网卡”与“收起网卡列表”之间切换。</summary>
    public bool ChannelsExpanded => channelsExpanded;
    /// <summary>“使用此网卡”的结果：保存成功并切换到该网卡，或失败原因；显示在手动调试区内。</summary>
    public string DetectionMessage => detectionMessage;
    public bool HasDetectionMessage => detectionMessage.Length > 0;
    public bool DetectionMessageFailed => detectionFailed;

    public bool DetectionRunning => Detection?.State == "running";
    public bool CanStartDetection => CanEditCapture && !DetectionRunning;
    public bool HasDetection => Detection is { Id: not null };
    public bool CanConfirmDetection => CanEditCapture && Detection is { State: "completed", WinnerChannelId: not null };

    /// <summary>检测进度或结论：进行中写明已用时间；结束写明判定的网卡或原因。</summary>
    public string DetectionText => Detection switch
    {
        null or { Id: null } => string.Empty,
        { State: "running" } value => UiText.Pick($"正在检测 {Seconds(value.ElapsedSeconds)} / {value.DurationSeconds} 秒，请在游戏内持续移动角色",
            $"正在檢測 {Seconds(value.ElapsedSeconds)} / {value.DurationSeconds} 秒，請在遊戲內持續移動角色"),
        { State: "completed", WinnerChannelId: { } winner } value =>
            UiText.Pick("检测完成：", "檢測完成：") + (value.Channels.FirstOrDefault(entry => entry.ChannelId == winner)?.Description ?? winner)
            + UiText.Pick(" 承载游戏数据。确认后保存为指定网卡。", " 承載遊戲資料。確認後儲存為指定網卡。"),
        { State: "completed" } => UiText.Pick("检测完成，没有网卡收到足够的游戏数据。请在游戏内持续移动后重试，或从列表指定网卡。",
            "檢測完成，沒有網卡收到足夠的遊戲資料。請在遊戲內持續移動後重試，或從清單指定網卡。"),
        { State: "cancelled" } => UiText.Pick("检测已停止。", "檢測已停止。"),
        { Reason: var code } => UiText.Pick("检测未完成：", "檢測未完成：") + (CaptureText.Reason(code) ?? code ?? UiText.Pick("未知原因", "未知原因"))
    };

    public IReadOnlyList<DetectionRow> DetectionRows => (Detection?.Channels ?? []).Select(entry => new DetectionRow(entry.Description,
        entry.Opened ? UiText.Pick($"{entry.Packets.Value:N0} 个包 · {Bytes(entry.PayloadBytes.Value)}", $"{entry.Packets.Value:N0} 個封包 · {Bytes(entry.PayloadBytes.Value)}")
            : UiText.Pick("无法打开", "無法開啟"),
        entry.ChannelId == Detection?.WinnerChannelId)).ToList();

    private static string Seconds(double value) => Math.Floor(value).ToString(CultureInfo.InvariantCulture);
    private static string Bytes(ulong value) => value >= 1024 * 1024
        ? (value / 1024d / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
        : (value / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KiB";

    private void ResetCapture() => captureLoaded = false;

    // 改动即保存：基于读到的设置与修订发送一次；结果写在抓包区，失败时回到后端当前值。
    // 开启自动抓包属于用户手动开始抓包：保存后 Npcap 已知缺失时弹出安装指引。
    private void SaveCapture(Func<GameCaptureSettings, GameCaptureSettings> change, bool starts = false)
    {
        if (Capture is not { } view || captureSaving) return;
        var next = change(view.Settings);
        if (next == view.Settings) return;
        _ = RunAsync(token => Backend.SaveCaptureSettingsAsync(next, view.ETag, token), starts);
    }

    /// <summary>用户手动开始抓包（列出网卡、开始检测）前：后端确知 Npcap 未安装时弹出安装指引并写明原因，不发送请求。</summary>
    private bool GuideIfNpcapMissing()
    {
        if (captureSaving || !Backend.GetCaptureStatus().NpcapKnownMissing) return false;
        captureMessage = CaptureText.Reason(CaptureStatusView.NpcapMissingCode) ?? string.Empty;
        ShellRequests.ShowNpcapGuide();
        Reload();
        return true;
    }

    private async Task RunAsync(Func<CancellationToken, Task<BackendReply>> send, bool starts = false)
    {
        if (captureSaving) return;
        captureSaving = true;
        Raise(nameof(CanEditCapture));
        try
        {
            var reply = await send(CancellationToken.None);
            captureMessage = reply.Message;
            if (reply.Code == CaptureStatusView.NpcapMissingCode || reply.Ok && starts && Backend.GetCaptureStatus().NpcapKnownMissing)
                ShellRequests.ShowNpcapGuide();
        }
        finally
        {
            captureSaving = false;
            Reload();
        }
    }

    private async Task ListChannelsAsync()
    {
        if (captureSaving || GuideIfNpcapMissing()) return;
        captureSaving = true;
        Raise(nameof(CanEditCapture));
        try
        {
            var reply = await Backend.ListCaptureChannelsAsync(CancellationToken.None);
            if (reply.Ok) { listedChannels = reply.Channels; channelsExpanded = true; }
            captureMessage = reply.Message;
            if (reply.Code == CaptureStatusView.NpcapMissingCode) ShellRequests.ShowNpcapGuide();
        }
        finally
        {
            captureSaving = false;
            Reload();
        }
    }

    // 保存检测到的网卡为指定网卡；结果写在手动调试区，并立即刷新抓包状态。
    private async Task ConfirmDetectionAsync(GameCaptureSettings next, string etag, string name)
    {
        captureSaving = true;
        Raise(nameof(CanEditCapture));
        try
        {
            var reply = await Backend.SaveCaptureSettingsAsync(next, etag, CancellationToken.None);
            detectionFailed = !reply.Ok;
            detectionMessage = reply.Ok
                ? UiText.Pick($"已改用网卡：{name}。抓包将切换到该网卡，状态见上方。", $"已改用網卡：{name}。抓包將切換到該網卡，狀態見上方。")
                : UiText.Pick("未能改用该网卡：", "未能改用該網卡：") + reply.Message;
        }
        finally
        {
            captureSaving = false;
            Reload();
        }
    }
}
