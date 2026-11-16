using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;
using MabiTool.Providers.GitHubReleases;

namespace MabiTool.Host.Updates;

public sealed record UpdateCheckResult(bool UpdateAvailable, string ReleaseId, string ManifestSha256,
    string? PlanId, DateTimeOffset? PlanExpiresAt, UpdateSourceKind Source);

public interface IUpdateCheckService
{
    bool IsConfigured { get; }
    Task<UpdateCheckResult> CheckAsync(CancellationToken token);
}

public sealed class ReleaseUpdateCheckService(UpdateInstallationOptions? options, ReleaseIndexSourceProvider source,
    TimeProvider? timeProvider = null) : IUpdateCheckService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public bool IsConfigured => options is not null;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        if (options is null) throw new UpdateSourceException("update.sourceNotConfigured", []);
        var trust = OpenTrust();
        var indexBytes = await source.FetchChannelIndexAsync(options.Source, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var index = trust.VerifyChannelIndex(SignedPayloadEnvelope.Parse(indexBytes.Content), options.ProductId, options.Source.Channel, clock.GetUtcNow());
        var manifest = await source.FetchReleaseManifestAsync(options.Source, index.TargetManifestSha256, index.TargetManifestLength, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var release = trust.VerifyReleaseManifest(manifest.Content, index, options.PlannerContext.Platform, clock.GetUtcNow());
        if (release.Manifest.ReleaseSequence < options.InstalledReleaseSequence)
            throw new UpdatePlanException("update.versionRollback");
        if (release.Manifest.ReleaseSequence == options.InstalledReleaseSequence)
        {
            if (!string.Equals(release.Manifest.ManifestSha256, options.InstalledManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdatePlanException("update.releaseIdentityMismatch");
            return new(false, release.Manifest.ReleaseId, release.Manifest.ManifestSha256, null, null, manifest.Source);
        }
        var planner = new UpdatePlanner(clock);
        var plan = options.Runtime.UpdaterVersion < release.MinimumUpdaterVersion
            ? planner.CreateUpdaterBootstrapPlan(release, CurrentContext(), options.Runtime)
            : planner.CreatePlan(release, CurrentContext(), options.Runtime);
        await new UpdatePlanRepository(options.StateRoot).SaveAsync(new(1, plan, indexBytes.Content, manifest.Content), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new(true, plan.ReleaseId, plan.ManifestSha256, plan.PlanId, plan.ExpiresAt, manifest.Source);
    }

    internal async Task<(UpdatePlan Plan, VerifiedReleaseManifest Release)> ReadValidatedPlanAsync(string planId, CancellationToken token = default,
        bool verifyAvailableSpace = true)
    {
        if (options is null) throw new UpdateSourceException("update.sourceNotConfigured", []);
        return await UpdateSignedPlanVerifier.ReadAsync(options.StateRoot, options.Validation, planId, clock, verifyAvailableSpace, token).ConfigureAwait(false);
    }

    private UpdateTrustStore OpenTrust() => new(Path.Combine(options!.StateRoot, "trust.json"), options.InitialRoot, clock.GetUtcNow());

    private UpdatePlannerContext CurrentContext() => options!.PlannerContext with
    {
        AvailableBytes = Math.Min(options.PlannerContext.AvailableBytes,
            new DriveInfo(Path.GetPathRoot(options.InstallationRoot)!).AvailableFreeSpace),
        OsBuild = Environment.OSVersion.Version
    };
}
