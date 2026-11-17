using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MabiTool.PortableUpdate;

namespace MabiTool.PortableLauncher;

/// <summary>替换器的真实系统实现：按进程号与启动时间等待旧进程、经启动计划打开前端、按可执行文件位置结束包内进程。</summary>
internal sealed class SystemReplacerEnvironment : IReplacerEnvironment
{
    public DateTime UtcNow => DateTime.UtcNow;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

    public bool WaitForExit(int processId, long startTimeUtcTicks, TimeSpan timeout)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; }
        using (process)
        {
            try
            {
                // 进程号可能已被新进程复用：启动时间不同就不是要等的那个。
                if (Math.Abs(process.StartTime.ToUniversalTime().Ticks - startTimeUtcTicks) > TimeSpan.TicksPerMillisecond) return true;
                return process.WaitForExit((int)Math.Max(0, Math.Min(int.MaxValue, timeout.TotalMilliseconds)));
            }
            catch (InvalidOperationException) { return true; }
            catch (Win32Exception) { return process.WaitForExit((int)Math.Max(0, Math.Min(int.MaxValue, timeout.TotalMilliseconds))); }
        }
    }

    public void Launch(string packageRoot) => Program.Launch(packageRoot, new string[0], watch: false);

    public void StopPackageProcesses(string packageRoot)
    {
        var components = Path.Combine(Path.GetFullPath(packageRoot), "components") + Path.DirectorySeparatorChar;
        var self = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == self) continue;
                var image = ImagePath(process.Id);
                if (image is null || !image.StartsWith(components, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(10_000);
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32_768);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
