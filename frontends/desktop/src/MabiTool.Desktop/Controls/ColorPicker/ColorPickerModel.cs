using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Media;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 选色浮层用的 HSV 与 HEX 换算。只用于展示与点选，不参与寻色判定。染色页与浮窗配色共用。
/// </summary>
public static partial class ColorMath
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

    /// <summary>
    /// 六位十六进制色码整理成大写 #RRGGBB（可以不带 #，前后空白忽略）；格式不对时为空（原工具 vision.normalize_hex 的规则）。
    /// </summary>
    public static string? NormalizeHex(string? text)
    {
        var value = (text ?? string.Empty).Trim().TrimStart('#').ToUpperInvariant();
        return HexPattern().IsMatch(value) ? "#" + value : null;
    }

    [GeneratedRegex("^[0-9A-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();

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
/// 一组最近选择的颜色：浮层关闭时颜色若已改变，关闭时的颜色排到首位（去重，最多 8 个），本次运行期间保留。
/// 染色页三个区域共用 <see cref="ColorRecent"/>；浮窗配色另有一组，两边互不混入。
/// </summary>
public sealed class RecentColors
{
    public const int Capacity = 8;
    private readonly object gate = new();
    private List<string> items = [];

    public IReadOnlyList<string> Items
    {
        get { lock (gate) return items; }
    }

    public void Remember(string hex)
    {
        lock (gate)
        {
            items = [hex, .. items.Where(item => item != hex)];
            if (items.Count > Capacity) items = items.Take(Capacity).ToList();
        }
    }
}

/// <summary>染色页的最近选择：三个区域共用，本次运行期间保留，页面重建（切换模块）后仍在。</summary>
public static class ColorRecent
{
    public const int Capacity = RecentColors.Capacity;

    /// <summary>染色页的那一组。</summary>
    public static RecentColors Shared { get; } = new();

    public static IReadOnlyList<string> Items => Shared.Items;

    public static void Remember(string hex) => Shared.Remember(hex);
}

/// <summary>
/// 选色浮层的状态（染色页每张区域卡一个，浮窗配色每个颜色项打开时一个）。调用方的颜色是唯一状态源：浮层只保存色相、饱和度、明度（HSV）作为色相环、方块与滑杆的位置，
/// 改动时换成 HEX 经 apply 交给调用方；颜色从别处改变时由 <see cref="Sync"/> 重算。中性色（饱和度 0）与纯黑（明度 0）
/// 换算时保留原色相与饱和度，拖动方块不会让色相环跳回红色。
/// </summary>
public sealed class ColorPickerModel : ObservableObject
{
    private readonly Func<string> read;
    private readonly Action<string> apply;
    private readonly RecentColors recent;
    private double hue, saturation, brightness;
    private string written = string.Empty;
    private string original = "#000000";
    private string? hueDraft, saturationDraft, brightnessDraft, hexDraft;

    /// <param name="read">读取当前颜色（#RRGGBB）。</param>
    /// <param name="apply">写回新颜色（#RRGGBB）。</param>
    /// <param name="recent">“最近”一栏取自哪一组；为空时是染色页的 <see cref="ColorRecent"/>。</param>
    public ColorPickerModel(Func<string> read, Action<string> apply, RecentColors? recent = null)
    {
        this.read = read;
        this.apply = apply;
        this.recent = recent ?? ColorRecent.Shared;
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
    /// <summary>
    /// HEX 输入：六位十六进制（可不带 #）立即写回，否则标红、颜色不变（<see cref="ColorMath.NormalizeHex"/>）。
    /// </summary>
    public string HexInput
    {
        get => hexDraft ?? Target;
        set
        {
            if (ColorMath.NormalizeHex(value) is { } hex)
            {
                hexDraft = null;
                Pick(hex);
                return;
            }
            hexDraft = value ?? string.Empty;
            Raise(string.Empty);
        }
    }

    public bool HexInvalid => hexDraft is not null;
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

    /// <summary>以当前颜色打开，读入最近选择。</summary>
    public void Open()
    {
        original = read();
        Recent = Slots(recent.Items);
        written = string.Empty;
        Sync(original);
    }

    /// <summary>关闭浮层：颜色比打开时改变了，就把它记进“最近”。</summary>
    public void Close()
    {
        if (read() is var current && current != original) recent.Remember(current);
    }

    // 先写回调用方再刷新浮层，标题色块与 HEX 读到的是新的颜色。
    public void Pick(string hex)
    {
        apply(hex);
        Sync(hex);
    }

    /// <summary>颜色改变：不是浮层自己写回的颜色时按新颜色重算 HSV。</summary>
    internal void Sync(string hex)
    {
        if (hex == written) return;
        var (h, s, v) = ColorMath.ToHsv(hex);
        if (v <= 0) (h, s) = (hue, saturation);
        else if (s <= 0) h = hue;
        (hue, saturation, brightness) = (h, s, v);
        written = hex;
        hueDraft = saturationDraft = brightnessDraft = hexDraft = null;
        Raise(string.Empty);
    }

    public void Adjust(double h, double s, double v)
    {
        hue = Math.Clamp(h, 0, 360);
        saturation = Math.Clamp(s, 0, 100);
        brightness = Math.Clamp(v, 0, 100);
        written = ColorMath.FromHsv(hue, saturation, brightness);
        hueDraft = saturationDraft = brightnessDraft = hexDraft = null;
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
        Enumerable.Range(0, RecentColors.Capacity).Select(index => new RecentSlot(index < colors.Count ? new PickerSwatch(colors[index]) : null)).ToList();

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
