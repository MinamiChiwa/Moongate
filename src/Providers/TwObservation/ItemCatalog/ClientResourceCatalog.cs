namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 角色资源（钱包货币）的客户端表数据：<c>MoneyInfo</c> 按 <c>MoneyType</c> 名称给出游戏原文名称；
/// <c>CurrencySupplyConstants</c> 经 <c>CurrencyInfo</c> 关联到 <c>MoneyType</c>，给出补充间隔与补充上限（银币、魔族貢品）；
/// <c>Membership</c>、<c>MembershipParameter</c> 给出会员对补充上限的加成。
/// 只读取 sheetmeta、sheettext、sheetdata 三项资源，读完即释放；不保存表内容。
/// </summary>
/// <param name="Names">MoneyType 名称（如 Gold、SilverCoin、Pass_Deep）→ 游戏原文名称。</param>
/// <param name="RefillIntervals">MoneyType 名称 → 补充间隔。</param>
/// <param name="CurrencyMoneyTypes">CurrencyInfoSheetId → MoneyType 名称（只含货币类行），用于补充通知 20615。</param>
/// <param name="Descriptions">MoneyType 名称 → 说明原文：取 CurrencyInfo.Description，没有时取 MoneyInfo.FlavorText；两处都为空的不列。</param>
/// <param name="RefillCaps">补充上限规则；表读不出时为空（上限未知）。</param>
public sealed record ClientResourceCatalog(IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, TimeSpan> RefillIntervals, IReadOnlyDictionary<uint, string> CurrencyMoneyTypes,
    IReadOnlyDictionary<string, string>? Descriptions = null, ClientRefillCaps? RefillCaps = null)
{
    public const int MaximumDescriptionLength = 256;
    private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
    private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
    private const string DataPath = "Sheetdata_Client/client.sheetdata.bin";
    public const int MaximumNames = 256, MaximumNameLength = 64;

    public static ClientResourceCatalog Read(string appdataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appdataDirectory);
        var package = ClientResourcePackage.Open(Path.Combine(Path.GetFullPath(appdataDirectory), "ResourcesPackage", "Starter"), cancellationToken);
        var wanted = new[] { MetaPath, TextPath, DataPath };
        var records = package.Entries.Where(e => wanted.Contains(e.Path, StringComparer.Ordinal)).ToArray();
        if (records.Length != wanted.Length || records.Select(e => e.Path).Distinct(StringComparer.Ordinal).Count() != wanted.Length)
            throw new InvalidDataException("客户端缺少货币名称资源");
        var stamps = records.Select(e => e.BlobPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, StableFileStamp.Capture, StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in records) resources.Add(entry.Path, package.Read(entry, stamps[entry.BlobPath], cancellationToken).Decoded);
        var result = Decode(resources[MetaPath], resources[TextPath], resources[DataPath], cancellationToken);
        package.EnsureUnchanged();
        foreach (var pair in stamps) pair.Value.EnsureUnchanged(pair.Key);
        return result;
    }

    /// <summary>
    /// 行布局（台服 2026-11 客户端）：MoneyInfo 的主键块每行一个 VarUInt32，是 MoneyType 名称的文本偏移；正文首个 VarUInt32 是 DisplayName 文本偏移。
    /// CurrencyInfo 主键块为 UInt32 编号，正文前两个 VarUInt32 是货币大类与 MoneyType 名称的文本偏移（大类为 Money 的行才是钱包货币）。
    /// CurrencySupplyConstants 主键块为 CurrencyInfo 编号，正文为 MaxCount、LoadLimitCount、RefillTime（float64 秒）、RefillAmount，
    /// 其中 int 列为有符号变长整数（见 <see cref="ClientDataReader.Int"/>）。MaxCount 是自然补充的上限（银币 100、魔族貢品 10），LoadLimitCount 为 999／99。
    /// MembershipParameter 没有主键块，每行是参数名与参数值两个文本偏移；Membership 主键块为会员编号。
    /// </summary>
    public static ClientResourceCatalog Decode(byte[] meta, byte[] text, byte[] data, CancellationToken cancellationToken = default)
    {
        var tables = ClientNameTable.ReadTables(meta, text, cancellationToken);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var flavors = new Dictionary<string, string>(StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var money = Table(tables, "MoneyInfo");
        var keys = new ClientDataReader(money.Block);
        for (var row = 0; row < money.Rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = Name(text, keys.Var());
            var body = Row(money, row, data);
            var display = Name(text, body.Var());
            if (names.Count >= MaximumNames || !names.TryAdd(type, display)) throw new InvalidDataException("MoneyInfo 主键重复或超过预算");
            if (Description(text, body.Var()) is { } flavor) flavors[type] = flavor;
        }
        keys.EnsureEnd();
        var currencies = new Dictionary<uint, string>();
        var info = Table(tables, "CurrencyInfo");
        if (info.Block.Length != checked((int)info.Rows * 4)) throw new InvalidDataException("CurrencyInfo 主键结构不支持");
        var ids = new ClientDataReader(info.Block);
        for (var row = 0; row < info.Rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = ids.UInt32(); var reader = Row(info, row, data);
            var kind = ClientNameTable.ReadString(text, checked((int)reader.Var()));
            if (kind != "Money") continue;
            var moneyType = Name(text, reader.Var());
            currencies[id] = moneyType;
            // Description 是正文最后一列（VarUInt32 文本偏移）。
            if (LastVar(data, table: info, row) is { } last && Description(text, last) is { } description) descriptions.TryAdd(moneyType, description);
        }
        foreach (var (type, flavor) in flavors) descriptions.TryAdd(type, flavor);
        var intervals = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var caps = new Dictionary<string, int>(StringComparer.Ordinal);
        var supply = Table(tables, "CurrencySupplyConstants");
        if (supply.Block.Length != checked((int)supply.Rows * 4)) throw new InvalidDataException("CurrencySupplyConstants 主键结构不支持");
        var supplyIds = new ClientDataReader(supply.Block);
        for (var row = 0; row < supply.Rows; row++)
        {
            var id = supplyIds.UInt32(); var reader = Row(supply, row, data);
            var maximum = reader.Int(); _ = reader.Int();
            var seconds = BitConverter.Int64BitsToDouble(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(reader.Take(8)));
            _ = reader.Int(); reader.EnsureEnd();
            if (!double.IsFinite(seconds) || seconds is <= 0 or > 30 * 86400) throw new InvalidDataException("CurrencySupplyConstants 补充间隔无效");
            if (!currencies.TryGetValue(id, out var type)) continue;
            intervals[type] = TimeSpan.FromSeconds(seconds);
            if (maximum is > 0 and <= ClientRefillCaps.MaximumCap) caps[type] = maximum.Value;
        }
        return new(names, intervals, currencies, descriptions, caps.Count == 0 ? null : Memberships(tables, text, data, caps));
    }

    /// <summary>会员表与加成作为资料读取；读不出时只保留基础上限，判定不受影响。</summary>
    private static ClientRefillCaps Memberships(IReadOnlyDictionary<string, ClientTable> tables, byte[] text, byte[] data, Dictionary<string, int> caps)
    {
        try
        {
            var membership = Table(tables, "Membership");
            if (membership.Block.Length != checked((int)membership.Rows * 4)) throw new InvalidDataException("Membership 主键结构不支持");
            var ids = new HashSet<uint>(); var keys = new ClientDataReader(membership.Block);
            for (var row = 0; row < membership.Rows; row++) ids.Add(keys.UInt32());
            var bonus = new Dictionary<string, int>(StringComparer.Ordinal);
            var parameters = Table(tables, "MembershipParameter");
            for (var row = 0; row < parameters.Rows; row++)
            {
                var reader = Row(parameters, row, data);
                var name = ClientNameTable.ReadString(text, checked((int)reader.Var()));
                var type = name switch { "SilverCoinRefillCapacity_Bonus" => "SilverCoin", "PassDeepRefillCapacity_Bonus" => "Pass_Deep", _ => null };
                if (type is null) continue;
                var value = ClientNameTable.ReadString(text, checked((int)reader.Var()));
                if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amount) &&
                    amount is >= 0 and <= ClientRefillCaps.MaximumCap) bonus[type] = amount;
            }
            return new(caps, bonus, ids);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return new(caps, new Dictionary<string, int>(), null); }
    }

    private static string Name(byte[] text, uint offset)
    {
        var value = ClientNameTable.ReadString(text, checked((int)offset));
        if (value.Length is 0 or > MaximumNameLength || value.Any(char.IsControl)) throw new InvalidDataException("货币名称无效");
        return value;
    }

    private static string? Description(byte[] text, uint offset)
    {
        string value;
        try { value = ClientNameTable.ReadString(text, checked((int)offset)).Trim(); }
        catch (InvalidDataException) { return null; }
        return value.Length is 0 or > MaximumDescriptionLength || value.Any(char.IsControl) ? null : value;
    }

    private static uint? LastVar(byte[] data, ClientTable table, int row)
    {
        var start = (int)table.DataOffsets[row]; var end = (int)(row + 1 < table.Rows ? table.DataOffsets[row + 1] : table.DataEnd);
        if (end <= start || data[end - 1] >= 0x80) return null;
        var at = end - 1;
        while (at > start && data[at - 1] >= 0x80 && end - at < 5) at--;
        return new ClientDataReader(data, at, end).Var();
    }

    private static ClientTable Table(IReadOnlyDictionary<string, ClientTable> tables, string name)
    {
        if (!tables.TryGetValue(name, out var table) || table.Rows == 0 || table.Rows > 4096) throw new InvalidDataException("客户端缺少 " + name + " 表");
        return table;
    }

    private static ClientDataReader Row(ClientTable table, int row, byte[] data)
    {
        var start = table.DataOffsets[row]; var end = row + 1 < table.Rows ? table.DataOffsets[row + 1] : table.DataEnd;
        if (end > (ulong)data.Length || end < start) throw new InvalidDataException("客户端表行范围越界");
        return new(data, checked((int)start), checked((int)end));
    }
}

/// <summary>
/// 银币、魔族貢品的补充上限规则（台服 2026-11 客户端表）。
/// <para>
/// 基础上限是 <c>CurrencySupplyConstants.MaxCount</c>（100、10）。会员加成是 <c>MembershipParameter</c> 的 <c>SilverCoinRefillCapacity_Bonus</c>、
/// <c>PassDeepRefillCapacity_Bonus</c>（+50、+5）；表里没有“哪种会员带这项加成”的列，客户端商品说明只在全方位会员（<c>MembershipSheetId_.Combination</c>）
/// 下列出“銀幣持有上限追加50”“魔族貢品持有上限追加5”。会员编号是成员名 UTF-8 的 MD5 前 4 字节（小端），<c>Combination</c> 即 0x3D92AC47。
/// </para>
/// 留存实采只有没有会员的角色，其中一个角色停在银币 100、魔族貢品 10 且补充计时不再前进，与基础上限一致。
/// 会员生效时的登录数据没有样本（全方位在 <c>ActivatedMemberships</c> 里是单独一项还是由其余三项构成未知），
/// 只判定没有会员的状态：登录数据明确没有生效会员时为基础上限；有任何生效会员、
/// 会员数据没有读到或有到期时刻读不出的会员时为空（未知）。会员加成与会员表编号保留为资料，不参与判定；150／15 待有会员的样本核实后启用。
/// </summary>
/// <param name="Base">MoneyType 名称 → 基础上限。</param>
/// <param name="MemberBonus">MoneyType 名称 → 全方位会员的上限加成（资料，当前不参与判定）。</param>
/// <param name="Memberships"><c>Membership</c> 表的全部会员编号（资料）；会员表读不出时为空。</param>
public sealed record ClientRefillCaps(IReadOnlyDictionary<string, int> Base, IReadOnlyDictionary<string, int> MemberBonus, IReadOnlySet<uint>? Memberships)
{
    public const int MaximumCap = 1_000_000;
    /// <summary><c>Shared.Symbols.MembershipSheetId_.Combination</c>。</summary>
    public static uint Combination { get; } = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
        System.Security.Cryptography.MD5.HashData("Combination"u8));

    /// <summary>该角色此刻的补充上限：没有生效会员时为基础上限，其余为空。<paramref name="memberships"/> 是登录数据里的会员（空表示没有读到）。</summary>
    public int? For(string moneyType, IReadOnlyList<Inventory.LoginMembership>? memberships, DateTimeOffset now)
    {
        if (!Base.TryGetValue(moneyType, out var cap) || memberships is null || memberships.Any(m => m.ExpiresAt is null)) return null;
        return memberships.Any(m => m.ExpiresAt > now) ? null : cap;
    }
}
