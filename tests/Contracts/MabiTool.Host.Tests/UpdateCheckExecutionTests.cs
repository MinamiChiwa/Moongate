using System.Net;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Updates;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.Providers.GitHubReleases;
using MabiTool.Updates.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class UpdateCheckExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnacknowledgedOrShutdownCancelledInstallTransferReturnsFailure(bool shutdown)
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var now = DateTimeOffset.UtcNow;
        var plan = new UpdatePlan(Guid.NewGuid().ToString("N"), new string('a', 64), "release-1", 1,
            [], [], [], 0, 0, now, now.AddHours(1), new string('b', 64));
        var installer = new UnacknowledgedInstaller(shutdown);
        using var worker = new UpdateCheckOperations(operations, new(database), new Checker(_ => Task.FromResult(Result())),
            TimeProvider.System, new PlanDownloader(plan), installer, new ResourceLeaseCoordinator(activityBarrier: database.Activities));
        await worker.StartAsync(default);
        var accepted = await worker.SubmitApplyAsync("owner", plan.PlanId, "unacknowledged-transfer", OperationStore.UpdatePlanETag(plan));
        await installer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (shutdown) await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        var failed = await Terminal(operations, accepted.Id);
        Assert.Equal(OperationState.Failed, failed.State.KnownValue);
        Assert.Equal("failed", failed.Phase);
        Assert.Equal("update.installationFailed", failed.Problem!.Code);
        Assert.Null(failed.Result);
        Assert.False(installer.ShutdownRequested);
        Assert.Null(database.Activities.Snapshot().MaintenanceOwner);
        if (!shutdown)
        {
            var check = await worker.SubmitAsync("owner", "check-after-transfer-failure");
            Assert.Equal(OperationState.Succeeded, (await Terminal(operations, check.Id)).State.KnownValue);
            await worker.StopAsync(default);
        }
        Assert.Equal(0, await database.RecoverOperationsAsync());
    }

    [Fact]
    public async Task Automatic_discovery_retains_offer_without_downloading_or_installing_after_restart()
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        var clock = new Clock(f.Now);
        var manifest = f.Release();
        var index = JsonSerializer.SerializeToUtf8Bytes(f.Index(manifest), SignedReleaseFixture.Options);
        var requests = new List<string>();
        using var provider = new ReleaseIndexSourceProvider(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            Assert.True(path.EndsWith("channel-index.json", StringComparison.Ordinal) || path.EndsWith(".manifest.json", StringComparison.Ordinal));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(path.EndsWith("channel-index.json", StringComparison.Ordinal) ? index : manifest) };
        }), retryCount: 0);
        var options = Options(f, 0, new('0', 64));
        var checker = new ReleaseUpdateCheckService(options, provider, clock);
        OperationSnapshot offer;
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var policy = new UpdatePolicyStore(database, clock);
            await policy.ScheduleStartupAsync();
            using var worker = new UpdateCheckOperations(new(database, clock), policy, checker, clock);
            await worker.StartAsync(default);
            clock.Now = clock.Now.AddSeconds(61);
            await worker.RunScheduledCheckAsync(0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var saved = database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT snapshot FROM core_operations";
                    using var rows = command.ExecuteReader();
                    var values = new List<OperationSnapshot>();
                    while (rows.Read()) values.Add(JsonSerializer.Deserialize<OperationSnapshot>(rows.GetString(0), ContractJson.Options)!);
                    return values;
                });
                if (saved.Count > 0)
                {
                    var operation = Assert.Single(saved);
                    Assert.Equal("update.check", operation.Kind);
                    if (operation.State.KnownValue == OperationState.Succeeded) { offer = operation; break; }
                    Assert.NotEqual(OperationState.Failed, operation.State.KnownValue);
                }
                await Task.Delay(10, timeout.Token);
            }
            await worker.StopAsync(default);
            Assert.True(offer.Result!.Value.GetProperty("updateAvailable").GetBoolean());
            var planId = offer.Result.Value.GetProperty("planId").GetString()!;
            Assert.Equal(planId, (await new UpdatePlanRepository(options.StateRoot).ReadAsync(planId)).Plan.PlanId);
            Assert.Equal(2, requests.Count);
            Assert.False(Directory.Exists(Path.Combine(options.InstallationRoot, "staging")));
            Assert.False(Directory.Exists(Path.Combine(options.InstallationRoot, "versions")));
        }
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var operations = new OperationStore(database, clock);
            Assert.Equal(offer.Result!.Value.GetRawText(), operations.Get(offer.Id, "system-updates", true).Result!.Value.GetRawText());
            using var worker = new UpdateCheckOperations(operations, new(database, clock), checker, clock);
            await worker.StartAsync(default);
            Assert.False(await worker.RunScheduledCheckAsync(0));
            await worker.StopAsync(default);
            Assert.Equal(2, requests.Count);
        }
    }

    [Fact]
    public async Task Concurrent_manual_checks_share_execution_and_do_not_depend_on_request_connection()
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var policy = new UpdatePolicyStore(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new Checker(async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result(); });
        using var worker = new UpdateCheckOperations(operations, policy, checker, TimeProvider.System);
        await worker.StartAsync(default);
        using var request = new CancellationTokenSource();
        var first = await worker.SubmitAsync("a", "check-key-a", request.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();
        var others = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => worker.SubmitAsync("client-" + i, "check-key-" + i)));
        Assert.All(others, item => Assert.Equal(first.Id, item.Id));
        Assert.Equal(1, checker.Calls);
        release.TrySetResult();
        var finished = await Terminal(operations, first.Id);
        Assert.Equal(OperationState.Succeeded, finished.State.KnownValue);
        Assert.False(finished.Result!.Value.GetProperty("updateAvailable").GetBoolean());
        await worker.StopAsync(default);
        Assert.Equal(first.Id, (await operations.AdmitUpdateAsync("a", UpdateAction.Check, null, "check-key-a")).Snapshot.Id);
    }

    [Fact]
    public async Task Explicit_cancel_interrupts_network_and_cannot_be_overwritten_by_completion()
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new Checker(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Result();
        });
        using var worker = new UpdateCheckOperations(operations, new(database), checker, TimeProvider.System);
        await worker.StartAsync(default);
        var submitted = await worker.SubmitAsync("a", "check-key-cancel");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await operations.CancelAsync(submitted.Id, "a", false, "user-cancelled");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        var final = operations.Get(submitted.Id, "a", false);
        Assert.Equal(OperationState.Cancelled, final.State.KnownValue);
        Assert.Null(final.Result);
    }

    [Fact]
    public async Task Scheduler_waits_for_startup_and_claims_once_without_consuming_due_time_while_busy()
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        var clock = new Clock(f.Now);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database, clock);
        var policy = new UpdatePolicyStore(database, clock);
        await policy.ScheduleStartupAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new Checker(async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result(); }, configured: true);
        using var worker = new UpdateCheckOperations(operations, policy, checker, clock);
        await worker.StartAsync(default);
        Assert.False(await worker.RunScheduledCheckAsync(0));
        var manual = await worker.SubmitAsync("a", "check-key-manual");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now = f.Now.AddSeconds(61);
        var due = policy.Schedule().NextAttemptAt;
        Assert.False(await policy.TryClaimCheckAsync(0));
        Assert.Equal(due, policy.Schedule().NextAttemptAt);
        release.TrySetResult();
        await Terminal(operations, manual.Id);
        // 等待消费端释放入口，随后两个并发 tick 只能领取一次。
        var claims = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!claims)
        {
            var results = await Task.WhenAll(worker.RunScheduledCheckAsync(0, timeout.Token), worker.RunScheduledCheckAsync(0, timeout.Token));
            Assert.InRange(results.Count(value => value), 0, 1);
            claims = results.Any(value => value) || policy.Schedule().LastAttemptAt is not null;
            if (!claims) await Task.Delay(10, timeout.Token);
        }
        Assert.InRange(policy.Schedule().NextAttemptAt!.Value, clock.Now.AddHours(6), clock.Now.AddHours(6).AddMinutes(15));
        await worker.StopAsync(default);
        Assert.InRange(checker.Calls, 1, 2);
    }

    [Fact]
    public async Task Unconfigured_host_is_ready_and_manual_check_returns_stable_failure()
    {
        using var f = new SignedReleaseFixture();
        await using var app = BackendApplication.Build(new(0, new string('a', 43), new HashSet<string>()),
            storage: new(f.Path("bootstrap"), true, f.Path("data")));
        await app.StartAsync();
        Assert.False(app.Services.GetRequiredService<IUpdateCheckService>().IsConfigured);
        var operation = await app.Services.GetRequiredService<UpdateCheckOperations>().SubmitAsync("owner", "missing-config-check");
        var finished = await Terminal(app.Services.GetRequiredService<OperationStore>(), operation.Id);
        Assert.Equal(OperationState.Failed, finished.State.KnownValue);
        Assert.Equal("update.sourceNotConfigured", finished.Problem!.Code);
        await app.StopAsync();
    }

    [Fact]
    public async Task Host_stop_cancels_inflight_check_and_closes_admission()
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new Checker(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result();
        });
        using var worker = new UpdateCheckOperations(operations, new(database), checker, TimeProvider.System);
        await worker.StartAsync(default);
        var submitted = await worker.SubmitAsync("owner", "shutdown-check-key");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationState.Cancelled, operations.Get(submitted.Id, "owner", true).State.KnownValue);
        Assert.Equal("storage.unavailable", (await Assert.ThrowsAsync<OperationException>(() => worker.SubmitAsync("owner", "after-shutdown-key"))).Code);
    }

    [Fact]
    public async Task Expected_failures_publish_stable_problems_and_keep_executor_available()
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database);
        var failures = new (Exception Error, string Code)[]
        {
            (new UpdateSourceException("update.allSourcesFailed", []), "update.allSourcesFailed"),
            (new TrustValidationException("update.targetTreeMismatch"), "update.targetTreeMismatch"),
            (new TrustValidationException("update.signatureInvalid"), "update.signatureInvalid"),
            (new UpdatePlanException("update.updaterRequired"), "update.updaterRequired"),
            (new IOException("test-failure"), "update.storageUnavailable")
        };
        var index = 0;
        var checker = new Checker(_ => Task.FromException<UpdateCheckResult>(failures[index++].Error));
        using var worker = new UpdateCheckOperations(operations, new(database), checker, TimeProvider.System);
        await worker.StartAsync(default);
        for (var i = 0; i < failures.Length; i++)
        {
            var submitted = await worker.SubmitAsync("owner", "failure-check-" + i);
            var final = await Terminal(operations, submitted.Id);
            Assert.Equal(OperationState.Failed, final.State.KnownValue);
            Assert.Equal(failures[i].Code, final.Problem!.Code);
            Assert.Null(final.Result);
        }
        await worker.StopAsync(default);
        Assert.Equal(failures.Length, checker.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_failure_closes_admission_and_restart_does_not_requeue_history(bool persistenceFailure)
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        string id;
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            if (persistenceFailure)
                await database.WriteAsync(tx => tx.Execute("CREATE TRIGGER reject_update_result BEFORE UPDATE ON core_operations WHEN NEW.terminal=1 BEGIN SELECT RAISE(ABORT,'test-failure'); END"));
            var operations = new OperationStore(database);
            var checker = new Checker(_ => persistenceFailure ? Task.FromResult(Result()) : throw new InvalidOperationException("test-failure"));
            using var worker = new UpdateCheckOperations(operations, new(database), checker, TimeProvider.System);
            await worker.StartAsync(default);
            id = (await worker.SubmitAsync("owner", "interrupted-check-key")).Id;
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("storage.unavailable", (await Assert.ThrowsAsync<OperationException>(() => worker.SubmitAsync("owner", "closed-check-key"))).Code);
            Assert.Equal(OperationState.Running, operations.Get(id, "owner", true).State.KnownValue);
            await worker.StopAsync(default);
            if (persistenceFailure) await database.WriteAsync(tx => tx.Execute("DROP TRIGGER reject_update_result"));
        }
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            Assert.Equal(1, await database.RecoverOperationsAsync());
            var checker = new Checker(_ => Task.FromResult(Result()));
            using var worker = new UpdateCheckOperations(new(database), new(database), checker, TimeProvider.System);
            await worker.StartAsync(default);
            var retry = await worker.SubmitAsync("owner", "interrupted-check-key");
            Assert.Equal(id, retry.Id);
            Assert.Equal(OperationState.Interrupted, retry.State.KnownValue);
            await worker.StopAsync(default);
            Assert.Equal(0, checker.Calls);
        }
    }

    [Fact]
    public async Task Disabled_automatic_checks_leave_due_schedule_and_manual_checks_available()
    {
        using var f = new SignedReleaseFixture();
        using var lease = Lease(f);
        var clock = new Clock(f.Now);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database, clock);
        var policy = new UpdatePolicyStore(database, clock);
        await policy.ScheduleStartupAsync();
        await policy.SaveAsync(new(AutomaticChecks: false), UpdatePolicyStore.ETag(policy.Get()));
        clock.Now = f.Now.AddDays(1);
        var due = policy.Schedule().NextAttemptAt;
        var checker = new Checker(_ => Task.FromResult(Result()), configured: true);
        using var worker = new UpdateCheckOperations(operations, policy, checker, clock);
        await worker.StartAsync(default);
        Assert.False(await worker.RunScheduledCheckAsync(0));
        Assert.Equal(0, checker.Calls);
        Assert.Equal(due, policy.Schedule().NextAttemptAt);
        var manual = await worker.SubmitAsync("owner", "disabled-auto-manual");
        Assert.Equal(OperationState.Succeeded, (await Terminal(operations, manual.Id)).State.KnownValue);
        await worker.StopAsync(default);
        Assert.Equal(1, checker.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_plan_requires_fresh_authorization_and_current_root(bool revoke)
    {
        using var f = new SignedReleaseFixture();
        var manifest = f.Release();
        var index = JsonSerializer.SerializeToUtf8Bytes(f.Index(manifest), SignedReleaseFixture.Options);
        using var provider = new ReleaseIndexSourceProvider(new Handler(request =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("channel-index.json", StringComparison.Ordinal) ? index : manifest) }), retryCount: 0);
        var options = Options(f, 0, new('0', 64));
        var clock = new Clock(f.Now);
        var service = new ReleaseUpdateCheckService(options, provider, clock);
        var result = await service.CheckAsync(default);
        if (revoke)
        {
            var root = f.Root with { RootVersion = 2, Keys = f.Root.Keys.Select(key => key.KeyId == "release" ? key with { Revoked = true } : key).ToArray() };
            new UpdateTrustStore(Path.Combine(options.StateRoot, "trust.json"), f.Root, f.Now).ApplyRootRotation(
                SignedReleaseFixture.Sign(JsonSerializer.SerializeToUtf8Bytes(root, SignedReleaseFixture.Options), ("root", f.RootSigner)), f.Now);
        }
        else clock.Now = f.Now.AddHours(2);
        var error = await Assert.ThrowsAsync<TrustValidationException>(() => service.ReadValidatedPlanAsync(result.PlanId!));
        Assert.Equal(revoke ? "update.signatureInvalid" : "update.metadataExpired", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_signed_source_persists_plan_and_revalidates_after_restart(bool alreadyInstalled)
    {
        using var f = new SignedReleaseFixture();
        var manifest = f.Release();
        var index = JsonSerializer.SerializeToUtf8Bytes(f.Index(manifest), SignedReleaseFixture.Options);
        using var provider = new ReleaseIndexSourceProvider(new Handler(request =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("channel-index.json", StringComparison.Ordinal) ? index : manifest) }), retryCount: 0);
        var options = Options(f, alreadyInstalled ? 1 : 0, alreadyInstalled ? SignedReleaseFixture.Hash(manifest) : new('0', 64));
        options.Validate();
        var service = new ReleaseUpdateCheckService(options, provider, new Clock(f.Now));
        var result = await service.CheckAsync(default);
        Assert.Equal(!alreadyInstalled, result.UpdateAvailable);
        if (alreadyInstalled) { Assert.Null(result.PlanId); return; }
        var restarted = new ReleaseUpdateCheckService(options, provider, new Clock(f.Now));
        var validated = await restarted.ReadValidatedPlanAsync(result.PlanId!);
        Assert.Equal(result.ManifestSha256, validated.Plan.ManifestSha256);
        Assert.Equal(1, validated.Release.Manifest.ReleaseSequence);
        var saved = await new UpdatePlanRepository(options.StateRoot).ReadAsync(result.PlanId!);
        Assert.Equal(index, saved.SignedIndex);
        Assert.Equal(manifest, saved.SignedManifest);
        var path = Path.Combine(options.StateRoot, "plans", result.PlanId + ".json");
        var edited = saved with { Plan = saved.Plan with { DownloadBytes = 0 } };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(edited, SignedReleaseFixture.Options));
        Assert.Equal("update.planStale", (await Assert.ThrowsAsync<UpdatePlanException>(() => restarted.ReadValidatedPlanAsync(result.PlanId!))).Code);
    }

    [Fact]
    public async Task Signature_failure_is_not_reported_as_up_to_date_and_creates_no_plan()
    {
        using var f = new SignedReleaseFixture();
        using var provider = new ReleaseIndexSourceProvider(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent("{}"u8.ToArray()) }), retryCount: 0);
        var options = Options(f, 0, new('0', 64));
        var service = new ReleaseUpdateCheckService(options, provider, new Clock(f.Now));
        await Assert.ThrowsAsync<TrustValidationException>(() => service.CheckAsync(default));
        Assert.False(Directory.Exists(Path.Combine(options.StateRoot, "plans")));
    }

    private static UpdateInstallationOptions Options(SignedReleaseFixture f, long installedSequence, string installedHash) =>
        new("mabitool", f.Path("installation"), new("owner/project", "stable", null, null), f.Root, installedSequence, installedHash,
            new(new(1, 0, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000, []), new(new(1, 0, 0), 1, "self-contained"),
            installedSequence > 0 ? "release-1" : null);
    private static DataRootLease Lease(SignedReleaseFixture f) => DataRootLease.Open(new(f.Path("bootstrap"), true, f.Path("data")));
    private static UpdateCheckResult Result() => new(false, "release-1", new('a', 64), null, null, UpdateSourceKind.GithubDirect);
    private static async Task<OperationSnapshot> Terminal(OperationStore operations, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var value = operations.Get(id, "owner", true);
            if (value.State.KnownValue is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.Interrupted) return value;
            await Task.Delay(10, timeout.Token);
        }
    }
    private sealed class Checker(Func<CancellationToken, Task<UpdateCheckResult>> check, bool configured = false) : IUpdateCheckService
    {
        internal int Calls;
        public bool IsConfigured => configured;
        public Task<UpdateCheckResult> CheckAsync(CancellationToken token) { Interlocked.Increment(ref Calls); return check(token); }
    }
    private sealed class PlanDownloader(UpdatePlan plan) : IUpdateDownloadService
    {
        public Task<UpdatePlan> ReadPlanAsync(string planId, CancellationToken token = default) => Task.FromResult(plan);
        public Task<UpdateDownloadStatus> DownloadAsync(UpdatePlan frozenPlan, Func<string, UpdateDownloadStatus, CancellationToken, Task> report,
            CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class UnacknowledgedInstaller(bool waitForShutdown) : IUpdateInstallService
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ShutdownRequested;
        public async Task<bool> TransferAsync(string operationId, UpdatePlan plan, ActivityBarrier.MaintenanceLease maintenance, CancellationToken token)
        {
            Entered.TrySetResult();
            if (waitForShutdown) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return false;
        }
        public void RequestShutdown() => ShutdownRequested = true;
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
