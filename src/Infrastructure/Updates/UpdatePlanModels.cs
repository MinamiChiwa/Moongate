namespace MabiTool.Infrastructure.Updates;

public sealed record UpdateComponent(
    string ComponentId,
    Version Version,
    IReadOnlyList<UpdateDependency> Dependencies,
    long DownloadBytes,
    long InstalledBytes,
    string Sha256,
    string? MinimumHostVersion = null,
    string? MaximumHostVersion = null,
    int? MinimumWorkerProtocol = null,
    int? MaximumWorkerProtocol = null,
    int? MinimumSchema = null,
    int? MaximumSchema = null,
    string? MinimumOsBuild = null,
    bool IsOptional = false);

public sealed record UpdateDependency(string ComponentId, Version MinimumVersion);

public sealed record InstalledUpdateComponent(string ComponentId, Version Version, long InstalledBytes, string Sha256);

public sealed record UpdatePlannerContext(
    Version HostVersion,
    int WorkerProtocol,
    int DataSchema,
    string Platform,
    Version OsBuild,
    long AvailableBytes,
    IReadOnlyList<InstalledUpdateComponent> InstalledComponents,
    long DatabaseBackupBytes = 0);

public sealed record UpdateManifest(
    string ReleaseId,
    long ReleaseSequence,
    string ProductId,
    string Channel,
    string Platform,
    IReadOnlyList<UpdateComponent> Components,
    string ManifestSha256);

public sealed record UpdatePlan(
    string PlanId,
    string ManifestSha256,
    string ReleaseId,
    long ReleaseSequence,
    IReadOnlyList<UpdateComponent> Components,
    IReadOnlyList<InstalledUpdateComponent> Reused,
    IReadOnlyList<InstalledUpdateComponent> Removed,
    long DownloadBytes,
    long PeakBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string BaseFingerprint,
    Version? RequiredUpdaterVersion = null);
