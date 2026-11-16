using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MabiTool.Desktop.Controls;

/// <summary>单值进度条：<see cref="Value"/> 取 0 到 1，超出范围按边界绘制。</summary>
public sealed class MeterBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Value));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(Track));

    static MeterBar() => AffectsRender<MeterBar>(ValueProperty, FillProperty, TrackProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 6);

    public override void Render(DrawingContext context)
    {
        var radius = Bounds.Height / 2;
        context.DrawRectangle(Track, null, new Rect(Bounds.Size), radius, radius);
        var ratio = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);
        if (ratio > 0)
            context.DrawRectangle(Fill, null, new Rect(0, 0, Bounds.Width * ratio, Bounds.Height), radius, radius);
    }
}

public sealed record BarSegment(double Weight, IBrush Fill)
{
    public static BarSegment FromHex(string hex, double weight = 1) =>
        new(weight, Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent);
}

/// <summary>
/// 按权重分段的色条：横向用于占比条和颜色预览的一列，纵向用于候选组合的叠放色块。
/// 未设置 Height 时为 12 高的占比条；<see cref="Radius"/> 为负时取半高做成胶囊形。
/// </summary>
public sealed class SegmentBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentBar, IReadOnlyList<BarSegment>?>(nameof(Segments));
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SegmentBar, double>(nameof(Gap), 2);
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<SegmentBar, double>(nameof(Radius), -1);
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<SegmentBar, Orientation>(nameof(Orientation));
    public static readonly StyledProperty<IBrush?> OutlineProperty =
        AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Outline));

    static SegmentBar() =>
        AffectsRender<SegmentBar>(SegmentsProperty, GapProperty, RadiusProperty, OrientationProperty, OutlineProperty);

    public IReadOnlyList<BarSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <summary>浅色色块用的描边，避免与面板底色混在一起。</summary>
    public IBrush? Outline { get => GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 12);

    public override void Render(DrawingContext context)
    {
        var segments = Segments?.Where(segment => segment.Weight > 0).ToList();
        if (segments is null || segments.Count == 0) return;
        var horizontal = Orientation == Orientation.Horizontal;
        var length = horizontal ? Bounds.Width : Bounds.Height;
        var total = segments.Sum(segment => segment.Weight);
        var usable = Math.Max(0, length - Gap * (segments.Count - 1));
        var radius = Radius < 0 ? Math.Min(Bounds.Width, Bounds.Height) / 2 : Radius;
        var whole = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(whole, radius)))
        {
            double offset = 0;
            foreach (var segment in segments)
            {
                var size = usable * segment.Weight / total;
                context.DrawRectangle(segment.Fill, null, horizontal
                    ? new Rect(offset, 0, size, Bounds.Height)
                    : new Rect(0, offset, Bounds.Width, size));
                offset += size + Gap;
            }
        }
        if (Outline is { } outline) context.DrawRectangle(null, new Pen(outline), whole.Deflate(0.5), radius, radius);
    }
}
