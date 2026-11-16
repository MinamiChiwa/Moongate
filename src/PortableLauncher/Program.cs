using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MabiTool.PortableLauncher;

/// <summary>
/// 解压版包根的 Moongate.exe：以随包 .NET 运行时打开 <c>components/desktop</c> 里的桌面前端，参数原样转交；
/// 宿主由前端按需启动并在首次运行时初始化数据。本程序不写任何文件，无控制台窗口，失败时弹出中文消息框。
/// </summary>
internal static class Program
{
    /// <summary>前端在这段时间内以非零码退出视为启动失败（运行时无法加载、程序文件损坏等）。</summary>
    private const int StartupWatchMilliseconds = 5000;

    [STAThread]
    private static int Main(string[] args)
    {
        var plan = LaunchPlan.Create(AppDomain.CurrentDomain.BaseDirectory, Environment.Is64BitOperatingSystem);
        if (plan.Problem is not null) return Fail(plan.Problem);
        var start = new ProcessStartInfo(plan.Desktop, LaunchPlan.CommandLine(args))
        {
            UseShellExecute = false, WorkingDirectory = plan.PackageRoot
        };
        foreach (var pair in plan.Environment) start.EnvironmentVariables[pair.Key] = pair.Value;
        try
        {
            using var process = Process.Start(start);
            if (process is null) return Fail("桌面前端未能启动。");
            if (process.WaitForExit(StartupWatchMilliseconds) && process.ExitCode != 0) return Fail(LaunchPlan.ExitMessage(process.ExitCode));
            return 0;
        }
        catch (Win32Exception error)
        {
            return Fail($"桌面前端未能启动：{error.Message}\n\n可能被安全软件拦截，或程序文件已损坏；请重新完整解压下载的压缩包后再试。");
        }
    }

    private static int Fail(string message)
    {
        MessageBoxW(IntPtr.Zero, message, LaunchPlan.Title, 0x10 /* MB_ICONERROR */);
        return 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
