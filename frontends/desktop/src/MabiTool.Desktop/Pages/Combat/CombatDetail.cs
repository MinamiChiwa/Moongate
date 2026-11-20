using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>
/// DPS 随时间曲线的数据：每个桶的 DPS（桶内本人与计入的召唤物伤害 ÷ 桶宽）。<see cref="Unavailable"/> 不为空时曲线停用并写明原因；
/// <see cref="Loading"/> 为真表示窗口刚打开、时间线还在读取。
/// </summary>
/// <param name="Origin">横轴的起点：整场按本机时钟标注（第一个点的时刻），其余范围为空、按相对时间标注。</param>
public sealed record CombatTimelineView(IReadOnlyList<double> Dps, double BucketSeconds, double SpanSeconds, string? Unavailable, bool Loading = false,
    DateTimeOffset? Origin = null)
{
    public static CombatTimelineView Pending { get; } = new([], 0, 0, null, true);
    public bool Available => Unavailable is null && !Loading && Dps.Count > 0;

    /// <summary>第 <paramref name="index"/> 个点的横坐标：整场为本机时钟“21:30:15”，其余为相对时间“0:42”。</summary>
    public string TimeAt(int index) => TimeOf(index * BucketSeconds);

    private string TimeOf(double seconds) => Origin is { } origin
        ? origin.AddSeconds(seconds).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
        : CombatPageModel.DurationText(seconds * 1000);

    /// <summary>每个点的悬停提示：横坐标（时间）与纵坐标（DPS 数值）。</summary>
    public IReadOnlyList<string> Tips => Dps.Select((value, index) =>
        $"{TimeAt(index)} · DPS {Math.Round(double.IsFinite(value) ? value : 0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}").ToList();

    /// <summary>横轴范围与每点宽度，例如“0:00 – 2:30，每点 3 秒”或整场“21:30:00 – 21:45:10，每点 15 秒”。</summary>
    public string SpanText => UiText.Pick($"{TimeOf(0)} – {TimeOf(SpanSeconds)}，每点 {BucketSeconds:0.#} 秒",
        $"{TimeOf(0)} – {TimeOf(SpanSeconds)}，每點 {BucketSeconds:0.#} 秒");
}

/// <summary>
/// 图表里的一条排行：名称、数值文字、占比（0–1）与是否高亮（与表格选中的同一项）。
/// 标签占比的项带客户端属性图标的键（<see cref="Icon"/>），图标可用时名称换成图标、名称进悬浮提示。
/// </summary>
public sealed record BarItem(string Label, string Value, double Ratio, bool Highlighted = false, string Tip = "", string? Icon = null)
{
    public string Hint => Tip.Length > 0 ? Tip : Label;
}

/// <summary>DPS 明细窗口的位置与尺寸；与背包窗口一样保存在前端偏好里。</summary>
public static class CombatDetailPreference
{
    public static Inventory.WindowPlacement? Window { get; private set; }
    public static event EventHandler? Changed;

    public static void SetWindow(Inventory.WindowPlacement placement)
    {
        if (Window == placement) return;
        Window = placement;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void Restore(Inventory.WindowPlacement? window) => Window = window;
}

// DPS 明细窗口的图表部分：排行与标签占比来自页面同一份数据，DPS 曲线来自后端时间线分桶（只在窗口打开时读取）。
public sealed partial class CombatPageModel
{
    private string? highlighted;
    private bool detailOpen;

    /// <summary>DPS 明细窗口用的模型：同一后端，范围与目标是窗口自己的选择（与主页面互不影响）。</summary>
    public CombatPageModel Companion() => new(backend, page: false, detail: true);

    /// <summary>记录明细窗口用的模型：同一后端，范围固定为 <paramref name="record"/> 这条战斗记录。</summary>
    public CombatPageModel Companion(string record) => new(backend, page: false, record: record);

    /// <summary>窗口打开期间为真：后端适配层读取时间线分桶（记录明细读取这条记录的范围汇总与分桶）。</summary>
    public void SetDetailOpen(bool open)
    {
        detailOpen = open;
        if (recordId is { } id) backend.SetCombatRecordOpen(id, open);
        else backend.SetCombatDetailOpen(open);
        RefreshDetail();
    }

    // ---- 记录明细窗口：标题、不完整标记与只有摘要时的说明 ----

    public bool IsRecord => recordId is not null;
    public bool IsLive => recordId is null;
    public string RecordLabel => Data.RecordInfo?.Name ?? string.Empty;
    public string RecordWhen => Data.RecordInfo?.When ?? string.Empty;
    public bool HasRecordWhen => RecordWhen.Length > 0;
    public bool RecordIncomplete => Data.RecordInfo?.DataIncomplete == true;
    public string RecordNote => Data.RecordInfo?.Note ?? string.Empty;
    public bool HasRecordNote => Data.RecordInfo?.HasNote == true;
    public string RecordGapNote => Data.RecordInfo?.GapNote ?? string.Empty;
    public bool HasRecordGapNote => RecordGapNote.Length > 0;
    /// <summary>图表、排行与总表是否显示：实时明细总显示；只有摘要的记录不显示，不以空图代替。</summary>
    public bool ShowDetailContent => Data.RecordInfo is not { SummaryOnly: true };
    public bool HasBreakLine => IsRecord && ShowDetailContent && Data.Break is not null;
    /// <summary>窗口标题栏（任务栏）文字：“记录名称 · 记录明细”。</summary>
    public string RecordWindowTitle => RecordLabel.Length > 0
        ? UiText.Pick($"{RecordLabel} · 记录明细", $"{RecordLabel} · 紀錄明細") : UiText.Pick("记录明细", "紀錄明細");

    // ---- 战斗记录卡片打开记录明细：开满时的说明 ----

    private string recordWindowNote = string.Empty;
    public string RecordWindowNote => recordWindowNote;
    public bool HasRecordWindowNote => recordWindowNote.Length > 0;

    /// <summary>打开记录明细的结果：开满时写明上限，打开成功时清除说明。</summary>
    public void SetRecordWindowLimit(bool reached, int maximum)
    {
        var text = reached ? UiText.Pick($"最多同时打开 {maximum} 个记录明细", $"最多同時開啟 {maximum} 個紀錄明細") : string.Empty;
        if (text == recordWindowNote) return;
        recordWindowNote = text;
        Raise(nameof(RecordWindowNote)); Raise(nameof(HasRecordWindowNote));
    }

    public CombatTimelineView Timeline { get; private set; } = CombatTimelineView.Pending;
    public bool TimelineAvailable => Timeline.Available;
    public bool TimelineLoading => Timeline.Loading;
    public string TimelineNote => Timeline.Unavailable ?? (Timeline.Loading ? UiText.Pick("正在读取时间线…", "正在讀取時間線…") : string.Empty);
    public bool HasTimelineNote => TimelineNote.Length > 0;
    public string TimelinePeak => Timeline.Available
        ? UiText.Pick($"峰值 {Math.Round(Timeline.Dps.Max()):N0}", $"峰值 {Math.Round(Timeline.Dps.Max()):N0}") : string.Empty;
    public string TimelineSpan => Timeline.Available ? Timeline.SpanText : string.Empty;
    public string HealingCurveNote => UiText.Pick("后端时间线分桶只含伤害，HPS 曲线暂不提供", "後端時間線分桶只含傷害，HPS 曲線暫不提供");

    public StableList<BarItem> SkillBars { get; } = [];
    public StableList<BarItem> TargetBars { get; } = [];
    public StableList<BarItem> TagBars { get; } = [];

    /// <summary>表格选中或排行点中的名称；排行中同名的项高亮。</summary>
    public string? Highlighted => highlighted;

    public void Highlight(string? name)
    {
        if (highlighted == name) return;
        highlighted = name;
        SyncBars();
        Raise(nameof(Highlighted));
    }

    public ICommand HighlightCommand => highlightCommand ??= new RelayCommand(item => Highlight((item as BarItem)?.Label is { } label && label != highlighted ? label : null));
    private ICommand? highlightCommand;

    // 排行取明细行（技能按伤害、目标按统一排序）的前 12 项；标签占比取暴击、强击、连击、追击与攻击特性各块（可重叠）。
    private void SyncBars()
    {
        static IReadOnlyList<BarItem> Rank(IReadOnlyList<DamageRow> rows, string? highlight) => rows.Take(12)
            .Select(row => new BarItem(row.Name, $"{row.Damage} · {row.Share}", row.ShareRatio, row.Name == highlight, row.Tip)).ToList();
        SkillBars.Sync(Rank(Data.Skills, highlighted));
        TargetBars.Sync(Rank(Data.Targets, highlighted));
        TagBars.Sync([.. Data.Coverage.Concat(Data.Traits ?? []).Select(tile => new BarItem(tile.Name, tile.Percent, tile.Ratio, false, tile.Note, tile.Icon))]);
    }

    private void RefreshDetail()
    {
        Timeline = !detailOpen ? CombatTimelineView.Pending
            : (recordId is { } id ? backend.GetCombatRecordTimeline(id) : backend.GetCombatTimeline()) ?? CombatTimelineView.Pending;
        SyncBars();
    }
}
