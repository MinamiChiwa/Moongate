using System.Text.Json;
using MabiTool.Infrastructure.Updates;
using Xunit;
using Fixture = MabiTool.Updates.Tests.SignedReleaseFixture;

namespace MabiTool.Updates.Tests;

public sealed class VerifiedReleasePlannerTests
{
    private static readonly UpdatePlannerContext Context = new(new(1, 0, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000, []);
    private static readonly ReleaseRuntimeContext Runtime = new(new(1, 0, 0), 1, "self-contained");

    [Theory]
    [InlineData("updater", "update.updaterRequired")]
    [InlineData("module", "update.incompatibleComponents")]
    [InlineData("runtime", "update.incompatibleComponents")]
    public void Signed_compatibility_fields_gate_planning(string field, string expected)
    {
        using var f = new Fixture();
        var release = Accept(f);
        var runtime = field switch
        {
            "updater" => Runtime with { UpdaterVersion = new(0, 0, 1) },
            "module" => Runtime with { ModuleApi = 2 },
            _ => Runtime with { RuntimeMode = "framework-dependent" }
        };
        Assert.Equal(expected, Assert.Throws<UpdatePlanException>(() => new UpdatePlanner(new Clock(f.Now)).CreatePlan(release, Context, runtime)).Code);
    }

    [Fact]
    public void Plan_expires_with_signed_authorization_and_roundtrips_for_revalidation()
    {
        using var f = new Fixture();
        var release = Accept(f);
        var clock = new Clock(f.Now);
        var planner = new UpdatePlanner(clock, TimeSpan.FromHours(2));
        var plan = planner.CreatePlan(release, Context, Runtime);
        Assert.Equal(release.AuthorizedUntil, plan.ExpiresAt);
        var saved = JsonSerializer.Deserialize<UpdatePlan>(JsonSerializer.Serialize(plan))!;
        planner.ValidatePlan(saved, release, Context, Runtime);
        clock.Now = release.AuthorizedUntil;
        Assert.Equal("update.metadataExpired", Assert.Throws<UpdatePlanException>(() => planner.ValidatePlan(saved, release, Context, Runtime)).Code);
        Assert.Equal("update.metadataExpired", Assert.Throws<UpdatePlanException>(() => planner.CreatePlan(release, Context, Runtime)).Code);
    }

    [Theory]
    [InlineData("updater")]
    [InlineData("module")]
    [InlineData("runtime")]
    public void Runtime_changes_invalidate_existing_plan(string field)
    {
        using var f = new Fixture();
        var release = Accept(f);
        var planner = new UpdatePlanner(new Clock(f.Now));
        var plan = planner.CreatePlan(release, Context, Runtime);
        var changed = field switch
        {
            "updater" => Runtime with { UpdaterVersion = new(2, 0, 0) },
            "module" => Runtime with { ModuleApi = 2 },
            _ => Runtime with { RuntimeMode = "framework-dependent" }
        };
        Assert.Throws<UpdatePlanException>(() => planner.ValidatePlan(plan, release, Context, changed));
    }

    [Theory]
    [InlineData("component")]
    [InlineData("budget")]
    [InlineData("missing-core")]
    [InlineData("expiry")]
    [InlineData("dependencies")]
    public void Persisted_plan_edits_cannot_change_signed_target_or_budget(string field)
    {
        using var f = new Fixture();
        var release = Accept(f);
        var planner = new UpdatePlanner(new Clock(f.Now));
        var plan = planner.CreatePlan(release, Context, Runtime);
        var changed = field switch
        {
            "component" => plan with { Components = [plan.Components[0] with { Sha256 = new('a', 64) }] },
            "budget" => plan with { DownloadBytes = 0 },
            "missing-core" => plan with { Components = [] },
            "dependencies" => plan with { Components = [plan.Components[0] with { Dependencies = [new("unknown", new(1, 0, 0))] }] },
            _ => plan with { ExpiresAt = release.AuthorizedUntil.AddMinutes(1) }
        };
        Assert.Equal("update.planStale", Assert.Throws<UpdatePlanException>(() => planner.ValidatePlan(changed, release, Context, Runtime)).Code);
    }

    [Fact]
    public void Explicit_optional_selection_keeps_mandatory_core_and_skips_unselected_incompatible_optional()
    {
        using var f = new Fixture();
        var node = f.Payload();
        var components = node["platforms"]![0]!["components"]!.AsArray();
        var optional = components[0]!.DeepClone();
        optional["componentId"] = "optional";
        optional["optional"] = true;
        foreach (var file in optional["files"]!.AsArray()) file!["componentId"] = "optional";
        optional["moduleApiRange"]!["maximum"] = 2;
        components.Add(optional);
        var incompatible = optional.DeepClone();
        incompatible["componentId"] = "unused";
        foreach (var file in incompatible["files"]!.AsArray()) file!["componentId"] = "unused";
        incompatible["moduleApiRange"]!["minimum"] = 2;
        components.Add(incompatible);
        var bytes = f.Release(node);
        var release = f.Store.VerifyReleaseManifest(bytes, f.AcceptIndex(bytes), "win-x64", f.Now);
        var planner = new UpdatePlanner(new Clock(f.Now));
        var plan = planner.CreatePlan(release, Context, Runtime, new HashSet<string> { "optional" });
        Assert.Equal(new[] { "core", "optional" }, plan.Components.Select(item => item.ComponentId));
        planner.ValidatePlan(plan, release, Context, Runtime);
        Assert.Throws<UpdatePlanException>(() => planner.CreatePlan(release, Context, Runtime, new HashSet<string> { "unused" }));
    }

    private static VerifiedReleaseManifest Accept(Fixture f)
    {
        var bytes = f.Release();
        return f.Store.VerifyReleaseManifest(bytes, f.AcceptIndex(bytes), "win-x64", f.Now);
    }

    [Fact]
    public void BootstrapPlanFreezesMinimumAndFullTargetButOrdinaryPlannerStillRejectsOldUpdater()
    {
        using var f = new Fixture(); var release = BootstrapRelease(f);
        var runtime = Runtime with { UpdaterVersion = new(0, 1, 0) }; var planner = new UpdatePlanner(new Clock(f.Now));
        Assert.Equal("update.updaterRequired", Assert.Throws<UpdatePlanException>(() => planner.CreatePlan(release, Context, runtime)).Code);
        var plan = planner.CreateUpdaterBootstrapPlan(release, Context, runtime);
        Assert.Equal(new Version(0, 2, 0), plan.RequiredUpdaterVersion);
        Assert.Equal(new[] { "core", "updater" }, plan.Components.Select(item => item.ComponentId));
        planner.ValidateUpdaterBootstrapPlan(plan, release, Context, runtime);
        Assert.Equal("update.updaterRequired", Assert.Throws<UpdatePlanException>(() => planner.ValidatePlan(plan, release, Context, runtime)).Code);
        var business = planner.CreatePlan(release, Context, runtime with { UpdaterVersion = new(0, 2, 0) });
        Assert.Null(business.RequiredUpdaterVersion); Assert.True(plan.PeakBytes > business.PeakBytes);
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("budget")]
    [InlineData("component")]
    [InlineData("runtime")]
    public void BootstrapPlanTamperingAndChangedBaselineAreRejected(string field)
    {
        using var f = new Fixture(); var release = BootstrapRelease(f);
        var runtime = Runtime with { UpdaterVersion = new(0, 1, 0) }; var planner = new UpdatePlanner(new Clock(f.Now));
        var plan = planner.CreateUpdaterBootstrapPlan(release, Context, runtime);
        var changed = field switch
        {
            "minimum" => plan with { RequiredUpdaterVersion = new(0, 1, 0) },
            "budget" => plan with { PeakBytes = plan.PeakBytes - 1 },
            "component" => plan with { Components = [plan.Components[0]] },
            _ => plan
        };
        Assert.Throws<UpdatePlanException>(() => planner.ValidateUpdaterBootstrapPlan(changed, release, Context,
            field == "runtime" ? runtime with { ModuleApi = 2 } : runtime));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("old")]
    [InlineData("dependency")]
    [InlineData("optional")]
    [InlineData("entry")]
    public void BootstrapRequiresStandaloneSignedUpdaterAtRequiredVersion(string field)
    {
        using var f = new Fixture(); var release = BootstrapRelease(f, field);
        Assert.Equal("update.updaterRequired", Assert.Throws<UpdatePlanException>(() => new UpdatePlanner(new Clock(f.Now))
            .CreateUpdaterBootstrapPlan(release, Context, Runtime with { UpdaterVersion = new(0, 1, 0) })).Code);
    }

    private static VerifiedReleaseManifest BootstrapRelease(Fixture f, string? mutation = null)
    {
        var payload = f.Payload(); payload["minUpdaterVersion"] = "0.2.0";
        var components = payload["platforms"]![0]!["components"]!.AsArray();
        if (mutation != "missing")
        {
            var updater = components[0]!.DeepClone(); updater["componentId"] = "updater";
            updater["version"] = mutation == "old" ? "0.1.0" : "0.2.0"; updater["optional"] = mutation == "optional";
            var files = updater["files"]!.AsArray();
            for (var i = 0; i < files.Count; i++)
            { files[i]!["componentId"] = "updater"; files[i]!["path"] = i == 0 ? mutation == "entry" ? "unrecognized.dll" : "MabiTool.Updater.dll" : "MabiTool.Updater.exe"; }
            updater["targetTreeHash"] = FileTreePlanner.TreeHash(files.Select(file => new ManifestFile(file!["path"]!.GetValue<string>(),
                file["size"]!.GetValue<long>(), file["sha256"]!.GetValue<string>())).ToArray());
            if (mutation == "dependency") updater["dependencies"] = JsonSerializer.SerializeToNode(new[] { new { componentId = "core", minimumVersion = "1.0.0" } });
            components.Add(updater);
        }
        var bytes = f.Release(payload); return f.Store.VerifyReleaseManifest(bytes, f.AcceptIndex(bytes), "win-x64", f.Now);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
