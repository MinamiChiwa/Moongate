using System.Buffers.Binary;
using System.IO.Compression;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>解码后的图像：Rgba 为每像素 4 字节、自上而下逐行排列。</summary>
public sealed record ClientIconBitmap(int Width, int Height, byte[] Rgba);

/// <summary>
/// 从 UnityFS 包解出一个精灵的图像：包内恰好一个 SerializedFile；给出精灵名时取 m_Name 与之相同的唯一 Sprite（元素图标与飘字共用一个包），
/// 不给时包内须恰好一个 Sprite。精灵不属于图集、不紧密打包、不旋转、没有独立透明通道纹理，
/// 矩形覆盖整张纹理；纹理为单张二维 DXT5（BC3）或 RGBA32，像素在对象内或包内 .resS。Unity 纹理自下而上存储，解码后上下翻转。
/// </summary>
public static class ClientIconImage
{
    public const int MaximumTextureSide = 1024;
    public const int TextureFormatRgba32 = 4, TextureFormatDxt5 = 12;
    private const int ClassTexture2D = 28, ClassSprite = 213;

    public static ClientIconBitmap DecodeSpriteBundle(ReadOnlySpan<byte> bundle, string? spriteName = null, CancellationToken cancellationToken = default)
    {
        var content = UnityBundle.Parse(bundle, cancellationToken);
        var serialized = content.Nodes.Where(node => node.IsSerializedFile).ToArray();
        if (serialized.Length != 1) throw ClientIconFormatException.Unsupported("包内 SerializedFile 数量不是 1");
        var file = UnitySerializedFile.Parse(content.Slice(serialized[0]), cancellationToken);
        var sprites = file.Objects.Where(item => item.ClassId == ClassSprite).Select(item => file.ReadFields(item, cancellationToken))
            .Where(fields => spriteName is null || fields.Text("m_Name") == spriteName).ToArray();
        if (sprites.Length != 1) throw ClientIconFormatException.Unsupported(spriteName is null ? "包内精灵数量不是 1" : "包内没有唯一的同名精灵：" + spriteName);
        var sprite = sprites[0];
        if (sprite.Int64("m_SpriteAtlas.m_PathID") != 0) throw ClientIconFormatException.Unsupported("精灵属于图集");
        if (sprite.Int32("m_RD.texture.m_FileID") != 0) throw ClientIconFormatException.Unsupported("精灵纹理位于其他文件");
        if (sprite.Int64("m_RD.alphaTexture.m_PathID") != 0) throw ClientIconFormatException.Unsupported("精灵带独立透明通道纹理");
        var settings = sprite.UInt32("m_RD.settingsRaw");
        if ((settings & 1) != 0) throw ClientIconFormatException.Unsupported("精灵为紧密打包");
        if (((settings >> 2) & 0xf) != 0) throw ClientIconFormatException.Unsupported("精灵带旋转");
        var textureId = sprite.Int64("m_RD.texture.m_PathID");
        var textures = file.Objects.Where(item => item.ClassId == ClassTexture2D && item.PathId == textureId).ToArray();
        if (textures.Length != 1) throw ClientIconFormatException.Invalid("精灵引用的纹理不存在");
        var texture = file.ReadFields(textures[0], cancellationToken);

        var width = texture.Int32("m_Width");
        var height = texture.Int32("m_Height");
        if (width is < 1 or > MaximumTextureSide || height is < 1 or > MaximumTextureSide)
            throw ClientIconFormatException.Unsupported("纹理尺寸超出支持范围");
        if (texture.Int32("m_ImageCount") != 1 || texture.Int32("m_TextureDimension") != 2 || texture.Int32("m_MipCount") < 1)
            throw ClientIconFormatException.Unsupported("纹理不是单张二维纹理");
        if (sprite.Single("m_Rect.x") != 0 || sprite.Single("m_Rect.y") != 0 || sprite.Single("m_Rect.width") != width ||
            sprite.Single("m_Rect.height") != height)
            throw ClientIconFormatException.Unsupported("精灵没有覆盖整张纹理");
        var format = texture.Int32("m_TextureFormat");
        var required = format switch
        {
            TextureFormatDxt5 => (width + 3) / 4 * ((height + 3) / 4) * 16,
            TextureFormatRgba32 => width * height * 4,
            _ => throw ClientIconFormatException.Unsupported("纹理格式不支持：" + format)
        };
        var pixels = Pixels(content, file, texture)[..required];
        cancellationToken.ThrowIfCancellationRequested();
        var rgba = format == TextureFormatDxt5 ? Bc3.Decode(pixels, width, height) : pixels.ToArray();
        FlipRows(rgba, width, height);
        return new ClientIconBitmap(width, height, rgba);

        ReadOnlySpan<byte> Pixels(UnityBundleContent bundleContent, UnitySerializedFile serializedFile, UnityFieldSet fields)
        {
            var (offset, length) = fields.Bytes("image data");
            if (length > 0)
            {
                if (length < required) throw ClientIconFormatException.Invalid("纹理像素数据短于格式要求");
                return serializedFile.Data.Span.Slice(offset, length);
            }
            var path = fields.Text("m_StreamData.path");
            var streamOffset = fields.Unsigned("m_StreamData.offset");
            var streamSize = fields.Unsigned("m_StreamData.size");
            if (!path.StartsWith("archive:/", StringComparison.Ordinal)) throw ClientIconFormatException.Unsupported("纹理像素不在包内");
            var name = path[(path.LastIndexOf('/') + 1)..];
            var nodes = bundleContent.Nodes.Where(node => node.Path == name).ToArray();
            if (nodes.Length != 1) throw ClientIconFormatException.Invalid("纹理像素所在的包内文件不存在");
            if (streamSize < (ulong)required || streamOffset > (ulong)nodes[0].Size || streamSize > (ulong)nodes[0].Size - streamOffset)
                throw ClientIconFormatException.Invalid("纹理像素范围越界或短于格式要求");
            return bundleContent.Slice(nodes[0]).Span.Slice((int)streamOffset, (int)streamSize);
        }
    }

    private static void FlipRows(byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        var row = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            var upper = rgba.AsSpan(top * stride, stride);
            var lower = rgba.AsSpan(bottom * stride, stride);
            upper.CopyTo(row);
            lower.CopyTo(upper);
            row.CopyTo(lower);
        }
    }
}

/// <summary>
/// DXT5（BC3）解码：每 4×4 像素块 16 字节，前 8 字节为透明度（两个端点与 16 个 3 位索引），后 8 字节为颜色
/// （两个 RGB565 端点与 16 个 2 位索引，BC3 的颜色固定为四色插值）。输出按块数据的行序排列。
/// </summary>
public static class Bc3
{
    public static byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height)
    {
        if (width < 1 || height < 1) throw ClientIconFormatException.Invalid("纹理尺寸无效");
        int columns = (width + 3) / 4, rows = (height + 3) / 4;
        if (blocks.Length < columns * rows * 16) throw ClientIconFormatException.Invalid("DXT5 数据短于纹理尺寸");
        var rgba = new byte[width * height * 4];
        Span<byte> alpha = stackalloc byte[8];
        Span<byte> colors = stackalloc byte[16];
        for (var blockY = 0; blockY < rows; blockY++)
        for (var blockX = 0; blockX < columns; blockX++)
        {
            var block = blocks.Slice((blockY * columns + blockX) * 16, 16);
            int a0 = block[0], a1 = block[1];
            alpha[0] = (byte)a0; alpha[1] = (byte)a1;
            if (a0 > a1)
                for (var step = 1; step <= 6; step++) alpha[step + 1] = (byte)(((7 - step) * a0 + step * a1 + 3) / 7);
            else
            {
                for (var step = 1; step <= 4; step++) alpha[step + 1] = (byte)(((5 - step) * a0 + step * a1 + 2) / 5);
                alpha[6] = 0; alpha[7] = 255;
            }
            ulong alphaBits = 0;
            for (var index = 0; index < 6; index++) alphaBits |= (ulong)block[2 + index] << (8 * index);
            Expand(BinaryPrimitives.ReadUInt16LittleEndian(block[8..]), colors[..4]);
            Expand(BinaryPrimitives.ReadUInt16LittleEndian(block[10..]), colors[4..8]);
            for (var channel = 0; channel < 3; channel++)
            {
                colors[8 + channel] = (byte)((2 * colors[channel] + colors[4 + channel] + 1) / 3);
                colors[12 + channel] = (byte)((colors[channel] + 2 * colors[4 + channel] + 1) / 3);
            }
            var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
            for (var pixel = 0; pixel < 16; pixel++)
            {
                int x = blockX * 4 + pixel % 4, y = blockY * 4 + pixel / 4;
                if (x >= width || y >= height) continue;
                var target = (y * width + x) * 4;
                var color = (int)(indices >> (2 * pixel)) & 3;
                rgba[target] = colors[color * 4];
                rgba[target + 1] = colors[color * 4 + 1];
                rgba[target + 2] = colors[color * 4 + 2];
                rgba[target + 3] = alpha[(int)(alphaBits >> (3 * pixel)) & 7];
            }
        }
        return rgba;
    }

    private static void Expand(ushort value, Span<byte> rgb)
    {
        int red = value >> 11, green = (value >> 5) & 63, blue = value & 31;
        rgb[0] = (byte)(red << 3 | red >> 2);
        rgb[1] = (byte)(green << 2 | green >> 4);
        rgb[2] = (byte)(blue << 3 | blue >> 2);
        rgb[3] = 255;
    }
}

/// <summary>最小 PNG 编码：8 位 RGBA、无隔行、每行过滤类型 0，IDAT 为 zlib 压缩。</summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(ClientIconBitmap bitmap)
    {
        var stride = bitmap.Width * 4;
        if (bitmap.Width < 1 || bitmap.Height < 1 || bitmap.Rgba.Length != stride * bitmap.Height)
            throw new ArgumentException("图像尺寸与像素数据不符", nameof(bitmap));
        var scanlines = new byte[(stride + 1) * bitmap.Height];
        for (var row = 0; row < bitmap.Height; row++)
            bitmap.Rgba.AsSpan(row * stride, stride).CopyTo(scanlines.AsSpan(row * (stride + 1) + 1));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(scanlines);
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, bitmap.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], bitmap.Height);
        header[8] = 8; header[9] = 6; header[10] = 0; header[11] = 0; header[12] = 0;
        Chunk(output, "IHDR"u8, header);
        Chunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        var crc = Crc(Crc(0xffff_ffff, type), data) ^ 0xffff_ffff;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb8_8320 ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
}
