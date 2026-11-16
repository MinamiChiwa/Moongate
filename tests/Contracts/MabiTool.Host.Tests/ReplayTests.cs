using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Providers.Replay;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class ReplayTests
{
    private const string Token = "replay_only_0123456789012345678901234567890123456789";

    [Fact]
    public void ProviderHasBoundedVersionedSyntheticScenariosAndPreservesDomainIdentity()
    {
        var clock = new Clock(); var provider = new ReplayProvider("epoch", clock);
        foreach (var scenario in ReplayProvider.Scenarios)
        {
            var first = provider.Frame("op-a", scenario, 1);
            clock.Advance(TimeSpan.FromSeconds(2));
            var following = provider.Frame("op-b", scenario, 1);
            Assert.Equal("replay-synthetic", first.Source.Provider); Assert.Equal("mock-v1", first.Source.GameBuild);
            Assert.Equal("TW", first.Source.Region); Assert.Equal("epoch", first.HostClockEpoch);
            Assert.Equal("9007199254740993", first.Value.Combat.LocalEntity.RawInstanceId.ToString());
            Assert.NotEqual(first.Value.Combat.LocalEntity.GameSessionId, following.Value.Combat.LocalEntity.GameSessionId);
            Assert.True(following.MonotonicMs > first.MonotonicMs);
            Assert.NotEqual(first.Value.SelfEffects.EntityKey, first.Value.MonsterEffects.EntityKey);
            Assert.Throws<ArgumentOutOfRangeException>(() => provider.Frame("op-a", scenario, 0));
        }
        var partial = provider.Frame("op-a", ReplayProvider.Validate(new("partial", 25)), 2);
        Assert.Equal(ObservationQuality.Partial, partial.Quality.KnownValue);
        Assert.NotNull(partial.Value.Combat.EncounterDps); Assert.Equal((double?)(partial.Value.Combat.DpsDurationMs is > 0 ? (double)partial.Value.Combat.AttributedDamage.Value * 1000 / partial.Value.Combat.DpsDurationMs.Value : null), partial.Value.Combat.EncounterDps); Assert.False(partial.Value.MonsterEffects.Complete);
        Assert.Equal(ObservationQuality.Confirmed, provider.Frame("op-a", ReplayProvider.Scenarios[1], 3).Quality.KnownValue);
        Assert.False(provider.Frame("op-a", ReplayProvider.Scenarios[2], 1).Value.Combat.Final);
        foreach (var request in new[] { new ReplayRequest("normal", 0), new("normal", 2001), new("missing", 100) })
            Assert.Throws<ArgumentException>(() => ReplayProvider.Validate(request));
    }

    [Fact]
    public async Task ReplayAdmissionIsDurableOwnedIdempotentBoundedAndSingleActive()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        await using var store = SqliteStore.Open(lease, mock: true); var operations = new OperationStore(store);
        var request = new ReplayRequest("normal", 100);
        var first = await operations.AdmitReplayAsync("a", request, "key-original");
        var same = await operations.AdmitReplayAsync("a", request, "key-original");
        Assert.True(first.Created); Assert.False(same.Created); Assert.Equal(first.Snapshot.Id, same.Snapshot.Id);
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitReplayAsync("a", request with { IntervalMs = 200 }, "key-original"))).Code);
        Assert.Equal("resource.busy", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitReplayAsync("b", request, "key-original"))).Code);
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => operations.Get(first.Snapshot.Id, "b", false)).Code);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<OperationException>(() => operations.CancelAsync(first.Snapshot.Id, "b", false, null))).Code);
        await operations.CancelAsync(first.Snapshot.Id, "a", false, null);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitReplayAsync("a", request, "queue-full", queueAvailable: false))).Code);
        Assert.False((await operations.AdmitReplayAsync("a", request, "key-original", queueAvailable: false)).Created);
        Assert.Equal("idempotency.required", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitReplayAsync("a", request, null))).Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitReplayAsync("a", request, "short"))).Code);
        Assert.True((await operations.AdmitReplayAsync("b", request, "new-owner")).Created);
    }

    [Fact]
    public async Task FrameCommitCancellationAndCrashRecoveryNeverReplayOrPublishLateFrames()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        string id;
        var provider = new ReplayProvider("first");
        await using (var store = SqliteStore.Open(lease, mock: true))
        {
            var operations = new OperationStore(store);
            id = (await operations.AdmitReplayAsync("a", new("normal", 100), "first-run")).Snapshot.Id;
            Assert.True(await operations.CommitReplayFrameAsync(id, provider.Frame(id, ReplayProvider.Scenarios[0], 1), 3, false));
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => operations.CommitReplayFrameAsync(id, provider.Frame(id, ReplayProvider.Scenarios[0], 1), 3, false))).Code);
        }
        await using (var reopened = SqliteStore.Open(lease, mock: true))
        {
            await reopened.RecoverOperationsAsync(); var operations = new OperationStore(reopened);
            Assert.Equal(OperationState.Interrupted, operations.Get(id, "a", false).State.KnownValue);
            Assert.False((await operations.AdmitReplayAsync("a", new("normal", 100), "first-run")).Created);
            Assert.False(await operations.CommitReplayFrameAsync(id, provider.Frame(id, ReplayProvider.Scenarios[0], 2), 3, false));
            var next = (await operations.AdmitReplayAsync("a", new("normal", 100), "second-run")).Snapshot;
            var raced = operations.CommitReplayFrameAsync(next.Id, provider.Frame(next.Id, ReplayProvider.Scenarios[0], 1), 3, false);
            var cancel = operations.CancelAsync(next.Id, "a", false, "stop");
            await raced; await cancel;
            Assert.False(await operations.CommitReplayFrameAsync(next.Id, provider.Frame(next.Id, ReplayProvider.Scenarios[0], 2), 3, false));
            Assert.Equal(OperationState.Cancelled, operations.Get(next.Id, "a", false).State.KnownValue);
            Assert.Equal(2L, reopened.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='replay.observation-changed'"));
        }
    }

    [Fact]
    public async Task DatabaseModesRejectCrossUseAndUpgradeLegacyCoreWithoutMutatingItsMode()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        await using (var store = SqliteStore.Open(lease, mock: true))
            Assert.Equal("mock", store.ReadScalar("SELECT mode FROM core_backend_mode"));
        Assert.Equal("storage.configurationConflict", Assert.Throws<StorageException>(() => SqliteStore.Open(lease)).Code);
        await using (var store = SqliteStore.Open(lease, mock: true)) Assert.Equal(4L, store.ReadScalar("SELECT max(version) FROM core_migrations WHERE module_id='core'"));
        using var live = new Workspace(); using var liveLease = DataRootLease.Open(live.Options);
        await using (var store = SqliteStore.Open(liveLease))
            await store.WriteAsync(tx => { tx.Execute("DROP TABLE core_backend_mode"); tx.Execute("DELETE FROM core_migrations WHERE module_id='core' AND version=4"); return true; });
        Assert.Equal("storage.configurationConflict", Assert.Throws<StorageException>(() => SqliteStore.Open(liveLease, mock: true)).Code);
        await using (var upgraded = SqliteStore.Open(liveLease))
        {
            Assert.Equal("live", upgraded.ReadScalar("SELECT mode FROM core_backend_mode"));
            await upgraded.WriteAsync(tx => { tx.Execute("DROP TABLE core_backend_mode"); return true; });
        }
        Assert.Equal("storage.databaseInvalid", Assert.Throws<StorageException>(() => SqliteStore.Open(liveLease)).Code);
        Assert.Equal("storage.databaseInvalid", Assert.Throws<StorageException>(() => SqliteStore.Open(liveLease, mock: true)).Code);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task ActualMockHttpSupportsTwoClientsFramesResumeErrorsPermissionsAndLiveModeGating()
    {
        using var workspace = new Workspace();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: workspace.Options, mock: true);
        await app.StartAsync(); using var sdk = Sdk(app);
        Assert.True((await sdk.GetReplayCatalogAsync()).Enabled);
        Assert.Equal(3, (await sdk.GetReplayCatalogAsync()).Scenarios.Count);
        var challenge = await sdk.CreatePairingChallengeAsync(new("reader", null, [ApiPermissions.SystemRead]));
        var grant = await sdk.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        using var reader = new MabiToolClient(Address(app), _ => ValueTask.FromResult(grant.Token));
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.StartReplayAsync(new("normal", 25), "read-denied"))).Status);
        var writerChallenge = await sdk.CreatePairingChallengeAsync(new("writer", null, [ApiPermissions.SystemRead, ApiPermissions.ReplayControl]));
        var writerGrant = await sdk.RedeemPairingAsync(new(writerChallenge.ChallengeId, writerChallenge.Code));
        using var writer = new MabiToolClient(Address(app), _ => ValueTask.FromResult(writerGrant.Token));
        var op = await writer.StartReplayAsync(new("normal", 500), "http-normal");
        Assert.Equal(op.Id, (await writer.StartReplayAsync(new("normal", 500), "http-normal")).Id);
        var filter = new EventStreamOptions(["replay.observation-changed"], [op.Id], null);
        var before = await sdk.GetEventSnapshotAsync(filter);
        await using var first = sdk.StreamEventsAsync(filter with { LastEventId = before.EventCursor }, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        var frame = first.Current.Data.GetProperty("data").Deserialize<Observation<ReplayState>>(ContractJson.Options)!;
        Assert.Equal("replay-synthetic", frame.Source.Provider);
        var cursor = first.Current.Id; await first.DisposeAsync();
        var final = await Terminal(writer, op.Id); Assert.Equal(OperationState.Succeeded, final.State.KnownValue);
        var data = final.Result!.Value.Deserialize<Observation<ReplayState>>(ContractJson.Options)!;
        Assert.Equal(3, data.Value.Step); Assert.Equal(EffectStatus.None, final.EffectStatus.KnownValue);
        await using var resumed = sdk.StreamEventsAsync(filter with { LastEventId = cursor }, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).GetAsyncEnumerator();
        for (var step = frame.Value.Step + 1; step <= 3; step++)
        {
            Assert.True(await resumed.MoveNextAsync()); Assert.Equal(step, resumed.Current.Data.GetProperty("data").GetProperty("value").GetProperty("step").GetInt32());
        }
        await resumed.DisposeAsync();
        var ownerSnapshot = await sdk.GetEventSnapshotAsync(filter); var readerSnapshot = await reader.GetEventSnapshotAsync(filter);
        Assert.Equal(Assert.Single(ownerSnapshot.Resources).Data.GetRawText(), Assert.Single(readerSnapshot.Resources).Data.GetRawText());
        foreach (var scenario in new[] { "partial", "failure" })
        {
            var run = await sdk.StartReplayAsync(new(scenario, 25), "http-" + scenario);
            var result = await Terminal(sdk, run.Id);
            Assert.Equal(scenario == "failure" ? OperationState.Failed : OperationState.Succeeded, result.State.KnownValue);
            if (scenario == "failure") Assert.Equal("provider.unavailable", result.Problem!.Code);
        }
        Assert.Equal(400, (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.StartReplayAsync(new("missing", 25), "http-invalid"))).Status);
        var cancelled = await sdk.StartReplayAsync(new("normal", 2000), "http-cancel");
        await sdk.CancelOperationAsync(cancelled.Id, new("stop"));
        Assert.Equal(OperationState.Cancelled, (await sdk.GetOperationAsync(cancelled.Id)).State.KnownValue);
        Assert.Empty((await sdk.GetEventSnapshotAsync(filter with { ResourceIds = [cancelled.Id] })).Resources);
        var stopping = await sdk.StartReplayAsync(new("normal", 2000), "http-shutdown");
        await app.StopAsync();
        Assert.Equal(OperationState.Cancelled, app.Services.GetRequiredService<OperationStore>().Get(stopping.Id, "owner", true).State.KnownValue);
        using var liveWorkspace = new Workspace();
        await using var live = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: liveWorkspace.Options);
        await live.StartAsync(); using var liveSdk = Sdk(live);
        Assert.False((await liveSdk.GetReplayCatalogAsync()).Enabled);
        Assert.Equal(422, (await Assert.ThrowsAsync<MabiToolApiException>(() => liveSdk.StartReplayAsync(new("normal", 25), "live-denied"))).Status);
        await live.StopAsync();
    }

    [Fact]
    public void MockRequiresIsolatedPortableStorageBeforeBuildingHost()
    {
        Assert.Throws<ArgumentException>(() => BackendApplication.Build(new(0, Token, new HashSet<string>()), mock: true));
        using var workspace = new Workspace();
        Assert.Throws<ArgumentException>(() => BackendApplication.Build(new(0, Token, new HashSet<string>(), Path.Combine(workspace.Root, "other", "runtime")), storage: workspace.Options, mock: true));
    }

    private static async Task<OperationSnapshot> Terminal(MabiToolClient client, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var value = await client.GetOperationAsync(id, timeout.Token);
            if (SqliteStore.IsTerminal(value.State.KnownValue)) return value;
            await Task.Delay(20, timeout.Token);
        }
    }
    private static Uri Address(Microsoft.AspNetCore.Builder.WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static MabiToolClient Sdk(Microsoft.AspNetCore.Builder.WebApplication app) => new(Address(app), _ => ValueTask.FromResult(Token));
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
    private sealed class Workspace : IDisposable
    {
        public string Root { get; }
        public StorageOptions Options => new(Path.Combine(Root, "bootstrap"), true, PortableRoot: Root);
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            Root = Path.Combine(repository!.FullName, "artifacts/tmp/replay-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
