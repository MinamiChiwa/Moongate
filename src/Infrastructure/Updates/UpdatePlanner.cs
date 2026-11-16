using System.Security.Cryptography;
using System.Text;

namespace MabiTool.Infrastructure.Updates;

public sealed class UpdatePlanException(string code, string? componentId = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? ComponentId { get; } = componentId;
}

public sealed partial class UpdatePlanner
{
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan planLifetime;

    public UpdatePlanner(TimeProvider? timeProvider = null, TimeSpan? planLifetime = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.planLifetime = planLifetime ?? TimeSpan.FromHours(1);
        if (this.planLifetime <= TimeSpan.Zero || this.planLifetime > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(planLifetime));
    }

    public UpdatePlan CreatePlan(UpdateManifest manifest, UpdatePlannerContext context,
        IReadOnlySet<string>? requestedComponents = null)
    {
        ValidateManifest(manifest);
        ValidateContext(context);
        var components = manifest.Components.ToDictionary(component => component.ComponentId, StringComparer.Ordinal);
        var defaultRoots = components.Values.Where(component => !component.IsOptional).Select(component => component.ComponentId)
            .Concat(context.InstalledComponents.Select(component => component.ComponentId).Where(components.ContainsKey)).ToHashSet(StringComparer.Ordinal);
        var roots = requestedComponents is null || requestedComponents.Count == 0 ? defaultRoots :
            requestedComponents.Concat(components.Values.Where(component => !component.IsOptional).Select(component => component.ComponentId)).ToHashSet(StringComparer.Ordinal);
        var selected = ResolveClosure(components, roots);
        if (selected.Count == 0) throw new UpdatePlanException("update.incompatibleComponents");
        foreach (var component in selected.Values) ValidateCompatibility(component, context);
        if (!string.Equals(manifest.Platform, context.Platform, StringComparison.OrdinalIgnoreCase))
            throw new UpdatePlanException("update.incompatibleComponents");

        var installed = context.InstalledComponents.ToDictionary(component => component.ComponentId, StringComparer.Ordinal);
        var reused = selected.Values.Where(component => installed.TryGetValue(component.ComponentId, out var current) &&
            current.Version == component.Version && string.Equals(current.Sha256, component.Sha256, StringComparison.OrdinalIgnoreCase))
            .Select(component => installed[component.ComponentId]).OrderBy(component => component.ComponentId, StringComparer.Ordinal).ToArray();
        var selectedIds = selected.Keys.ToHashSet(StringComparer.Ordinal);
        var removed = installed.Values.Where(component => !selectedIds.Contains(component.ComponentId)).ToArray();
        var downloadBytes = selected.Values.Where(component => !reused.Any(item => item.ComponentId == component.ComponentId))
            .Sum(component => component.DownloadBytes);
        var installBytes = selected.Values.Sum(component => component.InstalledBytes);
        if (context.DatabaseBackupBytes < 0) throw new UpdatePlanException("update.manifestInvalid");
        var peakBytes = EstimatePeakBytes(downloadBytes, installBytes, context.DatabaseBackupBytes);
        if (peakBytes > context.AvailableBytes) throw new UpdatePlanException("update.diskSpaceInsufficient");

        var now = timeProvider.GetUtcNow();
        var fingerprint = Fingerprint(context);
        return new UpdatePlan(Guid.NewGuid().ToString("N"), manifest.ManifestSha256, manifest.ReleaseId,
            manifest.ReleaseSequence, selected.Values.OrderBy(component => component.ComponentId, StringComparer.Ordinal).ToArray(),
            reused, removed, downloadBytes, peakBytes, now, now + planLifetime, fingerprint);
    }

    public void ValidatePlan(UpdatePlan plan, UpdateManifest currentManifest, UpdatePlannerContext currentContext)
    {
        ValidateContext(currentContext);
        if (timeProvider.GetUtcNow() >= plan.ExpiresAt || !string.Equals(plan.ManifestSha256, currentManifest.ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ReleaseId, currentManifest.ReleaseId, StringComparison.Ordinal) ||
            plan.ReleaseSequence != currentManifest.ReleaseSequence ||
            !string.Equals(plan.BaseFingerprint, Fingerprint(currentContext), StringComparison.Ordinal) ||
            plan.PeakBytes > currentContext.AvailableBytes ||
            EstimatePeakBytes(plan.DownloadBytes, plan.Components.Sum(component => component.InstalledBytes), currentContext.DatabaseBackupBytes) > currentContext.AvailableBytes)
            throw new UpdatePlanException("update.planStale");
        foreach (var component in plan.Components) ValidateCompatibility(component, currentContext);
        if (!string.Equals(currentManifest.Platform, currentContext.Platform, StringComparison.OrdinalIgnoreCase))
            throw new UpdatePlanException("update.planStale");
    }

    private static Dictionary<string, UpdateComponent> ResolveClosure(Dictionary<string, UpdateComponent> components,
        IReadOnlySet<string>? requested)
    {
        var roots = requested ?? components.Keys.ToHashSet(StringComparer.Ordinal);
        var selected = new Dictionary<string, UpdateComponent>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots) Visit(root);
        return selected;

        void Visit(string id)
        {
            if (selected.ContainsKey(id)) return;
            if (!components.TryGetValue(id, out var component)) throw new UpdatePlanException("update.incompatibleComponents", id);
            if (!visiting.Add(id)) throw new UpdatePlanException("update.dependencyCycle", id);
            foreach (var dependency in component.Dependencies)
            {
                Visit(dependency.ComponentId);
                if (selected[dependency.ComponentId].Version < dependency.MinimumVersion)
                    throw new UpdatePlanException("update.incompatibleComponents", dependency.ComponentId);
            }
            visiting.Remove(id);
            selected[id] = component;
        }
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ReleaseId) || string.IsNullOrWhiteSpace(manifest.ProductId) ||
            string.IsNullOrWhiteSpace(manifest.Channel) || string.IsNullOrWhiteSpace(manifest.Platform) ||
            manifest.ReleaseSequence < 0 || manifest.Components.Count == 0 || manifest.ManifestSha256.Length != 64 ||
            !manifest.ManifestSha256.All(Uri.IsHexDigit) ||
            manifest.Components.Any(component => string.IsNullOrWhiteSpace(component.ComponentId) || component.DownloadBytes < 0 || component.InstalledBytes < 0 ||
                component.Sha256.Length != 64 || !component.Sha256.All(Uri.IsHexDigit)) ||
            manifest.Components.Select(component => component.ComponentId).Distinct(StringComparer.Ordinal).Count() != manifest.Components.Count)
            throw new UpdatePlanException("update.manifestInvalid");
    }

    private static void ValidateCompatibility(UpdateComponent component, UpdatePlannerContext context)
    {
        if (component.MinimumHostVersion is not null && context.HostVersion < ParseVersion(component.MinimumHostVersion) ||
            component.MaximumHostVersion is not null && context.HostVersion > ParseVersion(component.MaximumHostVersion) ||
            component.MinimumWorkerProtocol is not null && context.WorkerProtocol < component.MinimumWorkerProtocol ||
            component.MaximumWorkerProtocol is not null && context.WorkerProtocol > component.MaximumWorkerProtocol ||
            component.MinimumSchema is not null && context.DataSchema < component.MinimumSchema ||
            component.MaximumSchema is not null && context.DataSchema > component.MaximumSchema ||
            component.MinimumOsBuild is not null && context.OsBuild < ParseVersion(component.MinimumOsBuild))
            throw new UpdatePlanException("update.incompatibleComponents", component.ComponentId);
    }

    private static void ValidateContext(UpdatePlannerContext context)
    {
        if (context.AvailableBytes < 0 || context.DatabaseBackupBytes < 0 || context.WorkerProtocol < 0 || context.DataSchema < 0 ||
            string.IsNullOrWhiteSpace(context.Platform) || context.InstalledComponents.Any(component =>
                string.IsNullOrWhiteSpace(component.ComponentId) || component.InstalledBytes < 0 ||
                component.Sha256.Length != 64 || !component.Sha256.All(Uri.IsHexDigit)) ||
            context.InstalledComponents.Select(component => component.ComponentId).Distinct(StringComparer.Ordinal).Count() != context.InstalledComponents.Count)
            throw new UpdatePlanException("update.manifestInvalid");
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : throw new UpdatePlanException("update.manifestInvalid");

    private static string Fingerprint(UpdatePlannerContext context)
    {
        var installed = string.Join("|", context.InstalledComponents.OrderBy(item => item.ComponentId, StringComparer.Ordinal)
            .Select(item => $"{item.ComponentId}:{item.Version}:{item.Sha256}"));
        var text = $"{installed}|{context.HostVersion}|{context.WorkerProtocol}|{context.DataSchema}|{context.Platform}|{context.OsBuild}|{context.DatabaseBackupBytes}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static long EstimatePeakBytes(long downloadBytes, long installBytes, long databaseBackupBytes) =>
        checked((long)Math.Ceiling(checked(downloadBytes + installBytes + databaseBackupBytes) * 1.2d));
}
