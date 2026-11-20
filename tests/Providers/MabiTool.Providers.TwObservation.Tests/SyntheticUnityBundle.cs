using System.Buffers.Binary;
using System.Text;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>
/// 合成单精灵 UnityFS 包：UnityFS 8（块信息 LZ4HC、第一个数据块 LZ4HC、第二个不压缩）内含一个 SerializedFile v22
/// （带 TypeTree：Texture2D 与 Sprite 两个类型，字段按客户端 2021.3 的布局取用到的子集）与 .resS。像素为人工构造的 DXT5 块或 RGBA32 字节。
/// </summary>
internal static class SyntheticBundle
{
    public sealed record Options
    {
        public int Format { get; init; } = 12;
        public int Width { get; init; } = 8;
        public int Height { get; init; } = 8;
        public bool Streamed { get; init; } = true;
        public uint SettingsRaw { get; init; } = 64;
        public float? RectWidth { get; init; }
        public long AtlasPathId { get; init; }
        public int InfoCompression { get; init; } = 3;
        public uint? DeclaredFirstBlockBytes { get; init; }
        public ulong StreamOffset { get; init; }
        public int ExtraTextureBytes { get; init; }
        /// <summary>同一包内另一个精灵的名称（模拟元素图标与飘字共用一个包）；为空时只有名为 icon 的一个精灵。</summary>
        public string? SecondSpriteName { get; init; }
    }

    private const long TexturePathId = 1234567890123, SpritePathId = -77;
    private const string Cab = "CAB-0123456789abcdef0123456789abcdef";
    /// <summary>Unity 公共字符串表中的偏移（与 Unity 内置表独立核对）。</summary>
    private static readonly Dictionary<string, uint> Common = new(StringComparer.Ordinal)
    {
        ["Array"] = 49, ["Base"] = 55, ["int"] = 222, ["string"] = 840, ["data"] = 106, ["size"] = 795, ["float"] = 161, ["char"] = 81,
        ["UInt8"] = 928, ["SInt64"] = 814, ["TypelessData"] = 894, ["unsigned int"] = 934, ["UInt64"] = 921, ["m_Name"] = 427, ["vector"] = 981
    };

    public static byte[] Rgba32Pixels(int width, int height) => [.. Enumerable.Range(0, width * height * 4).Select(index => (byte)(index * 7 + 3))];

    /// <summary>8×8 DXT5：四个块颜色端点为红（RGB565 0xF800）与蓝（0x001F），透明度全 255，颜色索引依次全 0、全 1、全 2、全 3。</summary>
    private static byte[] Dxt5Pixels() =>
        [.. new uint[] { 0, 0x5555_5555, 0xaaaa_aaaa, 0xffff_ffff }.SelectMany(indices =>
            new byte[] { 255, 255, 0, 0, 0, 0, 0, 0, 0x00, 0xf8, 0x1f, 0x00 }.Concat(BitConverter.GetBytes(indices)))];

    public static byte[] Build(Options options)
    {
        var pixels = options.Format == 4 ? Rgba32Pixels(options.Width, options.Height) : Dxt5Pixels();
        var texture = new Writer();
        texture.String("icon"); texture.Int32(options.Width); texture.Int32(options.Height); texture.Int32(options.Format); texture.Int32(1);
        texture.Byte(0); texture.Align4(); texture.Int32(1); texture.Int32(2);
        texture.Int32(0); texture.Align4();
        if (options.Streamed)
        {
            texture.Int32(0); texture.Align4();
            texture.UInt64(options.StreamOffset); texture.UInt32((uint)pixels.Length); texture.String("archive:/" + Cab + "/" + Cab + ".resS");
        }
        else
        {
            texture.Int32(pixels.Length); texture.Bytes(pixels); texture.Align4();
            texture.UInt64(0); texture.UInt32(0); texture.String("");
        }
        texture.Bytes(new byte[options.ExtraTextureBytes]);

        byte[] Sprite(string name, float rectWidth, uint settings)
        {
            var sprite = new Writer();
            sprite.String(name);
            sprite.Float(0); sprite.Float(0); sprite.Float(rectWidth); sprite.Float(options.Height);
            sprite.Int32(1); sprite.String("tag"); sprite.Align4();
            sprite.Int32(0); sprite.Int64(options.AtlasPathId); sprite.Align4();
            sprite.Int32(0); sprite.Int64(TexturePathId); sprite.Int32(0); sprite.Int64(0); sprite.UInt32(settings); sprite.Align4();
            return sprite.ToArray();
        }
        (long, int, byte[])[] objects = [(TexturePathId, 0, texture.ToArray()), (SpritePathId, 1, Sprite("icon", options.RectWidth ?? options.Width, options.SettingsRaw))];
        // 另一个精灵带旋转：只有按名称选中 icon 时才能成功解码。
        if (options.SecondSpriteName is { } second) objects = [.. objects, (SpritePathId - 1, 1, Sprite(second, options.Width, 64 | 4 << 2))];
        var file = SerializedFile([(28, TextureTree()), (213, SpriteTree())], objects);
        return UnityFs(file, options.Streamed ? pixels : [], options);
    }

    private static (int Level, string Type, string Name, int Size, byte Flags, int Meta)[] StringNode(int level, string name) =>
        [(level, "string", name, -1, 0, 0x8000), (level + 1, "Array", "Array", -1, 1, 0x4001), (level + 2, "int", "size", 4, 0, 1), (level + 2, "char", "data", 1, 0, 1)];

    private static (int, string, string, int, byte, int)[] TextureTree() =>
    [
        (0, "Texture2D", "Base", -1, 0, 0x8000), .. StringNode(1, "m_Name"),
        (1, "int", "m_Width", 4, 0, 0x10), (1, "int", "m_Height", 4, 0, 0x10), (1, "int", "m_TextureFormat", 4, 0, 1), (1, "int", "m_MipCount", 4, 0, 0x10),
        (1, "bool", "m_IsReadable", 1, 0, 0x4000), (1, "int", "m_ImageCount", 4, 0, 0x10), (1, "int", "m_TextureDimension", 4, 0, 1),
        (1, "vector", "m_PlatformBlob", -1, 0, 0x8000), (2, "Array", "Array", -1, 1, 0x4000), (3, "int", "size", 4, 0, 0), (3, "UInt8", "data", 1, 0, 0),
        (1, "TypelessData", "image data", -1, 1, 0x4001), (2, "int", "size", 4, 0, 1), (2, "UInt8", "data", 1, 0, 1),
        (1, "StreamingInfo", "m_StreamData", -1, 0, 0x8000), (2, "UInt64", "offset", 8, 0, 0), (2, "unsigned int", "size", 4, 0, 0), .. StringNode(2, "path")
    ];

    private static (int, string, string, int, byte, int)[] SpriteTree() =>
    [
        (0, "Sprite", "Base", -1, 0, 0x8000), .. StringNode(1, "m_Name"),
        (1, "Rectf", "m_Rect", 16, 0, 0), (2, "float", "x", 4, 0, 0), (2, "float", "y", 4, 0, 0), (2, "float", "width", 4, 0, 0), (2, "float", "height", 4, 0, 0),
        (1, "vector", "m_AtlasTags", -1, 0, 0x8000), (2, "Array", "Array", -1, 1, 0xc000), (3, "int", "size", 4, 0, 0), .. StringNode(3, "data"),
        (1, "PPtr<SpriteAtlas>", "m_SpriteAtlas", 12, 0, 0x4000), (2, "int", "m_FileID", 4, 0, 0x800001), (2, "SInt64", "m_PathID", 8, 0, 0x800001),
        (1, "SpriteRenderData", "m_RD", -1, 0, 0xc000),
        (2, "PPtr<Texture2D>", "texture", 12, 0, 0), (3, "int", "m_FileID", 4, 0, 0), (3, "SInt64", "m_PathID", 8, 0, 0),
        (2, "PPtr<Texture2D>", "alphaTexture", 12, 0, 0), (3, "int", "m_FileID", 4, 0, 0), (3, "SInt64", "m_PathID", 8, 0, 0),
        (2, "unsigned int", "settingsRaw", 4, 0, 0)
    ];

    private static byte[] SerializedFile((int ClassId, (int Level, string Type, string Name, int Size, byte Flags, int Meta)[] Tree)[] types,
        (long PathId, int Type, byte[] Data)[] objects)
    {
        var metadata = new Writer();
        metadata.CString("2021.3.51f1"); metadata.Int32(19); metadata.Byte(1); metadata.Int32(types.Length);
        foreach (var (classId, tree) in types)
        {
            metadata.Int32(classId); metadata.Byte(0); metadata.Bytes([0xff, 0xff]); metadata.Bytes(new byte[16]);
            var strings = new List<byte>();
            uint Offset(string value)
            {
                if (Common.TryGetValue(value, out var common)) return 0x8000_0000 | common;
                var offset = (uint)strings.Count; strings.AddRange(Encoding.UTF8.GetBytes(value)); strings.Add(0); return offset;
            }
            var nodes = new Writer();
            foreach (var (level, type, name, size, flags, metaFlag) in tree)
            {
                nodes.Bytes([1, 0, (byte)level, flags]); nodes.UInt32(Offset(type)); nodes.UInt32(Offset(name));
                nodes.Int32(size); nodes.Int32(0); nodes.Int32(metaFlag); nodes.UInt64(0);
            }
            metadata.Int32(tree.Length); metadata.Int32(strings.Count); metadata.Bytes(nodes.ToArray()); metadata.Bytes([.. strings]);
            metadata.Int32(0);
        }
        metadata.Int32(objects.Length);
        long start = 0;
        var placed = new List<long>();
        foreach (var (pathId, type, data) in objects)
        {
            metadata.Align4(); metadata.Int64(pathId); metadata.Int64(start); metadata.UInt32((uint)data.Length); metadata.Int32(type);
            placed.Add(start);
            start = (start + data.Length + 7) / 8 * 8;
        }
        metadata.Int32(0); metadata.Int32(0); metadata.Int32(0); metadata.CString("");
        var meta = metadata.ToArray();
        var dataOffset = (48 + meta.Length + 15) / 16 * 16;
        var file = new byte[dataOffset + placed[^1] + objects[^1].Data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), 22);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), (uint)meta.Length);
        BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(24), file.Length);
        BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(32), dataOffset);
        meta.CopyTo(file, 48);
        for (var index = 0; index < objects.Length; index++) objects[index].Data.CopyTo(file, dataOffset + placed[index]);
        return file;
    }

    private static byte[] UnityFs(byte[] file, byte[] resource, Options options)
    {
        byte[] data = [.. file, .. resource];
        var half = data.Length / 2;
        var first = Lz4Literals(data[..half]);
        var info = new BigWriter();
        info.Bytes(new byte[16]); info.Int32(2);
        info.UInt32(options.DeclaredFirstBlockBytes ?? (uint)half); info.UInt32((uint)first.Length); info.UInt16(3);
        info.UInt32((uint)(data.Length - half)); info.UInt32((uint)(data.Length - half)); info.UInt16(0);
        info.Int32(2);
        info.Int64(0); info.Int64(file.Length); info.UInt32(4); info.CString(Cab);
        info.Int64(file.Length); info.Int64(resource.Length); info.UInt32(0); info.CString(Cab + ".resS");
        var blocksInfo = info.ToArray();
        var storedInfo = options.InfoCompression == 3 ? Lz4Literals(blocksInfo) : blocksInfo;

        var bundle = new BigWriter();
        bundle.CString("UnityFS"); bundle.UInt32(8); bundle.CString("5.x.x"); bundle.CString("2021.3.51f1");
        var sizeAt = bundle.Length; bundle.Int64(0);
        bundle.UInt32((uint)storedInfo.Length); bundle.UInt32((uint)blocksInfo.Length); bundle.UInt32(0x240 | (uint)options.InfoCompression);
        bundle.Align16(); bundle.Bytes(storedInfo); bundle.Align16();
        bundle.Bytes(first); bundle.Bytes(data[half..]);
        var result = bundle.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(sizeAt), result.Length);
        return result;
    }

    /// <summary>只含一个字面量序列的 LZ4 块（合法的 LZ4 块格式）。</summary>
    private static byte[] Lz4Literals(byte[] input)
    {
        var output = new List<byte>();
        if (input.Length < 15) output.Add((byte)(input.Length << 4));
        else
        {
            output.Add(0xf0);
            var rest = input.Length - 15;
            for (; rest >= 255; rest -= 255) output.Add(255);
            output.Add((byte)rest);
        }
        output.AddRange(input);
        return [.. output];
    }

    private sealed class Writer
    {
        private readonly List<byte> bytes = [];
        public void Byte(byte value) => bytes.Add(value);
        public void Bytes(byte[] value) => bytes.AddRange(value);
        public void Int32(int value) => bytes.AddRange(BitConverter.GetBytes(value));
        public void UInt32(uint value) => bytes.AddRange(BitConverter.GetBytes(value));
        public void Int64(long value) => bytes.AddRange(BitConverter.GetBytes(value));
        public void UInt64(ulong value) => bytes.AddRange(BitConverter.GetBytes(value));
        public void Float(float value) => bytes.AddRange(BitConverter.GetBytes(value));
        public void CString(string value) { bytes.AddRange(Encoding.UTF8.GetBytes(value)); bytes.Add(0); }
        public void String(string value) { var text = Encoding.UTF8.GetBytes(value); Int32(text.Length); bytes.AddRange(text); Align4(); }
        public void Align4() { while (bytes.Count % 4 != 0) bytes.Add(0); }
        public byte[] ToArray() => [.. bytes];
    }

    private sealed class BigWriter
    {
        private readonly List<byte> bytes = [];
        public int Length => bytes.Count;
        public void Bytes(byte[] value) => bytes.AddRange(value);
        public void UInt16(ushort value) { Span<byte> buffer = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(buffer, value); bytes.AddRange(buffer.ToArray()); }
        public void Int32(int value) { Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(buffer, value); bytes.AddRange(buffer.ToArray()); }
        public void UInt32(uint value) { Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(buffer, value); bytes.AddRange(buffer.ToArray()); }
        public void Int64(long value) { Span<byte> buffer = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(buffer, value); bytes.AddRange(buffer.ToArray()); }
        public void CString(string value) { bytes.AddRange(Encoding.UTF8.GetBytes(value)); bytes.Add(0); }
        public void Align16() { while (bytes.Count % 16 != 0) bytes.Add(0); }
        public byte[] ToArray() => [.. bytes];
    }
}
