using Avalonia;
using Avalonia.Controls;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 主栏加侧栏：宽度不小于 <see cref="Threshold"/> 时并排，侧栏固定 <see cref="SideWidth"/>；
/// 更窄时上下排，两栏都占满宽度。第一个子元素是主栏，第二个是侧栏。
/// </summary>
public sealed class SplitPanel : Panel
{
    public static readonly StyledProperty<double> SideWidthProperty =
        AvaloniaProperty.Register<SplitPanel, double>(nameof(SideWidth), 320);
    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<SplitPanel, double>(nameof(Threshold), 820);
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SplitPanel, double>(nameof(Spacing), 14);

    static SplitPanel() => AffectsMeasure<SplitPanel>(SideWidthProperty, ThresholdProperty, SpacingProperty);

    public double SideWidth { get => GetValue(SideWidthProperty); set => SetValue(SideWidthProperty, value); }
    public double Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private bool SideBySide(double width) => Children.Count > 1 && (double.IsInfinity(width) || width >= Threshold);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return default;
        var width = double.IsInfinity(availableSize.Width) ? Threshold : availableSize.Width;
        if (SideBySide(availableSize.Width))
        {
            Children[1].Measure(new Size(SideWidth, double.PositiveInfinity));
            Children[0].Measure(new Size(Math.Max(0, width - SideWidth - Spacing), double.PositiveInfinity));
            return new Size(width, Math.Max(Children[0].DesiredSize.Height, Children[1].DesiredSize.Height));
        }
        double height = 0;
        foreach (var child in Children.Take(2))
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            height += child.DesiredSize.Height + (height > 0 ? Spacing : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        if (SideBySide(finalSize.Width))
        {
            var main = Math.Max(0, finalSize.Width - SideWidth - Spacing);
            Children[0].Arrange(new Rect(0, 0, main, finalSize.Height));
            Children[1].Arrange(new Rect(main + Spacing, 0, SideWidth, finalSize.Height));
            return finalSize;
        }
        double y = 0;
        foreach (var child in Children.Take(2))
        {
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height + Spacing;
        }
        return finalSize;
    }
}

/// <summary>
/// 标签与数值：一行放得下时标签在左、数值靠右；放不下时上下排，数值换到标签下方。
/// </summary>
public sealed class PairRow : Panel
{
    private const double Gap = 12;
    private bool stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2) return base.MeasureOverride(availableSize);
        var (label, value) = (Children[0], Children[1]);
        label.Measure(Size.Infinity);
        value.Measure(Size.Infinity);
        var natural = label.DesiredSize.Width + Gap + value.DesiredSize.Width;
        stacked = !double.IsInfinity(availableSize.Width) && natural > availableSize.Width;
        if (!stacked)
            return new Size(double.IsInfinity(availableSize.Width) ? natural : availableSize.Width,
                Math.Max(label.DesiredSize.Height, value.DesiredSize.Height));
        label.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        value.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(availableSize.Width, label.DesiredSize.Height + 2 + value.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2) return base.ArrangeOverride(finalSize);
        var (label, value) = (Children[0], Children[1]);
        if (stacked)
        {
            label.Arrange(new Rect(0, 0, finalSize.Width, label.DesiredSize.Height));
            value.Arrange(new Rect(0, label.DesiredSize.Height + 2, finalSize.Width, value.DesiredSize.Height));
            return finalSize;
        }
        label.Arrange(new Rect(0, 0, label.DesiredSize.Width, finalSize.Height));
        value.Arrange(new Rect(finalSize.Width - value.DesiredSize.Width, 0, value.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }
}
