using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using MabiTool.Desktop.Controls.Tables;
using MabiTool.Desktop.Shell;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>
/// 成员表的拖动调整分组与顺序，做法与侧栏的 <see cref="SidebarReorder"/> 相同：按住行（不在输入框、下拉框、勾选框或按钮上）
/// 拖过 <see cref="SidebarReorder.Threshold"/> 后行被拽起（略微放大、跟随指针），其余行按拽起行中心经过的位置让位；
/// 松开后按落点交给页面模型提交分组与顺序，拖到表外松开回到原位。聚焦行内元素后 Alt+↑/↓ 移动一位。
/// 动效只用变换，不改布局尺寸；动效减弱时没有过渡。只在房主可以编辑时生效。
/// </summary>
public sealed class RosterReorder
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(180);

    private readonly ItemsControl list;
    private readonly Func<RoomsPageModel?> model;
    private (IPointer Pointer, RosterRow Row, Point Start)? press;
    private Drag? drag;

    private sealed class Drag(int from, IReadOnlyList<(Control Container, double Top, double Height)> slots)
    {
        public int From { get; } = from;
        public IReadOnlyList<(Control Container, double Top, double Height)> Slots { get; } = slots;
        public double Offset { get; set; }
        public int Target { get; set; } = from;
    }

    private RosterReorder(ItemsControl list, Func<RoomsPageModel?> model)
    {
        this.list = list;
        this.model = model;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public static void Attach(ItemsControl list, Func<RoomsPageModel?> model) => _ = new RosterReorder(list, model);

    /// <summary>拖动开始与结束时触发，供测试观察；参数为真表示开始。</summary>
    public static event EventHandler<bool>? DragStateChanged;

    private static bool Motion(Visual visual) => TopLevel.GetTopLevel(visual)?.Classes.Contains("motion") == true;

    private RosterRow? RowAt(object? source, bool allowInteractive)
    {
        var chain = (source as Visual)?.GetSelfAndVisualAncestors().TakeWhile(visual => visual != list).ToList() ?? [];
        if (!allowInteractive && chain.Any(visual => visual is TextBox or TableInput or ComboBox or TableChoice or CheckBox or Button)) return null;
        return chain.OfType<Control>().Select(control => control.DataContext).OfType<RosterRow>().FirstOrDefault();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (drag is not null || model() is not { CanEditRoster: true } || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed
            || RowAt(e.Source, false) is not { } row) return;
        press = (e.Pointer, row, e.GetPosition(list));
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (press is not { } pressed || e.Pointer != pressed.Pointer) return;
        var position = e.GetPosition(list);
        if (drag is null)
        {
            if (Math.Abs(position.Y - pressed.Start.Y) < SidebarReorder.Threshold && Math.Abs(position.X - pressed.Start.X) < SidebarReorder.Threshold) return;
            if (!Begin(pressed.Row)) return;
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

    private bool Begin(RosterRow row)
    {
        if (model() is not { } page || page.Rows.Count < 2) return false;
        var containers = page.Rows.Select(item => list.ContainerFromItem(item)).ToList();
        if (containers.Any(container => container is null)) return false;
        var from = page.Rows.ToList().FindIndex(item => item.Id == row.Id);
        if (from < 0) return false;
        var motion = Motion(list);
        var slots = containers.Select(container => (container!, container!.Bounds.Y, container.Bounds.Height)).ToList();
        foreach (var (container, _, _) in slots)
        {
            container.Transitions = motion && container != slots[from].Item1
                ? [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Settle, Easing = new CubicEaseOut() }]
                : null;
            container.RenderTransform = null;
        }
        var lifted = slots[from].Item1;
        lifted.ZIndex = 1;
        lifted.RenderTransformOrigin = RelativePoint.Center;
        lifted.Opacity = 0.92;
        drag = new Drag(from, slots);
        DragStateChanged?.Invoke(list, true);
        return true;
    }

    private void Follow(double delta)
    {
        if (drag is null) return;
        var slots = drag.Slots;
        var moving = slots[drag.From];
        var top = Math.Clamp(moving.Top + delta, slots[0].Top, slots[^1].Top + slots[^1].Height - moving.Height);
        drag.Offset = top - moving.Top;
        moving.Container.RenderTransform = Lift(drag.Offset, SidebarReorder.LiftScale);
        var center = moving.Top + moving.Height / 2 + drag.Offset;
        var target = 0;
        for (var index = 0; index < slots.Count; index++)
            if (index != drag.From && slots[index].Top + slots[index].Height / 2 < center) target++;
        drag.Target = target;
        for (var index = 0; index < slots.Count; index++)
        {
            if (index == drag.From) continue;
            var shift = index < drag.From && index >= target ? moving.Height : index > drag.From && index <= target ? -moving.Height : 0;
            slots[index].Container.RenderTransform = shift == 0 ? null : Lift(shift, 1);
        }
    }

    private void Finish(bool commit)
    {
        if (drag is not { } done) return;
        drag = null;
        var moving = done.Slots[done.From];
        moving.Container.ClearValue(Visual.ZIndexProperty);
        moving.Container.ClearValue(Visual.OpacityProperty);
        var motion = Motion(list);
        foreach (var (container, _, _) in done.Slots)
        {
            container.Transitions = null;
            container.RenderTransform = null;
        }
        var moved = commit && done.Target != done.From && model()?.MoveRow(done.From, done.Target) == true;
        // 落位：拽起的行从松开处回到落点（名单随后以后端重读为准）；没有提交时回到原位。动效减弱时直接到位。
        var landing = moving.Top + done.Offset;
        var finalTop = moved
            ? done.Slots.Where((_, index) => index != done.From).Take(done.Target).Sum(slot => slot.Height) + done.Slots[0].Top
            : moving.Top;
        if (motion && Math.Abs(landing - finalTop) > 0.5)
        {
            moving.Container.RenderTransform = Lift(landing - finalTop, SidebarReorder.LiftScale);
            moving.Container.Transitions =
                [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Settle, Easing = new CubicEaseOut() }];
            moving.Container.RenderTransform = Lift(0, 1);
        }
        DragStateChanged?.Invoke(list, false);
    }

    private static ITransform Lift(double y, double scale)
    {
        var builder = new TransformOperations.Builder(2);
        builder.AppendScale(scale, scale);
        builder.AppendTranslate(0, y);
        return builder.Build();
    }

    // Alt+↑/↓：焦点所在的行移动一位。
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (drag is not null || e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down)) return;
        if (model() is not { CanEditRoster: true } page || RowAt(e.Source, true) is not { } row) return;
        var from = page.Rows.ToList().FindIndex(item => item.Id == row.Id);
        e.Handled = true;
        page.MoveRow(from, from + (e.Key == Key.Up ? -1 : 1));
    }
}
