using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 选色浮层用的 HSV 与 HEX 换算。只用于展示与点选，不参与寻色判定。
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// 选色浮层下方两行时尚设计常用色，色值固定。第一行基础中性色，第二行经典服饰色（胶囊衣橱“中性基础色 + 经典点缀色”的划分）。
    /// 有 W3C CSS Color Module Level 4 命名色的取其值；炭灰、卡其、驼色、酒红、芥末黄取 Wikipedia “List of colors” 的通行值
    /// （CSS 的 khaki #F0E68C 偏黄，与服饰用语不符）。
    /// </summary>
    public static readonly IReadOnlyList<(string Hex, string Hans, string Hant)> CommonColors =
    [
        ("#000000", "纯黑", "純黑"), ("#36454F", "炭灰", "炭灰"), ("#808080", "灰", "灰"), ("#C0C0C0", "银灰", "銀灰"),
        ("#FFFFFF", "纯白", "純白"), ("#FFFFF0", "象牙白", "象牙白"), ("#F5F5DC", "米色", "米色"), ("#C3B091", "卡其", "卡其"),
        ("#C19A6B", "驼色", "駝色"), ("#8B4513", "棕", "棕"),
        ("#000080", "藏青", "藏青"), ("#4169E1", "皇家蓝", "皇家藍"), ("#800020", "酒红", "酒紅"), ("#DC143C", "绯红", "緋紅"),
        ("#FFC0CB", "粉", "粉"), ("#FF7F50", "珊瑚", "珊瑚"), ("#FFDB58", "芥末黄", "芥末黃"), ("#808000", "橄榄绿", "橄欖綠"),
        ("#228B22", "森林绿", "森林綠"), ("#E6E6FA", "薰衣草", "薰衣草")
    ];

    public static (int R, int G, int B) Rgb(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    public static string Hex(double r, double g, double b)
    {
        static int Channel(double value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.ToEven), 0, 255);
        return $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";
    }

    /// <summary>HEX 换成色相 0–360（不含 360）、饱和度 0–100、明度 0–100，不取整。</summary>
    public static (double H, double S, double V) ToHsv(string hex)
    {
        var (red, green, blue) = Rgb(hex);
        double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var range = max - min;
        double h = 0;
        if (range > 0)
        {
            h = max == r ? (g - b) / range : max == g ? 2 + (b - r) / range : 4 + (r - g) / range;
            h = (h * 60 % 360 + 360) % 360;
        }
        var s = max <= 0 ? 0 : range / max;
        return (h, s * 100, max * 100);
    }

    public static string FromHsv(double h, double s, double v)
    {
        var hue = (h % 360 + 360) % 360 / 60;
        double saturation = Math.Clamp(s, 0, 100) / 100, value = Math.Clamp(v, 0, 100) / 100;
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue % 2 - 1));
        var (r, g, b) = (int)hue switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        var m = value - chroma;
        return Hex((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }
}

/// <summary>选色浮层里的一格颜色；<see cref="Name"/> 是常用色的名称，其余为空。</summary>
public sealed record PickerSwatch(string Hex, string Name = "")
{
    public IBrush Brush => new SolidColorBrush(Color.Parse(Hex));
    public string Tip => Name.Length > 0 ? Name + " " + Hex : Hex;
}

/// <summary>“最近”的一个格子：有颜色时可点，没有记录时是空框，格子数固定为 8。</summary>
public sealed record RecentSlot(PickerSwatch? Swatch)
{
    public bool Filled => Swatch is not null;
    public bool Empty => Swatch is null;
}

/// <summary>
/// 最近选择的颜色：浮层关闭时目标色若已改变，关闭时的颜色排到首位（去重，最多 8 个）。三个区域共用，本次运行期间保留，
/// 页面重建（切换模块）后仍在。
/// </summary>
public static class ColorRecent
{
    public const int Capacity = 8;
    private static readonly object Gate = new();
    private static List<string> items = [];

    public static IReadOnlyList<string> Items
    {
        get { lock (Gate) return items; }
    }

    public static void Remember(string hex)
    {
        lock (Gate)
        {
            items = [hex, .. items.Where(item => item != hex)];
            if (items.Count > Capacity) items = items.Take(Capacity).ToList();
        }
    }
}

/// <summary>
/// 一张区域卡的选色浮层。区域卡的目标色是唯一状态源：浮层只保存色相、饱和度、明度（HSV）作为色相环、方块与滑杆的位置，
/// 改动时换成 HEX 交给卡片；卡片目标色从别处改变时由 <see cref="Sync"/> 重算。中性色（饱和度 0）与纯黑（明度 0）
/// 换算时保留原色相与饱和度，拖动方块不会让色相环跳回红色。
/// </summary>
public sealed class ColorPickerModel : ObservableObject
{
    private readonly Func<string> read;
    private readonly Action<string> apply;
    private double hue, saturation, brightness;
    private string written = string.Empty;
    private string original = "#000000";
    private string? hueDraft, saturationDraft, brightnessDraft;

    public ColorPickerModel(Func<string> read, Action<string> apply)
    {
        this.read = read;
        this.apply = apply;
        PickCommand = new RelayCommand(value =>
        {
            if (value is PickerSwatch swatch) Pick(swatch.Hex);
        });
        RevertCommand = new RelayCommand(_ => Pick(original));
    }

    public ICommand PickCommand { get; }

    /// <summary>回到打开浮层时的颜色。</summary>
    public ICommand RevertCommand { get; }

    /// <summary>当前目标色（读自区域卡）。</summary>
    public string Target => read();
    public IBrush TargetBrush => new SolidColorBrush(Color.Parse(Target));

    /// <summary>打开浮层时的目标色。</summary>
    public string Original => original;
    public IBrush OriginalBrush => new SolidColorBrush(Color.Parse(original));
    public string OriginalTip => UiText.Pick("打开时 ", "開啟時 ") + original + UiText.Pick("，点击还原", "，點擊還原");

    public double Hue { get => hue; set => Adjust(value, saturation, brightness); }
    public double Saturation { get => saturation; set => Adjust(hue, value, brightness); }
    public double Brightness { get => brightness; set => Adjust(hue, saturation, value); }

    /// <summary>精调的数值输入：合法整数立即写回（超出范围夹到边界），不是整数时标红、不写入。</summary>
    public string HueInput { get => hueDraft ?? Whole(hue); set => Input(value, 360, ref hueDraft, number => Adjust(number, saturation, brightness)); }
    public string SaturationInput { get => saturationDraft ?? Whole(saturation); set => Input(value, 100, ref saturationDraft, number => Adjust(hue, number, brightness)); }
    public string BrightnessInput { get => brightnessDraft ?? Whole(brightness); set => Input(value, 100, ref brightnessDraft, number => Adjust(hue, saturation, number)); }
    public bool HueInvalid => hueDraft is not null;
    public bool SaturationInvalid => saturationDraft is not null;
    public bool BrightnessInvalid => brightnessDraft is not null;

    public IReadOnlyList<RecentSlot> Recent { get; private set; } = Slots([]);
    public bool NoRecent => Recent.All(slot => slot.Empty);

    public IReadOnlyList<PickerSwatch> Common { get; } =
        ColorMath.CommonColors.Select(color => new PickerSwatch(color.Hex, UiText.Pick(color.Hans, color.Hant))).ToList();

    /// <summary>精调轨道：色相是完整色相环展开，饱和度与明度随当前颜色重画。</summary>
    public IBrush HueTrack => Track(Enumerable.Range(0, 7).Select(step => ColorMath.FromHsv(step * 60, 100, 100)).ToArray());
    public IBrush SaturationTrack => Track(ColorMath.FromHsv(hue, 0, brightness), ColorMath.FromHsv(hue, 100, brightness));
    public IBrush BrightnessTrack => Track("#000000", ColorMath.FromHsv(hue, saturation, 100));

    /// <summary>以卡片当前的目标色打开，读入最近选择。</summary>
    public void Open()
    {
        original = read();
        Recent = Slots(ColorRecent.Items);
        written = string.Empty;
        Sync(original);
    }

    // 先写回卡片再刷新浮层，标题色块与 HEX 读到的是新的目标色。
    public void Pick(string hex)
    {
        apply(hex);
        Sync(hex);
    }

    /// <summary>卡片目标色改变：不是浮层自己写回的颜色时按新颜色重算 HSV。</summary>
    internal void Sync(string hex)
    {
        if (hex == written) return;
        var (h, s, v) = ColorMath.ToHsv(hex);
        if (v <= 0) (h, s) = (hue, saturation);
        else if (s <= 0) h = hue;
        (hue, saturation, brightness) = (h, s, v);
        written = hex;
        hueDraft = saturationDraft = brightnessDraft = null;
        Raise(string.Empty);
    }

    public void Adjust(double h, double s, double v)
    {
        hue = Math.Clamp(h, 0, 360);
        saturation = Math.Clamp(s, 0, 100);
        brightness = Math.Clamp(v, 0, 100);
        written = ColorMath.FromHsv(hue, saturation, brightness);
        hueDraft = saturationDraft = brightnessDraft = null;
        apply(written);
        Raise(string.Empty);
    }

    private void Input(string? text, int maximum, ref string? draft, Action<double> write)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            write(Math.Clamp(number, 0, maximum));
            return;
        }
        draft = text ?? string.Empty;
        Raise(string.Empty);
    }

    private static string Whole(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);

    private static List<RecentSlot> Slots(IReadOnlyList<string> colors) =>
        Enumerable.Range(0, ColorRecent.Capacity).Select(index => new RecentSlot(index < colors.Count ? new PickerSwatch(colors[index]) : null)).ToList();

    private static IBrush Track(params string[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative)
        };
        for (var index = 0; index < stops.Length; index++)
            brush.GradientStops.Add(new GradientStop(Color.Parse(stops[index]), stops.Length == 1 ? 0 : index / (double)(stops.Length - 1)));
        return brush;
    }
}

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
