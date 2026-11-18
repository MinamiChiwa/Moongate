using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class PersistenceTests
{
    [Fact]
    public async Task MaintenanceWritesRequireCurrentCredentialAndBusinessWritesRemainBlocked()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        var maintenance = store.Activities.TryEnterMaintenance("updater");
        Assert.NotNull(maintenance);
        using var foreign = new ActivityBarrier().TryEnterMaintenance("updater");
        Assert.NotNull(foreign);
        await Assert.ThrowsAsync<ActivityBarrierException>(() => store.WriteMaintenanceAsync(foreign, _ => 0));
        await store.WriteMaintenanceAsync(maintenance, tx => tx.Execute("INSERT INTO fixture_values VALUES('phase',1)"));
        await Assert.ThrowsAsync<ActivityBarrierException>(() => store.WriteAsync(_ => 0));
        Assert.Equal(1L, store.ReadScalar("SELECT value FROM fixture_values WHERE id='phase'"));
        await Assert.ThrowsAsync<IOException>(() => store.WriteMaintenanceAsync<int>(maintenance, _ => throw new IOException("injected")));
        Assert.Empty(store.Activities.Snapshot().Activities);
        Assert.Equal("updater", store.Activities.Snapshot().MaintenanceOwner);
        maintenance.Dispose();
        await Assert.ThrowsAsync<ActivityBarrierException>(() => store.WriteMaintenanceAsync(maintenance, _ => 0));
        Assert.Equal(1, await store.WriteAsync(tx => tx.Execute("UPDATE fixture_values SET value=2 WHERE id='phase'")));
    }

    [Fact]
    public async Task WritesHoldActivityThroughCommitAndReleaseBeforeCompletionOnEveryOutcome()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        await store.WriteAsync(tx =>
        {
            Assert.Null(store.Activities.TryEnterMaintenance("updater"));
            return tx.Execute("INSERT INTO fixture_values VALUES('committed',42)");
        });
        Assert.Empty(store.Activities.Snapshot().Activities);
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync<int>(_ => throw new IOException("injected")));
        Assert.Empty(store.Activities.Snapshot().Activities);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(_ => 1, cancelled.Token));
        using (var maintenance = store.Activities.TryEnterMaintenance("updater"))
        {
            Assert.NotNull(maintenance);
            Assert.Equal(42L, store.ReadScalar("SELECT value FROM fixture_values WHERE id='committed'"));
            await Assert.ThrowsAsync<ActivityBarrierException>(() => store.WriteAsync(_ => 1));
        }
        Assert.Equal(2, await store.WriteAsync(_ => 2));
        Assert.Empty(store.Activities.Snapshot().Activities);
    }

    private static DatabaseMigration Migration => new("fixture", 1, "CREATE TABLE fixture_values(id TEXT PRIMARY KEY, value INTEGER NOT NULL) STRICT;");

    [Fact]
    public async Task WriteTimingObserverFailurePreservesCommitRollbackAndCancellation()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        var observations = new System.Collections.Concurrent.ConcurrentQueue<(SqliteWriteTiming Timing, bool Released)>();
        store.WriteTimingObserver = timing =>
        {
            observations.Enqueue((timing, store.Activities.Snapshot().Activities.Count == 0));
            throw new IOException("injected diagnostic failure");
        };
        await store.WriteAsync(tx => SqliteStore.SaveOperation(tx, Operation("op-timing")));
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync<bool>(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('rolled-back',1)");
            SqliteStore.SaveOperation(tx, Operation("op-timing-rollback"));
            throw new IOException("injected business failure");
        }));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('cancelled',1)");
            cancellation.Cancel(); return true;
        }, cancellation.Token));
        var actual = observations.ToArray();
        Assert.Equal(new[] { "succeeded", "IOException", "cancelled" }, actual.Select(value => value.Timing.Outcome));
        Assert.All(actual, value =>
        {
            Assert.True(value.Released);
            Assert.True(value.Timing.SqlCommands > 0);
            Assert.All(new[] { value.Timing.QueueWaitMs, value.Timing.BeginMs, value.Timing.CallbackMs,
                value.Timing.SqlPrepareMs, value.Timing.SqlExecuteMs, value.Timing.OperationEncodingMs, value.Timing.CommitMs },
                elapsed => Assert.True(double.IsFinite(elapsed) && elapsed >= 0));
        });
        Assert.True(actual[0].Timing.OperationEncodingMs > 0);
        Assert.NotNull(store.GetOperation("op-timing"));
        Assert.Null(store.GetOperation("op-timing-rollback"));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM fixture_values"));
        Assert.Equal("op-timing", Assert.Single(store.ReadPendingEvents()).Event.ResourceId);
    }

    private static StateEvent Event(string id = "event-1", ulong revision = 1) => new(id, "fixture", "resource", new(revision), "fixture.changed", JsonSerializer.SerializeToElement(new { value = "状态" }));
    private static OperationSnapshot Operation(string id = "op-fixture", OperationState state = OperationState.Running) =>
        new(id, "fixture", "owner", [], state, "fixture", new(1), null,
            new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null), null, null, new(CancellationState.None, null), EffectStatus.InputDispatched);

    [Fact]
    public async Task MigrationsAreVersionedChecksummedAndFailedChangesRollback()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using (var store = SqliteStore.Open(lease, [Migration]))
        {
            Assert.Equal("wal", store.ReadScalar("PRAGMA journal_mode"));
            Assert.Equal(1L, store.ReadScalar("SELECT version FROM core_migrations WHERE module_id='fixture'"));
            await store.WriteAsync(tx => tx.Execute("INSERT INTO fixture_values VALUES('keep',42)"));
        }
        var bad = new DatabaseMigration("fixture", 2, "CREATE TABLE fixture_partial(id INTEGER); INSERT INTO missing_table VALUES(1);");
        Assert.Equal("storage.databaseInvalid", Assert.Throws<StorageException>(() => SqliteStore.Open(lease, [Migration, bad])).Code);
        var next = new DatabaseMigration("fixture", 2, "CREATE TABLE fixture_batch(id INTEGER); UPDATE fixture_values SET value=99;");
        Assert.Equal("storage.databaseInvalid", Assert.Throws<StorageException>(() => SqliteStore.Open(lease,
            [Migration, next, bad with { Version = 3 }])).Code);
        await using (var reopened = SqliteStore.Open(lease, [Migration]))
        {
            Assert.Equal(42L, reopened.ReadScalar("SELECT value FROM fixture_values WHERE id='keep'"));
            Assert.Equal(0L, reopened.ReadScalar("SELECT count(*) FROM sqlite_master WHERE name='fixture_partial'"));
            Assert.Equal(0L, reopened.ReadScalar("SELECT count(*) FROM sqlite_master WHERE name='fixture_batch'"));
            Assert.Equal(1L, reopened.ReadScalar("SELECT max(version) FROM core_migrations WHERE module_id='fixture'"));
        }
        Assert.Equal("storage.migrationIncompatible", Assert.Throws<StorageException>(() => SqliteStore.Open(lease, [Migration with { Sql = Migration.Sql + " -- changed" }])).Code);
        Assert.Equal("storage.migrationIncompatible", Assert.Throws<StorageException>(() => SqliteStore.Open(lease)).Code);
        Assert.Equal("storage.migrationInvalid", Assert.Throws<StorageException>(() => SqliteStore.Open(lease, [Migration with { Version = 2 }])).Code);
    }

    private const string MultilineSql = "CREATE TABLE eol_values (\n    id TEXT PRIMARY KEY,\n    value INTEGER NOT NULL\n) STRICT;";
    private static DatabaseMigration LfMigration => new("eol-fixture", 1, MultilineSql);
    private static DatabaseMigration CrlfMigration => new("eol-fixture", 1, MultilineSql.Replace("\n", "\r\n"));
    private static string RawChecksum(string sql) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)));

    [Fact]
    public void MigrationChecksumIgnoresSourceLineEndingsAndAcceptsLegacyRecords()
    {
        Assert.Equal(LfMigration.Checksum, CrlfMigration.Checksum);
        Assert.Equal(LfMigration.Checksum, new DatabaseMigration("eol-fixture", 1, MultilineSql.Replace("\n", "\r")).Checksum);
        Assert.Equal(RawChecksum(MultilineSql), CrlfMigration.Checksum);
        foreach (var migration in new[] { LfMigration, CrlfMigration })
        {
            Assert.True(migration.Matches(RawChecksum(MultilineSql)));
            Assert.True(migration.Matches(RawChecksum(MultilineSql.Replace("\n", "\r\n"))));
            Assert.False(migration.Matches(RawChecksum(MultilineSql + "\n-- changed")));
            Assert.False(migration.Matches(null));
        }
    }

    [Theory]
    [InlineData("lf", "crlf")]
    [InlineData("crlf", "lf")]
    [InlineData("crlf", "crlf")]
    public async Task LegacyLineEndingChecksumsOpenAndAreRecordedNormalized(string storedForm, string sourceForm)
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        var stored = RawChecksum(storedForm == "lf" ? MultilineSql : MultilineSql.Replace("\n", "\r\n"));
        var source = sourceForm == "lf" ? LfMigration : CrlfMigration;
        await using (var store = SqliteStore.Open(lease, [LfMigration]))
            await store.WriteAsync(tx => tx.Execute("UPDATE core_migrations SET checksum=$checksum WHERE module_id='eol-fixture'", ("$checksum", stored)));
        await using (var reopened = SqliteStore.Open(lease, [source]))
            Assert.Equal(LfMigration.Checksum, reopened.ReadScalar("SELECT checksum FROM core_migrations WHERE module_id='eol-fixture'"));
        Assert.Equal("storage.migrationIncompatible", Assert.Throws<StorageException>(() =>
            SqliteStore.Open(lease, [source with { Sql = source.Sql + "\n-- changed" }])).Code);
    }

    [Fact]
    public async Task BusinessWriteAndOutboxCommitOrRollbackTogetherAndDuplicateIdsAreChecked()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync<bool>(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('rolled-back',1)");
            SqliteStore.AppendEvent(tx, Event());
            throw new InvalidOperationException("injected");
        }));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM fixture_values"));
        Assert.Empty(store.ReadPendingEvents());
        Assert.True(await store.WriteAsync(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('committed',42)");
            return SqliteStore.AppendEvent(tx, Event());
        }));
        Assert.False(await store.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event())));
        Assert.Single(store.ReadPendingEvents());
        var conflict = await Assert.ThrowsAsync<StorageException>(() => store.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event(revision: 2))));
        Assert.Equal("storage.eventConflict", conflict.Code);
        Assert.Equal(42L, store.ReadScalar("SELECT value FROM fixture_values"));
        Assert.Equal(1UL, Assert.Single(store.ReadPendingEvents()).Event.Revision.Value);
    }

    [Fact]
    public async Task CancelledOverBudgetAndAsynchronousTransactionsCannotCommitAndQueueSurvives()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('cancelled',1)");
            cancellation.Cancel();
            return true;
        }, cancellation.Token));
        store.SoftTransactionBudget = TimeSpan.FromMilliseconds(10); store.HardTransactionLimit = TimeSpan.FromMilliseconds(100);
        var budget = await Assert.ThrowsAsync<StorageException>(() => store.WriteAsync(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('slow',1)");
            Thread.Sleep(store.HardTransactionLimit + TimeSpan.FromMilliseconds(30));
            return true;
        }));
        Assert.Equal("storage.transactionBudgetExceeded", budget.Code);
        store.SoftTransactionBudget = SqliteStore.TransactionBudget; store.HardTransactionLimit = SqliteStore.TransactionHardLimit;
        Assert.Throws<ArgumentException>(() => { _ = store.WriteAsync(_ => Task.FromResult(true)); });
        DatabaseTransaction? captured = null;
        await store.WriteAsync(tx =>
        {
            captured = tx;
            Task.Run(() => Assert.Throws<InvalidOperationException>(() => tx.Scalar("SELECT 1"))).GetAwaiter().GetResult();
            return true;
        });
        Assert.Throws<InvalidOperationException>(() => captured!.Scalar("SELECT 1"));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM fixture_values"));
        Assert.Equal(1, await store.WriteAsync(tx => tx.Execute("INSERT INTO fixture_values VALUES('after',1)")));
    }

    [Fact]
    public async Task OverDesignBudgetCommitsWithTelemetryWhileHardLimitRollsBackEverything()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        Assert.Equal(TimeSpan.FromMilliseconds(250), SqliteStore.TransactionBudget);
        Assert.Equal(TimeSpan.FromSeconds(30), SqliteStore.TransactionHardLimit);
        store.SoftTransactionBudget = TimeSpan.FromMilliseconds(20); store.HardTransactionLimit = TimeSpan.FromMilliseconds(400);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<SqliteTransactionBudgetSample>();
        store.TransactionBudgetObserver = sample => { observed.Enqueue(sample); throw new IOException("injected telemetry failure"); };
        // 超过设计预算、未达硬上限：照常提交，业务与通知都可见，遥测带回调与分段耗时。
        Assert.True(await store.WriteAsync(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('soft',1)");
            Thread.Sleep(60);
            return SqliteStore.AppendEvent(tx, Event("soft-event"));
        }));
        Assert.Equal(1L, store.ReadScalar("SELECT value FROM fixture_values WHERE id='soft'"));
        Assert.Equal("soft-event", Assert.Single(store.ReadPendingEvents()).Event.EventId);
        var soft = Assert.Single(observed);
        Assert.Equal("succeeded", soft.Outcome);
        Assert.InRange(soft.BudgetElapsedMs, 60, 400);
        Assert.Equal(20, soft.SoftBudgetMs); Assert.Equal(400, soft.HardLimitMs);
        Assert.Contains(nameof(OverDesignBudgetCommitsWithTelemetryWhileHardLimitRollsBackEverything), soft.Timing.Callback);
        Assert.True(soft.Timing.SqlCommands >= 2 && soft.Timing.CallbackMs >= 60 && soft.Timing.CommitMs >= 0 && soft.Timing.QueueWaitMs >= 0);
        Assert.True(soft.Gen0Collections >= 0 && soft.GcPauseMs >= 0 && soft.ProcessorCount == Environment.ProcessorCount);
        Assert.Equal(1, store.SoftBudgetExceededCount); Assert.Equal(0, store.HardLimitExceededCount);
        // 超过硬上限：整笔回滚，返回登记错误码，后续写入继续。
        var hard = await Assert.ThrowsAsync<StorageException>(() => store.WriteAsync(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('hard',1)");
            SqliteStore.AppendEvent(tx, Event("hard-event"));
            Thread.Sleep(450);
            return tx.Execute("INSERT INTO fixture_values VALUES('never',1)");
        }));
        Assert.Equal("storage.transactionBudgetExceeded", hard.Code);
        Assert.True((long)hard.Data["transactionElapsedMs"]! > 400);
        Assert.Contains(nameof(OverDesignBudgetCommitsWithTelemetryWhileHardLimitRollsBackEverything), (string)hard.Data["transactionCallback"]!);
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM fixture_values WHERE id IN ('hard','never')"));
        Assert.Single(store.ReadPendingEvents());
        Assert.Equal("storage.transactionBudgetExceeded", observed.Last().Outcome);
        Assert.Equal(1, store.HardLimitExceededCount);
        Assert.Equal(new[] { "succeeded", "storage.transactionBudgetExceeded" }, store.RecentBudgetSamples().Select(x => x.Outcome));
        // 设计预算内的写入不产生遥测。
        store.SoftTransactionBudget = SqliteStore.TransactionBudget;
        Assert.Equal(1, await store.WriteAsync(tx => tx.Execute("INSERT INTO fixture_values VALUES('fast',1)")));
        Assert.Equal(2, observed.Count);
    }

    /// <summary>慢盘或杀毒扫描下超过原 2 秒上限的写事务按默认上限照常提交，只记设计预算遥测。</summary>
    [Fact]
    public async Task WriteSlowerThanTheFormerTwoSecondLimitCommitsWithTelemetryOnly()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<SqliteTransactionBudgetSample>();
        store.TransactionBudgetObserver = observed.Enqueue;
        Assert.True(await store.WriteAsync(tx =>
        {
            tx.Execute("INSERT INTO fixture_values VALUES('slow-disk',1)");
            Thread.Sleep(2100);
            return SqliteStore.AppendEvent(tx, Event("slow-disk-event"));
        }));
        Assert.Equal(1L, store.ReadScalar("SELECT value FROM fixture_values WHERE id='slow-disk'"));
        Assert.Equal("slow-disk-event", Assert.Single(store.ReadPendingEvents()).Event.EventId);
        Assert.Equal("succeeded", Assert.Single(observed).Outcome);
        Assert.Equal((1L, 0L), (store.SoftBudgetExceededCount, store.HardLimitExceededCount));
    }

    [Fact]
    public async Task ReadersWaitBrieflyForAReleasedSlotAndFailWithRegisteredCodeAfterTheBound()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        Assert.Equal(TimeSpan.FromSeconds(5), SqliteStore.ReadCapacityWait);
        store.ReadSlotWait = TimeSpan.FromMilliseconds(1000);
        using var entered = new CountdownEvent(8); using var release = new SemaphoreSlim(0);
        var holders = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() => store.Read(_ =>
        {
            entered.Signal(); release.Wait(TimeSpan.FromSeconds(10)); return 1;
        }), TaskCreationOptions.LongRunning)).ToArray();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        // 一个槽位在等待上限内释放：新读取取得该槽位并成功。
        var waiting = Task.Factory.StartNew(() => store.ReadScalar("SELECT 7"), TaskCreationOptions.LongRunning);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        release.Release();
        Assert.Equal(7L, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        // 槽位在上限内未释放：返回可重试的登记错误。
        store.ReadSlotWait = TimeSpan.FromMilliseconds(150);
        using var reentered = new ManualResetEventSlim();
        var refill = Task.Factory.StartNew(() => store.Read(_ => { reentered.Set(); release.Wait(TimeSpan.FromSeconds(10)); return 1; }), TaskCreationOptions.LongRunning);
        Assert.True(reentered.Wait(TimeSpan.FromSeconds(5)));
        var timer = Stopwatch.StartNew();
        var full = Assert.Throws<StorageException>(() => store.ReadScalar("SELECT 1"));
        Assert.Equal("storage.readCapacityExceeded", full.Code);
        Assert.True(timer.ElapsedMilliseconds >= 140);
        var definition = Assert.Single(ApiErrors.Definitions, x => x.Code == full.Code);
        Assert.Equal((503, true), (definition.Status, definition.Retryable));
        release.Release(8);
        await Task.WhenAll(holders.Append(refill));
        Assert.Equal(1L, store.ReadScalar("SELECT 1"));
    }

    [Fact]
    public async Task OutboxReceiptIndexMigrationUpgradesExistingDatabaseAndServesCascadeLookup()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using (var legacy = SqliteStore.Open(lease, [Migration]))
        {
            await legacy.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event("receipt-event")));
            await legacy.ProjectAsync(Assert.Single(legacy.ReadPendingEvents()));
        }
        await using var upgraded = SqliteStore.Open(lease, [Migration, .. PlatformMigrations.All]);
        Assert.Equal(2L, upgraded.ReadScalar("SELECT max(version) FROM core_migrations WHERE module_id='platform-outbox'"));
        Assert.Equal(1L, upgraded.ReadScalar("SELECT count(*) FROM core_event_receipts WHERE event_id='receipt-event'"));
        string Plan(string sql) => upgraded.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var rows = command.ExecuteReader(); var details = new List<string>();
            while (rows.Read()) details.Add(rows.GetString(3));
            return string.Join(" | ", details);
        });
        Assert.Contains("core_event_receipts_event", Plan("SELECT 1 FROM core_event_receipts WHERE event_id='receipt-event'"));
        // 追加事件时的按时间回收走索引，不随保留事件逐行读取 payload。
        Assert.Contains("core_outbox_published_created", Plan("DELETE FROM core_outbox WHERE published=1 AND created_at < '2026-01-01'"));
        // 回收已确认事件时级联删除对应收据。
        await upgraded.WriteAsync(tx => tx.Execute("UPDATE core_outbox SET published=1"));
        await upgraded.WriteAsync(tx => tx.Execute("DELETE FROM core_outbox WHERE event_id='receipt-event'"));
        Assert.Equal(0L, upgraded.ReadScalar("SELECT count(*) FROM core_event_receipts"));
        Assert.Contains(SqliteStore.MigrationTargets(PlatformMigrations.All), x => x.ModuleId == "platform-outbox" && x.Version == 1);
    }

    [Fact]
    public async Task QueueIsBoundedAndShutdownCancelsQueuedWrites()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        var store = SqliteStore.Open(lease);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = store.WriteAsync(_ => { entered.Set(); release.Wait(); return true; });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var queued = Enumerable.Range(0, SqliteStore.WriteQueueCapacity).Select(_ => store.WriteAsync(tx => tx.Scalar("SELECT 1"))).ToArray();
        Assert.Equal("storage.writeQueueFull", Assert.Throws<StorageException>(() => { _ = store.WriteAsync(tx => tx.Scalar("SELECT 1")); }).Code);
        var stop = store.DisposeAsync().AsTask();
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        foreach (var pending in queued) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await stop;
        Assert.Throws<ObjectDisposedException>(() => { _ = store.WriteAsync(tx => tx.Scalar("SELECT 1")); });
    }

    [Fact]
    public async Task ConcurrentWritersSerializeWhileReadersSeeCommittedState()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease, [Migration]);
        await store.WriteAsync(tx => tx.Execute("INSERT INTO fixture_values VALUES('counter',0)"));
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => store.WriteAsync(tx => tx.Execute("UPDATE fixture_values SET value=value+1 WHERE id='counter'"))));
        Assert.Equal(64L, store.ReadScalar("SELECT value FROM fixture_values WHERE id='counter'"));
        await store.WriteAsync(tx =>
        {
            tx.Execute("UPDATE fixture_values SET value=65 WHERE id='counter'");
            Assert.Equal(64L, store.ReadScalar("SELECT value FROM fixture_values WHERE id='counter'"));
            return true;
        });
        Assert.Equal(65L, store.ReadScalar("SELECT value FROM fixture_values WHERE id='counter'"));
        Assert.Throws<SqliteException>(() => store.ReadScalar("DELETE FROM fixture_values"));
    }

    [Fact]
    public async Task ProjectionDeduplicationSurvivesFailureAfterDeliveryBeforeAcknowledgement()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using (var first = SqliteStore.Open(lease, [Migration]))
        {
            await first.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event()));
            var delivered = new RecordingSink(first, failAfterApply: true);
            await Assert.ThrowsAsync<IOException>(() => first.DispatchPendingAsync(delivered));
            Assert.Equal(1, delivered.Calls);
            Assert.Equal(1L, first.ReadScalar("SELECT count(*) FROM core_event_receipts"));
            Assert.Single(first.ReadPendingEvents());
        }
        await using var reopened = SqliteStore.Open(lease, [Migration]);
        var retry = new RecordingSink(reopened);
        Assert.Equal(1, await reopened.DispatchPendingAsync(retry));
        Assert.Equal(1, retry.Calls);
        Assert.Equal(0, retry.Applied);
        Assert.Empty(reopened.ReadPendingEvents());
        Assert.Equal(1L, reopened.ReadScalar("SELECT count(*) FROM core_event_receipts"));
        await reopened.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event("newer", ulong.MaxValue)));
        await reopened.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event("late", 2)));
        await reopened.DispatchPendingAsync(retry);
        Assert.Equal(ulong.MaxValue.ToString(), reopened.ReadScalar("SELECT revision FROM core_state_projection"));
    }

    // 整批投影在一个事务内提交、确认也按批提交：批内保持 outbox 顺序；确认前崩溃时重新发布，投影回执去重，不重复改变投影。
    [Fact]
    public async Task BatchedDispatchKeepsOrderAndRecoversWhenAcknowledgementIsLost()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using (var first = SqliteStore.Open(lease, [Migration]))
        {
            foreach (var revision in new ulong[] { 1, 2, 3 })
                await first.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event("batch-" + revision, revision)));
            var crashed = new BatchSink(first, failAfterPublish: true);
            await Assert.ThrowsAsync<IOException>(() => first.DispatchPendingAsync(crashed));
            Assert.Equal(["batch-1", "batch-2", "batch-3"], crashed.Delivered);
            Assert.Equal([true, true, true], crashed.Changed);
            Assert.Equal(3, first.ReadPendingEvents().Count);
            Assert.Equal("3", first.ReadScalar("SELECT revision FROM core_state_projection"));
        }
        await using var reopened = SqliteStore.Open(lease, [Migration]);
        var retry = new BatchSink(reopened);
        Assert.Equal(3, await reopened.DispatchPendingAsync(retry));
        Assert.Equal(["batch-1", "batch-2", "batch-3"], retry.Delivered);
        Assert.All(retry.Changed, Assert.False);
        Assert.Empty(reopened.ReadPendingEvents());
        Assert.Equal(3L, reopened.ReadScalar("SELECT count(*) FROM core_event_receipts"));
    }

    private sealed class BatchSink(SqliteStore store, bool failAfterPublish = false) : IBatchStateEventSink
    {
        public List<string> Delivered { get; } = [];
        public List<bool> Changed { get; } = [];
        public Task PublishAsync(PersistedEvent state, CancellationToken cancellationToken) => throw new InvalidOperationException("应按批发布");
        public async Task PublishBatchAsync(IReadOnlyList<PersistedEvent> batch, CancellationToken cancellationToken)
        {
            Changed.AddRange(await store.ProjectBatchForStreamAsync(batch, cancellationToken));
            Delivered.AddRange(batch.Select(state => state.Event.EventId));
            if (failAfterPublish) throw new IOException("injected before acknowledgement");
        }
    }

    [Fact]
    public async Task StartupRecoveryInterruptsActiveOperationsOnceAndPreservesTerminalResults()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using (var first = SqliteStore.Open(lease))
        {
            await first.SaveOperationAsync(Operation());
            await first.SaveOperationAsync(Operation("completed", OperationState.Succeeded));
        }
        await using var recovered = SqliteStore.Open(lease);
        Assert.Equal(1, await recovered.RecoverOperationsAsync());
        var state = recovered.GetOperation("op-fixture")!;
        Assert.Equal(OperationState.Interrupted, state.State.KnownValue);
        Assert.Equal(EffectStatus.Unknown, state.EffectStatus.KnownValue);
        Assert.Equal(2UL, state.Revision.Value);
        Assert.NotNull(state.Timestamps.CompletedAt);
        Assert.Equal(OperationState.Succeeded, recovered.GetOperation("completed")!.State.KnownValue);
        Assert.Equal(0, await recovered.RecoverOperationsAsync());
        Assert.Equal(3, recovered.ReadPendingEvents().Count);
        var conflict = await Assert.ThrowsAsync<StorageException>(() => recovered.SaveOperationAsync(Operation()));
        Assert.Equal("storage.operationRevisionConflict", conflict.Code);
    }

    [Fact]
    public async Task OutboxCapacityProtectsPendingNotificationsAndReclaimsOnlyAcknowledgedRows()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease);
        await store.WriteAsync(tx => tx.Execute("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$maximum)
            INSERT INTO core_outbox(event_id,resource_type,resource_id,revision,event_type,payload,created_at)
            SELECT 'full-'||x,'fixture','capacity',CAST(x AS TEXT),'fixture.changed','{}','2026-11-08T00:00:00.0000000+00:00' FROM n
            """, ("$maximum", SqliteStore.MaximumOutboxEvents)));
        var full = await Assert.ThrowsAsync<StorageException>(() => store.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event("next"))));
        Assert.Equal("storage.outboxFull", full.Code);
        var first = store.ReadPendingEvents(1).Single();
        Assert.True(await store.ProjectAsync(first));
        await store.WriteAsync(tx => tx.Execute("UPDATE core_outbox SET published=1 WHERE sequence=$sequence", ("$sequence", first.Sequence)));
        Assert.True(await store.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event("next"))));
        Assert.Equal((long)SqliteStore.MaximumOutboxEvents, store.ReadScalar("SELECT count(*) FROM core_outbox"));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_id=$id", ("$id", first.Event.EventId)));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_event_receipts"));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_outbox WHERE published=1"));
    }

    [Fact]
    public async Task EventSizeAndDeliveryContentAreCheckedBeforeProjection()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease);
        var large = Event() with { Payload = JsonSerializer.SerializeToElement(new string('x', SqliteStore.MaximumEventBytes + 1)) };
        var error = await Assert.ThrowsAsync<StorageException>(() => store.WriteAsync(tx => SqliteStore.AppendEvent(tx, large)));
        Assert.Equal("storage.eventTooLarge", error.Code);
        await store.WriteAsync(tx => SqliteStore.AppendEvent(tx, Event()));
        var original = Assert.Single(store.ReadPendingEvents());
        var altered = original with { Event = original.Event with { Revision = new(2) } };
        var mismatch = await Assert.ThrowsAsync<StorageException>(() => store.ProjectAsync(altered));
        Assert.Equal("storage.eventInvalid", mismatch.Code);
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_state_projection"));
        Assert.True(await store.ProjectAsync(original));
        Assert.False(await store.ProjectAsync(original));
    }

    [Fact]
    public async Task InitializedDatabaseMissingEmptyOrFromAnotherRootIsRejected()
    {
        using var firstWorkspace = new Workspace();
        using var firstLease = firstWorkspace.Lease();
        await using (var store = SqliteStore.Open(firstLease)) { }
        var db = firstLease.Paths.Database;
        var saved = File.ReadAllBytes(db);
        File.Delete(db);
        Assert.Equal("storage.databaseMissing", Assert.Throws<StorageException>(() => SqliteStore.Open(firstLease)).Code);
        Assert.False(File.Exists(db));
        File.WriteAllBytes(db, []);
        Assert.Equal("storage.databaseIdentityMismatch", Assert.Throws<StorageException>(() => SqliteStore.Open(firstLease)).Code);
        using var other = new Workspace();
        using var otherLease = other.Lease();
        await using (var store = SqliteStore.Open(otherLease)) { }
        File.Copy(otherLease.Paths.Database, db, overwrite: true);
        Assert.Equal("storage.databaseIdentityMismatch", Assert.Throws<StorageException>(() => SqliteStore.Open(firstLease)).Code);
        File.WriteAllBytes(db, saved);
        await using var reopened = SqliteStore.Open(firstLease);
        Assert.Equal("ok", reopened.ReadScalar("PRAGMA quick_check"));
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("uncommitted")]
    public async Task ForcedProcessTerminationRecoversCommittedStateWithoutReplayingActions(string scenario)
    {
        using var workspace = new Workspace();
        var marker = Path.Combine(workspace.Directory, "ready.txt");
        var probeDll = Path.Combine(workspace.Repository, "artifacts/build/bin/MabiTool.PersistenceProbe", new DirectoryInfo(AppContext.BaseDirectory).Name, "MabiTool.PersistenceProbe.dll");
        using (var child = workspace.Start(probeDll, [scenario, workspace.Bootstrap, workspace.Root, marker]))
        {
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                for (var i = 0; !File.Exists(marker); i++)
                {
                    if (child.HasExited) throw new InvalidOperationException(await errors);
                    if (i == 150) throw new TimeoutException("故障注入进程未进入目标阶段");
                    await Task.Delay(100);
                }
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                await Task.WhenAll(output, errors);
            }
        }
        using (var lease = workspace.Lease())
        await using (var store = SqliteStore.Open(lease))
        {
            Assert.Equal(OperationState.Running, store.GetOperation("op-crash-fixture")!.State.KnownValue);
            Assert.Single(store.ReadPendingEvents());
            Assert.DoesNotContain(store.ReadPendingEvents(), e => e.Event.EventId == "uncommitted-event");
        }
        using var host = workspace.Start(typeof(BackendApplication).Assembly.Location, []);
        var hostOutput = host.StandardOutput.ReadToEndAsync();
        var hostErrors = host.StandardError.ReadToEndAsync();
        try
        {
            var discovery = Path.Combine(workspace.Directory, "runtime", "instance.json");
            for (var i = 0; !File.Exists(discovery); i++)
            {
                if (host.HasExited) throw new InvalidOperationException(await hostErrors);
                if (i == 150) throw new TimeoutException("恢复宿主未就绪");
                await Task.Delay(100);
            }
            await LocalBootstrapClient.ConnectAsync(discovery, "recovery-test");
            using var read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(workspace.Root, "store/main.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            read.Open();
            using var query = read.CreateCommand();
            query.CommandText = "SELECT snapshot FROM core_operations WHERE id='op-crash-fixture'";
            var snapshot = JsonSerializer.Deserialize<OperationSnapshot>((string)query.ExecuteScalar()!, ContractJson.Options)!;
            Assert.Equal(OperationState.Interrupted, snapshot.State.KnownValue);
            Assert.Equal(EffectStatus.Unknown, snapshot.EffectStatus.KnownValue);
            Assert.Equal(2UL, snapshot.Revision.Value);
            Assert.Equal(scenario, File.ReadAllText(marker));
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
            await Task.WhenAll(hostOutput, hostErrors);
        }
    }

    private sealed class RecordingSink(SqliteStore store, bool failAfterApply = false) : IStateEventSink
    {
        public int Calls { get; private set; }
        public int Applied { get; private set; }
        public async Task PublishAsync(PersistedEvent state, CancellationToken cancellationToken)
        {
            Calls++;
            if (await store.ProjectAsync(state, cancellationToken)) Applied++;
            if (failAfterApply) throw new IOException("injected after publish");
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Repository { get; }
        public string Directory { get; }
        public string Root => Path.Combine(Directory, "data");
        public string Bootstrap => Path.Combine(Directory, "bootstrap");
        public Workspace()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "MabiTool.slnx"))) current = current.Parent;
            Repository = current!.FullName;
            Directory = Path.GetFullPath(Path.Combine(Repository, "artifacts/tmp/persistence-tests", Guid.NewGuid().ToString("N")));
            System.IO.Directory.CreateDirectory(Directory);
        }
        public DataRootLease Lease() => DataRootLease.Open(new(Bootstrap, true, Root));
        public Process Start(string assembly, string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Directory, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(assembly);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["MABITOOL_BOOTSTRAP_DIRECTORY"] = Bootstrap;
            start.Environment["MABITOOL_RUNTIME_DIRECTORY"] = Path.Combine(Directory, "runtime");
            start.Environment["MABITOOL_API_PORT"] = "0";
            start.Environment["MABITOOL_ALLOWED_ORIGINS"] = "";
            start.Environment.Remove("MABITOOL_API_TOKEN");
            return Process.Start(start)!;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
