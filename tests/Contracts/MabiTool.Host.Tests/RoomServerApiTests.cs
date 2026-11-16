using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.Tunnels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class RoomServerApiTests
{
    private const string Token = "room-server-owner-token-123456789012345678901234567890";

    [Fact]
    public async Task LoopbackServerSharesStatusAndEnforcesOwnershipRevisionAndIdempotency()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync();
        var grant = Pair(app, true); using var builder = Client(app, grant.Token); using var owner = Client(app);
        var viewerGrant = Pair(app, false); using var viewer = Client(app, viewerGrant.Token);
        var initial = await Read(builder); var request = new RoomServerStartRequest(RoomServerMode.Loopback);
        await Error(builder, ApiContract.StartRoomServer.Path, request, null, initial.ETag, HttpStatusCode.BadRequest, "idempotency.required");
        await Error(builder, ApiContract.StartRoomServer.Path, request, "start-loopback", null, (HttpStatusCode)428, "revision.required");
        await Error(builder, ApiContract.StartRoomServer.Path, new { mode = "loopback", executablePath = "arbitrary.exe" },
            "unexpected-server-path", initial.ETag, HttpStatusCode.BadRequest, "request.invalid");
        var queued = await Post<OperationSnapshot>(builder, ApiContract.StartRoomServer.Path, request, "start-loopback", initial.ETag);
        var finished = await Terminal(builder, queued.Id); Assert.Equal(OperationState.Succeeded, finished.State.KnownValue);
        var running = await Read(builder); Assert.Equal(RoomServerState.Running, running.Snapshot.State.KnownValue);
        Assert.Equal(grant.Session.ClientId, running.Snapshot.Owner);
        Assert.Equal(running.Snapshot.ServerEpoch, (await Read(viewer)).Snapshot.ServerEpoch);
        var endpoint = new Uri(running.Snapshot.LocalEndpoint!);
        Assert.NotEqual(builder.BaseAddress!.Port, endpoint.Port);
        Assert.True(await new RoomTunnelProbe().VerifyAsync(endpoint, running.Snapshot.ServerEpoch!, CancellationToken.None));
        using var roomHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { BaseAddress = new UriBuilder(endpoint) { Scheme = "http", Path = "/" }.Uri };
        using var rejected = await roomHttp.GetAsync(ApiContract.SystemInfo.Path);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        var created = await Post<RoomLifecycleSnapshot>(builder, ApiContract.CreateRoom.Path,
            new RoomCreateRequest(endpoint.AbsoluteUri, "房主"), "create-local-room");
        Assert.Equal("connected", created.State);
        var barrier = app.Services.GetRequiredService<ActivityBarrier>();
        Assert.Contains(barrier.Snapshot().Activities, value => value.Kind == "room.server"); Assert.Null(barrier.TryEnterMaintenance("update"));
        Assert.Contains(queued.Id, app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("rooms"));
        Assert.Equal(queued.Id, (await Post<OperationSnapshot>(builder, ApiContract.StartRoomServer.Path, request, "start-loopback", initial.ETag)).Id);
        await Error(builder, ApiContract.StartRoomServer.Path, new RoomServerStartRequest(RoomServerMode.Lan),
            "start-loopback", initial.ETag, HttpStatusCode.Conflict, "idempotency.conflict");
        await Error(builder, ApiContract.StartRoomServer.Path, request, "stale-loopback", initial.ETag, HttpStatusCode.PreconditionFailed, "revision.stale");
        await Error(builder, ApiContract.StartRoomServer.Path, request, "second-loopback", running.ETag, HttpStatusCode.Conflict, "resource.busy");
        var otherGrant = Pair(app, true); using var other = Client(app, otherGrant.Token);
        Assert.Equal(queued.Id, (await Terminal(other, queued.Id)).Id);
        await Error(other, ApiContract.CancelOperation.Path.Replace("{id}", queued.Id), new CancelOperationRequest("其他客户端取消"),
            null, null, HttpStatusCode.NotFound, "resource.notFound");
        await Error(other, ApiContract.StopRoomServer.Path, new { }, "stop-other-owner", running.ETag, HttpStatusCode.Forbidden, "permission.denied");
        await Error(viewer, ApiContract.StartRoomServer.Path, request, "viewer-loopback", running.ETag, HttpStatusCode.Forbidden, "permission.denied");
        var stopped = await Post<OperationSnapshot>(owner, ApiContract.StopRoomServer.Path, new { }, "stop-as-host-owner", running.ETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(owner, stopped.Id)).State.KnownValue);
        Assert.Equal(RoomServerState.Stopped, (await Read(owner)).Snapshot.State.KnownValue);
        Assert.DoesNotContain(barrier.Snapshot().Activities, value => value.Kind == "room.server");
        Assert.False(await new RoomTunnelProbe().VerifyAsync(endpoint, running.Snapshot.ServerEpoch!, CancellationToken.None));
        Assert.Equal(stopped.Id, (await Post<OperationSnapshot>(owner, ApiContract.StopRoomServer.Path, new { }, "stop-as-host-owner", running.ETag)).Id);
        await app.StopAsync();
    }

    [Fact]
    public async Task RestartReturnsOriginalOperationAndNewRequestRequiresCurrentEpoch()
    {
        using var area = new Area(); string initialETag, originalEpoch, originalId, originalOwner, previousToken;
        var request = new RoomServerStartRequest(RoomServerMode.Loopback);
        await using (var app = area.Build())
        {
            await app.StartAsync(); var grant = Pair(app, true); using var owner = Client(app, grant.Token);
            originalOwner = grant.Session.ClientId; previousToken = grant.Token; initialETag = (await Read(owner)).ETag;
            var original = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, request, "restart-room-start", initialETag);
            await Terminal(owner, original.Id); originalId = original.Id; originalEpoch = (await Read(owner)).Snapshot.ServerEpoch!;
            await app.StopAsync();
        }
        await using (var app = area.Build(false))
        {
            await app.StartAsync(); using var stale = Client(app, previousToken);
            using var refused = await stale.GetAsync(ApiContract.Operation.Path.Replace("{id}", originalId));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            var grant = Pair(app, true); using var owner = Client(app, grant.Token);
            Assert.NotEqual(originalOwner, grant.Session.ClientId);
            Assert.Equal(RoomServerState.Stopped, (await Read(owner)).Snapshot.State.KnownValue);
            var original = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, request, "restart-room-start", initialETag);
            Assert.Equal(originalId, original.Id); Assert.Equal(OperationState.Succeeded, original.State.KnownValue);
            Assert.Equal(originalOwner, original.Owner);
            var otherGrant = Pair(app, false); using var other = Client(app, otherGrant.Token);
            Assert.Equal(originalId, (await Terminal(other, originalId)).Id);
            using var sdk = Sdk(app, otherGrant.Token);
            var projected = await sdk.GetEventSnapshotAsync(new(["operation.changed", "room.server.changed"], [], null));
            Assert.Equal(originalId, projected.Resources.Single(item => item.Type == "operation.changed").Data.GetProperty("id").GetString());
            Assert.Equal("stopped", projected.Resources.Single(item => item.Type == "room.server.changed").Data.GetProperty("state").GetString());
            Assert.Equal(RoomServerState.Stopped, (await Read(owner)).Snapshot.State.KnownValue);
            await Error(owner, ApiContract.StartRoomServer.Path, request, "new-old-room-epoch", initialETag, HttpStatusCode.PreconditionFailed, "revision.stale");
            var next = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, request, "new-room-current", (await Read(owner)).ETag);
            await Terminal(owner, next.Id); Assert.NotEqual(originalEpoch, (await Read(owner)).Snapshot.ServerEpoch);
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task TwoClientsShareServerOperationEventsAndReconnectToStopTerminal()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync();
        var grant = Pair(app, true); var viewerGrant = Pair(app, false);
        using var builder = Sdk(app, grant.Token); using var viewer = Sdk(app, viewerGrant.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = timeout.Token;
        var filter = new EventStreamOptions(["operation.changed", "room.server.changed"], [], null);
        await using var first = builder.StreamEventsAsync(filter, ct).GetAsyncEnumerator(ct);
        var second = viewer.StreamEventsAsync(filter, ct).GetAsyncEnumerator(ct);
        try
        {
            Assert.True(await first.MoveNextAsync()); Assert.Equal("snapshot", first.Current.Event);
            Assert.True(await second.MoveNextAsync()); Assert.Equal("snapshot", second.Current.Event);
            string? tag = null; await builder.GetRoomServerAsync(ct, metadata => tag = metadata.ETag);
            var start = await builder.StartRoomServerAsync(new(RoomServerMode.Loopback), "shared-events-start", tag!, ct);
            var terminal = await Task.WhenAll(ReadTerminal(first, start.Id), ReadTerminal(second, start.Id));
            Assert.Equal(JsonSerializer.Serialize(terminal[0].Data, ContractJson.Options), JsonSerializer.Serialize(terminal[1].Data, ContractJson.Options));
            var cursor = terminal[1].Id; await second.DisposeAsync();
            await builder.GetRoomServerAsync(ct, metadata => tag = metadata.ETag);
            var stop = await builder.StopRoomServerAsync(new(), "shared-events-stop", tag!, ct);
            await ReadTerminal(first, stop.Id);
            await using var resumed = viewer.StreamEventsAsync(filter with { LastEventId = cursor }, ct).GetAsyncEnumerator(ct);
            var stopped = (await ReadTerminal(resumed, stop.Id)).Data.Deserialize<EventEnvelope<OperationSnapshot>>(ContractJson.Options)!.Data;
            Assert.Equal(OperationState.Succeeded, stopped.State.KnownValue);
            Assert.Equal(RoomServerState.Stopped, stopped.Result!.Value.Deserialize<RoomServerSnapshot>(ContractJson.Options)!.State.KnownValue);
            var snapshot = await viewer.GetEventSnapshotAsync(filter, ct);
            Assert.Contains(snapshot.Resources, value => value.Type == "operation.changed" && value.Data.GetProperty("id").GetString() == stop.Id &&
                value.Data.GetProperty("state").GetString() == "succeeded");
        }
        finally { await second.DisposeAsync(); }
        await app.StopAsync();

        static async Task<StreamMessage> ReadTerminal(IAsyncEnumerator<StreamMessage> stream, string operationId)
        {
            while (await stream.MoveNextAsync())
                if (stream.Current.Event == "operation.changed")
                {
                    var operation = stream.Current.Data.Deserialize<EventEnvelope<OperationSnapshot>>(ContractJson.Options)!.Data;
                    if (operation.Id == operationId && operation.State.KnownValue == OperationState.Succeeded) return stream.Current;
                }
            throw new InvalidOperationException("建房事件流在操作终态前关闭");
        }
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task TunnelDisposalFailureFinishesStartAndStopAndReleasesListenerAndActivity()
    {
        using var area = new Area(); using var component = new QuickTunnelTests.Area();
        var processes = new ControlledTunnelProcessFactory(); processes.Tunnel.FailDisposal = true;
        await using var app = area.Build(tunnels: new(component.Component, processes,
            budgets: new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(50))));
        await app.StartAsync(); using var owner = Client(app);
        var start = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, new RoomServerStartRequest(RoomServerMode.Public),
            "start-release-failure", (await Read(owner)).ETag);
        Assert.Equal(OperationState.Failed, (await Terminal(owner, start.Id)).State.KnownValue);
        var running = await Read(owner); Assert.Equal(RoomServerState.Running, running.Snapshot.State.KnownValue);
        var stop = await Post<OperationSnapshot>(owner, ApiContract.StopRoomServer.Path, new { }, "stop-release-failure", running.ETag);
        Assert.Equal(OperationState.Failed, (await Terminal(owner, stop.Id)).State.KnownValue);
        Assert.Equal(RoomServerState.Failed, (await Read(owner)).Snapshot.State.KnownValue);
        Assert.False(await new RoomTunnelProbe().VerifyAsync(new(running.Snapshot.LocalEndpoint!), running.Snapshot.ServerEpoch!, CancellationToken.None));
        Assert.DoesNotContain(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities, value => value.Kind == "room.server");
        var next = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, new RoomServerStartRequest(RoomServerMode.Loopback),
            "start-after-release-failure", (await Read(owner)).ETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(owner, next.Id)).State.KnownValue);
        await app.StopAsync();
    }

    [Fact]
    public async Task PublicComponentFailureRetainsLocalServiceAndMaintenanceRejectsNewStart()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var owner = Client(app);
        var initial = await Read(owner); var barrier = app.Services.GetRequiredService<ActivityBarrier>();
        using (var maintenance = barrier.TryEnterMaintenance("update"))
        {
            Assert.NotNull(maintenance);
            await Error(owner, ApiContract.StartRoomServer.Path, new RoomServerStartRequest(RoomServerMode.Public),
                "public-after-maintenance", initial.ETag, HttpStatusCode.ServiceUnavailable, "update.maintenanceActive");
            Assert.Equal(initial.ETag, (await Read(owner)).ETag);
        }
        var start = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, new RoomServerStartRequest(RoomServerMode.Public),
            "public-after-maintenance", initial.ETag);
        var finished = await Terminal(owner, start.Id); Assert.Equal(OperationState.Failed, finished.State.KnownValue);
        Assert.Equal("room.tunnelNotConfigured", finished.Problem!.Code);
        var running = await Read(owner); Assert.Equal(RoomServerState.Running, running.Snapshot.State.KnownValue);
        Assert.Equal(RoomTunnelState.Failed, running.Snapshot.Tunnel.State.KnownValue); Assert.Null(running.Snapshot.Tunnel.Endpoint);
        Assert.True(await new RoomTunnelProbe().VerifyAsync(new(running.Snapshot.LocalEndpoint!), running.Snapshot.ServerEpoch!, CancellationToken.None));
        var stop = await Post<OperationSnapshot>(owner, ApiContract.StopRoomServer.Path, new { }, "stop-failed-public", running.ETag);
        await Terminal(owner, stop.Id);
        Assert.DoesNotContain(barrier.Snapshot().Activities, value => value.Kind == "room.server"); await app.StopAsync();
    }

    [Fact]
    public async Task CancellationRemainsPendingUntilListenerReleaseAndNextGenerationCanStart()
    {
        using var area = new Area(); using var component = new QuickTunnelTests.Area();
        var processes = new ControlledTunnelProcessFactory(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ControlledTunnelProbe(async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return false; });
        var listener = new DelayedListenerFactory();
        await using var app = area.Build(tunnels: new(component.Component, processes, probe), listeners: listener);
        await app.StartAsync(); using var owner = Client(app);
        var start = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, new RoomServerStartRequest(RoomServerMode.Public),
            "cancel-public-start", (await Read(owner)).ETag);
        processes.Tunnel.Output.Write("https://cancel-room.trycloudflare.com\n"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var cancel = await Post<OperationSnapshot>(owner, ApiContract.CancelOperation.Path.Replace("{id}", start.Id),
                new CancelOperationRequest("取消建房"));
            Assert.Equal(OperationState.CancelRequested, cancel.State.KnownValue);
            await listener.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(OperationState.CancelRequested, (await owner.GetFromJsonAsync<OperationSnapshot>(ApiContract.Operation.Path.Replace("{id}", start.Id), ContractJson.Options))!.State.KnownValue);
            Assert.Contains(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities, value => value.Kind == "room.server");
            Assert.Null((await Read(owner)).Snapshot.Tunnel.Endpoint);
        }
        finally { listener.Release.TrySetResult(); }
        var finished = await Terminal(owner, start.Id);
        Assert.Equal(OperationState.Cancelled, finished.State.KnownValue); Assert.True(processes.Tunnel.Disposed);
        Assert.DoesNotContain(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities, value => value.Kind == "room.server");
        var next = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, new RoomServerStartRequest(RoomServerMode.Loopback),
            "new-after-cancel", (await Read(owner)).ETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(owner, next.Id)).State.KnownValue); await app.StopAsync();
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task LanListenerAcceptsAdvertisedAddressWhileControlApiKeepsLoopback()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var owner = Client(app);
        var start = await Post<OperationSnapshot>(owner, ApiContract.StartRoomServer.Path, new RoomServerStartRequest(RoomServerMode.Lan),
            "start-lan-listener", (await Read(owner)).ETag);
        var finished = await Terminal(owner, start.Id); var running = (await Read(owner)).Snapshot;
        if (finished.State.KnownValue == OperationState.Failed)
        { Assert.Equal("room.lanUnavailable", finished.Problem!.Code); await app.StopAsync(); return; }
        Assert.NotEmpty(running.LanEndpoints);
        var advertised = new Uri(running.LanEndpoints[0]);
        Assert.True(await new RoomTunnelProbe().VerifyAsync(advertised, running.ServerEpoch!, CancellationToken.None));
        using var remoteControl = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3),
            BaseAddress = new UriBuilder(owner.BaseAddress!) { Host = advertised.Host }.Uri };
        await Assert.ThrowsAsync<HttpRequestException>(() => remoteControl.GetAsync(ApiContract.SystemInfo.Path));
        await app.StopAsync();
    }

    private sealed class DelayedListenerFactory : IRoomServerListenerFactory
    {
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IRoomServerListener> StartAsync(RoomServerMode mode, CancellationToken ct) =>
            new Listener(await new RoomServerListenerFactory().StartAsync(mode, ct), Closing, Release);
        private sealed class Listener(IRoomServerListener actual, TaskCompletionSource closing, TaskCompletionSource release) : IRoomServerListener
        {
            public int Port => actual.Port; public string Epoch => actual.Epoch; public string LocalEndpoint => actual.LocalEndpoint;
            public IReadOnlyList<string> LanEndpoints => actual.LanEndpoints;
            public async ValueTask DisposeAsync() { closing.TrySetResult(); await release.Task; await actual.DisposeAsync(); }
        }
    }
    private sealed class Area : IDisposable
    {
        private readonly string root = Path.Combine(QuickTunnelTests.Area.FindRoot(), "artifacts", "tmp", "room-server-api", Guid.NewGuid().ToString("N"));
        public WebApplication Build(bool initialize = true, QuickTunnelFactory? tunnels = null, IRoomServerListenerFactory? listeners = null) =>
            BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: new(Path.Combine(root, "bootstrap"), initialize,
                DataRoot: Path.Combine(root, "data")), roomTunnels: tunnels ?? new(null), roomListeners: listeners);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static AccessGrant Pair(WebApplication app, bool coordinate)
    {
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var challenge = auth.CreateChallenge(new("建房客户端", null, coordinate ? [ApiPermissions.SystemRead, ApiPermissions.RoomsCoordinate] : [ApiPermissions.SystemRead]));
        return auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
    }
    private static HttpClient Client(WebApplication app, string token = Token)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress =
            new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", token); return client;
    }
    private static MabiToolClient Sdk(WebApplication app, string token) =>
        new(new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), _ => ValueTask.FromResult(token));
    private static async Task<(RoomServerSnapshot Snapshot, string ETag)> Read(HttpClient client)
    {
        using var response = await client.GetAsync(ApiContract.RoomServer.Path); response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<RoomServerSnapshot>(ContractJson.Options))!, response.Headers.ETag!.ToString());
    }
    private static async Task<OperationSnapshot> Terminal(HttpClient client, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var value = (await client.GetFromJsonAsync<OperationSnapshot>(ApiContract.Operation.Path.Replace("{id}", id), ContractJson.Options, deadline.Token))!;
            if (SqliteStore.IsTerminal(value.State.KnownValue)) return value;
            await Task.Delay(20, deadline.Token);
        }
    }
    private static async Task<T> Post<T>(HttpClient client, string path, object body, string? key = null, string? etag = null)
    {
        using var request = Request(path, body, key, etag); using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }
    private static async Task Error(HttpClient client, string path, object body, string? key, string? etag, HttpStatusCode expected, string code)
    {
        using var request = Request(path, body, key, etag); using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
    }
    private static HttpRequestMessage Request(string path, object body, string? key, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (etag is not null) request.Headers.Add("If-Match", etag);
        return request;
    }
}
