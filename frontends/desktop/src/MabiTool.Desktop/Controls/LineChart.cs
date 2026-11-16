using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 自绘折线图：<see cref="Values"/> 按等间距排开，纵轴从 0 到最大值；画三条水平参考线、折线与其下方的浅色填充。
/// 颜色全部取自属性（页面以动态资源绑定），两套主题下随令牌变化。没有数据时只画参考线。
/// </summary>
public sealed class LineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<LineChart, IReadOnlyList<double>?>(nameof(Values));
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<LineChart, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> GridProperty =
        AvaloniaProperty.Register<LineChart, IBrush?>(nameof(Grid));

    static LineChart() => AffectsRender<LineChart>(ValuesProperty, StrokeProperty, GridProperty);

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Grid { get => GetValue(GridProperty); set => SetValue(GridProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 140);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var grid = new Pen(Grid, 1);
        for (var line = 0; line <= 2; line++)
        {
            var y = Math.Round(size.Height * line / 2) + 0.5;
            context.DrawLine(grid, new Point(0, Math.Min(y, size.Height - 0.5)), new Point(size.Width, Math.Min(y, size.Height - 0.5)));
        }
        if (Values is not { Count: > 0 } values) return;
        var max = values.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        if (max <= 0) max = 1;
        Point At(int index) => new(values.Count == 1 ? size.Width / 2 : size.Width * index / (values.Count - 1),
            size.Height - Math.Clamp(double.IsFinite(values[index]) ? values[index] / max : 0, 0, 1) * (size.Height - 2) - 1);
        var line2 = new StreamGeometry();
        using (var ctx = line2.Open())
        {
            ctx.BeginFigure(At(0), false);
            for (var i = 1; i < values.Count; i++) ctx.LineTo(At(i));
            ctx.EndFigure(false);
        }
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(At(0).X, size.Height), true);
            for (var i = 0; i < values.Count; i++) ctx.LineTo(At(i));
            ctx.LineTo(new Point(At(values.Count - 1).X, size.Height));
            ctx.EndFigure(true);
        }
        if (Stroke is ISolidColorBrush solid)
            context.DrawGeometry(new SolidColorBrush(solid.Color, 0.16), null, area);
        context.DrawGeometry(null, new Pen(Stroke, 2, lineJoin: PenLineJoin.Round), line2);
    }
}
