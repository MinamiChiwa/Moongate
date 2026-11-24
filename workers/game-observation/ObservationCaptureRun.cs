using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;

namespace MabiTool.Observation.Worker;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ObservationInterestCommand(string CommandId, string Method, ObservationCaptureInterests Arguments);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ObservationCaptureCommand(string CommandId, string Method, JsonElement Arguments);

/// <summary>Worker 到宿主的一条消息：种类、正文与所属请求。</summary>
internal interface IObservationPeer
{
    Task SendAsync(string kind, object payload, WorkerFrame? scope, CancellationToken ct);
    /// <summary>正文为已编码的 UTF-8 JSON 对象：原样拼入帧。</summary>
    Task SendEncodedAsync(string kind, ReadOnlyMemory<byte> payload, WorkerFrame? scope, CancellationToken ct);
}
internal sealed class ObservationPeer(WorkerPeerConnection connection) : IObservationPeer
{
    public Task SendAsync(string kind, object payload, WorkerFrame? scope, CancellationToken ct) => connection.SendAsync(kind, payload, scope, ct);
    public Task SendEncodedAsync(string kind, ReadOnlyMemory<byte> payload, WorkerFrame? scope, CancellationToken ct) => connection.SendEncodedAsync(kind, payload, scope, ct);
}

/// <summary>已编码的事件正文（契约序列化选项编出的 UTF-8 JSON 对象）：发送时原样拼入 Worker 帧，不再解析与重新编码。</summary>
internal sealed record EncodedEvent(byte[] Json, int Length = -1, bool Pooled = false)
{
    public ReadOnlyMemory<byte> Memory => Json.AsMemory(0, Length < 0 ? Json.Length : Length);
    internal static ReadOnlySpan<byte> CombatPrefix => "{\"combat\":"u8;
    internal static ReadOnlySpan<byte> Suffix => "}"u8;
    /// <summary>以入队时的库存报告编码组成 {"inventory":…} 事件。</summary>
    internal static EncodedEvent Inventory(ReadOnlySpan<byte> report)
    {
        var json = new byte[report.Length + 14];
        "{\"inventory\":"u8.CopyTo(json); report.CopyTo(json.AsSpan(13)); json[^1] = (byte)'}';
        return new(json);
    }
}

internal sealed class ObservationCaptureRun(IObservationPeer peer, IObservationCaptureSource source,
    ObservationCaptureRequest request, WorkerFrame scope,
    Func<InventoryCaptureReport, byte[]?, CancellationToken, Task<object>>? prepareInventory = null, bool reportDiagnostics = false)
{
    /// <summary>离线回放的测量钩子：报告入队前与写出后各调用一次（参数为报告对象）。生产路径不设置。</summary>
    internal Action<object>? Enqueued { get; set; }
    internal Action<object>? Sent { get; set; }
    /// <summary>诊断计数至多每秒写出一次，与音乐摘要共用写出锁；计数未变化时不发送。</summary>
    internal static readonly TimeSpan DiagnosticsInterval = TimeSpan.FromSeconds(1);
    private long diagnosticsSentAt = long.MinValue;
    private byte[]? diagnosticsSent;
    private readonly Channel<(GameCaptureReport Report, double QueuedAt)> music = Channel.CreateBounded<(GameCaptureReport, double)>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private readonly CombatCaptureQueue combat = new();
    private readonly InventoryCaptureQueue inventory = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private ObservationCaptureContext? context;
    private ulong commandSequence;
    private int finished;
    internal void Initialize() => context = new(source.CombatProfilesFor(request),
        report => { Enqueued?.Invoke(report); music.Writer.TryWrite((report, Now())); combatReady.Set(); },
        report => { Enqueued?.Invoke(report); combat.TryWrite(report); combatReady.Set(); },
        source.InventoryProfileFor(request), source.InventoryLayoutFor(request), report => { Enqueued?.Invoke(report); inventory.TryWrite(Bound(report)); inventoryReady.Set(); });
    /// <summary>有数据入队时唤醒对应的写出循环；空闲时写出循环不按固定周期醒来。</summary>
    private readonly WakeSignal combatReady = new(), inventoryReady = new();
    /// <summary>库存帧在入队前标上本次捕获的绑定修订，入队时的编码即为写出内容；修订无效时保持原样，写出时以 worker.frameInvalid 结束。</summary>
    private InventoryCaptureReport Bound(InventoryCaptureReport value) =>
        ulong.TryParse(scope.BindingRevision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision) && revision != 0
            ? value with { Frame = value.Frame with { BindingRevision = new(revision) } } : value;
    internal async Task RunAsync(CancellationTokenSource stop, CancellationToken lifetime)
    {
        var capture = Task.Run(() => source.CaptureSharedAsync(request, context!, stop.Token), CancellationToken.None);
        var combatWriter = WriteAsync(lifetime); var inventoryWriter = WriteInventoryAsync(lifetime);
        // 任一写出循环失败即视为写出失败并停止捕获；正常结束时等待两者排空。
        var writer = Task.Run(async () =>
        {
            var first = await Task.WhenAny(combatWriter, inventoryWriter).ConfigureAwait(false);
            if (!first.IsCompletedSuccessfully) await first.ConfigureAwait(false);
            await Task.WhenAll(combatWriter, inventoryWriter).ConfigureAwait(false);
        });
        Exception? failure = null;
        try
        {
            if (await Task.WhenAny(capture, writer).ConfigureAwait(false) == writer && !capture.IsCompleted) stop.Cancel();
            await capture.ConfigureAwait(false);
        }
        catch (Exception error) { failure = Classified("capture", error); }
        finally { Interlocked.Exchange(ref finished, 1); music.Writer.TryComplete(); combatReady.Set(); inventoryReady.Set(); }
        try { await writer.ConfigureAwait(false); }
        catch (Exception error) { failure ??= Classified("writer", error); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    /// <summary>已有固定码的异常与取消原样保留；其余按发生位置与类型给出细分原因，宿主仍按 observation.captureFailed 处理。</summary>
    private static Exception Classified(string area, Exception error) => error is WorkerException or WorkerObservationException or
        CaptureFaultException or OperationCanceledException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
        ? error : new CaptureFaultException(CaptureFaultException.Classify(area, error), error);
    internal async Task ConfigureAsync(WorkerFrame frame, CancellationToken ct)
    {
        var envelope = frame.Payload.Deserialize<ObservationCaptureCommand>(ContractJson.Options) ?? throw new WorkerException("worker.commandInvalid");
        if (envelope.CommandId != checked(++commandSequence).ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new WorkerException("worker.commandInvalid");
        var activeContext = context ?? throw new WorkerException("worker.requestInactive");
        if (envelope.Method == "inventory.prepare")
        {
            object result;
            try
            {
                var client = envelope.Arguments.Deserialize<InventoryClientRequest>(ContractJson.Options) ?? throw new WorkerException("worker.commandInvalid");
                if (client.ProcessId != request.ProcessId || client.ProcessStartTime != request.ProcessStartTime ||
                    client.GameSessionId != request.GameSessionId || client.Region != request.Region || !ulong.TryParse(scope.BindingRevision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var binding) || binding == 0)
                    throw new WorkerObservationException("input.bindingChanged");
                InventoryClientPrepared prepared;
                WorkerMemoryTrace.Mark("layout.prepareStarted");
                try { prepared = await source.PrepareInventoryAsync(client, ct).ConfigureAwait(false); }
                finally { WorkerMemoryTrace.Mark("layout.prepareFinished"); }
                var layout = source.InventoryLayoutFor(request) ?? throw new WorkerObservationException("inventory.buildUnsupported");
                ct.ThrowIfCancellationRequested(); activeContext.InstallInventoryLayout(prepared.Profile, layout);
                result = new { commandId = envelope.CommandId, prepared };
            }
            catch (Exception error) when (error is WorkerObservationException or WorkerException or InvalidDataException or IOException or
                System.ComponentModel.Win32Exception or UnauthorizedAccessException or JsonException)
            {
                var code = error is WorkerObservationException observation ? observation.Code : error is WorkerException worker ? worker.Code : "inventory.buildUnsupported";
                result = new { commandId = envelope.CommandId, code };
            }
            await writes.WaitAsync(ct).ConfigureAwait(false);
            try { await peer.SendAsync("commandResult", result, scope, ct).ConfigureAwait(false); }
            finally { writes.Release(); }
            return;
        }
        var command = new ObservationInterestCommand(envelope.CommandId, envelope.Method,
            envelope.Arguments.Deserialize<ObservationCaptureInterests>(ContractJson.Options)!);
        if (command.Method != "observation.interests" ||
            command.Arguments is null || command.Arguments.Combat is { } value &&
            (!WorkerProtocol.IsId(value.Id) || value.Profile is null or { Length: > 64 } ||
             !value.DamageEnabled && !value.SelfEffectsEnabled && !value.MonsterEffectsEnabled) ||
            command.Arguments.Inventory is { } requestedInventory &&
            (!WorkerProtocol.IsId(requestedInventory.Id) || requestedInventory.Profile is null or { Length: > 64 }))
            throw new WorkerException("worker.commandInvalid");
        await writes.WaitAsync(ct);
        try
        {
            var accepted = activeContext.CanConfigure(command.Arguments);
            if (accepted)
            {
                (GameCaptureReport Report, double QueuedAt)? previousMusic = null;
                IReadOnlyList<CombatCaptureBatch> previousCombat = [];
                IReadOnlyList<(InventoryCaptureReport Report, byte[]? Encoded)> previousInventory = [];
                activeContext.Configure(command.Arguments, () =>
                {
                    if (music.Reader.TryRead(out var report)) previousMusic = report;
                    previousCombat = combat.DrainBatches();
                    previousInventory = inventory.TakeEncoded(64);
                });
                if (previousMusic is { } pending && !QueueStale(pending)) await peer.SendAsync("event", new { report = pending.Report }, scope, ct).ConfigureAwait(false);
                foreach (var previous in previousCombat) await SendCombatAsync(previous, ct).ConfigureAwait(false);
                foreach (var (previous, encoded) in previousInventory) await SendInventoryAsync(previous, encoded, ct).ConfigureAwait(false);
                await peer.SendAsync("event", new { interestsApplied = command.Arguments }, scope, ct).ConfigureAwait(false);
            }
            await peer.SendAsync("commandResult", new { commandId = command.CommandId, accepted,
                code = accepted ? null : command.Arguments.Inventory is not null ? "inventory.buildUnsupported" : "combat.buildUnsupported",
                interests = activeContext.Current }, scope, ct).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
    /// <summary>战斗批次的写出速率上限（每 100 ms 至多 8 批，可积攒 8 批的突发）：宿主每秒至多受理 256 条 Worker 消息。</summary>
    internal const int CombatBatchBurst = 8;
    internal const double CombatBatchesPerMs = 0.08;
    /// <summary>
    /// 音乐摘要、战斗与诊断的写出循环，按负载自适应：有数据入队即唤醒。令牌桶满（近期没有写出、数据少）时立即写出，来一条发一条；
    /// 令牌已被近期的写出消耗（有负载）时，队尾批次（仍可并入新报告）要满一个合批窗口才写出，窗口随令牌减少而加长
    /// （<see cref="CoalesceWindowMs"/>），期间到达的报告并入队尾（数据多时自动合批，同一轮读到的一串报告不拆成多条消息）；
    /// 更早的批次按令牌写出，令牌不足一批时等到下一批可写出的时刻。等待由高精度计时器按截止时间唤醒。
    /// 没有数据时只为诊断计数按间隔醒来（未启用诊断时不醒来）。捕获结束且队列已空时返回。
    /// </summary>
    private async Task WriteAsync(CancellationToken ct)
    {
        using var precise = new MabiTool.ModuleSdk.PreciseTimer();
        var tokens = (double)CombatBatchBurst; var refilled = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            await combatReady.WaitAsync(reportDiagnostics ? DiagnosticsInterval : Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            while (true)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                tokens = Math.Min(CombatBatchBurst, tokens + System.Diagnostics.Stopwatch.GetElapsedTime(refilled, now).TotalMilliseconds * CombatBatchesPerMs);
                refilled = now;
                var maximum = Math.Max(0, (int)tokens);
                if (combat.PendingCount > 0)
                {
                    var wait = 0d;
                    if (tokens < 1) wait = (1 - tokens) / CombatBatchesPerMs;
                    else if (tokens < CombatBatchBurst - 0.5 && combat.TailAgeMs(Now()) is { } age && age < CoalesceWindowMs(tokens))
                    {
                        // 有负载：队尾批次未满合批窗口，先写更早的批次；只剩队尾时等它满窗口。
                        if (combat.Count > 1) maximum = Math.Min(maximum, combat.Count - 1);
                        else wait = CoalesceWindowMs(tokens) - age;
                    }
                    if (wait > 0)
                    {
                        await precise.DelayAsync(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
                        continue;
                    }
                }
                var (done, sent) = await WriteRoundAsync(ct, maximum).ConfigureAwait(false);
                if (done) return;
                tokens -= sent;
                if (combat.PendingCount == 0) break;
            }
        }
    }
    /// <summary>
    /// 有负载时队尾批次的合批窗口：令牌每少一批，窗口加一批的令牌周期（12.5 ms），至多 100 ms（批内时间跨度上限）。
    /// 写出越接近速率上限，每批合并的报告越多，积压时批次逐渐装满而不是以半满的批次耗尽每秒的批数。
    /// </summary>
    internal static double CoalesceWindowMs(double tokens) => Math.Min(100, (CombatBatchBurst - tokens) / CombatBatchesPerMs);
    /// <summary>写出循环的一轮：音乐摘要、至多 8 批战斗与诊断计数；捕获已结束且队列已空时返回真。</summary>
    internal async Task<bool> WriteOnceAsync(CancellationToken ct) => (await WriteRoundAsync(ct, CombatBatchBurst).ConfigureAwait(false)).Done;
    private async Task<(bool Done, int Sent)> WriteRoundAsync(CancellationToken ct, int maximum)
    {
        var done = Volatile.Read(ref finished) != 0;
        int sent;
        await writes.WaitAsync(ct);
        try
        {
            sent = await FlushAsync(ct, maximum).ConfigureAwait(false);
            await SendDiagnosticsAsync(done && !music.Reader.TryPeek(out _) && combat.PendingCount == 0, ct).ConfigureAwait(false);
        }
        finally { writes.Release(); }
        return (done && !music.Reader.TryPeek(out _) && combat.PendingCount == 0, sent);
    }
    /// <summary>离线回放直接驱动捕获上下文（不经写出循环）。</summary>
    internal ObservationCaptureContext Context => context ?? throw new InvalidOperationException();
    /// <summary>写出队列当前占用的编码字节（战斗含容量缺口模板）。</summary>
    internal (int Combat, int Inventory) QueuedBytes => (combat.Bytes, inventory.Bytes);
    /// <summary>采样队列高水位；启用诊断时按间隔发送变化后的累计计数，结束前补发最终值。</summary>
    private async Task SendDiagnosticsAsync(bool final, CancellationToken ct)
    {
        var counters = context?.Diagnostics;
        if (counters is null) return;
        counters.ObserveQueues(combat.PendingCount, inventory.Count);
        if (!reportDiagnostics || !final && diagnosticsSentAt != long.MinValue &&
            System.Diagnostics.Stopwatch.GetElapsedTime(diagnosticsSentAt) < DiagnosticsInterval) return;
        var snapshot = counters.Snapshot(combat.PendingCount, inventory.Count);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(snapshot, ContractJson.Options);
        if (diagnosticsSent is not null && encoded.AsSpan().SequenceEqual(diagnosticsSent)) return;
        diagnosticsSent = encoded; diagnosticsSentAt = System.Diagnostics.Stopwatch.GetTimestamp();
        await peer.SendAsync("event", new { captureDiagnostics = snapshot }, scope, ct).ConfigureAwait(false);
    }
    /// <summary>写出最新的音乐摘要与至多 <paramref name="maximum"/> 批战斗记录，返回写出的战斗批数。</summary>
    private async Task<int> FlushAsync(CancellationToken ct, int maximum)
    {
        if (music.Reader.TryRead(out var queued) && !QueueStale(queued))
        {
            await peer.SendAsync("event", new { report = queued.Report }, scope, ct).ConfigureAwait(false);
            Sent?.Invoke(queued.Report);
        }
        if (maximum == 0) return 0;
        var batches = combat.TakeBatches(maximum);
        foreach (var value in batches) await SendCombatAsync(value, ct).ConfigureAwait(false);
        return batches.Count;
    }
    /// <summary>
    /// 库存帧独立写出：完整库存的编码和受管资产写入在写出锁外完成，只把小型事件信封放入共用管道，
    /// 音乐和战斗写出不等待大帧编码。
    /// </summary>
    private async Task WriteInventoryAsync(CancellationToken ct)
    {
        // 有库存帧入队即唤醒；两轮之间至少隔 100 ms（每轮至多 TakePerTick 帧，速率上限同此前的 100 ms 周期）。
        var last = long.MinValue;
        while (true)
        {
            await inventoryReady.WaitAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            if (last != long.MinValue && System.Diagnostics.Stopwatch.GetElapsedTime(last) is var since && since < TimeSpan.FromMilliseconds(100))
                await Task.Delay(TimeSpan.FromMilliseconds(100) - since, ct).ConfigureAwait(false);
            last = System.Diagnostics.Stopwatch.GetTimestamp();
            if (await WriteInventoryOnceAsync(ct).ConfigureAwait(false)) return;
            if (inventory.Count > 0) inventoryReady.Set();
        }
    }
    /// <summary>库存写出的一轮：至多 <see cref="InventoryCaptureQueue.TakePerTick"/> 帧；捕获已结束且队列已空时返回真。</summary>
    internal async Task<bool> WriteInventoryOnceAsync(CancellationToken ct)
    {
        foreach (var (value, encoded) in inventory.TakeEncoded(InventoryCaptureQueue.TakePerTick))
        {
            var payload = await PrepareInventoryAsync(value, encoded, ct).ConfigureAwait(false);
            await writes.WaitAsync(ct);
            try { await SendEventAsync(payload, ct).ConfigureAwait(false); }
            finally { writes.Release(); }
            Sent?.Invoke(value);
        }
        return Volatile.Read(ref finished) != 0 && inventory.Count == 0;
    }
    /// <summary>
    /// 写出侧时点预算：Host 拒绝超过 2 s 的帧，管道与 Host 处理另留 1 s。捕获或写出停顿后仍在队列中的帧
    /// 超过此预算时按写出时点校正，与抓包侧 combat.captureLag 语义一致。
    /// </summary>
    internal const double MaximumQueueLagMs = 1000;
    private readonly QueueLagCorrector lag = new();
    /// <summary>
    /// 状态报告只保留最新值：入队时仍在预算内、因排队超过预算的旧状态直接丢弃，等待下一条；
    /// 来源本身给出的过期状态照常发送，由 Host 按原规则拒绝。
    /// </summary>
    private static bool QueueStale((GameCaptureReport Report, double QueuedAt) value) =>
        Now() - value.Report.MonotonicMs > MaximumQueueLagMs && value.QueuedAt - value.Report.MonotonicMs <= MaximumQueueLagMs;
    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency);
    /// <summary>
    /// 写出一批战斗记录。无需按写出时点校正时，事件正文由入队时的编码拼出（与对象编码的 JSON 相同）；需要校正或是容量缺口时，
    /// 按报告对象校正并编码。
    /// </summary>
    private async Task SendCombatAsync(CombatCaptureBatch batch, CancellationToken ct)
    {
        var now = Now();
        if (batch.Encoded && lag.CombatPasses(batch.MonotonicMs, now))
        {
            // 事件正文写入租用的缓冲，发送后归还。
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(EncodedEvent.CombatPrefix.Length + batch.Bytes + EncodedEvent.Suffix.Length);
            await SendEventAsync(new EncodedEvent(buffer, batch.WriteEnvelope(buffer, EncodedEvent.CombatPrefix, EncodedEvent.Suffix), Pooled: true), ct).ConfigureAwait(false);
        }
        else await peer.SendAsync("event", new { combat = lag.Combat(batch.Encoded ? Decode<CombatCaptureReport>(batch.WriteEnvelope([], [])) : batch.Report, now) },
            scope, ct).ConfigureAwait(false);
        Sent?.Invoke(batch);
    }
    private async Task SendInventoryAsync(InventoryCaptureReport value, byte[]? encoded, CancellationToken ct) =>
        await SendEventAsync(await PrepareInventoryAsync(value, encoded, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    /// <summary>
    /// 库存事件正文。帧已在入队时标上绑定修订；无需按写出时点校正时沿用入队时的编码，需要校正时由该编码还原帧、校正后重新编码。
    /// </summary>
    private async Task<object> PrepareInventoryAsync(InventoryCaptureReport value, byte[]? encoded, CancellationToken ct)
    {
        if (!ulong.TryParse(scope.BindingRevision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var revision) || revision == 0) throw new WorkerException("worker.frameInvalid");
        if (encoded is null || !lag.InventoryPasses(value.Frame.MonotonicMs, Now()))
        {
            value = lag.Inventory(encoded is null ? value : Decode<InventoryCaptureReport>(encoded), Now());
            value = value with { Frame = value.Frame with { BindingRevision = new(revision) } };
            encoded = null;
        }
        if (prepareInventory is not null) return await prepareInventory(value, encoded, ct).ConfigureAwait(false);
        return encoded is null ? new { inventory = value } : EncodedEvent.Inventory(encoded);
    }
    private static T Decode<T>(byte[] encoded) => JsonSerializer.Deserialize<T>(encoded, ContractJson.Options)!;
    private async Task SendEventAsync(object payload, CancellationToken ct)
    {
        if (payload is not EncodedEvent encoded) { await peer.SendAsync("event", payload, scope, ct).ConfigureAwait(false); return; }
        try { await peer.SendEncodedAsync("event", encoded.Memory, scope, ct).ConfigureAwait(false); }
        finally { if (encoded.Pooled) System.Buffers.ArrayPool<byte>.Shared.Return(encoded.Json); }
    }
}

/// <summary>单个等待方的唤醒信号：多次 <see cref="Set"/> 合并为一次唤醒。</summary>
internal sealed class WakeSignal
{
    private readonly SemaphoreSlim semaphore = new(0, 1);
    private int set;
    public void Set() { if (Interlocked.Exchange(ref set, 1) == 0) semaphore.Release(); }
    /// <summary>等到信号（返回真并复位）或超时（返回假）。</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!await semaphore.WaitAsync(timeout, ct).ConfigureAwait(false)) return false;
        Interlocked.Exchange(ref set, 0); return true;
    }
}
