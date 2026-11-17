using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MabiTool.PortableUpdate;

namespace MabiTool.PortableLauncher;

/// <summary>
/// 解压版包根的 Moongate.exe：以随包 .NET 运行时打开 <c>components/desktop</c> 里的桌面前端，参数原样转交；
/// 宿主由前端按需启动并在首次运行时初始化数据。启动前先检查上一次更新是否留下未完成的事务，按日志回退或收尾。
/// 带 <c>--apply-update &lt;事务&gt; &lt;包根&gt;</c> 时作为替换器运行（宿主把新版 Moongate.exe 复制到工具根的事务目录后启动它）。
/// 无控制台窗口，失败时弹出中文消息框。
/// </summary>
internal static class Program
{
    /// <summary>前端在这段时间内以非零码退出视为启动失败（运行时无法加载、程序文件损坏等）。</summary>
    private const int StartupWatchMilliseconds = 5000;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--apply-update") return ApplyUpdate(args[1], args[2]);
        var packageRoot = AppDomain.CurrentDomain.BaseDirectory;
        var toolRoot = UpdateLayout.ToolRootOfPackage(packageRoot);
        if (Directory.Exists(UpdateLayout.Root(toolRoot)))
        {
            if (UpdateLayout.ReplacerActive(toolRoot))
            {
                Inform("Moongate 正在安装更新，完成后会自动打开，请稍候。");
                return 0;
            }
            var recovered = Recover(packageRoot, toolRoot);
            if (recovered is not null) return recovered.Value;
        }
        return Launch(packageRoot, args, watch: true);
    }

    private static int? Recover(string packageRoot, string toolRoot)
    {
        try
        {
            using var ownership = new FileStream(UpdateLayout.ReplacerLock(toolRoot), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var replacer = new UpdateReplacer(packageRoot, toolRoot, new SystemReplacerEnvironment());
            return replacer.Recover() switch
            {
                ReplacerOutcome.RolledBack => Inform("上次更新没有完成：" + replacer.FailureReason),
                ReplacerOutcome.RollbackFailed => Fail(ManualUpgrade(replacer.FailureReason)),
                _ => null
            };
        }
        catch (IOException)
        {
            Inform("Moongate 正在安装更新，完成后会自动打开，请稍候。");
            return 0;
        }
    }

    private static int ApplyUpdate(string transactionId, string packageRoot)
    {
        if (!UpdateLayout.IsTransactionId(transactionId) || !Path.IsPathRooted(packageRoot)) return 2;
        var toolRoot = UpdateLayout.ToolRootOfPackage(packageRoot);
        FileStream ownership;
        try { ownership = new FileStream(UpdateLayout.ReplacerLock(toolRoot), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return 3; }
        using (ownership)
        {
            var replacer = new UpdateReplacer(packageRoot, toolRoot, new SystemReplacerEnvironment());
            return replacer.Apply(transactionId) switch
            {
                ReplacerOutcome.Committed or ReplacerOutcome.None => 0,
                ReplacerOutcome.RolledBack => Inform("更新没有完成：" + replacer.FailureReason),
                ReplacerOutcome.Aborted => Inform("更新没有安装：" + replacer.FailureReason),
                _ => Fail(ManualUpgrade(replacer.FailureReason))
            };
        }
    }

    /// <summary>新旧版本都无法自动启动时给出手动升级步骤；用户数据在 userdata 里，不随程序文件替换。</summary>
    private static string ManualUpgrade(string? reason) =>
        "Moongate 更新失败，旧版本也没有自动恢复。" + (reason is null ? "" : "\n" + reason) +
        "\n\n请手动升级：\n1. 从 https://github.com/MinamiChiwa/Moongate/releases 下载最新的 Moongate 压缩包并解压到一个新文件夹；" +
        "\n2. 关闭 Moongate 后，把旧文件夹里的 userdata 文件夹整个移到新文件夹里（与新的 Moongate.exe 放在一起）；" +
        "\n3. 运行新文件夹里的 Moongate.exe。\n\n旧版本的程序文件保存在 userdata\\update\\previous 里，确认新版本正常后可以删除旧文件夹。";

    internal static int Launch(string packageRoot, string[] args, bool watch)
    {
        var plan = LaunchPlan.Create(packageRoot, Environment.Is64BitOperatingSystem);
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
            if (watch && process.WaitForExit(StartupWatchMilliseconds) && process.ExitCode != 0) return Fail(LaunchPlan.ExitMessage(process.ExitCode));
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

    private static int Inform(string message)
    {
        MessageBoxW(IntPtr.Zero, message, LaunchPlan.Title, 0x40 /* MB_ICONINFORMATION */);
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
