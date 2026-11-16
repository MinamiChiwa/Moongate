using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.RoomServer.Protocol;

namespace MabiTool.RoomServer.Domain;

internal interface IRoomPeer
{
    bool Open { get; }
    void Send(byte[] bytes);
    void Close(int status, string reason);
}

internal sealed class RoomConnection(IRoomPeer peer)
{
    internal IRoomPeer Peer { get; } = peer;
    internal bool Accepted { get; set; }
    internal bool SupportsScoreDraft { get; set; }
    internal double RateStart { get; set; }
    internal int RateCount { get; set; }
    internal RoomState? Room { get; set; }
    internal RoomMemberState? Member { get; set; }
}

internal sealed class RoomState(string code, RoomMemberState host, double touched)
{
    internal string Code { get; } = code;
    internal string HostId { get; } = host.Id;
    internal string LeaderId { get; set; } = host.Id;
    internal string Mode { get; set; } = "emote";
    internal Dictionary<string, RoomMemberState> Members { get; } = new(StringComparer.Ordinal) { [host.Id] = host };
    internal RoomRoundState? Round { get; set; }
    internal RoomScoreDraft? ScoreDraft { get; set; }
    internal ulong ScoreDraftRevision { get; set; }
    internal double Touched { get; set; } = touched;
    internal Dictionary<string, RetiredRoomAction> RetiredActions { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, HashSet<string>> RetiredSessionAcks { get; } = new(StringComparer.Ordinal);
}

internal sealed record RoomCapabilities(bool Simulation, bool CanStart, bool CanStop, int TimingOffsetMs = 0,
    string MusicRole = "initiator", string? ReadyMode = null, double ClockRoundTripMs = 0, int WindowActivationMs = 0);

internal sealed class RoomMemberState(string id, string nickname, string token, string role)
{
    internal string Id { get; } = id;
    internal string Nickname { get; set; } = nickname;
    internal string Token { get; } = token;
    internal string Role { get; } = role;
    internal RoomConnection? Connection { get; set; }
    internal bool Connected => Connection?.Peer.Open == true;
    internal bool Ready { get; set; }
    internal RoomCapabilities Capabilities { get; set; } = new(true, false, false);
    internal int Group { get; set; } = 1;
    internal string MusicRole { get; set; } = role == "host" ? "initiator" : "joiner";
    internal int Position { get; set; } = 1000;
    internal bool Selected { get; set; } = true;
    internal string Instrument { get; set; } = "";
    internal RoomScoreMemberMapping? ScoreMapping { get; set; }
    internal RoomObservation? Observation { get; set; }
    internal string? ObservationGeneration { get; set; }
    internal double ObservationSequence { get; set; }
    internal HashSet<string> RetiredObservationGenerations { get; } = new(StringComparer.Ordinal);
    internal RoomCharacterBinding? CharacterBinding { get; set; }
}

internal sealed record RoomCharacter(string CharacterId, int RealmId, string Nickname);
internal sealed record RoomCharacterBinding(string CharacterId, int RealmId);
internal sealed record RoomConcert(string Id, string HostCharacterId, string[] OrderedCharacterIds, RoomCharacter[]? Members);
internal sealed record RoomMusic(string ConcertId, string PerformerCharacterId, string HostCharacterId, string Title,
    bool IsLoop, int[] SelectedChannels, string? ScoreSha256, double? WholeScoreDurationSeconds, double? SelectedDurationSeconds,
    string? Issue, double? EstimatedStartServerMs, string? TimingSource, bool AutomaticStopAvailable, double? GameStartLocalMs = null);
internal sealed record RoomObservation(string Generation, double Sequence, double At, RoomCharacter Identity,
    RoomConcert? Concert, RoomMusic? Music, string? SessionId);
internal sealed record RoomRosterRow(string MemberId, int Group, string Role, int Order);
internal sealed record RoomAction(string Id, string SessionId, string Kind, double TargetServerMs, string ServerEpoch)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ScoreDraftId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ScoreDraftRevision { get; init; }
}
internal sealed record RetiredRoomAction(HashSet<string> Members, bool Committed);
internal sealed record RoomCycleBinding(string Generation, int RealmId, string CharacterId, string ConcertId,
    string? ScoreSha256, string Title, double DurationSeconds, double EstimatedEndServerMs);

internal sealed class RoomRoundState
{
    internal required string Id { get; init; }
    internal required string Mode { get; init; }
    internal string Phase { get; set; } = "preparing";
    internal string? LeaderId { get; init; }
    internal required int CountdownMs { get; init; }
    internal required int RequestedCountdownMs { get; init; }
    internal required string[] ParticipantIds { get; init; }
    internal required string[] StartParticipantIds { get; init; }
    internal required string[] ManualJoinerIds { get; init; }
    internal required RoomAction Start { get; init; }
    internal RoomAction? Stop { get; set; }
    internal required double EarliestStartServerMs { get; init; }
    internal required double LatestStartServerMs { get; init; }
    internal required int DelayMs { get; init; }
    internal required int MinimumOffsetMs { get; init; }
    internal required int LeaderOffsetMs { get; init; }
    internal required string StopRule { get; init; }
    internal required int TailHoldMs { get; init; }
    internal string? StopTimingSource { get; set; }
    internal string? CycleStopStatus { get; set; }
    internal string? ScoreDraftId { get; init; }
    internal UInt64String? ScoreDraftRevision { get; init; }
    internal RoomCycleBinding? CycleBinding { get; set; }
    internal bool CycleInterruptionReported { get; set; }
    internal required HashSet<string> MusicBaseline { get; init; }
    internal required HashSet<string> ObservationBaseline { get; init; }
    internal required RoomRosterRow[] Roster { get; init; }
    internal HashSet<string> SessionAcks { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, HashSet<string>> Acks { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, JsonObject> Receipts { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Committed { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> CancelledActions { get; } = new(StringComparer.Ordinal);
    internal List<ITimer> Timers { get; } = [];
    internal ITimer? CycleTimer { get; set; }
    internal JsonObject? Interruption { get; set; }
    internal bool Terminal => Phase is "cancelled" or "stopped";
    internal IEnumerable<RoomAction> Actions => Stop is null ? [Start] : [Start, Stop];
    internal string[] ActionMembers(RoomAction action) => action.Kind == "start" ? StartParticipantIds : ParticipantIds;
    internal bool Prepared(string memberId) => (!ManualJoinerIds.Contains(memberId) || SessionAcks.Contains(memberId)) &&
        Actions.Where(action => ActionMembers(action).Contains(memberId)).All(action => Acks[action.Id].Contains(memberId));
    internal void ClearTimers() { foreach (var timer in Timers) timer.Dispose(); Timers.Clear(); CycleTimer = null; }
}
