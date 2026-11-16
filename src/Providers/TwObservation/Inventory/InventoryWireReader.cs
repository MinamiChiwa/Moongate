// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
// 来源：MabiAgent ClientInventoryDecoder.Reader；扩展为有界类型化字段读取。
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MabiTool.Providers.TwObservation.Inventory;

internal sealed record InventoryWireValue(string Type, int Start, int End, object? Scalar = null,
    IReadOnlyDictionary<string, InventoryWireValue>? Fields = null,
    IReadOnlyList<(InventoryWireValue Key, InventoryWireValue Value, string KeyHex)>? Pairs = null,
    IReadOnlyList<InventoryWireValue>? Elements = null)
{
    public InventoryWireValue? Field(string name) => Fields?.GetValueOrDefault(name);
    public object? JsonValue() => Fields is not null ? Fields.ToDictionary(p => p.Key, p => p.Value.JsonValue()) :
        Pairs is not null ? Pairs.Select(p => new { key = p.Key.JsonValue(), value = p.Value.JsonValue() }).ToArray() :
        Elements is not null ? Elements.Select(p => p.JsonValue()).ToArray() : Scalar;
    public ulong? Unsigned()
    {
        if (Scalar is string text && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return number;
        if (Scalar is byte b) return b;
        if (Scalar is ushort s) return s;
        if (Scalar is uint u) return u;
        if (Scalar is int i && i >= 0) return (ulong)i;
        if (Fields?.Count == 1) return Fields.Values.Single().Unsigned();
        return null;
    }
}

/// <summary>
/// schema 类型记录的按需索引。JsonElement 按属性名查找是线性扫描（当前客户端 924 个类型，约 9 µs/次），
/// 完整登录体逐值查找会让解析耗时达到秒级；成功解析的类型与泛型参数按名称缓存，错误仍在使用时抛出。
/// </summary>
internal sealed class InventoryWireSchema(JsonElement types)
{
    internal sealed record TypeRecord(string? Parent, (string Name, string? Type)[] Fields, string? EnumType);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TypeRecord> records = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Prefix, string[] Arguments)> generics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> index = Index(types);
    private static Dictionary<string, JsonElement> Index(JsonElement types)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in types.EnumerateObject()) result.TryAdd(property.Name, property.Value);
        return result;
    }
    public TypeRecord Type(string name)
    {
        if (records.TryGetValue(name, out var cached)) return cached;
        if (!index.TryGetValue(name, out var record)) throw new InvalidDataException("inventory.typeUnsupported:" + name);
        var fields = record.GetProperty("fields"); if (fields.GetArrayLength() > 512) throw new InvalidDataException("inventory.fieldBudget");
        var parent = record.GetProperty("parent").GetString();
        var result = parent == "System.Enum"
            ? new TypeRecord(parent, [], fields.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "value__").GetProperty("type").GetString())
            : new TypeRecord(parent, fields.EnumerateArray().Select(field => (field.GetProperty("name").GetString() ?? throw new InvalidDataException("inventory.fieldInvalid"),
                field.GetProperty("type").GetString())).ToArray(), null);
        return records.GetOrAdd(name, result);
    }
    public (string Prefix, string[] Arguments) Generic(string name)
    {
        if (generics.TryGetValue(name, out var cached)) return cached;
        return generics.GetOrAdd(name, (name.Split('<')[0], InventoryWireReader.Arguments(name).ToArray()));
    }
}

internal sealed class InventoryWireReader(byte[] body, InventoryWireSchema schema, CancellationToken cancellation = default)
{
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);
    private int elements;
    public int Offset { get; private set; }
    /// <summary>从指定位置继续读取（定位登录数据里的钱包段，见 <see cref="LoginWalletLocator"/>）。</summary>
    public void Seek(int offset)
    {
        if (offset < 0 || offset > body.Length) throw new InvalidDataException("inventory.bodyTruncated");
        Offset = offset;
    }
    public ReadOnlySpan<byte> Read(int count)
    {
        cancellation.ThrowIfCancellationRequested();
        if (count < 0 || count > body.Length - Offset) throw new InvalidDataException("inventory.bodyTruncated");
        var result = body.AsSpan(Offset, count); Offset += count; return result;
    }
    public int Count()
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(Read(4));
        if (count < 0 || count > 4096 || checked(elements + count) > 65536) throw new InvalidDataException("inventory.collectionBudget");
        elements += count; return count;
    }
    public InventoryWireValue Value(string? name, int depth = 0, bool capture = true,
        Func<string, bool>? selectFields = null)
    {
        if (depth > 48) throw new InvalidDataException("inventory.recursionBudget");
        var start = Offset;
        if (name is null or "object" or "System.Object" or "System.ValueType" or "System.Enum") return new(name ?? "object", start, Offset);
        object? scalar;
        switch (name)
        {
            case "bool": var flag = Read(1)[0]; if (flag > 1) throw new InvalidDataException("inventory.booleanInvalid"); scalar = flag == 1; break;
            case "int8": scalar = (int)unchecked((sbyte)Read(1)[0]); break;
            case "uint8": scalar = Read(1)[0]; break;
            case "int16": scalar = (int)BinaryPrimitives.ReadInt16LittleEndian(Read(2)); break;
            case "uint16": case "char": scalar = BinaryPrimitives.ReadUInt16LittleEndian(Read(2)); break;
            case "int32": scalar = BinaryPrimitives.ReadInt32LittleEndian(Read(4)); break;
            case "uint32": scalar = BinaryPrimitives.ReadUInt32LittleEndian(Read(4)); break;
            case "int64": scalar = BinaryPrimitives.ReadInt64LittleEndian(Read(8)).ToString(CultureInfo.InvariantCulture); break;
            case "uint64": scalar = BinaryPrimitives.ReadUInt64LittleEndian(Read(8)).ToString(CultureInfo.InvariantCulture); break;
            case "float32": var f = BinaryPrimitives.ReadSingleLittleEndian(Read(4)); if (!float.IsFinite(f)) throw new InvalidDataException("inventory.floatInvalid"); scalar = f; break;
            case "float64": var d = BinaryPrimitives.ReadDoubleLittleEndian(Read(8)); if (!double.IsFinite(d)) throw new InvalidDataException("inventory.floatInvalid"); scalar = d; break;
            // 客户端 ReplicatedTime.ReadFrom 只读一个 float32（offsetInSeconds，发送时点的剩余秒数），channelTime 由接收方按本地时钟补出。
            case "Shared.Protocols.ProtocolDatas.ReplicatedDatas.ReplicatedTime": var remaining = BinaryPrimitives.ReadSingleLittleEndian(Read(4)); if (!float.IsFinite(remaining)) throw new InvalidDataException("inventory.floatInvalid"); scalar = remaining; break;
            case "System.Decimal": var bytes = Read(16); scalar = Convert.ToHexStringLower(bytes); break;
            // 客户端 Decoder 读 UniqueString 与读 string 相同（int32 字节数 + UTF-16），元数据里的 hashCode 不上线。
            case "string": case "Silvervine.ManualMemory.UniqueString":
                var size = BinaryPrimitives.ReadInt32LittleEndian(Read(4));
                if (size < 0 || size > 65536 || size % 2 != 0) throw new InvalidDataException("inventory.textBudget");
                try { scalar = Utf16.GetString(Read(size)); } catch (DecoderFallbackException error) { throw new InvalidDataException("inventory.textInvalid", error); }
                break;
            default: return Composite(name, start, depth, capture, selectFields);
        }
        return new(name, start, Offset, capture ? scalar : null);
    }
    private InventoryWireValue Composite(string name, int start, int depth, bool capture, Func<string, bool>? selectFields)
    {
        var (prefix, args) = schema.Generic(name);
        if (prefix == "System.Nullable`1" && args.Length == 1)
        {
            var present = Read(1)[0]; if (present > 1) throw new InvalidDataException("inventory.nullableInvalid");
            var value = present == 1 ? Value(args[0], depth + 1, capture) : null;
            return new(name, start, Offset, value?.Scalar, value?.Fields, value?.Pairs, value?.Elements);
        }
        var dictionary = args.Length == 2 && (prefix.EndsWith("Dictionary`2", StringComparison.Ordinal) || prefix.EndsWith("MMDictionary`2", StringComparison.Ordinal));
        var sequence = args.Length == 1 && (prefix.EndsWith("List`1", StringComparison.Ordinal) || prefix.EndsWith("HashSet`1", StringComparison.Ordinal) || prefix.EndsWith("ArraySegment`1", StringComparison.Ordinal));
        if (name.EndsWith("[]", StringComparison.Ordinal)) { args = [name[..^2]]; sequence = true; }
        if (dictionary || sequence)
        {
            var count = Count(); var pairs = new List<(InventoryWireValue, InventoryWireValue, string)>();
            var values = new List<InventoryWireValue>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var key = Value(args[0], depth + 1, capture);
                var hex = Convert.ToHexStringLower(body.AsSpan(key.Start, key.End - key.Start));
                if (dictionary)
                {
                    if (!seen.Add(hex)) throw new InvalidDataException("inventory.duplicateKey");
                    var value = Value(args[1], depth + 1, capture);
                    if (capture) pairs.Add((key, value, hex));
                }
                else if (capture) values.Add(key);
            }
            return new(name, start, Offset, Pairs: dictionary && capture ? pairs : null, Elements: sequence && capture ? values : null);
        }
        var record = schema.Type(name);
        if (record.Parent == "System.Enum")
        {
            var value = Value(record.EnumType, depth + 1, capture);
            return new(name, start, Offset, value.Scalar);
        }
        var inherited = Value(record.Parent, depth + 1, capture);
        var result = inherited.Fields is null ? new Dictionary<string, InventoryWireValue>(StringComparer.Ordinal) : new(inherited.Fields, StringComparer.Ordinal);
        foreach (var (key, type) in record.Fields)
        {
            var fieldOffset = Offset;
            var wanted = capture && (selectFields is null || selectFields(key));
            try
            {
                var value = Value(type, depth + 1, wanted);
                if (wanted && !result.TryAdd(key, value)) throw new InvalidDataException("inventory.fieldDuplicate");
            }
            catch (InvalidDataException error) { throw new InvalidDataException(name + "." + key + "@" + fieldOffset + ": " + error.Message, error); }
        }
        return new(name, start, Offset, Fields: capture ? result : null);
    }
    /// <summary>
    /// 按字段顺序读取根记录，只保留 <paramref name="selectFields"/> 选中的字段。未选中的字段因数据形状不符读取失败时停在该字段并返回已读部分
    /// （<paramref name="stoppedAt"/> 为该字段名），其后的字段视为未读；选中的字段读取失败仍抛出。
    /// 用于登录个人数据：台服 2026-11 构建里任务等字段的类型含客户端元数据看不出的未序列化成员，按元数据顺序读不到帧尾。
    /// </summary>
    public InventoryWireValue RootPrefix(string name, Func<string, bool> selectFields, out string? stoppedAt)
    {
        stoppedAt = null;
        var start = Offset;
        var record = schema.Type(name);
        var inherited = Value(record.Parent, 1, true);
        var result = inherited.Fields is null ? new Dictionary<string, InventoryWireValue>(StringComparer.Ordinal) : new(inherited.Fields, StringComparer.Ordinal);
        foreach (var (key, type) in record.Fields)
        {
            var fieldOffset = Offset;
            var wanted = selectFields(key);
            try
            {
                var value = Value(type, 1, wanted);
                if (wanted && !result.TryAdd(key, value)) throw new InvalidDataException("inventory.fieldDuplicate");
            }
            // 帧被截断仍按整帧无效处理；只有数据形状与元数据不符时才停在该字段。
            catch (InvalidDataException error) when (!wanted && !error.Message.EndsWith("inventory.bodyTruncated", StringComparison.Ordinal))
            { stoppedAt = key; Offset = fieldOffset; break; }
            catch (InvalidDataException error) { throw new InvalidDataException(name + "." + key + "@" + fieldOffset + ": " + error.Message, error); }
        }
        return new(name, start, Offset, Fields: result);
    }
    public void EnsureEnd() { if (Offset != body.Length) throw new InvalidDataException("inventory.bodyTrailing"); }
    internal static List<string> Arguments(string name)
    {
        var first = name.IndexOf('<'); if (first < 0 || !name.EndsWith('>')) return [];
        var result = new List<string>(); var start = first + 1; var depth = 0;
        for (var i = start; i < name.Length - 1; i++)
        {
            if (name[i] == '<') depth++; else if (name[i] == '>') depth--;
            else if (name[i] == ',' && depth == 0) { result.Add(name[start..i]); start = i + 1; }
        }
        if (depth != 0) throw new InvalidDataException("inventory.genericInvalid");
        result.Add(name[start..^1]); return result;
    }
}
