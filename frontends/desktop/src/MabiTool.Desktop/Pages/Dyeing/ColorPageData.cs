using System.Globalization;
using System.Text.RegularExpressions;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>后端的原始取值。界面不认识的值原样保留，除非用户改选。</summary>
public static partial class ColorCodes
{
    public const string ExactHex = "exact-hex";
    public const string Similar = "similar";
    public const string Screenshot = "screenshot";
    public const string Precise = "precise";

    /// <summary>替代色上限（与原工具、后端校验一致）。</summary>
    public const int MaximumAlternatives = 63;

    /// <summary>卡片上直接显示的替代色个数，其余以“+N”表示。</summary>
    public const int VisibleAlternatives = 6;

    public const double MinimumTolerance = 1, MaximumTolerance = 35;

    /// <summary>
    /// 六位十六进制色码整理成大写 #RRGGBB；格式不对时为空（原工具 vision.normalize_hex 的规则）。
    /// </summary>
    public static string? NormalizeHex(string? text)
    {
        var value = (text ?? string.Empty).Trim().TrimStart('#').ToUpperInvariant();
        return HexPattern().IsMatch(value) ? "#" + value : null;
    }

    [GeneratedRegex("^[0-9A-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();
}

/// <summary>草稿里的一个区域；<see cref="MatchMode"/> 是后端原始值。替代色按内容比较，刷新时设置没变的卡片不重新载入。</summary>
public sealed record ColorRegionDraft(string Id, string TargetHex, IReadOnlyList<string> AlternativeHex, string MatchMode,
    double Tolerance, int Priority, bool Enabled)
{
    public bool Equals(ColorRegionDraft? other) => other is not null && Id == other.Id && TargetHex == other.TargetHex &&
        MatchMode == other.MatchMode && Tolerance.Equals(other.Tolerance) && Priority == other.Priority && Enabled == other.Enabled &&
        AlternativeHex.SequenceEqual(other.AlternativeHex);

    public override int GetHashCode() => HashCode.Combine(Id, TargetHex, MatchMode, Tolerance, Priority, Enabled, AlternativeHex.Count);
}

/// <summary>交给后端的草稿：寻色方式（后端原始值）与三个区域。</summary>
public sealed record ColorDraft(string Strategy, IReadOnlyList<ColorRegionDraft> Regions);

/// <summary>
/// 后端草稿的一个区域。<see cref="Available"/> 为假表示本局不可用（后端按本局色板给出；后端没有该信息时为真）。
/// </summary>
public sealed record ColorRegionView(string Id, string TargetHex, IReadOnlyList<string> AlternativeHex, string MatchMode,
    double Tolerance, int Priority, bool Enabled, bool Available = true)
{
    public ColorRegionDraft ToDraft() => new(Id, TargetHex, AlternativeHex, MatchMode, Tolerance, Priority, Enabled);
}

/// <summary>后端的当前草稿；<see cref="Revision"/> 变化表示后端的草稿已更新。</summary>
public sealed record ColorDraftView(string Strategy, IReadOnlyList<ColorRegionView> Regions, string Revision);

/// <summary>
/// 一个区域的读数，区域卡、运行栏与游戏浮窗取同一份：<see cref="CurrentHex"/> 是引擎对当前画面的读数（进行中的最新读数、结束时复核的当前颜色），
/// <see cref="MeasuredHex"/>、<see cref="MeasuredDelta"/> 与 <see cref="Mark"/> 是这份实测的色码、色差与后端判定；没有读数时为空。
/// <see cref="Unconfirmed"/> 为真表示结束时没有复核当前颜色，实测取的是上一次实测（原工具“上次实测（当前未确认）”）。
/// </summary>
public sealed record ColorReading(string? CurrentHex, string? MeasuredHex, double? MeasuredDelta, ColorMark? Mark = null, bool Unconfirmed = false)
{
    public static string UnreadText => UiText.Pick("未读取", "未讀取");

    /// <summary>“实测”一行的标签：上次实测且当前未确认时照原工具写明。</summary>
    public string MeasuredLabel => Unconfirmed ? UiText.Pick("上次实测（当前未确认）", "上次實測（目前未確認）") : UiText.Pick("实测", "實測");
}

/// <summary>
/// 后端的分区判定（ColorRegionJudgement.state 原始值）：达标、未达标、未开始、未参与、未启用。
/// 原工具 RegionCard 徽标：达标为绿色，未达标为琥珀色，其余为灰色；不认识的值原样显示。
/// </summary>
public sealed record ColorMark(string State)
{
    public const string Met = "met", Unmet = "unmet", NotStarted = "not-started", Excluded = "excluded", Disabled = "disabled";

    /// <summary>前端占位卡片的判定：没有读到后端草稿，不知道该区域的状态。</summary>
    public const string Unread = "unread";

    public static ColorMark Waiting { get; } = new(NotStarted);

    public string Text => State switch
    {
        Met => UiText.Pick("达标", "達標"),
        Unmet => UiText.Pick("未达标", "未達標"),
        NotStarted => UiText.Pick("未开始", "未開始"),
        Excluded => UiText.Pick("未参与", "未參與"),
        Disabled => UiText.Pick("未启用", "未啟用"),
        Unread => UiText.Pick("未读取", "未讀取"),
        _ => State
    };

    public bool Good => State == Met;
    public bool Warn => State == Unmet;
    public bool Muted => !Good && !Warn;

    /// <summary>
    /// 按区域标识取判定：后端只列本局存在的区域，空列表或列表里没有该区域时为“未开始”；
    /// 规则里停用的区域没有判定时为“未启用”。
    /// </summary>
    public static ColorMark Of(IEnumerable<(string RegionId, string State)> judgements, string regionId, bool enabled = true) =>
        judgements.FirstOrDefault(item => item.RegionId == regionId) is { State.Length: > 0 } found
            ? new ColorMark(found.State)
            : new ColorMark(enabled ? NotStarted : Disabled);
}

/// <summary>候选卡里的一个区域：HEX、色差文字（后端没有给出时为空）与分区判定。</summary>
public sealed record CandidateRegion(string Name, string Hex, string Delta, ColorMark? Mark = null)
{
    public bool HasHex => Hex.Length > 0;
    public Avalonia.Media.IBrush Swatch => ColorSwatches.Brush(Hex);
    public ColorMark Badge => Mark ?? ColorMark.Waiting;
}

/// <summary>一个候选方案：标题标明游戏实测或预测颜色；当前方案的按钮不可点。</summary>
public sealed record ColorCandidateCard(string Id, string Title, IReadOnlyList<CandidateRegion> Regions, string Summary,
    string ActionLabel, bool CanSelect)
{
    // 区域列表按内容比较：刷新时内容没变的候选卡容器保留（StableItems）。
    public bool Equals(ColorCandidateCard? other) => other is not null && Id == other.Id && Title == other.Title &&
        Summary == other.Summary && ActionLabel == other.ActionLabel && CanSelect == other.CanSelect && Regions.SequenceEqual(other.Regions);

    public override int GetHashCode() => HashCode.Combine(Id, Title, Summary, ActionLabel, CanSelect, Regions.Count);

    public string Intent => "color.select:" + Id;
    public bool HasSummary => Summary.Length > 0;
}

/// <summary>命名方案：名称原文与各启用区域的目标色预览（原工具 presetRows 只给色码，这里画成色块）。</summary>
public sealed record ColorProfileRow(string Id, string Name, IReadOnlyList<ProfileSwatch> Swatches)
{
    // 色块列表按内容比较：刷新时内容没变的方案行容器保留（StableItems）。
    public bool Equals(ColorProfileRow? other) => other is not null && Id == other.Id && Name == other.Name && Swatches.SequenceEqual(other.Swatches);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Swatches.Count);

    public bool NoSwatches => Swatches.Count == 0;

    /// <summary>悬停说明：各启用区域的目标色码。</summary>
    public string Tip => string.Join(", ", Swatches.Select(swatch => swatch.Hex));
}

/// <summary>方案里一个启用区域的目标色；有替代色时在色块旁标出数量。</summary>
public sealed record ProfileSwatch(string Hex, int Alternatives)
{
    public Avalonia.Media.IBrush Brush => ColorSwatches.Brush(Hex);
    public bool HasAlternatives => Alternatives > 0;
    public string AlternativesText => "+" + Alternatives.ToString(CultureInfo.InvariantCulture);
    public string Tip => Alternatives > 0
        ? Hex + UiText.Pick($"（另有 {Alternatives} 个替代色）", $"（另有 {Alternatives} 個替代色）")
        : Hex;
}

/// <summary>色块画刷：合法的 #RRGGBB 画成该颜色，其余透明。</summary>
public static class ColorSwatches
{
    public static Avalonia.Media.IBrush Brush(string? hex) => ColorCodes.NormalizeHex(hex) is { } value
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(value))
        : Avalonia.Media.Brushes.Transparent;
}

/// <summary>
/// 寻色进度（后端 ColorRun.progress）：原阶段键、计数，以及按本机时钟推算剩余时间所需的锚点。
/// <see cref="RemainingAtUpdateMs"/> = deadlineMonotonicMs − updatedMonotonicMs，没有期限时为空；<see cref="RunSeenAt"/> 是本机第一次看到这次寻色的时间，“已用”从这里算起。
/// </summary>
public sealed record ColorProgressView(string RunId, string? Stage, int? Current, int? Total, DateTimeOffset StageStartedAt,
    double? RemainingAtUpdateMs, DateTimeOffset UpdatedAt, DateTimeOffset RunSeenAt)
{
    /// <summary>剩余时间 = 更新时的剩余 − (本机现在 − updatedAt)，小于 0 按 0；没有期限时为空。</summary>
    public TimeSpan? Remaining(DateTimeOffset now) => RemainingAtUpdateMs is { } left
        ? TimeSpan.FromMilliseconds(Math.Max(0, left - (now - UpdatedAt).TotalMilliseconds))
        : null;

    /// <summary>本阶段已进行的时间（stageStartedAt 起，小于 0 按 0）。</summary>
    public TimeSpan StageElapsed(DateTimeOffset now) => now > StageStartedAt ? now - StageStartedAt : TimeSpan.Zero;

    public TimeSpan Elapsed(DateTimeOffset now) => now > RunSeenAt ? now - RunSeenAt : TimeSpan.Zero;

    /// <summary>“当前/总数”，只在后端给出总数时有。</summary>
    public string Count => Total is > 0 ? $"{(Current ?? 0).ToString(CultureInfo.InvariantCulture)}/{Total.Value.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

    /// <summary>“剩余 mm:ss”；没有期限时为空。剩 10 秒内（原工具浮窗 left ≤ 10）为琥珀色。</summary>
    public string RemainingText(DateTimeOffset now) => Remaining(now) is { } left
        ? UiText.Pick("剩余 ", "剩餘 ") + Clock(left)
        : string.Empty;

    public bool Low(DateTimeOffset now) => Remaining(now) is { } left && (int)left.TotalSeconds <= 10;

    /// <summary>“已用 N 秒 · 本阶段 N 秒”（原工具浮窗 heartbeat）。</summary>
    public string ElapsedText(DateTimeOffset now) => UiText.Pick(
        $"已用 {(int)Elapsed(now).TotalSeconds} 秒 · 本阶段 {(int)StageElapsed(now).TotalSeconds} 秒",
        $"已用 {(int)Elapsed(now).TotalSeconds} 秒 · 本階段 {(int)StageElapsed(now).TotalSeconds} 秒");

    public static string Clock(TimeSpan value)
    {
        var seconds = (int)value.TotalSeconds;
        return $"{(seconds / 60).ToString("00", CultureInfo.InvariantCulture)}:{(seconds % 60).ToString("00", CultureInfo.InvariantCulture)}";
    }
}

public sealed record ColorActions(bool CanEdit, bool CanStart, bool CanStop, bool CanRetry, bool CanPick);

/// <summary>状态行的色调：空闲、进行中、需要注意。</summary>
public enum ColorTone { Idle, Live, Warn }

/// <summary>
/// 染色页数据。<see cref="Readings"/> 按区域标识索引；<see cref="Accepted"/> 是后端最佳实测的整体达标标记，没有实测时为空。
/// <see cref="StartBlocker"/> 是“开始寻色”不可用的具体原因（组件缺失、未绑定游戏窗口、窗口最小化等），可以开始或正在寻色时为空。
/// <see cref="Status"/> 与 <see cref="Detail"/> 是运行栏的状态行与说明行（<see cref="ColorRunText"/>），游戏浮窗的染色元件取同一对文字。
/// </summary>
public sealed record ColorPageData(ColorDraftView? Draft, IReadOnlyDictionary<string, ColorReading> Readings, string Status,
    ColorTone Tone, bool? Accepted, IReadOnlyList<ColorCandidateCard> Candidates, IReadOnlyList<ColorProfileRow> Profiles,
    ColorActions Actions, string Notice = "", ColorProgressView? Progress = null, string StartBlocker = "", string Detail = "")
{
    public bool HasDetail => Detail.Length > 0;

    /// <summary>运行栏状态的悬停提示：原因、背景与引擎说明全文（<see cref="ColorRunStatus.Tip"/>）。</summary>
    public string DetailTip { get; init; } = string.Empty;

    /// <summary>
    /// 游戏浮窗染色元件的区域行（原工具浮窗的读数部分）：启用的区域按顺序各一行，目标色与实测色各占色块一半，
    /// 后接实测 HEX、色差与判定，与区域卡“实测”一行同一份实测；没读出时为“未读取”。
    /// </summary>
    public IReadOnlyList<ColorOverlayRow> OverlayRows => Draft?.Regions.Select((region, index) => (region, index))
        .Where(pair => pair.region.Enabled).Select(pair =>
        {
            var reading = Readings.GetValueOrDefault(pair.region.Id);
            var text = reading?.MeasuredHex is { } hex
                ? hex + (reading.MeasuredDelta is { } delta ? " · " + ColorStages.Delta(delta) : string.Empty)
                : ColorReading.UnreadText;
            return new ColorOverlayRow(UiText.Pick($"区域 {pair.index + 1}", $"區域 {pair.index + 1}") + "  " + text,
                pair.region.TargetHex, reading?.MeasuredHex, reading?.MeasuredHex is null ? null : reading.Mark);
        }).ToList() ?? [];

    /// <summary>浮窗区域行上方的标签：结束时没有复核当前颜色时写“上次实测（当前未确认）”，否则为“游戏实测”。</summary>
    public string OverlayCaption => Readings.Values.Any(reading => reading.Unconfirmed)
        ? UiText.Pick("上次实测（当前未确认）", "上次實測（目前未確認）")
        : UiText.Pick("游戏实测", "遊戲實測");

    /// <summary>浮窗说明：与运行栏同一份文字，去掉已由区域行显示的读数。</summary>
    public string OverlayGuide { get; init; } = Detail;
    public bool HasOverlayGuide => OverlayGuide.Length > 0;
    public bool Running => Actions.CanStop;
    public bool CanRetry => Actions.CanRetry;
    public bool HasCandidates => Candidates.Count > 0;
    public bool Warn => Tone == ColorTone.Warn;
    public bool Live => Tone == ColorTone.Live;
}

/// <summary>浮窗元件里的一个区域：文字、目标色与实测色（色块各占一半）与判定徽标。</summary>
public sealed record ColorOverlayRow(string Text, string TargetHex, string? MeasuredHex, ColorMark? Mark = null)
{
    public IReadOnlyList<MabiTool.Desktop.Controls.BarSegment> Swatch =>
        [MabiTool.Desktop.Controls.BarSegment.FromHex(TargetHex), MabiTool.Desktop.Controls.BarSegment.FromHex(MeasuredHex ?? "#00000000")];

    public bool HasMark => Mark is not null;
}

/// <summary>后端给出的一张 RGB24 位图（逐行排列）。</summary>
public sealed record ColorBitmap(int Width, int Height, byte[] Rgb);

/// <summary>
/// 目标色集合的一张图（后端 POST /color/palettes 的结果）：颜色数、后端实际采用的容差（精准 HEX 为 0）与解码后的图像。
/// 缩略图（overview，240×64）与全集图（atlas，每个允许的颜色至少一个像素）分两次读取。
/// </summary>
public sealed record ColorAtlas(int Count, double Tolerance, Avalonia.Media.Imaging.Bitmap Image);

/// <summary>读取目标色集合的回执；没有取到时 <see cref="Atlas"/> 为空，消息说明原因；<see cref="Retryable"/> 表示稍后再试可能成功（排队已满、计算超时）。</summary>
public sealed record ColorAtlasReply(ColorAtlas? Atlas, string Message, bool Retryable = false);

/// <summary>后端阶段名到原工具 ui_progress.STAGES 的中文阶段说明；没有登记的阶段显示“未知阶段”，原文记入诊断页。</summary>
public static class ColorStages
{
    private static readonly Dictionary<string, (string Hans, string Hant)> Names = new(StringComparer.Ordinal)
    {
        ["native_validate"] = ("检查当前色板", "檢查目前色板"),
        ["discovery"] = ("准备寻色", "準備尋色"),
        ["waiting"] = ("等待染色界面", "等待染色介面"),
        ["zoom"] = ("识别与缩放", "辨識與縮放"),
        ["capture"] = ("采集颜色板", "採集顏色板"),
        ["binding"] = ("识别本局色板", "辨識本局色板"),
        ["align"] = ("对齐采集画面", "對齊採集畫面"),
        ["period"] = ("测量色板周期", "測量色板週期"),
        ["stitch"] = ("拼接颜色板", "拼接顏色板"),
        ["export"] = ("保存颜色板", "儲存顏色板"),
        ["validate"] = ("检查拼图质量", "檢查拼圖品質"),
        ["search"] = ("搜索目标颜色", "搜尋目標顏色"),
        ["similarity"] = ("计算旋转与缩放", "計算旋轉與縮放"),
        ["ready"] = ("准备候选方案", "準備候選方案"),
        ["position"] = ("移动到目标位置", "移動到目標位置"),
        ["verify"] = ("复核游戏色码", "複核遊戲色碼"),
        ["observe"] = ("读取游戏色码", "讀取遊戲色碼"),
        ["restore"] = ("返回本轮最好颜色", "返回本輪最好顏色"),
        ["retry"] = ("重新搜索目标颜色", "重新搜尋目標顏色"),
        ["refinement"] = ("正在优化当前配色", "正在最佳化目前配色"),
        ["initial"] = ("准备寻色", "準備尋色"),
        ["complete"] = ("准备完成", "準備完成"),
        ["timer_grace_binding"] = ("等待计时稳定", "等待計時穩定"),
        ["return_protection"] = ("返回较好结果", "返回較好結果")
    };

    /// <summary>登记了中文名的阶段。</summary>
    public static IReadOnlyCollection<string> Known => Names.Keys;

    public static string Name(string? phase) => phase is not null && Names.TryGetValue(phase, out var name)
        ? UiText.Pick(name.Hans, name.Hant)
        : phase is { Length: > 0 } ? CodeText.Unknown("color-stage", phase, "未知阶段", "未知階段") : UiText.Pick("正在计算", "正在計算");

    /// <summary>寻色结果：达标、妥协与未复核取原工具的说法；宿主给出的常见停止原因写成中文，其余显示“未知结果”并记入诊断页。</summary>
    public static string Outcome(string? outcome) => outcome switch
    {
        "matched" => UiText.Pick("目标达标", "目標達標"),
        "compromise" => UiText.Pick("妥协结果", "妥協結果"),
        "unverified" => UiText.Pick("未完成复核", "未完成複核"),
        "stopped" => UiText.Pick("已停止", "已停止"),
        "not_found" => UiText.Pick("未找到达标方案", "未找到達標方案"),
        "input.focusChanged" => UiText.Pick("游戏不在前台", "遊戲不在前景"),
        "input.bindingChanged" => UiText.Pick("游戏窗口已变化", "遊戲視窗已變化"),
        "resource.busy" => UiText.Pick("游戏输入被占用", "遊戲輸入被佔用"),
        "frontend-lost" => UiText.Pick("界面失联", "介面失聯"),
        "emergency-stop" => UiText.Pick("紧急停止", "緊急停止"),
        "host-stopped" => UiText.Pick("后端已停止", "後端已停止"),
        null or "" => UiText.Pick("未知", "未知"),
        _ when CodeText.TryReason(outcome, out var reason) => reason,
        _ => CodeText.Unknown("color-outcome", outcome, "未知结果", "未知結果")
    };

    /// <summary>色差文字，原工具的两位小数写法。</summary>
    public static string Delta(double value) => "ΔE " + value.ToString("0.00", CultureInfo.InvariantCulture);
}
