using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class TwAutomatonAppearanceTests
{
    [Fact]
    public void AVerifiedNonMonsterAppearanceWithdrawsAnEarlierMonsterRole()
    {
        List<CombatFrame> frames = [];
        var observer = new TwCombatObserver("fixture", Tw20261110CombatProtocol.CombatProfile.Source, frames.Add);
        var monster = Prefix(type: 3); var pet = Prefix(type: 4);
        observer.Accept(Tw20261110CombatProtocol.AppearingAutomaton, monster, 0, monster.Length + 9, 0, CombatProtocolTests.At(0), 1);
        Assert.Equal(CombatEntityRole.Monster, Assert.Single(frames[^1].Entities).Role.KnownValue);
        observer.Accept(Tw20261110CombatProtocol.AppearingAutomaton, pet, 2000, pet.Length + 9, 1, CombatProtocolTests.At(1), 1);
        Assert.Equal(CombatEntityRole.Unknown, Assert.Single(frames[^1].Entities).Role.KnownValue);
        Assert.Empty(frames.SelectMany(frame => frame.Gaps));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void SequentialPrefixPreservesFullEntityAndClassifiesByType(int type, bool monster)
    {
        var bytes = Prefix(type: type, entity: 0x12345678FEDCBA98);
        var result = TwAutomatonAppearance.Decode(bytes);
        Assert.Equal(0x12345678FEDCBA98UL, result.EntityId);
        Assert.Equal(monster, result.IsMonster);
        Assert.Equal(monster ? "44332211" : null, result.MonsterTypeCode);
    }

    [Fact]
    public void PetWithFalseMonsterMarkerInTrailingDataRemainsUnknown()
    {
        var bytes = Prefix(type: 4).Concat(new byte[] { 3, 0, 0, 0, 0x44, 0x33, 0x22, 0x11, 0, 0, 0, 0 }).ToArray();
        var result = TwAutomatonAppearance.Decode(bytes);
        Assert.False(result.IsMonster);
        Assert.Null(result.MonsterTypeCode);
    }

    [Fact]
    public void VariablePrefixUsesFieldLengthsAndDoesNotRequireFixedTail()
    {
        var shortPrefix = Prefix(text: "");
        var longPrefix = Prefix(text: "合成前缀 variation");
        Assert.True(longPrefix.Length > shortPrefix.Length);
        Assert.Equal(TwAutomatonAppearance.Decode(shortPrefix), TwAutomatonAppearance.Decode(longPrefix));
        Assert.Equal(TwAutomatonAppearance.Decode(shortPrefix), TwAutomatonAppearance.Decode(shortPrefix.Concat(new byte[19]).ToArray()));
    }

    [Fact]
    public void TruncatedIdentityAndPrefixAreRejected()
    {
        var bytes = Prefix();
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(bytes.AsSpan(0, 15)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void UnknownActorTypeIsRejected(int type) =>
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(Prefix(type: type)));

    [Fact]
    public void InvalidBoolAndNonFiniteOffsetAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(Prefix(boss: 2)));
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(Prefix(offset: float.NaN)));
    }

    [Fact]
    public void MissingEntityOrMatchingSheetIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(Prefix(entity: 0)));
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(Prefix(sheet: 0)));
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(Prefix(type: 4, sheet: 0)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void InvalidCollectionCountIsRejected(int count) =>
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(Prefix(countOverride: count)));

    [Fact]
    public void OddUtf16ByteLengthIsRejected() =>
        Assert.Throws<InvalidDataException>(() => TwAutomatonAppearance.Decode(Prefix(stringBytesOverride: 1)));

    internal static byte[] Prefix(int type = 3, ulong entity = 1, uint sheet = 0x11223344,
        string text = "", byte boss = 0, float offset = 0, int? countOverride = null, int? stringBytesOverride = null,
        float breakPoint = 0, long breakPointMax = 0)
    {
        using var schema = JsonDocument.Parse(typeof(TwAutomatonAppearance).Assembly.GetManifestResourceStream("MabiTool.Tw20261110AutomatonSchema")!);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode, true);
        void Write(string name, string field = "")
        {
            if (name is "object" or "System.Object" or "System.ValueType" or "System.Enum") return;
            if (name == "Shared.ChannelObjects.ChannelObjectId") { writer.Write(field == "Id" ? entity : 0UL); return; }
            if (name == "Shared.Protocols.ProtocolDatas.ReplicatedDatas.ReplicatedTime") { writer.Write(0f); return; }
            if (name == "Silvervine.ManualMemory.UniqueString") { Write("string"); return; }
            if (name is "Shared.Symbols.ActorModelId" or "Shared.Symbols.ModelPartId" or "Shared.Symbols.MountPartId") { writer.Write(0); return; }
            switch (name)
            {
                case "bool": writer.Write(field == "IsBossMonster" ? boss : (byte)0); return;
                case "uint8": case "int8": writer.Write((byte)0); return;
                case "int16": case "uint16": writer.Write((short)0); return;
                case "int32": case "uint32": writer.Write(0); return;
                case "int64": case "uint64": writer.Write(field == "BreakPointMax" ? breakPointMax : 0L); return;
                case "float32": writer.Write(field == "OffsetY" ? offset : field == "BreakPoint" ? breakPoint : 0f); return;
                case "float64": writer.Write(0d); return;
                case "string": var bytes = Encoding.Unicode.GetBytes(text); writer.Write(stringBytesOverride ?? bytes.Length); writer.Write(bytes); return;
            }
            if (name.EndsWith("[]", StringComparison.Ordinal)) { writer.Write(countOverride ?? 0); return; }
            if (name.Contains('<', StringComparison.Ordinal))
            { if (name.Contains("Nullable`1", StringComparison.Ordinal)) writer.Write((byte)1); else writer.Write(countOverride ?? 0); return; }
            if (name == "Shared.GameLogic.ChannelObjects.ActorType") { writer.Write(type); return; }
            if (name is "Shared.Symbols.MonsterSheetId" or "Shared.Symbols.PetSheetId")
            { writer.Write(name.Contains(type == 4 ? ".PetSheetId" : ".MonsterSheetId", StringComparison.Ordinal) ? sheet : 0); return; }
            var record = schema.RootElement.GetProperty(name);
            if (record.GetProperty("parent").GetString() == "System.Enum") { Write(record.GetProperty("fields")[0].GetProperty("type").GetString()!); return; }
            Write(record.GetProperty("parent").GetString()!);
            foreach (var item in record.GetProperty("fields").EnumerateArray()) Write(item.GetProperty("type").GetString()!, item.GetProperty("name").GetString()!);
        }
        Write("Shared.Protocols.Replications.GroupReplicatedDatas.AutomatonPublicReplicatedData");
        return stream.ToArray();
    }
}

