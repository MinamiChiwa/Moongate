using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Rooms.Domain;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

if (args is ["--configuration-transactions", ..])
{
    await ConfigurationTransactionProbe.RunAsync(args[1..]);
    return;
}

if (args is ["--command-admission", ..]) { await RunCommandAdmissionAsync(args[1..]); return; }
if (args is ["--live-replay", ..]) { await LiveReplayProbe.RunAsync(args[1..]); return; }
if (args is ["--self-test"])
{
    var distribution = Distribution.From(Enumerable.Range(1, 100).Select(x => (double)x));
    if (distribution.P50 != 50 || distribution.P95 != 95 || distribution.P99 != 99 || distribution.Maximum != 100) throw new InvalidOperationException("分位数计算不符");
    foreach (var invalid in new[] { Array.Empty<double>(), new[] { double.NaN }, new[] { double.PositiveInfinity }, new[] { -1d } })
    {
        try { Distribution.From(invalid); throw new InvalidOperationException("无效样本未拒绝"); } catch (InvalidDataException) { }
    }
    var stops = MeasureStops(3); var schedules = await MeasureSchedulerAsync(3);
    if (stops.Count != 3 || schedules.Count != 3 || schedules.Any(x => x < 0)) throw new InvalidOperationException("停止或调度验证失败");
    using var current = Process.GetCurrentProcess(); var memory = ProcessSample.Read(current);
    if (memory.PrivateWorkingSetBytes <= 0 || memory.PrivateWorkingSetBytes > memory.WorkingSetBytes) throw new InvalidOperationException("私有工作集测量无效");
    await RuntimeProbeSelfTest.RunAsync();
    Console.WriteLine("性能统计、无效样本、停止释放、调度截止、私有工作集、目录隔离及失败/取消释放验证通过"); return;
}
if (!OperatingSystem.IsWindows() || args.Length is not (8 or 9)) throw new ArgumentException("参数：repository report hostAssembly environmentJson idleSeconds coldCount sampleCount eventCount [temporaryRoot]；需 Windows x64");
var repository = Path.GetFullPath(args[0]); var report = Path.GetFullPath(args[1]);
var hostAssembly = Path.GetFullPath(args[2]); var environmentFile = Path.GetFullPath(args[3]);
var idleSeconds = int.Parse(args[4]); var coldCount = int.Parse(args[5]); var sampleCount = int.Parse(args[6]); var eventCount = int.Parse(args[7]);
if (!Path.IsPathFullyQualified(args[0]) || !File.Exists(Path.Combine(repository, "MabiTool.slnx")) ||
    !report.StartsWith(Path.Combine(repository, "artifacts", "reports") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
    idleSeconds is < 1 or > 3600 || coldCount is < 1 or > 100 || sampleCount is < 1 or > 10000 || eventCount is < 1 or > 10000)
    throw new ArgumentException("路径或测量数量超出预算");
TemporaryWorkspace.EnsureNoLinks(hostAssembly); TemporaryWorkspace.EnsureNoLinks(environmentFile); TemporaryWorkspace.EnsureNoLinks(report);
if (!File.Exists(hostAssembly)) throw new ArgumentException("宿主程序集不存在");
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
var cold = new List<double>(); var queries = new List<double>(); var commands = new List<double>(); var idle = new List<TreeSample>();
var startupSamples = new List<StartupTiming>();
var complete = Stopwatch.StartNew(); var environment = JsonDocument.Parse(await File.ReadAllTextAsync(environmentFile)).RootElement.Clone();
using var workspace = TemporaryWorkspace.Create(args.Length == 9 ? args[8] : Path.Combine(repository, "artifacts", "tmp"));
var temporary = workspace.DirectoryPath;
var processData = ProbeDataPaths.FromPortableRoot(Path.Combine(temporary, "process-host"));
var eventData = ProbeDataPaths.FromPortableRoot(Path.Combine(temporary, "event-host"));
using var cancellation = new CancellationTokenSource(); var cancellationToken = cancellation.Token;
ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancelHandler;
var children = new List<int>();
try
{
    Console.WriteLine("初始化模拟数据根");
    await using (var initialize = await RunningHost.StartAsync(hostAssembly, Path.Combine(temporary, "process-host"), true, cancellationToken)) { children.Add(initialize.Process.Id); }
    Console.WriteLine($"新进程启动测量：{coldCount} 次");
    for (var i = 0; i < coldCount; i++)
    {
        await using var current = await RunningHost.StartAsync(hostAssembly, Path.Combine(temporary, "process-host"), false, cancellationToken);
        children.Add(current.Process.Id); cold.Add(current.StartMilliseconds);
        startupSamples.Add(current.StartupTiming);
        if ((i + 1) % 5 == 0 || i + 1 == coldCount) Console.WriteLine($"启动测量完成 {i + 1}/{coldCount}");
    }
    double idleCpu; double idleElapsed;
    await using (var host = await RunningHost.StartAsync(hostAssembly, Path.Combine(temporary, "process-host"), false, cancellationToken))
    {
        Console.WriteLine($"空闲测量：{idleSeconds} 秒");
        children.Add(host.Process.Id); var cpuBefore = host.Process.TotalProcessorTime.TotalMilliseconds; var period = Stopwatch.StartNew();
        while (period.Elapsed.TotalSeconds < idleSeconds)
        {
            idle.Add(TreeSample.Read(host.Process));
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(1000, idleSeconds * 1000 - period.Elapsed.TotalMilliseconds))), cancellationToken);
        }
        idleElapsed = period.Elapsed.TotalMilliseconds;
        host.Process.Refresh(); idleCpu = (host.Process.TotalProcessorTime.TotalMilliseconds - cpuBefore) / idleElapsed / Environment.ProcessorCount * 100;
        Console.WriteLine($"查询测量：{sampleCount} 次");
        for (var i = 0; i < sampleCount; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await host.Client.GetAsync(ApiContract.MusicLibrary.Path, cancellationToken); response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync(cancellationToken); queries.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if ((i + 1) % 100 == 0 || i + 1 == sampleCount) Console.WriteLine($"查询完成 {i + 1}/{sampleCount}");
        }
        commands.AddRange(await MeasureCommandsAsync(host.Client, sampleCount, cancellationToken));
    }
    Console.WriteLine($"SSE 测量：{eventCount} 次");
    var events = await MeasureEventsAsync(eventCount, temporary, cancellationToken);
    Console.WriteLine($"停止与调度测量：各 {sampleCount} 次");
    var stops = MeasureStops(sampleCount, cancellationToken); var schedules = await MeasureSchedulerAsync(sampleCount, cancellationToken);
    var packages = Directory.Exists(Path.Combine(repository, "artifacts", "packages"))
        ? Directory.EnumerateDirectories(Path.Combine(repository, "artifacts", "packages")).Select(p => new { name = Path.GetFileName(p), bytes = Bytes(p) }).ToArray() : [];
    var fullProtocol = idleSeconds >= 600 && coldCount >= 30 && sampleCount >= 1000 && eventCount >= 1000;
    var probePath = Assembly.GetExecutingAssembly().Location;
    var selectedHostSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(hostAssembly, cancellationToken))).ToLowerInvariant();
    var loadedEventHost = await EventHostIdentity.CaptureAsync(cancellationToken);
    if (!children.All(Exited)) throw new InvalidOperationException("测量子进程尚未退出");
    workspace.Dispose();
    var output = new
    {
        schemaVersion = 1, evidenceKind = "backend-local-performance", measuredAtUtc = DateTimeOffset.UtcNow,
        environment, runtime = RuntimeInformation.FrameworkDescription, probeAssembly = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
        hostAssemblySha256 = selectedHostSha256,
        eventHostAssemblySha256 = loadedEventHost.Sha256,
        selectedHostMatchesEventHost = selectedHostSha256 == loadedEventHost.Sha256,
        paths = new { hostAssembly, eventHostAssembly = loadedEventHost.Path, probeAssembly = probePath, temporaryParent = workspace.ParentPath, temporaryDirectory = temporary,
            processDataRoot = processData.DataRoot, processSqlite = processData.Sqlite,
            eventDataRoot = eventData.DataRoot, eventSqlite = eventData.Sqlite },
        probeAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(probePath))).ToLowerInvariant(),
        clock = new { name = "Windows QPC / Stopwatch", frequency = Stopwatch.Frequency, highResolution = Stopwatch.IsHighResolution },
        baseline = new { required = "benchmark machine = local development machine (Windows 11 x64); hardware recorded in environment", status = fullProtocol ? "benchmark-machine-full-protocol" : "benchmark-machine-shortened-protocol" },
        protocol = new { fullProtocol, idleSeconds, coldCount, sampleCount, eventCount, eventHostSource = "in-process Host referenced by probe assembly",
            commandTiming = "HTTP send through response-body deserialization; completion wait excluded",
            load = "无游戏 mock 宿主；独立进程新启动，OS 文件缓存未清空；查询及新模拟请求；SSE 10次/秒；单调调度20ms间隔" },
        coldStartupMs = Distribution.From(cold), queryMs = Distribution.From(queries), commandAdmissionMs = Distribution.From(commands),
        coldStartupStages = new
        {
            timingBoundary = "total: before Process.Start through first successful GET ready; processToDiscovery: before Process.Start through instance.json matching started PID; discoveryToBootstrap: matched discovery through ConnectAsync completion; bootstrapToReady: ConnectAsync completion through original total endpoint",
            samples = startupSamples,
            processToDiscoveryMs = Distribution.From(startupSamples.Select(x => x.ProcessToDiscoveryMs)),
            discoveryToBootstrapMs = Distribution.From(startupSamples.Select(x => x.DiscoveryToBootstrapMs)),
            bootstrapToReadyMs = Distribution.From(startupSamples.Select(x => x.BootstrapToReadyMs))
        },
        ssePublishToClientMs = Distribution.From(events), emergencyStopMs = Distribution.From(stops), schedulerLatenessMs = Distribution.From(schedules),
        idle = new { elapsedMs = idleElapsed, cpuWholeMachinePercent = idleCpu, samples = idle.Count,
            hostPeakWorkingSetBytes = idle.Max(x => x.Host.WorkingSetBytes), hostPeakPrivateBytes = idle.Max(x => x.Host.PrivateBytes),
            hostPeakPrivateWorkingSetBytes = idle.Max(x => x.Host.PrivateWorkingSetBytes) },
        workers = new { idleProcessCount = idle.Max(x => x.Workers.Length), idleWorkingSetBytes = idle.Max(x => x.Workers.Sum(p => p.WorkingSetBytes)), idlePrivateBytes = idle.Max(x => x.Workers.Sum(p => p.PrivateBytes)), processes = idle.SelectMany(x => x.Workers).DistinctBy(x => x.ProcessId).ToArray() },
        processTree = new { idlePeakProcessCount = idle.Max(x => 1 + x.Descendants.Length), idlePeakWorkingSetBytes = idle.Max(x => x.WorkingSetBytes), idlePeakPrivateBytes = idle.Max(x => x.PrivateBytes), descendants = idle.SelectMany(x => x.Descendants).DistinctBy(x => x.ProcessId).ToArray() },
        localBudgetChecks = new { coldP95 = Distribution.From(cold).P95 <= 2000, queriesP95 = Distribution.From(queries).P95 <= 50,
            commandP95 = Distribution.From(commands).P95 <= 50, eventP95 = Distribution.From(events).P95 <= 100, stopP95 = Distribution.From(stops).P95 <= 100,
            schedulerP95 = Distribution.From(schedules).P95 <= 10, schedulerP99 = Distribution.From(schedules).P99 <= 25,
            idleWorkers = idle.All(x => x.Workers.Length == 0), idleCpu = idleCpu <= .5, idlePrivateWorkingSet = idle.Max(x => x.Host.PrivateWorkingSetBytes) <= 120 * 1024 * 1024 },
        packages, elapsedMs = complete.Elapsed.TotalMilliseconds, cleanup = new { spawnedProcesses = children.Count, allSpawnedProcessesExited = children.All(Exited), temporaryDirectoryRemoved = !Directory.Exists(temporary), systemInputCalls = 0 }
    };
    if (!children.All(Exited)) throw new InvalidOperationException("测量子进程尚未退出");
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(output, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }), cancellationToken);
    Console.WriteLine(report);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static bool Exited(int id) => RunningHost.ExitedProcesses.ContainsKey(id);
// 逐次受理模拟回放并等待完成后再发下一次；计时只含 HTTP 发送至响应正文反序列化。
static async Task<List<double>> MeasureCommandsAsync(HttpClient client, int count, CancellationToken cancellationToken)
{
    Console.WriteLine($"命令受理测量：{count} 次");
    var samples = new List<double>();
    for (var i = 0; i < count; i++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiContract.StartReplay.Path)
        { Content = JsonContent.Create(new ReplayRequest("normal", 25), options: ContractJson.Options) };
        request.Headers.Add("Idempotency-Key", "budget-replay-" + i);
        var started = Stopwatch.GetTimestamp(); using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
        var operation = (await response.Content.ReadFromJsonAsync<OperationSnapshot>(ContractJson.Options, cancellationToken))!;
        samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (operation.State.KnownValue is OperationState.Queued or OperationState.Running)
        {
            await Task.Delay(2, timeout.Token);
            operation = (await client.GetFromJsonAsync<OperationSnapshot>(ApiContract.Operation.Path.Replace("{id}", operation.Id), ContractJson.Options, timeout.Token))!;
        }
        if (operation.State.KnownValue != OperationState.Succeeded) throw new InvalidOperationException("模拟受理未成功：" + operation.Problem?.Code);
        if ((i + 1) % 100 == 0 || i + 1 == count) Console.WriteLine($"模拟请求完成 {i + 1}/{count}");
    }
    return samples;
}
static async Task RunCommandAdmissionAsync(string[] args)
{
    if (!OperatingSystem.IsWindows() || args.Length != 4) throw new ArgumentException("参数：--command-admission repository hostAssembly sampleCount temporaryRoot；需 Windows x64");
    var repository = Path.GetFullPath(args[0]); var hostAssembly = Path.GetFullPath(args[1]);
    if (!File.Exists(Path.Combine(repository, "MabiTool.slnx")) || !File.Exists(hostAssembly) || !int.TryParse(args[2], out var count) || count is < 1 or > 10000)
        throw new ArgumentException("路径或测量数量超出预算");
    TemporaryWorkspace.EnsureNoLinks(hostAssembly);
    using var workspace = TemporaryWorkspace.Create(args[3]);
    var root = Path.Combine(workspace.DirectoryPath, "process-host");
    await using (var initialize = await RunningHost.StartAsync(hostAssembly, root, true)) { }
    List<double> samples;
    await using (var host = await RunningHost.StartAsync(hostAssembly, root, false)) samples = await MeasureCommandsAsync(host.Client, count, CancellationToken.None);
    var all = Distribution.From(samples); var quarter = Math.Max(1, count / 4);
    Console.WriteLine($"命令受理 p50 {all.P50:F2} / p95 {all.P95:F2} / p99 {all.P99:F2} / 最大 {all.Maximum:F2} ms（{all.Count} 次）");
    Console.WriteLine($"末 {quarter} 次 p50 {Distribution.From(samples.Skip(count - quarter)).P50:F2} / p95 {Distribution.From(samples.Skip(count - quarter)).P95:F2} ms");
}
static long Bytes(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
static async Task<List<double>> MeasureEventsAsync(int count, string temporary, CancellationToken cancellationToken = default)
{
    var stampClock = new PublicationClock(); var secret = LocalAuthService.NewSecret();
    var directory = Path.Combine(temporary, "event-host");
    await using var app = BackendApplication.Build(new(0, secret, new HashSet<string>()), clock: stampClock,
        storage: new(Path.Combine(directory, "bootstrap"), true, PortableRoot: directory), mock: true);
    await app.StartAsync(cancellationToken);
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new(address), Timeout = Timeout.InfiniteTimeSpan };
    client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(count / 10.0 + 30));
    using var request = new HttpRequestMessage(HttpMethod.Get, ApiContract.Events.Path);
    request.Headers.Add("X-Event-Topics", "game.environment.changed"); request.Headers.Add("X-Event-Resources", "budget-environment");
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
    var deliveries = new ConcurrentDictionary<int, long>(); var stamps = new Dictionary<int, long>();
    var received = Task.Run(async () =>
    {
        string? kind = null;
        while (deliveries.Count < count)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException();
            if (line.StartsWith("event: ", StringComparison.Ordinal)) kind = line[7..];
            if (kind == "game.environment.changed" && line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var readAt = Stopwatch.GetTimestamp(); using var document = JsonDocument.Parse(line[6..]);
                var sequence = checked((int)ulong.Parse(document.RootElement.GetProperty("resourceRevision").GetString()!) - 1);
                deliveries.AddOrUpdate(sequence, readAt, (_, _) => throw new InvalidDataException("重复事件"));
            }
        }
    }, timeout.Token);
    var hub = app.Services.GetRequiredService<StateEventHub>();
    try
    {
      for (var i = 0; i < count; i++)
      {
        var capture = new PublicationStamp(); stampClock.Capture.Value = capture;
        var observedAt = DateTimeOffset.UtcNow;
        var observation = new Observation<EnvironmentSnapshot>(new(null, null, null, null, null, ObservationQuality.Unknown, ["time", "weather", "channel", "area"]),
            new("replay-synthetic", "1.0", "TW", "probe-mock"), observedAt, "budget-clock", Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency,
            1000, new UInt64String(1), new UInt64String((ulong)i + 1), ObservationQuality.Unknown);
        await hub.PublishVolatileAsync(new("budget-event-" + i, "environment", "budget-environment", new UInt64String((ulong)i + 1),
            "game.environment.changed", JsonSerializer.SerializeToElement(observation, ContractJson.Options)), observedAt, timeout.Token);
        stampClock.Capture.Value = null;
        if (capture.First == 0) throw new InvalidOperationException("未取得实际发布时间戳"); stamps.Add(i, capture.First);
        await Task.Delay(100, timeout.Token);
      }
      await received;
    }
    finally
    {
        timeout.Cancel();
        try { await received; } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StopAsync(stopTimeout.Token);
    }
    return Enumerable.Range(0, count).Select(i => Stopwatch.GetElapsedTime(stamps[i], deliveries[i]).TotalMilliseconds).ToList();
}
static List<double> MeasureStops(int count, CancellationToken cancellationToken = default)
{
    var tracker = new GameSessionBindingTracker();
    var binding = tracker.Update(new("TW", 100, DateTimeOffset.UtcNow, new UInt64String(500), "probe-synthetic", new PhysicalWindowRect(0, 0, 800, 600), 96, 96, false));
    var leases = new ResourceLeaseCoordinator(); var device = new SyntheticInput(binding.Session.WindowIdentity); var broker = new GameInputBroker(leases, tracker, device);
    var samples = new List<double>();
    for (var i = 0; i < count; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = leases.Acquire(new("budget", "stop-" + i, binding.Session.Id, binding.Session.BindingRevision, ["foreground-input"], TimeSpan.FromSeconds(5)));
        broker.Execute(new("budget", lease.Id, lease.FencingToken, binding.Session.Id, binding.Session.BindingRevision, binding.Session.WindowIdentity, InputActionKind.Press, InputControl.MouseLeft));
        var before = Stopwatch.GetTimestamp(); var result = broker.EmergencyStop(); samples.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
        if (result.RevokedLeases != 1 || result.ReleasedControls != 1 || device.Held || leases.Validate(lease.Id, "foreground-input", lease.FencingToken, binding.Session.Id, binding.Session.BindingRevision))
            throw new InvalidOperationException("停止未同步释放输入或租约");
    }
    return samples;
}
static async Task<List<double>> MeasureSchedulerAsync(int count, CancellationToken cancellationToken = default)
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new ScheduledInput(clock);
    using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("budget-epoch");
    scheduler.Receipt += receipt =>
    {
        if (receipt.Status != "sent") input.Completion.TrySetException(new InvalidOperationException("调度测量未执行：" + receipt.Status + "；" + receipt.Detail));
    };
    var values = new List<double>();
    for (var i = 0; i < count; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.NowMs; if (!room.AddSample(now, now, now, now)) throw new InvalidOperationException();
        var action = new ScheduledAction("budget-" + i, "budget-session", "start", clock.NowMs + 20, "budget-epoch");
        input.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!scheduler.Prepare(action).Ready || !scheduler.Commit(action)) throw new InvalidOperationException("调度未受理");
        var observed = await input.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); values.Add(observed - action.TargetServerMs);
        if (observed < action.TargetServerMs) throw new InvalidOperationException("调度提前执行");
    }
    scheduler.CancelAll(); return values;
}

internal sealed record Distribution(int Count, double P50, double P95, double P99, double Maximum)
{
    public static Distribution From(IEnumerable<double> samples)
    {
        var values = samples.Order().ToArray();
        if (values.Length == 0 || values.Any(x => !double.IsFinite(x) || x < 0)) throw new InvalidDataException("延迟样本无效");
        double At(double p) => values[Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1)];
        return new(values.Length, At(.5), At(.95), At(.99), values[^1]);
    }
}
internal sealed class PublicationStamp { public long First; }
internal sealed class PublicationClock : TimeProvider
{
    public AsyncLocal<PublicationStamp?> Capture { get; } = new();
    public override long GetTimestamp() { var stamp = Stopwatch.GetTimestamp(); if (Capture.Value is { First: 0 } capture) capture.First = stamp; return stamp; }
    public override long TimestampFrequency => Stopwatch.Frequency;
}
internal sealed class SyntheticInput(UInt64String identity) : IGameInputDevice
{
    public bool Held { get; private set; }
    public UInt64String? ForegroundWindowIdentity => identity;
    public void Press(InputControl control) => Held = true;
    public void Release(InputControl control) => Held = false;
    public void MovePointer(PhysicalScreenPoint point) { }
    public void ReleaseAll() => Held = false;
}
internal sealed class ScheduledInput(IMonotonicClock clock) : IInputExecutor
{
    public TaskCompletionSource<double> Completion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public InputPreparation Prepare(ScheduledAction action) => new(true, null);
    public InputOutcome Execute(ScheduledAction action, CancellationToken cancellationToken) { var now = clock.NowMs; Completion.TrySetResult(now); return new("sent", now, true, null); }
    public bool IsSimulation(ScheduledAction action) => true;
    public void Forget(string actionId) { }
    public void ReleaseAll() { }
}
internal sealed record StartupTiming(double TotalMs, double ProcessToDiscoveryMs, double DiscoveryToBootstrapMs, double BootstrapToReadyMs);

internal sealed record EventHostIdentity(string Path, string Sha256)
{
    public static async Task<EventHostIdentity> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var path = typeof(BackendApplication).Assembly.Location;
        var hash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken));
        return new(path, Convert.ToHexString(hash).ToLowerInvariant());
    }
}

internal sealed record ProbeDataPaths(string DataRoot, string Sqlite)
{
    public static ProbeDataPaths FromPortableRoot(string portableRoot)
    {
        var dataRoot = Path.Combine(portableRoot, "data");
        return new(dataRoot, Path.Combine(dataRoot, "store", "main.sqlite"));
    }
}

internal sealed class RunningHost(Process process, HttpClient client, double startMs, StartupTiming startupTiming, Task stdout, Task stderr) : IAsyncDisposable
{
    public static ConcurrentDictionary<int, bool> ExitedProcesses { get; } = new();
    public Process Process => process;
    public HttpClient Client => client;
    public double StartMilliseconds => startMs;
    public StartupTiming StartupTiming => startupTiming;
    public static async Task<RunningHost> StartAsync(string assembly, string root, bool initialize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); TemporaryWorkspace.EnsureNoLinks(root); TemporaryWorkspace.EnsureNoLinks(assembly);
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(assembly); start.ArgumentList.Add("--mock"); start.ArgumentList.Add("--portable-root"); start.ArgumentList.Add(root);
        if (initialize) start.ArgumentList.Add("--initialize-data");
        start.Environment["MABITOOL_API_PORT"] = "0"; start.Environment["MABITOOL_ALLOWED_ORIGINS"] = "";
        start.Environment.Remove("MABITOOL_API_TOKEN"); start.Environment.Remove("MABITOOL_RUNTIME_DIRECTORY"); start.Environment.Remove("MABITOOL_BOOTSTRAP_DIRECTORY");
        var before = Stopwatch.GetTimestamp(); var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动测试宿主");
        var stdout = DrainAsync(process.StandardOutput); var stderr = DrainErrorAsync(process.StandardError);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(initialize ? 120 : 30));
            var discovery = Path.Combine(root, "runtime", "instance.json");
            long discovered = 0;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("测试宿主提前退出，exit=" + process.ExitCode);
                try
                {
                    if (File.Exists(discovery))
                    {
                        LocalDiscoveryInfo? info;
                        using (var file = new FileStream(discovery, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                            info = await JsonSerializer.DeserializeAsync<LocalDiscoveryInfo>(file, ContractJson.Options, timeout.Token);
                        if (info?.ProcessId == process.Id)
                        {
                            if (discovered == 0) discovered = Stopwatch.GetTimestamp();
                            var boot = await LocalBootstrapClient.ConnectAsync(discovery, "backend-budget", timeout.Token);
                            var bootstrapped = Stopwatch.GetTimestamp();
                            using var ready = new HttpClient { BaseAddress = new(boot.ApiBaseUri), Timeout = TimeSpan.FromSeconds(10) };
                            ready.DefaultRequestHeaders.Authorization = new("Bearer", boot.Grant.Token);
                            using var response = await ready.GetAsync(ApiContract.Ready.Path, timeout.Token); response.EnsureSuccessStatusCode();
                            var elapsed = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                            var processToDiscovery = Stopwatch.GetElapsedTime(before, discovered).TotalMilliseconds;
                            var discoveryToBootstrap = Stopwatch.GetElapsedTime(discovered, bootstrapped).TotalMilliseconds;
                            var startupTiming = new StartupTiming(elapsed, processToDiscovery, discoveryToBootstrap, elapsed - processToDiscovery - discoveryToBootstrap);
                            var client = new HttpClient { BaseAddress = new(boot.ApiBaseUri), Timeout = TimeSpan.FromSeconds(15) };
                            client.DefaultRequestHeaders.Authorization = new("Bearer", boot.Grant.Token);
                            return new(process, client, elapsed, startupTiming, stdout, stderr);
                        }
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
                await Task.Delay(10, timeout.Token);
            }
        }
        catch (Exception exception)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr); ExitedProcesses[process.Id] = true; process.Dispose();
            if (exception is OperationCanceledException) throw;
            var error = await stderr;
            throw new InvalidOperationException(exception.Message + "；stderr 尾部：" + (error.Length == 0 ? "[无输出]" : error));
        }
    }
    public async ValueTask DisposeAsync()
    {
        client.Dispose(); if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(stdout, stderr); ExitedProcesses[process.Id] = true; process.Dispose();
    }
    private static async Task DrainAsync(TextReader reader) { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) != 0) { } }
    internal static async Task<string> DrainErrorAsync(TextReader reader)
    {
        var buffer = new char[4096]; var tail = new System.Text.StringBuilder(); var truncated = false; int read;
        while ((read = await reader.ReadAsync(buffer)) != 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > 8192) { tail.Remove(0, tail.Length - 8192); truncated = true; }
        }
        var text = tail.ToString();
        if (truncated) { var firstEnd = text.IndexOf('\n'); text = firstEnd >= 0 ? text[(firstEnd + 1)..] : ""; }
        var safeLines = text.Split('\n').Where(line => !System.Text.RegularExpressions.Regex.IsMatch(line,
            @"\b(?:token|(?:access|refresh|api)[_-]?token|secret|(?:api|client)[_-]?secret|password|authorization|credentials?|bearer|pairing(?:code|token)?)\b['""]?\s*[:=]\s*\S+|\bBearer\s+\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        var safe = System.Text.RegularExpressions.Regex.Replace(string.Join('\n', safeLines), "[a-zA-Z][a-zA-Z0-9+.-]*://\\S+", "[address]");
        var bytes = System.Text.Encoding.UTF8.GetBytes(safe);
        if (bytes.Length <= 8192) return safe;
        safe = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(bytes.Length - 8192));
        while (System.Text.Encoding.UTF8.GetByteCount(safe) > 8192) safe = safe[1..];
        return safe;
    }
}
internal sealed record ProcessSample(int ProcessId, string ProcessName, DateTimeOffset StartedAtUtc, long WorkingSetBytes, long PrivateBytes, long PrivateWorkingSetBytes)
{
    public static ProcessSample Read(Process p) { p.Refresh(); return new(p.Id, p.ProcessName, p.StartTime.ToUniversalTime(), p.WorkingSet64, p.PrivateMemorySize64, NativeProcess.PrivateWorkingSet(p)); }
}
internal sealed record TreeSample(ProcessSample Host, ProcessSample[] Descendants)
{
    public ProcessSample[] Workers => Descendants.Where(x => !string.Equals(x.ProcessName, "conhost", StringComparison.OrdinalIgnoreCase)).ToArray();
    public long WorkingSetBytes => Host.WorkingSetBytes + Descendants.Sum(x => x.WorkingSetBytes);
    public long PrivateBytes => Host.PrivateBytes + Descendants.Sum(x => x.PrivateBytes);
    public static TreeSample Read(Process p) => new(ProcessSample.Read(p), NativeProcess.Descendants(p.Id).Select(id => { using var child = Process.GetProcessById(id); return ProcessSample.Read(child); }).ToArray());
}
internal static class NativeProcess
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry
    {
        public uint Size, Usage, ProcessId; public UIntPtr Heap; public uint Module, Threads, ParentId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool QueryWorkingSet(IntPtr process, [Out] byte[] buffer, int bytes);
    public static int[] Descendants(int parent)
    {
        using var owner = Process.GetProcessById(parent);
        var ownerStarted = owner.StartTime.ToUniversalTime();
        var snapshot = CreateToolhelp32Snapshot(2, 0); if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var entries = new List<(int Id, int Parent)>(); var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
            if (!Process32FirstW(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception();
            do
            {
                try
                {
                    using var candidate = Process.GetProcessById((int)entry.ProcessId);
                    if (candidate.StartTime.ToUniversalTime() >= ownerStarted)
                        entries.Add(((int)entry.ProcessId, (int)entry.ParentId));
                }
                catch (ArgumentException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            } while (Process32NextW(snapshot, ref entry));
            var found = new HashSet<int> { parent }; bool added;
            do { added = false; foreach (var pair in entries) if (found.Contains(pair.Parent) && found.Add(pair.Id)) added = true; } while (added);
            found.Remove(parent); return found.ToArray();
        }
        finally { CloseHandle(snapshot); }
    }
    public static long PrivateWorkingSet(Process process)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("工作集测量需要x64");
        for (var length = 1024 * 1024; length <= 64 * 1024 * 1024; length *= 2)
        {
            var buffer = new byte[length]; if (!QueryWorkingSet(process.Handle, buffer, length))
            { if (Marshal.GetLastWin32Error() == 24) continue; throw new System.ComponentModel.Win32Exception(); }
            var count = BitConverter.ToUInt64(buffer, 0); if (count > (ulong)(length / 8 - 1)) throw new InvalidDataException("工作集计数超限");
            long privatePages = 0; for (ulong i = 0; i < count; i++) if ((BitConverter.ToUInt64(buffer, checked((int)(8 + i * 8))) & 0x100) == 0) privatePages++;
            return privatePages * Environment.SystemPageSize;
        }
        throw new InvalidOperationException("工作集测量超出64MiB缓冲预算");
    }
}
