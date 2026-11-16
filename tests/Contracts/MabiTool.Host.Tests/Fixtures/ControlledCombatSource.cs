using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Providers.Replay;

namespace MabiTool.Host.Tests;

internal sealed class ControlledCombatSource : ICombatObservationSource
{
    private readonly Channel<CombatFrame> frames = Channel.CreateUnbounded<CombatFrame>();
    private int active, opened;
    private EntityKey? local;
    private long started;
    public int ActiveSubscriptions => Volatile.Read(ref active);
    public int Opened => Volatile.Read(ref opened);
    public bool SelfEffectsUnavailable { get; init; }
    public bool CancelObserved { get; private set; }
    public CombatCapabilities Capabilities(string? gameSessionId) => new(true,
        new[] { "combat.damage.local", "combat.effects.self", "combat.effects.monster" }.Select(id =>
            new CombatCapability(id, SelfEffectsUnavailable && id == "combat.effects.self" ? CapabilityStatus.Unavailable : CapabilityStatus.Available,
                ReplayProvider.Source, SelfEffectsUnavailable && id == "combat.effects.self" ? "combat.buildUnsupported" : null, null, null)).ToArray());
    public ValueTask<ICombatObservationSubscription> OpenAsync(string observationId, CombatObservationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref opened); Interlocked.Increment(ref active);
        started = Stopwatch.GetTimestamp();
        local = new(request.GameSessionId, new(1), new(9007199254740993), new(1));
        return ValueTask.FromResult<ICombatObservationSubscription>(new Subscription(this, local));
    }
    public IDisposable HoldOtherModuleSubscription() { Interlocked.Increment(ref active); return new Lease(() => Interlocked.Decrement(ref active)); }
    public void Send(int step, double? frameMonotonicMs = null, double? damageMonotonicMs = null)
    {
        var frame = CombatReplayFrames.Frame(local!, "normal", step, DateTimeOffset.UtcNow);
        if (step > 3) frame = frame with { Damage = [new("late-" + step, ReplayProvider.Source, local!, local! with { RawInstanceId = new(80) },
            new(1000), new(1200), CombatDamageKind.ActualHp, frame.ObservedAt, frame.MonotonicMs, frame.Sequence, 0, false, true, ObservationQuality.Confirmed)] };
        var mono = frameMonotonicMs ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        frame = frame with { MonotonicMs = mono, Damage = frame.Damage.Select(x => x with { MonotonicMs = damageMonotonicMs ?? mono }).ToArray(),
            Reconciliations = frame.Reconciliations.Select(x => x with { Effects = x.Effects.Select(effect => effect with { MonotonicMs = mono }).ToArray() }).ToArray() };
        frames.Writer.TryWrite(frame);
    }
    private sealed class Subscription(ControlledCombatSource owner, EntityKey key) : ICombatObservationSubscription
    {
        private int disposed;
        public ObservationSource Source => ReplayProvider.Source;
        public CombatEntityObservation LocalEntity => CombatReplayFrames.Entity(key, CombatEntityRole.LocalPlayer, "測試角色");
        public double ClockRate => 1;
        public async IAsyncEnumerable<CombatFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            try { await foreach (var frame in owner.frames.Reader.ReadAllAsync(ct)) yield return frame; }
            finally { owner.CancelObserved = ct.IsCancellationRequested; }
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref owner.active);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? cleanup = release;
        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
}
