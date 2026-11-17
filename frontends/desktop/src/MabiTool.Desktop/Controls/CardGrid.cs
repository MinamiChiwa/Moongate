using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 一张卡片在网格里的格位。横向以半列为单位（三列时一行 6 格、两列时 4 格、一列时 1 格）：<see cref="Column"/> 是起始格、<see cref="Span"/> 是所占格数；
/// 纵向是行轨，固定档以半行为一条行轨：<see cref="Row"/> 是起始行轨，<see cref="Rows"/> 是所占的半行数，0 表示随内容档（所在行轨的高度随内容）。
/// </summary>
public readonly record struct CardCell(int Column, int Span, int Row, int Rows);

/// <summary>
/// 页面卡片网格，各模块页共用。子元素是 <see cref="Card"/>，按顺序自左向右、自上而下排入最先放得下的位置。
/// <para>
/// 排版规则：基准 <see cref="BaseColumns"/> 列，列间距与行间距都是 <see cref="Spacing"/>（14）；一行高 <see cref="RowUnit"/>（160），
/// 占 n 行的卡片高度为 160×n + 14×(n−1)，n 以半行为最小单位（半行 73、一行半 247）。可用宽度放不下三列（每列至少
/// <see cref="MinColumnWidth"/>）时降为两列，再降为一列：两列时一列档与半宽档占一列、两列与三列档占满一行，一列时全部占满一行。
/// 固定档高度不变，内容放不下时在卡片内滚动（<see cref="Card.ScrollsBody"/>）。随内容档高度取内容高度：相邻的随内容卡片在一行里放得下时
/// 共用一条行轨，行高取其中最高的一张，展开的卡片拉到行高；全宽档独占一行。收起的卡片保持列宽、高度缩成标题行；
/// 一条行轨上的卡片全部收起时行轨缩到标题行高度，还有展开的卡片时行高不变。不可见的卡片不占位置。
/// </para>
/// <para>
/// 子元素也可以是项目列表生成的容器（把网格用作 <c>ItemsControl</c> 的面板，容器里是 <see cref="Card"/>，例如染色页的区域卡）。
/// </para>
/// <para>
/// 顺序与尺寸档：缺省取子元素的先后与各卡片的 <see cref="Card.Size"/>；用户在编辑模式（<see cref="IsEditing"/>）里调整后，
/// 按“页面标识（<see cref="Card.PageIdProperty"/>）/卡片标识 + 列数”存入 <see cref="CardStateStore"/>，各列数分别保存。
/// 编辑模式内每张卡片盖一层编辑层：卡片内容不响应指针与键盘，按住拖动换位（原处画落点框，其余卡片平滑让位，动效减弱时直接到位）；
/// 卡片标题行右端的操作区让给布局菜单（前移、后移与该卡片适用的尺寸档），菜单不与标题、状态重叠；编辑层可聚焦，
/// 左／上方向键前移、右／下方向键后移。“恢复默认”让各列数回到缺省布局、卡片回到缺省的折叠状态；“完成”保存，“取消”丢弃本次编辑的全部改动。
/// 入口用 <see cref="CardGridEditBar"/>。
/// </para>
/// </summary>
public sealed class CardGrid : Panel
{
    /// <summary>一行的高度。</summary>
    public const double RowUnit = 160;
    /// <summary>基准列数。</summary>
    public const int BaseColumns = 3;
    private const double DefaultSpacing = 14;
    private const double DragThreshold = 4;

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<CardGrid, double>(nameof(Spacing), DefaultSpacing);
    public static readonly StyledProperty<double> MinColumnWidthProperty = AvaloniaProperty.Register<CardGrid, double>(nameof(MinColumnWidth), 280);
    public static readonly StyledProperty<bool> IsEditingProperty = AvaloniaProperty.Register<CardGrid, bool>(nameof(IsEditing));

    private static readonly CubicEaseOut Ease = new();

    private readonly Dictionary<Control, Rect> slots = [];
    private readonly Dictionary<Card, EditLayer> layers = [];
    private readonly Dictionary<int, Working> works = [];
    private readonly Dictionary<Control, Glide> glides = [];
    private readonly Dictionary<Card, bool> expandedAtEnter = [];
    private readonly DropMark mark = new();
    private bool resetAll;
    private bool discard;
    private bool animating;
    private Card? dragged;
    private Point dragStart;
    private Point dragOrigin;
    private Point dragNow;

    static CardGrid() => AffectsMeasure<CardGrid>(SpacingProperty, MinColumnWidthProperty);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>每列的最小宽度；放不下时减少列数。</summary>
    public double MinColumnWidth { get => GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

    /// <summary>编辑模式：为真时才能拖动卡片与改尺寸档；变回假时保存。</summary>
    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    /// <summary>最近一次排版用的列数。</summary>
    public int Columns { get; private set; } = BaseColumns;

    /// <summary>当前列数下的显示顺序（含不可见的卡片）。</summary>
    public IReadOnlyList<Control> Order => Ordered(Columns);

    /// <summary>某张卡片在当前列数下生效的尺寸档。</summary>
    public CardSize SizeOf(Card card) => SizeFor(HostOf(card), Columns);

    // ---- 排版规则（纯计算） ----

    /// <summary>可用宽度下的列数：每列至少 <paramref name="minColumnWidth"/>，最多 <see cref="BaseColumns"/> 列，最少一列。</summary>
    public static int ColumnsFor(double width, double minColumnWidth = 280, double spacing = DefaultSpacing) => double.IsInfinity(width)
        ? BaseColumns
        : Math.Clamp((int)Math.Floor((width + spacing) / (minColumnWidth + spacing)), 1, BaseColumns);

    /// <summary>占 <paramref name="rows"/> 行的卡片高度；行数以半行为最小单位（0.5、1、1.5……）。</summary>
    public static double HeightFor(double rows, double spacing = DefaultSpacing) => rows * RowUnit + (rows - 1) * spacing;

    /// <summary>尺寸档的高度是否固定（不随内容）。</summary>
    public static bool IsFixedHeight(CardSize size) => Dimensions(size).HalfRows > 0;

    /// <summary>最接近 <paramref name="height"/> 的行数（至少一行）。</summary>
    public static int RowsFor(double height, double spacing = DefaultSpacing) =>
        Math.Max(1, (int)Math.Round((height + spacing) / (RowUnit + spacing)));

    private static int UnitsFor(int columns) => columns <= 1 ? 1 : columns * 2;

    // 尺寸档的宽（列数，0 表示半宽）与高（半行数，0 表示随内容）。
    private static (int Wide, int HalfRows) Dimensions(CardSize size) => size switch
    {
        CardSize.OneByOne => (1, 2), CardSize.OneByOneAndHalf => (1, 3), CardSize.OneByTwo => (1, 4), CardSize.OneByTwoAndHalf => (1, 5),
        CardSize.OneByThree => (1, 6), CardSize.OneByFour => (1, 8),
        CardSize.TwoByOne => (2, 2), CardSize.TwoByTwo => (2, 4), CardSize.TwoByThree => (2, 6), CardSize.TwoByFour => (2, 8),
        CardSize.ThreeByOne => (3, 2), CardSize.ThreeByTwo => (3, 4),
        CardSize.HalfByTwo => (0, 4), CardSize.HalfByThree => (0, 6), CardSize.HalfByFour => (0, 8),
        CardSize.OneByAuto => (1, 0), CardSize.TwoByAuto => (2, 0), CardSize.HalfByAuto => (0, 0),
        _ => (BaseColumns, 0)
    };

    // 尺寸档在某个列数下占的格数与半行数；半行数 0 是随内容档。
    private static (int Span, int Rows) Shape(CardSize size, int columns)
    {
        var (wide, rows) = Dimensions(size);
        var units = UnitsFor(columns);
        return (columns <= 1 ? 1 : wide == 0 ? units / 2 : Math.Min(wide, columns) * 2, rows);
    }

    /// <summary>
    /// 把一串尺寸档按顺序排进 <paramref name="columns"/> 列的网格。固定档排到上一张之后最先放得下的位置；
    /// 随内容档排进一条高度随内容的行轨：上一张也是随内容档且这一行还放得下时接在它右边，否则另起一条行轨；其后的固定档从它下面接着排。
    /// </summary>
    public static IReadOnlyList<CardCell> Place(IReadOnlyList<CardSize> sizes, int columns)
    {
        var units = UnitsFor(columns);
        var cells = new List<CardCell>(sizes.Count);
        var taken = new List<bool[]>();
        int origin = 0, row = 0, x = 0, band = -1;
        bool Free(int atRow, int atX, int span, int rows)
        {
            for (var r = atRow; r < atRow + rows && r < taken.Count; r++)
                for (var c = atX; c < atX + span; c++)
                    if (taken[r][c]) return false;
            return true;
        }
        foreach (var size in sizes)
        {
            var (span, rows) = Shape(size, columns);
            if (rows == 0)
            {
                if (band < 0 || x + span > units)
                {
                    origin += taken.Count;
                    taken.Clear();
                    band = origin++;
                    x = 0;
                }
                cells.Add(new CardCell(x, span, band, 0));
                x += span;
                row = 0;
                continue;
            }
            if (band >= 0)
            {
                band = -1;
                x = 0;
            }
            while (x + span > units || !Free(row, x, span, rows))
                if (++x + span > units)
                {
                    x = 0;
                    row++;
                }
            while (taken.Count < row + rows) taken.Add(new bool[units]);
            for (var r = row; r < row + rows; r++)
                for (var c = x; c < x + span; c++)
                    taken[r][c] = true;
            cells.Add(new CardCell(x, span, origin + row, rows));
            x += span;
        }
        return cells;
    }

    // ---- 顺序与尺寸档 ----

    private sealed class Working
    {
        public required List<Control> Order { get; init; }
        public required Dictionary<Card, CardSize> Sizes { get; init; }
    }

    private string? PageId => Card.GetPageId(this) is { Length: > 0 } page ? page : null;

    // 子元素里的卡片：卡片本身，或项目列表容器里的卡片。
    private static Card? CardOf(Control child) => child as Card ?? (child as ContentPresenter)?.Child as Card;

    // 卡片对应的子元素。
    private Control HostOf(Card card) => ReferenceEquals(card.GetVisualParent(), this) ? card : card.GetVisualParent() as Control ?? card;

    private IEnumerable<Card> Cards => Children.Select(CardOf).OfType<Card>();

    /// <summary>网格里有可编辑布局的卡片（带 <see cref="Card.CardId"/>）；没有时编辑入口不显示。</summary>
    public bool HasEditableCards => Cards.Any(card => card.CardId is { Length: > 0 });

    private static string? IdOf(Control child) => CardOf(child) is { CardId: { Length: > 0 } id } ? id : null;

    private CardSlot? Saved(Control child, int columns) =>
        !resetAll && PageId is { } page && IdOf(child) is { } id ? CardStateStore.Slot(page, id, columns) : null;

    private List<Control> SavedOrder(int columns) => Children
        .Select((child, index) => (child, index, key: Saved(child, columns)?.Order ?? index))
        .OrderBy(item => item.key).ThenBy(item => item.index).Select(item => item.child).ToList();

    private CardSize SavedSize(Card card, int columns) =>
        Saved(card, columns)?.Size is { } size && card.Sizes.Contains(size) ? size : card.Size;

    private Working Work(int columns)
    {
        if (works.TryGetValue(columns, out var work)) return work;
        work = new Working
        {
            Order = SavedOrder(columns), Sizes = Cards.ToDictionary(card => card, card => SavedSize(card, columns))
        };
        works[columns] = work;
        return work;
    }

    private IReadOnlyList<Control> Ordered(int columns) => IsEditing ? Work(columns).Order : SavedOrder(columns);

    private CardSize SizeFor(Control child, int columns) => CardOf(child) is not { } card ? CardSize.FullWidth
        : IsEditing ? Work(columns).Sizes.GetValueOrDefault(card, card.Size) : SavedSize(card, columns);

    // ---- 编辑模式 ----

    /// <summary>
    /// 把卡片在当前列数的顺序里前移（负数）或后移（正数），跳过不可见的卡片；只在编辑模式内有效。返回是否移动了。
    /// </summary>
    public bool Move(Card card, int delta)
    {
        if (!IsEditing || delta == 0) return false;
        var order = Work(Columns).Order;
        var host = HostOf(card);
        var from = order.IndexOf(host);
        if (from < 0) return false;
        var step = Math.Sign(delta);
        var to = from;
        for (var left = Math.Abs(delta); left > 0; left--)
        {
            var next = to + step;
            while (next >= 0 && next < order.Count && !order[next].IsVisible) next += step;
            if (next < 0 || next >= order.Count) break;
            to = next;
        }
        if (to == from) return false;
        order.RemoveAt(from);
        order.Insert(to, host);
        InvalidateMeasure();
        return true;
    }

    /// <summary>改卡片在当前列数下的尺寸档，只接受该卡片适用的档；只在编辑模式内有效。</summary>
    public bool Resize(Card card, CardSize size)
    {
        if (!IsEditing || !card.Sizes.Contains(size)) return false;
        var sizes = Work(Columns).Sizes;
        if (sizes.GetValueOrDefault(card, card.Size) == size) return false;
        sizes[card] = size;
        layers.GetValueOrDefault(card)?.Refresh();
        InvalidateMeasure();
        return true;
    }

    /// <summary>
    /// 编辑模式内回到页面缺省：各列数的顺序与尺寸档、各卡片的折叠状态。仍在编辑模式里，随“完成”保存，“取消”则撤销。
    /// </summary>
    public void ResetToDefault()
    {
        if (!IsEditing) return;
        resetAll = true;
        works.Clear();
        foreach (var card in layers.Keys) card.Preview(card.DefaultExpanded);
        foreach (var layer in layers.Values) layer.Refresh();
        InvalidateMeasure();
    }

    /// <summary>退出编辑模式并丢弃本次编辑的全部改动（换位、改档、恢复默认），回到进入编辑时的布局与折叠状态。</summary>
    public void CancelEditing()
    {
        if (!IsEditing) return;
        discard = true;
        SetCurrentValue(IsEditingProperty, false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsEditingProperty) return;
        if (change.GetNewValue<bool>()) Enter();
        else Leave();
        InvalidateMeasure();
    }

    private void Enter()
    {
        resetAll = discard = false;
        works.Clear();
        expandedAtEnter.Clear();
        VisualChildren.Add(mark);
        foreach (var card in Cards.Where(card => card.CardId is { Length: > 0 }).ToList())
        {
            // 卡片内容不接收指针，Tab 不进入卡片内部；标题行右端的操作区让给布局菜单。
            expandedAtEnter[card] = card.IsExpanded;
            card.IsHitTestVisible = false;
            KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.None);
            card.EditSlotWidth = EditLayer.MenuWidth;
            var layer = new EditLayer(this, card);
            layers[card] = layer;
            LogicalChildren.Add(layer);
            VisualChildren.Add(layer);
        }
    }

    private void Leave()
    {
        EndDrag();
        if (discard)
            foreach (var (card, expanded) in expandedAtEnter) card.Preview(expanded);
        else Save();
        foreach (var (card, layer) in layers)
        {
            layer.Detach();
            card.EditSlotWidth = null;
            card.ClearValue(IsHitTestVisibleProperty);
            card.ClearValue(KeyboardNavigation.TabNavigationProperty);
            HostOf(card).ClearValue(ZIndexProperty);
            Shift(HostOf(card), default);
            VisualChildren.Remove(layer);
            LogicalChildren.Remove(layer);
        }
        VisualChildren.Remove(mark);
        layers.Clear();
        glides.Clear();
        works.Clear();
        expandedAtEnter.Clear();
        resetAll = discard = false;
    }

    // 与页面缺省相同的列数不留保存项，以后缺省布局调整时随之更新。恢复默认后该页卡片的布局与折叠状态都不留保存项。
    private void Save()
    {
        if (PageId is not { } page) return;
        if (resetAll) CardStateStore.ResetCards(page, layers.Keys.Select(card => card.CardId!).ToList());
        foreach (var (columns, work) in works)
        {
            var cards = work.Order.Select(CardOf).OfType<Card>().Where(card => card.CardId is { Length: > 0 }).ToList();
            var isDefault = work.Order.SequenceEqual(Children) && cards.All(card => work.Sizes.GetValueOrDefault(card, card.Size) == card.Size);
            if (isDefault) CardStateStore.ClearLayout(page, columns);
            else CardStateStore.SetLayout(page, columns, cards.Select(card => (card.CardId!, work.Sizes.GetValueOrDefault(card, card.Size))).ToList());
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        CardStateStore.LayoutChanged += OnLayoutChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CardStateStore.LayoutChanged -= OnLayoutChanged;
        // 编辑中切走页面按“完成”处理。
        SetCurrentValue(IsEditingProperty, false);
    }

    private void OnLayoutChanged(object? sender, string page)
    {
        if (page == PageId) InvalidateMeasure();
    }

    // ---- 排版 ----

    private Rect CellRect(CardCell cell, int units, double width, double spacing) =>
        new(cell.Column * (width + spacing) / units, 0, Math.Max(0, cell.Span * (width + spacing) / units - spacing), 0);

    // 按顺序给出各可见卡片的位置：行轨高度取固定行高，或（该行轨上没有展开到位的卡片时）取收起或过渡中的卡片的实际高度。
    private double Solve(IReadOnlyList<Control> order, int columns, double width, Dictionary<Control, Rect> result)
    {
        result.Clear();
        var spacing = Spacing;
        var units = UnitsFor(columns);
        var halfRow = (RowUnit - spacing) / 2;
        var visible = order.Where(child => child.IsVisible).ToList();
        var cells = Place(visible.Select(child => SizeFor(child, columns)).ToList(), columns);
        var tracks = new double[cells.Count == 0 ? 0 : cells.Max(cell => cell.Row + Math.Max(1, cell.Rows))];
        bool Settled(int index) => CardOf(visible[index]) is not { IsSettled: false };
        bool Solid(int index) => cells[index].Rows > 0 && Settled(index);
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            if (cell.Rows == 0) tracks[cell.Row] = Math.Max(tracks[cell.Row], visible[index].DesiredSize.Height);
            else if (Solid(index))
                for (var r = cell.Row; r < cell.Row + cell.Rows; r++) tracks[r] = halfRow;
        }
        double Extent(CardCell cell)
        {
            double sum = 0;
            var any = false;
            for (var r = cell.Row; r < cell.Row + cell.Rows; r++)
            {
                if (tracks[r] <= 0) continue;
                sum += tracks[r] + (any ? spacing : 0);
                any = true;
            }
            return sum;
        }
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            if (cell.Rows == 0 || Solid(index)) continue;
            var height = visible[index].DesiredSize.Height;
            tracks[cell.Row] = Math.Max(tracks[cell.Row], Math.Min(height, halfRow));
            var missing = height - Extent(cell);
            if (missing <= 0.5) continue;
            var last = cell.Row + cell.Rows - 1;
            tracks[last] = tracks[last] > 0 ? tracks[last] + missing : Math.Max(0.5, missing - spacing);
        }
        var tops = new double[tracks.Length];
        double y = 0;
        var placed = false;
        for (var r = 0; r < tracks.Length; r++)
        {
            if (tracks[r] > 0 && placed) y += spacing;
            tops[r] = y;
            y += tracks[r];
            placed |= tracks[r] > 0;
        }
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            var rect = CellRect(cell, units, width, spacing);
            // 随内容档：展开到位的卡片拉到行高，收起或过渡中的按自己的高度；固定档：展开到位的取档高。
            var height = !Settled(index) ? visible[index].DesiredSize.Height
                : cell.Rows == 0 ? tracks[cell.Row] : HeightFor(cell.Rows / 2.0, spacing);
            result[visible[index]] = new Rect(rect.X, tops[cell.Row], rect.Width, height);
        }
        return y;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var spacing = Spacing;
        var columns = ColumnsFor(availableSize.Width, MinColumnWidth, spacing);
        // 项目列表容器里的卡片在容器建立内容之后才有；先建好，才能取到它的尺寸档。
        foreach (var child in Children)
        {
            if (child is ContentPresenter { Child: null } presenter) presenter.UpdateChild();
            if (CardOf(child) is { } hosted) hosted.Grid = this;
        }
        if (columns != Columns && layers.Count > 0)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { foreach (var layer in layers.Values) layer.Refresh(); });
        Columns = columns;
        var width = double.IsInfinity(availableSize.Width)
            ? BaseColumns * MinColumnWidth + (BaseColumns - 1) * spacing
            : availableSize.Width;
        var units = UnitsFor(columns);
        var order = Ordered(columns);
        var visible = order.Where(child => child.IsVisible).ToList();
        var cells = Place(visible.Select(child => SizeFor(child, columns)).ToList(), columns);
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            var cellWidth = CellRect(cell, units, width, spacing).Width;
            if (CardOf(visible[index]) is { } card) card.FillsHeight = cell.Rows > 0;
            visible[index].Measure(new Size(cellWidth, cell.Rows > 0 ? HeightFor(cell.Rows / 2.0, spacing) : double.PositiveInfinity));
        }
        var height = Solve(order, columns, width, slots);
        foreach (var (card, layer) in layers)
        {
            layer.IsVisible = slots.ContainsKey(HostOf(card));
            if (slots.TryGetValue(HostOf(card), out var slot)) layer.Measure(slot.Size);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var moving = IsEditing && UiMotion.Active && TopLevel.GetTopLevel(this) is not null;
        foreach (var (child, slot) in slots)
        {
            // 编辑中位置变了的卡片从原来的位置滑到新位置；正在拖的卡片跟着指针。
            if (moving && !(dragged is not null && ReferenceEquals(CardOf(child), dragged)) && child.Bounds.Position != slot.Position && child.Bounds.Width > 0)
                glides[child] = new Glide(child.Bounds.Position + Current(child) - slot.Position);
            child.Arrange(slot);
            if (CardOf(child) is { } card && layers.TryGetValue(card, out var layer)) layer.Arrange(slot);
        }
        foreach (var stale in glides.Keys.Where(child => !slots.ContainsKey(child)).ToList()) glides.Remove(stale);
        if (dragged is not null && slots.TryGetValue(HostOf(dragged), out var drop))
        {
            mark.Measure(drop.Size);
            mark.Arrange(drop);
        }
        ApplyShifts();
        if (glides.Count > 0) StartFrames();
        return finalSize;
    }

    // ---- 让位动效与拖动 ----

    private sealed class Glide(Vector from)
    {
        public Vector From { get; } = from;
        public TimeSpan? Started { get; set; }
        public Vector Value { get; set; } = from;
    }

    private Vector Current(Control child) => glides.TryGetValue(child, out var glide) ? glide.Value : default;

    private void Shift(Control child, Vector offset)
    {
        var transform = offset == default ? null : new TranslateTransform(offset.X, offset.Y);
        child.RenderTransform = transform;
        if (CardOf(child) is { } card && layers.TryGetValue(card, out var layer))
            layer.RenderTransform = transform is null ? null : new TranslateTransform(offset.X, offset.Y);
    }

    private void ApplyShifts()
    {
        foreach (var child in slots.Keys)
            Shift(child, dragged is not null && ReferenceEquals(CardOf(child), dragged) ? dragOrigin + (dragNow - dragStart) - slots[child].Position : Current(child));
    }

    private void StartFrames()
    {
        if (animating || TopLevel.GetTopLevel(this) is not { } top) return;
        animating = true;
        top.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan time)
    {
        animating = false;
        var active = UiMotion.Active && TopLevel.GetTopLevel(this) is not null;
        foreach (var (child, glide) in glides.ToList())
        {
            glide.Started ??= time;
            var progress = active ? (time - glide.Started.Value).TotalMilliseconds / UiMotion.Expand.TotalMilliseconds : 1;
            if (progress >= 1) glides.Remove(child);
            else glide.Value = glide.From * (1 - Ease.Ease(progress));
        }
        ApplyShifts();
        if (glides.Count > 0) StartFrames();
    }

    private void BeginDrag(Card card, Point point)
    {
        var host = HostOf(card);
        if (!slots.TryGetValue(host, out var slot)) return;
        dragged = card;
        dragStart = dragNow = point;
        dragOrigin = slot.Position + Current(host);
        glides.Remove(host);
        host.ZIndex = 1;
        layers[card].ZIndex = 3;
        mark.IsVisible = true;
        InvalidateArrange();
    }

    // 落点：按不含被拖卡片时其余卡片的位置判断指针在谁之前（那套位置在一次拖动里不变，落点不会来回跳）。
    // 占满一行的卡片以纵向中线为界，其余以“所在行带内、横向中线之前”为界。
    private void DragTo(Point point)
    {
        if (dragged is not { } card) return;
        dragNow = point;
        var order = Work(Columns).Order;
        var host = HostOf(card);
        var others = order.Where(child => !ReferenceEquals(child, host)).ToList();
        var reference = new Dictionary<Control, Rect>();
        Solve(others, Columns, Bounds.Width, reference);
        var before = others.FirstOrDefault(child => reference.TryGetValue(child, out var rect) &&
            (rect.Width >= Bounds.Width - 1 ? point.Y < rect.Center.Y : point.Y < rect.Top || (point.Y < rect.Bottom && point.X < rect.Center.X)));
        var index = before is null ? others.Count : others.IndexOf(before);
        if (order.IndexOf(host) != index)
        {
            order.Clear();
            order.AddRange(others);
            order.Insert(index, host);
            InvalidateMeasure();
        }
        ApplyShifts();
        ScrollNearEdge(point);
    }

    // 拖到滚动区上下边缘附近时滚动页面。
    private void ScrollNearEdge(Point point)
    {
        if (this.FindAncestorOfType<ScrollViewer>() is not { } scroller || this.TranslatePoint(point, scroller) is not { } at) return;
        var delta = at.Y < 48 ? -24 : at.Y > scroller.Viewport.Height - 48 ? 24 : 0;
        if (delta != 0) scroller.Offset = scroller.Offset.WithY(Math.Clamp(scroller.Offset.Y + delta, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
    }

    private void EndDrag()
    {
        if (dragged is not { } card) return;
        dragged = null;
        mark.IsVisible = false;
        var host = HostOf(card);
        host.ClearValue(ZIndexProperty);
        if (layers.TryGetValue(card, out var layer)) layer.ZIndex = 2;
        // 松手后从当前位置滑到落点。
        if (slots.TryGetValue(host, out var slot) && UiMotion.Active && TopLevel.GetTopLevel(this) is not null)
        {
            glides[host] = new Glide(dragOrigin + (dragNow - dragStart) - slot.Position);
            StartFrames();
        }
        ApplyShifts();
        InvalidateArrange();
    }

    // 落点框：画在被拖卡片的落点处，在所有卡片之下。
    private sealed class DropMark : Control
    {
        public DropMark()
        {
            ZIndex = -1;
            IsHitTestVisible = false;
            IsVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            var color = this.TryFindResource("InkBrush", ActualThemeVariant, out var value) && value is ISolidColorBrush found ? found.Color : Colors.Gray;
            context.DrawRectangle(new SolidColorBrush(color, 0.06), new Pen(new SolidColorBrush(color, 0.45), 2, new DashStyle([4, 3], 0)),
                new Rect(Bounds.Size).Deflate(1), 26, 26);
        }
    }

    /// <summary>尺寸档在布局菜单里的名称：宽×高，高为行数或“随内容”；全宽随内容的一档叫“全宽”。</summary>
    internal static string SizeLabel(CardSize size)
    {
        var (wide, rows) = Dimensions(size);
        if (wide == BaseColumns && rows == 0) return UiText.Pick("全宽", "全寬");
        var width = wide == 0 ? UiText.Pick("半宽", "半寬") : wide.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var height = rows == 0 ? UiText.Pick("随内容", "隨內容") : (rows / 2.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return $"{width}×{height}";
    }

    /// <summary>
    /// 编辑模式下盖在一张卡片上的编辑层：接住指针（卡片内容不响应），按住拖动换位；可聚焦，方向键前移或后移。
    /// 布局菜单（前移、后移、适用的尺寸档）排在卡片标题行右端：卡片在那里留出同样宽度的空位（<see cref="Card.EditSlotWidth"/>），
    /// 菜单按钮宽度固定，所有卡片一致，不压在标题、状态与卡片自己的操作按钮上。
    /// </summary>
    private sealed class EditLayer : Control
    {
        private readonly CardGrid grid;
        private readonly Card card;
        private readonly Button menu = new();
        private Point? pressed;
        private bool dragging;

        public EditLayer(CardGrid grid, Card card)
        {
            this.grid = grid;
            this.card = card;
            Focusable = true;
            ZIndex = 2;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            Name = "CardEditLayer";
            menu.Name = "CardLayoutMenu";
            menu.Classes.Add("chip");
            menu.Width = MenuWidth;
            menu.Padding = new Thickness(4, 3);
            menu.Cursor = Cursor.Default;
            LogicalChildren.Add(menu);
            VisualChildren.Add(menu);
            card.EditSlotMoved += InvalidateArrange;
            Refresh();
        }

        /// <summary>布局菜单按钮的宽度，也是卡片标题行右端留出的宽度。</summary>
        public const double MenuWidth = 104;

        /// <summary>卡片（供测试按卡片找到它的编辑层）。</summary>
        public Card Card => card;

        public void Detach() => card.EditSlotMoved -= InvalidateArrange;

        // 菜单按钮排在卡片标题行右端的空位上，纵向以标题行中线为准；标题行还没排版时先放在右上角。
        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = menu.DesiredSize;
            var slot = card.EditSlotBounds ?? new Rect(finalSize.Width - 18 - size.Width, 18 + size.Height / 2, size.Width, 0);
            menu.Arrange(new Rect(slot.Right - size.Width, slot.Center.Y - size.Height / 2, size.Width, size.Height));
            return finalSize;
        }

        public void Refresh()
        {
            var title = card.Title ?? card.CardId ?? string.Empty;
            // 菜单项在打开之前就要备好：在 Opening 事件里才给 ItemsSource 时，这一版 Avalonia 的菜单弹出层是空的（没有任何项）。
            menu.Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, ItemsSource = MenuItems() };
            menu.Content = SizeLabel(grid.SizeFor(card, grid.Columns)) + " ▾";
            AutomationProperties.SetName(menu, UiText.Pick($"{title} 布局菜单", $"{title} 版面選單"));
            ToolTip.SetTip(menu, UiText.Pick("前移、后移或改尺寸", "前移、後移或改尺寸"));
            AutomationProperties.SetName(this, UiText.Pick($"{title}：拖动换位，方向键前移或后移", $"{title}：拖動換位，方向鍵前移或後移"));
        }

        private List<Control> MenuItems()
        {
            MenuItem Item(string header, Action action, bool check = false, bool isChecked = false)
            {
                var item = new MenuItem { Header = header };
                if (check)
                {
                    item.ToggleType = MenuItemToggleType.Radio;
                    item.IsChecked = isChecked;
                }
                // 菜单收起之后再执行：改档会换掉这个菜单本身。
                item.Click += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
                return item;
            }
            var current = grid.SizeFor(card, grid.Columns);
            var items = new List<Control>
            {
                Item(UiText.Pick("前移", "前移"), () => { if (grid.Move(card, -1)) this.BringIntoView(); }),
                Item(UiText.Pick("后移", "後移"), () => { if (grid.Move(card, 1)) this.BringIntoView(); }),
                new Separator()
            };
            items.AddRange(card.Sizes.Select(size => Item(SizeLabel(size), () => grid.Resize(card, size), true, size == current)));
            return items;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus();
            pressed = e.GetPosition(grid);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (pressed is not { } start) return;
            var point = e.GetPosition(grid);
            if (!dragging)
            {
                if (Math.Abs(point.X - start.X) < DragThreshold && Math.Abs(point.Y - start.Y) < DragThreshold) return;
                dragging = true;
                grid.BeginDrag(card, start);
            }
            grid.DragTo(point);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (pressed is null) return;
            e.Pointer.Capture(null);
            Finish();
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            Finish();
        }

        private void Finish()
        {
            pressed = null;
            if (!dragging) return;
            dragging = false;
            grid.EndDrag();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || !ReferenceEquals(e.Source, this)) return;
            var delta = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
            if (delta == 0) return;
            if (grid.Move(card, delta)) this.BringIntoView();
            e.Handled = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsFocusedProperty || change.Property == IsPointerOverProperty) InvalidateVisual();
        }

        // 整块用透明底接住指针，其上画虚线框（聚焦或悬停时加重）。
        public override void Render(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
            var color = this.TryFindResource("InkBrush", ActualThemeVariant, out var value) && value is ISolidColorBrush found ? found.Color : Colors.Gray;
            var strong = IsFocused || IsPointerOver;
            context.DrawRectangle(null, new Pen(new SolidColorBrush(color, strong ? 0.85 : 0.4), strong ? 2 : 1.5, new DashStyle([4, 3], 0)),
                new Rect(Bounds.Size).Deflate(1), 26, 26);
        }
    }
}

/// <summary>
/// 卡片网格的编辑入口，各页面同一位置：页面标题同一行、紧跟标题之后（与标题垂直居中对齐），平时是“编辑布局”，
/// 编辑中在同一处换成“完成”“取消”“恢复默认”。标题行除标题外只有它，宽度变化不推动页面其他内容；窗口右上角的按钮组另有预留，不相邻。
/// 窄窗口下放不下时右端被裁，“完成”排在最前保证可见。网格里没有可编辑的卡片时不显示。<see cref="Target"/> 指向页面的 <see cref="CardGrid"/>。
/// “完成”保存本次编辑；“取消”丢弃本次编辑的全部改动；“恢复默认”让该页各列数的布局与卡片的折叠状态回到缺省，仍在编辑模式里。
/// </summary>
public sealed class CardGridEditBar : Decorator
{
    public static readonly StyledProperty<CardGrid?> TargetProperty = AvaloniaProperty.Register<CardGridEditBar, CardGrid?>(nameof(Target));

    private readonly Button edit = new() { Name = "EditLayoutButton" };
    private readonly Button cancel = new() { Name = "CancelLayoutButton" };
    private readonly Button reset = new() { Name = "ResetLayoutButton" };
    private readonly Button done = new() { Name = "DoneLayoutButton" };
    private IDisposable? watch;
    private CardGrid? watched;

    public CardGridEditBar()
    {
        edit.Classes.Add("chip");
        cancel.Classes.Add("chip");
        reset.Classes.Add("chip");
        done.Classes.Add("chip");
        done.Classes.Add("active");
        edit.Content = UiText.Pick("编辑布局", "編輯版面");
        cancel.Content = UiText.Pick("取消", "取消");
        reset.Content = UiText.Pick("恢复默认", "恢復預設");
        done.Content = UiText.Pick("完成", "完成");
        ToolTip.SetTip(edit, UiText.Pick("调整本页卡片的顺序与尺寸", "調整本頁卡片的順序與尺寸"));
        ToolTip.SetTip(cancel, UiText.Pick("放弃本次编辑的全部改动", "放棄本次編輯的全部改動"));
        ToolTip.SetTip(reset, UiText.Pick("本页卡片回到默认的顺序、尺寸与折叠状态", "本頁卡片回到預設的順序、尺寸與摺疊狀態"));
        ToolTip.SetTip(done, UiText.Pick("保存布局并退出编辑", "儲存版面並結束編輯"));
        edit.Click += (_, _) => Target?.SetCurrentValue(CardGrid.IsEditingProperty, true);
        cancel.Click += (_, _) => Target?.CancelEditing();
        reset.Click += (_, _) => Target?.ResetToDefault();
        done.Click += (_, _) => Target?.SetCurrentValue(CardGrid.IsEditingProperty, false);
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { edit, done, cancel, reset } };
        Apply();
    }

    public CardGrid? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TargetProperty) return;
        watch?.Dispose();
        watch = Target?.GetObservable(CardGrid.IsEditingProperty).Subscribe(new Watcher(this));
        if (watched is not null) watched.Children.CollectionChanged -= OnCardsChanged;
        watched = Target;
        if (watched is not null) watched.Children.CollectionChanged += OnCardsChanged;
        Apply();
    }

    private void OnCardsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Apply();

    private void Apply()
    {
        var editing = Target?.IsEditing == true;
        IsVisible = editing || Target?.HasEditableCards == true;
        edit.IsVisible = !editing;
        cancel.IsVisible = reset.IsVisible = done.IsVisible = editing;
    }

    private sealed class Watcher(CardGridEditBar bar) : IObserver<bool>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(bool value) => bar.Apply();
    }
}
