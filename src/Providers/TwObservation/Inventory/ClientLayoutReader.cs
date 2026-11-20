// Copyright (c) 2026 MinamiChiwa（南千和）; MIT. 来源 MabiAgent working tree ClientLayoutReader.cs SHA256 6c5eb5c7d9bcd8bfd8d92696808a59957f891ff4536a63a565a9f91cb4ba075e。
using System.Buffers.Binary;
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

/// <summary>
/// Reads IL2CPP v31 metadata and x64 PE data. Never loads client code or reads process memory.
/// 两个文件以只读共享句柄打开并在整个读取期间持有（更新程序不能写入或替换）；哈希为有界缓冲的流式 SHA-256，
/// 解析经只读内存映射按需读取所用的表与节，不把文件整读进托管数组。
/// </summary>
public sealed class ClientLayoutReader
{
    private readonly MappedClientFile metadata, assembly;
    private readonly int strings, stringSize, fields, fieldSize, definitions, definitionSize;
    private readonly ulong imageBase;
    private readonly List<(uint Rva, uint Size, uint Offset, uint Characteristics)> sections = [];
    private readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);
    private readonly string[] names;
    private readonly ulong typeTable, typeCount;
    private readonly CancellationToken cancellation;
    /// <summary>布局读取规则版本；读取规则或缓存格式变化后旧的布局缓存失效。</summary>
    public const int LayoutRulesVersion = 1;
    private (int Offset, int Size) Pair(int at, int stride = 1)
    {
        var offset = metadata.I32(at); var size = metadata.I32(at + 4);
        metadata.Check(offset, size);
        if (size % stride != 0 || size / stride > 1_000_000 && stride != 1) throw new InvalidDataException("客户端元数据表格式不支持");
        return (offset, size);
    }
    private string Text(int index)
    {
        if (index < 0 || index >= stringSize) throw new InvalidDataException("客户端字符串索引无效");
        return metadata.Text(strings + index, stringSize - index);
    }
    private int Offset(ulong address, int length = 1)
    {
        if (address < imageBase) throw new InvalidDataException("客户端地址无效");
        var rva = address - imageBase;
        foreach (var section in sections)
            if (rva >= section.Rva && rva - section.Rva < section.Size && (ulong)length <= section.Size - (rva - section.Rva))
            {
                var at = checked((int)(section.Offset + rva - section.Rva)); assembly.Check(at, length); return at;
            }
        throw new InvalidDataException("客户端地址未映射");
    }
    private ulong Pointer(ulong address) => assembly.U64(Offset(address, 8));
    private ClientLayoutReader(MappedClientFile metadata, MappedClientFile assembly, CancellationToken cancellation)
    {
        this.metadata = metadata; this.assembly = assembly; this.cancellation = cancellation;
        if (metadata.U32(0) != 0xfab11baf || metadata.U32(4) != 31) throw new InvalidDataException("暂不支持此客户端元数据版本");
        (strings, stringSize) = Pair(24); (fields, fieldSize) = Pair(96, 12); (definitions, definitionSize) = Pair(160, 88);
        if (assembly.U32(0) % 65536 != 0x5a4d) throw new InvalidDataException("客户端不是有效PE文件");
        var pe = assembly.I32(60);
        if (assembly.U32(pe) != 0x4550 || assembly.U16(pe + 4) != 0x8664 || assembly.U16(pe + 24) != 0x20b)
            throw new InvalidDataException("暂不支持此客户端架构");
        imageBase = assembly.U64(pe + 48);
        var count = assembly.U16(pe + 6);
        var sectionAt = checked(pe + 24 + assembly.U16(pe + 20));
        if (count is 0 or > 96) throw new InvalidDataException("客户端PE节数量无效");
        for (var i = 0; i < count; i++)
        {
            var at = checked(sectionAt + i * 40); var size = assembly.U32(at + 16); var raw = assembly.U32(at + 20);
            assembly.Check(checked((int)raw), checked((int)size)); sections.Add((assembly.U32(at + 12), size, raw, assembly.U32(at + 36)));
        }
        names = new string[definitionSize / 88];
        for (var i = 0; i < names.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var at = definitions + i * 88;
            names[i] = Text(metadata.I32(at + 4)) + "." + Text(metadata.I32(at));
            // Generic and nested names can repeat; reject ambiguity only when requested.
            if (!indices.TryAdd(names[i], i)) indices[names[i]] = -1;
        }
        (typeCount, typeTable) = TypeRegistry();
    }
    /// <summary>
    /// 类型注册表（Il2CppMetadataRegistration）是带重定位指针的已初始化数据：只在含已初始化数据的节（IMAGE_SCN_CNT_INITIALIZED_DATA）
    /// 内按 8 字节对齐查找，不扫描代码节；查找经 1 MiB 有界窗口读取。
    /// </summary>
    private (ulong Count, ulong Table) TypeRegistry()
    {
        var candidates = new List<(ulong Count, ulong Table)>();
        const int Window = 1024 * 1024;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Window + 64);
        try
        {
            foreach (var section in sections.Where(value => (value.Characteristics & 0x40) != 0).OrderBy(value => value.Offset))
            {
                var first = Math.Max(32L, (section.Offset + 7L) & ~7L); var end = Math.Min(section.Offset + (long)section.Size, assembly.Length - 31L);
                for (var start = first; start < end; start += Window)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var stop = Math.Min(end, start + Window); var from = start - 32; var length = (int)(Math.Min(assembly.Length, stop + 24) - from);
                    assembly.Read((int)from, buffer.AsSpan(0, length));
                    for (var at = start; at < stop; at += 8)
                    {
                        var local = (int)(at - from);
                        if (BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(local, 8)) != (ulong)names.Length ||
                            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(local + 16, 8)) != (ulong)names.Length) continue;
                        try
                        {
                            var position = (int)at;
                            var fieldTable = assembly.U64(position + 8); _ = Offset(assembly.U64(position + 24));
                            for (var i = 0; i < 16; i++) { var ptr = Pointer(fieldTable + (ulong)i * 8); if (ptr != 0) _ = Offset(ptr); }
                            var n = assembly.U64(position - 32); var table = assembly.U64(position - 24);
                            if (n is 0 or > 1_000_000) continue;
                            _ = Offset(table, checked((int)n * 8)); candidates.Add((n, table));
                        }
                        catch (Exception error) when (error is InvalidDataException or OverflowException) { }
                    }
                }
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        if (candidates.Count != 1) throw new InvalidDataException("客户端类型注册表无法唯一定位");
        return candidates[0];
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
        var at = Offset(pointer, 12); var data = assembly.U64(at); var bits = assembly.U32(at + 8); var kind = bits >> 16 & 255;
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
        return (kind == 19 ? "T" : "M") + metadata.U16(checked(offset + (int)data * 16 + 12));
    }
    private string Generic(ulong data, int depth)
    {
        var instance = Pointer(data + 8); var count = Pointer(instance); var args = Pointer(instance + 8);
        if (count > 32) throw new InvalidDataException("客户端泛型参数过多");
        return PointerName(Pointer(data), depth) + "<" + string.Join(',', Enumerable.Range(0, (int)count).Select(i => PointerName(Pointer(args + (ulong)i * 8), depth))) + ">";
    }
    private (int Start, int Count) FieldRange(int index)
    {
        var at = definitions + index * 88; var start = metadata.I32(at + 32); var count = metadata.U16(at + 68);
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
            var parent = TypeName(metadata.I32(definitions + index * 88 + 16));
            var list = new List<object>(); var children = new List<string>(); var range = FieldRange(index);
            for (var i = 0; i < range.Count; i++)
            {
                var at = fields + (range.Start + i) * 12; var typ = metadata.I32(at + 4);
                if ((assembly.U32(Offset(TypePointer(typ), 12) + 8) & 0x10) != 0) continue;
                var fieldType = TypeName(typ)!; list.Add(new { name = Text(metadata.I32(at)), type = fieldType }); children.Add(fieldType);
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
                if (!map.TryAdd(metadata.I32(at), metadata.I32(at + 8))) throw new InvalidDataException("客户端协议值无效");
            defaultValues = map;
        }
        if (!defaultValues.TryGetValue(field, out var index)) return null;
        var values = Pair(72);
        if (index < 0 || index >= values.Size) throw new InvalidDataException("客户端协议值无效");
        var start = values.Offset + index; var b = metadata.U8(start); uint encoded;
        if (b < 0x80) encoded = b;
        else if (b < 0xc0) { metadata.Check(start, 2); encoded = (uint)((b & 0x3f) << 8 | metadata.U8(start + 1)); }
        else if (b < 0xe0) { metadata.Check(start, 4); encoded = (uint)((b & 0x1f) << 24 | metadata.U8(start + 1) << 16 | metadata.U8(start + 2) << 8 | metadata.U8(start + 3)); }
        else if (b == 0xf0) encoded = metadata.U32(start + 1);
        else throw new InvalidDataException("客户端协议值压缩格式不支持");
        return (encoded & 1) != 0 ? -checked((int)(encoded >> 1)) - 1 : checked((int)(encoded >> 1));
    }
    /// <summary>协议枚举字段名 → 字段序号（同名字段记 -1），只建一次。</summary>
    private Dictionary<string, int>? protocolFields;
    private uint Opcode(string field)
    {
        if (protocolFields is null)
        {
            var range = FieldRange(ProtocolDefinition()); var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < range.Count; i++)
            {
                var name = Text(metadata.I32(fields + (range.Start + i) * 12));
                if (!map.TryAdd(name, range.Start + i)) map[name] = -1;
            }
            protocolFields = map;
        }
        if (!protocolFields.TryGetValue(field, out var found)) throw new InvalidDataException("客户端缺少协议：" + field);
        if (found < 0) throw new InvalidDataException("客户端协议字段重复");
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
            if ((assembly.U32(Offset(TypePointer(metadata.I32(at + 4)), 12) + 8) & 0x10) == 0) continue;
            var name = Text(metadata.I32(at));
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
        var start = metadata.I32(definition + 36);
        var count = metadata.U16(definition + 64);
        if (start < 0 || (long)start + count > methods.Size / 36) throw new InvalidDataException("客户端方法范围无效");
        var result = new List<InventoryProtocolMethod>();
        for (var i = 0; i < count; i++)
        {
            var at = methods.Offset + (start + i) * 36; var name = Text(metadata.I32(at));
            var paramStart = metadata.I32(at + 16); var paramCount = metadata.U16(at + 34);
            if (paramCount > 32 || paramCount > 0 && (paramStart < 0 || (long)paramStart + paramCount > parameters.Size / 12)) throw new InvalidDataException("客户端参数范围无效");
            result.Add(new(name, interfaceName, Enumerable.Range(0, paramCount).Select(n => TypeName(metadata.I32(parameters.Offset + (paramStart + n) * 12 + 8))
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
            var name = Text(metadata.I32(fields + (range.Start + i) * 12));
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
            var start = metadata.I32(definition + 36);
            var count = metadata.U16(definition + 64);
            if (start < 0 || (long)start + count > methods.Size / 36) throw new InvalidDataException("库存方法范围无效");
            for (var i = 0; i < count; i++)
            {
                var at = methods.Offset + (start + i) * 36; var name = Text(metadata.I32(at));
                if (!opcodes.TryGetValue("REPLICATION_" + group + "_" + name + "_REPL", out var opcode)) continue;
                var paramStart = metadata.I32(at + 16); var paramCount = metadata.U16(at + 34);
                if (paramCount > 32 || paramCount > 0 && (paramStart < 0 || (long)paramStart + paramCount > parameters.Size / 12)) throw new InvalidDataException("库存参数范围无效");
                var args = Enumerable.Range(0, paramCount).Select(n => TypeName(metadata.I32(parameters.Offset + (paramStart + n) * 12 + 8))
                    ?? throw new InvalidDataException("库存参数类型无效")).ToArray();
                result.Add(opcode, new(name, group.EndsWith("Inventory", StringComparison.Ordinal) ? group[..^"Inventory".Length] : group, args));
            }
        }
        return result;
    }
    /// <summary>
    /// 读取库存布局。<paramref name="cached"/> 与当前文件哈希相同时直接返回它；<paramref name="cacheDirectory"/> 不为空时，
    /// 同一 GameAssembly 与 metadata 哈希、同一规则版本的布局从该目录读取（不再映射与解析客户端文件），解析结果写入该目录。
    /// 两种情况下哈希都由本次读取以流式方式核对（同一进程内文件身份未变时复用已算出的哈希）。
    /// </summary>
    public static ClientCaptureLayout Read(string directory, CancellationToken cancellation = default, ClientCaptureLayout? cached = null,
        string? cacheDirectory = null)
    {
        using var files = ClientFiles.Open(directory, cancellation);
        if (cached is not null && cached.AssemblyHash == files.AssemblyHash && cached.MetadataHash == files.MetadataHash) return cached;
        if (cacheDirectory is not null && ClientLayoutCache.Read(cacheDirectory, files.AssemblyHash, files.MetadataHash) is { } stored)
        { files.RequireUnchanged(); return stored; }
        var reader = new ClientLayoutReader(files.Metadata, files.Assembly, cancellation);
        var protocol = new CaptureProtocol(reader.Opcode("CHANNEL_MyCharacterPersonalData_NTF"), reader.Opcode("REPLICATION_GuildMembers_UpdateGuildMemberInfo_REPL"), reader.Opcode("REPLICATION_ConsumableInventory_UpdateConsumableStack_REPL"));
        if (new[] { protocol.Login, protocol.Profile, protocol.Inventory }.Distinct().Count() != 3) throw new InvalidDataException("客户端协议号冲突");
        var opcodes = reader.InventoryOpcodes(); var methods = reader.InventoryMethods(opcodes);
        var layout = new ClientCaptureLayout(files.AssemblyHash, files.MetadataHash, protocol, reader.Schema(methods.Values.SelectMany(m => m.Parameters))) { Opcodes = opcodes, Methods = methods };
        files.RequireUnchanged();
        if (cacheDirectory is not null) ClientLayoutCache.Write(cacheDirectory, layout);
        return layout;
    }
    /// <summary>读取构建核对所需的客户端事实：完整协议枚举、给定根类型的字段闭包与接口方法签名。</summary>
    public static ClientBuildFacts ReadBuildFacts(string directory, IReadOnlyCollection<string> schemaRoots, IReadOnlyCollection<string> interfaces,
        CancellationToken cancellation = default)
    {
        using var files = ClientFiles.Open(directory, cancellation);
        var reader = new ClientLayoutReader(files.Metadata, files.Assembly, cancellation);
        var facts = new ClientBuildFacts(files.AssemblyHash, files.MetadataHash, reader.ProtocolTable(), reader.Schema(schemaRoots, inventoryRoot: false),
            interfaces.ToDictionary(name => name, reader.InterfaceMethods, StringComparer.Ordinal));
        files.RequireUnchanged();
        return facts;
    }

    /// <summary>GameAssembly.dll 与 metadata 的只读共享句柄、流式哈希与按需建立的只读映射；释放时关闭映射与句柄。</summary>
    private sealed class ClientFiles : IDisposable
    {
        private const int AssemblyLimit = 768 * 1024 * 1024, MetadataLimit = 256 * 1024 * 1024;
        private readonly FileStream assemblyLock, metadataLock;
        private readonly (long Length, DateTime Write)[] stamps;
        private MappedClientFile? assembly, metadata;
        public string AssemblyHash { get; }
        public string MetadataHash { get; }
        public MappedClientFile Assembly => assembly ??= new(assemblyLock, AssemblyLimit);
        public MappedClientFile Metadata => metadata ??= new(metadataLock, MetadataLimit);
        private ClientFiles(FileStream assemblyLock, FileStream metadataLock, CancellationToken cancellation)
        {
            this.assemblyLock = assemblyLock; this.metadataLock = metadataLock;
            if (assemblyLock.Length is <= 0 or > AssemblyLimit || metadataLock.Length is <= 0 or > MetadataLimit) throw new InvalidDataException("客户端文件大小不支持");
            stamps = Stamps();
            AssemblyHash = Combat.ClientFileHash.Compute(assemblyLock, cancellation);
            MetadataHash = Combat.ClientFileHash.Compute(metadataLock, cancellation);
        }
        public static ClientFiles Open(string directory, CancellationToken cancellation)
        {
            directory = Path.GetFullPath(directory);
            var assemblyPath = Path.Combine(directory, "GameAssembly.dll");
            var metadataFiles = Directory.EnumerateDirectories(directory, "*_Data", SearchOption.TopDirectoryOnly)
                .Select(d => Path.Combine(d, "il2cpp_data", "Metadata", "global-metadata.dat")).Where(File.Exists).Take(2).ToArray();
            if (metadataFiles.Length != 1) throw new InvalidDataException("未找到唯一的客户端元数据文件，请选择游戏程序所在目录");
            // Hold both files read-only for the entire extraction, preventing mixed-version reads during an update.
            FileStream? assemblyLock = null, metadataLock = null;
            try
            {
                assemblyLock = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                metadataLock = new FileStream(metadataFiles[0], FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                return new ClientFiles(assemblyLock, metadataLock, cancellation);
            }
            catch { metadataLock?.Dispose(); assemblyLock?.Dispose(); throw; }
        }
        private (long, DateTime)[] Stamps() =>
            [(assemblyLock.Length, File.GetLastWriteTimeUtc(assemblyLock.SafeFileHandle)), (metadataLock.Length, File.GetLastWriteTimeUtc(metadataLock.SafeFileHandle))];
        /// <summary>读取前后两个文件的大小与修改时间一致；不一致时视为解析期间客户端发生更新。</summary>
        public void RequireUnchanged()
        {
            if (!stamps.SequenceEqual(Stamps())) throw new InvalidDataException("解析期间客户端发生更新，请等待更新结束");
        }
        public void Dispose()
        {
            metadata?.Dispose(); assembly?.Dispose();
            metadataLock.Dispose(); assemblyLock.Dispose();
        }
    }
}

/// <summary>客户端文件的只读内存映射；所有读取按文件实际长度做越界检查。</summary>
internal sealed class MappedClientFile : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly System.IO.MemoryMappedFiles.MemoryMappedFile map;
    private readonly System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view;
    public int Length { get; }
    public MappedClientFile(FileStream file, int limit)
    {
        if (file.Length <= 0 || file.Length > limit) throw new InvalidDataException("客户端文件大小不支持");
        Length = checked((int)file.Length);
        map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(file, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: true);
        try { view = map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read); }
        catch { map.Dispose(); throw; }
    }
    public void Check(int at, int count)
    {
        if (at < 0 || count < 0 || at > Length - count) throw new InvalidDataException("客户端文件数据越界");
    }
    public byte U8(int at) { Check(at, 1); return view.ReadByte(at); }
    public ushort U16(int at) { Check(at, 2); return view.ReadUInt16(at); }
    public int I32(int at) { Check(at, 4); return view.ReadInt32(at); }
    public uint U32(int at) { Check(at, 4); return view.ReadUInt32(at); }
    public ulong U64(int at) { Check(at, 8); return view.ReadUInt64(at); }
    public void Read(int at, Span<byte> destination)
    {
        Check(at, destination.Length);
        view.SafeMemoryMappedViewHandle.ReadSpan((ulong)(view.PointerOffset + at), destination);
    }
    /// <summary>从 <paramref name="at"/> 读取以 0 结尾的 UTF-8 字符串：至多 <paramref name="available"/> 字节，正文至多 16384 字节。</summary>
    public string Text(int at, int available)
    {
        Span<byte> small = stackalloc byte[256];
        var first = small[..Math.Min(small.Length, available)];
        Read(at, first);
        var end = first.IndexOf((byte)0);
        if (end >= 0) return Utf8.GetString(first[..end]);
        var length = Math.Min(available, 16385);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var span = buffer.AsSpan(0, length); Read(at, span);
            end = span.IndexOf((byte)0);
            if (end < 0 || end > 16384) throw new InvalidDataException("客户端字符串长度无效");
            return Utf8.GetString(span[..end]);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
    public void Dispose() { view.Dispose(); map.Dispose(); }
}

/// <summary>
/// 库存布局缓存：数据根缓存目录中每个客户端构建一个 JSON 文件（按 GameAssembly 与 metadata 哈希前缀命名），内容带完整哈希与
/// <see cref="ClientLayoutReader.LayoutRulesVersion"/>；单个文件至多 8 MiB，目录内至多保留 4 个布局文件，写入时删除最久未写的。
/// 哈希不符、损坏或规则版本不同的文件视为没有缓存。
/// </summary>
public static class ClientLayoutCache
{
    private const long MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumFiles = 4;
    private sealed record Entry(int RulesVersion, string AssemblyHash, string MetadataHash, CaptureProtocol Protocol, JsonElement Schema,
        Dictionary<string, uint> Opcodes, Dictionary<string, InventoryProtocolMethod> Methods);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static string FilePath(string directory, string assembly, string metadata) =>
        Path.Combine(directory, "layout-" + assembly[..16] + "-" + metadata[..16] + ".json");

    public static ClientCaptureLayout? Read(string directory, string assemblyHash, string metadataHash)
    {
        try
        {
            var path = FilePath(directory, assemblyHash, metadataHash);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path), Options);
            if (entry is null || entry.RulesVersion != ClientLayoutReader.LayoutRulesVersion || entry.AssemblyHash != assemblyHash ||
                entry.MetadataHash != metadataHash || entry.Protocol is null || entry.Opcodes is null || entry.Methods is null ||
                entry.Schema.ValueKind != JsonValueKind.Object ||
                entry.Methods.Values.Any(value => value?.Name is null || value.Category is null || value.Parameters is null || value.Parameters.Any(parameter => parameter is null)))
                return null;
            var methods = new Dictionary<uint, InventoryProtocolMethod>();
            foreach (var (key, value) in entry.Methods)
                if (!uint.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var opcode) || !methods.TryAdd(opcode, value))
                    return null;
            return new(assemblyHash, metadataHash, entry.Protocol, entry.Schema)
            { Opcodes = new Dictionary<string, uint>(entry.Opcodes, StringComparer.Ordinal), Methods = methods };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    public static void Write(string directory, ClientCaptureLayout layout)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Entry(ClientLayoutReader.LayoutRulesVersion, layout.AssemblyHash, layout.MetadataHash, layout.Protocol,
                layout.Schema, new(layout.Opcodes, StringComparer.Ordinal),
                layout.Methods.ToDictionary(value => value.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), value => value.Value)), Options);
            if (bytes.Length > MaximumBytes) return;
            Directory.CreateDirectory(directory);
            var path = FilePath(directory, layout.AssemblyHash, layout.MetadataHash); var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            foreach (var stale in new DirectoryInfo(directory).EnumerateFiles("layout-*.json").OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumFiles))
                stale.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
