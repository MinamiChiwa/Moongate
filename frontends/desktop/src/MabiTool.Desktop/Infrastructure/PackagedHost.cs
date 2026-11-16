using System.Diagnostics;
using MabiTool.Desktop.Localization;
using MabiTool.ModuleSdk;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>
/// 目录包里的前端（<c>&lt;根&gt;/components/desktop</c>）在启动时按需拉起同一工具根的宿主：宿主未运行时用随包运行时启动
/// <c>components/core/MabiTool.Host.dll</c>，工具根（包根下的 userdata）还没有引导配置时加 <c>--initialize-data</c> 完成首次初始化；宿主已在运行时只连接。
/// 宿主是独立进程，前端关闭后继续运行，供其他前端与浮窗共用。显式指定工具根、发现文件或样本时不启动宿主。
/// </summary>
internal static class PackagedHost
{
    /// <summary>宿主在这段时间内退出且退出码非零时，连接状态写明启动失败。</summary>
    internal static readonly TimeSpan StartupWatch = TimeSpan.FromSeconds(30);

    /// <param name="PackageRoot">包根，也是宿主的工作目录；工具根是包根下的 userdata（旧布局为包根本身）。</param>
    internal sealed record Launch(string Runtime, string HostAssembly, string PackageRoot, bool InitializeData,
        IReadOnlyDictionary<string, string> Environment, string HostLock);

    /// <summary>需要启动宿主时返回启动参数；不是目录包布局、显式指定了连接目标或宿主已在运行时返回 null。</summary>
    internal static Launch? Plan(IReadOnlyList<string> args, string toolRoot, string programDirectory)
    {
        if (args.Contains(ToolRoot.Argument) || args.Contains("--instance") || args.Contains("--sample")) return null;
        if (ToolRoot.PackageRoot(programDirectory) is not { } package ||
            !string.Equals(ToolRoot.FromProgramDirectory(programDirectory), toolRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var components = Path.Combine(package, "components");
        var runtime = Path.Combine(components, "dotnet-runtime", "dotnet.exe");
        var host = Path.Combine(components, "core", "MabiTool.Host.dll");
        if (!File.Exists(runtime) || !File.Exists(host)) return null;
        var bootstrap = Configured("MABITOOL_BOOTSTRAP_DIRECTORY") ?? ToolRoot.Bootstrap(toolRoot);
        var hostLock = Path.Combine(Configured("MABITOOL_RUNTIME_DIRECTORY") ?? ToolRoot.Runtime(toolRoot), "host.lock");
        if (IsHostRunning(hostLock)) return null;
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_ROOT"] = Path.GetDirectoryName(runtime)!, ["DOTNET_ROOT_X64"] = Path.GetDirectoryName(runtime)!,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0"
        };
        // 与 components/core/launch-backend.ps1 相同：组件存在时把可选 Worker 固定为包内路径。
        var observation = Path.Combine(components, "observation-worker", "MabiTool.Observation.Worker.exe");
        if (File.Exists(observation)) environment["MABITOOL_OBSERVATION_EXECUTABLE"] = observation;
        var color = Path.Combine(components, "color-engine");
        if (File.Exists(Path.Combine(color, "python", "python.exe")))
        {
            environment["MABITOOL_COLOR_PYTHON"] = Path.Combine(color, "python", "python.exe");
            environment["MABITOOL_COLOR_WORKER_DIRECTORY"] = color;
            environment["MABITOOL_COLOR_TESSERACT_DIRECTORY"] = Path.Combine(color, "tesseract");
        }
        // 旧默认位置有数据时宿主先迁入并写出引导配置，此时初始化参数不再生效。
        return new(runtime, host, package, !File.Exists(Path.Combine(bootstrap, "data-root.json")), environment, hostLock);
    }

    /// <summary>宿主运行期间独占 <c>runtime/host.lock</c>；能以不共享方式打开说明没有宿主在用这个工具根。</summary>
    internal static bool IsHostRunning(string hostLock)
    {
        if (!File.Exists(hostLock)) return false;
        try
        {
            using var probe = new FileStream(hostLock, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    internal static ProcessStartInfo StartInfo(Launch launch)
    {
        var start = new ProcessStartInfo(launch.Runtime)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = launch.PackageRoot
        };
        start.ArgumentList.Add(launch.HostAssembly);
        if (launch.InitializeData) start.ArgumentList.Add("--initialize-data");
        foreach (var (name, value) in launch.Environment) start.Environment[name] = value;
        return start;
    }

    /// <summary>需要时启动宿主，并在后台观察启动结果；失败原因写进连接状态。</summary>
    internal static void StartIfNeeded(IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows() || DesktopFiles.Problem is not null) return;
        var launch = Plan(args, DesktopFiles.Root, AppContext.BaseDirectory);
        if (launch is null) return;
        Process process;
        try { process = Process.Start(StartInfo(launch)) ?? throw new IOException("宿主进程未能创建"); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DesktopFiles.ReportProblem($"后端未能启动：{error.Message}");
            return;
        }
        _ = WatchAsync(process, launch.HostLock);
    }

    private static async Task WatchAsync(Process process, string hostLock)
    {
        using (process)
        {
            using var timeout = new CancellationTokenSource(StartupWatch);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            // 同时双击两次时后启动的宿主因数据被占用退出，另一宿主正常运行，不算失败。
            if (process.ExitCode != 0 && !IsHostRunning(hostLock)) DesktopFiles.ReportProblem(FailureMessage(process.ExitCode));
        }
    }

    /// <summary>宿主退出码到连接状态说明；退出码的分类见宿主 <c>HostStartupFailure</c>。</summary>
    internal static string FailureMessage(int exitCode) => exitCode switch
    {
        3 => UiText.Pick("后端未能启动（退出码 3）：数据目录无法使用，可能是程序文件夹不可写、数据正被占用、需要恢复或配置冲突。",
            "後端未能啟動（結束代碼 3）：資料目錄無法使用，可能是程式資料夾不可寫入、資料正被佔用、需要復原或設定衝突。") + Diagnose,
        4 => UiText.Pick("后端未能启动（退出码 4）：上次更新尚未完成恢复。", "後端未能啟動（結束代碼 4）：上次更新尚未完成復原。") + Diagnose,
        5 => UiText.Pick(
            $"后端未能启动（退出码 5）：本机端口 {Port} 已被占用，通常是另一个文件夹里的 Moongate 仍在运行。" +
            "请先退出那一份：在它的设置页按“退出 Moongate（含后台服务）”，或在任务管理器的“详细信息”里结束命令行含 MabiTool.Host 的 dotnet.exe；然后重新打开本程序。",
            $"後端未能啟動（結束代碼 5）：本機連接埠 {Port} 已被佔用，通常是另一個資料夾裡的 Moongate 仍在執行。" +
            "請先結束那一份：在它的設定頁按「結束 Moongate（含背景服務）」，或在工作管理員的「詳細資料」裡結束命令列含 MabiTool.Host 的 dotnet.exe；然後重新開啟本程式。"),
        6 => UiText.Pick(
            $"后端未能启动（退出码 6）：本机端口 {Port} 被其他程序占用或被系统保留，请关闭占用该端口的程序后重新打开。",
            $"後端未能啟動（結束代碼 6）：本機連接埠 {Port} 被其他程式佔用或被系統保留，請關閉佔用該連接埠的程式後重新開啟。") + Diagnose,
        7 => UiText.Pick("后端未能启动（退出码 7）：未知启动错误。", "後端未能啟動（結束代碼 7）：未知啟動錯誤。") + Diagnose,
        _ => UiText.Pick($"后端未能启动（退出码 {exitCode}）。", $"後端未能啟動（結束代碼 {exitCode}）。") + Diagnose
    };

    private static string Diagnose => UiText.Pick(@"在程序文件夹运行 components\core\launch-backend.cmd -Wait 可查看具体原因。",
        @"在程式資料夾執行 components\core\launch-backend.cmd -Wait 可查看具體原因。");

    /// <summary>宿主监听的本机端口：与宿主相同，取 MABITOOL_API_PORT，未设置时为 19998。</summary>
    private static string Port => System.Environment.GetEnvironmentVariable("MABITOOL_API_PORT") is { Length: > 0 } port ? port : "19998";

    private static string? Configured(string variable) =>
        System.Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? Path.GetFullPath(value) : null;
}
