using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using MabiTool.Desktop.Infrastructure;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>槽位圆点的状态：空槽（可安排）、已安排待执行（游戏里排队未开始，或总队列里尚未执行的该设施加工）、加工中、已完成。</summary>
public enum LifeDotState { Empty, Scheduled, Working, Done }

/// <summary>设施块上的一个槽位圆点；空槽与已完成的圆点可以点按（安排加工、领取）。</summary>
public sealed class LifeSlotDot : ObservableObject
{
    private string tip = string.Empty;

    public LifeDotState State { get; init; }
    public bool IsEmpty => State == LifeDotState.Empty;
    public bool IsScheduled => State == LifeDotState.Scheduled;
    public bool IsWorking => State == LifeDotState.Working;
    public bool IsDone => State == LifeDotState.Done;
    public string Glyph => State switch { LifeDotState.Empty => "+", LifeDotState.Done => "✓", _ => string.Empty };
    public ICommand? Command { get; init; }
    public object? Parameter { get; init; }
    public bool Clickable => Command is not null;

    /// <summary>悬停提示：加工品名与状态或完成还需的时间。</summary>
    public string Tip { get => tip; set => Set(ref tip, value); }

    /// <summary>游戏加工队列里的项目对应的圆点（倒计时更新提示用）；总队列里待执行的圆点为空。</summary>
    internal (string Facility, string Name, int Ordinal)? Work { get; init; }
}

/// <summary>加工设施块：名称、图标、一排槽位圆点、当前剩余与全部完成还需、全部领取。</summary>
public sealed class LifeFacilityTile : ObservableObject
{
    private IReadOnlyList<LifeSlotDot> dots = [];
    private string subtitle = string.Empty, signature = string.Empty, fullNote = string.Empty;
    private int completed;
    private bool canCollect, canArrange;

    public required string Name { get; init; }
    public string IconSlot => LifeIcons.FacilitySlot(Name);
    public IBrush Accent => LifeIcons.FacilityAccent(Name);
    public required ICommand CollectCommand { get; init; }

    public IReadOnlyList<LifeSlotDot> Dots => dots;

    /// <summary>圆点的组成变化时才换列表（同样的组成只更新提示文字）。</summary>
    internal bool SetDots(string next, Func<IReadOnlyList<LifeSlotDot>> build)
    {
        if (next == signature) return false;
        signature = next;
        dots = build();
        Raise(nameof(Dots));
        return true;
    }

    public string Subtitle { get => subtitle; set => Set(ref subtitle, value); }

    /// <summary>槽位已满的说明；没有时为空。</summary>
    public string FullNote { get => fullNote; set { if (Set(ref fullNote, value)) Raise(nameof(HasFullNote)); } }
    public bool HasFullNote => fullNote.Length > 0;

    public int Completed
    {
        get => completed;
        set { if (Set(ref completed, value)) { Raise(nameof(CollectEnabled)); Raise(nameof(CollectText)); Raise(nameof(CollectTip)); } }
    }

    /// <summary>“全部领取”始终显示；没有已完成的加工，或领取不可用（连接器、活动锁、专注钓鱼）时停用。</summary>
    public bool CollectEnabled => canCollect && completed > 0;
    public string CollectText => completed > 0 ? Localization.UiText.Pick($"全部领取 · {completed}", $"全部領取 · {completed}") : Localization.UiText.Pick("全部领取", "全部領取");
    /// <summary>停用原因：没有已完成的加工时写明；其余时候没有悬停提示。</summary>
    public string? CollectTip => completed > 0 ? null : LifeText.NothingToCollect;

    /// <summary>领取是否可用（连接器、活动锁、专注钓鱼与指令）；与已完成数一起决定“全部领取”是否可点。</summary>
    public bool CanCollect { get => canCollect; set { if (Set(ref canCollect, value)) Raise(nameof(CollectEnabled)); } }
    public bool CanArrange { get => canArrange; set => Set(ref canArrange, value); }
}

/// <summary>制作设施小块：点按弹出该设施的配方菜单。</summary>
public sealed class LifeCraftTile : ObservableObject
{
    private string summary = string.Empty;

    public required string Name { get; init; }
    public string? Facility { get; init; }
    public string IconSlot => LifeIcons.CraftSlot(Facility);
    public IBrush Accent => LifeIcons.CraftAccent(Facility);
    public required ICommand OpenCommand { get; init; }
    public string Summary { get => summary; set => Set(ref summary, value); }
}

/// <summary>采集的一个技能分组：默认折叠，展开时才生成采集物的行。</summary>
public sealed class LifeSkillGroup : ObservableObject
{
    private string countText = string.Empty, issueText = string.Empty, reason = string.Empty;
    private bool expanded;
    private IReadOnlyList<LifeGatherRow> items = [];

    public required string Name { get; init; }
    public string? Skill { get; init; }
    public string IconSlot => LifeIcons.SkillSlot(Skill);
    public IBrush Accent => LifeIcons.SkillAccent(Skill);
    public required ICommand ToggleCommand { get; init; }

    public string CountText { get => countText; set => Set(ref countText, value); }
    public string IssueText { get => issueText; set { if (Set(ref issueText, value)) Raise(nameof(HasIssue)); } }
    public bool HasIssue => issueText.Length > 0;

    /// <summary>“其他”分组的原因（归属读不到的原因）；其他分组为空。</summary>
    public string Reason { get => reason; set { if (Set(ref reason, value)) Raise(nameof(HasReason)); } }
    public bool HasReason => reason.Length > 0 && expanded;

    public bool IsExpanded
    {
        get => expanded;
        set { if (Set(ref expanded, value)) { Raise(nameof(Chevron)); Raise(nameof(HasReason)); } }
    }

    public string Chevron => expanded ? "▾" : "▸";

    /// <summary>展开时的采集物行；折叠时为空列表，不生成控件。</summary>
    public IReadOnlyList<LifeGatherRow> Items { get => items; set => Set(ref items, value); }
}

/// <summary>
/// 展开后的一个采集物：工具状态、目标数量框（个，默认 10：命令行每次以 10 个为目标采集）与“加入队列”；钓鱼类采集物没有加入按钮，指向专注钓鱼。
/// </summary>
public sealed class LifeGatherRow : ObservableObject
{
    private string count = "10";
    private bool canAdd;

    public required string Name { get; init; }
    public string ToolText { get; init; } = string.Empty;
    public bool ToolProblem { get; init; }
    public bool Fishing { get; init; }
    public bool NotFishing => !Fishing;
    public required ICommand AddCommand { get; init; }
    public required ICommand FishingCommand { get; init; }
    public string CountText { get => count; set => Set(ref count, value); }
    public bool CanAdd { get => canAdd; set => Set(ref canAdd, value); }
}

/// <summary>总队列的一行：种类图标、标题、进度、状态、最近一次结果；待执行的可以改数量、移动与删除。</summary>
public sealed class LifeTaskRow : ObservableObject
{
    private string title = string.Empty, subtitle = string.Empty, progressText = string.Empty, stateText = string.Empty, result = string.Empty, countText = string.Empty;
    private double progress;
    private bool running, pausedHere, finished, failed, canEdit, canMove, canRemove, canUp, canDown, showProgress;
    private string kind = "gather";
    private string? facility;

    public required string Id { get; init; }
    public string Kind { get => kind; set { if (Set(ref kind, value)) { Raise(nameof(IconSlot)); Raise(nameof(Accent)); } } }
    public string? Facility { get => facility; set { if (Set(ref facility, value)) { Raise(nameof(IconSlot)); Raise(nameof(Accent)); } } }
    public string IconSlot => LifeIcons.TaskSlot(kind, facility);
    public IBrush Accent => LifeIcons.TaskAccent(kind, facility);
    public required ICommand RemoveCommand { get; init; }
    public required ICommand UpCommand { get; init; }
    public required ICommand DownCommand { get; init; }
    public required ICommand MinusCommand { get; init; }
    public required ICommand PlusCommand { get; init; }

    public string Title { get => title; set => Set(ref title, value); }
    public string Subtitle { get => subtitle; set { if (Set(ref subtitle, value)) Raise(nameof(HasSubtitle)); } }
    public bool HasSubtitle => subtitle.Length > 0;
    public string ProgressText { get => progressText; set => Set(ref progressText, value); }
    public double Progress { get => progress; set => Set(ref progress, value); }
    public bool ShowProgress { get => showProgress; set => Set(ref showProgress, value); }
    public string StateText { get => stateText; set => Set(ref stateText, value); }
    public string Result { get => result; set { if (Set(ref result, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => result.Length > 0;
    public bool IsRunning { get => running; set => Set(ref running, value); }
    public bool IsPausedHere { get => pausedHere; set => Set(ref pausedHere, value); }
    public bool IsFinished { get => finished; set => Set(ref finished, value); }
    public bool IsFailed { get => failed; set => Set(ref failed, value); }

    /// <summary>数量框：只有待执行的采集、加工、制作可改。</summary>
    public string CountText { get => countText; set => Set(ref countText, value); }
    public bool CanEdit { get => canEdit; set => Set(ref canEdit, value); }
    public bool CanMove { get => canMove; set => Set(ref canMove, value); }
    public bool CanUp { get => canUp; set => Set(ref canUp, value); }
    public bool CanDown { get => canDown; set => Set(ref canDown, value); }
    public bool CanRemove { get => canRemove; set => Set(ref canRemove, value); }

    /// <summary>上次从快照取得的数量（数量框改动的比较基准）。</summary>
    internal int Count { get; set; }
}

/// <summary>
/// 二级菜单（加工空槽的配方、制作设施的配方）：打开时才生成。搜索框按配方名与材料名过滤（忽略大小写与全角半角），
/// 过滤后的列表沿用同一批行对象，行里填好的数量不因过滤丢失。
/// </summary>
public sealed class LifePicker : ObservableObject
{
    private string search = string.Empty;
    private IReadOnlyList<LifePickRow>? filtered;
    private string[][]? keys;

    public required string Title { get; init; }
    public string Note { get; init; } = string.Empty;
    public bool HasNote => Note.Length > 0;

    /// <summary>这个菜单的全部配方。</summary>
    public required IReadOnlyList<LifePickRow> All { get; init; }

    /// <summary>按搜索词过滤后显示的配方；搜索框为空时是全部配方。</summary>
    public IReadOnlyList<LifePickRow> Items => filtered ?? All;

    /// <summary>设施没有读到任何配方时的说明。</summary>
    public string Empty { get; init; } = string.Empty;

    /// <summary>列表为空时显示的文字：没有配方，或搜索没有匹配。</summary>
    public string EmptyText => All.Count == 0 ? Empty : Items.Count == 0 ? Localization.UiText.Pick("没有匹配的配方", "沒有符合的配方") : string.Empty;
    public bool HasEmpty => Items.Count == 0;

    public string Search
    {
        get => search;
        set
        {
            if (!Set(ref search, value ?? string.Empty)) return;
            var term = Fold(search.Trim());
            if (term.Length == 0) filtered = null;
            else
            {
                keys ??= [.. All.Select(row => new[] { Fold(row.Name) }.Concat(row.MaterialNames.Select(Fold)).ToArray())];
                filtered = [.. All.Where((_, i) => keys[i].Any(key => key.Contains(term, StringComparison.OrdinalIgnoreCase)))];
            }
            Raise(nameof(Items)); Raise(nameof(EmptyText)); Raise(nameof(HasEmpty));
        }
    }

    // 全角 ASCII（U+FF01 到 U+FF5E）折成对应的半角字符，全角空格（U+3000）折成半角空格；大小写由比较时忽略。
    private const char FullWidthFirst = (char)0xFF01, FullWidthLast = (char)0xFF5E, FullWidthSpace = (char)0x3000;
    private const int FullWidthOffset = 0xFEE0;

    internal static string Fold(string text)
    {
        if (!text.Any(ch => ch is >= FullWidthFirst and <= FullWidthLast or FullWidthSpace)) return text;
        return string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                span[i] = ch is >= FullWidthFirst and <= FullWidthLast ? (char)(ch - FullWidthOffset) : ch == FullWidthSpace ? ' ' : ch;
            }
        });
    }
}

/// <summary>二级菜单的一项配方：状态、材料持有/需要、数量框与“加入队列”。</summary>
public sealed class LifePickRow : ObservableObject
{
    private string countText = "1";

    public required string Name { get; init; }
    /// <summary>游戏给出的配方名（加入队列用）；<see cref="Name"/> 另带配方版本。</summary>
    public required string Target { get; init; }
    public required string Kind { get; init; }
    public string? Facility { get; init; }
    public int? Variant { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool StatusProblem { get; init; }
    public string Materials { get; init; } = string.Empty;
    public bool HasMaterials => Materials.Length > 0;
    /// <summary>材料名（搜索用）：加工配方是缺少的材料，制作配方是全部材料。</summary>
    public IReadOnlyList<string> MaterialNames { get; init; } = [];
    public bool CanAdd { get; init; }
    public int MaximumCount { get; init; } = 1;
    public string CountLabel { get; init; } = string.Empty;
    public required ICommand AddCommand { get; init; }
    public string CountText { get => countText; set => Set(ref countText, value); }
}

/// <summary>按键对齐的就地同步：沿用已有的项（控件保留），按目标顺序移动、插入与删除。</summary>
internal static class LifeSync
{
    public static void Sync<T>(ObservableCollection<T> list, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < list.Count && ReferenceEquals(list[i], item)) continue;
            var at = -1;
            for (var j = i + 1; j < list.Count; j++) if (ReferenceEquals(list[j], item)) { at = j; break; }
            if (at >= 0) list.Move(at, i);
            else list.Insert(i, item);
        }
        while (list.Count > desired.Count) list.RemoveAt(list.Count - 1);
    }
}
