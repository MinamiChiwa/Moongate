using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MabiTool.Desktop.Backend;

namespace MabiTool.Desktop.Theme;

/// <summary>
/// 随包游戏图标的一项（来自 <c>Assets/GameIcons/game-icons.lock.json</c>）：<see cref="Id"/> 为“分组/键”，
/// <see cref="Glyph"/> 为真表示白色字形（界面按主题或调用方给的颜色着色），否则为彩色图标原样显示。
/// 属性图标另带客户端属性表的着色 <see cref="Tint"/> 与显示名称原文；生活技能与货币带游戏原文名称 <see cref="Names"/>，货币带钱包货币类型。
/// </summary>
public sealed record GameIconEntry(string Id, string Group, string Key, string File, bool Glyph, string Sha256, string? Tint, string? DisplayName,
    IReadOnlyList<string> Names, IReadOnlyList<string> MoneyTypes);

/// <summary>
/// 随包游戏图标：取自台服客户端的图标（版权归 Nexon / devCAT）与按同一风格自绘的图标，随桌面程序集内嵌分发，
/// 来源与哈希登记在 <c>game-icons.lock.json</c>（由 <c>scripts/maintenance/GameIconExtractor</c> 生成）。没有绑定游戏时也可用。
/// 登记文件第一次用到时读入，位图按项解码一次后留在内存；登记文件读不出时所有查找为空，界面只显示文字。
/// </summary>
public static class GameIcons
{
    private const string Base = "avares://MabiTool.Desktop/Assets/GameIcons/";
    private static readonly IReadOnlyDictionary<string, GameIconEntry> Empty = new Dictionary<string, GameIconEntry>(StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, GameIconEntry>? entries;
    private static readonly ConcurrentDictionary<string, Bitmap?> Bitmaps = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ClientIconImage?> Attributes = new(StringComparer.Ordinal);

    /// <summary>
    /// 登记的全部图标（按“分组/键”）。界面平台尚未初始化（资源加载器不可用）时返回空表且不记住，初始化之后再取时读入。
    /// </summary>
    public static IReadOnlyDictionary<string, GameIconEntry> All => entries ?? (entries = Load()) ?? Empty;

    /// <summary>“分组/键”对应的图标；没有登记时为空。</summary>
    public static GameIconEntry? Find(string? id) => id is { Length: > 0 } && All.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>图标位图（解码一次后沿用）；没有登记或解码失败时为空。</summary>
    public static Bitmap? Bitmap(GameIconEntry entry) => Bitmaps.GetOrAdd(entry.Id, _ =>
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(Base + entry.File));
            return new Bitmap(stream);
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            System.Diagnostics.Trace.WriteLine($"随包图标 {entry.File} 读取失败：{error.Message}");
            return null;
        }
    });

    /// <summary>
    /// 属性图标（键同后端 <c>/api/v1/client-icons</c>，例如 critical、element.fire）的随包版本；包里没有这个键时为空。
    /// 版本为 PNG 的 SHA-256，着色与显示名称取登记文件（客户端属性表 MainColor 与 DisplayName）。
    /// </summary>
    public static ClientIconImage? Attribute(string key) => Find("attribute/" + key) is { } entry
        ? Attributes.GetOrAdd(key, name => Bitmap(entry) is { } bitmap ? new ClientIconImage(name, entry.Sha256, bitmap, entry.Tint, entry.DisplayName) : null)
        : null;

    /// <summary>包里没有的属性图标键（这些键才需要运行时读取）。</summary>
    public static IReadOnlyList<string> MissingAttributes(IEnumerable<string> keys) =>
        keys.Where(key => Find("attribute/" + key) is null).ToList();

    /// <summary>生活技能（游戏原文名称，如“採礦”）的图标；对应不上时为空。</summary>
    public static string? LifeSkill(string? name) => ByName("life", name);

    /// <summary>货币的图标：先按钱包货币类型（如 Gold、SilverCoin），再按游戏原文名称（如“金幣”）；对应不上时为空。</summary>
    public static string? Currency(string? moneyType, string? name)
    {
        if (moneyType is { Length: > 0 })
            foreach (var entry in All.Values)
                if (entry.Group == "currency" && entry.MoneyTypes.Contains(moneyType, StringComparer.Ordinal)) return entry.Id;
        return ByName("currency", name);
    }

    /// <summary>天气代码（Sunny、Rain 等，大小写不论）的图标；未收录的天气为空。</summary>
    public static string? Weather(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "sunny" or "clear" or "fine" => "weather/sunny",
        "cloudy" or "partlycloudy" or "overcast" => "weather/cloudy",
        "rain" or "rainy" or "raining" or "shower" => "weather/rain",
        "drizzle" => "weather/drizzle",
        "storm" or "stormy" or "thunder" or "thunderstorm" or "heavyrain" => "weather/storm",
        "snow" or "snowy" or "snowing" => "weather/snow",
        "fog" or "foggy" or "mist" or "misty" => "weather/fog",
        "night" => "weather/night",
        _ => null
    };

    private static string? ByName(string group, string? name)
    {
        if (name is not { Length: > 0 }) return null;
        var trimmed = name.Trim();
        foreach (var entry in All.Values)
            if (entry.Group == group && entry.Names.Contains(trimmed, StringComparer.Ordinal)) return entry.Id;
        return null;
    }

    // 读入登记文件：界面应用还没建立（资源加载器不可用）时返回空、不记住；登记文件损坏时返回空表并记住，不反复读取。
    private static IReadOnlyDictionary<string, GameIconEntry>? Load()
    {
        if (Application.Current is null) return null;
        try
        {
            using var stream = AssetLoader.Open(new Uri(Base + "game-icons.lock.json"));
            using var document = JsonDocument.Parse(stream);
            var result = new Dictionary<string, GameIconEntry>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.GetProperty("files").EnumerateArray())
            {
                string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                IReadOnlyList<string> List(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Select(element => element.GetString()).OfType<string>().ToList() : [];
                var (group, key, file, sha256) = (Text("group"), Text("key"), Text("path"), Text("sha256"));
                if (group is null || key is null || file is null || sha256 is null) continue;
                result[group + "/" + key] = new(group + "/" + key, group, key, file, Text("style") != "color", sha256, Text("tint"), Text("displayName"),
                    List("names"), List("moneyTypes"));
            }
            return result;
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine("随包图标登记文件读取失败：" + error.Message);
            return Empty;
        }
    }
}
