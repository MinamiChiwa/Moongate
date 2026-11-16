namespace MabiTool.Contracts;

public sealed record ApiList<T>(IReadOnlyList<T> Items, string? NextCursor, string SnapshotRevision);
public sealed record ModuleDependency(string Id, string MinimumVersion);
public sealed record ModuleProblem(string Code, string Message, string? DependencyId = null);
public enum ModuleRiskLevel { Low, High }
public enum ModuleRiskBehavior { MemoryRead, MemoryInjection, GameFileModification, GamePacketSend }
public sealed record ModuleRisk(OpenEnum<ModuleRiskLevel> Level,
    IReadOnlyList<OpenEnum<ModuleRiskBehavior>> HighRiskBehaviors);
/// <param name="DisplayNames">模块的界面名称，键为语言标记（zh-Hans、zh-Hant）；旧宿主没有该字段时为空。</param>
/// <param name="Essential">工具运行所必需的核心模块：始终启用，公共启停接口拒绝停用（module.essential）；旧宿主没有该字段时为空。</param>
/// <param name="Descriptions">模块功能的一句说明，键同 <paramref name="DisplayNames"/>；旧宿主没有该字段时为空。</param>
public sealed record ModuleSnapshot(
    string Id, string Version, string MinimumHostApi, string MaximumHostApiExclusive,
    IReadOnlyList<ModuleDependency> Dependencies, bool Enabled, string State,
    IReadOnlyList<ModuleProblem> Problems, IReadOnlyList<string> ActiveOperationIds,
    int MaximumConcurrentDemands, int IdleRetentionSeconds, string SettingsSchema, ModuleRisk? Risk = null,
    IReadOnlyDictionary<string, string>? DisplayNames = null, IReadOnlyDictionary<string, string>? Descriptions = null,
    bool? Essential = null);
public enum CapabilityStatus { Available, Unavailable, Degraded, Unknown }
public sealed record CapabilitySnapshot(string Id, string ModuleId, OpenEnum<CapabilityStatus> Status,
    string? ProviderId, string? ProviderVersion, string? GameBuild, string? GameSessionId,
    string ReasonCode, DateTimeOffset CheckedAt, IReadOnlyList<string> Limitations);
public sealed record SystemInfo(string Product, string Version, string ApiVersion, string InstanceId,
    DateTimeOffset StartedAt, string Runtime, string OperatingSystem);
public sealed record HealthInfo(string Status, string InstanceId);
