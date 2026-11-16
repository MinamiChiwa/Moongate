using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 把进图通知的频道投影为公开位置。地图名称表在首次需要时于线程池读取一次并缓存到本次捕获结束，
/// 读取期间与失败后地图名为空并给出原因，不阻塞抓包循环，也不在同一次捕获中反复重读。
/// </summary>
public sealed class MapLocationResolver(Func<CancellationToken, IReadOnlyDictionary<int, ClientMapEntry>> load, CancellationToken cancellationToken)
{
    public const string CatalogPending = "location.mapCatalogPending", CatalogUnavailable = "location.mapCatalogUnavailable",
        MapUnknown = "location.mapUnknown", DisplayUnverified = "location.channelDisplayUnverified",
        NotPermanent = "location.channelNotPermanent", RuleUnknown = "location.channelRuleUnknown",
        IndexUnresolved = "location.channelIndexUnresolved";
    private Task<IReadOnlyDictionary<int, ClientMapEntry>>? catalog;

    public ObservedLocation? Resolve(GameChannel? channel, int? realmId)
    {
        if (channel is null) return null;
        catalog ??= Task.Run(() => load(cancellationToken), cancellationToken);
        if (!catalog.IsCompleted) return new(channel.SheetId, null, null, CatalogPending, null, null, RuleUnknown);
        if (!catalog.IsCompletedSuccessfully)
        {
            _ = catalog.Exception;
            return new(channel.SheetId, null, null, CatalogUnavailable, null, null, RuleUnknown);
        }
        if (!catalog.Result.TryGetValue(channel.SheetId, out var entry)) return new(channel.SheetId, null, null, MapUnknown, null, null, RuleUnknown);
        var (index, reason) = !entry.PermanentChannel ? ((int?)null, NotPermanent) : PermanentIndex(channel.UniqueNumber, realmId);
        return new(channel.SheetId, entry.DisplayName, entry.GameSpaceSheetId, null, entry.PermanentChannel, index, reason);
    }

    /// <summary>
    /// 客户端永久频道编号为 Int32 的 realmId × 10000 + 序号再符号扩展为 Int64；实例编号不在该范围或 Realm 与本机不符时不推出序号。
    /// 序号与游戏界面频道号的对应（是否从 0 起、是否有偏移）尚无实机对照，因此只公开序号，不给显示频道号。
    /// </summary>
    private static (int?, string) PermanentIndex(long unique, int? realmId)
    {
        if (unique is < 0 or > int.MaxValue) return (null, IndexUnresolved);
        if (realmId is { } realm && unique / 10000 != realm) return (null, IndexUnresolved);
        return ((int)(unique % 10000), DisplayUnverified);
    }
}
