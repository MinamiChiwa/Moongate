using System.Buffers.Binary;
using System.IO.Compression;

namespace MabiTool.LifeVision.Worker;

/// <summary>
/// 最小 PNG 解码器，只接受识别模板与测试夹具的格式：8 位 RGB（颜色类型 2）、非隔行。其他格式一律拒绝，
/// 不处理调色板、透明度与灰度，也不核对块 CRC（模板由登记文件的 SHA-256 保证内容）。
/// </summary>
internal static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static RgbImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < Signature.Length + 12 || !data[..Signature.Length].SequenceEqual(Signature)) throw new InvalidDataException("不是 PNG 文件");
        var position = Signature.Length;
        var width = 0;
        var height = 0;
        var header = false;
        var ended = false;
        using var compressed = new MemoryStream();
        while (position + 12 <= data.Length && !ended)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var type = data.Slice(position + 4, 4);
            if (length > data.Length - position - 12) throw new InvalidDataException("PNG 块长度越界");
            var body = data.Slice(position + 8, (int)length);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (body.Length != 13) throw new InvalidDataException("PNG 头无效");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]));
                if (body[8] != 8 || body[9] != 2 || body[10] != 0 || body[11] != 0 || body[12] != 0)
                    throw new InvalidDataException("只支持 8 位 RGB 非隔行 PNG");
                if (width <= 0 || height <= 0 || (long)width * height > RgbImage.MaximumPixels) throw new InvalidDataException("PNG 尺寸无效");
                header = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!header) throw new InvalidDataException("PNG 块顺序无效");
                compressed.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8)) ended = true;
            position += 12 + (int)length;
        }
        if (!header || !ended || compressed.Length == 0) throw new InvalidDataException("PNG 缺少必要的块");

        var stride = width * 3;
        var raw = new byte[(stride + 1) * height];
        compressed.Position = 0;
        using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress))
            inflate.ReadExactly(raw);

        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = raw.AsSpan(y * (stride + 1) + 1, stride);
            var row = pixels.AsSpan(y * stride, stride);
            var above = y == 0 ? ReadOnlySpan<byte>.Empty : pixels.AsSpan((y - 1) * stride, stride);
            Unfilter(filter, source, row, above);
        }
        return new RgbImage(width, height, pixels);
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> source, Span<byte> row, ReadOnlySpan<byte> above)
    {
        const int bytesPerPixel = 3;
        for (var i = 0; i < row.Length; i++)
        {
            var left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
            var up = above.IsEmpty ? 0 : above[i];
            var upLeft = i >= bytesPerPixel && !above.IsEmpty ? above[i - bytesPerPixel] : 0;
            row[i] = filter switch
            {
                0 => source[i],
                1 => (byte)(source[i] + left),
                2 => (byte)(source[i] + up),
                3 => (byte)(source[i] + ((left + up) >> 1)),
                4 => (byte)(source[i] + Paeth(left, up, upLeft)),
                _ => throw new InvalidDataException("PNG 过滤类型无效")
            };
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpLeft = Math.Abs(estimate - upLeft);
        return distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft ? left : distanceUp <= distanceUpLeft ? up : upLeft;
    }
}
