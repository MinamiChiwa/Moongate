// Copyright (c) 2026 MinamiChiwa（南千和）; MIT. 来源 MabiAgent working tree ClientLayoutReader.cs SHA256 6c5eb5c7d9bcd8bfd8d92696808a59957f891ff4536a63a565a9f91cb4ba075e。
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MabiTool.Providers.TwObservation.Inventory;

public sealed record CaptureProtocol(uint Login, uint Profile, uint Inventory)
{
    // Only explicit offline/test callers use the historical profile.
    
}
public sealed record ClientCaptureLayout(string AssemblyHash, string MetadataHash, CaptureProtocol Protocol, JsonElement Schema)
{
    public ClientInventoryDecoder CreateDecoder() => new(Schema);
    public IReadOnlyDictionary<string, uint> Opcodes { get; init; } = new Dictionary<string, uint>();
    public IReadOnlyDictionary<uint, InventoryProtocolMethod> Methods { get; init; } = new Dictionary<uint, InventoryProtocolMethod>();
    /// <summary>钱包货币的客户端表数据（名称、说明、补充间隔）；读不到时为空，资源照常按变化消息更新，只是没有名称与倒计时。</summary>
    public ItemCatalog.ClientResourceCatalog? Resources { get; init; }
    public bool SupportsInventoryMutations => CreateDecoder().Categories is { Count: > 0 } categories && categories.All(category =>
        new[] { "Add", "Update", "Remove" }.All(verb => Methods.Values.Any(method =>
            method.Parameters.Count is 1 or 2 && method.Name.StartsWith(verb, StringComparison.Ordinal) &&
            !method.Name.Contains("Storage", StringComparison.Ordinal) && !method.Name.Contains("Album", StringComparison.Ordinal) &&
            !method.Name.Contains("Equipped", StringComparison.Ordinal) &&
            (method.Category == category || method.Category == "MemorialSaga" && method.Name.Contains(category, StringComparison.Ordinal)) &&
            (method.Name == verb + category || verb == "Update" && method.Name == verb + category + "Stack"))));
}
public sealed record InventoryProtocolMethod(string Name, string Category, IReadOnlyList<string> Parameters);
/// <summary>客户端构建事实：协议枚举（名称 → 编号）、字段闭包（types 字典）与接口方法签名。</summary>
public sealed record ClientBuildFacts(string AssemblyHash, string MetadataHash, IReadOnlyDictionary<string, int> Protocols, JsonElement Schema,
    IReadOnlyDictionary<string, IReadOnlyList<InventoryProtocolMethod>> Interfaces);

/// <summary>Reads IL2CPP v31 metadata and x64 PE data. Never loads client code or reads process memory.</summary>
public sealed class ClientLayoutReader
{
    private readonly byte[] metadata, assembly;
    private readonly int strings, stringSize, fields, fieldSize, definitions, definitionSize;
    private readonly ulong imageBase;
    private readonly List<(uint Rva, uint Size, uint Offset)> sections = [];
    private readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);
    private readonly string[] names;
    private readonly ulong typeTable, typeCount;
    private readonly CancellationToken cancellation;
    private static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(Slice(bytes, at, 4));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(bytes, at, 4));
    private static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(bytes, at, 8));
    private static ReadOnlySpan<byte> Slice(byte[] bytes, int at, int count)
    {
        if (at < 0 || count < 0 || at > bytes.Length - count) throw new InvalidDataException("客户端文件数据越界");
        return bytes.AsSpan(at, count);
    }
    private (int Offset, int Size) Pair(int at, int stride = 1)
    {
        var offset = I32(metadata, at); var size = I32(metadata, at + 4);
        _ = Slice(metadata, offset, size);
        if (size % stride != 0 || size / stride > 1_000_000 && stride != 1) throw new InvalidDataException("客户端元数据表格式不支持");
        return (offset, size);
    }
    private string Text(int index)
    {
        if (index < 0 || index >= stringSize) throw new InvalidDataException("客户端字符串索引无效");
        var span = metadata.AsSpan(strings + index, stringSize - index); var end = span.IndexOf((byte)0);
        if (end < 0 || end > 16384) throw new InvalidDataException("客户端字符串长度无效");
        return new UTF8Encoding(false, true).GetString(span[..end]);
    }
    private int Offset(ulong address, int length = 1)
    {
        if (address < imageBase) throw new InvalidDataException("客户端地址无效");
        var rva = address - imageBase;
        foreach (var section in sections)
            if (rva >= section.Rva && rva - section.Rva < section.Size && (ulong)length <= section.Size - (rva - section.Rva))
            {
                var at = checked((int)(section.Offset + rva - section.Rva)); _ = Slice(assembly, at, length); return at;
            }
        throw new InvalidDataException("客户端地址未映射");
    }
    private ulong Pointer(ulong address) => U64(assembly, Offset(address, 8));
    private ClientLayoutReader(byte[] metadata, byte[] assembly, CancellationToken cancellation)
    {
        this.metadata = metadata; this.assembly = assembly; this.cancellation = cancellation;
        if (U32(metadata, 0) != 0xfab11baf || U32(metadata, 4) != 31) throw new InvalidDataException("暂不支持此客户端元数据版本");
        (strings, stringSize) = Pair(24); (fields, fieldSize) = Pair(96, 12); (definitions, definitionSize) = Pair(160, 88);
        if (U32(assembly, 0) % 65536 != 0x5a4d) throw new InvalidDataException("客户端不是有效PE文件");
        var pe = I32(assembly, 60);
        if (U32(assembly, pe) != 0x4550 || BinaryPrimitives.ReadUInt16LittleEndian(Slice(assembly, pe + 4, 2)) != 0x8664 || BinaryPrimitives.ReadUInt16LittleEndian(Slice(assembly, pe + 24, 2)) != 0x20b)
            throw new InvalidDataException("暂不支持此客户端架构");
        imageBase = U64(assembly, pe + 48);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(Slice(assembly, pe + 6, 2));
        var sectionAt = checked(pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(Slice(assembly, pe + 20, 2)));
        if (count is 0 or > 96) throw new InvalidDataException("客户端PE节数量无效");
        for (var i = 0; i < count; i++)
        {
            var at = checked(sectionAt + i * 40); var size = U32(assembly, at + 16); var raw = U32(assembly, at + 20);
            _ = Slice(assembly, checked((int)raw), checked((int)size)); sections.Add((U32(assembly, at + 12), size, raw));
        }
        names = new string[definitionSize / 88];
        for (var i = 0; i < names.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var at = definitions + i * 88;
            names[i] = Text(I32(metadata, at + 4)) + "." + Text(I32(metadata, at));
            // Generic and nested names can repeat; reject ambiguity only when requested.
            if (!indices.TryAdd(names[i], i)) indices[names[i]] = -1;
        }
        var candidates = new List<(ulong Count, ulong Table)>();
        for (var at = 32; at <= assembly.Length - 32; at += 8)
        {
            if ((at & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
            if (U64(assembly, at) != (ulong)names.Length || U64(assembly, at + 16) != (ulong)names.Length) continue;
            try
            {
                var fieldTable = U64(assembly, at + 8); _ = Offset(U64(assembly, at + 24));
                for (var i = 0; i < 16; i++) { var ptr = Pointer(fieldTable + (ulong)i * 8); if (ptr != 0) _ = Offset(ptr); }
                var n = U64(assembly, at - 32); var table = U64(assembly, at - 24);
                if (n is 0 or > 1_000_000) continue;
                _ = Offset(table, checked((int)n * 8)); candidates.Add((n, table));
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException) { }
        }
        if (candidates.Count != 1) throw new InvalidDataException("客户端类型注册表无法唯一定位");
        (typeCount, typeTable) = candidates[0];
    }
    private ulong TypePointer(int index)
    {
        if (index < 0 || (ulong)index >= typeCount) throw new InvalidDataException("客户端类型索引无效");
        return Pointer(typeTable + (ulong)index * 8);
    }
    private string? TypeName(int index) => index < 0 ? null : PointerName(TypePointer(index));
    private string PointerName(ulong pointer, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("客户端泛型递归超限");
        var at = Offset(pointer, 12); var data = U64(assembly, at); var bits = U32(assembly, at + 8); var kind = bits >> 16 & 255;
        var name = kind switch
        {
            1 => "void", 2 => "bool", 3 => "char", 4 => "int8", 5 => "uint8", 6 => "int16", 7 => "uint16", 8 => "int32", 9 => "uint32", 10 => "int64", 11 => "uint64", 12 => "float32", 13 => "float64", 14 => "string", 28 => "object",
            17 or 18 => data < (ulong)names.Length ? names[(int)data] : throw new InvalidDataException("客户端类型定义无效"),
            15 or 16 or 29 => PointerName(data, depth + 1) + (kind == 15 ? "*" : kind == 16 ? "&" : "[]"),
            21 => Generic(data, depth + 1),
            19 or 30 => Parameter(data, kind),
            _ => throw new InvalidDataException("不支持的客户端类型：" + kind)
        };
        return (bits & 1u << 29) != 0 ? name + "&" : name;
    }
    private string Parameter(ulong data, uint kind)
    {
        var (offset, size) = Pair(104, 16);
        if (data >= (ulong)(size / 16)) throw new InvalidDataException("客户端泛型参数无效");
        return (kind == 19 ? "T" : "M") + BinaryPrimitives.ReadUInt16LittleEndian(Slice(metadata, checked(offset + (int)data * 16 + 12), 2));
    }
    private string Generic(ulong data, int depth)
    {
        var instance = Pointer(data + 8); var count = Pointer(instance); var args = Pointer(instance + 8);
        if (count > 32) throw new InvalidDataException("客户端泛型参数过多");
        return PointerName(Pointer(data), depth) + "<" + string.Join(',', Enumerable.Range(0, (int)count).Select(i => PointerName(Pointer(args + (ulong)i * 8), depth))) + ">";
    }
    private (int Start, int Count) FieldRange(int index)
    {
        var at = definitions + index * 88; var start = I32(metadata, at + 32); var count = BinaryPrimitives.ReadUInt16LittleEndian(Slice(metadata, at + 68, 2));
        if (count > 0 && (start < 0 || (long)start + count > fieldSize / 12)) throw new InvalidDataException("客户端字段范围无效");
        return (start, count);
    }
    private static IEnumerable<string> Arguments(string name)
    {
        var first = name.IndexOf('<'); if (first < 0) yield break;
        var start = first + 1; var depth = 0;
        for (var i = start; i < name.Length; i++)
        {
            if (name[i] == '<') depth++;
            if (name[i] == '>' && depth-- == 0) { yield return name[start..i]; yield break; }
            if (name[i] == ',' && depth == 0) { yield return name[start..i]; start = i + 1; }
        }
    }
    private JsonElement Schema(IEnumerable<string>? additional = null, bool inventoryRoot = true)
    {
        var result = new Dictionary<string, object>();
        void Collect(string? name, int depth = 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if(depth > 128) throw new InvalidDataException("客户端库存类型依赖过深");
            if (name is null or "object" or "System.Object" or "System.ValueType" or "System.Enum" || result.ContainsKey(name)) return;
            if (name.EndsWith("[]", StringComparison.Ordinal)) { Collect(name[..^2],depth+1); return; }
            if (name.Contains('<')) { foreach (var arg in Arguments(name)) Collect(arg,depth+1); return; }
            if (!indices.TryGetValue(name, out var index)) return;
            if (index < 0 || result.Count >= 4096) throw new InvalidDataException("客户端库存类型重复或过多");
            var parent = TypeName(I32(metadata, definitions + index * 88 + 16));
            var list = new List<object>(); var children = new List<string>(); var range = FieldRange(index);
            for (var i = 0; i < range.Count; i++)
            {
                var at = fields + (range.Start + i) * 12; var typ = I32(metadata, at + 4);
                if ((U32(assembly, Offset(TypePointer(typ), 12) + 8) & 0x10) != 0) continue;
                var fieldType = TypeName(typ)!; list.Add(new { name = Text(I32(metadata, at)), type = fieldType }); children.Add(fieldType);
            }
            result.Add(name, new { parent, fields = list }); Collect(parent,depth+1); foreach (var child in children) Collect(child,depth+1);
        }
        if (inventoryRoot) Collect(ClientInventoryDecoder.RootType);
        foreach (var name in additional ?? []) Collect(name);
        if (inventoryRoot && !result.ContainsKey(ClientInventoryDecoder.RootType)) throw new InvalidDataException("客户端缺少库存根类型");
        return JsonSerializer.SerializeToElement(new { types = result });
    }
    private Dictionary<int, int>? defaultValues;
    private int ProtocolDefinition()
    {
        var matches = indices.Where(p => p.Key.EndsWith(".GCProtocol", StringComparison.Ordinal) && p.Value >= 0).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("客户端协议枚举无法唯一定位");
        return matches[0].Value;
    }
    /// <summary>字段默认值（枚举成员值）按字段序号建索引，只建一次。</summary>
    private int? FieldDefault(int field)
    {
        if (defaultValues is null)
        {
            var defaults = Pair(64, 12); var map = new Dictionary<int, int>();
            for (var at = defaults.Offset; at < defaults.Offset + defaults.Size; at += 12)
                if (!map.TryAdd(I32(metadata, at), I32(metadata, at + 8))) throw new InvalidDataException("客户端协议值无效");
            defaultValues = map;
        }
        if (!defaultValues.TryGetValue(field, out var index)) return null;
        var values = Pair(72);
        if (index < 0 || index >= values.Size) throw new InvalidDataException("客户端协议值无效");
        var start = values.Offset + index; var b = metadata[start]; uint encoded;
        if (b < 0x80) encoded = b;
        else if (b < 0xc0) { _ = Slice(metadata, start, 2); encoded = (uint)((b & 0x3f) << 8 | metadata[start + 1]); }
        else if (b < 0xe0) { _ = Slice(metadata, start, 4); encoded = (uint)((b & 0x1f) << 24 | metadata[start + 1] << 16 | metadata[start + 2] << 8 | metadata[start + 3]); }
        else if (b == 0xf0) encoded = U32(metadata, start + 1);
        else throw new InvalidDataException("客户端协议值压缩格式不支持");
        return (encoded & 1) != 0 ? -checked((int)(encoded >> 1)) - 1 : checked((int)(encoded >> 1));
    }
    private uint Opcode(string field)
    {
        var range = FieldRange(ProtocolDefinition()); var found = -1;
        for (var i = 0; i < range.Count; i++) if (Text(I32(metadata, fields + (range.Start + i) * 12)) == field) { if (found >= 0) throw new InvalidDataException("客户端协议字段重复"); found = range.Start + i; }
        if (found < 0) throw new InvalidDataException("客户端缺少协议：" + field);
        var value = FieldDefault(found);
        if (value is null or <= 0) throw new InvalidDataException("客户端协议号无效");
        return (uint)value.Value;
    }
    /// <summary>GCProtocol 全部常量成员（名称 → 线上编号）；名称重复或缺值时整表无效。</summary>
    private IReadOnlyDictionary<string, int> ProtocolTable()
    {
        var range = FieldRange(ProtocolDefinition());
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < range.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var at = fields + (range.Start + i) * 12;
            if ((U32(assembly, Offset(TypePointer(I32(metadata, at + 4)), 12) + 8) & 0x10) == 0) continue;
            var name = Text(I32(metadata, at));
            if (FieldDefault(range.Start + i) is not { } value || !result.TryAdd(name, value)) throw new InvalidDataException("客户端协议枚举无效");
        }
        return result;
    }
    /// <summary>接口方法的名称与参数类型（按声明顺序）；接口不存在时为空。</summary>
    private IReadOnlyList<InventoryProtocolMethod> InterfaceMethods(string interfaceName)
    {
        if (!indices.TryGetValue(interfaceName, out var index) || index < 0) return [];
        var methods = Pair(48, 36); var parameters = Pair(88, 12);
        var definition = definitions + index * 88;
        var start = I32(metadata, definition + 36);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(Slice(metadata, definition + 64, 2));
        if (start < 0 || (long)start + count > methods.Size / 36) throw new InvalidDataException("客户端方法范围无效");
        var result = new List<InventoryProtocolMethod>();
        for (var i = 0; i < count; i++)
        {
            var at = methods.Offset + (start + i) * 36; var name = Text(I32(metadata, at));
            var paramStart = I32(metadata, at + 16); var paramCount = BinaryPrimitives.ReadUInt16LittleEndian(Slice(metadata, at + 34, 2));
            if (paramCount > 32 || paramCount > 0 && (paramStart < 0 || (long)paramStart + paramCount > parameters.Size / 12)) throw new InvalidDataException("客户端参数范围无效");
            result.Add(new(name, interfaceName, Enumerable.Range(0, paramCount).Select(n => TypeName(I32(metadata, parameters.Offset + (paramStart + n) * 12 + 8))
                ?? throw new InvalidDataException("客户端参数类型无效")).ToArray()));
        }
        return result;
    }
    private IReadOnlyDictionary<string, uint> InventoryOpcodes()
    {
        var range = FieldRange(ProtocolDefinition());
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var i = 0; i < range.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = Text(I32(metadata, fields + (range.Start + i) * 12));
            if (name.Contains("Inventory", StringComparison.Ordinal) || name.Contains("Wallet", StringComparison.Ordinal) ||
                name.Contains("EquipmentSlots", StringComparison.Ordinal) || name.Contains("EquipmentToolSlots", StringComparison.Ordinal) ||
                name.Contains("EquippedCostumes", StringComparison.Ordinal) || name == "CHANNEL_MyCharacterPersonalData_NTF" ||
                name == "REPLICATION_GuildMembers_UpdateGuildMemberInfo_REPL" || WalletProtocol.ExtraProtocolNames.Contains(name)) result.Add(name, Opcode(name));
        }
        if (result.Values.Distinct().Count() != result.Count) throw new InvalidDataException("库存协议号冲突");
        return result;
    }
    private IReadOnlyDictionary<uint, InventoryProtocolMethod> InventoryMethods(IReadOnlyDictionary<string, uint> opcodes)
    {
        var methods = Pair(48, 36); var parameters = Pair(88, 12);
        var result = new Dictionary<uint, InventoryProtocolMethod>();
        foreach (var pair in indices.Where(p => p.Value >= 0 && p.Key.Contains(".IReplicatedGroups.I", StringComparison.Ordinal) &&
            (p.Key.EndsWith("InventoryReplicated", StringComparison.Ordinal) || p.Key.EndsWith("IGuildMembersReplicated", StringComparison.Ordinal))))
        {
            cancellation.ThrowIfCancellationRequested();
            var simple = pair.Key.Split('.').Last(); var group = simple[1..^"Replicated".Length];
            var definition = definitions + pair.Value * 88;
            var start = I32(metadata, definition + 36);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(Slice(metadata, definition + 64, 2));
            if (start < 0 || (long)start + count > methods.Size / 36) throw new InvalidDataException("库存方法范围无效");
            for (var i = 0; i < count; i++)
            {
                var at = methods.Offset + (start + i) * 36; var name = Text(I32(metadata, at));
                if (!opcodes.TryGetValue("REPLICATION_" + group + "_" + name + "_REPL", out var opcode)) continue;
                var paramStart = I32(metadata, at + 16); var paramCount = BinaryPrimitives.ReadUInt16LittleEndian(Slice(metadata, at + 34, 2));
                if (paramCount > 32 || paramCount > 0 && (paramStart < 0 || (long)paramStart + paramCount > parameters.Size / 12)) throw new InvalidDataException("库存参数范围无效");
                var args = Enumerable.Range(0, paramCount).Select(n => TypeName(I32(metadata, parameters.Offset + (paramStart + n) * 12 + 8))
                    ?? throw new InvalidDataException("库存参数类型无效")).ToArray();
                result.Add(opcode, new(name, group.EndsWith("Inventory", StringComparison.Ordinal) ? group[..^"Inventory".Length] : group, args));
            }
        }
        return result;
    }
    public static ClientCaptureLayout Read(string directory, CancellationToken cancellation = default, ClientCaptureLayout? cached = null)
    {
        var (dll, meta, assemblyHash, metadataHash) = ReadClientFiles(directory, cancellation);
        if(cached is not null && cached.AssemblyHash == assemblyHash && cached.MetadataHash == metadataHash) return cached;
        var reader = new ClientLayoutReader(meta, dll, cancellation);
        var protocol = new CaptureProtocol(reader.Opcode("CHANNEL_MyCharacterPersonalData_NTF"), reader.Opcode("REPLICATION_GuildMembers_UpdateGuildMemberInfo_REPL"), reader.Opcode("REPLICATION_ConsumableInventory_UpdateConsumableStack_REPL"));
        if (new[] { protocol.Login, protocol.Profile, protocol.Inventory }.Distinct().Count() != 3) throw new InvalidDataException("客户端协议号冲突");
        var opcodes = reader.InventoryOpcodes(); var methods = reader.InventoryMethods(opcodes);
        return new(assemblyHash, metadataHash, protocol, reader.Schema(methods.Values.SelectMany(m => m.Parameters))) { Opcodes = opcodes, Methods = methods };
    }
    /// <summary>读取构建核对所需的客户端事实：完整协议枚举、给定根类型的字段闭包与接口方法签名。</summary>
    public static ClientBuildFacts ReadBuildFacts(string directory, IReadOnlyCollection<string> schemaRoots, IReadOnlyCollection<string> interfaces,
        CancellationToken cancellation = default)
    {
        var (dll, meta, assemblyHash, metadataHash) = ReadClientFiles(directory, cancellation);
        var reader = new ClientLayoutReader(meta, dll, cancellation);
        return new(assemblyHash, metadataHash, reader.ProtocolTable(), reader.Schema(schemaRoots, inventoryRoot: false),
            interfaces.ToDictionary(name => name, reader.InterfaceMethods, StringComparer.Ordinal));
    }
    private static (byte[] Assembly, byte[] Metadata, string AssemblyHash, string MetadataHash) ReadClientFiles(string directory, CancellationToken cancellation)
    {
        directory = Path.GetFullPath(directory);
        var assemblyPath = Path.Combine(directory, "GameAssembly.dll");
        var metadataFiles = Directory.EnumerateDirectories(directory, "*_Data", SearchOption.TopDirectoryOnly)
            .Select(d => Path.Combine(d, "il2cpp_data", "Metadata", "global-metadata.dat")).Where(File.Exists).Take(2).ToArray();
        if (metadataFiles.Length != 1) throw new InvalidDataException("未找到唯一的客户端元数据文件，请选择游戏程序所在目录");
        // Hold both files read-only for the entire extraction, preventing mixed-version reads during an update.
        using var assemblyLock = new FileStream(assemblyPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var metadataLock = new FileStream(metadataFiles[0],FileMode.Open,FileAccess.Read,FileShare.Read);
        var dll = ReadFile(assemblyPath, 768 * 1024 * 1024, cancellation);
        var meta = ReadFile(metadataFiles[0], 256 * 1024 * 1024, cancellation);
        return (dll, meta, Convert.ToHexStringLower(SHA256.HashData(dll)), Convert.ToHexStringLower(SHA256.HashData(meta)));
    }
    private static byte[] ReadFile(string path, int limit, CancellationToken cancellation)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length <= 0 || input.Length > limit) throw new InvalidDataException("客户端文件大小不支持");
        var bytes = new byte[checked((int)input.Length)];
        for (var at = 0; at < bytes.Length;) { cancellation.ThrowIfCancellationRequested(); var count = input.Read(bytes, at, Math.Min(1024 * 1024, bytes.Length - at)); if (count == 0) throw new EndOfStreamException(); at += count; }
        if (input.Length != bytes.Length) throw new InvalidDataException("解析期间客户端发生更新，请等待更新结束");
        return bytes;
    }
}
