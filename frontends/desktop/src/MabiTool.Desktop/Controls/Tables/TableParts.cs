using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Controls.Tables;

/// <summary>一列在表格里的位置与宽度（表格内容坐标）。</summary>
public readonly record struct ColumnSlot(TableColumn Column, double X, double Width);

/// <summary>按给定的横向位置与宽度排放子元素的一行（表头与列组标题共用）。</summary>
internal sealed class TableStrip : Panel
{
    private readonly List<(Control Child, double X, double Width)> slots = [];

    /// <summary>换成这一组子元素：留下来的子元素不取下再挂上（那样会重新套用样式），只增删差出来的。</summary>
    public void SetSlots(IEnumerable<(Control Child, double X, double Width)> items)
    {
        slots.Clear();
        slots.AddRange(items);
        var keep = slots.Select(slot => slot.Child).ToHashSet();
        for (var i = Children.Count - 1; i >= 0; i--)
            if (!keep.Contains(Children[i])) Children.RemoveAt(i);
        foreach (var slot in slots)
            if (!Children.Contains(slot.Child)) Children.Add(slot.Child);
        InvalidateMeasure();
    }

    public void Move(IReadOnlyList<(Control Child, double X, double Width)> items)
    {
        slots.Clear();
        slots.AddRange(items);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0, right = 0;
        foreach (var (child, x, width) in slots)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
            right = Math.Max(right, x + width);
        }
        return new Size(right, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, x, width) in slots) child.Arrange(new Rect(x, 0, width, finalSize.Height));
        return finalSize;
    }
}

/// <summary>
/// 表体一个显示位置的槽：表体集合里的项，刷新时只换其中的行对象，槽本身与集合不变。
/// 与行对象比较时按槽里的行对象比较，所以 <c>List.ContainerFromItem(行对象)</c>、<c>Items.IndexOf(行对象)</c> 照常找到该行；
/// 两个槽之间只按实例比较。
/// </summary>
internal sealed class TableSlot(object row)
{
    public object Row { get; set; } = row;

    public override bool Equals(object? obj) => obj is TableSlot ? ReferenceEquals(this, obj) : Equals(Row, obj);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>
/// 表体列表：项是 <see cref="TableSlot"/>，行容器的内容与 <c>DataContext</c> 是槽里的行对象，
/// 所以页面拿到的行容器、行右键的发送者与单元格模板看到的仍是行对象本身。
/// </summary>
internal sealed class TableList : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is ContentControl content && item is TableSlot slot) Show(content, slot);
    }

    /// <summary>把槽里的行对象放进该位置的容器；行对象没变时不动，容器不回收也不重建。</summary>
    internal static void Show(ContentControl container, TableSlot slot)
    {
        if (ReferenceEquals(container.DataContext, slot.Row) && ReferenceEquals(container.Content, slot.Row)) return;
        container.DataContext = slot.Row;
        container.Content = slot.Row;
    }
}


internal static class AutomationHelpers
{
    public static void Name(Control control, string name) => Avalonia.Automation.AutomationProperties.SetName(control, name);
}

/// <summary>表头单元格：列名、排序标记、筛选按钮与右侧的列宽拖动条。</summary>
internal sealed class TableHeaderCell : Border
{
    public TableHeaderCell(TableColumn column)
    {
        Column = column;
        Background = Brushes.Transparent;
        Classes.Add("tableheader");
        Title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        Title.Classes.Add("muted");
        SortGlyph = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 10, Margin = new Thickness(4, 0, 0, 0), IsVisible = false };
        SortGlyph.Classes.Add("muted");
        FilterButton = new TableFilterGlyph();
        Gripper = new Border { Width = 8, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, Cursor = new Cursor(StandardCursorType.SizeWestEast) };
        Gripper.Classes.Add("tablegrip");
        var dock = new DockPanel { Margin = new Thickness(10, 4, 8, 4), LastChildFill = true };
        DockPanel.SetDock(FilterButton, Dock.Right);
        DockPanel.SetDock(SortGlyph, Dock.Right);
        dock.Children.Add(FilterButton);
        dock.Children.Add(SortGlyph);
        dock.Children.Add(Title);
        if (column.Kind == ColumnKind.Number) Title.TextAlignment = TextAlignment.Right;
        Child = new Panel { Children = { dock, Gripper } };
    }

    public TableColumn Column { get; }
    public TextBlock Title { get; }
    public TextBlock SortGlyph { get; }
    public TableFilterGlyph FilterButton { get; }
    public Border Gripper { get; }
}

/// <summary>
/// 表头的筛选按钮：一个圆形底加漏斗图形，按下并在按钮上松开、或获得焦点时按 Enter／空格触发 <see cref="Click"/>。
/// 不用 <see cref="Button"/>：宽表每列一个，按钮模板（外层面板、外观层、内容呈现器）在首次排版时逐个实例化，
/// 32 列的 DPS 明细表仅这一项就占约 50 ms。外观由 <c>Border.tablefilter</c> 样式给出（悬停、筛选生效时的 active）。
/// </summary>
internal sealed class TableFilterGlyph : Border
{
    private static readonly Geometry Funnel = Geometry.Parse("M0,0 H10 L6,5 V9 L4,8 V5 Z");
    private bool pressed;

    public TableFilterGlyph()
    {
        Width = 22;
        Height = 22;
        Margin = new Thickness(2, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        CornerRadius = new CornerRadius(11);
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Classes.Add("tablefilter");
        Child = new Avalonia.Controls.Shapes.Path
        {
            Data = Funnel, StrokeThickness = 1, Width = 10, Height = 9, Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    public event EventHandler<RoutedEventArgs>? Click;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        pressed = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!pressed) return;
        pressed = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) Click?.Invoke(this, new RoutedEventArgs());
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        pressed = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        Click?.Invoke(this, new RoutedEventArgs());
    }
}
