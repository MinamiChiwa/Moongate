using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class UpdateArtifactSelectorTests
{
    private static readonly string Base = new('a', 64);
    private static readonly string Target = new('b', 64);
    private static readonly string Sha = new('c', 64);

    [Fact]
    public void Uses_patch_only_for_exact_base_supported_algorithm_and_real_savings()
    {
        var choice = UpdateArtifactSelector.Select(Base, Target, 1000, 2000, Full(2000),
            new(Base, Target, "bsdiff-v1", 600, Sha), new HashSet<string>(["bsdiff-v1"]));
        Assert.Equal(UpdateArtifactKind.BinaryPatch, choice.Kind);
        Assert.Equal(600, choice.DownloadBytes);
    }

    [Fact]
    public void Falls_back_to_file_incremental_or_full_package_when_patch_is_unusable()
    {
        var file = UpdateArtifactSelector.Select(new('d', 64), Target, 1000, 2000, Full(2000),
            new(Base, Target, "unknown", 100, Sha), new HashSet<string>(["bsdiff-v1"]));
        Assert.Equal(UpdateArtifactKind.FileIncremental, file.Kind);
        var full = UpdateArtifactSelector.Select(Base, Target, 3000, 2000, Full(2000));
        Assert.Equal(UpdateArtifactKind.FullPackage, full.Kind);
    }

    [Fact]
    public void Rejects_insufficient_space_and_target_mismatch()
    {
        Assert.Throws<UpdatePlanException>(() => UpdateArtifactSelector.Select(Base, Target, 3000, 2000, Full(2000), availableBytes: 100));
        Assert.Throws<UpdatePlanException>(() => UpdateArtifactSelector.ValidateRebuiltTarget(Target, Base));
    }

    [Fact]
    public void Rejects_unrelated_full_package_and_incremental_over_space_budget()
    {
        Assert.Throws<UpdatePlanException>(() => UpdateArtifactSelector.Select(Base, Target, 1000, 2000, Full(2000) with { TargetManifestSha256 = Base }));
        Assert.Throws<UpdatePlanException>(() => UpdateArtifactSelector.Select(Base, Target, 1000, 2000, Full(2000), availableBytes: 999));
    }

    [Fact]
    public void Savings_comparison_does_not_overflow_and_rejects_bad_patch_hash()
    {
        var choice = UpdateArtifactSelector.Select(Base, Target, long.MaxValue, long.MaxValue, Full(long.MaxValue),
            new(Base, Target, "bsdiff-v1", long.MaxValue - 1, Sha), new HashSet<string>(["bsdiff-v1"]));
        Assert.Equal(UpdateArtifactKind.FileIncremental, choice.Kind);
        var bad = UpdateArtifactSelector.Select(Base, Target, 1000, 2000, Full(2000),
            new(Base, Target, "bsdiff-v1", 100, "invalid"), new HashSet<string>(["bsdiff-v1"]));
        Assert.Equal(UpdateArtifactKind.FileIncremental, bad.Kind);
    }

    private static FullPackageArtifact Full(long size) => new(Target, size, Sha);
}
