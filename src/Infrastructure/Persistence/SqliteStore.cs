using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

public sealed partial class SqliteStore : IAsyncDisposable
{
    public const int WriteQueueCapacity = 128;
    public const int MaximumOutboxEvents = 10000;
    public static readonly TimeSpan PublishedEventRetention = TimeSpan.FromMinutes(10);
    public const int MaximumEventBytes = 64 * 1024;
    /// <summary>写事务设计预算（软预算）：超过时照常提交并记录遥测。</summary>
    public static readonly TimeSpan TransactionBudget = TimeSpan.FromMilliseconds(250);
    /// <summary>写事务硬上限：超过时整笔回滚；取值按基准机实测校准。</summary>
    public static readonly TimeSpan TransactionHardLimit = TimeSpan.FromMilliseconds(2000);
    /// <summary>读槽全部占用时的最长等待；与设计预算一致，超过说明存在长读或积压。</summary>
    public static readonly TimeSpan ReadCapacityWait = TimeSpan.FromMilliseconds(250);
    public const int RetainedBudgetSamples = 32;
    private readonly PathService paths;
    private readonly TimeProvider storageClock;
    private readonly SqliteConnection writer;
    private readonly Channel<IWriteJob> queue = Channel.CreateBounded<IWriteJob>(new BoundedChannelOptions(WriteQueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly SemaphoreSlim readers = new(8, 8);
    private readonly SemaphoreSlim outboxSignal = new(0, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private readonly IDisposable databaseOwnership;
    private readonly object disposalGate = new();
    private Task? disposalTask;
    private int stopped;
    public ActivityBarrier Activities { get; }
    public VolumeSpaceReservations Space { get; }
    public TemporaryStorageRegistry TemporaryFiles { get; }
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");
    internal bool IsStopping => Volatile.Read(ref stopped) != 0;
    internal TimeProvider StorageClock => storageClock;
    internal Action<SqliteWriteTiming>? WriteTimingObserver { get; set; }
    /// <summary>超过设计预算或硬上限的写事务遥测；观察器失败不改变写入结果。</summary>
    internal Action<SqliteTransactionBudgetSample>? TransactionBudgetObserver { get; set; }
    internal TimeSpan SoftTransactionBudget { get; set; } = TransactionBudget;
    internal TimeSpan HardTransactionLimit { get; set; } = TransactionHardLimit;
    internal TimeSpan ReadSlotWait { get; set; } = ReadCapacityWait;
    private readonly Queue<SqliteTransactionBudgetSample> budgetSamples = new();
    private long softBudgetExceeded, hardLimitExceeded;
    internal long SoftBudgetExceededCount => Interlocked.Read(ref softBudgetExceeded);
    internal long HardLimitExceededCount => Interlocked.Read(ref hardLimitExceeded);
    internal IReadOnlyList<SqliteTransactionBudgetSample> RecentBudgetSamples()
    {
        lock (budgetSamples) return budgetSamples.ToArray();
    }

    private void RecordBudget(SqliteTransactionBudgetSample sample, bool hard)
    {
        if (hard) Interlocked.Increment(ref hardLimitExceeded);
        else Interlocked.Increment(ref softBudgetExceeded);
        lock (budgetSamples)
        {
            if (budgetSamples.Count == RetainedBudgetSamples) budgetSamples.Dequeue();
            budgetSamples.Enqueue(sample);
        }
        try { TransactionBudgetObserver?.Invoke(sample); } catch (Exception) { }
    }

    private SqliteStore(PathService paths, SqliteConnection writer, ActivityBarrier activities, IDisposable databaseOwnership,
        VolumeSpaceReservations space, TimeProvider clock)
    {
        this.paths = paths;
        storageClock = clock;
        this.writer = writer;
        Activities = activities;
        Space = space;
        TemporaryFiles = new(this, paths, clock, RuntimeId);
        this.databaseOwnership = databaseOwnership;
        worker = Task.Factory.StartNew(ProcessQueue, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public static SqliteStore Open(DataRootLease lease, IReadOnlyList<DatabaseMigration>? moduleMigrations = null, bool mock = false,
        ActivityBarrier? activityBarrier = null, VolumeSpaceReservations? spaceReservations = null, TimeProvider? clock = null)
    {
        var ownership = lease.AcquireDatabaseOwnership();
        try { return OpenOwned(lease, ownership, moduleMigrations, mock, activityBarrier, spaceReservations ?? VolumeSpaceReservations.Shared, clock ?? TimeProvider.System); }
        catch { ownership.Dispose(); throw; }
    }

    private static SqliteStore OpenOwned(DataRootLease lease, IDisposable ownership,
        IReadOnlyList<DatabaseMigration>? moduleMigrations, bool mock, ActivityBarrier? activityBarrier, VolumeSpaceReservations space, TimeProvider clock)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = lease.Paths.Database;
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) LocalDiscoverySecurity.RejectLinks(path + suffix);
        if (lease.DatabaseId is not null && !File.Exists(path))
            throw new StorageException("storage.databaseMissing", "已初始化数据库丢失，请恢复备份；不会自动创建空库");
        var existed = File.Exists(path) && new FileInfo(path).Length > 0;
        if (lease.DatabaseId is not null && !existed)
            throw new StorageException("storage.databaseIdentityMismatch", "已初始化数据库为空或身份无效");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = lease.DatabaseId is null ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            string databaseId;
            if (existed)
            {
                using var identity = connection.CreateCommand();
                identity.CommandText = "SELECT root_id, database_id FROM core_database_identity WHERE singleton=1";
                using var row = identity.ExecuteReader();
                if (!row.Read() || row.GetString(0) != lease.Paths.RootId || !Guid.TryParseExact(row.GetString(1), "N", out _) ||
                    lease.DatabaseId is not null && row.GetString(1) != lease.DatabaseId)
                    throw new StorageException("storage.databaseIdentityMismatch", "数据库不属于当前数据根");
                databaseId = row.GetString(1);
            }
            else
            {
                databaseId = Guid.NewGuid().ToString("N");
                using var tx = connection.BeginTransaction();
                using var initialize = connection.CreateCommand();
                initialize.Transaction = tx;
                initialize.CommandText = """
                    CREATE TABLE core_database_identity (singleton INTEGER PRIMARY KEY CHECK(singleton=1), root_id TEXT NOT NULL, database_id TEXT NOT NULL) STRICT;
                    CREATE TABLE core_migrations (module_id TEXT NOT NULL, version INTEGER NOT NULL, checksum TEXT NOT NULL, PRIMARY KEY(module_id, version)) STRICT;
                    INSERT INTO core_database_identity VALUES(1, $root, $id);
                    CREATE TABLE core_backend_mode (singleton INTEGER PRIMARY KEY CHECK(singleton=1), mode TEXT NOT NULL CHECK(mode IN ('live','mock'))) STRICT;
                    INSERT INTO core_backend_mode VALUES(1,$mode);
                    """;
                initialize.Parameters.AddWithValue("$mode", mock ? "mock" : "live");
                initialize.Parameters.AddWithValue("$root", lease.Paths.RootId);
                initialize.Parameters.AddWithValue("$id", databaseId);
                initialize.ExecuteNonQuery();
                tx.Commit();
            }
            using (var policy = connection.CreateCommand())
            {
                policy.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=1000;";
                policy.ExecuteNonQuery();
            }
            // 旧数据库在模式迁移前按正式数据处理，禁止模拟启动改写它。
            if (existed)
            {
                using var mode = connection.CreateCommand();
                mode.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='core_backend_mode'";
                var hasMode = (long)mode.ExecuteScalar()! != 0;
                if (!hasMode)
                {
                    mode.CommandText = "SELECT count(*) FROM core_migrations WHERE module_id='core' AND version>=4";
                    if ((long)mode.ExecuteScalar()! != 0)
                        throw new StorageException("storage.databaseInvalid", "已迁移数据库缺少后端模式身份");
                }
                mode.CommandText = "SELECT mode FROM core_backend_mode WHERE singleton=1";
                var savedMode = hasMode ? mode.ExecuteScalar() as string : "live";
                if (savedMode != (mock ? "mock" : "live"))
                    throw new StorageException("storage.configurationConflict", "模拟与正式数据根不能混用");
            }
            ApplyMigrations(connection, [CoreMigrations.Initial, CoreMigrations.Operations, CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode, .. moduleMigrations ?? []]);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                if (!string.Equals((string?)pragma.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase))
                    throw new StorageException("storage.walUnavailable", "数据库无法启用 WAL");
                // WAL 在检查点重置后截回 64 MiB 以内，长时间写入不保留历史峰值大小。
                pragma.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1000; PRAGMA wal_autocheckpoint=1000; PRAGMA journal_size_limit=67108864;";
                pragma.ExecuteNonQuery();
                pragma.CommandText = "PRAGMA quick_check;";
                if ((string?)pragma.ExecuteScalar() != "ok") throw new StorageException("storage.databaseDamaged", "数据库完整性检查失败");
                pragma.CommandText = "PRAGMA foreign_key_check;";
                if (pragma.ExecuteScalar() is not null) throw new StorageException("storage.databaseDamaged", "数据库外键完整性检查失败");
            }
            lease.SealDatabaseId(databaseId);
            return new(lease.Paths, connection, activityBarrier ?? new(), ownership, space, clock);
        }
        catch (Exception error)
        {
            connection.Dispose();
            if (error is SqliteException) throw new StorageException("storage.databaseInvalid", "数据库打开、迁移或完整性验证失败", error);
            throw;
        }
    }

    private static void ApplyMigrations(SqliteConnection connection, IReadOnlyList<DatabaseMigration> migrations)
    {
        var known = MigrationCatalog(migrations);
        var applied = new HashSet<(string, int)>();
        var legacy = new List<DatabaseMigration>();
        using (var history = connection.CreateCommand())
        {
            history.CommandText = "SELECT module_id, version, checksum FROM core_migrations ORDER BY module_id, version";
            using var rows = history.ExecuteReader();
            var versions = new Dictionary<string, int>();
            while (rows.Read())
            {
                var key = (rows.GetString(0), rows.GetInt32(1));
                var stored = rows.GetString(2);
                if (!known.TryGetValue(key, out var expected) || !expected.Matches(stored) ||
                    key.Item2 != versions.GetValueOrDefault(key.Item1) + 1)
                    throw new StorageException("storage.migrationIncompatible", "数据库迁移历史超前、缺失或内容发生变化");
                if (stored != expected.Checksum) legacy.Add(expected);
                versions[key.Item1] = key.Item2;
                applied.Add(key);
            }
        }
        var pending = migrations.Where(m => !applied.Contains((m.ModuleId, m.Version)))
            .OrderBy(m => m.ModuleId == "core" ? "" : m.ModuleId, StringComparer.Ordinal).ThenBy(m => m.Version).ToArray();
        if (pending.Length == 0 && legacy.Count == 0) return;
        // 同次启动的迁移与历史一起提交；失败时回到整批之前，避免逐迁移 FULL 刷盘。
        using var transaction = connection.BeginTransaction();
        // 旧版按源文件换行写入的校验和已由 Matches 核实为同一 SQL，随同一事务改记为规范化值；
        // 更新器、健康检查与备份印记只比较记录值，统一后不需要迁移 SQL 也能核对。
        foreach (var migration in legacy)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE core_migrations SET checksum=$checksum WHERE module_id=$module AND version=$version";
            command.Parameters.AddWithValue("$module", migration.ModuleId);
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$checksum", migration.Checksum);
            command.ExecuteNonQuery();
        }
        foreach (var migration in pending)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 1;
            command.CommandText = migration.Sql;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO core_migrations VALUES($module, $version, $checksum)";
            command.Parameters.AddWithValue("$module", migration.ModuleId);
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$checksum", migration.Checksum);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static Dictionary<(string ModuleId, int Version), DatabaseMigration> MigrationCatalog(IReadOnlyList<DatabaseMigration> migrations)
    {
        foreach (var group in migrations.GroupBy(m => m.ModuleId))
            if (!Regex.IsMatch(group.Key, @"\A[a-z][a-z0-9-]{0,63}\z") ||
                !group.OrderBy(m => m.Version).Select(m => m.Version).SequenceEqual(Enumerable.Range(1, group.Count())) ||
                group.Any(m => string.IsNullOrWhiteSpace(m.Sql)))
                throw new StorageException("storage.migrationInvalid", "模块迁移必须从 1 开始连续编号且内容非空");
        return migrations.ToDictionary(m => (m.ModuleId, m.Version));
    }

    public Task<T> WriteAsync<T>(Func<DatabaseTransaction, T> callback, CancellationToken cancellationToken = default)
        => EnqueueWrite(callback, cancellationToken);

    internal Task<T> WriteMaintenanceAsync<T>(ActivityBarrier.MaintenanceLease maintenance,
        Func<DatabaseTransaction, T> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        return EnqueueWrite(callback, cancellationToken, maintenance);
    }

    private Task<T> EnqueueWrite<T>(Func<DatabaseTransaction, T> callback, CancellationToken cancellationToken,
        ActivityBarrier.MaintenanceLease? maintenance = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (typeof(T).GetMethod("GetAwaiter") is not null || typeof(Task).IsAssignableFrom(typeof(T)))
            throw new ArgumentException("数据库事务回调必须同步返回，不能返回异步任务");
        if (Volatile.Read(ref stopped) != 0) throw new ObjectDisposedException(nameof(SqliteStore));
        cancellationToken.ThrowIfCancellationRequested();
        var activity = maintenance is null ? Activities.EnterActivity("sqlite", "storage.write") :
            Activities.EnterMaintenanceActivity(maintenance, "storage.maintenanceWrite");
        var job = new WriteJob<T>(callback, cancellationToken, activity);
        if (!queue.Writer.TryWrite(job))
        {
            activity.Dispose();
            throw new StorageException("storage.writeQueueFull", "数据库写队列已满，请稍后重试");
        }
        return job.Task;
    }

    private void ProcessQueue()
    {
        try
        {
            while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (queue.Reader.TryRead(out var job)) job.Run(this);
        }
        finally
        {
            try
            {
                using var checkpoint = writer.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.ExecuteNonQuery();
            }
            catch (SqliteException) { }
            writer.Dispose();
        }
    }

    private interface IWriteJob { void Run(SqliteStore store); }
    private sealed class WriteJob<T>(Func<DatabaseTransaction, T> callback, CancellationToken cancellation, IDisposable activity) : IWriteJob
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // 分段计时常开，用于设计预算遥测；完整样本只在诊断观察器启用或超过设计预算时生成。
        private readonly SqliteWriteTrace timing = new(callback);
        public Task<T> Task => completion.Task;
        public void Run(SqliteStore store)
        {
            T result = default!;
            Exception? failure = null;
            CancellationToken? cancelled = null;
            DatabaseTransaction? context = null;
            var soft = store.SoftTransactionBudget; var hard = store.HardTransactionLimit;
            timing.QueueWaitTicks = Stopwatch.GetTimestamp() - timing.EnqueuedAt;
            try
            {
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation, store.shutdown.Token);
                combined.Token.ThrowIfCancellationRequested();
                store.shutdown.Token.ThrowIfCancellationRequested();
                var beginStarted = Stopwatch.GetTimestamp();
                SqliteTransaction transaction;
                try { transaction = store.writer.BeginTransaction(); }
                finally { timing.BeginTicks = Stopwatch.GetTimestamp() - beginStarted; }
                using (transaction)
                {
                    timing.MarkTransactionStart();
                    context = new DatabaseTransaction(store.writer, transaction, combined.Token, store.shutdown.Token, timing, hard) { Clock = store.storageClock };
                    try
                    {
                        var callbackStarted = Stopwatch.GetTimestamp();
                        try { result = callback(context); context.Check(); }
                        finally { timing.CallbackTicks = Stopwatch.GetTimestamp() - callbackStarted; }
                        var commitStarted = Stopwatch.GetTimestamp();
                        try { transaction.Commit(); }
                        finally { timing.CommitTicks = Stopwatch.GetTimestamp() - commitStarted; }
                        if (context.HasEvents) store.SignalOutbox();
                    }
                    finally { context.End(); }
                }
            }
            catch (OperationCanceledException) { cancelled = cancellation.IsCancellationRequested ? cancellation : store.shutdown.Token; }
            catch (Exception error)
            {
                if (error is StorageException { Code: "storage.transactionBudgetExceeded" })
                    error.Data["transactionCallback"] = timing.Callback;
                failure = error;
            }
            finally { activity.Dispose(); }
            var outcome = cancelled is not null ? "cancelled" : failure switch
            {
                null => "succeeded", StorageException error => error.Code, OperationException error => error.Code,
                _ => failure.GetType().Name
            };
            var hardExceeded = failure is StorageException { Code: "storage.transactionBudgetExceeded" };
            if (context is not null && (hardExceeded || context.CheckedElapsed > soft))
                store.RecordBudget(timing.BudgetSample(store.storageClock.GetUtcNow(), context.CheckedElapsed, soft, hard, outcome), hardExceeded);
            if (store.WriteTimingObserver is { } observer)
            {
                // 诊断消费失败不改变业务提交、回滚或取消结果。
                try { observer(timing.Snapshot(outcome)); } catch (Exception) { }
            }
            if (cancelled is { } token) completion.TrySetCanceled(token);
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult(result);
        }
    }

    /// <summary>在受限只读连接中查询模块拥有的数据；多条一致查询可在回调内建立只读事务。</summary>
    public T Read<T>(Func<SqliteConnection, T> read)
    {
        if (Volatile.Read(ref stopped) != 0) throw new ObjectDisposedException(nameof(SqliteStore));
        // 短时并发尖峰在有界等待内取得槽位；超过上限返回可重试错误，不长期阻塞调用线程。
        if (!readers.Wait(0) && !readers.Wait(ReadSlotWait)) throw new StorageException("storage.readCapacityExceeded", "数据库读并发已满");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = paths.Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            return read(connection);
        }
        finally { readers.Release(); }
    }

    public object? ReadScalar(string sql, params (string Name, object? Value)[] parameters) => Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var valueRead = command.ExecuteScalar();
        return valueRead is DBNull ? null : valueRead;
    });

    public static bool AppendEvent(DatabaseTransaction tx, StateEvent state) => AppendPreparedEvent(tx, PrepareEvent(state));

    internal static bool AppendPreparedEvent(DatabaseTransaction tx, PreparedStateEvent encoded)
    {
        var state = encoded.State; var payload = encoded.Payload; var content = encoded.Content;
        var existing = tx.Scalar(EventContentQuery + " WHERE event_id=$id", ("$id", state.EventId)) as string;
        if (existing is not null)
        {
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(existing), System.Text.Json.Nodes.JsonNode.Parse(content)))
                throw new StorageException("storage.eventConflict", "相同事件 ID 对应不同内容");
            return false;
        }
        tx.Execute("DELETE FROM core_outbox WHERE published=1 AND sequence IN (SELECT sequence FROM core_outbox WHERE published=1 ORDER BY sequence LIMIT max(0, (SELECT count(*) FROM core_outbox) - $limit + 1))", ("$limit", MaximumOutboxEvents));
        // 已发布事件另按时间回收：超过重放窗口两倍（10 分钟）的不再保留。
        tx.Execute("DELETE FROM core_outbox WHERE published=1 AND created_at < $cutoff",
            ("$cutoff", tx.Clock.GetUtcNow().Subtract(PublishedEventRetention).ToString("O")));
        if ((long)tx.Scalar("SELECT count(*) FROM core_outbox")! >= MaximumOutboxEvents)
            throw new StorageException("storage.outboxFull", "可靠通知积压已达到容量上限");
        tx.Execute("INSERT INTO core_outbox(event_id,resource_type,resource_id,revision,event_type,payload,created_at) VALUES($id,$rt,$rid,$revision,$type,$payload,$at)",
            ("$id", state.EventId), ("$rt", state.ResourceType), ("$rid", state.ResourceId), ("$revision", state.Revision.ToString()),
            ("$type", state.EventType), ("$payload", payload), ("$at", tx.Clock.GetUtcNow().ToString("O")));
        tx.HasEvents = true;
        return true;
    }

    private const string EventContentQuery = "SELECT json_object('eventId', event_id, 'resourceType', resource_type, 'resourceId', resource_id, 'revision', revision, 'eventType', event_type, 'payload', json(payload)) FROM core_outbox";

    public IReadOnlyList<PersistedEvent> ReadPendingEvents(int limit = 100)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        return Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sequence,event_id,resource_type,resource_id,revision,event_type,payload,created_at FROM core_outbox WHERE published=0 ORDER BY sequence LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            using var rows = command.ExecuteReader();
            var result = new List<PersistedEvent>();
            while (rows.Read()) result.Add(new(rows.GetInt64(0), new(rows.GetString(1), rows.GetString(2), rows.GetString(3),
                UInt64String.Parse(rows.GetString(4)), rows.GetString(5), JsonSerializer.Deserialize<JsonElement>(rows.GetString(6))), DateTimeOffset.Parse(rows.GetString(7), System.Globalization.CultureInfo.InvariantCulture)));
            return (IReadOnlyList<PersistedEvent>)result;
        });
    }

    public Task<bool> ApplyOnceAsync(string consumerId, PersistedEvent state, Action<DatabaseTransaction, StateEvent> apply,
        CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(consumerId, @"\A[a-z][a-z0-9.-]{0,63}\z")) throw new ArgumentException("消费者身份无效");
        return WriteAsync(tx =>
        {
            var durable = tx.Scalar(EventContentQuery + " WHERE sequence=$sequence AND event_id=$event", ("$sequence", state.Sequence), ("$event", state.Event.EventId)) as string;
            if (durable is null || !System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(durable),
                JsonSerializer.SerializeToNode(state.Event, ContractJson.Options)))
                throw new StorageException("storage.eventInvalid", "事件与当前 outbox 内容不一致");
            if ((long)tx.Scalar("SELECT count(*) FROM core_event_receipts WHERE consumer_id=$consumer AND event_id=$event", ("$consumer", consumerId), ("$event", state.Event.EventId))! != 0) return false;
            apply(tx, state.Event);
            tx.Execute("INSERT INTO core_event_receipts VALUES($consumer,$event)", ("$consumer", consumerId), ("$event", state.Event.EventId));
            return true;
        }, cancellationToken);
    }

    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    public async Task<int> DispatchPendingAsync(IStateEventSink sink, CancellationToken cancellationToken = default)
    {
        if (!await dispatchGate.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            var batch = ReadPendingEvents();
            using var activity = batch.Count == 0 ? null : Activities.EnterActivity("outbox", "storage.publish");
            foreach (var state in batch)
            {
                await sink.PublishAsync(state, cancellationToken);
                await WriteAsync(tx => tx.Execute("UPDATE core_outbox SET published=1 WHERE sequence=$sequence AND event_id=$id", ("$sequence", state.Sequence), ("$id", state.Event.EventId)), cancellationToken);
            }
            return batch.Count;
        }
        finally { dispatchGate.Release(); }
    }

    public Task<bool> ProjectAsync(PersistedEvent state, CancellationToken cancellationToken = default) => ApplyOnceAsync("core.state-projection", state, (tx, value) => ApplyProjection(tx, value), cancellationToken);

    internal async Task<bool> ProjectForStreamAsync(PersistedEvent state, CancellationToken cancellationToken)
    {
        var changed = false;
        await ApplyOnceAsync("core.state-projection", state, (tx, value) => changed = ApplyProjection(tx, value), cancellationToken);
        return changed;
    }

    private static bool ApplyProjection(DatabaseTransaction tx, StateEvent value)
    {
        var previous = tx.Scalar("SELECT revision FROM core_state_projection WHERE resource_type=$type AND resource_id=$id", ("$type", value.ResourceType), ("$id", value.ResourceId)) as string;
        if (previous is not null && UInt64String.Parse(previous).Value >= value.Revision.Value) return false;
        if (value.Payload.ValueKind == JsonValueKind.Object && value.Payload.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
        {
            tx.Execute("DELETE FROM core_state_projection WHERE resource_type=$type AND resource_id=$id", ("$type", value.ResourceType), ("$id", value.ResourceId));
            return true;
        }
        tx.Execute("INSERT INTO core_state_projection(resource_type,resource_id,revision,event_id,payload,event_type) VALUES($type,$id,$revision,$event,$payload,$eventType) ON CONFLICT(resource_type,resource_id) DO UPDATE SET revision=excluded.revision,event_id=excluded.event_id,payload=excluded.payload,event_type=excluded.event_type",
            ("$type", value.ResourceType), ("$id", value.ResourceId), ("$revision", value.Revision.ToString()), ("$event", value.EventId), ("$payload", value.Payload.GetRawText()), ("$eventType", value.EventType));
        return true;
    }

    private void SignalOutbox()
    {
        try { outboxSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task RunOutboxAsync(IStateEventSink sink, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await DispatchPendingAsync(sink, cancellationToken) > 0) { }
                await outboxSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // 保留未确认事件；失败期间有限速重试，不能转为游戏动作重放。
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    public Task SaveOperationAsync(OperationSnapshot snapshot, CancellationToken cancellationToken = default) => WriteAsync(tx => SaveOperation(tx, snapshot), cancellationToken);

    internal static bool SaveOperation(DatabaseTransaction tx, OperationSnapshot snapshot)
    {
        if (tx.Timing is not { } timing) return SavePreparedOperation(tx, PrepareOperation(snapshot));
        var encodingStarted = Stopwatch.GetTimestamp();
        PreparedOperationRecord encoded;
        try { encoded = PrepareOperation(snapshot); }
        finally { timing.OperationEncodingTicks += Stopwatch.GetTimestamp() - encodingStarted; }
        return SavePreparedOperation(tx, encoded);
    }

    internal static bool SavePreparedOperation(DatabaseTransaction tx, PreparedOperationRecord encoded)
    {
        var snapshot = encoded.Snapshot; var json = encoded.Json;
        var prior = tx.Scalar("SELECT snapshot FROM core_operations WHERE id=$id", ("$id", snapshot.Id)) as string;
        if (prior is not null)
        {
            if (prior == json) return false;
            var previous = JsonSerializer.Deserialize<OperationSnapshot>(prior, ContractJson.Options)!;
            if (IsTerminal(previous.State.KnownValue) || previous.Revision.Value == ulong.MaxValue || snapshot.Revision.Value != previous.Revision.Value + 1)
                throw new StorageException("storage.operationRevisionConflict", "操作修订冲突或已进入终态");
        }
        tx.Execute("INSERT INTO core_operations VALUES($id,$snapshot,$terminal) ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot,terminal=excluded.terminal",
            ("$id", snapshot.Id), ("$snapshot", json), ("$terminal", IsTerminal(snapshot.State.KnownValue) ? 1 : 0));
        foreach (var state in encoded.Events) AppendPreparedEvent(tx, state);
        return true;
    }

    public OperationSnapshot? GetOperation(string id) => Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM core_operations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<OperationSnapshot>(json, ContractJson.Options);
    });

    public async Task<int> RecoverOperationsAsync(CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (true)
        {
            var recovered = await WriteAsync(tx =>
            {
                var json = tx.Scalar("SELECT snapshot FROM core_operations WHERE terminal=0 ORDER BY id LIMIT 1") as string;
                if (json is null) return false;
                var previous = JsonSerializer.Deserialize<OperationSnapshot>(json, ContractJson.Options)!;
                var snapshot = previous with
                {
                    State = OperationState.Interrupted, Phase = "backend-restarted", Revision = new(checked(previous.Revision.Value + 1)),
                    EffectStatus = OperationStore.IsRoomRoundOperation(previous.Kind) && previous.Phase is "queued" or "validating"
                        ? EffectStatus.None : EffectStatus.Unknown,
                    Timestamps = previous.Timestamps with { CompletedAt = DateTimeOffset.UtcNow },
                    Cancellation = new(CancellationState.Completed, "backend-restarted")
                };
                if (snapshot.Kind == "color.search")
                    snapshot = snapshot with { Result = JsonSerializer.SerializeToElement(OperationStore.CloseColor(OperationStore.ReadColor(previous),
                        "interrupted", "backend-restarted") with { Revision = snapshot.Revision }, ContractJson.Options) };
                SaveOperation(tx, snapshot);
                tx.Execute("UPDATE core_module_configuration SET pending_operation=NULL WHERE pending_operation=$id", ("$id", snapshot.Id));
                return true;
            }, cancellationToken);
            if (!recovered)
            {
                // 启动时按保留条数裁剪寻色历史：覆盖刚中断的运行，以及升级前已超出条数的旧记录。
                await WriteAsync(tx => OperationStore.PruneColorHistory(tx), cancellationToken);
                return total;
            }
            total++;
        }
    }

    public static bool IsTerminal(OperationState? state) => state is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.Interrupted;

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposalTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref stopped, 1);
        try { await shutdown.CancelAsync().ConfigureAwait(false); }
        finally
        {
            queue.Writer.TryComplete();
            try { await worker.ConfigureAwait(false); }
            finally
            {
                await databaseBackupGate.WaitAsync().ConfigureAwait(false);
                databaseBackupGate.Release();
                // Read 在取得槽位后复查 stopped；已进入回调的连接全部退出才交还文件所有权。
                for (var i = 0; i < 8; i++) await readers.WaitAsync().ConfigureAwait(false);
                databaseOwnership.Dispose();
                shutdown.Dispose();
            }
        }
    }
}
