using System.Globalization;
using System.Text.Json;

namespace MabiTool.Providers.TwObservation;

/// <summary>角色出现帧中的演奏快照；未演奏时 ConcertId 为 0。</summary>
public sealed record InstrumentSnapshot(ulong ConcertId, IReadOnlyList<int> Channels, bool IsLoop);
public sealed record CharacterAppearance(GameIdentity Identity, InstrumentSnapshot? Instrument);

/// <summary>
/// 身份与音乐协议号。参考布局为固定 MabiSync 构建；当前布局按 2026-11-10 台服构建登记，
/// 负载字段与参考布局一致（2026-11-14 独奏/合奏实采核对：演奏帧以 8 字节实体开头，停止帧只含实体，合奏增删帧无实体前缀）。
/// </summary>
public sealed record TwMusicProtocolMap(int Entering, int Appearing, int Personal, int ConcertJoin, int ConcertAdd,
    int ConcertRemove, int Play, int Stop, int Disappearing)
{
    public static TwMusicProtocolMap Reference { get; } = new(20307, 20047, 20520, 20533, 120412, 120413, 120692, 120693, 20238);
    public static TwMusicProtocolMap Tw20261110 { get; } = new(20344, 20054, 20590, 20608, 170415, 170416, 170703, 170704, 20272);
    /// <summary>身份帧：不依赖任何模块兴趣，始终解码。</summary>
    public IReadOnlySet<int> IdentityCodes { get; } = new HashSet<int> { Entering, Appearing, Personal, Disappearing };
    /// <summary>音乐帧：只在音乐兴趣开启时解码。</summary>
    public IReadOnlySet<int> MusicCodes { get; } = new HashSet<int> { ConcertJoin, ConcertAdd, ConcertRemove, Play, Stop };
}

/// <summary>当前构建进图快照：按已核实的字段顺序读取合奏前后的变长数据。</summary>
internal static class TwChannelMusicSnapshot
{
    private const string RootType = "Shared.Protocols.Replications.GroupReplicatedDatas.ChannelGlobalReplicatedData";
    private static readonly JsonDocument Schema = JsonDocument.Parse(typeof(TwChannelMusicSnapshot).Assembly
        .GetManifestResourceStream("MabiTool.Tw20261110ChannelMusicSchema")!);

    /// <summary>
    /// 读取首字段 ChannelIdentity（ChannelId：Int32 表 ID、Int64 实例编号、Int16 游戏服务器 ID），不依赖音乐兴趣。
    /// 负载不足或表 ID 为零时返回 null，不拒绝整帧。
    /// </summary>
    public static GameChannel? ReadChannel(ReadOnlySpan<byte> body)
    {
        if (body.Length > TwGameProtocol.MaximumBody) return null;
        try
        {
            var reader = new TwGameProtocol.WireReader(body.ToArray(), Schema.RootElement);
            var root = Schema.RootElement.GetProperty(RootType);
            reader.Value(root.GetProperty("parent").GetString()!);
            var first = root.GetProperty("fields")[0];
            if (first.GetProperty("name").GetString() != "ChannelIdentity") return null;
            var identity = (Dictionary<string, object?>)reader.Value(first.GetProperty("type").GetString()!)!;
            var channel = (Dictionary<string, object?>)identity["ChannelId"]!;
            var sheet = (int)((Dictionary<string, object?>)channel["sheetId"]!)["Id"]!;
            var server = (short)((Dictionary<string, object?>)channel["serviceGameServerId"]!)["int16Value"]!;
            return sheet == 0 ? null : new(sheet, (long)channel["uniqueNumber"]!, server);
        }
        catch (InvalidDataException) { return null; }
    }

    public static void Read(ReadOnlySpan<byte> body, Action<TwGameProtocol.WireReader> acceptConcert)
    {
        if (body.Length > TwGameProtocol.MaximumBody) throw new InvalidDataException("Channel snapshot exceeds budget");
        try
        {
            var reader = new TwGameProtocol.WireReader(body.ToArray(), Schema.RootElement);
            var root = Schema.RootElement.GetProperty(RootType);
            reader.Value(root.GetProperty("parent").GetString()!);
            foreach (var field in root.GetProperty("fields").EnumerateArray())
            {
                if (field.GetProperty("name").GetString() != "Concerts")
                {
                    reader.Value(field.GetProperty("type").GetString()!);
                    continue;
                }
                // 字典键与值依次为合奏 ID 和新增帧字段；压缩谱按字节长度读取，沿用 AcceptConcert 的校验与缓存。
                var count = reader.Count();
                for (var i = 0; i < count; i++) acceptConcert(reader);
            }
            reader.Finish();
        }
        catch (System.Text.DecoderFallbackException error) { throw new InvalidDataException("Invalid channel snapshot", error); }
    }
}

/// <summary>当前构建的角色出现帧：按字段前缀读到演奏快照为止，不读取其后的显示数据。</summary>
public static class TwCharacterMusicAppearance
{
    private const string RootType = "Shared.Protocols.Replications.GroupReplicatedDatas.CharacterPublicReplicatedData";
    private static readonly JsonDocument Schema = Merge("MabiTool.Tw20261110CharacterSchema", "MabiTool.Tw20261110CharacterMusicSchema");

    private static JsonDocument Merge(string prefix, string overlay)
    {
        using var first = JsonDocument.Parse(typeof(TwCharacterMusicAppearance).Assembly.GetManifestResourceStream(prefix)!);
        using var second = JsonDocument.Parse(typeof(TwCharacterMusicAppearance).Assembly.GetManifestResourceStream(overlay)!);
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in first.RootElement.EnumerateObject()) merged[item.Name] = item.Value.Clone();
        foreach (var item in second.RootElement.EnumerateObject()) merged[item.Name] = item.Value.Clone();
        return JsonSerializer.SerializeToDocument(merged);
    }

    public static CharacterAppearance Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length > TwGameProtocol.MaximumBody) throw new InvalidDataException("Character appearance exceeds budget");
        try
        {
            var reader = new TwGameProtocol.WireReader(body.ToArray(), Schema.RootElement);
            var value = (Dictionary<string, object?>)reader.Value(RootType)!;
            var entity = Convert.ToUInt64(value["Id"], CultureInfo.InvariantCulture);
            var identity = (Dictionary<string, object?>)value["CharacterIdentity"]!;
            var character = unchecked((ulong)Convert.ToInt64(((Dictionary<string, object?>)identity["CharacterId"]!)["int64Value"], CultureInfo.InvariantCulture));
            var realm = Convert.ToInt32(((Dictionary<string, object?>)identity["RealmId"]!)["rawValue"], CultureInfo.InvariantCulture);
            var name = (string)identity["Name"]!;
            var player = (Dictionary<string, object?>)value["InstrumentPlayer"]!;
            var concert = Convert.ToUInt64(((Dictionary<string, object?>)player["ConcertId"]!)["uint64Value"], CultureInfo.InvariantCulture);
            var channels = ((object?[])player["ConcertChannelIndices"]!).Select(item => Convert.ToInt32(item, CultureInfo.InvariantCulture)).ToArray();
            if (channels.Length > 128 || channels.Any(channel => channel is < 0 or >= 128)) throw new InvalidDataException("Invalid instrument channel");
            var instrument = concert == 0 ? null : new InstrumentSnapshot(concert, channels, (bool)player["IsLoop"]!);
            return new(new(character, realm, name, entity), instrument);
        }
        catch (Exception error) when (error is OverflowException or InvalidCastException or KeyNotFoundException or System.Text.DecoderFallbackException)
        { throw new InvalidDataException("Invalid character appearance", error); }
    }
}
