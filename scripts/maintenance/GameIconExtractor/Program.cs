// 随包游戏图标的提取工具：从本机台服客户端只读取出固定清单（IconManifest）里的图标，缺少的按 DrawnIcons 自绘，
// 写入桌面前端的 Assets/GameIcons 与登记文件 game-icons.lock.json（路径、来源资源、长度、SHA-256、版权方或“自绘”）。
// 用法：
//   extract <客户端 appdata 目录> <输出目录> [--sheet <核对图.png>]   提取全部图标、重写登记文件，可另出一张核对用的拼图
//   list <客户端 appdata 目录> <资源路径前缀>                          列出客户端资源路径（找新图标用）
//   probe <客户端 appdata 目录> <拼图.png> <资源路径...>                试读若干资源并拼成一张图
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.GameIconExtractor;
using MabiTool.Providers.TwObservation.ItemCatalog;

if (args is ["list", var appdata, var prefix])
{
    foreach (var path in ClientSpriteResources.List(appdata, prefix)) Console.WriteLine(path);
    return 0;
}
if (args is ["probe", var probeRoot, var probeSheet, .. var paths])
{
    var results = ClientSpriteResources.Extract(probeRoot, paths);
    for (var i = 0; i < results.Count; i++)
        Console.WriteLine($"{i,3} {(results[i].Bitmap is { } b ? $"{b.Width}x{b.Height}" : results[i].Reason)} {results[i].Path}");
    File.WriteAllBytes(probeSheet, Sheet(results.Select(r => r.Bitmap).ToList()));
    return 0;
}
if (args is ["extract", var root, var output, .. var rest])
{
    var sheet = rest is ["--sheet", var value] ? value : null;
    return Extract(root, output, sheet);
}
Console.Error.WriteLine("用法：extract <客户端目录> <输出目录> [--sheet <核对图.png>] | list <客户端目录> <资源路径前缀> | probe <客户端目录> <拼图.png> <资源路径...>");
return 2;

static int Extract(string root, string output, string? sheetPath)
{
    const int side = 64;
    var entries = new List<JsonObject>();
    var images = new List<ClientIconBitmap?>();
    var failures = new List<string>();
    var vendor = "Nexon / devCAT";

    // 属性图标：与宿主 /api/v1/client-icons 同一读取（ParameterInfo 表决定路径与着色）。
    var attributes = ClientAttributeIcons.Extract(root);
    var attributeBitmaps = ClientSpriteResources.Extract(root, attributes.Icons.Select(icon => icon.SourcePath).Distinct(StringComparer.Ordinal).ToList())
        .ToDictionary(item => item.Path, StringComparer.Ordinal);
    foreach (var (key, role) in IconManifest.Attributes)
    {
        var icon = attributes.Icons.Single(item => item.Key == key);
        if (attributeBitmaps.GetValueOrDefault(icon.SourcePath)?.Bitmap is not { } bitmap) { failures.Add($"{key}: {icon.Reason ?? "读取失败"}"); continue; }
        var spec = new IconSpec("attribute", key, role, icon.SourcePath);
        var entry = Write(output, spec, Fit(bitmap, side), vendor, images);
        entry["tint"] = icon.Tint;
        entry["displayName"] = icon.DisplayName;
        entries.Add(entry);
    }

    var sprites = IconManifest.Sprites.Where(spec => spec.Origin is not null).ToList();
    var decoded = ClientSpriteResources.Extract(root, sprites.Select(spec => spec.Origin!).Distinct(StringComparer.Ordinal).ToList())
        .ToDictionary(item => item.Path, StringComparer.Ordinal);
    foreach (var spec in IconManifest.Sprites)
    {
        if (spec.Origin is null)
        {
            entries.Add(Write(output, spec, DrawnIcons.Draw(spec.Group + "/" + spec.Key), null, images));
            continue;
        }
        var result = decoded[spec.Origin];
        if (result.Bitmap is not { } bitmap) { failures.Add($"{spec.File}: {result.Reason}"); continue; }
        entries.Add(Write(output, spec, Fit(bitmap, side), vendor, images));
    }
    if (failures.Count > 0)
    {
        foreach (var failure in failures) Console.Error.WriteLine("读取失败：" + failure);
        return 1;
    }

    // 输出目录里不在清单内的图片删去，登记与文件一一对应。
    var expected = entries.Select(entry => (string)entry["path"]!).ToHashSet(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(output, "*.png", SearchOption.AllDirectories))
        if (!expected.Contains(Path.GetRelativePath(output, file).Replace('\\', '/'))) File.Delete(file);

    var lockFile = new JsonObject
    {
        ["schemaVersion"] = 1,
        ["id"] = "desktop-game-icons",
        ["vendor"] = vendor,
        ["copyright"] = "author 为 Nexon / devCAT 的图标取自台服 Mabinogi Mobile 客户端，版权归 Nexon / devCAT 所有，不适用 Moongate 的 MIT 许可；" +
                        "Moongate 是非商业的爱好者项目，与 NEXON、devCAT 无关。author 为“自绘”的图标由 Moongate 按客户端图标风格绘制，随项目以 MIT 许可发布。",
        ["source"] = "由 scripts/maintenance/GameIconExtractor 从本机台服客户端（只读）按固定清单提取；属性图标的路径与着色取客户端 ParameterInfo 表，" +
                     "生活技能与货币的路径和名称取 LivingSkill、MoneyInfo 表；边长大于 64 的图标按面积平均缩到 64×64，其余按解码结果原样编码为 PNG。",
        ["distribution"] = "随 Moongate 仓库（frontends/desktop/src/MabiTool.Desktop/Assets/GameIcons/）与发行包（桌面前端程序集内嵌资源）分发。",
        ["clientId"] = attributes.ClientId,
        ["files"] = new JsonArray(entries.ToArray<JsonNode?>())
    };
    var json = lockFile.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    File.WriteAllText(Path.Combine(output, "game-icons.lock.json"), json.Replace("\r\n", "\n") + "\n");
    var total = entries.Sum(entry => (long)(int)entry["length"]!);
    Console.WriteLine($"已写入 {entries.Count} 个图标（自绘 {entries.Count(entry => (string?)entry["origin"] is null)} 个），共 {total:N0} 字节");
    if (sheetPath is not null) File.WriteAllBytes(sheetPath, Sheet(images));
    return 0;
}

static JsonObject Write(string output, IconSpec spec, ClientIconBitmap bitmap, string? vendor, List<ClientIconBitmap?> images)
{
    var (png, sha256) = ClientSpriteResources.Encode(bitmap);
    var path = Path.Combine(output, spec.File.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, png);
    images.Add(bitmap);
    var entry = new JsonObject
    {
        ["path"] = spec.File,
        ["key"] = spec.Key,
        ["group"] = spec.Group,
        ["role"] = spec.Role,
        ["style"] = spec.Glyph ? "glyph" : "color",
        ["width"] = bitmap.Width,
        ["height"] = bitmap.Height,
        ["length"] = png.Length,
        ["sha256"] = sha256,
        ["origin"] = spec.Origin,
        ["author"] = vendor ?? "自绘"
    };
    if (spec.Names is { Length: > 0 } names) entry["names"] = new JsonArray(names.Select(name => (JsonNode?)name).ToArray());
    if (spec.MoneyTypes is { Length: > 0 } types) entry["moneyTypes"] = new JsonArray(types.Select(type => (JsonNode?)type).ToArray());
    return entry;
}

// 边长大于 side 且为其整数倍的图标按面积平均缩小（预乘透明度），其余原样返回。
static ClientIconBitmap Fit(ClientIconBitmap bitmap, int side)
{
    if (bitmap.Width <= side && bitmap.Height <= side) return bitmap;
    if (bitmap.Width != bitmap.Height || bitmap.Width % side != 0) throw new InvalidDataException($"图标尺寸 {bitmap.Width}×{bitmap.Height} 不能整倍缩到 {side}");
    var factor = bitmap.Width / side;
    var rgba = new byte[side * side * 4];
    for (var y = 0; y < side; y++)
    for (var x = 0; x < side; x++)
    {
        double r = 0, g = 0, b = 0, a = 0;
        for (var dy = 0; dy < factor; dy++)
        for (var dx = 0; dx < factor; dx++)
        {
            var at = ((y * factor + dy) * bitmap.Width + x * factor + dx) * 4;
            var alpha = bitmap.Rgba[at + 3] / 255.0;
            r += bitmap.Rgba[at] * alpha; g += bitmap.Rgba[at + 1] * alpha; b += bitmap.Rgba[at + 2] * alpha; a += alpha;
        }
        var to = (y * side + x) * 4;
        var count = factor * factor;
        if (a > 0) { rgba[to] = (byte)Math.Round(r / a); rgba[to + 1] = (byte)Math.Round(g / a); rgba[to + 2] = (byte)Math.Round(b / a); }
        rgba[to + 3] = (byte)Math.Round(a / count * 255);
    }
    return new ClientIconBitmap(side, side, rgba);
}

// 核对用拼图：每格 64×64，深色底，每行 8 个，顺序同登记文件。
static byte[] Sheet(IReadOnlyList<ClientIconBitmap?> images)
{
    const int cell = 72, side = 64, columns = 8;
    var rows = Math.Max(1, (images.Count + columns - 1) / columns);
    var (width, height) = (columns * cell, rows * cell);
    var rgba = new byte[width * height * 4];
    for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = 0x2A; rgba[i + 1] = 0x2D; rgba[i + 2] = 0x36; rgba[i + 3] = 255; }
    for (var index = 0; index < images.Count; index++)
    {
        if (images[index] is not { } image) continue;
        var (ox, oy) = (index % columns * cell + 4, index / columns * cell + 4);
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var (sx, sy) = (x * image.Width / side, y * image.Height / side);
            var from = (sy * image.Width + sx) * 4;
            var to = ((oy + y) * width + ox + x) * 4;
            var a = image.Rgba[from + 3];
            for (var c = 0; c < 3; c++) rgba[to + c] = (byte)((image.Rgba[from + c] * a + rgba[to + c] * (255 - a)) / 255);
        }
    }
    return ClientSpriteResources.Encode(new ClientIconBitmap(width, height, rgba)).Png;
}
