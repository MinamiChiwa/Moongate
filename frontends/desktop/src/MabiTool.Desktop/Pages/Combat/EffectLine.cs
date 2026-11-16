using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>
/// 效果列表的一行，自绘：极性色标、收藏星标、名称、层数与右对齐的剩余时间；行背景按“剩余 ÷ 总时长”填充，作为倒计时进度。
/// 倒计时节拍（<see cref="EffectTicker"/>）上只重绘这一行，不重新测量、不触发列表重排。点按星标收藏或取消。
/// </summary>
public sealed class EffectLine : Control
{
    public const double RowHeight = 26, CompactHeight = 20;
    /// <summary>少于这么久时文字与进度换成警示色。</summary>
    public const double WarningMs = 5000;
    private const double StarLeft = 20, StarWidth = 16, NameLeft = 38, TimeWidth = 46;

    public static readonly StyledProperty<EffectRow?> RowProperty = AvaloniaProperty.Register<EffectLine, EffectRow?>(nameof(Row));
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<EffectLine, bool>(nameof(Compact));
    /// <summary>浮窗配色：叠在游戏画面与深色半透明外框上，用不透明的深色轨道、饱和的极性色填充与带阴影的白字。</summary>
    public static readonly StyledProperty<bool> OverlayProperty = AvaloniaProperty.Register<EffectLine, bool>(nameof(Overlay));

    private static readonly IBrush PositiveTint = new SolidColorBrush(Color.Parse("#402F6FD6"));
    private static readonly IBrush NegativeTint = new SolidColorBrush(Color.Parse("#40D2393B"));
    private static readonly IBrush OtherTint = new SolidColorBrush(Color.Parse("#357D8597"));
    private static readonly IBrush WarningTint = new SolidColorBrush(Color.Parse("#55E0A100"));
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#14808080"));
    // 浮窗配色（不随主题变化）：白字在填充色上对比 4.8–5.2，在轨道上不低于 11；填充与轨道亮度差明显。
    internal static readonly Color OverlayTrackColor = Color.Parse("#E6141826");
    internal static readonly Color OverlayPositiveColor = Color.Parse("#2563EB"), OverlayNegativeColor = Color.Parse("#DC2626"),
        OverlayOtherColor = Color.Parse("#6B7280"), OverlayWarningColor = Color.Parse("#B45309");
    private static readonly IBrush OverlayTrack = new SolidColorBrush(OverlayTrackColor);
    private static readonly IBrush OverlayText = Brushes.White;
    private static readonly IBrush OverlayMuted = new SolidColorBrush(Color.Parse("#E5E9F2"));
    private static readonly IBrush OverlayShadow = new SolidColorBrush(Color.Parse("#E6000000"));
    private static readonly IBrush StarBrush = new SolidColorBrush(Color.Parse("#D99A00"));
    private static readonly Geometry Pentagon = Geometry.Parse("M5,0 L10,3 L9,11 L1,11 L0,3 Z");

    static EffectLine() => AffectsRender<EffectLine>(RowProperty, CompactProperty, OverlayProperty);

    public EffectRow? Row { get => GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public bool Overlay { get => GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }

    /// <summary>这一行的填充色：浮窗用饱和的不透明极性色，主窗口用低饱和半透明色；少于 5 秒为警示色。</summary>
    internal static IBrush FillOf(EffectRow row, bool warning, bool overlay) => overlay
        ? new SolidColorBrush(warning ? OverlayWarningColor : row.Polarity switch
            { EffectPolarity.Positive => OverlayPositiveColor, EffectPolarity.Negative => OverlayNegativeColor, _ => OverlayOtherColor })
        : warning ? WarningTint : row.Polarity switch
            { EffectPolarity.Positive => PositiveTint, EffectPolarity.Negative => NegativeTint, _ => OtherTint };

    /// <summary>测试与渲染共用的时钟；默认取本机 <see cref="Stopwatch"/>。</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowProperty)
            ToolTip.SetTip(this, Row is { } row ? string.Join("\n", new[] { row.Hint, row.Meta, row.Coverage }.Where(text => text.Length > 0)) : null);
        if (change.Property == CompactProperty) InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width, Compact ? CompactHeight : RowHeight);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectTicker.Tick += OnTick;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectTicker.Tick -= OnTick;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (Row is { Infinite: false, RemainingMs: not null }) InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var x = e.GetPosition(this).X;
        if (Row?.EffectId is { Length: > 0 } id && x >= StarLeft - 2 && x <= StarLeft + StarWidth + 2)
        {
            CombatEffectFavorites.Toggle(id);
            e.Handled = true;
        }
    }

    /// <summary>这一行此刻显示的内容：名称、层数、剩余文字、进度（0–1，常驻或未知为空）与是否警示。</summary>
    public static (string Name, string Stacks, string Remaining, double? Progress, bool Warning) Describe(EffectRow row, long now)
    {
        var remaining = CombatEffectOrder.Remaining(row, now);
        var text = row.Infinite ? UiText.Pick("持续", "持續")
            : remaining is { } ms ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, Math.Ceiling(ms / 1000)):0}s")
            : UiText.Pick("未知", "未知");
        return (row.Name, row.Stacks is { } stacks and > 1 ? $"×{stacks}" : string.Empty, text,
            CombatEffectOrder.Progress(row, now), remaining is < WarningMs);
    }

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row) return;
        var bounds = new Rect(Bounds.Size).Deflate(new Thickness(0, 1));
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var (name, stacks, remaining, progress, warning) = Describe(row, Clock());
        var tint = FillOf(row, warning, Overlay);
        const double radius = 4;
        // 底色：常驻为淡色整行，有进度时为轨道加按比例填充，未知只有轨道。
        context.DrawRectangle(Overlay ? OverlayTrack : Track, null, bounds, radius, radius);
        if (row.Infinite) context.DrawRectangle(new SolidColorBrush(((ISolidColorBrush)tint).Color, 0.45), null, bounds, radius, radius);
        else if (progress is { } ratio and > 0) context.DrawRectangle(tint, null, bounds.WithWidth(bounds.Width * ratio), radius, radius);

        var middle = bounds.Y + bounds.Height / 2;
        using (context.PushTransform(Matrix.CreateTranslation(-5, -5.5) *
                   (row.Polarity == EffectPolarity.Negative ? Matrix.CreateRotation(Math.PI) : Matrix.Identity) *
                   Matrix.CreateTranslation(10, middle)))
            context.DrawGeometry(row.Fill, null, Pentagon);

        var ink = Overlay ? OverlayText : Brush("InkBrush", Brushes.Black);
        var muted = Overlay ? OverlayMuted : Brush("MutedBrush", Brushes.Gray);
        var shadow = Overlay ? OverlayShadow : null;
        var size = Compact ? 11 : 12;
        var family = TextElement.GetFontFamily(this);
        if (row.Favorite || IsPointerOver)
            Draw(context, row.Favorite ? "★" : "☆", StarLeft, middle, StarWidth, row.Favorite ? StarBrush : muted, size + 1, family, FontWeight.Normal, shadow: shadow);
        var right = bounds.Right - 6;
        // 浮窗的警示由填充色表达，文字保持白色以保证对比。
        var timeBrush = warning && !Overlay ? Brush("UnknownBrush", Brushes.DarkOrange) : ink;
        // 剩余时间占固定宽度、右对齐，数字变化不挤动名称；层数紧挨其左。
        Draw(context, remaining, right - TimeWidth, middle, TimeWidth, timeBrush, size, family, FontWeight.Bold, TextAlignment.Right, shadow);
        if (stacks.Length > 0) Draw(context, stacks, right - TimeWidth - 34, middle, 30, muted, size, family, FontWeight.Normal, TextAlignment.Right, shadow);
        var nameWidth = Math.Max(0, right - TimeWidth - (stacks.Length > 0 ? 40 : 6) - NameLeft);
        Draw(context, name, NameLeft, middle, nameWidth, ink, size, family, Overlay ? FontWeight.SemiBold : FontWeight.Normal, shadow: shadow);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static void Draw(DrawingContext context, string text, double x, double middle, double width, IBrush brush, double size,
        FontFamily family, FontWeight weight, TextAlignment alignment = TextAlignment.Left, IBrush? shadow = null)
    {
        if (width <= 0 || text.Length == 0) return;
        // 浮窗文字带 1 px 深色阴影，在亮暗游戏画面与填充色上都清楚。
        if (shadow is not null) Draw(context, text, x + 1, middle + 1, width, shadow, size, family, weight, alignment);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = alignment
        };
        context.DrawText(formatted, new Point(x, middle - formatted.Height / 2));
    }
}
