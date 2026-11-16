using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.Observation.Fixtures;

/// <summary>
/// 共用观察离线 fixture：库存布局使用当前台服协议号（登录个人数据 20590 与战斗 Self 共用、身份关联 170631），
/// 合成 schema 的完整登录体以当前战斗 fixture 的角色 ID 开头，使战斗与库存解析同一帧。
/// </summary>
public static class CoexistenceInventoryFixture
{
    public const uint Login = 20590, Profile = 170631, ConsumableStack = 170427;
    public static readonly ObservationSource Source = new("mabiagent-inventory", "1.0.0", "TW", TwClientFingerprint.GameBuild);

    public static ClientCaptureLayout Layout { get; } = new(Tw20261110CombatProtocol.AssemblySha256, Tw20261110CombatProtocol.MetadataSha256,
        new(Login, Profile, ConsumableStack), Schema())
    {
        Opcodes = new Dictionary<string, uint>
        {
            ["CHANNEL_MyCharacterPersonalData_NTF"] = Login,
            ["REPLICATION_GuildMembers_UpdateGuildMemberInfo_REPL"] = Profile,
            ["REPLICATION_ConsumableInventory_UpdateConsumableStack_REPL"] = ConsumableStack,
        },
        Methods = new Dictionary<uint, InventoryProtocolMethod>
        {
            [Profile] = new("UpdateGuildMemberInfo", "GuildMembers", ["AccountId", "Profile"]),
        }
    };

    public static ObservationInventoryProfile InventoryProfile { get; } = new("fixture-coexist-inventory", Source,
        Layout.AssemblyHash, Layout.MetadataHash, ["Equipment", "Consumable"], true, false);

    private static JsonElement Schema()
    {
        var types = new Dictionary<string, object>();
        void Record(string name, string? parent, params (string Name, string Type)[] fields) =>
            types.Add(name, new { parent = parent ?? "object", fields = fields.Select(f => new { name = f.Name, type = f.Type }).ToArray() });
        // 当前客户端库存 schema 含 924 个类型；补足同量级的未用类型，使按名称查找类型的成本与实机一致。
        for (var i = 0; i < 900; i++) Record("Shared.Protocols.Unused.Type" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), null, ("Value", "int32"));
        Record("Base", null, ("CharacterId", "int64"));
        Record(ClientInventoryDecoder.RootType, "Base", ("EquipmentInventory", "Equipments"), ("ConsumableInventory", "Consumables"), ("EquipmentSlots", "Slots"));
        Record("NonStackableId", null, ("rawValue", "int64"));
        Record("EquipmentSheetId", null, ("Id", "uint32"));
        Record("Durability", null, ("Current", "int32"), ("Maximum", "int32"));
        Record("Equipment", null, ("Id", "NonStackableId"), ("SheetId", "EquipmentSheetId"), ("IsLocked", "bool"), ("Durability", "Durability"), ("Label", "string"));
        Record("Stored", null, ("Data", "Equipment"), ("IsAccountStorage", "bool"));
        Record("Equipments", null, ("Equipments", "System.Collections.Generic.Dictionary`2<NonStackableId,Equipment>"),
            ("EquipmentsInStorage", "System.Collections.Generic.Dictionary`2<NonStackableId,Stored>"));
        Record("Stack", null, ("SheetId", "uint32"), ("Amount", "int64"));
        Record("Consumables", null, ("ConsumableStacks", "System.Collections.Generic.Dictionary`2<uint32,Stack>"),
            ("ConsumableStacksInAccountStorage", "System.Collections.Generic.Dictionary`2<uint32,Stack>"),
            ("ConsumableStacksInCharacterStorage", "System.Collections.Generic.Dictionary`2<uint32,Stack>"));
        Record("Slots", null, ("Slots", "System.Collections.Generic.Dictionary`2<int32,NonStackableId>"));
        Record("AccountId", null, ("rawValue", "int64"));
        Record("Recent", null, ("CharacterId", "int64"), ("Name", "string"));
        Record("Profile", null, ("<RecentCharacterId>k__BackingField", "int64"), ("<RecentCharacter>k__BackingField", "Recent"));
        return JsonSerializer.SerializeToElement(new { types });
    }

    /// <summary>完整登录体：前 8 字节为角色 ID（与战斗个人数据前缀一致），装备与消耗品各 <paramref name="perCategory"/> 项。</summary>
    public static byte[] LoginBody(ulong character, int perCategory)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(character);
        w.Write(perCategory);
        for (var i = 0; i < perCategory; i++)
        {
            var id = 4000000000000001L + i;
            w.Write(id); w.Write(id); w.Write((uint)(1000 + i % 500)); w.Write(false); w.Write(100); w.Write(100);
            var label = Encoding.Unicode.GetBytes("fixture-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)); w.Write(label.Length); w.Write(label);
        }
        w.Write(0);
        w.Write(perCategory);
        for (var i = 0; i < perCategory; i++) { w.Write((uint)(50000 + i)); w.Write((uint)(50000 + i)); w.Write(1L + i % 99); }
        w.Write(0); w.Write(0);
        w.Write(0);
        return stream.ToArray();
    }

    public static byte[] ProfileBody(ulong character)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(7L); w.Write(character); w.Write(character);
        var name = Encoding.Unicode.GetBytes("Fixture"); w.Write(name.Length); w.Write(name); return stream.ToArray();
    }

    public static ulong PersonalCharacter(byte[] personalBody) => BinaryPrimitives.ReadUInt64LittleEndian(personalBody);
}
