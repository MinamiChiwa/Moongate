using System.Windows.Input;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>
/// 导出启动记录：不依赖后端，后端没有启动时也可用。前端把宿主每次启动的退出码与失败原因、工具根是否可写、磁盘与文件系统、
/// 是否以管理员身份运行、Windows 与程序版本写成一个文本文件（用户名、计算机名等已替换），保存位置由用户在保存对话框里选择。
/// </summary>
public sealed class DiagnosticsStartupSection : ObservableObject
{
    private string message = string.Empty;
    private bool failed;

    public DiagnosticsStartupSection() => ExportCommand = new RelayCommand(_ => _ = ExportAsync());

    /// <summary>由页面提供：弹出系统保存对话框并写出文件；返回保存后的文件名，用户取消时为空。</summary>
    public Func<string, byte[], Task<string?>>? FileSaver { get; set; }

    public ICommand ExportCommand { get; }
    public string Message => message;
    public bool HasMessage => message.Length > 0;
    public bool Failed => failed;

    public async Task ExportAsync()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var name = FileSaver is null ? null : await FileSaver(StartupRecordExport.FileName(now), StartupRecordExport.Build(now));
            message = name is null ? Pick("已取消保存", "已取消儲存") : Pick($"已保存：{name}", $"已儲存：{name}");
            failed = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            message = Pick("保存失败：", "儲存失敗：") + Localization.CodeText.Error(exception);
            failed = true;
        }
        Raise(string.Empty);
    }
}
