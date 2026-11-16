using System.Diagnostics;

namespace MabiTool.Infrastructure;

internal sealed record SqliteWriteTiming(string Callback, double QueueWaitMs, double BeginMs, double CallbackMs,
    double SqlPrepareMs, double SqlExecuteMs, double OperationEncodingMs, double CommitMs, int SqlCommands, string Outcome);

/// <summary>一次超过设计预算的写事务；硬上限回滚时 Outcome 为错误码。</summary>
internal sealed record SqliteTransactionBudgetSample(DateTimeOffset OccurredAt, double BudgetElapsedMs, double SoftBudgetMs,
    double HardLimitMs, SqliteWriteTiming Timing, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    double GcPauseMs, int ProcessorCount)
{
    public string Outcome => Timing.Outcome;
}

internal sealed class SqliteWriteTrace(Delegate callback)
{
    internal long EnqueuedAt { get; } = Stopwatch.GetTimestamp();
    internal long QueueWaitTicks;
    internal long BeginTicks;
    internal long CallbackTicks;
    internal long SqlPrepareTicks;
    internal long SqlExecuteTicks;
    internal long OperationEncodingTicks;
    internal long CommitTicks;
    internal int SqlCommands;
    private int gen0, gen1, gen2;
    private TimeSpan pause;

    internal string Callback => callback.Method.DeclaringType?.FullName + "." + callback.Method.Name;

    // 事务开始时记录 GC 计数；仅在超过设计预算时计算差值。
    internal void MarkTransactionStart()
    {
        gen0 = GC.CollectionCount(0); gen1 = GC.CollectionCount(1); gen2 = GC.CollectionCount(2);
        pause = GC.GetTotalPauseDuration();
    }

    internal SqliteWriteTiming Snapshot(string outcome) => new(Callback, Milliseconds(QueueWaitTicks), Milliseconds(BeginTicks),
        Milliseconds(CallbackTicks), Milliseconds(SqlPrepareTicks), Milliseconds(SqlExecuteTicks),
        Milliseconds(OperationEncodingTicks), Milliseconds(CommitTicks), SqlCommands, outcome);

    internal SqliteTransactionBudgetSample BudgetSample(DateTimeOffset at, TimeSpan elapsed, TimeSpan soft, TimeSpan hard, string outcome) =>
        new(at, elapsed.TotalMilliseconds, soft.TotalMilliseconds, hard.TotalMilliseconds, Snapshot(outcome),
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
            (GC.GetTotalPauseDuration() - pause).TotalMilliseconds, Environment.ProcessorCount);

    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
}
