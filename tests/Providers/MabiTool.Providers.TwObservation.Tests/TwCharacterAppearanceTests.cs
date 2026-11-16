using System.Text;
using System.Text.Json;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class TwCharacterAppearanceTests
{
    [Fact]
    public void CompleteIdsMatchPersonalIdentityWithoutTruncatingUpperBits()
    {
        const ulong entity = 0xFEDCBA9876543210, character = 0x12345678AABBCCDD;
        var result = TwCharacterAppearance.Decode(Prefix(entity, character));
        Assert.Equal(entity, result.EntityId);
        Assert.Equal(character, result.CharacterId);
        Assert.Equal(character, TwCharacterAppearance.DecodePersonalId(BitConverter.GetBytes(character)));
        Assert.NotEqual(result.CharacterId, TwCharacterAppearance.Decode(Prefix(entity, 0x87654321AABBCCDD)).CharacterId);
    }

    [Fact]
    public void PersonalMarkerInTrailingDataCannotReplaceCharacterIdentity()
    {
        const ulong personal = 0x12345678AABBCCDD;
        var body = Prefix(character: 0x87654321AABBCCDD).Concat(BitConverter.GetBytes(personal)).ToArray();
        Assert.NotEqual(personal, TwCharacterAppearance.Decode(body).CharacterId);
    }

    [Fact]
    public void PrefixEndsBeforeNameAndUsesVariableParentLengths()
    {
        var shortBody = Prefix();
        var longBody = Prefix(text: "合成父级字段");
        Assert.True(longBody.Length > shortBody.Length);
        Assert.Equal(TwCharacterAppearance.Decode(shortBody), TwCharacterAppearance.Decode(longBody));
        Assert.Equal(TwCharacterAppearance.Decode(shortBody), TwCharacterAppearance.Decode(shortBody.Concat(new byte[] { 0xff }).ToArray()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    public void TruncatedPrefixIsRejected(int remove)
    {
        var body = Prefix();
        Assert.Throws<InvalidDataException>(() => TwCharacterAppearance.Decode(body.AsSpan(0, body.Length - remove)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void InvalidCollectionCountsAreRejected(int count) =>
        Assert.Throws<InvalidDataException>(() => TwCharacterAppearance.Decode(Prefix(countOverride: count)));

    [Fact]
    public void InvalidBooleanAndUtf16LengthAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => TwCharacterAppearance.Decode(Prefix(boolOverride: 2)));
        Assert.Throws<InvalidDataException>(() => TwCharacterAppearance.Decode(Prefix(stringBytesOverride: 1)));
    }

    [Fact]
    public void MissingIdentityAndInvalidPersonalExtentAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => TwCharacterAppearance.Decode(Prefix(entity: 0)));
        Assert.Throws<InvalidDataException>(() => TwCharacterAppearance.Decode(Prefix(character: 0)));
        Assert.Throws<InvalidDataException>(() => TwCharacterAppearance.DecodePersonalId(new byte[7]));
        Assert.Throws<InvalidDataException>(() => TwCharacterAppearance.DecodePersonalId(new byte[8]));
    }

    internal static byte[] Prefix(ulong entity = 0x100000001, ulong character = 0x200000002,
        string text = "", int? countOverride = null, byte boolOverride = 0, int? stringBytesOverride = null)
    {
        using var schema = JsonDocument.Parse(typeof(TwCharacterAppearance).Assembly.GetManifestResourceStream("MabiTool.Tw20261110CharacterSchema")!);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode, true);
        void Write(string name, string field = "")
        {
            if (name is "object" or "System.Object" or "System.ValueType" or "System.Enum") return;
            if (name == "Shared.ChannelObjects.ChannelObjectId") { writer.Write(field == "Id" ? entity : 0UL); return; }
            if (name == "Shared.Characters.CharacterId") { writer.Write(character); return; }
            if (name == "Shared.Protocols.ProtocolDatas.ReplicatedDatas.ReplicatedTime") { writer.Write(0f); return; }
            if (name == "Silvervine.ManualMemory.UniqueString") { Write("string"); return; }
            if (name is "Shared.Symbols.ActorModelId" or "Shared.Symbols.ModelPartId" or "Shared.Symbols.MountPartId") { writer.Write(0); return; }
            switch (name)
            {
                case "bool": writer.Write(boolOverride); return;
                case "uint8": case "int8": writer.Write((byte)0); return;
                case "int16": case "uint16": case "char": writer.Write((short)0); return;
                case "int32": case "uint32": writer.Write(0); return;
                case "int64": case "uint64": writer.Write(0L); return;
                case "float32": writer.Write(0f); return;
                case "float64": writer.Write(0d); return;
                case "string": var bytes = Encoding.Unicode.GetBytes(text); writer.Write(stringBytesOverride ?? bytes.Length); writer.Write(bytes); return;
            }
            if (name.EndsWith("[]", StringComparison.Ordinal)) { writer.Write(countOverride ?? 0); return; }
            if (name.Contains('<', StringComparison.Ordinal))
            { if (name.Contains("Nullable`1", StringComparison.Ordinal)) writer.Write((byte)1); else writer.Write(countOverride ?? 0); return; }
            var record = schema.RootElement.GetProperty(name);
            if (record.GetProperty("parent").GetString() == "System.Enum") { Write(record.GetProperty("fields")[0].GetProperty("type").GetString()!); return; }
            Write(record.GetProperty("parent").GetString()!);
            foreach (var item in record.GetProperty("fields").EnumerateArray()) Write(item.GetProperty("type").GetString()!, item.GetProperty("name").GetString()!);
        }
        Write("Shared.Protocols.Replications.GroupReplicatedDatas.CharacterPublicReplicatedData");
        return stream.ToArray();
    }
}
