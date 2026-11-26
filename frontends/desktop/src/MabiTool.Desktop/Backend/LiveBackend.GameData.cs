using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Character;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Market;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 游戏连接器的数据读取（角色页、背包页资源卡的货币、环境信息与附近演奏浮窗元件）、交易所查价与背包估价。
// 读取只在界面可见时进行：角色页（可见处 character）读货币以外的全部主题，进入页面带 Refresh；背包页（inventory）只读货币；环境信息元件（overlay.environment）只读环境，
// 附近演奏元件（overlay.nearby）只读附近；背包估价（inventory.valuation，背包页可见且估价开关打开）按名称向交易所取价。
// 每个主题按自己的间隔读取（环境与附近 5 秒，属性、货币、任务 30 秒，成长指南 60 秒，估价 60 秒），没有可见使用者时一概不读；
// 读取在后台任务里进行，不占住主循环；后端没有这些接口（旧宿主，404）时本次连接不再探测，读取失败按未读取。
public sealed partial class LiveBackend
{
    /// <summary>一次数据查询之后，同一主题再读的最小间隔（界面请求刷新时也不早于这个间隔的一部分，见 <see cref="ForcedSpacing"/>）。</summary>
    internal static TimeSpan DataInterval(string topic) => topic switch
    {
        GameDataTopics.Environment or GameDataTopics.Nearby => TimeSpan.FromSeconds(5),
        GameDataTopics.GrowthGuide => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromSeconds(30)
    };

    /// <summary>进入页面或点“刷新”时同一主题两次读取的最小间隔（与后端一致）。</summary>
    internal static readonly TimeSpan ForcedSpacing = TimeSpan.FromSeconds(3);

    /// <summary>估价的更新间隔；背包内容变化时最早 5 秒后重新估价。</summary>
    internal static readonly TimeSpan ValuationInterval = TimeSpan.FromSeconds(60), ValuationChangeSpacing = TimeSpan.FromSeconds(5);

    private const int ValuationBatch = 200;

    private GameDataSnapshot? gameData;
    private bool gameDataUnsupported, characterShown, inventoryCurrenciesShown, environmentShown, nearbyShown;
    private int gameDataGeneration;
    private Task? gameDataTask;
    private readonly Dictionary<string, long> topicReadAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> topicForced = new(StringComparer.Ordinal);
    private readonly Memo<Pages.Character.CharacterPageData> characterMemo = new();
    private readonly Memo<EnvironmentView> environmentMemo = new();
    private readonly Memo<NearbyView> nearbyMemo = new();

    // 估价：请求过的名称与数量、已读到的行、取价时刻与整体读不到的原因。
    private bool valuationShown, valuationUnsupported, valuationForced;
    private Task? valuationTask;
    private string valuationSignature = string.Empty;
    private long valuationReadAt;
    private Dictionary<string, GameMarketValuationRow> valuationRows = new(StringComparer.Ordinal);
    private DateTimeOffset? valuationPricedAt;
    private string? valuationFailure;
    private InventoryValuationView valuationView = InventoryValuationView.Off;

    /// <summary>新连接：数据与估价的缓存作废，下一轮重新读取。</summary>
    private void ResetGameData()
    {
        lock (gate)
        {
            gameData = null; gameDataUnsupported = false; gameDataGeneration++;
            topicReadAt.Clear(); topicForced.Clear(); characterShown = inventoryCurrenciesShown = environmentShown = nearbyShown = false;
            valuationUnsupported = false; valuationShown = false; valuationRows = new(StringComparer.Ordinal);
            valuationPricedAt = null; valuationFailure = null; valuationSignature = string.Empty; valuationReadAt = 0;
            valuationView = InventoryValuationView.Off;
        }
    }

    // ---- 角色页、环境、附近 ----

    /// <summary>角色页读取的主题：货币（归背包页的资源卡）与附近玩家（只供附近演奏字幕）以外的全部。</summary>
    internal static readonly string[] CharacterTopics = [.. GameDataTopics.All.Where(topic => topic is not GameDataTopics.Currencies and not GameDataTopics.Nearby)];

    // 调用方持有 gate。可见处刚出现的主题标记为需要立即（带 Refresh）读取。
    private void SyncGameDataSurfaces()
    {
        void Track(string surface, ref bool shown, params string[] topics)
        {
            var now = surfaces.GetValueOrDefault(surface) > 0;
            if (now && !shown) foreach (var topic in topics) topicForced.Add(topic);
            shown = now;
        }
        Track("character", ref characterShown, CharacterTopics);
        Track("inventory", ref inventoryCurrenciesShown, GameDataTopics.Currencies);
        Track("overlay.environment", ref environmentShown, GameDataTopics.Environment);
        Track("overlay.nearby", ref nearbyShown, GameDataTopics.Nearby);
    }

    // 调用方持有 gate。当前有可见使用者的主题；没有可见处报告（测试、样本）时一概不读。
    private HashSet<string> WantedTopics()
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (surfaces.GetValueOrDefault("character") > 0) wanted.UnionWith(CharacterTopics);
        if (surfaces.GetValueOrDefault("inventory") > 0) wanted.Add(GameDataTopics.Currencies);
        if (surfaces.GetValueOrDefault("overlay.environment") > 0) wanted.Add(GameDataTopics.Environment);
        if (surfaces.GetValueOrDefault("overlay.nearby") > 0) wanted.Add(GameDataTopics.Nearby);
        return wanted;
    }

    /// <summary>测试用：当前有可见使用者的数据主题（没有可见处时为空）。</summary>
    internal IReadOnlyList<string> WantedGameDataTopics
    {
        get { lock (gate) return WantedTopics().Order(StringComparer.Ordinal).ToList(); }
    }

    // 调用方持有 gate。某个主题下一次读取的时刻：没读过或进入页面时立即（同一主题两次读取至少相隔 ForcedSpacing），否则按主题间隔。
    private long TopicDueAt(string topic, long now)
    {
        topicReadAt.TryGetValue(topic, out var last);
        if (last == 0) return now;
        return topicForced.Contains(topic) ? last + Span(ForcedSpacing) : last + Span(DataInterval(topic));
    }

    // 调用方持有 gate。数据读取与估价下一次到期的时刻（NextWork 用）；没有要做的时为空。
    private long? GameDataDueAt(long now)
    {
        long? next = null;
        SyncGameDataSurfaces();
        if (!gameDataUnsupported && gameDataTask is not { IsCompleted: false })
            foreach (var topic in WantedTopics())
            {
                var at = TopicDueAt(topic, now);
                next = next is null ? at : Math.Min(next.Value, at);
            }
        if (ValuationDueAt(now) is { } valuation) next = next is null ? valuation : Math.Min(next.Value, valuation);
        return next;
    }

    private void RefreshGameData(MabiToolClient api, CancellationToken token)
    {
        List<string> topics;
        bool refresh;
        int generation;
        lock (gate)
        {
            if (gameDataUnsupported || gameDataTask is { IsCompleted: false }) { StartValuationIfDue(api, token); return; }
            SyncGameDataSurfaces();
            var now = Now;
            topics = WantedTopics().Where(topic => TopicDueAt(topic, now) <= now).ToList();
            if (topics.Count == 0) { StartValuationIfDue(api, token); return; }
            refresh = topics.Any(topicForced.Contains);
            foreach (var topic in topics) { topicForced.Remove(topic); topicReadAt[topic] = now; }
            generation = gameDataGeneration;
            gameDataTask = Task.Run(() => ReadGameDataAsync(api, topics, refresh, generation, token), token);
        }
        lock (gate) StartValuationIfDue(api, token);
    }

    private async Task ReadGameDataAsync(MabiToolClient api, List<string> topics, bool refresh, int generation, CancellationToken token)
    {
        GameDataSnapshot? snapshot = null;
        var unsupported = false;
        try { snapshot = await api.QueryGameDataAsync(new(topics, refresh), token); }
        catch (MabiToolApiException error) { unsupported = error.Status == 404; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ObjectDisposedException or IOException
                                          or InvalidOperationException or JsonException) { }
        bool changed;
        lock (gate)
        {
            if (generation != gameDataGeneration) return;
            changed = ApplyGameData(snapshot, unsupported, topics);
        }
        if (changed) Notify();
        Signal();
    }

    // 调用方持有 gate。读取成功按主题合并（内容没变的主题保留原对象），读取失败把已有内容标为旧数据；返回显示的内容是否变化。
    private bool ApplyGameData(GameDataSnapshot? snapshot, bool unsupported, IReadOnlyList<string> topics)
    {
        if (unsupported)
        {
            var was = gameDataUnsupported;
            gameDataUnsupported = true;
            return !was;
        }
        var before = gameData;
        gameData = snapshot is null ? MarkStale(gameData, topics) : Merge(gameData, snapshot);
        return !ReferenceEquals(before, gameData);
    }

    private static T? Keep<T>(T? old, T? next) where T : class
    {
        if (next is null) return old;
        return old is not null && JsonSerializer.SerializeToUtf8Bytes(old, ContractJson.Options).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(next, ContractJson.Options)) ? old : next;
    }

    internal static GameDataSnapshot Merge(GameDataSnapshot? old, GameDataSnapshot next)
    {
        var merged = new GameDataSnapshot(next.BackendEpoch, Keep(old?.Profile, next.Profile), Keep(old?.Currencies, next.Currencies),
            Keep(old?.Missions, next.Missions), Keep(old?.GrowthGuide, next.GrowthGuide), Keep(old?.Environment, next.Environment),
            Keep(old?.Nearby, next.Nearby));
        return old is not null && ReferenceEquals(old.Profile, merged.Profile) && ReferenceEquals(old.Currencies, merged.Currencies) &&
            ReferenceEquals(old.Missions, merged.Missions) && ReferenceEquals(old.GrowthGuide, merged.GrowthGuide) &&
            ReferenceEquals(old.Environment, merged.Environment) && ReferenceEquals(old.Nearby, merged.Nearby) ? old : merged;
    }

    /// <summary>读取失败：请求过的主题已有的内容标为旧数据（原因：读取失败），没有内容的主题仍为未读取。</summary>
    internal static GameDataSnapshot? MarkStale(GameDataSnapshot? data, IReadOnlyList<string> topics)
    {
        if (data is null) return null;
        GameDataStatus Stale(GameDataStatus status) => status.Stale ? status : status with { Stale = true, Reason = status.Reason ?? "gameCli.readFailed" };
        GameMissionList Mission(GameMissionList list) => list with { Status = Stale(list.Status) };
        var missions = topics.Contains(GameDataTopics.Missions) && data.Missions is { } value
            ? value with { Daily = Mission(value.Daily), Weekly = Mission(value.Weekly) } : data.Missions;
        return data with
        {
            Profile = topics.Contains(GameDataTopics.Profile) && data.Profile is { } profile ? profile with { Status = Stale(profile.Status) } : data.Profile,
            Currencies = topics.Contains(GameDataTopics.Currencies) && data.Currencies is { } currencies ? currencies with { Status = Stale(currencies.Status) } : data.Currencies,
            Missions = missions,
            GrowthGuide = topics.Contains(GameDataTopics.GrowthGuide) && data.GrowthGuide is { } guide ? guide with { Status = Stale(guide.Status) } : data.GrowthGuide,
            Environment = topics.Contains(GameDataTopics.Environment) && data.Environment is { } environment ? environment with { Status = Stale(environment.Status) } : data.Environment,
            Nearby = topics.Contains(GameDataTopics.Nearby) && data.Nearby is { } nearby ? nearby with { Status = Stale(nearby.Status) } : data.Nearby
        };
    }

    public Pages.Character.CharacterPageData GetCharacter()
    {
        var (current, _) = Read();
        if (!current.Connected) return Pages.Character.CharacterPageData.Offline;
        GameDataSnapshot? data;
        bool unsupported;
        lock (gate) { data = gameData; unsupported = gameDataUnsupported; }
        if (unsupported) return Pages.Character.CharacterPageData.Unsupported;
        if (data is null) return Pages.Character.CharacterPageData.NotReadYet;
        return characterMemo.Get([data.Profile, data.Missions, data.GrowthGuide, data.Environment, data.Nearby, Localization.UiText.Language],
            () => CharacterMapper.Map(CharacterRead.Loaded, data));
    }

    public EnvironmentView GetEnvironment()
    {
        var (current, _) = Read();
        GameEnvironmentData? data;
        lock (gate) data = gameData?.Environment;
        if (!current.Connected || data is null) return EnvironmentView.NotRead;
        return environmentMemo.Get([data, Localization.UiText.Language], () => CharacterMapper.Environment(data));
    }

    public NearbyView GetNearby()
    {
        var (current, _) = Read();
        GameNearbyData? data;
        lock (gate) data = gameData?.Nearby;
        if (!current.Connected || data is null) return NearbyView.NotRead;
        return nearbyMemo.Get([data, Localization.UiText.Language], () => CharacterMapper.Nearby(data));
    }

    // 角色页的“刷新”：下一轮带 Refresh 读取角色页的全部主题（同一主题两次读取至少相隔 3 秒）。
    private void RequestCharacterRefresh()
    {
        lock (gate) foreach (var topic in CharacterTopics) topicForced.Add(topic);
        MarkDue(Due.Status, immediate: true);
    }

    // ---- 交易所查价 ----

    public async Task<MarketPricesView> QueryMarketPricesAsync(MarketPriceRequest request, CancellationToken cancellationToken)
    {
        if (request.Problem is { } problem)
            return MarketPricesView.None with { Reason = problem, Message = MarketText.Reason(problem) };
        var api = client;
        if (!Read().State.Connected || api is null)
            return MarketPricesView.None with { Reason = "gameCli.notConnected", Message = Pick("未连接后端", "未連接後端") };
        try { return MarketMapper.Prices(await api.QueryGameMarketPricesAsync(request.ToQuery(), cancellationToken)); }
        catch (MabiToolApiException error) { return Failure<MarketPricesView>(error, (reason, message) => MarketPricesView.None with { Reason = reason, Message = message }); }
        catch (Exception error) when (error is HttpRequestException or ObjectDisposedException or IOException or JsonException)
        {
            return MarketPricesView.None with { Reason = "gameCli.notConnected", Message = Localization.CodeText.Error(error) };
        }
    }

    public async Task<MarketDetailView> QueryMarketDetailAsync(MarketDetailRequest request, CancellationToken cancellationToken)
    {
        var api = client;
        if (!Read().State.Connected || api is null)
            return MarketDetailView.None with { Reason = "gameCli.notConnected", Message = Pick("未连接后端", "未連接後端") };
        try { return MarketMapper.Detail(await api.QueryGameMarketPriceDetailAsync(request.ToQuery(), cancellationToken)); }
        catch (MabiToolApiException error) { return Failure<MarketDetailView>(error, (reason, message) => MarketDetailView.None with { Reason = reason, Message = message }); }
        catch (Exception error) when (error is HttpRequestException or ObjectDisposedException or IOException or JsonException)
        {
            return MarketDetailView.None with { Reason = "gameCli.notConnected", Message = Localization.CodeText.Error(error) };
        }
    }

    private static T Failure<T>(MabiToolApiException error, Func<string, string, T> make)
    {
        var reason = error.Problem?.Code ?? $"http.{error.Status}";
        return make(reason, MarketText.Reason(reason));
    }

    // ---- 背包估价 ----

    public InventoryValuationView GetInventoryValuation()
    {
        lock (gate) return valuationShown ? valuationView : InventoryValuationView.Off;
    }

    // 调用方持有 gate。估价请求的道具：透视表里有名称、持有数量大于 0 的行，同名合并；乐谱实例不上交易所。
    private List<GameMarketValuationItem> ValuationItems() => (pages.Pivot?.Rows ?? [])
        .Where(row => row.Score is null && row.Category != "MusicNoteItem" && row.RawName is { Length: > 0 } && row.Total is { State: InventoryCountState.Value or InventoryCountState.Partial, Quantity: > 0 })
        .GroupBy(row => row.RawName!, StringComparer.Ordinal)
        .Select(group => new GameMarketValuationItem(group.Key, (int)Math.Min(int.MaxValue, group.Sum(row => (long)row.Total.Quantity!.Value))))
        .OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToList();

    private static string SignatureOf(IReadOnlyList<GameMarketValuationItem> items) =>
        string.Join('\u0001', items.Select(item => item.DisplayName + '\u0002' + item.Count));

    // 调用方持有 gate。估价下一次到期的时刻：估价可见（可见处 inventory.valuation）时，首次、开关刚打开、背包内容变化（至少隔 5 秒）或每 60 秒。
    private long? ValuationDueAt(long now)
    {
        var shown = surfaces.GetValueOrDefault("inventory.valuation") > 0;
        if (!shown) { valuationShown = false; return null; }
        if (!valuationShown) { valuationShown = true; valuationForced = true; valuationView = Pending(); }
        if (valuationUnsupported || valuationTask is { IsCompleted: false }) return null;
        var items = ValuationItems();
        var changed = SignatureOf(items) != valuationSignature;
        if (valuationReadAt == 0 || valuationForced) return now;
        if (changed) return valuationReadAt + Span(ValuationChangeSpacing);
        return valuationReadAt + Span(ValuationInterval);
    }

    private InventoryValuationView Pending() => ValuationPresentation(ValuationItems(), valuationRows, valuationPricedAt, valuationFailure);

    private static InventoryValuationView ValuationPresentation(IReadOnlyList<GameMarketValuationItem> items,
        IReadOnlyDictionary<string, GameMarketValuationRow> rows, DateTimeOffset? pricedAt, string? failure) =>
        ValuationMapper.Map(items.Select(item => item.DisplayName).ToList(), rows, pricedAt, failure);

    // 调用方持有 gate。
    private void StartValuationIfDue(MabiToolClient api, CancellationToken token)
    {
        if (ValuationDueAt(Now) is not { } at || at > Now) return;
        var items = ValuationItems();
        valuationForced = false;
        valuationReadAt = Now;
        valuationSignature = SignatureOf(items);
        // 背包还没读到（没有可估价的道具）时不发请求；背包出现后内容变化，下一次估价最早 5 秒后到期。
        if (items.Count == 0)
        {
            valuationView = ValuationPresentation(items, valuationRows, valuationPricedAt, valuationFailure);
            return;
        }
        var generation = gameDataGeneration;
        valuationTask = Task.Run(() => ReadValuationAsync(api, items, generation, token), token);
    }

    private async Task ReadValuationAsync(MabiToolClient api, IReadOnlyList<GameMarketValuationItem> items, int generation, CancellationToken token)
    {
        var rows = new Dictionary<string, GameMarketValuationRow>(StringComparer.Ordinal);
        DateTimeOffset? pricedAt = null;
        string? failure = null;
        var unsupported = false;
        var names = items.Select(item => item.DisplayName).ToHashSet(StringComparer.Ordinal);
        lock (gate) foreach (var (name, row) in valuationRows) if (names.Contains(name)) rows[name] = row;
        foreach (var chunk in items.Chunk(ValuationBatch))
        {
            try
            {
                var result = await api.QueryGameMarketValuationAsync(new(chunk), token);
                failure = result.Reason;
                if (result.PricedAt is { } at && (pricedAt is null || at < pricedAt)) pricedAt = at;
                foreach (var row in result.Rows) rows[row.DisplayName] = row;
            }
            catch (MabiToolApiException error)
            {
                unsupported = error.Status == 404;
                failure = error.Problem?.Code ?? $"http.{error.Status}";
                break;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ObjectDisposedException or IOException or JsonException)
            {
                failure = "gameCli.notConnected";
                break;
            }
            Publish();
        }
        Publish();
        bool changed;
        lock (gate)
        {
            if (generation != gameDataGeneration) return;
            if (unsupported) valuationUnsupported = true;
            changed = true;
        }
        if (changed) Notify();
        Signal();

        void Publish()
        {
            lock (gate)
            {
                if (generation != gameDataGeneration) return;
                valuationRows = rows;
                valuationPricedAt = pricedAt ?? valuationPricedAt;
                valuationFailure = failure;
                valuationView = ValuationPresentation(items, rows, valuationPricedAt, failure);
            }
        }
    }
}
