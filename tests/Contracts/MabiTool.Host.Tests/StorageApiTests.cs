using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Host.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class StorageApiTests
{
    [Fact]
    public async Task BusyCleanupRetriesDoNotCreateNewDiagnosticWrites()
    {
        await using var host = await Host.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        string? etag = null;
        var plan = await owner.PreviewStorageCleanupAsync(new(["cache"]), "busy-cleanup-preview", ct, metadata => etag = metadata.ETag);
        var recorder = host.Services.GetRequiredService<DiagnosticRecorder>(); var diagnostics = host.Services.GetRequiredService<DiagnosticStore>();
        await recorder.FlushAsync(ct); var before = diagnostics.Sessions().Sum(x => (long)x.Records.Value);
        using (host.Services.GetRequiredService<ActivityBarrier>().EnterActivity("fixture", "running-task"))
        {
            for (var i = 0; i < 10; i++)
                Assert.Equal("resource.busy", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.ExecuteStorageCleanupAsync(plan.Id, "busy-cleanup-execute", etag!, ct))).Problem!.Code);
            await recorder.FlushAsync(ct);
            Assert.Equal(before, diagnostics.Sessions().Sum(x => (long)x.Records.Value));
        }
        Assert.Equal("completed", (await Execute(owner, plan.Id, etag!, ct)).State);
    }

    [Fact]
    public async Task StorageCapacityAndHardLimitFailuresKeepRegisteredRetryableCodesOverHttp()
    {
        await using var host = await Host.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = timeout.Token;
        var store = host.Services.GetRequiredService<SqliteStore>();
        static void AssertProblem(MabiToolApiException error, string code)
        {
            Assert.Equal(503, error.Status); Assert.Equal(code, error.Problem!.Code); Assert.True(error.Problem.Retryable);
        }
        // 写事务超过硬上限：请求得到登记错误码，变更回滚，恢复后同一请求可成功。
        store.HardTransactionLimit = TimeSpan.Zero;
        try { AssertProblem(await Assert.ThrowsAsync<MabiToolApiException>(() => owner.PreviewStorageCleanupAsync(new(["cache"]), "budget-cleanup-preview", ct)), "storage.transactionBudgetExceeded"); }
        finally { store.HardTransactionLimit = SqliteStore.TransactionHardLimit; }
        Assert.NotNull(await owner.PreviewStorageCleanupAsync(new(["cache"]), "budget-cleanup-preview", ct));
        // 写队列已满。
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocker = store.WriteAsync(_ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); return true; });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var queued = new List<Task>();
        try
        {
            while (true)
            {
                try { queued.Add(store.WriteAsync(tx => tx.Scalar("SELECT 1"))); }
                catch (StorageException error) when (error.Code == "storage.writeQueueFull") { break; }
            }
            AssertProblem(await Assert.ThrowsAsync<MabiToolApiException>(() => owner.PreviewStorageCleanupAsync(new(["cache"]), "queue-cleanup-preview", ct)), "storage.writeQueueFull");
        }
        finally { release.Set(); }
        await blocker; await Task.WhenAll(queued);
        // 只读连接在有界等待内未释放。
        store.ReadSlotWait = TimeSpan.FromMilliseconds(50);
        using var held = new CountdownEvent(8); using var free = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() => store.Read(_ => { held.Signal(); free.Wait(TimeSpan.FromSeconds(30)); return 0; }),
            TaskCreationOptions.LongRunning)).ToArray();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            AssertProblem(await Assert.ThrowsAsync<MabiToolApiException>(() => owner.GetStorageBackupsAsync(ct)), "storage.readCapacityExceeded");
        }
        finally { free.Set(); await Task.WhenAll(readers); store.ReadSlotWait = SqliteStore.ReadCapacityWait; }
        Assert.NotNull(await owner.GetStorageBackupsAsync(ct));
    }

    [Fact]
    public async Task BackupSdkPublishesCompleteCatalogAndReconnectableOwnerEvent()
    {
        await using var host = await Host.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = timeout.Token;
        var filter = new EventStreamOptions(["storage.backup.changed"], [], null);
        var before = await owner.GetEventSnapshotAsync(filter, ct);
        await using var events = owner.StreamEventsAsync(filter with { LastEventId = before.EventCursor }, ct).GetAsyncEnumerator(ct);
        var pending = events.MoveNextAsync().AsTask();
        var operation = await owner.CreateStorageBackupAsync(new(), "http-backup-create", ct);
        Assert.Equal(operation.Id, (await owner.CreateStorageBackupAsync(new(), "http-backup-create", ct)).Id);
        var completed = await WaitForOperation(owner, operation.Id, ct); Assert.Equal(OperationState.Succeeded, completed.State.KnownValue);
        var backup = completed.Result!.Value.Deserialize<StorageBackupSummary>(ContractJson.Options)!;
        Assert.Equal("complete", backup.State); Assert.Equal("manual", backup.Purpose);
        Assert.Equal(Json(backup), Json(await owner.GetStorageBackupAsync(backup.Id, ct)));
        Assert.Contains((await owner.GetStorageBackupsAsync(ct)).Items, x => x.Id == backup.Id);
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(backup.Id, events.Current.Data.Deserialize<EventEnvelope<StorageBackupChanged>>(ContractJson.Options)!.Data.Backup.Id);
        var managerGrant = await Pair(owner, [ApiPermissions.SystemRead, ApiPermissions.StorageManage]); using var manager = host.Client(managerGrant.Token);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => manager.CreateStorageBackupAsync(new(), "paired-backup-create", ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => manager.GetStorageBackupsAsync(ct))).Status);
        Assert.Empty((await manager.GetEventSnapshotAsync(filter, ct)).Resources);
        await WaitForProjection(owner, filter, ct);
        Assert.Equal(backup.Id, (await owner.GetEventSnapshotAsync(filter, ct)).Resources.Single().Data.GetProperty("backup").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaintenanceWaitsForActivitiesThenHonorsCancellationOrRejectsChangedData(bool cancel)
    {
        await using var host = await Host.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = timeout.Token;
        var backupOperation = await WaitForOperation(owner, (await owner.CreateStorageBackupAsync(new(), "wait-backup-create", ct)).Id, ct);
        var backup = backupOperation.Result!.Value.Deserialize<StorageBackupSummary>(ContractJson.Options)!;
        string? etag = null;
        var plan = await owner.PreviewStorageRestoreAsync(new(backup.Id), "wait-restore-preview", ct, metadata => etag = metadata.ETag);
        using (var activity = host.Services.GetRequiredService<ActivityBarrier>().EnterActivity("fixture", "active-game"))
        {
            var admitted = await owner.ExecuteStorageRestoreAsync(plan.Id, "wait-restore-execute", etag!, ct);
            for (var attempt = 0; ; attempt++)
            {
                if ((await owner.GetOperationAsync(admitted.Id, ct)).Phase == "waitingForIdle") break;
                Assert.True(attempt < 100); await Task.Delay(20, ct);
            }
            Assert.False(host.Stopping.IsCancellationRequested);
            if (cancel)
            {
                await owner.CancelOperationAsync(admitted.Id, new("cancel-waiting"), ct);
                Assert.Equal(OperationState.Cancelled, (await WaitForOperation(owner, admitted.Id, ct)).State.KnownValue);
            }
            else
                await host.Services.GetRequiredService<AssetStore>().PutAsync("music-library", "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("changed-during-wait")), cancellationToken: ct);
            host.PendingOperation = admitted.Id;
        }
        if (!cancel)
        {
            var failed = await WaitForOperation(owner, host.PendingOperation!, ct);
            Assert.Equal(OperationState.Failed, failed.State.KnownValue); Assert.Equal("storage.planStale", failed.Problem!.Code);
        }
        Assert.False(StorageMaintenanceCoordinator.NeedsRecovery(host.Storage));
        Assert.False(host.Stopping.IsCancellationRequested);
        for (var attempt = 0; host.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities.Count > 0; attempt++)
        {
            Assert.True(attempt < 100); await Task.Delay(20, ct);
        }
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task RestoreHttpHandoffClosesDatabaseAndRecoversSameReceiptInNewEpoch()
    {
        await using var host = await Host.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = timeout.Token;
        var assets = host.Services.GetRequiredService<AssetStore>();
        var original = await assets.PutAsync("music-library", "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("before-backup")), cancellationToken: ct);
        var backupOperation = await WaitForOperation(owner, (await owner.CreateStorageBackupAsync(new(), "handoff-backup-create", ct)).Id, ct);
        var backup = backupOperation.Result!.Value.Deserialize<StorageBackupSummary>(ContractJson.Options)!;
        var after = await assets.PutAsync("music-library", "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("after-backup")), cancellationToken: ct);
        var oldEpoch = (await owner.GetSystemInfoAsync(ct)).InstanceId;
        var filter = new EventStreamOptions(["storage.maintenance.changed"], [], null);
        var previousEvents = await owner.GetEventSnapshotAsync(filter, ct);
        string? etag = null;
        var plan = await owner.PreviewStorageRestoreAsync(new(backup.Id), "handoff-restore-preview", ct, metadata => etag = metadata.ETag);
        var operation = await owner.ExecuteStorageRestoreAsync(plan.Id, "handoff-restore-execute", etag!, ct);
        await host.RestartAfterMaintenance(ct); using var restarted = host.Client();
        Assert.NotEqual(oldEpoch, (await restarted.GetSystemInfoAsync(ct)).InstanceId);
        var result = await restarted.GetOperationAsync(operation.Id, ct); Assert.Equal(OperationState.Succeeded, result.State.KnownValue);
        Assert.Equal(EffectStatus.None, result.EffectStatus.KnownValue);
        Assert.Equal(operation.Id, (await restarted.ExecuteStorageRestoreAsync(plan.Id, "handoff-restore-execute", etag!, ct)).Id);
        Assert.Equal(original.Id, host.Services.GetRequiredService<AssetStore>().GetMetadata(original.Id)!.Id);
        Assert.Null(host.Services.GetRequiredService<AssetStore>().GetMetadata(after.Id));
        await WaitForProjection(restarted, filter, ct);
        Assert.Equal(operation.Id, (await restarted.GetEventSnapshotAsync(filter, ct)).Resources.Single().Data.GetProperty("operationId").GetString());
        await using var stale = restarted.StreamEventsAsync(filter with { LastEventId = previousEvents.EventCursor }, ct).GetAsyncEnumerator(ct);
        Assert.Equal("events.resyncRequired", (await Assert.ThrowsAsync<MabiToolApiException>(() => stale.MoveNextAsync().AsTask())).Problem!.Code);
        Assert.False(StorageMaintenanceCoordinator.NeedsRecovery(host.Storage));
    }

    [Fact]
    public async Task AuthenticatedSdkUsesActualETagAndReceivesPersistentCleanupEvent()
    {
        await using var host = await Host.Start(); using var client = host.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        var usage = await client.GetStorageUsageAsync(ct); Assert.NotEmpty(usage.Volumes);
        string? etag = null;
        var plan = await client.PreviewStorageCleanupAsync(new(["cache"]), "http-storage-preview", ct, metadata => etag = metadata.ETag);
        Assert.NotNull(etag); Assert.Equal(1UL, plan.Revision.Value);
        var filter = new EventStreamOptions(["storage.changed"], [plan.Id], null);
        var before = await client.GetEventSnapshotAsync(filter, ct);
        await using var stream = client.StreamEventsAsync(filter with { LastEventId = before.EventCursor }, ct).GetAsyncEnumerator(ct);
        var pending = stream.MoveNextAsync().AsTask();
        var result = await Execute(client, plan.Id, etag!, ct);
        Assert.Equal("completed", result.State); Assert.Equal(3UL, result.Revision.Value);
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal("storage.changed", stream.Current.Event);
        var envelope = stream.Current.Data.Deserialize<EventEnvelope<StorageChanged>>(ContractJson.Options)!;
        Assert.Equal(plan.Id, envelope.Data.PlanId); Assert.Equal(result.Revision, envelope.ResourceRevision);
        using var second = host.Client();
        var recovered = await second.GetStorageCleanupResultAsync(plan.Id, ct);
        Assert.Equal(Json(result), Json(recovered));
        var duplicate = await second.ExecuteStorageCleanupAsync(plan.Id, "http-storage-execute", etag!, ct);
        Assert.Equal(Json(result), Json(duplicate));
        Assert.Equal("completed", (await second.GetStorageCleanupPlanAsync(plan.Id, ct)).State);
    }

    [Fact]
    public async Task StoragePermissionAndPlanSubjectAreEnforcedInHttpAndSse()
    {
        await using var host = await Host.Start(); using var owner = host.Client();
        var readGrant = await Pair(owner, [ApiPermissions.SystemRead]); using var reader = host.Client(readGrant.Token);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.GetStorageUsageAsync())).Status);
        var firstGrant = await Pair(owner, [ApiPermissions.SystemRead, ApiPermissions.StorageManage]);
        var otherGrant = await Pair(owner, [ApiPermissions.SystemRead, ApiPermissions.StorageManage]);
        using var first = host.Client(firstGrant.Token); using var other = host.Client(otherGrant.Token);
        string? etag = null;
        var plan = await first.PreviewStorageCleanupAsync(new(["cache"]), "subject-preview", onResponse: metadata => etag = metadata.ETag);
        Assert.Equal(404, (await Assert.ThrowsAsync<MabiToolApiException>(() => other.GetStorageCleanupPlanAsync(plan.Id))).Status);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await Execute(first, plan.Id, etag!, deadline.Token);
        var filter = new EventStreamOptions(["storage.changed"], [plan.Id], null);
        await WaitForProjection(first, filter, deadline.Token);
        Assert.Empty((await other.GetEventSnapshotAsync(filter, deadline.Token)).Resources);
        Assert.Empty((await reader.GetEventSnapshotAsync(filter, deadline.Token)).Resources);
        Assert.Equal(result.State, (await first.GetStorageCleanupResultAsync(plan.Id, deadline.Token)).State);
    }

    private static async Task<StorageCleanupResult> Execute(MabiToolClient client, string id, string etag, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await client.ExecuteStorageCleanupAsync(id, "http-storage-execute", etag, ct); }
            catch (MabiToolApiException error) when (error.Problem?.Code == "resource.busy" && attempt < 50) { await Task.Delay(100, ct); }
        }
    }
    private static async Task WaitForProjection(MabiToolClient client, EventStreamOptions filter, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var snapshot = await client.GetEventSnapshotAsync(filter, ct);
            if (snapshot.Resources.Count > 0 || attempt >= 100) { Assert.Single(snapshot.Resources); return; }
            await Task.Delay(20, ct);
        }
    }
    private static async Task<AccessGrant> Pair(MabiToolClient owner, IReadOnlyList<string> permissions)
    {
        var challenge = await owner.CreatePairingChallengeAsync(new("storage-client", null, permissions));
        return await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    internal static async Task<OperationSnapshot> WaitForOperation(MabiToolClient client, string id, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var value = await client.GetOperationAsync(id, ct);
                if (SqliteStore.IsTerminal(value.State.KnownValue)) return value;
            }
            catch (MabiToolApiException error) when (error.Problem?.Code == "resource.busy") { }
            await Task.Delay(50, ct);
        }
    }
    internal sealed class Host : IAsyncDisposable
    {
        private readonly string root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "storage-api-" + Guid.NewGuid().ToString("N"));
        private readonly string token = LocalAuthService.NewSecret();
        private WebApplication app = null!;
        private Uri address = null!;
        public IServiceProvider Services => app.Services;
        public StorageOptions Storage => new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data"));
        public CancellationToken Stopping => app.Lifetime.ApplicationStopping;
        public string? PendingOperation { get; set; }
        public static async Task<Host> Start()
        {
            var host = new Host(); Directory.CreateDirectory(host.root);
            await host.Open();
            return host;
        }
        private async Task Open()
        {
            app = BackendApplication.Build(new(0, token, new HashSet<string>()), storage: Storage);
            await app.StartAsync();
            address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }
        public async Task RestartAfterMaintenance(CancellationToken ct)
        {
            while (!Stopping.IsCancellationRequested) await Task.Delay(20, ct);
            await app.StopAsync(ct); await app.DisposeAsync();
            Assert.True(await StorageMaintenanceCoordinator.RecoverAsync(Storage, BackendApplication.DatabaseMigrations, BackendApplication.StorageData, ct: ct));
            await Open();
        }
        public async Task RestartAsync(CancellationToken ct)
        {
            await app.StopAsync(ct); await app.DisposeAsync(); await Open();
        }
        public MabiToolClient Client(string? credential = null) => new(address, _ => ValueTask.FromResult(credential ?? token));
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(); await app.DisposeAsync();
            var allowed = Path.GetFullPath(Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(allowed, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); Directory.Delete(root, true);
        }
    }
}
