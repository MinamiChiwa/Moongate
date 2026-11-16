using MabiTool.Contracts;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Backend;

/// <summary>
/// 全局抓包的当前状态：后端常驻共享捕获的运行状态与自动启动的结果。
/// <see cref="Installed"/> 为空表示尚未读取；<see cref="Enabled"/> 是设置里的“自动抓包”；
/// <see cref="State"/> 是捕获状态（running、starting、stopping、stopped、faulted），<see cref="Automation"/> 是自动启动状态
/// （off、waiting、starting、running、failed），<see cref="Reason"/> 是未抓包的原因码。
/// <see cref="Character"/> 是抓包识别到的本机角色名（游戏原文，未过期的观测），未识别时为空。
/// <see cref="Location"/> 是抓包运行中且观测未过期时的当前地图与频道，其余时候为空。
/// <see cref="Npcap"/> 是后端运行时报告的 Npcap 安装状态（installed、missing），未读取或后端不报告时为空，按未知处理。
/// </summary>
public sealed record CaptureStatusView(bool Connected, bool? Installed, bool Enabled, string State, string? Automation, string? Reason,
    string? Channel, string? ChannelSource, DateTimeOffset? NextAttemptAt, string? Character = null, ObservedLocation? Location = null,
    string? Npcap = null)
{
    public const string NpcapMissingCode = "observation.npcapMissing";
    public static CaptureStatusView Offline { get; } = new(false, null, true, "stopped", null, null, null, null, null);
    public bool Running => State == "running";

    /// <summary>未抓包的原因是 Npcap 缺失：显示带“查看指引”入口的原因，自动开始失败时弹出一次安装指引。</summary>
    public bool NpcapMissingReason => Connected && Reason == NpcapMissingCode;

    /// <summary>后端确知 Npcap 未安装；未读取（为空）不算缺失。用户手动开始抓包前据此弹出安装指引。</summary>
    public bool NpcapKnownMissing => Connected && Npcap == "missing";

    public static CaptureStatusView From(bool connected, GameObservationRuntime? runtime, GameObservationSnapshot? snapshot)
    {
        var automation = snapshot?.Automation;
        // 自动启动失败时捕获已停止，原因取自动启动的结果；捕获故障时取捕获原因。
        var reason = snapshot?.State is "running" or "starting" ? null
            : automation?.Reason ?? snapshot?.Reason;
        var fresh = snapshot is { State: "running", Latest: { Quality.KnownValue: not ObservationQuality.Stale } latest } ? latest : null;
        return new(connected, runtime?.Installed, automation?.Enabled ?? true, snapshot?.State ?? "stopped", automation?.State, reason,
            automation?.ChannelDescription ?? snapshot?.ChannelId, automation?.ChannelSource, automation?.NextAttemptAt,
            fresh?.Value.Identity?.Nickname, fresh?.Value.Location, runtime?.Npcap);
    }
}

/// <summary>抓包状态与原因的界面文字；设置页、合奏页与 DPS 检测页共用。</summary>
public static class CaptureText
{
    private static string Pick(string simplified, string traditional) => UiText.Pick(simplified, traditional);

    /// <summary>一行状态：抓包中写明网卡及其来源；未抓包写明原因。</summary>
    public static string Status(CaptureStatusView value)
    {
        if (!value.Connected) return Pick("未连接后端，抓包状态未知。", "未連接後端，抓包狀態未知。");
        if (value.Installed is null) return Pick("正在读取游戏观测组件状态…", "正在讀取遊戲觀測元件狀態…");
        if (value.Installed == false) return Pick("抓包不可用：宿主未安装或未配置游戏观测组件。", "抓包不可用：宿主未安裝或未設定遊戲觀測元件。");
        var channel = value.Channel is { Length: > 0 } name
            ? Pick("网卡：", "網卡：") + name + (value.ChannelSource switch
            {
                "automatic" => Pick("（按游戏连接自动选择）", "（依遊戲連線自動選擇）"),
                "saved" => Pick("（已保存的网卡）", "（已儲存的網卡）"),
                _ => string.Empty
            })
            : null;
        return value.State switch
        {
            "running" => Pick("抓包中", "抓包中") + (channel is null ? string.Empty : " · " + channel),
            "starting" => Pick("正在启动抓包…", "正在啟動抓包…") + (channel is null ? string.Empty : " · " + channel),
            "stopping" => Pick("正在停止抓包…", "正在停止抓包…"),
            _ => Pick("未抓包：", "未抓包：") + (Reason(value.Reason, value.Enabled) ?? Pick("等待自动启动", "等待自動啟動")) + Pick("。", "。")
                 + (value.Automation == "failed" && value.NextAttemptAt is { } next
                     ? Pick($"将于 {next.ToLocalTime():HH:mm:ss} 重试。", $"將於 {next.ToLocalTime():HH:mm:ss} 重試。") : string.Empty)
        };
    }

    /// <summary>
    /// 窗口左上角的全局抓包状态：未抓包写明原因；抓包中但未识别本机角色时提示切换地图；已识别时显示角色名（游戏原文）。
    /// </summary>
    public static string Shell(CaptureStatusView value) => value switch
    {
        { Running: true, Character: { Length: > 0 } name } => name,
        { Running: true } => Pick("抓包中 · 未识别角色，请切换一次地图", "抓包中 · 未辨識角色，請切換一次地圖"),
        { State: "starting" } => Pick("正在启动抓包…", "正在啟動抓包…"),
        _ => Status(value)
    };

    /// <summary>
    /// 侧栏的地图与频道：地图为位置投影的地图名原文；频道以客户端频道序号作为频道号（“频道 N”；游戏界面不显示频道号），
    /// 非永久频道为“副本频道”；没有值时为“未知”，提示写明原因。抓包未运行或观测过期时两项都为“未知”。
    /// </summary>
    public static LocationText Place(ObservedLocation? location)
    {
        var unknown = Pick("未知", "未知");
        if (location is null)
        {
            var none = Pick("抓包尚未收到进图通知（切换一次地图后显示）", "抓包尚未收到進圖通知（切換一次地圖後顯示）");
            return new(unknown, unknown, none, none);
        }
        var mapNote = location.MapReason switch
        {
            null => location.MapName ?? string.Empty,
            "location.mapCatalogPending" => Pick("正在读取客户端地图名称", "正在讀取客戶端地圖名稱"),
            "location.mapCatalogUnavailable" => Pick("无法读取客户端地图名称资源", "無法讀取客戶端地圖名稱資源"),
            "location.mapUnknown" => Pick($"客户端地图表里没有这个地图（{location.ChannelSheetId}）", $"客戶端地圖表裡沒有這個地圖（{location.ChannelSheetId}）"),
            var other => other
        };
        var (channel, channelNote) = location switch
        {
            { PermanentChannelIndex: { } index } => (Pick($"频道 {index}", $"頻道 {index}"),
                Pick($"客户端频道序号 {index}，按此作为频道号显示", $"客戶端頻道序號 {index}，按此作為頻道號顯示")),
            { ChannelReason: "location.channelNotPermanent" } => (Pick("副本频道", "副本頻道"),
                Pick("此地图为非永久频道（如地下城副本），没有固定频道号", "此地圖為非永久頻道（如地下城副本），沒有固定頻道號")),
            { ChannelReason: "location.channelRuleUnknown" } => (unknown, Pick("地图名称表未读取，无法判断频道", "地圖名稱表未讀取，無法判斷頻道")),
            { ChannelReason: "location.channelIndexUnresolved" } => (unknown, Pick("实例编号不符合永久频道规则，无法得出频道", "實例編號不符合永久頻道規則，無法得出頻道")),
            { ChannelReason: { } other } => (unknown, other),
            _ => (unknown, Pick("没有频道信息", "沒有頻道資訊"))
        };
        return new(location.MapName ?? unknown, channel, mapNote, channelNote);
    }

    /// <summary>未抓包的原因：自动启动的等待与失败原因，以及共享捕获的故障码。</summary>
    public static string? Reason(string? code, bool enabled = true) => code switch
    {
        null when !enabled => Pick("自动抓包已关闭", "自動抓包已關閉"),
        null => null,
        "observation.automaticCaptureDisabled" => Pick("自动抓包已关闭，可在设置中开启", "自動抓包已關閉，可在設定中開啟"),
        "input.bindingUnavailable" => Pick("尚未绑定游戏窗口", "尚未綁定遊戲視窗"),
        "gameBuild.incompatible" => Pick("当前游戏版本尚未核验，抓包配置不可用", "目前遊戲版本尚未核驗，抓包設定不可用"),
        "observation.channelNotFound" => Pick("没有网卡承载游戏连接，可在设置中用手动调试找出网卡", "沒有網卡承載遊戲連線，可在設定中用手動調試找出網卡"),
        "observation.channelAmbiguous" => Pick("多张网卡都有游戏连接，请在设置中指定网卡", "多張網卡都有遊戲連線，請在設定中指定網卡"),
        "observation.savedChannelMissing" => Pick("已保存的网卡当前不可用，请在设置中重新选择", "已儲存的網卡目前不可用，請在設定中重新選擇"),
        "observation.channelDetecting" => Pick("正在检测网卡，结束后自动启动", "正在檢測網卡，結束後自動啟動"),
        "update.maintenanceActive" => Pick("正在安装更新，结束后自动启动", "正在安裝更新，結束後自動啟動"),
        _ => RoomReadinessText.CaptureReason(code) ?? code
    };
}

/// <summary>侧栏“地图 · 频道”一行的文字与两项各自的悬停提示。</summary>
public sealed record LocationText(string Map, string Channel, string MapNote, string ChannelNote)
{
    public string Line => $"{Map} · {Channel}";
}
