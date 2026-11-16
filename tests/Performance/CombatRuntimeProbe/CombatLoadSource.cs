using System.Diagnostics;
using System.Runtime.CompilerServices;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Providers.Replay;

internal sealed class CombatLoadSource : ICombatObservationSource
{
    private int active;
    public int ActiveSubscriptions => Volatile.Read(ref active);
    public int Frames { get; private set; }
    public long EmittedDamageEvents { get; private set; }
    public double EmitMs { get; private set; }
    public CombatCapabilities Capabilities(string? session) => new(true,
        new[] { "combat.damage.local", "combat.effects.self", "combat.effects.monster" }.Select(id =>
            new CombatCapability(id, CapabilityStatus.Available, ReplayProvider.Source, null, null, null)).ToArray());
    public ValueTask<ICombatObservationSubscription> OpenAsync(string id, CombatObservationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref active);
        return ValueTask.FromResult<ICombatObservationSubscription>(new Subscription(this,
            new(request.GameSessionId, new(1), new(9007199254740993), new(1))));
    }
    private sealed class Subscription(CombatLoadSource owner, EntityKey self) : ICombatObservationSubscription
    {
        private int disposed;
        public ObservationSource Source => ReplayProvider.Source;
        public CombatEntityObservation LocalEntity => CombatReplayFrames.Entity(self, CombatEntityRole.LocalPlayer, "負載回放角色");
        public double ClockRate => 1;
        public async IAsyncEnumerable<CombatFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var start = Stopwatch.GetTimestamp(); var target = self with { RawInstanceId = new(9007199254740995) };
            for (var step = 1; step <= 1000; step++)
            {
                ct.ThrowIfCancellationRequested();
                if (step == 2) await Task.Delay(400, ct);
                var at = DateTimeOffset.UtcNow; var mono = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var damage = Enumerable.Range(0, 64).Select(index => new CombatDamageEvent($"load-{step}-{index}", Source, self, target,
                    new((ulong)(index % 32)), new(1200), CombatDamageKind.Unclassified, at, mono, new((ulong)step), index,
                    index % 5 == 0, true, ObservationQuality.Confirmed)).ToArray();
                var effects = step % 25 == 1 ? new[] { self, target }.Select(key =>
                    new CombatEffectReconciliation(key, true, [new(CombatEffectChangeKind.Refresh, key, new(100), new(200), self,
                        EffectDurationKind.Finite, 5000, step % 10 + 1, "synthetic", false, at, mono, ObservationQuality.Confirmed)])).ToArray() : [];
                owner.Frames++; owner.EmittedDamageEvents += damage.Length;
                yield return new(1, Source, self.GameSessionId, self.ObservationGeneration, new((ulong)step), at, mono,
                    step == 1 ? [LocalEntity, CombatReplayFrames.Entity(target, CombatEntityRole.Monster, "負載回放怪物")] : [],
                    damage, [], effects, []);
            }
            owner.EmitMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref owner.active);
            return ValueTask.CompletedTask;
        }
    }
}
