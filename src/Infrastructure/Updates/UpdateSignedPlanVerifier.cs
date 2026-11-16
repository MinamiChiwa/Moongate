namespace MabiTool.Infrastructure.Updates;

internal sealed record UpdateInstallValidation(string ProductId, string Channel, TrustRoot InitialRoot,
    long InstalledReleaseSequence, string InstalledManifestSha256, UpdatePlannerContext Context, ReleaseRuntimeContext Runtime);

internal static class UpdateSignedPlanVerifier
{
    internal static async Task<(UpdatePlan Plan, VerifiedReleaseManifest Release)> ReadAsync(string stateRoot,
        UpdateInstallValidation validation, string planId, TimeProvider? time = null, bool verifyAvailableSpace = true,
        CancellationToken token = default)
    {
        var clock = time ?? TimeProvider.System;
        var saved = await new UpdatePlanRepository(stateRoot).ReadAsync(planId, token).ConfigureAwait(false);
        var trust = new UpdateTrustStore(UpdatePaths.Local(stateRoot, "trust.json"), validation.InitialRoot, clock.GetUtcNow());
        var index = trust.VerifyChannelIndex(SignedPayloadEnvelope.Parse(saved.SignedIndex), validation.ProductId, validation.Channel, clock.GetUtcNow());
        var release = trust.VerifyReleaseManifest(saved.SignedManifest, index, validation.Context.Platform, clock.GetUtcNow());
        if (release.Manifest.ReleaseSequence <= validation.InstalledReleaseSequence) throw new UpdatePlanException("update.planStale");
        var context = validation.Context with
        {
            AvailableBytes = verifyAvailableSpace ? Math.Min(validation.Context.AvailableBytes,
                new DriveInfo(Path.GetPathRoot(Path.GetFullPath(stateRoot))!).AvailableFreeSpace) : long.MaxValue,
            OsBuild = Environment.OSVersion.Version
        };
        var planner = new UpdatePlanner(clock);
        if (saved.Plan.RequiredUpdaterVersion is null) planner.ValidatePlan(saved.Plan, release, context, validation.Runtime);
        else planner.ValidateUpdaterBootstrapPlan(saved.Plan, release, context, validation.Runtime);
        return (saved.Plan, release);
    }

    internal static IReadOnlyList<ManifestFile> TargetFiles(UpdatePlan plan, VerifiedReleaseManifest release)
    {
        var files = new List<ManifestFile>(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in plan.Components)
        {
            if (!ids.Add(component.ComponentId) || UpdatePaths.Normalize(component.ComponentId).Contains('/'))
                throw new FileTreeException("update.manifestPathInvalid");
            foreach (var artifact in release.Components[component.ComponentId].Artifacts.Files)
            {
                if (files.Count >= 100_000) throw new FileTreeException("update.manifestTooLarge");
                files.Add(artifact.File with { RelativePath = "components/" + component.ComponentId + "/" + artifact.File.RelativePath });
            }
        }
        return FileTreePlanner.ValidateManifest(files).Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }
}
