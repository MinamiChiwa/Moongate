using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Controls.Tables;

/// <summary>
/// 表格行内的短文字输入：平时只画当前文字（一个 <see cref="Border"/> 加一个自绘层，没有模板），
/// 获得焦点（点按、Tab、F2、回车或直接打字）时才把所在 <see cref="DataTable"/> 整表共用的一个输入框
/// （<see cref="TableInputEditor"/>，不在表格里时自带一个）放进这一格编辑。
/// <para>
/// 用来代替行内的 <see cref="TextBox"/>：TextBox 每个实例在首次排版时实例化整套模板（外框、滚动区、文字呈现器、占位文字、
/// 清除按钮等），成员表每行两个，是合奏页第一帧里仅次于下拉框的一项。
/// </para>
/// 提交：回车提交并继续编辑，离开（焦点移走）时提交并结束编辑；两种情况都先把输入写回 <see cref="Text"/>（默认双向绑定），
/// 再发出 <see cref="Committed"/>。Esc 放弃输入、回到原文字，焦点留在这一格。Tab／Shift+Tab 移到前后的可聚焦元素。
/// <see cref="IsReadOnly"/> 为真时可以聚焦与读屏，不进入编辑。外观由 <c>Border.tableinput</c> 样式给出。
/// </summary>
public sealed class TableInput : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TableInput, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<TableInput, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<TableInput, TextAlignment>(nameof(TextAlignment), TextAlignment.Left);

    public static readonly DirectProperty<TableInput, bool> IsEditingProperty =
        AvaloniaProperty.RegisterDirect<TableInput, bool>(nameof(IsEditing), o => o.IsEditing);

    /// <summary>回车或离开时提交（<see cref="Text"/> 已是输入的文字）。</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CommittedEvent =
        RoutedEvent.Register<TableInput, RoutedEventArgs>(nameof(Committed), RoutingStrategies.Bubble);

    private readonly InputFace face;
    private TableInputEditor? ownEditor;
    private TableInputEditor? active;
    private bool editing;
    private bool quiet;
    private int? pressCaret;

    public TableInput()
    {
        Focusable = true;
        Classes.Add("tableinput");
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Child = face = new InputFace(this);
        // 按下时先记下点到的字符位置：焦点在按下的隧道阶段就移到这一格，进入编辑时光标直接落在那里。
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            pressCaret = !editing && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? face.CaretAt(e.GetPosition(face)) : null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override Type StyleKeyOverride => typeof(Border);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    /// <summary>整表共用的输入框此时是否在这一格。</summary>
    public bool IsEditing => editing;

    public event EventHandler<RoutedEventArgs>? Committed
    {
        add => AddHandler(CommittedEvent, value);
        remove => RemoveHandler(CommittedEvent, value);
    }

    internal TableInputEditor Editor => this.FindAncestorOfType<DataTable>()?.InputEditor ?? (ownEditor ??= new TableInputEditor());

    /// <summary>进入编辑并全选（F2、回车、Tab 进入时）；只读、停用或不在界面上时不进入。</summary>
    public void BeginEdit() => Begin(null);

    private bool Begin(int? caret)
    {
        if (editing) return true;
        if (IsReadOnly || !IsEffectivelyEnabled || !this.IsAttachedToVisualTree()) return false;
        Editor.Begin(this, caret);
        return true;
    }

    /// <summary>输入框放进或移出这一格。编辑期间这一格本身不参与 Tab 顺序，Shift+Tab 从输入框退回前一个元素而不是这一格。</summary>
    internal void Host(TableInputEditor? editor)
    {
        var value = editor is not null;
        if (value == editing) return;
        active = editor;
        Child = editor?.Box ?? (Control)face;
        Focusable = !value;
        SetAndRaise(IsEditingProperty, ref editing, value);
        PseudoClasses.Set(":editing", value);
    }

    /// <summary>写回输入并发出提交。</summary>
    internal void Commit(string text)
    {
        Text = text;
        RaiseEvent(new RoutedEventArgs(CommittedEvent, this));
    }

    /// <summary>编辑结束后把焦点留在这一格（Esc），不再次进入编辑。</summary>
    internal void FocusQuietly()
    {
        quiet = true;
        try { Focus(NavigationMethod.Directional); }
        finally { quiet = false; }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == TextAlignmentProperty)
        {
            face.InvalidateVisual();
            active?.Sync(this);
        }
        else if (change.Property == IsReadOnlyProperty && IsReadOnly || change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled)
            active?.End(this, commit: false);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        // 点按时光标落在点到的位置，其余方式进入时全选，与 TextBox 被 Tab 聚焦时相同。
        if (!quiet && e.Source == this) Begin(e.NavigationMethod == NavigationMethod.Pointer ? pressCaret ?? Text?.Length ?? 0 : null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (pressCaret is not { } caret) return;
        pressCaret = null;
        // 按下的那一下由这一格接住（与 TextBox 相同，只读时也是：不选中行、不从这里拖起行）；已聚焦在格上（Esc 之后）再点时从这里进入编辑。
        if (Begin(caret)) active?.PlaceCaret(this, caret);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || editing || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is Key.Enter or Key.F2 && Begin(null)) e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        // 聚焦在格上（Esc 之后）直接打字：进入编辑，以打的字代替原文字。
        if (e.Handled || editing || string.IsNullOrEmpty(e.Text) || !Begin(null)) return;
        active?.Type(this, e.Text);
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        active?.End(this, commit: true);
        base.OnDetachedFromVisualTree(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new InputPeer(this);

    // 当前文字：一个自绘层，没有子控件（Border 自己的 Render 已画底色与边框）。
    private sealed class InputFace(TableInput owner) : Control
    {
        private TextLayout? layout;
        private (string Text, double Width, IBrush? Brush, FontFamily Family, double Size, TextAlignment Align) key;

        protected override Size MeasureOverride(Size availableSize)
        {
            var size = GetValue(TextElement.FontSizeProperty);
            using var probe = new TextLayout("Ag国", new Typeface(GetValue(TextElement.FontFamilyProperty)), size, null);
            var width = double.IsInfinity(availableSize.Width) ? 40 : availableSize.Width;
            return new Size(width, probe.Height);
        }

        private TextLayout Layout()
        {
            var brush = GetValue(TextElement.ForegroundProperty);
            var family = GetValue(TextElement.FontFamilyProperty);
            var size = GetValue(TextElement.FontSizeProperty);
            var width = Math.Max(1, Bounds.Width);
            var text = owner.Text ?? string.Empty;
            var align = owner.TextAlignment;
            if (layout is null || key != (text, width, brush, family, size, align))
            {
                layout?.Dispose();
                key = (text, width, brush, family, size, align);
                layout = new TextLayout(text, new Typeface(family), size, brush, align, textTrimming: TextTrimming.CharacterEllipsis, maxWidth: width);
            }
            return layout;
        }

        private double Top(TextLayout text) => Math.Max(0, (Bounds.Height - text.Height) / 2);

        public override void Render(DrawingContext context)
        {
            if (string.IsNullOrEmpty(owner.Text)) return;
            var text = Layout();
            text.Draw(context, new Point(0, Top(text)));
        }

        /// <summary>点在这一层上的位置对应的光标位置。</summary>
        public int CaretAt(Point point)
        {
            if (string.IsNullOrEmpty(owner.Text)) return 0;
            var text = Layout();
            var hit = text.HitTestPoint(new Point(point.X, point.Y - Top(text)));
            return Math.Clamp(hit.CharacterHit.FirstCharacterIndex + hit.CharacterHit.TrailingLength, 0, owner.Text.Length);
        }
    }

    /// <summary>读屏：编辑框，名称取 <c>AutomationProperties.Name</c>，值为当前文字；设置值等同输入后提交。</summary>
    private sealed class InputPeer(TableInput owner) : ControlAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
        public bool IsReadOnly => owner.IsReadOnly || !owner.IsEffectivelyEnabled;
        public string? Value => owner.Text;
        public void SetValue(string? value)
        {
            if (!IsReadOnly) owner.Commit(value ?? string.Empty);
        }
    }
}

/// <summary>
/// 行内输入（<see cref="TableInput"/>）整表共用的输入框：一个 <see cref="TextBox"/>，编辑时放进当前那一格，结束时移出。
/// 同一时间只在一格里；移到另一格前先按离开提交上一格。
/// </summary>
internal sealed class TableInputEditor
{
    private readonly TextBox box;
    private TableInput? owner;
    private bool cancelled;
    private bool writing;

    public TableInputEditor()
    {
        box = new TextBox { VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        box.Classes.Add("tableinputeditor");
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) =>
        {
            if (owner is { } target) End(target, commit: !cancelled);
            cancelled = false;
        };
    }

    internal TextBox Box => box;
    internal TableInput? Owner => owner;

    public void Begin(TableInput target, int? caret)
    {
        if (owner == target) return;
        if (owner is { } previous) End(previous, commit: true);
        owner = target;
        cancelled = false;
        Sync(target);
        AutomationProperties.SetName(box, AutomationProperties.GetName(target));
        box.TextAlignment = target.TextAlignment;
        target.Host(this);
        box.Focus(NavigationMethod.Directional);
        if (caret is { } index) PlaceCaret(target, index);
        else box.SelectAll();
    }

    /// <summary>格里的文字从外面变了（后端回写）：输入框随之改写，与原来每格一个 TextBox 时绑定改写输入相同。</summary>
    public void Sync(TableInput target)
    {
        if (owner != target || writing) return;
        box.Text = target.Text ?? string.Empty;
    }

    public void PlaceCaret(TableInput target, int caret)
    {
        if (owner != target) return;
        box.ClearSelection();
        box.CaretIndex = Math.Clamp(caret, 0, box.Text?.Length ?? 0);
    }

    public void Type(TableInput target, string text)
    {
        if (owner != target) return;
        box.Text = text;
        PlaceCaret(target, text.Length);
    }

    public void End(TableInput target, bool commit)
    {
        if (owner != target) return;
        owner = null;
        var text = box.Text ?? string.Empty;
        target.Host(null);
        if (commit) Write(target, text);
    }

    private void Write(TableInput target, string text)
    {
        writing = true;
        try { target.Commit(text); }
        finally { writing = false; }
        // 提交处理把文字改回（输入被拒后还原为当前值）时，仍在编辑的输入框随之改写。
        if (owner == target && box.Text != (target.Text ?? string.Empty)) box.Text = target.Text ?? string.Empty;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (owner is not { } target || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                // 回车提交并继续编辑（原来每格一个 TextBox 时相同）。
                Write(target, box.Text ?? string.Empty);
                box.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                // 放弃输入：回到原文字，焦点留在这一格。
                cancelled = true;
                End(target, commit: false);
                target.FocusQuietly();
                cancelled = false;
                e.Handled = true;
                break;
        }
    }
}
