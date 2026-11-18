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
/// <see cref="Build"/> 是绑定客户端的构建核对（已登记、已自动适配及单项停用），后端不报告时为空。
/// <see cref="Hint"/> 是抓包运行中后端给出的提示原因：<c>observation.noGameFrames</c>（约 20 秒收不到游戏数据）或
/// <c>observation.identityPending</c>（能分帧但还没有本机角色信息），其余时候为空。
/// <see cref="SourceKind"/> 是自动模式下实际出数据的主来源类别（physical、wintun、tap、hyperv、virtual、loopback），没有时为空。
/// </summary>
public sealed record CaptureStatusView(bool Connected, bool? Installed, bool Enabled, string State, string? Automation, string? Reason,
    string? Channel, string? ChannelSource, DateTimeOffset? NextAttemptAt, string? Character = null, ObservedLocation? Location = null,
    string? Npcap = null, GameBuildCheck? Build = null, string? Hint = null, string? SourceKind = null)
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
            fresh?.Value.Identity?.Nickname, fresh?.Value.Location, runtime?.Npcap, automation?.Build,
            snapshot?.State == "running" && automation?.State == "running" ? automation.Reason : null,
            snapshot?.State == "running" ? automation?.SourceKind : null);
    }
}

/// <summary>抓包状态与原因的界面文字；设置页、合奏页与 DPS 检测页共用。</summary>
public static class CaptureText
{
    private static string Pick(string simplified, string traditional) => UiText.Pick(simplified, traditional);

    /// <summary>一行状态：自动模式写明实际出数据的来源类别，指定模式写明网卡；未抓包写明原因。</summary>
    public static string Status(CaptureStatusView value)
    {
        if (!value.Connected) return Pick("未连接后端", "未連接後端");
        if (value.Installed is null) return Pick("正在读取抓包状态…", "正在讀取抓包狀態…");
        if (value.Installed == false) return Pick("抓包不可用：未安装观测组件", "抓包不可用：未安裝觀測元件");
        var channel = value.ChannelSource is "saved" or "request" && value.Channel is { Length: > 0 } name
            ? Pick("网卡：", "網卡：") + name + (value.ChannelSource == "saved" ? Pick("（已指定）", "（已指定）") : string.Empty)
            : Source(value.SourceKind) is { } source ? Pick("来源：", "來源：") + source : null;
        var build = Build(value.Build) is { } note ? " · " + note : string.Empty;
        return value.State switch
        {
            "running" => Pick("抓包中", "抓包中") + (channel is null ? string.Empty : " · " + channel) + build
                         + (value.Character is null && Waiting(value) is { } waiting ? " · " + waiting : string.Empty),
            "starting" => Pick("正在启动抓包…", "正在啟動抓包…") + (channel is null ? string.Empty : " · " + channel) + build,
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
        { Running: true } => Waiting(value) ?? Pick("未识别角色，请切换一次地图", "未辨識角色，請切換一次地圖"),
        { State: "starting" } => Pick("正在启动抓包…", "正在啟動抓包…"),
        _ => Status(value)
    };

    /// <summary>
    /// 抓包运行中还没有数据时的统一提示（侧栏、设置页、DPS 与背包页共用）：约 20 秒收不到游戏数据时写明，
    /// 能分帧但没有本机角色信息时请用户切换一次地图；其余时候为空。
    /// </summary>
    public static string? Waiting(CaptureStatusView value) => value is { Running: true, Character: null } ? value.Hint switch
    {
        "observation.noGameFrames" => Pick("收不到游戏数据", "收不到遊戲資料"),
        "observation.identityPending" => Pick("等待角色信息，请切换一次地图", "等待角色資訊，請切換一次地圖"),
        _ => null
    } : null;

    /// <summary>收不到游戏数据时的下一步（设置页状态下方）；其余时候为空。</summary>
    public static string? Advice(CaptureStatusView value) => value is { Running: true, Character: null, Hint: "observation.noGameFrames" }
        ? Pick("请确认 Npcap 已勾选回环支持，或在设置中指定网卡", "請確認 Npcap 已勾選迴環支援，或在設定中指定網卡") : null;

    /// <summary>自动模式下数据来源类别的简短名称；没有来源或未知类别时为空。</summary>
    public static string? Source(string? kind) => kind switch
    {
        "physical" => Pick("物理网卡", "實體網卡"),
        "wintun" or "tap" => Pick("隧道（加速器）", "隧道（加速器）"),
        "hyperv" or "virtual" => Pick("虚拟网卡", "虛擬網卡"),
        "loopback" => Pick("回环（加速器）", "迴環（加速器）"),
        _ => null
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

    /// <summary>
    /// 构建核对的一句说明：未登记构建已自动适配时写明；有能力因核对不过停用时写明停用的能力。已登记构建与未报告时为空。
    /// </summary>
    public static string? Build(GameBuildCheck? check)
    {
        if (check is not { State: "adapted" }) return null;
        if (check.Disabled.Count == 0) return Pick("已适配新版游戏", "已適配新版遊戲");
        var names = check.Disabled.Select(item => item.Capability switch
        {
            "identity" => Pick("角色识别", "角色辨識"),
            "music" => Pick("合奏", "合奏"),
            "combat" => Pick("战斗统计", "戰鬥統計"),
            var other => other
        });
        return Pick("新版游戏：", "新版遊戲：") + string.Join(Pick("、", "、"), names) + Pick("暂停用", "暫停用");
    }

    /// <summary>
    /// 列出网卡后的参考信息（带游戏绑定时）：恰有一张网卡承载游戏连接时写明该网卡；自动模式同时抓全部网卡，其余情况不给结论。
    /// </summary>
    public static string Located(IReadOnlyList<CaptureChannelOption> channels)
    {
        var matched = channels.Where(channel => channel.GameConnections > 0).ToArray();
        return matched.Length == 1 ? Pick("，游戏连接在：", "，遊戲連線在：") + matched[0].Description : string.Empty;
    }

    /// <summary>未抓包的原因：自动启动的等待与失败原因，以及共享捕获的故障码。</summary>
    public static string? Reason(string? code, bool enabled = true) => code switch
    {
        null when !enabled => Pick("自动抓包已关闭", "自動抓包已關閉"),
        null => null,
        "observation.automaticCaptureDisabled" => Pick("自动抓包已关闭，可在设置中开启", "自動抓包已關閉，可在設定中開啟"),
        "input.bindingUnavailable" => Pick("尚未绑定游戏窗口", "尚未綁定遊戲視窗"),
        "gameBuild.incompatible" => Pick("未识别为台服客户端，暂不能抓包", "未辨識為台服用戶端，暫不能抓包"),
        "gameBuild.verifying" => Pick("正在核对新版游戏，稍后自动开始", "正在核對新版遊戲，稍後自動開始"),
        "gameBuild.verifyFailed" => Pick("读不到游戏文件，请重启游戏后重试", "讀不到遊戲檔案，請重啟遊戲後重試"),
        "observation.channelChanged" => Pick("网卡设置已更改，正在按新设置重新开始", "網卡設定已變更，正在按新設定重新開始"),
        "observation.savedChannelMissing" => Pick("已保存的网卡不可用，请重新选择", "已儲存的網卡不可用，請重新選擇"),
        "observation.channelDetecting" => Pick("正在检测网卡，结束后自动启动", "正在檢測網卡，結束後自動啟動"),
        "update.maintenanceActive" => Pick("正在安装更新，结束后自动启动", "正在安裝更新，結束後自動啟動"),
        "observation.npcapLoopbackMissing" => Pick("Npcap 未启用回环抓包，请重装并勾选回环支持", "Npcap 未啟用迴環擷取，請重裝並勾選迴環支援"),
        _ => RoomReadinessText.CaptureReason(code) ?? Localization.CodeText.Reason(code)
    };
}

/// <summary>侧栏“地图 · 频道”一行的文字与两项各自的悬停提示。</summary>
public sealed record LocationText(string Map, string Channel, string MapNote, string ChannelNote)
{
    public string Line => $"{Map} · {Channel}";
}
