using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public enum RoomRoundControl { StopAll, Cancel, Continue, Interrupt }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomRoundControlRequest(OpenEnum<RoomRoundControl> Action, string? Reason = null);

public sealed record RoomRoundAction(string Id, string SessionId, string Kind, double TargetServerMs, string ServerEpoch);
public sealed record RoomRoundReceipt(string ActionId, string MemberId, string Status, double? ActualServerMs,
    bool Simulated, bool GameResponseVerified, string? Detail);
/// <summary>取样时点的本机时间基准。剩余毫秒以响应到达时刻为零点，负值表示目标时刻已过去的毫秒数。</summary>
public sealed record RoomRoundTiming(double ServerNowMs, double RoundTripMs, double UncertaintyMs, double SampleAgeMs,
    int TimingOffsetMs, double? StartRemainingMs, double? StopRemainingMs);
public sealed record RoomRoundSnapshot(string BackendEpoch, string? ServerEpoch, string? RoomCode, string? MemberId,
    UInt64String Revision, string? Id, string Phase, string? Mode, string? LeaderId,
    IReadOnlyList<string> ParticipantIds, OpenEnum<MusicStopRule>? StopRule, int TailHoldMs,
    RoomRoundAction? StartAction, RoomRoundAction? StopAction, bool StopCommitted, string? CycleStopStatus,
    string? StopTimingSource, string? StopTimingUnavailable, JsonElement? Interruption,
    IReadOnlyList<RoomRoundReceipt> Receipts, RoomLocalPreparation? LocalPreparation,
    string? ScoreDraftId = null, UInt64String? ScoreDraftRevision = null, RoomRoundTiming? Timing = null);
public sealed record RoomRoundCommandResult(string Command, bool ServerConfirmed, RoomRoundSnapshot Round);
