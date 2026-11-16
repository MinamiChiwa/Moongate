using System.Security.Cryptography;
using System.Text;

namespace MabiTool.Infrastructure.Updates;

public sealed record ReleaseRuntimeContext(Version UpdaterVersion, int ModuleApi, string RuntimeMode);

public sealed partial class UpdatePlanner
{
    public UpdatePlan CreatePlan(VerifiedReleaseManifest release, UpdatePlannerContext context,
        ReleaseRuntimeContext runtime, IReadOnlySet<string>? requestedComponents = null)
    {
        ValidateReleaseRuntime(release, runtime);
        return CreateVerifiedPlan(release, context, runtime, requestedComponents, null);
    }

    /// <summary>固定可下载的目标；业务安装须由达到最低版本的 updater 重新验证。</summary>
    public UpdatePlan CreateUpdaterBootstrapPlan(VerifiedReleaseManifest release, UpdatePlannerContext context,
        ReleaseRuntimeContext runtime, IReadOnlySet<string>? requestedComponents = null)
    {
        ValidateRuntimeMetadata(release, runtime);
        RequireBootstrapUpdater(release, runtime);
        return CreateVerifiedPlan(release, context, runtime, requestedComponents, release.MinimumUpdaterVersion);
    }

    private UpdatePlan CreateVerifiedPlan(VerifiedReleaseManifest release, UpdatePlannerContext context,
        ReleaseRuntimeContext runtime, IReadOnlySet<string>? requestedComponents, Version? bootstrap)
    {
        var plan = CreatePlan(release.Manifest, context, requestedComponents);
        ValidateModuleApis(release, runtime, plan.Components);
        var peak = BootstrapPeak(plan.PeakBytes, release, bootstrap);
        if (peak > context.AvailableBytes) throw new UpdatePlanException("update.diskSpaceInsufficient");
        return plan with { BaseFingerprint = PlanFingerprint(plan.BaseFingerprint, runtime, bootstrap), PeakBytes = peak,
            ExpiresAt = plan.ExpiresAt < release.AuthorizedUntil ? plan.ExpiresAt : release.AuthorizedUntil,
            RequiredUpdaterVersion = bootstrap };
    }

    public void ValidatePlan(UpdatePlan plan, VerifiedReleaseManifest release, UpdatePlannerContext context,
        ReleaseRuntimeContext runtime)
    {
        ValidateReleaseRuntime(release, runtime);
        if (plan.RequiredUpdaterVersion is not null) throw new UpdatePlanException("update.updaterRequired");
        ValidateVerifiedPlan(plan, release, context, runtime);
    }

    public void ValidateUpdaterBootstrapPlan(UpdatePlan plan, VerifiedReleaseManifest release, UpdatePlannerContext context,
        ReleaseRuntimeContext runtime)
    {
        ValidateRuntimeMetadata(release, runtime);
        RequireBootstrapUpdater(release, runtime);
        if (plan.RequiredUpdaterVersion != release.MinimumUpdaterVersion) throw new UpdatePlanException("update.planStale");
        ValidateVerifiedPlan(plan, release, context, runtime);
    }

    private void ValidateVerifiedPlan(UpdatePlan plan, VerifiedReleaseManifest release, UpdatePlannerContext context,
        ReleaseRuntimeContext runtime)
    {
        ValidateContext(context);
        if (plan.ExpiresAt > release.AuthorizedUntil ||
            plan.BaseFingerprint != PlanFingerprint(Fingerprint(context), runtime, plan.RequiredUpdaterVersion) ||
            plan.Components.Any(component => !release.Components.TryGetValue(component.ComponentId, out var signed) ||
                (component with { Dependencies = signed.Metadata.Dependencies }) != signed.Metadata ||
                !component.Dependencies.SequenceEqual(signed.Metadata.Dependencies)))
            throw new UpdatePlanException("update.planStale");
        // 重建选定闭包，校验必需组件、依赖与预算；持久化 DTO 本身不授予安装权限。
        var expected = CreatePlan(release.Manifest, context, plan.Components.Select(item => item.ComponentId).ToHashSet(StringComparer.Ordinal));
        if (!expected.Components.Select(item => item.ComponentId).SequenceEqual(plan.Components.Select(item => item.ComponentId)) ||
            expected.DownloadBytes != plan.DownloadBytes || BootstrapPeak(expected.PeakBytes, release, plan.RequiredUpdaterVersion) != plan.PeakBytes ||
            !expected.Reused.SequenceEqual(plan.Reused) || !expected.Removed.SequenceEqual(plan.Removed))
            throw new UpdatePlanException("update.planStale");
        ValidateModuleApis(release, runtime, plan.Components);
        ValidatePlan(plan with { BaseFingerprint = Fingerprint(context) }, release.Manifest, context);
    }

    private void ValidateReleaseRuntime(VerifiedReleaseManifest release, ReleaseRuntimeContext runtime)
    {
        ValidateRuntimeMetadata(release, runtime);
        if (runtime.UpdaterVersion < release.MinimumUpdaterVersion) throw new UpdatePlanException("update.updaterRequired");
    }

    private void ValidateRuntimeMetadata(VerifiedReleaseManifest release, ReleaseRuntimeContext runtime)
    {
        if (runtime.UpdaterVersion is null || runtime.ModuleApi < 0 || runtime.RuntimeMode is not ("self-contained" or "framework-dependent"))
            throw new UpdatePlanException("update.manifestInvalid");
        if (timeProvider.GetUtcNow() >= release.AuthorizedUntil) throw new UpdatePlanException("update.metadataExpired");
        if (runtime.RuntimeMode != release.RuntimeMode) throw new UpdatePlanException("update.incompatibleComponents");
    }

    private static void RequireBootstrapUpdater(VerifiedReleaseManifest release, ReleaseRuntimeContext runtime)
    {
        if (runtime.UpdaterVersion >= release.MinimumUpdaterVersion ||
            !release.Components.TryGetValue("updater", out var updater) || updater.Metadata.IsOptional ||
            updater.Metadata.Dependencies.Count != 0 || updater.Metadata.Version < release.MinimumUpdaterVersion ||
            !updater.Artifacts.Files.Any(file => file.File.RelativePath == "MabiTool.Updater.dll" && file.File.Executable) ||
            release.RuntimeMode == "self-contained" && !updater.Artifacts.Files.Any(file => file.File.RelativePath == "MabiTool.Updater.exe" && file.File.Executable))
            throw new UpdatePlanException("update.updaterRequired");
    }

    private static long BootstrapPeak(long peak, VerifiedReleaseManifest release, Version? bootstrap) =>
        bootstrap is null ? peak : checked(peak + (long)Math.Ceiling(release.Components["updater"].Metadata.InstalledBytes * 1.2d));

    private static void ValidateModuleApis(VerifiedReleaseManifest release, ReleaseRuntimeContext runtime, IReadOnlyList<UpdateComponent> selected)
    {
        foreach (var component in selected)
        {
            var range = release.Components[component.ComponentId].ModuleApiRange;
            if (runtime.ModuleApi < range.Minimum || runtime.ModuleApi > range.Maximum)
                throw new UpdatePlanException("update.incompatibleComponents", component.ComponentId);
        }
    }

    private static string ReleaseFingerprint(string baseFingerprint, ReleaseRuntimeContext runtime) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{baseFingerprint}|{runtime.UpdaterVersion}|{runtime.ModuleApi}|{runtime.RuntimeMode}")))).ToLowerInvariant();

    private static string PlanFingerprint(string baseFingerprint, ReleaseRuntimeContext runtime, Version? bootstrap) =>
        bootstrap is null ? ReleaseFingerprint(baseFingerprint, runtime) :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{ReleaseFingerprint(baseFingerprint, runtime)}|updater-bootstrap|{bootstrap}"))).ToLowerInvariant();
}
