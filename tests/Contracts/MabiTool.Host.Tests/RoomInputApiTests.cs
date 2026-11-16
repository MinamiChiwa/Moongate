using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Rooms.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class RoomInputApiTests
{
    private const string Token = "room_input_0123456789012345678901234567890123456789";

    [Fact]
    public async Task GameSelectionChecksCandidateScopeRevisionAndIdempotency()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var owner = Client(app);
        var candidate = (await owner.GetFromJsonAsync<ApiList<GameWindowCandidate>>(ApiContract.GameWindows.Path, ContractJson.Options))!.Items.Single();
        var revision = await ETag(owner, ApiContract.GameBinding.Path);
        var grant = Pair(app); using var other = Client(app, grant.Token);
        await Error(other, ApiContract.SelectGameBinding.Path, new GameBindingRequest(candidate.Id), "scope-select", revision, HttpStatusCode.NotFound, "resource.notFound");
        await Error(owner, ApiContract.SelectGameBinding.Path, new GameBindingRequest(candidate.Id), null, revision, HttpStatusCode.BadRequest, "idempotency.required");
        var selected = await Post<GameBindingSnapshot>(owner, ApiContract.SelectGameBinding.Path, new GameBindingRequest(candidate.Id), "select-first", revision);
        Assert.Equal("bound", selected.State); Assert.Equal(area.Windows.Current.ProcessStartTime, selected.Binding!.Session.ProcessStartTime);
        Assert.Equal(selected, await Post<GameBindingSnapshot>(owner, ApiContract.SelectGameBinding.Path, new GameBindingRequest(candidate.Id), "select-first", revision));
        await Error(owner, ApiContract.SelectGameBinding.Path, new GameBindingRequest(candidate.Id), "stale-select", revision, HttpStatusCode.PreconditionFailed, "revision.stale");
        await Error(owner, ApiContract.SelectGameBinding.Path, new { candidateId = candidate.Id, processId = 999 }, "extra-select",
            await ETag(owner, ApiContract.GameBinding.Path), HttpStatusCode.BadRequest, "request.invalid");
        Assert.Empty(area.Device.Commands);
        await app.StopAsync();
    }

    [Fact]
    public async Task BackendReadinessAndDuplicateCommitKeepOneMusicLeaseUntilExplicitRelease()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var owner = Client(app);
        await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
        await Bind(owner); await Save(owner);
        var room = await Post<RoomLifecycleSnapshot>(owner, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.Endpoint, "Host"));
        var ready = await Ready(owner);
        Assert.True(ready.LocalPreparation!.CanStart); Assert.True(ready.LocalPreparation.CanStop); Assert.Empty(area.Device.Commands);
        var command = server.Commands.Last(x => x.GetProperty("type").GetString() == "set-ready").GetProperty("capabilities");
        Assert.True(command.GetProperty("canStart").GetBoolean()); Assert.InRange(command.GetProperty("clockRoundTripMs").GetDouble(), 0, 250);
        var now = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
        var action = new ScheduledAction("start-action", "round", "start", now + 500, server.ServerEpoch) { Mode = "ensemble" };
        await server.SendFrameAsync(room.MemberId!, new { type = "prepare-action", mode = "ensemble", action });
        await Wait(() => server.Commands.Any(x => x.GetProperty("type").GetString() == "action-ready" && x.GetProperty("ready").GetBoolean()));
        await server.SendFrameAsync(room.MemberId!, new { type = "commit-action", action });
        await server.SendFrameAsync(room.MemberId!, new { type = "commit-action", action });
        await Wait(() => area.Device.Commands.Count == 2);
        Assert.Equal(["down:Space", "up:Space"], area.Device.Commands);
        var leases = app.Services.GetRequiredService<ResourceLeaseCoordinator>();
        Assert.NotNull(leases.FindConflict(["foreground-input"]));
        var id = ready.LocalPreparation.Id!;
        var released = await Post<RoomLifecycleSnapshot>(owner, ApiContract.ReleaseRoomInput.Path.Replace("{id}", id), new { }, "release-first");
        Assert.Equal("connected", released.State); Assert.Equal(room.MemberId, released.MemberId);
        Assert.Equal("released", released.LocalPreparation!.State); Assert.Equal(EffectStatus.Unknown, released.LocalPreparation.EffectStatus.KnownValue);
        Assert.Null(leases.FindConflict(["foreground-input"]));
        var next = await Ready(owner); Assert.NotEqual(id, next.LocalPreparation!.Id);
        var replayed = await Post<RoomLifecycleSnapshot>(owner, ApiContract.ReleaseRoomInput.Path.Replace("{id}", id), new { }, "release-first");
        Assert.Equal(id, replayed.LocalPreparation!.Id);
        await Error(owner, ApiContract.ReleaseRoomInput.Path.Replace("{id}", id), new { }, "release-old-again", null, HttpStatusCode.Conflict, "room.planStale");
        Assert.Equal(next.LocalPreparation.Id, (await owner.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options))!.LocalPreparation!.Id);
        await app.StopAsync(); Assert.Null(leases.FindConflict(["foreground-input"]));
    }

    [Fact]
    public async Task PreferenceRevisionsPersistAndRestartDoesNotRestoreRoomInputs()
    {
        using var area = new Area();
        RoomPreferences stored;
        string initial;
        await using (var app = area.Build())
        {
            await app.StartAsync(); using var owner = Client(app); await Bind(owner);
            initial = await ETag(owner, ApiContract.RoomPreferences.Path);
            stored = await Save(owner);
            await Error(owner, ApiContract.EditRoomPreferences.Path, Preferences, "stale-preferences", initial, HttpStatusCode.PreconditionFailed, "revision.stale");
            var grant = Pair(app); using var other = Client(app, grant.Token);
            Assert.Equal(new UInt64String(0), (await other.GetFromJsonAsync<RoomPreferences>(ApiContract.RoomPreferences.Path, ContractJson.Options))!.Revision);
            await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
            await Post<RoomLifecycleSnapshot>(owner, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.Endpoint, "Host"));
            await Ready(owner); await app.StopAsync();
        }
        await using (var app = area.Build(initialize: false))
        {
            await app.StartAsync(); using var owner = Client(app);
            Assert.Equal(stored.Revision, (await owner.GetFromJsonAsync<RoomPreferences>(ApiContract.RoomPreferences.Path, ContractJson.Options))!.Revision);
            Assert.Equal(stored.Revision, (await Post<RoomPreferences>(owner, ApiContract.EditRoomPreferences.Path, Preferences, "save-preferences", initial)).Revision);
            var room = await owner.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options);
            Assert.Equal("disconnected", room!.State); Assert.Null(room.LocalPreparation);
            Assert.Equal("unbound", (await owner.GetFromJsonAsync<GameBindingSnapshot>(ApiContract.GameBinding.Path, ContractJson.Options))!.State);
            Assert.Empty(area.Device.Commands); await app.StopAsync();
        }
    }

    [Fact]
    public async Task RevocationAndWindowRestartWithdrawPreparationWithoutDisconnectingRoom()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var owner = Client(app); await Bind(owner);
        var grant = Pair(app); using var other = Client(app, grant.Token); await Save(other);
        await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
        var room = await Post<RoomLifecycleSnapshot>(other, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.Endpoint, "Member")); await Ready(other);
        app.Services.GetRequiredService<LocalAuthService>().Revoke(grant.Session.ClientId);
        var current = app.Services.GetRequiredService<RoomSessionManager>().Get(grant.Session.ClientId);
        Assert.Equal("permission.denied", current.LocalPreparation!.Reason); Assert.Equal(room.MemberId, current.MemberId); Assert.Equal("connected", current.State);
        Assert.Null(app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"]));
        await Save(owner); var ownRoom = await Post<RoomLifecycleSnapshot>(owner, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.Endpoint, "Host")); var old = await Ready(owner);
        area.Windows.Current = area.Windows.Current with { ProcessStartTime = area.Windows.Current.ProcessStartTime.AddSeconds(1) };
        var invalid = await owner.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options);
        Assert.Equal("input.bindingChanged", invalid!.LocalPreparation!.Reason); Assert.Equal(ownRoom.MemberId, invalid.MemberId);
        await Bind(owner); var next = await Ready(owner); Assert.NotEqual(old.LocalPreparation!.Id, next.LocalPreparation!.Id);
        await Error(owner, ApiContract.ReleaseRoomInput.Path.Replace("{id}", old.LocalPreparation.Id!), new { }, "old-after-restart", null, HttpStatusCode.Conflict, "room.planStale");
        Assert.Empty(area.Device.Commands); await app.StopAsync();
    }

    internal static RoomPreferences Preferences => RoomPreferences.Default with { MusicStopRule = MusicStopRule.Manual,
        Bindings = new Dictionary<string, ModeInputPreferences> { ["emote"] = new(new(LocalBindingKind.Key, 32), null, false),
            ["ensemble"] = new(new(LocalBindingKind.Key, 32), new(LocalBindingKind.Key, 27), false),
            ["independent"] = new(new(LocalBindingKind.Key, 32), new(LocalBindingKind.Key, 27), false) } };
    internal static Task<RoomLifecycleSnapshot> Ready(HttpClient client) => Put<RoomLifecycleSnapshot>(client, ApiContract.SetRoomReadiness.Path,
        new RoomReadinessRequest(true, false, false, false, ReadyMode: "ensemble", ClockRoundTripMs: 12000));
    internal static async Task<RoomPreferences> Save(HttpClient client) => await Post<RoomPreferences>(client, ApiContract.EditRoomPreferences.Path,
        Preferences, "save-preferences", await ETag(client, ApiContract.RoomPreferences.Path));
    internal static async Task Bind(HttpClient client)
    {
        var candidate = (await client.GetFromJsonAsync<ApiList<GameWindowCandidate>>(ApiContract.GameWindows.Path, ContractJson.Options))!.Items.Last();
        await Post<GameBindingSnapshot>(client, ApiContract.SelectGameBinding.Path, new GameBindingRequest(candidate.Id), Guid.NewGuid().ToString("N"), await ETag(client, ApiContract.GameBinding.Path));
    }
    private static AccessGrant Pair(WebApplication app)
    {
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var challenge = auth.CreateChallenge(new("房间客户端", null, [ApiPermissions.SystemRead, ApiPermissions.RoomsCoordinate]));
        return auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
    }
    internal static HttpClient Client(WebApplication app, string token = Token)
    {
        var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", token); return client;
    }
    internal static async Task<string> ETag(HttpClient client, string path)
    { using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode(); return response.Headers.ETag!.ToString(); }
    internal static async Task<T> Post<T>(HttpClient client, string path, object body, string? key = null, string? etag = null)
    {
        using var request = Request(path, body, key, etag); using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }
    private static async Task<T> Put<T>(HttpClient client, string path, object body)
    { using var response = await client.PutAsJsonAsync(path, body, ContractJson.Options); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!; }
    private static HttpRequestMessage Request(string path, object body, string? key, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key); if (etag is not null) request.Headers.Add("If-Match", etag); return request;
    }
    private static async Task Error(HttpClient client, string path, object body, string? key, string? etag, HttpStatusCode status, string code)
    { using var request = Request(path, body, key, etag); using var response = await client.SendAsync(request); Assert.Equal(status, response.StatusCode); Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code); }
    private static async Task Wait(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }

    internal sealed class Area : IDisposable
    {
        private readonly string root = Path.Combine(FindRoot(), "artifacts", "test-data", "room-input-" + Guid.NewGuid().ToString("N"));
        public Device Device { get; } = new();
        public WindowSource Windows { get; } = new();
        public WebApplication Build(bool initialize = true) => BackendApplication.Build(new(0, Token, new HashSet<string>()),
            storage: new(Path.Combine(root, "bootstrap"), initialize, DataRoot: Path.Combine(root, "data")), gameWindowSource: Windows,
            gameControls: sp =>
            {
                var bindings = new GameSessionBindingTracker(); var leases = sp.GetRequiredService<ResourceLeaseCoordinator>();
                return new(bindings, leases, new(leases, bindings, Device), false);
            });
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private static string FindRoot()
        { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent; return directory!.FullName; }
    }
    internal sealed class WindowSource : IGameWindowSource
    {
        public GameWindowObservation Current { get; set; } = new("TW", 100, DateTimeOffset.UtcNow, new(500), "fake-game", new(0, 0, 800, 600), 96, 96, false);
        public IReadOnlyList<SelectableGameWindow> Enumerate() => [new("洛奇 Mobile", "MabinogiMobile", Current)];
        public GameWindowObservation? Refresh(GameWindowObservation selected) => selected.ProcessStartTime == Current.ProcessStartTime && selected.ProcessId == Current.ProcessId ? Current : null;
        public bool TryActivate(GameWindowObservation selected) => Refresh(selected) is not null;
    }
    internal sealed class Device : IGameInputDevice
    {
        public ConcurrentQueue<string> Commands { get; } = new();
        public UInt64String? ForegroundWindowIdentity => new(500);
        public void Press(InputControl control) => Commands.Enqueue("down:" + control);
        public void Release(InputControl control) => Commands.Enqueue("up:" + control);
        public void MovePointer(PhysicalScreenPoint point) => Commands.Enqueue($"move:{point.X},{point.Y}");
        public void ReleaseAll() { }
    }
}
