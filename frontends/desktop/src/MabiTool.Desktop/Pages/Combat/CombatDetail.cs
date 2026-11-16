using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>
/// DPS 随时间曲线的数据：每个桶的 DPS（桶内本人与计入的召唤物伤害 ÷ 桶宽）。<see cref="Unavailable"/> 不为空时曲线停用并写明原因；
/// <see cref="Loading"/> 为真表示窗口刚打开、时间线还在读取。
/// </summary>
public sealed record CombatTimelineView(IReadOnlyList<double> Dps, double BucketSeconds, double SpanSeconds, string? Unavailable, bool Loading = false)
{
    public static CombatTimelineView Pending { get; } = new([], 0, 0, null, true);
    public bool Available => Unavailable is null && !Loading && Dps.Count > 0;
}

/// <summary>图表里的一条排行：名称、数值文字、占比（0–1）与是否高亮（与表格选中的同一项）。</summary>
public sealed record BarItem(string Label, string Value, double Ratio, bool Highlighted = false, string Tip = "")
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

    /// <summary>DPS 明细窗口用的模型：同一后端，范围、目标与主页面是同一个选择。</summary>
    public CombatPageModel Companion() => new(backend, page: false);

    /// <summary>窗口打开期间为真：后端适配层读取时间线分桶。</summary>
    public void SetDetailOpen(bool open)
    {
        detailOpen = open;
        backend.SetCombatDetailOpen(open);
        RefreshDetail();
    }

    public CombatTimelineView Timeline { get; private set; } = CombatTimelineView.Pending;
    public bool TimelineAvailable => Timeline.Available;
    public bool TimelineLoading => Timeline.Loading;
    public string TimelineNote => Timeline.Unavailable ?? (Timeline.Loading ? UiText.Pick("正在读取时间线…", "正在讀取時間線…") : string.Empty);
    public bool HasTimelineNote => TimelineNote.Length > 0;
    public string TimelinePeak => Timeline.Available
        ? UiText.Pick($"峰值 {Math.Round(Timeline.Dps.Max()):N0}", $"峰值 {Math.Round(Timeline.Dps.Max()):N0}") : string.Empty;
    public string TimelineSpan => Timeline.Available
        ? UiText.Pick($"0:00 – {CombatPageModel.DurationText(Timeline.SpanSeconds * 1000)}，每点 {Timeline.BucketSeconds:0.#} 秒",
            $"0:00 – {CombatPageModel.DurationText(Timeline.SpanSeconds * 1000)}，每點 {Timeline.BucketSeconds:0.#} 秒") : string.Empty;
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
        TagBars.Sync([.. Data.Coverage.Concat(Data.Traits ?? []).Select(tile => new BarItem(tile.Name, tile.Percent, tile.Ratio, false, tile.Note))]);
    }

    private void RefreshDetail()
    {
        Timeline = detailOpen ? backend.GetCombatTimeline() ?? CombatTimelineView.Pending : CombatTimelineView.Pending;
        SyncBars();
    }
}
