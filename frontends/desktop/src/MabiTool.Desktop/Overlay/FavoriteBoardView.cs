using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MabiTool.Desktop.Pages.Combat;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 收藏浮窗的内容，自绘：每项一行（竖排）或一格（横排，从左到右、放不下换行），画法与浮窗效果条目（<see cref="EffectLine"/>）相同——
/// 极性色标、“自身／目标”标记、名称、层数与右对齐的剩余时间，背景按剩余比例填充；没有出现的项整项变暗，觉醒触发后按设定颜色闪烁。
/// 放不下时最后一格改为“+N”。元件解锁时按住一项拖过 6 px 即拖动排序，其余项让位，松手交给 <see cref="Reorder"/> 保存；
/// 按下即处理指针事件，所以不会同时拖动整个元件（空白处与外框内边距仍拖动元件）。倒计时与闪烁随 200 ms 的效果节拍重绘。
/// <see cref="Prominent"/> 为真时（DPS 元件里的觉醒显示）行高与字号加大。
/// </summary>
public sealed class FavoriteBoardView : Control
{
    public const double ItemHeight = EffectLine.CompactHeight, ProminentHeight = EffectLine.RowHeight, MinimumColumnWidth = 132, Threshold = 6;
    private const double TagWidth = 28, TimeWidth = 40, FlashPeriodMs = 500;

    public static readonly StyledProperty<IReadOnlyList<FavoriteItem>?> ItemsProperty =
        AvaloniaProperty.Register<FavoriteBoardView, IReadOnlyList<FavoriteItem>?>(nameof(Items));
    public static readonly StyledProperty<Action<string, int>?> ReorderProperty =
        AvaloniaProperty.Register<FavoriteBoardView, Action<string, int>?>(nameof(Reorder));
    public static readonly StyledProperty<bool> HorizontalProperty = AvaloniaProperty.Register<FavoriteBoardView, bool>(nameof(Horizontal));
    public static readonly StyledProperty<bool> ProminentProperty = AvaloniaProperty.Register<FavoriteBoardView, bool>(nameof(Prominent));
    public static readonly StyledProperty<AwakeningSettings?> AlertsProperty = AvaloniaProperty.Register<FavoriteBoardView, AwakeningSettings?>(nameof(Alerts));
    public static readonly StyledProperty<IBrush> PositiveProperty = AvaloniaProperty.Register<FavoriteBoardView, IBrush>(nameof(Positive), new ImmutableSolidColorBrush(EffectLine.OverlayPositiveColor));
    public static readonly StyledProperty<IBrush> NegativeProperty = AvaloniaProperty.Register<FavoriteBoardView, IBrush>(nameof(Negative), new ImmutableSolidColorBrush(EffectLine.OverlayNegativeColor));
    public static readonly StyledProperty<IBrush> OtherProperty = AvaloniaProperty.Register<FavoriteBoardView, IBrush>(nameof(Other), new ImmutableSolidColorBrush(EffectLine.OverlayOtherColor));
    public static readonly StyledProperty<IBrush> WarningProperty = AvaloniaProperty.Register<FavoriteBoardView, IBrush>(nameof(Warning), new ImmutableSolidColorBrush(EffectLine.OverlayWarningColor));
    public static readonly StyledProperty<IBrush> TextProperty = AvaloniaProperty.Register<FavoriteBoardView, IBrush>(nameof(Text), Brushes.White);
    public static readonly StyledProperty<IBrush> MutedProperty = AvaloniaProperty.Register<FavoriteBoardView, IBrush>(nameof(Muted), new ImmutableSolidColorBrush(Color.Parse("#E5E9F2")));
    public static readonly StyledProperty<IBrush> TrackProperty = AvaloniaProperty.Register<FavoriteBoardView, IBrush>(nameof(Track), new ImmutableSolidColorBrush(EffectLine.OverlayTrackColor));
    public static readonly StyledProperty<IBrush> OverflowProperty = AvaloniaProperty.Register<FavoriteBoardView, IBrush>(nameof(Overflow), new ImmutableSolidColorBrush(EffectLine.OverlayTrackColor));

    private static readonly IBrush Shadow = new ImmutableSolidColorBrush(Color.Parse("#E6000000"));
    private int shown, hidden, columns = 1;
    private double columnWidth;
    private (IPointer Pointer, int Index, Point Start)? press;
    private (int From, int Target, Point At)? drag;

    static FavoriteBoardView()
    {
        AffectsMeasure<FavoriteBoardView>(ItemsProperty, HorizontalProperty, ProminentProperty);
        AffectsRender<FavoriteBoardView>(AlertsProperty, PositiveProperty, NegativeProperty, OtherProperty, WarningProperty, TextProperty, MutedProperty,
            TrackProperty, OverflowProperty);
    }

    public IReadOnlyList<FavoriteItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public bool Horizontal { get => GetValue(HorizontalProperty); set => SetValue(HorizontalProperty, value); }
    /// <summary>加大行高与字号，用于 DPS 元件里醒目的觉醒显示。</summary>
    public bool Prominent { get => GetValue(ProminentProperty); set => SetValue(ProminentProperty, value); }
    private double RowSize => Prominent ? ProminentHeight : ItemHeight;
    public AwakeningSettings? Alerts { get => GetValue(AlertsProperty); set => SetValue(AlertsProperty, value); }
    public IBrush Positive { get => GetValue(PositiveProperty); set => SetValue(PositiveProperty, value); }
    public IBrush Negative { get => GetValue(NegativeProperty); set => SetValue(NegativeProperty, value); }
    public IBrush Other { get => GetValue(OtherProperty); set => SetValue(OtherProperty, value); }
    public IBrush Warning { get => GetValue(WarningProperty); set => SetValue(WarningProperty, value); }
    public IBrush Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IBrush Muted { get => GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    /// <summary>条目轨道（浮窗配色“效果条目”组的条目轨道）。</summary>
    public IBrush Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    /// <summary>“+N”的底色（浮窗配色“效果条目”组的溢出底）。</summary>
    public IBrush Overflow { get => GetValue(OverflowProperty); set => SetValue(OverflowProperty, value); }

    /// <summary>拖动排序松手：项的键与新位置（按显示顺序）。</summary>
    public Action<string, int>? Reorder { get => GetValue(ReorderProperty); set => SetValue(ReorderProperty, value); }

    /// <summary>测试与渲染共用的时钟。</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>未显示的项数；全部放得下时为 0。</summary>
    public int HiddenCount => hidden;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 数据换了：进行中的拖动以新列表为准重新开始比较困难，直接取消，顺序不变。
        if (change.Property == ItemsProperty && drag is not null) Cancel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectTicker.Tick += OnTick;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectTicker.Tick -= OnTick;
        Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (Items is { Count: > 0 }) InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        var count = Items?.Count ?? 0;
        columns = Horizontal ? Math.Max(1, (int)Math.Floor(width / MinimumColumnWidth)) : 1;
        columnWidth = width / columns;
        var rows = double.IsInfinity(availableSize.Height) ? int.MaxValue : Math.Max(1, (int)Math.Floor(availableSize.Height / RowSize));
        var capacity = rows == int.MaxValue ? count : rows * columns;
        if (count <= capacity) { shown = count; hidden = 0; }
        else { shown = Math.Max(0, capacity - 1); hidden = count - shown; }
        var used = shown + (hidden > 0 ? 1 : 0);
        return new(width, Math.Ceiling(used / (double)columns) * RowSize);
    }

    private Rect SlotRect(int index) =>
        new(index % columns * columnWidth, index / columns * RowSize, columnWidth, RowSize);

    private int SlotAt(Point point)
    {
        var column = Math.Clamp((int)Math.Floor(point.X / Math.Max(1, columnWidth)), 0, columns - 1);
        var row = Math.Max(0, (int)Math.Floor(point.Y / RowSize));
        return Math.Clamp(row * columns + column, 0, Math.Max(0, shown - 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // 没有排序回调（觉醒监控浮窗、DPS 元件里的觉醒显示）时不处理按下，按在项上照常拖动整个元件。
        if (Reorder is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Items is not { Count: > 0 }) return;
        var point = e.GetPosition(this);
        var index = SlotAt(point);
        if (index >= shown || !SlotRect(index).Contains(point)) return;
        press = (e.Pointer, index, point);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (press is not { } pressed || e.Pointer != pressed.Pointer) return;
        var point = e.GetPosition(this);
        if (drag is null && Math.Abs(point.X - pressed.Start.X) < Threshold && Math.Abs(point.Y - pressed.Start.Y) < Threshold) return;
        drag = (pressed.Index, SlotAt(point), point);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (press is not { } pressed || e.Pointer != pressed.Pointer) return;
        var done = drag;
        press = null; drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
        if (done is { } moved && moved.Target != moved.From && Items is { } items && moved.From < items.Count) Reorder?.Invoke(items[moved.From].Key, moved.Target);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Cancel();
    }

    private void Cancel()
    {
        if (press is null && drag is null) return;
        press = null; drag = null;
        InvalidateVisual();
    }

    /// <summary>此刻的绘制顺序：拖动中被拽起的项移到目标位置，其余项让位。</summary>
    private List<int> Order()
    {
        var order = Enumerable.Range(0, shown).ToList();
        if (drag is { } moving && moving.From < shown)
        {
            order.RemoveAt(moving.From);
            order.Insert(Math.Clamp(moving.Target, 0, order.Count), moving.From);
        }
        return order;
    }

    public override void Render(DrawingContext context)
    {
        if (Items is not { } items || items.Count == 0) return;
        var now = Clock();
        var family = TextElement.GetFontFamily(this);
        var order = Order();
        for (var slot = 0; slot < order.Count; slot++)
        {
            var index = order[slot];
            if (drag is { } moving && moving.From == index) continue;
            Paint(context, items[index], SlotRect(slot), now, family, 1);
        }
        if (hidden > 0)
        {
            var rect = SlotRect(shown).Deflate(new Thickness(0, 1));
            context.DrawRectangle(Overflow, null, rect, 4, 4);
            Draw(context, $"+{hidden.ToString(CultureInfo.InvariantCulture)}", rect.X + 10, rect.Y + rect.Height / 2, rect.Width - 20, Text, 12, family,
                FontWeight.Bold, TextAlignment.Left, Shadow);
        }
        // 被拽起的项跟随指针，画在最上层。
        if (drag is { } lifted && lifted.From < items.Count)
        {
            var origin = SlotRect(lifted.From);
            var start = press?.Start ?? lifted.At;
            var rect = origin.Translate(new Vector(lifted.At.X - start.X, lifted.At.Y - start.Y));
            using (context.PushOpacity(0.92)) Paint(context, items[lifted.From], rect, now, family, 1);
        }
    }

    private void Paint(DrawingContext context, FavoriteItem item, Rect slot, long now, FontFamily family, double opacity)
    {
        var bounds = slot.Deflate(new Thickness(0, 1, Horizontal ? 3 : 0, 1));
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var look = item.Describe(now);
        using var dim = context.PushOpacity(look.Dim ? 0.45 * opacity : opacity);
        const double radius = 4;
        var fill = look.Warning ? Warning : look.Polarity switch
            { EffectPolarity.Positive => Positive, EffectPolarity.Negative => Negative, _ => Other };
        context.DrawRectangle(Track, null, bounds, radius, radius);
        if (!look.Dim)
        {
            if (look.Infinite) context.DrawRectangle(new SolidColorBrush(fill is ISolidColorBrush solid ? solid.Color : Colors.Gray, 0.45), null, bounds, radius, radius);
            else if (look.Progress is { } ratio and > 0) context.DrawRectangle(fill, null, bounds.WithWidth(bounds.Width * ratio), radius, radius);
        }
        // 觉醒触发后的闪烁：按设定的颜色与时长，每 0.5 秒亮暗一次。
        if (item.Kind == FavoriteItemKind.Badge && look.TriggerTick != 0 && Alerts?.Badge is { Flash: true } alert)
        {
            var elapsed = (now - look.TriggerTick) * 1000d / Stopwatch.Frequency;
            if (elapsed >= 0 && elapsed < alert.FlashSeconds * 1000 && (int)(elapsed / (FlashPeriodMs / 2)) % 2 == 0)
                context.DrawRectangle(new SolidColorBrush(Color.Parse(alert.Color), 0.85), null, bounds, radius, radius);
        }

        var middle = bounds.Y + bounds.Height / 2;
        using (context.PushTransform(Matrix.CreateTranslation(-5, -5.5) *
                   (look.Polarity == EffectPolarity.Negative ? Matrix.CreateRotation(Math.PI) : Matrix.Identity) *
                   Matrix.CreateTranslation(bounds.X + 10, middle)))
            context.DrawGeometry(PentagonFill(look.Polarity), null, Pentagon);

        var size = Prominent ? 13d : 11d;
        var left = bounds.X + 20;
        if (look.Tag.Length > 0)
        {
            Draw(context, look.Tag, left, middle, TagWidth - 2, Muted, size - 1, family, FontWeight.Normal, TextAlignment.Left, Shadow);
            left += TagWidth;
        }
        var right = bounds.Right - 5;
        var timeWidth = look.Remaining.Length > 0 ? TimeWidth : 0;
        if (timeWidth > 0) Draw(context, look.Remaining, right - timeWidth, middle, timeWidth, Text, size, family, FontWeight.Bold, TextAlignment.Right, Shadow);
        var cursor = right - timeWidth;
        if (look.Note.Length > 0)
        {
            var noteWidth = look.Note.Length * (size + 1) + 4;
            Draw(context, look.Note, cursor - noteWidth, middle, noteWidth, Muted, size - 1, family, FontWeight.Normal, TextAlignment.Right, Shadow);
            cursor -= noteWidth + 2;
        }
        if (look.Stacks.Length > 0)
        {
            Draw(context, look.Stacks, cursor - 30, middle, 28, Muted, size, family, FontWeight.Normal, TextAlignment.Right, Shadow);
            cursor -= 32;
        }
        Draw(context, look.Name, left, middle, Math.Max(0, cursor - 4 - left), Text, size, family, FontWeight.SemiBold, TextAlignment.Left, Shadow);
    }

    private static readonly Geometry Pentagon = Geometry.Parse("M5,0 L10,3 L9,11 L1,11 L0,3 Z");
    private static readonly IBrush PositiveFill = new ImmutableSolidColorBrush(Color.Parse("#2F6FD6"));
    private static readonly IBrush NegativeFill = new ImmutableSolidColorBrush(Color.Parse("#D2393B"));
    private static readonly IBrush OtherFill = new ImmutableSolidColorBrush(Color.Parse("#7D8597"));
    private static IBrush PentagonFill(EffectPolarity polarity) =>
        polarity switch { EffectPolarity.Positive => PositiveFill, EffectPolarity.Negative => NegativeFill, _ => OtherFill };

    private static void Draw(DrawingContext context, string text, double x, double middle, double width, IBrush brush, double size,
        FontFamily family, FontWeight weight, TextAlignment alignment, IBrush? shadow)
    {
        if (width <= 0 || text.Length == 0) return;
        if (shadow is not null) Draw(context, text, x + 1, middle + 1, width, shadow, size, family, weight, alignment, null);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = alignment
        };
        context.DrawText(formatted, new Point(x, middle - formatted.Height / 2));
    }
}
