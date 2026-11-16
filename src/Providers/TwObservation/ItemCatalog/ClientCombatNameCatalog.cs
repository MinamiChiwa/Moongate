using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 当前客户端的怪物、技能与效果名称表。Id 为资源包索引的 SHA-256；Unsupported 记录未能读取的表及原因
/// （monster、skill、effect → combat.nameTableMissing / combat.nameLayoutUnsupported）。
/// EffectPolarities 为 Buff 表 AffectType 列：buff（增益）、debuff（减益）、none（表里标为无）；列结构不符时为空，效果极性按未知处理。
/// </summary>
public sealed record CombatNameTables(string Id, IReadOnlyDictionary<uint, string> Monsters, IReadOnlyDictionary<uint, string> Skills,
    IReadOnlyDictionary<uint, string> Effects, IReadOnlyDictionary<string, string> Unsupported,
    IReadOnlyDictionary<uint, string>? EffectPolarities = null)
{
    /// <summary>名称常驻内存的估算：每条按 48 字节加 UTF-16 文本计。</summary>
    public long Bytes => new[] { Monsters, Skills, Effects }.Sum(table => table.Sum(pair => 48L + pair.Value.Length * 2L)) + (EffectPolarities?.Count ?? 0) * 24L;
}

/// <summary>
/// 从客户端 Sheetdata 读取 Monster、Skill、Buff 三张表的 DisplayName。列顺序以 global-metadata 中对应 SheetData 的字段声明核对；
/// Skill 的 DisplayName 位于第 49 列，之前各列按客户端表编码逐列跳过：枚举与字符串为文本偏移 VarUInt32，TimeSpan 为 8 字节，
/// int32 为有符号变长整数（<see cref="ClientDataReader.Int"/>），bool 为 1 字节，SheetId 为 4 字节，float 为 1 字节标记加其低 3 位给出的字节数，数组为 VarUInt32 个数加元素。
/// </summary>
public static partial class ClientCombatNameCatalog
{
    public const int MaximumEntriesPerTable = 100000, MaximumNameChars = 80;
    private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
    private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
    private const string DataPath = "Sheetdata_Client/client.sheetdata.bin";
    public const string MonsterSheet = "Monster", SkillSheet = "Skill", BuffSheet = "Buff";
    private static readonly string[] Sheets = [MonsterSheet, SkillSheet, BuffSheet];
    /// <summary>当前客户端 BuffSheetData 第 0–7 列（tw-20261110 构建离线核对）：第 1–6 列为文本偏移，第 7 列 AffectType 为枚举名的文本偏移（Buff、Debuff、None）。</summary>
    public static readonly IReadOnlyList<string> BuffPolarityColumns = ["Id", "DisplayBuffTypeText", "DisplayName", "DescriptionText",
        "BuffScrollDisplayName", "BuffScrollDescriptionText", "BuffGainNotification", "AffectType"];
    /// <summary>当前客户端 SkillSheetData 第 0–49 列（tw-20261110 构建，GameAssembly 字段类型已离线核对）。</summary>
    public static readonly IReadOnlyList<string> SkillColumns = ["Id", "MainProcessScriptType", "GeneralSkillSubScriptType", "ControlCategory", "Args",
        "SkillActor", "CastingActionName", "CastingDuration", "ChannelingActionName", "ChannelingEndActionName", "ChannelingDuration",
        "ChannelingTickStartTime", "ChannelingInterval", "ChargeActionName", "ChargeDelay", "ChargeDuration", "MaxChargeStep",
        "ActivationActionNames", "ExtraActionNames", "AttackCategory", "Range", "AttackCoefficients", "CriticalChance", "FriendlyBuffIds",
        "EnemyBuffIds", "ExtraBuffIds", "CriticalDamageCoefficient", "InitialCooldown", "InitialCooldownVariance", "Cooldown",
        "CooldownVariance", "MaxStackCount", "NextAutoChargingSkill", "CostBuff", "CostBuffStack", "ResourceCost", "ResourceGain",
        "IsUltimate", "UltimateSkillCost", "UltimateResourceGain", "IsPaladinTransfigure", "PaladinTransfigureCost", "ProjectileSheetId",
        "BreakDamageType", "FixedBreakDamage", "BreakStateType", "BreakStateDuration", "SkillBreakDamage", "ShoveDistance", "DisplayName"];
    /// <summary>第 1–48 列的编码：V 文本偏移/枚举，T TimeSpan，I int32，B bool，U SheetId，F float，VA/UA/FA 为对应数组。</summary>
    internal static readonly string[] SkillCodecs = ["V", "V", "V", "V", "V", "V", "T", "V", "V", "T", "T", "T", "V", "T", "T", "I", "VA", "VA",
        "V", "F", "FA", "F", "UA", "UA", "UA", "F", "T", "T", "T", "T", "I", "U", "U", "V", "I", "I", "B", "F", "F", "B", "F", "U", "V", "F",
        "V", "T", "I", "F"];

    public static CombatNameTables Extract(string appdataDirectory, CancellationToken cancellationToken = default)
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
        var fields = ClientItemCatalogExtractor.ReadSheetFields(File.ReadAllBytes(metadataPath), Sheets, cancellationToken);
        metadataStamp.EnsureUnchanged(metadataPath);
        package.EnsureUnchanged();
        foreach (var pair in stamps) pair.Value.EnsureUnchanged(pair.Key);
        return Decode(resources[MetaPath], resources[TextPath], resources[DataPath], fields,
            Convert.ToHexStringLower(SHA256.HashData(package.IndexBytes)), cancellationToken);
    }

    public static CombatNameTables Decode(byte[] meta, byte[] text, byte[] data, IReadOnlyDictionary<string, IReadOnlyList<string>> fields,
        string id, CancellationToken cancellationToken = default)
    {
        var tables = ClientNameTable.ReadTables(meta, text, cancellationToken);
        var unsupported = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<uint, string> Read(string kind, string sheet, IReadOnlyList<string> columns, Func<ClientDataReader, uint> name)
        {
            var result = new Dictionary<uint, string>();
            if (!tables.TryGetValue(sheet, out var table)) { unsupported[kind] = "combat.nameTableMissing"; return result; }
            if (!fields.TryGetValue(sheet, out var declared) || declared.Count < columns.Count || !declared.Take(columns.Count).SequenceEqual(columns, StringComparer.Ordinal) ||
                table.Rows > MaximumEntriesPerTable || table.Block.Length < checked((long)table.Rows * 4))
            { unsupported[kind] = "combat.nameLayoutUnsupported"; return result; }
            var ids = new ClientDataReader(table.Block);
            for (var row = 0; row < table.Rows; row++)
            {
                if ((row & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var key = ids.UInt32();
                var start = table.DataOffsets[row];
                var end = row + 1 < table.Rows ? table.DataOffsets[row + 1] : table.DataEnd;
                if (start > end || end > (ulong)data.Length) { unsupported[kind] = "combat.nameLayoutUnsupported"; return new(); }
                // 单行编码异常只使该行名称未知，不影响其余行。
                try
                {
                    var offset = name(new ClientDataReader(data, checked((int)start), checked((int)end)));
                    if (Clean(ClientNameTable.ReadString(text, checked((int)offset))) is { } value) result.TryAdd(key, value);
                }
                catch (InvalidDataException) { }
            }
            return result;
        }
        var monsters = Read("monster", MonsterSheet, ["Id", "DisplayName"], row => row.Var());
        var effects = Read("effect", BuffSheet, ["Id", "DisplayBuffTypeText", "DisplayName"], row => { _ = row.Var(); return row.Var(); });
        var skills = Read("skill", SkillSheet, SkillColumns, row =>
        {
            foreach (var codec in SkillCodecs) Skip(row, codec);
            return row.Var();
        });
        // 效果极性单独读取：列结构不符时只有极性未知，名称照常可用。
        var polarities = new Dictionary<uint, string>();
        var affect = Read("effect-polarity", BuffSheet, BuffPolarityColumns, row => { for (var i = 0; i < 6; i++) row.Var(); return row.Var(); });
        if (!unsupported.Remove("effect-polarity", out _))
            foreach (var (key, value) in affect)
                if (value switch { "Buff" => "buff", "Debuff" => "debuff", "None" => "none", _ => null } is { } polarity) polarities[key] = polarity;
        return new(id, monsters, skills, effects, unsupported, polarities);
    }
    private static void Skip(ClientDataReader row, string codec)
    {
        switch (codec)
        {
            case "V": row.Var(); break;
            case "I": row.Int(); break;
            case "T": row.Take(8); break;
            case "B": row.Take(1); break;
            case "U": row.Take(4); break;
            case "F": Float(row); break;
            case "VA": for (var count = Count(row); count > 0; count--) row.Var(); break;
            case "UA": for (var count = Count(row); count > 0; count--) row.Take(4); break;
            case "FA": for (var count = Count(row); count > 0; count--) Float(row); break;
            default: throw new InvalidDataException("未登记的名称表列编码");
        }
        static uint Count(ClientDataReader row) => row.Var() is var count and <= 4096 ? count : throw new InvalidDataException("名称表数组超过预算");
        static void Float(ClientDataReader row) { var tag = row.Take(1)[0]; if (tag != 0) row.Take(tag & 7); }
    }
    /// <summary>去掉颜色标记与首尾空白；空、含控制字符或超过 80 字的名称视为不可用。</summary>
    private static string? Clean(string raw)
    {
        var value = ColorTag().Replace(raw, string.Empty).Trim();
        return value.Length is 0 or > MaximumNameChars || value.Any(char.IsControl) ? null : value;
    }
    [GeneratedRegex(@"</?color(?:=#[0-9a-fA-F]{6,8})?>", RegexOptions.CultureInvariant)] private static partial Regex ColorTag();
}
