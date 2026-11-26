using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Character;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Market;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的游戏数据、交易所查价与背包估价：固定样本经与真实后端相同的映射显示，来源标为“样本数据”；
// 估价与可见处的开关行为同真实后端一致（估价开关没有打开时不给结果）。
public sealed partial class SampleBackend
{
    private readonly Dictionary<string, int> surfaceCounts = new(StringComparer.Ordinal);
    private GameDataSnapshot? characterSample;
    private (GameDataSnapshot? Data, Localization.UiLanguage Language, Pages.Character.CharacterPageData Page)? characterMapped;

    /// <summary>测试用：界面各处报告的可见处（成对按次数计），例如 character、overlay.environment、inventory.valuation。</summary>
    internal IReadOnlyDictionary<string, int> VisibleSurfaces => surfaceCounts;

    public void SetSurfaceVisible(string surface, bool visible)
    {
        surfaceCounts[surface] = Math.Max(0, surfaceCounts.GetValueOrDefault(surface) + (visible ? 1 : -1));
        SurfaceChanges.Add((surface, visible));
    }

    /// <summary>测试用：可见处的变化，按先后记录。</summary>
    internal List<(string Surface, bool Visible)> SurfaceChanges { get; } = [];

    /// <summary>测试用：角色页的数据；为空时用固定样本。改动后调用 <see cref="RaiseChanged"/>。</summary>
    internal GameDataSnapshot? CharacterData { get; set; }

    /// <summary>测试用：角色页“刷新”意图发出的次数。</summary>
    internal int CharacterRefreshes { get; private set; }

    /// <summary>测试用：角色页的读取情况（默认已读到）。</summary>
    internal CharacterRead? CharacterReadOverride { get; set; }

    private GameDataSnapshot SampleGameData()
    {
        var now = DateTimeOffset.Now;
        var status = new GameDataStatus(GameDataSource.Connector, null, now, false, []);
        GameMission Mission(string title, string description, int current, int goal, bool done, bool received) =>
            new(title, description, current, goal, done, received, false);
        GameMissionList Missions(params GameMission[] items) => new(status, items);
        return characterSample ??= new("sample",
            new(status, new("艾薇", "樂團的新星", "愛麗沙", 65, "魔法師",
                [new("CombatScore", "戰鬥力", 32503), new("LivingScore", "生活力", 16369), new("AttackPower", "攻擊力", 15959),
                 new("DefencePower", "防禦力", 8685), new("STR", "力量", 2215), new("DEX", "技巧", 2251), new("INT", "智力", 2678),
                 new("LUCK", "幸運", 2394), new("WILL", "意志", 2003)],
                [new("PaladinAttackPower", "神聖力", 0), new("JusticePower", "正義", 0)],
                new(24086, 24086, 0, 0, 0, 100, 0, 696.387634, 1110, 1),
                [new("日常採集", 18, 3286, 3725), new("伐木", 28, 19378, 21040), new("釣魚", 22, 9293, 12180), new("料理", 19, 2812, 4875),
                 new("鍊金術", 10, null, null)])),
            new(status, [new("金幣", 12345678), new("銀幣", 1520), new("魔族貢品", 87)]),
            new(Missions(Mission("擊敗哥布林", "擊敗 20 隻哥布林", 12, 20, false, false), Mission("採集原木", "採集 30 個原木", 30, 30, true, false),
                    Mission("演奏一次", "在營火旁演奏一首樂曲", 1, 1, true, true), Mission("完成副本", "通關任一副本", 0, 1, false, false)),
                Missions(Mission("本週討伐", "討伐 100 隻怪物", 64, 100, false, false), Mission("本週演奏", "演奏 10 次", 10, 10, true, true))),
            new(status, false, null, [new("每日副本", "Daily", true, false, 1, null), new("每週團隊副本", "Weekly", true, true, 0, null),
                new("深淵挑戰", "Weekly", false, null, null, "角色等級達 70")]),
            new(new(GameDataSource.Connector, null, now, false, []), new("庫漢", "庫漢", 12.0674438, 52.9489059, "Sunny", "2968-6-08 13:13",
                new(false, false, true, string.Empty, false))),
            new(status, [new("愛麗沙", "新手冒險家", 6.2, 41, "劍士", false, false, false, false, false,
                    new(true, "月光下的艾明馬夏", true, true, 240, 92, 148, 3)),
                new("愛麗沙", null, 11.8, 65, "魔法師", true, true, true, true, false, new(false, null, null, null, null, null, null, null)),
                new("愛麗沙", null, 25.5, 58, "弓手", false, false, false, false, true, null)]));
    }

    public Pages.Character.CharacterPageData GetCharacter()
    {
        if (Offline) return Pages.Character.CharacterPageData.Offline;
        var data = CharacterData ?? SampleGameData();
        var key = Localization.UiText.Language;
        if (characterMapped is { } cached && ReferenceEquals(cached.Data, data) && cached.Language == key) return cached.Page;
        var page = CharacterMapper.Map(CharacterReadOverride ?? CharacterRead.Loaded, data, sample: CharacterData is null);
        characterMapped = (data, key, page);
        return page;
    }

    public EnvironmentView GetEnvironment() => Offline ? EnvironmentView.NotRead : GetCharacter().Environment;

    public NearbyView GetNearby() => Offline ? NearbyView.NotRead : GetCharacter().Nearby;

    // ---- 估价 ----

    private InventoryValuationView? sampleValuation;
    private string? sampleValuationKey;

    /// <summary>测试用：估价的固定结果；为空时按样本背包的名称生成。</summary>
    internal InventoryValuationView? ValuationOverride { get; set; }

    public InventoryValuationView GetInventoryValuation()
    {
        if (surfaceCounts.GetValueOrDefault("inventory.valuation") == 0) return InventoryValuationView.Off;
        if (ValuationOverride is not null) return ValuationOverride;
        var inventory = GetInventory();
        var key = inventoryAccount + "|" + Localization.UiText.Language;
        if (sampleValuation is not null && sampleValuationKey == key) return sampleValuation;
        var rows = new Dictionary<string, GameMarketValuationRow>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var row in inventory.Rows.Where(row => row.RawName is { Length: > 0 }))
        {
            var count = (int)Math.Min(int.MaxValue, row.Total.Quantity ?? 0);
            names.Add(row.RawName!);
            var (state, unit) = row.RawName switch
            {
                "魯特琴" => (GameMarketValuationState.Priced, 18000d),
                "豎琴" => (GameMarketValuationState.Priced, 22000d),
                "長笛" => (GameMarketValuationState.Priced, 15000d),
                "生命藥水 100" => (GameMarketValuationState.Priced, 120d),
                "冬夜禮服" => (GameMarketValuationState.MultipleVariants, 0d),
                "染色安瓶" => (GameMarketValuationState.NoStock, 0d),
                _ => (GameMarketValuationState.NotTradable, 0d)
            };
            rows[row.RawName!] = state == GameMarketValuationState.Priced
                ? new(row.RawName!, count, state, unit, unit * count, null) : new(row.RawName!, count, state, null, null, null);
        }
        sampleValuationKey = key;
        return sampleValuation = ValuationMapper.Map(names, rows, DateTimeOffset.Now, null);
    }

    // ---- 交易所查价 ----

    private static readonly GameMarketRow[] MarketTable =
    [
        new("原木", null, null, null, null, false, true, 38), new("堅硬原木", null, null, null, null, false, true, 95),
        new("魯特琴", "稀有", null, null, null, false, true, 18000), new("豎琴", "稀有", null, null, null, true, true, 22000),
        new("冬夜禮服", "傳說", 2, "S1", "防禦 120–150", false, true, 340000), new("冬夜禮服", "傳說", 3, "S1", "防禦 150–180", false, true, 520000),
        new("染色安瓶", null, null, null, null, false, false, null)
    ];

    /// <summary>测试用：收到的价目查询与明细查询，按先后记录。</summary>
    internal List<MarketPriceRequest> MarketPriceRequests { get; } = [];
    internal List<MarketDetailRequest> MarketDetailRequests { get; } = [];

    /// <summary>测试用：下一次明细查询以“查询太频繁”拒绝，等待这么多秒（用后清空）。</summary>
    internal int? MarketDetailRateLimit { get; set; }

    public Task<MarketPricesView> QueryMarketPricesAsync(MarketPriceRequest request, CancellationToken cancellationToken)
    {
        MarketPriceRequests.Add(request);
        if (Offline) return Task.FromResult(MarketPricesView.None with { Reason = "gameCli.notConnected", Message = Pick("未连接后端", "未連接後端") });
        if (request.Problem is { } problem) return Task.FromResult(MarketPricesView.None with { Reason = problem, Message = MarketText.Reason(problem) });
        var now = DateTimeOffset.Now;
        IReadOnlyList<GameMarketRow> All() => MarketTable;
        var result = request.Mode switch
        {
            MarketMode.Names => new GameMarketPrices("sample", "names", null, null, null, [], now, false, null, null,
                [.. request.Names.Select(name =>
                {
                    var rows = MarketTable.Where(row => row.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    return new GameMarketNameResult(name, rows.Count > 0, rows);
                })], []),
            MarketMode.Favorites => new("sample", "favorites", null, null, null, [], now, false, null, null, [], [.. MarketTable.Where(row => row.Favorite)]),
            _ when request.Category == "樂器" => new("sample", "category", null, null, null, [], now, false, request.Category, request.SubCategory, [],
                [.. MarketTable.Where(row => row.DisplayName is "魯特琴" or "豎琴")]),
            _ => new("sample", "category", "gameCli.categoryNotFound", null, null, ["樂器", "材料", "服裝"], null, false, request.Category, request.SubCategory, [], All())
        };
        return Task.FromResult(MarketMapper.Prices(result));
    }

    public Task<MarketDetailView> QueryMarketDetailAsync(MarketDetailRequest request, CancellationToken cancellationToken)
    {
        MarketDetailRequests.Add(request);
        if (Offline) return Task.FromResult(MarketDetailView.None with { Reason = "gameCli.notConnected", Message = Pick("未连接后端", "未連接後端") });
        if (MarketDetailRateLimit is { } wait)
        {
            MarketDetailRateLimit = null;
            return Task.FromResult(MarketMapper.Detail(new("sample", "gameCli.rateLimited", null, wait, [], null, null, null)));
        }
        var row = MarketTable.FirstOrDefault(item => item.DisplayName == request.Name && item.Rarity == request.Rarity && item.Star == request.Star && item.Season == request.Season);
        if (row is null) return Task.FromResult(MarketMapper.Detail(new("sample", "gameCli.notFound", null, null, [], null, null, null)));
        var unit = row.MinUnitPrice ?? 0;
        GameMarketPriceDetail detail = row.InStock
            ? new("sample", null, null, null, [], DateTimeOffset.Now,
                new(row.DisplayName, row.Rarity, row.Star, row.Season, row.MainAttributeRange, unit, unit * 3, unit * 1.4,
                    [new(unit, 3), new(unit + unit / 10, 5), new(unit + unit / 4, 10)]), null)
            : new("sample", null, null, null, [], DateTimeOffset.Now,
                new(row.DisplayName, row.Rarity, row.Star, row.Season, row.MainAttributeRange, null, null, null, []), null);
        return Task.FromResult(MarketMapper.Detail(detail));
    }
}
