using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Theme;

/// <summary>一个图标槽的外观：矢量线稿或自定图片，以及悬停和选中时各播放哪种动效。</summary>
public sealed record IconSpec(Geometry? Vector, IImage? Image, string HoverMotion, string ActiveMotion);

/// <summary>
/// 图标槽之外的素材种类，各自对应 pack.json 的一个字段：
/// 窗口背景 backgrounds（按模块标识）、效果图标 effects（按效果编号）、乐器图标 instruments（按乐器标识）、清单封面 playlists（按清单标识）。
/// </summary>
public enum AssetKind { Background, Effect, Instrument, Playlist }

/// <summary>
/// 一张窗口背景，以及图里最暗与最亮的颜色（半透明像素按叠在黑底与白底上分别计入）。
/// 遮罩按这两个极端颜色和当前主题计算，保证文字对比度。
/// </summary>
public sealed record BackdropImage(IImage Image, Color Darkest, Color Brightest);

/// <summary>
/// 图标动效预设。每种预设把 0 到 1 的进度换算成图标的位移、缩放和旋转；
/// 进度为 0 和 1 时都回到原位，所以动效可以随时重播。
/// </summary>
public static class IconMotion
{
    public const string None = "none";
    public static readonly IReadOnlyList<string> Presets = ["none", "pop", "bounce", "swing", "spin", "pulse", "float", "shake"];

    public static string Normalize(string? preset) =>
        preset is not null && Presets.Contains(preset, StringComparer.Ordinal) ? preset : None;

    /// <summary>以图标中心为原点的变换。</summary>
    public static Matrix Transform(string preset, double progress, double size)
    {
        var p = Math.Clamp(progress, 0, 1);
        if (p is 0 or 1) return Matrix.Identity;
        var wave = Math.Sin(Math.PI * p);
        var fade = 1 - p;
        return preset switch
        {
            "pop" => Matrix.CreateScale(1 + 0.2 * wave, 1 + 0.2 * wave),
            "bounce" => Matrix.CreateScale(1 + 0.06 * wave, 1 - 0.06 * wave)
                        * Matrix.CreateTranslation(0, -size * 0.14 * Math.Abs(Math.Sin(2 * Math.PI * p)) * (0.4 + 0.6 * fade)),
            "swing" => Matrix.CreateRotation(Math.Sin(4 * Math.PI * p) * fade * 0.32),
            "spin" => Matrix.CreateRotation(2 * Math.PI * (1 - Math.Pow(1 - p, 3))),
            "pulse" => Matrix.CreateScale(1 + 0.1 * Math.Sin(2 * Math.PI * p), 1 + 0.1 * Math.Sin(2 * Math.PI * p)),
            "float" => Matrix.CreateTranslation(0, -size * 0.1 * wave),
            "shake" => Matrix.CreateTranslation(size * 0.08 * Math.Sin(6 * Math.PI * p) * fade, 0),
            _ => Matrix.Identity
        };
    }
}

/// <summary>
/// 图标槽的登记处。每个槽有内建的矢量图标和默认动效；素材包可以按槽替换图片和动效。
/// 槽名规则：模块入口为 module.&lt;模块标识&gt;，面板标题为 panel.&lt;页面&gt;.&lt;面板&gt;，外壳入口为 shell.&lt;名称&gt;，
/// 生活页的设施、技能与队列为 life.&lt;类别&gt;.&lt;种类&gt;。
/// </summary>
public static class IconRegistry
{
    private const long MaximumImageBytes = 2 * 1024 * 1024;
    // 窗口背景铺满整个窗口，允许较大的图片。
    private const long MaximumBackdropBytes = 8 * 1024 * 1024;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    // 24×24 画板上的线稿，以白色描边画在色块上。
    private static readonly Dictionary<string, (string Path, string Hover)> BuiltIn = new(StringComparer.Ordinal)
    {
        ["module.music-library"] = ("M9 17.5V5.5L19 3.5V15.5 M9 17.5A2.5 2.5 0 1 1 4 17.5A2.5 2.5 0 0 1 9 17.5 M19 15.5A2.5 2.5 0 1 1 14 15.5A2.5 2.5 0 0 1 19 15.5", "swing"),
        ["module.rooms"] = ("M8.5 11A3 3 0 1 0 8.5 5A3 3 0 0 0 8.5 11 M3 19C3 16 5.5 14 8.5 14C11.5 14 14 16 14 19 M15.5 11A2.5 2.5 0 1 0 15.5 6 M17 14C19.5 14.3 21 16.2 21 19", "pulse"),
        ["module.color"] = ("M12 3.5C12 3.5 6 10 6 14.5A6 6 0 0 0 18 14.5C18 10 12 3.5 12 3.5Z M9.5 15A2.5 2.5 0 0 0 12 17.5", "bounce"),
        ["module.combat-observation"] = ("M12 3V7 M12 17V21 M3 12H7 M17 12H21 M12 16A4 4 0 1 0 12 8A4 4 0 0 0 12 16Z", "spin"),
        ["module.inventory"] = ("M5 9H19L18 20H6Z M9 9V7A3 3 0 0 1 15 7V9", "bounce"),
        ["module.character"] = ("M12 11A3.5 3.5 0 1 0 12 4A3.5 3.5 0 0 0 12 11Z M5 20C5 16.5 8 14 12 14C16 14 19 16.5 19 20", "pulse"),
        ["module.market"] = ("M4 9L12 4L20 9 M6 9V17 M10 9V17 M14 9V17 M18 9V17 M4 20H20", "bounce"),
        ["module.life"] = ("M5 19C5 11 10 5 19 5C19 14 14 19 5 19Z M5 19L13 11", "float"),
        ["module.presentation"] = ("M4 6H15V15H4Z M9 15V19H20V10H15", "float"),
        ["module.shell.modules"] = ("M4 4H10V10H4Z M14 4H20V10H14Z M4 14H10V20H4Z M17 13.5V20.5 M13.5 17H20.5", "pop"),
        ["module.shell.settings"] = ("M4 7H14 M18 7H20 M16 9A2 2 0 1 0 16 5A2 2 0 0 0 16 9Z M4 17H6 M10 17H20 M8 19A2 2 0 1 0 8 15A2 2 0 0 0 8 19Z", "shake"),
        ["module.shell.diagnostics"] = ("M3 12H7L9.5 6L13 18L15.5 12H21", "pulse"),
        ["shell.language"] = ("M12 21A9 9 0 1 0 12 3A9 9 0 0 0 12 21Z M3 12H21 M12 3C9 6 9 18 12 21 M12 3C15 6 15 18 12 21", "spin"),
        ["panel.combat.damage"] = ("M12 3L14 9L20.5 9.5L15.5 13.5L17.5 20L12 16.2L6.5 20L8.5 13.5L3.5 9.5L10 9Z", "pop"),
        ["panel.combat.self"] = ("M12 11A3.5 3.5 0 1 0 12 4A3.5 3.5 0 0 0 12 11Z M5 20C5 16.5 8 14 12 14C16 14 19 16.5 19 20", "pulse"),
        ["panel.combat.target"] = ("M6 10A6 6 0 0 1 18 10V14A6 6 0 0 1 6 14Z M6 9L4 4L8.5 6.5 M18 9L20 4L15.5 6.5 M10 12.5V13 M14 12.5V13", "shake"),
        ["panel.combat.history"] = ("M12 21A9 9 0 1 0 12 3A9 9 0 0 0 12 21Z M12 7.5V12L15 14", "spin"),
        // 生活：加工设施 life.facility.<种类>、制作设施 life.craft.<种类>、生活技能 life.skill.<种类>、总队列 life.queue。
        // 槽名固定，游戏内图标打包后按槽替换，页面只引用槽名。
        ["life.facility.metal"] = ("M4 16L7 10H17L20 16Z M7 10L9 6H15L17 10", "bounce"),
        ["life.facility.wood"] = ("M3 9H21V14H3Z M3 18H21 M7 9V14 M12 9V14 M17 9V14", "shake"),
        ["life.facility.cloth"] = ("M7 4H17 M7 20H17 M9 4V20 M15 4V20 M9 8H15 M9 12H15 M9 16H15", "spin"),
        ["life.facility.leather"] = ("M7 4L12 6L17 4L19 8L17 12L19 18L12 20L5 18L7 12L5 8Z", "swing"),
        ["life.facility.herb"] = ("M10 3H14 M10 3V9L5 18A2 2 0 0 0 7 21H17A2 2 0 0 0 19 18L14 9V3 M7.5 15H16.5", "bounce"),
        ["life.facility.food"] = ("M4 10H20 M5 10V17A3 3 0 0 0 8 20H16A3 3 0 0 0 19 17V10 M9 7C9 6 10 6 10 4 M14 7C14 6 15 6 15 4", "pulse"),
        ["life.facility.other"] = ("M12 15A3 3 0 1 0 12 9A3 3 0 0 0 12 15Z M12 3V6 M12 18V21 M3 12H6 M18 12H21 M5.6 5.6L7.8 7.8 M16.2 16.2L18.4 18.4 M5.6 18.4L7.8 16.2 M16.2 7.8L18.4 5.6", "spin"),
        ["life.craft.weapon"] = ("M14 4H20V10L9 21L6 18Z M5 13L11 19 M3 21L6 18", "swing"),
        ["life.craft.armor"] = ("M12 3L19 6V11C19 16 16 19 12 21C8 19 5 16 5 11V6Z", "pop"),
        ["life.craft.potion"] = ("M10 3H14 M10.5 3V8A6 6 0 1 0 13.5 8V3 M7 14H17", "bounce"),
        ["life.craft.food"] = ("M3 11H21A9 9 0 0 1 3 11Z M8 7C8 6 9 6 9 4 M12 7C12 6 13 6 13 4 M16 7C16 6 17 6 17 4", "pulse"),
        ["life.craft.multi"] = ("M3 9H21V19H3Z M8 9V6H16V9 M3 13H21 M11 13V15H13V13", "bounce"),
        ["life.craft.decor"] = ("M12 21V12 M12 12C12 8 8 6 5 7C5 10 8 12 12 12 M12 12C12 8 16 6 19 7C19 10 16 12 12 12 M8 21H16", "float"),
        ["life.craft.instant"] = ("M13 3L5 14H12L11 21L19 10H12Z", "pop"),
        ["life.craft.other"] = ("M14 6L18 10 M4 20L13.5 10.5 M12 4L20 12L17 15L9 7Z", "swing"),
        ["life.skill.logging"] = ("M12 3L6 11H9L5 17H19L15 11H18Z M12 17V21", "swing"),
        ["life.skill.mining"] = ("M4 9C8 4 16 4 20 9 M12 6V21", "shake"),
        ["life.skill.herb"] = ("M12 21V10 M12 14C8 14 6 11 6 8C9 8 12 10 12 14 M12 11C16 11 18 8 18 5C15 5 12 7 12 11", "float"),
        ["life.skill.daily"] = ("M4 10H20L18 20H6Z M8 10L12 4L16 10 M9 14V17 M12 14V17 M15 14V17", "bounce"),
        ["life.skill.insect"] = ("M12 7V19 M12 9C9 4 4 5 4 9C4 12 8 13 12 12 M12 9C15 4 20 5 20 9C20 12 16 13 12 12", "float"),
        ["life.skill.wool"] = ("M7 16A3 3 0 0 1 7 10A4 4 0 0 1 14 8A3.5 3.5 0 0 1 18 14A2.5 2.5 0 0 1 17 16Z M9 16V20 M15 16V20", "bounce"),
        ["life.skill.harvest"] = ("M12 21V6 M12 9C10 8 9 6 9 4C11 5 12 7 12 9 M12 9C14 8 15 6 15 4C13 5 12 7 12 9 M12 14C10 13 9 11 9 9C11 10 12 12 12 14 M12 14C14 13 15 11 15 9C13 10 12 12 12 14", "swing"),
        ["life.skill.hoe"] = ("M5 20L15 10 M13 5H20V9L15 10L13 8Z", "shake"),
        ["life.skill.fishing"] = ("M3 12C6 7 13 6 17 12C13 18 6 17 3 12Z M17 12L21 8V16Z M8 11.5V12", "swing"),
        ["life.skill.other"] = ("M6 12H6.01 M12 12H12.01 M18 12H18.01", "pulse"),
        ["life.queue"] = ("M8 6H20 M8 12H20 M8 18H20 M4 6H4.01 M4 12H4.01 M4 18H4.01", "pulse")
    };

    private static readonly Dictionary<string, IconSpec> Vectors = new(StringComparer.Ordinal);
    private static Dictionary<string, IconSpec> custom = new(StringComparer.Ordinal);
    private static Dictionary<AssetKind, Dictionary<string, IImage>> images = EmptyImages();
    private static Dictionary<string, BackdropImage> backdrops = new(StringComparer.Ordinal);
    private static int loads;

    /// <summary>素材包里某种素材的图片；没有对应条目时为空，调用方保持内建外观。</summary>
    public static IImage? Find(AssetKind kind, string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (kind == AssetKind.Background) return Backdrop(key)?.Image;
        return images[kind].GetValueOrDefault(key);
    }

    /// <summary>某个模块的窗口背景；素材包没有为它提供时为空。</summary>
    public static BackdropImage? Backdrop(string? moduleId) =>
        moduleId is null ? null : backdrops.GetValueOrDefault(moduleId);

    /// <summary>当前素材包里某种素材生效的条目数。</summary>
    public static int Count(AssetKind kind) => kind == AssetKind.Background ? backdrops.Count : images[kind].Count;

    private static Dictionary<AssetKind, Dictionary<string, IImage>> EmptyImages() => new()
    {
        [AssetKind.Effect] = new(StringComparer.Ordinal),
        [AssetKind.Instrument] = new(StringComparer.Ordinal),
        [AssetKind.Playlist] = new(StringComparer.Ordinal)
    };

    public static event EventHandler? Changed;

    /// <summary>目前生效的素材包名称；没有素材包时为空。</summary>
    public static string? PackName { get; private set; }

    /// <summary>目前生效的素材包目录；没有素材包时为空。</summary>
    public static string? PackDirectory { get; private set; }

    /// <summary>
    /// 用户在界面上选定的素材包目录，保存在前端偏好文件里，下次启动自动载入；
    /// 以 --assets 临时指定的素材包不改变这项选择。
    /// </summary>
    public static string? ChosenDirectory { get; set; }

    /// <summary>用户选定或移除素材包时触发，前端偏好随之保存。</summary>
    public static event EventHandler? ChoiceChanged;

    /// <summary>
    /// 载入用户选定的素材包。找到并读出 pack.json 才算载入并记为选择，返回真；
    /// <paramref name="problems"/> 是被跳过的条目或没有载入的原因。
    /// </summary>
    public static bool Choose(string directory, out IReadOnlyList<string> problems)
    {
        var before = loads;
        problems = LoadPack(directory);
        if (loads == before) return false;
        ChosenDirectory = PackDirectory;
        ChoiceChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>移除素材包并清除选择，回到内建外观。</summary>
    public static void Remove()
    {
        Reset();
        ChosenDirectory = null;
        ChoiceChanged?.Invoke(null, EventArgs.Empty);
    }

    public static IReadOnlyCollection<string> BuiltInSlots => BuiltIn.Keys;

    /// <summary>没有登记的槽返回空，由调用方显示字章。</summary>
    public static IconSpec? Resolve(string? slot)
    {
        if (string.IsNullOrEmpty(slot)) return null;
        if (custom.TryGetValue(slot, out var replaced)) return replaced;
        if (Vectors.TryGetValue(slot, out var cached)) return cached;
        if (!BuiltIn.TryGetValue(slot, out var builtIn)) return null;
        var spec = new IconSpec(Geometry.Parse(builtIn.Path), null, builtIn.Hover, "pop");
        Vectors[slot] = spec;
        return spec;
    }

    /// <summary>
    /// 载入素材包目录下的 pack.json。图片必须位于包目录内、扩展名受支持且不超过 2 MB；
    /// 不合格的条目跳过并在返回的清单里说明，其余条目照常生效。
    /// </summary>
    public static IReadOnlyList<string> LoadPack(string directory)
    {
        var problems = new List<string>();
        var loaded = new Dictionary<string, IconSpec>(StringComparer.Ordinal);
        var root = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(root, "pack.json");
        if (!File.Exists(manifestPath))
        {
            problems.Add(Pick("找不到 pack.json", "找不到 pack.json"));
            return problems;
        }

        AssetPackManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AssetPackManifest>(File.ReadAllBytes(manifestPath), JsonOptions);
        }
        catch (JsonException)
        {
            problems.Add(Pick("pack.json 无法解析", "pack.json 無法解析"));
            return problems;
        }

        foreach (var (slot, entry) in manifest?.Icons ?? [])
        {
            var fallback = BuiltIn.TryGetValue(slot, out var builtIn) ? builtIn : (Path: null, Hover: IconMotion.None);
            var image = entry.Image is { Length: > 0 } relative
                ? ReadImage(root, slot, relative, MaximumImageBytes, problems)
                : null;
            var vector = image is null && fallback.Path is not null ? Geometry.Parse(fallback.Path) : null;
            if (image is null && vector is null && entry.Hover is null && entry.Selected is null) continue;
            loaded[slot] = new IconSpec(vector, image, IconMotion.Normalize(entry.Hover ?? fallback.Hover),
                IconMotion.Normalize(entry.Selected ?? "pop"));
        }

        // 其余素材按种类读入；条目名在问题清单里写成「字段.键」，与 pack.json 对得上。
        var loadedImages = EmptyImages();
        foreach (var (kind, field, entries) in new[]
                 {
                     (AssetKind.Effect, "effects", manifest?.Effects),
                     (AssetKind.Instrument, "instruments", manifest?.Instruments),
                     (AssetKind.Playlist, "playlists", manifest?.Playlists)
                 })
            foreach (var (key, entry) in entries ?? [])
                if (entry.Image is { Length: > 0 } relative
                    && ReadImage(root, $"{field}.{key}", relative, MaximumImageBytes, problems) is { } image)
                    loadedImages[kind][key] = image;

        var loadedBackdrops = new Dictionary<string, BackdropImage>(StringComparer.Ordinal);
        foreach (var (moduleId, entry) in manifest?.Backgrounds ?? [])
            if (entry.Image is { Length: > 0 } relative
                && ReadImage(root, $"backgrounds.{moduleId}", relative, MaximumBackdropBytes, problems) is { } image)
            {
                if (Extremes(image) is { } extremes)
                    loadedBackdrops[moduleId] = new BackdropImage(image, extremes.Darkest, extremes.Brightest);
                else
                    problems.Add(Pick($"backgrounds.{moduleId}：图片无法读取", $"backgrounds.{moduleId}：圖片無法讀取"));
            }

        custom = loaded;
        images = loadedImages;
        backdrops = loadedBackdrops;
        loads++;
        PackDirectory = root;
        PackName = string.IsNullOrWhiteSpace(manifest?.Name) ? Path.GetFileName(root) : manifest.Name;
        Changed?.Invoke(null, EventArgs.Empty);
        return problems;
    }

    /// <summary>移除素材包，回到内建外观。</summary>
    public static void Reset()
    {
        custom = new(StringComparer.Ordinal);
        images = EmptyImages();
        backdrops = new(StringComparer.Ordinal);
        PackName = null;
        PackDirectory = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    /// <summary>
    /// 读取包目录内的一张图片：必须位于包目录内、扩展名受支持、不超过 <paramref name="limit"/>；
    /// 不合格时记下原因并返回空。
    /// </summary>
    private static Bitmap? ReadImage(string root, string label, string relative, long limit, List<string> problems)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var megabytes = limit / (1024 * 1024);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            problems.Add(Pick($"{label}：图片不在素材包目录内", $"{label}：圖片不在素材包目錄內"));
        else if (!ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            problems.Add(Pick($"{label}：不支持的图片格式 {Path.GetExtension(path)}", $"{label}：不支援的圖片格式 {Path.GetExtension(path)}"));
        else if (!File.Exists(path))
            problems.Add(Pick($"{label}：找不到图片 {relative}", $"{label}：找不到圖片 {relative}"));
        else if (new FileInfo(path).Length > limit)
            problems.Add(Pick($"{label}：图片超过 {megabytes} MB", $"{label}：圖片超過 {megabytes} MB"));
        else
            try { return new Bitmap(path); }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException
                                              or NotSupportedException)
            {
                problems.Add(Pick($"{label}：图片无法读取", $"{label}：圖片無法讀取"));
            }
        return null;
    }

    /// <summary>
    /// 把图片缩到 64 宽后找出最暗与最亮的颜色。半透明像素下面可能是任何颜色，
    /// 所以分别按叠在黑底和白底上计入，结果偏保守。
    /// </summary>
    private static (Color Darkest, Color Brightest)? Extremes(Bitmap image)
    {
        if (image.PixelSize.Width <= 0 || image.PixelSize.Height <= 0) return null;
        var width = Math.Min(64, image.PixelSize.Width);
        var height = Math.Max(1, (int)Math.Round(image.PixelSize.Height * (double)width / image.PixelSize.Width));
        using var small = image.CreateScaledBitmap(new PixelSize(width, height), BitmapInterpolationMode.MediumQuality);
        // 解码后的位图是 32 位 BGRA 或 RGBA；其他格式不做亮度分析，按无法读取处理。
        var format = small.Format;
        var rgba = format == PixelFormats.Rgba8888;
        if (format != PixelFormats.Bgra8888 && !rgba) return null;
        var premultiplied = small.AlphaFormat == AlphaFormat.Premul;

        var pixels = new byte[width * height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { small.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
        finally { handle.Free(); }

        Color darkest = Colors.White, brightest = Colors.Black;
        double low = double.MaxValue, high = double.MinValue;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3] / 255.0;
            // 预乘的通道已经乘过透明度，叠在底色上时不再乘一次。
            byte Over(byte channel, byte under) => (byte)Math.Clamp(Math.Round(
                (premultiplied ? channel : channel * alpha) + under * (1 - alpha)), 0, 255);
            var (b, g, r) = rgba
                ? (pixels[index + 2], pixels[index + 1], pixels[index])
                : (pixels[index], pixels[index + 1], pixels[index + 2]);
            var onBlack = Color.FromRgb(Over(r, 0), Over(g, 0), Over(b, 0));
            var onWhite = Color.FromRgb(Over(r, 255), Over(g, 255), Over(b, 255));
            if (UiTheme.Luminance(onBlack) < low) (low, darkest) = (UiTheme.Luminance(onBlack), onBlack);
            if (UiTheme.Luminance(onWhite) > high) (high, brightest) = (UiTheme.Luminance(onWhite), onWhite);
        }
        return (darkest, brightest);
    }

    private sealed record AssetPackManifest(string? Name, Dictionary<string, AssetPackIcon>? Icons,
        Dictionary<string, AssetPackImage>? Backgrounds = null, Dictionary<string, AssetPackImage>? Effects = null,
        Dictionary<string, AssetPackImage>? Instruments = null, Dictionary<string, AssetPackImage>? Playlists = null);

    private sealed record AssetPackIcon(string? Image, string? Hover, string? Selected);

    private sealed record AssetPackImage(string? Image);
}
