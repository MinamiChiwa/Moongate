using MabiTool.Platform.Windows;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 启动一个扩展进程所需的全部参数：程序、参数、工作目录（扩展安装目录）、环境变量白名单与资源上限。
/// </summary>
public sealed record ExtensionLaunch(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment, long MemoryBytes, int CpuPercent, int ProcessLimit);

/// <summary>一个运行中的扩展进程；标准输入输出承载协议，标准错误进入扩展日志。</summary>
public interface IExtensionProcess : IAsyncDisposable
{
    int Id { get; }
    Stream Input { get; }
    Stream Output { get; }
    TextReader Error { get; }
    Task<int> WaitForExitAsync(CancellationToken ct = default);
    /// <summary>立即结束整个进程树。</summary>
    void Kill();
    long? PeakMemoryBytes { get; }
}

/// <summary>Windows：进程创建时加入私有 Job，宿主退出时由内核收回；Job 限制内存、CPU、进程数与界面句柄访问。</summary>
public sealed class WindowsExtensionProcess(WindowsOwnedChildProcess process) : IExtensionProcess
{
    public static IExtensionProcess Start(ExtensionLaunch launch) => new WindowsExtensionProcess(WindowsOwnedChildProcess.Start(
        launch.Executable, launch.Arguments, new ChildProcessOptions(launch.WorkingDirectory, launch.Environment, RedirectInput: true,
            JobMemoryBytes: launch.MemoryBytes, CpuRatePercent: launch.CpuPercent, ActiveProcessLimit: launch.ProcessLimit,
            RestrictUserInterface: true)));

    public int Id => process.Id;
    public Stream Input => process.StandardInput!;
    public Stream Output => process.StandardOutput.BaseStream;
    public TextReader Error => process.StandardError;
    public long? PeakMemoryBytes => process.PeakJobMemoryBytes;
    public Task<int> WaitForExitAsync(CancellationToken ct = default) => process.WaitForExitAsync(ct);
    public void Kill()
    {
        try { process.Terminate(); } catch (System.ComponentModel.Win32Exception) { }
    }
    public ValueTask DisposeAsync() => process.DisposeAsync();
}
