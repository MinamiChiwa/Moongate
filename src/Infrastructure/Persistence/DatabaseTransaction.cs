using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

// 该类型只在所属同步事务回调期间有效；模块不得保留它或执行外部 I/O。
public sealed class DatabaseTransaction
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly CancellationToken cancellation;
    private readonly CancellationToken shutdown;
    private readonly TimeSpan hardLimit;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private bool active = true;
    internal bool HasEvents { get; set; }
    /// <summary>所属存储的时钟；可靠通知的写入时间与按时间回收使用它。</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    /// <summary>所属存储；可靠通知按周期回收时使用它记录上次回收时刻。</summary>
    internal SqliteStore? Owner { get; init; }
    internal SqliteWriteTrace? Timing { get; }
    /// <summary>最近一次检查时的事务耗时；提交前检查后即为计入预算的耗时。</summary>
    internal TimeSpan CheckedElapsed { get; private set; }

    internal DatabaseTransaction(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellation,
        CancellationToken shutdown, SqliteWriteTrace? timing = null, TimeSpan? hardLimit = null)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.cancellation = cancellation;
        this.shutdown = shutdown;
        this.hardLimit = hardLimit ?? SqliteStore.TransactionHardLimit;
        Timing = timing;
    }

    internal void Check()
    {
        if (!active) throw new InvalidOperationException("事务回调已经结束");
        if (Environment.CurrentManagedThreadId != threadId) throw new InvalidOperationException("事务只能由所属写入线程使用");
        cancellation.ThrowIfCancellationRequested();
        // CancelAsync 异步转发到链接令牌；提交和 SQL 检查直接读取原关闭信号。
        shutdown.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.GetElapsedTime(started);
        CheckedElapsed = elapsed;
        // 设计预算只记录遥测；硬上限拦截失控事务并整笔回滚。
        if (elapsed > hardLimit)
        {
            var error = new StorageException("storage.transactionBudgetExceeded", "写事务超过执行硬上限，变更已回滚");
            error.Data["transactionElapsedMs"] = (long)elapsed.TotalMilliseconds; throw error;
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        Check();
        var preparationStarted = Timing is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 1;
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }
        finally
        {
            if (Timing is { } timing)
            {
                timing.SqlPrepareTicks += Stopwatch.GetTimestamp() - preparationStarted;
                timing.SqlCommands++;
            }
        }
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var executionStarted = Timing is null ? 0 : Stopwatch.GetTimestamp();
        int result;
        try { result = command.ExecuteNonQuery(); }
        finally { if (Timing is { } timing) timing.SqlExecuteTicks += Stopwatch.GetTimestamp() - executionStarted; }
        Check();
        return result;
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var executionStarted = Timing is null ? 0 : Stopwatch.GetTimestamp();
        object? result;
        try { result = command.ExecuteScalar(); }
        finally { if (Timing is { } timing) timing.SqlExecuteTicks += Stopwatch.GetTimestamp() - executionStarted; }
        Check();
        return result is DBNull ? null : result;
    }

    internal void End() => active = false;
}
