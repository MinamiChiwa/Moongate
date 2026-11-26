using System.Runtime.Versioning;
using MabiTool.LifeVision.Worker;

[assembly: SupportedOSPlatform("windows")]

namespace MabiTool.LifeVision.Tests;

/// <summary>夹具、模板与合成帧：测试里的帧都是 1280×960 的 RGB 图像，模板像素原样拷贝到期望位置。</summary>
internal static class TestAssets
{
    public static readonly byte[] FillColor = [40, 220, 88];
    public static readonly byte[] EmptyColor = [39, 42, 50];

    private static readonly Lazy<TemplateCatalog> catalog = new(() => TemplateCatalog.Load(AppContext.BaseDirectory));

    public static TemplateCatalog Catalog => catalog.Value;

    public static RgbImage Fixture(string relativePath) =>
        PngDecoder.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath)));

    public static RgbImage Template(TemplateSpec spec) =>
        PngDecoder.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, TemplateCatalog.FolderName, spec.File)));

    /// <summary>仓库根目录：自测试输出目录向上找到 MabiTool.slnx。</summary>
    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("找不到仓库根目录");
    }

    public static RgbImage Black() => new(1280, 960, new byte[1280 * 960 * 3]);

    public static RgbImage Solid(int width, int height, byte red, byte green, byte blue)
    {
        var image = new RgbImage(width, height, new byte[width * height * 3]);
        for (var i = 0; i < image.Pixels.Length; i += 3) { image.Pixels[i] = red; image.Pixels[i + 1] = green; image.Pixels[i + 2] = blue; }
        return image;
    }

    public static RgbImage Copy(RgbImage image) => new(image.Width, image.Height, (byte[])image.Pixels.Clone());

    public static void Paste(RgbImage frame, RgbImage patch, int left, int top)
    {
        for (var y = 0; y < patch.Height; y++)
            Array.Copy(patch.Pixels, y * patch.Width * 3, frame.Pixels, ((top + y) * frame.Width + left) * 3, patch.Width * 3);
    }

    public static void PasteTemplate(RgbImage frame, TemplateSpec spec, int offsetX = 0, int offsetY = 0) =>
        Paste(frame, Template(spec), spec.Box.Left + offsetX, spec.Box.Top + offsetY);

    /// <summary>画一条两色进度条：左侧 <paramref name="fill"/> 个像素为填充色，其余为空槽色。</summary>
    public static void DrawGauge(RgbImage frame, int left, int top, int width, int rows, int fill)
    {
        for (var y = top; y < top + rows; y++)
            for (var x = left; x < left + width; x++)
            {
                var color = x < left + fill ? FillColor : EmptyColor;
                var offset = (y * frame.Width + x) * 3;
                frame.Pixels[offset] = color[0]; frame.Pixels[offset + 1] = color[1]; frame.Pixels[offset + 2] = color[2];
            }
    }

    /// <summary>上钩图标的期望中心是 (630,371)，轨道框是中心偏移 (-48,25)–(48,55)，条形画在其中的 11 行。</summary>
    public static RgbImage HookFrame(bool hook, int fill)
    {
        var frame = Black();
        if (hook) PasteTemplate(frame, FishingProfile.Hook);
        if (fill >= 0) DrawGauge(frame, 582, 405, 96, 11, fill);
        return frame;
    }
}
