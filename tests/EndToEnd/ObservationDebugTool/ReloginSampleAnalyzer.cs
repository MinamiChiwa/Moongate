using System.Globalization;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.ObservationDebugTool;

/// <summary>离线分析所需的协议号：全部已知帧头、登录个人数据、身份关联与库存增量；Layout 存在时重放库存观察器。</summary>
public sealed record ReloginAnalysisProtocol(IReadOnlySet<int> Known, int Login, int Profile, IReadOnlySet<int> Inventory, ClientCaptureLayout? Layout)
{
    public static ReloginAnalysisProtocol From(ClientCaptureLayout? layout) => new(Tw20261110CombatProtocol.StreamProfile.KnownProtocols,
        (int)(layout?.Protocol.Login ?? 20590), (int)(layout?.Protocol.Profile ?? 170631),
        layout is null ? new HashSet<int>() : layout.Opcodes.Values.Select(value => (int)value).ToHashSet(), layout);
}

/// <summary>
/// 重新登录样本的离线分析：按连接重组 capture-combat-evidence.py --track-process-connections 写出的下行 TCP，
/// 输出各连接的端口与生命周期、已知协议号直方图、登录/身份关联/库存增量帧的时点与长度，以及库存观察器按连接重放的
/// 状态与失效原因。报告只含协议号、长度、时点、计数与布尔判定，不含载荷、身份值或文本内容。
/// </summary>
public static class ReloginSampleAnalyzer
{
    public const int MaximumPackets = 200_000;
    private static readonly ObservationSource ReplaySource = new("relogin-sample", "1.0.0", "TW", "offline");

    public static ReloginSampleAnalysis Analyze(string directory, ReloginAnalysisProtocol protocol, CancellationToken ct = default)
    {
        var connections = ReadConnections(Path.Combine(directory, "connections.json"));
        var packets = new Dictionary<string, List<SamplePacket>>(StringComparer.Ordinal);
        var path = Path.Combine(directory, "packets.jsonl");
        var total = 0;
        if (File.Exists(path))
            foreach (var line in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                if (++total > MaximumPackets) break;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = root.GetProperty("connectionId").GetString() ?? "unknown";
                if (!packets.TryGetValue(id, out var list)) packets[id] = list = [];
                list.Add(new(root.GetProperty("monotonicMs").GetDouble(), root.GetProperty("tcpSequence").GetUInt32(),
                    root.GetProperty("tcpFlags").GetByte(), Convert.FromBase64String(root.GetProperty("payloadBase64").GetString() ?? "")));
            }
        var events = new List<ReloginFrameEvent>();
        var observerStates = new List<ReloginObserverState>();
        var logins = new List<ReloginLoginDecode>();
        var profiles = new List<ReloginProfileDecode>();
        var decoder = protocol.Layout?.CreateDecoder();
        var pendingCharacters = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var summaries = new List<ReloginConnectionSummary>();
        var ids = connections.Select(value => value.ConnectionId).Concat(packets.Keys).Distinct(StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var id = ids[index]; var label = "c" + (index + 1).ToString(CultureInfo.InvariantCulture);
            var meta = connections.FirstOrDefault(value => value.ConnectionId == id);
            var rows = packets.GetValueOrDefault(id) ?? [];
            var histogram = new SortedDictionary<int, int>();
            var resets = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var decodeFailures = new SortedDictionary<int, int>();
            double now = 0;
            InventoryProtocolObserver? observer = null;
            ulong generation = 0;
            if (protocol.Layout is { } layout)
                observer = new("relogin-sample", ReplaySource, layout, frame => observerStates.Add(new(Math.Round(now, 1), label,
                    frame.Kind.KnownValue?.ToString() ?? "unknown", InventoryFrameTimeline.Reason(frame.Reason), frame.Items.Count, frame.Identity is not null)),
                    () => ++generation);
            GameTcpStream? stream = null;
            stream = new GameTcpStream((code, body) =>
            {
                histogram[code] = histogram.GetValueOrDefault(code) + 1;
                var at = Math.Round(stream!.FrameReceivedLocalMs ?? now, 1);
                var tracked = code == protocol.Login || code == protocol.Profile || protocol.Inventory.Contains(code);
                if (tracked && events.Count < 4096)
                    events.Add(new(at, label, code, code == protocol.Login ? "login" : code == protocol.Profile ? "profile" : "inventoryDelta",
                        stream.FrameWireLength, body.Length, stream.FrameEncoding == 1));
                if (decoder is not null && code == protocol.Login && logins.Count < 256)
                {
                    try
                    {
                        var login = decoder.DecodeLogin(body);
                        pendingCharacters[label] = login.CharacterId.Value;
                        logins.Add(new(at, label, body.Length, true, null, login.Items.Count, login.Coverage.Count(value => value.Complete)));
                    }
                    catch (InvalidDataException error) { logins.Add(new(at, label, body.Length, false, Code(error), null, null)); }
                }
                if (decoder is not null && code == protocol.Profile && profiles.Count < 256 &&
                    protocol.Layout!.Methods.TryGetValue((uint)code, out var method))
                {
                    try
                    {
                        var identity = decoder.DecodeIdentity(body, method);
                        var character = ulong.Parse(identity.CharacterId, CultureInfo.InvariantCulture);
                        profiles.Add(new(at, label, body.Length, true, null, pendingCharacters.GetValueOrDefault(label) == character,
                            pendingCharacters.Values.Contains(character)));
                    }
                    catch (InvalidDataException error) { profiles.Add(new(at, label, body.Length, false, Code(error), null, null)); }
                }
                if (observer is not null && (code == protocol.Login || code == protocol.Profile || protocol.Inventory.Contains(code)))
                    observer.Accept(code, body, at, DateTimeOffset.UnixEpoch.AddMilliseconds(at));
            }, reason => { var key = stream!.LastResetCause.ToString(); resets[key] = resets.GetValueOrDefault(key) + 1; },
                new GameStreamProfile(protocol.Known, protocol.Known), null,
                (code, _) => decodeFailures[code] = decodeFailures.GetValueOrDefault(code) + 1);
            uint? synAck = null; bool? fromStart = null; long bytes = 0;
            foreach (var packet in rows)
            {
                now = packet.MonotonicMs;
                if ((packet.Flags & 0x12) == 0x12) { synAck = packet.Sequence; stream.Restart("syn", GameStreamResetCause.ConnectionSyn); continue; }
                if (packet.Payload.Length > 0)
                {
                    fromStart ??= synAck is { } isn && packet.Sequence == unchecked(isn + 1);
                    bytes += packet.Payload.Length;
                    for (var offset = 0; offset < packet.Payload.Length; offset += 65536)
                        stream.Feed(unchecked(packet.Sequence + (uint)offset), packet.Payload.AsSpan(offset, Math.Min(65536, packet.Payload.Length - offset)), now, now);
                }
                stream.CheckGap(now);
                observer?.Tick(now, DateTimeOffset.UnixEpoch.AddMilliseconds(now));
            }
            var end = now + InventoryProtocolObserver.IdentityWindowMs + 1;
            observer?.Tick(end, DateTimeOffset.UnixEpoch.AddMilliseconds(end));
            summaries.Add(new(label, meta?.LocalPort, meta?.RemotePort, meta?.RemoteScope, meta?.FirstSeenTableMs, meta?.ClosedTableMs,
                meta?.SynAckMs, meta?.FinMs ?? meta?.RstMs, rows.Count, bytes, meta?.PrearmedPackets ?? 0, fromStart,
                histogram.Values.Sum(), histogram.GetValueOrDefault(protocol.Login), histogram.GetValueOrDefault(protocol.Profile),
                histogram.Where(pair => protocol.Inventory.Contains(pair.Key) && pair.Key != protocol.Login && pair.Key != protocol.Profile).Sum(pair => pair.Value),
                histogram.Count > 512 ? histogram.Take(512).ToDictionary() : histogram.ToDictionary(), resets.ToDictionary(StringComparer.Ordinal), decodeFailures.ToDictionary()));
        }
        var loginConnections = events.Where(value => value.Kind == "login").Select(value => value.Connection).ToHashSet(StringComparer.Ordinal);
        var profileConnections = events.Where(value => value.Kind == "profile").Select(value => value.Connection).ToHashSet(StringComparer.Ordinal);
        var findings = new ReloginFindings(loginConnections.Count > 0, profileConnections.Count > 0,
            profileConnections.Count == 0 ? null : profileConnections.Overlaps(loginConnections),
            summaries.Where(value => value.Logins > 0).Select(value => value.RemotePort).Distinct().OrderBy(value => value).ToArray(),
            summaries.Where(value => value.Logins > 0 && value.RemotePort != 1111).Select(value => value.Connection).ToArray(),
            summaries.Where(value => value.Logins > 0 && value.CapturedFromStart == false).Select(value => value.Connection).ToArray(),
            observerStates.Any(value => value.Kind == "Full"),
            observerStates.Where(value => value.Reason is not null).GroupBy(value => value.Reason!).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
        return new(total, summaries, events, logins, profiles, observerStates, findings);
    }

    private static string Code(Exception error) =>
        error.Message.StartsWith("inventory.", StringComparison.Ordinal) && error.Message.Length <= 96 && !error.Message.Contains(' ')
            ? error.Message.Split(':')[0] : "inventory.decodeFailed";

    private static IReadOnlyList<SampleConnection> ReadConnections(string path)
    {
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        static double? Number(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        return document.RootElement.GetProperty("connections").EnumerateArray().Take(256).Select(row => new SampleConnection(
            row.GetProperty("connectionId").GetString()!, row.GetProperty("localPort").GetInt32(), row.GetProperty("remotePort").GetInt32(),
            row.GetProperty("remoteScope").GetString(), Number(row, "firstSeenTableMs"), Number(row, "closedTableMs"), Number(row, "synAckMs"),
            Number(row, "finMs"), Number(row, "rstMs"), row.TryGetProperty("prearmedPackets", out var pre) ? pre.GetInt32() : 0)).ToArray();
    }

    private sealed record SamplePacket(double MonotonicMs, uint Sequence, byte Flags, byte[] Payload);
    private sealed record SampleConnection(string ConnectionId, int LocalPort, int RemotePort, string? RemoteScope, double? FirstSeenTableMs,
        double? ClosedTableMs, double? SynAckMs, double? FinMs, double? RstMs, int PrearmedPackets);
}

/// <param name="Connection">报告内连接标签（c1、c2…，按样本中首次出现顺序），不含地址。</param>
/// <param name="CapturedFromStart">首个载荷紧接 SYN-ACK 时为 true；未见 SYN-ACK 时为 false，表示连接建立后的首批数据可能未采到。</param>
public sealed record ReloginConnectionSummary(string Connection, int? LocalPort, int? RemotePort, string? RemoteScope, double? FirstSeenTableMs,
    double? ClosedTableMs, double? SynAckMs, double? FinOrResetMs, int Packets, long PayloadBytes, int PrearmedPackets, bool? CapturedFromStart,
    int Frames, int Logins, int Profiles, int InventoryDeltas, IReadOnlyDictionary<int, int> Protocols, IReadOnlyDictionary<string, int> StreamResets,
    IReadOnlyDictionary<int, int> DecodeFailures);
public sealed record ReloginFrameEvent(double AtMs, string Connection, int Code, string Kind, int WireBytes, int BodyBytes, bool Compressed);
public sealed record ReloginLoginDecode(double AtMs, string Connection, int BodyBytes, bool Decoded, string? Failure, int? Items, int? CompleteCoverage);
/// <param name="MatchesLoginOnConnection">身份关联的角色与同一连接上最近一次登录数据的角色一致；任一连接上的登录角色见 MatchesAnyLogin。</param>
public sealed record ReloginProfileDecode(double AtMs, string Connection, int BodyBytes, bool Decoded, string? Failure, bool? MatchesLoginOnConnection, bool? MatchesAnyLogin);
public sealed record ReloginObserverState(double AtMs, string Connection, string Kind, string? Reason, int Items, bool IdentityPresent);
/// <param name="ProfileOnLoginConnection">身份关联帧是否出现在登录数据所在的连接上；Worker 的库存观察器按连接独立，跨连接不会关联。</param>
public sealed record ReloginFindings(bool LoginObserved, bool ProfileObserved, bool? ProfileOnLoginConnection, IReadOnlyList<int?> LoginRemotePorts,
    IReadOnlyList<string> LoginOutsidePort1111, IReadOnlyList<string> LoginConnectionsMissingStart, bool ReplayFullSync,
    IReadOnlyDictionary<string, int> ReplayReasons);
public sealed record ReloginSampleAnalysis(int Packets, IReadOnlyList<ReloginConnectionSummary> Connections, IReadOnlyList<ReloginFrameEvent> Events,
    IReadOnlyList<ReloginLoginDecode> Logins, IReadOnlyList<ReloginProfileDecode> Profiles, IReadOnlyList<ReloginObserverState> ObserverStates,
    ReloginFindings Findings);
