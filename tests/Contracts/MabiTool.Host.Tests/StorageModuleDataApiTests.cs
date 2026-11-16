using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Color.Domain;
using MabiTool.Modules.Color.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class StorageModuleDataApiTests
{
    [Fact]
    public async Task DisabledModuleKeepsDataAcrossRestartAndExplicitDeletionAdvancesRevisions()
    {
        await using var host = await StorageApiTests.Host.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = timeout.Token;
        var profile = ColorProfiles.Default with { Id = "saved-profile", Name = "台服配色" };
        await owner.EditColorLibraryAsync(new([profile], [], null), "before-disable-save", ColorLibraryStore.ETag(new(0)), ct);
        await Enabled(owner, "color", false, ct);
        Assert.Equal(profile.Id, Assert.Single((await owner.GetColorLibraryAsync(ct)).Profiles).Id);
        await host.RestartAsync(ct); using var resumed = host.Client();
        Assert.False((await resumed.GetStorageModuleDataAsync("color", ct)).Enabled);
        await Enabled(resumed, "color", true, ct);
        Assert.Equal(profile.Id, Assert.Single((await resumed.GetColorLibraryAsync(ct)).Profiles).Id);
        await Enabled(resumed, "color", false, ct);
        string? etag = null;
        var plan = await resumed.PreviewStorageModuleDeletionAsync(new("color"), "clear-color-preview", ct, metadata => etag = metadata.ETag);
        Assert.Empty(plan.Data.BlockingReasons);
        var operation = await resumed.ExecuteStorageModuleDeletionAsync(plan.Id, "clear-color-execute", etag!, ct);
        Assert.Equal(OperationState.Succeeded, (await StorageApiTests.WaitForOperation(resumed, operation.Id, ct)).State.KnownValue);
        await Enabled(resumed, "color", true, ct);
        var empty = await resumed.GetColorLibraryAsync(ct); Assert.Empty(empty.Profiles); Assert.Equal(2UL, empty.Revision.Value);
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() => resumed.EditColorLibraryAsync(new([profile], [], null), "old-library-revision", ColorLibraryStore.ETag(new(1)), ct))).Problem!.Code);
        Assert.Equal(3UL, (await resumed.EditColorLibraryAsync(new([profile], [], null), "after-clear-save", ColorLibraryStore.ETag(empty.Revision), ct)).Revision.Value);
    }

    [Fact]
    public async Task OwnerScopeTwoClientsSseRestartAndIdempotencyRecoverTheSameDeletion()
    {
        await using var host = await StorageApiTests.Host.Start(); using var first = host.Client(); using var second = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = timeout.Token;
        var asset = await host.Services.GetRequiredService<AssetStore>().PutAsync("music-library", "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("saved-score")), cancellationToken: ct);
        await Enabled(first, "music-library", false, ct);
        var challenge = await first.CreatePairingChallengeAsync(new("storage-manager", null, [ApiPermissions.SystemRead, ApiPermissions.StorageManage]), ct);
        var grant = await first.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code), ct); using var paired = host.Client(grant.Token);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => paired.GetStorageModuleDataAsync("music-library", ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => paired.PreviewStorageModuleDeletionAsync(new("music-library"), "denied-preview", ct))).Status);
        string? etag = null;
        var plan = await first.PreviewStorageModuleDeletionAsync(new("music-library"), "http-delete-preview", ct, metadata => etag = metadata.ETag);
        Assert.Equal(plan.Id, (await second.PreviewStorageModuleDeletionAsync(new("music-library"), "http-delete-preview", ct)).Id);
        var filter = new EventStreamOptions(["storage.module-data.changed"], ["music-library"], null);
        var before = await second.GetEventSnapshotAsync(filter, ct);
        await using (var stream = second.StreamEventsAsync(filter with { LastEventId = before.EventCursor }, ct).GetAsyncEnumerator(ct))
        {
            var pending = stream.MoveNextAsync().AsTask();
            var started = await first.ExecuteStorageModuleDeletionAsync(plan.Id, "http-delete-execute", etag!, ct);
            var result = await StorageApiTests.WaitForOperation(second, started.Id, ct);
            Assert.Equal(OperationState.Succeeded, result.State.KnownValue); Assert.Equal(EffectStatus.None, result.EffectStatus.KnownValue);
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            var changed = stream.Current.Data.Deserialize<EventEnvelope<StorageModuleDataChanged>>(ContractJson.Options)!;
            Assert.Equal(started.Id, changed.Data.OperationId); Assert.Equal("music-library", changed.Data.Result.ModuleId);
            Assert.Equal(1UL, changed.Data.Result.RemovedAssets.Value);
            Assert.Equal(started.Id, (await second.ExecuteStorageModuleDeletionAsync(plan.Id, "http-delete-execute", etag!, ct)).Id);
        }
        var completedPlan = await second.GetStorageModuleDeletionPlanAsync(plan.Id, ct); Assert.Equal("completed", completedPlan.State);
        Assert.Null(host.Services.GetRequiredService<AssetStore>().GetMetadata(asset.Id));
        Assert.Empty((await paired.GetEventSnapshotAsync(filter, ct)).Resources);
        await host.RestartAsync(ct); using var restarted = host.Client();
        var same = await restarted.ExecuteStorageModuleDeletionAsync(plan.Id, "http-delete-execute", etag!, ct);
        Assert.Equal(OperationState.Succeeded, same.State.KnownValue);
        Assert.Equal(plan.Id, same.Result!.Value.Deserialize<StorageModuleDeletionResult>(ContractJson.Options)!.PlanId);
        var projection = await restarted.GetEventSnapshotAsync(filter, ct);
        Assert.Equal(same.Id, Assert.Single(projection.Resources).Data.GetProperty("operationId").GetString());
        Assert.Equal("storage.planStale", (await Assert.ThrowsAsync<MabiToolApiException>(() => restarted.ExecuteStorageModuleDeletionAsync(plan.Id, "new-delete-execute", StorageModuleDataStore.ETag(completedPlan), ct))).Problem!.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitingDeletionCanBeCancelledAndRechecksChangedContent(bool cancel)
    {
        await using var host = await StorageApiTests.Host.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = timeout.Token;
        await Enabled(owner, "color", false, ct); string? etag = null;
        var plan = await owner.PreviewStorageModuleDeletionAsync(new("color"), "wait-delete-preview", ct, metadata => etag = metadata.ETag);
        OperationSnapshot operation;
        using (host.Services.GetRequiredService<ActivityBarrier>().EnterActivity("fixture", "game-task"))
        {
            operation = await owner.ExecuteStorageModuleDeletionAsync(plan.Id, "wait-delete-execute", etag!, ct);
            while ((await owner.GetOperationAsync(operation.Id, ct)).Phase != "waitingForIdle") await Task.Delay(20, ct);
            if (cancel) await owner.CancelOperationAsync(operation.Id, new("cancel-before-delete"), ct);
            else await host.Services.GetRequiredService<AssetStore>().PutAsync("color", "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("new-color")), cancellationToken: ct);
        }
        var terminal = await StorageApiTests.WaitForOperation(owner, operation.Id, ct);
        Assert.Equal(cancel ? OperationState.Cancelled : OperationState.Failed, terminal.State.KnownValue);
        if (!cancel) Assert.Equal("storage.planStale", terminal.Problem!.Code);
        Assert.Equal("planned", (await owner.GetStorageModuleDeletionPlanAsync(plan.Id, ct)).State);
        Assert.False(host.Stopping.IsCancellationRequested);
    }

    internal static async Task Enabled(MabiToolClient owner, string id, bool enabled, CancellationToken ct)
    {
        var configuration = await owner.GetModuleConfigurationAsync(id, ct);
        var operation = await owner.ConfigureModuleAsync(id, new(enabled), Guid.NewGuid().ToString("N"), OperationStore.ConfigurationETag(configuration), ct);
        Assert.Equal(OperationState.Succeeded, (await StorageApiTests.WaitForOperation(owner, operation.Id, ct)).State.KnownValue);
    }
}
