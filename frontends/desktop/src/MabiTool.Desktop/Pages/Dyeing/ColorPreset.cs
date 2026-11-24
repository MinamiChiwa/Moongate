using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>默认预设的分组：日系插画配色排在前，时尚设计配色在后。</summary>
public enum ColorPresetGroup { Illustration, Fashion }

/// <summary>
/// 选色方案层的十套默认预设：内置只读，套用时把区域 1–3 的目标色依次设为三种颜色。
/// </summary>
public sealed record ColorPreset(string Id, ColorPresetGroup Group, string Hans, string Hant, IReadOnlyList<string> Colors)
{
    /// <summary>
    /// 日系插画配色（按日系插画里常见的配色做法选取，不对应具体画师）：
    /// 透明感粉彩：高明度、低饱和的淡粉与灰蓝，加带暖意的柔白做高光；
    /// 夜空暖灯：深蓝夜空与暖橙灯光的冷暖互补，月光奶黄做亮部；
    /// 黑白一点红：黑白灰主体只留一处高饱和强调色，强调色取和色大辞典“赤”#E60033；
    /// 和风传统色：和色大辞典的藍色 #165E83、朱色 #EB6101、生成り色 #FBFAF5；
    /// 奶油薄荷：奶油色底配薄荷主色，再用深一档的青绿收边。
    /// 时尚设计配色：
    /// 摩卡慕斯大地色：Pantone 2025 Mocha Mousse（17-1230，sRGB #A47864）配米色、棕；
    /// 蜜桃柔和色：Pantone 2024 Peach Fuzz（13-1023，sRGB #FFBE98）配薰衣草、象牙白；
    /// 极简黑白灰：单色极简；海军学院风：学院风藏青、白、酒红；静奢风：驼色、炭灰、象牙白（搭配色取 <see cref="ColorMath.CommonColors"/>）。
    /// </summary>
    public static readonly IReadOnlyList<ColorPreset> BuiltIn =
    [
        new("illust-pastel", ColorPresetGroup.Illustration, "透明感粉彩", "透明感粉彩", ["#F4C7CF", "#A9BCD0", "#F7F3EE"]),
        new("illust-night", ColorPresetGroup.Illustration, "夜空暖灯", "夜空暖燈", ["#1B2A4A", "#F2A541", "#F6E7C1"]),
        new("illust-accent", ColorPresetGroup.Illustration, "黑白一点红", "黑白一點紅", ["#1A1A1A", "#F5F5F5", "#E60033"]),
        new("illust-wafu", ColorPresetGroup.Illustration, "和风传统色", "和風傳統色", ["#165E83", "#EB6101", "#FBFAF5"]),
        new("illust-mint", ColorPresetGroup.Illustration, "奶油薄荷", "奶油薄荷", ["#FFF3D4", "#A8DFC9", "#5E9E92"]),
        new("mocha-earth", ColorPresetGroup.Fashion, "摩卡慕斯大地色", "摩卡慕斯大地色", ["#A47864", "#F5F5DC", "#8B4513"]),
        new("peach-pastel", ColorPresetGroup.Fashion, "蜜桃柔和色", "蜜桃柔和色", ["#FFBE98", "#E6E6FA", "#FFFFF0"]),
        new("monochrome", ColorPresetGroup.Fashion, "极简黑白灰", "極簡黑白灰", ["#000000", "#FFFFFF", "#808080"]),
        new("preppy-navy", ColorPresetGroup.Fashion, "海军学院风", "海軍學院風", ["#000080", "#FFFFFF", "#800020"]),
        new("quiet-luxury", ColorPresetGroup.Fashion, "静奢风", "靜奢風", ["#C19A6B", "#36454F", "#FFFFF0"])
    ];

    public string Name => UiText.Pick(Hans, Hant);
    public IReadOnlyList<PickerSwatch> Swatches => Colors.Select(hex => new PickerSwatch(hex)).ToList();
    public string Tip => string.Join(" · ", Colors);
}
