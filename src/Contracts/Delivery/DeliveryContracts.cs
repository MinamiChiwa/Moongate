namespace MabiTool.Contracts;

public sealed record AssetSnapshot(string Id, string ModuleId, string Scope, string MediaType, string Sha256,
    UInt64String SizeBytes, UInt64String Revision);
public enum UpdateSourceKind { GithubDirect, GithubApi, Relay }
public sealed record UpdateSourceSettings(string? GithubRepository, string Channel, string? ApiBaseUri, string? RelayBaseUri);
/// <param name="MaximumBytesPerSecond">下载带宽上限；null 为不限速（默认），设定值须在 65,536–67,108,864 字节/秒。</param>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdatePolicy(bool AutomaticChecks = true, int CheckIntervalHours = 6,
    int DownloadConcurrency = 2, int? MaximumBytesPerSecond = null);
public sealed record UpdatePolicySnapshot(UpdatePolicy Policy, UInt64String Revision);
public sealed record UpdateProgress(string PlanId, string Phase, OpenEnum<UpdateSourceKind>? Source,
    UInt64String VerifiedBytes, UInt64String? TotalBytes, string? ComponentId, string? ReasonCode,
    string? OperationId = null, UInt64String? Revision = null);
public sealed record UpdateInstallResult(string PlanId, string TransactionId, string ReleaseId, string Outcome, string? RecoverySnapshotId, string? FailureCode = null);
