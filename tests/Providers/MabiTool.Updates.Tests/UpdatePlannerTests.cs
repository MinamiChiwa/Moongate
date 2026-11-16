using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class UpdatePlannerTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    [Fact]
    public void Default_plan_keeps_installed_components_and_omits_uninstalled_optional_components()
    {
        var core = Component("core", "1.0", 100, 200, HashA);
        var optionalColor = Component("color-worker", "2.0", 500, 900, HashB, optional: true);
        var manifest = Manifest([core, optionalColor]);
        var context = Context([new("core", new(1, 0), 180, HashA)]);

        var plan = new UpdatePlanner().CreatePlan(manifest, context);

        Assert.Equal(new[] { "core" }, plan.Components.Select(component => component.ComponentId));
        Assert.Equal(0, plan.DownloadBytes);
        Assert.DoesNotContain(plan.Components, component => component.ComponentId == "color-worker");
    }

    [Fact]
    public void Requested_component_includes_exact_dependency_closure_and_estimates_peak_space()
    {
        var core = Component("core", "1.2", 100, 200, HashB);
        var feature = Component("feature", "3.1", 50, 80, HashA, [new("core", new(1, 0))]);
        var plan = new UpdatePlanner().CreatePlan(Manifest([core, feature]), Context([], available: 1000, backup: 100),
            new HashSet<string>(["feature"], StringComparer.Ordinal));

        Assert.Equal(new[] { "core", "feature" }, plan.Components.Select(component => component.ComponentId));
        Assert.Equal(150, plan.DownloadBytes);
        Assert.Equal(636, plan.PeakBytes);
    }

    [Fact]
    public void Rejects_missing_dependency_version_cycle_platform_schema_and_insufficient_space()
    {
        var missing = Component("feature", "1.0", 1, 1, HashA, [new("absent", new(1, 0))]);
        AssertPlanCode("update.incompatibleComponents", () => new UpdatePlanner().CreatePlan(Manifest([missing]), Context([])));

        var oldDependency = Component("core", "1.0", 1, 1, HashA);
        var feature = Component("feature", "1.0", 1, 1, HashB, [new("core", new(2, 0))]);
        AssertPlanCode("update.incompatibleComponents", () => new UpdatePlanner().CreatePlan(Manifest([oldDependency, feature]), Context([]), Set("feature")));

        var cycleA = Component("a", "1.0", 1, 1, HashA, [new("b", new(1, 0))]);
        var cycleB = Component("b", "1.0", 1, 1, HashB, [new("a", new(1, 0))]);
        AssertPlanCode("update.dependencyCycle", () => new UpdatePlanner().CreatePlan(Manifest([cycleA, cycleB]), Context([]), Set("a")));

        var incompatible = Component("core", "1.0", 1, 1, HashA, minimumSchema: 3);
        AssertPlanCode("update.incompatibleComponents", () => new UpdatePlanner().CreatePlan(Manifest([incompatible]), Context([])));
        AssertPlanCode("update.incompatibleComponents", () => new UpdatePlanner().CreatePlan(Manifest([Component("core", "1.0", 1, 1, HashA)]), Context([], platform: "linux-x64")));
        AssertPlanCode("update.diskSpaceInsufficient", () => new UpdatePlanner().CreatePlan(Manifest([Component("core", "1.0", 500, 500, HashA)]), Context([], available: 100)));
    }

    [Fact]
    public void Plan_becomes_stale_when_manifest_base_or_compatibility_context_changes_or_expires()
    {
        var clock = new ManualClock();
        var planner = new UpdatePlanner(clock, TimeSpan.FromMinutes(10));
        var manifest = Manifest([Component("core", "1.0", 100, 200, HashA)]);
        var context = Context([]);
        var plan = planner.CreatePlan(manifest, context);

        planner.ValidatePlan(plan, manifest, context);
        AssertPlanCode("update.planStale", () => planner.ValidatePlan(plan with { ManifestSha256 = HashA }, manifest, context));
        AssertPlanCode("update.planStale", () => planner.ValidatePlan(plan, manifest, Context([new("core", new(0, 9), 100, HashB)])));
        AssertPlanCode("update.planStale", () => planner.ValidatePlan(plan, manifest, context with { WorkerProtocol = 3 }));
        AssertPlanCode("update.planStale", () => planner.ValidatePlan(plan, manifest, context with { AvailableBytes = plan.PeakBytes - 1 }));
        clock.Advance(TimeSpan.FromMinutes(11));
        AssertPlanCode("update.planStale", () => planner.ValidatePlan(plan, manifest, context));
    }

    [Fact]
    public void Reuses_only_exact_version_and_hash_and_removes_unselected_installed_components()
    {
        var core = Component("core", "2.0", 100, 200, HashA);
        var manifest = Manifest([core]);
        var context = Context([
            new("core", new(1, 0), 150, HashA),
            new("color-worker", new(1, 0), 800, HashB)
        ]);

        var plan = new UpdatePlanner().CreatePlan(manifest, context);

        Assert.Equal(100, plan.DownloadBytes);
        Assert.Contains(plan.Removed, item => item.ComponentId == "color-worker");
        Assert.Empty(plan.Reused);
    }

    private static UpdateComponent Component(string id, string version, long download, long installed, string hash,
        IReadOnlyList<UpdateDependency>? dependencies = null, bool optional = false, int? minimumSchema = null) =>
        new(id, Version.Parse(version), dependencies ?? [], download, installed, hash,
            MinimumHostVersion: "1.0", MaximumHostVersion: "2.0", MinimumWorkerProtocol: 1,
            MaximumWorkerProtocol: 3, MinimumSchema: minimumSchema, MaximumSchema: 5, MinimumOsBuild: "10.0.19041", IsOptional: optional);

    private static UpdateManifest Manifest(IReadOnlyList<UpdateComponent> components, string? platform = null, string? hash = null) =>
        new("release-1", 1, "mabitool", "stable", platform ?? "win-x64", components, hash ?? HashB);

    private static UpdatePlannerContext Context(IReadOnlyList<InstalledUpdateComponent> installed, long available = 10_000,
        long backup = 0, string platform = "win-x64") =>
        new(new(1, 5), 2, 2, platform, new(10, 0, 22621), available, installed, backup);

    private static HashSet<string> Set(string value) => new([value], StringComparer.Ordinal);

    private static void AssertPlanCode(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<UpdatePlanException>(action).Code);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan value) => utcNow += value;
    }
}
