using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Metadata;
using MabiTool.Desktop.Infrastructure;
using Path = Avalonia.Controls.Shapes.Path;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 卡片在页面网格（<see cref="CardGrid"/>）里的尺寸档（宽×高）。只在卡片放进网格时生效。
/// 宽度是一列、两列、三列或半宽（页面宽度的一半，三列时一列半，用于两张卡片等宽并排）。
/// 高度分两类：固定档是行高的倍数（最小单位半行，名称里的 AndHalf 表示多半行），内容放不下时在卡片内滚动；
/// 随内容档（名称以 Auto 结尾，以及全宽 <see cref="FullWidth"/>）高度取内容高度，同一行相邻的随内容卡片共用一行、行高取其中最高的一张。
/// 名称写进偏好文件，定下后不改名。
/// </summary>
public enum CardSize
{
    OneByOne,
    TwoByOne,
    ThreeByOne,
    TwoByTwo,
    ThreeByTwo,
    FullWidth,
    HalfByTwo,
    HalfByThree,
    HalfByFour,
    OneByAuto,
    TwoByAuto,
    HalfByAuto,
    OneByOneAndHalf,
    OneByTwo,
    OneByTwoAndHalf,
    OneByThree,
    OneByFour,
    TwoByThree,
    TwoByFour
}

/// <summary>
/// 页面卡片的统一外壳：面板底（<c>Border.panel</c>）里一行标题和其下的内容区。
/// 标题行从左到右是折叠箭头、可选图标（<see cref="Icon"/>）、标题（<see cref="Title"/>）、可选的副标题或状态（<see cref="Status"/>，占满剩余宽度），
/// 最右是操作区（<see cref="Actions"/>，按钮等可操作的控件）。箭头到状态这一段整行是一个切换按钮：点击、空格或回车折叠或展开，
/// 无障碍上报告“已展开／已折叠”。折叠后只留标题行，操作区一并隐藏；内容区由 <see cref="ExpandPanel"/> 过渡，内容仍在逻辑树里、绑定不断。
/// <para>
/// 折叠状态按“页面标识（<see cref="PageIdProperty"/>，写在页面根上向下继承）+ 卡片标识（<see cref="CardId"/>）”存入
/// <see cref="CardStateStore"/>；没有保存过时取 <see cref="DefaultExpanded"/>。两个标识不全时只在内存里。
/// </para>
/// </summary>
public sealed class Card : Control
{
    /// <summary>页面标识：写在页面根元素上，页面里的卡片继承。</summary>
    public static readonly AttachedProperty<string?> PageIdProperty =
        AvaloniaProperty.RegisterAttached<Card, Control, string?>("PageId", inherits: true);

    public static readonly StyledProperty<string?> CardIdProperty = AvaloniaProperty.Register<Card, string?>(nameof(CardId));
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<Card, string?>(nameof(Title));
    public static readonly StyledProperty<object?> TitleTipProperty = AvaloniaProperty.Register<Card, object?>(nameof(TitleTip));
    public static readonly StyledProperty<Control?> IconProperty = AvaloniaProperty.Register<Card, Control?>(nameof(Icon));
    public static readonly StyledProperty<Control?> StatusProperty = AvaloniaProperty.Register<Card, Control?>(nameof(Status));
    public static readonly StyledProperty<Control?> ActionsProperty = AvaloniaProperty.Register<Card, Control?>(nameof(Actions));
    public static readonly StyledProperty<Control?> BodyProperty = AvaloniaProperty.Register<Card, Control?>(nameof(Body));
    public static readonly StyledProperty<bool> IsCollapsibleProperty = AvaloniaProperty.Register<Card, bool>(nameof(IsCollapsible), true);
    public static readonly StyledProperty<bool> DefaultExpandedProperty = AvaloniaProperty.Register<Card, bool>(nameof(DefaultExpanded), true);
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<Card, bool>(nameof(IsExpanded), true, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<CardSize> SizeProperty = AvaloniaProperty.Register<Card, CardSize>(nameof(Size), CardSize.FullWidth);
    public static readonly StyledProperty<string?> AllowedSizesProperty = AvaloniaProperty.Register<Card, string?>(nameof(AllowedSizes));
    public static readonly StyledProperty<bool> ScrollsBodyProperty = AvaloniaProperty.Register<Card, bool>(nameof(ScrollsBody), true);
    public static readonly StyledProperty<Thickness?> PaddingProperty = AvaloniaProperty.Register<Card, Thickness?>(nameof(Padding));

    private readonly Border frame = new();
    private readonly CardToggle toggle = new();
    private readonly ExpandPanel expander = new() { IsExpanded = true };
    private readonly Panel chevron;
    private readonly Path closedMark = new() { Data = Geometry.Parse("M0,0 L5,4 L0,8 Z") };
    private readonly Path openMark = new() { Data = Geometry.Parse("M0,1 L8,1 L4,6 Z") };
    private readonly TextBlock title = new();
    private readonly Decorator iconHost = new() { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Decorator statusHost = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Decorator actionsHost = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly SlotMark editSlot = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Height = 0, IsVisible = false };
    private readonly BodyHost bodyHost = new();
    private ScrollViewer? scroller;
    private bool resolving;
    private CardGrid? grid;

    public Card()
    {
        closedMark.Bind(Shape.FillProperty, closedMark.GetResourceObservable("MutedBrush"));
        openMark.Bind(Shape.FillProperty, openMark.GetResourceObservable("MutedBrush"));
        chevron = new Panel { Width = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { closedMark, openMark } };
        title.Classes.Add("h2");
        // 窄栏里标题换行，不截断。
        title.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(chevron, Dock.Left);
        DockPanel.SetDock(iconHost, Dock.Left);
        DockPanel.SetDock(title, Dock.Left);
        toggle.Classes.Add("sectionhead");
        toggle.Content = new DockPanel { Children = { chevron, iconHost, title, statusHost } };
        toggle.IsChecked = true;
        toggle.IsCheckedChanged += (_, _) => SetCurrentValue(IsExpandedProperty, toggle.IsChecked == true);
        DockPanel.SetDock(actionsHost, Dock.Right);
        DockPanel.SetDock(editSlot, Dock.Right);
        expander.Child = bodyHost;
        frame.Classes.Add("panel");
        var head = new DockPanel { Children = { actionsHost, editSlot, toggle } };
        DockPanel.SetDock(head, Dock.Top);
        frame.Child = new DockPanel { Children = { head, expander } };
        LogicalChildren.Add(frame);
        VisualChildren.Add(frame);
        Apply();
    }

    public static string? GetPageId(Control control) => control.GetValue(PageIdProperty);
    public static void SetPageId(Control control, string? value) => control.SetValue(PageIdProperty, value);

    /// <summary>卡片标识，页面内唯一；与页面标识一起决定保存折叠状态、尺寸档与位置的键。</summary>
    public string? CardId { get => GetValue(CardIdProperty); set => SetValue(CardIdProperty, value); }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>标题文字上的悬停提示。</summary>
    public object? TitleTip { get => GetValue(TitleTipProperty); set => SetValue(TitleTipProperty, value); }

    /// <summary>标题左侧的图标。</summary>
    public Control? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <summary>标题右侧的副标题或状态：只读内容，在切换按钮里，折叠后仍显示。</summary>
    public Control? Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    /// <summary>标题行最右的操作区：按钮等可操作的控件，不在切换按钮里；折叠后隐藏。</summary>
    public Control? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    [Content]
    public Control? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <summary>缺省可折叠；关闭后没有箭头，标题行不可点，内容始终展开。</summary>
    public bool IsCollapsible { get => GetValue(IsCollapsibleProperty); set => SetValue(IsCollapsibleProperty, value); }

    /// <summary>没有保存过折叠状态时是否展开，缺省展开。</summary>
    public bool DefaultExpanded { get => GetValue(DefaultExpandedProperty); set => SetValue(DefaultExpandedProperty, value); }

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>网格里的缺省尺寸档；用户在编辑模式里改过的以保存的为准。</summary>
    public CardSize Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>这张卡片适用的尺寸档，逗号分隔的 <see cref="CardSize"/> 名称；缺省档总在其中。不写时只有缺省档。</summary>
    public string? AllowedSizes { get => GetValue(AllowedSizesProperty); set => SetValue(AllowedSizesProperty, value); }

    /// <summary>适用的尺寸档：缺省档在前，其后按 <see cref="AllowedSizes"/> 的顺序。</summary>
    public IReadOnlyList<CardSize> Sizes
    {
        get
        {
            var sizes = new List<CardSize> { Size };
            foreach (var name in (AllowedSizes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Enum.TryParse<CardSize>(name, out var size) && Enum.IsDefined(size) && !sizes.Contains(size)) sizes.Add(size);
            return sizes;
        }
    }

    /// <summary>
    /// 在网格里取固定高度的尺寸档时，内容放不下由卡片外壳提供纵向滚动（缺省）；内容自己安排滚动区的卡片（效果列表）设为假，
    /// 内容按卡片给出的固定高度排版。
    /// </summary>
    public bool ScrollsBody { get => GetValue(ScrollsBodyProperty); set => SetValue(ScrollsBodyProperty, value); }

    /// <summary>网格给出固定高度时为真：内容区占满标题行以下的高度。</summary>
    internal bool FillsHeight { get => expander.Fill; set => expander.Fill = value; }

    /// <summary>所在的网格，由网格在排版时写入；不在网格里时为空。</summary>
    internal CardGrid? Grid
    {
        get => grid;
        set
        {
            if (ReferenceEquals(grid, value)) return;
            grid = value;
            Apply();
        }
    }

    /// <summary>
    /// 编辑布局时标题行右端让给网格的布局菜单：给出宽度后操作区隐藏（收起的卡片同样留位），标题与状态在剩余宽度里排，
    /// 菜单不压在任何内容上。为空时恢复操作区。
    /// </summary>
    internal double? EditSlotWidth
    {
        get => editSlot.IsVisible ? editSlot.Width : null;
        set
        {
            if (value == EditSlotWidth) return;
            editSlot.IsVisible = value is not null;
            editSlot.Width = value ?? 0;
            Apply();
        }
    }

    /// <summary>布局菜单的位置（卡片坐标）：水平范围与标题行的纵向中线；标题行还没排版时为空。</summary>
    internal Rect? EditSlotBounds =>
        editSlot.IsVisible && editSlot.TranslatePoint(default, this) is { } origin ? new Rect(origin, editSlot.Bounds.Size) : null;

    /// <summary>布局菜单的位置变了（标题换行、卡片宽度变化）。</summary>
    internal event Action? EditSlotMoved { add => editSlot.Moved += value; remove => editSlot.Moved -= value; }

    /// <summary>只改显示、不写入保存的状态：编辑布局里的“恢复默认”先这样预览，按“完成”才保存，按“取消”再改回来。</summary>
    internal void Preview(bool expanded)
    {
        if (!IsCollapsible) return;
        resolving = true;
        SetCurrentValue(IsExpandedProperty, expanded);
        resolving = false;
    }

    /// <summary>已展开且不在过渡中：网格按尺寸档的固定高度排它；否则按它当前的实际高度排。</summary>
    internal bool IsSettled => !IsCollapsible || (IsExpanded && !expander.IsTransitioning);

    /// <summary>面板内边距；不设时用 <c>Border.panel</c> 的 18。</summary>
    public Thickness? Padding { get => GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    /// <summary>标题行的切换按钮（测试与无障碍用）。</summary>
    public ToggleButton Toggle => toggle;

    /// <summary>内容区的展开容器（测试据此确认过渡）。</summary>
    public ExpandPanel Expander => expander;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CardIdProperty || change.Property == PageIdProperty || change.Property == DefaultExpandedProperty) Resolve();
        else if (change.Property == IsExpandedProperty)
        {
            Apply();
            if (!resolving && IsCollapsible && GetPageId(this) is { Length: > 0 } page && CardId is { Length: > 0 } card)
                CardStateStore.SetExpanded(page, card, IsExpanded, DefaultExpanded);
        }
        else if (change.Property == TitleProperty || change.Property == TitleTipProperty || change.Property == IconProperty
                 || change.Property == StatusProperty || change.Property == ActionsProperty || change.Property == BodyProperty
                 || change.Property == IsCollapsibleProperty || change.Property == PaddingProperty || change.Property == ScrollsBodyProperty) Apply();
        if (change.Property == SizeProperty || change.Property == AllowedSizesProperty)
        {
            Apply();
            grid?.InvalidateMeasure();
        }
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        CardStateStore.Changed += OnStoreChanged;
        Resolve();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        CardStateStore.Changed -= OnStoreChanged;
        Grid = null;
    }

    // 别处改了同一张卡片的状态（另一个窗口里的同一页）：跟上，照常过渡。
    private void OnStoreChanged(object? sender, string key)
    {
        if (!IsCollapsible || GetPageId(this) is not { Length: > 0 } page || CardId is not { Length: > 0 } card) return;
        if (key != CardStateStore.Key(page, card)) return;
        resolving = true;
        SetCurrentValue(IsExpandedProperty, CardStateStore.IsExpanded(page, card, DefaultExpanded));
        resolving = false;
    }

    // 取保存的状态（没有则取缺省），不经过渡直接到位。
    private void Resolve()
    {
        if (!IsCollapsible) return;
        var expanded = GetPageId(this) is { Length: > 0 } page && CardId is { Length: > 0 } card
            ? CardStateStore.IsExpanded(page, card, DefaultExpanded)
            : DefaultExpanded;
        resolving = true;
        SetCurrentValue(IsExpandedProperty, expanded);
        resolving = false;
        expander.Snap();
    }

    private void Apply()
    {
        var expanded = !IsCollapsible || IsExpanded;
        title.Text = Title;
        ToolTip.SetTip(title, TitleTip);
        AutomationProperties.SetName(toggle, Title);
        iconHost.Child = Icon;
        iconHost.IsVisible = Icon is not null;
        statusHost.Child = Status;
        statusHost.IsVisible = Status is not null;
        actionsHost.Child = Actions;
        actionsHost.IsVisible = Actions is not null && expanded && !editSlot.IsVisible;
        // 网格里可以取固定高度档的卡片：内容放进外壳的滚动区（高度不固定时等同没有滚动区）；内容自己安排滚动的、只有随内容档的不放。
        if (grid is not null && ScrollsBody && Sizes.Any(CardGrid.IsFixedHeight))
        {
            scroller ??= new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            if (!ReferenceEquals(bodyHost.Child, scroller)) bodyHost.Child = scroller;
            scroller.Content = Body;
        }
        else
        {
            if (scroller is not null) scroller.Content = null;
            bodyHost.Child = Body;
        }
        chevron.IsVisible = IsCollapsible;
        closedMark.IsVisible = !expanded;
        openMark.IsVisible = expanded;
        toggle.IsHitTestVisible = toggle.Focusable = IsCollapsible;
        toggle.IsChecked = expanded;
        expander.IsExpanded = expanded;
        // 与别的卡片并排时，折叠的卡片不被拉到同一行的高度。
        frame.VerticalAlignment = expanded ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        if (Padding is { } padding) frame.Padding = padding; else frame.ClearValue(Decorator.PaddingProperty);
    }

    // 布局菜单的占位：宽度由网格给出、高度为零，排在标题行右端的纵向中线上。
    private sealed class SlotMark : Control
    {
        public event Action? Moved;

        protected override Size ArrangeOverride(Size finalSize)
        {
            Moved?.Invoke();
            return base.ArrangeOverride(finalSize);
        }
    }

    // 内容区与标题行之间的间距放在展开容器里面，随展开一起过渡；内容本身没有高度时不留间距。
    private sealed class BodyHost : Decorator
    {
        public BodyHost() => Padding = new Thickness(0, 10, 0, 0);

        protected override Size MeasureOverride(Size availableSize)
        {
            var size = base.MeasureOverride(availableSize);
            return Child is { IsVisible: true, DesiredSize.Height: > 0 } ? size : new Size(size.Width, 0);
        }
    }
}

/// <summary>
/// 卡片标题行的切换按钮：选中表示展开。无障碍上同时报告展开与收起状态（ExpandCollapse），读屏软件读出“已展开／已折叠”。
/// 卡片外壳自带；自己排版的折叠区（合奏页底部状态卡、染色页“默认预设”）单独使用同一个按钮。
/// </summary>
public sealed class CardToggle : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override AutomationPeer OnCreateAutomationPeer() => new TogglePeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty && ControlAutomationPeer.FromElement(this) is TogglePeer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                TogglePeer.StateOf(change.OldValue as bool?), TogglePeer.StateOf(change.NewValue as bool?));
    }

    private sealed class TogglePeer(CardToggle owner) : ToggleButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public static ExpandCollapseState StateOf(bool? isChecked) =>
            isChecked == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public ExpandCollapseState ExpandCollapseState => StateOf(owner.IsChecked);
        public bool ShowsMenu => false;
        public void Expand() => owner.SetCurrentValue(IsCheckedProperty, true);
        public void Collapse() => owner.SetCurrentValue(IsCheckedProperty, false);
    }
}
