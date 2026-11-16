// 来源：MinamiChiwa/MabiAgent 本机工作树；MIT，Copyright (c) 2026 MinamiChiwa（南千和）。
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

public static partial class ClientItemCatalogExtractor
{
    private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
    private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
    private const string DataPath = "Sheetdata_Client/client.sheetdata.bin";
    private const string StringsPath = "StringTables/compiled.strings.bin";
    private static readonly string[] Tables = ["Consumable", "Equipment", "EquipmentTool", "Bag", "AccompanyEquipment", "Chair", "Costume", "Pet", "Ridable", "Socketable", "Artifact", "HousingProp", "JamItem", "MemorialSagaItem", "MemorialSagaMinion", "AquariumFish"];
    public const int MaximumEntries = 100000;

    public static InventoryCatalogData Extract(string appdataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appdataDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var appdata = Path.GetFullPath(appdataDirectory);
        var package = ClientResourcePackage.Open(Path.Combine(appdata, "ResourcesPackage", "Starter"), cancellationToken);
        var wanted = new[] { MetaPath, TextPath, DataPath, StringsPath };
        var records = package.Entries.Where(e => wanted.Contains(e.Path, StringComparer.Ordinal)).ToArray();
        if (records.Length != wanted.Length || records.Select(e => e.Path).Distinct(StringComparer.Ordinal).Count() != wanted.Length)
            throw new InvalidDataException("客户端缺少全类别名称资源");
        var stamps = records.Select(e => e.BlobPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, StableFileStamp.Capture, StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sources = new List<InventoryCatalogResource>();
        foreach (var entry in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = package.Read(entry, stamps[entry.BlobPath], cancellationToken);
            resources.Add(entry.Path, decoded.Decoded);
            sources.Add(new(entry.Path, entry.PackageKey.ToString(CultureInfo.InvariantCulture), new(decoded.Offset),
                new((ulong)decoded.PackedBytes), Hash(decoded.Packed), Hash(decoded.Decoded)));
        }
        var metadataPath = Path.Combine(appdata, "MabinogiMobile_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var metadataStamp = StableFileStamp.Capture(metadataPath);
        if (metadataStamp.Length > 256L * 1024 * 1024) throw new InvalidDataException("客户端 metadata 超过读取预算");
        var metadata = File.ReadAllBytes(metadataPath);
        metadataStamp.EnsureUnchanged(metadataPath);
        var sheetFields = ReadSheetFields(metadata, cancellationToken);
        var entries = Decode(resources[MetaPath], resources[TextPath], resources[DataPath], resources[StringsPath], sheetFields,
            cancellationToken, out var excluded, out var categories);
        package.EnsureUnchanged();
        foreach (var pair in stamps) pair.Value.EnsureUnchanged(pair.Key);
        metadataStamp.EnsureUnchanged(metadataPath);
        sources.Add(new("Metadata/global-metadata.dat", "metadata", new(0), new((ulong)metadata.Length), Hash(metadata), Hash(metadata)));
        cancellationToken.ThrowIfCancellationRequested();
        var indexHash = Hash(package.IndexBytes);
        return new("tw-inventory-" + indexHash, "TW", indexHash, DateTimeOffset.UtcNow, sources, entries, excluded, categories);
    }

    public static IReadOnlyList<InventoryCatalogEntry> Decode(byte[] meta, byte[] text, byte[] data, byte[] strings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> sheetFields, CancellationToken cancellationToken,
        out IReadOnlyList<InventoryCatalogExcluded> excluded, out IReadOnlyList<InventoryCatalogCategory> categories)
    {
        var tables = ClientNameTable.ReadTables(meta, text, cancellationToken);
        var localized = ClientNameTable.ReadStringTables(strings, cancellationToken);
        var instrumentKeys = new HashSet<uint>();
        if (tables.TryGetValue("Instrument", out var instruments))
        {
            if (instruments.Rows > MaximumEntries || instruments.Block.Length != checked((int)instruments.Rows * 4)) throw new InvalidDataException("Instrument 索引结构不支持");
            var idsReader = new ClientDataReader(instruments.Block);
            for (uint i = 0; i < instruments.Rows; i++) { cancellationToken.ThrowIfCancellationRequested(); instrumentKeys.Add(idsReader.UInt32()); }
        }
        var result = new List<InventoryCatalogEntry>();
        var failures = new List<InventoryCatalogExcluded>();
        var support = new List<InventoryCatalogCategory>();
        foreach (var name in Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tables.TryGetValue(name, out var table)) { support.Add(new(name, name, false, 0, "客户端缺少对应名称表")); continue; }
            if (!sheetFields.TryGetValue(name, out var fields) || !HasDisplayName(fields))
            { support.Add(new(name, name, false, 0, "客户端字段结构未确认 DisplayName 列")); continue; }
            if (table.Rows > MaximumEntries || result.Count + failures.Count + table.Rows > MaximumEntries)
                throw new InvalidDataException("名称目录超过 100000 条预算");
            var reader = new ClientDataReader(table.Block);
            var ids = new HashSet<uint>();
            var before = result.Count;
            for (uint row = 0; row < table.Rows; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metaAt = checked(table.MetaOffset + (ulong)reader.Offset);
                var id = reader.UInt32();
                if (id == 0 || !ids.Add(id)) throw new InvalidDataException(name + " 名称表类型 ID 重复或为零");
                uint nameOffset;
                bool? isInstrument = null; UInt64String? instrumentType = null; UInt64String? toolType = null;
                if (name == "Consumable") { nameOffset = reader.Var(); _ = reader.Var(); }
                else
                {
                    if (name == "Socketable")
                    {
                        if (!fields.Contains("RuneSheetId") || !fields.Contains("JewelSheetId")) throw new InvalidDataException("Socketable 索引结构不支持");
                        _ = reader.UInt32(); _ = reader.UInt32();
                    }
                    if (name == "HousingProp") _ = reader.Var();
                    var offset = table.DataOffsets[checked((int)row)];
                    if (offset >= (ulong)data.Length) throw new InvalidDataException(name + " 名称正文偏移越界");
                    var end = row + 1 < table.Rows ? table.DataOffsets[checked((int)row + 1)] : table.DataEnd;
                    if (end > (ulong)data.Length || end < offset) throw new InvalidDataException(name + " 名称正文范围越界");
                    var value = new ClientDataReader(data, checked((int)offset), checked((int)end));
                    nameOffset = value.Var();
                    if (name == "EquipmentTool")
                    {
                        if (fields.Count < 5 || fields[2] != "FlavorText" || fields[3] != "EquipmentToolTypeSheetId" || fields[4] != "InstrumentType") throw new InvalidDataException("EquipmentTool 乐器字段结构不支持");
                        _ = value.Var(); toolType = new(value.UInt32()); var instrument = value.UInt32(); instrumentType = new(instrument);
                        isInstrument = instrument == 0 ? false : instrumentKeys.Contains(instrument) ? true : null;
                    }
                }
                var raw = ClientNameTable.ReadString(text, checked((int)nameOffset));
                var key = new InventoryTypeKey(name, new(id), name);
                try { result.Add(new(key, raw, DisplayName(raw, localized), name, new(row), new(metaAt), new(nameOffset), new(table.DataOffsets[checked((int)row)]), isInstrument, instrumentType, toolType)); }
                catch (UnresolvedNameException error) { failures.Add(new(key, raw, error.Message)); }
            }
            reader.EnsureEnd();
            support.Add(new(name, name, true, result.Count - before));
        }
        support.Add(new("MusicNoteItem", null, false, 0, "客户端乐谱名称来自实际乐谱实例，未提供固定名称 sheet"));
        // Instrument 是演奏音色定义；持有乐器名称属于 EquipmentTool 的实际道具。
        excluded = failures; categories = support;
        return result;
    }

    private static bool HasDisplayName(IReadOnlyList<string> fields)
    {
        var index = Array.IndexOf(fields.ToArray(), "DisplayName");
        return index >= 1 && fields.Take(index).All(f => f is "Id" or "Index");
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSheetFields(byte[] metadata, CancellationToken cancellationToken = default) =>
        ReadSheetFields(metadata, Tables, cancellationToken);
    /// <summary>读取指定 sheet 的字段名（按声明顺序）；用于核对表正文的列顺序。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSheetFields(byte[] metadata, IReadOnlyCollection<string> tables,
        CancellationToken cancellationToken = default)
    {
        static int I32(byte[] b, int at) { if (at < 0 || at > b.Length - 4) throw new InvalidDataException("metadata 字段越界"); return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)); }
        if (metadata.Length < 176 || I32(metadata, 0) != unchecked((int)0xfab11baf) || I32(metadata, 4) != 31)
            throw new InvalidDataException("客户端名称 metadata 版本不支持");
        var strings = I32(metadata, 24); var stringBytes = I32(metadata, 28);
        var fields = I32(metadata, 96); var fieldBytes = I32(metadata, 100);
        var types = I32(metadata, 160); var typeBytes = I32(metadata, 164);
        foreach (var (at, bytes, stride) in new[] { (strings, stringBytes, 1), (fields, fieldBytes, 12), (types, typeBytes, 88) })
            if (at < 0 || bytes < 0 || at > metadata.Length - bytes || bytes % stride != 0) throw new InvalidDataException("metadata 表越界");
        string Text(int index)
        {
            if (index < 0 || index >= stringBytes) throw new InvalidDataException("metadata 字符串越界");
            var span = metadata.AsSpan(strings + index, stringBytes - index); var end = span.IndexOf((byte)0);
            if (end < 0 || end > 16384) throw new InvalidDataException("metadata 字符串截断");
            return new UTF8Encoding(false, true).GetString(span[..end]);
        }
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var index = 0; index < typeBytes / 88; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = types + index * 88;
            var name = Text(I32(metadata, at));
            if (!name.EndsWith("SheetData", StringComparison.Ordinal) || !tables.Contains(name[..^9], StringComparer.Ordinal) ||
                Text(I32(metadata, at + 4)) != "MM.Client.PluginLeaf.SheetDatas.Generated") continue;
            var first = I32(metadata, at + 32); var count = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(at + 68, 2));
            if (count > 512 || first < 0 || first > fieldBytes / 12 - count) throw new InvalidDataException("metadata 名称字段预算或范围无效");
            var names = Enumerable.Range(0, count).Select(i => Text(I32(metadata, fields + (first + i) * 12)))
                .Select(n => n.StartsWith('<') && n.EndsWith(">k__BackingField", StringComparison.Ordinal) ? n[1..n.IndexOf('>')] : n).ToArray();
            if (!result.TryAdd(name[..^9], names)) throw new InvalidDataException("metadata 名称类型歧义");
        }
        return result;
    }

    public static string DisplayName(string rawName, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> localized)
    {
        var name = RuneTierPattern().Replace(rawName, match =>
        {
            var value = System.Numerics.BigInteger.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (value <= 0) return string.Empty;
            if (!localized.TryGetValue("Common", out var common) || !common.TryGetValue("NoRichText_RuneTierFormat", out var template))
                throw new InvalidDataException("客户端缺少符文层级名称格式");
            return template.Replace("{0}", value > 1 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty, StringComparison.Ordinal);
        });
        name = ColorTagPattern().Replace(name, string.Empty).Trim();
        if (UnresolvedPlaceholderPattern().IsMatch(name) || name.Contains('<') || name.Length == 0 || name.EnumerateRunes().Count() > 80 || name.Any(char.IsControl))
            throw new UnresolvedNameException("尚未解析的物品显示名：" + rawName);
        return name;
    }
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private sealed class UnresolvedNameException(string message) : Exception(message);
    [GeneratedRegex(@"%RuneTier,\s*(Season\d+),\s*(\d+)%", RegexOptions.CultureInvariant)] private static partial Regex RuneTierPattern();
    [GeneratedRegex(@"</?color(?:=#[0-9a-fA-F]{6,8})?>", RegexOptions.CultureInvariant)] private static partial Regex ColorTagPattern();
    [GeneratedRegex(@"%[A-Za-z]+[,\s]", RegexOptions.CultureInvariant)] private static partial Regex UnresolvedPlaceholderPattern();
}

