using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateCheckRequest;
/// <summary>检查发现的新版本。解压版整包更新中 <c>ReleaseId</c> 为版本号（如 <c>0.2.0-preview</c>），
/// <c>ManifestSha256</c> 为整包 zip 的 SHA-256，<c>SizeBytes</c> 为 zip 字节数；<c>DownloadKind</c> 为本次下载方式
/// （<c>incremental</c> 增量包、<c>full</c> 完整包），<c>DownloadBytes</c> 为本次需要下载的字节数，
/// <c>ResumableBytes</c> 为上次中断的下载已保存、再次下载可续传的字节数（没有时为空）；
/// <c>FallbackReason</c> 为增量失败后改用完整包的原因码。</summary>
public sealed record UpdateAvailable(string ReleaseId, string ManifestSha256, string? PlanId,
    DateTimeOffset? PlanExpiresAt, OpenEnum<UpdateSourceKind> Source, UInt64String? SizeBytes = null,
    string? DownloadKind = null, UInt64String? DownloadBytes = null, UInt64String? ResumableBytes = null, string? FallbackReason = null);
/// <summary>最近一次完成的检查：<c>Outcome</c> 为 <c>current</c>（当前运行版本已是最新）、<c>available</c>（发现新版本）
/// 或 <c>failed</c>（<c>FailureCode</c> 为原因码）。</summary>
public sealed record UpdateCheckSummary(DateTimeOffset CheckedAt, string Outcome, string? FailureCode = null);
/// <summary>
/// 更新状态。<c>State</c>：idle、checking、available、preparing/downloading/verifying、ready（已下载并校验、可安装）、
/// waitingForIdle、applying、handedOff（已交给替换器，程序即将重启）、failed。<c>InstalledReleaseId</c> 为当前运行的版本号；
/// <c>LastInstall</c> 为最近一次安装的结果；<c>Configured</c> 为 false 时 <c>UnavailableReason</c> 给出原因码。
/// <c>Available</c> 只给高于当前运行版本、未过期且计划仍在的发行；<c>LastCheck</c> 只取针对当前运行版本的检查，从未检查时为空。
/// </summary>
public sealed record UpdateStateSnapshot(bool Configured, string State, string? InstalledReleaseId,
    UpdatePolicySnapshot Policy, OperationSnapshot? Operation, UpdateAvailable? Available,
    string? Channel = null, UpdateInstallResult? LastInstall = null, string? UnavailableReason = null,
    UpdateCheckSummary? LastCheck = null);
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
    OpenEnum<UpdateSourceKind>? Source = null, string? ComponentId = null,
    string? DownloadKind = null, string? FallbackReason = null);
