using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Life;

/// <summary>
/// 持久保存的总队列（单行快照，格式版本见 <see cref="LifeQueueStore.FormatVersion"/>）。<c>Tasks</c> 是待执行与正在执行的任务（按顺序），
/// <c>History</c> 是已结束的记录；暂停原因随快照保存，宿主重启后统一改为“工具重启后暂停”。
/// </summary>
public sealed record LifeQueueRecord(int Version, IReadOnlyList<LifeTask> Tasks, IReadOnlyList<LifeTask> History, string? PauseCode, string? PauseDetail,
    string? PauseTaskId);

/// <summary>
/// 总队列的持久存储：数据库里的单行快照（迁移 <c>life-queue</c> v1）。写入按最新快照合并，在后台依次写，不在业务锁里等待数据库；
/// 没有数据根时（模拟宿主、无界面测试）保存在内存里，同一个存储对象在服务重建后仍能读回，用来模拟宿主重启。
/// </summary>
public sealed class LifeQueueStore(SqliteStore? database) : IDisposable
{
    public const int FormatVersion = 1;

    public static DatabaseMigration Migration { get; } = new("life-queue", 1,
        "CREATE TABLE life_queue(id INTEGER PRIMARY KEY CHECK(id=1), snapshot TEXT NOT NULL) STRICT;");

    private readonly object gate = new();
    private string? memory, pending;
    private Task writer = Task.CompletedTask;
    private bool disposed;

    /// <summary>读出保存的队列；没有保存过、格式版本更高或内容损坏时为空（损坏的快照不阻止宿主启动，队列从空开始）。</summary>
    public LifeQueueRecord? Read()
    {
        string? json;
        if (database is null) { lock (gate) json = pending ?? memory; }
        else json = database.ReadScalar("SELECT snapshot FROM life_queue WHERE id=1") as string;
        if (json is null) return null;
        try
        {
            var record = JsonSerializer.Deserialize<LifeQueueRecord>(json, ContractJson.Options);
            return record is { Version: >= 1 and <= FormatVersion } ? record : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>记下最新快照；数据库写入在后台进行，连续的保存只写最后一份。</summary>
    public void Save(LifeQueueRecord record)
    {
        var json = JsonSerializer.Serialize(record, ContractJson.Options);
        lock (gate)
        {
            if (disposed) return;
            if (database is null) { memory = json; return; }
            var idle = pending is null;
            pending = json;
            if (idle) writer = writer.ContinueWith(_ => WriteAsync(), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task WriteAsync()
    {
        while (true)
        {
            string? json;
            lock (gate) { json = pending; pending = null; }
            if (json is null) return;
            try
            {
                await database!.WriteAsync(tx =>
                {
                    tx.Execute("INSERT INTO life_queue VALUES(1,$snapshot) ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot", ("$snapshot", json));
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or IOException or System.Data.Common.DbException) { }
        }
    }

    /// <summary>等后台写入完成（测试与退出时用）。</summary>
    public Task FlushAsync()
    {
        lock (gate) return writer;
    }

    public void Dispose()
    {
        Task last;
        lock (gate) { last = writer; }
        try { last.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        lock (gate) disposed = true;
    }
}
