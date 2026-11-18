using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Pages.Diagnostics;

public sealed partial class DiagnosticsPage : UserControl
{
    private DiagnosticsPageModel? watched;

    public DiagnosticsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Detach();
            watched = DataContext as DiagnosticsPageModel;
            if (watched is null) return;
            watched.Runtime.ClipboardWriter = WriteClipboardAsync;
            watched.Export.FileSaver = SaveFileAsync;
            watched.Startup.FileSaver = SaveFileAsync;
            watched.Capture.LiveRows.CollectionChanged += OnLiveChanged;
            watched.Export.PropertyChanged += OnExportChanged;
            if (((Avalonia.Visual)this).IsAttachedToVisualTree()) watched.StartPolling();
        };
        AttachedToVisualTree += (_, _) => watched?.StartPolling();
        DetachedFromVisualTree += (_, _) => watched?.StopPolling();
    }

    private void Detach()
    {
        if (watched is null) return;
        watched.StopPolling();
        watched.Capture.LiveRows.CollectionChanged -= OnLiveChanged;
        watched.Export.PropertyChanged -= OnExportChanged;
        watched = null;
    }

    private Task WriteClipboardAsync(string text) =>
        TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;

    // 系统保存对话框由用户主动的“导出”触发；选定位置后才写出文件。
    private async Task<string?> SaveFileAsync(string suggestedName, byte[] content)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) throw new InvalidOperationException("没有可用的保存对话框");
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });
        if (file is null) return null;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(content);
        return file.Name;
    }

    // 已经停在底部时，新记录到来后继续跟随。
    private void OnLiveChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var scroller = LiveScroller;
        var atEnd = scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 24;
        if (atEnd) Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Loaded);
    }

    // 确认层打开时焦点落在“继续导出”，关闭后回到导出按钮。
    private void OnExportChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (watched is null || e.PropertyName is not (nameof(DiagnosticsExportSection.ConfirmOpen) or "")) return;
        var open = watched.Export.ConfirmOpen;
        Dispatcher.UIThread.Post(() =>
        {
            if (open) ConfirmExportButton.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void OnConfirmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is not { Export.ConfirmOpen: true }) return;
        watched.Export.CancelConfirmCommand.Execute(null);
        e.Handled = true;
    }
}
