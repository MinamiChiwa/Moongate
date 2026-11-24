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
        ["color.current"] = (new(288, 232), new(200, 120)),
        // 默认与最小高度都含底部的怪物血量区块（三行约 48）：最小尺寸下标题、主 DPS 与区块正好放下，其余指标在元件内滚动；
        // 区块收起时腾出的高度给上方指标。
        ["combat.dps"] = (new(240, 160), new(168, 152)),
        ["combat.skills"] = (new(240, 136), new(160, 80)),
        ["combat.coverage"] = (new(256, 136), new(176, 88)),
        ["combat.self-effects"] = (new(352, 96), new(112, 96)),
        ["combat.target-effects"] = (new(352, 120), new(136, 120)),
        ["combat.measurement"] = (new(248, 168), new(184, 144)),
        ["combat.healing"] = (new(240, 128), new(168, 112)),
        // 收藏浮窗：默认竖排约 5 项；最小一项加“+N”。
        [FavoriteBoard.First] = (new(256, 128), new(136, 64)),
        [FavoriteBoard.Second] = (new(256, 128), new(136, 64)),
        // 觉醒监控：竖排两项；横排时放宽即并排。
        [AwakeningBoard.Id] = (new(256, 72), new(136, 64))
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
