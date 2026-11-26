using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>
/// “生活”页模型：四层卡片（加工设施、制作、采集、总队列）与专注钓鱼卡片。数据来自后端的“生活”快照（<see cref="IDesktopBackend.GetLife"/>），
/// 页面显示时读取一次（进入页面、加工剩余时间到点、用户点刷新），任务执行由宿主的总队列进行，这里只排版、发出队列编辑与命令。
/// 刷新时各卡片的项目按名称或任务标识就地更新，不重建控件；采集分组默认折叠、展开时才生成行，二级菜单打开时才生成。
/// 开始或继续前弹出确认框写明逐项花费与整队上限，“本次运行期间不再询问”见 <see cref="LifeConfirmPolicy"/>。
/// </summary>
public sealed class LifePageModel : ObservableObject, IDisposable, IPageLifecycle
{
    public const string Surface = "life";
    // 数量上限：加工的项数、制作次数（游戏没有给出上限时）、采集的目标数量（与后端相同）。
    private const int MaximumRounds = 50, DefaultMaximumCount = 999, MaximumGatherCount = 500, ExtraScheduledDots = 6;
    private static readonly TimeSpan DueRefreshGap = TimeSpan.FromSeconds(10);
    private static string? notifiedCompletion;
    private static readonly HashSet<string> autoTried = new(StringComparer.Ordinal);
    // 采集分组的展开状态：本次运行内保持，默认折叠。
    private static readonly HashSet<string> expandedSkills = new(StringComparer.Ordinal);
    // 目录以 invalid_count 拒绝制作时给出的次数上限，按配方名记住。
    private static readonly Dictionary<string, int> craftLimits = new(StringComparer.Ordinal);

    /// <summary>测试用：清掉本次运行里记下的完成提示、自动领取尝试、展开状态与次数上限。</summary>
    internal static void ResetAnnouncements()
    {
        notifiedCompletion = null;
        autoTried.Clear();
        expandedSkills.Clear();
        craftLimits.Clear();
    }

    private enum ConfirmKind { None, Start, Add, Fishing }

    private sealed record PendingAdd(LifeTaskAddRequest Request, string Title);

    private readonly IDesktopBackend backend;
    private readonly LifeCountdown countdown;
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, LifeFacilityTile> facilityTiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifeCraftTile> craftTiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifeSkillGroup> skillGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifeTaskRow> taskRows = new(StringComparer.Ordinal);
    private LifeSnapshot? snapshot;
    private bool busy, disposed;
    private string message = string.Empty;
    private bool messageFailed;
    private ConfirmKind confirm;
    private PendingAdd? pendingAdd;
    private int? pendingFishing;
    private bool confirmRemember, fishingRunning;
    private LifePicker? picker;
    private long lastDueRefresh = long.MinValue;

    public LifePageModel(IDesktopBackend backend) : this(backend, null) { }

    /// <param name="now">倒计时用的当前时刻（UTC）；测试注入替身。</param>
    internal LifePageModel(IDesktopBackend backend, Func<DateTimeOffset>? now)
    {
        this.backend = backend;
        countdown = new(now);
        snapshot = backend.GetLife();
        Fishing = new(backend, FishingStartBlocked);
        Fishing.StartRequested += OnFishingStart;
        fishingRunning = Fishing.IsRunning;
        countdown.Update(snapshot?.Queue?.Works ?? [], snapshot?.Works?.ReadAt);
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync("all"));
        StartCommand = new RelayCommand(_ => RequestStart());
        PauseCommand = new RelayCommand(_ => _ = SendAsync(backend.CommandLifeQueueAsync(new("pause"), CancellationToken.None)));
        StopCurrentCommand = new RelayCommand(_ => _ = SendAsync(backend.CommandLifeQueueAsync(new("stop-current"), CancellationToken.None)));
        ClearFinishedCommand = new RelayCommand(_ => _ = SendAsync(backend.CommandLifeQueueAsync(new("clear-finished"), CancellationToken.None)));
        ConfirmCommand = new RelayCommand(_ => Confirm());
        CancelCommand = new RelayCommand(_ => { confirm = ConfirmKind.None; pendingAdd = null; pendingFishing = null; confirmRemember = false; Raise(string.Empty); });
        ClosePickerCommand = new RelayCommand(_ => ClosePicker());
        Build();
        backend.Changed += OnBackendChanged;
        UiText.LanguageChanged += OnLanguageChanged;
        tick.Tick += OnTick;
        tick.Start();
        backend.SetSurfaceVisible(Surface, true);
        _ = RefreshAsync("all");
    }

    // 页面外框在切走时释放本页，回来时重新建立并读取一次（展开状态与次数上限保存在本次运行内）。
    bool IPageLifecycle.Cacheable => false;
    void IPageLifecycle.Activate() { }
    void IPageLifecycle.Deactivate() { }

    /// <summary>专注钓鱼卡片（原地专注钓鱼）。</summary>
    public LifeFishingPanel Fishing { get; }

    /// <summary>采集物“钓鱼请使用专注钓鱼”的点按：视图把专注钓鱼卡片滚入视野。</summary>
    public event EventHandler? FishingFocusRequested;

    public ICommand RefreshCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCurrentCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClosePickerCommand { get; }

    public ObservableCollection<LifeFacilityTile> Facilities { get; } = [];
    public ObservableCollection<LifeCraftTile> CraftGroups { get; } = [];
    public ObservableCollection<LifeSkillGroup> SkillGroups { get; } = [];
    public ObservableCollection<LifeTaskRow> Tasks { get; } = [];

    // ───────────────────────── 文字 ─────────────────────────

    public string Title => Pick("生活", "生活");
    public string RefreshText => Pick("刷新", "重新整理");
    public string FacilitiesTitle => Pick("加工设施", "加工設施");
    public string CraftTitle => Pick("制作", "製作");
    public string GatherTitle => Pick("采集", "採集");
    public string QueueTitle => Pick("总队列", "總佇列");
    public string AutoCollectText => Pick("到点自动领取", "到點自動領取");
    public string AutoCollectTip => Pick("加工完成后自动领取该设施（会移动角色，不花费精靈之翼；只在本次运行内有效）", "加工完成後自動領取該設施（會移動角色，不花費精靈之翼；只在本次執行內有效）");
    public string ConfirmOkText => confirm == ConfirmKind.Add ? Pick("确认加入", "確認加入") : Pick("确认执行", "確認執行");
    public string ConfirmCancelText => Pick("取消", "取消");
    public string RememberText => Pick("本次运行期间不再询问", "本次執行期間不再詢問");
    public string PauseText => Pick("暂停", "暫停");
    public string StopCurrentText => Pick("停止当前", "停止目前");
    public string ClearFinishedText => Pick("清空已完成", "清空已完成");
    public string AddText => Pick("加入队列", "加入佇列");
    public string CloseText => Pick("关闭", "關閉");
    public string PickerSearchHint => Pick("搜索配方或材料", "搜尋配方或材料");
    public string CraftNote => Pick("点按设施选择配方；制作执行尚未经实机确认", "點按設施選擇配方；製作執行尚未經實機確認");
    public string GatherCountLabel => Pick("个", "個");
    public string DragTip => Pick("拖动调整顺序", "拖動調整順序");
    public string MinusTip => Pick("减少数量", "減少數量");
    public string PlusTip => Pick("增加数量", "增加數量");
    public string UpTip => Pick("上移", "上移");
    public string DownTip => Pick("下移", "下移");
    public string RemoveTip => Pick("删除", "刪除");
    public string LegendDone => Pick("已完成（点按领取）", "已完成（點按領取）");
    public string LegendWorking => Pick("加工中", "加工中");
    public string LegendScheduled => Pick("已安排待执行", "已安排待執行");
    public string LegendEmpty => Pick("空槽（点按安排）", "空槽（點按安排）");

    public string StatusLine
    {
        get
        {
            if (!backend.GetGameBinding().Connected) return Pick("未连接后端", "未連接後端");
            if (snapshot is null) return Pick("未读取", "未讀取");
            return snapshot.Connector.KnownValue == GameConnectorStatus.Connected
                ? Pick("游戏连接器已连接", "遊戲連接器已連接") : CodeText.Reason(snapshot.ConnectorReason);
        }
    }

    /// <summary>此刻队列不能开始的原因（连接器、活动锁占用、专注钓鱼、缺少指令）；没有时为空。</summary>
    public string Blocked
    {
        get
        {
            var notes = new List<string>();
            if (Fishing.IsRunning)
                notes.Add(Pick("专注钓鱼进行中，队列暂时不能开始或继续", "專注釣魚進行中，佇列暫時不能開始或繼續"));
            if (snapshot is not null && snapshot.Connector.KnownValue == GameConnectorStatus.Connected)
            {
                if (snapshot.Occupied is { Life: false } other)
                    notes.Add(Pick($"游戏活动正被「{LifeText.Owner(other.OwnerId)}」占用，暂时不能执行", $"遊戲活動正被「{LifeText.Owner(other.OwnerId)}」佔用，暫時不能執行"));
                foreach (var (name, commands) in new[] { (GatherTitle, new[] { "get_gatherable_items", "execute_gathering" }),
                             (Pick("加工", "加工"), ["get_alterable_items", "get_altering_works", "execute_altering", "complete_altering_work"]),
                             (CraftTitle, ["get_craftable_items", "execute_crafting"]) })
                    if (snapshot.Commands.FirstOrDefault(x => commands.Contains(x.Command) && !x.Available) is { } missing)
                        notes.Add(Pick($"{name}不可用：", $"{name}不可用：") + CodeText.Reason(missing.Reason));
            }
            return string.Join("；", notes);
        }
    }

    public bool HasBlocked => Blocked.Length > 0;

    public string ConnectorHint =>
        snapshot is { } current && current.Connector.KnownValue != GameConnectorStatus.Connected
            ? Pick("连接器需要在设置页启用并重启游戏后才能使用生活功能。", "連接器需要在設定頁啟用並重啟遊戲後才能使用生活功能。") : string.Empty;

    public bool HasConnectorHint => ConnectorHint.Length > 0;

    public string Message => message;
    public bool HasMessage => message.Length > 0;
    public bool MessageFailed => messageFailed;

    // ───────────────────────── 状态 ─────────────────────────

    private bool Connected => snapshot?.Connector.KnownValue == GameConnectorStatus.Connected;
    private bool Executing => LifeQueueLogic.Executing(snapshot);

    /// <summary>队列可以开始或继续：连接器已连接、没有在执行、活动锁没有被其他功能占用、专注钓鱼没有进行、有待执行的任务。</summary>
    public bool CanStart => Connected && !Executing && !Fishing.IsRunning && (snapshot?.Occupied is null or { Life: true }) && !busy
                            && LifeQueueLogic.Pending(snapshot).Count > 0;

    public string StartText => snapshot?.Tasks?.State.KnownValue == LifeQueueState.Paused ? Pick("继续", "繼續") : Pick("开始", "開始");
    public bool CanPause => snapshot?.Tasks is { State.KnownValue: LifeQueueState.Running } queue && queue.PauseCode != "life.pauseRequested";
    public bool CanStopCurrent => Executing;
    public bool CanClearFinished => snapshot?.Tasks?.Tasks.Any(x => x.State.KnownValue is LifeTaskState.Completed or LifeTaskState.Failed or LifeTaskState.Stopped) == true;
    public bool IsRunning => Executing;
    public bool CanRead => !busy && !Executing;

    /// <summary>领取会移动角色：连接器已连接、没有其他功能占用、专注钓鱼没有进行时可用（队列执行中排到当前任务之后）。</summary>
    private bool CanCollect => Connected && !Fishing.IsRunning && (snapshot?.Occupied is null or { Life: true })
                               && snapshot?.Commands.FirstOrDefault(x => x.Command == "complete_altering_work")?.Available == true;

    /// <summary>加入队列需要后端快照（队列在宿主）；能否执行到开始时再核对。</summary>
    private bool CanQueue => snapshot?.Tasks is not null;

    public bool AutoCollect
    {
        get => LifePreference.AutoCollect;
        set { LifePreference.SetAutoCollect(value); Raise(); if (value) AutoCollectNow(); }
    }

    public string FacilitiesStatus => Section(snapshot?.Works, snapshot?.Queue?.Works.Count);
    public string CraftStatus => snapshot is { CraftingUnlocked: false, Craft.State.RawValue: "loaded" }
        ? Pick("制作系统尚未解锁", "製作系統尚未解鎖") : Section(snapshot?.Craft, snapshot?.CraftRecipes.Count);
    public string GatherStatus => Section(snapshot?.Gather, snapshot?.GatherItems.Count);

    private static string Section(LifeSectionStatus? status, int? count)
    {
        if (status is null) return Pick("未读取", "未讀取");
        return status.State.KnownValue switch
        {
            LifeReadState.Loaded => Pick($"{status.ReadAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} 读取 · {count ?? 0} 项",
                $"{status.ReadAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} 讀取 · {count ?? 0} 項"),
            LifeReadState.Failed => Pick("未读取：", "未讀取：") + LifeText.Reason(status.Reason),
            _ => Pick("未读取", "未讀取")
        };
    }

    public string FacilitiesEmpty => snapshot?.Alter.State.KnownValue == LifeReadState.Loaded && Facilities.Count == 0 ? Pick("没有加工设施的数据", "沒有加工設施的資料") : string.Empty;
    public string CraftEmpty => snapshot?.Craft.State.KnownValue == LifeReadState.Loaded && CraftGroups.Count == 0 ? Pick("没有制作配方", "沒有製作配方") : string.Empty;
    public string GatherEmpty => snapshot?.Gather.State.KnownValue == LifeReadState.Loaded && SkillGroups.Count == 0 ? Pick("没有可采集的物品", "沒有可採集的物品") : string.Empty;
    public string QueueEmpty => Tasks.Count == 0 ? Pick("队列是空的：在上方的设施、制作或采集里加入任务", "佇列是空的：在上方的設施、製作或採集裡加入任務") : string.Empty;
    public bool HasFacilitiesEmpty => FacilitiesEmpty.Length > 0;
    public bool HasCraftEmpty => CraftEmpty.Length > 0;
    public bool HasGatherEmpty => GatherEmpty.Length > 0;
    public bool HasQueueEmpty => QueueEmpty.Length > 0;

    public string QueueState => LifeQueueLogic.StateText(snapshot);
    public bool QueueRunning => snapshot?.Tasks?.State.KnownValue == LifeQueueState.Running;
    public bool QueuePaused => snapshot?.Tasks?.State.KnownValue == LifeQueueState.Paused;
    public string QueuePause => LifeQueueLogic.PauseText(snapshot);
    public bool HasQueuePause => QueuePause.Length > 0;

    /// <summary>执行中的花费：已确认的上限与按目录单价累计的预计花费。</summary>
    public string CostLine => snapshot?.Tasks is { ApprovedCost: { } approved } queue && Executing
        ? Pick($"已确认上限 {approved} {queue.CostCurrency} · 预计已花 {queue.EstimatedSpent ?? 0}", $"已確認上限 {approved} {queue.CostCurrency} · 預計已花 {queue.EstimatedSpent ?? 0}")
        : string.Empty;
    public bool HasCostLine => CostLine.Length > 0;

    // ───────────────────────── 确认框与二级菜单 ─────────────────────────

    public bool ConfirmOpen => confirm != ConfirmKind.None;
    public bool ConfirmRemember { get => confirmRemember; set => Set(ref confirmRemember, value); }

    public string ConfirmTitle => confirm switch
    {
        ConfirmKind.Fishing => Pick("确认开始专注钓鱼", "確認開始專注釣魚"),
        ConfirmKind.Add => Pick("确认加入队列", "確認加入佇列"),
        ConfirmKind.Start => StartText == Pick("继续", "繼續") ? Pick("确认继续队列", "確認繼續佇列") : Pick("确认开始队列", "確認開始佇列"),
        _ => string.Empty
    };

    public IReadOnlyList<string> ConfirmLines => confirm switch
    {
        ConfirmKind.Fishing => FishingConfirmText(pendingFishing),
        ConfirmKind.Add when pendingAdd is { } add && snapshot is { } current => LifeQueueLogic.AddLines(current, add.Request.Kind, add.Title, add.Request.Count),
        ConfirmKind.Start when snapshot is { } current => LifeQueueLogic.StartLines(current),
        _ => []
    };

    public LifePicker? Picker => picker;
    public bool PickerOpen => picker is not null;

    private static IReadOnlyList<string> FishingConfirmText(int? seconds) =>
    [
        Pick("专注钓鱼（原地）：目标时长 ", "專注釣魚（原地）：目標時長 ") + (seconds is { } total ? LifeText.Duration(total) : Pick("直到你停止", "直到你停止")),
        Pick("请先把角色停在可钓鱼位置。", "請先把角色停在可釣魚位置。"),
        Pick("运行期间工具会在后台向游戏窗口发送 W/S 与空格按键和点按。", "執行期間工具會在背景向遊戲視窗傳送 W/S 與空白鍵按鍵和點按。"),
        Pick("运行期间合奏、演奏、染色以及本页的总队列不能同时进行。", "執行期間合奏、演奏、染色以及本頁的總佇列不能同時進行。"),
        Pick("按 F9（默认的紧急停止快捷键）可随时立即停止。", "按 F9（預設的緊急停止快速鍵）可隨時立即停止。")
    ];

    private void ClosePicker()
    {
        if (picker is null) return;
        picker = null;
        Raise(nameof(Picker)); Raise(nameof(PickerOpen));
    }

    private void OpenPicker(LifePicker value)
    {
        picker = value;
        Raise(nameof(Picker)); Raise(nameof(PickerOpen));
    }

    /// <summary>加工空槽的配方菜单：只列归属为该设施的加工配方。</summary>
    internal void OpenArrange(string facility)
    {
        if (snapshot is null) return;
        var rows = snapshot.AlterRecipes.Where(x => x.Group == facility).Select(recipe =>
        {
            var ok = recipe.Alterable == true;
            var status = ok ? Pick("可加工", "可加工") + (recipe.ProducedPerWork is { } made ? Pick($" · 每次产出 {made}", $" · 每次產出 {made}") : string.Empty)
                : recipe.Alterable is null ? Pick("可否加工：未读取", "可否加工：未讀取")
                : Pick("不可加工：", "不可加工：") + (recipe.Reason is { Length: > 0 } reason ? LifeText.GameReason(reason) : Pick("未写明原因", "未寫明原因"));
            return new LifePickRow
            {
                Name = recipe.Name, Target = recipe.Name, Kind = "alter", Facility = facility, Status = status, StatusProblem = !ok,
                Materials = recipe.Missing.Count == 0 ? string.Empty : Pick("缺：", "缺：") + string.Join("、", recipe.Missing.Select(Material)),
                MaterialNames = [.. recipe.Missing.Select(x => x.Name)],
                CanAdd = ok && CanQueue, MaximumCount = MaximumRounds, CountLabel = Pick("项", "項"), AddCommand = new RelayCommand(row => Add(row as LifePickRow))
            };
        }).ToList();
        OpenPicker(new()
        {
            Title = facility + Pick(" · 安排加工", " · 安排加工"), All = rows,
            Note = Pick("选择配方与项数加入总队列，按顺序执行（会移动角色）。", "選擇配方與項數加入總佇列，按順序執行（會移動角色）。"),
            Empty = Pick("这个设施没有读到加工配方", "這個設施沒有讀到加工配方")
        });
    }

    /// <summary>制作设施的配方菜单：成功率、大成功率、产出、材料持有/需要与次数框。</summary>
    internal void OpenCraft(string? facility)
    {
        if (snapshot is null) return;
        var rows = snapshot.CraftRecipes.Where(x => x.Group == facility).Select(recipe =>
        {
            var ok = recipe.Craftable == true;
            var parts = new List<string>();
            if (recipe.Craftable == false) parts.Add(Pick("不可制作", "不可製作") + (recipe.Reason is { Length: > 0 } reason ? "：" + LifeText.GameReason(reason) : string.Empty));
            else if (recipe.Craftable is null) parts.Add(Pick("可否制作：未读取", "可否製作：未讀取"));
            parts.Add(Pick("成功率 ", "成功率 ") + (recipe.SuccessRate is { } rate ? rate.ToString("0.#", CultureInfo.InvariantCulture) + "%" : Pick("未读取", "未讀取")));
            if (recipe.CriticalSuccessRate is { } critical) parts.Add(Pick("大成功 ", "大成功 ") + critical.ToString("0.#", CultureInfo.InvariantCulture) + "%");
            if (recipe.ProducedPerCraft is { } made) parts.Add(Pick($"每次产出 {made}", $"每次產出 {made}"));
            var limit = craftLimits.GetValueOrDefault(recipe.Name, DefaultMaximumCount);
            if (craftLimits.ContainsKey(recipe.Name)) parts.Add(Pick($"最多 {limit} 次", $"最多 {limit} 次"));
            return new LifePickRow
            {
                Name = recipe.Name + LifeQueueLogic.Variant(recipe.Variant), Target = recipe.Name, Kind = "craft", Facility = facility, Variant = recipe.Variant,
                Status = string.Join(" · ", parts), StatusProblem = !ok,
                Materials = recipe.Ingredients.Count == 0 ? string.Empty : Pick("材料：", "材料：") + string.Join("、", recipe.Ingredients.Select(Material)),
                MaterialNames = [.. recipe.Ingredients.Select(x => x.Name)],
                CanAdd = ok && CanQueue, MaximumCount = limit, CountLabel = Pick("次", "次"), AddCommand = new RelayCommand(row => Add(row as LifePickRow))
            };
        }).ToList();
        OpenPicker(new()
        {
            Title = (facility ?? Pick("其他", "其他")) + Pick(" · 制作", " · 製作"), All = rows,
            Note = Pick("制作执行尚未经实机确认。选择配方与次数加入总队列。", "製作執行尚未經實機確認。選擇配方與次數加入總佇列。"),
            Empty = Pick("这个设施没有读到制作配方", "這個設施沒有讀到製作配方")
        });
    }

    private static string Material(LifeMaterial item) =>
        $"{item.Name} {(item.Owned is { } owned ? owned.ToString(CultureInfo.InvariantCulture) : Pick("未读取", "未讀取"))}/{(item.Required is { } required ? required.ToString(CultureInfo.InvariantCulture) : Pick("未读取", "未讀取"))}";

    // ───────────────────────── 读取、加入与命令 ─────────────────────────

    private async Task RefreshAsync(string section)
    {
        if (busy || disposed || Executing) return;
        busy = true; Raise(string.Empty);
        try
        {
            var reply = await backend.RefreshLifeAsync(section, null, CancellationToken.None);
            if (!reply.Ok && reply.Code != "life.actionRunning") Fail(reply);
            else if (reply.Ok) message = string.Empty;
        }
        finally
        {
            busy = false;
            if (!disposed) Apply(backend.GetLife());
        }
    }

    private async Task SendAsync(Task<BackendReply> request)
    {
        var reply = await request;
        if (!reply.Ok) Fail(reply);
        else if (messageFailed) { message = string.Empty; messageFailed = false; }
        if (!disposed) Apply(backend.GetLife());
    }

    private static int? ParseCount(string text, int maximum) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1 && value <= maximum ? value : null;

    private void CountError(int maximum)
    {
        message = Pick($"数量需要是 1 到 {maximum} 的整数", $"數量需要是 1 到 {maximum} 的整數");
        messageFailed = true;
        Raise(string.Empty);
    }

    private void Add(LifePickRow? row)
    {
        if (row is null || !row.CanAdd) return;
        if (ParseCount(row.CountText, row.MaximumCount) is not { } count) { CountError(row.MaximumCount); return; }
        var title = row.Kind == "craft" ? Pick($"制作 {row.Name} × {count} 次", $"製作 {row.Name} × {count} 次") : Pick($"加工 {row.Name} × {count} 项", $"加工 {row.Name} × {count} 項");
        if (Enqueue(new(row.Kind, row.Target, row.Facility, count, row.Variant), title)) ClosePicker();
    }

    private void AddGather(LifeGatherRow? row)
    {
        if (row is null || !row.CanAdd || row.Fishing) return;
        if (ParseCount(row.CountText, MaximumGatherCount) is not { } count) { CountError(MaximumGatherCount); return; }
        Enqueue(new("gather", row.Name, null, count), Pick($"采集 {row.Name} × {count} 个", $"採集 {row.Name} × {count} 個"));
    }

    /// <summary>加入队列：队列执行中时先确认这项的花费（或本次运行不再询问），花费并入已确认的上限。</summary>
    private bool Enqueue(LifeTaskAddRequest request, string title)
    {
        if (snapshot is null) return false;
        if (Executing && snapshot.Tasks?.State.KnownValue == LifeQueueState.Running)
        {
            if (LifeConfirmPolicy.ShouldAsk(snapshot.BackendEpoch))
            {
                pendingAdd = new(request, title); confirm = ConfirmKind.Add; confirmRemember = false;
                Raise(string.Empty);
                return true;
            }
            request = request with { ExpectedCost = LifeQueueLogic.Info(snapshot, request.Kind)?.CostText ?? string.Empty };
        }
        _ = AddAsync(request, title);
        return true;
    }

    private async Task AddAsync(LifeTaskAddRequest request, string title)
    {
        var reply = await backend.AddLifeTaskAsync(request, CancellationToken.None);
        if (!reply.Ok)
        {
            if (reply.Code == "life.costChanged") LifeConfirmPolicy.Reset();
            Fail(reply);
        }
        else { message = Pick("已加入队列：", "已加入佇列：") + title; messageFailed = false; }
        if (!disposed) Apply(backend.GetLife());
    }

    private void RequestStart()
    {
        if (!CanStart || snapshot is null) return;
        if (LifeConfirmPolicy.ShouldAsk(snapshot.BackendEpoch)) { confirm = ConfirmKind.Start; confirmRemember = false; Raise(string.Empty); }
        else _ = StartAsync(LifeQueueLogic.Ceiling(snapshot));
    }

    private async Task StartAsync(int ceiling)
    {
        message = string.Empty; messageFailed = false;
        var reply = await backend.CommandLifeQueueAsync(new("start", ceiling), CancellationToken.None);
        if (!reply.Ok)
        {
            // 花费与确认时不同：恢复每次询问，重读后要求按新的花费确认。
            if (reply.Code == "life.costChanged") { LifeConfirmPolicy.Reset(); await RefreshAsync("all"); }
            Fail(reply);
        }
        if (!disposed) Apply(backend.GetLife());
    }

    private void Confirm()
    {
        var kind = confirm;
        var remember = confirmRemember;
        var add = pendingAdd;
        var seconds = pendingFishing;
        confirm = ConfirmKind.None; pendingAdd = null; pendingFishing = null; confirmRemember = false;
        Raise(string.Empty);
        switch (kind)
        {
            case ConfirmKind.Fishing:
                if (remember) LifeConfirmPolicy.Remember(snapshot?.BackendEpoch, LifeConfirmPolicy.Fishing);
                _ = Fishing.StartAsync(seconds);
                break;
            case ConfirmKind.Start when snapshot is { } current:
                if (remember) LifeConfirmPolicy.Remember(current.BackendEpoch);
                _ = StartAsync(LifeQueueLogic.Ceiling(current));
                break;
            case ConfirmKind.Add when add is not null && snapshot is { } current:
                if (remember) LifeConfirmPolicy.Remember(current.BackendEpoch);
                _ = AddAsync(add.Request with { ExpectedCost = LifeQueueLogic.Info(current, add.Request.Kind)?.CostText ?? string.Empty }, add.Title);
                break;
        }
        ClosePicker();
    }

    private void Collect(string facility)
    {
        if (snapshot is null || !CanCollect) return;
        _ = SendAsync(LifeQueueLogic.CollectAsync(backend, snapshot, facility));
    }

    private void Fail(BackendReply reply)
    {
        message = reply.Code is { } code ? LifeText.Reason(code) : reply.Message;
        messageFailed = true;
        Raise(string.Empty);
    }

    // ───────────────────────── 队列行的编辑 ─────────────────────────

    private LifeTask? TaskOf(string id) => snapshot?.Tasks?.Tasks.FirstOrDefault(x => x.Id == id);

    private void Update(string id, int? count, int? index)
    {
        if (snapshot?.Tasks is not { } queue) return;
        _ = SendAsync(backend.UpdateLifeTaskAsync(id, new(queue.Revision, count, index), CancellationToken.None));
    }

    /// <summary>待执行任务里的位置（0 起，不含正在执行的一项）。</summary>
    private int PendingIndex(string id) =>
        LifeQueueLogic.Pending(snapshot).Where(x => x.Id != snapshot?.Tasks?.CurrentTaskId).Select(x => x.Id).ToList().IndexOf(id);

    /// <summary>拖动排序：把任务移到待执行任务里的第 <paramref name="index"/> 位。</summary>
    internal void Move(string id, int index)
    {
        var from = PendingIndex(id);
        if (from < 0 || from == index) return;
        Update(id, null, index);
    }

    /// <summary>数量框改完（回车或离开）：数值变化且合法时发出修改，不合法时恢复原值并提示。</summary>
    internal void CommitCount(LifeTaskRow row)
    {
        if (TaskOf(row.Id) is not { } task || !row.CanEdit) return;
        var maximum = Maximum(task);
        if (ParseCount(row.CountText, maximum) is not { } count) { row.CountText = row.Count.ToString(CultureInfo.InvariantCulture); CountError(maximum); return; }
        if (count != task.Count) Update(row.Id, count, null);
    }

    private static int Maximum(LifeTask task) => task.Kind switch
    {
        "craft" => craftLimits.GetValueOrDefault(task.Target, DefaultMaximumCount),
        "gather" => MaximumGatherCount,
        _ => MaximumRounds
    };

    // 加减按钮：采集的目标数量至少比已采集多一个，加工的项数不少于已完成（正在执行时再加一）。
    private void Step(string id, int delta)
    {
        if (TaskOf(id) is not { } task) return;
        var minimum = Math.Max(1, task.Done + (task.Kind == "gather" || task.State.KnownValue == LifeTaskState.Running ? 1 : 0));
        var next = Math.Clamp(task.Count + delta, Math.Min(minimum, Maximum(task)), Maximum(task));
        if (next != task.Count) Update(id, next, null);
    }

    // ───────────────────────── 后端通知与计时 ─────────────────────────

    private void OnBackendChanged(object? sender, EventArgs e)
    {
        var fishingChanged = Fishing.Refresh();
        var next = backend.GetLife();
        if (ReferenceEquals(next, snapshot))
        {
            if (fishingChanged && fishingRunning != Fishing.IsRunning)
            {
                fishingRunning = Fishing.IsRunning;
                Build();
                Raise(string.Empty);
            }
            return;
        }
        Apply(next);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Build(languageChanged: true);
        Fishing.Invalidate();
        Raise(string.Empty);
    }

    private void OnFishingStart(int? seconds)
    {
        if (LifeConfirmPolicy.ShouldAsk(snapshot?.BackendEpoch, LifeConfirmPolicy.Fishing))
        {
            pendingAdd = null; pendingFishing = seconds; confirm = ConfirmKind.Fishing; confirmRemember = false;
            Raise(string.Empty);
        }
        else _ = Fishing.StartAsync(seconds);
    }

    private string? FishingStartBlocked()
    {
        if (Executing && snapshot?.Tasks?.Tasks.FirstOrDefault(x => x.Id == snapshot.Tasks.CurrentTaskId) is { } task)
            return Pick($"总队列正在执行「{LifeQueueLogic.Title(task)}」，结束或暂停后才能开始钓鱼", $"總佇列正在執行「{LifeQueueLogic.Title(task)}」，結束或暫停後才能開始釣魚");
        if (Executing && snapshot?.Action is { } action)
            return Pick($"正在执行{LifeText.KindName(action.Kind)} {action.Target}，结束后才能开始钓鱼", $"正在執行{LifeText.KindName(action.Kind)} {action.Target}，結束後才能開始釣魚");
        return snapshot?.Occupied is { Life: false } other
            ? Pick($"游戏活动正被「{LifeText.Owner(other.OwnerId)}」占用，暂时不能开始钓鱼", $"遊戲活動正被「{LifeText.Owner(other.OwnerId)}」佔用，暫時不能開始釣魚") : null;
    }

    private void Apply(LifeSnapshot? next)
    {
        if (disposed) return;
        if (!ReferenceEquals(next, snapshot))
        {
            snapshot = next;
            countdown.Update(next?.Queue?.Works ?? [], next?.Works?.ReadAt);
            RememberLimits(next);
        }
        fishingRunning = Fishing.IsRunning;
        Build();
        NotifyCompletion();
        Fishing.Invalidate();
        Raise(string.Empty);
        AutoCollectNow();
    }

    // 制作被游戏以 invalid_count 拒绝时，结果里的 maxCount 作为该配方的次数上限。
    private static void RememberLimits(LifeSnapshot? next)
    {
        foreach (var task in next?.Tasks?.Tasks ?? [])
            if (task.Kind == "craft" && task.Results.LastOrDefault(x => x.MaxCount is not null) is { MaxCount: { } limit }) craftLimits[task.Target] = limit;
        if (next?.Action is { Kind: "craft", Phase.RawValue: not "running" } action && action.Results.LastOrDefault(x => x.MaxCount is not null) is { MaxCount: { } max })
            craftLimits[action.Target] = max;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (disposed) return;
        Fishing.Tick();
        foreach (var tile in Facilities)
        {
            tile.Subtitle = FacilitySubtitle(tile.Name);
            foreach (var dot in tile.Dots) if (dot.Work is { } work) dot.Tip = WorkTip(work.Facility, work.Name, work.Ordinal, dot.State);
        }
        // 只有加工中的一项会到点（排队项目的时长不减少）；到点后至多每 10 秒重读一次加工队列。
        if (countdown.AnyDue && CanRead && Connected && Environment.TickCount64 - lastDueRefresh >= DueRefreshGap.TotalMilliseconds)
        {
            lastDueRefresh = Environment.TickCount64;
            _ = RefreshAsync("works");
        }
    }

    // 加工完成提示：只写进左上角的活动状态，不弹窗不响铃；同一批完成的项目不重复提示。
    private void NotifyCompletion()
    {
        var completed = Facilities.Where(x => x.Completed > 0).ToList();
        var signature = string.Join('|', completed.Select(x => x.Name + "=" + x.Completed));
        if (signature.Length == 0) { notifiedCompletion = null; return; }
        if (signature == notifiedCompletion) return;
        notifiedCompletion = signature;
        backend.ReportNotice(Pick("加工完成：" + string.Join("、", completed.Select(x => $"{x.Name} {x.Completed} 项")) + " 待领取",
            "加工完成：" + string.Join("、", completed.Select(x => $"{x.Name} {x.Completed} 項")) + " 待領取"));
    }

    // 自动领取（默认关闭）：每次只领一个设施，同一批完成的项目只尝试一次，失败不自动重试；队列执行中不插入。
    private void AutoCollectNow()
    {
        if (!LifePreference.AutoCollect || !CanCollect || Executing || snapshot is null) return;
        foreach (var tile in Facilities.Where(x => x.Completed > 0))
        {
            var key = snapshot.BackendEpoch + "|" + tile.Name + "|" + tile.Completed;
            if (!autoTried.Add(key)) continue;
            _ = SendAsync(LifeQueueLogic.CollectAsync(backend, snapshot, tile.Name));
            return;
        }
    }

    // ───────────────────────── 排版 ─────────────────────────

    private string OtherTitle => Pick("其他", "其他");

    private void Build(bool languageChanged = false)
    {
        if (languageChanged)
        {
            // 标题与分组名里含界面文字：语言切换后重建这几组（不常发生）。
            facilityTiles.Clear(); craftTiles.Clear(); skillGroups.Clear(); taskRows.Clear();
            Facilities.Clear(); CraftGroups.Clear(); SkillGroups.Clear(); Tasks.Clear();
        }
        BuildFacilities();
        BuildCrafts();
        BuildSkills();
        BuildTasks();
    }

    private void BuildFacilities()
    {
        var current = snapshot;
        var names = current?.Facilities?.Select(x => x.Name).ToList()
                    ?? (current is null ? [] : current.AlterRecipes.Select(x => x.Group).Concat(current.Queue?.Works.Select(x => x.Facility) ?? [])
                        .Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).ToList());
        var desired = new List<LifeFacilityTile>();
        foreach (var name in names)
        {
            if (!facilityTiles.TryGetValue(name, out var tile))
                facilityTiles[name] = tile = new LifeFacilityTile { Name = name, CollectCommand = new RelayCommand(_ => Collect(name)) };
            var works = current?.Queue?.Works.Where(x => x.Facility == name).ToList() ?? [];
            var full = current?.Facilities?.FirstOrDefault(x => x.Name == name)?.QueueFull == true;
            var scheduled = LifeQueueLogic.Pending(current).Where(x => x.Kind == "alter" && x.Facility == name).Sum(LifeQueueLogic.RemainingCalls);
            tile.Completed = works.Count(x => x.Completed == true);
            tile.CanCollect = CanCollect;
            tile.CanArrange = CanQueue && !full;
            tile.FullNote = full ? Pick("槽位已满", "槽位已滿") : string.Empty;
            var states = works.Select(x => x.Completed == true ? 'D' : x.State == "NotStarted" ? 'S' : 'W').ToList();
            var extra = Math.Min(scheduled, ExtraScheduledDots);
            var signature = new string([.. states]) + new string('Q', extra) + (full ? string.Empty : "+") + "|" + scheduled + "|" + (tile.CanArrange ? 1 : 0);
            if (!tile.SetDots(signature, () => Dots(name, works, extra, scheduled, full, tile.CanArrange)))
                foreach (var dot in tile.Dots) if (dot.Work is { } work) dot.Tip = WorkTip(work.Facility, work.Name, work.Ordinal, dot.State);
            tile.Subtitle = FacilitySubtitle(name);
            desired.Add(tile);
        }
        LifeSync.Sync(Facilities, desired);
    }

    private IReadOnlyList<LifeSlotDot> Dots(string facility, IReadOnlyList<LifeAlterWork> works, int extra, int scheduled, bool full, bool canArrange)
    {
        var dots = new List<LifeSlotDot>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var collect = new RelayCommand(_ => Collect(facility));
        foreach (var work in works)
        {
            var ordinal = seen.TryGetValue(work.Name, out var count) ? count : 0;
            seen[work.Name] = ordinal + 1;
            var state = work.Completed == true ? LifeDotState.Done : work.State == "NotStarted" ? LifeDotState.Scheduled : LifeDotState.Working;
            dots.Add(new LifeSlotDot
            {
                State = state, Work = (facility, work.Name, ordinal), Command = state == LifeDotState.Done ? collect : null,
                Tip = WorkTip(facility, work.Name, ordinal, state)
            });
        }
        for (var i = 0; i < extra; i++)
            dots.Add(new LifeSlotDot { State = LifeDotState.Scheduled, Tip = Pick($"总队列里还有 {scheduled} 项该设施的加工待执行", $"總佇列裡還有 {scheduled} 項該設施的加工待執行") });
        if (!full)
            dots.Add(new LifeSlotDot
            {
                State = LifeDotState.Empty, Command = canArrange ? new RelayCommand(_ => OpenArrange(facility)) : null,
                Tip = Pick("空槽：点按选择配方加入总队列", "空槽：點按選擇配方加入總佇列")
            });
        return dots;
    }

    private string WorkTip(string facility, string name, int ordinal, LifeDotState state) =>
        LifeText.WorkTip(name, state, state == LifeDotState.Done ? null : countdown.Remaining(facility, name, ordinal));

    // 设施块副标题：待领取数、加工中一项的剩余、全部完成还需（设施内逐项加工，有排队项目时才另写全部完成）。
    private string FacilitySubtitle(string facility)
    {
        if (snapshot?.Works.State.KnownValue != LifeReadState.Loaded) return Pick("加工队列未读取", "加工佇列未讀取");
        var parts = new List<string>();
        var done = snapshot.Queue?.Works.Count(x => x.Facility == facility && x.Completed == true) ?? 0;
        if (done > 0) parts.Add(Pick($"{done} 项待领取", $"{done} 項待領取"));
        var line = countdown.Facility(facility);
        if (line.Working > 0)
            parts.Add(line.Current is { } current
                ? current <= 0 ? Pick("即将完成", "即將完成") : Pick("当前剩余 ", "目前剩餘 ") + LifeText.Duration(current)
                : Pick($"{line.Working} 项进行中", $"{line.Working} 項進行中"));
        if (line.Queued > 0)
            parts.Add(line.Total is { } total ? Pick("全部完成 ", "全部完成 ") + LifeText.Duration(total) : Pick($"{line.Queued} 项排队中", $"{line.Queued} 項排隊中"));
        return parts.Count == 0 ? Pick("空闲", "空閒") : string.Join(" · ", parts);
    }

    private void BuildCrafts()
    {
        var current = snapshot;
        var desired = new List<LifeCraftTile>();
        if (current is not null)
        {
            var groups = current.CraftRecipes.GroupBy(x => x.Group).OrderBy(x => x.Key is null ? 1 : 0).ToList();
            foreach (var group in groups)
            {
                var key = group.Key ?? "\u0001other";
                if (!craftTiles.TryGetValue(key, out var tile))
                {
                    var facility = group.Key;
                    craftTiles[key] = tile = new LifeCraftTile { Name = facility ?? OtherTitle, Facility = facility, OpenCommand = new RelayCommand(_ => OpenCraft(facility)) };
                }
                var total = group.Count();
                var craftable = group.Count(x => x.Craftable == true);
                tile.Summary = Pick($"{total} 个配方 · 可制作 {craftable}", $"{total} 個配方 · 可製作 {craftable}");
                desired.Add(tile);
            }
        }
        LifeSync.Sync(CraftGroups, desired);
    }

    private void BuildSkills()
    {
        var current = snapshot;
        var desired = new List<LifeSkillGroup>();
        if (current is not null)
        {
            var groups = current.GatherItems.GroupBy(x => x.Group)
                .OrderBy(x => x.Key is null ? 2 : LifeIcons.SkillOrder.Contains(x.Key) ? 0 : 1)
                .ThenBy(x => x.Key is null ? 0 : LifeIcons.SkillOrder.Contains(x.Key) ? LifeIcons.SkillOrder.ToList().IndexOf(x.Key) : 0).ToList();
            foreach (var group in groups)
            {
                var key = group.Key ?? "\u0001other";
                if (!skillGroups.TryGetValue(key, out var item))
                    skillGroups[key] = item = new LifeSkillGroup { Name = group.Key ?? OtherTitle, Skill = group.Key, ToggleCommand = new RelayCommand(_ => Toggle(key)) };
                var members = group.ToList();
                item.CountText = Pick($"{members.Count} 项", $"{members.Count} 項");
                var issues = members.Count(x => x.ToolOk == false && !x.Fishing);
                item.IssueText = issues > 0 ? Pick($"{issues} 项缺工具", $"{issues} 項缺工具") : string.Empty;
                item.Reason = group.Key is null
                    ? string.Join("；", members.Select(x => x.GroupReason).Where(x => x is not null).Distinct(StringComparer.Ordinal).Select(LifeText.Reason)) : string.Empty;
                item.IsExpanded = expandedSkills.Contains(key);
                item.Items = item.IsExpanded ? Rows(item, members) : [];
                desired.Add(item);
            }
        }
        LifeSync.Sync(SkillGroups, desired);
    }

    // 展开的分组：沿用同名的行（数量框里的输入保留），刷新可加入状态。
    private IReadOnlyList<LifeGatherRow> Rows(LifeSkillGroup group, IReadOnlyList<LifeGatherItem> members)
    {
        var old = group.Items.ToDictionary(x => x.Name, StringComparer.Ordinal);
        return members.Select(member =>
        {
            if (!old.TryGetValue(member.Name, out var row) || row.Fishing != member.Fishing || row.ToolProblem != (member.ToolOk == false))
                row = new LifeGatherRow
                {
                    Name = member.Name, Fishing = member.Fishing, ToolProblem = member.ToolOk == false,
                    ToolText = member.Fishing ? Pick("钓鱼请使用专注钓鱼", "釣魚請使用專注釣魚")
                        : member.ToolOk switch { true => Pick("工具可用", "工具可用"), false => Pick("缺少工具或工具已损坏", "缺少工具或工具已損壞"), _ => Pick("工具状态未知", "工具狀態未知") },
                    AddCommand = new RelayCommand(value => AddGather(value as LifeGatherRow)),
                    FishingCommand = new RelayCommand(_ => FishingFocusRequested?.Invoke(this, EventArgs.Empty))
                };
            row.CanAdd = !member.Fishing && CanQueue;
            return row;
        }).ToList();
    }

    private void Toggle(string key)
    {
        if (!expandedSkills.Add(key)) expandedSkills.Remove(key);
        BuildSkills();
        Raise(nameof(GatherEmpty)); Raise(nameof(HasGatherEmpty));
    }

    private void BuildTasks()
    {
        var queue = snapshot?.Tasks;
        var desired = new List<LifeTaskRow>();
        if (queue is not null)
        {
            var pending = LifeQueueLogic.Pending(snapshot).Where(x => x.Id != queue.CurrentTaskId).Select(x => x.Id).ToList();
            foreach (var task in queue.Tasks)
            {
                if (!taskRows.TryGetValue(task.Id, out var row))
                {
                    var id = task.Id;
                    taskRows[id] = row = new LifeTaskRow
                    {
                        Id = id, RemoveCommand = new RelayCommand(_ => _ = SendAsync(backend.RemoveLifeTaskAsync(id, CancellationToken.None))),
                        UpCommand = new RelayCommand(_ => Move(id, PendingIndex(id) - 1)), DownCommand = new RelayCommand(_ => Move(id, PendingIndex(id) + 1)),
                        MinusCommand = new RelayCommand(_ => Step(id, -1)), PlusCommand = new RelayCommand(_ => Step(id, 1))
                    };
                }
                Fill(row, task, queue, pending);
                desired.Add(row);
            }
            foreach (var gone in taskRows.Keys.Except(queue.Tasks.Select(x => x.Id)).ToList()) taskRows.Remove(gone);
        }
        LifeSync.Sync(Tasks, desired);
    }

    private void Fill(LifeTaskRow row, LifeTask task, LifeTaskQueue queue, List<string> pending)
    {
        var state = task.State.KnownValue;
        var current = task.Id == queue.CurrentTaskId;
        var waiting = state is LifeTaskState.Pending or LifeTaskState.Running;
        row.Kind = task.Kind;
        // 图标：采集按采集物所属的技能，其余按设施。
        row.Facility = task.Kind == "gather" ? snapshot?.GatherItems.FirstOrDefault(x => x.Name == task.Target)?.Group : task.Facility;
        row.Title = LifeQueueLogic.Title(task);
        row.Subtitle = task.Kind is "alter" or "craft" && task.Facility is { } facility ? facility : task.Immediate ? Pick("立即执行", "立即執行") : string.Empty;
        row.ShowProgress = LifeQueueLogic.ShowsProgress(task);
        row.Progress = LifeQueueLogic.Fraction(task);
        row.ProgressText = LifeQueueLogic.ProgressText(task);
        row.IsRunning = current;
        row.IsPausedHere = !current && queue.State.KnownValue == LifeQueueState.Paused && queue.PauseTaskId == task.Id;
        row.IsFinished = state == LifeTaskState.Completed;
        row.IsFailed = state is LifeTaskState.Failed or LifeTaskState.Stopped;
        row.StateText = current ? Pick("执行中", "執行中")
            : row.IsPausedHere ? Pick("暂停在这一项", "暫停在這一項")
            : state switch
            {
                LifeTaskState.Completed => Pick("已完成", "已完成"),
                LifeTaskState.Failed => Pick("失败", "失敗"),
                LifeTaskState.Stopped => Pick("已停止", "已停止"),
                _ => Pick("待执行", "待執行")
            };
        var index = pending.IndexOf(task.Id);
        row.CanEdit = waiting && !current && task.Kind != "collect";
        row.CanMove = index >= 0;
        row.CanUp = index > 0;
        row.CanDown = index >= 0 && index < pending.Count - 1;
        row.CanRemove = !current;
        if (row.Count != task.Count || !row.CanEdit) row.CountText = task.Count.ToString(CultureInfo.InvariantCulture);
        row.Count = task.Count;
        row.Result = Result(task);
    }

    // 最近一次结果：失败或停止的原因（游戏里的结果未知时注明），否则最后一次调用的获得（采集写本次获得的数量）与花费。
    private static string Result(LifeTask task)
    {
        if (task.LastCode is { } code && task.State.KnownValue != LifeTaskState.Completed)
        {
            var reason = code == "gameCli.blocked" ? LifeText.Blocked(task.LastDetail) : LifeText.Reason(code);
            if (code == "gameCli.invalidCount" && task.Results.LastOrDefault()?.MaxCount is { } limit) reason += Pick($"（最多 {limit} 次）", $"（最多 {limit} 次）");
            return task.StateUnknown ? reason + Pick("；游戏里的结果未知", "；遊戲裡的結果未知") : reason;
        }
        if (task.Results.LastOrDefault() is not { } last) return string.Empty;
        var got = last.Outcome.KnownValue switch
        {
            LifeRoundOutcome.Completed => last.Gained is { } gained ? task.Kind == "gather" ? Pick($"获得 {gained} 个", $"獲得 {gained} 個") : Pick($"获得 {gained}", $"獲得 {gained}")
                : last.CraftCount is { } crafted ? Pick($"完成 {crafted} 次制作", $"完成 {crafted} 次製作") : Pick("已完成", "已完成"),
            LifeRoundOutcome.Started => Pick("已排队", "已排隊"),
            LifeRoundOutcome.Collected => Pick($"领取 {LifeText.Number(last.Collected)} 项", $"領取 {LifeText.Number(last.Collected)} 項"),
            _ => string.Empty
        };
        var rewards = last.Rewards.Count == 0 ? string.Empty
            : "：" + string.Join("、", last.Rewards.Select(item => item.Name + (item.Amount is { } amount ? " ×" + amount.ToString(CultureInfo.InvariantCulture) : string.Empty)));
        var cost = last.Spent is { } spent ? Pick($" · 花费 {spent}，剩余 {LifeText.Number(last.Left)} {last.CostCurrency}", $" · 花費 {spent}，剩餘 {LifeText.Number(last.Left)} {last.CostCurrency}")
            : last.Cost is { Length: > 0 } raw ? Pick(" · 花费：", " · 花費：") + raw : string.Empty;
        var head = task.Kind == "gather" ? Pick($"第 {last.Round} 次：", $"第 {last.Round} 次：") : Pick($"第 {last.Round} 轮：", $"第 {last.Round} 輪：");
        return head + got + rewards + cost;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tick.Stop();
        tick.Tick -= OnTick;
        Fishing.StartRequested -= OnFishingStart;
        backend.Changed -= OnBackendChanged;
        UiText.LanguageChanged -= OnLanguageChanged;
        backend.SetSurfaceVisible(Surface, false);
    }
}
