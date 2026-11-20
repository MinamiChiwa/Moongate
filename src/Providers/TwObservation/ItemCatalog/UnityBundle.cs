using System.Buffers.Binary;
using System.Text;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 客户端图标资源不符合已核对格式时的受控异常。Code 为 <see cref="ClientIconReasons"/> 中的原因码：
/// 结构损坏、越界或超出读取预算为 resourceInvalid，结构完好但属于未支持的变体（压缩方式、版本、纹理格式、打包方式）为 formatUnsupported。
/// </summary>
public sealed class ClientIconFormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    internal static ClientIconFormatException Invalid(string message) => new(ClientIconReasons.ResourceInvalid, message);
    internal static ClientIconFormatException Unsupported(string message) => new(ClientIconReasons.FormatUnsupported, message);
}

/// <summary>UnityFS 包内的一个文件：Data 中 [Offset, Offset + Size) 的字节，Flags 第 2 位（4）为 SerializedFile。</summary>
public sealed record UnityBundleNode(string Path, int Offset, int Size, uint Flags)
{
    public bool IsSerializedFile => (Flags & 4) != 0;
}

/// <summary>解压后的 UnityFS 包：全部数据块按顺序拼接为 Data，Nodes 指向其中的各文件。</summary>
public sealed record UnityBundleContent(byte[] Data, IReadOnlyList<UnityBundleNode> Nodes)
{
    public ReadOnlyMemory<byte> Slice(UnityBundleNode node) => Data.AsMemory(node.Offset, node.Size);
}

/// <summary>
/// UnityFS（格式版本 6–8）读取：头部与块信息为大端；块信息与数据块支持不压缩、LZ4 与 LZ4HC（LZMA 等按不支持处理）。
/// 输入按不可信数据处理：包、块信息、块数、文件数、解压总量与单块解压比例都有上限，所有偏移先校验再使用。
/// </summary>
public static class UnityBundle
{
    public const int MaximumBundleBytes = 8 * 1024 * 1024;
    public const int MaximumBlocksInfoBytes = 1024 * 1024;
    public const int MaximumBlocks = 4096;
    public const int MaximumNodes = 64;
    public const int MaximumDataBytes = 32 * 1024 * 1024;
    /// <summary>LZ4 的理论最大压缩比约 255:1；声明的解压长度超过压缩长度的该倍数（另加 64 字节）时拒绝。</summary>
    public const int MaximumExpansionRatio = 256;
    private const int CompressionNone = 0, CompressionLzma = 1, CompressionLz4 = 2, CompressionLz4Hc = 3;
    private const uint BlocksInfoAtEnd = 0x80, BlockInfoNeedPaddingAtStart = 0x200;

    public static UnityBundleContent Parse(ReadOnlySpan<byte> bundle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bundle.Length > MaximumBundleBytes) throw ClientIconFormatException.Invalid("UnityFS 包超过大小预算");
        var reader = new BigEndianReader(bundle);
        var signature = reader.CString(16);
        if (signature != "UnityFS") throw ClientIconFormatException.Unsupported("资源不是 UnityFS 包");
        var version = reader.UInt32();
        if (version is < 6 or > 8) throw ClientIconFormatException.Unsupported("UnityFS 格式版本不支持");
        reader.CString(64);
        reader.CString(64);
        var declaredSize = reader.Int64();
        if (declaredSize != bundle.Length) throw ClientIconFormatException.Invalid("UnityFS 声明长度与条目长度不符");
        var compressedInfo = reader.UInt32();
        var uncompressedInfo = reader.UInt32();
        var flags = reader.UInt32();
        if (version >= 7) reader.Align(16);
        if (uncompressedInfo > MaximumBlocksInfoBytes || compressedInfo > (uint)bundle.Length)
            throw ClientIconFormatException.Invalid("UnityFS 块信息超过大小预算");

        int infoStart;
        if ((flags & BlocksInfoAtEnd) != 0) infoStart = bundle.Length - (int)compressedInfo;
        else { infoStart = reader.Offset; reader.Skip((int)compressedInfo); }
        if (infoStart < reader.Offset && (flags & BlocksInfoAtEnd) != 0 || infoStart < 0)
            throw ClientIconFormatException.Invalid("UnityFS 块信息越界");
        var info = new byte[uncompressedInfo];
        Decompress((int)(flags & 0x3f), bundle.Slice(infoStart, (int)compressedInfo), info);
        if ((flags & BlockInfoNeedPaddingAtStart) != 0) reader.Align(16);

        var blocksReader = new BigEndianReader(info);
        blocksReader.Skip(16);
        var blockCount = blocksReader.Int32();
        if (blockCount is < 0 or > MaximumBlocks) throw ClientIconFormatException.Invalid("UnityFS 块数量超过预算");
        var blocks = new (int Uncompressed, int Compressed, int Compression)[blockCount];
        long totalUncompressed = 0, totalCompressed = 0;
        for (var index = 0; index < blockCount; index++)
        {
            var uncompressed = blocksReader.UInt32();
            var compressed = blocksReader.UInt32();
            var compression = blocksReader.UInt16() & 0x3f;
            totalUncompressed += uncompressed;
            totalCompressed += compressed;
            if (totalUncompressed > MaximumDataBytes || totalCompressed > bundle.Length)
                throw ClientIconFormatException.Invalid("UnityFS 数据块超过大小预算");
            if (compression != CompressionNone && uncompressed > (ulong)compressed * MaximumExpansionRatio + 64)
                throw ClientIconFormatException.Invalid("UnityFS 数据块解压比例超过预算");
            blocks[index] = ((int)uncompressed, (int)compressed, compression);
        }
        var nodeCount = blocksReader.Int32();
        if (nodeCount is < 1 or > MaximumNodes) throw ClientIconFormatException.Invalid("UnityFS 文件数量超过预算");
        var nodes = new UnityBundleNode[nodeCount];
        for (var index = 0; index < nodeCount; index++)
        {
            var offset = blocksReader.Int64();
            var size = blocksReader.Int64();
            var nodeFlags = blocksReader.UInt32();
            var path = blocksReader.CString(256);
            if (offset < 0 || size < 0 || offset > totalUncompressed - size)
                throw ClientIconFormatException.Invalid("UnityFS 文件范围越界");
            nodes[index] = new UnityBundleNode(path, (int)offset, (int)size, nodeFlags);
        }

        var data = new byte[totalUncompressed];
        var source = reader.Offset;
        var target = 0;
        foreach (var (uncompressed, compressed, compression) in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source > bundle.Length - compressed) throw ClientIconFormatException.Invalid("UnityFS 数据块截断");
            Decompress(compression, bundle.Slice(source, compressed), data.AsSpan(target, uncompressed));
            source += compressed;
            target += uncompressed;
        }
        return new UnityBundleContent(data, nodes);
    }

    private static void Decompress(int compression, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        switch (compression)
        {
            case CompressionNone:
                if (source.Length != destination.Length) throw ClientIconFormatException.Invalid("UnityFS 未压缩块长度不符");
                source.CopyTo(destination);
                break;
            case CompressionLz4 or CompressionLz4Hc:
                Lz4Block.Decode(source, destination);
                break;
            case CompressionLzma:
                throw ClientIconFormatException.Unsupported("UnityFS 使用 LZMA 压缩");
            default:
                throw ClientIconFormatException.Unsupported("UnityFS 压缩方式未知");
        }
    }

    private ref struct BigEndianReader
    {
        private readonly ReadOnlySpan<byte> data;
        public BigEndianReader(ReadOnlySpan<byte> data) => this.data = data;
        public int Offset { get; private set; }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Offset > data.Length - count) throw ClientIconFormatException.Invalid("UnityFS 头部截断");
            var value = data.Slice(Offset, count);
            Offset += count;
            return value;
        }

        public void Skip(int count) => Take(count);
        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public int Int32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
        public long Int64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

        public void Align(int alignment)
        {
            var padding = (alignment - Offset % alignment) % alignment;
            Take(padding);
        }

        public string CString(int maximum)
        {
            var rest = data[Offset..];
            var end = rest[..Math.Min(rest.Length, maximum + 1)].IndexOf((byte)0);
            if (end < 0) throw ClientIconFormatException.Invalid("UnityFS 字符串过长或截断");
            var value = Encoding.UTF8.GetString(rest[..end]);
            Offset += end + 1;
            return value;
        }
    }
}

/// <summary>
/// LZ4 块格式解码：每个序列为标记字节（高 4 位字面量长度、低 4 位匹配长度减 4，取 15 时以后续字节累加）、字面量、
/// 2 字节小端回溯距离；最后一个序列只有字面量。输出必须恰好填满目标，任何越界、零距离或回溯超出已输出范围都按损坏处理。
/// </summary>
public static class Lz4Block
{
    public static void Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int input = 0, output = 0;
        while (true)
        {
            if (input >= source.Length) throw ClientIconFormatException.Invalid("LZ4 数据截断");
            var token = source[input++];
            var literal = Length(token >> 4, source, ref input, destination.Length);
            if (literal > source.Length - input || literal > destination.Length - output)
                throw ClientIconFormatException.Invalid("LZ4 字面量越界");
            source.Slice(input, literal).CopyTo(destination[output..]);
            input += literal;
            output += literal;
            if (input == source.Length) break;
            if (input > source.Length - 2) throw ClientIconFormatException.Invalid("LZ4 回溯距离截断");
            var distance = source[input] | source[input + 1] << 8;
            input += 2;
            if (distance == 0 || distance > output) throw ClientIconFormatException.Invalid("LZ4 回溯距离无效");
            var match = Length(token & 15, source, ref input, destination.Length) + 4;
            if (match > destination.Length - output) throw ClientIconFormatException.Invalid("LZ4 匹配长度越界");
            var from = output - distance;
            if (distance >= match) destination.Slice(from, match).CopyTo(destination[output..]);
            else for (var index = 0; index < match; index++) destination[output + index] = destination[from + index];
            output += match;
        }
        if (output != destination.Length) throw ClientIconFormatException.Invalid("LZ4 解压长度与声明不符");
    }

    private static int Length(int initial, ReadOnlySpan<byte> source, ref int input, int limit)
    {
        var length = initial;
        if (initial != 15) return length;
        while (true)
        {
            if (input >= source.Length) throw ClientIconFormatException.Invalid("LZ4 长度截断");
            var next = source[input++];
            length += next;
            if (length > limit) throw ClientIconFormatException.Invalid("LZ4 长度越界");
            if (next != 255) return length;
        }
    }
}
