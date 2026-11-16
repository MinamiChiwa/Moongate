using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class OfflineFaultMatrixTests
{
    [Fact]
    public async Task KilledSharedWorkerInterruptsCombatAndMusicCaptureReleasesActivitiesAndRequiresFreshRequests()
    {
        using var area = new GameObservationApiTests.Area();
        var profile = SyntheticCombat.Profile;
        area.Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), profile.Source.GameBuild!, new(0, 0, 800, 600), 96, 96, false));
        await using var app = area.Build("combat-normal", combatProfile: profile,
            storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: Path.Combine(area.Root, "portable")));
        await app.StartAsync();
        try
        {
            using var client = GameObservationApiTests.Area.Owner(app);
            using var sdk = new MabiToolClient(client.BaseAddress!, _ => ValueTask.FromResult(client.DefaultRequestHeaders.Authorization!.Parameter!));
            await GameObservationApiTests.BeginCapture(client, area, "fault-music");
            var combat = await sdk.StartCombatObservationAsync(new(area.Bindings.Current!.Session.Id), "fault-combat");
            var service = app.Services.GetRequiredService<GameObservationService>();
            try
            {
                await Wait(async () => (await sdk.GetCombatSnapshotAsync()).Data?.Encounter?.Summary.AttributedDamage.ToString() is { } amount && amount != "0");
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(JsonSerializer.Serialize(new { operation = await sdk.GetOperationAsync(combat.Id), snapshot = await sdk.GetCombatSnapshotAsync(), runtime = service.Runtime() }, ContractJson.Options));
            }
            var pid = service.Runtime().WorkerProcessId!.Value;
            using (var process = Process.GetProcessById(pid))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await Wait(async () => (await sdk.GetOperationAsync(combat.Id)).State.KnownValue == OperationState.Interrupted);
            await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null && service.Runtime().SubscriberCount == 0 &&
                app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities.Count == 0));
            var snapshot = (await sdk.GetCombatSnapshotAsync()).Data!;
            Assert.True(snapshot.Encounter!.Summary.Final);
            Assert.Equal(ObservationQuality.Partial, snapshot.Encounter.Summary.Quality.KnownValue);
            Assert.Equal((double?)(snapshot.Encounter.Summary.DpsDurationMs is > 0 ? (double)snapshot.Encounter.Summary.AttributedDamage.Value * 1000 / snapshot.Encounter.Summary.DpsDurationMs.Value : null), snapshot.Encounter.Summary.EncounterDps);
            Assert.Single((await sdk.GetCombatEncountersAsync()).Items);
            Assert.Single(Directory.GetFiles(area.Root, "capture-start-*"));
            Assert.Null(service.Snapshot().Latest);
            Assert.Null(service.Snapshot().CaptureId);
            Assert.Empty(area.Device.Commands);
            Assert.NotEmpty((await sdk.GetModulesAsync()).Items);
            var next = await GameObservationApiTests.BeginCapture(client, area, "fresh-after-fault");
            await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is { } replacement && replacement != pid));
            await GameObservationApiTests.EndCapture(client, next.Id);
            await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
            Assert.Equal(OperationState.Interrupted, (await sdk.GetOperationAsync(combat.Id)).State.KnownValue);
            Assert.Equal(snapshot.Encounter.Summary.AttributedDamage, (await sdk.GetCombatSnapshotAsync()).Data!.Encounter!.Summary.AttributedDamage);
            // Worker 结束后才发出的生命周期记录由诊断记录器在后台写入，写入期间持有 diagnostic.write 活动；
            // 排空写入队列后活动必须清空，未释放的活动会让等待超时失败。
            var recorder = app.Services.GetRequiredService<MabiTool.Host.Diagnostics.DiagnosticRecorder>();
            await Wait(async () =>
            {
                await recorder.FlushAsync();
                return app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities.Count == 0;
            });
        }
        finally { await app.StopAsync(); }
    }

    private static async Task Wait(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
}
