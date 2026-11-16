using Avalonia;
using Avalonia.Controls;

namespace MabiTool.Desktop.Overlay;

public sealed partial class OverlayWidgetView : UserControl
{
    private TopLevel? host;

    public OverlayWidgetView() => InitializeComponent();

    // 元件挂在可见的窗口上时才算显示：合奏信息浮窗的倒计时定时器只在这时运行，窗口隐藏或元件移出画面即停止。
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        host = TopLevel.GetTopLevel(this);
        if (host is not null) host.PropertyChanged += OnHostChanged;
        UpdateShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (host is not null) host.PropertyChanged -= OnHostChanged;
        host = null;
        UpdateShown();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateShown();
    }

    private void OnHostChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) UpdateShown();
    }

    private void UpdateShown() => (DataContext as OverlayWidgetModel)?.SetShown(host is { IsVisible: true });
}
