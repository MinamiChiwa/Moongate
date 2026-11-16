using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Host.Combat;
using MabiTool.ModuleSdk;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.Replay;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 2 || !args.All(Path.IsPathFullyQualified)) throw new ArgumentException("参数为仓库和报告的绝对路径");
var repository = Path.GetFullPath(args[0]); var reportPath = Path.GetFullPath(args[1]);
var reportRoot = Path.GetFullPath(Path.Combine(repository, "artifacts", "reports")) + Path.DirectorySeparatorChar;
if (!reportPath.StartsWith(reportRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("报告须位于 artifacts/reports");
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
var temporary = Path.Combine(repository, "artifacts", "tmp", "combat-budget-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
var baseline = Memory(); var source = new CombatLoadSource(); var samples = new List<Sample>();
var latencies = new List<double>(); var total = Stopwatch.StartNew();
try
{
    var secret = LocalAuthService.NewSecret();
    await using var app = BackendApplication.Build(new(0, secret, new HashSet<string>()),
        storage: new(Path.Combine(temporary, "bootstrap"), true, PortableRoot: temporary), mock: true, combatSource: _ => source);
    await app.StartAsync(); var hostStartMs = total.Elapsed.TotalMilliseconds;
    var operations = app.Services.GetRequiredService<CombatOperations>(); var idle = Memory();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var first = new HttpClient { BaseAddress = new(address) }; first.DefaultRequestHeaders.Authorization = new("Bearer", secret);
    using var second = new HttpClient { BaseAddress = new(address) }; second.DefaultRequestHeaders.Authorization = new("Bearer", secret);
    var binding = (await first.GetFromJsonAsync<GameBindingSnapshot>(ApiContract.GameBinding.Path, ContractJson.Options))!;
    using var command = new HttpRequestMessage(HttpMethod.Post, ApiContract.StartCombatObservation.Path)
        { Content = JsonContent.Create(new CombatObservationRequest(binding.Binding!.Session.Id), options: ContractJson.Options) };
    command.Headers.Add("Idempotency-Key", "combat-budget-start");
    using var response = await first.SendAsync(command); response.EnsureSuccessStatusCode();
    var admitted = (await response.Content.ReadFromJsonAsync<OperationSnapshot>(ContractJson.Options))!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var sampling = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        while (!sampling.IsCancellationRequested)
        {
            samples.Add(new(Memory(), operations.InputUsage()));
            try { await Task.Delay(10, sampling.Token); } catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        }
    });
    OperationSnapshot final;
    try
    {
        while (true)
        {
            foreach (var client in new[] { first, second })
            {
                var start = Stopwatch.GetTimestamp();
                await client.GetFromJsonAsync<CombatSnapshot>(ApiContract.CombatSnapshot.Path, ContractJson.Options, timeout.Token);
                latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            final = (await first.GetFromJsonAsync<OperationSnapshot>(ApiContract.CombatObservation.Path.Replace("{id}", admitted.Id), ContractJson.Options, timeout.Token))!;
            if (final.State.KnownValue is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.Interrupted) break;
            await Task.Delay(20, timeout.Token);
        }
    }
    finally { sampling.Cancel(); await sampler; }
    if (final.State.KnownValue != OperationState.Succeeded) throw new InvalidOperationException("负载观察未正常结束：" + final.Problem?.Code);
    var snapshot = (await first.GetFromJsonAsync<CombatSnapshot>(ApiContract.CombatSnapshot.Path, ContractJson.Options))!;
    var encounter = snapshot.Data!.Encounter!; var limits = new CombatLimits();
    if (encounter.Summary.Quality.KnownValue != ObservationQuality.Partial || encounter.Resources.DroppedEvents.Value == 0 ||
        encounter.Resources.CacheBytes.Value > (ulong)limits.MaximumCacheBytes || encounter.Resources.TimelineBytes.Value > (ulong)limits.MaximumTimelineBytes ||
        encounter.Resources.TimelineItems > limits.MaximumTimelineItems || source.ActiveSubscriptions != 0 ||
        samples.Any(x => x.Input.QueuedEvents > limits.MaximumQueueEvents || x.Input.QueueBytes > limits.MaximumQueueBytes))
        throw new InvalidOperationException("负载资源或质量状态不符合容量契约");
    using var room = await second.GetAsync(ApiContract.RoomPreferences.Path); room.EnsureSuccessStatusCode();
    using var music = await second.GetAsync(ApiContract.MusicLibrary.Path); music.EnsureSuccessStatusCode();
    var afterLoad = Memory(); var shutdown = Stopwatch.StartNew(); await app.StopAsync(); shutdown.Stop();
    GC.Collect(); GC.WaitForPendingFinalizers(); var afterShutdown = Memory();
    var report = new
    {
        schemaVersion = 1, measuredAt = DateTimeOffset.UtcNow,
        environment = new { os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
            processor = OperatingSystem.IsWindows() ? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString() : null },
        workload = "嵌入真实 Host；1000 帧共 64000 次合成伤害、双客户端快照查询及音乐/房间查询",
        source = ReplayProvider.Source, hostStartMs, totalMs = total.Elapsed.TotalMilliseconds, source.Frames, source.EmittedDamageEvents, source.EmitMs,
        emittedEventsPerSecond = source.EmittedDamageEvents * 1000 / source.EmitMs,
        snapshotMs = Percentiles(latencies), sampleCount = samples.Count,
        mainProcess = new { baseline, idle, afterLoad, afterShutdown, peakWorkingSetBytes = samples.Max(x => x.Memory.WorkingSetBytes),
            peakPrivateBytes = samples.Max(x => x.Memory.PrivateBytes), peakManagedBytes = samples.Max(x => x.Memory.ManagedBytes) },
        workers = new { processCount = 0, workingSetBytes = 0, privateBytes = 0 },
        processTree = new { processCount = 1, peakWorkingSetBytes = samples.Max(x => x.Memory.WorkingSetBytes), peakPrivateBytes = samples.Max(x => x.Memory.PrivateBytes) },
        package = new { frameworkDependentPublishBytes = TreeBytes(Path.Combine(repository, "artifacts", "packages", "combat-backend-probe")) },
        inputLimits = new { limits.MaximumQueueEvents, limits.MaximumQueueBytes, observedPeakEvents = samples.Max(x => x.Input.QueuedEvents), observedPeakBytes = samples.Max(x => x.Input.QueueBytes) },
        limits, encounter.Resources, encounter.Summary, snapshot.Storage,
        releasedSubscriptions = source.ActiveSubscriptions == 0, activitiesAfterStop = app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities.Count,
        shutdownMs = shutdown.Elapsed.TotalMilliseconds
    };
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
    Console.WriteLine(reportPath);
}
finally
{
    var allowed = Path.GetFullPath(Path.Combine(repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(temporary).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
    Directory.Delete(temporary, true);
}
static object Percentiles(IEnumerable<double> values)
{
    var data = values.Order().ToArray(); double At(double percentile) => data[Math.Clamp((int)Math.Ceiling(data.Length * percentile) - 1, 0, data.Length - 1)];
    return new { count = data.Length, p50 = At(.5), p95 = At(.95), maximum = data[^1] };
}
static MemorySample Memory()
{
    using var process = Process.GetCurrentProcess(); process.Refresh();
    return new(process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false), process.TotalProcessorTime.TotalMilliseconds);
}
static long TreeBytes(string root) => Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0;
internal sealed record MemorySample(long WorkingSetBytes, long PrivateBytes, long ManagedBytes, double CpuMs);
internal sealed record Sample(MemorySample Memory, CombatInputUsage Input);
