using Avalonia;
using Avalonia.Controls;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 等宽多栏排列：栏数由可用宽度和 <see cref="MinItemWidth"/> 决定，放不下时整项换到下一行。
/// 每一行的高度取该行最高的项，内容增高时容器随之增高。
/// </summary>
public sealed class ReflowPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<ReflowPanel, double>(nameof(MinItemWidth), 160);
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ReflowPanel, double>(nameof(Spacing), 10);

    static ReflowPanel() => AffectsMeasure<ReflowPanel>(MinItemWidthProperty, SpacingProperty);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private int ColumnsFor(double width, int count)
    {
        if (count == 0) return 1;
        if (double.IsInfinity(width)) return count;
        var columns = (int)Math.Floor((width + Spacing) / (MinItemWidth + Spacing));
        return Math.Clamp(columns, 1, count);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(child => child.IsVisible).ToList();
        var columns = ColumnsFor(availableSize.Width, visible.Count);
        var itemWidth = double.IsInfinity(availableSize.Width)
            ? MinItemWidth
            : Math.Max(0, (availableSize.Width - Spacing * (columns - 1)) / columns);
        double height = 0, rowHeight = 0;
        for (var index = 0; index < visible.Count; index++)
        {
            visible[index].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[index].DesiredSize.Height);
            if (index % columns == columns - 1 || index == visible.Count - 1)
            {
                height += rowHeight + (height > 0 ? Spacing : 0);
                rowHeight = 0;
            }
        }
        var width = double.IsInfinity(availableSize.Width)
            ? columns * itemWidth + Spacing * (columns - 1)
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(child => child.IsVisible).ToList();
        var columns = ColumnsFor(finalSize.Width, visible.Count);
        var itemWidth = Math.Max(0, (finalSize.Width - Spacing * (columns - 1)) / columns);
        double y = 0;
        for (var start = 0; start < visible.Count; start += columns)
        {
            var row = visible.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(child => child.DesiredSize.Height);
            for (var column = 0; column < row.Count; column++)
                row[column].Arrange(new Rect(column * (itemWidth + Spacing), y, itemWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
