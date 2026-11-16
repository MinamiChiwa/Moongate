using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomScoreAssignment(string MemberId, string? PartId, string? InstrumentId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomScoreDraftRequest(MusicScoreSelection Score, IReadOnlyList<RoomScoreAssignment> Assignments);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomScoreMappingRequest(MusicScoreSelection? Score, string? InstrumentId);
public sealed record RoomScoreDraft(int SchemaVersion, string Id, UInt64String Revision, MusicScoreSelection Source,
    MusicResolvedScore Score, IReadOnlyList<RoomScoreAssignment> Assignments);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomScoreMemberMapping(string MemberId, string State, string? Reason, UInt64String? DraftRevision,
    string? ContentSha256, string? PartId, string? InstrumentId);
public sealed record RoomLocalScoreMapping(MusicScoreSelection? Selection, string? InstrumentId,
    MusicResolvedScore? Score, string State, string? Reason);
public sealed record RoomScoreDraftSnapshot(string BackendEpoch, string? ServerEpoch, string? RoomCode,
    string? MemberId, bool Supported, UInt64String Revision, RoomScoreDraft? Draft,
    IReadOnlyList<RoomScoreMemberMapping> Members, RoomLocalScoreMapping? Local);
public sealed record RoomScoreDraftCommandResult(string Command, bool ServerConfirmed, RoomScoreDraftSnapshot Snapshot);
public sealed record RoomScoreDraftChanged(string Owner, RoomScoreDraftSnapshot Snapshot);
