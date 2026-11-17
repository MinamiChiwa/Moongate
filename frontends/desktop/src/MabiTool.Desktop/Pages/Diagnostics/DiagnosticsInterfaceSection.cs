using System.Windows.Input;
using Avalonia.Threading;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Overlay;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>
/// 界面诊断：浮窗拖动记录的开关（默认关闭，只在本次运行内有效）与记录文件位置，
/// 以及界面上出现过、还没有中文文案的内部标识（<see cref="CodeText.Unmapped"/>），便于补齐文案。不需要连接后端。
/// </summary>
public sealed class DiagnosticsInterfaceSection : ObservableObject, IDisposable
{
    public DiagnosticsInterfaceSection()
    {
        ToggleDragTraceCommand = new RelayCommand(_ => OverlayDragTrace.Enabled = !OverlayDragTrace.Enabled);
        OverlayDragTrace.Changed += OnChanged;
        CodeText.UnmappedChanged += OnChanged;
    }

    public ICommand ToggleDragTraceCommand { get; }

    public bool DragTraceEnabled => OverlayDragTrace.Enabled;

    public string DragTraceLabel => DragTraceEnabled
        ? Pick("浮窗拖动记录：开", "浮窗拖動紀錄：開")
        : Pick("浮窗拖动记录：关", "浮窗拖動紀錄：關");

    public string DragTraceHint => DragTraceEnabled
        ? Pick($"已开启。复现一次拖动后，把这个文件发给开发者：{OverlayDragTrace.FilePath}",
            $"已開啟。重現一次拖動後，把這個檔案傳給開發者：{OverlayDragTrace.FilePath}")
        : Pick("开启后每次拖动浮窗元件都记录显示器、浮窗窗口范围与光标坐标，不记录屏幕内容；关闭程序后自动恢复为关闭。",
            "開啟後每次拖動浮窗元件都記錄顯示器、浮窗視窗範圍與游標座標，不記錄螢幕內容；關閉程式後自動恢復為關閉。");

    /// <summary>未登记文案的标识，每行一个“类别:原文”；没有时为空。</summary>
    public string UnmappedText => string.Join(Environment.NewLine, CodeText.Unmapped);
    public bool HasUnmapped => CodeText.Unmapped.Count > 0;

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Raise(string.Empty));

    public void Dispose()
    {
        OverlayDragTrace.Changed -= OnChanged;
        CodeText.UnmappedChanged -= OnChanged;
    }
}
