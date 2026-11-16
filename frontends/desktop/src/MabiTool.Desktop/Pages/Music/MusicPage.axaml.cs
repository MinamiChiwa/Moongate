using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MabiTool.Desktop.Pages.Music;

public sealed partial class MusicPage : UserControl
{
    private MusicPageModel? watched;

    public MusicPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnModelChanged;
            watched = DataContext as MusicPageModel;
            if (watched is not null) watched.PropertyChanged += OnModelChanged;
        };
    }

    // 打开时焦点落在层内的关闭按钮，关闭后回到入口按钮。
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MusicPageModel.IsGameOpen) || watched is null) return;
        // 层的可见性随绑定更新，等布局完成后再移焦点。
        var open = watched.IsGameOpen;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (open) GameLayerClose.Focus(NavigationMethod.Tab);
            else GameLayerButton.Focus(NavigationMethod.Tab);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnGameLayerOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.CloseGameCommand.Execute(null);
        e.Handled = true;
    }

    private void OnGameLayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is not { IsGameOpen: true }) return;
        watched.CloseGameCommand.Execute(null);
        e.Handled = true;
    }
}
