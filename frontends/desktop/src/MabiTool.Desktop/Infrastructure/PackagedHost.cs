using System.Diagnostics;
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

    internal static string FailureMessage(int exitCode) => exitCode switch
    {
        3 => "后端未能启动（退出码 3）：数据目录无法使用，可能是程序文件夹不可写、数据正被占用、需要恢复或配置冲突。" + Diagnose,
        4 => "后端未能启动（退出码 4）：上次更新尚未完成恢复。" + Diagnose,
        _ => $"后端未能启动（退出码 {exitCode}）。" + Diagnose
    };

    private const string Diagnose = @"在程序文件夹运行 components\core\launch-backend.cmd -Wait 可查看具体原因。";

    private static string? Configured(string variable) =>
        System.Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? Path.GetFullPath(value) : null;
}
