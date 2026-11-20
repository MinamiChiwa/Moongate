using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 当前攻击目标的血量槽：剩余部分按 <see cref="TargetHealthView.Fill"/> 画在暗底上，不超过 30% 时换 <see cref="LowFill"/>，
/// 置灰的状态（未读取、最大值未知、血量由首领体现）只画暗底。同一目标的比例变化在动效开启时 <see cref="UiMotion.Base"/> 内过渡，
/// 只在过渡期间按帧重绘；目标换了、动效减弱或还没挂上窗口时直接画新比例，不从旧目标的比例过渡。
/// </summary>
public sealed class HealthBar : Control
{
    public static readonly StyledProperty<TargetHealthView?> HealthProperty =
        AvaloniaProperty.Register<HealthBar, TargetHealthView?>(nameof(Health));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<HealthBar, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> LowFillProperty =
        AvaloniaProperty.Register<HealthBar, IBrush?>(nameof(LowFill));
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<HealthBar, IBrush?>(nameof(Track));

    private static readonly CubicEaseOut Ease = new();

    static HealthBar() => AffectsRender<HealthBar>(FillProperty, LowFillProperty, TrackProperty);

    // 正在显示的比例；过渡期间是当前帧的值。
    private double shown;
    private double from;
    private double target;
    private TimeSpan? started;
    private bool animating;

    public TargetHealthView? Health { get => GetValue(HealthProperty); set => SetValue(HealthProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? LowFill { get => GetValue(LowFillProperty); set => SetValue(LowFillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    /// <summary>此刻画出的剩余比例（0–1）；测试据此核对填充。</summary>
    public double Shown => shown;

    /// <summary>正在过渡。</summary>
    public bool IsTransitioning => animating;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != HealthProperty) return;
        // 收起中的区块保留最后的画面；没有新数据时不改。
        if (change.GetNewValue<TargetHealthView?>() is not { } next) return;
        var previous = change.GetOldValue<TargetHealthView?>();
        var value = next.Empty ? 0 : Math.Clamp(next.Fill, 0, 1);
        var top = TopLevel.GetTopLevel(this);
        // 从置灰状态（还没有比例）到读到血量同样直接到位，不从 0 长出来。
        if (previous is null || previous.EntityId != next.EntityId || previous.Empty || next.Empty || top is null || !UiMotion.Active)
        {
            animating = false;
            shown = value;
            InvalidateVisual();
            return;
        }
        if (Math.Abs(value - shown) < 1e-6 && !animating)
        {
            InvalidateVisual();
            return;
        }
        from = shown;
        target = value;
        started = null;
        if (animating) return;
        animating = true;
        top.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan time)
    {
        if (!animating) return;
        started ??= time;
        var progress = (time - started.Value).TotalMilliseconds / UiMotion.Base.TotalMilliseconds;
        if (progress >= 1 || !UiMotion.Active || TopLevel.GetTopLevel(this) is not { } top)
        {
            animating = false;
            shown = target;
            InvalidateVisual();
            return;
        }
        shown = from + (target - from) * Ease.Ease(progress);
        InvalidateVisual();
        top.RequestAnimationFrame(Frame);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!animating) return;
        animating = false;
        shown = target;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 8);

    public override void Render(DrawingContext context)
    {
        var radius = Bounds.Height / 2;
        context.DrawRectangle(Track, null, new Rect(Bounds.Size), radius, radius);
        if (Health is not { Empty: false } health || shown <= 0) return;
        context.DrawRectangle(health.Low ? LowFill : Fill, null, new Rect(0, 0, Bounds.Width * shown, Bounds.Height), radius, radius);
    }
}
