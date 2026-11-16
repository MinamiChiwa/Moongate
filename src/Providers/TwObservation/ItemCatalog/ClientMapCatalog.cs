using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>一条 Channel 行解析出的地图：GameSpace 主键与 DisplayName 原文、ChannelRule 是否永久频道。</summary>
public sealed record ClientMapEntry(int ChannelSheetId, int GameSpaceSheetId, string DisplayName, bool PermanentChannel);

/// <summary>
/// 客户端地图名称表：Channel 行以零基行号引用 GameSpace 与 ChannelRule，地图名取 GameSpace.DisplayName 原文。
/// 只读取 sheetmeta、sheettext、sheetdata 三项资源（当前客户端解码后约 28 MiB，读完即释放），
/// 常驻结果为每个 Channel 一条（上限 <see cref="MaximumChannels"/>）。行布局见 contracts/fixtures/tw-map-channel-20261114.json。
/// </summary>
public static class ClientMapCatalog
{
    private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
    private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
    private const string DataPath = "Sheetdata_Client/client.sheetdata.bin";
    public const int MaximumChannels = 4096, MaximumGameSpaces = 8192, MaximumRules = 1024, MaximumNameLength = 128;

    public static IReadOnlyDictionary<int, ClientMapEntry> Read(string appdataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appdataDirectory);
        var package = ClientResourcePackage.Open(Path.Combine(Path.GetFullPath(appdataDirectory), "ResourcesPackage", "Starter"), cancellationToken);
        var wanted = new[] { MetaPath, TextPath, DataPath };
        var records = package.Entries.Where(e => wanted.Contains(e.Path, StringComparer.Ordinal)).ToArray();
        if (records.Length != wanted.Length || records.Select(e => e.Path).Distinct(StringComparer.Ordinal).Count() != wanted.Length)
            throw new InvalidDataException("客户端缺少地图名称资源");
        var stamps = records.Select(e => e.BlobPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, StableFileStamp.Capture, StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in records) resources.Add(entry.Path, package.Read(entry, stamps[entry.BlobPath], cancellationToken).Decoded);
        var result = Decode(resources[MetaPath], resources[TextPath], resources[DataPath], cancellationToken);
        package.EnsureUnchanged();
        foreach (var pair in stamps) pair.Value.EnsureUnchanged(pair.Key);
        return result;
    }

    public static IReadOnlyDictionary<int, ClientMapEntry> Decode(byte[] meta, byte[] text, byte[] data, CancellationToken cancellationToken = default)
    {
        var tables = ClientNameTable.ReadTables(meta, text, cancellationToken);
        var channels = Table(tables, "Channel", MaximumChannels, data);
        var spaces = Table(tables, "GameSpace", MaximumGameSpaces, data);
        var rules = Table(tables, "ChannelRule", MaximumRules, data);
        var result = new Dictionary<int, ClientMapEntry>(channels.Ids.Length);
        for (var row = 0; row < channels.Ids.Length; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Channel 行：名称、类型、Rule 行、GameSpace 行、回退出生区域（VarUInt32），ActivateMap（bool），DisableMapMonologue（计数 + UInt32）。
            var reader = channels.Row(row, data);
            _ = reader.Var(); _ = reader.Var();
            var rule = reader.Var(); var space = reader.Var(); _ = reader.Var();
            Bool(reader);
            var monologues = reader.Var();
            if (monologues > 4096) throw new InvalidDataException("Channel 行引用数量超过预算");
            for (var i = 0u; i < monologues; i++) _ = reader.UInt32();
            reader.EnsureEnd();
            if (rule >= rules.Ids.Length || space >= spaces.Ids.Length) throw new InvalidDataException("Channel 行引用越界");
            // GameSpace 行首个 VarUInt32 为 DisplayName 文本偏移；ChannelRule 行首字段为 IsPermanentChannel。
            var name = ClientNameTable.ReadString(text, checked((int)spaces.Row((int)space, data).Var()));
            if (name.Length is 0 or > MaximumNameLength || name.Any(char.IsControl)) throw new InvalidDataException("GameSpace 名称无效");
            var permanent = Bool(rules.Row((int)rule, data));
            result.Add(channels.Ids[row], new(channels.Ids[row], spaces.Ids[space], name, permanent));
        }
        return result;
    }

    /// <summary>规范化映射哈希：按有符号 ChannelSheetId 升序的 [ChannelSheetId, GameSpaceSheetId, DisplayName] 紧凑 UTF-8 JSON。</summary>
    public static string CanonicalSha256(IReadOnlyDictionary<int, ClientMapEntry> entries)
    {
        var rows = entries.Values.OrderBy(e => e.ChannelSheetId).Select(e => new object[] { e.ChannelSheetId, e.GameSpaceSheetId, e.DisplayName });
        var json = JsonSerializer.SerializeToUtf8Bytes(rows, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    private static bool Bool(ClientDataReader reader) => reader.Take(1)[0] switch
    { 0 => false, 1 => true, _ => throw new InvalidDataException("客户端表布尔值无效") };

    private sealed record IdTable(string Name, int[] Ids, IReadOnlyList<ulong> Offsets, ulong End)
    {
        public ClientDataReader Row(int row, byte[] data)
        {
            var start = Offsets[row]; var end = row + 1 < Ids.Length ? Offsets[row + 1] : End;
            if (end > (ulong)data.Length || end < start) throw new InvalidDataException(Name + " 行范围越界");
            return new(data, checked((int)start), checked((int)end));
        }
    }

    private static IdTable Table(IReadOnlyDictionary<string, ClientTable> tables, string name, int maximum, byte[] data)
    {
        if (!tables.TryGetValue(name, out var table)) throw new InvalidDataException("客户端缺少 " + name + " 表");
        if (table.Rows == 0 || table.Rows > maximum || table.Block.Length != checked((int)table.Rows * 4) || table.DataEnd > (ulong)data.Length)
            throw new InvalidDataException(name + " 表结构不支持");
        var reader = new ClientDataReader(table.Block);
        var ids = new int[table.Rows];
        var seen = new HashSet<int>();
        for (var row = 0; row < ids.Length; row++)
        {
            ids[row] = unchecked((int)reader.UInt32());
            if (ids[row] == 0 || !seen.Add(ids[row])) throw new InvalidDataException(name + " 主键重复或为零");
        }
        return new(name, ids, table.DataOffsets, table.DataEnd);
    }
}
