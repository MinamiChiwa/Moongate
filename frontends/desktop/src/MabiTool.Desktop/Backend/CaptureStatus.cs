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
        if (!value.Connected) return Pick("未连接后端", "未連接後端");
        if (value.Installed is null) return Pick("正在读取抓包状态…", "正在讀取抓包狀態…");
        if (value.Installed == false) return Pick("抓包不可用：未安装观测组件", "抓包不可用：未安裝觀測元件");
        var channel = value.Channel is { Length: > 0 } name
            ? Pick("网卡：", "網卡：") + name + (value.ChannelSource switch
            {
                "automatic" => Pick("（自动）", "（自動）"),
                "saved" => Pick("（已保存）", "（已儲存）"),
                _ => string.Empty
            })
            : null;
        return value.State switch
        {
            "running" => Pick("抓包中", "抓包中") + (channel is null ? string.Empty : " · " + channel),
            "starting" => Pick("正在启动抓包…", "正在啟動抓包…") + (channel is null ? string.Empty : " · " + channel),
            "stopping" => Pick("正在停止抓包…", "正在停止抓包…"),
            _ => Pick("未抓包：", "未抓包：") + (Reason(value.Reason, value.Enabled) ?? Pick("等待自动启动", "等待自動啟動")) + Pick("，", "，")
                 + (value.Automation == "failed" && value.NextAttemptAt is { } next
                     ? Pick($"{next.ToLocalTime():HH:mm:ss} 重试", $"{next.ToLocalTime():HH:mm:ss} 重試") : string.Empty)
        };
    }

    /// <summary>
    /// 窗口左上角的全局抓包状态：未抓包写明原因；抓包中但未识别本机角色时提示切换地图；已识别时显示角色名（游戏原文）。
    /// </summary>
    public static string Shell(CaptureStatusView value) => value switch
    {
        { Running: true, Character: { Length: > 0 } name } => name,
        { Running: true } => Pick("未识别角色，请切换一次地图", "未辨識角色，請切換一次地圖"),
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
            var none = Pick("切换一次地图后显示", "切換一次地圖後顯示");
            return new(unknown, unknown, none, none);
        }
        var mapNote = location.MapReason switch
        {
            null => location.MapName ?? string.Empty,
            "location.mapCatalogPending" => Pick("正在读取地图名称", "正在讀取地圖名稱"),
            "location.mapCatalogUnavailable" => Pick("无法读取地图名称", "無法讀取地圖名稱"),
            "location.mapUnknown" => Pick("地图表里没有这个地图", "地圖表裡沒有這個地圖"),
            var other => Localization.CodeText.Reason(other)
        };
        var (channel, channelNote) = location switch
        {
            { PermanentChannelIndex: { } index } => (Pick($"频道 {index}", $"頻道 {index}"),
                Pick($"客户端频道序号 {index}", $"客戶端頻道序號 {index}")),
            { ChannelReason: "location.channelNotPermanent" } => (Pick("副本频道", "副本頻道"),
                Pick("非永久频道，没有固定频道号", "非永久頻道，沒有固定頻道號")),
            { ChannelReason: "location.channelRuleUnknown" } => (unknown, Pick("地图名称表未读取，无法判断频道", "地圖名稱表未讀取，無法判斷頻道")),
            { ChannelReason: "location.channelIndexUnresolved" } => (unknown, Pick("无法得出频道号", "無法得出頻道號")),
            { ChannelReason: { } other } => (unknown, Localization.CodeText.Reason(other)),
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
        "gameBuild.incompatible" => Pick("游戏版本未核验，暂不能抓包", "遊戲版本未核驗，暫不能抓包"),
        "observation.channelNotFound" => Pick("找不到游戏网卡，可在设置中指定", "找不到遊戲網卡，可在設定中指定"),
        "observation.channelAmbiguous" => Pick("多张网卡有游戏连接，请在设置中指定", "多張網卡有遊戲連線，請在設定中指定"),
        "observation.savedChannelMissing" => Pick("已保存的网卡不可用，请重新选择", "已儲存的網卡不可用，請重新選擇"),
        "observation.channelDetecting" => Pick("正在检测网卡，结束后自动启动", "正在檢測網卡，結束後自動啟動"),
        "update.maintenanceActive" => Pick("正在安装更新，结束后自动启动", "正在安裝更新，結束後自動啟動"),
        _ => RoomReadinessText.CaptureReason(code) ?? Localization.CodeText.Reason(code)
    };
}

/// <summary>侧栏“地图 · 频道”一行的文字与两项各自的悬停提示。</summary>
public sealed record LocationText(string Map, string Channel, string MapNote, string ChannelNote)
{
    public string Line => $"{Map} · {Channel}";
}
