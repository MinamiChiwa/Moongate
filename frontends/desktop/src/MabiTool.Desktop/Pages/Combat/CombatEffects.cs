using System.Windows.Input;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Combat;

// 自身与怪物效果卡片的 Buff 收藏：只显示收藏的开关与收藏层（按名称或编号搜索客户端 Buff 表与已提取到的 Buff 并收藏或取消）。
// 状态只有 CombatEffectFavorites 一份，主页面、明细窗口与浮窗联动。
public sealed partial class CombatPageModel
{
    private string favoriteQuery = string.Empty;

    public bool SelfFavoritesOnly => CombatEffectFavorites.SelfOnly;
    public bool TargetFavoritesOnly => CombatEffectFavorites.TargetOnly;
    /// <summary>“只显示收藏”开着、但这一刻没有收藏的效果：卡片写明，不显示为空白。</summary>
    public bool SelfFavoritesEmpty => Data.SelfFavoritesEmpty;
    public bool TargetFavoritesEmpty => Data.TargetFavoritesEmpty;

    public ICommand ToggleSelfFavoritesOnlyCommand => toggleSelfOnly ??= new RelayCommand(_ => CombatEffectFavorites.SetSelfOnly(!CombatEffectFavorites.SelfOnly));
    public ICommand ToggleTargetFavoritesOnlyCommand => toggleTargetOnly ??= new RelayCommand(_ => CombatEffectFavorites.SetTargetOnly(!CombatEffectFavorites.TargetOnly));
    /// <summary>收藏或取消一项：参数是收藏层的一行或效果卡片上的一个效果。</summary>
    public ICommand ToggleFavoriteEffectCommand => toggleFavorite ??= new RelayCommand(item =>
    {
        if (item is FavoriteEntryView entry) CombatEffectFavorites.Toggle(entry);
        else if (item is EffectRow { EffectId: { } id }) CombatEffectFavorites.Toggle(id);
    });
    private ICommand? toggleSelfOnly, toggleTargetOnly, toggleFavorite;

    /// <summary>收藏层的搜索词（名称或编号）；属于界面的临时输入，不保存。</summary>
    public string FavoriteQuery
    {
        get => favoriteQuery;
        set { if (Set(ref favoriteQuery, value)) SyncFavorites(); }
    }

    /// <summary>
    /// 收藏层列出的效果：客户端 Buff 表与本机观测到的效果目录的并集，已收藏的在前、其次观测到过的，至多 <see cref="CombatEffectFavorites.SearchLimit"/> 项。
    /// </summary>
    public StableList<FavoriteEntryView> FavoriteCatalog { get; } = [];
    public bool NoFavoriteCatalog => FavoriteCatalog.Count == 0;
    public string FavoriteEmptyText => favoriteQuery.Trim().Length > 0
        ? UiText.Pick("没有符合的效果", "沒有符合的效果") : UiText.Pick("还没有可选的效果", "還沒有可選的效果");
    private int favoriteTotal;

    /// <summary>来源与数量：客户端 Buff 表的状态（读取中、不可用的原因）、本机观测到的数量与收藏数；结果超过上限时写明总数。</summary>
    public string FavoriteCatalogNote
    {
        get
        {
            var client = backend.GetClientBuffs();
            var observed = CombatEffectFavorites.CatalogCount;
            var favorites = CombatEffectFavorites.Favorites.Count;
            var source = client.Ready
                ? UiText.Pick($"客户端 Buff 表 {client.Entries.Count} 种，本机观测到 {observed} 种，已收藏 {favorites} 种",
                    $"用戶端 Buff 表 {client.Entries.Count} 種，本機觀測到 {observed} 種，已收藏 {favorites} 種")
                : client.Loading
                    ? UiText.Pick($"正在读取客户端 Buff 表，暂时只列出本机观测到的 {observed} 种，已收藏 {favorites} 种",
                        $"正在讀取用戶端 Buff 表，暫時只列出本機觀測到的 {observed} 種，已收藏 {favorites} 種")
                    : client.Problem is { } problem
                        ? UiText.Pick($"客户端 Buff 表不可用（{problem}），只列出本机观测到的 {observed} 种，已收藏 {favorites} 种",
                            $"用戶端 Buff 表無法使用（{problem}），只列出本機觀測到的 {observed} 種，已收藏 {favorites} 種")
                        : UiText.Pick($"本机观测到 {observed} 种，已收藏 {favorites} 种", $"本機觀測到 {observed} 種，已收藏 {favorites} 種");
            return favoriteTotal > FavoriteCatalog.Count
                ? source + UiText.Pick($"；符合 {favoriteTotal} 项，只列出前 {FavoriteCatalog.Count} 项，输入更多文字缩小范围",
                    $"；符合 {favoriteTotal} 項，只列出前 {FavoriteCatalog.Count} 項，輸入更多文字縮小範圍")
                : source;
        }
    }

    /// <summary>收藏层打开：请后端适配层读取客户端 Buff 表（已读到且游戏没换进程时沿用），列表随即按现有内容刷新。</summary>
    public void OpenFavorites()
    {
        backend.RequestClientBuffs();
        SyncFavorites();
    }

    // 客户端 Buff 表到达或状态变化：补齐收藏的名称，再刷新收藏层。
    private void OnClientBuffsChanged(object? sender, EventArgs e)
    {
        CombatEffectFavorites.Learn(backend.GetClientBuffs());
        SyncFavorites();
    }

    private void SyncFavorites()
    {
        var result = CombatEffectFavorites.Find(favoriteQuery, backend.GetClientBuffs());
        favoriteTotal = result.Total;
        FavoriteCatalog.Sync(result.Items);
        Raise(nameof(NoFavoriteCatalog)); Raise(nameof(FavoriteEmptyText)); Raise(nameof(FavoriteCatalogNote));
        Raise(nameof(SelfFavoritesOnly)); Raise(nameof(TargetFavoritesOnly));
    }
}

/// <summary>
/// 效果卡片里的一区（正面、负面或未分类）：<see cref="All"/> 是这一区单列排列的条目，顺序由 <see cref="CombatEffectOrder"/> 给出。
/// 同一效果沿用同一个 <see cref="EffectItem"/>，刷新只更新它的 <see cref="EffectItem.Row"/>，列表不重建、行不移动；
/// <see cref="Prune"/> 在倒计时节拍上移除推算已归零的行。
/// </summary>
public sealed class EffectStrip : ObservableObject
{
    public EffectStrip(EffectPolarity polarity) => Polarity = polarity;

    public EffectPolarity Polarity { get; }
    public string Title => Polarity switch
    {
        EffectPolarity.Positive => UiText.Pick("正面", "正面"),
        EffectPolarity.Negative => UiText.Pick("负面", "負面"),
        _ => UiText.Pick("未分类", "未分類")
    };
    public string TitleTip => Polarity switch
    {
        EffectPolarity.Positive => UiText.Pick("客户端效果表标为增益（Buff）的效果", "用戶端效果表標為增益（Buff）的效果"),
        EffectPolarity.Negative => UiText.Pick("客户端效果表标为减益（Debuff）的效果", "用戶端效果表標為減益（Debuff）的效果"),
        _ => UiText.Pick("客户端效果表标为无，或名称表未读取、表里没有这一项，没有极性来源", "用戶端效果表標為無，或名稱表未讀取、表裡沒有這一項，沒有極性來源")
    };
    private EffectRow Sample => sample ??= new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, string.Empty, Polarity);
    private EffectRow? sample;
    /// <summary>标题旁的小五边形，与这一区条目的色标同色同向。</summary>
    public Avalonia.Media.IBrush Fill => Sample.Fill;
    public double Angle => Sample.Angle;
    public bool IsPositive => Polarity == EffectPolarity.Positive;
    public bool IsNegative => Polarity == EffectPolarity.Negative;
    public StableList<EffectItem> All { get; } = [];
    public bool Empty => All.Count == 0;
    public string CountText => All.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private IReadOnlyList<EffectRow> rows = [];
    private readonly Dictionary<string, EffectItem> items = new(StringComparer.Ordinal);

    public void Sync(IReadOnlyList<EffectRow> next)
    {
        rows = next;
        Prune(System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>按 <paramref name="now"/> 去掉推算已归零的行并同步列表；返回列表是否变化。</summary>
    public bool Prune(long now)
    {
        var visible = rows.Where(row => !CombatEffectOrder.Expired(row, now)).ToList();
        var keep = new List<EffectItem>(visible.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in visible)
        {
            var key = row.Scope + "|" + (row.EffectId ?? row.Name);
            if (!keys.Add(key)) continue;
            if (!items.TryGetValue(key, out var item)) items[key] = item = new(key, row);
            else item.Row = row;
            keep.Add(item);
        }
        foreach (var key in items.Keys.Where(key => !keys.Contains(key)).ToList()) items.Remove(key);
        var empty = Empty;
        var changed = All.Sync(keep);
        if (empty != Empty) Raise(nameof(Empty));
        if (changed) Raise(nameof(CountText));
        return changed;
    }
}

/// <summary>效果列表里的一行：同一效果在刷新之间沿用同一个实例，只更新 <see cref="Row"/>。</summary>
public sealed class EffectItem(string key, EffectRow row) : ObservableObject
{
    public string Key { get; } = key;
    private EffectRow row = row;
    public EffectRow Row { get => row; set { if (!Equals(row, value)) { row = value; Raise(nameof(Row)); } } }
}
