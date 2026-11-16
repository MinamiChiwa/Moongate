using System.Collections;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Controls.Tables;

/// <summary>
/// 表格行内的选择框：平时只画当前值与下拉箭头（一个 <see cref="Border"/> 加一个自绘层，没有模板），
/// 点击、空格、回车或 F4 时才弹出选择浮层。浮层由所在的 <see cref="DataTable"/> 整表共用一个（不在表格里时自带一个），
/// 选中后写回 <see cref="SelectedIndex"/> 与 <see cref="SelectedItem"/>（默认双向绑定），行对象的属性照常收到改动。
/// <para>
/// 用来代替行内的 <see cref="ComboBox"/>：ComboBox 每个实例在首次排版时实例化整套模板（外框、内容呈现器、占位文字、箭头、弹出层，
/// 约 20 个视觉元素），成员表每行两个，切到合奏页的第一帧大部分花在这里。
/// </para>
/// 键盘：Tab 聚焦；空格、回车、F4 打开；未打开时 ↑／↓ 直接选上一项／下一项（与 ComboBox 相同）；浮层里 ↑／↓ 移动、
/// 空格或回车选中、Esc 关闭。Alt 组合键不处理（成员表的 Alt+↑/↓ 移动行）。外观由 <c>Border.tablechoice</c> 样式给出。
/// </summary>
public sealed class TableChoice : Border
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<TableChoice, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<TableChoice, int>(nameof(SelectedIndex), -1, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<TableChoice, object?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public static readonly DirectProperty<TableChoice, bool> IsDropDownOpenProperty =
        AvaloniaProperty.RegisterDirect<TableChoice, bool>(nameof(IsDropDownOpen), o => o.IsDropDownOpen, (o, v) => o.IsDropDownOpen = v);

    private static readonly Geometry Chevron = Geometry.Parse("M0,0 L4,4 L8,0");

    private readonly ChoiceFace face;
    private List<object> items = [];
    private bool syncing;
    private bool pressed;
    private bool open;
    private TableChoicePopup? ownPopup;

    public TableChoice()
    {
        Focusable = true;
        Classes.Add("tablechoice");
        Cursor = new Cursor(StandardCursorType.Hand);
        Child = face = new ChoiceFace(this);
    }

    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>可选项；显示文字取各项的 <c>ToString()</c>。</summary>
    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    /// <summary>选择浮层是否打开（打开的是整表共用的那一个，锚在这一格上）。</summary>
    public bool IsDropDownOpen
    {
        get => open;
        set
        {
            if (value == open) return;
            if (value) Open();
            else Popup.Hide(this);
        }
    }

    internal IReadOnlyList<object> Items => items;
    internal int ItemCount => items.Count;

    /// <summary>当前值的显示文字。</summary>
    public string Text => SelectedItem?.ToString() ?? string.Empty;

    private TableChoicePopup Popup => this.FindAncestorOfType<DataTable>()?.ChoicePopup ?? (ownPopup ??= new TableChoicePopup());

    internal void SetOpen(bool value)
    {
        if (value == open) return;
        SetAndRaise(IsDropDownOpenProperty, ref open, value);
        PseudoClasses.Set(":dropdownopen", value);
    }

    private void Open()
    {
        if (!IsEffectivelyEnabled || items.Count == 0 || !this.IsAttachedToVisualTree()) return;
        Popup.Show(this);
    }

    /// <summary>浮层里选中了第几项：写回选择（与在 ComboBox 里选中相同，绑定把值交给行对象）。</summary>
    internal void Choose(int index)
    {
        if (index < 0 || index >= items.Count) return;
        SelectedIndex = index;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            items = ItemsSource?.Cast<object>().ToList() ?? [];
            // 选择可能先于可选项到达（绑定顺序）：已选的项在列表里时按项定位，否则按序号取项。
            if (SelectedItem is not null && items.Contains(SelectedItem)) SyncIndex();
            else SyncItem();
            if (open) Popup.Refresh(this);
        }
        else if (change.Property == SelectedItemProperty)
        {
            face.InvalidateVisual();
            if (!syncing && SelectedItem is not null && items.Count > 0) SyncIndex();
        }
        else if (change.Property == SelectedIndexProperty && !syncing) SyncItem();
        else if (change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled && open) Popup.Hide(this);
    }

    private void SyncIndex() => Sync(() => SelectedIndex = items.FindIndex(item => Equals(item, SelectedItem)));

    private void SyncItem()
    {
        if (SelectedIndex >= 0 && SelectedIndex < items.Count) Sync(() => SelectedItem = items[SelectedIndex]);
    }

    private void Sync(Action action)
    {
        syncing = true;
        try { action(); }
        finally { syncing = false; }
        face.InvalidateVisual();
    }

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
        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this))) return;
        Focus(NavigationMethod.Pointer);
        if (open) Popup.Hide(this);
        else Open();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        pressed = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Space or Key.F4:
                Open();
                e.Handled = true;
                break;
            case Key.Up or Key.Down when !open && items.Count > 0:
                var next = Math.Clamp((SelectedIndex < 0 ? (e.Key == Key.Down ? -1 : items.Count) : SelectedIndex) + (e.Key == Key.Down ? 1 : -1), 0, items.Count - 1);
                if (next != SelectedIndex) Choose(next);
                e.Handled = true;
                break;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChoicePeer(this);

    // 当前值与箭头：一个自绘层，没有子控件（Border 自己的 Render 已画底色与边框）。
    private sealed class ChoiceFace(TableChoice owner) : Control
    {
        private const double ArrowWidth = 8;
        private TextLayout? layout;
        private (string Text, double Width, IBrush? Brush, FontFamily Family, double Size) key;

        protected override Size MeasureOverride(Size availableSize)
        {
            var size = GetValue(TextElement.FontSizeProperty);
            using var probe = new TextLayout("Ag国", new Typeface(GetValue(TextElement.FontFamilyProperty)), size, null);
            var width = double.IsInfinity(availableSize.Width) ? 80 : availableSize.Width;
            return new Size(width, probe.Height);
        }

        public override void Render(DrawingContext context)
        {
            var brush = GetValue(TextElement.ForegroundProperty);
            var family = GetValue(TextElement.FontFamilyProperty);
            var size = GetValue(TextElement.FontSizeProperty);
            var width = Math.Max(0, Bounds.Width - ArrowWidth - 8);
            var text = owner.Text;
            if (layout is null || key != (text, width, brush, family, size))
            {
                layout?.Dispose();
                key = (text, width, brush, family, size);
                layout = new TextLayout(text, new Typeface(family), size, brush, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: Math.Max(1, width));
            }
            if (text.Length > 0) layout.Draw(context, new Point(0, Math.Max(0, (Bounds.Height - layout.Height) / 2)));
            var muted = this.TryFindResource("MutedBrush", ActualThemeVariant, out var found) ? found as IBrush : null;
            var pen = new Pen(muted ?? brush, 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            using (context.PushTransform(Matrix.CreateTranslation(Bounds.Width - ArrowWidth, Math.Round(Bounds.Height / 2) - 2)))
                context.DrawGeometry(null, pen, Chevron);
        }
    }

    /// <summary>读屏：组合框，名称取 <c>AutomationProperties.Name</c>，值为当前显示文字，可展开与收起。</summary>
    private sealed class ChoicePeer(TableChoice owner) : ControlAutomationPeer(owner), IExpandCollapseProvider, IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;
        public ExpandCollapseState ExpandCollapseState => owner.IsDropDownOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public bool ShowsMenu => true;
        public void Expand() => owner.IsDropDownOpen = true;
        public void Collapse() => owner.IsDropDownOpen = false;
        public bool IsReadOnly => !owner.IsEffectivelyEnabled;
        public string? Value => owner.Text;
        public void SetValue(string? value)
        {
            var index = owner.items.FindIndex(item => item.ToString() == value);
            if (index >= 0) owner.Choose(index);
        }
    }
}

/// <summary>
/// 行内选择框的选择浮层：一个 <see cref="Flyout"/> 内放一个列表，整表共用、锚在当前打开的那一格上。
/// 浮层开着时行对象就地更新（成员表每秒刷新）不会关闭它；可选项变化时列表随之更新。
/// </summary>
internal sealed class TableChoicePopup
{
    private readonly Flyout flyout;
    private readonly ListBox list;
    private TableChoice? anchor;

    public TableChoicePopup()
    {
        list = new ListBox { MaxHeight = 320 };
        list.Classes.Add("tablechoicelist");
        list.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        flyout = new Flyout { Content = list, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.FlyoutPresenterClasses.Add("tablepopup");
        flyout.FlyoutPresenterClasses.Add("tablechoice");
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(FocusSelected, DispatcherPriority.Loaded);
        flyout.Closed += (_, _) =>
        {
            var closed = anchor;
            anchor = null;
            if (closed is null) return;
            closed.SetOpen(false);
            if (closed.IsAttachedToVisualTree()) closed.Focus(NavigationMethod.Directional);
        };
    }

    internal TableChoice? Anchor => anchor;
    internal ListBox List => list;

    public void Show(TableChoice target)
    {
        if (anchor is not null && anchor != target) flyout.Hide();
        anchor = target;
        Refresh(target);
        list.MinWidth = target.Bounds.Width;
        target.SetOpen(true);
        flyout.ShowAt(target);
    }

    public void Hide(TableChoice target)
    {
        if (anchor == target) flyout.Hide();
    }

    /// <summary>按锚点当前的可选项与选择重填列表（打开时与可选项变化时）。</summary>
    public void Refresh(TableChoice target)
    {
        if (anchor != target) return;
        var texts = target.Items.Select(item => item.ToString() ?? string.Empty).ToList();
        if (list.ItemsSource is not List<string> current || !current.SequenceEqual(texts)) list.ItemsSource = texts;
        list.SelectedIndex = target.SelectedIndex;
    }

    private void FocusSelected()
    {
        if (anchor is null) return;
        var index = Math.Max(0, list.SelectedIndex);
        list.ScrollIntoView(index);
        (list.ContainerFromIndex(index) as Control ?? list).Focus(NavigationMethod.Directional);
    }

    private void Commit(int index)
    {
        var target = anchor;
        flyout.Hide();
        target?.Choose(index);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter or Key.Space:
                Commit(list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Escape:
                flyout.Hide();
                e.Handled = true;
                break;
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item) return;
        Commit(list.IndexFromContainer(item));
    }
}
