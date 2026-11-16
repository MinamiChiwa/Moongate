using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 选色浮层的色相环与饱和度／明度方块（类似 SAI 的选色器），自绘。色相 0° 红色在正上方、顺时针增加；
/// 方块横向饱和度 0→100、纵向明度 100→0。按下即取值，拖动时实时写回：在色相环上按下只改色相，在方块里按下只改饱和度与明度
/// （拖出边界时夹在边界）。方向键左右改饱和度、上下改明度，PageUp／PageDown 改色相。
/// </summary>
public sealed class HsvWheel : Control
{
    public const double RingWidth = 18;
    private const int Segments = 120;

    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<HsvWheel, double>(nameof(Hue), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<HsvWheel, double>(nameof(Saturation), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<HsvWheel, double>(nameof(Brightness), defaultBindingMode: BindingMode.TwoWay);

    private static readonly IBrush[] SegmentBrushes = Enumerable.Range(0, Segments)
        .Select(index => (IBrush)new ImmutableSolidColorBrush(Color.Parse(ColorMath.FromHsv((index + 0.5) * 360.0 / Segments, 100, 100)))).ToArray();
    private static readonly IBrush WhiteFade = Gradient(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), horizontal: true);
    private static readonly IBrush BlackFade = Gradient(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0), horizontal: false);
    private static readonly IPen MarkerOuter = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 3.5);
    private static readonly IPen MarkerInner = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 1.6);

    private enum Drag { None, Ring, Square }

    private Drag drag;
    private Size geometrySize;
    private StreamGeometry[] wedges = [];

    static HsvWheel()
    {
        AffectsRender<HsvWheel>(HueProperty, SaturationProperty, BrightnessProperty);
        FocusableProperty.OverrideDefaultValue<HsvWheel>(true);
        CursorProperty.OverrideDefaultValue<HsvWheel>(new Cursor(StandardCursorType.Cross));
    }

    public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }
    public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Brightness { get => GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

    private double Side => Math.Min(Bounds.Width, Bounds.Height);
    private Point Middle => new(Bounds.Width / 2, Bounds.Height / 2);
    private double Outer => Side / 2 - 1;
    private double Inner => Outer - RingWidth;

    /// <summary>内接于色相环内侧的方块，四角与环之间留 4 的空隙。</summary>
    public Rect Square
    {
        get
        {
            var half = Math.Max(0, (Inner - 4) / Math.Sqrt(2));
            return new Rect(Middle.X - half, Middle.Y - half, half * 2, half * 2);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(double.IsInfinity(availableSize.Width) ? 184 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 184 : availableSize.Height);
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        if (Side <= RingWidth * 2) return;
        if (geometrySize != Bounds.Size) BuildWedges();
        for (var index = 0; index < Segments; index++) context.DrawGeometry(SegmentBrushes[index], null, wedges[index]);

        var square = Square;
        context.DrawRectangle(new ImmutableSolidColorBrush(Color.Parse(ColorMath.FromHsv(Hue, 100, 100))), null, square);
        context.DrawRectangle(WhiteFade, null, square);
        context.DrawRectangle(BlackFade, null, square);

        var angle = Hue * Math.PI / 180;
        var radius = (Outer + Inner) / 2;
        var ring = new Point(Middle.X + radius * Math.Sin(angle), Middle.Y - radius * Math.Cos(angle));
        Marker(context, ring, RingWidth / 2 - 1);
        var cursor = new Point(square.X + Math.Clamp(Saturation, 0, 100) / 100 * square.Width,
            square.Y + (1 - Math.Clamp(Brightness, 0, 100) / 100) * square.Height);
        Marker(context, cursor, 6);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        var distance = Distance(point);
        drag = distance >= Inner - 3 && distance <= Outer + 3 ? Drag.Ring
            : Square.Inflate(4).Contains(point) ? Drag.Square : Drag.None;
        if (drag == Drag.None) return;
        Focus();
        e.Pointer.Capture(this);
        Apply(point);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (drag != Drag.None) Apply(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (drag == Drag.None) return;
        drag = Drag.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        drag = Drag.None;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEffectivelyEnabled) return;
        switch (e.Key)
        {
            case Key.Left: Saturation = Math.Clamp(Math.Round(Saturation) - 1, 0, 100); break;
            case Key.Right: Saturation = Math.Clamp(Math.Round(Saturation) + 1, 0, 100); break;
            case Key.Up: Brightness = Math.Clamp(Math.Round(Brightness) + 1, 0, 100); break;
            case Key.Down: Brightness = Math.Clamp(Math.Round(Brightness) - 1, 0, 100); break;
            case Key.PageUp: Hue = (Math.Round(Hue) + 5) % 360; break;
            case Key.PageDown: Hue = (Math.Round(Hue) + 355) % 360; break;
            default: return;
        }
        e.Handled = true;
    }

    private double Distance(Point point) => Math.Sqrt(Math.Pow(point.X - Middle.X, 2) + Math.Pow(point.Y - Middle.Y, 2));

    private void Apply(Point point)
    {
        if (drag == Drag.Ring)
        {
            var degrees = Math.Atan2(point.X - Middle.X, Middle.Y - point.Y) * 180 / Math.PI;
            var hue = (degrees + 360) % 360;
            if (hue != Hue) Hue = hue;
            return;
        }
        var square = Square;
        if (square.Width <= 0) return;
        var saturation = Math.Clamp((point.X - square.X) / square.Width, 0, 1) * 100;
        var brightness = (1 - Math.Clamp((point.Y - square.Y) / square.Height, 0, 1)) * 100;
        // 两个值分别写回，连续的保存由区域卡的防抖合并成一次。
        if (saturation != Saturation) Saturation = saturation;
        if (brightness != Brightness) Brightness = brightness;
    }

    private void BuildWedges()
    {
        geometrySize = Bounds.Size;
        wedges = new StreamGeometry[Segments];
        var step = 2 * Math.PI / Segments;
        // 相邻扇段重叠 0.4°，避免抗锯齿在接缝处露出底色。
        var overlap = 0.4 * Math.PI / 180;
        for (var index = 0; index < Segments; index++)
        {
            double from = index * step - overlap, to = (index + 1) * step + overlap;
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                sink.BeginFigure(On(Outer, from), true);
                sink.ArcTo(On(Outer, to), new Size(Outer, Outer), 0, false, SweepDirection.Clockwise);
                sink.LineTo(On(Inner, to));
                sink.ArcTo(On(Inner, from), new Size(Inner, Inner), 0, false, SweepDirection.CounterClockwise);
                sink.EndFigure(true);
            }
            wedges[index] = geometry;
        }
    }

    private Point On(double radius, double angle) => new(Middle.X + radius * Math.Sin(angle), Middle.Y - radius * Math.Cos(angle));

    private static void Marker(DrawingContext context, Point center, double radius)
    {
        context.DrawEllipse(null, MarkerOuter, center, radius, radius);
        context.DrawEllipse(null, MarkerInner, center, radius, radius);
    }

    private static IBrush Gradient(Color from, Color to, bool horizontal) => new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, from), new ImmutableGradientStop(1, to)],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
        endPoint: horizontal ? new RelativePoint(1, 0, RelativeUnit.Relative) : new RelativePoint(0, 1, RelativeUnit.Relative));
}
