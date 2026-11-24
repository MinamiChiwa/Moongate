using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace MabiTool.Providers.TwObservation.Combat;

/// <summary>自动体出现帧读出的身份、破防与 HP 字段。</summary>
/// <remarks>
/// ActorType 为客户端 ActorType：1 NPC、2 NPCCompanion、3 Monster、4 Pet、5 SummonedAutomaton、6 GimmickActor、7 AICompanion、8 MemorialSagaMinion；
/// SheetId 为按类型取的客户端表 ID（AICompanion 没有表，为 0）；HealthMax 为 ActorParameter.HealthMax（非正值为空）；
/// HealthKeyId 为 ActorStatus.ChannelObjectId（HP 解密密钥的来源）；HealthEncrypted 为 IsSetHealthEncrypted；
/// HealthPayload 为 HP 初值（明文时 8 字节 Int64，加密时为密文）；HealthIv 为 16 字节 IV，长度不符时为空。
/// OwnerPlayerId 为身份块首字段 AutomatonIdentity.OwnerPlayerId（ChannelObjectId，所有者玩家的实体 ID），没有所有者时为 0。
/// </remarks>
public sealed record TwAutomatonIdentity(ulong EntityId, bool IsMonster, string? MonsterTypeCode,
    double? BreakPoint = null, long? BreakPointMax = null)
{
    public int ActorType { get; init; }
    public ulong OwnerPlayerId { get; init; }
    public uint SheetId { get; init; }
    public bool IsBoss { get; init; }
    public long? HealthMax { get; init; }
    public ulong HealthKeyId { get; init; }
    public bool HealthEncrypted { get; init; }
    public byte[] HealthPayload { get; init; } = [];
    public byte[]? HealthIv { get; init; }
}

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
            var health = Convert.ToInt64(parameter["HealthMax"], CultureInfo.InvariantCulture);
            var iv = Bytes(status["IV"]);
            return new(id, type == 3, type == 3 ? Convert.ToHexString(code) : null,
                float.IsFinite(current) && current >= 0 ? current : null, maximum >= 0 ? maximum : null)
            {
                ActorType = type, SheetId = sheet,
                OwnerPlayerId = Convert.ToUInt64(identity["OwnerPlayerId"], CultureInfo.InvariantCulture), IsBoss = Convert.ToBoolean(identity["IsBossMonster"], CultureInfo.InvariantCulture),
                HealthMax = health > 0 ? health : null, HealthKeyId = Convert.ToUInt64(status["ChannelObjectId"], CultureInfo.InvariantCulture),
                HealthEncrypted = Convert.ToBoolean(status["IsSetHealthEncrypted"], CultureInfo.InvariantCulture),
                HealthPayload = Bytes(status["Payload"]), HealthIv = iv.Length == 16 ? iv : null
            };
        }
        catch (Exception error) when (error is OverflowException or InvalidCastException or KeyNotFoundException or System.Text.DecoderFallbackException)
        { throw new InvalidDataException("Invalid automaton prefix", error); }
    }
    /// <summary>schema 中 uint8[] 字段读出的元素数组；超过 64 字节按结构错误处理。</summary>
    private static byte[] Bytes(object? value)
    {
        var items = (object?[])value!;
        if (items.Length > 64) throw new InvalidDataException("Automaton status bytes exceed budget");
        return items.Select(item => Convert.ToByte(item, CultureInfo.InvariantCulture)).ToArray();
    }
}

