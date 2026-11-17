using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MabiTool.Desktop.Overlay;

namespace MabiTool.Desktop.Pages.Overlay;

public sealed partial class OverlayPage : UserControl
{
    private OverlayPageModel? watched;
    private Screens? screens;

    public OverlayPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnModelChanged;
            watched = DataContext as OverlayPageModel;
            if (watched is not null) watched.PropertyChanged += OnModelChanged;
            UpdateScreens();
        };
    }

    // 显示器说明随显示器布局（插拔、排列、分辨率、缩放）更新。
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        screens = TopLevel.GetTopLevel(this)?.Screens;
        if (screens is not null) screens.Changed += OnScreensChanged;
        UpdateScreens();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (screens is not null) screens.Changed -= OnScreensChanged;
        screens = null;
    }

    private void OnScreensChanged(object? sender, EventArgs e) => UpdateScreens();

    private void UpdateScreens()
    {
        if (watched is not null) watched.Screens = OverlayArea.Describe(screens ?? TopLevel.GetTopLevel(this)?.Screens);
    }

    // 打开时焦点落在层内的关闭按钮，关闭后回到入口按钮。
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OverlayPageModel.IsGameDataOpen) || watched is null) return;
        // 层的可见性随绑定更新，等布局完成后再移焦点。
        var open = watched.IsGameDataOpen;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (open) GameDataClose.Focus(NavigationMethod.Tab);
            else GameDataButton.Focus(NavigationMethod.Tab);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnGameDataOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.CloseGameDataCommand.Execute(null);
        e.Handled = true;
    }

    private void OnGameDataKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is not { IsGameDataOpen: true }) return;
        watched.CloseGameDataCommand.Execute(null);
        e.Handled = true;
    }

    public static OverlayPage Create(OverlayPageModel model) => new() { DataContext = model };

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
