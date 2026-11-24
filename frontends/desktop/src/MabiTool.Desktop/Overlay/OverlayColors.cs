using Avalonia.Controls;
using Avalonia.Media;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Overlay;

/// <summary>浮窗配色的六组。底板与文字两组可以按元件覆盖。</summary>
public enum OverlayColorGroup { Plate, Text, Status, Bars, Effects, Icons }

/// <summary>浮窗配色的一项：保存用的键、所属组、简繁名称与默认值（原配色，#RRGGBB）。</summary>
public sealed record OverlayColorItem(string Key, OverlayColorGroup Group, string Hans, string Hant, string Default)
{
    public string Name => UiText.Pick(Hans, Hant);
}

/// <summary>
/// 浮窗配色：各项的定义与默认值、版面文件里配色字典的整理、按配色给元件下发画刷。
/// 版面文件只保存改过的项（键 → #RRGGBB），没有的项等于默认值，所以旧文件与没改过的浮窗与原配色相同。
/// 画刷以资源的形式放在每个元件外框的资源字典里，覆盖 Tokens.axaml 里同名的默认值；元件内容以 DynamicResource 引用，
/// 改色时只替换字典里的画刷，不重建元件。
/// </summary>
public static class OverlayColors
{
    public const string Plate = "plate", Text = "text", Muted = "muted", Accent = "accent", Positive = "positive", Warning = "warning",
        BarFill = "barFill", BarTrack = "barTrack", EffectPositive = "effectPositive", EffectNegative = "effectNegative",
        EffectOther = "effectOther", EffectWarning = "effectWarning", EffectText = "effectText", EffectMuted = "effectMuted",
        EffectTrack = "effectTrack", EffectOverflow = "effectOverflow",
        IconAttribute = "iconAttribute", IconExposed = "iconExposed";

    /// <summary>元素图标项的键：<c>element.</c> 加元素名，与客户端图标键相同。</summary>
    public static readonly IReadOnlyList<string> Elements = ["bleed", "dark", "fire", "holy", "ice", "lightning", "poison", "soul"];

    /// <summary>玻璃与纯色外观的底色透明度：底板只取 RGB，透明度沿用原来的 #A3／#F2，再由背景不透明度整体调整。</summary>
    public const byte GlassAlpha = 0xA3, SolidAlpha = 0xF2;

    /// <summary>效果条目轨道与溢出“+N”底的透明度：只取 RGB，透明度沿用原来的 #E6。</summary>
    public const byte EffectTrackAlpha = 0xE6;

    /// <summary>轨道的透明度（25%）：画在底板上，与原来的 #40FFFFFF 相同。</summary>
    public const byte TrackAlpha = 0x40;

    /// <summary>全部项，按组排列；默认值即原配色。属性图标的默认白色只用于显示：未改时沿用客户端给出的颜色。</summary>
    public static readonly IReadOnlyList<OverlayColorItem> Items =
    [
        new(Plate, OverlayColorGroup.Plate, "背景色", "背景色", "#121624"),
        new(Text, OverlayColorGroup.Text, "主文字", "主文字", "#FFFFFF"),
        new(Muted, OverlayColorGroup.Text, "次要文字", "次要文字", "#D5DAE8"),
        new(Accent, OverlayColorGroup.Status, "强调", "強調", "#FFD08A"),
        new(Positive, OverlayColorGroup.Status, "正面／就绪", "正面／就緒", "#6EE7B7"),
        new(Warning, OverlayColorGroup.Status, "警示／负面", "警示／負面", "#FF8A80"),
        new(BarFill, OverlayColorGroup.Bars, "进度填充", "進度填充", "#FF9C7F"),
        new(BarTrack, OverlayColorGroup.Bars, "轨道", "軌道", "#FFFFFF"),
        new(EffectPositive, OverlayColorGroup.Effects, "正面底", "正面底", "#2563EB"),
        new(EffectNegative, OverlayColorGroup.Effects, "负面底", "負面底", "#DC2626"),
        new(EffectOther, OverlayColorGroup.Effects, "其他底", "其他底", "#6B7280"),
        new(EffectWarning, OverlayColorGroup.Effects, "警示底", "警示底", "#B45309"),
        new(EffectText, OverlayColorGroup.Effects, "条目文字", "條目文字", "#FFFFFF"),
        new(EffectMuted, OverlayColorGroup.Effects, "条目次要文字", "條目次要文字", "#E5E9F2"),
        new(EffectTrack, OverlayColorGroup.Effects, "条目轨道", "條目軌道", "#141826"),
        new(EffectOverflow, OverlayColorGroup.Effects, "溢出“+N”底", "溢出「+N」底", "#141826"),
        new(IconAttribute, OverlayColorGroup.Icons, "属性图标", "屬性圖示", "#FFFFFF"),
        new(IconExposed, OverlayColorGroup.Icons, "无防备", "無防備", Backend.ClientIconNames.BreakTint),
        .. Elements.Select(element => new OverlayColorItem(Backend.ClientIconNames.Element(element), OverlayColorGroup.Icons,
            ElementHans(element), ElementHant(element), Backend.CombatMapper.ElementColor(element)))
    ];

    private static string ElementHans(string element) => element switch
    {
        "bleed" => "出血", "dark" => "暗黑", "fire" => "火焰", "holy" => "神圣", "ice" => "冰冻", "lightning" => "触电", "poison" => "毒", _ => "精神"
    };

    private static string ElementHant(string element) => element switch
    {
        "bleed" => "出血", "dark" => "暗黑", "fire" => "火焰", "holy" => "神聖", "ice" => "冰凍", "lightning" => "觸電", "poison" => "毒", _ => "精神"
    };

    private static readonly Dictionary<string, OverlayColorItem> ByKey = Items.ToDictionary(item => item.Key, StringComparer.Ordinal);

    /// <summary>可以按元件覆盖的项：底板与文字两组。</summary>
    public static readonly IReadOnlyList<string> WidgetKeys = [Plate, Text, Muted];

    public static OverlayColorItem? Find(string key) => ByKey.GetValueOrDefault(key);

    public static string GroupName(OverlayColorGroup group) => group switch
    {
        OverlayColorGroup.Plate => UiText.Pick("底板", "底板"),
        OverlayColorGroup.Text => UiText.Pick("文字", "文字"),
        OverlayColorGroup.Status => UiText.Pick("强调与状态", "強調與狀態"),
        OverlayColorGroup.Bars => UiText.Pick("条形", "條形"),
        OverlayColorGroup.Effects => UiText.Pick("效果条目", "效果條目"),
        _ => UiText.Pick("图标", "圖示")
    };

    /// <summary>
    /// 整理版面文件里的配色字典：只留认识的键与六位 HEX（整理成大写 #RRGGBB），HEX 不合法的项去掉（按默认值或整体配色显示）；
    /// 整理后没有项时为空，保存时不写出。整体配色（<paramref name="allowed"/> 为空）里等于默认值的项也去掉；
    /// 元件的覆盖只留 <paramref name="allowed"/> 里的键，等于默认值的覆盖保留（整体配色改过时，它让这个元件仍用原色）。
    /// </summary>
    public static Dictionary<string, string>? Normalize(Dictionary<string, string>? colors, IReadOnlyList<string>? allowed = null)
    {
        if (colors is null) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in colors)
        {
            if (Find(key) is not { } item || (allowed is not null && !allowed.Contains(key))) continue;
            if (ColorMath.NormalizeHex(value) is { } hex && (allowed is not null || hex != item.Default)) result[key] = hex;
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>某一项实际使用的颜色：元件的覆盖、整体配色、默认值，依次取第一个有的。</summary>
    public static string Resolve(string key, IReadOnlyDictionary<string, string>? palette, IReadOnlyDictionary<string, string>? widget = null) =>
        widget is not null && widget.TryGetValue(key, out var own) ? own
        : palette is not null && palette.TryGetValue(key, out var shared) ? shared
        : Find(key)?.Default ?? "#FFFFFF";

    /// <summary>图标的着色覆盖：只含改过的图标项，未改的图标沿用客户端给出的颜色。</summary>
    public static ClientIconTints? IconTints(IReadOnlyDictionary<string, string>? palette)
    {
        if (palette is null) return null;
        var map = palette.Where(pair => Find(pair.Key)?.Group == OverlayColorGroup.Icons)
            .ToDictionary(pair => pair.Key, pair => Color.Parse(pair.Value), StringComparer.Ordinal);
        return map.Count > 0 ? new ClientIconTints(map) : null;
    }

    /// <summary>
    /// 把配色写进元件外框（或示意元件）的资源字典：每个资源键一个画刷，颜色没变的键不替换，
    /// 改色时只有变了的画刷通知引用它的控件。
    /// </summary>
    public static void Apply(IResourceDictionary resources, IReadOnlyDictionary<string, string>? palette, IReadOnlyDictionary<string, string>? widget = null)
    {
        Color Of(string key, byte alpha = 0xFF) => Color.Parse(Resolve(key, palette, widget)) is var color ? Color.FromArgb(alpha, color.R, color.G, color.B) : default;
        Set(resources, "OverlayGlassBrush", Of(Plate, GlassAlpha));
        Set(resources, "OverlaySolidBrush", Of(Plate, SolidAlpha));
        Set(resources, "OverlayTextBrush", Of(Text));
        Set(resources, "OverlayMutedBrush", Of(Muted));
        Set(resources, "OverlayAccentBrush", Of(Accent));
        Set(resources, "OverlayReadyBrush", Of(Positive));
        Set(resources, "OverlayWaitingBrush", Of(Warning));
        Set(resources, "OverlayBarFillBrush", Of(BarFill));
        Set(resources, "OverlayBarTrackBrush", Of(BarTrack, TrackAlpha));
        Set(resources, "OverlayEffectPositiveBrush", Of(EffectPositive));
        Set(resources, "OverlayEffectNegativeBrush", Of(EffectNegative));
        Set(resources, "OverlayEffectOtherBrush", Of(EffectOther));
        Set(resources, "OverlayEffectWarningBrush", Of(EffectWarning));
        Set(resources, "OverlayEffectTextBrush", Of(EffectText));
        Set(resources, "OverlayEffectMutedBrush", Of(EffectMuted));
        Set(resources, "OverlayEffectTrackBrush", Of(EffectTrack, EffectTrackAlpha));
        Set(resources, "OverlayEffectOverflowBrush", Of(EffectOverflow, EffectTrackAlpha));
    }

    private static void Set(IResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetValue(key, out var existing) && existing is ISolidColorBrush { Color: var current } && current == color) return;
        resources[key] = new SolidColorBrush(color);
    }
}
