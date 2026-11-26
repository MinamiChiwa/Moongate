using System.Windows.Input;
using Avalonia.Threading;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>“加工设施”浮窗元件的一个设施：名称、槽位圆点、当前剩余与全部完成还需、领取。</summary>
public sealed class LifeOverlayFacility : ObservableObject
{
    private string subtitle = string.Empty;
    private IReadOnlyList<LifeSlotDot> dots = [];
    private int completed;

    public required string Name { get; init; }
    public required ICommand CollectCommand { get; init; }
    public IReadOnlyList<LifeSlotDot> Dots { get => dots; set => Set(ref dots, value); }
    public string Subtitle { get => subtitle; set => Set(ref subtitle, value); }

    /// <summary>已完成待领取的项数：“领取”始终显示，为 0 时停用并在悬停写明原因。</summary>
    public int Completed
    {
        get => completed;
        set { if (Set(ref completed, value)) { Raise(nameof(CollectEnabled)); Raise(nameof(CollectTip)); } }
    }

    public bool CollectEnabled => completed > 0;
    public string CollectTip => completed > 0 ? Name : LifeText.NothingToCollect;
    internal string Signature { get; set; } = string.Empty;
}

/// <summary>
/// 生活浮窗元件（“加工设施”与“生活队列”）的数据：与“生活”页读同一份后端快照，后端变化时重读。
/// 交互（领取、安排加工、暂停、继续、停止当前）发出与页面相同的请求；配方菜单与继续前的花费确认在元件外框内显示，
/// 元件上锁时这些交互区隐藏，视图随之关闭菜单与确认（<see cref="CloseTransient"/>）。
/// 配方菜单用页面同一个 <see cref="LifePicker"/> 过滤（配方名与缺少的材料名，忽略大小写与全角半角）；快照变化时重建菜单并沿用搜索词。
/// </summary>
public sealed class LifeOverlayData : ObservableObject, IDisposable
{
    private readonly IDesktopBackend backend;
    private readonly LifeCountdown countdown;
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, LifeOverlayFacility> rows = new(StringComparer.Ordinal);
    private LifeSnapshot? snapshot;
    private IReadOnlyList<LifeOverlayFacility> facilities = [];
    private LifePicker? menu;
    private string menuSearch = string.Empty;
    private string? menuFacility;
    private LifeTaskAddRequest? pendingAdd;
    private bool confirmStart, shown, disposed;
    private string message = string.Empty;

    public LifeOverlayData(IDesktopBackend backend) : this(backend, null) { }

    /// <param name="now">倒计时用的当前时刻（UTC）；测试注入替身。</param>
    internal LifeOverlayData(IDesktopBackend backend, Func<DateTimeOffset>? now)
    {
        this.backend = backend;
        countdown = new(now);
        PauseCommand = new RelayCommand(_ => _ = SendAsync(backend.CommandLifeQueueAsync(new("pause"), CancellationToken.None)));
        StopCommand = new RelayCommand(_ => _ = SendAsync(backend.CommandLifeQueueAsync(new("stop-current"), CancellationToken.None)));
        ContinueCommand = new RelayCommand(_ => RequestContinue());
        CloseMenuCommand = new RelayCommand(_ => CloseTransient());
        ConfirmCommand = new RelayCommand(_ => Confirm());
        CancelCommand = new RelayCommand(_ => CloseTransient());
        tick.Tick += (_, _) => Tick();
        backend.Changed += OnChanged;
        Read();
    }

    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand CloseMenuCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public string FacilitiesTitle => Pick("加工设施", "加工設施");
    public string QueueTitle => Pick("生活队列", "生活佇列");
    public string BackText => Pick("返回", "返回");
    public string AddText => Pick("加入", "加入");
    public string CollectText => Pick("领取", "領取");
    public string PauseText => Pick("暂停", "暫停");
    public string ContinueText => snapshot?.Tasks?.State.KnownValue == LifeQueueState.Paused ? Pick("继续", "繼續") : Pick("开始", "開始");
    public string StopText => Pick("停止当前", "停止目前");
    public string ConfirmText => Pick("确认", "確認");
    public string CancelText => Pick("取消", "取消");

    public IReadOnlyList<LifeOverlayFacility> Facilities => facilities;
    public bool NoFacilities => facilities.Count == 0;
    public string NoFacilitiesText => snapshot is null ? Pick("未读取：打开“生活”页读取一次", "未讀取：打開「生活」頁讀取一次") : Pick("没有加工设施的数据", "沒有加工設施的資料");

    public bool MenuOpen => menuFacility is not null;

    /// <summary>设施列表：元件内的菜单或确认打开时让出位置。</summary>
    public bool ShowList => !MenuOpen && !ConfirmOpen;
    public string MenuTitle => menuFacility is null ? string.Empty : menuFacility + Pick(" · 安排加工", " · 安排加工");

    /// <summary>菜单里按搜索词过滤后的配方。</summary>
    public IReadOnlyList<LifePickRow> Menu => menu?.Items ?? [];
    public string MenuSearchHint => Pick("搜索配方或材料", "搜尋配方或材料");

    /// <summary>菜单的搜索框：按配方名与缺少的材料名过滤，菜单尺寸不变。</summary>
    public string MenuSearch
    {
        get => menuSearch;
        set
        {
            if (!Set(ref menuSearch, value ?? string.Empty)) return;
            if (menu is not null) menu.Search = menuSearch;
            Raise(nameof(Menu)); Raise(nameof(MenuEmptyText)); Raise(nameof(MenuHasEmpty));
        }
    }

    /// <summary>菜单列表为空时的说明：设施没有读到配方，或搜索没有匹配。</summary>
    public string MenuEmptyText => menu?.EmptyText ?? string.Empty;
    public bool MenuHasEmpty => menu?.HasEmpty == true;

    public bool ConfirmOpen => confirmStart || pendingAdd is not null;
    public IReadOnlyList<string> ConfirmLines
    {
        get
        {
            if (snapshot is null) return [];
            if (pendingAdd is { } add) return LifeQueueLogic.AddLines(snapshot, add.Kind, add.DisplayName ?? string.Empty, add.Count);
            var pending = LifeQueueLogic.Pending(snapshot).Count;
            return
            [
                Pick($"继续执行剩余 {pending} 项，整队花费上限 {LifeQueueLogic.Ceiling(snapshot)} {LifeQueueLogic.Currency(snapshot)}",
                    $"繼續執行剩餘 {pending} 項，整隊花費上限 {LifeQueueLogic.Ceiling(snapshot)} {LifeQueueLogic.Currency(snapshot)}"),
                Pick("执行会自动移动角色；失败时整队暂停。", "執行會自動移動角色；失敗時整隊暫停。")
            ];
        }
    }

    // ───────────────────────── 队列 ─────────────────────────

    public string QueueState => LifeQueueLogic.StateText(snapshot);
    public bool QueueWarn => snapshot?.Tasks?.State.KnownValue == LifeQueueState.Paused;
    public bool QueueLive => LifeQueueLogic.Executing(snapshot);
    public string PauseReason => LifeQueueLogic.PauseText(snapshot);
    public bool HasPauseReason => PauseReason.Length > 0;

    private LifeTask? Current => snapshot?.Tasks is { CurrentTaskId: { } id } queue ? queue.Tasks.FirstOrDefault(x => x.Id == id) : null;

    /// <summary>当前任务的标题（采集另写已采集数量），没有当前任务时写下一项。</summary>
    public string CurrentText => Current is { } task
        ? LifeQueueLogic.Title(task) + (task.Kind == "gather" ? " · " + LifeQueueLogic.GatherProgress(task) : string.Empty)
        : LifeQueueLogic.Pending(snapshot).FirstOrDefault() is { } first
            ? Pick("下一项：", "下一項：") + LifeQueueLogic.Title(first) : Pick("没有待执行的任务", "沒有待執行的任務");
    public double CurrentProgress => Current is { } task ? LifeQueueLogic.Fraction(task) : 0;
    public bool HasCurrentProgress => Current is { } task && LifeQueueLogic.ShowsProgress(task);

    public string NextText => Current is not null && LifeQueueLogic.Pending(snapshot).Where(x => x.Id != Current.Id).FirstOrDefault() is { } next
        ? Pick("下一项：", "下一項：") + LifeQueueLogic.Title(next) : string.Empty;
    public bool HasNext => NextText.Length > 0;

    public bool CanPause => snapshot?.Tasks is { State.KnownValue: LifeQueueState.Running } queue && queue.PauseCode != "life.pauseRequested";
    public bool CanStop => LifeQueueLogic.Executing(snapshot);
    public bool CanContinue => snapshot?.Connector.KnownValue == GameConnectorStatus.Connected && !LifeQueueLogic.Executing(snapshot)
                               && (snapshot.Occupied is null or { Life: true }) && LifeQueueLogic.Pending(snapshot).Count > 0;
    public bool ShowContinue => !LifeQueueLogic.Executing(snapshot);

    public string Message => message;
    public bool HasMessage => message.Length > 0;

    // ───────────────────────── 读取与命令 ─────────────────────────

    /// <summary>元件显示状态（视图挂上可见窗口时为真）：只有显示时运行倒计时。</summary>
    public void SetShown(bool value)
    {
        shown = value;
        if (value) Read();
        UpdateTick();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (!ReferenceEquals(backend.GetLife(), snapshot)) Read();
    }

    private void Read()
    {
        snapshot = backend.GetLife();
        countdown.Update(snapshot?.Queue?.Works ?? [], snapshot?.Works?.ReadAt);
        BuildFacilities();
        if (menuFacility is { } facility) menu = Recipes(facility);
        Raise(string.Empty);
        UpdateTick();
    }

    private void UpdateTick()
    {
        var wanted = shown && !disposed && snapshot?.Queue?.Works.Any(x => x.Completed != true) == true;
        if (wanted && !tick.IsEnabled) tick.Start();
        else if (!wanted && tick.IsEnabled) tick.Stop();
    }

    private void Tick()
    {
        foreach (var row in facilities)
        {
            row.Subtitle = Subtitle(row.Name);
            foreach (var dot in row.Dots) if (dot.Work is { } work) dot.Tip = WorkTip(work.Facility, work.Name, work.Ordinal, dot.State);
        }
        if (countdown.AnyDue) UpdateTick();
    }

    private string WorkTip(string facility, string name, int ordinal, LifeDotState state) =>
        LifeText.WorkTip(name, state, state == LifeDotState.Done ? null : countdown.Remaining(facility, name, ordinal));

    private void BuildFacilities()
    {
        var current = snapshot;
        var names = current?.Facilities?.Select(x => x.Name).ToList() ?? current?.Queue?.Works.Select(x => x.Facility).Distinct(StringComparer.Ordinal).ToList() ?? [];
        var result = new List<LifeOverlayFacility>();
        foreach (var name in names)
        {
            if (!rows.TryGetValue(name, out var row))
                rows[name] = row = new LifeOverlayFacility { Name = name, CollectCommand = new RelayCommand(_ => Collect(name)) };
            var works = current?.Queue?.Works.Where(x => x.Facility == name).ToList() ?? [];
            var full = current?.Facilities?.FirstOrDefault(x => x.Name == name)?.QueueFull == true;
            var scheduled = Math.Min(4, LifeQueueLogic.Pending(current).Where(x => x.Kind == "alter" && x.Facility == name).Sum(LifeQueueLogic.RemainingCalls));
            var signature = string.Concat(works.Select(x => x.Completed == true ? 'D' : x.State == "NotStarted" ? 'S' : 'W')) + new string('Q', scheduled) + (full ? "" : "+");
            if (signature != row.Signature)
            {
                row.Signature = signature;
                var dots = new List<LifeSlotDot>();
                var collect = row.CollectCommand;
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var work in works)
                {
                    var ordinal = seen.TryGetValue(work.Name, out var count) ? count : 0;
                    seen[work.Name] = ordinal + 1;
                    var state = work.Completed == true ? LifeDotState.Done : work.State == "NotStarted" ? LifeDotState.Scheduled : LifeDotState.Working;
                    dots.Add(new LifeSlotDot { State = state, Command = state == LifeDotState.Done ? collect : null, Work = (name, work.Name, ordinal) });
                }
                for (var i = 0; i < scheduled; i++) dots.Add(new LifeSlotDot { State = LifeDotState.Scheduled, Tip = Pick("总队列里待执行", "總佇列裡待執行") });
                if (!full) dots.Add(new LifeSlotDot { State = LifeDotState.Empty, Command = new RelayCommand(_ => OpenMenu(name)), Tip = Pick("空槽：点按安排加工", "空槽：點按安排加工") });
                row.Dots = dots;
            }
            foreach (var dot in row.Dots) if (dot.Work is { } work) dot.Tip = WorkTip(work.Facility, work.Name, work.Ordinal, dot.State);
            row.Completed = works.Count(x => x.Completed == true);
            row.Subtitle = Subtitle(name);
            result.Add(row);
        }
        if (!result.SequenceEqual(facilities)) facilities = result;
    }

    // 紧凑的副标题：待领取数、加工中一项的剩余；有排队项目时另写全部完成还需。
    private string Subtitle(string facility)
    {
        var done = snapshot?.Queue?.Works.Count(x => x.Facility == facility && x.Completed == true) ?? 0;
        var line = countdown.Facility(facility);
        var parts = new List<string>();
        if (done > 0) parts.Add(Pick($"{done} 待领取", $"{done} 待領取"));
        if (line.Current is { } current) parts.Add(current <= 0 ? Pick("即将完成", "即將完成") : LifeText.Duration(current));
        if (line.Queued > 0 && line.Total is { } total) parts.Add(Pick("全部 ", "全部 ") + LifeText.Duration(total));
        return string.Join(" · ", parts);
    }

    private LifePicker Recipes(string facility)
    {
        var rows = snapshot?.AlterRecipes.Where(x => x.Group == facility).Select(recipe =>
        {
            var ok = recipe.Alterable == true;
            var status = ok ? Pick("可加工", "可加工") : recipe.Reason is { Length: > 0 } reason ? LifeText.GameReason(reason) : Pick("不可加工", "不可加工");
            var name = recipe.Name;
            return new LifePickRow
            {
                Name = name, Target = name, Kind = "alter", Facility = facility, Status = status, StatusProblem = !ok,
                MaterialNames = [.. recipe.Missing.Select(x => x.Name)], CanAdd = ok && snapshot?.Tasks is not null,
                AddCommand = new RelayCommand(_ => Arrange(facility, name))
            };
        }).ToList() ?? [];
        return new LifePicker
        {
            Title = facility, All = rows, Empty = Pick("这个设施没有读到加工配方", "這個設施沒有讀到加工配方"), Search = menuSearch
        };
    }

    private void OpenMenu(string facility)
    {
        menuFacility = facility;
        menuSearch = string.Empty;
        menu = Recipes(facility);
        confirmStart = false; pendingAdd = null;
        Raise(string.Empty);
    }

    /// <summary>关闭元件内的配方菜单与确认（点“返回”“取消”，或元件上锁时由视图调用）。</summary>
    public void CloseTransient()
    {
        if (menuFacility is null && !confirmStart && pendingAdd is null) return;
        menuFacility = null; menu = null; menuSearch = string.Empty; confirmStart = false; pendingAdd = null;
        Raise(string.Empty);
    }

    private void Arrange(string facility, string recipe)
    {
        if (snapshot is null) return;
        var request = new LifeTaskAddRequest("alter", recipe, facility, 1);
        if (LifeQueueLogic.Executing(snapshot) && snapshot.Tasks?.State.KnownValue == LifeQueueState.Running)
        {
            if (LifeConfirmPolicy.ShouldAsk(snapshot.BackendEpoch)) { pendingAdd = request; menuFacility = null; Raise(string.Empty); return; }
            request = request with { ExpectedCost = LifeQueueLogic.Info(snapshot, "alter")?.CostText ?? string.Empty };
        }
        CloseTransient();
        _ = SendAsync(backend.AddLifeTaskAsync(request, CancellationToken.None));
    }

    private void Collect(string facility)
    {
        if (snapshot is null) return;
        _ = SendAsync(LifeQueueLogic.CollectAsync(backend, snapshot, facility));
    }

    private void RequestContinue()
    {
        if (!CanContinue || snapshot is null) return;
        if (LifeConfirmPolicy.ShouldAsk(snapshot.BackendEpoch)) { confirmStart = true; pendingAdd = null; menuFacility = null; Raise(string.Empty); }
        else _ = SendAsync(backend.CommandLifeQueueAsync(new("start", LifeQueueLogic.Ceiling(snapshot)), CancellationToken.None));
    }

    private void Confirm()
    {
        if (snapshot is not { } current) return;
        var add = pendingAdd;
        var start = confirmStart;
        CloseTransient();
        if (add is not null)
            _ = SendAsync(backend.AddLifeTaskAsync(add with { ExpectedCost = LifeQueueLogic.Info(current, add.Kind)?.CostText ?? string.Empty }, CancellationToken.None));
        else if (start)
            _ = SendAsync(backend.CommandLifeQueueAsync(new("start", LifeQueueLogic.Ceiling(current)), CancellationToken.None));
    }

    private async Task SendAsync(Task<BackendReply> request)
    {
        var reply = await request;
        message = reply.Ok ? string.Empty : reply.Code is { } code ? LifeText.Reason(code) : reply.Message;
        if (!reply.Ok && reply.Code == "life.costChanged") LifeConfirmPolicy.Reset();
        if (!disposed) Read();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tick.Stop();
        backend.Changed -= OnChanged;
    }
}
