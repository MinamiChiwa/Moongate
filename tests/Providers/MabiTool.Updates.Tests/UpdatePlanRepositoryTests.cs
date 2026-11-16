using System.Text.Json;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class UpdatePlanRepositoryTests
{
    [Fact]
    public async Task Roundtrip_preserves_signed_bytes_and_rejects_overwrite()
    {
        using var f = new SignedReleaseFixture();
        var saved = Document(f);
        var repository = new UpdatePlanRepository(f.Path("state"));
        await repository.SaveAsync(saved);
        var read = await repository.ReadAsync(saved.Plan.PlanId);
        Assert.Equal(saved.SignedIndex, read.SignedIndex);
        Assert.Equal(saved.SignedManifest, read.SignedManifest);
        Assert.Equal(saved.Plan.PlanId, read.Plan.PlanId);
        await Assert.ThrowsAsync<IOException>(() => repository.SaveAsync(saved with { SignedIndex = [1] }));
        Assert.Equal(saved.SignedIndex, (await repository.ReadAsync(saved.Plan.PlanId)).SignedIndex);
        Assert.Empty(Directory.GetFiles(f.Path("state/plans"), "*.tmp-*"));
    }

    [Fact]
    public async Task Cancellation_leaves_no_published_plan_or_temporary_file()
    {
        using var f = new SignedReleaseFixture();
        var repository = new UpdatePlanRepository(f.Path("state"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SaveAsync(Document(f), cancelled.Token));
        Assert.Empty(Directory.GetFiles(f.Path("state/plans"), "*.json"));
        Assert.Empty(Directory.GetFiles(f.Path("state/plans"), "*.tmp-*"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("")]
    public async Task Invalid_identifiers_cannot_address_external_files(string id)
    {
        using var f = new SignedReleaseFixture();
        var repository = new UpdatePlanRepository(f.Path("state"));
        Assert.Equal("update.planInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => repository.ReadAsync(id))).Code);
        var saved = Document(f);
        Assert.Equal("update.planInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => repository.SaveAsync(saved with { Plan = saved.Plan with { PlanId = id } }))).Code);
    }

    [Fact]
    public async Task Malformed_or_oversized_material_is_rejected_before_publication()
    {
        using var f = new SignedReleaseFixture();
        var repository = new UpdatePlanRepository(f.Path("state"));
        var saved = Document(f);
        foreach (var bad in new[] { saved with { SchemaVersion = 2 }, saved with { Plan = null! },
            saved with { SignedIndex = [] }, saved with { SignedIndex = new byte[1_048_577] },
            saved with { SignedManifest = new byte[12 * 1024 * 1024 + 1] } })
            Assert.Equal("update.planInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => repository.SaveAsync(bad))).Code);
        Assert.False(Directory.Exists(f.Path("state/plans")));
        Directory.CreateDirectory(f.Path("state/plans"));
        var path = f.Path("state/plans/" + saved.Plan.PlanId + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(saved with { Plan = saved.Plan with { Components = null! } }, SignedReleaseFixture.Options));
        Assert.Equal("update.planInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => repository.ReadAsync(saved.Plan.PlanId))).Code);
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"unexpected\":true}");
        Assert.Equal("update.planInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => repository.ReadAsync(saved.Plan.PlanId))).Code);
        await using (var stream = File.Create(path)) stream.SetLength(20 * 1024 * 1024 + 1);
        Assert.Equal("update.planTooLarge", (await Assert.ThrowsAsync<UpdatePlanException>(() => repository.ReadAsync(saved.Plan.PlanId))).Code);
    }

    [Fact]
    public async Task Capacity_rejection_preserves_existing_plans()
    {
        using var f = new SignedReleaseFixture();
        Directory.CreateDirectory(f.Path("state/plans"));
        for (var i = 0; i < 128; i++) await File.WriteAllTextAsync(f.Path($"state/plans/{i:x32}.json"), "preserved");
        var repository = new UpdatePlanRepository(f.Path("state"));
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<UpdatePlanException>(() => repository.SaveAsync(Document(f)))).Code);
        Assert.Equal(128, Directory.GetFiles(f.Path("state/plans"), "*.json").Length);
        Assert.All(Directory.GetFiles(f.Path("state/plans"), "*.json"), path => Assert.Equal("preserved", File.ReadAllText(path)));
    }

    private static StoredUpdatePlan Document(SignedReleaseFixture f)
    {
        var manifest = f.Release();
        var index = f.Index(manifest);
        var verified = f.Store.VerifyChannelIndex(index, "mabitool", "stable", f.Now);
        var release = f.Store.VerifyReleaseManifest(manifest, verified, "win-x64", f.Now);
        var plan = new UpdatePlanner(new Clock(f.Now)).CreatePlan(release,
            new(new(1, 0, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000, []), new(new(1, 0, 0), 1, "self-contained"));
        return new(1, plan, JsonSerializer.SerializeToUtf8Bytes(index, SignedReleaseFixture.Options), manifest);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
