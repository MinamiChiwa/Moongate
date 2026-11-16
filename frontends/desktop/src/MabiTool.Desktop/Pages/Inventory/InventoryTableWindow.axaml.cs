using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Pages.Inventory;

/// <summary>
/// 背包表格的独立窗口：无边框，窗口按钮在右上角，顶部面板的空白处拖动（双击最大化或还原），四边可缩放并保留系统贴靠。
/// 与背包页是同一张表：同一后端、同一套列设置（表标识 inventory.items）、同一份备注与账号选择。
/// 整个应用只有一个，主窗口关闭时一并关闭；位置与尺寸保存在前端偏好里。
/// </summary>
public sealed partial class InventoryTableWindow : Window
{
    private static InventoryTableWindow? current;
    private IDisposable? maximizeTip;
    private IDisposable? maximizeName;

    public InventoryTableWindow()
    {
        InitializeComponent();
        UiMotion.Attach(this);
        FocusRelease.Attach(this);
        AddHandler(PointerPressedEvent, OnCaptionPressed, RoutingStrategies.Bubble);
        ItemsTable.RowKey = row => (row as InventoryPivotRow)?.TypeKey;
        SnapFrame.Attach(this, MaximizeButton);
        UpdateMaximizeButton();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            Frame.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 26);
            UpdateMaximizeButton();
        };
        Closing += (_, _) => InventoryPreference.SetWindow(new WindowPlacement(Position.X, Position.Y,
            WindowState == WindowState.Normal ? Width : InventoryPreference.Window?.Width ?? Width,
            WindowState == WindowState.Normal ? Height : InventoryPreference.Window?.Height ?? Height,
            WindowState == WindowState.Maximized));
    }

    /// <summary>当前打开的背包窗口；没有时为空。</summary>
    public static InventoryTableWindow? Current => current;

    /// <summary>打开（已打开时带到前面）背包窗口；<paramref name="owner"/> 关闭时一并关闭。</summary>
    public static InventoryTableWindow Open(InventoryPageModel page, Window? owner)
    {
        if (current is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = new InventoryTableWindow { DataContext = page.Companion() };
        window.Restore(InventoryPreference.Window);
        current = window;
        EventHandler? ownerClosed = null;
        ownerClosed = (_, _) => window.Close();
        if (owner is not null) owner.Closed += ownerClosed;
        window.Closed += (_, _) =>
        {
            if (owner is not null) owner.Closed -= ownerClosed;
            (window.DataContext as IDisposable)?.Dispose();
            if (current == window) current = null;
        };
        window.Show();
        return window;
    }

    // 上次的位置仍在某块屏幕的工作区内时沿用，否则居中显示。
    private void Restore(WindowPlacement? placement)
    {
        if (placement is null) return;
        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        var point = new PixelPoint(placement.X, placement.Y);
        if (Screens.All.Any(screen => screen.WorkingArea.Contains(point)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = point;
        }
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    private double CaptionBandBottom => TopPanel.TranslatePoint(new Point(0, TopPanel.Bounds.Height), this)?.Y ?? 0;

    private void OnCaptionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || !WindowCaption.IsDragSurface(this, point.Position, CaptionBandBottom)) return;
        e.Handled = true;
        if (e.ClickCount >= 2 && e.ClickCount % 2 == 0)
        {
            ToggleMaximize();
            return;
        }
        BeginMoveDrag(e);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeButton()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        var key = maximized ? "S.Restore" : "S.Maximize";
        maximizeTip?.Dispose();
        maximizeName?.Dispose();
        maximizeTip = MaximizeButton.Bind(ToolTip.TipProperty, MaximizeButton.GetResourceObservable(key));
        maximizeName = MaximizeButton.Bind((AvaloniaProperty)AutomationProperties.NameProperty, MaximizeButton.GetResourceObservable(key));
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnExportClick(object? sender, RoutedEventArgs e) => _ = ItemsTable.ExportCsvAsync();

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var windowEdge)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(windowEdge, e);
    }
}
