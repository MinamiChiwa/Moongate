using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>
/// 浮窗效果元件的单列面板：按可用高度只排能完整显示的行，放不下时最后一行改为“+N”（N 为未显示的效果数）。
/// 浮窗上锁时元件不能滚动，所以数据里排在前面的（收藏）总是可见。
/// </summary>
public sealed class EffectOverflowPanel : Panel
{
    private int shown, hidden;
    private double rowHeight;
    private readonly TextBlock more = new() { Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Border indicator;

    public EffectOverflowPanel()
    {
        indicator = new Border { Background = new SolidColorBrush(EffectLine.OverlayTrackColor), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 0), Child = more, IsVisible = false, IsHitTestVisible = false };
        VisualChildren.Add(indicator);
    }

    /// <summary>未显示的效果数；全部放得下时为 0。</summary>
    public int HiddenCount => hidden;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width;
        rowHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        var count = Children.Count;
        if (count == 0 || rowHeight <= 0) { shown = count; hidden = 0; }
        else
        {
            var capacity = double.IsInfinity(availableSize.Height) ? count : Math.Max(1, (int)Math.Floor(availableSize.Height / rowHeight));
            if (count <= capacity) { shown = count; hidden = 0; }
            else { shown = Math.Max(0, capacity - 1); hidden = count - shown; }
        }
        indicator.IsVisible = hidden > 0;
        more.Text = $"+{hidden.ToString(CultureInfo.InvariantCulture)}";
        indicator.Measure(new Size(width, Math.Max(0, rowHeight - 2)));
        return new(width, (shown + (hidden > 0 ? 1 : 0)) * rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < Children.Count; i++)
            Children[i].Arrange(i < shown ? new Rect(0, i * rowHeight, finalSize.Width, rowHeight) : default);
        indicator.Arrange(hidden > 0 ? new Rect(0, shown * rowHeight + 1, finalSize.Width, Math.Max(0, rowHeight - 2)) : default);
        return finalSize;
    }
}
