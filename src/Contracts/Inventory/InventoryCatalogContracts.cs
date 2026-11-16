using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public sealed record InventoryCatalogResource(string Path, string PackageKey, UInt64String Offset,
    UInt64String PackedBytes, string Sha256, string DecodedSha256);
public sealed record InventoryCatalogEntry(InventoryTypeKey Type, string RawName, string DisplayName,
    string Table, UInt64String Row, UInt64String MetaOffset, UInt64String NameOffset, UInt64String DataOffset,
    bool? IsInstrument = null, UInt64String? InstrumentTypeId = null, UInt64String? EquipmentToolTypeId = null);
public sealed record InventoryCatalogExcluded(InventoryTypeKey Type, string RawName, string Reason);
public sealed record InventoryCatalogCategory(string Category, string? Table, bool Supported,
    int Entries, string? Reason = null);
public sealed record InventoryCatalogData(string Id, string Region, string IndexSha256,
    DateTimeOffset ExtractedAt, IReadOnlyList<InventoryCatalogResource> Resources,
    IReadOnlyList<InventoryCatalogEntry> Entries, IReadOnlyList<InventoryCatalogExcluded> Excluded,
    IReadOnlyList<InventoryCatalogCategory> Categories);
public sealed record InventoryCatalogSnapshot(UInt64String Revision, InventoryCatalogData? Data);
public sealed record InventoryCatalogSummary(UInt64String Revision, string? Id, string? IndexSha256,
    int Entries, int Excluded, IReadOnlyList<InventoryCatalogCategory> Categories);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryCatalogQuery(string? Category = null, string? Search = null,
    int PageSize = 100, string? Cursor = null);
public sealed record InventoryCatalogPage(IReadOnlyList<InventoryCatalogEntry> Items, string? NextCursor,
    UInt64String Revision, IReadOnlyList<InventoryCatalogCategory> Categories);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryCatalogRefreshRequest(string GameSessionId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryCatalogImportRequest(string FileSelectionId);
public sealed record InventoryCatalogChanged(UInt64String Revision, string Id, int Entries, int Excluded);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryImportRequest(string FileSelectionId, string? AccountId = null);
public sealed record InventoryImportFile(string Name, string Content);
public sealed record InventoryImportIssue(string Id, string Kind, string? AccountId, string? CharacterId,
    InventoryTypeKey? Type, string Description);
public sealed record InventoryImportAccount(string AccountId, string Label, int Characters, int Items,
    int Associations);
public sealed record InventoryImportPreview(string Id, UInt64String Revision, DateTimeOffset CreatedAt,
    IReadOnlyList<InventoryImportAccount> Accounts, IReadOnlyList<InventoryImportIssue> Issues,
    int Files, int Items, int Associations);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryImportResolution(string IssueId, string Choice);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryImportCommitRequest(IReadOnlyList<InventoryImportResolution> Resolutions);
public sealed record InventoryImportReceipt(string PreviewId, UInt64String Revision, int Items, int Associations, int Accounts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryExportRequest(string? AccountId = null, bool IncludeHistory = true);
public sealed record InventoryExportFile(string Name, string Content, string MediaType = "text/csv");
