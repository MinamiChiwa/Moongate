using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Pages.Life;

public sealed partial class LifePage : UserControl
{
    private LifePageModel? watched;
    private (LifeTaskRow Row, Control Container, IPointer Pointer)? dragging;

    public LifePage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (watched is not null) { watched.PropertyChanged -= OnModelChanged; watched.FishingFocusRequested -= OnFishingFocus; }
            watched = DataContext as LifePageModel;
            if (watched is not null) { watched.PropertyChanged += OnModelChanged; watched.FishingFocusRequested += OnFishingFocus; }
        };
        // 任务行：拖动把手排序；数量框回车或离开时提交。
        TaskList.AddHandler(PointerPressedEvent, OnTaskPointerPressed, RoutingStrategies.Tunnel);
        TaskList.AddHandler(PointerMovedEvent, OnTaskPointerMoved, RoutingStrategies.Tunnel);
        TaskList.AddHandler(PointerReleasedEvent, OnTaskPointerReleased, RoutingStrategies.Tunnel);
        TaskList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble);
        TaskList.AddHandler(KeyDownEvent, OnTaskKeyDown, RoutingStrategies.Tunnel);
        TaskList.AddHandler(LostFocusEvent, OnTaskLostFocus, RoutingStrategies.Bubble);
        // 二级菜单的 Esc：焦点在搜索框里时按键可能已被输入框处理，仍然收起菜单。
        PickerLayer.AddHandler(KeyDownEvent, OnPickerKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // 确认框打开时焦点落在取消按钮（默认不执行），二级菜单打开时焦点落在搜索框；Esc 或点框外等同取消或关闭。
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (watched is null) return;
        if (e.PropertyName is null or "" or nameof(LifePageModel.ConfirmOpen) && watched.ConfirmOpen)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (watched?.ConfirmOpen == true && !ConfirmPanel.IsKeyboardFocusWithin) ConfirmCancelButton.Focus(NavigationMethod.Tab); },
                Avalonia.Threading.DispatcherPriority.Loaded);
        else if (e.PropertyName is nameof(LifePageModel.PickerOpen) && watched.PickerOpen)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (watched?.PickerOpen == true && !PickerPanel.IsKeyboardFocusWithin) PickerSearchBox.Focus(NavigationMethod.Tab); },
                Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnFishingFocus(object? sender, EventArgs e) => FishingCard.BringIntoView();

    private void OnConfirmOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.CancelCommand.Execute(null);
        e.Handled = true;
    }

    private void OnConfirmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is not { ConfirmOpen: true }) return;
        watched.CancelCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPickerOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.ClosePickerCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is not { PickerOpen: true }) return;
        watched.ClosePickerCommand.Execute(null);
        e.Handled = true;
    }

    // ───────────────────────── 拖动排序 ─────────────────────────

    private void OnTaskPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not TextBlock { Classes: var classes } handle || !classes.Contains("draghandle") || handle.DataContext is not LifeTaskRow { CanMove: true } row) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        var container = handle.FindAncestorOfType<ContentPresenter>();
        if (container is null) return;
        dragging = (row, container, e.Pointer);
        container.Opacity = 0.55;
        e.Pointer.Capture(TaskList);
        e.Handled = true;
    }

    private void OnTaskPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragging is not { } drag || e.Pointer != drag.Pointer) return;
        e.Handled = true;
    }

    private void OnTaskPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragging is not { } drag || e.Pointer != drag.Pointer) return;
        var target = DropIndex(e.GetPosition(TaskList), drag.Row);
        EndDrag();
        e.Pointer.Capture(null);
        if (target is { } index) watched?.Move(drag.Row.Id, index);
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (dragging is { } drag) drag.Container.Opacity = 1;
        dragging = null;
    }

    /// <summary>松开处对应的待执行位置：可移动的行里，第一个中线在指针下方的行的位置；都在上方时排到最后。</summary>
    private int? DropIndex(Point point, LifeTaskRow moving)
    {
        var rows = TaskList.GetRealizedContainers().OfType<ContentPresenter>()
            .Where(container => container.DataContext is LifeTaskRow { CanMove: true })
            .Select(container => (Row: (LifeTaskRow)container.DataContext!, Top: container.TranslatePoint(default, TaskList)?.Y ?? 0, container.Bounds.Height))
            .OrderBy(x => x.Top).ToList();
        if (rows.Count == 0) return null;
        var others = rows.Where(x => !ReferenceEquals(x.Row, moving)).ToList();
        var index = others.FindIndex(x => point.Y < x.Top + x.Height / 2);
        var all = watched?.Tasks.Where(x => x.CanMove).ToList() ?? [];
        var firstVisible = all.IndexOf(rows[0].Row);
        if (firstVisible < 0) return null;
        return index < 0 ? Math.Max(0, firstVisible + others.Count) : firstVisible + index;
    }

    // ───────────────────────── 数量框 ─────────────────────────

    private void OnTaskKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is not TextBox { Classes: var classes } box || !classes.Contains("countbox") || box.DataContext is not LifeTaskRow row) return;
        watched?.CommitCount(row);
        e.Handled = true;
    }

    private void OnTaskLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { Classes: var classes } box && classes.Contains("countbox") && box.DataContext is LifeTaskRow row) watched?.CommitCount(row);
    }
}
