using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>生活技能：<see cref="IsFishing"/> 为真表示客户端表里该技能类型为“钓鱼”。</summary>
public sealed record LifeSkillRef(string Name, bool IsFishing);

/// <summary>
/// 客户端表给出的生活归属。<see cref="GatherSkills"/>：采集物名称 → 掉落它的采集点所属的生活技能；
/// <see cref="AlterFacilities"/> / <see cref="CraftFacilities"/>：加工配方名、制作配方名（结果道具名）→ 设施显示名。
/// <see cref="Unsupported"/> 记录没能读取的分类及原因（键 gather、alter、craft，值 life.xxxTableMissing / life.xxxLayoutUnsupported）。
/// </summary>
public sealed record LifeClientTables(string Id, IReadOnlyDictionary<string, IReadOnlySet<LifeSkillRef>> GatherSkills,
    IReadOnlyDictionary<string, IReadOnlySet<string>> AlterFacilities, IReadOnlyDictionary<string, IReadOnlySet<string>> CraftFacilities,
    IReadOnlyDictionary<string, string> Unsupported);

/// <summary>
/// 从客户端 Sheetdata 解析生活归属（2026-11-25 对台服客户端核对，列顺序以 global-metadata 的 SheetData 字段声明核对）。
/// 表的“键块”按行存放被索引的列，其余列在行正文里：
/// <list type="bullet">
/// <item>LivingSkill：键块每行 Id(4 字节)、LivingSkillType(文本偏移)、必备工具类型(4 字节)、一个有符号变长整数；正文首列 DisplayName。</item>
/// <item>Gatherable：键块每行 Id、生活技能 Id(4 字节)、解锁等级、显示顺序(有符号变长整数)、显示类型(文本偏移)、展示道具 Id(4 字节)；
/// 正文依次 DisplayName、Description、必需道具 Id(4 字节)、数量、InteractionId(4 字节)、掉落奖励组（个数加每组 4 字节 Id）。</item>
/// <item>DropRewardTable：键块每行 GroupId(4 字节，同一组多行)；正文依次 PoolGroupNumber、PickCount、IsFixedDrop(1 字节)、奖励类型与道具开发名（文本偏移），
/// 行尾 5 字节为 FishableSheetId(4 字节)与 IsHidden，FishableSheetId 非零表示钓鱼点奖励；道具开发名的 MD5 前 4 字节按有符号小端取绝对值是 Consumable 的 Id（见 <see cref="ItemId"/>）。</item>
/// <item>Consumable：键块每行 Id、DisplayName、FlavorText(文本偏移)。</item>
/// <item>FacilityType：键块每行 枚举名与图标路径(文本偏移)；正文首列 DisplayName。</item>
/// <item>Altering：键块每行 Id(4 字节)、DisplayName、Description、FacilityType(文本偏移)、设施等级、一个 4 字节引用、顺序、一个文本偏移、显示类型、展示道具 Id(4 字节)、末尾一列。</item>
/// <item>CraftingRecipe：键块每行 Index(4 字节)、IsBasicCrafting(1 字节)、BasicCraftingOrder、FacilityType(文本偏移，3 字节)、设施等级(1 字节)、技能 Id(4 字节)…，行长不固定，
/// 以技能 Id 前 4 字节定位每行的设施类型；正文依次 DisplayName(空)、Description，随后是消耗道具（个数加每项 Id 与数量）、假道具个数、结果道具（个数加每项 Id 与数量）。</item>
/// </list>
/// 一张表的核对失败只让依赖它的分类不可读，其他分类不受影响。
/// </summary>
public static class ClientLifeCatalog
{
    public const int MaximumEntries = 200000;
    private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
    private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
    private const string DataPath = "Sheetdata_Client/client.sheetdata.bin";
    private static readonly string[] SheetNames = ["LivingSkill", "Gatherable", "DropRewardTable", "Consumable", "FacilityType", "Altering", "CraftingRecipe"];
    private static readonly string[] LivingSkillColumns = ["Id", "DisplayName", "FlavorText", "LivingSkillType", "RequiredEquipmentToolTypeSheetId"];
    private static readonly string[] GatherableColumns = ["Id", "DisplayName", "Description", "GatherUnlockLivingSkill", "GatherUnlockSkillLevel",
        "RequiredConsumableId", "RequiredConsumableAmount", "InteractionId", "GatherDropRewardIds"];
    private static readonly string[] DropColumns = ["GroupId", "PoolGroupNumber", "PickCount", "IsFixedDrop", "RewardData", "FishableSheetId", "IsHidden"];
    private static readonly string[] ConsumableColumns = ["Id", "DisplayName", "FlavorText"];
    private static readonly string[] FacilityColumns = ["FacilityType", "DisplayName", "Title"];
    private static readonly string[] AlteringColumns = ["FeatureSwitch", "Id", "DisplayName", "Description", "FacilityType"];
    private static readonly string[] CraftingColumns = ["Index", "Id", "DisplayName", "Description", "IsBasicCrafting", "BasicCraftingOrder", "FacilityType",
        "FacilityLevel", "RecipeUnlockLivingSkill", "RecipeUnlockSkillLevel"];

    public static LifeClientTables Extract(string appdataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appdataDirectory);
        var appdata = Path.GetFullPath(appdataDirectory);
        var package = ClientResourcePackage.Open(Path.Combine(appdata, "ResourcesPackage", "Starter"), cancellationToken);
        var wanted = new[] { MetaPath, TextPath, DataPath };
        var records = package.Entries.Where(e => wanted.Contains(e.Path, StringComparer.Ordinal)).ToArray();
        if (records.Length != wanted.Length || records.Select(e => e.Path).Distinct(StringComparer.Ordinal).Count() != wanted.Length)
            throw new InvalidDataException("客户端缺少名称表资源");
        var stamps = records.Select(e => e.BlobPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, StableFileStamp.Capture, StringComparer.OrdinalIgnoreCase);
        var resources = records.ToDictionary(e => e.Path, e => package.Read(e, stamps[e.BlobPath], cancellationToken).Decoded, StringComparer.Ordinal);
        var metadataPath = Path.Combine(appdata, "MabinogiMobile_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var metadataStamp = StableFileStamp.Capture(metadataPath);
        if (metadataStamp.Length > 256L * 1024 * 1024) throw new InvalidDataException("客户端 metadata 超过读取预算");
        var fields = ClientItemCatalogExtractor.ReadSheetFields(File.ReadAllBytes(metadataPath), SheetNames, cancellationToken);
        metadataStamp.EnsureUnchanged(metadataPath);
        package.EnsureUnchanged();
        foreach (var pair in stamps) pair.Value.EnsureUnchanged(pair.Key);
        return Decode(resources[MetaPath], resources[TextPath], resources[DataPath], fields,
            Convert.ToHexStringLower(SHA256.HashData(package.IndexBytes)), cancellationToken);
    }

    public static LifeClientTables Decode(byte[] meta, byte[] text, byte[] data, IReadOnlyDictionary<string, IReadOnlyList<string>> fields, string id,
        CancellationToken cancellationToken = default)
    {
        var sheets = new Sheets(ClientNameTable.ReadTables(meta, text, cancellationToken), fields, text, data);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        var skills = sheets.Load("skill", "LivingSkill", LivingSkillColumns, problems, table => ReadSkills(sheets, table));
        var consumables = sheets.Load("item", "Consumable", ConsumableColumns, problems, table => ReadConsumables(sheets, table));
        var facilities = sheets.Load("facility", "FacilityType", FacilityColumns, problems, table => ReadFacilities(sheets, table));
        var unsupported = new Dictionary<string, string>(StringComparer.Ordinal);

        var gather = new Dictionary<string, HashSet<LifeSkillRef>>(StringComparer.Ordinal);
        if (Prerequisite(problems, "skill", "item") is { } gatherProblem) unsupported["gather"] = gatherProblem;
        else if (!sheets.Has("DropRewardTable")) unsupported["gather"] = "life.gatherTableMissing";
        else if (!sheets.Declared("DropRewardTable", DropColumns)) unsupported["gather"] = "life.gatherLayoutUnsupported";
        else
        {
            var read = sheets.Load("gather", "Gatherable", GatherableColumns, problems, table => ReadGather(sheets, table, skills!, consumables!));
            if (read is null) unsupported["gather"] = problems["gather"]; else gather = read;
        }

        var alter = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (Prerequisite(problems, "facility") is { } alterProblem) unsupported["alter"] = alterProblem;
        else
        {
            var read = sheets.Load("alter", "Altering", AlteringColumns, problems, table => ReadAltering(sheets, table, facilities!));
            if (read is null) unsupported["alter"] = problems["alter"]; else alter = read;
        }

        var craft = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (Prerequisite(problems, "facility", "skill", "item") is { } craftProblem) unsupported["craft"] = craftProblem;
        else
        {
            var read = sheets.Load("craft", "CraftingRecipe", CraftingColumns, problems, table => ReadCrafting(sheets, table, skills!, facilities!, consumables!));
            if (read is null) unsupported["craft"] = problems["craft"]; else craft = read;
        }

        return new(id, gather.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<LifeSkillRef>)pair.Value, StringComparer.Ordinal),
            alter.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal),
            craft.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal), unsupported);
    }

    private static string? Prerequisite(Dictionary<string, string> problems, params string[] kinds) =>
        kinds.Select(kind => problems.GetValueOrDefault(kind)).FirstOrDefault(problem => problem is not null);

    private sealed class Sheets(IReadOnlyDictionary<string, ClientTable> tables, IReadOnlyDictionary<string, IReadOnlyList<string>> fields, byte[] text, byte[] data)
    {
        public bool Has(string sheet) => tables.ContainsKey(sheet);

        public bool Declared(string sheet, string[] columns) => tables.ContainsKey(sheet) && fields.TryGetValue(sheet, out var declared) &&
            declared.Count >= columns.Length && declared.Take(columns.Length).SequenceEqual(columns, StringComparer.Ordinal);

        /// <summary>读取一张表：缺失、列顺序不符、行数超限或解析中发现结构不符时记入 <paramref name="problems"/> 并返回空。</summary>
        public T? Load<T>(string kind, string sheet, string[] columns, Dictionary<string, string> problems, Func<ClientTable, T> read) where T : class
        {
            if (!tables.TryGetValue(sheet, out var table)) { problems[kind] = "life." + kind + "TableMissing"; return null; }
            if (!Declared(sheet, columns) || table.Rows > MaximumEntries || table.DataOffsets.Count != table.Rows) { problems[kind] = "life." + kind + "LayoutUnsupported"; return null; }
            try { return read(table); }
            catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
            {
                problems[kind] = "life." + kind + "LayoutUnsupported";
                return null;
            }
        }

        public ClientTable Table(string sheet) => tables[sheet];

        public (int Start, int End) Range(ClientTable table, int row)
        {
            var start = table.DataOffsets[row];
            var end = row + 1 < table.Rows ? table.DataOffsets[row + 1] : table.DataEnd;
            if (start > end || end > (ulong)data.Length) throw new InvalidDataException("客户端表行范围越界");
            return (checked((int)start), checked((int)end));
        }

        public ClientDataReader Row(ClientTable table, int row)
        {
            var (start, end) = Range(table, row);
            return new(data, start, end);
        }

        public ClientDataReader Probe(int from, int end) => new(data, from, end);

        /// <summary>文本偏移处的字符串，去首尾空白；偏移无效或为空时为空。</summary>
        public string? Text(uint offset)
        {
            try { return ClientNameTable.ReadString(text, checked((int)offset)).Trim() is { Length: > 0 } value ? value : null; }
            catch (Exception error) when (error is InvalidDataException or OverflowException) { return null; }
        }
    }

    private static Dictionary<uint, LifeSkillRef> ReadSkills(Sheets sheets, ClientTable table)
    {
        var keys = new ClientDataReader(table.Block);
        var result = new Dictionary<uint, LifeSkillRef>();
        for (var row = 0; row < table.Rows; row++)
        {
            var skill = keys.UInt32(); var type = sheets.Text(keys.Var()); keys.UInt32(); keys.Int();
            if (sheets.Text(sheets.Row(table, row).Var()) is { } name) result[skill] = new(name, string.Equals(type, "Fishing", StringComparison.Ordinal));
        }
        keys.EnsureEnd();
        return result;
    }

    private static Dictionary<uint, string> ReadConsumables(Sheets sheets, ClientTable table)
    {
        var keys = new ClientDataReader(table.Block);
        var result = new Dictionary<uint, string>();
        for (var row = 0; row < table.Rows; row++)
        {
            var item = keys.UInt32(); var name = sheets.Text(keys.Var()); keys.Var();
            if (name is not null) result.TryAdd(item, name);
        }
        keys.EnsureEnd();
        return result;
    }

    private static Dictionary<string, string> ReadFacilities(Sheets sheets, ClientTable table)
    {
        var keys = new ClientDataReader(table.Block);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var row = 0; row < table.Rows; row++)
        {
            var type = sheets.Text(keys.Var()); keys.Var();
            if (type is not null && sheets.Text(sheets.Row(table, row).Var()) is { } display) result.TryAdd(type, display);
        }
        keys.EnsureEnd();
        return result;
    }

    // 采集点 → 掉落奖励组 → 奖励行：Consumable 类型的开发名换算成道具 Id；钓鱼点奖励行（行尾 FishableSheetId 非零）归入钓鱼技能。
    private static Dictionary<string, HashSet<LifeSkillRef>> ReadGather(Sheets sheets, ClientTable gatherable, IReadOnlyDictionary<uint, LifeSkillRef> skills,
        IReadOnlyDictionary<uint, string> consumables)
    {
        var drops = sheets.Table("DropRewardTable");
        if (drops.Rows > MaximumEntries || drops.Block.Length != checked((long)drops.Rows * 4) || drops.DataOffsets.Count != drops.Rows)
            throw new InvalidDataException("掉落奖励表规模或键块长度不符");
        var groups = new Dictionary<uint, List<(string Item, bool Fishing)>>();
        var groupKeys = new ClientDataReader(drops.Block);
        for (var index = 0; index < drops.Rows; index++)
        {
            var group = groupKeys.UInt32();
            try
            {
                var (start, end) = sheets.Range(drops, index);
                if (end - start < 8) throw new InvalidDataException("奖励行过短");
                var row = sheets.Probe(start, end);
                row.Int(); row.Int(); row.Take(1);
                var type = sheets.Text(row.Var()); var symbol = sheets.Text(row.Var());
                if (type != "Consumable" || symbol is null || ItemName(symbol, consumables) is not { } item) continue;
                var tail = sheets.Probe(end - 5, end);
                if (!groups.TryGetValue(group, out var list)) groups[group] = list = [];
                list.Add((item, tail.UInt32() != 0));
            }
            catch (InvalidDataException) { }
        }
        var result = new Dictionary<string, HashSet<LifeSkillRef>>(StringComparer.Ordinal);
        if (skills.Values.FirstOrDefault(skill => skill.IsFishing) is { } fishing)
            foreach (var list in groups.Values)
                foreach (var (item, isFishing) in list)
                    if (isFishing) Add(result, item, fishing);
        var keys = new ClientDataReader(gatherable.Block);
        for (var index = 0; index < gatherable.Rows; index++)
        {
            keys.UInt32();
            var skill = keys.UInt32(); keys.Int(); keys.Int(); keys.Var(); keys.UInt32();
            var row = sheets.Row(gatherable, index);
            row.Var(); row.Var(); row.UInt32(); row.Int(); row.UInt32();
            var count = row.Var();
            if (count > 64) throw new InvalidDataException("采集掉落奖励组个数超过预算");
            var rewardGroups = new uint[count];
            for (var i = 0; i < count; i++) rewardGroups[i] = row.UInt32();
            if (!skills.TryGetValue(skill, out var owner)) continue;
            foreach (var group in rewardGroups)
                if (groups.TryGetValue(group, out var list))
                    foreach (var (item, isFishing) in list)
                        if (!isFishing) Add(result, item, owner);
        }
        keys.EnsureEnd();
        return result;
    }

    private static Dictionary<string, HashSet<string>> ReadAltering(Sheets sheets, ClientTable table, IReadOnlyDictionary<string, string> facilities)
    {
        var keys = new ClientDataReader(table.Block);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (var row = 0; row < table.Rows; row++)
        {
            keys.UInt32();
            var name = sheets.Text(keys.Var()); keys.Var();
            var facility = sheets.Text(keys.Var());
            keys.Int(); keys.UInt32(); keys.Int(); keys.Var(); keys.Var(); keys.UInt32(); keys.Var();
            if (name is null || facility is null) continue;
            if (!facilities.TryGetValue(facility, out var display)) throw new InvalidDataException("加工设施类型不在 FacilityType 表");
            Add(result, name, display);
        }
        keys.EnsureEnd();
        return result;
    }

    // 制作配方的键块行长不固定，行内含技能 Id：其前 4 字节是“设施类型文本偏移（3 字节）+ 设施等级（1 字节）”，按技能 Id 定位每行的设施类型，行数必须与表一致。
    private static Dictionary<string, HashSet<string>> ReadCrafting(Sheets sheets, ClientTable table, IReadOnlyDictionary<uint, LifeSkillRef> skills,
        IReadOnlyDictionary<string, string> facilities, IReadOnlyDictionary<uint, string> consumables)
    {
        var block = table.Block;
        var anchors = new List<string>();
        for (var at = 4; at <= block.Length - 4; at++)
        {
            if (!skills.ContainsKey(BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at, 4)))) continue;
            try
            {
                var reader = new ClientDataReader(block, at - 4, at);
                var type = sheets.Text(reader.Var());
                if (reader.Offset == at - 1 && type is not null && facilities.ContainsKey(type)) anchors.Add(type);
            }
            catch (InvalidDataException) { }
        }
        if (anchors.Count != table.Rows) throw new InvalidDataException("制作配方行数与键块不一致");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (var row = 0; row < table.Rows; row++)
            if (ResultName(sheets, table, row, consumables) is { } name) Add(result, name, facilities[anchors[row]]);
        return result;
    }

    // 配方结果：跳过名称与说明，在其后 40 字节内找到“消耗道具（个数、Id、数量）”数组，其后是假道具个数 0 与“结果道具”数组，取第一个结果的道具名；
    // 没有消耗品类结果（例如装备）时为空。
    private static string? ResultName(Sheets sheets, ClientTable table, int index, IReadOnlyDictionary<uint, string> consumables)
    {
        try
        {
            var (start, end) = sheets.Range(table, index);
            var header = sheets.Probe(start, end);
            header.Var(); header.Var();
            for (var at = header.Offset; at < Math.Min(end, header.Offset + 40); at++)
            {
                try
                {
                    var probe = sheets.Probe(at, end);
                    var count = probe.Take(1)[0];
                    if (count is < 1 or > 12) continue;
                    var valid = true;
                    for (var i = 0; i < count && valid; i++)
                    {
                        valid = consumables.ContainsKey(probe.UInt32());
                        SkipInt(probe);
                    }
                    if (!valid) continue;
                    if (probe.Take(1)[0] != 0) return null;
                    return probe.Take(1)[0] == 0 ? null : consumables.GetValueOrDefault(probe.UInt32());
                }
                catch (InvalidDataException) { }
            }
        }
        catch (InvalidDataException) { }
        return null;
    }

    private static void SkipInt(ClientDataReader reader)
    {
        var next = reader.Take(1)[0];
        while (next >= 128) next = reader.Take(1)[0];
    }

    /// <summary>
    /// 道具开发名换算 Consumable 的 Id：MD5 前 4 字节按有符号小端整数取绝对值（客户端道具 Id 都小于 2^31；2026-11-26 对台服客户端核对，
    /// 掉落奖励的 486 个 Consumable 开发名全部换算出道具，按无符号读取只能换算 251 个）。读不到名称时没有结果。
    /// </summary>
    internal static uint ItemId(string symbol)
    {
#pragma warning disable CA5351 // 只用于换算客户端表的道具 Id，不涉及安全。
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(symbol));
#pragma warning restore CA5351
        return (uint)Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(hash));
    }

    private static string? ItemName(string symbol, IReadOnlyDictionary<uint, string> consumables) =>
        consumables.TryGetValue(ItemId(symbol), out var name) ? name : null;

    private static void Add<T>(Dictionary<string, HashSet<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = [];
        set.Add(value);
    }
}
