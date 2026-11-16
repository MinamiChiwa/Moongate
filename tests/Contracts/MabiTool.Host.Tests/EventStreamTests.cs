using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class EventStreamTests
{
    private const string Token = "events_only_0123456789012345678901234567890123456789";
    private static readonly AuthSession Owner = new("owner", "owner", [ApiPermissions.SystemRead], null, null, true);
    private static readonly EventFilter All = EventFilter.Create();

    [Fact]
    public async Task ShortcutNotificationsRequireDesktopPermissionExpireAndNeverReplay()
    {
        var clock = new NotificationClock(); var hub = new StateEventHub(null, "shortcut-test", clock);
        var desktop = Owner with { Permissions = [ApiPermissions.SystemRead, ApiPermissions.DesktopControl] };
        var filter = EventFilter.Create(["shortcut.triggered"]);
        var before = await hub.SnapshotAsync(desktop, filter);
        await using var active = await hub.SubscribeAsync(desktop, filter);
        await using var viewer = await hub.SubscribeAsync(Owner, filter);
        await using var otherTopic = await hub.SubscribeAsync(desktop, EventFilter.Create(["module.changed"]));
        var value = new ShortcutTriggered("overlay-toggle", new("F8", false, false, false, false), clock.GetUtcNow(), clock.GetUtcNow().AddSeconds(2));
        await hub.PublishShortcutAsync(value);
        Assert.True(active.TryRead(out var received));
        Assert.Equal(value, received!.Data.Deserialize<ShortcutTriggered>(ContractJson.Options));
        Assert.False(viewer.TryRead(out _)); Assert.False(otherTopic.TryRead(out _));
        Assert.Empty((await hub.SnapshotAsync(desktop, filter)).Resources);
        await using var reconnect = await hub.SubscribeAsync(desktop, filter, before.EventCursor);
        Assert.Empty(reconnect.Replay); Assert.False(reconnect.TryRead(out _));
        await hub.PublishShortcutAsync(value);
        clock.Now = clock.Now.AddSeconds(2);
        Assert.False(active.TryRead(out _)); Assert.False(reconnect.TryRead(out _));
        await hub.PublishShortcutAsync(value);
        Assert.False(active.TryRead(out _));
        // 紧急停止由后端执行，事后通知在线前端；不在动作表里的动作仍被拒绝。
        await hub.PublishShortcutAsync(value with { ActionId = "emergency-stop", TriggeredAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddSeconds(2) });
        Assert.True(active.TryRead(out var stopped));
        Assert.Equal("emergency-stop", stopped!.Data.Deserialize<ShortcutTriggered>(ContractJson.Options)!.ActionId);
        await Assert.ThrowsAsync<OperationException>(() => hub.PublishShortcutAsync(value with { ActionId = "unknown-action" }));
    }

    private sealed class NotificationClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static PersistedEvent Event(ulong revision, string id = "color", string? principal = null, int padding = 0) => new((long)revision,
        new("event-" + Guid.NewGuid().ToString("N"), principal is null ? "module-configuration" : "operation", id, new(revision),
            principal is null ? "module.configuration-changed" : "operation.changed",
            principal is null && padding == 0 ? JsonSerializer.SerializeToElement(new ModuleConfiguration(id, revision % 2 == 0, new(revision)), ContractJson.Options) :
                JsonSerializer.SerializeToElement(new { owner = principal, value = new string('x', padding) })), DateTimeOffset.UtcNow);

    [Fact]
    public async Task SnapshotAndTwoSubscribersShareOneWatermarkAcrossConcurrentPublicationAndDeduplicateOutbox()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        await using var store = SqliteStore.Open(lease); var hub = new StateEventHub(store, "epoch");
        var state = Event(1); await Publish(hub, store, state);
        var snapshot = await hub.SnapshotAsync(Owner, All);
        Assert.Equal(1UL, Assert.Single(snapshot.Resources).ResourceRevision.Value);
        await using var first = await hub.SubscribeAsync(Owner, All, snapshot.EventCursor);
        await using var second = await hub.SubscribeAsync(Owner, All);
        Assert.Empty(first.Replay); Assert.Equal(snapshot.EventCursor, second.InitialSnapshot!.EventCursor);
        await Publish(hub, store, state); await Publish(hub, store, Event(0));
        Assert.False(first.TryRead(out _)); Assert.False(second.TryRead(out _));
        for (ulong i = 2; i <= 17; i++)
        {
            // 发布与登记无论谁先取得顺序锁，资源都在快照或快照之后的事件中出现。
            var publication = Publish(hub, store, Event(i));
            var registration = hub.SubscribeAsync(Owner, All);
            await publication; await using var raced = await registration;
            var seen = Assert.Single(raced.InitialSnapshot!.Resources).ResourceRevision.Value;
            if (raced.TryRead(out var following)) seen = following!.Data.GetProperty("revision").GetString() is { } value ? ulong.Parse(value) : 0;
            Assert.Equal(i, seen);
        }
        var a = new List<ulong>(); var b = new List<ulong>();
        while (first.TryRead(out var item)) a.Add(item!.Sequence);
        while (second.TryRead(out var item)) b.Add(item!.Sequence);
        Assert.Equal(Enumerable.Range(2, 16).Select(i => (ulong)i), a); Assert.Equal(a, b);
        var resumedSnapshot = await hub.SnapshotAsync(Owner, All);
        await Publish(hub, store, Event(18));
        await using var resumed = await hub.SubscribeAsync(Owner, All, resumedSnapshot.EventCursor);
        Assert.Equal(18UL, Assert.Single(resumed.Replay).Sequence);
    }

    [Fact]
    public async Task FiltersAndOwnershipApplyToSnapshotReplayAndLiveEventsAndBindCanonicalCursors()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        await using var store = SqliteStore.Open(lease); var hub = new StateEventHub(store, "epoch");
        var a = Owner with { ClientId = "a", Owner = false }; var b = a with { ClientId = "b" };
        var filter = EventFilter.Create(["operation.changed", "module.configuration-changed", "operation.changed"], ["op-a", "color", "op-b"]);
        var start = await hub.SnapshotAsync(a, filter);
        await Publish(hub, store, Event(1, "op-a", "a")); await Publish(hub, store, Event(1, "op-b", "b"));
        await Publish(hub, store, Event(1)); await Publish(hub, store, Event(1, "rooms"));
        var normalized = EventFilter.Create(["module.configuration-changed", "operation.changed"], ["color", "op-b", "op-a"]);
        await using var subscription = await hub.SubscribeAsync(a, normalized, start.EventCursor);
        Assert.Equal(new[] { "op-a", "color" }, subscription.Replay.Select(e => e.ResourceId));
        var captured = await hub.SnapshotAsync(a, normalized);
        Assert.Equal(new[] { "color", "op-a" }, captured.Resources.Select(e => e.ResourceId).Order());
        await Publish(hub, store, Event(2, "op-b", "b")); Assert.False(subscription.TryRead(out _));
        await Publish(hub, store, Event(2, "op-a", "a")); Assert.True(subscription.TryRead(out var item)); Assert.Equal("op-a", item!.ResourceId);
        foreach (var (session, otherFilter, cursor) in new[] {
            (b, filter, start.EventCursor), (a, All, start.EventCursor), (a, filter, start.EventCursor.Replace("epoch:", "old:")),
            (a, filter, hub.Cursor(a, filter, 999)), (a, filter, start.EventCursor.Replace(":0:", ":00:")) })
            Assert.Equal("events.resyncRequired", (await Assert.ThrowsAsync<OperationException>(() => hub.SubscribeAsync(session, otherFilter, cursor))).Code);
        Assert.Throws<OperationException>(() => EventFilter.Create(["unknown"]));
        Assert.Throws<OperationException>(() => EventFilter.Create(resourceIds: ["../private"]));
    }

    [Fact]
    public async Task MonotonicExpiryRequiresResyncWithoutWallClockDependence()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        await using var store = SqliteStore.Open(lease); var clock = new Clock(); var hub = new StateEventHub(store, "epoch", clock);
        var before = await hub.SnapshotAsync(Owner, All); await Publish(hub, store, Event(1));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("events.resyncRequired", (await Assert.ThrowsAsync<OperationException>(() => hub.SubscribeAsync(Owner, All, before.EventCursor))).Code);
        var current = (await hub.SnapshotAsync(Owner, All)).EventCursor;
        await using var connected = await hub.SubscribeAsync(Owner, All, current);
        Assert.Empty(connected.Replay);
    }

    [Fact]
    public void ReplayRetentionEnforcesCountBytesAndMonotonicAgeAtExactBoundaries()
    {
        var clock = new Clock(); var window = new EventReplayWindow(clock);
        EventDelivery Item(ulong sequence, int bytes = 1) => new(sequence, "operation.changed", "op", JsonSerializer.SerializeToElement(new { }), "{}", bytes, clock.GetTimestamp());
        for (ulong i = 1; i <= 10000; i++) window.Add(Item(i));
        Assert.Equal(0UL, window.Floor); Assert.Equal(10000, window.After(0, _ => true).Count);
        window.Add(Item(10001)); Assert.Equal(1UL, window.Floor); Assert.Equal(10000, window.After(1, _ => true).Count);
        var bytes = new EventReplayWindow(clock);
        bytes.Add(Item(1, StateEventHub.MaximumReplayBytes / 2)); bytes.Add(Item(2, StateEventHub.MaximumReplayBytes / 2));
        Assert.Equal(0UL, bytes.Floor); bytes.Add(Item(3)); Assert.Equal(1UL, bytes.Floor);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1)); bytes.Prune(); Assert.Equal(1UL, bytes.Floor);
        clock.Advance(TimeSpan.FromTicks(1)); bytes.Prune(); Assert.Equal(3UL, bytes.Floor); Assert.Empty(bytes.After(0, _ => true));
    }

    [Fact]
    public async Task SlowConsumerHasCountAndByteBudgetsAndCannotBlockHealthyConsumer()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        await using var store = SqliteStore.Open(lease); var hub = new StateEventHub(store, "epoch");
        await using var slow = await hub.SubscribeAsync(Owner, All);
        await using var healthy = await hub.SubscribeAsync(Owner with { ClientId = "healthy" }, All);
        var payload = JsonSerializer.SerializeToElement(new { });
        for (ulong i = 1; i <= 128; i++) slow.Enqueue(new(i, "module.configuration-changed", "color", payload, "{}", 2, 0));
        Assert.False(slow.NeedsResync);
        await Publish(hub, store, Event(1));
        Assert.True(healthy.TryRead(out var item)); Assert.Equal(1UL, item!.Sequence);
        Assert.True(slow.NeedsResync); Assert.False(slow.TryRead(out _)); Assert.False(healthy.NeedsResync);
        await using var byteSlow = await hub.SubscribeAsync(Owner, All);
        byteSlow.Enqueue(new(2, "module.configuration-changed", "color", payload, "{}", StateEventHub.MaximumQueueBytes, 0));
        Assert.False(byteSlow.NeedsResync);
        await Publish(hub, store, Event(2)); Assert.True(healthy.TryRead(out _));
        Assert.True(byteSlow.NeedsResync); Assert.False(healthy.NeedsResync);
    }

    [Fact]
    public async Task ConnectionCapsArePerClientAndGlobalAndDisposalReleasesCapacity()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        await using var store = SqliteStore.Open(lease); var hub = new StateEventHub(store, "epoch");
        var active = new List<EventSubscription>();
        try
        {
            for (var i = 0; i < 4; i++) active.Add(await hub.SubscribeAsync(Owner, All));
            Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => hub.SubscribeAsync(Owner, All))).Code);
            for (var i = 4; i < 32; i++) active.Add(await hub.SubscribeAsync(Owner with { ClientId = "client-" + i }, All));
            await Assert.ThrowsAsync<OperationException>(() => hub.SubscribeAsync(Owner with { ClientId = "extra" }, All));
            await active[0].DisposeAsync();
            active.Add(await hub.SubscribeAsync(Owner, All));
        }
        finally { foreach (var item in active) await item.DisposeAsync(); }
        await using var released = await hub.SubscribeAsync(Owner, All);
    }

    [Fact]
    public async Task OversizedSnapshotsRequireNarrowerFiltersAndDoNotLeakConnectionReservations()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        await using var store = SqliteStore.Open(lease); var hub = new StateEventHub(store, "epoch");
        await store.WriteAsync(tx =>
        {
            for (var i = 0; i < 513; i++)
                tx.Execute("INSERT INTO core_state_projection VALUES('module-configuration',$id,'1',$id,'{}','module.configuration-changed')", ("$id", "id-" + i));
            return true;
        });
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<OperationException>(() => hub.SubscribeAsync(Owner, All));
        await using var narrow = await hub.SubscribeAsync(Owner, EventFilter.Create(resourceIds: ["id-0"]));
        Assert.Single(narrow.InitialSnapshot!.Resources);
        await store.WriteAsync(tx => tx.Execute("DELETE FROM core_state_projection WHERE resource_id='id-512'"));
        // 约 7 MiB 的超限快照分多笔写入，每笔保持在短事务的设计预算规模内。
        var payload = JsonSerializer.Serialize(new { text = new string('x', 64 * 1024) });
        var oversized = Enumerable.Range(0, 512).Select(i => "id-" + i).Where(id => string.CompareOrdinal(id, "id-2") < 0).ToArray();
        foreach (var chunk in oversized.Chunk(16))
            await store.WriteAsync(tx =>
            {
                foreach (var id in chunk)
                    tx.Execute("UPDATE core_state_projection SET payload=$payload WHERE resource_id=$id", ("$payload", payload), ("$id", id));
                return true;
            });
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => hub.SnapshotAsync(Owner, All))).Code);
        Assert.Single((await hub.SnapshotAsync(Owner, EventFilter.Create(resourceIds: ["id-0"]))).Resources);
    }

    [Fact]
    public async Task RestartLoadsDurableProjectionRejectsOldEpochAndDoesNotRepublishDuplicate()
    {
        using var workspace = new Workspace(); using var lease = DataRootLease.Open(workspace.Options);
        string cursor; var state = Event(1);
        await using (var store = SqliteStore.Open(lease))
        {
            var hub = new StateEventHub(store, "old"); await Publish(hub, store, state);
            cursor = (await hub.SnapshotAsync(Owner, All)).EventCursor;
        }
        await using (var store = SqliteStore.Open(lease))
        {
            var hub = new StateEventHub(store, "new");
            Assert.Single((await hub.SnapshotAsync(Owner, All)).Resources);
            await Assert.ThrowsAsync<OperationException>(() => hub.SubscribeAsync(Owner, All, cursor));
            await using var subscription = await hub.SubscribeAsync(Owner, All);
            await Publish(hub, store, state); Assert.False(subscription.TryRead(out _));
            await Publish(hub, store, Event(2)); Assert.True(subscription.TryRead(out var item)); Assert.Equal(1UL, item!.Sequence);
        }
    }

    [Fact]
    public async Task HttpAndCSharpSdkDeliverSnapshotLiveReplayAndCloseRevokedConnections()
    {
        using var workspace = new Workspace();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>(["http://localhost:5173"])), storage: workspace.Options);
        await app.StartAsync();
        using var sdk = Sdk(app); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var options = new EventStreamOptions(["module.configuration-changed"], ["color"], null);
        await using var events = sdk.StreamEventsAsync(options, timeout.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync()); Assert.Equal("snapshot", events.Current.Event);
        var cursor = events.Current.Id!;
        var operation = await sdk.ConfigureModuleAsync("color", new(false), "stream-disable", "\"module-color-0\"");
        Assert.True(await events.MoveNextAsync()); Assert.Equal("module.configuration-changed", events.Current.Event);
        Assert.False(events.Current.Data.GetProperty("data").GetProperty("enabled").GetBoolean());
        await using (var replay = sdk.StreamEventsAsync(options with { LastEventId = cursor }, timeout.Token).GetAsyncEnumerator())
        { Assert.True(await replay.MoveNextAsync()); Assert.Equal(events.Current.Id, replay.Current.Id); }
        var snapshot = await sdk.GetEventSnapshotAsync(options, timeout.Token);
        Assert.Equal(1UL, Assert.Single(snapshot.Resources).ResourceRevision.Value);
        var challenge = await sdk.CreatePairingChallengeAsync(new("events-reader", null, [ApiPermissions.SystemRead]));
        var grant = await sdk.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        using var paired = Sdk(app, grant.Token);
        await using var connection = paired.StreamEventsAsync(cancellationToken: timeout.Token).GetAsyncEnumerator();
        Assert.True(await connection.MoveNextAsync()); Assert.DoesNotContain(connection.Current.Data.GetProperty("resources").EnumerateArray(), r => r.GetProperty("type").GetString() == "operation.changed");
        await sdk.RevokeClientAsync(grant.Session.ClientId);
        Assert.True(await connection.MoveNextAsync()); Assert.Equal("control", connection.Current.Event);
        Assert.Equal("auth.required", connection.Current.Data.GetProperty("code").GetString()); Assert.False(await connection.MoveNextAsync());
        await using var cancelled = sdk.StreamEventsAsync(options, timeout.Token).GetAsyncEnumerator();
        Assert.True(await cancelled.MoveNextAsync()); await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.MoveNextAsync().AsTask());
        await app.StopAsync();
    }

    [Fact]
    public async Task HttpRejectsInvalidFiltersCursorsAndUrlTokensAndAllowsBrowserStreamHeaders()
    {
        using var workspace = new Workspace();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>(["http://localhost:5173"])), storage: workspace.Options);
        await app.StartAsync(); using var sdk = Sdk(app);
        using var http = new HttpClient { BaseAddress = Address(app) }; http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        foreach (var (header, value, status) in new[] { ("X-Event-Topics", "unknown", 400), ("X-Event-Resources", "../a", 400), ("Last-Event-ID", "bad", 409) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events"); request.Headers.Add(header, value);
            using var response = await http.SendAsync(request); Assert.Equal(status, (int)response.StatusCode);
        }
        using var url = await http.GetAsync("/api/v1/events?token=hidden"); Assert.Equal(HttpStatusCode.BadRequest, url.StatusCode);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/events");
        preflight.Headers.Add("Origin", "http://localhost:5173"); preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,x-event-topics,x-event-resources,last-event-id");
        using var allowed = await http.SendAsync(preflight); Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        await using var bad = sdk.StreamEventsAsync(new([], [], "bad")).GetAsyncEnumerator();
        Assert.Equal(409, (await Assert.ThrowsAsync<MabiToolApiException>(() => bad.MoveNextAsync().AsTask())).Status);
        await app.StopAsync();
    }

    [Fact]
    public async Task BrowserOriginStreamClosesWhenMonotonicCredentialLifetimeExpires()
    {
        using var workspace = new Workspace(); var clock = new Clock(); const string origin = "http://localhost:5173";
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>([origin])), clock, workspace.Options);
        await app.StartAsync(); using var owner = Sdk(app);
        var challenge = await owner.CreatePairingChallengeAsync(new("browser-stream", origin, [ApiPermissions.SystemRead]));
        var grant = app.Services.GetRequiredService<LocalAuthService>().Redeem(new(challenge.ChallengeId, challenge.Code), origin);
        using var browser = new MabiToolClient(Address(app), _ => ValueTask.FromResult(grant.Token), new OriginHandler(origin));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = browser.StreamEventsAsync(cancellationToken: timeout.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync()); Assert.Equal("snapshot", events.Current.Event);
        clock.Advance(LocalAuthService.ClientLifetime);
        Assert.True(await events.MoveNextAsync()); Assert.Equal("auth.required", events.Current.Data.GetProperty("code").GetString());
        Assert.False(await events.MoveNextAsync()); await app.StopAsync();
    }

    [Fact]
    public async Task CSharpSdkParsesFragmentedUtf8CrLfMultilineAndControlsAndBoundsFrames()
    {
        const string text = "\uFEFF: connected\r\nid: cursor\r\nevent: snapshot\r\ndata: {\r\ndata: \"value\":\"台服🎵\"}\r\n\r\n: heartbeat\r\nevent: control\rdata: {\"code\":\"auth.required\"}\r\rdata: {\"unfinished\":true}";
        using var sdk = new MabiToolClient(new("http://localhost:19998/"), _ => ValueTask.FromResult(Token), new Handler(text));
        var messages = new List<StreamMessage>(); await foreach (var message in sdk.StreamEventsAsync()) messages.Add(message);
        Assert.Equal(2, messages.Count); Assert.Equal("台服🎵", messages[0].Data.GetProperty("value").GetString());
        Assert.Equal("cursor", messages[1].Id); Assert.Equal("control", messages[1].Event);
        using var large = new MabiToolClient(new("http://localhost:19998/"), _ => ValueTask.FromResult(Token), new Handler("data: " + new string('x', 4 * 1024 * 1024)));
        await using var enumerator = large.StreamEventsAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidDataException>(() => enumerator.MoveNextAsync().AsTask());
    }

    private static async Task Publish(StateEventHub hub, SqliteStore store, PersistedEvent state)
    {
        await store.WriteAsync(tx => SqliteStore.AppendEvent(tx, state.Event));
        var sequence = (long)store.ReadScalar("SELECT sequence FROM core_outbox WHERE event_id=$event", ("$event", state.Event.EventId))!;
        await hub.PublishAsync(state with { Sequence = sequence }, default);
    }

    private static Uri Address(Microsoft.AspNetCore.Builder.WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static MabiToolClient Sdk(Microsoft.AspNetCore.Builder.WebApplication app, string token = Token) => new(Address(app), _ => ValueTask.FromResult(token));
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan interval) => timestamp += interval.Ticks;
    }
    private sealed class Handler(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new Fragmented(Encoding.UTF8.GetBytes(text))) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } });
    }
    private sealed class OriginHandler(string origin) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { request.Headers.Add("Origin", origin); return base.SendAsync(request, ct); }
    }
    private sealed class Fragmented(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], ct);
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string directory;
        public StorageOptions Options => new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data"));
        public Workspace()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
            directory = Path.Combine(root!.FullName, "artifacts/tmp/event-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
