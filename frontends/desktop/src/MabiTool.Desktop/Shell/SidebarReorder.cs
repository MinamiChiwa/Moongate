using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 侧栏模块入口的拖动排序：按住入口拖动超过 <see cref="Threshold"/> 后，入口像卡片一样被拽起（略微放大、带阴影、跟随指针），
/// 拖到两项之间时其余入口平滑让位，松开后落位并保存顺序；拖到分组外松开时回到原位。
/// 只在同一分组（一个 <see cref="ItemsControl"/>）内排序；没有拖过阈值的按下仍是点击。
/// 聚焦入口后 Alt+↑/↓ 向上或向下移动一位。动效只用变换，不改布局尺寸；动效减弱时没有过渡。
/// </summary>
public sealed class SidebarReorder
{
    /// <summary>按下后移动超过这段距离才算拖动，否则松开仍是点击。</summary>
    public const double Threshold = 6;

    /// <summary>拖起时的放大比例。</summary>
    public const double LiftScale = 1.02;

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(180);

    private readonly ItemsControl list;
    private readonly Func<ShellViewModel?> shell;
    private Press? press;
    private Drag? drag;

    private SidebarReorder(ItemsControl list, Func<ShellViewModel?> shell)
    {
        this.list = list;
        this.shell = shell;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>让一个分组的入口可以拖动排序。</summary>
    public static void Attach(ItemsControl list, Func<ShellViewModel?> shell) => _ = new SidebarReorder(list, shell);

    /// <summary>拖动开始与结束时触发，供测试观察；参数为真表示开始。</summary>
    public static event EventHandler<bool>? DragStateChanged;

    private static bool Motion(Visual visual) => TopLevel.GetTopLevel(visual)?.Classes.Contains("motion") == true;

    private sealed record Press(IPointer Pointer, NavigationItem Item, Point Start);

    private sealed class Drag(NavigationItem item, int from, IReadOnlyList<Slot> slots, double start)
    {
        public NavigationItem Item { get; } = item;
        public int From { get; } = from;
        public IReadOnlyList<Slot> Slots { get; } = slots;
        public double Start { get; } = start;
        public double Offset { get; set; }
        public int Target { get; set; } = from;
    }

    // 拖动开始时各入口的位置（分组坐标），之后的让位都按这份位置计算，不随变换改变。
    private sealed record Slot(Control Container, double Top, double Height)
    {
        public double Center => Top + Height / 2;
    }

    private NavigationItem? ItemAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().TakeWhile(visual => visual != list).OfType<Button>()
            .FirstOrDefault(button => button.DataContext is NavigationItem)?.DataContext as NavigationItem;

    private Control? ContainerOf(NavigationItem item) => list.ContainerFromItem(item);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (drag is not null || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed || ItemAt(e.Source) is not { } item) return;
        press = new Press(e.Pointer, item, e.GetPosition(list));
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (press is not { } pressed || e.Pointer != pressed.Pointer) return;
        var position = e.GetPosition(list);
        if (drag is null)
        {
            if (Math.Abs(position.Y - pressed.Start.Y) < Threshold && Math.Abs(position.X - pressed.Start.X) < Threshold) return;
            if (!Begin(pressed)) return;
            // 拖动开始后指针交给分组：入口按钮失去捕获，松开时不再算作点击。
            e.Pointer.Capture(list);
        }
        Follow(position.Y - pressed.Start.Y);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (press is not { } pressed || e.Pointer != pressed.Pointer) return;
        press = null;
        if (drag is null) return;
        var position = e.GetPosition(list);
        // 松开在分组外（左右越出分组，或上下越出首末两项半个入口以上）时回到原位。
        var limit = new Rect(list.Bounds.Size).Inflate(new Thickness(0, drag.Slots[0].Height / 2));
        Finish(commit: limit.Contains(position));
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (drag is null || press is not null) return;
        Finish(commit: false);
    }

    private bool Begin(Press pressed)
    {
        if (shell()?.GroupOf(pressed.Item) is not { Count: > 1 } group) return false;
        var slots = group.Select(entry => ContainerOf(entry)).ToList();
        if (slots.Any(container => container is null)) return false;
        var from = group.IndexOf(pressed.Item);
        var motion = Motion(list);
        var measured = slots.Select(container => new Slot(container!, container!.Bounds.Y, container.Bounds.Height)).ToList();
        foreach (var slot in measured)
        {
            slot.Container.Transitions = motion && slot.Container != measured[from].Container
                ? [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Settle, Easing = new CubicEaseOut() }]
                : null;
            slot.Container.RenderTransform = null;
        }
        var lifted = measured[from].Container;
        lifted.ZIndex = 1;
        lifted.RenderTransformOrigin = RelativePoint.Center;
        if (lifted.GetVisualDescendants().OfType<Button>().FirstOrDefault() is { } button) button.Classes.Add("dragging");
        drag = new Drag(pressed.Item, from, measured, measured[from].Top);
        DragStateChanged?.Invoke(list, true);
        return true;
    }

    // 拽起的入口跟随指针，限制在分组范围内；其余入口按拽起项中心经过的位置让位。
    private void Follow(double delta)
    {
        if (drag is null) return;
        var slots = drag.Slots;
        var moving = slots[drag.From];
        var top = Math.Clamp(moving.Top + delta, slots[0].Top, slots[^1].Top + slots[^1].Height - moving.Height);
        drag.Offset = top - moving.Top;
        moving.Container.RenderTransform = Lift(drag.Offset, LiftScale);

        var center = moving.Center + drag.Offset;
        var target = 0;
        for (var index = 0; index < slots.Count; index++)
            if (index != drag.From && slots[index].Center < center) target++;
        drag.Target = target;
        for (var index = 0; index < slots.Count; index++)
        {
            if (index == drag.From) continue;
            var shift = index < drag.From && index >= target ? moving.Height
                : index > drag.From && index <= target ? -moving.Height
                : 0;
            slots[index].Container.RenderTransform = shift == 0 ? null : Lift(shift, 1);
        }
    }

    private void Finish(bool commit)
    {
        if (drag is not { } done) return;
        drag = null;
        var motion = Motion(list);
        var moving = done.Slots[done.From];
        if (moving.Container.GetVisualDescendants().OfType<Button>().FirstOrDefault() is { } button) button.Classes.Remove("dragging");
        moving.Container.ClearValue(Visual.ZIndexProperty);
        foreach (var slot in done.Slots)
        {
            slot.Container.Transitions = null;
            slot.Container.RenderTransform = null;
        }

        var moved = commit && done.Target != done.From && shell()?.MoveModule(done.Item, done.Target) == true;
        // 落位：拽起项从松开时的位置回到它在列表里的新位置（或原位）；动效减弱时直接到位。
        var landing = done.Slots[done.From].Top + done.Offset;
        var finalTop = moved
            ? done.Slots.Where((_, index) => index != done.From).Take(done.Target).Sum(slot => slot.Height) + done.Slots[0].Top
            : done.Slots[done.From].Top;
        if (motion && ContainerOf(done.Item) is { } container && Math.Abs(landing - finalTop) > 0.5)
        {
            container.RenderTransformOrigin = RelativePoint.Center;
            container.Transitions = null;
            container.RenderTransform = Lift(landing - finalTop, LiftScale);
            container.Transitions =
                [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Settle, Easing = new CubicEaseOut() }];
            container.RenderTransform = Lift(0, 1);
        }
        DragStateChanged?.Invoke(list, false);
    }

    private static ITransform Lift(double y, double scale)
    {
        // 先缩放再平移：平移量不随缩放放大，拽起的入口停在分组范围内。
        var builder = new TransformOperations.Builder(2);
        builder.AppendScale(scale, scale);
        builder.AppendTranslate(0, y);
        return builder.Build();
    }

    // Alt+↑/↓：聚焦的入口在分组内移动一位，焦点留在它身上。
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (drag is not null || e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down)) return;
        if (ItemAt(e.Source) is not { } item || shell() is not { } model || model.GroupOf(item) is not { } group) return;
        var index = group.IndexOf(item) + (e.Key == Key.Up ? -1 : 1);
        e.Handled = true;
        if (index < 0 || index >= group.Count || !model.MoveModule(item, index)) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            ContainerOf(item)?.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus(NavigationMethod.Tab),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
