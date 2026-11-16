using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class StorageMaintenanceTests
{
    [Fact]
    public async Task NewReservationProtectsPreviewedCachesAndUserAssetsRemainReadable()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control();
        var first = work.Cache("first.cache", "first-cache"); var second = work.Cache("second.cache", "second-cache");
        var user = await work.Assets.PutAsync("music-library", "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("user-content")));
        var plan = await control.PreviewAsync("owner", new(["cache"]), "cache-preview-001");
        Assert.Equal(2, plan.Candidates.Count); Assert.DoesNotContain(work.Paths.DataRoot, Json(plan));
        using (work.Space.Reserve("download", new VolumeSpaceRequirement(work.Paths.Cache, 1)))
        {
            var result = await control.ExecuteAsync(plan.Id, "owner", "cache-execute-001", StorageControlStore.ETag(plan));
            Assert.Equal(0UL, result.ReclaimedBytes.Value); Assert.All(result.Items, x => Assert.Equal("activeReservation", x.Reason));
            Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
        }
        var next = await control.PreviewAsync("owner", new(["cache"]), "cache-preview-002");
        var cleaned = await control.ExecuteAsync(next.Id, "owner", "cache-execute-002", StorageControlStore.ETag(next));
        Assert.Equal("completed", cleaned.State); Assert.Equal(23UL, cleaned.ReclaimedBytes.Value);
        Assert.False(File.Exists(first)); Assert.False(File.Exists(second)); Assert.Equal(user, work.Assets.Get(user.Id));
    }

    [Fact]
    public async Task SameLengthReplacementWithPreservedTimestampIsNotDeleted()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control();
        var changed = work.Cache("changed.cache", "original"); var other = work.Cache("other.cache", "unchanged");
        var plan = await control.PreviewAsync("owner", new(["cache"]), "hash-preview-001");
        var timestamp = File.GetLastWriteTimeUtc(changed); File.WriteAllText(changed, "modified"); File.SetLastWriteTimeUtc(changed, timestamp);
        var result = await control.ExecuteAsync(plan.Id, "owner", "hash-execute-001", StorageControlStore.ETag(plan));
        Assert.Contains(result.Items, x => x.Reason == "changed"); Assert.Equal("modified", File.ReadAllText(changed));
        Assert.False(File.Exists(other)); Assert.Equal(9UL, result.ReclaimedBytes.Value);
    }

    [Fact]
    public async Task TemporaryRetentionStartsWhenTheTaskTerminates()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control();
        var path = work.Paths.Resolve("temp", "owned.part");
        var task = await work.Database.TemporaryFiles.TrackAsync("files", path); await File.WriteAllTextAsync(path, "temporary");
        work.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Empty((await control.PreviewAsync("owner", new(["temporary"]), "temporary-active")).Candidates);
        await task!.DisposeAsync(); work.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Empty((await control.PreviewAsync("owner", new(["temporary"]), "temporary-recent")).Candidates);
        work.Clock.Advance(TimeSpan.FromHours(1));
        var eligible = await control.PreviewAsync("owner", new(["temporary"]), "temporary-aged"); Assert.Single(eligible.Candidates);
        var result = await control.ExecuteAsync(eligible.Id, "owner", "temporary-delete", StorageControlStore.ETag(eligible));
        Assert.Equal(9UL, result.ReclaimedBytes.Value); Assert.False(File.Exists(path));
        Assert.Equal(0L, work.Database.ReadScalar("SELECT count(*) FROM platform_temporary_files"));
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task MissingCompletedRegistrationsAreReclaimedAtCapacityAndActiveTasksRemainProtected()
    {
        await using var work = new StorageTestWorkspace();
        var activePath = work.Paths.Resolve("temp", "active.part");
        await using var active = await work.Database.TemporaryFiles.TrackAsync("files", activePath);
        for (var first = 1; first <= 9999; first += 100)
            await work.Database.WriteAsync(tx => tx.Execute("""
                WITH RECURSIVE n(value) AS (SELECT $first UNION ALL SELECT value+1 FROM n WHERE value<$last)
                INSERT INTO platform_temporary_files SELECT $prefix || value || '.part','files',$epoch,'completed',1 FROM n
                """, ("$first", first), ("$last", Math.Min(9999, first + 99)),
                ("$prefix", Path.Combine(work.Paths.Temporary, "missing-")), ("$epoch", work.Database.RuntimeId)));
        Assert.Equal((long)TemporaryStorageRegistry.MaximumFiles, work.Database.ReadScalar("SELECT count(*) FROM platform_temporary_files"));
        var nextPath = work.Paths.Resolve("temp", "next.part");
        await using var next = await work.Database.TemporaryFiles.TrackAsync("files", nextPath);
        Assert.Equal(2L, work.Database.ReadScalar("SELECT count(*) FROM platform_temporary_files"));
        Assert.Equal("activeTask", work.Database.TemporaryFiles.State(activePath).Protection);
    }

    [Fact]
    public async Task RecoveryForgetsRemovedCompletedFilesAndRetainsExistingAbandonedFiles()
    {
        await using var work = new StorageTestWorkspace();
        var completedPath = work.Paths.Resolve("temp", "completed.part");
        var completed = await work.Database.TemporaryFiles.TrackAsync("files", completedPath);
        await File.WriteAllTextAsync(completedPath, "completed"); await completed!.DisposeAsync(); File.Delete(completedPath);
        var abandonedPath = work.Paths.Resolve("temp", "abandoned.part");
        await work.Database.TemporaryFiles.TrackAsync("files", abandonedPath); await File.WriteAllTextAsync(abandonedPath, "abandoned");
        await work.RestartAsync();
        Assert.Equal(1L, work.Database.ReadScalar("SELECT count(*) FROM platform_temporary_files"));
        Assert.Equal("temporaryRetention", work.Database.TemporaryFiles.State(abandonedPath).Protection);
        work.Clock.Advance(TimeSpan.FromHours(24)); Assert.True(work.Database.TemporaryFiles.State(abandonedPath).Eligible);
    }

    [Fact]
    public async Task UnregisteredTemporaryFilesRemainProtected()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control();
        var path = work.Paths.Resolve("temp", "unknown.part"); await File.WriteAllTextAsync(path, "unknown");
        File.SetLastWriteTimeUtc(path, work.Clock.GetUtcNow().AddDays(-30).UtcDateTime);
        var plan = await control.PreviewAsync("owner", new(["temporary"]), "unknown-preview"); Assert.Empty(plan.Candidates);
        Assert.Contains((await control.UsageAsync()).Categories, x => x.Category == "temporary" && x.ProtectionReasons.Contains("unregisteredTemporary"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task MonotonicExpiryRejectsAPlanAfterWallClockRollback()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control(); var path = work.Cache("retained.cache", "retained");
        var plan = await control.PreviewAsync("owner", new(["cache"]), "expiry-preview");
        work.Clock.Advance(TimeSpan.FromMinutes(16)); work.Clock.SetUtc(plan.CreatedAt.AddDays(-1));
        var error = await Assert.ThrowsAsync<OperationException>(() => control.ExecuteAsync(plan.Id, "owner", "expiry-execute", StorageControlStore.ETag(plan)));
        Assert.Equal("storage.planExpired", error.Code); Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("other-owner", "storage-test", "resource.notFound")]
    [InlineData("owner", "different-epoch", "storage.planExpired")]
    public async Task PlansAreBoundToTheirPrincipalAndHostEpoch(string owner, string epoch, string expected)
    {
        await using var work = new StorageTestWorkspace(); var original = work.Control(); var path = work.Cache("scope.cache", "scope");
        var plan = await original.PreviewAsync("owner", new(["cache"]), "scope-preview");
        var error = await Assert.ThrowsAsync<OperationException>(() => work.Control(epoch).ExecuteAsync(plan.Id, owner, "scope-execute", StorageControlStore.ETag(plan)));
        Assert.Equal(expected, error.Code); Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RevisionAndIdempotencyConflictsDoNotDeleteFiles()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control(); var path = work.Cache("revision.cache", "revision");
        var plan = await control.PreviewAsync("owner", new(["cache"]), "revision-preview");
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() => control.PreviewAsync("owner", new(["temporary"]), "revision-preview"))).Code);
        Assert.Equal("revision.required", (await Assert.ThrowsAsync<OperationException>(() => control.ExecuteAsync(plan.Id, "owner", "revision-execute", null))).Code);
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() => control.ExecuteAsync(plan.Id, "owner", "revision-execute", "\"old\""))).Code);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CompletedReceiptSurvivesRestartAndPlanExpiry()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control(); work.Cache("receipt.cache", "receipt");
        var plan = await control.PreviewAsync("owner", new(["cache"]), "receipt-preview");
        var result = await control.ExecuteAsync(plan.Id, "owner", "receipt-execute", StorageControlStore.ETag(plan));
        work.Clock.Advance(TimeSpan.FromMinutes(20)); await work.RestartAsync(); var next = work.Control("new-epoch"); await next.RecoverAsync();
        Assert.Equal(Json(result), Json(await next.ExecuteAsync(plan.Id, "owner", "receipt-execute", StorageControlStore.ETag(plan))));
        Assert.Equal(Json(result), Json(next.Result(plan.Id, "owner")));
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() => next.ExecuteAsync(plan.Id, "owner", "receipt-execute", "\"other\""))).Code);
    }

    [Fact]
    public async Task ActiveBusinessTaskBlocksExecutionAndTheSameRequestCanRetry()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control(); var path = work.Cache("activity.cache", "activity");
        var plan = await control.PreviewAsync("owner", new(["cache"]), "activity-preview");
        using (work.Database.Activities.EnterActivity("music", "performance"))
            Assert.Equal("resource.busy", (await Assert.ThrowsAsync<OperationException>(() => control.ExecuteAsync(plan.Id, "owner", "activity-execute", StorageControlStore.ETag(plan)))).Code);
        Assert.True(File.Exists(path));
        Assert.Equal("completed", (await control.ExecuteAsync(plan.Id, "owner", "activity-execute", StorageControlStore.ETag(plan))).State);
    }

    [Fact]
    public async Task InterruptedDeletionReturnsUnknownAndDoesNotReplayRemainingWork()
    {
        await using var work = new StorageTestWorkspace(); work.Cache("first.cache", "first"); var remaining = work.Cache("second.cache", "second");
        var fault = new AfterDelete(work.Files, () => throw new InvalidOperationException("process boundary")); var control = work.Control("original", fault);
        var plan = await control.PreviewAsync("owner", new(["cache"]), "interrupted-preview");
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ExecuteAsync(plan.Id, "owner", "interrupted-execute", StorageControlStore.ETag(plan)));
        var next = work.Control("restarted", work.Files); await next.RecoverAsync();
        var result = await next.ExecuteAsync(plan.Id, "owner", "interrupted-execute", StorageControlStore.ETag(plan));
        Assert.Equal("interrupted", result.State); Assert.Contains(result.Items, x => x.State == "unknown");
        Assert.Equal(0UL, result.ReclaimedBytes.Value); Assert.True(File.Exists(remaining));
    }

    [Fact]
    public async Task CancellationStopsLaterDeletionsAndPersistsTheCompletedPart()
    {
        await using var work = new StorageTestWorkspace(); work.Cache("first.cache", "first"); var remaining = work.Cache("second.cache", "second");
        using var cancellation = new CancellationTokenSource(); var control = work.Control("cancel", new AfterDelete(work.Files, cancellation.Cancel));
        var plan = await control.PreviewAsync("owner", new(["cache"]), "cancel-preview");
        var result = await control.ExecuteAsync(plan.Id, "owner", "cancel-execute", StorageControlStore.ETag(plan), cancellation.Token);
        Assert.Equal("cancelled", result.State); Assert.Equal(5UL, result.ReclaimedBytes.Value); Assert.True(File.Exists(remaining));
        Assert.Equal(Json(result), Json(control.Result(plan.Id, "owner"))); Assert.Empty(work.Database.Activities.Snapshot().Activities);
    }

    [Fact]
    public async Task ProtectedQuotaIsReportedAndAUserCategoryCannotBeCleaned()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control(); work.Cache("protected.cache", "protected");
        using var reservation = work.Space.Reserve("download", new VolumeSpaceRequirement(work.Paths.Cache, 1));
        var usage = await control.UsageAsync(); var cache = Assert.Single(usage.Categories, x => x.Category == "cache");
        Assert.True(cache.OverBudget); Assert.Equal(cache.Bytes, cache.ProtectedBytes);
        var plan = await control.PreviewAsync("owner", new(["cache"]), "protected-preview"); Assert.Empty(plan.Candidates);
        Assert.Contains("cache:protectedBudget", plan.BlockingReasons);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => control.PreviewAsync("owner", new(["user"]), "user-preview"))).Code);
    }

    [Fact]
    public async Task AnOrphanThatGetsANewReferenceIsProtectedAtExecution()
    {
        await using var work = new StorageTestWorkspace(); var control = work.Control(); var bytes = Encoding.UTF8.GetBytes("shared-content");
        var first = await work.Assets.PutAsync("color", "old", "text/plain", new MemoryStream(bytes)); await work.Assets.RemoveAsync(first.Id);
        Assert.Empty((await control.PreviewAsync("owner", new(["orphans"]), "orphan-recent")).Candidates);
        work.Clock.Advance(TimeSpan.FromHours(25));
        var plan = await control.PreviewAsync("owner", new(["orphans"]), "orphan-preview"); Assert.Single(plan.Candidates);
        var second = await work.Assets.PutAsync("music-library", "new", "text/plain", new MemoryStream(bytes));
        var result = await control.ExecuteAsync(plan.Id, "owner", "orphan-execute", StorageControlStore.ETag(plan));
        Assert.Equal(0UL, result.ReclaimedBytes.Value); Assert.Equal("businessReference", Assert.Single(result.Items).Reason);
        Assert.Equal(second, work.Assets.Get(second.Id));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private sealed class AfterDelete(IStorageMaintenanceParticipant inner, Action after) : IStorageMaintenanceParticipant
    {
        public string Id => inner.Id;
        public Task<IReadOnlyList<StorageManagedItem>> InspectAsync(CancellationToken ct = default) => inner.InspectAsync(ct);
        public Task<StorageManagedItem> FreezeAsync(StorageManagedItem item, CancellationToken ct = default) => inner.FreezeAsync(item, ct);
        public async Task<StorageDeletion> DeleteAsync(StorageManagedItem item, ActivityBarrier.MaintenanceLease maintenance, CancellationToken ct = default)
        { var result = await inner.DeleteAsync(item, maintenance, ct); after(); return result; }
    }
}
