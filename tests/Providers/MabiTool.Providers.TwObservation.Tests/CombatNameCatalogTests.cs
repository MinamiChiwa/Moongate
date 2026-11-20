using System.Text;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>按客户端 Sheetdata 编码合成 Monster、Skill、Buff 三张表，核对名称列定位与未知 ID。</summary>
public sealed class CombatNameCatalogTests
{
    private sealed class Sheets
    {
        // 偏移 0 为空字符串，供不关心的文本列引用。
        private readonly MemoryStream text = Seeded();
        private static MemoryStream Seeded() { var value = new MemoryStream(); value.WriteByte(0); return value; }
        private readonly List<(string Name, uint[] Ids, byte[][] Rows)> tables = [];
        public uint Text(string value)
        {
            var offset = (uint)text.Position; var bytes = Encoding.UTF8.GetBytes(value);
            text.WriteByte((byte)bytes.Length); text.Write(bytes); text.WriteByte(0); return offset;
        }
        public void Table(string name, uint[] ids, byte[][] rows) => tables.Add((name, ids, rows));
        public (byte[] Meta, byte[] Text, byte[] Data) Build()
        {
            var meta = new List<byte>(); var data = new List<byte>();
            Var(meta, (uint)tables.Count);
            var names = tables.Select(table => Text(table.Name)).ToArray();
            for (var i = 0; i < tables.Count; i++)
            {
                Var(meta, names[i]); Var(meta, 1); Var(meta, (uint)tables[i].Ids.Length); Var(meta, (uint)tables[i].Ids.Length * 4);
                Var(meta, (uint)tables[i].Rows.Sum(row => row.Length));
            }
            foreach (var table in tables) foreach (var row in table.Rows) { Var(meta, (uint)row.Length); data.AddRange(row); }
            foreach (var table in tables) foreach (var id in table.Ids) meta.AddRange(BitConverter.GetBytes(id));
            return (meta.ToArray(), text.ToArray(), data.ToArray());
        }
    }
    private static void Var(List<byte> output, uint value)
    {
        while (value >= 0x80) { output.Add((byte)(value | 0x80)); value >>= 7; }
        output.Add((byte)value);
    }
    private static byte[] Row(params uint[] vars) { var row = new List<byte>(); foreach (var value in vars) Var(row, value); return row.ToArray(); }
    /// <summary>按当前构建的列编码写出 Skill 第 1–48 列（数组与非零 float 均覆盖），第 49 列为名称偏移。</summary>
    private static byte[] SkillRow(uint name)
    {
        var row = new List<byte>();
        string[] codecs = ["V", "V", "V", "V", "V", "V", "T", "V", "V", "T", "T", "T", "V", "T", "T", "I", "VA", "VA", "V", "F", "FA", "F", "UA", "UA", "UA",
            "F", "T", "T", "T", "T", "I", "U", "U", "V", "I", "I", "B", "F", "F", "B", "F", "U", "V", "F", "V", "T", "I", "F"];
        foreach (var codec in codecs)
            switch (codec)
            {
                case "V" or "I" or "B": row.Add(0); break;
                case "T": row.AddRange(BitConverter.GetBytes(3.0)); break;
                case "U": row.AddRange(BitConverter.GetBytes(0x08063289u)); break;
                case "F": row.AddRange([0x12, 0x71, 0x02]); break;
                case "VA": row.Add(2); row.Add(0); row.Add(0); break;
                case "UA": row.Add(1); row.AddRange(BitConverter.GetBytes(0x135A6D93u)); break;
                case "FA": row.Add(1); row.AddRange([0x09, 0x15]); break;
            }
        Var(row, name);
        return row.ToArray();
    }

    [Fact]
    public void DisplayNamesComeFromTheDeclaredColumnsAndMissingIdsStayUnknown()
    {
        var sheets = new Sheets();
        sheets.Table("Monster", [0x0C11E09E, 0x0FB91CAE], [Row(sheets.Text("深層棕毛鼠")), Row(sheets.Text("<color=#FF0000>深層泥魔咒術師</color>"))]);
        sheets.Table("Buff", [0x08063289], [Row(sheets.Text("Synergy"), sheets.Text("召喚連結者"), sheets.Text("傷害增加 25%"))]);
        sheets.Table("Skill", [0x4EBE8A05, 0x1D4ADFF2], [SkillRow(sheets.Text("召喚連結者")), SkillRow(0)]);
        var fields = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Monster"] = ["Id", "DisplayName", "CombatStat"], ["Buff"] = ["Id", "DisplayBuffTypeText", "DisplayName", "DescriptionText"],
            ["Skill"] = [.. ClientCombatNameCatalog.SkillColumns, "ActionBoundHitStageTimelines"]
        };
        ActorTables(sheets, fields);
        var (meta, text, data) = sheets.Build();

        var names = ClientCombatNameCatalog.Decode(meta, text, data, fields, "fixture");
        Assert.Empty(names.Unsupported);
        Assert.Equal("深層棕毛鼠", names.Monsters[0x0C11E09E]);
        Assert.Equal("深層泥魔咒術師", names.Monsters[0x0FB91CAE]);
        Assert.Equal("召喚連結者", names.Effects[0x08063289]);
        Assert.Equal("召喚連結者", names.Skills[0x4EBE8A05]);
        // 名称为空的行与表中不存在的 ID 都不给名称，由读取侧标为 unknown。
        Assert.False(names.Skills.ContainsKey(0x1D4ADFF2));
        Assert.False(names.Effects.ContainsKey(0x02B5D546));

        // 客户端字段声明与已核对的列顺序不符时整表不可用，不按旧位置猜测名称。
        var moved = new Dictionary<string, IReadOnlyList<string>>(fields) { ["Skill"] = ["Id", "DisplayName"] };
        var unsupported = ClientCombatNameCatalog.Decode(meta, text, data, moved, "fixture");
        Assert.Equal("combat.nameLayoutUnsupported", unsupported.Unsupported["skill"]);
        Assert.Empty(unsupported.Skills); Assert.Equal(2, unsupported.Monsters.Count);
    }

    /// <summary>怪物以外的自动体名称表（第 1 列 DisplayName）、GimmickActor → GimmickActorState 的出生状态名称。</summary>
    private static void ActorTables(Sheets sheets, Dictionary<string, IReadOnlyList<string>> fields)
    {
        foreach (var (sheet, id, name) in new[] { ("NPC", 3477078042u, "愛麗沙"), ("NPCCompanion", 1653633150u, "艾爾達"), ("Pet", 1092954967u, "羊"),
            ("SummonedAutomaton", 1201808552u, "陰影"), ("MemorialSagaMinion", 77u, "隨從") })
        { sheets.Table(sheet, [id], [Row(sheets.Text(name))]); fields[sheet] = ["Id", "DisplayName", "Next"]; }
        sheets.Table("GimmickActorState", [1335824821, 559316366], [Row(sheets.Text("柱子")), Row(0)]);
        var gimmick = new List<byte>(); gimmick.Add(0); gimmick.Add(0); gimmick.Add(2);
        gimmick.AddRange(BitConverter.GetBytes(559316366u)); gimmick.AddRange(BitConverter.GetBytes(1335824821u)); gimmick.Add(0);
        gimmick.AddRange(BitConverter.GetBytes(0u));
        sheets.Table("GimmickActor", [865690504], [gimmick.ToArray()]);
        fields["GimmickActorState"] = ["Id", "DisplayName"]; fields["GimmickActor"] = [.. ClientCombatNameCatalog.GimmickColumns, "StateOverrideWhenDeactivate"];
        fields["Raid"] = ClientCombatNameCatalog.RaidColumns; fields["RaidGroup"] = ["Id", "DisplayName"];
        fields["BattleZone"] = ClientCombatNameCatalog.BattleZoneColumns; fields["BattleZoneGroup"] = ["Index", "Id", "DisplayName"];
    }

    [Fact]
    public void OtherActorTypesAndEmptyMonsterNamesResolveFromTheirTables()
    {
        var sheets = new Sheets();
        const uint raidBoss = 0x2D766F8D, zoneSub = 0x11111111, nameless = 0x22222222, named = 0x33333333;
        sheets.Table("Monster", [raidBoss, zoneSub, nameless, named], [Row(sheets.Text(" ")), Row(sheets.Text("")), Row(sheets.Text(" ")), Row(sheets.Text("佩里"))]);
        sheets.Table("Buff", [1], [Row(0, sheets.Text("x"))]);
        sheets.Table("Skill", [1], [SkillRow(sheets.Text("x"))]);
        var fields = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Monster"] = ["Id", "DisplayName"], ["Buff"] = ["Id", "DisplayBuffTypeText", "DisplayName"], ["Skill"] = ClientCombatNameCatalog.SkillColumns
        };
        ActorTables(sheets, fields);
        var raid = new List<byte>(); raid.AddRange(BitConverter.GetBytes(1622709806u)); raid.AddRange(BitConverter.GetBytes(5u));
        raid.AddRange(new byte[6]); raid.AddRange(BitConverter.GetBytes(7u)); raid.Add(0); raid.AddRange(BitConverter.GetBytes(raidBoss));
        sheets.Table("Raid", [1561309765], [raid.ToArray()]);
        sheets.Table("RaidGroup", [1622709806], [Row(sheets.Text("白魅魔"))]);
        var zone = new List<byte> { 0, 0 }; zone.AddRange(BitConverter.GetBytes(named)); zone.Add(1); zone.AddRange(BitConverter.GetBytes(zoneSub));
        zone.AddRange(BitConverter.GetBytes(0u)); zone.AddRange(BitConverter.GetBytes(928252885u));
        sheets.Table("BattleZone", [662468863], [zone.ToArray()]);
        sheets.Table("BattleZoneGroup", [928252885], [Row(sheets.Text("佩里"))]);
        var (meta, text, data) = sheets.Build();

        var names = ClientCombatNameCatalog.Decode(meta, text, data, fields, "fixture");
        Assert.Empty(names.Unsupported);
        Assert.Equal("愛麗沙", names.Actors!["npc"][3477078042]); Assert.Equal("艾爾達", names.Actors["npc-companion"][1653633150]);
        Assert.Equal("羊", names.Actors["pet"][1092954967]); Assert.Equal("陰影", names.Actors["summoned-automaton"][1201808552]);
        Assert.Equal("柱子", names.Actors["gimmick"][865690504]);
        Assert.Equal([zoneSub, nameless, raidBoss], names.EmptyMonsters!.Order());
        Assert.Equal("白魅魔", names.BossGroupNames![raidBoss]); Assert.Equal("佩里", names.BossGroupNames[zoneSub]);
        Assert.False(names.BossGroupNames.ContainsKey(nameless));
    }
}
