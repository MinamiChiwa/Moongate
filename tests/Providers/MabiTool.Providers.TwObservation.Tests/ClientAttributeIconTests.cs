using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Xunit;
using Xunit.Abstractions;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>
/// 用代码合成的最小 UnityFS 包（一个 SerializedFile v22，带 TypeTree，一张纹理与一个精灵）验证各层解析、解码与拒绝原因；
/// 不使用任何客户端导出的图像或字节。
/// </summary>
public sealed class ClientAttributeIconTests(ITestOutputHelper output)
{
    [Fact]
    public void StreamedDxt5SpriteDecodesToTopDownRgbaAndPng()
    {
        var bitmap = ClientIconImage.DecodeSpriteBundle(SyntheticBundle.Build(new()));
        Assert.Equal((8, 8), (bitmap.Width, bitmap.Height));
        // 数据第 0 行块（红、蓝）在图像底部；第 1 行块（三分之二红、三分之二蓝）翻到顶部。
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, Pixel(bitmap, 0, 0));
        Assert.Equal(new byte[] { 85, 0, 170, 255 }, Pixel(bitmap, 7, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(bitmap, 0, 7));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(bitmap, 7, 7));
        var (width, height, rgba) = DecodePng(PngEncoder.Encode(bitmap));
        Assert.Equal((8, 8), (width, height));
        Assert.Equal(bitmap.Rgba, rgba);
    }

    [Fact]
    public void InlineRgba32SpriteIsFlippedVertically()
    {
        var bitmap = ClientIconImage.DecodeSpriteBundle(SyntheticBundle.Build(new() { Format = ClientIconImage.TextureFormatRgba32, Width = 4, Height = 4, Streamed = false }));
        var pixels = SyntheticBundle.Rgba32Pixels(4, 4);
        for (var row = 0; row < 4; row++)
            Assert.Equal(pixels.AsSpan((3 - row) * 16, 16).ToArray(), bitmap.Rgba.AsSpan(row * 16, 16).ToArray());
    }

    [Fact]
    public void SharedBundleIsDecodedBySpriteName()
    {
        var bundle = SyntheticBundle.Build(new() { SecondSpriteName = "font_other" });
        Assert.Equal(8, ClientIconImage.DecodeSpriteBundle(bundle, "icon").Width);
        Assert.Equal(ClientIconReasons.FormatUnsupported, Assert.Throws<ClientIconFormatException>(() => ClientIconImage.DecodeSpriteBundle(bundle)).Code);
        Assert.Equal(ClientIconReasons.FormatUnsupported, Assert.Throws<ClientIconFormatException>(() => ClientIconImage.DecodeSpriteBundle(bundle, "missing")).Code);
    }

    [Theory]
    [InlineData("rotated", ClientIconReasons.FormatUnsupported)]
    [InlineData("packed", ClientIconReasons.FormatUnsupported)]
    [InlineData("partialRect", ClientIconReasons.FormatUnsupported)]
    [InlineData("atlas", ClientIconReasons.FormatUnsupported)]
    [InlineData("dxt1", ClientIconReasons.FormatUnsupported)]
    [InlineData("lzma", ClientIconReasons.FormatUnsupported)]
    [InlineData("declaredSize", ClientIconReasons.ResourceInvalid)]
    [InlineData("truncated", ClientIconReasons.ResourceInvalid)]
    [InlineData("expansion", ClientIconReasons.ResourceInvalid)]
    [InlineData("streamRange", ClientIconReasons.ResourceInvalid)]
    [InlineData("objectSize", ClientIconReasons.ResourceInvalid)]
    public void UnsupportedOrDamagedBundlesAreRejectedWithReason(string variant, string reason)
    {
        var options = variant switch
        {
            "rotated" => new SyntheticBundle.Options { SettingsRaw = 64 | 4 << 2 },
            "packed" => new SyntheticBundle.Options { SettingsRaw = 65 },
            "partialRect" => new SyntheticBundle.Options { RectWidth = 6 },
            "atlas" => new SyntheticBundle.Options { AtlasPathId = 99 },
            "dxt1" => new SyntheticBundle.Options { Format = 10 },
            "lzma" => new SyntheticBundle.Options { InfoCompression = 1 },
            "expansion" => new SyntheticBundle.Options { DeclaredFirstBlockBytes = 1_000_000 },
            "streamRange" => new SyntheticBundle.Options { StreamOffset = 1 << 20 },
            "objectSize" => new SyntheticBundle.Options { ExtraTextureBytes = 4 },
            _ => new SyntheticBundle.Options()
        };
        var bundle = SyntheticBundle.Build(options);
        if (variant == "declaredSize") bundle = [.. bundle, 0];
        if (variant == "truncated") bundle = bundle[..^9];
        var error = Assert.Throws<ClientIconFormatException>(() => ClientIconImage.DecodeSpriteBundle(bundle));
        Assert.Equal(reason, error.Code);
    }

    [Fact]
    public void Lz4BlockHandlesOverlappingMatchesAndRejectsBadReferences()
    {
        byte[] block = [0x35, (byte)'a', (byte)'b', (byte)'c', 3, 0, 0x10, (byte)'x'];
        var decoded = new byte[13];
        Lz4Block.Decode(block, decoded);
        Assert.Equal("abcabcabcabcx", Encoding.ASCII.GetString(decoded));
        foreach (var broken in new byte[][] { [0x35, 97, 98, 99, 0, 0, 0x10, 120], [0x35, 97, 98, 99, 9, 0, 0x10, 120], [0x35, 97, 98, 99, 3], [0xf0, 255] })
            Assert.Equal(ClientIconReasons.ResourceInvalid, Assert.Throws<ClientIconFormatException>(() => Lz4Block.Decode(broken, new byte[13])).Code);
        Assert.Throws<ClientIconFormatException>(() => Lz4Block.Decode(block, new byte[12]));
        Assert.Throws<ClientIconFormatException>(() => Lz4Block.Decode(block, new byte[14]));
    }

    [Fact]
    public void Bc3DecodesBothAlphaModesAndFourColorPalette()
    {
        // 第一块 a0 > a1（八级透明度）；第二块 a0 ≤ a1（六级加 0 与 255）。像素 0 取透明度索引 2、颜色索引 1；像素 1 取透明度索引 7；像素 15 取颜色索引 3。
        byte[] Block(byte a0, byte a1) => [a0, a1, 2 | 7 << 3, 0, 0, 0, 0, 0, 0x00, 0xf8, 0xe0, 0x07, 1, 0, 0, 0xc0];
        var rgba = Bc3.Decode([.. Block(200, 100), .. Block(0, 255)], 8, 4);
        byte[] At(int x, int y) => rgba.AsSpan((y * 8 + x) * 4, 4).ToArray();
        Assert.Equal(new byte[] { 0, 255, 0, 186 }, At(0, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 114 }, At(1, 0));
        Assert.Equal(new byte[] { 85, 170, 0, 200 }, At(3, 3));
        Assert.Equal(new byte[] { 0, 255, 0, 51 }, At(4, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(5, 0));
        Assert.Equal(0, At(6, 0)[3]);
    }

    [Fact]
    public void ParameterInfoRowsGiveIconPathAndTintAndSkipUnreadableRows()
    {
        var sheets = new ParameterSheets();
        var critical = sheets.Row("暴擊", "LUCK", [0xb0, 0x6c, 0xf0, 0x8f], "UI/Sprites/Common/Icon64/Icon64_SubStat_Critical.png");
        var breaked = sheets.Row("無防備傷害", "Invalid", [0x9f], "UI/Sprites/Common/Icon64/Icon64_Stat_Brake.png");
        var unknownTag = sheets.Row("追擊", "LUCK", [0x55], "UI/Sprites/Common/Icon64/ICON64_An_Additional_Hit.png");
        var escape = sheets.Row("廣域強化", "INT", [0x9f], "../Escape.png");
        var (meta, text, data) = sheets.Build(("Critical", critical), ("DamageAmplificationOnBreak", breaked), ("DoubleHit", unknownTag), ("MultiHitPowerUp", escape));
        var rows = ClientAttributeIcons.DecodeParameters(meta, text, data);
        Assert.Equal(new ClientParameterIcon("暴擊", "UI/Sprites/Common/Icon64/Icon64_SubStat_Critical.png", "#6CF08F"), rows["Critical"]);
        Assert.Equal(new ClientParameterIcon("無防備傷害", "UI/Sprites/Common/Icon64/Icon64_Stat_Brake.png", null), rows["DamageAmplificationOnBreak"]);
        Assert.False(rows.ContainsKey("DoubleHit"));
        Assert.Equal(new ClientParameterIcon("廣域強化", null, null), rows["MultiHitPowerUp"]);
        var (otherMeta, otherText, otherData) = new ParameterSheets().Build();
        Assert.Equal(ClientIconReasons.ParameterTableUnavailable,
            Assert.Throws<ClientIconFormatException>(() => ClientAttributeIcons.DecodeParameters(otherMeta, otherText, otherData)).Code);
    }

    [Fact]
    public void CacheRoundTripsDiscardsTamperedFilesAndKeepsTwoClients()
    {
        var root = Path.Combine(Path.GetTempPath(), "moongate-client-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            var png = PngEncoder.Encode(new ClientIconBitmap(1, 1, [255, 255, 255, 255]));
            ClientIconSet Set(char digit) => new(new string(digit, 64), [.. ClientAttributeIcons.Definitions.Select(definition => definition.Key == ClientIconKeys.Critical
                ? new ClientIconEntry(definition.Key, null, "#6CF08F", ClientIconSource.ParameterInfo, definition.FixedPath, 1, 1, png,
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)), "Critical", "暴擊")
                : new ClientIconEntry(definition.Key, ClientIconReasons.ResourceMissing, null, ClientIconSource.FixedPath, definition.FixedPath, null, null, null, null))],
                [ClientIconReasons.ParameterTableUnavailable]);
            Directory.CreateDirectory(Path.Combine(root, "v0"));
            ClientIconCache.Write(root, Set('a'));
            Assert.False(Directory.Exists(Path.Combine(root, "v0")));
            var read = ClientIconCache.Read(root, new string('a', 64))!;
            Assert.Equal([ClientIconReasons.ParameterTableUnavailable], read.Reasons);
            Assert.Equal(png, read.Icons.Single(icon => icon.Key == ClientIconKeys.Critical).Png);
            Assert.Equal(("#6CF08F", ClientIconSource.ParameterInfo, "Critical", "暴擊"), (read.Icons[0].Tint, read.Icons[0].Source, read.Icons[0].NameKey, read.Icons[0].DisplayName));
            Assert.Equal(ClientIconReasons.ResourceMissing, read.Icons.Single(icon => icon.Key == "element.soul").Reason);
            Assert.Null(ClientIconCache.Read(root, new string('b', 64)));

            var directory = Path.Combine(root, "v" + ClientIconCache.RulesVersion, new string('a', 32));
            File.WriteAllBytes(Path.Combine(directory, "critical.png"), [.. png[..^1], 0]);
            Assert.Null(ClientIconCache.Read(root, new string('a', 64)));
            Assert.False(Directory.Exists(directory));

            ClientIconCache.Write(root, Set('a'));
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddHours(-2));
            ClientIconCache.Write(root, Set('b'));
            Directory.SetLastWriteTimeUtc(Path.Combine(root, "v" + ClientIconCache.RulesVersion, new string('b', 32)), DateTime.UtcNow.AddHours(-1));
            ClientIconCache.Write(root, Set('c'));
            Assert.Equal([new string('b', 32), new string('c', 32)],
                Directory.GetDirectories(Path.Combine(root, "v" + ClientIconCache.RulesVersion)).Select(Path.GetFileName).Order());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// 本机验证：读取 <c>MABITOOL_TEST_TW_CLIENT</c>（默认 C:\Nexon\Library\MabinogiMobile_TW\appdata）的客户端，
    /// 对全部图标键断言 64×64、非全透明，并列出每个键的结果。只在设置 <c>MABITOOL_TEST_CLIENT_ICONS=1</c> 时运行；不保存任何图像。
    /// </summary>
    [InstalledClientIconFact]
    public void InstalledClientProvidesEveryAttributeIcon()
    {
        var set = ClientAttributeIcons.Extract(InstalledTwClientFactAttribute.ClientDirectory);
        output.WriteLine("clientId " + set.ClientId + (set.Reasons.Count > 0 ? " reasons " + string.Join(",", set.Reasons) : ""));
        var failures = new List<string>();
        foreach (var icon in set.Icons)
        {
            string result;
            if (icon.Png is null) result = "失败 " + icon.Reason;
            else
            {
                var (width, height, rgba) = DecodePng(icon.Png);
                var visible = Enumerable.Range(0, width * height).Count(pixel => rgba[pixel * 4 + 3] != 0);
                result = width == 64 && height == 64 && visible > 0 ? $"成功 {width}x{height} 可见像素 {visible}" : $"失败 {width}x{height} 可见像素 {visible}";
            }
            if (result.StartsWith("失败", StringComparison.Ordinal)) failures.Add(icon.Key);
            output.WriteLine($"{icon.Key,-18} {result} 着色 {icon.Tint ?? "无"} 名称 {icon.NameKey ?? "无"}={icon.DisplayName ?? "无"} 来源 {icon.Source} {icon.SourcePath}");
        }
        Assert.Empty(failures);
    }

    private static byte[] Pixel(ClientIconBitmap bitmap, int x, int y) => bitmap.Rgba.AsSpan((y * bitmap.Width + x) * 4, 4).ToArray();

    /// <summary>按 PNG 规范读回：核对签名、各块 CRC 与 IHDR，解压 IDAT 并去掉每行的过滤类型字节（编码器只写过滤类型 0）。</summary>
    private static (int Width, int Height, byte[] Rgba) DecodePng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, png[..8]);
        int at = 8, width = 0, height = 0;
        using var idat = new MemoryStream();
        while (at < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var body = png.AsSpan(at + 8, length);
            Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length)), Crc32(png.AsSpan(at + 4, length + 4)));
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body); height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                Assert.Equal(new byte[] { 8, 6, 0, 0, 0 }, body[8..].ToArray());
            }
            if (type == "IDAT") idat.Write(body);
            at += 12 + length;
        }
        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress)) zlib.CopyTo(inflated);
        var raw = inflated.ToArray();
        Assert.Equal((width * 4 + 1) * height, raw.Length);
        var rgba = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Assert.Equal(0, raw[row * (width * 4 + 1)]);
            raw.AsSpan(row * (width * 4 + 1) + 1, width * 4).CopyTo(rgba.AsSpan(row * width * 4));
        }
        return (width, height, rgba);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xffff_ffffu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb8_8320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    /// <summary>按客户端 Sheetdata 编码合成只有 ParameterInfo 一张表的三份资源；键列为 ParameterType 名称的文本偏移。</summary>
    private sealed class ParameterSheets
    {
        private readonly MemoryStream text = Seeded();
        private static MemoryStream Seeded() { var value = new MemoryStream(); value.WriteByte(0); return value; }

        public uint Text(string value)
        {
            var offset = (uint)text.Position; var bytes = Encoding.UTF8.GetBytes(value);
            text.WriteByte((byte)bytes.Length); text.Write(bytes); text.WriteByte(0); return offset;
        }

        public byte[] Row(string name, string primary, byte[] color, string icon)
        {
            var row = new List<byte>(); Var(row, Text(name)); Var(row, Text(primary)); row.AddRange(color); Var(row, Text(icon)); return [.. row];
        }

        public (byte[] Meta, byte[] Text, byte[] Data) Build(params (string Key, byte[] Row)[] rows)
        {
            var meta = new List<byte>(); var keys = new List<byte>();
            foreach (var (key, _) in rows) Var(keys, Text(key));
            Var(meta, 1);
            Var(meta, Text(rows.Length == 0 ? "OtherSheet" : ClientAttributeIcons.ParameterSheet)); Var(meta, 1); Var(meta, (uint)rows.Length);
            Var(meta, (uint)keys.Count); Var(meta, (uint)rows.Sum(row => row.Row.Length));
            foreach (var (_, row) in rows) Var(meta, (uint)row.Length);
            meta.AddRange(keys);
            return ([.. meta], text.ToArray(), [.. rows.SelectMany(row => row.Row)]);
        }

        private static void Var(List<byte> output, uint value)
        {
            while (value >= 0x80) { output.Add((byte)(value | 0x80)); value >>= 7; }
            output.Add((byte)value);
        }
    }
}

/// <summary>默认跳过；设置 MABITOOL_TEST_CLIENT_ICONS=1 且本机有台服客户端时运行。</summary>
public sealed class InstalledClientIconFactAttribute : FactAttribute
{
    public InstalledClientIconFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MABITOOL_TEST_CLIENT_ICONS") != "1")
            Skip = "本机客户端图标验证默认不运行；设置 MABITOOL_TEST_CLIENT_ICONS=1 后运行";
        else if (!File.Exists(Path.Combine(InstalledTwClientFactAttribute.ClientDirectory, "AssetPackages", "index")))
            Skip = "需要台服客户端；设置 MABITOOL_TEST_TW_CLIENT 指向 appdata";
    }
}
