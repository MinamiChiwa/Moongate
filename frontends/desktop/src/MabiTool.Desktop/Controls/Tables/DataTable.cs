using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Metadata;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Controls.Tables;

/// <summary>
/// 全前端共用的数据表格。拖动表头右边界改列宽、双击边界按内容自适应；点表头排序（Shift 追加多列）；表头筛选按钮；
/// Ctrl+F 检索并以 Enter／F3 在匹配之间跳转；单击、Ctrl、Shift、Ctrl+A 选择，Ctrl+C 复制所选行（制表符分隔、含表头；
/// 设了 <see cref="CopyColumnId"/> 时只复制该列的文字）；
/// 拖动表头调整列顺序，右键表头选择显示的列；列宽、顺序、显隐、排序按 <see cref="TableId"/> 保存在前端偏好里。
/// 快捷键只在表格获得焦点时生效。行多时虚拟化。
/// </summary>
public sealed class DataTable : Border
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<DataTable, IEnumerable?>(nameof(ItemsSource));
    public static readonly StyledProperty<IReadOnlyList<TableColumn>?> ColumnsSourceProperty =
        AvaloniaProperty.Register<DataTable, IReadOnlyList<TableColumn>?>(nameof(ColumnsSource));
    public static readonly StyledProperty<string?> TableIdProperty = AvaloniaProperty.Register<DataTable, string?>(nameof(TableId));
    public static readonly StyledProperty<string?> EmptyTextProperty = AvaloniaProperty.Register<DataTable, string?>(nameof(EmptyText));
    public static readonly StyledProperty<double> BodyMaxHeightProperty =
        AvaloniaProperty.Register<DataTable, double>(nameof(BodyMaxHeight), 480);
    public static readonly StyledProperty<bool> CanExportProperty = AvaloniaProperty.Register<DataTable, bool>(nameof(CanExport));
    public static readonly StyledProperty<string?> ExportFileNameProperty = AvaloniaProperty.Register<DataTable, string?>(nameof(ExportFileName));
    public static readonly StyledProperty<IDataTemplate?> RowDetailTemplateProperty =
        AvaloniaProperty.Register<DataTable, IDataTemplate?>(nameof(RowDetailTemplate));
    public static readonly StyledProperty<bool> AllowSortProperty = AvaloniaProperty.Register<DataTable, bool>(nameof(AllowSort), true);
    public static readonly StyledProperty<bool> AllowFilterProperty = AvaloniaProperty.Register<DataTable, bool>(nameof(AllowFilter), true);
    public static readonly StyledProperty<bool> AllowReorderProperty = AvaloniaProperty.Register<DataTable, bool>(nameof(AllowReorder), true);
    public static readonly StyledProperty<bool> AllowColumnMenuProperty = AvaloniaProperty.Register<DataTable, bool>(nameof(AllowColumnMenu), true);
    public static readonly StyledProperty<bool> VirtualizeProperty = AvaloniaProperty.Register<DataTable, bool>(nameof(Virtualize), true);
    public static readonly StyledProperty<Control?> CornerContentProperty = AvaloniaProperty.Register<DataTable, Control?>(nameof(CornerContent));
    public static readonly StyledProperty<string?> CopyColumnIdProperty = AvaloniaProperty.Register<DataTable, string?>(nameof(CopyColumnId));
    public static readonly StyledProperty<string?> CopyLabelProperty = AvaloniaProperty.Register<DataTable, string?>(nameof(CopyLabel));

    private const double DragThreshold = 6;
    private readonly ListBox list;
    private readonly ScrollViewer headerScroller;
    private readonly ScrollViewer groupScroller;
    private readonly Decorator cornerHost = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid groupRow = new();
    private readonly TableStrip header = new();
    private readonly TableStrip groups = new();
    private readonly Border searchBar;
    private readonly TextBox searchBox;
    private readonly TextBlock searchCount;
    private readonly TextBlock empty;
    private readonly Dictionary<string, ColumnFilter> filters = new(StringComparer.Ordinal);
    private readonly List<TableHeaderCell> headerCells = [];
    private ScrollViewer? body;
    private INotifyCollectionChanged? watched;
    private TableSettings settings = TableSettings.Empty;
    private IReadOnlyList<TableColumn> all = [];
    private IReadOnlyList<TableColumn> visible = [];
    private List<ColumnSlot> slots = [];
    private Dictionary<string, double> widths = new(StringComparer.Ordinal);
    private List<object> view = [];
    // 表体的数据源：每个显示位置一个固定的槽，行数不变时集合不发任何变更事件；刷新只改槽里的行对象与对应容器的内容（见 Rebuild）。
    // 元素类型是 object：IList.IndexOf(行对象) 才会逐个调用槽的 Equals（见 TableSlot），按行对象找容器的旧写法照常可用。
    private readonly AvaloniaList<object> bodyRows = [];
    private HashSet<object> matchSet = [];
    private List<int> matches = [];
    private int currentMatch = -1;
    private int cursor = -1;
    private (TableHeaderCell Cell, Point Start, IPointer Pointer)? press;
    private bool dragging;
    private (TableHeaderCell Cell, double Start, double Width)? resize;

    public DataTable()
    {
        ClipToBounds = false;
        list = new TableList { SelectionMode = SelectionMode.Multiple, ItemsSource = bodyRows };
        list.Classes.Add("datatable");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        list.ItemTemplate = new FuncDataTemplate<object>((_, _) => new TableRowPresenter(this), true);
        list.ContainerPrepared += (_, e) => Mark(e.Container, e.Index);
        list.ContainerIndexChanged += (_, e) => Mark(e.Container, e.NewIndex);
        list.TemplateApplied += (_, e) =>
        {
            if (body is not null) body.ScrollChanged -= OnBodyScrolled;
            body = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (body is null) return;
            body.MaxHeight = BodyMaxHeight;
            body.ScrollChanged += OnBodyScrolled;
        };
        list.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Bubble, true);
        list.ContextRequested += OnRowContext;

        headerScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = header
        };
        headerScroller.Classes.Add("tablehead");
        header.ContextRequested += OnHeaderContext;
        groupScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = groups, IsVisible = false
        };
        groupScroller.Classes.Add("tablegroups");
        groupScroller.VerticalAlignment = VerticalAlignment.Bottom;
        // 列组标题一行的左端可以放表格自己的操作（CornerContent），固定不随横向滚动；列组标题滚到它下面时被裁掉。
        groupRow.Children.Add(groupScroller);
        groupRow.Children.Add(cornerHost);
        groupRow.IsVisible = false;
        cornerHost.SizeChanged += (_, _) => UpdateCornerClip();
        groupScroller.SizeChanged += (_, _) => UpdateCornerClip();

        searchBox = new TextBox { MinWidth = 120 };
        searchBox.Classes.Add("field");
        searchBox.Bind(TextBox.PlaceholderTextProperty, searchBox.GetResourceObservable("T.FindHint"));
        searchBox.Bind(Avalonia.Automation.AutomationProperties.NameProperty, searchBox.GetResourceObservable("T.FindHint"));
        searchBox.TextChanged += (_, _) => UpdateSearch(true);
        searchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        searchCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        searchCount.Classes.Add("note");
        var previous = Chip("T.Previous", () => Jump(-1));
        var next = Chip("T.Next", () => Jump(1));
        var close = Chip("T.CloseFind", CloseSearch);
        previous.Name = "TablePrevious";
        next.Name = "TableNext";
        var searchRow = new DockPanel();
        foreach (var button in new[] { close, next, previous })
        {
            DockPanel.SetDock(button, Dock.Right);
            button.Margin = new Thickness(6, 0, 0, 0);
            searchRow.Children.Add(button);
        }
        DockPanel.SetDock(searchCount, Dock.Right);
        searchRow.Children.Add(searchCount);
        searchRow.Children.Add(searchBox);
        searchBar = new Border { Child = searchRow, IsVisible = false, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(8, 6) };
        searchBar.Classes.Add("inset");

        empty = new TextBlock { Margin = new Thickness(10), IsVisible = false };
        empty.Classes.Add("note");

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        Grid.SetRow(groupRow, 1);
        Grid.SetRow(headerScroller, 2);
        Grid.SetRow(list, 3);
        Grid.SetRow(empty, 3);
        grid.Children.Add(searchBar);
        grid.Children.Add(groupRow);
        grid.Children.Add(headerScroller);
        grid.Children.Add(list);
        grid.Children.Add(empty);
        Child = grid;

        Columns.CollectionChanged += (_, _) => ApplyColumns();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) Relayout(); };
    }

    /// <summary>在 XAML 里写的列；绑定了 <see cref="ColumnsSource"/> 时以后者为准。</summary>
    [Content]
    public AvaloniaList<TableColumn> Columns { get; } = [];

    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public IReadOnlyList<TableColumn>? ColumnsSource { get => GetValue(ColumnsSourceProperty); set => SetValue(ColumnsSourceProperty, value); }
    /// <summary>保存列设置用的表标识；同一标识的表格共用一份设置。为空时不保存。</summary>
    public string? TableId { get => GetValue(TableIdProperty); set => SetValue(TableIdProperty, value); }
    public string? EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    /// <summary>表体的最大高度；放在页面滚动区里时由它限高，行多时虚拟化。</summary>
    public double BodyMaxHeight { get => GetValue(BodyMaxHeightProperty); set => SetValue(BodyMaxHeightProperty, value); }

    /// <summary>
    /// 放在表格左上角（列组标题一行的左端、第一列表头上方）的内容，例如表格自己的操作按钮；为空时该行只在有列组时显示。
    /// </summary>
    public Control? CornerContent { get => GetValue(CornerContentProperty); set => SetValue(CornerContentProperty, value); }

    private void UpdateCornerClip()
    {
        var width = cornerHost.Child is { IsVisible: true } ? cornerHost.Bounds.Width + 8 : 0;
        var size = groupScroller.Bounds.Size;
        groupScroller.Clip = width <= 0 ? null : new RectangleGeometry(new Rect(width, 0, Math.Max(0, size.Width - width), size.Height));
    }

    private void UpdateGroupRow() => groupRow.IsVisible = groupScroller.IsVisible || cornerHost.Child is not null;
    /// <summary>右键菜单是否提供“导出 CSV”。</summary>
    public bool CanExport { get => GetValue(CanExportProperty); set => SetValue(CanExportProperty, value); }
    public string? ExportFileName { get => GetValue(ExportFileNameProperty); set => SetValue(ExportFileNameProperty, value); }
    /// <summary>行详情：排在单元格下方、占满整行的内容（例如可展开的文字详情）。</summary>
    public IDataTemplate? RowDetailTemplate { get => GetValue(RowDetailTemplateProperty); set => SetValue(RowDetailTemplateProperty, value); }
    public bool AllowSort { get => GetValue(AllowSortProperty); set => SetValue(AllowSortProperty, value); }
    public bool AllowFilter { get => GetValue(AllowFilterProperty); set => SetValue(AllowFilterProperty, value); }
    /// <summary>是否可以拖动表头调整列顺序。</summary>
    public bool AllowReorder { get => GetValue(AllowReorderProperty); set => SetValue(AllowReorderProperty, value); }
    /// <summary>
    /// 行多时只生成可见的行（默认）。行数有上限、且需要所有行都已生成的表（例如可拖动排序的合奏成员表）设为假。
    /// </summary>
    public bool Virtualize { get => GetValue(VirtualizeProperty); set => SetValue(VirtualizeProperty, value); }
    /// <summary>右键表头时是否提供选择显示列、恢复默认列与改列名的菜单。</summary>
    public bool AllowColumnMenu { get => GetValue(AllowColumnMenuProperty); set => SetValue(AllowColumnMenuProperty, value); }
    /// <summary>
    /// 复制只取这一列（列标识）：Ctrl+C 与行右键菜单把所选各行这一列的文字按行放进剪贴板，不带表头与其他列；
    /// 右键菜单只给这一项复制（文字取 <see cref="CopyLabel"/>），不给“复制单元格”。为空时复制整行（制表符分隔、含表头）。
    /// </summary>
    public string? CopyColumnId { get => GetValue(CopyColumnIdProperty); set => SetValue(CopyColumnIdProperty, value); }
    /// <summary>设了 <see cref="CopyColumnId"/> 时右键菜单复制项的文字（例如“复制乐谱名”）；为空时用“复制”。</summary>
    public string? CopyLabel { get => GetValue(CopyLabelProperty); set => SetValue(CopyLabelProperty, value); }
    /// <summary>行的标识：数据重读后按它恢复选择；缺省用行对象本身比较。</summary>
    public Func<object, object?>? RowKey { get; set; }

    /// <summary>复制到剪贴板的文字；测试与页面可以据此确认复制的内容。</summary>
    public event EventHandler<string>? Copied;
    /// <summary>在行上右键时先交给页面（发送者是该行，<c>DataContext</c> 为行对象）；页面标记已处理时不再显示复制菜单。</summary>
    public event EventHandler<ContextRequestedEventArgs>? RowContextRequested;
    /// <summary>列的顺序、显隐或宽度变化。</summary>
    public event EventHandler? LayoutChanged;

    public int LayoutVersion { get; private set; }
    public IReadOnlyList<ColumnSlot> Slots => slots;
    public double TotalWidth => slots.Count == 0 ? 0 : slots[^1].X + slots[^1].Width;
    /// <summary>当前显示的行：筛选与排序之后的顺序。</summary>
    public IReadOnlyList<object> View => view;
    public IReadOnlyList<TableColumn> VisibleColumns => visible;
    public IReadOnlyList<object> SelectedRows =>
        list.Selection.SelectedIndexes.Where(i => i >= 0 && i < view.Count).Order().Select(i => view[i]).ToList();
    public IReadOnlyList<int> MatchIndices => matches;
    public int CurrentMatch => currentMatch < 0 || currentMatch >= matches.Count ? -1 : matches[currentMatch];
    public bool SearchOpen => searchBar.IsVisible;
    public IReadOnlyDictionary<string, ColumnFilter> Filters => filters;
    public IReadOnlyList<TableSort> Sort => settings.Sort ?? [];
    internal ListBox List => list;
    internal TextBox SearchBox => searchBox;
    internal IReadOnlyList<TableHeaderCell> HeaderCells => headerCells;

    private TableChoicePopup? choicePopup;

    /// <summary>行内选择框（<see cref="TableChoice"/>）整表共用的选择浮层；第一次打开时建立。</summary>
    internal TableChoicePopup ChoicePopup => choicePopup ??= new TableChoicePopup();

    private TableInputEditor? inputEditor;

    /// <summary>行内输入（<see cref="TableInput"/>）整表共用的输入框；第一次编辑时建立。</summary>
    internal TableInputEditor InputEditor => inputEditor ??= new TableInputEditor();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (watched is not null) watched.CollectionChanged -= OnSourceChanged;
            watched = ItemsSource as INotifyCollectionChanged;
            if (watched is not null) watched.CollectionChanged += OnSourceChanged;
            // 列也在变（例如“按技能／按目标”同时换表标识、列与行）：等合并后的那一次 ApplyColumns 一并重排行。
            if (!columnsPending) Rebuild();
        }
        else if (change.Property == ColumnsSourceProperty || change.Property == TableIdProperty || change.Property == RowDetailTemplateProperty)
            RequestColumns();
        else if (change.Property == EmptyTextProperty) empty.Text = EmptyText;
        else if (change.Property == BodyMaxHeightProperty && body is not null) body.MaxHeight = BodyMaxHeight;
        else if (change.Property == CornerContentProperty)
        {
            cornerHost.Child = CornerContent;
            UpdateGroupRow();
            UpdateCornerClip();
        }
        else if (change.Property == AllowSortProperty || change.Property == AllowFilterProperty) BuildHeader();
        else if (change.Property == VirtualizeProperty)
            list.ItemsPanel = Virtualize ? new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) : new FuncTemplate<Panel?>(() => new StackPanel());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TableSettingsStore.Changed += OnSettingsChanged;
        Localization.UiText.LanguageChanged += OnLanguageChanged;
        // 页面切走再回来时表格重新挂上视觉树：列与列设置都没变就不重建表头和各行单元格。
        if (columnsDirty || TableId is not null && !Equals(TableSettingsStore.Get(TableId), settings)) ApplyColumns();
    }

    // 列定义或表标识变化后是否还没按新的列排过（挂上视觉树前的变化在挂上时补做）。
    private bool columnsDirty = true;
    private bool columnsPending;

    /// <summary>
    /// 表标识、列、行详情模板在同一次绑定更新里先后变化时（“按技能／按目标”切换一次就有三项），只在这一轮结束后重排一次，
    /// 不为每一项各重建一遍表头与全部单元格。没有挂在窗口上时立即重排。
    /// </summary>
    private void RequestColumns()
    {
        if (!((Visual)this).IsAttachedToVisualTree())
        {
            ApplyColumns();
            return;
        }
        if (columnsPending) return;
        columnsPending = true;
        Dispatcher.UIThread.Post(() => { if (columnsPending) ApplyColumns(); }, DispatcherPriority.Send);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TableSettingsStore.Changed -= OnSettingsChanged;
        Localization.UiText.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnSettingsChanged(object? sender, string id)
    {
        if (id == TableId) ApplyColumns();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        BuildHeader();
        Refresh();
    }

    // ---------- 列 ----------

    private void ApplyColumns()
    {
        columnsPending = false;
        // 没挂在窗口上时表头文字资源还取不到，挂上时再排一次。
        columnsDirty = !((Visual)this).IsAttachedToVisualTree();
        all = ColumnsSource ?? Columns;
        if (TableId is not null) settings = TableSettingsStore.Get(TableId);
        var order = TableQuery.ResolveOrder(all.Select(column => column.Id).ToList(), settings.Order);
        var byId = all.ToDictionary(column => column.Id, StringComparer.Ordinal);
        visible = order.Select(id => byId[id]).Where(IsShown).ToList();
        if (visible.Count == 0 && all.Count > 0) visible = [byId[order[0]]];
        widths = all.ToDictionary(column => column.Id,
            column => Math.Max(column.MinWidth, settings.Widths?.GetValueOrDefault(column.Id) is > 0 and var saved ? saved : column.Width),
            StringComparer.Ordinal);
        foreach (var id in filters.Keys.Where(id => !byId.ContainsKey(id)).ToList()) filters.Remove(id);
        Relayout(rebuildHeader: true);
        Rebuild();
    }

    private bool IsShown(TableColumn column) =>
        !(settings.Hidden?.Contains(column.Id) ?? false) && (!column.HiddenByDefault || (settings.Shown?.Contains(column.Id) ?? false));

    private void MoveHeader()
    {
        header.Move(headerCells.Select((cell, i) => ((Control)cell, slots[i].X, slots[i].Width)).ToList());
        BuildGroups();
    }

    private void Relayout(bool rebuildHeader = false)
    {
        // 表体右侧固定让出滚动条槽位（ScrollGutter）：填充列只铺到槽位左侧，表头与列组标题同样让出，横向滚动时与表体对齐。
        var gutter = ScrollGutter.Width(this);
        headerScroller.Margin = groupScroller.Margin = new Thickness(0, 0, gutter, 0);
        var viewport = Math.Max(0, Bounds.Width - gutter);
        double x = 0;
        var next = new List<ColumnSlot>();
        foreach (var column in visible)
        {
            var width = widths.GetValueOrDefault(column.Id, column.Width);
            next.Add(new ColumnSlot(column, x, width));
            x += width;
        }
        var extra = viewport - x;
        var fill = next.FindIndex(slot => slot.Column.Fill);
        if (extra > 0 && fill >= 0)
        {
            next[fill] = next[fill] with { Width = next[fill].Width + extra };
            for (var i = fill + 1; i < next.Count; i++) next[i] = next[i] with { X = next[i].X + extra };
        }
        var sameColumns = headerCells.Count == next.Count && headerCells.Select(cell => cell.Column).SequenceEqual(next.Select(slot => slot.Column));
        slots = next;
        LayoutVersion++;
        // 只是列宽变了（表格第一次排出宽度、窗口变宽、拖动列边界）：表头单元格挪位置，不重建。
        if (sameColumns && !rebuildHeader) MoveHeader();
        else BuildHeader();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        foreach (var container in list.GetRealizedContainers()) container.InvalidateMeasure();
    }

    /// <summary>列的表头文字：改过的名称、界面文字资源或原文。</summary>
    public string HeaderText(TableColumn column)
    {
        if (settings.Titles?.GetValueOrDefault(column.Id) is { Length: > 0 } title) return title;
        if (column.HeaderKey is { } key && this.TryFindResource(key, out var value) && value is string text) return text;
        return column.Header ?? column.Id;
    }

    private string GroupText(TableColumn column) =>
        column.GroupKey is { } key && this.TryFindResource(key, out var value) && value is string text ? text : column.Group ?? string.Empty;

    // 表头：同一列的表头单元格留用（事件只在建立时接一次），只更新文字、排序标记与筛选状态。
    private void BuildHeader()
    {
        var existing = headerCells.ToDictionary(cell => cell.Column);
        headerCells.Clear();
        var items = new List<(Control, double, double)>();
        var sort = Sort;
        foreach (var slot in slots)
        {
            if (!existing.Remove(slot.Column, out var cell))
            {
                cell = new TableHeaderCell(slot.Column);
                var created = cell;
                cell.FilterButton.Bind(ToolTip.TipProperty, cell.FilterButton.GetResourceObservable("T.Filter"));
                cell.FilterButton.Bind(Avalonia.Automation.AutomationProperties.NameProperty, cell.FilterButton.GetResourceObservable("T.Filter"));
                cell.FilterButton.Click += (_, _) => OpenFilter(created);
                cell.PointerPressed += OnHeaderPressed;
                cell.PointerMoved += OnHeaderMoved;
                cell.PointerReleased += OnHeaderReleased;
                cell.PointerCaptureLost += (_, _) => EndHeaderDrag();
                cell.Gripper.PointerPressed += OnGripPressed;
                cell.Gripper.PointerMoved += OnGripMoved;
                cell.Gripper.PointerReleased += OnGripReleased;
            }
            cell.Title.Text = HeaderText(slot.Column);
            ToolTip.SetTip(cell.Title, cell.Title.Text);
            var key = sort.Select((item, index) => (item, index)).FirstOrDefault(pair => pair.item.ColumnId == slot.Column.Id);
            cell.SortGlyph.IsVisible = AllowSort && key.item is not null;
            cell.SortGlyph.Text = key.item is null ? string.Empty
                : (key.item.Descending ? "▼" : "▲") + (sort.Count > 1 ? (key.index + 1).ToString(CultureInfo.InvariantCulture) : string.Empty);
            cell.FilterButton.IsVisible = AllowFilter && slot.Column.Filterable;
            cell.FilterButton.Classes.Set("active", filters.TryGetValue(slot.Column.Id, out var filter) && filter.IsActive);
            cell.Classes.Set("sortable", AllowSort && slot.Column.Sortable);
            cell.Cursor = AllowSort && slot.Column.Sortable ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            headerCells.Add(cell);
            items.Add((cell, slot.X, slot.Width));
        }
        header.SetSlots(items);
        BuildGroups();
    }

    private void BuildGroups()
    {
        var runs = new List<(Control, double, double)>();
        if (slots.Any(slot => GroupText(slot.Column).Length > 0))
        {
            for (var i = 0; i < slots.Count;)
            {
                var text = GroupText(slots[i].Column);
                var j = i;
                while (j + 1 < slots.Count && GroupText(slots[j + 1].Column) == text) j++;
                if (text.Length > 0)
                {
                    var label = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
                    label.Classes.Add("muted");
                    ToolTip.SetTip(label, text);
                    // 有客户端图标的列组（暴击、元素等）在图标可用时只显示图标，组标题进图标的悬浮提示。
                    Control title = slots[i].Column.GroupIcon is { Length: > 0 } icon
                        ? new ClientIconSlot { Icon = icon, Label = text, FontSize = 12, Child = label, IconAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
                        : label;
                    var box = new Border { Child = title, Padding = new Thickness(8, 4), Margin = new Thickness(4, 0) };
                    box.Classes.Add("tablegroup");
                    runs.Add((box, slots[i].X, slots[j].X + slots[j].Width - slots[i].X));
                }
                i = j + 1;
            }
        }
        groups.SetSlots(runs);
        groupScroller.IsVisible = runs.Count > 0;
        UpdateGroupRow();
        SyncHeaderOffset();
    }

    private void SaveSettings(TableSettings next)
    {
        settings = next;
        if (TableId is null) ApplyColumns();
        else TableSettingsStore.Set(TableId, next);
    }

    private TableSettings Current() => settings with
    {
        Widths = widths.Where(pair => all.Any(column => column.Id == pair.Key && Math.Abs(column.Width - pair.Value) > 0.5))
            .ToDictionary(pair => pair.Key, pair => pair.Value)
    };

    // ---------- 行 ----------

    private void Rebuild()
    {
        var rows = ItemsSource?.Cast<object>() ?? [];
        var next = TableQuery.Apply(rows, all, filters, AllowSort ? Sort : []);
        if (!next.SequenceEqual(view))
        {
            var key = RowKey ?? (row => row);
            var selected = list.Selection.SelectedIndexes.Where(i => i >= 0 && i < view.Count).Select(i => key(view[i])).ToHashSet();
            view = next;
            // 槽与行容器按位置固定：行数不变时集合不发事件，容器不回收、不重建，只把新的行对象写进槽和该位置的容器。
            // 用 Replace 换项时虚拟化面板会回收该位置的容器、从回收池取另一个补上，带着悬停状态的容器因此出现在别的行。
            var shared = Math.Min(bodyRows.Count, next.Count);
            for (var i = 0; i < shared; i++)
                if (bodyRows[i] is TableSlot slot && !Equals(slot.Row, next[i])) slot.Row = next[i];
            if (next.Count > bodyRows.Count) bodyRows.AddRange(next.Skip(bodyRows.Count).Select(row => (object)new TableSlot(row)).ToList());
            else if (next.Count < bodyRows.Count) bodyRows.RemoveRange(next.Count, bodyRows.Count - next.Count);
            foreach (var container in list.GetRealizedContainers())
                if (container is ContentControl content && list.IndexFromContainer(container) is var index and >= 0 && index < bodyRows.Count
                    && bodyRows[index] is TableSlot shown)
                    TableList.Show(content, shown);
            if (selected.Count > 0)
            {
                var wanted = Enumerable.Range(0, view.Count).Where(i => selected.Contains(key(view[i]))).ToList();
                if (!wanted.SequenceEqual(list.Selection.SelectedIndexes.Order()))
                {
                    list.Selection.BeginBatchUpdate();
                    list.Selection.Clear();
                    foreach (var i in wanted) list.Selection.Select(i);
                    list.Selection.EndBatchUpdate();
                }
            }
        }
        foreach (var container in list.GetRealizedContainers())
            if (container.GetVisualDescendants().OfType<TableRowPresenter>().FirstOrDefault() is { } presenter) presenter.Refresh();
        empty.IsVisible = view.Count == 0 && !string.IsNullOrEmpty(EmptyText);
        UpdateSearch(false);
    }

    /// <summary>重新取各行文字并按当前筛选与排序重排（例如编辑了自定义列）。</summary>
    public void Refresh() => Rebuild();

    private void Mark(Control container, int index)
    {
        var row = index >= 0 && index < view.Count ? view[index] : null;
        container.Classes.Set("match", row is not null && matchSet.Contains(row));
        container.Classes.Set("currentmatch", index >= 0 && index == CurrentMatch);
    }

    private void MarkAll()
    {
        foreach (var container in list.GetRealizedContainers()) Mark(container, list.IndexFromContainer(container));
    }

    private void OnBodyScrolled(object? sender, ScrollChangedEventArgs e) => SyncHeaderOffset();

    private void SyncHeaderOffset()
    {
        var x = body?.Offset.X ?? 0;
        headerScroller.Offset = new Vector(x, 0);
        groupScroller.Offset = new Vector(x, 0);
    }

    private int IndexOf(object? source)
    {
        var container = (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        return container is null ? -1 : list.IndexFromContainer(container);
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        var index = IndexOf(e.Source);
        if (index >= 0) cursor = index;
    }

    /// <summary>把焦点交回该行（编辑结束后）。</summary>
    internal void FocusRow(object? row)
    {
        var index = row is null ? -1 : view.IndexOf(row);
        if (index < 0) return;
        Dispatcher.UIThread.Post(() => list.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
    }

    // ---------- 键盘 ----------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var inText = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TextBox>().Any() == true;
        // 方向键只在焦点落在行本身时移动选择；行内的下拉框、勾选框与 Alt 组合键（例如成员表的 Alt+↑/↓）照常交给它们。
        var onRow = (e.Source is ListBoxItem || e.Source == list) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (ctrl && e.Key == Key.F) { OpenSearch(); e.Handled = true; return; }
        if (e.Key == Key.F3) { Jump(shift ? -1 : 1); e.Handled = true; return; }
        if (inText) return;
        switch (e.Key)
        {
            case Key.Escape when SearchOpen:
                CloseSearch();
                e.Handled = true;
                break;
            case Key.C when ctrl:
                _ = CopyAsync(SelectionText());
                e.Handled = true;
                break;
            case Key.A when ctrl:
                list.Selection.SelectAll();
                e.Handled = true;
                break;
            case Key.F2 when e.Source is TableInput input:
                input.BeginEdit();
                e.Handled = true;
                break;
            case Key.F2:
                BeginEdit();
                e.Handled = true;
                break;
            case Key.Up when onRow: Navigate(cursor - 1, shift); e.Handled = true; break;
            case Key.Down when onRow: Navigate(cursor + 1, shift); e.Handled = true; break;
            case Key.Home when onRow: Navigate(0, shift); e.Handled = true; break;
            case Key.End when onRow: Navigate(view.Count - 1, shift); e.Handled = true; break;
            case Key.PageUp when onRow: Navigate(cursor - PageRows(), shift); e.Handled = true; break;
            case Key.PageDown when onRow: Navigate(cursor + PageRows(), shift); e.Handled = true; break;
        }
    }

    private int PageRows()
    {
        var containers = list.GetRealizedContainers().ToList();
        var row = containers.Count == 0 ? 34 : Math.Max(1, containers.Average(container => container.Bounds.Height));
        return Math.Max(1, (int)((body?.Viewport.Height ?? 340) / row) - 1);
    }

    private void Navigate(int index, bool extend)
    {
        if (view.Count == 0) return;
        index = Math.Clamp(index, 0, view.Count - 1);
        var anchor = list.Selection.AnchorIndex >= 0 ? list.Selection.AnchorIndex : Math.Max(0, cursor);
        list.Selection.BeginBatchUpdate();
        list.Selection.Clear();
        if (extend)
        {
            list.Selection.SelectRange(Math.Min(anchor, index), Math.Max(anchor, index));
            list.Selection.AnchorIndex = anchor;
        }
        else list.Selection.Select(index);
        list.Selection.EndBatchUpdate();
        cursor = index;
        list.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() => list.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
    }

    /// <summary>在当前行的第一个可编辑列上打开编辑框（F2）。</summary>
    public void BeginEdit()
    {
        var index = cursor >= 0 && cursor < view.Count ? cursor : list.SelectedIndex;
        if (index < 0 || visible.FirstOrDefault(column => column.Editable) is not { } column) return;
        list.ScrollIntoView(index);
        list.UpdateLayout();
        if (list.ContainerFromIndex(index)?.GetVisualDescendants().OfType<TableRowPresenter>().FirstOrDefault() is { } presenter)
            presenter.BeginEdit(column);
    }

    // ---------- 复制与导出 ----------

    /// <summary>
    /// 所选行的复制文字：表头一行加所选行，按当前列顺序与可见列，制表符分隔；设了 <see cref="CopyColumnId"/> 时
    /// 只有所选各行这一列的文字，每行一条（CRLF 分隔，文字里的换行换成空格）。
    /// </summary>
    public string SelectionText() => CopyColumn is { } column
        ? string.Join("\r\n", SelectedRows.Select(row => column.TextOf(row).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ')))
        : TableQuery.Tsv(SelectedRows, visible, HeaderText);

    private TableColumn? CopyColumn => CopyColumnId is { Length: > 0 } id ? all.FirstOrDefault(column => column.Id == id) : null;

    private async Task CopyAsync(string text)
    {
        if (text.Length == 0) return;
        Copied?.Invoke(this, text);
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    /// <summary>当前看到的内容：列顺序、可见列、筛选、排序与自定义列，UTF-8 带 BOM 的 CSV。</summary>
    public byte[] ExportBytes() => TableQuery.CsvBytes(view, visible, HeaderText);

    /// <summary>用系统保存对话框选位置后写出 CSV；取消时返回空。</summary>
    public async Task<string?> ExportCsvAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return null;
        var name = TableQuery.SafeFileName(ExportFileName ?? TableId ?? "table");
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? name : name + ".csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        if (file is null) return null;
        var bytes = ExportBytes();
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(bytes);
        return file.Name;
    }

    // ---------- 检索 ----------

    /// <summary>打开检索框并聚焦（Ctrl+F）。</summary>
    public void OpenSearch()
    {
        searchBar.IsVisible = true;
        Dispatcher.UIThread.Post(() => { searchBox.Focus(); searchBox.SelectAll(); }, DispatcherPriority.Loaded);
        UpdateSearch(false);
    }

    public void CloseSearch()
    {
        searchBar.IsVisible = false;
        searchBox.Text = string.Empty;
        UpdateSearch(false);
        var index = cursor >= 0 && cursor < view.Count ? cursor : 0;
        if (view.Count > 0) Dispatcher.UIThread.Post(() => list.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
    }

    public string SearchText { get => searchBox.Text ?? string.Empty; set => searchBox.Text = value; }

    private void UpdateSearch(bool jumpToFirst)
    {
        var term = SearchOpen ? searchBox.Text : null;
        var previous = CurrentMatch;
        matches = [];
        for (var i = 0; i < view.Count; i++)
            if (TableQuery.Matches(view[i], visible, term)) matches.Add(i);
        matchSet = matches.Select(i => view[i]).ToHashSet();
        currentMatch = matches.Count == 0 ? -1 : jumpToFirst ? 0 : Math.Max(0, matches.IndexOf(previous));
        searchCount.Text = string.IsNullOrWhiteSpace(term) ? string.Empty
            : matches.Count == 0 ? (this.TryFindResource("T.NoMatch", out var none) ? none as string : null)
            : $"{currentMatch + 1} / {matches.Count}";
        MarkAll();
        if (jumpToFirst && CurrentMatch >= 0) Reveal(CurrentMatch);
    }

    /// <summary>跳到下一个（或上一个）匹配行并选中它。</summary>
    public void Jump(int step)
    {
        if (matches.Count == 0) return;
        currentMatch = ((currentMatch < 0 ? (step > 0 ? -1 : 0) : currentMatch) + step + matches.Count) % matches.Count;
        searchCount.Text = $"{currentMatch + 1} / {matches.Count}";
        MarkAll();
        Reveal(CurrentMatch);
    }

    private void Reveal(int index)
    {
        list.Selection.BeginBatchUpdate();
        list.Selection.Clear();
        list.Selection.Select(index);
        list.Selection.EndBatchUpdate();
        cursor = index;
        list.ScrollIntoView(index);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { Jump(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
    }

    private Button Chip(string key, Action action)
    {
        var button = new Button { VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("chip");
        button.Bind(Button.ContentProperty, button.GetResourceObservable(key));
        button.Click += (_, _) => action();
        return button;
    }

    // ---------- 排序、筛选、列顺序与列宽 ----------

    /// <summary>点表头：升序、降序、取消；按住 Shift 时在已有排序之后追加或切换这一列。</summary>
    public void ToggleSort(TableColumn column, bool append)
    {
        if (!AllowSort || !column.Sortable) return;
        var sort = Sort.ToList();
        var index = sort.FindIndex(item => item.ColumnId == column.Id);
        if (append)
        {
            if (index < 0) sort.Add(new(column.Id, false));
            else if (!sort[index].Descending) sort[index] = sort[index] with { Descending = true };
            else sort.RemoveAt(index);
        }
        else
        {
            var only = index >= 0 ? sort[index] : null;
            sort = only is null ? [new(column.Id, false)] : only.Descending ? [] : [only with { Descending = true }];
        }
        SaveSettings(Current() with { Sort = sort });
    }

    public void SetFilter(string columnId, ColumnFilter? filter)
    {
        if (filter is { IsActive: true }) filters[columnId] = filter;
        else filters.Remove(columnId);
        foreach (var cell in headerCells.Where(cell => cell.Column.Id == columnId)) cell.FilterButton.Classes.Set("active", filter is { IsActive: true });
        Rebuild();
    }

    /// <summary>把一列移到另一列之前（目标为空时移到最后）。</summary>
    public void MoveColumn(string id, string? before)
    {
        var order = TableQuery.ResolveOrder(all.Select(column => column.Id).ToList(), settings.Order).ToList();
        if (!order.Remove(id)) return;
        var target = before is null ? -1 : order.IndexOf(before);
        if (target < 0) order.Add(id);
        else order.Insert(target, id);
        SaveSettings(Current() with { Order = order });
    }

    public void SetColumnShown(TableColumn column, bool shown)
    {
        if (!shown && (!column.CanHide || visible.Count <= 1)) return;
        var hidden = (settings.Hidden ?? []).Where(id => id != column.Id).ToList();
        var extra = (settings.Shown ?? []).Where(id => id != column.Id).ToList();
        if (!shown) hidden.Add(column.Id);
        else if (column.HiddenByDefault) extra.Add(column.Id);
        SaveSettings(Current() with { Hidden = hidden, Shown = extra });
    }

    /// <summary>恢复默认列：顺序、显隐与列宽回到列定义；排序与改过的列名保留。</summary>
    public void ResetColumns()
    {
        widths = all.ToDictionary(column => column.Id, column => column.Width, StringComparer.Ordinal);
        SaveSettings(settings with { Order = null, Widths = null, Hidden = null, Shown = null });
    }

    public void SetWidth(string id, double width, bool save)
    {
        var column = all.FirstOrDefault(item => item.Id == id);
        if (column is null) return;
        widths[id] = Math.Clamp(width, column.MinWidth, 2000);
        if (save) SaveSettings(Current());
        else Relayout();
    }

    /// <summary>按表头与各行文字的实际宽度设定列宽（双击列边界）。</summary>
    public void AutoFit(TableColumn column)
    {
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.Normal);
        double Measure(string text, double size) =>
            string.IsNullOrEmpty(text) ? 0 : new TextLayout(text, typeface, size, null).WidthIncludingTrailingWhitespace;
        var width = Measure(HeaderText(column), 12) + 18 + (AllowSort && column.Sortable ? 22 : 0) + (AllowFilter && column.Filterable ? 26 : 0);
        foreach (var row in view) width = Math.Max(width, Measure(column.TextOf(row), GetValue(TextElement.FontSizeProperty)) + 22);
        SetWidth(column.Id, Math.Min(Math.Ceiling(width), 800), true);
    }

    public void Rename(TableColumn column, string title)
    {
        var titles = new Dictionary<string, string>(settings.Titles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(title)) titles.Remove(column.Id);
        else titles[column.Id] = title.Trim();
        SaveSettings(Current() with { Titles = titles });
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || sender is not TableHeaderCell cell || !e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().Any(visual => visual == cell.FilterButton || visual == cell.Gripper) == true) return;
        press = (cell, e.GetPosition(header), e.Pointer);
        dragging = false;
        e.Pointer.Capture(cell);
        e.Handled = true;
    }

    private void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (press is not { } p || p.Cell != sender) return;
        var dx = e.GetPosition(header).X - p.Start.X;
        if (!dragging && AllowReorder && Math.Abs(dx) > DragThreshold) dragging = true;
        if (!dragging) return;
        p.Cell.RenderTransform = new TranslateTransform(dx, 0);
        p.Cell.ZIndex = 10;
        p.Cell.Classes.Add("dragging");
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (press is not { } p || p.Cell != sender) return;
        var wasDragging = dragging;
        var x = e.GetPosition(header).X;
        EndHeaderDrag();
        if (wasDragging)
        {
            var target = slots.FirstOrDefault(slot => x < slot.X + slot.Width / 2 && slot.Column != p.Cell.Column).Column;
            var index = slots.FindIndex(slot => slot.Column == p.Cell.Column);
            var targetIndex = target is null ? slots.Count : slots.FindIndex(slot => slot.Column == target);
            if (targetIndex != index && targetIndex != index + 1) MoveColumn(p.Cell.Column.Id, target?.Id);
        }
        else ToggleSort(p.Cell.Column, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private void EndHeaderDrag()
    {
        if (press is not { } p) return;
        press = null;
        dragging = false;
        p.Cell.RenderTransform = null;
        p.Cell.ZIndex = 0;
        p.Cell.Classes.Remove("dragging");
        if (p.Pointer.Captured == p.Cell) p.Pointer.Capture(null);
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border grip || grip.GetVisualAncestors().OfType<TableHeaderCell>().FirstOrDefault() is not { } cell) return;
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            resize = null;
            AutoFit(cell.Column);
            return;
        }
        resize = (cell, e.GetPosition(header).X, widths.GetValueOrDefault(cell.Column.Id, cell.Column.Width));
        e.Pointer.Capture(grip);
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (resize is not { } r) return;
        SetWidth(r.Cell.Column.Id, r.Width + e.GetPosition(header).X - r.Start, false);
        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (resize is not { } r) return;
        resize = null;
        e.Pointer.Capture(null);
        SetWidth(r.Cell.Column.Id, widths.GetValueOrDefault(r.Cell.Column.Id, r.Width), true);
        e.Handled = true;
    }

    private void OpenFilter(TableHeaderCell cell)
    {
        var column = cell.Column;
        filters.TryGetValue(column.Id, out var current);
        var panel = new StackPanel { Spacing = 8, MinWidth = 220 };
        var title = new TextBlock { Text = HeaderText(column), FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        panel.Children.Add(title);
        TextBox Box(string hintKey, string? text)
        {
            var box = new TextBox { Text = text };
            box.Classes.Add("field");
            box.Bind(TextBox.PlaceholderTextProperty, box.GetResourceObservable(hintKey));
            box.Bind(Avalonia.Automation.AutomationProperties.NameProperty, box.GetResourceObservable(hintKey));
            panel.Children.Add(box);
            return box;
        }
        static double? Parse(string? text) => TableQuery.Number(text);
        if (column.Kind == ColumnKind.Number)
        {
            var min = Box("T.FilterMin", current?.Min?.ToString(CultureInfo.InvariantCulture));
            var max = Box("T.FilterMax", current?.Max?.ToString(CultureInfo.InvariantCulture));
            void Apply() => SetFilter(column.Id, new ColumnFilter(null, Parse(min.Text), Parse(max.Text)));
            min.TextChanged += (_, _) => Apply();
            max.TextChanged += (_, _) => Apply();
        }
        else
        {
            var contains = Box("T.FilterContains", current?.Contains);
            contains.TextChanged += (_, _) => SetFilter(column.Id, new ColumnFilter(contains.Text));
        }
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.FlyoutPresenterClasses.Add("tablepopup");
        var clear = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        clear.Classes.Add("chip");
        clear.Bind(Button.ContentProperty, clear.GetResourceObservable("T.ClearFilter"));
        clear.Click += (_, _) => { SetFilter(column.Id, null); flyout.Hide(); };
        panel.Children.Add(clear);
        flyout.ShowAt(cell.FilterButton);
    }

    // ---------- 右键菜单 ----------

    private string Text(string key) => this.TryFindResource(key, out var value) && value is string text ? text : key;

    private void OnHeaderContext(object? sender, ContextRequestedEventArgs e)
    {
        if (!AllowColumnMenu) return;
        var items = new List<Control>();
        foreach (var column in TableQuery.ResolveOrder(all.Select(item => item.Id).ToList(), settings.Order)
                     .Select(id => all.First(item => item.Id == id)))
        {
            var shown = visible.Contains(column);
            var item = new MenuItem
            {
                Header = HeaderText(column), ToggleType = MenuItemToggleType.CheckBox, IsChecked = shown,
                IsEnabled = column.CanHide && !(shown && visible.Count <= 1)
            };
            item.Click += (_, _) => SetColumnShown(column, !shown);
            items.Add(item);
        }
        items.Add(new Separator());
        var reset = new MenuItem { Header = Text("T.ResetColumns") };
        reset.Click += (_, _) => ResetColumns();
        items.Add(reset);
        var at = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TableHeaderCell>().FirstOrDefault();
        if (at?.Column is { Renamable: true } renamable)
        {
            var rename = new MenuItem { Header = Text("T.Rename") };
            rename.Click += (_, _) => OpenRename(at, renamable);
            items.Add(rename);
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(at ?? (Control)header);
        e.Handled = true;
    }

    private void OpenRename(TableHeaderCell cell, TableColumn column)
    {
        var box = new TextBox { Text = HeaderText(column), MinWidth = 200 };
        box.Classes.Add("field");
        box.Bind(Avalonia.Automation.AutomationProperties.NameProperty, box.GetResourceObservable("T.Rename"));
        var flyout = new Flyout { Content = box, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.FlyoutPresenterClasses.Add("tablepopup");
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return) { Rename(column, box.Text ?? string.Empty); flyout.Hide(); e.Handled = true; }
            else if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; }
        };
        flyout.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        flyout.ShowAt(cell);
    }

    private void OnRowContext(object? sender, ContextRequestedEventArgs e)
    {
        var index = IndexOf(e.Source);
        if (index < 0) return;
        if (!list.Selection.IsSelected(index))
        {
            list.Selection.Clear();
            list.Selection.Select(index);
        }
        cursor = index;
        var row = view[index];
        var presenter = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()
            ?.GetVisualDescendants().OfType<TableRowPresenter>().FirstOrDefault();
        RowContextRequested?.Invoke(presenter ?? (object)list, e);
        if (e.Handled) return;
        TableColumn? column = null;
        if (presenter is not null && e.TryGetPosition(presenter, out var point)) column = presenter.ColumnAt(point.X);
        var items = new List<Control>();
        var single = CopyColumn is not null;
        var copy = new MenuItem
        {
            Header = single && CopyLabel is { Length: > 0 } label ? label : Text("T.Copy"),
            InputGesture = new KeyGesture(Key.C, KeyModifiers.Control)
        };
        copy.Click += (_, _) => _ = CopyAsync(SelectionText());
        items.Add(copy);
        if (!single)
        {
            var copyCell = new MenuItem { Header = Text("T.CopyCell"), IsEnabled = column is not null };
            copyCell.Click += (_, _) => { if (column is not null) _ = CopyAsync(column.TextOf(row)); };
            items.Add(copyCell);
        }
        if (CanExport)
        {
            var export = new MenuItem { Header = Text("T.ExportCsv") };
            export.Click += (_, _) => _ = ExportCsvAsync();
            items.Add(export);
        }
        new ContextMenu { ItemsSource = items }.Open(presenter ?? (Control)list);
        e.Handled = true;
    }
}
