using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record ProviderCapabilityRequirement(
    string CapabilityId, string ModuleId, string ProviderId,
    Version MinimumProviderVersion, Version MaximumProviderVersionExclusive,
    string Region, IReadOnlySet<string> VerifiedGameBuilds, IReadOnlySet<string> RequiredPermissions,
    IReadOnlyList<string> Limitations);

public sealed record ProviderDescriptor(
    string Id, Version Version, string Region, string? GameBuild,
    IReadOnlySet<string> Capabilities, IReadOnlySet<string> Permissions,
    string? GameSessionId);

public static class ProviderCapabilityEvaluator
{
    public static CapabilitySnapshot Evaluate(ProviderCapabilityRequirement requirement,
        ProviderDescriptor? provider, DateTimeOffset checkedAt)
    {
        if (checkedAt.Offset != TimeSpan.Zero) throw new ArgumentException("检测时间必须使用 UTC", nameof(checkedAt));
        var version = provider?.Version.ToString();
        CapabilitySnapshot Unavailable(string reason) => new(requirement.CapabilityId,
            requirement.ModuleId, CapabilityStatus.Unavailable, provider?.Id, version,
            provider?.GameBuild, provider?.GameSessionId, reason, checkedAt, requirement.Limitations);

        if (provider is null) return Unavailable("provider.unavailable");
        if (!string.Equals(provider.Id, requirement.ProviderId, StringComparison.Ordinal))
            return Unavailable("provider.mismatch");
        if (provider.Version < requirement.MinimumProviderVersion ||
            provider.Version >= requirement.MaximumProviderVersionExclusive)
            return Unavailable("provider.versionIncompatible");
        if (!string.Equals(provider.Region, requirement.Region, StringComparison.OrdinalIgnoreCase))
            return Unavailable("provider.regionIncompatible");
        if (provider.GameBuild is null)
            return new(requirement.CapabilityId, requirement.ModuleId, CapabilityStatus.Unknown,
                provider.Id, version, null, provider.GameSessionId, "gameBuild.unknown", checkedAt,
                requirement.Limitations);
        if (requirement.VerifiedGameBuilds.Count == 0)
            return new(requirement.CapabilityId, requirement.ModuleId, CapabilityStatus.Unknown,
                provider.Id, version, provider.GameBuild, provider.GameSessionId, "gameBuild.unverified", checkedAt,
                requirement.Limitations);
        if (!requirement.VerifiedGameBuilds.Contains(provider.GameBuild))
            return Unavailable("gameBuild.incompatible");
        if (!provider.Capabilities.Contains(requirement.CapabilityId))
            return Unavailable("capability.unsupported");
        if (!requirement.RequiredPermissions.IsSubsetOf(provider.Permissions))
            return Unavailable("permission.missing");

        var status = requirement.Limitations.Count == 0 ? CapabilityStatus.Available : CapabilityStatus.Degraded;
        return new(requirement.CapabilityId, requirement.ModuleId, status,
            provider.Id, version, provider.GameBuild, provider.GameSessionId,
            status == CapabilityStatus.Available ? "available" : "capability.degraded", checkedAt,
            requirement.Limitations);
    }
}
