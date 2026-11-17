using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateCheckRequest;
/// <summary>检查发现的新版本。解压版整包更新中 <c>ReleaseId</c> 为版本号（如 <c>0.2.0-preview</c>），
/// <c>ManifestSha256</c> 为整包 zip 的 SHA-256，<c>SizeBytes</c> 为 zip 字节数。</summary>
public sealed record UpdateAvailable(string ReleaseId, string ManifestSha256, string? PlanId,
    DateTimeOffset? PlanExpiresAt, OpenEnum<UpdateSourceKind> Source, UInt64String? SizeBytes = null);
/// <summary>
/// 更新状态。<c>State</c>：idle、checking、available、preparing/downloading/verifying、ready（已下载并校验、可安装）、
/// waitingForIdle、applying、handedOff（已交给替换器，程序即将重启）、failed。<c>InstalledReleaseId</c> 为当前运行的版本号；
/// <c>LastInstall</c> 为最近一次安装的结果；<c>Configured</c> 为 false 时 <c>UnavailableReason</c> 给出原因码。
/// </summary>
public sealed record UpdateStateSnapshot(bool Configured, string State, string? InstalledReleaseId,
    UpdatePolicySnapshot Policy, OperationSnapshot? Operation, UpdateAvailable? Available,
    string? Channel = null, UpdateInstallResult? LastInstall = null, string? UnavailableReason = null);
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
