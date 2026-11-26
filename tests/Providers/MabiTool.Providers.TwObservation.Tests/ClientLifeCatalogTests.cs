using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>按客户端 Sheetdata 的键块与行正文编码合成生活相关的表，核对归属链路（技能、掉落、设施）与核对失败时的降级。</summary>
public sealed class ClientLifeCatalogTests
{
    private sealed class Sheets
    {
        private readonly MemoryStream text = new();
        private readonly List<(string Name, byte[] Block, byte[][] Rows)> tables = [];

        public Sheets()
        {
            text.WriteByte(0);
            // 设施类型枚举名的文本偏移需要三字节变长整数（真实客户端同样如此），先垫满前 16 KiB。
            for (var i = 0; i < 170; i++) Text(new string((char)('a' + i % 26), 100));
        }

        public uint Text(string value)
        {
            var offset = (uint)text.Position; var bytes = Encoding.UTF8.GetBytes(value);
            text.WriteByte((byte)bytes.Length); text.Write(bytes); text.WriteByte(0);
            return offset;
        }

        public void Table(string name, byte[] block, params byte[][] rows) => tables.Add((name, block, rows));

        public (byte[] Meta, byte[] Text, byte[] Data) Build()
        {
            var meta = new List<byte>(); var data = new List<byte>();
            Var(meta, (uint)tables.Count);
            var names = tables.Select(table => Text(table.Name)).ToArray();
            for (var i = 0; i < tables.Count; i++)
            {
                Var(meta, names[i]); Var(meta, 1); Var(meta, (uint)tables[i].Rows.Length); Var(meta, (uint)tables[i].Block.Length);
                Var(meta, (uint)tables[i].Rows.Sum(row => row.Length));
            }
            foreach (var table in tables) foreach (var row in table.Rows) { Var(meta, (uint)row.Length); data.AddRange(row); }
            foreach (var table in tables) meta.AddRange(table.Block);
            return (meta.ToArray(), text.ToArray(), data.ToArray());
        }
    }

    private static void Var(List<byte> output, uint value)
    {
        while (value >= 0x80) { output.Add((byte)(value | 0x80)); value >>= 7; }
        output.Add((byte)value);
    }

    private static byte[] Bytes(params object[] parts)
    {
        var output = new List<byte>();
        foreach (var part in parts)
            switch (part)
            {
                case uint value: output.AddRange(BitConverter.GetBytes(value)); break;
                case int small: output.Add((byte)small); break;
                case byte one: output.Add(one); break;
                case VarInt variable: Var(output, variable.Value); break;
                case byte[] raw: output.AddRange(raw); break;
            }
        return output.ToArray();
    }

    private sealed record VarInt(uint Value);

    private static object V(uint value) => new VarInt(value);
    private static readonly byte[] Empty = [];
    // 客户端的道具 Id：开发名 MD5 前四字节按有符号小端取绝对值（“Egg”的 MD5 首字节高位为 1，按无符号读取会换算不出）。
    private static uint Key(string symbol) => (uint)Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(MD5.HashData(Encoding.UTF8.GetBytes(symbol))));

    private const uint Woodcutting = 0x66DFB78D, Fishing = 0x62C9CB05, Daily = 0x0EE8D90A;
    private const uint GroupLog = 11, GroupBranch = 12, GroupFish = 13, GroupEgg = 14;

    private static (Sheets Sheets, Dictionary<string, IReadOnlyList<string>> Fields) Fixture()
    {
        var sheets = new Sheets();
        var fields = new Dictionary<string, IReadOnlyList<string>>
        {
            ["LivingSkill"] = ["Id", "DisplayName", "FlavorText", "LivingSkillType", "RequiredEquipmentToolTypeSheetId", "LimitLevel"],
            ["Gatherable"] = ["Id", "DisplayName", "Description", "GatherUnlockLivingSkill", "GatherUnlockSkillLevel", "RequiredConsumableId", "RequiredConsumableAmount",
                "InteractionId", "GatherDropRewardIds", "Next"],
            ["DropRewardTable"] = ["GroupId", "PoolGroupNumber", "PickCount", "IsFixedDrop", "RewardData", "FishableSheetId", "IsHidden"],
            ["Consumable"] = ["Id", "DisplayName", "FlavorText", "Rarity"],
            ["FacilityType"] = ["FacilityType", "DisplayName", "Title", "IconPath"],
            ["Altering"] = ["FeatureSwitch", "Id", "DisplayName", "Description", "FacilityType", "Next"],
            ["CraftingRecipe"] = ["Index", "Id", "DisplayName", "Description", "IsBasicCrafting", "BasicCraftingOrder", "FacilityType", "FacilityLevel",
                "RecipeUnlockLivingSkill", "RecipeUnlockSkillLevel", "Next"]
        };
        // 生活技能：键块 Id、类型、必备工具、顺序；正文首列名称。
        var types = new[] { sheets.Text("Gathering"), sheets.Text("Fishing"), sheets.Text("Gathering") };
        sheets.Table("LivingSkill", Bytes(Woodcutting, V(types[0]), 1u, 1, Fishing, V(types[1]), 2u, 2, Daily, V(types[2]), 3u, 3),
            Bytes(V(sheets.Text("伐木"))), Bytes(V(sheets.Text("釣魚"))), Bytes(V(sheets.Text("日常採集"))));
        // 道具：键块 Id、名称、说明；Id 是开发名 MD5 前四字节。
        sheets.Table("Consumable", Bytes(Key("Log"), V(sheets.Text("原木")), V(0), Key("FishSalmon"), V(sheets.Text("鮭魚")), V(0), Key("Branch"), V(sheets.Text("樹枝")), V(0),
            Key("Hammer"), V(sheets.Text("鐵錘")), V(0), Key("Egg"), V(sheets.Text("雞蛋")), V(0)), Empty, Empty, Empty, Empty, Empty);
        // 掉落奖励：键块组 Id；正文 池号、个数、固定、类型、开发名…行尾 FishableSheetId 与 IsHidden。
        byte[] Reward(string symbol, uint fishable, string type = "Consumable") =>
            Bytes(1, 1, (byte)0, V(sheets.Text(type)), V(sheets.Text(symbol)), 1, 1, new byte[12], fishable, (byte)0);
        sheets.Table("DropRewardTable", Bytes(GroupLog, GroupLog, GroupBranch, GroupFish, GroupFish, GroupFish, GroupEgg),
            Reward("Log", 0), Reward("Branch", 0), Reward("Branch", 0), Reward("FishSalmon", 0xAABBCCDD), Reward("Branch", 0xAABBCCDD), Reward("Unknown", 0xAABBCCDD, "Gold"),
            Reward("Egg", 0));
        // 采集点：键块 Id、技能、等级、顺序、类型、展示道具；正文 名称、说明、必需道具、数量、交互、掉落组。
        var show = sheets.Text("Show");
        byte[] Spot(string name, params uint[] groups) =>
            Bytes(V(sheets.Text(name)), V(0), 0u, 0, 0u, V((uint)groups.Length), groups.SelectMany(BitConverter.GetBytes).ToArray());
        sheets.Table("Gatherable", Bytes(101u, Woodcutting, 1, 0, V(show), 0u, 102u, Daily, 1, 0, V(show), 0u),
            Spot("樹木", GroupLog, GroupBranch), Spot("蘋果樹", GroupBranch, GroupEgg));
        // 设施：键块 枚举名与图标；正文 名称与标题。
        var altering = sheets.Text("Altering_LeatherProcessing"); var crafting = sheets.Text("Crafting_FoodCooking");
        sheets.Table("FacilityType", Bytes(V(altering), V(0), V(crafting), V(0)), Bytes(V(sheets.Text("皮革加工設備")), V(0)), Bytes(V(sheets.Text("食物製作台")), V(0)));
        // 加工：键块 Id、名称、说明、设施、等级、引用、顺序、文本、显示类型、展示道具、末列。
        sheets.Table("Altering", Bytes(7u, V(sheets.Text("皮革+")), V(0), V(altering), 1, 8u, 0, V(0), V(show), 0u, V(0)), Empty);
        // 制作：键块行长不固定，行内技能 Id 前是设施类型（三字节）与等级；正文 空名称、说明、六字节头、消耗、假道具、结果。
        byte[] Recipe(uint item) => Bytes(V(0), V(sheets.Text("說明")), new byte[6], 1, Key("Log"), 4, (byte)0, 1, item, 1);
        sheets.Table("CraftingRecipe", Bytes(21u, (byte)0, 0, V(crafting), 1, Woodcutting, 1, 20, 33u, new byte[3],
            22u, (byte)0, 0, V(crafting), 1, Daily, 1, 20, 33u), Recipe(Key("FishSalmon")), Recipe(0xDEADBEEF));
        return (sheets, fields);
    }

    [Fact]
    public void SkillsFacilitiesAndFishingAreResolvedThroughTheDeclaredColumnsAndTheDevelopmentKeyHash()
    {
        var (sheets, fields) = Fixture();
        var (meta, text, data) = sheets.Build();
        var tables = ClientLifeCatalog.Decode(meta, text, data, fields, "fixture");

        Assert.Empty(tables.Unsupported);
        // 原木只由伐木的采集点掉落；樹枝同时是日常採集与伐木采集点的掉落，也是钓鱼点的奖励，归属留给调用方按“对应多个”处理。
        Assert.Equal(["伐木"], tables.GatherSkills["原木"].Select(skill => skill.Name));
        // MD5 首字节高位为 1 的开发名同样换算出道具。
        Assert.Equal(["日常採集"], tables.GatherSkills["雞蛋"].Select(skill => skill.Name));
        Assert.Equal(["伐木", "日常採集", "釣魚"], tables.GatherSkills["樹枝"].Select(skill => skill.Name).Order(StringComparer.Ordinal));
        // 钓鱼点奖励（行尾 FishableSheetId 非零）归入类型为钓鱼的技能，只有消耗品类奖励有名称，金币等没有。
        Assert.Equal([("釣魚", true)], tables.GatherSkills["鮭魚"].Select(skill => (skill.Name, skill.IsFishing)));
        Assert.DoesNotContain("鐵錘", tables.GatherSkills.Keys);
        Assert.Equal(["皮革加工設備"], tables.AlterFacilities["皮革+"]);
        // 制作配方名取结果道具的名称；没有消耗品类结果的配方没有名称。
        Assert.Equal(["食物製作台"], tables.CraftFacilities["鮭魚"]);
        Assert.Single(tables.CraftFacilities);
    }

    [Fact]
    public void ALayoutThatDoesNotMatchOnlyMakesTheDependentCategoryUnreadable()
    {
        var (sheets, fields) = Fixture();
        var (meta, text, data) = sheets.Build();

        var moved = new Dictionary<string, IReadOnlyList<string>>(fields) { ["Altering"] = ["Id", "DisplayName", "Description"] };
        var altering = ClientLifeCatalog.Decode(meta, text, data, moved, "fixture");
        Assert.Equal("life.alterLayoutUnsupported", altering.Unsupported["alter"]);
        Assert.Empty(altering.AlterFacilities);
        Assert.Equal(2, altering.GatherSkills.Count(pair => pair.Key is "原木" or "鮭魚"));
        Assert.Single(altering.CraftFacilities);
        Assert.False(altering.Unsupported.ContainsKey("gather"));

        var facilities = new Dictionary<string, IReadOnlyList<string>>(fields) { ["FacilityType"] = ["Id", "DisplayName"] };
        var withoutFacilities = ClientLifeCatalog.Decode(meta, text, data, facilities, "fixture");
        Assert.Equal(("life.facilityLayoutUnsupported", "life.facilityLayoutUnsupported"), (withoutFacilities.Unsupported["alter"], withoutFacilities.Unsupported["craft"]));
        Assert.Equal(2, withoutFacilities.GatherSkills.Count(pair => pair.Key is "原木" or "鮭魚"));

        var drops = new Dictionary<string, IReadOnlyList<string>>(fields) { ["DropRewardTable"] = ["GroupId", "RewardData"] };
        var withoutDrops = ClientLifeCatalog.Decode(meta, text, data, drops, "fixture");
        Assert.Equal("life.gatherLayoutUnsupported", withoutDrops.Unsupported["gather"]);
        Assert.Empty(withoutDrops.GatherSkills);
        Assert.Single(withoutDrops.AlterFacilities);

        var missing = new Dictionary<string, IReadOnlyList<string>>(fields);
        missing.Remove("LivingSkill");
        Assert.Equal("life.skillLayoutUnsupported", ClientLifeCatalog.Decode(meta, text, data, missing, "fixture").Unsupported["gather"]);
    }
}
