using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Overlay;

namespace MabiTool.Desktop.Pages.Overlay;

public sealed partial class OverlayPage : UserControl
{
    private OverlayPageModel? watched;

    public OverlayPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnModelChanged;
            watched = DataContext as OverlayPageModel;
            if (watched is not null) watched.PropertyChanged += OnModelChanged;
            UpdateReferenceSize();
        };
        AttachedToVisualTree += (_, _) => UpdateReferenceSize();
    }

    // 编辑画布只画摆有元件的显示器（都没有时为主显示器），以主显示器的缩放比例为参照等比缩小，元件画在各自的显示器上。
    // 此前画整个虚拟桌面：两块显示器时元件只有约 1/5 大（1920 + 1920@125% 时 DPS 元件在 760 宽的画布上 34×35），看起来像没有元件。
    private void UpdateReferenceSize()
    {
        if (watched is null) return;
        var area = OverlayArea.ForEditor(OverlayArea.Describe(TopLevel.GetTopLevel(this)?.Screens),
            watched.Store.Active.Widgets.Select(widget => widget.Screen));
        Canvas.ReferenceSize = area?.Logical ?? OverlayArea.FallbackLogical;
        Canvas.Monitors = area?.Monitors ?? [];
        CanvasLabel.Text = OverlayArea.Caption(area?.Screens);
    }

    // 打开时焦点落在层内的关闭按钮，关闭后回到入口按钮。
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateReferenceSize();
        if (e.PropertyName != nameof(OverlayPageModel.IsGameDataOpen) || watched is null) return;
        // 层的可见性随绑定更新，等布局完成后再移焦点。
        var open = watched.IsGameDataOpen;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (open) GameDataClose.Focus(NavigationMethod.Tab);
            else GameDataButton.Focus(NavigationMethod.Tab);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnGameDataOutsidePressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        watched?.CloseGameDataCommand.Execute(null);
        e.Handled = true;
    }

    private void OnGameDataKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Escape || watched is not { IsGameDataOpen: true }) return;
        watched.CloseGameDataCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>画布与页面模型读同一份版面，所以画布上的拖动立即反映到右侧设置。</summary>
    public static OverlayPage Create(OverlayPageModel model, IDesktopBackend backend)
    {
        var page = new OverlayPage { DataContext = model };
        page.Canvas.Attach(model.Store, model.Catalog, backend);
        return page;
    }

    private async void OnPickImage(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not OverlayPageModel model) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG / WebP / JPEG") { Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg"] }]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) model.ImagePath = path;
    }
}
