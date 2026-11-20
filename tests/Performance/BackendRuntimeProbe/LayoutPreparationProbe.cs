using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;

/// <summary>
/// 客户端布局准备的离线测量：每个调用方在独立子进程里对本机客户端文件（只读）执行一次，报告耗时、进程 CPU、
/// 峰值工作集、峰值提交（私有字节）、托管分配总量与托管堆峰值。子进程模式 <c>--layout-phase</c> 由本探针自行启动。
/// </summary>
internal static class LayoutPreparationProbe
{
    private static readonly string[] Phases = ["hash", "build-facts", "inventory-layout", "hash-then-layout", "hash-then-build-facts", "layout-cache-cold", "layout-cache-warm"];

    public static async Task RunAsync(string[] args)
    {
        if (args.Length < 3 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("参数：repository clientDirectory report [--label 名称]，路径须为绝对路径");
        var repository = Path.GetFullPath(args[0]); var client = Path.GetFullPath(args[1]); var report = Path.GetFullPath(args[2]);
        if (!report.StartsWith(Path.Combine(repository, "artifacts", "reports") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("报告须位于 artifacts/reports");
        var labelIndex = Array.IndexOf(args, "--label"); var label = labelIndex >= 0 ? args[labelIndex + 1] : "current";
        var cache = Path.Combine(repository, "artifacts", "tmp", "layout-cache-" + Guid.NewGuid().ToString("N"));
        var results = new List<JsonElement>();
        try
        {
            foreach (var phase in Phases)
            {
                using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["--layout-phase", phase, client, cache])
                    { RedirectStandardOutput = true, UseShellExecute = false })!;
                var output = await child.StandardOutput.ReadToEndAsync();
                await child.WaitForExitAsync();
                if (child.ExitCode != 0) throw new InvalidOperationException(phase + " 子进程失败：" + output);
                results.Add(JsonDocument.Parse(output.Trim().Split('\n')[^1]).RootElement.Clone());
                Console.WriteLine(phase + " 完成");
            }
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
        var files = new[] { "GameAssembly.dll", Path.Combine("MabinogiMobile_Data", "il2cpp_data", "Metadata", "global-metadata.dat"), "UnityPlayer.dll" }
            .Select(name => new { file = name.Replace('\\', '/'), bytes = new FileInfo(Path.Combine(client, name)).Length }).ToArray();
        var output2 = new
        {
            schemaVersion = 1, evidenceKind = "client-layout-preparation", label, measuredAtUtc = DateTimeOffset.UtcNow,
            environment = new
            {
                os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
                processor = OperatingSystem.IsWindows() ? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString() : null,
#if DEBUG
                configuration = "Debug",
#else
                configuration = "Release",
#endif
            },
            clientFiles = files,
            protocol = "每项在新子进程中执行一次（文件系统缓存为热态：前一项已读过同一文件）；峰值工作集与峰值提交取自进程计数器，托管堆峰值为 10 ms 轮询 GC.GetTotalMemory 的最大值。" +
                "hash＝绑定准备的三文件哈希；build-facts＝未登记构建核对读取客户端事实；inventory-layout＝Worker 库存布局读取；hash-then-* 为同一进程先哈希再读取（Worker 构建核对后准备库存布局的顺序）；" +
                "layout-cache-cold/warm 为带布局缓存目录的首次与命中读取（只在支持缓存的实现上有意义）。",
            phases = results
        };
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(output2, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
        Console.WriteLine(report);
    }

    public static async Task RunPhaseAsync(string[] args)
    {
        var phase = args[0]; var client = args[1]; var cache = args[2];
        var image = Path.Combine(client, TwClientFingerprint.ImageName);
        using var process = Process.GetCurrentProcess();
        var peakManaged = GC.GetTotalMemory(false); var sampling = true;
        var sampler = new Thread(() => { while (Volatile.Read(ref sampling)) { peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false)); Thread.Sleep(10); } }) { IsBackground = true };
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        var baseWorking = process.WorkingSet64; var basePrivate = process.PrivateMemorySize64;
        var allocated = GC.GetTotalAllocatedBytes(true); var cpu = process.TotalProcessorTime;
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        sampler.Start();
        var clock = Stopwatch.StartNew();
        string detail;
        switch (phase)
        {
            case "hash": detail = Describe(await TwClientFingerprint.HashAsync(image, CancellationToken.None)); break;
            case "build-facts": detail = Describe(ClientLayoutReader.ReadBuildFacts(client, TwBuildVerification.SchemaRoots, TwBuildVerification.Interfaces)); break;
            case "inventory-layout": detail = Describe(ClientLayoutReader.Read(client)); break;
            case "hash-then-layout":
                _ = await TwClientFingerprint.HashAsync(image, CancellationToken.None);
                detail = Describe(ClientLayoutReader.Read(client)); break;
            case "hash-then-build-facts":
                _ = await TwClientFingerprint.HashAsync(image, CancellationToken.None);
                detail = Describe(ClientLayoutReader.ReadBuildFacts(client, TwBuildVerification.SchemaRoots, TwBuildVerification.Interfaces)); break;
            case "layout-cache-cold" or "layout-cache-warm": detail = Describe(CachedRead(client, cache)); break;
            default: throw new ArgumentException("未知阶段 " + phase);
        }
        var elapsed = clock.Elapsed.TotalMilliseconds;
        Volatile.Write(ref sampling, false); sampler.Join();
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            phase, detail, elapsedMs = elapsed, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            peakWorkingSetBytes = process.PeakWorkingSet64, baselineWorkingSetBytes = baseWorking,
            peakCommitBytes = process.PeakPagedMemorySize64, baselinePrivateBytes = basePrivate,
            privateBytesAfter = process.PrivateMemorySize64,
            managedAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, peakManagedHeapBytes = peakManaged,
            gcCollections = new { gen0 = GC.CollectionCount(0) - gen0, gen1 = GC.CollectionCount(1) - gen1, gen2 = GC.CollectionCount(2) - gen2 }
        }, ContractJson.Options));
    }

    /// <summary>带缓存目录的布局读取；当前实现不支持缓存时退回普通读取。</summary>
    private static ClientCaptureLayout CachedRead(string client, string cache)
    {
        var method = typeof(ClientLayoutReader).GetMethods().FirstOrDefault(value => value.Name == "Read" &&
            value.GetParameters().Any(parameter => parameter.Name == "cacheDirectory"));
        if (method is null) return ClientLayoutReader.Read(client);
        var parameters = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "directory" => (object?)client, "cacheDirectory" => cache, "cancellation" => CancellationToken.None, _ => parameter.DefaultValue
        }).ToArray();
        return (ClientCaptureLayout)method.Invoke(null, parameters)!;
    }

    private static string Describe(TwClientHashes? value) => value is null ? "null" : value.AssemblySha256[..12] + "/" + value.MetadataSha256[..12];
    private static string Describe(ClientCaptureLayout value) =>
        $"{value.AssemblyHash[..12]}/{value.MetadataHash[..12]} opcodes={value.Opcodes.Count} methods={value.Methods.Count} schemaBytes={value.Schema.GetRawText().Length}";
    private static string Describe(ClientBuildFacts value) =>
        $"{value.AssemblyHash[..12]}/{value.MetadataHash[..12]} protocols={value.Protocols.Count} interfaces={value.Interfaces.Count} schemaBytes={value.Schema.GetRawText().Length}";
}
