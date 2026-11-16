using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Host.Storage;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Diagnostics;

/// <summary>
/// 限时调试采集：同一时刻最多一个，到期、提前结束或宿主停止时收尾。命中的记录进入 2,000 条内存环形缓冲，
/// 后台写入独立采集会话文件（8 MiB 上限），并按每秒 50 条推送 <c>diagnostics.record</c>。
/// </summary>
public sealed class DiagnosticCaptureService : BackgroundService, IDiagnosticCaptureSink
{
    public const int BufferCapacity = 2000, PushPerSecond = 50, MaximumDurationSeconds = 600, MaximumDetailBytesPerCapture = 1024 * 1024;
    public static readonly IReadOnlyList<string> Modules = ["core", "game-access", "combat-observation", "inventory", "music-library", "color", "rooms"];
    private sealed record Item(string CaptureId, DiagnosticRecord Record);
    private sealed record Receipt(string Hash, string CaptureId, long Created);
    private sealed class Active(DiagnosticCaptureSnapshot snapshot, CancellationTokenSource expiry)
    {
        public DiagnosticCaptureSnapshot Initial { get; } = snapshot;
        public CancellationTokenSource Expiry { get; } = expiry;
        public long Records, Dropped, StoreDropped, PushDropped, DetailBytes, DetailTruncated;
        public DiagnosticCaptureSnapshot Snapshot(string state = "active", string? reason = null, DateTimeOffset? ended = null) => Initial with
        {
            State = state, EndReason = reason, EndedAt = ended, Records = new((ulong)Interlocked.Read(ref Records)),
            DroppedRecords = new((ulong)(Interlocked.Read(ref Dropped) + Interlocked.Read(ref StoreDropped))),
            PushDropped = new((ulong)Interlocked.Read(ref PushDropped)), DetailBytes = new((ulong)Interlocked.Read(ref DetailBytes)),
            DetailTruncated = new((ulong)Interlocked.Read(ref DetailTruncated))
        };
    }

    private readonly DiagnosticTrace trace;
    private readonly DiagnosticStore store;
    private readonly DiagnosticRuntimeService runtime;
    private readonly StateEventHub hub;
    private readonly TimeProvider clock;
    private readonly MaintenanceShutdownContext? shutdown;
    private readonly DiagnosticDetailSanitizer details;
    private readonly Channel<Item> buffer;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private readonly object gate = new();
    private readonly Dictionary<(string Owner, string Key), Receipt> receipts = [];
    private readonly List<TaskCompletionSource> flushWaiters = [];
    private Active? current;
    private bool stopping;
    private long windowStart, windowCount;

    public DiagnosticCaptureService(DiagnosticTrace trace, DiagnosticStore store, DiagnosticRuntimeService runtime, StateEventHub hub,
        PathService paths, TimeProvider clock, MaintenanceShutdownContext? shutdown = null, string? installRoot = null)
    {
        this.trace = trace; this.store = store; this.runtime = runtime; this.hub = hub; this.clock = clock; this.shutdown = shutdown;
        details = new(paths.DataRoot, installRoot ?? AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        buffer = Channel.CreateBounded<Item>(new BoundedChannelOptions(BufferCapacity)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }, dropped =>
            { if (Volatile.Read(ref current) is { } active && active.Initial.Id == dropped.CaptureId) Interlocked.Increment(ref active.Dropped); });
    }

    internal DiagnosticDetailSanitizer Details => details;

    public void Accept(DiagnosticCaptureGate capture, DiagnosticRecord record, Exception? exception, string? detail)
    {
        var active = Volatile.Read(ref current);
        if (active is null || active.Initial.Id != capture.Id) return;
        string? text = null;
        if (capture.IncludeDetail && (exception is not null || detail is not null))
        {
            text = details.Compose(exception, detail);
            if (text is not null)
            {
                var bytes = Encoding.UTF8.GetByteCount(text);
                if (Interlocked.Add(ref active.DetailBytes, bytes) > MaximumDetailBytesPerCapture)
                { Interlocked.Add(ref active.DetailBytes, -bytes); Interlocked.Increment(ref active.DetailTruncated); text = null; }
            }
        }
        record = DiagnosticSanitizer.Create(record.OccurredAt, record.Level, record.Module, record.Code, record.TraceId, record.OperationId,
            record.ProviderVersion, record.ExceptionType, record.Component, record.Event, record.Fields, record.Sequence, text, allowDetail: true,
            httpMethod: record.HttpMethod, route: record.Route);
        if (text is not null && record.Detail?.Length < text.Length) Interlocked.Increment(ref active.DetailTruncated);
        buffer.Writer.TryWrite(new(capture.Id, record));
    }

    public DiagnosticCaptureCurrent Current(string principal, bool owner)
    {
        var value = Volatile.Read(ref current)?.Snapshot() ?? store.LatestCapture();
        return new(value is not null && (owner || value.Owner == principal) ? value : null);
    }

    public async Task<DiagnosticCaptureSnapshot> StartAsync(string principal, DiagnosticCaptureRequest request, string? key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Modules is null || request.Modules.Count is < 1 or > 7 || request.Modules.Distinct(StringComparer.Ordinal).Count() != request.Modules.Count ||
            request.Modules.Any(x => !Modules.Contains(x)) || request.Level is not ("debug" or "trace") ||
            request.DurationSeconds is < 1 or > MaximumDurationSeconds)
            throw new OperationException("request.invalid", "调试采集模块、等级或时长无效");
        if (key is null) throw new OperationException("idempotency.required", "开启调试采集需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "调试采集幂等键无效");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ContractJson.Options))));
        await lifecycle.WaitAsync(ct);
        try
        {
            lock (gate)
            {
                foreach (var expired in receipts.Where(x => clock.GetElapsedTime(x.Value.Created) >= TimeSpan.FromMinutes(10)).Select(x => x.Key).ToArray())
                    receipts.Remove(expired);
                if (receipts.TryGetValue((principal, key), out var old))
                {
                    if (old.Hash != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他调试采集请求");
                    return (current?.Initial.Id == old.CaptureId ? current.Snapshot() : store.Capture(old.CaptureId))
                        ?? throw new OperationException("resource.notFound", "调试采集不存在");
                }
                if (stopping) throw new OperationException("provider.unavailable", "宿主正在停止");
                if (current is not null) throw new OperationException("diagnostics.captureActive", "已有调试采集正在进行");
                if (receipts.Count >= 1000) throw new OperationException("resource.capacityExceeded", "调试采集幂等记录已满");
            }
            var session = await store.BeginAsync(principal, DiagnosticStore.CaptureModule, ct);
            var now = clock.GetUtcNow();
            var modules = request.Modules.Order(StringComparer.Ordinal).ToArray();
            var snapshot = new DiagnosticCaptureSnapshot(Guid.NewGuid().ToString("N"), principal, session.Id, modules, request.Level,
                request.IncludeDetail, "active", null, now, now.AddSeconds(request.DurationSeconds), null, new(0), new(0), new(0), new(0), new(0));
            await store.SaveCaptureAsync(snapshot, runtime.Capture(), null, ct);
            var active = new Active(snapshot, new());
            lock (gate) { current = active; receipts[(principal, key)] = new(hash, snapshot.Id, clock.GetTimestamp()); }
            trace.BeginCapture(new(snapshot.Id, modules.ToHashSet(StringComparer.Ordinal), DiagnosticEvents.Level(request.Level), request.IncludeDetail, this));
            _ = ExpireAsync(snapshot.Id, TimeSpan.FromSeconds(request.DurationSeconds), active.Expiry.Token);
            trace.Emit(DiagnosticEvents.DebugCaptureStarted, new DiagnosticFields().Value("level", request.Level).Count("modules", modules.Length)
                .Count("durationSeconds", request.DurationSeconds).Flag("includeDetail", request.IncludeDetail));
            return active.Snapshot();
        }
        finally { lifecycle.Release(); }
    }

    public async Task<DiagnosticCaptureSnapshot> StopAsync(string id, string principal, bool owner, CancellationToken ct)
    {
        var active = Volatile.Read(ref current);
        if (active?.Initial.Id == id)
        {
            if (!owner && active.Initial.Owner != principal) throw new OperationException("resource.notFound", "调试采集不存在");
            if (await EndAsync(id, "stopped", ct) is { } ended) return ended;
        }
        var saved = store.Capture(id);
        if (saved is null || !owner && saved.Owner != principal) throw new OperationException("resource.notFound", "调试采集不存在");
        return saved;
    }

    private async Task ExpireAsync(string id, TimeSpan duration, CancellationToken ct)
    {
        try { await Task.Delay(duration, clock, ct); await EndAsync(id, "expired", CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is OperationException or StorageException or ActivityBarrierException or IOException) { }
    }

    /// <summary>先关闭采集门限，再等待缓冲写完，最后补结束总览并完成采集会话。</summary>
    private async Task<DiagnosticCaptureSnapshot?> EndAsync(string id, string reason, CancellationToken ct, bool hostStopping = false)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var active = Volatile.Read(ref current);
            if (active is null || active.Initial.Id != id) return null;
            trace.EndCapture(id);
            await active.Expiry.CancelAsync();
            await FlushAsync(ct);
            var ended = active.Snapshot("ended", reason, clock.GetUtcNow());
            var maintenance = hostStopping ? shutdown?.Lease : null;
            try
            {
                await store.CompleteAsync(active.Initial.SessionId, ct: ct, maintenance: maintenance);
                await store.SaveCaptureAsync(ended, null, runtime.Capture(), ct, maintenance);
            }
            finally { lock (gate) current = null; active.Expiry.Dispose(); }
            trace.Emit(DiagnosticEvents.DebugCaptureEnded, new DiagnosticFields().Value("reason", reason)
                .Count("records", ended.Records.Value).Count("dropped", ended.DroppedRecords.Value));
            return ended;
        }
        finally { lifecycle.Release(); }
    }

    private Task FlushAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) flushWaiters.Add(done);
        wake.Release();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<Item>(256);
        while (true)
        {
            batch.Clear();
            while (batch.Count < 256 && buffer.Reader.TryRead(out var item)) batch.Add(item);
            if (batch.Count > 0)
                try { await WriteAsync(batch); }
                catch (Exception error) when (error is OperationException or StorageException or ActivityBarrierException or IOException or UnauthorizedAccessException)
                { if (Volatile.Read(ref current) is { } active) Interlocked.Add(ref active.Dropped, batch.Count); }
            if (buffer.Reader.Count == 0)
            {
                TaskCompletionSource[] waiters;
                lock (gate) { waiters = [.. flushWaiters]; flushWaiters.Clear(); }
                foreach (var waiter in waiters) waiter.TrySetResult();
                if (stoppingToken.IsCancellationRequested && Volatile.Read(ref current) is null) return;
            }
            if (batch.Count == 0)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var read = buffer.Reader.WaitToReadAsync(idle.Token).AsTask();
                var poke = wake.WaitAsync(idle.Token);
                try { await Task.WhenAny(read, poke); } finally { await idle.CancelAsync(); }
                try { await Task.WhenAll(read, poke); } catch (OperationCanceledException) { }
                if (stoppingToken.IsCancellationRequested && Volatile.Read(ref current) is null && buffer.Reader.Count == 0) return;
            }
        }
    }

    private async Task WriteAsync(List<Item> batch)
    {
        var active = Volatile.Read(ref current);
        if (active is null) return;
        var records = batch.Where(x => x.CaptureId == active.Initial.Id).Select(x => x.Record).ToArray();
        if (records.Length == 0) return;
        var before = await store.AppendAsync(active.Initial.SessionId, records);
        if (before is not null)
        {
            Interlocked.Exchange(ref active.Records, (long)before.Records.Value);
            Interlocked.Exchange(ref active.StoreDropped, (long)before.DroppedRecords.Value);
        }
        foreach (var record in records)
        {
            // 每秒窗口内至多推送 50 条；超出部分只计入丢弃计数，记录仍在采集文件中。
            var now = clock.GetTimestamp();
            if (clock.GetElapsedTime(windowStart, now) >= TimeSpan.FromSeconds(1)) { windowStart = now; windowCount = 0; }
            if (windowCount >= PushPerSecond) { Interlocked.Increment(ref active.PushDropped); continue; }
            windowCount++;
            await hub.PublishDiagnosticRecordAsync(active.Initial.Id, record);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) stopping = true;
        if (Volatile.Read(ref current) is { } active)
            try { await EndAsync(active.Initial.Id, "hostStopping", cancellationToken, hostStopping: true); }
            catch (Exception error) when (error is OperationException or StorageException or ActivityBarrierException or IOException or OperationCanceledException) { }
        await base.StopAsync(cancellationToken);
    }
}
