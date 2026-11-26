using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Infrastructure.Workers;

namespace MabiTool.LifeVision.Worker;

/// <summary>
/// 一张已变换的模板：灰度平面、均值与标准差，以及匹配用的去均值浮点平面与按行前缀的能量表（行块提前终止用）。
/// </summary>
internal sealed class PreparedTemplate
{
    private PreparedTemplate(TemplateSpec spec, int width, int height, byte[] gray)
    {
        Spec = spec;
        Width = width;
        Height = height;
        Gray = gray;
        long sum = 0, squares = 0;
        foreach (var value in gray) { sum += value; squares += value * value; }
        var count = width * height;
        Mean = (double)sum / count;
        Std = Math.Sqrt(Math.Max(0, (double)squares / count - Mean * Mean));
        Centered = new float[count];
        PrefixSum = new double[height + 1];
        PrefixSquares = new double[height + 1];
        for (var row = 0; row < height; row++)
        {
            double rowSum = 0, rowSquares = 0;
            for (var x = 0; x < width; x++)
            {
                var value = (float)(gray[row * width + x] - Mean);
                Centered[row * width + x] = value;
                rowSum += value;
                rowSquares += (double)value * value;
            }
            PrefixSum[row + 1] = PrefixSum[row] + rowSum;
            PrefixSquares[row + 1] = PrefixSquares[row] + rowSquares;
        }
        Norm = Math.Sqrt(PrefixSquares[height]);
    }

    public TemplateSpec Spec { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Gray { get; }
    public double Mean { get; }
    public double Std { get; }
    /// <summary>去均值的模板（行主序），相关分子 Σ T'·I 用它与搜索区的灰度做点积。</summary>
    public float[] Centered { get; }
    /// <summary><c>PrefixSum[r]</c> 为前 r 行去均值值之和。</summary>
    public double[] PrefixSum { get; }
    /// <summary><c>PrefixSquares[r]</c> 为前 r 行去均值值平方和，<c>PrefixSquares[Height]</c> 为模板能量。</summary>
    public double[] PrefixSquares { get; }
    /// <summary>模板能量的平方根（OpenCV 相关归一化中的 templNorm）。</summary>
    public double Norm { get; }

    public static PreparedTemplate Create(TemplateSpec spec, RgbImage image, PlaneTransformer transformer)
    {
        var gray = new byte[image.Width * image.Height];
        transformer.Transform(image, new PixelBox(0, 0, image.Width, image.Height), spec, gray);
        return new PreparedTemplate(spec, image.Width, image.Height, gray);
    }
}

/// <summary>
/// 从 Worker 输出目录的 <c>templates/</c> 读取全部识别模板，并按同目录的 <c>templates.lock.json</c> 核对文件长度与 SHA-256。
/// 登记里 path 相对输出根（与仓库里 Assets/ 同构，如 <c>templates/fishing_hook.png</c>）。
/// 文件缺失、内容与登记不符、无法解码或尺寸与期望框不符，一律以 <c>life.fishing.componentMissing</c> 失败；
/// 模板常驻内存，总量固定为 8 张小图。
/// </summary>
internal sealed class TemplateCatalog
{
    public const string FolderName = "templates";
    public const string LockFileName = "templates.lock.json";
    public const string ComponentMissing = "life.fishing.componentMissing";

    private readonly Dictionary<string, PreparedTemplate> templates;

    private TemplateCatalog(Dictionary<string, PreparedTemplate> templates) => this.templates = templates;

    public PreparedTemplate Get(TemplateSpec spec) => templates[spec.Id];

    public static TemplateCatalog Load(string root)
    {
        try
        {
            var lockPath = Path.Combine(root, FolderName, LockFileName);
            var registered = new Dictionary<string, (string Sha256, long Length)>(StringComparer.Ordinal);
            using (var document = JsonDocument.Parse(File.ReadAllBytes(lockPath)))
            {
                var rootElement = document.RootElement;
                if (rootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("登记版本不受支持");
                foreach (var file in rootElement.GetProperty("files").EnumerateArray())
                    registered[file.GetProperty("path").GetString()!] = (file.GetProperty("sha256").GetString()!, file.GetProperty("length").GetInt64());
            }
            var maximumPixels = FishingProfile.Templates.Max(spec => spec.Box.Width * spec.Box.Height);
            var maximumWidth = FishingProfile.Templates.Max(spec => spec.Box.Width);
            var transformer = new PlaneTransformer(maximumPixels, maximumWidth, maximumPixels);
            var loaded = new Dictionary<string, PreparedTemplate>(StringComparer.Ordinal);
            foreach (var spec in FishingProfile.Templates)
            {
                var relative = FolderName + "/" + spec.File;
                if (!registered.TryGetValue(relative, out var entry)) throw new InvalidDataException("模板未登记：" + relative);
                var bytes = File.ReadAllBytes(Path.Combine(root, FolderName, spec.File));
                if (bytes.LongLength != entry.Length || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("模板内容与登记不符：" + relative);
                var image = PngDecoder.Decode(bytes);
                if (image.Width != spec.Box.Width || image.Height != spec.Box.Height) throw new InvalidDataException("模板尺寸与期望框不符：" + relative);
                loaded[spec.Id] = PreparedTemplate.Create(spec, image, transformer);
            }
            return new TemplateCatalog(loaded);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or
            KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new WorkerException(ComponentMissing, error);
        }
    }
}
