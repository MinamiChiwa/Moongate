using System.Buffers.Binary;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Inventory;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class InventoryDecoderTests
{
    [Fact]
    public void ReadsWholeHistoricalRootAndEveryDeclaredInventory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "contracts/fixtures/inventory-client-schema.historical.json"))) root = root.Parent;
        Assert.NotNull(root);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "contracts/fixtures/inventory-client-schema.historical.json")));
        var types = json.RootElement.GetProperty("types");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        WriteDefault(writer, types, ClientInventoryDecoder.RootType);
        var decoder = new ClientInventoryDecoder(json.RootElement);
        var value = decoder.DecodeLogin(stream.ToArray());
        Assert.Equal(9007199254740993UL, value.CharacterId.Value);
        Assert.Empty(value.Items);
        Assert.Contains(value.Coverage, c => c.Category == "Equipment" && c.Complete);
        Assert.Contains(value.Coverage, c => c.Category == "EquipmentTool" && c.Complete);
        Assert.Contains(value.Coverage, c => c.Category == "JamItem" && c.Complete);
        Assert.Contains(value.Coverage, c => c.Category == "MemorialSagaItem" && c.Complete);
        Assert.True(value.Coverage.Select(c => c.Category).Distinct().Count() >= 15);
        Assert.Throws<InvalidDataException>(() => decoder.DecodeLogin(stream.ToArray()[..^1]));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeLogin(stream.ToArray().Concat(new byte[] { 0 }).ToArray()));
    }
    [Fact]
    public void EquipmentInstancesStorageAndStackKeysRemainDistinct()
    {
        var schema = SimpleSchema(); var decoder = new ClientInventoryDecoder(schema);
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(9007199254740993L);
        w.Write(2); Equipment(w, 4000000000000001L, 42); Equipment(w, 4000000000000002L, 42);
        w.Write(2); Stored(w, 4000000000000003L, 43, true); Stored(w, 4000000000000004L, 44, false);
        w.Write(1); w.Write(42U); w.Write(42U); w.Write(19L);
        w.Write(0); w.Write(0);
        w.Write(1); w.Write(0); w.Write(4000000000000001L);
        var value = decoder.DecodeLogin(stream.ToArray());
        Assert.Equal(5, value.Items.Count);
        Assert.Equal(2, value.Items.Count(i => i.Category == "Equipment" && i.Container.KnownValue == InventoryContainer.Backpack));
        Assert.Single(value.Items, i => i.Container.KnownValue == InventoryContainer.AccountStorage);
        Assert.Single(value.Items, i => i.Container.KnownValue == InventoryContainer.CharacterStorage);
        Assert.Equal(19UL, Assert.Single(value.Items, i => i.Category == "Consumable").Quantity!.Value.Value);
        Assert.True(value.Items.Single(i => i.InstanceId?.Value == 4000000000000001UL).Equipped);
        Assert.False(value.Items.Single(i => i.InstanceId?.Value == 4000000000000002UL).Equipped);
        Assert.Contains("4000000000000001", value.Items[0].Attributes.GetRawText());
        var broken = stream.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(broken.AsSpan(20), 9);
        Assert.Throws<InvalidDataException>(() => decoder.DecodeLogin(broken));
    }
    [Fact]
    public void DuplicateTruncatedNegativeAndBudgetedInventoryUpdatesAreRejected()
    {
        var decoder = new ClientInventoryDecoder(SimpleSchema());
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(42U); w.Write(19L); w.Write(43U); w.Write(20L);
        var layout = new InventoryUpdateLayout("Consumable", InventoryContainer.Backpack, "Stack", InventoryDeltaKind.Upsert, Repeated: true);
        var updates = decoder.DecodeUpdate(stream.ToArray(), layout);
        Assert.Equal(new ulong[] { 19, 20 }, updates.Changes.Select(i => i.Item.Quantity!.Value.Value));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeUpdate(stream.ToArray()[..^1], layout));
        var negative = stream.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(negative.AsSpan(4), -1);
        Assert.Throws<InvalidDataException>(() => decoder.DecodeUpdate(negative, layout));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeUpdate(stream.ToArray()[..12].Concat(stream.ToArray()[..12]).ToArray(), layout));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeLogin(new byte[ClientInventoryDecoder.MaximumBodyBytes + 1]));
    }
    [Fact]
    public void NullableFlagsAndCollectionCountsAreStrict()
    {
        var decoder = new ClientInventoryDecoder(SimpleSchema());
        var layout = new InventoryUpdateLayout("Consumable", InventoryContainer.Backpack, "Stack", InventoryDeltaKind.Upsert, Collection: true);
        Assert.Throws<InvalidDataException>(() => decoder.DecodeUpdate(BitConverter.GetBytes(4097), layout));
        Assert.Empty(decoder.DecodeUpdate(BitConverter.GetBytes(0), layout).Changes);
    }
    [Fact]
    public void EquippedCostumeAndMusicAlbumRelationsDoNotDuplicateOwnedItems()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(9007199254740993L);
        for (var i = 0; i < 6; i++) writer.Write(0);
        writer.Write(1); Equipment(writer, 4000000000000001L, 42);
        writer.Write(0);
        writer.Write(1); Equipment(writer, 4000000000000001L, 42);
        writer.Write(0);
        writer.Write(1); writer.Write(4000000000000002L); writer.Write(7L);
        writer.Write(1); writer.Write(4000000000000003L); writer.Write(4000000000000002L);
        writer.Write(0);
        var value = new ClientInventoryDecoder(SimpleSchema(true)).DecodeLogin(stream.ToArray());
        var item = Assert.Single(value.Items); Assert.Equal("Costume", item.Category); Assert.True(item.Equipped);
        Assert.Contains(value.Coverage, c => c.Category == "MusicNoteItem" && c.Complete);
    }
    [Fact]
    public void LowercaseSocketableSheetKeysAndScalarJamQuantitiesAreDecoded()
    {
        var decoder = new ClientInventoryDecoder(SimpleSchema());
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(42U); writer.Write(false); writer.Write(42U); writer.Write(false); writer.Write(19L);
        var socket = decoder.DecodeUpdate(stream.ToArray(), new("Socketable", InventoryContainer.Backpack, "SocketStack", InventoryDeltaKind.Upsert, "SocketKey"));
        var item = Assert.Single(socket.Changes).Item;
        Assert.Equal(42UL, item.TypeId.Value); Assert.Equal("Socketable", item.Sheet); Assert.Null(item.InstanceId);
        Assert.Equal(19UL, item.Quantity!.Value.Value);
        stream.SetLength(0); writer.Write(42U); writer.Write(3L);
        var jam = decoder.DecodeUpdate(stream.ToArray(), new("JamItem", InventoryContainer.Backpack, "int64", InventoryDeltaKind.Upsert, "JamItemSheetId"));
        Assert.Equal(3UL, Assert.Single(jam.Changes).Item.Quantity!.Value.Value);
    }
    internal static JsonElement SimpleSchema(bool extras = false)
    {
        var types = new Dictionary<string, object>();
        void Record(string name, string? parent, params (string Name, string Type)[] fields) =>
            types.Add(name, new { parent = parent ?? "object", fields = fields.Select(f => new { name = f.Name, type = f.Type }).ToArray() });
        Record("Base", null, ("CharacterId", "int64"));
        var rootFields = new List<(string, string)> { ("EquipmentInventory", "Equipments"), ("ConsumableInventory", "Consumables"), ("EquipmentSlots", "Slots") };
        if (extras) { rootFields.Add(("CostumeInventory", "Costumes")); rootFields.Add(("MusicNoteItemInventory", "Notes")); }
        Record(ClientInventoryDecoder.RootType, "Base", rootFields.ToArray());
        Record("NonStackableId", null, ("rawValue", "int64"));
        Record("EquipmentSheetId", null, ("Id", "uint32"));
        Record("Equipment", null, ("Id", "NonStackableId"), ("SheetId", "EquipmentSheetId"), ("IsLocked", "bool"));
        Record("Stored", null, ("Data", "Equipment"), ("IsAccountStorage", "bool"));
        Record("Equipments", null, ("Equipments", "System.Collections.Generic.Dictionary`2<NonStackableId,Equipment>"), ("EquipmentsInStorage", "System.Collections.Generic.Dictionary`2<NonStackableId,Stored>"));
        Record("Stack", null, ("SheetId", "uint32"), ("Amount", "int64"));
        Record("Consumables", null, ("ConsumableStacks", "System.Collections.Generic.Dictionary`2<uint32,Stack>"), ("ConsumableStacksInAccountStorage", "System.Collections.Generic.Dictionary`2<uint32,Stack>"), ("ConsumableStacksInCharacterStorage", "System.Collections.Generic.Dictionary`2<uint32,Stack>"));
        Record("Slots", null, ("Slots", "System.Collections.Generic.Dictionary`2<int32,NonStackableId>"));
        Record("Costumes", null, ("Costumes", "System.Collections.Generic.Dictionary`2<NonStackableId,Equipment>"),
            ("CostumesInStorage", "System.Collections.Generic.Dictionary`2<NonStackableId,Stored>"), ("EquippedCostumes", "System.Collections.Generic.Dictionary`2<NonStackableId,Equipment>"));
        Record("Notes", null, ("MusicNoteItems", "System.Collections.Generic.Dictionary`2<NonStackableId,Equipment>"),
            ("MusicNoteAlbums", "System.Collections.Generic.Dictionary`2<NonStackableId,int64>"),
            ("MusicNoteItemAlbumMap", "System.Collections.Generic.Dictionary`2<NonStackableId,NonStackableId>"),
            ("MusicNoteItemsInStorage", "System.Collections.Generic.Dictionary`2<NonStackableId,Stored>"));
        Record("AccountId", null, ("rawValue", "int64"));
        Record("Recent", null, ("CharacterId", "int64"), ("Name", "string"));
        Record("Profile", null, ("<RecentCharacterId>k__BackingField", "int64"), ("<RecentCharacter>k__BackingField", "Recent"));
        Record("SocketableSheetId", null, ("Id", "uint32"));
        Record("SocketKey", null, ("sheetId", "SocketableSheetId"), ("canTrade", "bool"));
        Record("SocketStack", null, ("Key", "SocketKey"), ("Amount", "int64"));
        Record("JamItemSheetId", null, ("Id", "uint32"));
        return JsonSerializer.SerializeToElement(new { types });
    }
    private static void Equipment(BinaryWriter writer, long id, uint sheet)
    { writer.Write(id); writer.Write(id); writer.Write(sheet); writer.Write(false); }
    private static void Stored(BinaryWriter writer, long id, uint sheet, bool account)
    { Equipment(writer, id, sheet); writer.Write(account); }
    private static void WriteDefault(BinaryWriter writer, JsonElement types, string? name)
    {
        if (name is null or "object" or "System.Object" or "System.ValueType" or "System.Enum") return;
        if (name == "Shared.Characters.CharacterId") { writer.Write(9007199254740993L); return; }
        var size = name switch { "int8" or "uint8" or "bool" => 1, "int16" or "uint16" or "char" => 2,
            "int32" or "uint32" or "float32" => 4, "int64" or "uint64" or "float64" => 8, "System.Decimal" => 16, _ => 0 };
        if (size > 0) { writer.Write(new byte[size]); return; }
        if (name == "string") { writer.Write(0); return; }
        if (name.StartsWith("System.Nullable`1<", StringComparison.Ordinal) && name.EndsWith('>' )) { writer.Write(false); return; }
        if (name.Contains('<') || name.EndsWith("[]", StringComparison.Ordinal)) { writer.Write(0); return; }
        var record = types.GetProperty(name);
        var parent = record.GetProperty("parent").GetString();
        WriteDefault(writer, types, parent);
        foreach (var field in record.GetProperty("fields").EnumerateArray()) { WriteDefault(writer, types, field.GetProperty("type").GetString()); }
    }
}
