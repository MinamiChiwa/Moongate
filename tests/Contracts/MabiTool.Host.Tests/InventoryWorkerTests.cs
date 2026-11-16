using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Observation.Worker;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
[Collection("Persistent backend")]
public sealed class InventoryWorkerTests
{
    [Fact]
    public async Task ConsecutiveInterestCommandsKeepHeartbeatAndReleaseTheSingleCaptureHandle()
    {
        using var area = new GameObservationApiTests.Area(); var failures = new ConcurrentQueue<WorkerFailure>();
        await using var worker = await WorkerSession.StartAsync(Options(area, "normal"), failures.Enqueue);
        var call = await worker.BeginAsync(CaptureRequest()); var (events, drain) = Read(call);
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("report", out _)));
        await worker.CommandAsync(call, id => new { commandId = id, method = "inventory.prepare", arguments = ClientRequest() },
            result => Assert.True(result.TryGetProperty("prepared", out _)));
        var inventory = new ObservationInventoryInterest(Guid.NewGuid().ToString("N"), "fixture-inventory");
        var final = new ObservationCaptureInterests(true, null, inventory);
        for (var index = 0; index < 32; index++)
            await Interests(worker, call, index % 2 == 0 ? new(false, null, null) : final);
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("inventory", out _)));
        var applied = events.Last(e => e.Payload.TryGetProperty("interestsApplied", out _)).Payload.GetProperty("interestsApplied")
            .Deserialize<ObservationCaptureInterests>(ContractJson.Options);
        Assert.Equal(final, applied); Assert.True(worker.IsAlive); Assert.Empty(failures);
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*"));
        Assert.Throws<IOException>(() => new FileStream(Path.Combine(area.Root, "capture-handle"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        await worker.CancelAsync(call); Assert.Equal("cancelled", (await call.Completion).Kind); await drain;
        using var released = new FileStream(Path.Combine(area.Root, "capture-handle"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(File.Exists(Path.Combine(area.Root, "capture-stopped-1"))); Assert.Empty(failures);
    }

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData("late")]
    [InlineData("slow-prepare")]
    public async Task RunningCapturePreparesInventoryWithoutBlockingHeartbeatAndStopsInterestsIndependently(string mode)
    {
        using var area = new GameObservationApiTests.Area(); var failures = new ConcurrentQueue<WorkerFailure>();
        await using var worker = await WorkerSession.StartAsync(Options(area, mode), failures.Enqueue);
        var call = await worker.BeginAsync(CaptureRequest()); var (events, drain) = Read(call);
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("report", out _)));
        var watch = Stopwatch.StartNew();
        var prepared = await worker.CommandAsync(call, id => new { commandId = id, method = "inventory.prepare", arguments = ClientRequest() },
            result => Assert.True(result.TryGetProperty("prepared", out _)), commandTimeout: TimeSpan.FromSeconds(10));
        Assert.Equal("fixture-inventory", prepared.GetProperty("prepared").GetProperty("profile").GetProperty("id").GetString());
        if (mode == "slow-prepare") Assert.True(watch.ElapsedMilliseconds >= 2500);
        Assert.True(worker.IsAlive); Assert.Empty(failures);
        var inventory = Guid.NewGuid().ToString("N"); var combat = Guid.NewGuid().ToString("N");
        await Interests(worker, call, new(true, new(combat, "fixture-combat", true, false, false), new(inventory, "fixture-inventory")));
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("inventory", out _)) && events.Any(e => e.Payload.TryGetProperty("combat", out _)));
        await Interests(worker, call, new(true, new(combat, "fixture-combat", true, false, false)));
        if (mode == "late") await Wait(() => File.Exists(Path.Combine(area.Root, "late-attempt")));
        Assert.DoesNotContain(events, e => e.Payload.TryGetProperty("inventory", out var report) && report.GetProperty("frame").GetProperty("sequence").GetString() == "999999");
        await Interests(worker, call, new(false, new(combat, "fixture-combat", true, false, false)));
        var before = events.Count(e => e.Payload.TryGetProperty("combat", out _));
        await Wait(() => events.Count(e => e.Payload.TryGetProperty("combat", out _)) > before);
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("interestsApplied", out var applied) && !applied.GetProperty("musicEnabled").GetBoolean()));
        var delivered = events.ToArray();
        var stoppedMusic = Array.FindLastIndex(delivered, e => e.Payload.TryGetProperty("interestsApplied", out var applied) && !applied.GetProperty("musicEnabled").GetBoolean());
        Assert.DoesNotContain(delivered.Skip(stoppedMusic + 1), e => e.Payload.TryGetProperty("report", out _) || e.Payload.TryGetProperty("inventory", out _));
        await worker.CancelAsync(call); Assert.Equal("cancelled", (await call.Completion).Kind); await drain;
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*"));
        Assert.True(File.Exists(Path.Combine(area.Root, "capture-stopped-1"))); Assert.Empty(failures);
    }
    [Fact]
    public async Task FailedInventoryPreparationKeepsMusicCaptureAlive()
    {
        using var area = new GameObservationApiTests.Area();
        await using var worker = await WorkerSession.StartAsync(Options(area, "failed-prepare"), _ => { });
        var call = await worker.BeginAsync(CaptureRequest()); var (events, drain) = Read(call);
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("report", out _)));
        var result = await worker.CommandAsync(call, id => new { commandId = id, method = "inventory.prepare", arguments = ClientRequest() },
            value => Assert.Equal("inventory.buildUnsupported", value.GetProperty("code").GetString()));
        Assert.Equal("inventory.buildUnsupported", result.GetProperty("code").GetString());
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("report", out _))); Assert.True(worker.IsAlive);
        await worker.CancelAsync(call); await drain;
    }
    [Fact]
    public async Task LargeInventoryAndCatalogUseVerifiedAssetsAndRemoveConsumedFiles()
    {
        using var area = new GameObservationApiTests.Area();
        await using var worker = await WorkerSession.StartAsync(Options(area, "large"), _ => { });
        var call = await worker.BeginAsync(CaptureRequest()); var (events, drain) = Read(call);
        await worker.CommandAsync(call, id => new { commandId = id, method = "inventory.prepare", arguments = ClientRequest() }, _ => { });
        await Interests(worker, call, new(false, null, new(Guid.NewGuid().ToString("N"), "fixture-inventory")));
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("inventoryAsset", out _)));
        var artifact = events.First(e => e.Payload.TryGetProperty("inventoryAsset", out _)).Payload.GetProperty("inventoryAsset").Deserialize<InventoryCaptureArtifact>(ContractJson.Options)!;
        Assert.True(artifact.Bytes.Value > InventoryAssetStore.InlineReportBytes);
        var report = await InventoryWorkerAssets.ReadAsync<InventoryCaptureReport>(area.Root, artifact.FileName, artifact.Bytes, artifact.Sha256, CancellationToken.None);
        Assert.Equal(100, report.Frame.Items.Count); Assert.Equal(1UL, report.Frame.BindingRevision!.Value.Value);
        Assert.False(File.Exists(Path.Combine(area.Root, artifact.FileName)));
        await worker.CancelAsync(call); await drain;
        var catalogCall = await worker.BeginAsync(JobRequest("inventory.catalog")); var (_, catalogDrain) = Read(catalogCall);
        var result = await catalogCall.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await catalogDrain;
        Assert.Equal("result", result.Kind);
        var catalogAsset = result.Payload.GetProperty("catalogAsset").Deserialize<InventoryCatalogArtifact>(ContractJson.Options)!;
        Assert.True(catalogAsset.Bytes.Value > WorkerProtocol.MaximumFrameBytes);
        var catalog = await InventoryWorkerAssets.ReadAsync<InventoryCatalogData>(area.Root, catalogAsset.FileName, catalogAsset.Bytes, catalogAsset.Sha256, CancellationToken.None);
        Assert.Equal(2000, catalog.Entries.Count); Assert.Empty(Assets(area.Root));
    }
    [Theory]
    [InlineData("slow-catalog")]
    [InlineData("failed-catalog")]
    public async Task CatalogCancellationAndFailureLeaveNoCommittedAssets(string mode)
    {
        using var area = new GameObservationApiTests.Area();
        await using var worker = await WorkerSession.StartAsync(Options(area, mode), _ => { });
        var call = await worker.BeginAsync(JobRequest("inventory.catalog")); var (_, drain) = Read(call);
        await Wait(() => File.Exists(Path.Combine(area.Root, "catalog-start")));
        if (mode == "slow-catalog") await worker.CancelAsync(call);
        var result = await call.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await drain;
        Assert.Equal(mode == "slow-catalog" ? "cancelled" : "error", result.Kind);
        Assert.Empty(Assets(area.Root)); Assert.True(worker.IsAlive);
    }
    [Fact]
    public async Task WorkerCrashFinishesRequestAndReleasesProcessOwnedCaptureHandle()
    {
        using var area = new GameObservationApiTests.Area(); var failures = new ConcurrentQueue<WorkerFailure>();
        await using var worker = await WorkerSession.StartAsync(Options(area, "crash"), failure =>
            { failures.Enqueue(failure); InventoryWorkerAssets.CleanDirectory(area.Root); });
        var call = await worker.BeginAsync(CaptureRequest()); var (events, drain) = Read(call);
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("report", out _)));
        await worker.CommandAsync(call, id => new { commandId = id, method = "inventory.prepare", arguments = ClientRequest() }, _ => { });
        await Interests(worker, call, new(true, null, new(Guid.NewGuid().ToString("N"), "fixture-inventory")));
        await Wait(() => events.Any(e => e.Payload.TryGetProperty("inventoryAsset", out _))); Assert.Single(Assets(area.Root));
        await File.WriteAllTextAsync(Path.Combine(area.Root, "crash-now"), "exit");
        await Assert.ThrowsAsync<WorkerException>(() => call.Completion.WaitAsync(TimeSpan.FromSeconds(10))); await drain;
        await Wait(() => !worker.IsAlive); Assert.Single(failures);
        using var released = new FileStream(Path.Combine(area.Root, "capture-handle"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(Assets(area.Root));
    }
    [Fact]
    public void QueueCapacityInvalidatesLostGenerationAndAcceptsNextFullGeneration()
    {
        var queue = new InventoryCaptureQueue();
        for (ulong i = 1; i <= 64; i++) Assert.True(queue.TryWrite(Report(1, i)));
        Assert.False(queue.TryWrite(Report(1, 65))); Assert.False(queue.TryWrite(Report(1, 66)));
        Assert.True(queue.TryWrite(Report(2, 1)));
        var messages = queue.Take(); Assert.Equal(2, messages.Count);
        Assert.Equal(InventoryFrameKind.Invalidated, messages[0].Frame.Kind.KnownValue);
        Assert.Equal("inventory.transportCapacity", messages[0].Frame.Reason);
        Assert.Equal(2UL, messages[1].Frame.Generation.Value); Assert.Equal(InventoryFrameKind.Full, messages[1].Frame.Kind.KnownValue);
    }
    [Fact]
    public async Task AssetsRejectBadDigestLengthJsonAndPathAndCleanCancellationAndOwnedFiles()
    {
        using var area = new GameObservationApiTests.Area();
        using var store = new InventoryAssetStore(area.Root);
        var report = Report(1, 1);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ContractJson.Options);
        var digest = await store.WriteAsync(bytes, CancellationToken.None);
        var badHash = await Assert.ThrowsAsync<OperationException>(() => InventoryWorkerAssets.ReadAsync<InventoryCaptureReport>(area.Root, digest.FileName, digest.Bytes, new string('0', 64), CancellationToken.None));
        Assert.Equal("inventory.protocolInvalid", badHash.Code); Assert.Empty(Assets(area.Root));
        var length = await store.WriteAsync(bytes, CancellationToken.None);
        await Assert.ThrowsAsync<OperationException>(() => InventoryWorkerAssets.ReadAsync<InventoryCaptureReport>(area.Root, length.FileName, new(length.Bytes.Value + 1), length.Sha256, CancellationToken.None));
        var malformed = await store.WriteAsync([123], CancellationToken.None);
        await Assert.ThrowsAsync<OperationException>(() => InventoryWorkerAssets.ReadAsync<InventoryCaptureReport>(area.Root, malformed.FileName, malformed.Bytes, malformed.Sha256, CancellationToken.None));
        await Assert.ThrowsAsync<OperationException>(() => InventoryWorkerAssets.ReadAsync<InventoryCaptureReport>(area.Root, "../outside.json", new(1), new string('a', 64), CancellationToken.None));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(bytes, cancel.Token)); Assert.Empty(Assets(area.Root));
        var cancelledRead = await store.WriteAsync(bytes, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InventoryWorkerAssets.ReadAsync<InventoryCaptureReport>(area.Root,
            cancelledRead.FileName, cancelledRead.Bytes, cancelledRead.Sha256, cancel.Token)); Assert.Empty(Assets(area.Root));
        var abandoned = await store.WriteAsync(bytes, CancellationToken.None); store.Dispose();
        Assert.False(File.Exists(Path.Combine(area.Root, abandoned.FileName)));
    }
    [Fact]
    public async Task AssetFileAndTotalByteBudgetsRejectAdditionalWrites()
    {
        using var area = new GameObservationApiTests.Area();
        var filesRoot = Path.Combine(area.Root, "files"); Directory.CreateDirectory(filesRoot);
        using (var store = new InventoryAssetStore(filesRoot))
        {
            for (var i = 0; i < InventoryAssetStore.MaximumFiles; i++) await store.WriteAsync([123, 125], CancellationToken.None);
            var error = await Assert.ThrowsAsync<WorkerException>(() => store.WriteAsync([123, 125], CancellationToken.None));
            Assert.Equal("worker.assetInvalid", error.Code); Assert.Equal(8, Assets(filesRoot).Count());
        }
        Assert.Empty(Assets(filesRoot));
        var bytesRoot = Path.Combine(area.Root, "bytes"); Directory.CreateDirectory(bytesRoot);
        using (var existing = new FileStream(Path.Combine(bytesRoot, Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew)) existing.SetLength(InventoryAssetStore.MaximumBytes);
        using var bounded = new InventoryAssetStore(bytesRoot);
        await Assert.ThrowsAsync<WorkerException>(() => bounded.WriteAsync([123, 125], CancellationToken.None)); Assert.Single(Assets(bytesRoot));
    }
    private static InventoryCaptureReport Report(ulong generation, ulong sequence) => new(1, "fixture-interest", new(1,
        new("fixture", "1", "TW", "synthetic"), "fixture-game", new(generation), new(sequence), DateTimeOffset.UtcNow, 1,
        InventoryFrameKind.Full, new("7", "8", "南千和", "南千和"), [], [], [new("EquipmentTool", InventoryContainer.Backpack, true)]));
    private static WorkerProcessOptions Options(GameObservationApiTests.Area area, string mode)
    {
        var flavor = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var executable = Path.Combine(GameObservationApiTests.Area.FindRoot(), "artifacts", "build", "bin", "MabiTool.InventoryObservationProbe", flavor, "MabiTool.InventoryObservationProbe.exe");
        return new(executable, Path.GetDirectoryName(executable)!, area.Root, "game-observation")
        { Arguments = [area.Root, mode], HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(2), CancellationGrace = TimeSpan.FromSeconds(5) };
    }
    private static InventoryClientRequest ClientRequest()
    {
        using var process = Process.GetCurrentProcess();
        return new("fixture-game", process.Id, new(process.StartTime.ToUniversalTime()));
    }
    private static WorkerRequest CaptureRequest()
    {
        var client = ClientRequest(); return new("observation.capture", JsonSerializer.SerializeToElement(new ObservationCaptureRequest(client.GameSessionId, "TW", client.ProcessId,
            client.ProcessStartTime, new string('1', 32), "tw-music-v1"), ContractJson.Options), Guid.NewGuid().ToString("N"), "1", "0");
    }
    private static WorkerRequest JobRequest(string method) => new(method, JsonSerializer.SerializeToElement(ClientRequest(), ContractJson.Options), Guid.NewGuid().ToString("N"), "1", "0");
    private static Task<JsonElement> Interests(WorkerSession worker, WorkerCall call, ObservationCaptureInterests interests) => worker.CommandAsync(call,
        id => new { commandId = id, method = "observation.interests", arguments = interests }, result => Assert.True(result.GetProperty("accepted").GetBoolean()));
    private static (ConcurrentQueue<WorkerFrame> Events, Task Drain) Read(WorkerCall call)
    {
        var events = new ConcurrentQueue<WorkerFrame>();
        var drain = Task.Run(async () => { try { await foreach (var frame in call.Events.ReadAllAsync()) events.Enqueue(frame); } catch (WorkerException) { } });
        return (events, drain);
    }
    private static IEnumerable<string> Assets(string root) => Directory.EnumerateFiles(root, "*.json").Where(p => Path.GetFileName(p) is { Length: 37 } name && WorkerProtocol.IsId(name[..32]));
    private static async Task Wait(Func<bool> predicate)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!predicate()) await Task.Delay(20, deadline.Token); }
}
