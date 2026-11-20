using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.ObservationWorkerProbe;

/// <summary>
/// Worker 启动准备阶段的峰值内存：在独立子进程里按 Worker 的顺序执行一次（构建核对 → 库存布局 → 钱包资源表，实现有压缩回收时随后执行），
/// 只读本机客户端文件，报告峰值提交（私有字节）、峰值工作集、结束时私有字节、耗时与托管分配。
/// 布局缓存分首次（写入）与命中两项；实现不支持缓存目录时两项都按无缓存读取。
/// </summary>
internal static class PreparationProbe
{
    private static readonly string[] Phases = ["prepare-cold", "prepare-warm"];

    public static async Task<JsonNode> RunAsync(string client, string cache)
    {
        var results = new JsonArray();
        try
        {
            foreach (var phase in Phases)
            {
                using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["--prepare-phase", phase, client, cache])
                    { RedirectStandardOutput = true, UseShellExecute = false })!;
                var output = await child.StandardOutput.ReadToEndAsync();
                await child.WaitForExitAsync();
                if (child.ExitCode != 0) throw new InvalidOperationException(phase + " 子进程失败：" + output);
                results.Add(JsonNode.Parse(output.Trim().Split('\n')[^1]));
            }
        }
        finally { if (System.IO.Directory.Exists(cache)) System.IO.Directory.Delete(cache, true); }
        return new JsonObject { ["clientBytes"] = new FileInfo(Path.Combine(client, "GameAssembly.dll")).Length, ["phases"] = results };
    }

    public static async Task RunPhaseAsync(string phase, string client, string cache)
    {
        using var process = Process.GetCurrentProcess();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        var basePrivate = process.PrivateMemorySize64; var allocated = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        var image = Path.Combine(client, TwClientFingerprint.ImageName);
        var verify = typeof(TwBuildVerification).GetMethod("EvaluateAsync", [typeof(string), typeof(string), typeof(CancellationToken)])!;
        var verdict = await (Task<TwBuildVerdict?>)verify.Invoke(null, [image, Directory(cache, "client-builds"), CancellationToken.None])!;
        Compact(verdict is { Registered: false });
        var read = typeof(ClientLayoutReader).GetMethods().First(value => value.Name == "Read" && value.GetParameters().FirstOrDefault()?.ParameterType == typeof(string)
            && value.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken)));
        var layout = (ClientCaptureLayout)read.Invoke(null, read.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(CancellationToken) ? CancellationToken.None : parameter.Name switch
            {
                "directory" => (object?)client, "cacheDirectory" => Directory(cache, "client-layouts"), _ => parameter.DefaultValue
            }).ToArray())!;
        Compact(true);
        try { layout = layout with { Resources = ClientResourceCatalog.Read(ClientItemCatalogLocation.Resolve(image), CancellationToken.None) }; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        var elapsed = clock.Elapsed.TotalMilliseconds;
        process.Refresh();
        var resources = layout.Resources;
        var (peakCommit, peakWorking, privateAfter) = (process.PeakPagedMemorySize64, process.PeakWorkingSet64, process.PrivateMemorySize64);
        layout = null!; GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); process.Refresh();
        var privateCollected = process.PrivateMemorySize64; var managedLive = GC.GetTotalMemory(false);
        GC.KeepAlive(resources);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            phase, registeredBuild = verdict?.Registered, elapsedMs = Math.Round(elapsed), peakCommitBytes = peakCommit,
            peakWorkingSetBytes = peakWorking, baselinePrivateBytes = basePrivate, privateBytesAfter = privateAfter,
            privateBytesAfterCollect = privateCollected, managedLiveBytesWithResources = managedLive,
            managedAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            compaction = typeof(NpcapObservationSource).GetMethod("ReleaseLayoutBuffers", BindingFlags.Static | BindingFlags.NonPublic) is not null
        }, ContractJson.Options));
    }

    private static string Directory(string cache, string name) { var path = Path.Combine(cache, name); System.IO.Directory.CreateDirectory(path); return path; }
    /// <summary>实现带布局准备后的强制压缩回收时按 Worker 的位置调用；当前实现没有该方法。</summary>
    private static void Compact(bool when)
    {
        if (when) typeof(NpcapObservationSource).GetMethod("ReleaseLayoutBuffers", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);
    }
}
