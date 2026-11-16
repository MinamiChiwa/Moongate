namespace MabiTool.Contracts;

public sealed record RoomCreateRequest(string Endpoint, string Nickname);
public sealed record RoomJoinRequest(string Endpoint, string Code, string Nickname);
public sealed record RoomLeaveRequest(bool CloseRoom = false);
public sealed record RoomRosterEditRequest(string MemberId, int? Group = null, int? Order = null,
    string? MusicRole = null, string? Instrument = null, bool? Selected = null);
public sealed record RoomModeRequest(string Mode);
public sealed record RoomReadinessRequest(bool Ready, bool Simulation, bool CanStart, bool CanStop,
    int TimingOffsetMs = 0, string MusicRole = "initiator", string? ReadyMode = null,
    double ClockRoundTripMs = 0, int WindowActivationMs = 0, bool ChatDraftConfirmed = false,
    UInt64String? ExpectedBindingRevision = null, UInt64String? ExpectedPreferencesRevision = null);
public sealed record RoomCharacterBindingRequest(string MemberId, string? CharacterId, string? RealmId);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomLeaderRequest(string MemberId);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomMemberKickRequest(string MemberId);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomGameOperationRequest(bool Enabled);
/// <summary>本机停止结果：localAction 为 stopScheduled、stopRejected、cancelled 或 none；serverConfirmed 表示房间服务已确认 local-stop。</summary>
public sealed record RoomLocalStopResult(string LocalAction, string? StopActionId, bool ServerConfirmed, RoomLifecycleSnapshot Session);
public sealed record RoomInvitation(string Code, string Endpoint, DateTimeOffset CreatedAt);
public sealed record RoomLifecycleSnapshot(string State, string? Endpoint, string? Code, string? MemberId,
    bool IsOwner, string CloseStatus, int UnconfirmedRoomExits, System.Text.Json.JsonElement? Room,
    RoomLocalPreparation? LocalPreparation = null, bool OperateGame = true, string? ExitReason = null);
public sealed record RoomLocalPreparation(string? Id, string State, string? GameSessionId, UInt64String? BindingRevision,
    UInt64String? PreferencesRevision, string? Mode, int TimingOffsetMs, bool Simulation, bool CanStart,
    bool CanStop, string? ActiveSessionId, OpenEnum<EffectStatus> EffectStatus, string? Reason, UInt64String Revision);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomRoundRequest(string Mode, IReadOnlyList<string> ParticipantIds, int CountdownMs = 5000,
    OpenEnum<MusicStopRule>? StopRule = null, int? StopAfterMs = null, int? TailHoldMs = null);
public sealed record RoomReadiness(bool Ready, IReadOnlyList<string> Reasons);
public sealed record RoomMember(string Id, string DisplayName, CharacterIdentity? Character, string Role,
    string? GroupId, int? Order, string? InstrumentId, bool UsesScore, RoomReadiness EnsembleReadiness,
    RoomReadiness EmoteReadiness, UInt64String ConnectionGeneration);
public sealed record RoomClockSample(double RoundTripMs, double UncertaintyMs, double SampleAgeMs, double PersonalOffsetMs);
public sealed record RoomSnapshot(string Id, string OwnerMemberId, string ServerEpoch, UInt64String Revision,
    IReadOnlyList<RoomMember> Members, string? CurrentRoundId);
