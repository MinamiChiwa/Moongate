using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportSourceFile(string Name, string ContentBase64);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicImportRequest(string SourceId, string SourceLabel, string Region, string RealmId,
    UInt64String CharacterId, IReadOnlyList<ImportSourceFile> Files,
    IReadOnlyDictionary<string, string>? ArtistMappings = null);
public sealed record ImportFileSnapshot(string Name, string Sha256, UInt64String SizeBytes);
public sealed record ImportIdMapping(string Kind, string OldId, string? NewId, string? RawTitle,
    string Association, IReadOnlyList<string> CandidateIds);
public sealed record ImportIssue(string Code, string File, string OldId, string Message);
public sealed record MusicImportCounts(int Scores, int Folders, int Playlists, int PlaylistItems,
    int Artists, int History, int Durations, int UnresolvedReferences);
public sealed record MusicImportPreview(string Id, string SourceId, string SourceLabel, string SourceHash,
    string ScopeKey, UInt64String ExpectedLibraryRevision, DateTimeOffset ExpiresAt, string State,
    IReadOnlyList<ImportFileSnapshot> Files, MusicImportCounts Counts,
    IReadOnlyList<ImportIdMapping> Mappings, IReadOnlyList<ImportIssue> Issues,
    MusicPreferences? Preferences, IReadOnlyList<MusicImportInstrument> Instruments);
public sealed record MusicImportInstrument(string Id, string RawName, string? InstrumentId,
    DateTimeOffset? FetchedAt, JsonElement Original);
public sealed record MusicImportReceipt(string ImportId, string SourceHash, string ScopeKey,
    UInt64String LibraryRevision, MusicImportCounts Counts, DateTimeOffset CommittedAt);
public sealed record MusicImportChanged(string Owner, MusicImportReceipt Receipt);
public sealed record MusicPreferences(double GapSeconds, double AdvanceMargin, string? DefaultInstrumentId,
    bool AutoSync, bool StopBeforePlay, bool Ensemble, JsonElement PresentationPreferences)
{
    public static MusicPreferences Default { get; } = new(2, 2, null, true, true, true,
        JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>()));
}
