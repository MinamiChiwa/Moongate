using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class UpdatePolicyTests
{
    [Fact]
    public async Task DefaultsDelayStartupAndConcurrentTicksClaimOneCheckWithBoundedJitter()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var clock = new Clock();
        var policy = new UpdatePolicyStore(database, clock);
        Assert.Equal(new UpdatePolicy(), policy.Get().Policy);
        Assert.Equal(new UInt64String(0), policy.Get().Revision);
        Assert.Null(policy.Schedule().NextAttemptAt);
        await policy.ScheduleStartupAsync();
        Assert.Equal(clock.Now.AddSeconds(60), policy.Schedule().NextAttemptAt);
        Assert.False(await policy.TryClaimCheckAsync(0));
        clock.Now = clock.Now.AddSeconds(60);
        var claimed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => policy.TryClaimCheckAsync(0.5)));
        Assert.Single(claimed, value => value);
        Assert.Equal(clock.Now, policy.Schedule().LastAttemptAt);
        Assert.Equal(clock.Now.AddHours(6).AddMinutes(7.5), policy.Schedule().NextAttemptAt);
        Assert.Equal(new UInt64String(0), policy.Get().Revision);
        Assert.Empty(database.ReadPendingEvents());
    }

    [Fact]
    public async Task PolicyEditsUseRevisionAndPublishOneDurableEvent()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var policy = new UpdatePolicyStore(database);
        var initial = policy.Get();
        var attempts = await Task.WhenAll(
            Record.ExceptionAsync(() => policy.SaveAsync(initial.Policy with { AutomaticChecks = false }, UpdatePolicyStore.ETag(initial))),
            Record.ExceptionAsync(() => policy.SaveAsync(initial.Policy with { DownloadConcurrency = 3 }, UpdatePolicyStore.ETag(initial))));
        Assert.Single(attempts, error => error is null);
        Assert.Equal("revision.stale", Assert.IsType<OperationException>(Assert.Single(attempts, error => error is not null)).Code);
        var current = policy.Get();
        Assert.Equal(new UInt64String(1), current.Revision);
        var changed = Assert.Single(database.ReadPendingEvents());
        Assert.Equal("update.policy-changed", changed.Event.EventType);
        Assert.Equal(current, changed.Event.Payload.Deserialize<UpdatePolicySnapshot>(ContractJson.Options));
        Assert.Equal(current, await policy.SaveAsync(current.Policy, UpdatePolicyStore.ETag(current)));
        Assert.Single(database.ReadPendingEvents());
        Assert.Equal("revision.required", (await Assert.ThrowsAsync<OperationException>(() => policy.SaveAsync(current.Policy, null))).Code);
    }

    [Fact]
    public async Task RestartRetainsPolicyAndScheduleAndDelaysAnOverdueCheck()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        var clock = new Clock();
        var custom = new UpdatePolicy(false, 3, 4, 8_388_608);
        UpdatePolicySnapshot saved;
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var store = new UpdatePolicyStore(database, clock);
            await store.ScheduleStartupAsync();
            saved = await store.SaveAsync(custom, UpdatePolicyStore.ETag(store.Get()));
            clock.Now = clock.Now.AddDays(1);
            Assert.False(await store.TryClaimCheckAsync(0));
        }
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var store = new UpdatePolicyStore(database, clock);
            Assert.Equal(saved, store.Get());
            await store.ScheduleStartupAsync();
            Assert.Equal(clock.Now.AddSeconds(60), store.Schedule().NextAttemptAt);
            saved = await store.SaveAsync(custom with { AutomaticChecks = true }, UpdatePolicyStore.ETag(saved));
            Assert.False(await store.TryClaimCheckAsync(0));
            clock.Now = clock.Now.AddSeconds(60);
            Assert.True(await store.TryClaimCheckAsync(0.999));
            var later = store.Schedule().NextAttemptAt;
            await store.ScheduleStartupAsync();
            Assert.Equal(later, store.Schedule().NextAttemptAt);
            Assert.InRange(later!.Value, clock.Now.AddHours(3), clock.Now.AddHours(3).AddMinutes(15));
        }
    }

    [Theory]
    [InlineData("automaticDownloads")]
    [InlineData("installWhenIdle")]
    [InlineData("allowMeteredDownloads")]
    public void Removed_automation_and_network_cost_options_are_rejected(string property)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdatePolicy>("{\"" + property + "\":true}", ContractJson.Options));
    }

    [Fact]
    public async Task Legacy_policy_migrates_to_user_controlled_updates_and_retains_schedule_and_revision()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        var clock = new Clock();
        await using (var legacy = SqliteStore.Open(lease, UpdatePolicyStore.Migrations.Take(2).ToArray()))
        {
            await legacy.WriteAsync(tx => tx.Execute("""
                INSERT INTO platform_update_policy VALUES(1,'7',$policy,$last,$next)
                """, ("$policy", """
                {"automaticChecks":false,"checkIntervalHours":12,"automaticDownloads":true,"installWhenIdle":true,"allowMeteredDownloads":true,"downloadConcurrency":4,"maximumBytesPerSecond":8388608}
                """), ("$last", clock.Now.ToUnixTimeMilliseconds()), ("$next", clock.Now.AddHours(12).ToUnixTimeMilliseconds())));
        }
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var store = new UpdatePolicyStore(database, clock);
        Assert.Equal(new UpdatePolicySnapshot(new(false, 12, 4, 8_388_608), new(7)), store.Get());
        Assert.Equal(new UpdateCheckSchedule(clock.Now, clock.Now.AddHours(12)), store.Schedule());
        var json = JsonSerializer.SerializeToElement(store.Get().Policy, ContractJson.Options);
        Assert.Equal(4, json.EnumerateObject().Count());
        Assert.False(json.TryGetProperty("automaticDownloads", out _));
        Assert.False(json.TryGetProperty("installWhenIdle", out _));
        Assert.False(json.TryGetProperty("allowMeteredDownloads", out _));
    }

    [Fact]
    public async Task DefaultIsUnlimitedAndOnlyNeverSavedPoliciesMigrateToUnlimited()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using (var legacy = SqliteStore.Open(lease, UpdatePolicyStore.Migrations.Take(4).ToArray()))
            await legacy.WriteAsync(tx => tx.Execute("""
                INSERT INTO platform_update_policy VALUES(1,'0','{"automaticChecks":true,"checkIntervalHours":6,"downloadConcurrency":2,"maximumBytesPerSecond":4194304}',NULL,NULL)
                """));
        await using (var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations))
        {
            var store = new UpdatePolicyStore(database);
            Assert.Null(store.Get().Policy.MaximumBytesPerSecond);
            Assert.Equal(new UInt64String(0), store.Get().Revision);
            var saved = await store.SaveAsync(store.Get().Policy with { MaximumBytesPerSecond = 1_048_576 }, UpdatePolicyStore.ETag(store.Get()));
            Assert.Equal(1_048_576, saved.Policy.MaximumBytesPerSecond);
            var cleared = await store.SaveAsync(saved.Policy with { MaximumBytesPerSecond = null }, UpdatePolicyStore.ETag(saved));
            Assert.Null(cleared.Policy.MaximumBytesPerSecond);
            Assert.Equal(new UInt64String(2), cleared.Revision);
        }
        using var customized = new Workspace();
        using var customLease = customized.Lease();
        await using (var legacy = SqliteStore.Open(customLease, UpdatePolicyStore.Migrations.Take(4).ToArray()))
            await legacy.WriteAsync(tx => tx.Execute("""
                INSERT INTO platform_update_policy VALUES(1,'3','{"automaticChecks":true,"checkIntervalHours":12,"downloadConcurrency":2,"maximumBytesPerSecond":4194304}',NULL,NULL)
                """));
        await using var kept = SqliteStore.Open(customLease, UpdatePolicyStore.Migrations);
        Assert.Equal(4_194_304, new UpdatePolicyStore(kept).Get().Policy.MaximumBytesPerSecond);
    }

    [Fact]
    public async Task InvalidBudgetsAndMaintenanceDoNotMutatePolicy()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var store = new UpdatePolicyStore(database);
        var initial = store.Get();
        foreach (var invalid in new[]
        {
            initial.Policy with { CheckIntervalHours = 0 }, initial.Policy with { CheckIntervalHours = 169 },
            initial.Policy with { DownloadConcurrency = 0 }, initial.Policy with { DownloadConcurrency = 5 },
            initial.Policy with { MaximumBytesPerSecond = 65_535 }, initial.Policy with { MaximumBytesPerSecond = 67_108_865 }
        })
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => store.SaveAsync(invalid, UpdatePolicyStore.ETag(initial)))).Code);
        foreach (var invalid in new[] { -0.1, 1, double.NaN, double.PositiveInfinity })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryClaimCheckAsync(invalid));
        using (var maintenance = database.Activities.TryEnterMaintenance("updater"))
        {
            Assert.NotNull(maintenance);
            await Assert.ThrowsAsync<ActivityBarrierException>(() => store.SaveAsync(initial.Policy with { AutomaticChecks = false }, UpdatePolicyStore.ETag(initial)));
            await Assert.ThrowsAsync<ActivityBarrierException>(() => store.ScheduleStartupAsync());
            await Assert.ThrowsAsync<ActivityBarrierException>(() => store.TryClaimCheckAsync(0));
            Assert.Equal(initial, store.Get());
        }
        Assert.Empty(database.ReadPendingEvents());
    }

    [Fact]
    public async Task PolicyReceiptsRetainOriginalResultRejectConflictsAndExpireIndependently()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        var clock = new Clock();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var policy = new UpdatePolicyStore(database, clock); var initial = policy.Get();
        var first = await policy.SaveIdempotentAsync("a", new(false, 2), "policy-first-key", UpdatePolicyStore.ETag(initial));
        var second = await policy.SaveIdempotentAsync("b", new(false, 3), "policy-first-key", UpdatePolicyStore.ETag(first));
        Assert.Equal(first, await policy.SaveIdempotentAsync("a", first.Policy, "policy-first-key", UpdatePolicyStore.ETag(initial)));
        Assert.Equal(second, policy.Get());
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() => policy.SaveIdempotentAsync(
            "a", second.Policy, "policy-first-key", UpdatePolicyStore.ETag(initial)))).Code);
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() => policy.SaveIdempotentAsync(
            "a", first.Policy, "policy-new-key", UpdatePolicyStore.ETag(initial)))).Code);
        Assert.Equal(2L, database.ReadScalar("SELECT count(*) FROM platform_update_policy_receipts"));
        Assert.Equal(2, database.ReadPendingEvents().Count(x => x.Event.EventType == "update.policy-changed"));
        clock.Now += OperationStore.KeyRetention;
        var changed = await policy.SaveIdempotentAsync("a", first.Policy, "policy-first-key", UpdatePolicyStore.ETag(second));
        Assert.Equal(3UL, changed.Revision.Value);
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM platform_update_policy_receipts"));
    }

    [Fact]
    public async Task ReceiptFailureRollsBackPolicyScheduleAndEventAndLeavesKeyAvailable()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var policy = new UpdatePolicyStore(database, new Clock());
        await policy.ScheduleStartupAsync(); var initial = policy.Get(); var schedule = policy.Schedule();
        await database.WriteAsync(tx => tx.Execute("CREATE TRIGGER fail_policy_receipt BEFORE INSERT ON platform_update_policy_receipts BEGIN SELECT RAISE(ABORT,'receipt failure'); END"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => policy.SaveIdempotentAsync(
            "owner", new(false, 2), "rollback-policy-key", UpdatePolicyStore.ETag(initial)));
        Assert.Equal(initial, policy.Get()); Assert.Equal(schedule, policy.Schedule());
        Assert.Empty(database.ReadPendingEvents());
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM platform_update_policy_receipts"));
        await database.WriteAsync(tx => tx.Execute("DROP TRIGGER fail_policy_receipt"));
        Assert.Equal(1UL, (await policy.SaveIdempotentAsync("owner", new(false, 2), "rollback-policy-key", UpdatePolicyStore.ETag(initial))).Revision.Value);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string directory;
        public Workspace()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
            directory = Path.Combine(root!.FullName, "artifacts", "tmp", "update-policy-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }
        public DataRootLease Lease() => DataRootLease.Open(new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
        public void Dispose() => Directory.Delete(directory, true);
    }
}
