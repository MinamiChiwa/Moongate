using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class ProviderCapabilityTests
{
    private static readonly DateTimeOffset CheckedAt = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    private static ProviderCapabilityRequirement Requirement(IReadOnlyList<string>? limitations = null) => new(
        "music.inventory", "music-library", "tw-cli", new(1, 2), new(2, 0), "TW",
        new HashSet<string>(["tw-build-7"]),
        new HashSet<string>(["score.read"]), limitations ?? []);

    private static ProviderDescriptor Provider(Version? version = null, string region = "TW",
        string? build = "tw-build-7", IReadOnlySet<string>? capabilities = null,
        IReadOnlySet<string>? permissions = null) => new("tw-cli", version ?? new(1, 5), region, build,
            capabilities ?? new HashSet<string>(["music.inventory"]),
            permissions ?? new HashSet<string>(["score.read"]), "game-session-1");

    [Fact]
    public void MissingProviderAndBuildFingerprintNeverBecomeAvailable()
    {
        var missing = ProviderCapabilityEvaluator.Evaluate(Requirement(), null, CheckedAt);
        Assert.Equal(CapabilityStatus.Unavailable, missing.Status.KnownValue);
        Assert.Equal("provider.unavailable", missing.ReasonCode);

        var unknownBuild = ProviderCapabilityEvaluator.Evaluate(Requirement(), Provider(build: null), CheckedAt);
        Assert.Equal(CapabilityStatus.Unknown, unknownBuild.Status.KnownValue);
        Assert.Equal("gameBuild.unknown", unknownBuild.ReasonCode);

        var incompatibleBuild = ProviderCapabilityEvaluator.Evaluate(Requirement(), Provider(build: "tw-build-8"), CheckedAt);
        Assert.Equal(CapabilityStatus.Unavailable, incompatibleBuild.Status.KnownValue);
        Assert.Equal("gameBuild.incompatible", incompatibleBuild.ReasonCode);

        var unverifiedBuild = ProviderCapabilityEvaluator.Evaluate(
            Requirement() with { VerifiedGameBuilds = new HashSet<string>() }, Provider(), CheckedAt);
        Assert.Equal(CapabilityStatus.Unknown, unverifiedBuild.Status.KnownValue);
        Assert.Equal("gameBuild.unverified", unverifiedBuild.ReasonCode);
    }

    [Fact]
    public void ProviderVersionRegionCapabilityAndPermissionAreCheckedIndependently()
    {
        Assert.Equal("provider.versionIncompatible", ProviderCapabilityEvaluator.Evaluate(
            Requirement(), Provider(version: new(2, 0)), CheckedAt).ReasonCode);
        Assert.Equal("provider.regionIncompatible", ProviderCapabilityEvaluator.Evaluate(
            Requirement(), Provider(region: "KR"), CheckedAt).ReasonCode);
        Assert.Equal("capability.unsupported", ProviderCapabilityEvaluator.Evaluate(
            Requirement(), Provider(capabilities: new HashSet<string>()), CheckedAt).ReasonCode);
        Assert.Equal("permission.missing", ProviderCapabilityEvaluator.Evaluate(
            Requirement(), Provider(permissions: new HashSet<string>()), CheckedAt).ReasonCode);
    }

    [Fact]
    public void CompatibleProviderReportsSourceSessionAndExplicitLimitations()
    {
        var available = ProviderCapabilityEvaluator.Evaluate(Requirement(), Provider(), CheckedAt);
        Assert.Equal(CapabilityStatus.Available, available.Status.KnownValue);
        Assert.Equal("tw-cli", available.ProviderId);
        Assert.Equal("1.5", available.ProviderVersion);
        Assert.Equal("tw-build-7", available.GameBuild);
        Assert.Equal("game-session-1", available.GameSessionId);
        Assert.Equal(CheckedAt, available.CheckedAt);

        var degraded = ProviderCapabilityEvaluator.Evaluate(
            Requirement(["仅支持完整库存快照"]), Provider(), CheckedAt);
        Assert.Equal(CapabilityStatus.Degraded, degraded.Status.KnownValue);
        Assert.Equal("capability.degraded", degraded.ReasonCode);
        Assert.Equal(["仅支持完整库存快照"], degraded.Limitations);
    }

    [Fact]
    public void DetectionTimestampMustBeUtc()
    {
        Assert.Throws<ArgumentException>(() => ProviderCapabilityEvaluator.Evaluate(
            Requirement(), Provider(), CheckedAt.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void RegistryPublishesMissingAndRegisteredProvidersWithoutSharingMutableCollections()
    {
        var requirement = Requirement();
        var registry = new ProviderCapabilityRegistry([requirement], new FixedClock(CheckedAt));
        Assert.Equal("provider.unavailable", Assert.Single(registry.Snapshot()).ReasonCode);

        var capabilities = new HashSet<string>(["music.inventory"]);
        var permissions = new HashSet<string>(["score.read"]);
        registry.Register(Provider(capabilities: capabilities, permissions: permissions));
        capabilities.Clear(); permissions.Clear();
        var available = Assert.Single(registry.Snapshot());
        Assert.Equal(CapabilityStatus.Available, available.Status.KnownValue);
        Assert.Equal(CheckedAt, available.CheckedAt);

        Assert.True(registry.Remove("tw-cli"));
        Assert.Equal("provider.unavailable", Assert.Single(registry.Snapshot()).ReasonCode);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
