using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public sealed record StorageVolumeUsage(string VolumeId, UInt64String AvailableBytes, UInt64String ReservedBytes, int Reservations);
public sealed record StorageCategoryUsage(string Category, string Module, UInt64String Bytes, UInt64String ProtectedBytes,
    UInt64String? BudgetBytes, bool OverBudget, IReadOnlyList<string> ProtectionReasons);
public sealed record StorageUsageSnapshot(UInt64String Revision, DateTimeOffset MeasuredAt,
    IReadOnlyList<StorageCategoryUsage> Categories, IReadOnlyList<StorageVolumeUsage> Volumes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StorageCleanupRequest(IReadOnlyList<string>? Categories = null);
public sealed record StorageCleanupCandidate(string Id, string Category, string Module, UInt64String SizeBytes, string Reason);
public sealed record StorageCleanupPlan(string Id, string Owner, UInt64String Revision, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, IReadOnlyList<StorageCleanupCandidate> Candidates, UInt64String ProtectedBytes,
    IReadOnlyList<string> BlockingReasons, string State = "planned");
public sealed record StorageCleanupItemResult(string Id, string State, UInt64String ReclaimedBytes, string? Reason);
public sealed record StorageCleanupResult(string PlanId, UInt64String Revision, DateTimeOffset CompletedAt,
    UInt64String ReclaimedBytes, IReadOnlyList<StorageCleanupItemResult> Items, string State = "completed");
public sealed record StorageChanged(string Owner, string PlanId, UInt64String Revision, string State,
    UInt64String ReclaimedBytes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StorageBackupRequest;
public sealed record StorageSchemaVersion(string ModuleId, int Version);
public sealed record StorageBackupSummary(string Id, string Purpose, string State, DateTimeOffset CreatedAt,
    UInt64String SizeBytes, IReadOnlyList<StorageSchemaVersion> Schemas, bool Retained, UInt64String Revision);
public sealed record StorageBackupChanged(string Owner, StorageBackupSummary Backup);
public sealed record StorageDataCount(string Module, UInt64String Records);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StorageRestoreRequest(string BackupId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StorageMigrationRequest(string DirectorySelectionId);
public sealed record StorageDirectorySelectionSnapshot(string Id, string Owner, string Name, DateTimeOffset ExpiresAt);
public sealed record StorageMaintenancePlan(string Id, string Owner, string Kind, UInt64String Revision,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, StorageBackupSummary? Backup,
    IReadOnlyList<StorageDataCount> CurrentData, string? TargetName, string State = "planned");
public sealed record StorageMaintenanceResult(string PlanId, string Kind, string State, string RootId,
    string? BackupId, bool RolledBack);
public sealed record StorageMaintenanceChanged(string Owner, string OperationId, StorageMaintenanceResult Result);

public sealed record StorageModuleReference(string SourceModule, string ResourceId, string AssetId);
public sealed record StorageModuleDataSnapshot(string ModuleId, bool Enabled, UInt64String ConfigurationRevision,
    UInt64String Records, UInt64String Assets, UInt64String AssetBytes, IReadOnlyList<StorageModuleReference> References,
    IReadOnlyList<string> BlockingReasons);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StorageModuleDeletionRequest(string ModuleId);
public sealed record StorageModuleDeletionPlan(string Id, string Owner, UInt64String Revision, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, StorageModuleDataSnapshot Data, string State = "planned");
public sealed record StorageModuleDeletionResult(string PlanId, string ModuleId, UInt64String RemovedAssets,
    UInt64String RemovedAssetBytes, UInt64String DataRevision);
public sealed record StorageModuleDataChanged(string Owner, string OperationId, StorageModuleDeletionResult Result);
