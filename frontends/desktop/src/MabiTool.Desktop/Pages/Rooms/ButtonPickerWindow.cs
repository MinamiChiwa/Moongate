using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MabiTool.Contracts;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>
/// 点选按钮位置的拾取层（原工具 ButtonPickerWindow）：透明、置顶、无边框，覆盖游戏窗口客户区（未绑定时主显示器）。
/// 左键记录点击处的客户区坐标、客户区尺寸与 DPI；右键或 Esc 取消。拾取期间隐藏 Moongate 的其他可见窗口，结束后恢复。
/// 坐标换算见 <see cref="ButtonPick.ToBinding"/>。
/// </summary>
public sealed class ButtonPickerWindow : Window
{
    private readonly RoomGameWindow area;
    private readonly TaskCompletionSource<InputBindingPreference?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ButtonPickerWindow(RoomGameWindow area, string hint)
    {
        this.area = area;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = new SolidColorBrush(Color.FromArgb(24, 30, 100, 220));
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Cursor = new Cursor(StandardCursorType.Cross);
        Position = new PixelPoint(area.X, area.Y);
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(62, 130, 245)), BorderThickness = new Thickness(2),
            Child = new Border
            {
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12),
                Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(240, 24, 39, 64)),
                Child = new TextBlock { Text = hint, Foreground = Brushes.White, FontSize = 16, TextWrapping = TextWrapping.Wrap }
            }
        };
        Opened += (_, _) =>
        {
            // 位置与尺寸都按物理像素给出；窗口尺寸是逻辑像素，按所在显示器的缩放比例换算。
            Position = new PixelPoint(area.X, area.Y);
            Width = area.Width / RenderScaling;
            Height = area.Height / RenderScaling;
            Activate();
        };
        PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed) Finish(null);
            else if (point.Properties.IsLeftButtonPressed) Finish(ButtonPick.ToBinding(this.area, this.PointToScreen(point.Position)));
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Finish(null);
        };
        Closed += (_, _) => completion.TrySetResult(null);
    }

    private void Finish(InputBindingPreference? binding)
    {
        completion.TrySetResult(binding);
        Close();
    }

    /// <summary>
    /// 隐藏 Moongate 的可见窗口，显示拾取层，等待一次点击；返回绑定，取消时为空。之后恢复原来可见的窗口。
    /// </summary>
    public static async Task<InputBindingPreference?> PickAsync(RoomGameWindow? game, string hint, Visual owner)
    {
        var screens = TopLevel.GetTopLevel(owner) is Window window ? window.Screens : null;
        var area = game ?? (screens?.Primary is { } primary
            ? new RoomGameWindow(primary.Bounds.X, primary.Bounds.Y, primary.Bounds.Width, primary.Bounds.Height, (uint)Math.Round(primary.Scaling * 96))
            : null);
        if (area is null) return null;
        var hidden = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .Where(item => item.IsVisible).ToList() ?? [];
        foreach (var item in hidden) item.Hide();
        var picker = new ButtonPickerWindow(area, hint);
        try
        {
            picker.Show();
            return await picker.completion.Task;
        }
        finally
        {
            foreach (var item in hidden) item.Show();
            hidden.FirstOrDefault(item => item is not MabiTool.Desktop.Overlay.OverlayWindow)?.Activate();
        }
    }
}
