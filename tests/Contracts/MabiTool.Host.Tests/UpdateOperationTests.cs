using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class UpdateOperationTests
{
    [Fact]
    public async Task LatestRequestUsesPersistentAdmissionOrderAcrossEqualTimesAndClockRollback()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        var clock = new Clock(); string id;
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var operations = new OperationStore(database, clock);
            var first = await operations.AdmitUpdateAsync("a", UpdateAction.Check, null, "first-check-key");
            await operations.BeginUpdateAsync(first.Snapshot.Id); await operations.FinishUpdateAsync(first.Snapshot.Id);
            var sameTime = await operations.AdmitUpdateAsync("a", UpdateAction.Check, null, "second-check-key");
            Assert.Equal(sameTime.Snapshot.Id, operations.LatestUpdate()!.Id);
            await operations.BeginUpdateAsync(sameTime.Snapshot.Id); await operations.FinishUpdateAsync(sameTime.Snapshot.Id);
            clock.Now = clock.Now.AddMinutes(-1);
            var last = await operations.AdmitUpdateAsync("a", UpdateAction.Check, null, "third-check-key");
            id = last.Snapshot.Id; Assert.Equal(id, operations.LatestUpdate()!.Id);
        }
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var operations = new OperationStore(database, clock);
            Assert.Equal(id, operations.LatestUpdate()!.Id);
            Assert.Equal(id, operations.LatestUpdate("update.check")!.Id);
        }
    }

    [Fact]
    public async Task ConcurrentClientsShareOneCheckAndRestartRetainsEachRetryIdentity()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        string id;
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var operations = new OperationStore(database);
            var admissions = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
                operations.AdmitUpdateAsync("client-" + i, UpdateAction.Check, null, "check-key-" + i)));
            Assert.Single(admissions, item => item.Created);
            id = admissions[0].Snapshot.Id;
            Assert.All(admissions, item => Assert.Equal(id, item.Snapshot.Id));
            var events = database.ReadPendingEvents();
            Assert.Equal(id, Assert.Single(events, item => item.Event.EventType == "operation.changed").Event.ResourceId);
            var progress = Assert.Single(events, item => item.Event.EventType == "update.progress");
            Assert.Equal(id, progress.Event.Payload.Deserialize<UpdateProgress>(ContractJson.Options)!.OperationId);
            var joined = await operations.AdmitUpdateAsync("extra", UpdateAction.Check, null, "join-key-1", queueAvailable: false);
            Assert.False(joined.Created);
            Assert.Equal(id, joined.Snapshot.Id);
        }
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            Assert.Equal(1, await database.RecoverOperationsAsync());
            var operations = new OperationStore(database);
            for (var i = 0; i < 8; i++)
            {
                var retry = await operations.AdmitUpdateAsync("client-" + i, UpdateAction.Check, null, "check-key-" + i, queueAvailable: false);
                Assert.False(retry.Created);
                Assert.Equal(id, retry.Snapshot.Id);
                Assert.Equal(OperationState.Interrupted, retry.Snapshot.State.KnownValue);
                Assert.Equal(EffectStatus.Unknown, retry.Snapshot.EffectStatus.KnownValue);
            }
            Assert.Equal(0, await database.RecoverOperationsAsync());
            await Code("resource.capacityExceeded", () => operations.AdmitUpdateAsync("new", UpdateAction.Check, null, "new-check-key", queueAvailable: false));
            Assert.True((await operations.AdmitUpdateAsync("new", UpdateAction.Check, null, "new-check-key")).Created);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepairedClientsReuseCommittedApplyAfterRestartAndCanRetryFailedApply(bool succeeded)
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        var clock = new Clock(); var plan = Plan(clock.Now); var etag = OperationStore.UpdatePlanETag(plan);
        OperationSnapshot finished;
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var operations = new OperationStore(database, clock);
            var leases = new ResourceLeaseCoordinator(activityBarrier: database.Activities);
            var admitted = await operations.AdmitUpdateAsync("first-session", UpdateAction.Apply, plan, "original-apply", etag);
            await operations.BeginUpdateAsync(admitted.Snapshot.Id);
            Assert.True(await operations.TryBeginUpdateMaintenanceAsync(admitted.Snapshot.Id, leases));
            finished = await operations.FinishUpdateAsync(admitted.Snapshot.Id,
                problem: succeeded ? null : ApiErrors.Create("operation.executionFailed", "已恢复旧版", admitted.Snapshot.Id));
        }
        if (succeeded) clock.Now = clock.Now.AddHours(2);
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var operations = new OperationStore(database, clock);
            SameSnapshot(finished, (await operations.AdmitUpdateAsync("first-session", UpdateAction.Apply,
                plan, "original-apply", etag)).Snapshot);
            var retry = await operations.AdmitUpdateAsync("new-session", UpdateAction.Apply, plan,
                "original-apply", etag, queueAvailable: !succeeded);
            Assert.Equal(!succeeded, retry.Created);
            if (succeeded)
            {
                SameSnapshot(finished, retry.Snapshot);
                SameSnapshot(finished, (await operations.AdmitUpdateAsync("another-session", UpdateAction.Apply,
                    plan, "another-apply", etag, queueAvailable: false)).Snapshot);
                Assert.Equal(finished.Id, operations.LatestUpdate()!.Id);
                Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
            }
            else
            {
                Assert.NotEqual(finished.Id, retry.Snapshot.Id);
                Assert.Equal(OperationState.Queued, retry.Snapshot.State.KnownValue);
                await operations.CancelAsync(retry.Snapshot.Id, "new-session", false, "cancel-retry");
            }
        }
    }

    [Fact]
    public async Task PlanConditionsAndIdempotencyAreAtomicAndRetriesPrecedeExpiryValidation()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var clock = new Clock();
        var operations = new OperationStore(database, clock);
        var plan = Plan(clock.Now);
        var etag = OperationStore.UpdatePlanETag(plan);
        await Code("revision.required", () => operations.AdmitUpdateAsync("a", UpdateAction.Download, plan, "download-key"));
        await Code("revision.stale", () => operations.AdmitUpdateAsync("a", UpdateAction.Download, plan, "download-key", "W/" + etag));
        await Code("resource.capacityExceeded", () => operations.AdmitUpdateAsync("a", UpdateAction.Download, plan, "download-key", etag, false));
        Assert.Empty(database.ReadPendingEvents());
        var admitted = await operations.AdmitUpdateAsync("a", UpdateAction.Download, plan, "download-key", etag);
        await Code("idempotency.conflict", () => operations.AdmitUpdateAsync("a", UpdateAction.Download, plan with { ReleaseId = "other" }, "download-key", etag));
        await Code("idempotency.conflict", () => operations.AdmitUpdateAsync("a", UpdateAction.Download, plan, "download-key", "other"));
        await Code("resource.busy", () => operations.AdmitUpdateAsync("b", UpdateAction.Apply, plan, "apply-key-1", etag));
        await Code("resource.busy", () => operations.AdmitUpdateAsync("b", UpdateAction.Download, Plan(clock.Now), "other-plan-key", etag));
        await Code("resource.busy", () => operations.AdmitUpdateAsync("b", UpdateAction.Check, null, "other-check-key"));
        var joined = await operations.AdmitUpdateAsync("b", UpdateAction.Download, plan, "download-key", etag, false);
        Assert.Equal(admitted.Snapshot.Id, joined.Snapshot.Id);
        await Code(ApiErrors.ResourceNotFound, () => operations.CancelAsync(admitted.Snapshot.Id, "b", false, null));
        await operations.CancelAsync(admitted.Snapshot.Id, "a", false, null);
        clock.Now = clock.Now.AddHours(2);
        var retry = await operations.AdmitUpdateAsync("b", UpdateAction.Download, plan, "download-key", etag, false);
        Assert.False(retry.Created);
        Assert.Equal(OperationState.Cancelled, retry.Snapshot.State.KnownValue);
        await Code("update.planStale", () => operations.AdmitUpdateAsync("b", UpdateAction.Download, plan, "fresh-download-key", etag));
        var fresh = Plan(clock.Now);
        Assert.True((await operations.AdmitUpdateAsync("b", UpdateAction.Apply, fresh, "apply-key-1", OperationStore.UpdatePlanETag(fresh))).Created);
    }

    [Fact]
    public async Task AdmissionFreezesPlanAndRejectsMalformedRequestsWithoutPartialWrites()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var components = new List<UpdateComponent> { new("host", new(1, 0), [], 1, 2, new string('a', 64)) };
        var plan = Plan(DateTimeOffset.UtcNow) with { Components = components };
        await Code("request.invalid", () => operations.AdmitUpdateAsync("\na", UpdateAction.Check, null, "check-key"));
        await Code("request.invalid", () => operations.AdmitUpdateAsync("a", (UpdateAction)999, null, "check-key"));
        await Code("idempotency.required", () => operations.AdmitUpdateAsync("a", UpdateAction.Check, null, null));
        await Code("request.invalid", () => operations.AdmitUpdateAsync("a", UpdateAction.Check, null, "short"));
        await Code("request.invalid", () => operations.AdmitUpdateAsync("a", UpdateAction.Check, plan, "check-key"));
        await Code("request.invalid", () => operations.AdmitUpdateAsync("a", UpdateAction.Apply, plan with { PlanId = "../bad" }, "apply-key", "etag"));
        await Code("request.bodyTooLarge", () => operations.AdmitUpdateAsync("a", UpdateAction.Download, plan with { ReleaseId = new string('x', 131_072) }, "download-key", OperationStore.UpdatePlanETag(plan)));
        Assert.Empty(database.ReadPendingEvents());
        var pending = operations.AdmitUpdateAsync("a", UpdateAction.Download, plan, "download-key", OperationStore.UpdatePlanETag(plan));
        components.Clear();
        var admission = await pending;
        Assert.Equal("host", Assert.Single(operations.ReadUpdateWork(admission.Snapshot.Id).Plan!.Components).ComponentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingAllowsActivitiesAndApplyingDefersCancellationUntilSafeCompletion(bool fail)
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var leases = new ResourceLeaseCoordinator(activityBarrier: database.Activities);
        var plan = Plan(DateTimeOffset.UtcNow);
        var admitted = await operations.AdmitUpdateAsync("a", UpdateAction.Apply, plan, "apply-key", OperationStore.UpdatePlanETag(plan));
        var id = admitted.Snapshot.Id;
        Assert.Equal("waitingForIdle", (await operations.BeginUpdateAsync(id))!.Phase);
        using (database.Activities.EnterActivity("room", "room.ready"))
        {
            Assert.False(await operations.TryBeginUpdateMaintenanceAsync(id, leases));
            using var other = database.Activities.EnterActivity("import", "storage.import");
            Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
        }
        Assert.True(await operations.TryBeginUpdateMaintenanceAsync(id, leases));
        Assert.Equal(id, database.Activities.Snapshot().MaintenanceOwner);
        Assert.Equal("applying", operations.Get(id, "a", false).Phase);
        await Assert.ThrowsAsync<ActivityBarrierException>(() => database.WriteAsync(tx => tx.Scalar("SELECT 1")));
        Assert.Throws<ActivityBarrierException>(() => database.Activities.EnterActivity("room", "room.ready"));
        await Code(ApiErrors.ResourceNotFound, () => operations.CancelAsync(id, "b", false, "stop"));
        var cancelled = await operations.CancelAsync(id, "a", false, "stop");
        Assert.Equal(OperationState.CancelRequested, cancelled.State.KnownValue);
        Assert.Equal(CancellationState.Deferred, cancelled.Cancellation.State.KnownValue);
        Assert.Null(cancelled.Timestamps.CompletedAt);
        SameSnapshot(cancelled, await operations.CancelAsync(id, "a", false, "stop-again"));
        Assert.Equal(id, database.Activities.Snapshot().MaintenanceOwner);
        var completed = await operations.FinishUpdateAsync(id, problem: fail ? ApiErrors.Create("operation.executionFailed", "恢复旧版完成", id) : null);
        Assert.Equal(fail ? OperationState.Failed : OperationState.Succeeded, completed.State.KnownValue);
        Assert.Equal(CancellationState.Completed, completed.Cancellation.State.KnownValue);
        Assert.NotNull(completed.Timestamps.CompletedAt);
        Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
        Assert.Empty(database.Activities.Snapshot().Activities);
        SameSnapshot(completed, await operations.CancelAsync(id, "a", false, null));
        SameSnapshot(completed, await operations.FinishUpdateAsync(id));
        Assert.Equal(1L, await database.WriteAsync(tx => tx.Scalar("SELECT 1")));
    }

    [Fact]
    public async Task CancelBeforeApplyingPreventsExecutionAndExpiredPlanReleasesMaintenance()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var clock = new Clock();
        var operations = new OperationStore(database, clock);
        var leases = new ResourceLeaseCoordinator(activityBarrier: database.Activities);
        var plan = Plan(clock.Now);
        var first = await operations.AdmitUpdateAsync("a", UpdateAction.Apply, plan, "first-apply-key", OperationStore.UpdatePlanETag(plan));
        await operations.CancelAsync(first.Snapshot.Id, "a", false, null);
        Assert.Null(await operations.BeginUpdateAsync(first.Snapshot.Id));
        Assert.False(await operations.TryBeginUpdateMaintenanceAsync(first.Snapshot.Id, leases));
        var second = await operations.AdmitUpdateAsync("a", UpdateAction.Apply, plan, "second-apply-key", OperationStore.UpdatePlanETag(plan));
        await operations.BeginUpdateAsync(second.Snapshot.Id);
        clock.Now = plan.ExpiresAt;
        await Code("update.planStale", () => operations.TryBeginUpdateMaintenanceAsync(second.Snapshot.Id, leases));
        Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
        Assert.Equal("waitingForIdle", operations.Get(second.Snapshot.Id, "a", false).Phase);
        var failed = await operations.FinishUpdateAsync(second.Snapshot.Id, problem: ApiErrors.Create("update.planStale", "等待期间计划过期", second.Snapshot.Id));
        Assert.Equal(OperationState.Failed, failed.State.KnownValue);
        Assert.Equal("update.planStale", failed.Problem!.Code);
        Assert.False(await operations.TryBeginUpdateMaintenanceAsync(second.Snapshot.Id, leases));
    }

    [Theory]
    [InlineData(UpdateAction.Check, "checking", false)]
    [InlineData(UpdateAction.Download, "preparing", false)]
    [InlineData(UpdateAction.Download, "preparing", true)]
    public async Task CheckAndDownloadClaimsAreSingleUseAndCancellationPreservesTerminal(UpdateAction action, string phase, bool cancel)
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var plan = action == UpdateAction.Check ? null : Plan(DateTimeOffset.UtcNow);
        var admission = await operations.AdmitUpdateAsync("a", action, plan, "update-key", plan is null ? null : OperationStore.UpdatePlanETag(plan));
        var id = admission.Snapshot.Id;
        Assert.Equal(phase, (await operations.BeginUpdateAsync(id))!.Phase);
        await Code("resource.busy", () => operations.BeginUpdateAsync(id));
        if (cancel) await operations.CancelAsync(id, "a", false, "stop");
        var completed = await operations.FinishUpdateAsync(id);
        Assert.Equal(cancel ? OperationState.Cancelled : OperationState.Succeeded, completed.State.KnownValue);
        Assert.Null(await operations.BeginUpdateAsync(id));
        Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
    }

    [Fact]
    public async Task CancellationAndMaintenanceRacesHaveOnePersistedOutcome()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var leases = new ResourceLeaseCoordinator(activityBarrier: database.Activities);
        for (var i = 0; i < 8; i++)
        {
            var plan = Plan(DateTimeOffset.UtcNow);
            var admission = await operations.AdmitUpdateAsync("a", UpdateAction.Apply, plan, "apply-key-" + i, OperationStore.UpdatePlanETag(plan));
            var id = admission.Snapshot.Id;
            await operations.BeginUpdateAsync(id);
            var entering = Task.Run(() => operations.TryBeginUpdateMaintenanceAsync(id, leases));
            var cancelling = Task.Run(() => operations.CancelAsync(id, "a", false, "stop"));
            await Task.WhenAll(entering, cancelling);
            if (await entering)
            {
                Assert.Equal(CancellationState.Deferred, (await cancelling).Cancellation.State.KnownValue);
                Assert.Equal(id, database.Activities.Snapshot().MaintenanceOwner);
                await operations.FinishUpdateAsync(id);
            }
            else Assert.Equal(OperationState.Cancelled, (await cancelling).State.KnownValue);
            Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
        }
    }

    [Fact]
    public async Task MaintenanceAttemptsSweepExpiredGameLeasesWithoutRevokingLiveWork()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var clock = new Clock();
        var operations = new OperationStore(database, clock);
        var leases = new ResourceLeaseCoordinator(clock, database.Activities);
        var game = leases.Acquire(new("player", "game-operation", "game", new(1), ["foreground-input"], TimeSpan.FromSeconds(1)));
        var plan = Plan(clock.Now);
        var admission = await operations.AdmitUpdateAsync("a", UpdateAction.Apply, plan, "apply-key", OperationStore.UpdatePlanETag(plan));
        var id = admission.Snapshot.Id;
        await operations.BeginUpdateAsync(id);
        Assert.False(await operations.TryBeginUpdateMaintenanceAsync(id, leases));
        Assert.True(leases.Validate(game.Id, "foreground-input", game.FencingToken, "game", new(1)));
        clock.Timestamp += TimeSpan.FromSeconds(1).Ticks;
        Assert.True(await operations.TryBeginUpdateMaintenanceAsync(id, leases));
        Assert.False(leases.Validate(game.Id, "foreground-input", game.FencingToken, "game", new(1)));
        await operations.FinishUpdateAsync(id);
        Assert.Empty(database.Activities.Snapshot().Activities);
    }

    [Fact]
    public async Task FailedTerminalPersistenceKeepsMaintenanceUntilSafeResultIsDurable()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var leases = new ResourceLeaseCoordinator(activityBarrier: database.Activities);
        var plan = Plan(DateTimeOffset.UtcNow);
        var admitted = await operations.AdmitUpdateAsync("a", UpdateAction.Apply, plan, "apply-key", OperationStore.UpdatePlanETag(plan));
        var id = admitted.Snapshot.Id;
        await operations.BeginUpdateAsync(id);
        Assert.True(await operations.TryBeginUpdateMaintenanceAsync(id, leases));
        var applying = operations.Get(id, "a", false);
        var oversized = JsonSerializer.SerializeToElement(new { message = new string('x', SqliteStore.MaximumEventBytes) });
        var error = await Assert.ThrowsAsync<StorageException>(() => operations.FinishUpdateAsync(id, oversized));
        Assert.Equal("storage.eventTooLarge", error.Code);
        SameSnapshot(applying, operations.Get(id, "a", false));
        Assert.Equal(id, database.Activities.Snapshot().MaintenanceOwner);
        Assert.Throws<ActivityBarrierException>(() => database.Activities.EnterActivity("game", "game.resource"));
        var cancelled = await operations.CancelAsync(id, "a", false, "stop");
        Assert.Equal(CancellationState.Deferred, cancelled.Cancellation.State.KnownValue);
        await operations.FinishUpdateAsync(id);
        Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
        var hub = new StateEventHub(database, "epoch");
        await database.DispatchPendingAsync(hub);
        var session = new AuthSession("b", "frontend", [ApiPermissions.SystemRead], null, null, false);
        var projection = Assert.Single((await hub.SnapshotAsync(session, EventFilter.Create(["operation.changed"]))).Resources);
        Assert.Equal("committed", projection.Data.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task SharedUpdateIsVisibleInQuerySnapshotReplayAndLiveWhilePrivateOperationsStayScoped()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var hub = new StateEventHub(database, "test-epoch");
        var other = new AuthSession("b", "frontend", [ApiPermissions.SystemRead], null, null, false);
        var denied = other with { ClientId = "denied", Permissions = [] };
        var filter = EventFilter.Create(["operation.changed"]);
        var initial = await hub.SnapshotAsync(other, filter);
        await using var live = await hub.SubscribeAsync(other, filter, initial.EventCursor);
        await using var hidden = await hub.SubscribeAsync(denied, filter);
        var shared = await operations.AdmitUpdateAsync("a", UpdateAction.Check, null, "check-key");
        var privateOperation = await operations.AdmitConfigurationAsync("a", "color", true, new(false), "config-key", "\"module-color-0\"");
        await database.DispatchPendingAsync(hub);
        Assert.Equal(shared.Snapshot.Id, operations.Get(shared.Snapshot.Id, "b", false).Id);
        Assert.Equal(ApiErrors.ResourceNotFound, Assert.Throws<OperationException>(() => operations.Get(privateOperation.Snapshot.Id, "b", false)).Code);
        Assert.True(live.TryRead(out var item));
        Assert.Equal(shared.Snapshot.Id, item!.ResourceId);
        Assert.False(live.TryRead(out _));
        Assert.False(hidden.TryRead(out _));
        Assert.Equal(shared.Snapshot.Id, Assert.Single((await hub.SnapshotAsync(other, filter)).Resources).ResourceId);
        Assert.Empty((await hub.SnapshotAsync(denied, filter)).Resources);
        await using var resumed = await hub.SubscribeAsync(other, filter, initial.EventCursor);
        Assert.Equal(shared.Snapshot.Id, Assert.Single(resumed.Replay).ResourceId);
        await Code(ApiErrors.ResourceNotFound, () => operations.CancelAsync(shared.Snapshot.Id, "b", false, null));
    }

    private static async Task Code(string code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<OperationException>(action)).Code);

    private static void SameSnapshot(OperationSnapshot expected, OperationSnapshot actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, ContractJson.Options), JsonSerializer.Serialize(actual, ContractJson.Options));

    private static UpdatePlan Plan(DateTimeOffset now) => new(Guid.NewGuid().ToString("N"), new string('a', 64),
        "release-1", 1, [], [], [], 0, 0, now, now.AddHours(1), new string('b', 64));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string directory;
        public Workspace()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
            directory = Path.Combine(root!.FullName, "artifacts", "tmp", "update-operation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }
        public DataRootLease Lease() => DataRootLease.Open(new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
        public void Dispose() => Directory.Delete(directory, true);
    }
}
