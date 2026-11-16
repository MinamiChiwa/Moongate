using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace MabiTool.Providers.TwObservation.Combat;

public sealed record TwCharacterIdentity(ulong EntityId, ulong CharacterId);

/// <summary>按当前台服字段前缀关联完整角色 ID；停止在 CharacterId 后，不读取名称及显示数据。</summary>
public static class TwCharacterAppearance
{
    private const string RootType = "Shared.Protocols.Replications.GroupReplicatedDatas.CharacterPublicReplicatedData";
    private static readonly JsonDocument Schema = JsonDocument.Parse(typeof(TwCharacterAppearance).Assembly
        .GetManifestResourceStream("MabiTool.Tw20261110CharacterSchema")!);

    public static ulong DecodePersonalId(ReadOnlySpan<byte> body)
    {
        if (body.Length is < 8 or > TwGameProtocol.MaximumBody)
            throw new InvalidDataException("Invalid personal identity extent");
        var id = BinaryPrimitives.ReadUInt64LittleEndian(body);
        return id != 0 ? id : throw new InvalidDataException("Missing personal identity");
    }

    public static TwCharacterIdentity Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length > TwGameProtocol.MaximumBody) throw new InvalidDataException("Character prefix exceeds budget");
        try
        {
            var reader = new TwGameProtocol.WireReader(body.ToArray(), Schema.RootElement);
            var value = (Dictionary<string, object?>)reader.Value(RootType)!;
            var entity = Convert.ToUInt64(value["Id"], CultureInfo.InvariantCulture);
            var identity = (Dictionary<string, object?>)value["CharacterIdentity"]!;
            var character = (Dictionary<string, object?>)identity["CharacterId"]!;
            var characterId = unchecked((ulong)Convert.ToInt64(character["int64Value"], CultureInfo.InvariantCulture));
            if (entity == 0 || characterId == 0) throw new InvalidDataException("Missing character identity");
            return new(entity, characterId);
        }
        catch (Exception error) when (error is OverflowException or InvalidCastException or KeyNotFoundException or System.Text.DecoderFallbackException)
        { throw new InvalidDataException("Invalid character prefix", error); }
    }
}
