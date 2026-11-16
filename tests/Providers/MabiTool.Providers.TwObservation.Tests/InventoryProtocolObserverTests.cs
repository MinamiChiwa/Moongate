using Xunit;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class InventoryProtocolObserverTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GenericStorageRemovalRequiresExactlyOneKnownWarehouse(int matches)
    {
        var data = new InventoryStorageTestData(); var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), data.Layout, frames.Add, () => ++generation);
        var at = DateTimeOffset.UtcNow; observer.Accept(1, data.EmptyLogin(), 1, at); observer.Accept(2, Profile(9007199254740993), 2, at);
        var add = data.Methods.Single(m => m.Name == "AddEquipmentToStorage");
        var remove = data.Methods.Single(m => m.Name == "RemoveEquipmentFromStorage");
        var backpack = data.Methods.Single(m => m.Name == "AddEquipment");
        for (var index = 0; index < matches; index++) observer.Accept((int)data.Code(add), data.Body(add, account: index == 0), 3 + index, at);
        var retained = frames.Where(f => f.Kind.KnownValue == InventoryFrameKind.Delta).SelectMany(f => f.Changes).Select(c => c.Item).ToArray();
        observer.Accept((int)data.Code(remove), data.Body(remove), 6, at);
        if (matches == 1)
        {
            Assert.Equal(InventoryFrameKind.Delta, frames[^1].Kind.KnownValue);
            var removed = Assert.Single(frames[^1].Changes).Item; var previous = Assert.Single(retained);
            Assert.Equal(InventoryContainer.AccountStorage, removed.Container.KnownValue);
            Assert.Equal(previous.Key, removed.Key); Assert.Equal(previous.TypeId, removed.TypeId); Assert.Equal(previous.InstanceId, removed.InstanceId);
            Assert.Equal(previous.Attributes.GetRawText(), removed.Attributes.GetRawText()); Assert.Equal(0UL, removed.Quantity!.Value.Value);
            observer.Accept((int)data.Code(backpack), data.Body(backpack), 7, at);
            Assert.Equal(InventoryFrameKind.Delta, frames[^1].Kind.KnownValue);
        }
        else
        {
            Assert.Equal(InventoryFrameKind.Invalidated, frames[^1].Kind.KnownValue); Assert.Equal("inventory.protocolInvalid", frames[^1].Reason);
            var count = frames.Count; observer.Accept((int)data.Code(backpack), data.Body(backpack), 7, at); Assert.Equal(count, frames.Count);
            observer.Accept(1, data.EmptyLogin(), 8, at); observer.Accept(2, Profile(9007199254740993), 9, at);
            Assert.Equal(InventoryFrameKind.Full, frames[^1].Kind.KnownValue); Assert.Equal(2UL, frames[^1].Generation.Value);
            Assert.All(retained, item => { Assert.Equal(42UL, item.TypeId.Value); Assert.Equal(42UL, item.InstanceId!.Value.Value); Assert.Equal(1UL, item.Quantity!.Value.Value); });
        }
    }

    public static IEnumerable<object[]> UnsupportedAttributeMethods()
    {
        const string id = "Shared.GameLogic.ChannelObjects.Characters.Items.NonStackableId";
        const string time = "Core.Times.RealTime";
        yield return [new InventoryProtocolMethod("SetPetName", "Pet", [id, "string", time])];
        yield return [new InventoryProtocolMethod("SetPetUniqueSkillInfo", "Pet", [id, "Shared.GameLogic.Pet.PetEquippedUniqueSkillInfo"])];
        yield return [new InventoryProtocolMethod("SetPetRarity", "Pet", [id, "Shared.GameLogic.ChannelObjects.Characters.Items.Rarity"])];
        yield return [new InventoryProtocolMethod("SetPetTags", "Pet", [id, "System.Collections.Generic.List`1<Shared.GameLogic.ChannelObjects.Actors.PetSkillTag>"])];
        yield return [new InventoryProtocolMethod("SetPetSkills", "Pet", [id, "System.Collections.Generic.List`1<Shared.Symbols.SkillSheetId>"])];
        yield return [new InventoryProtocolMethod("SetPetSatietyTime", "Pet", [id, time, time])];
        yield return [new InventoryProtocolMethod("SetPetLevelAndExp", "Pet", [id, "int32", "int64"])];
        yield return [new InventoryProtocolMethod("SetPetParametersAsEquipment", "Pet", [id, "System.Collections.Generic.Dictionary`2<Shared.GameLogic.ChannelObjects.Actors.ActorParameters.ParameterType,float32>"])];
        yield return [new InventoryProtocolMethod("SetRidableName", "Ridable", [id, "string", time])];
        yield return [new InventoryProtocolMethod("UpdateWeightParameters", "Bag", ["Shared.GameLogic.ItemWeight.ItemWeightInCharacterInventoryData"])];
    }

    [Theory]
    [MemberData(nameof(UnsupportedAttributeMethods))]
    public void UnsupportedRealAttributeMethodInvalidatesUntilANewFull(InventoryProtocolMethod method)
    {
        var data = new InventoryStorageTestData(method); var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), data.Layout, frames.Add, () => ++generation);
        var at = DateTimeOffset.UtcNow; observer.Accept(1, data.EmptyLogin(), 1, at); observer.Accept(2, Profile(9007199254740993), 2, at);
        var add = data.Methods.Single(m => m.Name == "Add" + method.Category);
        observer.Accept((int)data.Code(add), data.Body(add), 3, at);
        var previous = Assert.Single(frames[^1].Changes).Item;
        Assert.Equal(InventoryFrameKind.Delta, frames[^1].Kind.KnownValue); Assert.Equal(method.Category, previous.Category);
        Assert.Equal(42UL, previous.TypeId.Value); Assert.Equal(42UL, previous.InstanceId!.Value.Value); Assert.Equal(1UL, previous.Quantity!.Value.Value);
        var body = data.Body(method, 9);
        Assert.Throws<InvalidDataException>(() => new ClientInventoryDecoder(data.Schema).DecodeMethod(body, method));
        observer.Accept((int)data.Code(method), body, 4, at);
        Assert.Equal(InventoryFrameKind.Invalidated, frames[^1].Kind.KnownValue); Assert.Equal("inventory.protocolInvalid", frames[^1].Reason);
        Assert.Empty(frames[^1].Changes); Assert.Empty(frames[^1].Items);
        var count = frames.Count; observer.Accept((int)data.Code(add), data.Body(add, 9), 5, at); Assert.Equal(count, frames.Count);
        observer.Accept(1, data.EmptyLogin(), 6, at); observer.Accept(2, Profile(9007199254740993), 7, at);
        Assert.Equal(InventoryFrameKind.Full, frames[^1].Kind.KnownValue); Assert.Equal(2UL, frames[^1].Generation.Value);
        observer.Accept((int)data.Code(add), data.Body(add, 9), 8, at);
        Assert.Equal(InventoryFrameKind.Delta, frames[^1].Kind.KnownValue);
        var recovered = Assert.Single(frames[^1].Changes).Item;
        Assert.Equal(previous.Container, recovered.Container); Assert.Equal(previous.TypeId, recovered.TypeId); Assert.Equal(previous.InstanceId, recovered.InstanceId);
        Assert.Equal(previous.Quantity, recovered.Quantity); Assert.NotEqual(previous.Attributes.GetRawText(), recovered.Attributes.GetRawText());
    }

    [Fact]
    public void RequiresJoinedIdentityThenTracksInstanceAndInvalidatesOnUnknownRemoval()
    {
        var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("test", "1", "TW", "offline"), Layout(), frames.Add, () => ++generation);
        var utc = DateTimeOffset.UtcNow;
        observer.Accept(1, Login(), 10, utc);
        Assert.Equal("inventory.awaitingIdentity", Assert.Single(frames).Reason);
        observer.Accept(2, Profile(99), 11, utc);
        Assert.Single(frames);
        observer.Accept(2, Profile(9007199254740993), 12, utc);
        var full = frames[^1]; Assert.Equal(InventoryFrameKind.Full, full.Kind.KnownValue);
        Assert.Equal("7", full.Identity!.AccountId); Assert.Equal("南千和", full.Identity.RawName);
        Assert.Single(full.Items);
        observer.Accept(3, Equipment(42), 13, utc);
        Assert.Equal(InventoryFrameKind.Delta, frames[^1].Kind.KnownValue);
        Assert.Equal(42UL, Assert.Single(frames[^1].Changes).Item.TypeId.Value);
        observer.Accept(4, BitConverter.GetBytes(123L), 14, utc);
        Assert.Equal(InventoryFrameKind.Invalidated, frames[^1].Kind.KnownValue);
        var count = frames.Count; observer.Accept(3, Equipment(42), 15, utc); Assert.Equal(count, frames.Count);
        observer.Accept(1, Login(), 16, utc); observer.Accept(2, Profile(9007199254740993), 17, utc);
        Assert.Equal(2UL, frames[^1].Generation.Value); Assert.Equal(InventoryFrameKind.Full, frames[^1].Kind.KnownValue);
    }
    [Fact]
    public void ProfileCopiesTimeoutAndPreIdentityChangesWaitForTheFull()
    {
        var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("test", "1", "TW", "offline"), Layout(), frames.Add, () => ++generation);
        var utc = DateTimeOffset.UtcNow;
        observer.Accept(1, Login(), 1, utc); observer.Tick(1 + InventoryProtocolObserver.IdentityWindowMs + 1, utc);
        Assert.Equal("inventory.identityTimeout", frames[^1].Reason);
        // 身份关联到达前的增量暂存，完整同步发出后叠加。
        observer.Accept(1, Login(), 40003, utc); observer.Accept(3, Equipment(42), 40004, utc);
        Assert.Equal("inventory.awaitingIdentity", frames[^1].Reason);
        // 自相矛盾的身份关联（角色 ID 两处不一致）只跳过，等待中的登录数据保留。
        var conflicting = Profile(9007199254740993); BitConverter.GetBytes(9L).CopyTo(conflicting, 16);
        observer.Accept(2, conflicting, 40005, utc);
        Assert.Equal("inventory.awaitingIdentity", frames[^1].Reason);
        observer.Accept(2, Profile(9007199254740993), 40006, utc);
        Assert.Equal(InventoryFrameKind.Full, frames[^2].Kind.KnownValue);
        Assert.Equal(InventoryFrameKind.Delta, frames[^1].Kind.KnownValue);
    }
    private static ClientCaptureLayout Layout() => new("assembly", "metadata", new(1, 2, 3), InventoryDecoderTests.SimpleSchema())
    {
        Opcodes = new Dictionary<string, uint> { ["login"] = 1, ["profile"] = 2, ["update"] = 3, ["remove"] = 4 },
        Methods = new Dictionary<uint, InventoryProtocolMethod>
        {
            [2] = new("UpdateGuildMemberInfo", "GuildMembers", ["AccountId", "Profile"]),
            [3] = new("UpdateEquipment", "Equipment", ["NonStackableId", "Equipment"]),
            [4] = new("RemoveEquipment", "Equipment", ["NonStackableId"])
        }
    };
    private static byte[] Login()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(9007199254740993L); writer.Write(1); writer.Write(Equipment(42));
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        return stream.ToArray();
    }
    private static byte[] Equipment(uint sheet)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(4000000000000001L); writer.Write(4000000000000001L); writer.Write(sheet); writer.Write(false);
        return stream.ToArray();
    }
    internal static byte[] Profile(long character)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(7L); writer.Write(character); writer.Write(character);
        var name = Encoding.Unicode.GetBytes("南千和"); writer.Write(name.Length); writer.Write(name); return stream.ToArray();
    }
}
