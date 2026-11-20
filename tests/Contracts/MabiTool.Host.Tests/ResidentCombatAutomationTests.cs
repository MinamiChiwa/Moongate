using System.Diagnostics;
using System.Net.Http.Json;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Combat;
using MabiTool.Host.Game;
using MabiTool.Host.Storage;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class ResidentCombatAutomationTests
{
    [Fact]
    public async Task AutomaticCombatWaitsForIdentityReusesItAndRecoversAfterMapChange()
    {
        using var area = Current();
        await File.WriteAllTextAsync(Path.Combine(area.Root, "identity-hold"), "waiting");
        await using var app = Build(area);
        await using var stop = new StopApplication(app);
        await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        using var sdk = Sdk(client);
        var capture = app.Services.GetRequiredService<GameObservationService>();
        var waiting = await Until(() => sdk.GetCombatAutoObservationAsync(), state => state.State == "waiting" && state.ObservationId is not null);
        await Until(() => Operation(client, waiting.ObservationId!), operation => operation.Phase == "identity-pending");
        Assert.Equal(1, capture.Runtime().SubscriberCount);
        Assert.Null(capture.Snapshot().Latest?.Value.Identity);

        File.Delete(Path.Combine(area.Root, "identity-hold"));
        var first = await Until(() => sdk.GetCombatSnapshotAsync(), snapshot => snapshot.Data?.Encounter is not null);
        Assert.Equal(1, Counter(area, "identity-feeds"));
        Assert.Equal(first.Data!.Observation.LocalEntity.RawInstanceId, capture.Snapshot().Latest!.Value.Identity!.ActorId);
        var pid = capture.Runtime().WorkerProcessId!.Value;
        var captureId = capture.Snapshot().CaptureId;
        await SetAuto(sdk, false, "resident-off-before-map");
        await Until(() => Task.FromResult(capture.Runtime()), runtime => runtime.SubscriberCount == 0);
        Assert.Equal("running", capture.Runtime().State);
        var previousDamage = Counter(area, "damage-feeds");

        await SetAuto(sdk, true, "resident-on-before-map");
        var resumed = await Until(() => sdk.GetCombatSnapshotAsync(), snapshot => snapshot.Data is { Encounter: not null } data &&
            data.Observation.Id != first.Data.Observation.Id);
        Assert.Equal(1, Counter(area, "identity-feeds"));
        Assert.Equal(pid, capture.Runtime().WorkerProcessId);
        Assert.Equal(captureId, capture.Snapshot().CaptureId);
        var oldGeneration = resumed.Data!.Observation.LocalEntity.ObservationGeneration;
        var oldEncounter = resumed.Data.Encounter!;
        await File.WriteAllTextAsync(Path.Combine(area.Root, "switch-connection"), "map changed");
        var mapped = await Until(() => sdk.GetCombatSnapshotAsync(), snapshot => snapshot.Data is { Encounter: not null } data &&
            data.Observation.LocalEntity.ObservationGeneration != oldGeneration && data.Encounter.Segment?.StartReason == "map-change");
        Assert.Equal(2, Counter(area, "identity-feeds"));
        Assert.NotEqual(oldEncounter.Id, mapped.Data!.Encounter!.Id);
        Assert.True((await sdk.GetCombatEncounterAsync(oldEncounter.Id)).Summary.Final);

        await File.WriteAllTextAsync(Path.Combine(area.Root, "pause-damage"), "pause");
        await Until(() => Task.FromResult(File.Exists(Path.Combine(area.Root, "damage-paused"))), paused => paused);
        var emitted = Counter(area, "damage-feeds") - previousDamage;
        // 切图使整场到期，切图后的分段开启新的一场：观察范围（当前整场）只含切图后的分段；切图前后两段合计等于重新开启后发出的全部伤害。
        var before = await sdk.QueryCombatRangeAsync(new("encounter", oldEncounter.Id));
        var range = await Until(() => sdk.QueryCombatRangeAsync(new("observation", ObservationId: resumed.Data.Observation.Id)),
            value => value.Totals.EventCount.Value + before.Totals.EventCount.Value == (ulong)emitted);
        Assert.Equal([mapped.Data!.Encounter!.Id], range.EncounterIds);
        Assert.Equal(new BigInteger(emitted) * 4294967395L, range.RangeDamage.Value + before.RangeDamage.Value);
        Assert.Empty(area.Device.Commands);
        await SetAuto(sdk, false, "resident-off-after-map");
        await Until(() => Task.FromResult(capture.Runtime()), runtime => runtime.SubscriberCount == 0);
        await app.StopAsync();
        await Until(() => Task.FromResult(Alive(pid)), alive => !alive);
        Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
    }

    [Fact]
    public async Task ManualDemandHoldsTheBarrierAndMaintenanceRestartsResidentCombat()
    {
        using var area = Current();
        await using var app = Build(area);
        await using var stop = new StopApplication(app);
        await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        using var sdk = Sdk(client);
        var capture = app.Services.GetRequiredService<GameObservationService>();
        var barrier = app.Services.GetRequiredService<ActivityBarrier>();
        var first = await Until(() => sdk.GetCombatSnapshotAsync(), snapshot => snapshot.Data?.Encounter is not null);
        var oldId = first.Data!.Observation.Id;
        var oldCapture = capture.Snapshot().CaptureId;
        var oldPid = capture.Runtime().WorkerProcessId!.Value;
        await Until(() => Task.FromResult(barrier.Snapshot()), state => state.Activities.Count == 0);
        Assert.False(Interests(area).MusicEnabled);

        // 常驻捕获打开全部网卡；手动音乐需求不指定网卡，加入同一捕获。
        var music = await GameObservationApiTests.JoinCapture(client, area, "resident-music-lifetime");
        Assert.Single(barrier.Snapshot().Activities, activity => activity.Kind == "game.observation");
        Assert.Equal(2, capture.Runtime().SubscriberCount);
        Assert.True(Interests(area).MusicEnabled);
        var identified = capture.Snapshot().Latest!.Value.Identity;
        var released = await sdk.ReleaseGameObservationAsync(music.Id);
        Assert.True(released.Released);
        Assert.NotNull(released.Snapshot.Latest?.Value.Identity);
        Assert.Equal(identified, released.Snapshot.Latest.Value.Identity);
        Assert.Null(released.Snapshot.Latest.Value.Music);
        Assert.Null(released.Snapshot.Latest.Value.Concert);
        await Until(() => Task.FromResult(barrier.Snapshot()), state => state.Activities.Count == 0);
        Assert.False(Interests(area).MusicEnabled);
        Assert.Equal(1, capture.Runtime().SubscriberCount);
        Assert.Equal(oldCapture, capture.Snapshot().CaptureId);
        Assert.Equal(oldPid, capture.Runtime().WorkerProcessId);
        Assert.Equal(oldId, (await sdk.GetCombatAutoObservationAsync()).ObservationId);

        var measurement = await sdk.StartCombatMeasurementAsync(new(oldId, 60000, 0), "resident-measure-maintenance");
        await Until(() => sdk.GetCombatMeasurementAsync(), state => state.MeasurementId == measurement.MeasurementId &&
            state.Damage is { } damage && damage.Value > BigInteger.Zero);

        using (await Maintenance(barrier))
        {
            await Until(() => Task.FromResult(capture.Runtime()), runtime => runtime.State == "stopped" && runtime.WorkerProcessId is null);
            await Until(() => Task.FromResult(app.Services.GetRequiredService<CombatOperations>().DiagnosticState()), state => state.Active == 0);
            await Until(() => sdk.GetCombatAutoObservationAsync(), state => state.State == "off");
            Assert.Equal(0, capture.Runtime().SubscriberCount);
            await Until(() => Task.FromResult(Alive(oldPid)), alive => !alive);
        }
        var restored = await Until(() => sdk.GetCombatSnapshotAsync(), snapshot => snapshot.Data is { Encounter: not null } data && data.Observation.Id != oldId);
        Assert.Equal(OperationState.Interrupted, (await Operation(client, oldId)).State.KnownValue);
        Assert.Equal("update-maintenance", (await sdk.GetCombatEncounterAsync(first.Data.Encounter!.Id)).Summary.BoundaryReason);
        Assert.NotEqual(oldCapture, capture.Snapshot().CaptureId);
        Assert.NotEqual(oldPid, capture.Runtime().WorkerProcessId);
        Assert.Equal("observing", (await Until(() => sdk.GetCombatAutoObservationAsync(), state => state.State == "observing")).State);
        Assert.NotEqual(oldId, restored.Data!.Observation.Id);
        var settled = await Until(() => sdk.GetCombatMeasurementAsync(), state => state.LastResultId == measurement.MeasurementId);
        Assert.False(settled.LastResult!.Complete);
        Assert.True(settled.LastResult.Damage.Value > BigInteger.Zero);
        var savedMeasurement = await sdk.GetCombatEncounterAsync(measurement.MeasurementId!);
        Assert.Equal("timed-measurement", savedMeasurement.Record!.Kind);
        Assert.Equal(ObservationQuality.Partial, savedMeasurement.Summary.Quality.KnownValue);
        Assert.Contains((await sdk.QueryCombatTimelineAsync(savedMeasurement.Id, new())).Items, item => item.Damage is not null);
        Assert.Empty(area.Device.Commands);
    }

    [Fact]
    public async Task ShutdownDuringMaintenanceReleasesTheWorkerAndRecoveryStartsANewObservation()
    {
        using var area = Current();
        string oldId;
        await using (var app = Build(area))
        {
            await using var stop = new StopApplication(app);
            await app.StartAsync();
            using var client = GameObservationApiTests.Area.Owner(app);
            using var sdk = Sdk(client);
            var first = await Until(() => sdk.GetCombatSnapshotAsync(), snapshot => snapshot.Data?.Encounter is not null);
            oldId = first.Data!.Observation.Id;
            var capture = app.Services.GetRequiredService<GameObservationService>();
            var pid = capture.Runtime().WorkerProcessId!.Value;
            var barrier = app.Services.GetRequiredService<ActivityBarrier>();
            using (var maintenance = await Maintenance(barrier))
            {
                app.Services.GetRequiredService<MaintenanceShutdownContext>().Publish(maintenance);
                await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, capture.Runtime().SubscriberCount);
                await Until(() => Task.FromResult(Alive(pid)), alive => !alive);
                Assert.Empty(barrier.Snapshot().Activities);
            }
        }
        await using var restarted = Build(area);
        await using var restartedStop = new StopApplication(restarted);
        await restarted.StartAsync();
        using var nextClient = GameObservationApiTests.Area.Owner(restarted);
        using var next = Sdk(nextClient);
        await Until(() => next.GetCombatSnapshotAsync(), snapshot => snapshot.Data is { Encounter: not null } data && data.Observation.Id != oldId);
        Assert.Equal(OperationState.Interrupted, (await Operation(nextClient, oldId)).State.KnownValue);
        Assert.Empty(area.Device.Commands);
    }

    private static GameObservationApiTests.Area Current()
    {
        var area = new GameObservationApiTests.Area();
        var current = area.Bindings.Current!;
        area.Bindings.Update(new("TW", current.Session.ProcessId, current.Session.ProcessStartTime, current.Session.WindowIdentity,
            TwClientFingerprint.GameBuild, current.Bounds, current.DpiX, current.DpiY, false));
        return area;
    }
    private static WebApplication Build(GameObservationApiTests.Area area) => area.Build("current-combat-resident",
        combatProfile: Tw20261110CombatProtocol.CombatProfile, automation: true,
        storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: area.Root));
    private static MabiToolClient Sdk(HttpClient client) => new(client.BaseAddress!, _ => ValueTask.FromResult(client.DefaultRequestHeaders.Authorization!.Parameter!));
    private static async Task SetAuto(MabiToolClient sdk, bool enabled, string key)
    {
        string? etag = null;
        await sdk.GetCombatAutoObservationAsync(onResponse: metadata => etag = metadata.ETag);
        Assert.NotNull(etag);
        await sdk.SetCombatAutoObservationAsync(new(enabled), key, etag);
    }
    private static Task<OperationSnapshot> Operation(HttpClient client, string id) => Read<OperationSnapshot>(client, ApiContract.Operation.Path.Replace("{id}", id));
    private static async Task<T> Read<T>(HttpClient client, string path) => (await client.GetFromJsonAsync<T>(path, ContractJson.Options))!;
    private static int Counter(GameObservationApiTests.Area area, string file) => int.Parse(File.ReadAllText(Path.Combine(area.Root, file)), System.Globalization.CultureInfo.InvariantCulture);
    private static ObservationCaptureInterests Interests(GameObservationApiTests.Area area) =>
        JsonSerializer.Deserialize<ObservationCaptureInterests>(File.ReadAllText(Path.Combine(area.Root, "interests")), ContractJson.Options)!;
    private static async Task<ActivityBarrier.MaintenanceLease> Maintenance(ActivityBarrier barrier) =>
        (await Until(() => Task.FromResult(barrier.TryEnterMaintenance("resident-combat-test")), value => value is not null))!;
    private static async Task<T> Until<T>(Func<Task<T>> read, Func<T, bool> match)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true) { var value = await read(); if (match(value)) return value; await Task.Delay(20, deadline.Token); }
    }
    private static bool Alive(int id)
    { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private sealed class StopApplication(WebApplication app) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.IsCancellationRequested
            ? ValueTask.CompletedTask : new(app.StopAsync());
    }
}
