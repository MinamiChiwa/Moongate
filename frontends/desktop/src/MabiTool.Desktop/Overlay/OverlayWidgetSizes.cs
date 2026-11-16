using Avalonia;
using MabiTool.Desktop.Modules;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 元件外框在缩放前的默认与最小尺寸（逻辑像素，含外框内边距）。内建元件在这里登记；
/// 扩展元件在 <see cref="WidgetDescriptor.DefaultSize"/>、<see cref="WidgetDescriptor.MinimumSize"/> 里声明。
/// </summary>
public static class OverlayWidgetSizes
{
    /// <summary>扩展元件没有声明最小尺寸时的下限。</summary>
    public static readonly Size FallbackMinimum = new(96, 48);

    private static readonly Dictionary<string, (Size Default, Size Minimum)> BuiltIn = new(StringComparer.Ordinal)
    {
        ["performance.now-playing"] = (new(272, 104), new(176, 104)),
        ["performance.nearby"] = (new(304, 48), new(168, 48)),
        ["rooms.readiness"] = (new(312, 344), new(248, 232)),
        ["color.current"] = (new(248, 144), new(176, 96)),
        ["combat.dps"] = (new(240, 112), new(168, 104)),
        ["combat.skills"] = (new(240, 136), new(160, 80)),
        ["combat.coverage"] = (new(256, 136), new(176, 88)),
        ["combat.self-effects"] = (new(352, 96), new(112, 96)),
        ["combat.target-effects"] = (new(352, 120), new(136, 120)),
        ["combat.measurement"] = (new(248, 168), new(184, 144))
    };

    /// <summary>元件的默认尺寸；扩展元件没有声明时为空（按内容的自然尺寸显示）。</summary>
    public static Size? DefaultOf(WidgetDescriptor widget) =>
        widget.DefaultSize ?? (BuiltIn.TryGetValue(widget.Id, out var sizes) ? sizes.Default : null);

    public static Size MinimumOf(WidgetDescriptor widget) =>
        widget.MinimumSize ?? (BuiltIn.TryGetValue(widget.Id, out var sizes) ? sizes.Minimum : FallbackMinimum);

    /// <summary>
    /// 元件实际使用的尺寸：保存的宽高优先，没有时取默认尺寸，都不小于最小尺寸；
    /// 没有保存值也没有默认尺寸时为空，按内容的自然尺寸显示。
    /// </summary>
    public static Size? Resolve(WidgetPlacement placement, Size? fallback, Size minimum)
    {
        var width = placement.Width ?? fallback?.Width;
        var height = placement.Height ?? fallback?.Height;
        if (width is null || height is null) return null;
        return new Size(Math.Max(width.Value, minimum.Width), Math.Max(height.Value, minimum.Height));
    }
}
