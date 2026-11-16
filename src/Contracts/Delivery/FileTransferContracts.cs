using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public sealed record FileTransferCapabilities(UInt64String MaximumUploadBytes, UInt64String MaximumExpandedBytes,
    int MaximumArchiveEntries, bool NativeSelections);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileUploadRequest(string Name, UInt64String SizeBytes, string Sha256,
    string MediaType = "application/octet-stream");
public sealed record FileUploadSnapshot(string Id, string Owner, string Name, UInt64String SizeBytes,
    string Sha256, string MediaType, string State, UInt64String Revision, DateTimeOffset ExpiresAt, AssetSnapshot? Asset);
public sealed record FileSelectionSnapshot(string Id, string Owner, string Name, string Action,
    UInt64String Revision, DateTimeOffset ExpiresAt, UInt64String? SizeBytes, string? Sha256, bool Overwrite);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileSelectionUploadRequest(string SelectionId);
public sealed record FileImportScope(string Region, string RealmId, UInt64String CharacterId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileImportRequest(string Project, string SourceId, string SourceLabel,
    IReadOnlyList<string> UploadIds, string? ArchiveUploadId = null, FileImportScope? Scope = null,
    IReadOnlyDictionary<string, string>? ArtistMappings = null,
    IReadOnlyDictionary<string, InputBindingPreference>? BindingOverrides = null,
    IReadOnlyDictionary<string, string>? ProfileNames = null);
public sealed record FileImportPreview(string Id, string Project, string ParserVersion, string TargetETag,
    DateTimeOffset ExpiresAt, MusicImportPreview? Music, SettingsImportPreview? Settings);
public sealed record FileExportSource(string Id, UInt64String Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileExportRequest(IReadOnlyList<FileExportSource> Sources, string Name = "export.zip",
    bool IncludeSelectedFiles = false);
public sealed record FileExportEntry(string Path, string Module, string SourceId, UInt64String Revision,
    string MediaType, string Sha256, UInt64String SizeBytes);
public sealed record FileExportManifest(int SchemaVersion, string ExportId, DateTimeOffset CreatedAt,
    IReadOnlyList<string> ContentCategories, IReadOnlyList<FileExportEntry> Files);
public sealed record FileExportArtifact(string Id, string Owner, string Name, AssetSnapshot Asset,
    DateTimeOffset ExpiresAt, IReadOnlyList<string> ContentCategories);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileExportSaveRequest(string SelectionId);
public sealed record FileSaveReceipt(string ExportId, string Name, string Sha256, UInt64String SizeBytes, bool Overwritten);
