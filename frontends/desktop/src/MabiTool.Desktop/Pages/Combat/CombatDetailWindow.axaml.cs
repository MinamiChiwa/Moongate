using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>
/// DPS 明细窗口：与背包表格窗口同一套无边框窗口、拖动、缩放与贴靠。范围、目标与主页面是同一个选择（后端适配层保存），
/// 显示完整总表与图表；表格选中的行与排行条目互相高亮。整个应用只有一个，主窗口关闭时一并关闭；位置与尺寸保存在前端偏好里。
/// </summary>
public sealed partial class CombatDetailWindow : Window
{
    private static CombatDetailWindow? current;
    private IDisposable? maximizeTip;
    private IDisposable? maximizeName;
    private bool syncing;

    public CombatDetailWindow()
    {
        InitializeComponent();
        UiMotion.Attach(this);
        FocusRelease.Attach(this);
        AddHandler(PointerPressedEvent, OnCaptionPressed, RoutingStrategies.Bubble);
        SnapFrame.Attach(this, MaximizeButton);
        UpdateMaximizeButton();
        DetailTable.List.SelectionChanged += (_, _) =>
        {
            if (syncing || DataContext is not CombatPageModel model) return;
            model.Highlight((DetailTable.SelectedRows.FirstOrDefault() as DamageRow)?.Name);
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            Frame.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 26);
            UpdateMaximizeButton();
        };
        Closing += (_, _) => CombatDetailPreference.SetWindow(new WindowPlacement(Position.X, Position.Y,
            WindowState == WindowState.Normal ? Width : CombatDetailPreference.Window?.Width ?? Width,
            WindowState == WindowState.Normal ? Height : CombatDetailPreference.Window?.Height ?? Height,
            WindowState == WindowState.Maximized));
    }

    /// <summary>当前打开的 DPS 明细窗口；没有时为空。</summary>
    public static CombatDetailWindow? Current => current;

    /// <summary>打开（已打开时带到前面）DPS 明细窗口；<paramref name="owner"/> 关闭时一并关闭。窗口立即显示内存中的最近结果，时间线随后填入。</summary>
    public static CombatDetailWindow Open(CombatPageModel page, Window? owner)
    {
        if (current is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var model = page.Companion();
        var window = new CombatDetailWindow { DataContext = model };
        model.PropertyChanged += window.OnModelChanged;
        model.SetDetailOpen(true);
        window.Restore(CombatDetailPreference.Window);
        current = window;
        EventHandler? ownerClosed = null;
        ownerClosed = (_, _) => window.Close();
        if (owner is not null) owner.Closed += ownerClosed;
        window.Closed += (_, _) =>
        {
            if (owner is not null) owner.Closed -= ownerClosed;
            model.PropertyChanged -= window.OnModelChanged;
            model.SetDetailOpen(false);
            model.Dispose();
            if (current == window) current = null;
        };
        window.Show();
        return window;
    }

    // 排行条目按下后：表格选中同名的行（没有时清除选择）。
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CombatPageModel.Highlighted) || sender is not CombatPageModel model) return;
        var index = DetailTable.View.ToList().FindIndex(row => row is DamageRow item && item.Name == model.Highlighted);
        if ((DetailTable.SelectedRows.FirstOrDefault() as DamageRow)?.Name == model.Highlighted) return;
        syncing = true;
        try { DetailTable.List.SelectedIndex = index; }
        finally { syncing = false; }
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

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var windowEdge)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(windowEdge, e);
    }
}
