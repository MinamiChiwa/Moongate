using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Market;

/// <summary>查价入口能否使用：桌面没连上后端，或能力 <c>market.prices</c> 不可用时 <see cref="Blocked"/> 写明原因（指向“游戏连接器”胶囊）。</summary>
public sealed record MarketAvailability(bool Connected, string? Blocked)
{
    private static readonly ConditionalWeakTable<IDesktopBackend, StrongBox<MarketAvailability>> Last = new();

    /// <summary>读取当前状态；内容相同时返回同一个对象（页面据此判断没有变化）。</summary>
    public static MarketAvailability Of(IDesktopBackend backend)
    {
        var connected = backend.GetGameConnector().Read != ConnectorRead.Offline;
        var next = new MarketAvailability(connected, !connected ? Pick("未连接后端", "未連接後端") : backend.GetCapabilityNotice(CapabilityIds.MarketPrices));
        var box = Last.GetValue(backend, _ => new(next));
        if (box.Value == next) return box.Value;
        box.Value = next;
        return next;
    }
}

/// <summary>
/// 交易所查价页模型：名称（至少 2 个字，可多个）、分类、收藏三种查价模式，价格明细与按数量计算的购买成本。只查价，没有任何买卖入口。
/// 所有查询都由用户点按钮发出，页面不显示时不发查询；游戏要求等待（<c>rate_limited</c>）时对应按钮停用并倒计时，到时不自动重发。
/// </summary>
public sealed class MarketPageModel : BackendPageModel<MarketAvailability>
{
    private readonly MarketCooldown priceCooldown, detailCooldown;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MarketMode mode = MarketMode.Names;
    private string namesText = string.Empty, category = string.Empty, subCategory = string.Empty, quantityText = "1";
    private MarketPricesView prices = MarketPricesView.None;
    private MarketDetailView detail = MarketDetailView.None;
    private MarketRowView? selected;
    private IReadOnlyList<MarketGroupView> groups = [];
    private bool busy, detailBusy;

    /// <summary>页面没有自己的后台读取，只有用户点按钮发出的查询：切走后保留已有结果。</summary>
    protected override bool Cacheable => true;

    public MarketPageModel(IDesktopBackend backend, Func<DateTimeOffset>? clock = null) : base(backend, MarketAvailability.Of)
    {
        priceCooldown = new(clock);
        detailCooldown = new(clock);
        timer.Tick += (_, _) => Tick();
        SetModeCommand = new RelayCommand(value =>
        {
            var next = (value as string) switch { "category" => MarketMode.Category, "favorites" => MarketMode.Favorites, _ => MarketMode.Names };
            if (next == mode) return;
            mode = next;
            RaiseInput();
        });
        QueryCommand = new RelayCommand(_ => _ = QueryAsync());
        QueryDetailCommand = new RelayCommand(_ => _ = QueryDetailAsync());
        SelectRowCommand = new RelayCommand(row => { if (row is MarketRowView value) Select(value); });
        ChooseCandidateCommand = new RelayCommand(row =>
        {
            if (row is not MarketRowView value) return;
            Select(value);
            _ = QueryDetailAsync();
        });
    }

    public ICommand SetModeCommand { get; }
    public ICommand QueryCommand { get; }
    public ICommand QueryDetailCommand { get; }
    public ICommand SelectRowCommand { get; }
    public ICommand ChooseCandidateCommand { get; }

    // ---- 输入 ----

    public bool ModeNames => mode == MarketMode.Names;
    public bool ModeCategory => mode == MarketMode.Category;
    public bool ModeFavorites => mode == MarketMode.Favorites;

    public string NamesText { get => namesText; set { if (Set(ref namesText, value ?? string.Empty)) Raise(nameof(CanQuery)); } }
    public string Category { get => category; set { if (Set(ref category, value ?? string.Empty)) Raise(nameof(CanQuery)); } }
    public string SubCategory { get => subCategory; set => Set(ref subCategory, value ?? string.Empty); }

    public string QuantityText
    {
        get => quantityText;
        set
        {
            if (!Set(ref quantityText, value ?? string.Empty)) return;
            Raise(nameof(Cost)); Raise(nameof(CostText)); Raise(nameof(HasCost)); Raise(nameof(CostLowerBound));
        }
    }

    /// <summary>数量：正整数，输入无效时按 1 计。</summary>
    public int Quantity => int.TryParse(quantityText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 1;

    /// <summary>当前输入对应的查询请求。</summary>
    public MarketPriceRequest BuildRequest() => new(mode, MarketPriceRequest.SplitNames(namesText), category.Trim(), subCategory.Trim());

    // ---- 可用性与冷却 ----

    public string Blocked => Data.Blocked ?? string.Empty;
    public bool HasBlocked => Blocked.Length > 0;

    public bool CanQuery => !HasBlocked && !busy && !priceCooldown.Active;
    public bool CanQueryDetail => !HasBlocked && selected is not null && !detailBusy && !detailCooldown.Active;

    public string QueryLabel => priceCooldown.Active ? Pick($"查询（{priceCooldown.Remaining} 秒后可用）", $"查詢（{priceCooldown.Remaining} 秒後可用）")
        : busy ? Pick("查询中…", "查詢中…") : Pick("查询", "查詢");

    public string DetailLabel => detailCooldown.Active ? Pick($"查询明细（{detailCooldown.Remaining} 秒后可用）", $"查詢明細（{detailCooldown.Remaining} 秒後可用）")
        : detailBusy ? Pick("查询中…", "查詢中…") : Pick("查询明细", "查詢明細");

    /// <summary>冷却倒计时每秒走一格；两个冷却都结束时停止计时。供界面计时器与测试调用。</summary>
    public void Tick()
    {
        Raise(nameof(QueryLabel)); Raise(nameof(DetailLabel)); Raise(nameof(CanQuery)); Raise(nameof(CanQueryDetail));
        if (!priceCooldown.Active && !detailCooldown.Active) timer.Stop();
    }

    private void StartTimer()
    {
        if (!timer.IsEnabled) timer.Start();
    }

    // ---- 价目 ----

    public string PriceMessage => prices.Message;
    public bool HasPriceMessage => prices.HasMessage;
    public string PriceNote => prices.Note;
    public bool HasPriceNote => prices.HasNote;
    public string CandidatesText => prices.CandidatesText;
    public bool HasCandidates => prices.HasCandidates;
    public IReadOnlyList<MarketGroupView> Groups => groups;
    public bool HasGroups => groups.Count > 0;

    /// <summary>发出价目查询；名称不足 2 个字、分类为空时只提示不发查询，被游戏要求等待的结果带倒计时。</summary>
    public async Task QueryAsync()
    {
        if (!CanQuery) return;
        var request = BuildRequest();
        if (request.Problem is { } problem)
        {
            ApplyPrices(MarketPricesView.None with { Reason = problem, Message = MarketText.Reason(problem) });
            return;
        }
        busy = true;
        RaiseBusy();
        MarketPricesView result;
        try { result = await Backend.QueryMarketPricesAsync(request, CancellationToken.None); }
        finally { busy = false; }
        ApplyPrices(result);
    }

    private void ApplyPrices(MarketPricesView result)
    {
        prices = result;
        selected = null;
        detail = MarketDetailView.None;
        detailCooldown.Clear();
        if (result.RetryAfterSeconds > 0) { priceCooldown.Start(result.RetryAfterSeconds); StartTimer(); }
        RebuildGroups();
        foreach (var name in new[] { nameof(PriceMessage), nameof(HasPriceMessage), nameof(PriceNote), nameof(HasPriceNote), nameof(CandidatesText),
                     nameof(HasCandidates), nameof(DetailView), nameof(HasSelection), nameof(SelectedTitle) }) Raise(name);
        RaiseBusy();
        RaiseDetail();
    }

    private void RebuildGroups()
    {
        groups = prices.Groups.Select(group => group with { Rows = group.Rows.Select(row => row with { Selected = selected is not null && row.Key == selected.Key }).ToList() }).ToList();
        Raise(nameof(Groups)); Raise(nameof(HasGroups));
    }

    // ---- 明细 ----

    public MarketDetailView DetailView => detail;
    public bool HasSelection => selected is not null;
    public bool NoSelection => selected is null;
    public string SelectedTitle => selected?.Title ?? string.Empty;
    public bool HasDetail => detail.Ok && detail.Title.Length > 0;
    public string DetailMessage => detail.Message;
    public bool HasDetailMessage => detail.HasMessage;
    public string DetailNote => detail.Note;
    public bool HasDetailNote => detail.Note.Length > 0;
    public IReadOnlyList<MarketRowView> DetailCandidates => detail.Candidates;
    public bool HasDetailCandidates => detail.HasCandidates;

    /// <summary>按数量计算的购买成本：从明细的三档价位就地计算，不再向游戏查询。</summary>
    public MarketCost Cost => MarketCost.Compute(detail.Tiers, Quantity);
    public string CostText => HasDetail ? Cost.Text : string.Empty;
    public bool HasCost => HasDetail && CostText.Length > 0;
    public bool CostLowerBound => HasDetail && Cost.IsLowerBound;

    private void Select(MarketRowView row)
    {
        selected = row;
        detail = MarketDetailView.None;
        detailCooldown.Clear();
        RebuildGroups();
        Raise(nameof(HasSelection)); Raise(nameof(NoSelection)); Raise(nameof(SelectedTitle));
        RaiseDetail();
    }

    /// <summary>发出明细查询；游戏要求等待时按其秒数倒计时，到时由用户再点，不自动重发。</summary>
    public async Task QueryDetailAsync()
    {
        if (!CanQueryDetail || selected is not { } row) return;
        detailBusy = true;
        RaiseBusy();
        MarketDetailView result;
        try { result = await Backend.QueryMarketDetailAsync(new(row.Name, row.Rarity, row.Star, row.Season), CancellationToken.None); }
        finally { detailBusy = false; }
        detail = result;
        if (result.RetryAfterSeconds > 0) { detailCooldown.Start(result.RetryAfterSeconds); StartTimer(); }
        RaiseDetail();
        RaiseBusy();
    }

    private void RaiseBusy()
    {
        foreach (var name in new[] { nameof(CanQuery), nameof(CanQueryDetail), nameof(QueryLabel), nameof(DetailLabel) }) Raise(name);
    }

    private void RaiseDetail()
    {
        foreach (var name in new[] { nameof(DetailView), nameof(HasDetail), nameof(DetailMessage), nameof(HasDetailMessage), nameof(DetailNote), nameof(HasDetailNote),
                     nameof(DetailCandidates), nameof(HasDetailCandidates), nameof(Cost), nameof(CostText), nameof(HasCost), nameof(CostLowerBound),
                     nameof(CanQueryDetail), nameof(DetailLabel) }) Raise(name);
    }

    private void RaiseInput()
    {
        foreach (var name in new[] { nameof(ModeNames), nameof(ModeCategory), nameof(ModeFavorites), nameof(CanQuery) }) Raise(name);
    }

    protected override void OnDataChanged()
    {
        foreach (var name in new[] { nameof(Blocked), nameof(HasBlocked), nameof(CanQuery), nameof(CanQueryDetail) }) Raise(name);
    }

    public override void Dispose()
    {
        timer.Stop();
        base.Dispose();
    }
}
