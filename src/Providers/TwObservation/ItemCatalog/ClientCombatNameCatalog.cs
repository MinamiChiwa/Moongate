using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 当前客户端的怪物、技能与效果名称表。Id 为资源包索引的 SHA-256；Unsupported 记录未能读取的表及原因
/// （monster、skill、effect → combat.nameTableMissing / combat.nameLayoutUnsupported）。
/// EffectPolarities 为 Buff 表 AffectType 列：buff（增益）、debuff（减益）、none（表里标为无）；列结构不符时为空，效果极性按未知处理。
/// Actors 为怪物以外的自动体名称（键为种类 npc、npc-companion、pet、summoned-automaton、gimmick、memorial-saga-minion）；
/// EmptyMonsters 为 Monster 表中名称为空或只有空格的行；BossGroupNames 为这些空名怪物经 Raid → RaidGroup、BattleZone → BattleZoneGroup 关联到的首领组名。
/// EffectDescriptions 为 Buff 表第 3 列 DescriptionText（去掉富文本标记，至多 <see cref="ClientCombatNameCatalog.MaximumDescriptionChars"/> 字）；
/// 列结构不符时为空，名称照常可用。
/// </summary>
public sealed record CombatNameTables(string Id, IReadOnlyDictionary<uint, string> Monsters, IReadOnlyDictionary<uint, string> Skills,
    IReadOnlyDictionary<uint, string> Effects, IReadOnlyDictionary<string, string> Unsupported,
    IReadOnlyDictionary<uint, string>? EffectPolarities = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>>? Actors = null,
    IReadOnlySet<uint>? EmptyMonsters = null, IReadOnlyDictionary<uint, string>? BossGroupNames = null,
    IReadOnlyDictionary<uint, string>? EffectDescriptions = null)
{
    /// <summary>名称常驻内存的估算：每条按 48 字节加 UTF-16 文本计。</summary>
    public long Bytes => new[] { Monsters, Skills, Effects }.Concat(Actors?.Values ?? []).Append(BossGroupNames ?? new Dictionary<uint, string>())
        .Append(EffectDescriptions ?? new Dictionary<uint, string>())
        .Sum(table => table.Sum(pair => 48L + pair.Value.Length * 2L)) + (EffectPolarities?.Count ?? 0) * 24L + (EmptyMonsters?.Count ?? 0) * 16L;
}

/// <summary>
/// 从客户端 Sheetdata 读取 Monster、Skill、Buff 三张表的 DisplayName。列顺序以 global-metadata 中对应 SheetData 的字段声明核对；
/// Skill 的 DisplayName 位于第 49 列，之前各列按客户端表编码逐列跳过：枚举与字符串为文本偏移 VarUInt32，TimeSpan 为 8 字节，
/// int32 为有符号变长整数（<see cref="ClientDataReader.Int"/>），bool 为 1 字节，SheetId 为 4 字节，float 为 1 字节标记加其低 3 位给出的字节数，数组为 VarUInt32 个数加元素。
/// </summary>
public static partial class ClientCombatNameCatalog
{
    public const int MaximumEntriesPerTable = 100000, MaximumNameChars = 80, MaximumDescriptionChars = 400;
    private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
    private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
    private const string DataPath = "Sheetdata_Client/client.sheetdata.bin";
    public const string MonsterSheet = "Monster", SkillSheet = "Skill", BuffSheet = "Buff";
    /// <summary>怪物以外的自动体名称表（种类 → 表名）；第 1 列均为 DisplayName（tw-20261118 客户端 metadata 核对）。</summary>
    public static readonly IReadOnlyDictionary<string, string> ActorSheets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["npc"] = "NPC", ["npc-companion"] = "NPCCompanion", ["pet"] = "Pet", ["summoned-automaton"] = "SummonedAutomaton",
        ["memorial-saga-minion"] = "MemorialSagaMinion"
    };
    private const string GimmickSheet = "GimmickActor", GimmickStateSheet = "GimmickActorState";
    private const string RaidSheet = "Raid", RaidGroupSheet = "RaidGroup", BattleZoneSheet = "BattleZone", BattleZoneGroupSheet = "BattleZoneGroup";
    private static readonly string[] Sheets = [MonsterSheet, SkillSheet, BuffSheet, .. ActorSheets.Values, GimmickSheet, GimmickStateSheet,
        RaidSheet, RaidGroupSheet, BattleZoneSheet, BattleZoneGroupSheet];
    /// <summary>GimmickActor 第 0–5 列：出生状态 SpawnActivateState 与 ActivateStates 引用 GimmickActorState 行。</summary>
    public static readonly IReadOnlyList<string> GimmickColumns = ["Id", "ModelName", "ModelScale", "ActivateStates", "SpawnAction", "SpawnActivateState"];
    /// <summary>Raid 第 0–13 列（Index、Id 在键块）：行内依次为 RaidGroup、RaidDifficulty、六个 int32、ChannelSheetId、一个枚举文本偏移与 BossMonsterSheetId。</summary>
    public static readonly IReadOnlyList<string> RaidColumns = ["Index", "Id", "RaidGroup", "RaidDifficulty", "RequiredCombatScore", "RecommendedCombatScore",
        "OverwhelmingCombatScore", "ContentLevel", "MaxCharacterCount", "MaxReviveCount", "ChannelSheetId", "PlayMode", "ChanceMeetingEnableType", "BossMonsterSheetId"];
    public static readonly IReadOnlyList<string> BattleZoneColumns = ["Id", "BattleZoneType", "DifficultyType", "BossMonsterSheetId", "SubBossMonsterSheetIds",
        "BossMonsterCutsceneSheetId", "BattleZoneGroupId"];
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
        // 逐行读取：表缺失或列顺序与已核对的不符时整表记原因；单行编码异常只跳过该行。
        bool Rows(string kind, string sheet, IReadOnlyList<string> columns, Action<uint, ClientDataReader> visit)
        {
            if (!tables.TryGetValue(sheet, out var table)) { unsupported[kind] = "combat.nameTableMissing"; return false; }
            if (!fields.TryGetValue(sheet, out var declared) || declared.Count < columns.Count || !declared.Take(columns.Count).SequenceEqual(columns, StringComparer.Ordinal) ||
                table.Rows > MaximumEntriesPerTable || table.Block.Length < checked((long)table.Rows * 4))
            { unsupported[kind] = "combat.nameLayoutUnsupported"; return false; }
            var ids = new ClientDataReader(table.Block);
            for (var row = 0; row < table.Rows; row++)
            {
                if ((row & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var key = ids.UInt32();
                var start = table.DataOffsets[row];
                var end = row + 1 < table.Rows ? table.DataOffsets[row + 1] : table.DataEnd;
                if (start > end || end > (ulong)data.Length) { unsupported[kind] = "combat.nameLayoutUnsupported"; return false; }
                try { visit(key, new ClientDataReader(data, checked((int)start), checked((int)end))); }
                catch (InvalidDataException) { }
            }
            return true;
        }
        Dictionary<uint, string> Read(string kind, string sheet, IReadOnlyList<string> columns, Func<ClientDataReader, uint> name, HashSet<uint>? empty = null)
        {
            var result = new Dictionary<uint, string>();
            var complete = Rows(kind, sheet, columns, (key, row) =>
            {
                var raw = ClientNameTable.ReadString(text, checked((int)name(row)));
                if (Clean(raw) is { } value) result.TryAdd(key, value);
                else if (string.IsNullOrWhiteSpace(raw)) empty?.Add(key);
            });
            return complete ? result : new();
        }
        var emptyMonsters = new HashSet<uint>();
        var monsters = Read("monster", MonsterSheet, ["Id", "DisplayName"], row => row.Var(), emptyMonsters);
        var actors = new Dictionary<string, IReadOnlyDictionary<uint, string>>(StringComparer.Ordinal);
        foreach (var (kind, sheet) in ActorSheets) actors[kind] = Read(kind, sheet, ["Id", "DisplayName"], row => row.Var());
        actors["gimmick"] = Gimmicks();
        // 机关取出生状态（其次第一个有名字的可激活状态）在 GimmickActorState 表的名称。
        Dictionary<uint, string> Gimmicks()
        {
            var states = Read("gimmick", GimmickStateSheet, ["Id", "DisplayName"], row => row.Var());
            var result = new Dictionary<uint, string>();
            if (unsupported.ContainsKey("gimmick")) return result;
            Rows("gimmick", GimmickSheet, GimmickColumns, (key, row) =>
            {
                row.Var(); Skip(row, "F");
                var count = row.Var();
                if (count > 4096) throw new InvalidDataException("机关状态数组超过预算");
                var activate = new uint[count];
                for (var index = 0; index < count; index++) activate[index] = row.UInt32();
                row.Var();
                var spawn = row.UInt32();
                if ((states.TryGetValue(spawn, out var name) ? name : activate.Select(state => states.GetValueOrDefault(state)).FirstOrDefault(value => value is not null)) is { } found)
                    result.TryAdd(key, found);
            });
            return unsupported.ContainsKey("gimmick") ? new() : result;
        }
        var bossGroups = BossGroups();
        // 空名怪物经 Raid（BossMonsterSheetId）→ RaidGroup.DisplayName、BattleZone（首领与副首领）→ BattleZoneGroup.DisplayName 关联首领组名；
        // 读出的怪物 ID 不在 Monster 表中的行视为列结构不符，跳过。
        Dictionary<uint, string> BossGroups()
        {
            var result = new Dictionary<uint, string>();
            if (emptyMonsters.Count == 0) return result;
            var raidGroups = Read("boss-group", RaidGroupSheet, ["Id", "DisplayName"], row => row.Var());
            var zoneGroups = Read("boss-group", BattleZoneGroupSheet, ["Index", "Id", "DisplayName"], row => row.Var());
            bool Known(uint monster) => monsters.ContainsKey(monster) || emptyMonsters.Contains(monster);
            Rows("boss-group", RaidSheet, RaidColumns, (_, row) =>
            {
                var group = row.UInt32(); row.UInt32();
                for (var index = 0; index < 6; index++) Skip(row, "I");
                row.UInt32(); row.Var();
                var boss = row.UInt32();
                if (Known(boss) && emptyMonsters.Contains(boss) && raidGroups.TryGetValue(group, out var name)) result.TryAdd(boss, name);
            });
            Rows("boss-group", BattleZoneSheet, BattleZoneColumns, (_, row) =>
            {
                row.Var(); row.Var();
                var boss = row.UInt32();
                var count = row.Var();
                if (count > 64) throw new InvalidDataException("副首领数组超过预算");
                var bosses = new List<uint> { boss };
                for (var index = 0; index < count; index++) bosses.Add(row.UInt32());
                row.UInt32();
                var group = row.UInt32();
                if (!bosses.All(Known) || !zoneGroups.TryGetValue(group, out var name)) return;
                foreach (var id in bosses.Where(emptyMonsters.Contains)) result.TryAdd(id, name);
            });
            unsupported.Remove("boss-group");
            return result;
        }
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
        // 效果描述同样单独读取：列结构不符时只有描述为空。
        var descriptions = new Dictionary<uint, string>();
        Rows("effect-description", BuffSheet, BuffPolarityColumns.Take(4).ToArray(), (key, row) =>
        {
            row.Var(); row.Var();
            if (Description(ClientNameTable.ReadString(text, checked((int)row.Var()))) is { } value) descriptions.TryAdd(key, value);
        });
        if (unsupported.Remove("effect-description")) descriptions.Clear();
        return new(id, monsters, skills, effects, unsupported, polarities, actors, emptyMonsters, bossGroups, descriptions);
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
    /// <summary>
    /// Buff 表里开发用的占位名称（2026-11-22 对 tw-20261118 客户端 4,551 个有名称的行离线核对）：没有任何文字（如“—”）、
    /// 含下划线的开发命名（如“假人_增益(顯示X)”“集中_視覺專用”，63 行），或以“不顯示”“測試”“拍攝用”“未標記”“假人”开头、含“임시”（临时）的名称。
    /// 这些行不列入客户端 Buff 目录；名称解析不受影响，实际观测到时照常显示原文。
    /// </summary>
    public static bool IsPlaceholderName(string name) =>
        !name.Any(char.IsLetter) || name.Contains('_') || name.Contains("임시", StringComparison.Ordinal)
        || PlaceholderPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    private static readonly string[] PlaceholderPrefixes = ["不顯示", "測試", "拍攝用", "未標記", "假人"];

    /// <summary>
    /// 描述去掉富文本标记（color、b、i、size 等），客户端运行时才代入的数值模板（<c>$#MagicEffect@…{0}[%]$</c>）换成“？”（带 [%] 的为“？%”），
    /// 保留换行，其余控制字符与连续空白合并为一个空格；空描述视为没有，超过 <see cref="MaximumDescriptionChars"/> 字时截断并以“…”结尾。
    /// </summary>
    internal static string? Description(string raw)
    {
        raw = ValueTemplate().Replace(raw, match => match.Value.Contains("[%]", StringComparison.Ordinal) ? "？%" : "？");
        var lines = RichTag().Replace(raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), string.Empty)
            .Split('\n').Select(line => Spaces().Replace(new string(line.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), " ").Trim())
            .Where(line => line.Length > 0);
        var value = string.Join('\n', lines);
        if (value.Length == 0) return null;
        return value.Length <= MaximumDescriptionChars ? value : value[..(MaximumDescriptionChars - 1)].TrimEnd() + "…";
    }
    [GeneratedRegex(@"</?color(?:=#[0-9a-fA-F]{6,8})?>", RegexOptions.CultureInvariant)] private static partial Regex ColorTag();
    [GeneratedRegex(@"\$#[^$\r\n]{1,256}\$", RegexOptions.CultureInvariant)] private static partial Regex ValueTemplate();
    [GeneratedRegex(@"</?(?:color|b|i|u|s|size|sup|sub|mark|link|style)(?:=[^>]{0,64})?>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] private static partial Regex RichTag();
    [GeneratedRegex(@"[\p{Zs}\t]{2,}",RegexOptions.CultureInvariant)] private static partial Regex Spaces();
}
