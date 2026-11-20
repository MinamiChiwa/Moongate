using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;

namespace MabiTool.Desktop.Tests;

/// <summary>客户端属性图标的替身：代码生成的 64×64 白色圆形 PNG，不含任何游戏图片。</summary>
internal static class TestIcons
{
    public static byte[] Png()
    {
        const int side = 64;
        var pixels = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var (dx, dy) = (x - 31.5, y - 31.5);
            if (dx * dx + dy * dy > 26 * 26) continue;
            pixels.AsSpan((y * side + x) * 4, 4).Fill(255);
        }
        using var bitmap = new WriteableBitmap(new PixelSize(side, side), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = bitmap.Lock())
            for (var y = 0; y < side; y++)
                Marshal.Copy(pixels, y * side * 4, frame.Address + y * frame.RowBytes, side * 4);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    public static ClientIconImage Image(string key, string version = "v1", string? tint = "#6CF08F")
    {
        using var stream = new MemoryStream(Png());
        return new(key, version, new Bitmap(stream), tint, null);
    }

    /// <summary>目录：<paramref name="version"/> 不为空时 critical 可用，其余键不可用。</summary>
    public static ClientIconCatalog Catalog(ClientIconCatalogState state, string? version) => new(state,
        state == ClientIconCatalogState.Ready ? null : "clientIcon.loading", "client-1",
        ClientIconKeys.All.Select(key => key == ClientIconKeys.Critical && version is not null
            ? new ClientIcon(key, true, null, "#6CF08F", version, 64, 64, ClientIconSource.ParameterInfo, "Icon64_SubStat_Critical", "Critical", "暴擊")
            : new ClientIcon(key, false, "clientIcon.loading", null, null, null, null, null, null)).ToList(), []);
}
