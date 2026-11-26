using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Character;

/// <summary>
/// 游戏数据块的来源与新鲜度标注：<see cref="Text"/> 是卡片标题处的一行（来源与读取时刻，旧数据带前缀），
/// <see cref="Detail"/> 是需要解释时的一句（回落抓包、旧数据或未读取的原因），<see cref="Conflict"/> 是来源冲突时双方的值。
/// <see cref="Dim"/> 表示内容不是当前读到的（旧数据或未读取），界面用次要色。
/// </summary>
public sealed record BlockNote(string Text, string Detail, string Conflict, bool Dim)
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasConflict => Conflict.Length > 0;

    /// <summary>没有读到任何内容：只写“未读取”和可选的说明。</summary>
    public static BlockNote Unread(string detail = "") => new(Pick("未读取", "未讀取"), detail, string.Empty, true);
}

/// <summary>角色页与浮窗元件共用的文字：来源标注、天气、数值格式。状态文字不出现英文代码。</summary>
public static class CharacterText
{
    public static string Unread => Pick("未读取", "未讀取");

    /// <summary>数据来源名称；没有来源时为空。</summary>
    public static string SourceName(GameDataSource source) => source switch
    {
        GameDataSource.Connector => Pick("游戏连接器", "遊戲連接器"),
        GameDataSource.Capture => Pick("抓包", "抓包"),
        _ => string.Empty
    };

    // 指向侧栏“游戏连接器”胶囊的原因：连接器自身的状态与能力问题；游戏拒绝或通道占用等不指向胶囊。
    private static readonly HashSet<string> ConnectorReasons = new(StringComparer.Ordinal)
    {
        "gameCli.disabled", "gameCli.gameNotRunning", "gameCli.expired", "gameCli.cliMissing", "gameCli.cliHashMismatch", "gameCli.checking",
        "gameCli.catalogLoading", "gameCli.commandMissing", "gameCli.notConnected", "gameCli.pipeUnavailable", "gameCli.cliError",
        "gameCli.notDeployed", "gameCli.deployedDiffers", "gameCli.directoryUnknown", "gameCli.processUnverifiable", "gameCli.unknownCommand"
    };

    /// <summary>原因码换成一句中文；连接器自身的原因后面指向“游戏连接器”胶囊。没有原因时为空。</summary>
    public static string ReasonLine(string? code) => code is not { Length: > 0 } ? string.Empty
        : CodeText.Reason(code) + (ConnectorReasons.Contains(code) ? ConnectorText.Pointer : string.Empty);

    private static string Time(DateTimeOffset? at) => at is { } value ? value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>字段名换成界面文字，用于来源冲突的说明。</summary>
    public static string FieldName(string field) => field switch
    {
        "nickname" => Pick("昵称", "暱稱"),
        "area" or "map" => Pick("区域", "區域"),
        _ => field
    };

    /// <summary>
    /// 一个数据主题的标注：来源与读取时刻；旧数据加“旧数据”前缀并附原因；抓包回落附原因；没有来源时写“未读取”与原因；来源冲突列出双方的值。
    /// <paramref name="sample"/> 为真（样本后端）时来源写“样本数据”。
    /// </summary>
    public static BlockNote Note(GameDataStatus? status, bool sample = false)
    {
        if (status is null) return BlockNote.Unread();
        var source = status.Source.KnownValue;
        var reason = ReasonLine(status.Reason);
        var conflict = status.Conflicts is { Count: > 0 }
            ? Pick("来源冲突：", "來源衝突：") + string.Join("；", status.Conflicts.Select(item =>
                $"{FieldName(item.Field)} {SourceName(GameDataSource.Connector)}“{item.ConnectorValue ?? Unread}” ／ {SourceName(GameDataSource.Capture)}“{item.CaptureValue ?? Unread}”"))
            : string.Empty;
        if (source is not (GameDataSource.Connector or GameDataSource.Capture)) return BlockNote.Unread(reason);
        var name = sample ? Pick("样本数据", "樣本資料") : SourceName(source.Value);
        var text = Time(status.ReadAt) is { Length: > 0 } time ? $"{name} · {time}" : name;
        if (status.Stale) text = Pick("旧数据 · ", "舊資料 · ") + text;
        var detail = status.Stale || source == GameDataSource.Capture ? reason : string.Empty;
        return new BlockNote(text, detail, conflict, status.Stale);
    }

    /// <summary>天气代码换成中文；未收录的天气显示游戏原文，没有值为“未读取”。</summary>
    public static string Weather(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Unread;
        return code.Trim().ToLowerInvariant() switch
        {
            "sunny" or "clear" or "fine" => Pick("晴", "晴"),
            "cloudy" or "partlycloudy" => Pick("多云", "多雲"),
            "overcast" => Pick("阴", "陰"),
            "rain" or "rainy" or "raining" or "shower" => Pick("雨", "雨"),
            "storm" or "stormy" or "thunder" or "thunderstorm" => Pick("雷雨", "雷雨"),
            "snow" or "snowy" or "snowing" => Pick("雪", "雪"),
            "fog" or "foggy" or "mist" or "misty" => Pick("雾", "霧"),
            "wind" or "windy" => Pick("大风", "大風"),
            "hail" => Pick("冰雹", "冰雹"),
            "dust" or "sandstorm" => Pick("沙尘", "沙塵"),
            _ => code.Trim()
        };
    }

    /// <summary>数值：整数带千分位，小数最多保留两位；没有值为“未读取”。</summary>
    public static string Number(double? value)
    {
        if (value is not { } number) return Unread;
        return Math.Abs(number - Math.Round(number)) < 0.005
            ? Math.Round(number).ToString("N0", CultureInfo.InvariantCulture)
            : number.ToString("N2", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    /// <summary>“当前 / 上限”；两者都没有为“未读取”，只有一项时只写这一项。<paramref name="decimals"/> 是最多保留的小数位，末尾的 0 不写。</summary>
    public static string Pair(double? current, double? maximum, int decimals = 0)
    {
        string Format(double value) => value.ToString(decimals == 0 ? "N0" : "#,##0." + new string('#', decimals), CultureInfo.InvariantCulture);
        return (current, maximum) switch
        {
            ({ } a, { } b) => $"{Format(a)} / {Format(b)}",
            ({ } a, null) => Format(a),
            (null, { } b) => Pick("上限 ", "上限 ") + Format(b),
            _ => Unread
        };
    }

    private static readonly System.Text.RegularExpressions.Regex ClockPattern =
        new(@"(?<![0-9])([0-9]{1,2}):([0-9]{2})(?::[0-9]{2}(?:\.[0-9]+)?)?(?![0-9])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly string[] AfternoonMarks = ["PM", "P.M.", "下午", "晚上", "오후"];
    private static readonly string[] MorningMarks = ["AM", "A.M.", "上午", "凌晨", "早上", "오전"];

    /// <summary>
    /// 游戏内时间：取 <c>ErinnNow</c> 原文里最后一处“时:分”（带上午/下午标记时换成 24 小时制），日期与秒不写；读不出时刻时为原文，没有值为“未读取”。
    /// 不换算为现实时间。
    /// </summary>
    public static string ErinnClock(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Unread;
        var text = raw.Trim();
        var matches = ClockPattern.Matches(text);
        if (matches.Count == 0) return text;
        var last = matches[^1];
        var hour = int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture);
        if (minute > 59 || hour > 24) return text;
        if (hour <= 12)
        {
            if (AfternoonMarks.Any(mark => text.Contains(mark, StringComparison.OrdinalIgnoreCase)) && hour < 12) hour += 12;
            else if (MorningMarks.Any(mark => text.Contains(mark, StringComparison.OrdinalIgnoreCase)) && hour == 12) hour = 0;
        }
        return $"{hour % 24:00}:{minute:00}";
    }

    /// <summary>位置坐标（保留一位小数）；任一坐标没有时为“未读取”。</summary>
    public static string Position(double? x, double? y) => x is { } a && y is { } b
        ? $"({a.ToString("0.0", CultureInfo.InvariantCulture)}, {b.ToString("0.0", CultureInfo.InvariantCulture)})" : Unread;

    /// <summary>住宅状态：不在住宅内、在自己或他人的住宅内；没有值为“未读取”。</summary>
    public static string Housing(GameHousing? housing) => housing?.IsInHousing switch
    {
        true => housing.IsOwnedHousing switch
        {
            true => Pick("在自己的住宅内", "在自己的住宅內"),
            false => Pick("在他人的住宅内", "在他人的住宅內"),
            _ => Pick("在住宅内", "在住宅內")
        },
        false => Pick("不在住宅内", "不在住宅內"),
        _ => Unread
    };

    /// <summary>剩余秒数 → m:ss。</summary>
    public static string Duration(double seconds)
    {
        var total = (int)Math.Max(0, Math.Round(seconds));
        return $"{total / 60}:{total % 60:00}";
    }
}
