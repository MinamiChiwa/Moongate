using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MabiTool.Desktop.Localization;
using MabiTool.ModuleSdk;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>
/// 目录包里的前端（<c>&lt;根&gt;/components/desktop</c>）与同一工具根的宿主一起启动、一起退出：
/// 宿主未运行时用随包运行时启动 <c>components/core/MabiTool.Host.dll</c>，并传入本前端的进程号（<c>--owner-process</c>），
/// 宿主在本前端结束（关闭、崩溃或被结束）后按正常停止流程退出；工具根（包根下的 userdata）还没有引导配置时加 <c>--initialize-data</c>。
/// 同一工具根只运行一个前端，再次打开时把已有窗口提到前台。宿主中途停止时自动重新启动一次。
/// 宿主为安装更新交出控制（工具根有 <c>update/restarting</c> 标记）后本前端退出，不再启动宿主，由替换器换入新版本后重新打开。
/// 显式指定工具根、发现文件或样本时不启动宿主，也不负责结束已在运行的宿主（开发与复验方式）。
/// </summary>
internal static class PackagedHost
{
    /// <summary>宿主在这段时间内以非零码退出视为启动失败：写明原因，不自动重试。</summary>
    internal static readonly TimeSpan StartupWatch = TimeSpan.FromSeconds(30);

    /// <summary>别的宿主（例如上一个前端留下、正在退出的宿主，或单独启动的宿主）占用工具根时，隔这么久检查一次它是否已退出。</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <param name="PackageRoot">包根，也是宿主的工作目录；工具根是包根下的 userdata（旧布局为包根本身）。</param>
    /// <param name="OwnerProcessId">拉起宿主的前端进程号；宿主在该进程结束后退出。</param>
    internal sealed record Launch(string Runtime, string HostAssembly, string PackageRoot, bool InitializeData,
        IReadOnlyDictionary<string, string> Environment, string HostLock, int OwnerProcessId);

    /// <summary>宿主停止或未能启动的说明；宿主正常运行时为空。连接状态在未连接时显示它。</summary>
    internal static string? Problem { get => Volatile.Read(ref problem); private set => Volatile.Write(ref problem, value); }

    /// <summary>宿主启动失败的短句（状态行）与做法（操作提示）；按宿主写到标准错误的启动失败记录给出，宿主正常运行时为空。</summary>
    internal static StartupFailureText? Failure { get => Volatile.Read(ref failure); private set => Volatile.Write(ref failure, value); }

    private static string? problem;
    private static StartupFailureText? failure;
    private static readonly object launchGate = new();
    private static readonly List<HostLaunchRecord> launches = [];

    /// <summary>本次运行里宿主每次启动的记录（至多 8 条）：启动时刻、退出码、运行时长、启动失败记录与标准错误尾部；供“导出启动记录”。</summary>
    internal static IReadOnlyList<HostLaunchRecord> Launches { get { lock (launchGate) return launches.ToArray(); } }

    /// <summary>另一次打开 Moongate 请求把已有窗口提到前台。在后台线程触发。</summary>
    internal static event Action? ActivationRequested;

    private static Mutex? instance;
    private static CancellationTokenSource? supervision;

    /// <summary>目录包布局且没有显式指定连接目标时返回包根，否则为空。</summary>
    internal static string? PackageOf(IReadOnlyList<string> args, string toolRoot, string programDirectory)
    {
        if (args.Contains(ToolRoot.Argument) || args.Contains("--instance") || args.Contains("--sample")) return null;
        if (ToolRoot.PackageRoot(programDirectory) is not { } package ||
            !string.Equals(ToolRoot.FromProgramDirectory(programDirectory), toolRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var components = Path.Combine(package, "components");
        return File.Exists(Path.Combine(components, "dotnet-runtime", "dotnet.exe")) &&
               File.Exists(Path.Combine(components, "core", "MabiTool.Host.dll")) ? package : null;
    }

    /// <summary>需要启动宿主时返回启动参数；不是目录包布局、显式指定了连接目标或宿主已在运行时返回 null。</summary>
    internal static Launch? Plan(IReadOnlyList<string> args, string toolRoot, string programDirectory)
    {
        if (PackageOf(args, toolRoot, programDirectory) is not { } package) return null;
        var components = Path.Combine(package, "components");
        var runtime = Path.Combine(components, "dotnet-runtime", "dotnet.exe");
        var host = Path.Combine(components, "core", "MabiTool.Host.dll");
        var bootstrap = Configured("MABITOOL_BOOTSTRAP_DIRECTORY") ?? ToolRoot.Bootstrap(toolRoot);
        var hostLock = HostLock(toolRoot);
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
        return new(runtime, host, package, !File.Exists(Path.Combine(bootstrap, "data-root.json")), environment, hostLock,
            System.Environment.ProcessId);
    }

    private static string HostLock(string toolRoot) =>
        Path.Combine(Configured("MABITOOL_RUNTIME_DIRECTORY") ?? ToolRoot.Runtime(toolRoot), "host.lock");

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
        // 标准错误由前端读取：宿主启动失败时在那里写原因与启动失败记录（HostStartupReport），工具根不可写时也能送达。
        var start = new ProcessStartInfo(launch.Runtime)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = launch.PackageRoot,
            RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(launch.HostAssembly);
        if (launch.InitializeData) start.ArgumentList.Add("--initialize-data");
        start.ArgumentList.Add("--owner-process");
        start.ArgumentList.Add(launch.OwnerProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (name, value) in launch.Environment) start.Environment[name] = value;
        return start;
    }

    /// <summary>
    /// 目录包里同一工具根只运行一个前端。已有前端在运行时请它把窗口提到前台并返回 false，本进程随即退出；
    /// 这样宿主只需跟随一个前端进程，浮窗、迷你窗与明细窗口都属于这一个进程。开发与复验方式不受限制。
    /// </summary>
    internal static bool ClaimInstance(IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var toolRoot = ToolRoot.FromProgramDirectory(AppContext.BaseDirectory);
        if (PackageOf(args, toolRoot, AppContext.BaseDirectory) is null) return true;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(toolRoot).ToUpperInvariant())))[..32];
        var activation = @"Local\Moongate-desktop-activate-" + key;
        instance = new Mutex(false, @"Local\Moongate-desktop-" + key);
        bool owned;
        try { owned = instance.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            instance.Dispose();
            instance = null;
            if (EventWaitHandle.TryOpenExisting(activation, out var existing))
            {
                // 刚由用户打开的进程有前台权限，交给已有前端以便它把窗口提到前台。
                _ = AllowSetForegroundWindow(AnyProcess);
                using (existing) existing.Set();
            }
            return false;
        }
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, activation);
        new Thread(() =>
        {
            while (signal.WaitOne()) ActivationRequested?.Invoke();
        }) { IsBackground = true, Name = "Moongate activation" }.Start();
        return true;
    }

    /// <summary>目录包布局下开始照看宿主：未运行时启动，中途停止时按 <see cref="AfterExit"/> 处理；前端退出时停止照看。</summary>
    internal static void Start(IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows() || DesktopFiles.Problem is not null) return;
        if (PackageOf(args, DesktopFiles.Root, AppContext.BaseDirectory) is null) return;
        supervision = new CancellationTokenSource();
        _ = SuperviseAsync(args, supervision.Token);
    }

    /// <summary>前端退出：不再重新启动宿主。宿主在本进程结束后自行退出。</summary>
    internal static void Stop() => supervision?.Cancel();

    private static async Task SuperviseAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var relaunched = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (UpdateRestarting(DesktopFiles.Root))
                {
                    RequestExit();
                    return;
                }
                var launch = Plan(args, DesktopFiles.Root, AppContext.BaseDirectory);
                if (launch is null)
                {
                    // 工具根已有宿主在运行：照常连接，它退出后由本前端启动自己的宿主。
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                    continue;
                }
                Process process;
                var record = new HostLaunchRecord(DateTimeOffset.Now);
                try
                {
                    process = new Process { StartInfo = StartInfo(launch) };
                    process.ErrorDataReceived += (_, line) => record.Error(line.Data);
                    if (!process.Start()) throw new IOException("宿主进程未能创建");
                    process.BeginErrorReadLine();
                }
                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    record.Finish(null, TimeSpan.Zero, error.Message);
                    Remember(record);
                    Problem = UiText.Pick("后端未能启动：", "後端未能啟動：") + Localization.CodeText.Error(error);
                    return;
                }
                Remember(record);
                using (process)
                {
                    var running = Stopwatch.StartNew();
                    var exited = process.WaitForExitAsync(token);
                    if (await Task.WhenAny(exited, Task.Delay(StartupWatch, token)).ConfigureAwait(false) != exited) { Problem = null; Failure = null; }
                    await exited.ConfigureAwait(false);
                    record.Finish(process.ExitCode, running.Elapsed, null);
                    // 同时启动的另一宿主已在运行（本宿主因数据被占用退出）：连接那一个，它退出后再启动。
                    // 宿主为安装更新退出时由循环开头的检查让本前端退出。
                    if (IsHostRunning(launch.HostLock) || UpdateRestarting(DesktopFiles.Root)) continue;
                    var (relaunch, problem) = AfterExit(process.ExitCode, running.Elapsed, relaunched, record.Report);
                    Problem = problem;
                    Failure = process.ExitCode != 0 && running.Elapsed < StartupWatch ? StartupFailureText.For(process.ExitCode, record.Report) : null;
                    if (!relaunch) return;
                    relaunched = true;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>宿主已把安装交给替换器：前端随宿主退出，让替换器能改名程序文件。</summary>
    internal static bool UpdateRestarting(string toolRoot) => File.Exists(Path.Combine(toolRoot, "update", "restarting"));

    private static void RequestExit() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown());

    /// <summary>
    /// 本前端运行期间宿主退出后的处理。启动阶段以非零码退出是启动失败，写明原因、不重试，以免反复崩溃；
    /// 运行中停止（崩溃或被结束）自动重新启动一次，再次停止时写明后端已停止并请用户重新打开 Moongate。
    /// </summary>
    /// <param name="report">宿主写到标准错误的启动失败记录；有记录时按其原因给出针对性的说明。</param>
    internal static (bool Relaunch, string Problem) AfterExit(int exitCode, TimeSpan ranFor, bool relaunched, HostStartupReport? report = null)
    {
        if (exitCode != 0 && ranFor < StartupWatch)
            return (false, report is not null && exitCode == 3 ? StartupFailureText.For(exitCode, report).Line : FailureMessage(exitCode));
        return relaunched
            ? (false, UiText.Pick("后端再次停止，请重新打开 Moongate。", "後端再次停止，請重新開啟 Moongate。") + Diagnose)
            : (true, UiText.Pick("后端已停止，正在自动重启。", "後端已停止，正在自動重新啟動。"));
    }

    /// <summary>宿主退出码到连接状态说明；退出码的分类见宿主 <c>HostStartupFailure</c>。退出码本身只在诊断记录里，不写进说明。</summary>
    internal static string FailureMessage(int exitCode) => exitCode switch
    {
        3 => UiText.Pick("后端未能启动：数据目录不可用。", "後端未能啟動：資料目錄不可用。") + Diagnose,
        4 => UiText.Pick("后端未能启动：上次更新未完成恢复。", "後端未能啟動：上次更新未完成復原。") + Diagnose,
        5 => UiText.Pick(
            $"后端未能启动：端口 {Port} 被占用，通常是另一份 Moongate 仍在运行。请关闭那一份，或在任务管理器结束命令行含 MabiTool.Host 的 dotnet.exe。",
            $"後端未能啟動：連接埠 {Port} 被佔用，通常是另一份 Moongate 仍在執行。請關閉那一份，或在工作管理員結束命令列含 MabiTool.Host 的 dotnet.exe。"),
        // 默认端口被其他程序占用或被系统保留时宿主自动改用空闲端口，退出码 6 只出现在用 MABITOOL_API_PORT 指定了端口的情形。
        6 => UiText.Pick($"后端未能启动：指定端口 {Port} 不可用，请清除 MABITOOL_API_PORT。", $"後端未能啟動：指定連接埠 {Port} 不可用，請清除 MABITOOL_API_PORT。") + Diagnose,
        _ => UiText.Pick("后端未能启动。", "後端未能啟動。") + Diagnose
    };

    private static string Diagnose => UiText.Pick("可在诊断页导出启动记录。", "可在診斷頁匯出啟動記錄。");

    private static void Remember(HostLaunchRecord record)
    {
        lock (launchGate)
        {
            launches.Add(record);
            if (launches.Count > 8) launches.RemoveAt(0);
        }
    }

    /// <summary>
    /// 端口冲突说明里的端口：与宿主相同，取 MABITOOL_API_PORT，未设置时为宿主的默认端口 19998。
    /// 宿主实际监听的地址以发现文件为准（可能是系统分配的端口），连接不使用这个值。
    /// </summary>
    private static string Port => System.Environment.GetEnvironmentVariable("MABITOOL_API_PORT") is { Length: > 0 } port ? port : "19998";

    private static string? Configured(string variable) =>
        System.Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? Path.GetFullPath(value) : null;

    private const uint AnyProcess = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
