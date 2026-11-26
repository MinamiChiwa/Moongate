using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>
/// 生活浮窗元件的视图：<see cref="Mode"/> 为 <c>facilities</c> 显示“加工设施”，<c>queue</c> 显示“生活队列”。
/// 操作区（overlayactions）在元件上锁时由浮窗画布的样式隐藏；这时关闭元件内的配方菜单与确认，解锁后回到圆点显示。
/// 浮窗窗口平时不激活（键盘留给游戏）；配方菜单打开或点按搜索框时激活浮窗窗口并把焦点放进搜索框，输入的文字才会进入搜索框。
/// </summary>
public sealed partial class LifeOverlayView : UserControl
{
    public static readonly StyledProperty<string?> ModeProperty = AvaloniaProperty.Register<LifeOverlayView, string?>(nameof(Mode));

    private LifeOverlayData? watched;
    private bool menuOpen;

    public LifeOverlayView()
    {
        InitializeComponent();
        foreach (var area in new Control[] { MenuArea, FacilitiesConfirmArea, QueueConfirmArea })
            area.PropertyChanged += (_, change) =>
            {
                if (change.Property == IsVisibleProperty && change.NewValue is false) (DataContext as LifeOverlayData)?.CloseTransient();
            };
        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnDataChanged;
            watched = DataContext as LifeOverlayData;
            menuOpen = watched?.MenuOpen == true;
            if (watched is not null) watched.PropertyChanged += OnDataChanged;
        };
        // 搜索框自己处理按下与按键；这里在已处理的事件上也响应：按下时激活浮窗窗口，Esc 收起菜单。
        OverlayMenuSearch.AddHandler(PointerPressedEvent, (_, _) => ActivateWindow(), RoutingStrategies.Tunnel, handledEventsToo: true);
        OverlayMenu.AddHandler(KeyDownEvent, OnMenuKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public string? Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ModeProperty) return;
        FacilitiesView.IsVisible = Mode == "facilities";
        QueueView.IsVisible = Mode == "queue";
    }

    // 菜单从关闭变为打开（点按“＋”空槽）时焦点落在搜索框一次；菜单开着期间的快照刷新不再抢焦点。
    private void OnDataChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(LifeOverlayData.MenuOpen))) return;
        var open = watched?.MenuOpen == true;
        var opened = open && !menuOpen;
        menuOpen = open;
        if (!opened) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (watched is not { MenuOpen: true } || OverlayMenuSearch.IsFocused) return;
            ActivateWindow();
            OverlayMenuSearch.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void ActivateWindow()
    {
        if (TopLevel.GetTopLevel(this) is Window { IsActive: false } window) window.Activate();
    }

    private void OnMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is not { MenuOpen: true }) return;
        watched.CloseTransient();
        e.Handled = true;
    }
}
