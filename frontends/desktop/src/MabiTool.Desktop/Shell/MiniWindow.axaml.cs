using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Shell;

/// <summary>窄长的迷你窗：无边框，拖动顶部面板的空白处移动，四边可缩放；主题、语言与动效跟随主窗口的设置。</summary>
public sealed partial class MiniWindow : Window
{
    public MiniWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
        UiMotion.Attach(this);
        AddHandler(PointerPressedEvent, OnCaptionPressed, RoutingStrategies.Bubble);
        FocusRelease.Attach(this);
        Opened += (_, _) =>
        {
            // 默认高度高于屏幕工作区时缩到工作区内。
            if (Screens.ScreenFromWindow(this) is { } screen && Height > screen.WorkingArea.Height / screen.Scaling)
                Height = Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling);
        };
    }

    /// <summary>在顶部面板的空白处按下、即将开始拖动窗口时触发。</summary>
    public event EventHandler? CaptionDragStarting;

    /// <summary>拖动带的下沿：顶部面板的底边（窗口坐标）。</summary>
    public double CaptionBandBottom => TopPanel.TranslatePoint(new Point(0, TopPanel.Bounds.Height), this)?.Y ?? 0;

    private void OnCaptionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || !WindowCaption.IsDragSurface(this, point.Position, CaptionBandBottom)) return;
        e.Handled = true;
        CaptionDragStarting?.Invoke(this, EventArgs.Empty);
        BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var windowEdge)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(windowEdge, e);
    }
}
