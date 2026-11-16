using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Host.Game;

internal sealed class CaptureCombatSubscription(GameObservationService service, string principal, string id, string interestId,
    CombatObservationRequest request, ObservationCombatProfile profile, bool resident = false) : ICombatObservationSubscription
{
    private readonly object gate = new();
    private readonly CombatFrameBuffer buffer = new();
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly TaskCompletionSource<CombatEntityObservation> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CombatFrame? last;
    private Exception? failure;
    private Task<IReadOnlyList<CombatGapInput>>? closing;
    private bool completed;
    private ulong deliverySequence, generation, sequence;
    private readonly Dictionary<ulong, string> fingerprints = [];
    private readonly Queue<ulong> fingerprintOrder = [];
    private readonly HashSet<(ulong Sequence, string Hash)> conflicts = [];
    private readonly Queue<(ulong Sequence, string Hash)> conflictOrder = [];
    private int reader;
    internal string Id => id;
    internal string Principal => principal;
    internal string InterestId => interestId;
    internal CombatObservationRequest Request => request;
    internal ObservationCombatProfile Profile => profile;
    internal bool Resident => resident;
    internal bool Closing { get; set; }
    internal Task<CombatEntityObservation> Ready => ready.Task;
    public ObservationSource Source => profile.Source;
    public CombatEntityObservation LocalEntity => ready.Task.IsCompletedSuccessfully ? ready.Task.Result : throw new InvalidOperationException("战斗身份尚未确认");
    public double ClockRate => 1;
    public bool HealingSupported => profile.Healing;
    internal void Publish(CombatCaptureReport report)
    {
        lock (gate)
        {
            if (completed) return;
            var frame = report.Frame;
            if (frame.Source != Source || frame.GameSessionId != request.GameSessionId) throw CaptureCombatValidation.Invalid("subscription.scope");
            if (frame.Generation.Value < generation) return;
            if (frame.Generation.Value > generation)
            {
                generation = frame.Generation.Value; sequence = 0;
                fingerprints.Clear(); fingerprintOrder.Clear(); conflicts.Clear(); conflictOrder.Clear();
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report, ContractJson.Options)));
            if (frame.Sequence.Value <= sequence)
            {
                if (fingerprints.TryGetValue(frame.Sequence.Value, out var previous) && previous != fingerprint &&
                    last is { } template && conflicts.Add((frame.Sequence.Value, fingerprint)))
                {
                    conflictOrder.Enqueue((frame.Sequence.Value, fingerprint));
                    if (conflictOrder.Count > 256) conflicts.Remove(conflictOrder.Dequeue());
                    buffer.TryWrite(template with { Damage = [], Effects = [], Reconciliations = [], ObservationResumed = false, EncounterEnded = false,
                        Gaps = [new("combat.frameConflict", template.MonotonicMs, template.MonotonicMs, new(1))] });
                    wake.Writer.TryWrite(true);
                }
                return;
            }
            if (last is not null && frame.MonotonicMs < last.MonotonicMs) throw new WorkerException("worker.sequenceInvalid");
            sequence = frame.Sequence.Value;
            fingerprints[sequence] = fingerprint; fingerprintOrder.Enqueue(sequence);
            if (fingerprintOrder.Count > 256) fingerprints.Remove(fingerprintOrder.Dequeue());
            if (report.LocalEntity is { } local)
            {
                if (local.Key.GameSessionId != request.GameSessionId || local.Key.ObservationGeneration != frame.Generation || local.Key.RawInstanceId.Value == 0 ||
                    local.Role.KnownValue != CombatEntityRole.LocalPlayer || local.Quality.KnownValue != ObservationQuality.Confirmed ||
                    !frame.Entities.Contains(local)) throw CaptureCombatValidation.Invalid("subscription.localEntity");
                ready.TrySetResult(local);
            }
            last = frame;
            buffer.TryWrite(frame with { Damage = request.DamageEnabled ? frame.Damage : [],
                Effects = frame.Effects.Where(value => value.EntityKey == report.LocalEntity?.Key ? request.SelfEffectsEnabled : request.MonsterEffectsEnabled).ToArray() });
            wake.Writer.TryWrite(true);
        }
    }
    internal void Complete(Exception? error)
    {
        lock (gate)
        {
            if (completed) return; completed = true; failure = error; buffer.Complete();
            if (!ready.Task.IsCompleted) ready.TrySetException(error ?? new OperationException("combat.sourceUnavailable", "捕获结束前未确认本机身份"));
            wake.Writer.TryWrite(true);
        }
    }
    public async IAsyncEnumerable<CombatFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Exchange(ref reader, 1) != 0) throw new InvalidOperationException("战斗订阅只允许一个读取者");
        while (true)
        {
            await wake.Reader.ReadAsync(ct);
            CombatBufferedBatch batch; CombatFrame? template;
            lock (gate) { batch = buffer.Take(10000); template = last; }
            foreach (var frame in batch.Frames) yield return frame with { Sequence = new(++deliverySequence) };
            if (batch.DroppedEvents.Value > 0 && template is not null)
                yield return template with { Sequence = new(++deliverySequence), Damage = [], Effects = [], Reconciliations = [],
                    ObservationResumed = false, EncounterEnded = false,
                    Gaps = [new("combat.subscriptionCapacity", template.MonotonicMs, template.MonotonicMs, batch.DroppedEvents)] };
            if (buffer.Count > 0) wake.Writer.TryWrite(true);
            lock (gate) if (completed && buffer.Completed) { if (failure is not null) throw failure; yield break; }
        }
    }
    public ValueTask<IReadOnlyList<CombatGapInput>> CloseAsync()
    {
        lock (gate) return new(closing ??= Task.Run(CloseCoreAsync));
    }
    private async Task<IReadOnlyList<CombatGapInput>> CloseCoreAsync()
    {
        await service.ReleaseCombatAsync(this);
        lock (gate)
        {
            var gaps = new CombatGapAccumulator();
            gaps.AddRange(buffer.DrainGaps("combat.unreadCapture", last?.MonotonicMs ?? 0));
            if (failure is not null && last is { } value) gaps.Add(new("combat.captureEnded", value.MonotonicMs, null, new(0)));
            return gaps.Snapshot();
        }
    }
    public async ValueTask DisposeAsync() => await CloseAsync();
}
