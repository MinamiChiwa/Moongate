using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Presentation;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Platform.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class PresentationApiTests
{
    private const string Token = "presentation_tests_012345678901234567890123456789012345678901";
    private const string Origin = "http://localhost:5173";

    [Fact]
    public async Task BrowserAndNativeReadSameFilteredResourcesAndResumeFromSnapshot()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var library = await sdk.GetColorLibraryAsync();
        await sdk.EditColorLibraryAsync(new([library.Draft with { Id = "shared-profile", Name = "共用方案" }], [], null),
            "presentation-library-edit", "\"color-library-0\"");
        PresentationSnapshot snapshot = null!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (snapshot?.Resources.Count is not > 0) { snapshot = await sdk.QueryPresentationAsync(new(["color"]), timeout.Token); if (snapshot.Resources.Count == 0) await Task.Delay(10, timeout.Token); }
        var grant = await host.Pair("browser", [ApiPermissions.SystemRead], Origin);
        using var browser = host.Http(grant.Token); browser.DefaultRequestHeaders.Add("Origin", Origin);
        using var response = await browser.PostAsJsonAsync(ApiContract.QueryPresentation.Path, new PresentationQuery(["color"]), ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var web = (await response.Content.ReadFromJsonAsync<PresentationSnapshot>(ContractJson.Options))!;
        Assert.Equal(snapshot.BackendEpoch, web.BackendEpoch);
        Assert.Equal(JsonSerializer.Serialize(snapshot.Resources, ContractJson.Options), JsonSerializer.Serialize(web.Resources, ContractJson.Options));
        Assert.All(web.Resources, resource => Assert.Equal("color", resource.Module));
        Assert.Equal("unavailable", (await browser.GetFromJsonAsync<DesktopBridgeStatus>(ApiContract.DesktopBridge.Path, ContractJson.Options))!.State);
        Assert.Equal(snapshot.BackendEpoch, snapshot.Clock.HostClockEpoch);
        Assert.Equal(snapshot.EventCursor, snapshot.Subscription!.LastEventId);
        Assert.True(snapshot.Clock.MonotonicMs > 0);
        var next = await sdk.GetColorLibraryAsync();
        await sdk.EditColorLibraryAsync(new([], ["shared-profile"], null), "presentation-library-delete", "\"color-library-" + next.Revision + "\"");
        await using var events = sdk.StreamEventsAsync(snapshot.Subscription, timeout.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("color.library.changed", events.Current.Event);
        Assert.Equal("2", events.Current.Data.GetProperty("resourceRevision").GetString());
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.QueryPresentationAsync(new(["window-controls"])))).Problem!.Code);
    }

    [Fact]
    public async Task RoomSnapshotSharesProtocolStateAndRestartClearsVolatileResources()
    {
        using var area = new Workspace();
        await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
        string previousEpoch;
        await using (var host = await Host.Start(area, roomSimulation: true))
        {
            using var first = host.Sdk(); using var second = host.Sdk();
            var room = await first.CreateRoomAsync(new(server.Endpoint, "測試房主"), "presentation-create-room");
            await first.SetRoomReadinessAsync(new(true, true, true, true, TimingOffsetMs: 35));
            var snapshot = await first.QueryPresentationAsync(new(["room"]));
            previousEpoch = snapshot.BackendEpoch;
            var resource = Assert.Single(snapshot.Resources);
            var presentation = resource.Data.Deserialize<RoomPresentation>(ContractJson.Options)!;
            Assert.Equal("connected", presentation.Session.State); Assert.Equal(room.MemberId, presentation.Session.MemberId);
            using var clockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (presentation.Clock is null)
            {
                await Task.Delay(10, clockTimeout.Token);
                presentation = Assert.Single((await first.QueryPresentationAsync(new(["room"]), clockTimeout.Token)).Resources).Data.Deserialize<RoomPresentation>(ContractJson.Options)!;
            }
            Assert.Equal(35, presentation.Clock.PersonalOffsetMs);
            Assert.DoesNotContain("token", JsonSerializer.Serialize(presentation, ContractJson.Options), StringComparison.OrdinalIgnoreCase);
            var other = await second.QueryPresentationAsync(new(["room"]));
            var otherResource = Assert.Single(other.Resources);
            var otherPresentation = otherResource.Data.Deserialize<RoomPresentation>(ContractJson.Options)!;
            Assert.Equal(JsonSerializer.Serialize(presentation.Session, ContractJson.Options), JsonSerializer.Serialize(otherPresentation.Session, ContractJson.Options));
            Assert.Equal(presentation.HostClockEpoch, otherPresentation.HostClockEpoch);
            Assert.True(otherResource.ResourceRevision.Value >= resource.ResourceRevision.Value);
            using var reader = host.Sdk((await host.Pair("room-reader", [ApiPermissions.SystemRead])).Token);
            var own = Assert.Single((await reader.QueryPresentationAsync(new(["room"]))).Resources).Data.Deserialize<RoomPresentation>(ContractJson.Options)!;
            Assert.Equal("disconnected", own.Session.State); Assert.Null(own.Session.MemberId);
            await first.LeaveRoomAsync(new(true));
            Assert.Equal("disconnected", Assert.Single((await first.QueryPresentationAsync(new(["room"]))).Resources).Data.GetProperty("session").GetProperty("state").GetString());
            Assert.Equal(0L, host.App.Services.GetRequiredService<SqliteStore>().ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='room.session.changed'"));
        }
        await using (var host = await Host.Start(area))
        {
            using var sdk = host.Sdk();
            var empty = await sdk.GetEventSnapshotAsync(new(["room.session.changed"], [], null));
            Assert.Empty(empty.Resources); Assert.NotEqual(previousEpoch, empty.BackendEpoch);
            Assert.Equal("disconnected", Assert.Single((await sdk.QueryPresentationAsync(new(["room"]))).Resources).Data.GetProperty("session").GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task UnconfiguredNearbyAndEnvironmentRemainExplicitAndEphemeralHostWorks()
    {
        await using var host = await Host.Start(null); using var sdk = host.Sdk();
        var snapshot = await sdk.QueryPresentationAsync(new(["nearby", "environment"]));
        Assert.Empty(snapshot.Resources);
        // 附近与环境的状态随游戏连接器：不支持连接器的宿主给出连接器的原因，不再固定为 provider.unavailable。
        Assert.All(snapshot.Sources, source => { Assert.Equal(CapabilityStatus.Unavailable, source.Status.KnownValue); Assert.Equal("platform.unsupported", source.Reason); });
        Assert.Equal("unavailable", (await sdk.GetDesktopBridgeAsync()).State);
        Assert.Equal(snapshot.BackendEpoch, (await sdk.GetPresentationAsync()).BackendEpoch);
    }

    [Fact]
    public async Task GameDataAndMarketQueriesOnAHostWithoutAConnectorReportReasonsInsteadOfValues()
    {
        await using var host = await Host.Start(null); using var sdk = host.Sdk();
        var data = await sdk.QueryGameDataAsync(new(GameDataTopics.All));
        Assert.Equal(host.App.Services.GetRequiredService<SystemInfo>().InstanceId, data.BackendEpoch);
        Assert.All(new[] { data.Profile!.Status, data.Currencies!.Status, data.Missions!.Daily.Status, data.Missions.Weekly.Status, data.GrowthGuide!.Status,
            data.Environment!.Status, data.Nearby!.Status }, status =>
        {
            Assert.Equal((GameDataSource.None, "platform.unsupported"), (status.Source.KnownValue!.Value, status.Reason));
            Assert.Null(status.ReadAt);
        });
        Assert.Null(data.Profile!.Profile); Assert.Null(data.Currencies!.Items); Assert.Null(data.Nearby!.Players); Assert.Null(data.Missions!.Daily.Items);
        var only = await sdk.QueryGameDataAsync(new(["environment"], true));
        Assert.Null(only.Profile); Assert.NotNull(only.Environment);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryGameDataAsync(new([])))).Problem!.Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryGameDataAsync(new(["room"])))).Problem!.Code);

        Assert.Equal("platform.unsupported", (await sdk.QueryGameMarketPricesAsync(new(["原木"]))).Reason);
        Assert.Equal("gameCli.nameTooShort", (await sdk.QueryGameMarketPricesAsync(new(["木"]))).Reason);
        Assert.Equal("platform.unsupported", (await sdk.QueryGameMarketPriceDetailAsync(new("原木", Quantity: 3))).Reason);
        var valuation = await sdk.QueryGameMarketValuationAsync(new([new("原木", 3)]));
        Assert.Equal(("platform.unsupported", GameMarketValuationState.Unread), (valuation.Reason, valuation.Rows[0].State.KnownValue!.Value));
        Assert.Null(valuation.Rows[0].Total);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryGameMarketPricesAsync(new()))).Problem!.Code);
    }

    [Trait("Category", "Desktop")]
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task NativeWindowsBridgeReadsActualFlagsAndEnforcesPermissionsAndRevision()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        await using var window = await NativeDesktopTestWindow.StartAsync();
        var registry = host.App.Services.GetRequiredService<DesktopBridgeRegistry>();
        using var registration = registry.Register("owner", window.Surface, out var id);
        var original = await sdk.GetDesktopSurfaceAsync(id);
        Assert.Equal(new(false, false, false), original.State);
        Assert.False(original.Capabilities.ColorPickerOverlay);
        var change = new DesktopSurfaceChange(true, true, true);
        var result = await RequireSuccess(() => sdk.ChangeDesktopSurfaceAsync(id, change, "native-surface-flags", DesktopBridgeRegistry.ETag(original.Revision)),
            async () => await window.Surface.ReadAsync(default));
        Assert.Equal(new(true, true, true), result.State);
        Assert.Equal(result, await sdk.ChangeDesktopSurfaceAsync(id, change, "native-surface-flags", DesktopBridgeRegistry.ETag(original.Revision)));
        Assert.Equal(result.State, (await window.Surface.ReadAsync(default)));
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.ChangeDesktopSurfaceAsync(id, new(false), "native-surface-flags", DesktopBridgeRegistry.ETag(original.Revision)))).Problem!.Code);
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.ChangeDesktopSurfaceAsync(id, new(false), "native-surface-stale", DesktopBridgeRegistry.ETag(original.Revision)))).Problem!.Code);
        using var reader = host.Sdk((await host.Pair("surface-reader", [ApiPermissions.SystemRead])).Token);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.GetDesktopSurfaceAsync(id))).Problem!.Code);
        Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            reader.ChangeDesktopSurfaceAsync(id, new(false), "surface-reader-denied", DesktopBridgeRegistry.ETag(result.Revision)))).Problem!.Code);
        var reset = await sdk.ChangeDesktopSurfaceAsync(id, new(false, false, false), "native-surface-reset", DesktopBridgeRegistry.ETag(result.Revision));
        Assert.Equal(original.State, reset.State);
        await window.DestroyAsync();
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.ChangeDesktopSurfaceAsync(id, change, "native-surface-flags", DesktopBridgeRegistry.ETag(original.Revision)))).Problem!.Code);
    }

    [Trait("Category", "Desktop")]
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task NativeSurfaceRequiresOwningUiThreadAndRegistrationIsScopedToPairing()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var owner = host.Sdk();
        await using var window = await NativeDesktopTestWindow.StartAsync();
        Assert.Equal("desktop.surfaceUnavailable", Assert.Throws<DesktopSurfaceException>(() =>
            new WindowsDesktopSurface(window.Handle, window.InvokeAsync)).Code);
        var grant = await host.Pair("desktop-client", [ApiPermissions.SystemRead, ApiPermissions.DesktopControl]);
        using var sdk = host.Sdk(grant.Token);
        var registry = host.App.Services.GetRequiredService<DesktopBridgeRegistry>();
        using var registration = registry.Register(grant.Session.ClientId, window.Surface, out var id);
        Assert.Single((await sdk.GetDesktopBridgeAsync()).Surfaces);
        Assert.Empty((await owner.GetDesktopBridgeAsync()).Surfaces);
        var before = await sdk.GetDesktopSurfaceAsync(id);
        var after = await RequireSuccess(() => sdk.ChangeDesktopSurfaceAsync(id, new(true), "paired-native-surface", DesktopBridgeRegistry.ETag(before.Revision)),
            async () => await window.Surface.ReadAsync(default));
        Assert.True(after.State!.Topmost);
        registration.Dispose();
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.GetDesktopSurfaceAsync(id))).Problem!.Code);
        Assert.Empty((await sdk.GetDesktopBridgeAsync()).Surfaces);
    }

    [Fact]
    public async Task VolatileStateBoundsRevisionAndSnapshotSubscriptionRace()
    {
        var hub = new StateEventHub(null, "presentation-epoch");
        var session = new AuthSession("client", "test", [ApiPermissions.SystemRead], null, null, false);
        var filter = EventFilter.Create(["desktop.surface.changed"]);
        DesktopSurfaceSnapshot State(int id, ulong revision) => new("surface-" + id, "client", new(revision), true, null, new(true, true, false, false), new(false, false, false));
        StateEvent Event(int id, ulong revision) => new("event-" + id + "-" + revision, "desktop-presentation", "surface-" + id, new(revision), "desktop.surface.changed", JsonSerializer.SerializeToElement(State(id, revision), ContractJson.Options));
        for (var i = 0; i < StateEventHub.MaximumVolatileResources; i++) await hub.PublishVolatileAsync(Event(i, 1), DateTimeOffset.UtcNow);
        var snapshot = await hub.SnapshotAsync(session, filter);
        Assert.Equal(StateEventHub.MaximumVolatileResources, snapshot.Resources.Count);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() =>
            hub.PublishVolatileAsync(Event(128, 1), DateTimeOffset.UtcNow))).Code);
        await hub.PublishVolatileAsync(Event(0, 1), DateTimeOffset.UtcNow);
        Assert.Equal(snapshot.EventCursor, (await hub.SnapshotAsync(session, filter)).EventCursor);
        await using var subscription = await hub.SubscribeAsync(session, filter, snapshot.EventCursor);
        await hub.PublishVolatileAsync(Event(0, 2), DateTimeOffset.UtcNow);
        Assert.True(subscription.TryRead(out var delivery)); Assert.Equal("surface-0", delivery!.ResourceId);
        Assert.Equal("resource.conflict", (await Assert.ThrowsAsync<OperationException>(() =>
            hub.PublishVolatileAsync(Event(0, 1), DateTimeOffset.UtcNow))).Code);
        for (var i = 3; i < 3 + StateEventHub.MaximumQueueEvents + 1; i++) await hub.PublishVolatileAsync(Event(0, (ulong)i), DateTimeOffset.UtcNow);
        Assert.True(subscription.NeedsResync);
    }

    [Fact]
    public async Task ClosedDesktopSnapshotReleasesCapacityAndClosureRemainsReplayable()
    {
        var hub = new StateEventHub(null, "closure-epoch");
        var session = new AuthSession("client", "test", [ApiPermissions.SystemRead], null, null, false);
        StateEvent State(int id, ulong revision, bool available) => new("closure-" + id + "-" + revision, "desktop-presentation", "surface-" + id, new(revision),
            "desktop.surface.changed", JsonSerializer.SerializeToElement(new DesktopSurfaceSnapshot("surface-" + id, "client", new(revision), available,
                available ? null : "desktop.surfaceUnavailable", new(true, true, true, false), available ? new(false, false, false) : null), ContractJson.Options));
        for (var i = 0; i < StateEventHub.MaximumVolatileResources; i++) await hub.PublishVolatileAsync(State(i, 1, true), DateTimeOffset.UtcNow);
        var filter = EventFilter.Create(["desktop.surface.changed"], ["surface-127"]);
        var before = await hub.SnapshotAsync(session, filter);
        await hub.PublishVolatileAsync(State(127, 2, false), DateTimeOffset.UtcNow);
        Assert.Equal("resource.conflict", (await Assert.ThrowsAsync<OperationException>(() => hub.PublishVolatileAsync(State(127, 1, true), DateTimeOffset.UtcNow))).Code);
        await hub.PublishVolatileAsync(State(128, 1, true), DateTimeOffset.UtcNow);
        Assert.Empty((await hub.SnapshotAsync(session, filter)).Resources);
        Assert.Equal(StateEventHub.MaximumVolatileResources, (await hub.SnapshotAsync(session, EventFilter.Create())).Resources.Count);
        await using var subscription = await hub.SubscribeAsync(session, filter, before.EventCursor);
        Assert.False(Assert.Single(subscription.Replay).Data.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task SurfaceIdempotencyBudgetReclaimsOnlyExpiredKeysUsingMonotonicTime()
    {
        var clock = new DesktopClock(); await using var registry = new DesktopBridgeRegistry(clock);
        var surface = new DelayedSurface(); surface.Resume.TrySetResult();
        using var registration = registry.Register("client", surface, out var id);
        var initial = await registry.ReadAsync("client", id, default);
        var first = await registry.ApplyAsync("client", id, new(true), "budget-first", DesktopBridgeRegistry.ETag(initial.Revision), default);
        var current = first;
        for (var i = 1; i < DesktopBridgeRegistry.MaximumKeysPerSurface; i++)
            current = await registry.ApplyAsync("client", id, new(i % 2 == 0), "budget-key-" + i, DesktopBridgeRegistry.ETag(current.Revision), default);
        clock.WallTime -= TimeSpan.FromDays(1);
        Assert.Equal(first, await registry.ApplyAsync("client", id, new(true), "budget-first", DesktopBridgeRegistry.ETag(initial.Revision), default));
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() =>
            registry.ApplyAsync("client", id, new(true), "budget-full", DesktopBridgeRegistry.ETag(current.Revision), default))).Code);
        clock.Timestamp = (long)DesktopBridgeRegistry.KeyRetention.TotalMilliseconds;
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() =>
            registry.ApplyAsync("client", id, new(true), "budget-first", DesktopBridgeRegistry.ETag(initial.Revision), default))).Code);
        var next = await registry.ApplyAsync("client", id, new(true), "budget-first", DesktopBridgeRegistry.ETag(current.Revision), default);
        Assert.True(next.State!.Topmost); Assert.Equal(DesktopBridgeRegistry.MaximumKeysPerSurface + 1, surface.Applied);
    }

    [Fact]
    public async Task AdmittedSurfaceChangeSurvivesHttpDisconnectAndConcurrentOldRevisionLoses()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var surface = new DelayedSurface();
        using var registration = host.App.Services.GetRequiredService<DesktopBridgeRegistry>().Register("owner", surface, out var id);
        var before = await sdk.GetDesktopSurfaceAsync(id);
        using var http = host.Http();
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiContract.ChangeDesktopSurface.Path.Replace("{id}", id))
            { Content = JsonContent.Create(new DesktopSurfaceChange(true), options: ContractJson.Options) };
        request.Headers.Add("Idempotency-Key", "disconnect-surface-change"); request.Headers.Add("If-Match", DesktopBridgeRegistry.ETag(before.Revision));
        var pending = http.SendAsync(request, cancellation.Token);
        await surface.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        surface.Resume.TrySetResult();
        var after = await sdk.GetDesktopSurfaceAsync(id);
        Assert.True(after.State!.Topmost); Assert.Equal(1, surface.Applied);
        var attempts = await Task.WhenAll(Change(new(false), "surface-concurrent-one"), Change(new(ClickThrough: true), "surface-concurrent-two"));
        Assert.Single(attempts, x => x == "succeeded"); Assert.Single(attempts, x => x == "revision.stale");
        Assert.Equal(2, surface.Applied);
        async Task<string> Change(DesktopSurfaceChange value, string key)
        {
            try { await sdk.ChangeDesktopSurfaceAsync(id, value, key, DesktopBridgeRegistry.ETag(after.Revision)); return "succeeded"; }
            catch (MabiToolApiException error) { return error.Problem!.Code; }
        }
    }

    // 失败时附上窗口线程回读的实际标志，用于区分 resource.conflict 中未生效的具体字段。
    private static async Task<T> RequireSuccess<T>(Func<Task<T>> action, Func<Task<DesktopSurfaceState>>? actual = null)
    {
        try { return await action(); }
        catch (MabiToolApiException error)
        {
            var observed = actual is null ? null : JsonSerializer.Serialize(await actual(), ContractJson.Options);
            Assert.Fail(JsonSerializer.Serialize(error.Problem, ContractJson.Options) + (observed is null ? "" : " actual=" + observed)); throw;
        }
    }

    private sealed class DesktopClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public DateTimeOffset WallTime { get; set; } = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => WallTime;
    }

    private sealed class DelayedSurface : IDesktopSurface
    {
        public DesktopSurfaceCapabilities Capabilities { get; } = new(true, true, true, false);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Applied { get; private set; }
        private DesktopSurfaceState state = new(false, false, false);
        public ValueTask<DesktopSurfaceState> ReadAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(state); }
        public async ValueTask<DesktopSurfaceState> ApplyAsync(DesktopSurfaceChange change, CancellationToken cancellationToken)
        {
            Started.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken);
            state = new(change.Topmost ?? state.Topmost, change.ClickThrough ?? state.ClickThrough, change.ExcludeFromCapture ?? state.ExcludeFromCapture);
            Applied++; return state;
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required Uri Address { get; init; }
        public static async Task<Host> Start(Workspace? area, bool roomSimulation = false)
        {
            var app = BackendApplication.Build(new(0, Token, new HashSet<string> { Origin }), storage: area?.Storage,
                gameControls: roomSimulation ? sp => GameControlContext.Create(sp.GetRequiredService<ResourceLeaseCoordinator>(), TimeProvider.System, true) : null);
            await app.StartAsync();
            return new() { App = app, Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        }
        public MabiToolClient Sdk(string token = Token) => new(Address, _ => ValueTask.FromResult(token));
        public HttpClient Http(string token = Token) { var client = new HttpClient { BaseAddress = Address }; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return client; }
        public async Task<AccessGrant> Pair(string name, IReadOnlyList<string> permissions, string? origin = null)
        {
            using var owner = Sdk(); var challenge = await owner.CreatePairingChallengeAsync(new(name, origin, permissions));
            if (origin is null) return await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
            using var browser = Http(); browser.DefaultRequestHeaders.Add("Origin", origin);
            using var response = await browser.PostAsJsonAsync(ApiContract.Pairings.Path, new PairingRequest(challenge.ChallengeId, challenge.Code), ContractJson.Options);
            return (await response.Content.ReadFromJsonAsync<AccessGrant>(ContractJson.Options))!;
        }
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string directory = Path.Combine(Root(), "artifacts", "tmp", "presentation-tests", Guid.NewGuid().ToString("N"));
        public StorageOptions Storage => new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data"));
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
