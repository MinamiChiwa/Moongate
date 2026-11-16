using System.Diagnostics;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Host.Storage;

namespace MabiTool.Host.Diagnostics;

/// <summary>日志调用只尝试入队；文件与数据库写入在后台串行执行。</summary>
public sealed class DiagnosticRecorder : BackgroundService, ILoggerProvider
{
    private readonly DiagnosticStore store;
    private readonly MaintenanceShutdownContext? shutdown;
    private readonly DiagnosticTrace? trace;
    /// <param name="trace">提供时日志与问题记录经诊断入口分配会话序号并分流到调试采集；默认记录仍写入本记录器。</param>
    public DiagnosticRecorder(DiagnosticStore store, TimeProvider? clock = null, MaintenanceShutdownContext? shutdown = null, DiagnosticTrace? trace = null)
    {
        this.store = store; this.shutdown = shutdown; this.trace = trace; time = clock ?? TimeProvider.System;
        trace?.AttachPersistence(Enqueue);
    }
    private sealed record Work(DiagnosticRecord? Record, TaskCompletionSource? Flushed);
    private readonly TimeProvider time;
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private volatile bool accepting = true;
    private long dropped;
    public ulong DroppedRecords => (ulong)Interlocked.Read(ref dropped);
    public string? LastError { get; private set; }
    public string? SessionId { get; private set; }
    public int QueuedRecords => queue.Reader.Count;
    public override async Task StartAsync(CancellationToken ct)
    {
        await store.RecoverAsync(ct);
        SessionId = (await store.BeginAsync("owner", "core", ct)).Id;
        await base.StartAsync(ct);
    }
    public bool Record(DiagnosticRecord record, Exception? exception = null) => trace is null ? Enqueue(record) : trace.Submit(record, exception);
    private bool Enqueue(DiagnosticRecord record)
    {
        record = DiagnosticSanitizer.Create(record.OccurredAt, record.Level, record.Module, record.Code, record.TraceId, record.OperationId,
            record.ProviderVersion, record.ExceptionType, record.Component, record.Event, record.Fields, record.Sequence,
            httpMethod: record.HttpMethod, route: record.Route);
        if (!accepting || !queue.Writer.TryWrite(new(record, null))) { Interlocked.Increment(ref dropped); return false; }
        return true;
    }
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new(null, done), ct);
        await done.Task.WaitAsync(ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var pulses = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var pulseLoop = PulseAsync(pulses.Token);
        var nextCleanup = time.GetTimestamp();
        try
        {
          await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
          {
            var records = new List<DiagnosticRecord>(); var confirmations = new List<TaskCompletionSource>();
            Add(work);
            while (records.Count + confirmations.Count < 64 && queue.Reader.TryRead(out var next)) Add(next);
            try
            {
                if (records.Count > 0) await store.AppendAsync(SessionId!, records, stoppingToken);
                if (time.GetElapsedTime(nextCleanup) >= TimeSpan.FromSeconds(30))
                { await store.CleanupAsync(stoppingToken); nextCleanup = time.GetTimestamp(); }
                foreach (var done in confirmations) done.TrySetResult();
            }
            catch (Exception error) when (error is StorageException or OperationException or ActivityBarrierException or IOException or UnauthorizedAccessException)
            {
                Interlocked.Add(ref dropped, records.Count);
                LastError = error switch { StorageException e => e.Code, OperationException e => e.Code, ActivityBarrierException e => e.Code, _ => "storage.unavailable" };
                foreach (var done in confirmations) done.TrySetException(new OperationException("storage.unavailable", "诊断写入未完成"));
            }
            void Add(Work item) { if (item.Record is { } record) records.Add(record); if (item.Flushed is { } flushed) confirmations.Add(flushed); }
          }
        }
        finally { pulses.Cancel(); await pulseLoop; }
    }
    private async Task PulseAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        try { while (await timer.WaitForNextTickAsync(ct)) queue.Writer.TryWrite(new(null, null)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        accepting = false; queue.Writer.TryComplete();
        if (ExecuteTask is { } execution) await execution.WaitAsync(ct);
        if (SessionId is { } id) await store.CompleteAsync(id, ct: ct, maintenance: shutdown?.Lease);
        await base.StopAsync(ct);
    }
    public ILogger CreateLogger(string categoryName) => new StructuredLogger(this, categoryName, time);
    void IDisposable.Dispose() => base.Dispose();
    /// <summary>日志类别到模块：与运行状态探针的 module 取值一致，其余记为 core。</summary>
    internal static string ModuleOf(string category)
    {
        static bool Under(string category, string prefix) => category.StartsWith(prefix, StringComparison.Ordinal) &&
            (category.Length == prefix.Length || category[prefix.Length] == '.');
        return Under(category, "MabiTool.Host.Color") ? "color" : Under(category, "MabiTool.Host.Rooms") ? "rooms" :
            Under(category, "MabiTool.Host.Combat") || Under(category, "MabiTool.Modules.CombatObservation") ? "combat-observation" :
            Under(category, "MabiTool.Host.Inventory") || Under(category, "MabiTool.Modules.Inventory") ? "inventory" :
            Under(category, "MabiTool.Host.Game") || Under(category, "MabiTool.Providers") ? "game-access" :
            Under(category, "MabiTool.Host.Performance") || Under(category, "MabiTool.Modules.MusicLibrary") ||
                category.StartsWith("MabiTool.Host.MusicImport", StringComparison.Ordinal) ? "music-library" : "core";
    }
    private sealed class StructuredLogger(DiagnosticRecorder recorder, string category, TimeProvider time) : ILogger
    {
        public bool IsEnabled(LogLevel level) => level is LogLevel.Warning or LogLevel.Error or LogLevel.Critical;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var values = state as IEnumerable<KeyValuePair<string, object?>>;
            string? Field(string name) => values?.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value as string;
            var code = exception switch { OperationException e => e.Code, StorageException e => e.Code,
                ActivityBarrierException e => e.Code, _ => Field("code") };
            // 日志类别与事件号随记录保存（类别过长或不合白名单时由清洗丢弃），没有登记错误码的日志也能定位来源。
            var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["category"] = category };
            if (eventId.Id != 0) fields["eventId"] = eventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            recorder.Record(DiagnosticSanitizer.Create(time.GetUtcNow(), level == LogLevel.Warning ? "warning" : "error", ModuleOf(category),
                code, Activity.Current?.TraceId.ToString(), Field("operationId"), Field("providerVersion"), exception?.GetType().Name,
                DiagnosticEvents.HostLog.Component, DiagnosticEvents.HostLog.Code, fields), exception);
        }
    }
}
