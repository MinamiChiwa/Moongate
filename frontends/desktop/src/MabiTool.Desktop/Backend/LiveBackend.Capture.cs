using MabiTool.Client;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 全局抓包：观测快照在 game.observation.changed 事件后读取，运行时、观测快照与设置另每五秒读取一次；网卡检测进行中时每秒读取进度。
// 抓包由后端在绑定游戏后常驻运行，前端只显示状态并提交设置；合奏在房间内时登记音乐需求，离开房间时释放。
public sealed partial class LiveBackend
{
    private GameObservationRuntime? captureRuntime;
    private GameObservationSnapshot? captureSnapshot;
    private GameCaptureSettings? captureSettings;
    private string captureSettingsETag = string.Empty;
    private CaptureChannelDetection? channelDetection;
    private IReadOnlyList<CaptureChannelOption>? settingsChannels;
    private string? captureSignature;
    private DateTimeOffset musicDemandRetryAt;

    private long lastCaptureFullRead;

    private async Task RefreshCaptureAsync(MabiToolClient api, CancellationToken token)
    {
        bool full;
        CaptureChannelDetection? detection;
        lock (gate)
        {
            detection = channelDetection;
            var interval = detection?.State == "running" ? StatusInterval : SlowInterval;
            if ((due & Due.Capture) == 0 && !Elapsed(lastCaptureRead, interval)) return;
            due &= ~Due.Capture;
            lastCaptureRead = Now;
            full = Elapsed(lastCaptureFullRead, SlowInterval);
            if (full) lastCaptureFullRead = lastCaptureRead;
        }
        GameObservationRuntime? runtime = null;
        GameCaptureSettings? settings = null;
        string? tag = null;
        if (full) runtime = await Try(() => api.GetGameObservationRuntimeAsync(token));
        var snapshot = await Try(() => api.GetGameObservationAsync(token));
        if (full) settings = await Try(() => api.GetGameCaptureSettingsAsync(token, metadata => tag = metadata.ETag));
        if (detection?.State == "running") detection = await Try(() => api.GetChannelDetectionAsync(token)) ?? detection;
        lock (gate)
        {
            runtime ??= captureRuntime;
            if (!full) (settings, tag) = (captureSettings, captureSettingsETag);
        }
        var automation = snapshot?.Automation;
        var next = string.Join('|', runtime?.Installed, runtime?.Npcap, snapshot?.BackendEpoch, snapshot?.Revision, snapshot?.State, snapshot?.Reason,
            automation?.State, automation?.Reason, automation?.ChannelDescription, settings?.Revision, tag,
            detection?.Id, detection?.State, detection?.ElapsedSeconds, detection?.WinnerChannelId,
            snapshot?.Latest?.Value.Identity?.Nickname, snapshot?.Latest?.Quality.KnownValue == ObservationQuality.Stale,
            CaptureLocation(snapshot));
        bool changed;
        lock (gate)
        {
            captureRuntime = runtime ?? captureRuntime;
            captureSnapshot = snapshot;
            if (settings is not null) (captureSettings, captureSettingsETag) = (settings, tag ?? string.Empty);
            channelDetection = detection;
            changed = next != captureSignature;
            captureSignature = next;
        }
        if (changed)
        {
            // 抓包状态变了，自动观测的状态（等待抓包、等待识别）通常随之变化。
            MarkDue(Due.Auto, immediate: false);
            Notify();
        }
    }

    /// <summary>
    /// 合奏的音乐需求：在房间内且共享捕获运行时登记（不指定网卡与配置，由后端按当前捕获加入）；不在房间时释放。
    /// 登记失败 10 秒后再试，不阻塞页面刷新。
    /// </summary>
    private async Task SyncMusicDemandAsync(MabiToolClient api, RoomLifecycleSnapshot? session, CancellationToken token)
    {
        string? demand; GameObservationSnapshot? snapshot; GameSession? binding;
        lock (gate) { demand = captureDemandId; snapshot = captureSnapshot; binding = state.Binding?.Binding?.Session; }
        var inRoom = session is { State: not "disconnected" };
        try
        {
            if (!inRoom && demand is not null)
            {
                await api.ReleaseGameObservationAsync(demand, token);
                lock (gate) captureDemandId = null;
            }
            else if (inRoom && demand is null && snapshot?.State == "running" && binding is not null && DateTimeOffset.UtcNow >= musicDemandRetryAt)
            {
                string? tag = null;
                await api.GetGameObservationAsync(token, metadata => tag = metadata.ETag);
                var created = await api.StartGameObservationAsync(new(null, binding.BindingRevision), IdempotencyKey(), tag ?? string.Empty, token);
                lock (gate) { captureDemandId = created.Id; captureSnapshot = created.Snapshot; }
            }
        }
        catch (MabiToolApiException)
        {
            lock (gate) { musicDemandRetryAt = DateTimeOffset.UtcNow.AddSeconds(10); if (!inRoom) captureDemandId = null; }
        }
    }

    /// <summary>共享抓包运行中且观测未过期时的当前地图与频道。</summary>
    private static ObservedLocation? CaptureLocation(GameObservationSnapshot? snapshot) =>
        snapshot is { State: "running", Latest: { Quality.KnownValue: not ObservationQuality.Stale } latest } ? latest.Value.Location : null;

    public CaptureStatusView GetCaptureStatus()
    {
        var connected = Read().State.Connected;
        lock (gate) return connected ? CaptureStatusView.From(true, captureRuntime, captureSnapshot) : CaptureStatusView.Offline;
    }

    public CaptureSettingsView? GetCaptureSettings()
    {
        var status = GetCaptureStatus();
        lock (gate)
            return status.Connected && captureSettings is { } settings
                ? new(settings, captureSettingsETag, status, settingsChannels, channelDetection) : null;
    }

    public Task<BackendReply> SaveCaptureSettingsAsync(GameCaptureSettings settings, string etag, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            string? tag = null;
            var saved = await api.SaveGameCaptureSettingsAsync(settings, IdempotencyKey(), etag, token, metadata => tag = metadata.ETag);
            lock (gate) (captureSettings, captureSettingsETag) = (saved, tag ?? captureSettingsETag);
            Notify();
            return Pick("已保存抓包设置", "已儲存抓包設定");
        }, cancellationToken);

    public async Task<CaptureChannelsReply> ListCaptureChannelsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CaptureChannelOption> channels = [];
        var reply = await ReplyAsync(async (api, token) =>
        {
            channels = (await api.QueryObservationChannelsAsync(token)).Items;
            lock (gate) settingsChannels = channels;
            Notify();
            return channels.Count == 0 ? Pick("没有找到可用网卡，请确认已安装 Npcap。", "沒有找到可用網卡，請確認已安裝 Npcap。")
                : Pick($"已列出 {channels.Count} 个网卡", $"已列出 {channels.Count} 個網卡") + CaptureText.Located(channels);
        }, cancellationToken, remember: false);
        return new(reply.Ok, reply.Message, channels, reply.Code);
    }

    public Task<BackendReply> StartChannelDetectionAsync(CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            var binding = Read().State.Binding?.Binding?.Session
                ?? throw new InvalidOperationException(Pick("请先选择游戏窗口", "請先選擇遊戲視窗"));
            var started = await api.StartChannelDetectionAsync(new(binding.BindingRevision, 20), IdempotencyKey(), token);
            lock (gate) channelDetection = started;
            Notify();
            return Pick("已开始检测网卡，请在游戏内持续移动角色", "已開始檢測網卡，請在遊戲內持續移動角色");
        }, cancellationToken);

    public Task<BackendReply> CancelChannelDetectionAsync(CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            var cancelled = await api.CancelChannelDetectionAsync(token);
            lock (gate) channelDetection = cancelled;
            Notify();
            return Pick("已停止检测网卡", "已停止檢測網卡");
        }, cancellationToken);
}
