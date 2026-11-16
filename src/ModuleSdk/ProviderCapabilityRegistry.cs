using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed class ProviderCapabilityRegistry
{
    private readonly IReadOnlyList<ProviderCapabilityRequirement> requirements;
    private readonly ConcurrentDictionary<string, ProviderDescriptor> providers = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;

    public ProviderCapabilityRegistry(IEnumerable<ProviderCapabilityRequirement> requirements,
        TimeProvider? timeProvider = null)
    {
        this.requirements = requirements.ToArray();
        if (this.requirements.Select(x => x.CapabilityId).Distinct(StringComparer.Ordinal).Count() != this.requirements.Count ||
            this.requirements.Any(x => string.IsNullOrWhiteSpace(x.CapabilityId) || string.IsNullOrWhiteSpace(x.ModuleId) ||
                string.IsNullOrWhiteSpace(x.ProviderId) || x.MinimumProviderVersion >= x.MaximumProviderVersionExclusive ||
                string.IsNullOrWhiteSpace(x.Region) || x.VerifiedGameBuilds.Any(string.IsNullOrWhiteSpace) ||
                x.RequiredPermissions.Any(string.IsNullOrWhiteSpace) || x.Limitations.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("Provider 能力清单无效", nameof(requirements));
        this.requirements = Array.AsReadOnly(this.requirements.Select(x => x with
        {
            VerifiedGameBuilds = new HashSet<string>(x.VerifiedGameBuilds, StringComparer.Ordinal),
            RequiredPermissions = new HashSet<string>(x.RequiredPermissions, StringComparer.Ordinal),
            Limitations = Array.AsReadOnly(x.Limitations.ToArray())
        }).ToArray());
        clock = timeProvider ?? TimeProvider.System;
    }

    public void Register(ProviderDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var id = provider.Id;
        if (id is null || !Regex.IsMatch(id, @"\A[a-z][a-z0-9.-]{0,63}\z") ||
            string.IsNullOrWhiteSpace(provider.Region) || provider.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            provider.Permissions.Any(string.IsNullOrWhiteSpace) || provider.GameBuild is { Length: > 128 })
            throw new ArgumentException("Provider 描述无效", nameof(provider));
        providers[id] = provider with
        {
            Capabilities = new HashSet<string>(provider.Capabilities, StringComparer.Ordinal),
            Permissions = new HashSet<string>(provider.Permissions, StringComparer.Ordinal)
        };
    }

    public bool Remove(string providerId) => providers.TryRemove(providerId, out _);

    public IReadOnlyList<CapabilitySnapshot> Snapshot() => requirements.Select(requirement =>
        ProviderCapabilityEvaluator.Evaluate(requirement,
            providers.TryGetValue(requirement.ProviderId, out var provider) ? provider : null,
            clock.GetUtcNow())).ToArray();
}
