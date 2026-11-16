using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateCheckRequest;
public sealed record UpdateAvailable(string ReleaseId, string ManifestSha256, string? PlanId,
    DateTimeOffset? PlanExpiresAt, OpenEnum<UpdateSourceKind> Source);
public sealed record UpdateStateSnapshot(bool Configured, string State, string? InstalledReleaseId,
    UpdatePolicySnapshot Policy, OperationSnapshot? Operation, UpdateAvailable? Available);
public sealed record UpdateComponentSnapshot(string Id, string Version, UInt64String DownloadBytes,
    UInt64String InstalledBytes, bool Optional, IReadOnlyList<string> Dependencies);
public sealed record UpdatePlanSnapshot(string Id, UInt64String Revision, string ReleaseId,
    string ManifestSha256, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Expired,
    IReadOnlyList<UpdateComponentSnapshot> Components, IReadOnlyList<string> RemovedComponents,
    UInt64String DownloadBytes, UInt64String PeakBytes, string? RequiredUpdaterVersion = null);
public sealed record UpdateDownloadStatus(string PlanId, string ReleaseId, string ManifestSha256,
    UInt64String RequiredDownloadBytes, UInt64String MaximumDownloadBytes, UInt64String RepairDownloadBytes,
    UInt64String PeakAdditionalBytes, int ReusedFiles, int DownloadedFiles, bool ReusedStage,
    UInt64String VerifiedBytes, UInt64String? TotalBytes = null,
    OpenEnum<UpdateSourceKind>? Source = null, string? ComponentId = null);
