using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>
/// 导出排查包（需要 diagnostics.export）：范围是最近一次调试采集的会话或全部可见会话；导出文档含文字详情时，
/// 在写出文件前提示。文档在内存中生成，用户在系统保存对话框里选择位置后才写盘。
/// </summary>
public sealed class DiagnosticsExportSection : ObservableObject
{
    private readonly IDesktopBackend backend;
    private readonly DiagnosticsCaptureSection capture;
    private bool chooseLatest = true;
    private bool busy;
    private double progress;
    private string message = string.Empty;
    private bool failed;
    private bool confirmOpen;
    private DiagnosticExportReply? pending;
    private bool disposed;

    public DiagnosticsExportSection(IDesktopBackend backend, DiagnosticsCaptureSection capture)
    {
        this.backend = backend;
        this.capture = capture;
        SelectLatestCommand = new RelayCommand(_ => { chooseLatest = true; Raise(string.Empty); });
        SelectAllCommand = new RelayCommand(_ => { chooseLatest = false; Raise(string.Empty); });
        ExportCommand = new RelayCommand(_ => _ = ExportAsync(), _ => CanExport);
        ConfirmCommand = new RelayCommand(_ => _ = ConfirmAsync());
        CancelConfirmCommand = new RelayCommand(_ => CancelConfirm());
        capture.PropertyChanged += (_, _) =>
        {
            Raise(nameof(LatestAvailable));
            Raise(nameof(UseLatest));
            Raise(nameof(UseAll));
        };
    }

    /// <summary>由页面提供：弹出系统保存对话框并写出文件；返回保存后的文件名，用户取消时为空。</summary>
    public Func<string, byte[], Task<string?>>? FileSaver { get; set; }

    public ICommand SelectLatestCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelConfirmCommand { get; }

    /// <summary>最近一次采集已结束时才能按它导出（进行中的会话仍在写入）。</summary>
    public bool LatestAvailable => capture.Current is { Active: false };

    public bool UseLatest => chooseLatest && LatestAvailable;
    public bool UseAll => !UseLatest;
    public bool CanExport => !busy && !confirmOpen && backend.GetDiagnosticAccess() is { Connected: true, CanExport: true };
    public bool Busy => busy;
    public double Progress => progress;
    public string ProgressText => Pick($"正在导出 {(int)(progress * 100)}%", $"正在匯出 {(int)(progress * 100)}%");
    public string Message => message;
    public bool HasMessage => message.Length > 0;
    public bool Failed => failed;
    public bool ConfirmOpen => confirmOpen;

    public string ConfirmText => Pick(
        "导出内容包含文字详情：异常消息、堆栈和 Worker 输出尾部。后端已替换本机路径、去除令牌形长串和网络地址，但自由文本无法保证完全不含个人信息，发送前请先确认。原始网络包、凭据、乐谱内容不在其中。",
        "匯出內容包含文字詳情：例外訊息、堆疊和 Worker 輸出尾部。後端已替換本機路徑、去除權杖形長串和網路位址，但自由文字無法保證完全不含個人資訊，傳送前請先確認。原始網路封包、憑證、樂譜內容不在其中。");

    public async Task ExportAsync()
    {
        if (!CanExport) return;
        // 已知所选采集带文字详情时，导出前先提示；范围是全部会话时，以导出文档的 containsDetail 为准，写出文件前再提示。
        if (UseLatest && capture.Current is { IncludeDetail: true })
        {
            pending = null;
            SetConfirm(true);
            return;
        }
        await RunAsync(confirmed: false);
    }

    private async Task ConfirmAsync()
    {
        var saved = pending;
        pending = null;
        SetConfirm(false);
        if (saved is not null) await SaveAsync(saved);
        else await RunAsync(confirmed: true);
    }

    private void CancelConfirm()
    {
        pending = null;
        SetConfirm(false);
        message = Pick("已取消导出", "已取消匯出");
        failed = false;
        Raise(string.Empty);
    }

    private void SetConfirm(bool open)
    {
        confirmOpen = open;
        Raise(string.Empty);
        (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task RunAsync(bool confirmed)
    {
        busy = true;
        progress = 0;
        message = string.Empty;
        failed = false;
        Raise(string.Empty);
        (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        var report = new Progress<double>(value =>
        {
            if (disposed || !busy) return;
            progress = value;
            Raise(nameof(Progress));
            Raise(nameof(ProgressText));
        });
        DiagnosticExportReply reply;
        try
        {
            reply = await backend.ExportDiagnosticsAsync(UseLatest ? DiagnosticExportScope.LatestCapture : DiagnosticExportScope.AllSessions,
                UseLatest ? capture.Current?.SessionId : null, report, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reply = new(false, exception.Message, null, false, string.Empty);
        }
        if (disposed) return;
        busy = false;
        if (reply is not { Ok: true, Content: not null })
        {
            message = DiagnosticsText.Error(reply.Message);
            failed = true;
            Raise(string.Empty);
            (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        else if (reply.ContainsDetail && !confirmed)
        {
            pending = reply;
            SetConfirm(true);
        }
        else await SaveAsync(reply);
    }

    private async Task SaveAsync(DiagnosticExportReply reply)
    {
        try
        {
            var name = FileSaver is null || reply.Content is null ? null : await FileSaver(reply.FileName, reply.Content);
            message = name is null ? Pick("已取消保存，导出文件没有写出", "已取消儲存，匯出檔案沒有寫出") : Pick($"已保存：{name}", $"已儲存：{name}");
            failed = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            message = Pick($"保存失败：{exception.Message}", $"儲存失敗：{exception.Message}");
            failed = true;
        }
        Raise(string.Empty);
        (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void Dispose() => disposed = true;
}
