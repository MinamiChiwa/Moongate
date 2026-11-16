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

internal sealed class ObservationCaptureRun(WorkerPeerConnection peer, IObservationCaptureSource source,
    ObservationCaptureRequest request, WorkerFrame scope,
    Func<InventoryCaptureReport, CancellationToken, Task<object>>? prepareInventory = null, bool reportDiagnostics = false)
{
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
        report => music.Writer.TryWrite((report, Now())), report => combat.TryWrite(report),
        source.InventoryProfileFor(request), source.InventoryLayoutFor(request), report => inventory.TryWrite(report));
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
        catch (Exception error) { failure = error; }
        finally { Interlocked.Exchange(ref finished, 1); music.Writer.TryComplete(); }
        try { await writer.ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
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
                IReadOnlyList<CombatCaptureReport> previousCombat = [];
                IReadOnlyList<InventoryCaptureReport> previousInventory = [];
                activeContext.Configure(command.Arguments, () =>
                {
                    if (music.Reader.TryRead(out var report)) previousMusic = report;
                    previousCombat = combat.Drain();
                    previousInventory = inventory.Take(64);
                });
                if (previousMusic is { } pending && !QueueStale(pending)) await peer.SendAsync("event", new { report = pending.Report }, scope, ct).ConfigureAwait(false);
                foreach (var previous in previousCombat) await SendCombatAsync(previous, ct).ConfigureAwait(false);
                foreach (var previous in previousInventory) await SendInventoryAsync(previous, ct).ConfigureAwait(false);
                await peer.SendAsync("event", new { interestsApplied = command.Arguments }, scope, ct).ConfigureAwait(false);
            }
            await peer.SendAsync("commandResult", new { commandId = command.CommandId, accepted,
                code = accepted ? null : command.Arguments.Inventory is not null ? "inventory.buildUnsupported" : "combat.buildUnsupported",
                interests = activeContext.Current }, scope, ct).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
    private async Task WriteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var done = Volatile.Read(ref finished) != 0;
            await writes.WaitAsync(ct);
            try
            {
                await FlushAsync(ct, 8).ConfigureAwait(false);
                await SendDiagnosticsAsync(done && !music.Reader.TryPeek(out _) && combat.PendingCount == 0, ct).ConfigureAwait(false);
            }
            finally { writes.Release(); }
            if (done && !music.Reader.TryPeek(out _) && combat.PendingCount == 0) return;
        }
    }
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
    private async Task FlushAsync(CancellationToken ct, int maximum)
    {
        if (music.Reader.TryRead(out var queued) && !QueueStale(queued))
            await peer.SendAsync("event", new { report = queued.Report }, scope, ct).ConfigureAwait(false);
        foreach (var value in combat.Take(maximum)) await SendCombatAsync(value, ct).ConfigureAwait(false);
    }
    /// <summary>
    /// 库存帧独立写出：完整库存的编码和受管资产写入在写出锁外完成，只把小型事件信封放入共用管道，
    /// 音乐和战斗写出不等待大帧编码。
    /// </summary>
    private async Task WriteInventoryAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            foreach (var value in inventory.Take(8))
            {
                var payload = await PrepareInventoryAsync(value, ct).ConfigureAwait(false);
                await writes.WaitAsync(ct);
                try { await peer.SendAsync("event", payload, scope, ct).ConfigureAwait(false); }
                finally { writes.Release(); }
            }
            if (Volatile.Read(ref finished) != 0 && inventory.Count == 0) return;
        }
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
    private Task SendCombatAsync(CombatCaptureReport value, CancellationToken ct) =>
        peer.SendAsync("event", new { combat = lag.Combat(value, Now()) }, scope, ct);
    private async Task SendInventoryAsync(InventoryCaptureReport value, CancellationToken ct) =>
        await peer.SendAsync("event", await PrepareInventoryAsync(value, ct).ConfigureAwait(false), scope, ct).ConfigureAwait(false);
    private async Task<object> PrepareInventoryAsync(InventoryCaptureReport value, CancellationToken ct)
    {
        if (!ulong.TryParse(scope.BindingRevision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var revision) || revision == 0) throw new WorkerException("worker.frameInvalid");
        value = lag.Inventory(value, Now());
        value = value with { Frame = value.Frame with { BindingRevision = new(revision) } };
        return prepareInventory is not null ? await prepareInventory(value, ct).ConfigureAwait(false) : new { inventory = value };
    }
}
