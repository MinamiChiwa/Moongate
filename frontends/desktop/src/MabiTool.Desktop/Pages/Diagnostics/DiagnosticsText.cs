using System.Globalization;
using System.Text;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>诊断页用到的固定取值与文字换算。探针、键、事件码、模块名是后端的稳定标识，原样显示不翻译。</summary>
public static class DiagnosticsText
{
    /// <summary>调试采集与记录筛选可选的模块，取自后端契约。</summary>
    public static readonly string[] Modules =
        ["core", "game-access", "combat-observation", "inventory", "music-library", "color", "rooms"];

    /// <summary>记录级别，取自后端的级别表。</summary>
    public static readonly string[] Levels = ["trace", "debug", "info", "warning", "error"];

    /// <summary>筛选按钮上的名称：模块取共用映射的中文名，级别写成中文。</summary>
    public static string Label(string id) => id switch
    {
        "trace" => Pick("跟踪", "追蹤"),
        "debug" => Pick("调试", "偵錯"),
        "info" => Pick("信息", "資訊"),
        "warning" => Pick("警告", "警告"),
        "error" => Pick("错误", "錯誤"),
        _ => CodeText.Module(id)
    };

    /// <summary>
    /// 计数值按键名后缀换算：Ms 为毫秒（满 1000 换成秒）、Bytes 为字节（KB、MB，1 KB = 1,024 字节）、
    /// Seconds 为秒（满 60 换成分）；其余键、不是整数的值原样返回。
    /// </summary>
    public static string Value(string key, string raw)
    {
        if (!ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return raw;
        if (key.EndsWith("Ms", StringComparison.Ordinal))
            return number < 1000
                ? Pick($"{number} 毫秒", $"{number} 毫秒")
                : Pick($"{Decimal(number / 1000.0)} 秒", $"{Decimal(number / 1000.0)} 秒");
        if (key.EndsWith("Bytes", StringComparison.Ordinal))
            return number < 1024
                ? Pick($"{number} 字节", $"{number} 位元組")
                : number < 1024UL * 1024
                    ? $"{Decimal(number / 1024.0)} KB"
                    : $"{Decimal(number / 1048576.0)} MB";
        if (key.EndsWith("Seconds", StringComparison.Ordinal))
        {
            if (number < 60) return Pick($"{number} 秒", $"{number} 秒");
            var (minutes, seconds) = (number / 60, number % 60);
            return seconds == 0
                ? Pick($"{minutes} 分", $"{minutes} 分")
                : Pick($"{minutes} 分 {seconds} 秒", $"{minutes} 分 {seconds} 秒");
        }
        return raw;
    }

    private static string Decimal(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// 探针的键值表：计数与短码合并，按键排序；计数按后缀换算，短码原样。
    /// </summary>
    public static IReadOnlyList<KeyValue> Rows(ProbeView probe) =>
    [
        .. probe.Counters.Select(item => new KeyValue(item.Label, Value(item.Label, item.Value)))
            .Concat(probe.Values)
            .OrderBy(item => item.Label, StringComparer.Ordinal)
    ];

    /// <summary>
    /// 探针原因码的短句，后面括注原因码（诊断页保留原码便于对照）；合奏相关的码沿用合奏页的文案，其余经共用映射，
    /// 没有文案的码写成中文概括并括注原码。
    /// </summary>
    public static string Reason(string code)
    {
        var text = code == "system.unexpectedError" ? Pick("该探针读取出错", "該探針讀取出錯")
            : CodeText.TryReason(code, out var known) ? known : RoomReadinessText.Reason(code);
        if (text is null) return code;
        return text.EndsWith($"（{code}）", StringComparison.Ordinal) ? text : $"{text}（{code}）";
    }

    /// <summary>请求被拒绝或失败时的页面文字：已知错误码给短句，其余带码显示。</summary>
    public static string Error(string code) => code switch
    {
        "diagnostics.captureActive" => Pick("已有采集在进行", "已有採集在進行"),
        "permission.denied" => Pick("当前授权没有这项权限", "目前授權沒有這項權限"),
        "frontend.notConnected" => Pick("未连接后端", "未連接後端"),
        "resource.notFound" => Pick("找不到所选内容，可能已被后端清理", "找不到所選內容，可能已被後端清理"),
        _ => Pick("请求未完成：", "請求未完成：") + CodeText.Reason(code)
    };

    /// <summary>采集的结束原因；进行中为空。</summary>
    public static string EndReason(string? reason) => reason switch
    {
        null or "" => string.Empty,
        "expired" => Pick("到期", "到期"),
        "stopped" => Pick("手动结束", "手動結束"),
        "hostStopping" => Pick("宿主退出", "宿主退出"),
        "restarted" => Pick("宿主重启", "宿主重啟"),
        _ => CodeText.Reason(reason)
    };

    /// <summary>剩余时间，例如 1:05；已过期显示 0:00。</summary>
    public static string Remaining(DateTimeOffset expires, DateTimeOffset now)
    {
        var left = Math.Max(0, (int)Math.Ceiling((expires - now).TotalSeconds));
        return $"{left / 60}:{left % 60:00}";
    }

    public static string LocalTime(DateTimeOffset time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string LocalClock(DateTimeOffset time) =>
        time.ToLocalTime().ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>探针上被截断或丢弃的条目说明；没有时为空。</summary>
    public static string EntryNotice(int truncated, int rejected)
    {
        var parts = new List<string>();
        if (truncated > 0) parts.Add(Pick($"{truncated} 项因数量上限被截断", $"{truncated} 項因數量上限被截斷"));
        if (rejected > 0) parts.Add(Pick($"{rejected} 项不符合白名单被丢弃", $"{rejected} 項不符合白名單被丟棄"));
        return string.Join(Pick("，", "，"), parts);
    }

    /// <summary>整份快照的纯文本，按探针列出键值，计数保持后端给出的原值，供“复制全部”粘进用户反馈。</summary>
    public static string SnapshotText(RuntimeView view)
    {
        var text = new StringBuilder();
        text.Append("capturedAt: ").AppendLine(view.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
        text.Append("backendEpoch: ").AppendLine(view.BackendEpoch);
        if (view.TruncatedProbes > 0) text.Append("truncatedProbes: ").AppendLine(view.TruncatedProbes.ToString(CultureInfo.InvariantCulture));
        if (view.RejectedProbes > 0) text.Append("rejectedProbes: ").AppendLine(view.RejectedProbes.ToString(CultureInfo.InvariantCulture));
        foreach (var probe in view.Probes)
        {
            text.AppendLine().Append('[').Append(probe.Id).Append("] module=").Append(probe.Module)
                .Append(" state=").Append(probe.State);
            if (probe.Reason is { Length: > 0 } reason) text.Append(" reason=").Append(reason);
            text.AppendLine();
            if (probe.TruncatedEntries > 0) text.Append("  truncatedEntries: ").AppendLine(probe.TruncatedEntries.ToString(CultureInfo.InvariantCulture));
            if (probe.RejectedEntries > 0) text.Append("  rejectedEntries: ").AppendLine(probe.RejectedEntries.ToString(CultureInfo.InvariantCulture));
            foreach (var item in probe.Counters.Concat(probe.Values).OrderBy(item => item.Label, StringComparer.Ordinal))
                text.Append("  ").Append(item.Label).Append(": ").AppendLine(item.Value);
        }
        return text.ToString();
    }
}
