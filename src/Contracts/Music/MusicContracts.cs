namespace MabiTool.Contracts;

public sealed record MusicCatalogChanged(string ScopeKey, UInt64String Revision, string Status, int EntryCount);
public sealed record ScoreSourceRef(string Provider, string Region, string CharacterScope, string? SourceId,
    string? Occurrence, string LocatorCapability, bool Ambiguous);
public sealed record ScoreEntry(string Id, string RawTitle, string DisplayTitle, string? ArtistId,
    IReadOnlyList<string> Tags, IReadOnlyList<ScoreSourceRef> SourceRefs, string? DuplicateGroup,
    string Availability, UInt64String Revision);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicScoreSelection(string ScopeKey, string ScoreId, UInt64String SourceRevision, string? PartId = null);
public sealed record MusicResolvedScore(ScoreEntry Entry, string? ContentSha256, ScoreTimeline Timeline, string Fingerprint,
    string? UnavailableReason = null);
public sealed record ScoreTimeline(string Id, double? FullCycleMs, double? SelectedPartCycleMs, bool Loop,
    double TailMs, string CalculatorVersion, string SourceHash, ObservationQuality Quality,
    TimelineEvidenceKind EvidenceKind = TimelineEvidenceKind.Unknown, string? PartId = null,
    DateTimeOffset? ObservedAt = null);
public enum TimelineEvidenceKind { Unknown, ImportedHistoryEstimate, ImportedMmlEstimate, ManualEstimate, PlaybackObservation }
public sealed record ScoreTimelineEvidence(string ScoreId, string? PartId, double? FullCycleMs,
    double? PartCycleMs, double TailMs, TimelineEvidenceKind EvidenceKind, ObservationQuality Quality,
    string SourceId, string CalculatorVersion, string SourceHash, DateTimeOffset RecordedAt);
public sealed record MusicTimelineQuery(string ScoreId, string? PartId);
public sealed record PerformanceStopDecision(bool CanStop, double? ThresholdMs, string ReasonCode);
public sealed record MusicPlaybackHistoryInput(string Id, string ScoreId, string? PartId,
    string? InstrumentId, DateTimeOffset StartedAt, double? DurationMs);
public sealed record MusicPlaybackHistoryItem(string Id, string ScoreId, string? PartId,
    string? InstrumentId, DateTimeOffset StartedAt, double? DurationMs,
    TimelineEvidenceKind EvidenceKind, ObservationQuality Quality, string SourceId);
public sealed record MusicPlaybackHistoryChanged(string Owner, UInt64String Revision,
    MusicPlaybackHistoryItem Record);
public sealed record InstrumentDefinition(string Id, IReadOnlyDictionary<string, string> DisplayNames, IReadOnlyList<string> Aliases);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PlaylistItem(string Id, string ScoreId, string? InstrumentId, string SortKey);
public sealed record Playlist(string Id, string? FolderId, string Name, string Notes, UInt64String Revision,
    IReadOnlyList<PlaylistItem> Items);
public sealed record PerformanceSnapshot(string Id, string Owner, string Mode, string GameSessionId,
    IReadOnlyList<PlaylistItem> Queue, string Phase, string StopPolicy, UInt64String ControlGeneration,
    Observation<ScoreTimeline>? CurrentObservation);

public enum PerformanceRepeatMode { Off, All, One }
public enum PerformancePhase { Idle, Preparing, Armed, Executing, Performing, Waiting, Stopping, Stopped, Failed, Cancelled, Interrupted }
public enum PerformanceStopReason { NaturalEnd, ManualStop, GameStopped, ControlFailure, ObservationLost, Cancelled, QueueExhausted }
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PerformanceQueueItem(PlaylistItem Item, int SourceIndex);
public sealed record PerformanceQueueState(string Id, string Owner, string Mode, string GameSessionId,
    IReadOnlyList<PerformanceQueueItem> Items, IReadOnlyList<int> Order, int CurrentPosition,
    bool Shuffle, PerformanceRepeatMode Repeat, double GapSeconds, double AdvanceMargin,
    PerformancePhase Phase, int ConsecutiveFailures, UInt64String Generation,
    string? CurrentScoreId, DateTimeOffset? CurrentStartedAt, DateTimeOffset? GapUntil,
    PerformanceStopReason? StopReason, string? LastError = null);
public sealed record PerformanceSessionCreateRequest(string? Id, string Mode, string GameSessionId,
    IReadOnlyList<PlaylistItem> Items, bool Shuffle, PerformanceRepeatMode Repeat,
    double GapSeconds = 2, double AdvanceMargin = 2);
public sealed record PerformanceSessionCommand(string Action, int Direction = 0,
    PerformanceStopReason? StopReason = null, DateTimeOffset? At = null,
    IReadOnlyList<PlaylistItem>? Items = null, string? Detail = null);
