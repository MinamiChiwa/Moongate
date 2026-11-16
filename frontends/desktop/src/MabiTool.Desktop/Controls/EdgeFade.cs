using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 滚动区的边缘淡出：某个方向还有内容时，该边缘渐隐到透明，内容不会被硬切一刀。
/// 全部内容都显示得下时不加遮罩。
/// </summary>
public static class EdgeFade
{
    private const double Length = 28;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(EdgeFade));

    static EdgeFade() => IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((scroller, change) =>
    {
        if (change.NewValue is not true) return;
        scroller.ScrollChanged += (_, _) => Update(scroller);
        scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) Update(scroller);
        };
        Update(scroller);
    });

    public static bool GetIsEnabled(ScrollViewer scroller) => scroller.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer scroller, bool value) => scroller.SetValue(IsEnabledProperty, value);

    private static void Update(ScrollViewer scroller)
    {
        var viewport = scroller.Viewport.Height;
        var above = scroller.Offset.Y > 0.5;
        var below = viewport > 0 && scroller.Offset.Y + viewport < scroller.Extent.Height - 0.5;
        if (viewport <= 0 || (!above && !below))
        {
            scroller.OpacityMask = null;
            return;
        }

        var fade = Math.Min(Length / viewport, 0.4);
        scroller.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(above ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, above ? fade : 0),
                new GradientStop(Colors.Black, below ? 1 - fade : 1),
                new GradientStop(below ? Colors.Transparent : Colors.Black, 1)
            }
        };
    }
}
