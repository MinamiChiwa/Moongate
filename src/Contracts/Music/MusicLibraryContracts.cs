using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicFolder(string Id, string? ParentId, string Name, string SortKey);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicPlaylist(string Id, string? FolderId, string Name, string Notes, string? DefaultInstrumentId,
    string SortKey, IReadOnlyList<PlaylistItem> Items);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicArtist(string Id, string Name, IReadOnlyList<string> Aliases);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicNameSettings(IReadOnlyList<MusicArtist> Artists, IReadOnlyDictionary<string, string> Assignments,
    IReadOnlyList<string> Noise);
public sealed record MusicScoreSource(string ScopeKey, string Region, string RealmId, string CharacterId, string Provider,
    string Kind, UInt64String Revision, string RefreshStatus, bool Stale);
public sealed record MusicLibraryInfo(UInt64String Revision, int FolderCount, int PlaylistCount, int ItemCount,
    MusicNameSettings NameSettings, IReadOnlyList<MusicScoreSource> Sources);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicEditBatch(IReadOnlyList<MusicFolder> PutFolders, IReadOnlyList<string> DeleteFolders,
    IReadOnlyList<MusicPlaylist> PutPlaylists, IReadOnlyList<string> DeletePlaylists, MusicNameSettings? NameSettings);
public sealed record MusicEditResult(UInt64String Revision, IReadOnlyList<string> ChangedFolderIds,
    IReadOnlyList<string> ChangedPlaylistIds, bool NameSettingsChanged);
public enum MusicCollection { Folders, Playlists, PlaylistItems, Scores }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicQuery(MusicCollection Collection, string? ParentId, string? PlaylistId, string? ScopeKey,
    string Search, int PageSize, string? Cursor);
public sealed record MusicLibraryPage(UInt64String LibraryRevision, UInt64String? SourceRevision,
    IReadOnlyList<MusicFolder> Folders, IReadOnlyList<MusicPlaylist> Playlists, IReadOnlyList<PlaylistItem> Items,
    IReadOnlyList<ScoreEntry> Scores, string? NextCursor);
