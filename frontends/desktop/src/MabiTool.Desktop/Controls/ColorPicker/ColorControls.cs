using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 染色页的整数滑杆（原工具 UiSlider 与选色浮层的 HSL 滑杆）：轨道可以是令牌颜色或渐变，按下、拖动与方向键改值。
/// </summary>
public sealed class DyeSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<DyeSlider, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<DyeSlider, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<DyeSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<DyeSlider, IBrush?>(nameof(Track));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<DyeSlider, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> KnobProperty = AvaloniaProperty.Register<DyeSlider, IBrush?>(nameof(Knob));
    public static readonly StyledProperty<IBrush?> KnobEdgeProperty = AvaloniaProperty.Register<DyeSlider, IBrush?>(nameof(KnobEdge));

    private bool dragging;

    static DyeSlider()
    {
        AffectsRender<DyeSlider>(ValueProperty, MinimumProperty, MaximumProperty, TrackProperty, FillProperty, KnobProperty, KnobEdgeProperty);
        FocusableProperty.OverrideDefaultValue<DyeSlider>(true);
        CursorProperty.OverrideDefaultValue<DyeSlider>(new Cursor(StandardCursorType.Hand));
    }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Knob { get => GetValue(KnobProperty); set => SetValue(KnobProperty, value); }
    public IBrush? KnobEdge { get => GetValue(KnobEdgeProperty); set => SetValue(KnobEdgeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 18);

    public override void Render(DrawingContext context)
    {
        const double track = 5, knob = 12;
        var width = Bounds.Width;
        var y = (Bounds.Height - track) / 2;
        var ratio = Ratio(Value);
        context.DrawRectangle(Track, null, new RoundedRect(new Rect(0, y, width, track), track / 2));
        if (Fill is { } fill) context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, y, Math.Max(track, width * ratio), track), track / 2));
        var x = Math.Clamp(width * ratio - knob / 2, 0, Math.Max(0, width - knob));
        context.DrawEllipse(Knob ?? Brushes.White, KnobEdge is { } edge ? new Pen(edge, 1) : null,
            new Rect(x, (Bounds.Height - knob) / 2, knob, knob));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        dragging = true;
        e.Pointer.Capture(this);
        Apply(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging) Apply(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!dragging) return;
        dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.Key switch { Key.Left or Key.Down => -1, Key.Right or Key.Up => 1, _ => 0 };
        if (step == 0 || !IsEffectivelyEnabled) return;
        Value = Math.Clamp(Math.Round(Value) + step, Minimum, Maximum);
        e.Handled = true;
    }

    private double Ratio(double value) => Maximum <= Minimum ? 0 : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);

    private void Apply(double x)
    {
        var ratio = Bounds.Width <= 0 ? 0 : Math.Clamp(x / Bounds.Width, 0, 1);
        var value = Math.Round(Minimum + ratio * (Maximum - Minimum));
        if (value != Value) Value = value;
    }
}

/// <summary>
/// 选色浮层的色块格：按 <see cref="Columns"/> 把可用宽度均分，逐行排列。各列边界取整到整数逻辑像素，最后一列止于容器右边，
/// 列宽之和恰好等于容器宽度，任何缩放下都不越出容器（UniformGrid 把每格宽度向上取整，十列累计会超出右边）。
/// 行高取该行子元素期望高度的最大值。
/// </summary>
public sealed class EvenColumns : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<EvenColumns, int>(nameof(Columns), 1);

    static EvenColumns() => AffectsMeasure<EvenColumns>(ColumnsProperty);

    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    private int Count => Math.Max(1, Columns);

    /// <summary>第 <paramref name="column"/> 列在宽度 <paramref name="width"/> 里的左右边界。</summary>
    public static (double Left, double Right) Edges(int column, int columns, double width) =>
        (Math.Floor(column * width / columns), column == columns - 1 ? width : Math.Floor((column + 1) * width / columns));

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var cell = width > 0 ? Math.Floor(width / Count) : double.PositiveInfinity;
        double height = 0, row = 0;
        for (var index = 0; index < Children.Count; index++)
        {
            Children[index].Measure(new Size(cell, double.PositiveInfinity));
            row = Math.Max(row, Children[index].DesiredSize.Height);
            if (index % Count == Count - 1 || index == Children.Count - 1)
            {
                height += row;
                row = 0;
            }
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double top = 0;
        for (var start = 0; start < Children.Count; start += Count)
        {
            var end = Math.Min(Children.Count, start + Count);
            double row = 0;
            for (var index = start; index < end; index++) row = Math.Max(row, Children[index].DesiredSize.Height);
            for (var index = start; index < end; index++)
            {
                var (left, right) = Edges(index - start, Count, finalSize.Width);
                Children[index].Arrange(new Rect(left, top, right - left, row));
            }
            top += row;
        }
        return finalSize;
    }
}
