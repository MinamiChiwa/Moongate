using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace MabiTool.Providers.TwObservation.Combat;

public sealed record TwAutomatonIdentity(ulong EntityId, bool IsMonster, string? MonsterTypeCode,
    double? BreakPoint = null, long? BreakPointMax = null);

/// <summary>按当前台服字段顺序读取实体及 AutomatonIdentity；后续展示字段由其他能力验证。</summary>
public static class TwAutomatonAppearance
{
    private const string RootType = "Shared.Protocols.Replications.GroupReplicatedDatas.AutomatonPublicReplicatedData";
    private static readonly JsonDocument Schema = JsonDocument.Parse(typeof(TwAutomatonAppearance).Assembly
        .GetManifestResourceStream("MabiTool.Tw20261110AutomatonSchema")!);

    public static TwAutomatonIdentity Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length > TwGameProtocol.MaximumBody) throw new InvalidDataException("Automaton prefix exceeds budget");
        try
        {
            var reader = new TwGameProtocol.WireReader(body.ToArray(), Schema.RootElement);
            var value = (Dictionary<string, object?>)reader.Value(RootType)!;
            var id = Convert.ToUInt64(value["Id"], CultureInfo.InvariantCulture);
            var identity = (Dictionary<string, object?>)value["AutomatonIdentity"]!;
            var type = Convert.ToInt32(identity["Type"], CultureInfo.InvariantCulture);
            var offset = Convert.ToSingle(identity["OffsetY"], CultureInfo.InvariantCulture);
            if (id == 0 || type is < 0 or > 8 || !float.IsFinite(offset))
                throw new InvalidDataException("Invalid automaton identity");
            var sheetField = type switch
            {
                1 => "NPCSheetId", 2 => "NPCCompanionSheetId", 3 => "MonsterSheetId",
                4 => "PetSheetId", 5 => "SummonedAutomatonSheetId", 6 => "GimmickActorSheetId",
                8 => "MemorialSagaMinionSheetId", _ => null
            };
            uint sheet = 0;
            if (sheetField is not null)
            {
                sheet = unchecked((uint)Convert.ToInt32(((Dictionary<string, object?>)identity[sheetField]!)["Id"], CultureInfo.InvariantCulture));
                if (sheet is 0 or uint.MaxValue) throw new InvalidDataException("Invalid automaton sheet identity");
            }
            // 类型是怪物归属依据；宠物中的其他字节不能作为怪物哨兵重新解释。
            Span<byte> code = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(code, sheet);
            var parameter = (Dictionary<string, object?>)value["ActorParameter"]!;
            var status = (Dictionary<string, object?>)value["ActorStatus"]!;
            var current = Convert.ToSingle(status["BreakPoint"], CultureInfo.InvariantCulture);
            var maximum = Convert.ToInt64(parameter["BreakPointMax"], CultureInfo.InvariantCulture);
            return new(id, type == 3, type == 3 ? Convert.ToHexString(code) : null,
                float.IsFinite(current) && current >= 0 ? current : null, maximum >= 0 ? maximum : null);
        }
        catch (Exception error) when (error is OverflowException or InvalidCastException or KeyNotFoundException or System.Text.DecoderFallbackException)
        { throw new InvalidDataException("Invalid automaton prefix", error); }
    }
}

