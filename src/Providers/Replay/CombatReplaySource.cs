using System.Runtime.CompilerServices;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Application;

namespace MabiTool.Providers.Replay;

/// <summary>以固定合成时间线驱动正式战斗领域规则；订阅独立释放。</summary>
public sealed class CombatReplaySource(TimeProvider? timeProvider = null) : ICombatObservationSource
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long generation = DateTimeOffset.UtcNow.Ticks;
    private int active;
    public int ActiveSubscriptions => Volatile.Read(ref active);
    public CombatCapabilities Capabilities(string? gameSessionId) => new(true,
        new[] { "combat.damage.local", "combat.effects.self", "combat.effects.monster" }.Select(id =>
            new CombatCapability(id, CapabilityStatus.Available, ReplayProvider.Source, null, null, null))
            .Append(new CombatCapability("combat.healing.local", CapabilityStatus.Unavailable, ReplayProvider.Source,
                "combat.sourceUnavailable", null, null))
            .Append(new CombatCapability("combat.health.monster", CapabilityStatus.Available, ReplayProvider.Source, null, null, null)).ToArray());
    public ValueTask<ICombatObservationSubscription> OpenAsync(string observationId, CombatObservationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scenario = request.ReplayScenarioId ?? "normal";
        if (scenario is not ("normal" or "partial" or "failure" or "live")) throw new OperationException("request.invalid", "战斗回放场景为 normal、partial、failure 或 live");
        var key = new EntityKey(request.GameSessionId, new((ulong)Interlocked.Increment(ref generation)), new(9007199254740993), new(1));
        Interlocked.Increment(ref active);
        return ValueTask.FromResult<ICombatObservationSubscription>(new Subscription(request, key, scenario, clock, () => Interlocked.Decrement(ref active)));
    }
    private sealed class Subscription(CombatObservationRequest request, EntityKey self, string scenario, TimeProvider clock, Action release) : ICombatObservationSubscription
    {
        private readonly CancellationTokenSource lifetime = new();
        private int disposed;
        public ObservationSource Source => ReplayProvider.Source;
        public CombatEntityObservation LocalEntity => CombatReplayFrames.Entity(self, CombatEntityRole.LocalPlayer, "模擬角色");
        public double ClockRate => 1000d / request.ReplayIntervalMs;
        public async IAsyncEnumerable<CombatFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            var step = 1;
            while (step <= 3 || scenario == "live")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(request.ReplayIntervalMs), clock, combined.Token);
                if (scenario == "failure" && step == 2) throw new OperationException("combat.sourceUnavailable", "合成观察源失联");
                yield return CombatReplayFrames.Frame(self, scenario, step++, clock.GetUtcNow());
            }
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) { lifetime.Cancel(); lifetime.Dispose(); release(); }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// 合成帧：normal、partial、failure 只有前三步带伤害；live 为模拟宿主的持续场景，每步按三步周期重复同样的伤害，
/// 并给两个怪物 HP（第一个按伤害递减、第二个为不下发血量的加密形态）与本机主目标，供前端在模拟宿主上开发定时测量与血量显示。
/// </summary>
public static class CombatReplayFrames
{
    public static CombatEntityObservation Entity(EntityKey key, CombatEntityRole role, string name) =>
        new(key, name, name, role, null, false, true, ObservationQuality.Confirmed);
    public static CombatFrame Frame(EntityKey self, string scenario, int step, DateTimeOffset at)
    {
        var monster = self with { RawInstanceId = new(9007199254740995) };
        var other = monster with { RawInstanceId = new(monster.RawInstanceId.Value + (1UL << 32)) };
        var mono = (step - 1) * 1000d; var partial = scenario == "partial" && step == 2;
        var live = scenario == "live"; var hit = live ? (step - 1) % 3 + 1 : step;
        CombatEffectObservation Effect(EntityKey entity, ulong instance) => new(CombatEffectChangeKind.Refresh, entity,
            new(100), new(instance), self, EffectDurationKind.Finite, 3000, step, "synthetic", false, at, mono, ObservationQuality.Confirmed);
        var entities = new[] { Entity(self, CombatEntityRole.LocalPlayer, "模擬角色"),
            Entity(monster, CombatEntityRole.Monster, "同名怪物") with { BreakGauge = new(step * 12.5, new(1000), at, ObservationQuality.Confirmed) },
            Entity(other, CombatEntityRole.Monster, "同名怪物") with { BreakGauge = new(step * 25.5, new(2000), at, ObservationQuality.Confirmed) } };
        if (live)
        {
            const ulong maximum = 1_000_000;
            var current = maximum - (ulong)(step * 2400 % (long)maximum);
            entities[1] = entities[1] with { Health = new(new(current), new(maximum), at, "read", Encrypted: true, Updated: true), IsBoss = true, Dead = false, ActorType = "monster" };
            entities[2] = entities[2] with { Health = new(new(maximum), new(maximum), at, "read", Encrypted: true, Updated: false), IsBoss = false, Dead = false, ActorType = "monster" };
        }
        return new(1, ReplayProvider.Source, self.GameSessionId, self.ObservationGeneration, new((ulong)step), at, mono, entities,
            step <= 3 || live ? [new("synthetic-hit-" + step, ReplayProvider.Source, self, hit == 2 ? other : monster, new(1000), new(1200),
                CombatDamageKind.Unclassified, at, mono, new((ulong)step), 0, hit == 2, true, ObservationQuality.Confirmed)
                { PowerfulHit = hit is 1 or 3, RapidHit = hit is 2 or 3, DoubleHit = hit == 3,
                  Classification = hit switch { 1 => CombatDamageClassification.Direct, 2 => CombatDamageClassification.Indirect,
                      _ => CombatDamageClassification.DamageOverTime },
                  BreakedHit = hit is 1 or 2, BreakOccurred = false, FirstAttack = hit == 1, DefaultAttack = false, MultiHit = hit == 1, AdditionalHit = hit == 2,
                  Elements = [hit switch { 1 => CombatDamageElement.Fire, 2 => CombatDamageElement.Ice, _ => CombatDamageElement.Lightning }] }] : [],
            [], step <= 3 ? [new(self, !partial, partial ? [] : [Effect(self, 200)]), new(monster, !partial, partial ? [] : [Effect(monster, 201)]),
                new(other, !partial, partial ? [] : [Effect(other, 202)])] : [],
            partial ? [new("synthetic-gap", mono, mono + 500, new(1))] : [], ObservationResumed: scenario == "partial" && step == 3,
            MainTarget: live ? new(monster, at, mono) : null);
    }
}
