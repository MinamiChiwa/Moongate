// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.Providers.TwObservation.Tests;

internal sealed class InventoryStorageTestData
{
    internal static readonly string[] SeparateWarehouses = ["Consumable", "Socketable", "Artifact", "HousingProp"];
    internal JsonElement Schema { get; }
    internal IReadOnlyList<InventoryProtocolMethod> Methods { get; }
    internal ClientCaptureLayout Layout { get; }
    internal InventoryStorageTestData(InventoryProtocolMethod? extra = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
        using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "tests/Providers/MabiTool.Providers.TwObservation.Tests/Fixtures/inventory-current-methods.json")));
        using var historical = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "contracts/fixtures/inventory-client-schema.historical.json")));
        var types = historical.RootElement.GetProperty("types").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        foreach (var type in current.RootElement.GetProperty("schema").GetProperty("types").EnumerateObject()) types[type.Name] = type.Value.Clone();
        foreach (var type in InventoryDecoderTests.SimpleSchema().GetProperty("types").EnumerateObject().Where(p => p.Name is "AccountId" or "Recent" or "Profile")) types[type.Name] = type.Value.Clone();
        var methods = current.RootElement.GetProperty("methods").Deserialize<InventoryProtocolMethod[]>()!.ToList();
        foreach (var category in InventoryCurrentMutationTests.Categories().Select(row => (string)row[0]).Where(c => !c.StartsWith("MemorialSaga") && c != "JamItem"))
        {
            var add = methods.Single(m => m.Category == category && m.Name == "Add" + category);
            var remove = methods.Single(m => m.Category == category && m.Name == "Remove" + category);
            if (SeparateWarehouses.Contains(category))
            {
                foreach (var container in new[] { "AccountStorage", "CharacterStorage" })
                {
                    methods.Add(new("Add" + category + "To" + container, category, add.Parameters));
                    methods.Add(new("Update" + category + "In" + container, category, add.Parameters));
                    methods.Add(new("Remove" + category + "From" + container, category, remove.Parameters));
                }
            }
            else
            {
                var dataType = add.Parameters[1]; var at = dataType.LastIndexOf('.') + 1;
                var storedType = dataType[..at] + "Stored" + dataType[at..];
                // Current metadata uses Data followed by IsAccountStorage for every non-stackable storage wrapper.
                types[storedType] = Record(("Data", dataType), ("IsAccountStorage", "bool"));
                methods.Add(new("Add" + category + "ToStorage", category, [add.Parameters[0], storedType]));
                methods.Add(new("Remove" + category + "FromStorage", category, remove.Parameters));
                if (category is "Equipment" or "Costume") methods.Add(new("Update" + category + (category == "Equipment" ? "OnStorage" : "InStorage"), category, [add.Parameters[0], storedType]));
            }
        }
        var fields = historical.RootElement.GetProperty("types").GetProperty(ClientInventoryDecoder.RootType).GetProperty("fields")
            .EnumerateArray().Where(f => f.GetProperty("name").GetString()!.EndsWith("Inventory", StringComparison.Ordinal))
            .Select(f => (f.GetProperty("name").GetString()!, f.GetProperty("type").GetString()!)).ToList();
        var fish = methods.Single(m => m.Name == "AddAquariumFish"); var storedFish = methods.Single(m => m.Name == "AddAquariumFishToStorage");
        types["TestAquariumInventory"] = Record(("AquariumFishes", $"System.Collections.Generic.Dictionary`2<{fish.Parameters[0]},{fish.Parameters[1]}>"),
            ("AquariumFishesInStorage", $"System.Collections.Generic.Dictionary`2<{fish.Parameters[0]},{storedFish.Parameters[1]}>"));
        fields.Add(("AquariumFishInventory", "TestAquariumInventory"));
        types["TestCharacterBase"] = Record(("CharacterId", "int64"));
        types[ClientInventoryDecoder.RootType] = JsonSerializer.SerializeToElement(new { parent = "TestCharacterBase", fields = fields.Select(f => new { name = f.Item1, type = f.Item2 }).ToArray() });
        if (extra is not null) methods.Add(extra);
        Schema = JsonSerializer.SerializeToElement(new { types }); Methods = methods;
        var map = methods.Select((method, index) => (method, code: (uint)(1000 + index))).ToDictionary(x => x.code, x => x.method);
        map[2] = new("UpdateGuildMemberInfo", "GuildMembers", ["AccountId", "Profile"]);
        Layout = new("assembly", "metadata", new(1, 2, 3), Schema)
        { Methods = map, Opcodes = map.ToDictionary(p => p.Value.Name, p => p.Key).Append(new("login", 1)).ToDictionary(p => p.Key, p => p.Value) };
    }
    internal uint Code(InventoryProtocolMethod method) => Layout.Methods.Single(p => p.Value == method).Key;
    internal byte[] Body(InventoryProtocolMethod method, long quantity = 3, bool account = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        for (var index = 0; index < method.Parameters.Count; index++) InventoryCurrentMutationTests.Write(writer,
            Schema.GetProperty("types"), method.Parameters[index], index == 0 ? 3 : quantity, accountStorage: account);
        return stream.ToArray();
    }
    internal byte[] EmptyLogin()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        Default(writer, ClientInventoryDecoder.RootType); return stream.ToArray();
    }
    private void Default(BinaryWriter writer, string name, string? field = null)
    {
        if (name is "object" or "System.Object" or "System.ValueType" or "System.Enum") return;
        if (field == "CharacterId") { writer.Write(9007199254740993L); return; }
        var bytes = name switch { "bool" or "int8" or "uint8" => 1, "int16" or "uint16" or "char" => 2,
            "int32" or "uint32" or "float32" => 4, "int64" or "uint64" or "float64" => 8, "System.Decimal" => 16, _ => 0 };
        if (bytes != 0) { writer.Write(new byte[bytes]); return; }
        if (name.StartsWith("System.Nullable`1<")) { writer.Write(false); return; }
        if (name == "string" || name.Contains('<') || name.EndsWith("[]")) { writer.Write(0); return; }
        var type = Schema.GetProperty("types").GetProperty(name); Default(writer, type.GetProperty("parent").GetString()!);
        foreach (var member in type.GetProperty("fields").EnumerateArray()) Default(writer, member.GetProperty("type").GetString()!, member.GetProperty("name").GetString());
    }
    private static JsonElement Record(params (string Name, string Type)[] fields) => JsonSerializer.SerializeToElement(new { parent = "object", fields = fields.Select(f => new { name = f.Name, type = f.Type }).ToArray() });
}
