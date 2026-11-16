// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Buffers.Binary;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.Providers.TwObservation.Inventory;

/// <summary>
/// 钱包资源的消息解码（台服 2026-11 客户端，协议号按名称从客户端布局读取）。
/// <list type="bullet">
/// <item><c>REPLICATION_Wallet_Change&lt;字段&gt;_REPL</c>：负载为该字段的新值（int64）；ChangeAbyssRaidMoney 为 AbyssRaidMoneyType（uint8）+ 新值。</item>
/// <item><c>CHANNEL_OnMoneyIncreased_NTF</c>／<c>OnMoneyDecreased_NTF</c>：MoneyType（int32）+ 变化量（int64）。</item>
/// <item><c>REPLICATION_SilverCoin_UpdateSilverCoinRefillData_REPL</c>、<c>REPLICATION_PassDeep_UpdateLastUpdatedTime_REPL</c>：上次补充时间（RealTime，Unix 毫秒）。</item>
/// <item><c>CHANNEL_NotifyCurrencyRefilled_NTF</c>：发送者 ChannelObjectId（uint64）+ CurrencyInfoSheetId（uint32）。</item>
/// </list>
/// 钱包字段与 MoneyType 的对应按名称：M 币与德卡、韦卡各有付费／免费两个字段，名称表只有一个 MoneyType。
/// </summary>
public static class WalletProtocol
{
    /// <summary>钱包字段（键）→（Change 方法后缀，MoneyType 名称）。</summary>
    public static IReadOnlyList<(string Key, string Method, string MoneyType)> Fields { get; } =
    [
        ("goldFree", "GoldFree", "Gold"), ("goldPaid", "GoldPaid", "Gold"),
        ("silverCoin", "SilverCoin", "SilverCoin"), ("passDeep", "PassDeep", "Pass_Deep"),
        ("heartToken", "HeartToken", "Coin_SouthRegionToken"), ("guildCoin", "GuildCoin", "GuildCoin"),
        ("catToken", "CatToken", "Coin_CatToken"), ("renewalTicket", "RenewalTicket", "RenewalTicket"),
        ("monsterKillToken", "MonsterKillToken", "Coin_MonsterKillToken"),
        ("wecaFree", "WecaFree", "Weca"), ("wecaPaid", "WecaPaid", "Weca"),
        ("redSoul", "RedSoul", "RedSoul"), ("blueSoul", "BlueSoul", "BlueSoul"), ("yellowSoul", "YellowSoul", "YellowSoul"),
        ("memorialSagaCoin", "MemorialSagaCoin", "MemorialSagaCoin"), ("afkGauge", "AFKGauge", "AFKGauge"),
        ("decaFree", "DecaFree", "RedCrystal"), ("decaPaid", "DecaPaid", "RedCrystal"),
        ("mCashFree", "MCashFree", "FreeCrystal"), ("mCashPaid", "MCashPaid", "Crystal"),
        ("cashShopPoint", "CashShopPoint", "Mileage"), ("packagePoint", "PackagePoint", "SpecialCoin"),
    ];
    /// <summary>AbyssRaidMoneyType（uint8）成员名，按客户端枚举顺序；与同名 MoneyType 对应。</summary>
    public static IReadOnlyList<string> AbyssRaidTypes { get; } =
    [
        "FossilofAbyss", "FossilofAbyssHard", "FossilofAbyssBari", "FossilofAbyssAncient", "AbyssBariToken", "FossilofAbyssRunda",
        "RaidGlasgavelenToken", "RaidTabhartasToken", "RaidTabhartasKillToken", "RaidGairechToken",
    ];
    /// <summary>MoneyType（int32）成员名，按客户端枚举值 0–32。</summary>
    public static IReadOnlyList<string> MoneyTypes { get; } =
    [
        "Invalid", "Crystal", "RedCrystal", "Gold", "RebirthStone", "Mileage", "SpecialCoin", "GuildCoin", "CharacterSlotExpansionTicket",
        "SilverCoin", "Pass_Deep", "FossilofAbyss", "Coin_CatToken", "RenewalTicket", "Coin_MonsterKillToken", "Coin_SouthRegionToken",
        "RaidGlasgavelenToken", "FossilofAbyssHard", "FossilofAbyssBari", "FossilofAbyssAncient", "RaidTabhartasToken", "Weca",
        "AbyssBariToken", "RaidTabhartasKillToken", "InfiniteRebirthStone", "RedSoul", "BlueSoul", "YellowSoul", "FreeCrystal",
        "FossilofAbyssRunda", "RaidGairechToken", "MemorialSagaCoin", "AFKGauge",
    ];
    public const string AbyssPrefix = "abyssRaid:";
    public const string IncreasedName = "CHANNEL_OnMoneyIncreased_NTF", DecreasedName = "CHANNEL_OnMoneyDecreased_NTF",
        RefilledName = "CHANNEL_NotifyCurrencyRefilled_NTF", SilverRefillName = "REPLICATION_SilverCoin_UpdateSilverCoinRefillData_REPL",
        PassDeepRefillName = "REPLICATION_PassDeep_UpdateLastUpdatedTime_REPL", AbyssName = "REPLICATION_Wallet_ChangeAbyssRaidMoney_REPL";
    /// <summary>布局需要登记的非 Wallet 前缀协议名（Wallet 复制帧已按名称包含 Wallet 登记）。</summary>
    public static IReadOnlyList<string> ExtraProtocolNames { get; } = [IncreasedName, DecreasedName, RefilledName, SilverRefillName, PassDeepRefillName];

    /// <summary>MoneyType 名称对应的默认资源键（增减通知没有付费／免费之分时使用）。</summary>
    public static string? DefaultKey(string moneyType) => moneyType switch
    {
        "Gold" => "goldFree", "Weca" => "wecaFree", "RedCrystal" => "decaFree",
        _ => AbyssRaidTypes.Contains(moneyType) ? AbyssPrefix + moneyType : Fields.FirstOrDefault(f => f.MoneyType == moneyType).Key,
    };

    /// <summary>资源键 → MoneyType 名称；未知键为空。</summary>
    public static string? MoneyTypeOf(string key) => key.StartsWith(AbyssPrefix, StringComparison.Ordinal)
        ? AbyssRaidTypes.Contains(key[AbyssPrefix.Length..]) ? key[AbyssPrefix.Length..] : null
        : Fields.FirstOrDefault(f => f.Key == key).MoneyType;

    /// <summary>按协议号建立解码表；布局里没有的协议名跳过。</summary>
    public static WalletDecoder CreateDecoder(IReadOnlyDictionary<string, uint> opcodes, ClientResourceCatalog? catalog) => new(opcodes, catalog);
}

/// <summary>按客户端布局的协议号解码钱包消息；无法识别的 MoneyType、负值或长度不符的负载返回空（跳过，不撤销库存）。</summary>
public sealed class WalletDecoder
{
    private readonly Dictionary<uint, (string Key, string MoneyType)> changes = [];
    private readonly uint? increased, decreased, refilled, silverRefill, passRefill, abyss;
    private readonly ClientResourceCatalog? catalog;
    private readonly Dictionary<string, string> lastKey = new(StringComparer.Ordinal);

    internal WalletDecoder(IReadOnlyDictionary<string, uint> opcodes, ClientResourceCatalog? catalog)
    {
        this.catalog = catalog;
        foreach (var (key, method, moneyType) in WalletProtocol.Fields)
            if (opcodes.TryGetValue("REPLICATION_Wallet_Change" + method + "_REPL", out var code)) changes[code] = (key, moneyType);
        uint? Find(string name) => opcodes.TryGetValue(name, out var code) ? code : null;
        increased = Find(WalletProtocol.IncreasedName); decreased = Find(WalletProtocol.DecreasedName); refilled = Find(WalletProtocol.RefilledName);
        silverRefill = Find(WalletProtocol.SilverRefillName); passRefill = Find(WalletProtocol.PassDeepRefillName); abyss = Find(WalletProtocol.AbyssName);
    }

    /// <summary>该协议号是否属于钱包资源消息。</summary>
    public bool Handles(int code) => code >= 0 && (changes.ContainsKey((uint)code) || (uint)code == increased || (uint)code == decreased ||
        (uint)code == refilled || (uint)code == silverRefill || (uint)code == passRefill || (uint)code == abyss);

    /// <summary>解码一帧；返回空表示协议号不属于钱包或内容无法识别（跳过）。</summary>
    public InventoryResourceChange? Decode(int code, ReadOnlySpan<byte> body)
    {
        var op = (uint)code;
        if (changes.TryGetValue(op, out var field))
        {
            if (body.Length != 8) return null;
            var value = BinaryPrimitives.ReadInt64LittleEndian(body);
            if (value < 0) return null;
            lastKey[field.MoneyType] = field.Key;
            return Describe(new(field.Key, field.MoneyType, new((ulong)value)));
        }
        if (op == abyss)
        {
            if (body.Length != 9 || body[0] >= WalletProtocol.AbyssRaidTypes.Count) return null;
            var value = BinaryPrimitives.ReadInt64LittleEndian(body[1..]);
            if (value < 0) return null;
            var type = WalletProtocol.AbyssRaidTypes[body[0]];
            lastKey[type] = WalletProtocol.AbyssPrefix + type;
            return Describe(new(WalletProtocol.AbyssPrefix + type, type, new((ulong)value)));
        }
        if (op == increased || op == decreased)
        {
            if (body.Length != 12) return null;
            var index = BinaryPrimitives.ReadInt32LittleEndian(body); var amount = BinaryPrimitives.ReadInt64LittleEndian(body[4..]);
            if (index <= 0 || index >= WalletProtocol.MoneyTypes.Count || amount < 0) return null;
            var type = WalletProtocol.MoneyTypes[index];
            var key = lastKey.GetValueOrDefault(type) ?? WalletProtocol.DefaultKey(type);
            if (key is null) return null;
            return Describe(op == increased ? new(key, type, Increase: new((ulong)amount)) : new(key, type, Decrease: new((ulong)amount)));
        }
        if (op == silverRefill || op == passRefill)
        {
            if (body.Length != 8) return null;
            var ms = BinaryPrimitives.ReadInt64LittleEndian(body);
            if (ms is <= 0 or > 253402300799999) return null;
            var (key, type) = op == silverRefill ? ("silverCoin", "SilverCoin") : ("passDeep", "Pass_Deep");
            return Describe(new(key, type, RefilledAt: DateTimeOffset.FromUnixTimeMilliseconds(ms)));
        }
        if (op == refilled)
        {
            if (body.Length != 12 || catalog?.CurrencyMoneyTypes.GetValueOrDefault(BinaryPrimitives.ReadUInt32LittleEndian(body[8..])) is not { } type) return null;
            var key = WalletProtocol.DefaultKey(type);
            return key is null ? null : Describe(new(key, type, RefillNotified: true));
        }
        return null;
    }

    /// <summary>
    /// 完整同步帧的资源：登录数据里读到的钱包数值与银币、魔族貢品的上次补充时间，加全部资源的名称与说明；
    /// 登录数据没有读到的项只带名称与说明（视图里仍为未读取）。客户端表读不到时只给出数值。
    /// 银币与魔族貢品另带补充上限（<see cref="ClientRefillCaps.For"/>，按登录数据里的会员在 <paramref name="now"/> 判定；无法确定时为空）。
    /// </summary>
    public IReadOnlyList<InventoryResourceChange> Login(LoginWallet? login, DateTimeOffset now)
    {
        var result = new List<InventoryResourceChange>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in InventoryResources.Definitions)
        {
            keys.Add(definition.Key);
            UInt64String? value = login?.Values?.TryGetValue(definition.Key, out var number) == true ? new(number) : null;
            DateTimeOffset? refilled = definition.Key switch { "silverCoin" => login?.SilverCoinRefilledAt, "passDeep" => login?.PassDeepRefilledAt, _ => null };
            if (catalog is null && value is null && refilled is null) continue;
            result.Add(Describe(new(definition.Key, definition.MoneyType, value, RefilledAt: refilled,
                RefillCap: catalog?.RefillCaps?.For(definition.MoneyType, login?.Memberships, now))));
        }
        foreach (var (key, number) in login?.Values ?? new Dictionary<string, ulong>())
            if (!keys.Contains(key) && WalletProtocol.MoneyTypeOf(key) is { } type) result.Add(Describe(new(key, type, new(number))));
        return result;
    }

    private InventoryResourceChange Describe(InventoryResourceChange change) => catalog is null ? change : change with
    {
        RawName = catalog.Names.GetValueOrDefault(change.MoneyType),
        Description = catalog.Descriptions?.GetValueOrDefault(change.MoneyType),
        RefillIntervalSeconds = catalog.RefillIntervals.TryGetValue(change.MoneyType, out var interval) ? interval.TotalSeconds : null,
    };
}
