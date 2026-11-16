using System.Globalization;
using System.Runtime.InteropServices;
using MabiTool.ModuleSdk;
using MabiTool.ObservationDebugTool;

// 统一实机调试工具。--preview 只显示面板与场景计划；正式运行必须带 --confirmed，缺少时不建立宿主或枚举设备。
var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
if (repository is null) { Console.Error.WriteLine("probe.repositoryUnavailable"); return 2; }
var preview = args.SequenceEqual(["--preview"]);
if (!preview && !args.Contains("--confirmed", StringComparer.Ordinal))
{
    Console.Error.WriteLine("capture.confirmationRequired");
    return 2;
}
if (!OperatingSystem.IsWindows()) return 2;
var values = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--confirmed" or "--preview") continue;
    if (args[i] is not ("--client-root" or "--worker" or "--channel-id" or "--python" or "--scenarios") ||
        i + 1 >= args.Length || !values.TryAdd(args[i], args[++i])) { Console.Error.WriteLine("probe.argumentsInvalid"); return 2; }
}
string Absolute(string key)
{
    var value = values[key];
    if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("probe.absolutePathRequired");
    var path = Path.GetFullPath(value); LocalDiscoverySecurity.RejectLinks(path); return path;
}
string clientRoot = "", worker = "";
string? python = null;
if (!preview)
{
    if (!values.ContainsKey("--client-root") || !values.ContainsKey("--worker")) { Console.Error.WriteLine("probe.argumentsInvalid"); return 2; }
    clientRoot = Path.TrimEndingDirectorySeparator(Absolute("--client-root"));
    worker = Absolute("--worker");
    python = values.ContainsKey("--python") ? Absolute("--python") : null;
    if (!Directory.Exists(clientRoot) || !File.Exists(Path.Combine(clientRoot, "MabinogiMobile.exe")) || !File.Exists(worker))
    { Console.Error.WriteLine("probe.pathsInvalid"); return 2; }
}
var script = Path.Combine(repository.FullName, "scripts", "maintenance", "capture-combat-evidence.py");
IRawSampleCapture? samples = python is null ? null : new PythonRawSampleCapture(python, script, clientRoot);
var environment = new NativeGameEnvironment(clientRoot, worker, values.GetValueOrDefault("--channel-id"), samples);
List<IDebugScenario> scenarios =
[
    new CombatScenario(environment),
    new CombatScenario(environment, new(CombatSeconds: 180, Segmentation: new())),
    new CoexistenceScenario(environment),
    new InventoryScenario(environment),
    new ReloginSampleScenario(environment, ct => ReloginSampleScenario.ReadClientLayout(clientRoot, ct)),
    .. RawSampleScenario.Definitions.Select(definition => new RawSampleScenario(definition, samples)),
];
if (values.TryGetValue("--scenarios", out var requested))
{
    var ids = requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (ids.Length == 0 || ids.Any(id => scenarios.All(value => value.Plan.Id != id))) { Console.Error.WriteLine("probe.scenarioUnknown"); return 2; }
    scenarios = ids.Distinct(StringComparer.Ordinal).Select(id => scenarios.Single(value => value.Plan.Id == id)).ToList();
}
var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
var scratchRoot = Path.Combine(repository.FullName, "artifacts", "tmp", "debugtool-" + stamp);
if (!preview) LocalDiscoverySecurity.EnsureDirectory(scratchRoot);
// 工具进程加入关闭即终止的 Job：工具崩溃时 Worker 与样本采集子进程随之结束。
var jobAttached = !preview && ToolProcessJob.TryAttach();
var reports = new ReportDirectory(Path.Combine(repository.FullName, "artifacts", "reports"), TimeProvider.System);
var session = new DebugSession(scenarios, reports, TimeProvider.System, scratchRoot, () =>
{
    var description = new Dictionary<string, object?>(environment.Describe(), StringComparer.Ordinal)
    {
        ["toolRunId"] = stamp, ["processJobAttached"] = jobAttached, ["machineProcessors"] = Environment.ProcessorCount,
    };
    return description;
}, preview);
var loop = session.RunAsync();
using var panel = new DebugPanel(session);
ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; session.RequestExit(); };
Console.CancelKeyPress += onCancel;
try
{
    await panel.ShowAsync();
    await Task.WhenAll(panel.Closed, loop);
}
finally { Console.CancelKeyPress -= onCancel; }
foreach (var result in session.Results)
    Console.WriteLine($"{result.Id}\t{result.Outcome}\t{(result.Accepted ? "accepted" : "notAccepted")}\t{result.ReportPath ?? result.ReportError}");
return session.Results.Count > 0 && session.Results.All(value => value.Accepted) ? 0 : 1;

static class ToolProcessJob
{
    public static bool TryAttach()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return false;
        var limits = new ExtendedLimitInformation { Basic = new() { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())) return false;
        // 句柄保持到进程结束；进程退出时内核关闭句柄并终止 Job 内全部子进程。
        return AssignProcessToJobObject(job, GetCurrentProcess());
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic; public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int information, ref ExtendedLimitInformation value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
