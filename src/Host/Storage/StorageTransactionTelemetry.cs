using MabiTool.Infrastructure;

namespace MabiTool.Host.Storage;

/// <summary>把超过设计预算或挂起上限的写事务样本、超过设计预算的读槽等待写入宿主日志。</summary>
internal static class StorageTransactionTelemetry
{
    private const int MaximumPending = 64;

    // 观察器在写线程上运行，只做有界入队；日志工厂在线程池上解析，避免启动期间写线程等待依赖注入。
    public static SqliteStore Attach(SqliteStore store, IServiceProvider services)
    {
        ILogger? logger = null; var pending = 0;
        store.TransactionBudgetObserver = sample =>
        {
            if (Interlocked.Increment(ref pending) > MaximumPending) { Interlocked.Decrement(ref pending); return; }
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try { Write(logger ??= services.GetRequiredService<ILoggerFactory>().CreateLogger("MabiTool.Host.Storage.TransactionBudget"), sample); }
                catch (Exception) { }
                finally { Interlocked.Decrement(ref pending); }
            }, null);
        };
        store.ReadSlotWaitObserver = waited =>
        {
            if (Interlocked.Increment(ref pending) > MaximumPending) { Interlocked.Decrement(ref pending); return; }
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    (logger ??= services.GetRequiredService<ILoggerFactory>().CreateLogger("MabiTool.Host.Storage.TransactionBudget"))
                        .LogWarning("数据库读槽等待 {waitedMs:F1} ms，超过设计预算 {budgetMs} ms", waited.TotalMilliseconds, SqliteStore.ReadSlotBudget.TotalMilliseconds);
                }
                catch (Exception) { }
                finally { Interlocked.Decrement(ref pending); }
            }, null);
        };
        return store;
    }

    private static void Write(ILogger logger, SqliteTransactionBudgetSample sample)
    {
        var hard = sample.Outcome == "storage.transactionBudgetExceeded"; var timing = sample.Timing;
        logger.LogWarning("写事务超过{limit}：{callback} 计入预算 {elapsedMs:F1} ms（设计预算 {softMs} ms，挂起上限 {hardMs} ms），结果 {outcome}，" +
            "代码 {code}；排队 {queueMs:F1}、建立 {beginMs:F1}、回调 {callbackMs:F1}、SQL 准备 {sqlPrepareMs:F1}、SQL 执行 {sqlExecuteMs:F1}、" +
            "编码 {encodingMs:F1}、提交 {commitMs:F1} ms，SQL {sqlCommands} 条；GC {gen0}/{gen1}/{gen2} 次、暂停 {gcPauseMs:F1} ms，处理器 {processors}",
            hard ? "挂起上限" : "设计预算", timing.Callback, sample.BudgetElapsedMs, sample.SoftBudgetMs, sample.HardLimitMs, sample.Outcome,
            hard ? sample.Outcome : null, timing.QueueWaitMs, timing.BeginMs, timing.CallbackMs, timing.SqlPrepareMs, timing.SqlExecuteMs,
            timing.OperationEncodingMs, timing.CommitMs, timing.SqlCommands, sample.Gen0Collections, sample.Gen1Collections,
            sample.Gen2Collections, sample.GcPauseMs, sample.ProcessorCount);
    }
}
