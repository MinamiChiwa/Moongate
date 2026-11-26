using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Overlay;

namespace MabiTool.Desktop.Pages.Overlay;

public sealed partial class OverlayPage : UserControl
{
    private OverlayPageModel? watched;
    private Screens? screens;
    // 选色浮层在第一次点色块时才建立，放在页面根容器里。
    private ColorPickerLayer? picker;

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

    // 点配色项的色块：打开选色浮层，贴在色块下方；选色时每次改动立即写进版面。
    private void OnColorSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ColorEntry entry } swatch || watched?.OpenColorPicker(entry) is not { } model) return;
        if (picker is null)
        {
            picker = new ColorPickerLayer { Name = "OverlayColorPicker", HexEditable = true };
            Root.Children.Add(picker);
        }
        picker.CloseCommand = watched.ClosePickerCommand;
        picker.Title = watched.PickerTitle;
        picker.Open(model, swatch);
    }

    // 打开时焦点落在层内的关闭按钮，关闭后回到入口按钮。
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayPageModel.IsPickerOpen) or "" && watched is { IsPickerOpen: false } && picker is { IsOpen: true })
            picker.Close();
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

    // 觉醒提示选用本机 wav 或 mp3；只记路径，播放时读取。
    private async void OnPickSound(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not OverlayPageModel model) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV / MP3") { Patterns = ["*.wav", "*.mp3"] }]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) model.SetAlertFile(path);
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
