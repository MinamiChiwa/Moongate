using System.Net.WebSockets;
using System.Text.Json;
using MabiTool.RoomServer;
using MabiTool.RoomServer.Domain;
using MabiTool.RoomServer.Protocol;
using MabiTool.RoomServer.Transport;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Rooms.Tests;

public sealed class RoomServerResourceTests
{
    [Fact]
    public async Task SlowConnectionClosesAndReleasesBothBudgetsAndKernelAttachment()
    {
        var options = new RoomServerOptions(MaximumFrameBytes: 1024, MaximumQueuedFrames: 1,
            MaximumConnectionQueueBytes: 2048, MaximumQueuedBytes: 4096);
        using var kernel = new RoomKernel(options); using var socket = new BlockedSocket();
        var budget = new RoomSendBudget(options.MaximumQueuedBytes);
        var peer = new RoomWebSocketPeer(socket, options, budget);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = peer.RunAsync(kernel, timeout.Token);
        await socket.Sending.Task.WaitAsync(timeout.Token);
        peer.Send(new byte[1024]); peer.Send(new byte[1024]);
        await run.WaitAsync(timeout.Token);
        Assert.False(peer.Open); Assert.Equal(0, budget.QueuedBytes); Assert.Equal(0, kernel.ConnectionCount);
        var available = kernel.Connect(new Peer()); Assert.NotNull(available); kernel.Disconnect(available);
    }

    [Fact]
    public async Task ServerRejectsOversizeFragmentedMessagesAndReleasesConnectionOnShutdown()
    {
        await using var application = RoomServerApplication.Build(new(Port: 0, MaximumFrameBytes: 1024));
        await application.StartAsync();
        var server = application.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new ClientWebSocket(); await client.ConnectAsync(new(address.Replace("http:", "ws:")), timeout.Token);
        _ = await Read(client, timeout.Token);
        await client.SendAsync(new byte[800], WebSocketMessageType.Text, false, timeout.Token);
        await client.SendAsync(new byte[800], WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[1024]; var close = await client.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType); Assert.Equal(WebSocketCloseStatus.MessageTooBig, client.CloseStatus);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Received", timeout.Token);
        await application.StopAsync(timeout.Token);
        Assert.Equal(0, application.Services.GetRequiredService<RoomKernel>().ConnectionCount);
        Assert.Equal(0, application.Services.GetRequiredService<RoomSendBudget>().QueuedBytes);
    }

    [Fact]
    public void FullRequestBudgetStillAcceptsHostStopAndCannotAllocateAdditionalRooms()
    {
        using var kernel = new RoomKernel(new(MaximumRooms: 1, MaximumConnections: 2));
        var peer = new Peer(); var connection = kernel.Connect(peer)!;
        Send(kernel, connection, new { type = "hello", product = "MabiSync", protocolVersion = 1 });
        Send(kernel, connection, new { type = "create", nickname = "Host" });
        Send(kernel, connection, new { type = "set-ready", ready = true, capabilities = new { simulation = true, canStart = true, canStop = true } });
        Send(kernel, connection, new { type = "prepare", mode = "emote", countdownMs = 2000, participantIds = new[] { connection.Member!.Id } });
        Assert.Contains(peer.Messages, value => value.Text("type") == "prepare-action");
        for (var index = 0; index < 181; index++) Send(kernel, connection, new { type = "sync", id = index, t1 = 0 });
        Assert.Equal("RATE_LIMIT", peer.Messages.Last().GetProperty("code").GetString());
        var before = peer.Messages.Count;
        Send(kernel, connection, new { type = "stop-all", requestId = "stop-after-rate-limit" });
        Assert.DoesNotContain(peer.Messages.Skip(before), value => value.Text("code") == "RATE_LIMIT");
        Assert.Contains(peer.Messages.Skip(before), value => value.Text("type") == "state" && value.Field("room").Field("performance").Text("phase") == "cancelled");
        var other = new Peer(); var second = kernel.Connect(other)!;
        Send(kernel, second, new { type = "hello", product = "MabiSync", protocolVersion = 1 });
        Send(kernel, second, new { type = "create", nickname = "Other" });
        Assert.Equal("SERVER_BUSY", other.Messages.Last().Text("code"));
        Assert.Null(kernel.Connect(new Peer()));
        kernel.Dispose(); Assert.False(peer.Open); Assert.False(other.Open); Assert.Equal(0, kernel.ConnectionCount);
    }

    [Fact]
    public void RestartChangesEpochAndRejectsOldIdentityResume()
    {
        string oldEpoch, token, code;
        using (var old = new RoomKernel(new()))
        {
            var peer = new Peer(); var connection = old.Connect(peer)!;
            Send(old, connection, new { type = "hello", product = "MabiSync", protocolVersion = 1 });
            Send(old, connection, new { type = "create", nickname = "Host" });
            var session = peer.Messages.Single(value => value.Text("type") == "session");
            oldEpoch = old.Epoch; token = session.Text("token")!; code = session.Text("code")!;
        }
        using var current = new RoomKernel(new()); Assert.NotEqual(oldEpoch, current.Epoch);
        var nextPeer = new Peer(); var next = current.Connect(nextPeer)!;
        Send(current, next, new { type = "hello", product = "MabiSync", protocolVersion = 1 });
        Send(current, next, new { type = "resume", code, token });
        Assert.Equal("ROOM_NOT_FOUND", nextPeer.Messages.Last().Text("code"));
        Assert.DoesNotContain(nextPeer.Messages, value => value.Text("type") == "commit");
        Assert.DoesNotContain(token, current.Health().ToJsonString());
    }

    [Theory]
    [InlineData("stop-all")]
    [InlineData("cancel")]
    [InlineData("decide")]
    public void OldRoundCommandsCannotChangeANewRoundWhileLegacyStopRemainsCompatible(string command)
    {
        using var kernel = new RoomKernel(new()); var peer = new Peer(); var connection = kernel.Connect(peer)!;
        Send(kernel, connection, new { type = "hello", product = "MabiSync", protocolVersion = 1 });
        Send(kernel, connection, new { type = "create", nickname = "房主" });
        Send(kernel, connection, new { type = "set-ready", ready = true, capabilities = new { simulation = true, canStart = true, canStop = true } });
        Send(kernel, connection, new { type = "prepare", mode = "emote", countdownMs = 5000, participantIds = new[] { connection.Member!.Id } });
        var old = connection.Room!.Round!.Id;
        Send(kernel, connection, new { type = "cancel", sessionId = old });
        Send(kernel, connection, new { type = "prepare", mode = "emote", countdownMs = 5000, participantIds = new[] { connection.Member!.Id } });
        var current = connection.Room.Round!; Assert.NotEqual(old, current.Id);
        Send(kernel, connection, new { type = command, sessionId = old, decision = "continue" });
        Assert.Equal("STALE_SESSION", peer.Messages.Last().Text("code")); Assert.Equal("preparing", current.Phase);
        Send(kernel, connection, new { type = "stop-all" }); Assert.Equal("cancelled", current.Phase);
    }

    private static void Send(RoomKernel kernel, RoomConnection connection, object message) =>
        kernel.Receive(connection, JsonSerializer.SerializeToElement(message, RoomProtocol.Json));
    private static async Task<JsonElement> Read(WebSocket socket, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[4096]; ValueWebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer.AsMemory(), token); bytes.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(bytes.ToArray()); return document.RootElement.Clone();
    }
    private sealed class Peer : IRoomPeer
    {
        public bool Open { get; private set; } = true;
        internal List<JsonElement> Messages { get; } = [];
        public void Send(byte[] bytes) { using var document = JsonDocument.Parse(bytes); Messages.Add(document.RootElement.Clone()); }
        public void Close(int status, string reason) => Open = false;
    }
    private sealed class BlockedSocket : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;
        internal TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { state = WebSocketState.Closed; return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        { Sending.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
    }
}
