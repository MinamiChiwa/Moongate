using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Inventory;
using MabiTool.Providers.TwObservation.Combat;

[assembly: SupportedOSPlatform("windows")]
var root = Path.GetFullPath(args[0]); var mode = args.ElementAtOrDefault(1) ?? "normal";
File.WriteAllText(Path.Combine(root, "process-start-" + Environment.ProcessId), "started");
await ObservationWorkerApplication.RunAsync(new InventoryFixtureSource(root, mode));

sealed class InventoryFixtureSource(string root, string mode) : IObservationCaptureSource
{
    private static readonly ObservationSource Source = new("mabiagent-inventory", "1.0.0", "TW", TwClientFingerprint.GameBuild);
    private static readonly ClientCaptureLayout Layout = new(Tw20261110CombatProtocol.AssemblySha256, Tw20261110CombatProtocol.MetadataSha256, new(1, 2, 3), JsonSerializer.SerializeToElement(new { types = new { } }));
    private static readonly ObservationInventoryProfile Profile = new("fixture-inventory", Source, Layout.AssemblyHash, Layout.MetadataHash, ["EquipmentTool"], true, true, true);
    private bool ready;
    private int runs;
    public IReadOnlyList<ObservationCombatProfile> CombatProfiles => [new("fixture-combat", Source, Simulation: true)];
    public IReadOnlyList<CaptureChannelOption> Channels() => [new(new string('1', 32), "库存离线通道", ["127.0.0.1"])];
    public ClientCaptureLayout? InventoryLayoutFor(ObservationCaptureRequest request) => ready ? Layout : null;
    public ObservationInventoryProfile? InventoryProfileFor(ObservationCaptureRequest request) => ready ? Profile : null;
    public async Task<InventoryClientPrepared> PrepareInventoryAsync(InventoryClientRequest request, CancellationToken ct)
    {
        File.WriteAllText(Path.Combine(root, "prepare-start"), "started");
        if (mode == "slow-prepare") await Task.Delay(3000, ct);
        if (mode == "failed-prepare") throw new InvalidDataException("fixture");
        ct.ThrowIfCancellationRequested(); ready = true;
        if (mode == "host-old-fingerprint") return new(Profile with { Source = Source with { GameBuild = "sha256:" + Layout.AssemblyHash + ":" + Layout.MetadataHash } });
        if (mode == "host-mismatched-fingerprint") return new(Profile with { AssemblySha256 = new string('a', 64) });
        return new(Profile);
    }
    public async Task<InventoryCatalogData> ReadInventoryCatalogAsync(InventoryClientRequest request, CancellationToken ct)
    {
        File.WriteAllText(Path.Combine(root, "catalog-start"), "started");
        if (mode == "slow-catalog") await Task.Delay(Timeout.Infinite, ct);
        if (mode == "failed-catalog") throw new InvalidDataException("fixture");
        return new("fixture-catalog", "TW", new string('a', 64), DateTimeOffset.UtcNow, [],
            Enumerable.Range(1, 2000).Select(i => new InventoryCatalogEntry(new("EquipmentTool", new((ulong)i)),
                Name(i), Name(i), "EquipmentTool", new((ulong)i), new(0), new(0), new(0), true)).ToArray(), [],
            [new("EquipmentTool", "EquipmentTool", true, 2000)]);
    }
    // debugtool 模式使用符合 Host 目录校验的短名称，其余行为与 normal 相同。
    private string Name(int i) => mode == "debugtool" ? "合成乐器" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) : new string('南', 256);
    public Task CaptureAsync(ObservationCaptureRequest request, Action<GameCaptureReport> publish, CancellationToken ct) =>
        CaptureSharedAsync(request, new(CombatProfiles, publish, _ => { }), ct);
    public async Task CaptureSharedAsync(ObservationCaptureRequest request, ObservationCaptureContext context, CancellationToken ct)
    {
        var run = ++runs; File.WriteAllText(Path.Combine(root, "capture-start-" + run), request.GameSessionId);
        using var handle = new FileStream(Path.Combine(root, "capture-handle"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var registration = context.OnChanging((_, next) => File.WriteAllText(Path.Combine(root, "interests"), JsonSerializer.Serialize(next, ContractJson.Options)));
        var musicGeneration = Guid.NewGuid().ToString("N"); ulong music = 0, combat = 0, inventory = 0; InventoryCaptureReport? retired = null;
        string? inventoryInterest = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested(); var mono = Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency); var utc = DateTimeOffset.UtcNow;
                context.Execute(() =>
                {
                    var current = context.Current;
                    if (current.MusicEnabled) context.Publish(new GameCaptureReport(new(musicGeneration, new(new(9007199254740993), "3", "南千和", new(11)), null, null, new(++music), "合成音乐"), new(music), true, mono, utc, null, Source));
                    if (current.Combat is { } observed)
                    {
                        var local = new CombatEntityObservation(new(request.GameSessionId, new(1), new(11), null),
                            "南千和", "南千和", CombatEntityRole.LocalPlayer, null, false, true, ObservationQuality.Confirmed);
                        context.Publish(new CombatCaptureReport(1, observed.Id, local,
                            new(1, Source, request.GameSessionId, new(1), new(++combat), utc, mono, [local], [], [], [], [])));
                    }
                    if (inventoryInterest != current.Inventory?.Id)
                    {
                        inventoryInterest = current.Inventory?.Id; inventory = 0;
                        if (retired is not null && (mode is "late" or "host-late"))
                        { context.Publish(retired with { Frame = retired.Frame with { Sequence = new(999999) } }); File.WriteAllText(Path.Combine(root, "late-attempt"), "attempted"); }
                    }
                    if (current.Inventory is { } interest && (mode is not ("large" or "crash") || inventory == 0))
                    {
                        var count = mode is "large" or "crash" ? 100 : 1;
                        var items = Enumerable.Range(1, count).Select(i => new InventoryItem("EquipmentTool", InventoryContainer.Backpack,
                            i.ToString(System.Globalization.CultureInfo.InvariantCulture), new(42), new((ulong)i), new(1),
                            JsonSerializer.SerializeToElement(new { payload = mode is "large" or "crash" ? new string('x', 8192) : "fixture" }), "EquipmentTool")).ToArray();
                        var report = new InventoryCaptureReport(1, interest.Id, new(1, Source, request.GameSessionId, new(1), new(++inventory), utc, mono,
                            InventoryFrameKind.Full, new("7", "9007199254740993", "南千和", "南千和"), items, [], [new("EquipmentTool", InventoryContainer.Backpack, true)]));
                        if (mode == "host-source-fault" && File.Exists(Path.Combine(root, "inject-fault"))) report = report with { Frame = report.Frame with { Source = Source with { Provider = "wrong-source" } } };
                        // 读模型拒绝的帧：非乐谱条目表编号为 0（itemIdentity），Worker 校验照常通过。
                        if (mode == "host-rejected-frame" && File.Exists(Path.Combine(root, "inject-fault"))) report = report with { Frame = report.Frame with { Items = items.Select(i => i with { TypeId = new(0) }).ToArray() } };
                        if (mode == "host-old-generation")
                        {
                            var injected = File.Exists(Path.Combine(root, "inject-fault"));
                            report = report with { Frame = report.Frame with { Generation = new(injected ? 11UL : 10UL), Items = items.Select(i => i with { Quantity = new(injected ? 2UL : 1UL) }).ToArray() } };
                            context.Publish(report);
                            if (injected) context.Publish(report with { Frame = report.Frame with { Generation = new(10), Items = items.Select(i => i with { Quantity = new(999999) }).ToArray() } });
                        }
                        else context.Publish(report); retired = report;
                    }
                });
                if (mode == "crash" && File.Exists(Path.Combine(root, "crash-now"))) Environment.Exit(17);
                await Task.Delay(100, ct);
            }
        }
        finally { File.WriteAllText(Path.Combine(root, "capture-stopped-" + run), "closed"); }
    }
}
