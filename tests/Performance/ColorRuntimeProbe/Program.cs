using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.Color.Domain;
using MabiTool.Platform.Windows;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args.Length != 3 || !args.All(Path.IsPathFullyQualified))
    throw new ArgumentException("参数为仓库、Python 可执行文件和报告文件的绝对路径");
var repository = Path.GetFullPath(args[0]);
var reports = Path.Combine(repository, "artifacts", "reports") + Path.DirectorySeparatorChar;
var output = Path.GetFullPath(args[2]);
if (!output.StartsWith(reports, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("报告须位于 artifacts/reports");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var root = Path.Combine(repository, "artifacts", "tmp", "color-budget-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var clock = new ProbeClock();
var measurements = new List<MemorySample>();
var previews = new List<double>();
var baseline = ReadMemory(null);
try
{
    var started = Stopwatch.StartNew();
    var secret = LocalAuthService.NewSecret();
    await using var app = BackendApplication.Build(new(0, secret, new HashSet<string>()), clock: clock,
        storage: new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data")),
        colorPreviewOptions: _ => new(args[1], Path.Combine(repository, "workers", "color-engine"), root)
            { MaximumParallelism = 2, IdleRetentionSeconds = 60 });
    await app.StartAsync();
    var hostStartMs = started.Elapsed.TotalMilliseconds;
    var pool = app.Services.GetRequiredService<ColorWorkerPool>();
    var idleRuntime = pool.Snapshot();
    var idle = ReadMemory(pool.ProcessId);
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new(address) };
    client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
    using var sampleStop = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        while (!sampleStop.IsCancellationRequested)
        {
            measurements.Add(ReadMemory(pool.ProcessId));
            try { await Task.Delay(10, sampleStop.Token); }
            catch (OperationCanceledException) when (sampleStop.IsCancellationRequested) { }
        }
    });
    try
    {
        for (var i = 0; i < 21; i++)
        {
            var request = new ColorPreviewRequest(ColorProfiles.Default, ["#202020", "#202020"]);
            var before = Stopwatch.GetTimestamp();
            using var response = await client.PostAsJsonAsync("/api/v1/color/previews", request, ContractJson.Options);
            response.EnsureSuccessStatusCode();
            var value = (await response.Content.ReadFromJsonAsync<ColorPreview>(ContractJson.Options))!;
            if (!value.Accepted || !value.PreviewOnly) throw new InvalidOperationException("预览行为不匹配");
            previews.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
        }
    }
    finally { sampleStop.Cancel(); await sampler; }
    var warmRuntime = pool.Snapshot();
    var warm = ReadMemory(pool.ProcessId);
    clock.Advance(TimeSpan.FromSeconds(60));
    var expire = Stopwatch.StartNew(); await pool.CollectIdleAsync(); expire.Stop();
    var expiredRuntime = pool.Snapshot();
    var afterExpiry = ReadMemory(pool.ProcessId);
    var stopping = Stopwatch.StartNew(); await app.StopAsync(); stopping.Stop();
    IReadOnlyList<GlobalShortcutSnapshot> shortcutResults;
    await using (var shortcuts = new WindowsGlobalShortcutSource())
    {
        await shortcuts.StartAsync(default);
        shortcutResults = shortcuts.GetRegistrations().Select(value => new GlobalShortcutSnapshot(value.ActionId,
            new(value.Configured.Key.ToString(), value.Configured.Control, value.Configured.Alt, value.Configured.Shift, value.Configured.Windows),
            value.Registered is { } key ? new(key.Key.ToString(), key.Control, key.Alt, key.Shift, key.Windows) : null,
            value.Status.ToString(), value.FailureCode, value.UsedFallback)).ToArray();
    }
    var report = new
    {
        schemaVersion = 1, measuredAt = DateTimeOffset.UtcNow, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
        processor = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString(),
        sample = "21 次双区域原算法 HTTP 预览；仅计算与本机 API", maximumParallelism = 2, idleRetentionSeconds = 60,
        hostStartMs, firstPreviewMs = previews[0], warmPreviewMs = Percentiles(previews.Skip(1)),
        baseline, idle, warm, afterExpiry,
        sampledPeak = new { hostWorkingSetBytes = measurements.Max(x => x.HostWorkingSetBytes), hostPrivateBytes = measurements.Max(x => x.HostPrivateBytes),
            workerWorkingSetBytes = measurements.Max(x => x.WorkerWorkingSetBytes), workerPrivateBytes = measurements.Max(x => x.WorkerPrivateBytes),
            processTreeWorkingSetBytes = measurements.Max(x => x.ProcessTreeWorkingSetBytes), processTreePrivateBytes = measurements.Max(x => x.ProcessTreePrivateBytes) },
        idleRuntime, warmRuntime, expiredRuntime, warmExitMs = expire.Elapsed.TotalMilliseconds,
        hostStopMs = stopping.Elapsed.TotalMilliseconds, shutdownRuntime = pool.Snapshot(), shortcuts = shortcutResults,
        package = new { backendFrameworkDependentPublishBytes = TreeBytes(Path.Combine(repository, "artifacts", "packages", "backend-budget")),
            backendFrameworkDependentZipBytes = File.Exists(Path.Combine(repository, "artifacts", "packages", "backend-budget.zip"))
                ? new FileInfo(Path.Combine(repository, "artifacts", "packages", "backend-budget.zip")).Length : 0,
            workerSourceBytes = TreeBytes(Path.Combine(repository, "workers", "color-engine")) }
    };
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
    Console.WriteLine(output);
}
finally
{
    var allowed = Path.GetFullPath(Path.Combine(repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
    Directory.Delete(root, true);
}

static object Percentiles(IEnumerable<double> source)
{
    var values = source.Order().ToArray();
    double At(double percentile) => values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];
    return new { count = values.Length, p50 = At(.5), p95 = At(.95), maximum = values[^1] };
}
static MemorySample ReadMemory(int? workerId)
{
    using var host = Process.GetCurrentProcess(); host.Refresh();
    long workerWorking = 0, workerPrivate = 0;
    if (workerId is { } id)
        try
        {
            using var worker = Process.GetProcessById(id); worker.Refresh();
            workerWorking = worker.WorkingSet64; workerPrivate = worker.PrivateMemorySize64;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    return new(host.WorkingSet64, host.PrivateMemorySize64, workerWorking, workerPrivate,
        host.WorkingSet64 + workerWorking, host.PrivateMemorySize64 + workerPrivate);
}
static long TreeBytes(string directory) => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
    .Where(path => !path.Contains("__pycache__", StringComparison.Ordinal) && !path.EndsWith(".pyc", StringComparison.Ordinal))
    .Sum(path => new FileInfo(path).Length) : 0;

sealed record MemorySample(long HostWorkingSetBytes, long HostPrivateBytes, long WorkerWorkingSetBytes,
    long WorkerPrivateBytes, long ProcessTreeWorkingSetBytes, long ProcessTreePrivateBytes);
sealed class ProbeClock : TimeProvider
{
    private long offset;
    public override long TimestampFrequency => Stopwatch.Frequency;
    public override long GetTimestamp() => Stopwatch.GetTimestamp() + Interlocked.Read(ref offset);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref offset, (long)(duration.TotalSeconds * Stopwatch.Frequency));
}
