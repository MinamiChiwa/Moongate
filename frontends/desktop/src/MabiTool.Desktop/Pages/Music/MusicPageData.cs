using Avalonia.Media;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Shell;

namespace MabiTool.Desktop.Pages.Music;

public sealed record PlaylistChip(string Id, string Name, string Note, string ColorHex, bool Selected)
{
    public string Intent => "music.playlist:" + Id;
    public IBrush Cover => Color.TryParse(ColorHex, out var color) ? new SolidColorBrush(color) : Brushes.Gray;
}

/// <summary>
/// 一张乐谱。标题是游戏原文；<see cref="Tag"/> 说明同名第几张或无法选取的原因，
/// <see cref="Selectable"/> 为假时不能加入队列，<see cref="Blocked"/> 写明原因（悬浮提示）；<see cref="Support"/> 是游戏连接器报告的可演奏性（玩家自制乐谱不能经连接器演奏）；
/// <see cref="Source"/> 是名称前来源标签的文字（游戏乐谱、自制乐谱、导入），<see cref="SourceKey"/> 是与界面语言无关的来源键
/// （<see cref="ScoreSources.Game"/>、<see cref="ScoreSources.Composed"/>、<see cref="ScoreSources.Imported"/>，其他提供方为提供方原文），来源筛选按它比较。
/// </summary>
public sealed record ScoreRow(string Id, string Title, string Tag, string Arranger, string Length,
    bool Playing, bool Selectable, ScoreSupportState Support = ScoreSupportState.Unknown, string? Blocked = null,
    string Source = "", string SourceKey = "")
{
    public string EnqueueIntent => "music.enqueue:" + Id;
    public bool HasTag => Tag.Length > 0;
    public bool HasSource => Source.Length > 0;
    public bool IsGameSource => SourceKey == ScoreSources.Game;
    public bool IsComposedSource => SourceKey == ScoreSources.Composed;
    public bool IsImportedSource => SourceKey == ScoreSources.Imported;
}

/// <summary>乐谱来源键：游戏列表提供的乐谱、背包抓包读到的玩家自制乐谱、MobiFolio 导入的乐谱。</summary>
public static class ScoreSources
{
    public const string Game = "game";
    public const string Composed = "composed";
    public const string Imported = "imported";

    /// <summary>来源标签与筛选的次序：游戏乐谱、自制乐谱、导入，其他来源排在最后。</summary>
    public static int Rank(string key) => key switch { Game => 0, Composed => 1, Imported => 2, _ => 3 };
}

/// <summary>
/// 演奏队列的一项。<see cref="Note"/> 是这一项的乐器名称（没有指定时为“游戏当前乐器”），<see cref="InstrumentId"/> 是乐器目录里的标识（为空表示不换乐器）；
/// <see cref="Playing"/> 为真表示这一项正在开始或演奏中，乐器不能再改。
/// </summary>
public sealed record QueueRow(string Id, string Number, string Title, string Note, string Length, bool Current,
    ScoreSupportState Support = ScoreSupportState.Unknown, string? InstrumentId = null, bool InstrumentLocked = false)
{
    public string RemoveIntent => "music.dequeue:" + Id;
}

/// <summary>队列面板上的一项：队列条目与它的乐器选择能否使用，<see cref="InstrumentHint"/> 是选择按钮的悬浮提示（停用时写明原因）。</summary>
public sealed record QueueEntry(QueueRow Row, bool CanPickInstrument, string InstrumentHint);

/// <summary>乐谱表上方的来源筛选项：<see cref="Key"/> 为空表示“全部”；<see cref="Text"/> 含当前张数。</summary>
public sealed record SourceFilterChip(string Key, string Text, bool Selected)
{
    public bool HasDot => Key.Length > 0;
    public bool IsGameSource => Key == ScoreSources.Game;
    public bool IsComposedSource => Key == ScoreSources.Composed;
    public bool IsImportedSource => Key == ScoreSources.Imported;
}

/// <summary>“游戏内演奏”层里的一项功能及其状态；这些功能都依赖游戏内选曲演奏，状态跟随 <c>performance.play</c>。</summary>
public sealed record GameFeatureRow(string Name, string Status, bool Unavailable);

public sealed record MusicActions(bool CanResync, bool CanEnqueue, bool CanStart, bool CanNext, bool CanStop);

/// <param name="PlayError">演奏会话最近一次的错误原因码（游戏拒绝、乐器不在背包等）；没有时为空。</param>
/// <param name="Lists">清单与文件夹的完整内容与编辑入口状态；后端没有提供时为空（等同没有清单，编辑停用）。</param>
public sealed record MusicPageData(string Status, string Search, IReadOnlyList<PlaylistChip> Playlists,
    IReadOnlyList<ScoreRow> Scores, string QueueSummary, IReadOnlyList<QueueRow> Queue,
    IReadOnlyList<KeyValue> QueueSettings, MusicActions Actions, string? PlayError = null, MusicListsView? Lists = null);

public sealed class MusicPageModel : BackendPageModel<MusicPageData>
{
    /// <summary>乐谱表每页的行数；时长只为当前页查询。</summary>
    public const int PageSize = 30;

    /// <summary>切走后保留页面，回来时直接显示并重读一次。</summary>
    protected override bool Cacheable => true;

    private readonly ScorePager pager;
    private string? search;
    private string sourceFilter = string.Empty;

    /// <param name="openMini">打开迷你窗；为空时标题区不提供入口。</param>
    public MusicPageModel(IDesktopBackend backend, Action? openMini = null) : base(backend, source => source.GetMusic())
    {
        pager = new ScorePager(backend, PageSize);
        pager.Update(FilteredScores());
        SourceFilterCommand = new RelayCommand(value => SourceFilter = value as string ?? string.Empty);
        Lists = new MusicListsModel(backend);
        Lists.Update(Data.Lists);
        OpenMiniCommand = new RelayCommand(_ => openMini?.Invoke());
        HasMini = openMini is not null;
        PreviousPageCommand = new RelayCommand(_ => Move(-1));
        NextPageCommand = new RelayCommand(_ => Move(1));
        OpenGameCommand = new RelayCommand(_ => IsGameOpen = true);
        CloseGameCommand = new RelayCommand(_ => IsGameOpen = false);
        SelectPlaylistCommand = new RelayCommand(value => { if (value is string id) Send("music.playlist", id); });
        OpenListsCommand = new RelayCommand(_ => IsListsOpen = true);
        CloseListsCommand = new RelayCommand(_ => IsListsOpen = false);
        Activity = backend.GetActivity();
    }

    private bool isGameOpen;

    /// <summary>“游戏内演奏”二级层是否打开；层里集中放会让后端在游戏里执行的操作与其状态。</summary>
    public bool IsGameOpen
    {
        get => isGameOpen;
        set
        {
            if (value) IsListsOpen = false;
            Set(ref isGameOpen, value);
        }
    }

    private bool isListsOpen;

    /// <summary>“管理清单”层是否打开：新建、重命名、排序、删除清单与文件夹；同一时刻只开一个层。</summary>
    public bool IsListsOpen
    {
        get => isListsOpen;
        set
        {
            if (value) IsGameOpen = false;
            Set(ref isListsOpen, value);
        }
    }

    public MusicListsModel Lists { get; }

    public System.Windows.Input.ICommand OpenGameCommand { get; }
    public System.Windows.Input.ICommand CloseGameCommand { get; }
    /// <summary>选择清单（清单栏同一个意图）：层里的清单标签用它，层打开时清单栏被层盖住。</summary>
    public System.Windows.Input.ICommand SelectPlaylistCommand { get; }
    public System.Windows.Input.ICommand OpenListsCommand { get; }
    public System.Windows.Input.ICommand CloseListsCommand { get; }

    public ActivitySnapshot Activity { get; private set; }
    public IReadOnlyList<StageViewModel> Stages => MiniWindowModel.StagesOf(Activity);
    public bool ShowStages => Activity.OwnerModuleId is not null;
    public string ActivityLine => Activity.OwnerLine;
    public string CurrentTitle => Data.Queue.FirstOrDefault(row => row.Current)?.Title
        ?? UiText.Pick("没有正在演奏的乐谱", "沒有正在演奏的樂譜");
    public bool HasQueueSettings => Data.QueueSettings.Count > 0;
    public bool CanStop => Data.Actions.CanStop && !HasCapabilityNotice;

    /// <summary>
    /// 层内的功能状态行：游戏内选曲演奏与自动连续播放跟随 <c>performance.play</c>，演奏前自动换乐器跟随 <c>performance.instrument</c>，
    /// 从游戏读取乐谱内容固定为不可用（命令行工具只提供乐谱标题，不含内容）。
    /// </summary>
    public IReadOnlyList<GameFeatureRow> GameFeatures
    {
        get
        {
            GameFeatureRow Row(string name, CapabilityLevel level) => new(name, level switch
            {
                CapabilityLevel.Unavailable => UiText.Pick("不可用", "不可用"),
                CapabilityLevel.Available => UiText.Pick("可用", "可用"),
                CapabilityLevel.Degraded => UiText.Pick("降级可用", "降級可用"),
                _ => UiText.Pick("未知", "未知")
            }, level == CapabilityLevel.Unavailable);
            var play = Backend.GetCapabilityLevel(CapabilityIds.PerformancePlay);
            var instrument = Backend.GetCapabilityLevel(CapabilityIds.PerformanceInstrument);
            return
            [
                Row(UiText.Pick("游戏内选曲演奏", "遊戲內選曲演奏"), play),
                Row(UiText.Pick("自动连续播放", "自動連續播放"), play),
                Row(UiText.Pick("演奏前自动换乐器", "演奏前自動換樂器"), instrument),
                Row(UiText.Pick("从游戏读取乐谱内容", "從遊戲讀取樂譜內容"), CapabilityLevel.Unavailable)
            ];
        }
    }

    // 队列里将要开始演奏的一项：标为当前的一首，没有时取第一首。
    private QueueRow? QueueHead => Data.Queue.FirstOrDefault(row => row.Current) ?? Data.Queue.FirstOrDefault();

    /// <summary>队列首项是玩家自制乐谱：游戏拒绝播放，“开始演奏”停用。可演奏性未知时不停用。</summary>
    public bool HeadUserComposed => QueueHead is { Support: ScoreSupportState.UserComposed };

    /// <summary>“开始演奏”停用的乐谱原因，作为按钮（含停用状态）的悬浮提示；没有时为空，不显示提示。</summary>
    public string? StartHint => HeadUserComposed ? ScoreSupportView.UserComposedTip : null;

    /// <summary>演奏会话最近一次错误的说明（原因码经共用映射，未收录的原样显示）；没有时为空，显示在当前曲目下方。</summary>
    public string PlayError => Data.PlayError is { Length: > 0 } code ? CodeText.TryReason(code, out var text) ? text : code : string.Empty;
    public bool HasPlayError => PlayError.Length > 0;

    public System.Windows.Input.ICommand OpenMiniCommand { get; }
    public System.Windows.Input.ICommand PreviousPageCommand { get; }
    public System.Windows.Input.ICommand NextPageCommand { get; }
    public bool HasMini { get; }

    /// <summary>后端能力表说明该主操作不可用的原因；可用时为空。</summary>
    public string CapabilityNotice => Backend.GetCapabilityNotice(CapabilityIds.PerformancePlay) ?? string.Empty;
    public bool HasCapabilityNotice => CapabilityNotice.Length > 0;

    /// <summary>
    /// 开始演奏、下一首与停止会让后端在游戏里执行；<c>performance.play</c> 标为不可用时停用，
    /// 其余状态（可用、降级、未知、没有该编号、未连接）沿用页面数据。队列首项是玩家自制乐谱时开始演奏另行停用。
    /// </summary>
    public bool CanStart => Data.Actions.CanStart && !HasCapabilityNotice && !HeadUserComposed;
    public bool CanNext => Data.Actions.CanNext && !HasCapabilityNotice;

    /// <summary>乐谱表当前页。</summary>
    public IReadOnlyList<ScoreRow> VisibleScores => pager.Visible;
    public bool HasPages => pager.HasPages;
    public bool CanPreviousPage => pager.CanPrevious;
    public bool CanNextPage => pager.CanNext;
    public string PageLabel => pager.Label;

    // 乐谱库、清单与演奏队列只在乐谱页、迷你窗、合奏页或演奏中浮窗元件显示时读取。
    protected override string? Surface => "music";

    protected override bool OtherStateChanged() => !Equals(Backend.GetActivity(), Activity);

    protected override void OnDataChanged()
    {
        Activity = Backend.GetActivity();
        pager.Update(FilteredScores());
        Lists.Update(Data.Lists);
    }

    private void Move(int delta)
    {
        pager.Move(delta);
        RaisePager();
    }

    private void RaisePager()
    {
        foreach (var name in new[] { nameof(VisibleScores), nameof(HasPages), nameof(CanPreviousPage), nameof(CanNextPage), nameof(PageLabel) })
            Raise(name);
    }

    // ---- 来源筛选 ----

    public System.Windows.Input.ICommand SourceFilterCommand { get; }

    /// <summary>
    /// 乐谱表的来源筛选（来源键，空为“全部”）：只在前端对后端已给出的乐谱（已按清单与搜索词筛过）再筛一次，不发送意图。
    /// 换筛选后回到第一页。
    /// </summary>
    public string SourceFilter
    {
        get => sourceFilter;
        set
        {
            if (!Set(ref sourceFilter, value)) return;
            pager.Update(FilteredScores());
            RaisePager();
            Raise(nameof(SourceFilters));
        }
    }

    private IReadOnlyList<ScoreRow> FilteredScores() =>
        sourceFilter.Length == 0 ? Data.Scores : [.. Data.Scores.Where(row => row.SourceKey == sourceFilter)];

    /// <summary>
    /// 筛选项：“全部”之后是当前乐谱里出现的来源（游戏乐谱、自制乐谱、导入、其他），各带张数；
    /// 已选的来源暂时没有乐谱（例如换了搜索词）时仍保留为 0 张，选择不丢失。
    /// </summary>
    public IReadOnlyList<SourceFilterChip> SourceFilters
    {
        get
        {
            var groups = Data.Scores.Where(row => row.SourceKey.Length > 0).GroupBy(row => row.SourceKey)
                .Select(group => (Key: group.Key, Label: group.First().Source, Count: group.Count())).ToList();
            if (sourceFilter.Length > 0 && groups.All(group => group.Key != sourceFilter))
                groups.Add((sourceFilter, SourceName(sourceFilter), 0));
            return
            [
                new(string.Empty, UiText.Pick($"全部 {Data.Scores.Count}", $"全部 {Data.Scores.Count}"), sourceFilter.Length == 0),
                .. groups.OrderBy(group => ScoreSources.Rank(group.Key)).ThenBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new SourceFilterChip(group.Key, $"{group.Label} {group.Count}", group.Key == sourceFilter))
            ];
        }
    }

    /// <summary>有来源可选时显示筛选（“全部”之外至少一项）。</summary>
    public bool HasSourceFilters => SourceFilters.Count > 1;

    private static string SourceName(string key) => key switch
    {
        ScoreSources.Game => UiText.Pick("游戏乐谱", "遊戲樂譜"),
        ScoreSources.Composed => UiText.Pick("自制乐谱", "自製樂譜"),
        ScoreSources.Imported => UiText.Pick("导入", "匯入"),
        _ => key
    };

    // ---- 演奏队列的乐器 ----

    /// <summary>换乐器（<c>performance.instrument</c>）不可用的原因；可用时为空。</summary>
    public string? InstrumentNotice => Backend.GetCapabilityNotice(CapabilityIds.PerformanceInstrument);

    /// <summary>
    /// 队列面板的各项：乐器选择在 <c>performance.instrument</c> 不可用时停用并写明原因；演奏进行中（准备、待命、执行、演奏阶段）
    /// 全部停用——后端在执行与演奏阶段拒绝队列编辑，其余阶段的编辑会把队列重置到开头。
    /// </summary>
    public IReadOnlyList<QueueEntry> QueueEntries
    {
        get
        {
            var notice = InstrumentNotice;
            var hint = UiText.Pick("轮到这首时先换成所选乐器再演奏；选“游戏当前乐器”时不换", "輪到這首時先換成所選樂器再演奏；選「遊戲目前樂器」時不換");
            var locked = UiText.Pick("演奏进行中不能改乐器，停止后再改", "演奏進行中不能改樂器，停止後再改");
            return [.. Data.Queue.Select(row => row.InstrumentLocked ? new QueueEntry(row, false, locked)
                : notice is not null ? new QueueEntry(row, false, notice) : new QueueEntry(row, true, hint))];
        }
    }

    /// <summary>队列条目可选的乐器：乐器目录（与清单编辑同一份）。</summary>
    public IReadOnlyList<InstrumentChoice> Instruments => Lists.InstrumentChoices;

    /// <summary>
    /// 把队列里一项的乐器改为 <paramref name="instrumentId"/>（为空表示游戏当前乐器，不换）：发出 <c>music.instrument</c>，
    /// 参数为“条目 ID 换行 乐器 ID”。选择停用的一项不发送。
    /// </summary>
    public void SetQueueInstrument(string itemId, string? instrumentId)
    {
        if (QueueEntries.FirstOrDefault(entry => entry.Row.Id == itemId) is not { CanPickInstrument: true } entry) return;
        if ((entry.Row.InstrumentId ?? string.Empty) == (instrumentId ?? string.Empty)) return;
        Send("music.instrument", itemId + "\n" + (instrumentId ?? string.Empty));
    }

    /// <summary>搜索词由后端按标题、编曲者和别名匹配；这里只把输入交给后端。</summary>
    public string Search
    {
        get => search ?? Data.Search;
        set
        {
            if (value == Search) return;
            search = value;
            Send("music.search", value);
        }
    }

    public bool NoScores => Data.Scores.Count == 0;
    public bool NoQueue => Data.Queue.Count == 0;
}
