using Avalonia;
using Avalonia.Controls;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 锚定浮层的排版容器：子元素（浮层）在同一次排版里按 <see cref="Anchor"/> 定位，先算好位置再画出，打开时不会先在旧位置出现再移动。
/// 锚点矩形在打开时取一次并保持，数据刷新、卡片重排都不移动浮层；只有容器尺寸变化（窗口缩放）时按同一规则重新夹在可见范围内。
/// </summary>
public sealed class AnchoredLayer : Panel
{
    /// <summary>浮层与锚点、与容器四边的间距。</summary>
    public const double Gap = 8;

    public static readonly StyledProperty<Rect?> AnchorProperty = AvaloniaProperty.Register<AnchoredLayer, Rect?>(nameof(Anchor));

    static AnchoredLayer() => AffectsArrange<AnchoredLayer>(AnchorProperty);

    /// <summary>锚点在本容器坐标里的矩形；为空时浮层贴左上角。</summary>
    public Rect? Anchor { get => GetValue(AnchorProperty); set => SetValue(AnchorProperty, value); }

    /// <summary>
    /// 定位规则：下方放得下就贴在锚点下方；否则上方放得下就贴在锚点上方；都放不下时放在空间较大的一侧并夹在容器内（内容在浮层里滚动）。
    /// 水平方向与锚点左边对齐，超出右边时左移、夹在容器内。
    /// </summary>
    public static Rect Place(Size size, Size bounds, Rect anchor)
    {
        var width = Math.Min(size.Width, Math.Max(0, bounds.Width - Gap * 2));
        var height = Math.Min(size.Height, Math.Max(0, bounds.Height - Gap * 2));
        double below = anchor.Bottom + Gap, above = anchor.Top - Gap - height;
        double top;
        if (below + height <= bounds.Height - Gap) top = below;
        else if (above >= Gap) top = above;
        else top = bounds.Height - anchor.Bottom >= anchor.Top ? below : above;
        top = Math.Clamp(top, Gap, Math.Max(Gap, bounds.Height - Gap - height));
        var left = Math.Clamp(anchor.Left, Gap, Math.Max(Gap, bounds.Width - Gap - width));
        return new Rect(left, top, width, height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = new Size(Math.Max(0, availableSize.Width - Gap * 2), Math.Max(0, availableSize.Height - Gap * 2));
        foreach (var child in Children) child.Measure(limit);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
            child.Arrange(Anchor is { } anchor ? Place(child.DesiredSize, finalSize, anchor) : new Rect(new Point(Gap, Gap), child.DesiredSize));
        return finalSize;
    }
}
