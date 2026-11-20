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
/// <see cref="Selectable"/> 为假时不能加入队列。
/// </summary>
public sealed record ScoreRow(string Id, string Title, string Tag, string Arranger, string Instrument, string Length,
    bool Playing, bool Selectable, string? InstrumentId = null)
{
    public string EnqueueIntent => "music.enqueue:" + Id;
    public bool HasTag => Tag.Length > 0;
}

public sealed record QueueRow(string Id, string Number, string Title, string Note, string Length, bool Current)
{
    public string RemoveIntent => "music.dequeue:" + Id;
}

/// <summary>“游戏内演奏”层里的一项功能及其状态；这些功能都依赖游戏内选曲演奏，状态跟随 <c>performance.play</c>。</summary>
public sealed record GameFeatureRow(string Name, string Status, bool Unavailable);

public sealed record MusicActions(bool CanResync, bool CanEnqueue, bool CanStart, bool CanNext, bool CanStop);

public sealed record MusicPageData(string Status, string Search, IReadOnlyList<PlaylistChip> Playlists,
    IReadOnlyList<ScoreRow> Scores, string QueueSummary, IReadOnlyList<QueueRow> Queue,
    IReadOnlyList<KeyValue> QueueSettings, MusicActions Actions);

public sealed class MusicPageModel : BackendPageModel<MusicPageData>
{
    /// <summary>乐谱表每页的行数；时长只为当前页查询。</summary>
    public const int PageSize = 30;

    /// <summary>切走后保留页面，回来时直接显示并重读一次。</summary>
    protected override bool Cacheable => true;

    private readonly ScorePager pager;
    private string? search;

    /// <param name="openMini">打开迷你窗；为空时标题区不提供入口。</param>
    public MusicPageModel(IDesktopBackend backend, Action? openMini = null) : base(backend, source => source.GetMusic())
    {
        pager = new ScorePager(backend, PageSize);
        pager.Update(Data.Scores);
        OpenMiniCommand = new RelayCommand(_ => openMini?.Invoke());
        HasMini = openMini is not null;
        PreviousPageCommand = new RelayCommand(_ => Move(-1));
        NextPageCommand = new RelayCommand(_ => Move(1));
        OpenGameCommand = new RelayCommand(_ => IsGameOpen = true);
        CloseGameCommand = new RelayCommand(_ => IsGameOpen = false);
        Activity = backend.GetActivity();
    }

    private bool isGameOpen;

    /// <summary>“游戏内演奏”二级层是否打开；层里集中放会让后端在游戏里执行的操作与其状态。</summary>
    public bool IsGameOpen
    {
        get => isGameOpen;
        set => Set(ref isGameOpen, value);
    }

    public System.Windows.Input.ICommand OpenGameCommand { get; }
    public System.Windows.Input.ICommand CloseGameCommand { get; }

    public ActivitySnapshot Activity { get; private set; }
    public IReadOnlyList<StageViewModel> Stages => MiniWindowModel.StagesOf(Activity);
    public bool ShowStages => Activity.OwnerModuleId is not null;
    public string ActivityLine => Activity.OwnerLine;
    public string CurrentTitle => Data.Queue.FirstOrDefault(row => row.Current)?.Title
        ?? UiText.Pick("没有正在演奏的乐谱", "沒有正在演奏的樂譜");
    public bool HasQueueSettings => Data.QueueSettings.Count > 0;
    public bool CanStop => Data.Actions.CanStop && !HasCapabilityNotice;

    /// <summary>层内的功能状态行；四项都依赖游戏内选曲演奏，状态取自 <c>performance.play</c>。</summary>
    public IReadOnlyList<GameFeatureRow> GameFeatures
    {
        get
        {
            var level = Backend.GetCapabilityLevel(CapabilityIds.PerformancePlay);
            var status = level switch
            {
                CapabilityLevel.Unavailable => UiText.Pick("不可用", "不可用"),
                CapabilityLevel.Available => UiText.Pick("可用", "可用"),
                CapabilityLevel.Degraded => UiText.Pick("降级可用", "降級可用"),
                _ => UiText.Pick("未知", "未知")
            };
            return new[]
            {
                UiText.Pick("游戏内选曲演奏", "遊戲內選曲演奏"), UiText.Pick("自动连续播放", "自動連續播放"),
                UiText.Pick("演奏前自动换乐器", "演奏前自動換樂器"), UiText.Pick("从游戏读取乐谱内容", "從遊戲讀取樂譜內容")
            }.Select(name => new GameFeatureRow(name, status, level == CapabilityLevel.Unavailable)).ToList();
        }
    }

    public System.Windows.Input.ICommand OpenMiniCommand { get; }
    public System.Windows.Input.ICommand PreviousPageCommand { get; }
    public System.Windows.Input.ICommand NextPageCommand { get; }
    public bool HasMini { get; }

    /// <summary>后端能力表说明该主操作不可用的原因；可用时为空。</summary>
    public string CapabilityNotice => Backend.GetCapabilityNotice(CapabilityIds.PerformancePlay) ?? string.Empty;
    public bool HasCapabilityNotice => CapabilityNotice.Length > 0;

    /// <summary>
    /// 开始演奏、下一首与停止会让后端在游戏里执行；<c>performance.play</c> 标为不可用时停用，
    /// 其余状态（可用、降级、未知、没有该编号、未连接）沿用页面数据。
    /// </summary>
    public bool CanStart => Data.Actions.CanStart && !HasCapabilityNotice;
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
        pager.Update(Data.Scores);
    }

    private void Move(int delta)
    {
        pager.Move(delta);
        foreach (var name in new[] { nameof(VisibleScores), nameof(HasPages), nameof(CanPreviousPage), nameof(CanNextPage), nameof(PageLabel) })
            Raise(name);
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
