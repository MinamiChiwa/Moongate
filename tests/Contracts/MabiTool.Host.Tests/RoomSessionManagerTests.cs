using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Rooms.Domain;
using MabiTool.Modules.Rooms.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class RoomSessionManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedRejectionReleasesResourcesAndAcceptedCancellationKeepsReadiness(bool reject)
    {
        await using var server = await FakeRoomServer.StartAsync();
        var barrier = new ActivityBarrier();
        await using var manager = InputManager(barrier);
        await manager.CreateAsync("owner", new(server.Endpoint, "Host"), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ReadyResume = resume.Task;
        server.RejectReadiness = reject;
        var request = manager.SetReadinessAsync("owner", new(true, false, true, true), cancellation.Token);
        try
        {
            await server.ReadinessObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(request.IsCompleted);
            Assert.Null(barrier.TryEnterMaintenance("updater"));
        }
        finally { resume.TrySetResult(); }
        if (reject) Assert.Equal("NOT_READY", (await Assert.ThrowsAsync<RoomRequestException>(() => request)).Code);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        if (reject) Assert.Empty(barrier.Snapshot().Activities);
        else
        {
            Assert.NotEmpty(barrier.Snapshot().Activities);
            Assert.Null(barrier.TryEnterMaintenance("updater"));
        }
        server.RejectReadiness = false;
        await manager.SetReadinessAsync("owner", new(false, false, true, true), CancellationToken.None);
        using var maintenance = barrier.TryEnterMaintenance("updater");
        Assert.NotNull(maintenance);
    }

    [Fact]
    public async Task ReadinessBlocksMaintenanceUntilUnreadyRebindLeaveOrDisconnect()
    {
        await using var server = await FakeRoomServer.StartAsync();
        var barrier = new ActivityBarrier();
        await using var manager = InputManager(barrier);
        var room = await manager.CreateAsync("owner", new(server.Endpoint, "Host"), CancellationToken.None);
        await Ready();
        Assert.Null(barrier.TryEnterMaintenance("updater"));
        await manager.SetReadinessAsync("owner", new(false, false, true, true), CancellationToken.None);
        using (var maintenance = barrier.TryEnterMaintenance("updater"))
        {
            Assert.NotNull(maintenance);
            var before = server.Commands.Count;
            Assert.Equal("update.maintenanceActive", (await Assert.ThrowsAsync<ActivityBarrierException>(Ready)).Code);
            Assert.Equal(before, server.Commands.Count);
        }
        await Ready();
        await manager.BindCharacterAsync("owner", new(room.MemberId!, "123", "3"), CancellationToken.None);
        Assert.Empty(barrier.Snapshot().Activities);
        await Ready();
        await manager.LeaveAsync("owner", new(), CancellationToken.None);
        Assert.Empty(barrier.Snapshot().Activities);
        room = await manager.CreateAsync("owner", new(server.Endpoint, "Host"), CancellationToken.None);
        await Ready();
        server.Disconnect(room.MemberId!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (barrier.Snapshot().Activities.Count != 0) await Task.Delay(10, timeout.Token);
        using var idle = barrier.TryEnterMaintenance("updater");
        Assert.NotNull(idle);

        async Task Ready() => await manager.SetReadinessAsync("owner", new(true, false, true, true), CancellationToken.None);
    }

    [Fact]
    public async Task LifecycleKeepsResumeSecretPrivateAndEnforcesHostClosePermission()
    {
        await using var server = await FakeRoomServer.StartAsync();
        await using var manager = new RoomSessionManager();
        var endpoint = server.Endpoint;
        var host = await manager.CreateAsync("host-client", new(endpoint, "Host"), CancellationToken.None);
        var invitation = manager.GetInvitation("host-client");
        Assert.Equal("connected", host.State);
        Assert.Equal(host.Code, invitation.Code);
        Assert.DoesNotContain("token", JsonSerializer.Serialize(invitation), StringComparison.OrdinalIgnoreCase);

        var member = await manager.JoinAsync("member-client", new(endpoint, invitation.Code, "Member"), CancellationToken.None);
        Assert.Equal("connected", member.State);
        var denied = await Assert.ThrowsAsync<OperationException>(() =>
            manager.LeaveAsync("member-client", new(true), CancellationToken.None));
        Assert.Equal("permission.denied", denied.Code);

        await manager.LeaveAsync("member-client", new(), CancellationToken.None);
        var closed = await manager.LeaveAsync("host-client", new(true), CancellationToken.None);
        Assert.Equal("confirmed", closed.CloseStatus);
        Assert.Equal("disconnected", closed.State);
    }

    [Fact]
    public async Task RoomControlsForwardRosterReadinessModeAndCharacterBinding()
    {
        await using var server = await FakeRoomServer.StartAsync();
        await using var manager = InputManager();
        var room = await manager.CreateAsync("host-client", new(server.Endpoint, "Host"), CancellationToken.None);

        await manager.EditRosterAsync("host-client", new(room.MemberId!, Group: 2, Order: 1, MusicRole: "initiator", Instrument: "豎琴"), CancellationToken.None);
        await manager.SetModeAsync("host-client", new("ensemble"), CancellationToken.None);
        await manager.SetReadinessAsync("host-client", new(true, false, true, true, TimingOffsetMs: -25, MusicRole: "initiator", ReadyMode: "ensemble"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.GetClockSample("host-client") is null) await Task.Delay(10, timeout.Token);
        Assert.Equal(-25, manager.GetClockSample("host-client")!.PersonalOffsetMs);
        await manager.BindCharacterAsync("host-client", new(room.MemberId!, "123", "3"), CancellationToken.None);

        Assert.Contains(server.Commands, command => command.GetProperty("type").GetString() == "edit-ensemble-member" &&
            command.GetProperty("group").GetInt32() == 2 && command.GetProperty("instrument").GetString() == "豎琴");
        Assert.Contains(server.Commands, command => command.GetProperty("type").GetString() == "set-room-mode" && command.GetProperty("mode").GetString() == "ensemble");
        Assert.Contains(server.Commands, command => command.GetProperty("type").GetString() == "set-ready" && command.GetProperty("ready").GetBoolean() &&
            command.GetProperty("capabilities").GetProperty("timingOffsetMs").GetInt32() == -25);
        Assert.Contains(server.Commands, command => command.GetProperty("type").GetString() == "bind-game-character" && command.GetProperty("characterId").GetString() == "123" &&
            command.GetProperty("realmId").GetInt32() == 3);
    }

    [Fact]
    public async Task RealRoomServerAcceptsCharacterBindingLeaderSelectionAndKick()
    {
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, CancellationToken.None);
        await using var manager = new RoomSessionManager();
        var host = await manager.CreateAsync("host-client", new(server.LocalEndpoint, "Host"), CancellationToken.None);
        var clock = new StopwatchClock(); var roomClock = new RoomClock(clock);
        using var scheduler = new ActionScheduler(clock, roomClock, new UnavailableInputExecutor());
        await using var member = new RoomClient(clock, roomClock, scheduler);
        await member.ConnectAsync(server.LocalEndpoint);
        var joined = await member.JoinRoomAsync("Member", host.Code!);
        await member.SendAsync(new { type = "game-observation", generation = "g", sequence = 1, status = "identified",
            identity = new { characterId = "123", realmId = 3, nickname = "Member" },
            concert = new { id = "900", hostCharacterId = "123", orderedCharacterIds = new[] { "123", "456" },
                members = new[] { new { characterId = "123", realmId = 3, nickname = "Member" }, new { characterId = "456", realmId = 3, nickname = "Host" } } } });
        await Until(() => manager.Get("host-client").Room is { } room && room.GetProperty("observedCharacters").GetArrayLength() == 2);

        // 为未抓包的房主匹配同场成员观测到的角色：realmId 以数字发送，取消时显式发送 characterId:null。
        var bound = await manager.BindCharacterAsync("host-client", new(host.MemberId!, "456", "3"), CancellationToken.None);
        Assert.Equal("456", Member(bound, host.MemberId!).GetProperty("gameIdentity").GetProperty("characterId").GetString());
        var cleared = await manager.BindCharacterAsync("host-client", new(host.MemberId!, null, null), CancellationToken.None);
        Assert.Equal(JsonValueKind.Null, Member(cleared, host.MemberId!).GetProperty("gameIdentity").ValueKind);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() =>
            manager.BindCharacterAsync("host-client", new(joined.MemberId, "123", "tw"), CancellationToken.None))).Code);
        var missing = await Assert.ThrowsAsync<OperationException>(() =>
            manager.BindCharacterAsync("host-client", new(joined.MemberId, "999", "3"), CancellationToken.None));
        Assert.Equal("room.commandRejected", missing.Code);
        Assert.Equal("CHARACTER_NOT_OBSERVED", missing.Details!["protocolCode"].GetString());

        var led = await manager.SelectLeaderAsync("host-client", new(joined.MemberId), CancellationToken.None);
        Assert.Equal(joined.MemberId, led.Room!.Value.GetProperty("leaderId").GetString());
        var other = await manager.JoinAsync("member-client", new(server.LocalEndpoint, host.Code!, "Other"), CancellationToken.None);
        Assert.Equal("permission.denied", (await Assert.ThrowsAsync<OperationException>(() =>
            manager.KickMemberAsync("member-client", new(joined.MemberId), CancellationToken.None))).Code);
        Assert.Equal("room.commandRejected", (await Assert.ThrowsAsync<OperationException>(() =>
            manager.KickMemberAsync("host-client", new(host.MemberId!), CancellationToken.None))).Code);

        var kicked = await manager.KickMemberAsync("host-client", new(joined.MemberId), CancellationToken.None);
        Assert.DoesNotContain(kicked.Room!.Value.GetProperty("members").EnumerateArray(), row => row.GetProperty("id").GetString() == joined.MemberId);
        Assert.Equal(host.MemberId, kicked.Room.Value.GetProperty("leaderId").GetString());
        await Until(() => member.Session is null);
        Assert.Equal("connected", manager.Get("member-client").State);
        Assert.Null(manager.Get("member-client").ExitReason);

        // 被移出的成员回到未加入状态并带上原因；重新加入后清除，房主关闭房间时成员得到 roomClosed。
        await manager.KickMemberAsync("host-client", new(other.MemberId!), CancellationToken.None);
        await Until(() => manager.Get("member-client").State == "disconnected");
        var removed = manager.Get("member-client");
        Assert.Equal("kicked", removed.ExitReason);
        Assert.Null(removed.Room);
        var rejoined = await manager.JoinAsync("member-client", new(server.LocalEndpoint, host.Code!, "Other"), CancellationToken.None);
        Assert.Equal("connected", rejoined.State);
        Assert.Null(rejoined.ExitReason);
        var closed = await manager.LeaveAsync("host-client", new(true), CancellationToken.None);
        Assert.Null(closed.ExitReason);
        await Until(() => manager.Get("member-client").State == "disconnected");
        Assert.Equal("roomClosed", manager.Get("member-client").ExitReason);
    }

    private static JsonElement Member(RoomLifecycleSnapshot snapshot, string id) =>
        snapshot.Room!.Value.GetProperty("members").EnumerateArray().Single(row => row.GetProperty("id").GetString() == id);
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }

    private static RoomSessionManager InputManager(ActivityBarrier? barrier = null)
    {
        var tracker = new GameSessionBindingTracker();
        tracker.Update(new("TW", 100, DateTimeOffset.UtcNow, new(42), "test", new(0, 0, 800, 600), 96, 96, false));
        var leases = new ResourceLeaseCoordinator(activityBarrier: barrier);
        var input = new GameInputBroker(leases, tracker, new TestInputDevice());
        var preferences = RoomPreferences.Default with { Bindings = new Dictionary<string, ModeInputPreferences>
        { ["emote"] = new(new(LocalBindingKind.Key, 32), null, false), ["ensemble"] = new(new(LocalBindingKind.Key, 32), new(LocalBindingKind.Key, 27), false),
            ["independent"] = new(new(LocalBindingKind.Key, 32), new(LocalBindingKind.Key, 27), false) } };
        return new(activityBarrier: barrier, inputPorts: new(tracker, leases, input), preferences: _ => preferences);
    }
    private sealed class TestInputDevice : IGameInputDevice
    {
        public UInt64String? ForegroundWindowIdentity => new(42);
        public void Press(InputControl control) { }
        public void Release(InputControl control) { }
        public void MovePointer(PhysicalScreenPoint point) { }
        public void ReleaseAll() { }
    }

    internal sealed class FakeRoomServer : IAsyncDisposable
    {
        private sealed record Member(string Id, string Token, string Nickname, WebSocket Socket, bool Ready = false);
        private readonly WebApplication app;
        private readonly ConcurrentDictionary<string, Member> members = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<JsonElement> commands = new();
        private readonly string epoch = Guid.NewGuid().ToString("D");
        private string? hostId;
        public string Endpoint { get; }
        public IReadOnlyCollection<JsonElement> Commands => commands.ToArray();
        public string ServerEpoch => epoch;
        private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> sendGates = new();
        public Task SendFrameAsync(string memberId, object frame) => SendAsync(members[memberId].Socket, frame, CancellationToken.None);
        public void Disconnect(string id) => members[id].Socket.Abort();
        public TaskCompletionSource ReadinessObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReadyResume { get; set; } = Task.CompletedTask;
        public bool RejectReadiness { get; set; }
        public Func<JsonElement, Task>? CommandHook { get; set; }
        public bool ConfirmCommands { get; set; } = true;
        public bool SupportsScoreDraft { get; set; }
        private RoomScoreDraft? scoreDraft;
        private ulong scoreDraftRevision;

        private FakeRoomServer(WebApplication app, string endpoint) => (this.app, Endpoint) = (app, endpoint);

        public static async Task<FakeRoomServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Configuration.Sources.Clear();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, 0));
            var app = builder.Build();
            app.UseWebSockets();
            FakeRoomServer? service = null;
            app.Map("/", async context => await service!.HandleAsync(context));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            service = new(app, "ws" + address[4..]);
            return service;
        }

        private async Task HandleAsync(HttpContext context)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await SendAsync(socket, new { type = "hello", product = "MabiSync", protocolVersion = 1, serverEpoch = epoch,
                features = new { gameObservation = 1, firstCycleStop = 1, firstCycleTail = 1, rosterEditing = 1, roundScoreDraft = SupportsScoreDraft ? 1 : 0 } }, context.RequestAborted);
            var buffer = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    using var document = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                    var message = document.RootElement;
                    var type = message.GetProperty("type").GetString();
                    switch (type)
                    {
                        case "hello":
                            await SendAsync(socket, new { type = "hello-accepted", product = "MabiSync", protocolVersion = 1, serverEpoch = epoch }, context.RequestAborted);
                            break;
                        case "sync":
                            var t1 = message.GetProperty("t1").GetDouble();
                            var now = System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency);
                            await SendAsync(socket, new { type = "sync", id = message.GetProperty("id").GetInt32(), t1, t2 = now, t3 = now, serverEpoch = epoch }, context.RequestAborted);
                            break;
                        case "create":
                            hostId = Guid.NewGuid().ToString("N");
                            await AttachAsync(socket, hostId, message.GetProperty("nickname").GetString()!, context.RequestAborted);
                            break;
                        case "join":
                            await AttachAsync(socket, Guid.NewGuid().ToString("N"), message.GetProperty("nickname").GetString()!, context.RequestAborted);
                            break;
                        case "leave":
                            await SendAsync(socket, new { type = "left", serverEpoch = epoch }, context.RequestAborted);
                            return;
                        case "close-room":
                            var closureId = Guid.NewGuid().ToString("N");
                            await SendAsync(socket, new { type = "room-closed", closureId, serverEpoch = epoch }, context.RequestAborted);
                            break;
                        case "room-closed-ack":
                            await SendAsync(socket, new { type = "room-close-complete", closureId = message.GetProperty("closureId").GetString(), unconfirmedMembers = 0, serverEpoch = epoch }, context.RequestAborted);
                            await SendAsync(socket, new { type = "left", serverEpoch = epoch }, context.RequestAborted);
                            return;
                        default:
                            commands.Enqueue(message.Clone());
                            if (CommandHook is { } hook) await hook(message.Clone()).WaitAsync(context.RequestAborted);
                            if (SupportsScoreDraft && type == "publish-score-draft")
                            {
                                scoreDraft = new(1, Guid.NewGuid().ToString("N"), new(++scoreDraftRevision),
                                    message.GetProperty("source").Deserialize<MusicScoreSelection>(ContractJson.Options)!,
                                    message.GetProperty("score").Deserialize<MusicResolvedScore>(ContractJson.Options)!,
                                    message.GetProperty("assignments").Deserialize<RoomScoreAssignment[]>(ContractJson.Options)!);
                                await BroadcastAsync(context.RequestAborted);
                            }
                            if (type == "set-ready" && message.GetProperty("ready").GetBoolean())
                            {
                                ReadinessObserved.TrySetResult();
                                await ReadyResume;
                                if (RejectReadiness)
                                {
                                    await SendAsync(socket, new { type = "error", requestId = message.GetProperty("requestId").GetString(), code = "NOT_READY", serverEpoch = epoch }, context.RequestAborted);
                                    break;
                                }
                            }
                            if (type is "set-ready" or "bind-game-character")
                            {
                                var member = members.Values.Single(m => ReferenceEquals(m.Socket, socket));
                                members[member.Id] = member with { Ready = type == "set-ready" && message.GetProperty("ready").GetBoolean() };
                                await BroadcastAsync(context.RequestAborted);
                            }
                            if (ConfirmCommands && message.TryGetProperty("requestId", out var requestId))
                                await SendAsync(socket, new { type = "request-complete", requestId = requestId.GetString(), serverEpoch = epoch }, context.RequestAborted);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }

        private async Task AttachAsync(WebSocket socket, string id, string nickname, CancellationToken token)
        {
            var member = new Member(id, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(), nickname, socket);
            members[id] = member;
            await SendAsync(socket, new { type = "session", code = "ABC123", memberId = id, token = member.Token, serverEpoch = epoch }, token);
            await BroadcastAsync(token);
        }

        private async Task BroadcastAsync(CancellationToken token)
        {
            var roster = members.Values.Select(m => new { id = m.Id, nickname = m.Nickname, ready = m.Ready, connected = m.Socket.State == WebSocketState.Open }).ToArray();
            var room = new Dictionary<string, object?> { ["code"] = "ABC123", ["hostId"] = hostId, ["members"] = roster };
            if (SupportsScoreDraft)
            {
                room["scoreDraftRevision"] = scoreDraftRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (scoreDraft is not null)
                { room["scoreDraft"] = JsonSerializer.SerializeToElement(scoreDraft, ContractJson.Options); room["scoreMappings"] = Array.Empty<RoomScoreMemberMapping>(); }
            }
            foreach (var recipient in members.Values.Where(m => m.Socket.State == WebSocketState.Open))
                await SendAsync(recipient.Socket, new { type = "state", serverEpoch = epoch, room }, token);
        }

        private async Task SendAsync(WebSocket socket, object value, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            var gate = sendGates.GetOrAdd(socket, _ => new(1, 1));
            await gate.WaitAsync(token);
            try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token); }
            finally { gate.Release(); }
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
