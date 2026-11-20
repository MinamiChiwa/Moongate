using System.Buffers.Binary;
using System.Text;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>SerializedFile 中的一个对象：Offset 为对象数据在文件内的绝对偏移。</summary>
public sealed record UnityObjectInfo(long PathId, int ClassId, int TypeIndex, int Offset, int Size);

/// <summary>TypeTree 节点。TypeFlags 第 0 位为数组；MetaFlag 的 0x4000 表示读完后按 4 字节对齐。</summary>
public sealed class UnityTypeTreeNode(string type, string name, int byteSize, byte typeFlags, int metaFlag)
{
    public string Type { get; } = type;
    public string Name { get; } = name;
    public int ByteSize { get; } = byteSize;
    public byte TypeFlags { get; } = typeFlags;
    public int MetaFlag { get; } = metaFlag;
    public List<UnityTypeTreeNode> Children { get; } = [];
    public bool IsArray => (TypeFlags & 1) != 0;
    public bool Aligned => (MetaFlag & 0x4000) != 0;
}

/// <summary>按 TypeTree 读出的对象字段：只收录数组之外的标量、字符串与字节数组，键为以点连接的字段路径（如 m_RD.texture.m_PathID）。</summary>
public sealed class UnityFieldSet(IReadOnlyDictionary<string, UnityFieldSet.Value> values)
{
    public readonly record struct Value(ulong Raw, int Size, string? Text, int BytesOffset, int BytesLength);

    public bool Contains(string path) => values.ContainsKey(path);
    public int Int32(string path) => unchecked((int)(uint)Number(path, 4));
    public uint UInt32(string path) => (uint)Number(path, 4);
    public long Int64(string path) => unchecked((long)Number(path, 8));
    public ulong UInt64(string path) => Number(path, 8);
    public float Single(string path) => BitConverter.Int32BitsToSingle(Int32(path));
    /// <summary>任意宽度（1、2、4、8 字节）的无符号整数；用于不同 Unity 版本宽度不同的字段（如 StreamingInfo.offset）。</summary>
    public ulong Unsigned(string path) => Get(path) is { Text: null, Size: > 0 } value ? value.Raw : throw Missing(path);
    public string Text(string path) => Get(path).Text ?? throw Missing(path);
    public (int Offset, int Length) Bytes(string path) =>
        Get(path) is { Text: null, Size: 0 } value ? (value.BytesOffset, value.BytesLength) : throw Missing(path);

    private ulong Number(string path, int size) => Get(path) is { Text: null } value && value.Size == size ? value.Raw : throw Missing(path);
    private Value Get(string path) => values.TryGetValue(path, out var value) ? value : throw Missing(path);
    private static ClientIconFormatException Missing(string path) => ClientIconFormatException.Unsupported("类型树缺少字段或类型不符：" + path);
}

/// <summary>
/// Unity SerializedFile（格式版本 22，小端，带 TypeTree）读取：解析类型表与对象表，按对象的 TypeTree 逐字段读取。
/// 不带 TypeTree、大端或其他版本按不支持处理；文件、元数据、类型数、节点数、字符串表、对象数、字段访问次数与嵌套深度均有上限。
/// </summary>
public sealed class UnitySerializedFile
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumMetadataBytes = 4 * 1024 * 1024;
    public const int MaximumTypes = 256;
    public const int MaximumTypeTreeNodes = 4096;
    public const int MaximumStringBufferBytes = 1024 * 1024;
    public const int MaximumObjects = 4096;
    public const int MaximumTypeDependencies = 65536;
    public const int MaximumFieldVisits = 262144;
    public const int MaximumDepth = 64;
    public const int SupportedVersion = 22;
    private const int HeaderBytes = 48;

    /// <summary>Unity 内置的公共字符串表；TypeTree 中最高位为 1 的偏移指向这里。</summary>
    private const string CommonStringTable =
        "AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0ColorRGBA\0Component\0data\0deque\0" +
        "double\0dynamic_array\0FastPropertyName\0first\0float\0Font\0GameObject\0Generic Mono\0GradientNEW\0GUID\0GUIStyle\0int\0list\0long long\0" +
        "map\0Matrix4x4f\0MdFour\0MonoBehaviour\0MonoScript\0m_ByteSize\0m_Curve\0m_EditorClassIdentifier\0m_EditorHideFlags\0m_Enabled\0" +
        "m_ExtensionPtr\0m_GameObject\0m_Index\0m_IsArray\0m_IsStatic\0m_MetaFlag\0m_Name\0m_ObjectHideFlags\0m_PrefabInternal\0" +
        "m_PrefabParentObject\0m_Script\0m_StaticEditorFlags\0m_Type\0m_Version\0Object\0pair\0PPtr<Component>\0PPtr<GameObject>\0" +
        "PPtr<Material>\0PPtr<MonoBehaviour>\0PPtr<MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0PPtr<Sprite>\0PPtr<TextAsset>\0PPtr<Texture>\0" +
        "PPtr<Texture2D>\0PPtr<Transform>\0Prefab\0Quaternionf\0Rectf\0RectInt\0RectOffset\0second\0set\0short\0size\0SInt16\0SInt32\0" +
        "SInt64\0SInt8\0staticvector\0string\0TextAsset\0TextMesh\0Texture\0Texture2D\0Transform\0TypelessData\0UInt16\0UInt32\0UInt64\0" +
        "UInt8\0unsigned int\0unsigned long long\0unsigned short\0vector\0Vector2f\0Vector3f\0Vector4f\0m_ScriptingClassIdentifier\0Gradient\0" +
        "Type*\0int2_storage\0int3_storage\0BoundsInt\0m_CorrespondingSourceObject\0m_PrefabInstance\0m_PrefabAsset\0FileSize\0Hash128\0" +
        "RenderingLayerMask\0fixed_array\0EntityId\0LoadableObjectId\0LoadableSceneId\0";
    private static readonly Dictionary<uint, string> CommonStrings = BuildCommonStrings();

    private readonly ReadOnlyMemory<byte> data;
    private readonly UnityTypeTreeNode[] types;

    private UnitySerializedFile(ReadOnlyMemory<byte> data, string unityVersion, UnityTypeTreeNode[] types, UnityObjectInfo[] objects)
    {
        this.data = data;
        this.types = types;
        UnityVersion = unityVersion;
        Objects = objects;
    }

    public string UnityVersion { get; }
    public IReadOnlyList<UnityObjectInfo> Objects { get; }
    public ReadOnlyMemory<byte> Data => data;

    public static UnitySerializedFile Parse(ReadOnlyMemory<byte> file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var span = file.Span;
        if (span.Length > MaximumFileBytes) throw ClientIconFormatException.Invalid("SerializedFile 超过大小预算");
        if (span.Length < HeaderBytes) throw ClientIconFormatException.Invalid("SerializedFile 头部截断");
        var version = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        if (version != SupportedVersion) throw ClientIconFormatException.Unsupported("SerializedFile 版本不支持");
        if (span[16] != 0) throw ClientIconFormatException.Unsupported("SerializedFile 为大端序");
        var metadataSize = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
        var fileSize = BinaryPrimitives.ReadInt64BigEndian(span[24..]);
        var dataOffset = BinaryPrimitives.ReadInt64BigEndian(span[32..]);
        if (fileSize != span.Length) throw ClientIconFormatException.Invalid("SerializedFile 声明长度不符");
        if (metadataSize > MaximumMetadataBytes || dataOffset < HeaderBytes + (long)metadataSize || dataOffset > fileSize)
            throw ClientIconFormatException.Invalid("SerializedFile 元数据范围无效");

        var reader = new LittleReader(span, HeaderBytes, HeaderBytes + (int)metadataSize);
        var unityVersion = reader.CString(64);
        reader.Int32();
        if (!reader.Bool()) throw ClientIconFormatException.Unsupported("SerializedFile 不带 TypeTree");
        var typeCount = reader.Int32();
        if (typeCount is < 0 or > MaximumTypes) throw ClientIconFormatException.Invalid("SerializedFile 类型数量超过预算");
        var types = new UnityTypeTreeNode[typeCount];
        var classIds = new int[typeCount];
        for (var index = 0; index < typeCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            classIds[index] = reader.Int32();
            reader.Bool();
            reader.Int16();
            if (classIds[index] == 114) reader.Skip(16);
            reader.Skip(16);
            types[index] = ReadTypeTree(ref reader);
            var dependencies = reader.Int32();
            if (dependencies is < 0 or > MaximumTypeDependencies) throw ClientIconFormatException.Invalid("SerializedFile 类型依赖超过预算");
            reader.Skip(dependencies * 4);
        }
        var objectCount = reader.Int32();
        if (objectCount is < 0 or > MaximumObjects) throw ClientIconFormatException.Invalid("SerializedFile 对象数量超过预算");
        var objects = new UnityObjectInfo[objectCount];
        for (var index = 0; index < objectCount; index++)
        {
            reader.Align4();
            var pathId = reader.Int64();
            var start = reader.Int64();
            var size = reader.UInt32();
            var typeIndex = reader.Int32();
            if (typeIndex < 0 || typeIndex >= typeCount) throw ClientIconFormatException.Invalid("SerializedFile 对象类型索引越界");
            if (start < 0 || start > fileSize - dataOffset || size > fileSize - dataOffset - start)
                throw ClientIconFormatException.Invalid("SerializedFile 对象范围越界");
            objects[index] = new UnityObjectInfo(pathId, classIds[typeIndex], typeIndex, (int)(dataOffset + start), (int)size);
        }
        return new UnitySerializedFile(file, unityVersion, types, objects);
    }

    /// <summary>按对象的 TypeTree 读取全部字段；读到的字节数必须与对象长度一致。</summary>
    public UnityFieldSet ReadFields(UnityObjectInfo info, CancellationToken cancellationToken = default)
    {
        if (info.TypeIndex < 0 || info.TypeIndex >= types.Length) throw ClientIconFormatException.Invalid("SerializedFile 对象类型索引越界");
        var values = new Dictionary<string, UnityFieldSet.Value>(StringComparer.Ordinal);
        var reader = new FieldReader(data.Span, info.Offset, info.Offset + info.Size, values, cancellationToken);
        foreach (var child in types[info.TypeIndex].Children) reader.Read(child, child.Name, 1);
        if (reader.Offset != info.Offset + info.Size) throw ClientIconFormatException.Invalid("对象长度与类型树不符");
        return new UnityFieldSet(values);
    }

    private static UnityTypeTreeNode ReadTypeTree(ref LittleReader reader)
    {
        var nodeCount = reader.Int32();
        var bufferSize = reader.Int32();
        if (nodeCount is < 1 or > MaximumTypeTreeNodes || bufferSize is < 0 or > MaximumStringBufferBytes)
            throw ClientIconFormatException.Invalid("TypeTree 超过大小预算");
        var nodes = reader.Take(nodeCount * 32);
        var buffer = reader.Take(bufferSize);
        UnityTypeTreeNode? root = null;
        var parents = new List<UnityTypeTreeNode>();
        for (var index = 0; index < nodeCount; index++)
        {
            var raw = nodes.Slice(index * 32, 32);
            var level = raw[2];
            var node = new UnityTypeTreeNode(TypeTreeString(buffer, BinaryPrimitives.ReadUInt32LittleEndian(raw[4..])),
                TypeTreeString(buffer, BinaryPrimitives.ReadUInt32LittleEndian(raw[8..])), BinaryPrimitives.ReadInt32LittleEndian(raw[12..]),
                raw[3], BinaryPrimitives.ReadInt32LittleEndian(raw[20..]));
            if (index == 0)
            {
                if (level != 0) throw ClientIconFormatException.Invalid("TypeTree 根节点层级无效");
                root = node;
            }
            else
            {
                if (level < 1 || level > parents.Count || level > MaximumDepth) throw ClientIconFormatException.Invalid("TypeTree 层级无效");
                parents[level - 1].Children.Add(node);
            }
            if (parents.Count > level) parents.RemoveRange(level, parents.Count - level);
            parents.Add(node);
        }
        return root!;
    }

    private static string TypeTreeString(ReadOnlySpan<byte> buffer, uint offset)
    {
        if ((offset & 0x8000_0000) != 0)
            return CommonStrings.TryGetValue(offset & 0x7fff_ffff, out var common) ? common : "#" + (offset & 0x7fff_ffff);
        if (offset >= (uint)buffer.Length) throw ClientIconFormatException.Invalid("TypeTree 字符串越界");
        var rest = buffer[(int)offset..];
        var end = rest.IndexOf((byte)0);
        if (end < 0) throw ClientIconFormatException.Invalid("TypeTree 字符串截断");
        return Encoding.UTF8.GetString(rest[..end]);
    }

    private static Dictionary<uint, string> BuildCommonStrings()
    {
        var result = new Dictionary<uint, string>();
        uint offset = 0;
        foreach (var value in CommonStringTable.Split('\0')[..^1])
        {
            result[offset] = value;
            offset += (uint)Encoding.ASCII.GetByteCount(value) + 1;
        }
        return result;
    }

    /// <summary>元数据读取；对齐以文件开头为基准。</summary>
    private ref struct LittleReader
    {
        private readonly ReadOnlySpan<byte> data;
        private readonly int end;
        public LittleReader(ReadOnlySpan<byte> data, int start, int end) { this.data = data; Offset = start; this.end = end; }
        public int Offset { get; private set; }

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Offset > end - count) throw ClientIconFormatException.Invalid("SerializedFile 元数据截断");
            var value = data.Slice(Offset, count);
            Offset += count;
            return value;
        }

        public void Skip(int count) => Take(count);
        public bool Bool() => Take(1)[0] != 0;
        public short Int16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public void Align4() => Take((4 - Offset % 4) % 4);

        public string CString(int maximum)
        {
            var rest = data[Offset..end];
            var stop = rest[..Math.Min(rest.Length, maximum + 1)].IndexOf((byte)0);
            if (stop < 0) throw ClientIconFormatException.Invalid("SerializedFile 字符串过长或截断");
            Offset += stop + 1;
            return Encoding.UTF8.GetString(rest[..stop]);
        }
    }

    /// <summary>按 TypeTree 读取对象字段；Path 为空表示处于数组元素内，只跳过不收录。对齐以文件开头为基准。</summary>
    private ref struct FieldReader
    {
        private readonly ReadOnlySpan<byte> data;
        private readonly int end;
        private readonly Dictionary<string, UnityFieldSet.Value> values;
        private readonly CancellationToken cancellationToken;
        private int visits;

        public FieldReader(ReadOnlySpan<byte> data, int start, int end, Dictionary<string, UnityFieldSet.Value> values, CancellationToken cancellationToken)
        {
            this.data = data; Offset = start; this.end = end; this.values = values; this.cancellationToken = cancellationToken;
        }

        public int Offset { get; private set; }

        public void Read(UnityTypeTreeNode node, string? path, int depth)
        {
            if (++visits > MaximumFieldVisits || depth > MaximumDepth) throw ClientIconFormatException.Invalid("对象字段数量或嵌套深度超过预算");
            if ((visits & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var align = node.Aligned;
            if (node.Children.Count == 0)
            {
                if (node.ByteSize is not (1 or 2 or 4 or 8)) throw ClientIconFormatException.Unsupported("类型树叶子节点长度不支持：" + node.Type);
                var raw = Take(node.ByteSize);
                var value = node.ByteSize switch
                {
                    1 => raw[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(raw),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(raw), _ => BinaryPrimitives.ReadUInt64LittleEndian(raw)
                };
                if (path is not null) values[path] = new(value, node.ByteSize, null, 0, 0);
            }
            else if (node.Type == "string")
            {
                var length = Int32();
                if (length < 0) throw ClientIconFormatException.Invalid("字符串长度为负");
                var text = Take(length);
                if (path is not null) values[path] = new(0, 0, Encoding.UTF8.GetString(text), 0, 0);
                align = true;
            }
            else if (node.IsArray) ReadArray(node, path, depth);
            else if (node.Children[0].IsArray)
            {
                align |= node.Children[0].Aligned;
                ReadArray(node.Children[0], path, depth + 1);
            }
            else
                foreach (var child in node.Children) Read(child, path is null ? null : path + "." + child.Name, depth + 1);
            if (align) Take((4 - Offset % 4) % 4);
        }

        private void ReadArray(UnityTypeTreeNode array, string? path, int depth)
        {
            if (array.Children.Count != 2) throw ClientIconFormatException.Invalid("类型树数组结构无效");
            var count = Int32();
            if (count < 0) throw ClientIconFormatException.Invalid("数组长度为负");
            var element = array.Children[1];
            if (element.Children.Count == 0 && element.ByteSize is 1 or 2 or 4 or 8)
            {
                if ((long)count * element.ByteSize > end - Offset) throw ClientIconFormatException.Invalid("数组越过对象末尾");
                var start = Offset;
                Take(count * element.ByteSize);
                if (path is not null && element.ByteSize == 1) values[path] = new(0, 0, null, start, count);
                return;
            }
            // 每个元素至少占 1 字节，元素数不会超过剩余字节数。
            if (count > end - Offset) throw ClientIconFormatException.Invalid("数组越过对象末尾");
            for (var index = 0; index < count; index++) Read(element, null, depth + 1);
        }

        private int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Offset > end - count) throw ClientIconFormatException.Invalid("对象数据截断");
            var value = data.Slice(Offset, count);
            Offset += count;
            return value;
        }
    }
}
