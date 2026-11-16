using System.Windows.Input;
using Avalonia.Media;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Shell;

/// <summary>迷你窗的一张乐谱磁贴。标题是游戏原文；不能选的磁贴显示原因。</summary>
public sealed record MiniScoreTile(ScoreRow Row, bool Selected)
{
    public string Id => Row.Id;
    public string Title => Row.Title;
    public string Meta => $"{Row.Instrument} · {Row.Length}";
    public string Tag => Row.Tag;
    public bool HasTag => Row.Tag.Length > 0;
    public bool Selectable => Row.Selectable;
    public bool Playing => Row.Playing;
    public string? InstrumentId => Row.InstrumentId;
}

/// <summary>
/// 迷你窗：读与乐谱库页相同的后端状态（<see cref="IDesktopBackend.GetMusic"/>、<see cref="IDesktopBackend.GetActivity"/>），
/// 意图沿用乐谱库页的 music.playlist、music.enqueue、music.start、music.next、music.stop 与紧急停止。
/// 所选乐谱只是迷你窗里的显示选择，不交给后端。
/// </summary>
public sealed class MiniWindowModel : BackendPageModel<MusicPageData>
{
    /// <summary>磁贴每页的张数；时长只为当前页查询。</summary>
    public const int PageSize = 24;

    private readonly ScorePager pager;
    private string? selectedId;

    public MiniWindowModel(IDesktopBackend backend, Action? showMain = null) : base(backend, source => source.GetMusic())
    {
        pager = new ScorePager(backend, PageSize);
        pager.Update(Data.Scores);
        Activity = backend.GetActivity();
        Binding = backend.GetGameBinding();
        SelectCommand = new RelayCommand(parameter =>
        {
            // 游戏无法选取的乐谱不能选；按钮本身也停用。
            if (parameter is string id && Data.Scores.FirstOrDefault(row => row.Id == id) is { Selectable: true })
            {
                selectedId = id;
                RaiseAll();
            }
        });
        PreviousPageCommand = new RelayCommand(_ => { pager.Move(-1); RaiseAll(); });
        NextPageCommand = new RelayCommand(_ => { pager.Move(1); RaiseAll(); });
        EmergencyStopCommand = new RelayCommand(_ => _ = backend.RequestEmergencyStopAsync(CancellationToken.None));
        ShowMainCommand = new RelayCommand(_ => showMain?.Invoke());
        UiText.LanguageChanged += OnLanguageChanged;
        UiTheme.Changed += OnThemeChanged;
        IconRegistry.Changed += OnPackChanged;
    }

    public ICommand SelectCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand EmergencyStopCommand { get; }
    public ICommand ShowMainCommand { get; }

    public ActivitySnapshot Activity { get; private set; }
    public BindingView Binding { get; private set; }

    /// <summary>后端能力表说明该主操作不可用的原因；可用时为空。</summary>
    public string CapabilityNotice => Backend.GetCapabilityNotice(CapabilityIds.PerformancePlay) ?? string.Empty;
    public bool HasCapabilityNotice => CapabilityNotice.Length > 0;

    /// <summary>
    /// 开始演奏、下一首与停止会让后端在游戏里执行；<c>performance.play</c> 标为不可用时停用，
    /// 其余状态（可用、降级、未知、没有该编号、未连接）沿用页面数据。
    /// </summary>
    public bool CanStart => Data.Actions.CanStart && !HasCapabilityNotice;
    public bool CanNext => Data.Actions.CanNext && !HasCapabilityNotice;
    public bool CanStop => Data.Actions.CanStop && !HasCapabilityNotice;

    /// <summary>所选清单的名称；没有选清单时是“全部乐谱”。</summary>
    public string ListName => Data.Playlists.FirstOrDefault(item => item.Selected)?.Name ?? UiText.Pick("全部乐谱", "全部樂譜");
    public string BindingLine => $"{Binding.CharacterName}，{Binding.Status}";

    public IReadOnlyList<MiniScoreTile> Tiles => pager.Visible.Select(row => new MiniScoreTile(row, row.Id == selectedId)).ToList();
    public bool NoScores => Data.Scores.Count == 0;
    public bool HasPages => pager.HasPages;
    public bool CanPreviousPage => pager.CanPrevious;
    public bool CanNextPage => pager.CanNext;
    public string PageLabel => pager.Label;

    private ScoreRow? Selected => Data.Scores.FirstOrDefault(row => row.Id == selectedId);
    public bool HasSelection => Selected is not null;
    public string SelectedLine => Selected is { } row ? UiText.Pick($"已选：{row.Title}", $"已選：{row.Title}") : string.Empty;
    public string EnqueueIntent => "music.enqueue:" + selectedId;
    public bool CanEnqueue => Data.Actions.CanEnqueue && Selected is { Selectable: true };

    private QueueRow? Current => Data.Queue.FirstOrDefault(row => row.Current);

    /// <summary>当前曲目：队列里标为当前的一首；没有时说明队列状态。</summary>
    public string CurrentTitle => Current?.Title ?? UiText.Pick("没有正在演奏的乐谱", "沒有正在演奏的樂譜");
    public string QueueSummary => Data.QueueSummary;

    public string NextLine
    {
        get
        {
            var index = Current is { } current ? Data.Queue.ToList().IndexOf(current) : -1;
            var next = index >= 0 && index + 1 < Data.Queue.Count ? Data.Queue[index + 1] : null;
            return next is null
                ? UiText.Pick("下一首：没有", "下一首：沒有")
                : UiText.Pick($"下一首：{next.Title}", $"下一首：{next.Title}");
        }
    }

    /// <summary>受理、送出、生效三段，与主窗口活动胶囊相同；没有模块持有输入时显示后端的说明。</summary>
    public IReadOnlyList<StageViewModel> Stages => StagesOf(Activity);

    internal static IReadOnlyList<StageViewModel> StagesOf(ActivitySnapshot activity) =>
    [
        new(new("已受理", "已受理"), new("尚未受理", "尚未受理"), activity.Accepted),
        new(new("已送出", "已送出"), new("尚未送出", "尚未送出"), activity.Dispatched),
        new(new("已观测生效", "已觀測生效"), new("尚未观测", "尚未觀測"), activity.Observed)
    ];
    public bool ShowStages => Activity.OwnerModuleId is not null;
    public string ActivityLine => Activity.OwnerLine;

    public IImage? BackdropImage => IconRegistry.Backdrop("music-library")?.Image;
    public IBrush? BackdropScrim => UiTheme.BackdropScrim(IconRegistry.Backdrop("music-library"));
    public bool HasBackdrop => IconRegistry.Backdrop("music-library") is not null;

    public override void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        UiTheme.Changed -= OnThemeChanged;
        IconRegistry.Changed -= OnPackChanged;
        base.Dispose();
    }

    protected override void OnDataChanged()
    {
        Activity = Backend.GetActivity();
        Binding = Backend.GetGameBinding();
        // 所选乐谱不在新的乐谱表里（换了清单或搜索）时取消选择。
        if (Selected is not { Selectable: true }) selectedId = null;
        pager.Update(Data.Scores);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Reload();

    private void OnThemeChanged(object? sender, EventArgs e) => Raise(nameof(BackdropScrim));

    private void OnPackChanged(object? sender, EventArgs e)
    {
        Raise(nameof(BackdropImage));
        Raise(nameof(BackdropScrim));
        Raise(nameof(HasBackdrop));
    }

    private void RaiseAll() => Raise(string.Empty);
}
