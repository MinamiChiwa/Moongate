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
/// 显示完整总表与图表；表格选中的行与排行条目互相高亮。实时明细整个应用只有一个，主窗口关闭时一并关闭；位置与尺寸保存在前端偏好里。
/// 同一窗口类另开记录明细（<see cref="OpenRecord"/>）：每条记录一个，至多 <see cref="MaximumRecordWindows"/> 个，关闭时不改写保存的位置。
/// </summary>
public sealed partial class CombatDetailWindow : Window
{
    /// <summary>同时打开的记录明细窗口上限（不含实时明细窗口）。</summary>
    public const int MaximumRecordWindows = 4;
    private static CombatDetailWindow? current;
    private static readonly Dictionary<string, CombatDetailWindow> records = new(StringComparer.Ordinal);
    private string? recordId;
    private int slot;
    private IDisposable? maximizeTip;
    private IDisposable? maximizeName;
    private bool syncing;

    public CombatDetailWindow()
    {
        InitializeComponent();
        Shell.AppIcon.Apply(this);
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
        Closing += (_, _) =>
        {
            if (recordId is not null) return;
            CombatDetailPreference.SetWindow(new WindowPlacement(Position.X, Position.Y,
            WindowState == WindowState.Normal ? Width : CombatDetailPreference.Window?.Width ?? Width,
            WindowState == WindowState.Normal ? Height : CombatDetailPreference.Window?.Height ?? Height,
            WindowState == WindowState.Maximized));
        };
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

    /// <summary>
    /// 打开（已打开时带到前面）这条战斗记录的明细窗口；已开满 <see cref="MaximumRecordWindows"/> 个时不打开并返回假。
    /// 窗口从保存的位置（没有时为主屏工作区居中）按最小的空闲序号错开，<paramref name="owner"/> 关闭时一并关闭。
    /// </summary>
    public static bool OpenRecord(CombatPageModel page, string id, Window? owner)
    {
        if (records.TryGetValue(id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return true;
        }
        if (records.Count >= MaximumRecordWindows) return false;
        var model = page.Companion(id);
        var window = new CombatDetailWindow { DataContext = model, recordId = id };
        model.PropertyChanged += window.OnModelChanged;
        model.SetDetailOpen(true);
        window.Title = model.RecordWindowTitle;
        window.Restore(CombatDetailPreference.Window);
        var saved = window.WindowStartupLocation == WindowStartupLocation.Manual;
        var screen = (saved ? window.Screens.ScreenFromPoint(window.Position) : null) ?? window.Screens.Primary;
        var scaling = screen?.Scaling ?? 1;
        var size = new PixelSize((int)Math.Ceiling(window.Width * scaling), (int)Math.Ceiling(window.Height * scaling));
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, size.Width, size.Height);
        var origin = saved ? window.Position
            : new PixelPoint(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
        (window.slot, var position) = RecordPlacement(records.Values.Select(open => open.slot).ToList(), origin, size, area);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = position;
        records[id] = window;
        EventHandler? ownerClosed = null;
        ownerClosed = (_, _) => window.Close();
        if (owner is not null) owner.Closed += ownerClosed;
        window.Closed += (_, _) =>
        {
            if (owner is not null) owner.Closed -= ownerClosed;
            model.PropertyChanged -= window.OnModelChanged;
            model.SetDetailOpen(false);
            model.Dispose();
            if (records.TryGetValue(id, out var open) && open == window) records.Remove(id);
        };
        window.Show();
        return true;
    }

    /// <summary>每个序号错开的像素（横纵相同）。</summary>
    internal const int RecordOffset = 32;

    /// <summary>
    /// 记录明细窗口的位置：取 1 起最小的未占用序号，从 <paramref name="origin"/> 横纵各错开“序号 × 32”像素，
    /// 再拉回 <paramref name="area"/>（所在屏幕工作区）内；窗口比工作区大时贴左上角。
    /// </summary>
    internal static (int Slot, PixelPoint Position) RecordPlacement(IReadOnlyCollection<int> used, PixelPoint origin, PixelSize size, PixelRect area)
    {
        var slot = 1;
        while (used.Contains(slot)) slot++;
        static int Clamp(int value, int start, int length, int span) => Math.Max(start, Math.Min(value, start + length - span));
        return (slot, new PixelPoint(Clamp(origin.X + RecordOffset * slot, area.X, area.Width, size.Width),
            Clamp(origin.Y + RecordOffset * slot, area.Y, area.Height, size.Height)));
    }

    // 排行条目按下后：表格选中同名的行（没有时清除选择）；记录名称读到后更新标题栏文字。
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CombatPageModel model) return;
        if (recordId is not null && Title != model.RecordWindowTitle) Title = model.RecordWindowTitle;
        if (e.PropertyName != nameof(CombatPageModel.Highlighted)) return;
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
