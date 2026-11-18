using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的全局抓包：设置改动即生效；关闭自动抓包时停止，检测立即给出合成结论（Detecting 为真时停在进行中）。
public sealed partial class SampleBackend
{
    /// <summary>测试用：全局抓包的捕获状态（running、stopped、faulted）与原因码。样本默认已常驻抓包。</summary>
    internal string CaptureState { get; set; } = "running";
    internal string? CaptureReason { get; set; }

    /// <summary>测试用：抓包识别到的本机角色名；为空表示抓包中但尚未识别（需切换地图）。</summary>
    internal string? CaptureCharacter { get; set; } = "琉特";

    /// <summary>测试用：宿主是否配置了游戏观测组件。</summary>
    internal bool CaptureInstalled { get; set; } = true;

    /// <summary>测试用：运行时报告的 Npcap 安装状态（installed、missing）；为空表示后端未报告。</summary>
    internal string? NpcapState { get; set; } = "installed";

    /// <summary>测试用：网卡检测停在进行中（第 6 秒）。</summary>
    internal bool Detecting { get; set; }

    /// <summary>保存过的抓包设置，按发送顺序记录。</summary>
    public List<GameCaptureSettings> SavedCaptureSettings { get; } = [];

    private GameCaptureSettings sampleCaptureSettings = GameCaptureSettings.Default with { Revision = new(1) };
    private IReadOnlyList<CaptureChannelOption>? listedCaptureChannels;
    private CaptureChannelDetection? sampleDetection;

    private static readonly CaptureChannelOption[] SampleChannels =
    [
        new("6f1c2a9e4b7d40a1a3c5e8f0b2d4c6a8", "Intel(R) Ethernet Connection (17) I219-V", ["192.168.1.10"], false, 1),
        new("0a2b4c6d8e0f42a4b6c8d0e2f4a6b8c0", "Npcap Loopback Adapter", [], true, 0),
        new("3c5e7a9b1d2f44c6a8e0b2d4f6a8c0e2", "WireGuard Tunnel", ["10.8.0.2"], false, 0)
    ];

    public CaptureStatusView GetCaptureStatus()
    {
        if (Offline) return CaptureStatusView.Offline;
        var running = CaptureState == "running";
        var reason = running ? null : CaptureReason ?? (sampleCaptureSettings.AutomaticCapture ? null : "observation.automaticCaptureDisabled");
        // 自动模式全部网卡同时抓，示例来源为物理网卡；指定模式显示保存的网卡。
        var manual = sampleCaptureSettings.ChannelMode == GameCaptureSettings.ManualChannel;
        return new(true, CaptureInstalled, sampleCaptureSettings.AutomaticCapture, CaptureState,
            running ? "running" : sampleCaptureSettings.AutomaticCapture ? "failed" : "off", reason,
            running && manual ? sampleCaptureSettings.ChannelDescription : null, running ? manual ? "saved" : "automatic" : null, null,
            running ? CaptureCharacter : null,
            // 示例位置：永久频道的地图名为游戏原文，频道号取客户端频道序号。
            running ? new(975984897, "杜巴頓", 975984897, null, true, 5, "location.channelDisplayUnverified") : null, NpcapState,
            SourceKind: running && !manual ? "physical" : null);
    }

    public CaptureSettingsView? GetCaptureSettings()
    {
        if (Offline) return null;
        var detection = Detecting ? new CaptureChannelDetection("sample", "detect-1", "running", new(3), 20, 6.2,
            SampleChannels.Select((channel, index) => new CaptureChannelDetectionEntry(channel.Id, channel.Description, true,
                new((ulong)(index == 2 ? 214 : index == 0 ? 3 : 0)), new((ulong)(index == 2 ? 281_000 : index == 0 ? 180 : 0)), null)).ToArray(),
            null, null, DateTimeOffset.UtcNow.AddSeconds(-6), null) : sampleDetection;
        return new(sampleCaptureSettings, "\"game-capture-settings-" + sampleCaptureSettings.Revision + "\"", GetCaptureStatus(), listedCaptureChannels, detection);
    }

    public Task<BackendReply> SaveCaptureSettingsAsync(GameCaptureSettings settings, string etag, CancellationToken cancellationToken)
    {
        if (Offline) return Task.FromResult(new BackendReply(false, Pick("未连接后端", "未連接後端")));
        SavedCaptureSettings.Add(settings);
        sampleCaptureSettings = settings with { Revision = new(settings.Revision.Value + 1) };
        if (!settings.AutomaticCapture) { CaptureState = "stopped"; CaptureReason = null; }
        else if (CaptureState != "running") { CaptureState = "running"; CaptureReason = null; }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, Pick("已保存抓包设置", "已儲存抓包設定")));
    }

    public Task<CaptureChannelsReply> ListCaptureChannelsAsync(CancellationToken cancellationToken)
    {
        if (Offline) return Task.FromResult(new CaptureChannelsReply(false, Pick("未连接后端", "未連接後端"), []));
        listedCaptureChannels = SampleChannels;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new CaptureChannelsReply(true, Pick($"已列出 {SampleChannels.Length} 个网卡", $"已列出 {SampleChannels.Length} 個網卡"), SampleChannels));
    }

    public Task<BackendReply> StartChannelDetectionAsync(CancellationToken cancellationToken)
    {
        // 合成结论：虚拟网卡收到游戏下行数据，以太网只有零星的包。
        sampleDetection = new("sample", "detect-1", "completed", new(3), 20, 14.5,
            SampleChannels.Select((channel, index) => new CaptureChannelDetectionEntry(channel.Id, channel.Description, true,
                new((ulong)(index == 2 ? 512 : index == 0 ? 4 : 0)), new((ulong)(index == 2 ? 690_000 : index == 0 ? 240 : 0)), null)).ToArray(),
            SampleChannels[2].Id, null, DateTimeOffset.UtcNow.AddSeconds(-15), DateTimeOffset.UtcNow);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, Pick("已开始检测网卡，请在游戏内持续移动角色", "已開始檢測網卡，請在遊戲內持續移動角色")));
    }

    public Task<BackendReply> CancelChannelDetectionAsync(CancellationToken cancellationToken)
    {
        Detecting = false;
        sampleDetection = sampleDetection is null ? null : sampleDetection with { State = "cancelled", WinnerChannelId = null };
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, Pick("已停止检测网卡", "已停止檢測網卡")));
    }
}
