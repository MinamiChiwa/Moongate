// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Inventory;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class InventoryCurrentMutationTests
{
    [Fact]
    public void InventoryLayoutUsesThePinnedGameIdentityAndBoundsOtherValidFingerprints()
    {
        Assert.Equal(TwClientFingerprint.GameBuild, TwClientFingerprint.GameBuildForInventoryLayout(
            Tw20261110CombatProtocol.AssemblySha256.ToUpperInvariant(), Tw20261110CombatProtocol.MetadataSha256.ToUpperInvariant()));
        foreach (var hashes in new[] { (new string('a', 64), Tw20261110CombatProtocol.MetadataSha256),
            (Tw20261110CombatProtocol.AssemblySha256, new string('b', 64)) })
        {
            var build = TwClientFingerprint.GameBuildForInventoryLayout(hashes.Item1, hashes.Item2);
            Assert.StartsWith("sha256:", build); Assert.NotEqual(TwClientFingerprint.GameBuild, build);
            Assert.InRange(build.Length, 1, 128);
            Assert.Equal(build, TwClientFingerprint.GameBuildForInventoryLayout(hashes.Item1.ToUpperInvariant(), hashes.Item2.ToUpperInvariant()));
        }
        Assert.InRange(TwClientFingerprint.GameBuild.Length, 1, 128);
        Assert.Throws<ArgumentException>(() => TwClientFingerprint.GameBuildForInventoryLayout(new string('a', 63), new string('b', 64)));
        Assert.Throws<ArgumentException>(() => TwClientFingerprint.GameBuildForInventoryLayout(new string('a', 64), new string('z', 64)));
        Assert.Throws<ArgumentException>(() => TwClientFingerprint.GameBuildForInventoryLayout(new string('a', 128), new string('b', 64)));
    }

    [Fact]
    public void MutationCapabilityRequiresAllOperationsForEveryDeclaredCategory()
    {
        var methods = new Dictionary<uint, InventoryProtocolMethod>(); uint opcode = 1;
        foreach (var category in new[] { "Equipment", "Consumable" })
            foreach (var verb in new[] { "Add", "Update", "Remove" })
                methods.Add(opcode++, new(verb + category, category, verb == "Remove" ? ["uint32"] : ["uint32", "Stack"]));
        var layout = new ClientCaptureLayout("a", "b", new(1, 2, 3), InventoryDecoderTests.SimpleSchema()) { Methods = methods };
        Assert.True(layout.SupportsInventoryMutations);
        methods.Remove(3);
        methods.Add(99, new("RemoveEquipmentTool", "EquipmentTool", ["uint32"]));
        Assert.False(layout.SupportsInventoryMutations);
        methods.Add(3, new("RemoveEquipment", "Equipment", ["uint32"]));
        methods.Remove(2);
        methods.Add(98, new("UpdateEquipmentPresetCount", "Equipment", ["uint32"]));
        Assert.False(layout.SupportsInventoryMutations);
    }

    public static IEnumerable<object[]> Categories() => new[] { "Equipment", "EquipmentTool", "Bag", "AccompanyEquipment", "Chair", "AquariumFish", "Costume", "MusicNoteItem", "Consumable", "Socketable", "Artifact", "Pet", "Ridable", "HousingProp", "MemorialSagaItem", "MemorialSagaMinion", "JamItem" }.Select(c => new object[] { c });

    public static IEnumerable<object[]> Warehouses() => Categories().Where(row => !((string)row[0]).StartsWith("MemorialSaga") && (string)row[0] != "JamItem")
        .SelectMany(row => new[] { InventoryContainer.CharacterStorage, InventoryContainer.AccountStorage }.Select(container => new object[] { row[0], container }));

    [Theory]
    [MemberData(nameof(Warehouses))]
    public void CurrentStorageMethodsUpdateAndRemoveOnlyTheirDeclaredContainer(string category, InventoryContainer container)
    {
        var data = new InventoryStorageTestData(); var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), data.Layout, frames.Add, () => ++generation);
        var at = DateTimeOffset.UtcNow;
        observer.Accept(1, data.EmptyLogin(), 1, at); observer.Accept(2, InventoryProtocolObserverTests.Profile(9007199254740993), 2, at);
        Assert.Equal(InventoryFrameKind.Full, frames[^1].Kind.KnownValue);
        Assert.Contains(frames[^1].Coverage, c => c.Category == category && c.Container.KnownValue == container && c.Complete);
        var methods = data.Methods.Where(m => m.Category == category && m.Name.Contains("Storage") &&
            (!InventoryStorageTestData.SeparateWarehouses.Contains(category) || m.Name.Contains(container == InventoryContainer.AccountStorage ? "AccountStorage" : "CharacterStorage"))).ToArray();
        var decoder = new ClientInventoryDecoder(data.Schema);
        InventoryItem? prior = null; var applied = 0;
        foreach (var verb in new[] { "Add", "Update", "Remove" })
        {
            var method = methods.SingleOrDefault(m => m.Name.StartsWith(verb));
            if (method is null) { Assert.Equal("Update", verb); Assert.DoesNotContain(category, new[] { "Equipment", "Costume", "Consumable", "Socketable", "Artifact", "HousingProp" }); continue; }
            var body = data.Body(method, verb == "Update" ? 9 : 3, container == InventoryContainer.AccountStorage);
            var raw = Assert.Single(decoder.DecodeMethod(body, method).Changes).Item;
            observer.Accept((int)data.Code(method), body, 3 + applied++, at);
            var frame = frames[^1]; Assert.Equal(InventoryFrameKind.Delta, frame.Kind.KnownValue);
            var item = Assert.Single(frame.Changes).Item;
            Assert.Equal(category, item.Category); Assert.Equal(container, item.Container.KnownValue);
            Assert.Equal(42UL, item.TypeId.Value);
            Assert.Equal(Convert.ToHexStringLower(data.Body(methods.Single(m => m.Name.StartsWith("Remove")))), item.Key);
            var stackable = InventoryStorageTestData.SeparateWarehouses.Contains(category);
            if (stackable) Assert.Null(item.InstanceId);
            else Assert.Equal(42UL, item.InstanceId!.Value.Value);
            Assert.Equal(verb == "Remove" ? 0UL : stackable ? verb == "Update" ? 9UL : 3UL : 1UL, item.Quantity!.Value.Value);
            Assert.Contains(frame.Coverage, c => c.Category == category && c.Container.KnownValue == container && c.Complete);
            if (verb != "Remove")
            {
                Assert.Equal(raw.TypeId, item.TypeId); Assert.Equal(raw.InstanceId, item.InstanceId); Assert.Equal(raw.Quantity, item.Quantity);
                Assert.Equal(raw.Attributes.GetRawText(), item.Attributes.GetRawText());
                if (prior is not null) { Assert.Equal(prior.Key, item.Key); Assert.Equal(prior.TypeId, item.TypeId); Assert.Equal(prior.InstanceId, item.InstanceId); Assert.NotEqual(prior.Attributes.GetRawText(), item.Attributes.GetRawText()); }
                prior = item;
            }
            else
            {
                Assert.Equal(prior!.Key, item.Key); Assert.Equal(prior.TypeId, item.TypeId); Assert.Equal(prior.InstanceId, item.InstanceId);
                Assert.Equal(0UL, item.Quantity!.Value.Value); Assert.Equal(prior.Attributes.GetRawText(), item.Attributes.GetRawText());
            }
        }
        Assert.Equal(category is "Equipment" or "Costume" || InventoryStorageTestData.SeparateWarehouses.Contains(category) ? 3 : 2, applied);
    }

    [Theory]
    [InlineData("JamItem")]
    [InlineData("MemorialSagaItem")]
    [InlineData("MemorialSagaMinion")]
    public void UndeclaredWarehousesKeepUnknownCoverageAfterFullAndBackpackIncrement(string category)
    {
        var data = new InventoryStorageTestData(); var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), data.Layout, frames.Add, () => ++generation);
        var at = DateTimeOffset.UtcNow; observer.Accept(1, data.EmptyLogin(), 1, at); observer.Accept(2, InventoryProtocolObserverTests.Profile(9007199254740993), 2, at);
        var method = data.Methods.Single(m => m.Name == "Add" + category);
        observer.Accept((int)data.Code(method), data.Body(method), 3, at);
        Assert.Equal(InventoryFrameKind.Delta, frames[^1].Kind.KnownValue);
        Assert.Equal(InventoryContainer.Backpack, Assert.Single(frames[^1].Changes).Item.Container.KnownValue);
        Assert.All(frames.Where(f => f.Kind.KnownValue is InventoryFrameKind.Full or InventoryFrameKind.Delta), frame =>
        {
            var storage = frame.Coverage.Where(c => c.Category == category && c.Container.KnownValue != InventoryContainer.Backpack).ToArray();
            Assert.Equal(2, storage.Length); Assert.All(storage, c => { Assert.False(c.Complete); Assert.Equal("inventory.containerNotDeclared", c.Reason); });
        });
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public void CurrentTypedAddUpdateAndRemovePreserveTheSameOwnedItem(string category)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "tests/Providers/MabiTool.Providers.TwObservation.Tests/Fixtures/inventory-current-methods.json")));
        var schema = fixture.RootElement.GetProperty("schema"); var types = schema.GetProperty("types");
        var methods = fixture.RootElement.GetProperty("methods").Deserialize<InventoryProtocolMethod[]>()!;
        var decoder = new ClientInventoryDecoder(schema);
        var decoded = new List<InventoryDelta>();
        foreach (var verb in new[] { "Add", "Update", "Remove" })
        {
            var method = Assert.Single(methods, m => (m.Category == category || m.Category == "MemorialSaga") && m.Name.StartsWith(verb + category, StringComparison.Ordinal));
            using var body = new MemoryStream(); using var writer = new BinaryWriter(body);
            for (var index = 0; index < method.Parameters.Count; index++) Write(writer, types, method.Parameters[index], index == 1 && verb == "Update" ? 9 : 3);
            var delta = Assert.Single(decoder.DecodeMethod(body.ToArray(), method).Changes);
            Assert.Equal(category, delta.Item.Category); Assert.Equal(InventoryContainer.Backpack, delta.Item.Container.KnownValue);
            Assert.Equal(verb == "Remove" ? InventoryDeltaKind.Remove : InventoryDeltaKind.Upsert, delta.Kind.KnownValue);
            decoded.Add(delta);
            Assert.Throws<InvalidDataException>(() => decoder.DecodeMethod(body.ToArray()[..^1], method));
        }
        Assert.All(decoded, delta => Assert.Equal(decoded[0].Item.Key, delta.Item.Key));
        Assert.Equal(decoded[0].Item.TypeId, decoded[1].Item.TypeId);
        Assert.Equal(0UL, decoded[2].Item.Quantity!.Value.Value);
        Assert.NotEqual(decoded[0].Item.Attributes.GetRawText(), decoded[1].Item.Attributes.GetRawText());
    }

    internal static void Write(BinaryWriter writer, JsonElement types, string name, long quantity, string? field = null, bool accountStorage = false)
    {
        if (name is "object" or "System.Object" or "System.ValueType" or "System.Enum") return;
        var number = field is "rawValue" or "Id" or "SheetId" or "sheetId" ? 42L : quantity;
        switch (name)
        {
            case "bool": writer.Write(field == "IsAccountStorage" ? accountStorage : field == "canTrade" ? false : quantity == 9); return;
            case "int8": case "uint8": writer.Write((byte)number); return;
            case "int16": case "uint16": case "char": writer.Write((short)number); return;
            case "int32": case "uint32": writer.Write((int)number); return;
            case "int64": case "uint64": writer.Write(field == null ? quantity : number); return;
            case "float32": writer.Write((float)number); return;
            case "float64": writer.Write((double)number); return;
            case "string": writer.Write(2); writer.Write((ushort)(quantity == 9 ? '更' : '原')); return;
            case "System.Decimal": writer.Write(new byte[16]); return;
        }
        if (name.StartsWith("System.Nullable`1<", StringComparison.Ordinal)) { writer.Write(false); return; }
        if (name.Contains('<') || name.EndsWith("[]", StringComparison.Ordinal)) { writer.Write(0); return; }
        var type = types.GetProperty(name);
        Write(writer, types, type.GetProperty("parent").GetString()!, quantity, accountStorage: accountStorage);
        foreach (var member in type.GetProperty("fields").EnumerateArray())
            Write(writer, types, member.GetProperty("type").GetString()!, member.GetProperty("name").GetString() == "Key" ? 3 : quantity, member.GetProperty("name").GetString(), accountStorage);
    }
}
