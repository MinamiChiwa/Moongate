using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.RoomServer.Protocol;

namespace MabiTool.RoomServer.Domain;

// 来源：MabiSync 2cb69b5，server/room-server.js；v1 兼容状态机使用有界传输及单调时钟。
internal sealed partial class RoomKernel : IDisposable
{
    private readonly object gate = new();
    private readonly RoomServerOptions options;
    private readonly TimeProvider time;
    private readonly long origin;
    private readonly Dictionary<string, RoomState> rooms = new(StringComparer.Ordinal);
    private readonly HashSet<RoomConnection> connections = [];
    private readonly Dictionary<string, RoomClosure> closures = new(StringComparer.Ordinal);
    private readonly ITimer expiry, idle;
    private bool closing;
    internal string Epoch { get; } = Guid.NewGuid().ToString();
    internal double Now => time.GetElapsedTime(origin).TotalMilliseconds;
    internal int ConnectionCount { get { lock (gate) return connections.Count; } }

    internal RoomKernel(RoomServerOptions options, TimeProvider? time = null)
    {
        options.Validate(); this.options = options; this.time = time ?? TimeProvider.System; origin = this.time.GetTimestamp();
        expiry = this.time.CreateTimer(_ => { lock (gate) { if (!closing) Expire(); } }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        idle = this.time.CreateTimer(_ => { lock (gate) { if (!closing) ExpireIdle(); } }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    internal JsonObject Identity(string type) => new()
    {
        ["type"] = type, ["product"] = "MabiSync", ["protocolVersion"] = 1, ["serverEpoch"] = Epoch,
        ["features"] = new JsonObject { ["gameObservation"] = 1, ["ensembleRoster"] = 1, ["firstCycleStop"] = 1,
            ["firstCycleTail"] = 1, ["rosterEditing"] = 1, ["roomClosure"] = 1, ["adaptiveTiming"] = 2, ["roundScoreDraft"] = 1 }
    };
    internal JsonObject Health()
    {
        var result = Identity("health"); result.Remove("type"); result["ok"] = true; result["automaticGameEndVerified"] = false; return result;
    }

    internal RoomConnection? Connect(IRoomPeer peer)
    {
        lock (gate)
        {
            if (closing || connections.Count >= options.MaximumConnections) return null;
            var connection = new RoomConnection(peer) { RateStart = Now }; connections.Add(connection);
            Send(connection, Identity("hello")); return connection;
        }
    }
    internal void Disconnect(RoomConnection connection)
    {
        lock (gate) { connections.Remove(connection); Detach(connection); }
    }
    internal void Receive(RoomConnection connection, JsonElement message, double? receivedAt = null)
    {
        lock (gate)
        {
            if (closing || !connections.Contains(connection)) return;
            try
            {
                if (Now - connection.RateStart > 10000) { connection.RateStart = Now; connection.RateCount = 0; }
                if (++connection.RateCount > 180 && message.Text("type") is not ("stop-all" or "local-stop")) throw new RoomProtocolException("RATE_LIMIT");
                Handle(connection, message, receivedAt ?? Now);
            }
            catch (RoomProtocolException error) { Error(connection, message, error.Code, error.Details); }
        }
    }
    internal void InvalidMessage(RoomConnection connection) { lock (gate) Error(connection, default, "BAD_MESSAGE"); }
    private static bool MusicMode(string? mode) => mode is "independent" or "ensemble";
    private static bool Mode(string? mode) => mode == "emote" || MusicMode(mode);
    private void Send(RoomConnection? connection, object message)
    {
        if (connection?.Peer.Open == true) connection.Peer.Send(JsonSerializer.SerializeToUtf8Bytes(message, RoomProtocol.Json));
    }
    private void Error(RoomConnection connection, JsonElement message, string code, JsonObject? details = null)
    {
        var response = new JsonObject { ["type"] = "error", ["code"] = code, ["message"] = code };
        if (message.Text("requestId") is { Length: <= 64 } request) response["requestId"] = request;
        if (details is not null) foreach (var field in details) response[field.Key] = field.Value?.DeepClone();
        Send(connection, response);
    }
    private void Broadcast(RoomState room, object? message = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message ?? new { type = "state", room = View(room), serverEpoch = Epoch, serverNowMs = Now }, RoomProtocol.Json);
        foreach (var member in room.Members.Values) if (member.Connection?.Peer.Open == true) member.Connection.Peer.Send(bytes);
    }
    private void Participants(RoomState room, RoomRoundState round, object message, IEnumerable<string>? ids = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, RoomProtocol.Json);
        foreach (var id in ids ?? round.ParticipantIds) if (room.Members.GetValueOrDefault(id)?.Connection is { } connection && connection.Peer.Open) connection.Peer.Send(bytes);
    }
    private ITimer Later(Action action, double delayMs) => time.CreateTimer(_ => { lock (gate) { if (!closing) action(); } }, null,
        TimeSpan.FromMilliseconds(Math.Max(0, delayMs)), Timeout.InfiniteTimeSpan);

    private void Handle(RoomConnection connection, JsonElement message, double receivedAt)
    {
        var type = message.Text("type") ?? throw new RoomProtocolException("BAD_MESSAGE");
        if (type == "hello")
        {
            if (message.Text("product") != "MabiSync" || message.Integer("protocolVersion") != 1) throw new RoomProtocolException("INCOMPATIBLE_CLIENT");
            connection.SupportsScoreDraft = message.Field("features").Integer("roundScoreDraft") == 1;
            connection.Accepted = true; Send(connection, Identity("hello-accepted")); return;
        }
        if (!connection.Accepted) throw new RoomProtocolException("INCOMPATIBLE_CLIENT");
        if (type == "room-closed-ack")
        {
            if (message.Text("closureId") is { } id && closures.TryGetValue(id, out var closure) && closure.Expected.Contains(connection))
            {
                closure.Confirmed.Add(connection); if (closure.Confirmed.Count == closure.Expected.Count) FinishClosure(closure);
            }
            return;
        }
        if (type == "sync")
        {
            var id = message.Number("id"); var t1 = message.Number("t1");
            if (!RoomProtocol.SafeInteger(id) || t1 is null) throw new RoomProtocolException("BAD_MESSAGE");
            Send(connection, new { type = "sync", id, t1, t2 = receivedAt, t3 = Now, serverEpoch = Epoch }); return;
        }
        if (type is "create" or "join" or "resume") { EnterRoom(connection, message, type); return; }
        var room = connection.Room; var member = connection.Member;
        if (room is null || member is null || member.Connection != connection) throw new RoomProtocolException("NOT_IN_ROOM");
        var hostOnly = type is "select-leader" or "prepare" or "cancel" or "stop-all" or "decide" or "close-room" or "score-timeline" or
            "move-ensemble-member" or "set-ensemble-role" or "edit-ensemble-member" or "set-member-selection" or "set-room-mode" or "kick-member" or "bind-game-character" or
            "publish-score-draft" or "clear-score-draft";
        if (hostOnly && room.HostId != member.Id) throw new RoomProtocolException("FORBIDDEN");
        if (type is "cancel" or "stop-all" or "decide" && message.Has("sessionId") &&
            (message.Text("sessionId") is not { } sessionId || sessionId != room.Round?.Id))
            throw new RoomProtocolException("STALE_SESSION");
        switch (type)
        {
            case "publish-score-draft": PublishScoreDraft(room, member, message); break;
            case "clear-score-draft": ClearScoreDraft(room, message); break;
            case "map-score-draft": MapScoreDraft(room, member, message); break;
            case "set-room-mode":
                RequireIdle(room); if (message.Text("mode") is not ("emote" or "ensemble")) throw new RoomProtocolException("BAD_MESSAGE");
                if (room.Mode != message.Text("mode")) { room.Mode = message.Text("mode")!; foreach (var other in room.Members.Values) other.Ready = false; }
                break;
            case "game-observation": RoomObservationRules.Accept(member, message, Now); ObserveCycle(room, member); break;
            case "bind-game-character": RoomObservationRules.Bind(room, message.Text("memberId"), message.Field("characterId"), message.Field("realmId"), Now); break;
            case "select-leader": RequireIdle(room); room.LeaderId = FindMember(room, message.Text("memberId")).Id; break;
            case "set-ready":
                var ready = message.Boolean("ready") ?? throw new RoomProtocolException("BAD_CAPABILITIES");
                var capability = Capabilities(message.Field("capabilities"));
                if (ready && (capability.ReadyMode ?? room.Mode) != "emote" && !RoomScoreDraftRules.Ready(room, member)) throw new RoomProtocolException("SCORE_MAPPING_REQUIRED");
                member.Capabilities = capability; member.Ready = ready;
                if (!ready) Interrupt(room, member, "not-ready"); break;
            case "configure-input": RequireIdle(room); member.Capabilities = Capabilities(message.Field("capabilities")); member.Ready = false; break;
            case "move-ensemble-member": case "set-ensemble-role": EditEnsemble(room, message); break;
            case "edit-ensemble-member": case "set-member-selection": EditMember(room, message); break;
            case "kick-member":
                if (message.Text("memberId") == room.HostId) throw new RoomProtocolException("CANNOT_KICK_HOST");
                var kicked = FindMember(room, message.Text("memberId")); Interrupt(room, kicked, "member-kicked");
                if (kicked.Connection is { } target) { Send(target, new { type = "kicked", serverEpoch = Epoch }); target.Room = null; target.Member = null; }
                room.Members.Remove(kicked.Id); if (room.LeaderId == kicked.Id) room.LeaderId = room.HostId; break;
            case "prepare": Prepare(room, message); break;
            case "action-ready": Acknowledge(room, member, message); break;
            case "session-ready": AcknowledgeSession(room, member, message); break;
            case "receipt": Receipt(room, member, message); break;
            case "cancel":
                if (room.Round is { Terminal: false } round && Now >= round.LatestStartServerMs) throw new RoomProtocolException("USE_STOP_ALL");
                Cancel(room, new JsonObject { ["source"] = "host-cancel" }); break;
            case "stop-all": StopAll(room); break;
            case "local-stop": member.Ready = false; Interrupt(room, member, "local-stop"); break;
            case "leader-status": LeaderStatus(room, member, message); break;
            case "decide": Decide(room, message); break;
            case "score-timeline": throw new RoomProtocolException("AUTOMATIC_END_UNVERIFIED");
            case "leave": Detach(connection, remove: true); Send(connection, new { type = "left", serverEpoch = Epoch }); return;
            case "close-room": CloseRoom(room); return;
            default: throw new RoomProtocolException("BAD_MESSAGE");
        }
        room.Touched = Now; Broadcast(room);
        if (message.Text("requestId") is { Length: <= 64 } request) Send(connection, new { type = "request-complete", requestId = request, serverEpoch = Epoch });
    }

    private static RoomMemberState FindMember(RoomState room, string? id) => id is not null && room.Members.TryGetValue(id, out var member) ? member : throw new RoomProtocolException("MEMBER_NOT_FOUND");
    private static void RequireIdle(RoomState room) { if (room.Round is { Terminal: false }) throw new RoomProtocolException("SESSION_ACTIVE"); }
    private static string Nickname(string? name)
    {
        name = name?.Trim(); if (string.IsNullOrEmpty(name) || name.EnumerateRunes().Count() > 20) throw new RoomProtocolException("BAD_NICKNAME"); return name;
    }
    private static RoomMemberState NewMember(string? name, string role) => new(Guid.NewGuid().ToString(), Nickname(name), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)), role);
    private void EnterRoom(RoomConnection connection, JsonElement message, string type)
    {
        if (connection.Room is not null) throw new RoomProtocolException("ALREADY_IN_ROOM");
        if (type == "create")
        {
            if (rooms.Count >= options.MaximumRooms) throw new RoomProtocolException("SERVER_BUSY");
            var member = NewMember(message.Text("nickname"), "host"); const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            string code; do { code = new string(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()); } while (rooms.ContainsKey(code));
            var room = new RoomState(code, member, Now); rooms.Add(code, room); Attach(connection, room, member); return;
        }
        var requested = message.Text("code")?.Trim().ToUpperInvariant() ?? "";
        if (!rooms.TryGetValue(requested, out var existing)) throw new RoomProtocolException("ROOM_NOT_FOUND");
        if (type == "resume")
        {
            var token = message.Text("token");
            var member = RoomProtocol.Hex(token, 48) ? existing.Members.Values.FirstOrDefault(member => CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(token!), System.Text.Encoding.ASCII.GetBytes(member.Token))) : null;
            if (member is null) throw new RoomProtocolException("SESSION_EXPIRED"); Attach(connection, existing, member);
        }
        else
        {
            if (existing.Members.Count >= 24) throw new RoomProtocolException("ROOM_FULL");
            var member = NewMember(message.Text("nickname"), "member"); existing.Members.Add(member.Id, member); Attach(connection, existing, member);
        }
    }
    private void Attach(RoomConnection connection, RoomState room, RoomMemberState member)
    {
        var old = member.Connection; member.Connection = connection; member.Ready = false; member.ScoreMapping = null; RoomObservationRules.Clear(member);
        connection.Room = room; connection.Member = member;
        if (old is not null && old != connection) { Interrupt(room, member, "tool-session-replaced"); old.Peer.Close(4001, "Session resumed elsewhere"); }
        room.Touched = Now; Send(connection, new { type = "session", code = room.Code, memberId = member.Id, token = member.Token, serverEpoch = Epoch }); Broadcast(room);
    }
    private void Detach(RoomConnection connection, bool remove = false)
    {
        var room = connection.Room; var member = connection.Member;
        if (closing || room is null || member is null || member.Connection != connection) return;
        if (remove && room.HostId == member.Id) { CloseRoom(room); return; }
        member.Connection = null; member.Ready = false; member.ScoreMapping = null; RoomObservationRules.Clear(member);
        Interrupt(room, member, remove ? "member-left" : "tool-connection");
        if (remove) { room.Members.Remove(member.Id); if (room.LeaderId == member.Id) room.LeaderId = room.HostId; }
        connection.Room = null; connection.Member = null; room.Touched = Now; Broadcast(room);
    }
    private sealed class RoomClosure(string id, RoomConnection? host, IEnumerable<RoomConnection> expected, int total)
    {
        internal string Id { get; } = id; internal RoomConnection? Host { get; } = host;
        internal HashSet<RoomConnection> Expected { get; } = expected.ToHashSet(); internal HashSet<RoomConnection> Confirmed { get; } = [];
        internal int Total { get; } = total; internal ITimer? Timer { get; set; }
    }
    private void CloseRoom(RoomState room)
    {
        if (room.Round is { Terminal: false } round && (round.Phase == "preparing" || Now < round.EarliestStartServerMs)) Cancel(room, new JsonObject { ["source"] = "host-cancel" });
        else StopAll(room);
        var closure = new RoomClosure(Guid.NewGuid().ToString(), room.Members[room.HostId].Connection,
            room.Members.Values.Where(member => member.Connected).Select(member => member.Connection!), room.Members.Count);
        closures.Add(closure.Id, closure);
        foreach (var member in room.Members.Values)
            if (member.Connection is { } connection) { Send(connection, new { type = "room-closed", closureId = closure.Id, serverEpoch = Epoch }); connection.Room = null; connection.Member = null; }
        rooms.Remove(room.Code); room.Round?.ClearTimers();
        closure.Timer = Later(() => FinishClosure(closure), 2000); if (closure.Expected.Count == 0) FinishClosure(closure);
    }
    private void FinishClosure(RoomClosure closure)
    {
        if (!closures.Remove(closure.Id)) return; closure.Timer?.Dispose();
        Send(closure.Host, new { type = "room-close-complete", closureId = closure.Id, unconfirmedMembers = closure.Total - closure.Confirmed.Count, serverEpoch = Epoch });
    }
    private void Expire()
    {
        foreach (var room in rooms.Values)
        {
            if (!RoomObservationRules.Expire(room, Now)) continue;
            if (room.Round?.LeaderId is { } id && room.Members.TryGetValue(id, out var leader)) ObserveCycle(room, leader);
            Broadcast(room);
        }
    }
    private void ExpireIdle() { foreach (var room in rooms.Values.ToArray()) if (!room.Members.Values.Any(member => member.Connected) && Now - room.Touched > 1_800_000) CloseRoom(room); }
    public void Dispose()
    {
        lock (gate)
        {
            if (closing) return; closing = true; expiry.Dispose(); idle.Dispose();
            foreach (var room in rooms.Values) room.Round?.ClearTimers();
            foreach (var closure in closures.Values) closure.Timer?.Dispose();
            foreach (var connection in connections) connection.Peer.Close(1001, "Server stopping");
            rooms.Clear(); closures.Clear(); connections.Clear();
        }
    }
}
