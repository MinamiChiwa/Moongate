using Avalonia;
using Avalonia.Controls;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 可折叠的一排标签。折叠时只排一行：按顺序放下能放下的项，标为 <see cref="IsPinnedProperty"/> 的项（例如选中的目标）总是放下；
/// 最后一个子元素是“+N”展开项，放不下的个数写在它的 <see cref="OverflowCountProperty"/> 上，全部放得下时它不显示。
/// 展开时像 WrapPanel 一样换行排列全部项，最后一项此时用作“收起”。放不下的项排在面板外并被裁掉，不占位置也不接收点击。
/// </summary>
public sealed class OverflowRow : Panel
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<OverflowRow, bool>(nameof(IsExpanded));

    /// <summary>折叠时也必须放下的项（选中的目标）。</summary>
    public static readonly AttachedProperty<bool> IsPinnedProperty =
        AvaloniaProperty.RegisterAttached<OverflowRow, Control, bool>("IsPinned");

    /// <summary>折叠时没有放下的项数，写在最后一个子元素（展开项）上；展开时为 0。</summary>
    public static readonly AttachedProperty<int> OverflowCountProperty =
        AvaloniaProperty.RegisterAttached<OverflowRow, Control, int>("OverflowCount");

    static OverflowRow()
    {
        AffectsMeasure<OverflowRow>(IsExpandedProperty);
        IsPinnedProperty.Changed.AddClassHandler<Control>((control, _) => (control.Parent as OverflowRow)?.InvalidateMeasure());
        IsExpandedProperty.Changed.AddClassHandler<OverflowRow>((row, _) => row.StartHeightTransition());
    }

    // 展开与收起时高度按共用展开动效（UiMotion.Expand，减速曲线）从原高度过渡到新高度，内容按新排法排好、只被裁切。
    private static readonly Avalonia.Animation.Easings.CubicEaseOut Ease = new();
    private double? transitionFrom;
    private TimeSpan? transitionStart;
    private double transitionProgress = 1;

    private void StartHeightTransition()
    {
        if (!MabiTool.Desktop.Theme.UiMotion.Active || TopLevel.GetTopLevel(this) is not { } top || Bounds.Height <= 0) return;
        transitionFrom = Bounds.Height;
        transitionStart = null;
        transitionProgress = 0;
        top.RequestAnimationFrame(OnTransitionFrame);
    }

    private void OnTransitionFrame(TimeSpan time)
    {
        if (transitionFrom is null) return;
        transitionStart ??= time;
        var elapsed = (time - transitionStart.Value).TotalMilliseconds / MabiTool.Desktop.Theme.UiMotion.Expand.TotalMilliseconds;
        transitionProgress = elapsed >= 1 || !MabiTool.Desktop.Theme.UiMotion.Active ? 1 : Ease.Ease(elapsed);
        if (transitionProgress >= 1) transitionFrom = null;
        InvalidateMeasure();
        if (transitionFrom is not null && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnTransitionFrame);
    }

    private Size Transition(Size target) => transitionFrom is { } from
        ? new Size(target.Width, from + (target.Height - from) * transitionProgress)
        : target;

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public static bool GetIsPinned(Control control) => control.GetValue(IsPinnedProperty);
    public static void SetIsPinned(Control control, bool value) => control.SetValue(IsPinnedProperty, value);
    public static int GetOverflowCount(Control control) => control.GetValue(OverflowCountProperty);
    public static void SetOverflowCount(Control control, int value) => control.SetValue(OverflowCountProperty, value);

    /// <summary>上一次排版放下的项（不含展开项）；测试与排版共用。</summary>
    internal IReadOnlyList<Control> Shown => shown;
    private List<Control> shown = [];
    private bool showToggle;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children.Where(child => child.IsVisible).ToList();
        foreach (var child in children) child.Measure(Size.Infinity);
        if (children.Count == 0) { shown = []; showToggle = false; return default; }
        var toggle = children[^1];
        var items = children.Take(children.Count - 1).ToList();
        if (IsExpanded)
        {
            shown = items; showToggle = true;
            SetOverflowCount(toggle, 0);
            return Transition(Wrap(children, availableSize.Width, arrange: false));
        }

        var width = availableSize.Width;
        shown = Fit(items, width, reserve: 0);
        if (shown.Count < items.Count) shown = Fit(items, width, toggle.DesiredSize.Width);
        showToggle = shown.Count < items.Count;
        SetOverflowCount(toggle, items.Count - shown.Count);
        var row = showToggle ? shown.Append(toggle) : shown;
        var desired = row.Aggregate(new Size(), (size, child) => new Size(size.Width + child.DesiredSize.Width, Math.Max(size.Height, child.DesiredSize.Height)));
        return Transition(new Size(double.IsInfinity(width) ? desired.Width : Math.Min(desired.Width, width), desired.Height));
    }

    // 先放必须放下的项，再按顺序放其余项；结果保持原来的先后顺序。
    private static List<Control> Fit(List<Control> items, double width, double reserve)
    {
        var used = reserve + items.Where(GetIsPinned).Sum(child => child.DesiredSize.Width);
        var keep = new HashSet<Control>(items.Where(GetIsPinned));
        foreach (var child in items)
        {
            if (keep.Contains(child)) continue;
            // 放到第一个放不下的项为止，显示的是排在前面的连续几项，不跳过大的去放后面小的。
            if (used + child.DesiredSize.Width > width) break;
            keep.Add(child);
            used += child.DesiredSize.Width;
        }
        return items.Where(keep.Contains).ToList();
    }

    private static Size Wrap(IReadOnlyList<Control> children, double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, widest = 0;
        foreach (var child in children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width) { y += rowHeight; x = 0; rowHeight = 0; }
            if (arrange) child.Arrange(new Rect(x, y, Math.Min(size.Width, width), size.Height));
            x += size.Width;
            widest = Math.Max(widest, x);
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(double.IsInfinity(width) ? widest : Math.Min(widest, width), y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 裁切留出悬停放大的余量：左边总留；右边只在展开（换行排列、没有被截短的项）时留；高度过渡中纵向按当前高度裁切。
        var room = HoverRoom.Size;
        HoverRoom.Clip(this, new Rect(-room, transitionFrom is null ? -room : 0,
            finalSize.Width + room + (IsExpanded ? room : 0), finalSize.Height + (transitionFrom is null ? room * 2 : 0)));
        var children = Children.Where(child => child.IsVisible).ToList();
        if (children.Count == 0) return finalSize;
        if (IsExpanded)
        {
            Wrap(children, finalSize.Width, arrange: true);
            return finalSize;
        }
        var toggle = children[^1];
        var placed = new HashSet<Control>(shown);
        double x = 0;
        foreach (var child in children)
        {
            var visible = placed.Contains(child) || child == toggle && showToggle;
            var size = child.DesiredSize;
            // 放不下的项排到面板外，由裁切范围裁掉。
            child.Arrange(visible ? new Rect(x, 0, Math.Min(size.Width, Math.Max(0, finalSize.Width - x)), size.Height)
                : new Rect(-100000, 0, size.Width, size.Height));
            if (visible) x += size.Width;
        }
        return finalSize;
    }
}
