using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace MabiTool.Desktop.Theme;

public enum UiThemeChoice { System, Light, Dark }

/// <summary>
/// 界面主题：浅色、深色或跟随系统，应用到 <see cref="Application.RequestedThemeVariant"/>。
/// XAML 里的令牌以动态资源跟随；代码里生成的颜色（模块光晕、活动胶囊的占用说明）订阅 <see cref="Changed"/> 重新取值。
/// </summary>
public static class UiTheme
{
    private static Application? attached;
    private static bool applying;

    public static UiThemeChoice Choice { get; private set; } = UiThemeChoice.System;

    /// <summary>选择变化，或跟随系统时系统主题变化。</summary>
    public static event EventHandler? Changed;

    /// <summary>当前实际生效的是深色。</summary>
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static void Apply(Application application, UiThemeChoice choice)
    {
        if (!ReferenceEquals(attached, application))
        {
            if (attached is not null) attached.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
            application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
            attached = application;
        }
        Choice = choice;
        applying = true;
        try
        {
            application.RequestedThemeVariant = choice switch
            {
                UiThemeChoice.Light => ThemeVariant.Light,
                UiThemeChoice.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
        finally
        {
            applying = false;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>深色主题的背景主光晕（令牌 DarkMainHaloBrush）；各模块相同，不随模块色调变化。</summary>
    public static IBrush DarkMainHalo =>
        Application.Current?.TryGetResource("DarkMainHaloBrush", null, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    /// <summary>按当前主题取令牌；找不到时返回 null。</summary>
    public static IBrush? Brush(string key) =>
        Application.Current is { } application
        && application.TryGetResource(key, application.ActualThemeVariant, out var value) ? value as IBrush : null;

    /// <summary>把模块色调当文字颜色用时的取值：深色下与白色按 45% 混合提亮。</summary>
    public static Color TextTone(Color accent, bool dark) => dark ? Mix(accent, Colors.White, 0.45) : accent;

    public static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(Channel(from.A, to.A), Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    /// <summary>相对亮度（WCAG 定义）。</summary>
    public static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    /// <summary>两种颜色的对比度（WCAG 定义）。</summary>
    public static double Contrast(Color a, Color b)
    {
        var (lighter, darker) = (Luminance(a), Luminance(b));
        if (lighter < darker) (lighter, darker) = (darker, lighter);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>把半透明的颜色叠在不透明的底色上。</summary>
    public static Color Over(Color top, Color bottom)
    {
        var alpha = top.A / 255.0;
        byte Channel(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromRgb(Channel(top.R, bottom.R), Channel(top.G, bottom.G), Channel(top.B, bottom.B));
    }

    private static readonly string[] TextTokens = ["InkBrush", "MutedBrush", "ConfirmedBrush", "UnknownBrush"];

    /// <summary>
    /// 窗口背景图上的遮罩：窗口底色加一个透明度。透明度取能让文字在图上最暗与最亮处都不低于 4.5:1 的最小值
    /// （文字可能直接在遮罩上，也可能在面板或面板里的内嵌块上），下限 40%，让背景不压过内容。
    /// </summary>
    public static Color ScrimFor(BackdropImage backdrop, ThemeVariant variant)
    {
        Color Token(string key) =>
            Application.Current?.TryGetResource(key, variant, out var value) == true && value is ISolidColorBrush brush
                ? brush.Color
                : Colors.Transparent;
        var canvas = Token("CanvasBrush");
        var panel = Token("PanelBrush");
        var inset = Token("InsetBrush");
        var texts = TextTokens.Select(Token).ToArray();
        for (var percent = 40; percent < 100; percent++)
        {
            var scrim = Color.FromArgb((byte)Math.Round(percent * 2.55), canvas.R, canvas.G, canvas.B);
            var fits = new[] { backdrop.Darkest, backdrop.Brightest }.All(pixel =>
            {
                var bare = Over(scrim, pixel);
                var onPanel = Over(panel, bare);
                var onInset = Over(inset, onPanel);
                return texts.All(text => Contrast(text, bare) >= 4.5 && Contrast(text, onPanel) >= 4.5 && Contrast(text, onInset) >= 4.5);
            });
            if (fits) return scrim;
        }
        return canvas;
    }

    /// <summary>当前主题下窗口背景图的遮罩画笔；没有背景图时为空。</summary>
    public static IBrush? BackdropScrim(BackdropImage? backdrop) =>
        backdrop is not null && Application.Current is { } application
            ? new SolidColorBrush(ScrimFor(backdrop, application.ActualThemeVariant))
            : null;

    private static void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (!applying) Changed?.Invoke(null, EventArgs.Empty);
    }
}
