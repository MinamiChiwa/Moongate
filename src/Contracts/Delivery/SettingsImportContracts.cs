using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SettingsImportRequest(string SourceId, string SourceLabel, IReadOnlyList<ImportSourceFile> Files,
    IReadOnlyDictionary<string, InputBindingPreference>? BindingOverrides = null,
    IReadOnlyDictionary<string, string>? ProfileNames = null);
public sealed record SettingsImportPreview(string Id, string Project, string SourceId, string SourceLabel, string SourceHash,
    string TargetETag, DateTimeOffset ExpiresAt, string State, IReadOnlyList<ImportFileSnapshot> Files,
    IReadOnlyList<ImportIdMapping> Mappings, IReadOnlyList<ImportIssue> Issues,
    RoomPreferences? RoomPreferences, ColorStudioPreferences? ColorPreferences, ColorProfile? Draft,
    IReadOnlyList<ColorProfile> Profiles, IReadOnlyList<ImportedColorHistoryItem> History,
    IReadOnlyList<GlobalShortcutSnapshot> Shortcuts);
public sealed record SettingsImportReceipt(string ImportId, string Project, string SourceId, string SourceHash,
    string TargetETag, int Profiles, int History, DateTimeOffset CommittedAt);
public sealed record SettingsImportChanged(string Owner, SettingsImportReceipt Receipt);
public sealed record PreferenceRevisionChanged(string Owner, UInt64String Revision);
