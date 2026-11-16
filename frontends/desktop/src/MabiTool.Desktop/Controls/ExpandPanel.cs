using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 全应用共用的展开／收起容器。<see cref="IsExpanded"/> 变化时高度在 <see cref="UiMotion.Expand"/>（220 ms，减速曲线）内
/// 从当前高度过渡到目标高度，内容的不透明度同步从 0 到 1（收起时相反）。过渡期间内容始终按展开后的完整尺寸排版、
/// 只由容器裁掉露不出来的部分，所以内容自己不重排，周围的元素只随容器高度平滑移动，不跳动。
/// 收起结束后内容不可见（不占位、不接收焦点与指针）；动效减弱（设置页或系统关闭动画）时直接到终态。
/// 同一时刻再次切换时从当前高度接着过渡。
/// </summary>
public sealed class ExpandPanel : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<ExpandPanel, bool>(nameof(IsExpanded), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private static readonly CubicEaseOut Ease = new();

    // 0 为完全收起，1 为完全展开；过渡期间是当前帧的进度。
    private double progress;
    private double from;
    private double target;
    private TimeSpan? started;
    private bool animating;
    private bool initialized;
    private bool fill;
    private double full;

    static ExpandPanel()
    {
        ClipToBoundsProperty.OverrideDefaultValue<ExpandPanel>(true);
    }

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>
    /// 为真且容器得到有限的高度时，内容按这个高度排版并占满（固定高度的卡片：内容自己在这个高度里滚动或截断）；
    /// 缺省为假，内容按自己的高度排版。
    /// </summary>
    public bool Fill
    {
        get => fill;
        set
        {
            if (fill == value) return;
            fill = value;
            InvalidateMeasure();
        }
    }

    /// <summary>当前的展开进度（0–1）；测试据此确认过渡是否走完。</summary>
    public double Progress => progress;

    /// <summary>正在过渡。</summary>
    public bool IsTransitioning => animating;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty) Toggle(IsExpanded);
        else if (change.Property == ChildProperty) Apply();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!initialized) Jump(IsExpanded);
    }

    // 数据上下文更换（列表回收容器、表格刷新换行对象）带来的展开状态变化直接到位，只有同一项上的切换才过渡。
    private bool contextChanging;

    protected override void OnDataContextBeginUpdate()
    {
        base.OnDataContextBeginUpdate();
        contextChanging = true;
    }

    protected override void OnDataContextEndUpdate()
    {
        base.OnDataContextEndUpdate();
        contextChanging = false;
    }

    /// <summary>不经过渡直接到当前 <see cref="IsExpanded"/> 的终态（读取保存的状态时用）。</summary>
    public void Snap() => Jump(IsExpanded);

    private void Toggle(bool expanded)
    {
        var top = TopLevel.GetTopLevel(this);
        if (!initialized || contextChanging || top is null || !UiMotion.Active)
        {
            Jump(expanded);
            return;
        }
        from = progress;
        target = expanded ? 1 : 0;
        started = null;
        IsVisible = true;
        if (Child is not null) Child.IsVisible = true;
        if (animating) return;
        animating = true;
        top.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan time)
    {
        if (!animating) return;
        started ??= time;
        var elapsed = (time - started.Value).TotalMilliseconds / UiMotion.Expand.TotalMilliseconds;
        if (elapsed >= 1 || !UiMotion.Active || TopLevel.GetTopLevel(this) is not { } top)
        {
            Jump(target >= 0.5);
            return;
        }
        progress = from + (target - from) * Ease.Ease(elapsed);
        Apply();
        top.RequestAnimationFrame(Frame);
    }

    private void Jump(bool expanded)
    {
        initialized = true;
        animating = false;
        progress = expanded ? 1 : 0;
        Apply();
    }

    private void Apply()
    {
        // 完全收起时容器自己也不可见，所在 StackPanel 的间距不再为它保留。
        IsVisible = progress > 0 || animating;
        if (Child is { } child)
        {
            child.IsVisible = progress > 0 || animating;
            if (progress >= 1)
            {
                child.ClearValue(OpacityProperty);
                child.ClearValue(IsHitTestVisibleProperty);
            }
            else
            {
                child.Opacity = progress;
                child.IsHitTestVisible = false;
            }
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { IsVisible: true } child) return default;
        var fixedHeight = fill && !double.IsInfinity(availableSize.Height);
        child.Measure(new Size(availableSize.Width, fixedHeight ? availableSize.Height : double.PositiveInfinity));
        full = fixedHeight ? availableSize.Height : child.DesiredSize.Height;
        return new Size(child.DesiredSize.Width, full * progress);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { IsVisible: true } child) child.Arrange(new Rect(0, 0, finalSize.Width, full));
        return finalSize;
    }
}
