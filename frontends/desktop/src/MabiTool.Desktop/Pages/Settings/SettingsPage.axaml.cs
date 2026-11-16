using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MabiTool.Desktop.Pages.Settings;

public sealed partial class SettingsPage : UserControl
{
    private TopLevel? host;

    public SettingsPage() => InitializeComponent();

    // 录入快捷键时在窗口层先于其他控件拿到按键，焦点在哪里都能录入，空格、回车也不会被当成点击。
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        host = TopLevel.GetTopLevel(this);
        host?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        host?.RemoveHandler(KeyDownEvent, OnKeyDown);
        host = null;
    }

    /// <summary>把更新区滚到滚动区顶部（窗口右上角的更新提示用）；页面尚未排版时等排版后再滚。</summary>
    public void RevealUpdates() => Reveal(nameof(UpdatePanel));

    /// <summary>把指定面板（UpdatePanel、CapturePanel）滚到滚动区顶部；页面尚未排版时等排版后再滚。</summary>
    public void Reveal(string name)
    {
        if (this.FindControl<Control>(name) is not { } panel) return;
        if (panel.Bounds.Height <= 0)
        {
            EventHandler? once = null;
            once = (_, _) =>
            {
                if (panel.Bounds.Height <= 0) return;
                LayoutUpdated -= once;
                Reveal(name);
            };
            LayoutUpdated += once;
            return;
        }
        if (PageScroller.Content is Visual content && panel.TranslatePoint(default, content) is { } top)
            PageScroller.Offset = new Vector(PageScroller.Offset.X,
                Math.Clamp(top.Y, 0, Math.Max(0, PageScroller.Extent.Height - PageScroller.Viewport.Height)));
    }

    // 下载限速在按回车或离开输入框时提交。
    private void OnLimitKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || DataContext is not SettingsPageModel model) return;
        e.Handled = true;
        model.CommitLimit();
    }

    private void OnLimitLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsPageModel model) model.CommitLimit();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsPageModel { IsRecording: true } model) return;
        e.Handled = true;
        _ = model.OnKeyAsync(e.Key, e.KeyModifiers);
    }
}
