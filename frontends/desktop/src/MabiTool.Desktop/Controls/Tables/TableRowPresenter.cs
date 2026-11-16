using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Controls.Tables;

/// <summary>
/// 表格的一行：按表格当前的列位置排放各列，行详情（有时）排在单元格下方占满整行。
/// <para>
/// 文字列不再是一格一个 <see cref="TextBlock"/>：整行一个控件，按列宽用 <see cref="TextLayout"/> 自己画出各列文字
/// （左右各留 10、单行省略号截断、数字列右对齐；<c>strong</c> 加粗，次要文字用 <c>MutedBrush</c>），排版结果按格缓存，
/// 某一格的文字变化只重排那一格并重画这一行，不重新测量。宽表（DPS 明细约 30 列）一行少建几十个控件、少套几十次样式。
/// 模板列（按钮、下拉、图标等可交互内容）仍是真实控件。悬停提示按指针所在的列给出，读屏按列给出“列名：文字”。
/// </para>
/// 列顺序、显隐或宽度变化时由表格通知重排；行对象变化（刷新或虚拟化回收）时重取文字。
/// </summary>
internal sealed class TableRowPresenter : Panel
{
    private const double CellPaddingX = 10;
    private const double CellPaddingY = 7;

    // 一格：模板列有 Control；文字列只有文字与缓存的排版。
    private sealed class Cell(TableColumn column, Control? control)
    {
        public TableColumn Column { get; } = column;
        public Control? Control { get; } = control;
        public string Text = string.Empty;
        public bool Dim;
        public string? Tip;
        public TextLayout? Layout;
        public double LayoutWidth = -1;
        // 排版所用的画笔、字体与字号；任何一项与这次绘制不同时才重新排版（页面切走再回来、主题切换回原样时留用）。
        public IBrush? LayoutBrush;
        public FontFamily? LayoutFamily;
        public double LayoutSize;
        public bool Strong => (Column.CellClasses ?? string.Empty).Split(' ').Contains("strong");
    }

    private static readonly Dictionary<(FontFamily, double), double> LineHeights = [];

    private readonly DataTable table;
    private readonly List<Cell> cells = [];
    private ContentControl? detail;
    private int version = -1;
    private TextBox? editor;
    private TableColumn? editing;
    private double rowHeight;

    public TableRowPresenter(DataTable table)
    {
        this.table = table;
        Background = Brushes.Transparent;
        layer = new TextLayer(this) { IsHitTestVisible = false };
        Children.Add(layer);
        DoubleTapped += OnDoubleTapped;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public bool IsEditing => editor is not null;

    /// <summary>各文字列此刻画出的文字（按列顺序，列名与文字）；读屏与测试用。</summary>
    internal IReadOnlyList<(string Header, string Text)> CellTexts =>
        cells.Where(cell => cell.Control is null).Select(cell => (table.HeaderText(cell.Column), cell.Text)).ToList();

    /// <summary>第一个文字符合条件的文字列在这一行里的范围（行坐标）；测试按文字找格用。</summary>
    internal Rect? TextCellBounds(Func<string, bool> match) =>
        cells.FirstOrDefault(cell => cell.Control is null && match(cell.Text)) is { } found ? CellBounds(found.Column.Id) : null;

    /// <summary>某一列在这一行里的范围（行坐标）；没有该列时为空。</summary>
    internal Rect? CellBounds(string columnId)
    {
        var slot = table.Slots.FirstOrDefault(item => item.Column.Id == columnId);
        return slot.Column is null ? null : new Rect(slot.X, 0, slot.Width, rowHeight);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        table.LayoutChanged += OnLayoutChanged;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        table.LayoutChanged -= OnLayoutChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        EndEdit(false);
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 继承来的字体与前景色（选中行、主题、缩放）变化时重画；排版缓存按画笔、字体与字号比对，真的不同才重排。
        if (change.Property == TextElement.ForegroundProperty) InvalidateVisual();
        else if (change.Property == TextElement.FontSizeProperty || change.Property == TextElement.FontFamilyProperty)
        {
            InvalidateVisual();
            InvalidateMeasure();
        }
    }

    private readonly TextLayer layer;

    private new void InvalidateVisual() => layer.InvalidateVisual();



    private void OnLayoutChanged(object? sender, EventArgs e) => Sync();

    /// <summary>
    /// 列结构变化时按新的列重排，否则只更新文字。模板列的控件按模板实例留用，只增删差出来的；
    /// 文字列只是一组数据，重排不建控件。
    /// </summary>
    public void Sync()
    {
        if (version != table.LayoutVersion)
        {
            EndEdit(false);
            version = table.LayoutVersion;
            var spare = new Dictionary<IDataTemplateKey, Queue<Control>>();
            foreach (var cell in cells.Where(cell => cell.Control is not null))
            {
                var key = new IDataTemplateKey(cell.Column.CellTemplate!);
                if (!spare.TryGetValue(key, out var queue)) spare[key] = queue = new Queue<Control>();
                queue.Enqueue(cell.Control!);
            }
            cells.Clear();
            foreach (var slot in table.Slots)
            {
                if (slot.Column.CellTemplate is not { } template)
                {
                    cells.Add(new Cell(slot.Column, null));
                    continue;
                }
                if (spare.TryGetValue(new IDataTemplateKey(template), out var queue) && queue.Count > 0)
                {
                    cells.Add(new Cell(slot.Column, queue.Dequeue()));
                    continue;
                }
                var control = new ContentControl
                {
                    ContentTemplate = template, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center
                };
                cells.Add(new Cell(slot.Column, control));
                Children.Add(control);
            }
            foreach (var unused in spare.Values.SelectMany(queue => queue)) Children.Remove(unused);
            if (table.RowDetailTemplate is { } detailTemplate)
            {
                if (detail is null || !ReferenceEquals(detail.ContentTemplate, detailTemplate))
                {
                    if (detail is not null) Children.Remove(detail);
                    detail = new ContentControl { ContentTemplate = detailTemplate, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    Children.Add(detail);
                }
            }
            else if (detail is not null)
            {
                Children.Remove(detail);
                detail = null;
            }
            InvalidateMeasure();
        }
        Refresh();
    }

    // 模板实例作为键（按引用比较）。
    private readonly record struct IDataTemplateKey(object Template)
    {
        public bool Equals(IDataTemplateKey other) => ReferenceEquals(Template, other.Template);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Template);
    }

    /// <summary>重取各列的文字与提示；文字变了的格丢掉排版缓存并重画这一行，行高不变时不重新测量。</summary>
    public void Refresh()
    {
        var row = DataContext is TableSlot slot ? slot.Row : DataContext;
        var changed = false;
        foreach (var cell in cells)
        {
            var tip = cell.Column.ToolTipOf(row);
            if (cell.Control is ContentControl content)
            {
                content.Content = row;
                ToolTip.SetTip(content, tip);
                continue;
            }
            var text = cell.Column.TextOf(row);
            var dim = cell.Column.IsDim(row);
            cell.Tip = tip;
            if (text == cell.Text && dim == cell.Dim && cell.Layout is not null) continue;
            cell.Text = text;
            cell.Dim = dim;
            cell.Layout = null;
            changed = true;
        }
        if (detail is not null) detail.Content = row;
        if (changed) InvalidateVisual();
    }

    /// <summary>该位置（行坐标）所在的列。</summary>
    public TableColumn? ColumnAt(double x) => table.Slots.FirstOrDefault(slot => x >= slot.X && x < slot.X + slot.Width).Column;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var column = ColumnAt(e.GetPosition(this).X);
        var tip = cells.FirstOrDefault(cell => cell.Column == column && cell.Control is null)?.Tip;
        if (!Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetTip(this, null);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ColumnAt(e.GetPosition(this).X) is { Editable: true } column)
        {
            BeginEdit(column);
            e.Handled = true;
        }
    }

    /// <summary>在该列的单元格上打开编辑框：Enter 确认、Esc 取消，离开编辑框时确认。</summary>
    public void BeginEdit(TableColumn column)
    {
        if (!column.Editable || DataContext is null) return;
        EndEdit(false);
        var index = cells.FindIndex(item => item.Column == column);
        if (index < 0) return;
        editing = column;
        editor = new TextBox { Text = column.TextOf(DataContext), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 2) };
        editor.Classes.Add("tableedit");
        AutomationHelpers.Name(editor, table.HeaderText(column));
        editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        editor.LostFocus += (_, _) => EndEdit(true);
        if (cells[index].Control is { } control) control.IsVisible = false;
        Children.Add(editor);
        InvalidateMeasure();
        InvalidateVisual();
        editor.AttachedToVisualTree += (_, _) => { editor?.Focus(); editor?.SelectAll(); };
        if (editor.IsAttachedToVisualTree()) { editor.Focus(); editor.SelectAll(); }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { EndEdit(true); table.FocusRow(DataContext); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndEdit(false); table.FocusRow(DataContext); e.Handled = true; }
    }

    public void EndEdit(bool commit)
    {
        if (editor is not { } box || editing is not { } column) return;
        editor = null;
        editing = null;
        var row = DataContext;
        Children.Remove(box);
        foreach (var cell in cells) if (cell.Control is { } control) control.IsVisible = true;
        InvalidateMeasure();
        InvalidateVisual();
        if (commit && row is not null && box.Text != column.TextOf(row))
        {
            column.Commit?.Invoke(row, box.Text ?? string.Empty);
            table.Refresh();
        }
    }

    private Typeface Face(bool strong) =>
        new(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, strong ? FontWeight.Bold : FontWeight.Normal);

    private double FontSize => GetValue(TextElement.FontSizeProperty);

    // 单行文字的高度：同一字体字号只量一次。
    private double LineHeight()
    {
        var key = (GetValue(TextElement.FontFamilyProperty), FontSize);
        if (LineHeights.TryGetValue(key, out var height)) return height;
        using var layout = new TextLayout("Ag国", Face(false), FontSize, null);
        return LineHeights[key] = layout.Height;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var slots = table.Slots;
        double height = cells.Any(cell => cell.Control is null) ? LineHeight() + CellPaddingY * 2 : 0;
        for (var i = 0; i < cells.Count && i < slots.Count; i++)
        {
            if (cells[i].Control is not { } control) continue;
            control.Measure(new Size(slots[i].Width, double.PositiveInfinity));
            height = Math.Max(height, control.DesiredSize.Height);
        }
        if (editor is not null && editing is not null && slots.FirstOrDefault(slot => slot.Column == editing) is { Column: not null } edit)
        {
            editor.Measure(new Size(edit.Width, double.PositiveInfinity));
            height = Math.Max(height, editor.DesiredSize.Height);
        }
        rowHeight = height;
        layer.Measure(Size.Infinity);
        var total = table.TotalWidth;
        double extra = 0;
        if (detail is not null)
        {
            detail.Measure(new Size(total, double.PositiveInfinity));
            extra = detail.DesiredSize.Height;
        }
        return new Size(total, height + extra);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var slots = table.Slots;
        for (var i = 0; i < cells.Count && i < slots.Count; i++)
            cells[i].Control?.Arrange(new Rect(slots[i].X, 0, slots[i].Width, rowHeight));
        if (editor is not null && editing is not null && slots.FirstOrDefault(slot => slot.Column == editing) is { Column: not null } edit)
            editor.Arrange(new Rect(edit.X, 0, edit.Width, rowHeight));
        detail?.Arrange(new Rect(0, rowHeight, Math.Max(finalSize.Width, table.TotalWidth), detail.DesiredSize.Height));
        layer.Arrange(new Rect(0, 0, Math.Max(finalSize.Width, table.TotalWidth), rowHeight));
        return finalSize;
    }

    // 文字层：一行一个，铺满整行、不参与命中测试，画出全部文字列（Panel 自己的 Render 不能重写）。
    private sealed class TextLayer(TableRowPresenter owner) : Control
    {
        public override void Render(DrawingContext context) => owner.DrawTexts(context);
    }

    private void DrawTexts(DrawingContext context)
    {
        var slots = table.Slots;
        var foreground = GetValue(TextElement.ForegroundProperty);
        var mutedBrush = this.TryFindResource("MutedBrush", ActualThemeVariant, out var muted) ? muted as IBrush : null;
        var family = GetValue(TextElement.FontFamilyProperty);
        var size = FontSize;
        for (var i = 0; i < cells.Count && i < slots.Count; i++)
        {
            var cell = cells[i];
            if (cell.Control is not null || cell.Column == editing || cell.Text.Length == 0) continue;
            var slot = slots[i];
            var width = slot.Width - CellPaddingX * 2;
            if (width <= 0) continue;
            var brush = cell.Dim ? mutedBrush ?? foreground : foreground;
            if (cell.Layout is null || Math.Abs(cell.LayoutWidth - width) > 0.5 || !ReferenceEquals(cell.LayoutBrush, brush)
                || !Equals(cell.LayoutFamily, family) || cell.LayoutSize != size)
            {
                cell.Layout?.Dispose();
                cell.LayoutBrush = brush;
                cell.LayoutFamily = family;
                cell.LayoutSize = size;
                cell.Layout = new TextLayout(cell.Text, Face(cell.Strong), size, brush,
                    textAlignment: cell.Column.Kind == ColumnKind.Number ? TextAlignment.Right : TextAlignment.Left,
                    textTrimming: TextTrimming.CharacterEllipsis, maxWidth: width);
                cell.LayoutWidth = width;
            }
            using (context.PushClip(new Rect(slot.X, 0, slot.Width, rowHeight)))
                cell.Layout.Draw(context, new Point(slot.X + CellPaddingX, Math.Max(0, (rowHeight - cell.Layout.Height) / 2)));
        }
    }

    /// <summary>读屏：这一行按列给出“列名：文字”，模板列里的控件照常是子项。</summary>
    private sealed class RowPeer(TableRowPresenter owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore() =>
            string.Join("，", owner.CellTexts.Where(cell => cell.Text.Length > 0).Select(cell => $"{cell.Header}：{cell.Text}"));

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;
    }
}
