using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

/// <summary>当前攻击目标的选取规则、防抖、回退与清空，锁血实体改指首领；伤害去重的内容比较与目标分组名称后补。</summary>
public sealed class CombatCurrentTargetTests
{
    private static readonly EntityKey A = Monster, B = Monster with { RawInstanceId = new(9007199254740997) },
        BossKey = Monster with { RawInstanceId = new(9007199254740999) }, Locked = Monster with { RawInstanceId = new(9007199254741001) };

    private static CombatEntityObservation Mob(EntityKey key, ulong? current = 1000, bool boss = false, bool dead = false, bool inScope = true,
        bool encrypted = false, bool updated = true) =>
        Entity(key, CombatEntityRole.Monster, "6A5E8F1C") with
        {
            InScope = inScope, IsBoss = boss, Dead = dead, ActorType = "monster",
            Health = new(current is { } value ? new(value) : null, new(2000), At, current is null ? "unread" : "read",
                Encrypted: encrypted ? true : null, Updated: updated)
        };
    private static IReadOnlyList<CombatEntityObservation> All(params CombatEntityObservation[] extra) =>
        [Entity(Self, CombatEntityRole.LocalPlayer, "測試角色"), .. extra];
    private sealed class Driver
    {
        public CombatSession Session { get; } = Session();
        private ulong sequence;
        public double Now { get; private set; }
        public IReadOnlyList<CombatEntityObservation> Entities = All(Mob(A), Mob(B));
        public CombatMainTarget? Main;
        public CombatStateChange[]? States;
        public void Step(double at, params CombatDamageEvent[] damage)
        {
            Now = at;
            Session.Apply(Frame(++sequence, at, damage, entities: Entities) with { MainTarget = Main, CombatStates = States });
            States = null;
        }
        public CombatCurrentTarget? Target => Session.Capture().Workspace.CurrentTarget;
        public void Hit(double at, EntityKey target, ulong amount = 100) => Step(at, Damage("d" + sequence + "-" + at, at, amount, target: target));
    }

    [Fact]
    public void RecentDamageSelectsTheTargetWithTheMostDamageAndMainTargetWinsWhenHit()
    {
        var driver = new Driver();
        driver.Hit(1000, A, 100);
        Assert.Equal(("recent-damage", CombatSession.EntityId(A)), (driver.Target!.Basis, driver.Target.EntityId));
        // 主目标改为 B 且 B 在 3 秒内被本机命中：立即切换，依据为主目标（即使 A 的伤害更多）。
        driver.Hit(1100, A, 5000);
        driver.Main = new(B, At.AddMilliseconds(1200), 1200);
        driver.Hit(1200, B, 10);
        Assert.Equal(("main-target", CombatSession.EntityId(B)), (driver.Target!.Basis, driver.Target.EntityId));
    }

    [Fact]
    public void ANewCandidateMustLeadForOneAndAHalfSeconds()
    {
        var driver = new Driver();
        driver.Hit(1000, A, 100);
        Assert.Equal(CombatSession.EntityId(A), driver.Target!.EntityId);
        driver.Hit(1500, B, 1000);
        Assert.Equal(CombatSession.EntityId(A), driver.Target!.EntityId);
        driver.Hit(2600, B, 1000);
        Assert.Equal(CombatSession.EntityId(A), driver.Target!.EntityId);
        driver.Hit(3100, B, 1000);
        Assert.Equal(CombatSession.EntityId(B), driver.Target!.EntityId);
        Assert.Equal(1, driver.Session.TargetSwitches);
    }

    [Fact]
    public void WithoutLocalDamageTheTargetFallsBackInOrder()
    {
        var driver = new Driver { Entities = All(Mob(A), Mob(B), Mob(BossKey, boss: true)) };
        driver.Hit(1000, A);
        // 主目标是怪物：回退到主目标（立即切换）。
        driver.Main = new(B, At.AddMilliseconds(5000), 5000);
        driver.Step(5000);
        Assert.Equal(("fallback-main-target", CombatSession.EntityId(B)), (driver.Target!.Basis, driver.Target.EntityId));
        // 没有主目标：上一个目标至多保留 10 秒（自最后一次有效伤害起）。
        driver.Main = new(null, At.AddMilliseconds(6000), 6000);
        driver.Step(6000);
        Assert.Equal(CombatSession.EntityId(B), driver.Target!.EntityId);
        // 超过 10 秒：HP 正在更新的首领。
        driver.Entities = All(Mob(A), Mob(B), Mob(BossKey, 1500, boss: true));
        driver.Step(12000);
        driver.Entities = All(Mob(A), Mob(B), Mob(BossKey, 1400, boss: true));
        driver.Step(16000);
        Assert.Equal(("fallback-boss", CombatSession.EntityId(BossKey)), (driver.Target!.Basis, driver.Target.EntityId));
    }

    [Fact]
    public void DeadTargetsAreHeldForThreeSecondsAndDepartedTargetsClearAtOnce()
    {
        var driver = new Driver();
        driver.Hit(1000, A);
        driver.Entities = All(Mob(A, 0, dead: true), Mob(B));
        driver.Step(1200);
        Assert.Equal((CombatSession.EntityId(A), true), (driver.Target!.EntityId, driver.Target.Dead == true));
        driver.Hit(1500, B);
        Assert.Equal(CombatSession.EntityId(A), driver.Target!.EntityId);
        driver.Hit(4300, B);
        Assert.Equal(CombatSession.EntityId(B), driver.Target!.EntityId);
        driver.Entities = All(Mob(A, 0, dead: true), Mob(B, inScope: false));
        driver.Step(4400);
        Assert.Null(driver.Target);
    }

    [Fact]
    public void OutOfCombatWithoutDamageOrMainTargetClearsAfterTenSeconds()
    {
        var driver = new Driver();
        driver.Hit(1000, A);
        driver.States = [new(CombatLocalState.NonCombat, At.AddMilliseconds(2000), 2000, null)];
        driver.Step(2000);
        driver.Step(9000);
        Assert.NotNull(driver.Target);
        driver.Step(11500);
        Assert.Null(driver.Target);
    }

    [Fact]
    public void LockedEncryptedTargetRedirectsToTheUpdatingBoss()
    {
        var driver = new Driver { Entities = All(Mob(BossKey, 1500, boss: true, encrypted: true), Mob(Locked, 2000, encrypted: true, updated: false)) };
        driver.Hit(1000, Locked, 1000);
        var target = driver.Target!;
        Assert.Equal((CombatSession.EntityId(BossKey), CombatSession.EntityId(Locked)), (target.EntityId, target.RedirectedFrom));
        var locked = driver.Session.Capture().Workspace.Entities.Single(entity => entity.Key == Locked).Health!;
        Assert.Equal(("shared", "combat.healthShared"), (locked.State, locked.Reason));
        // 没有可改指的首领：保留原目标与其血量状态。
        var alone = new Driver { Entities = All(Mob(Locked, 2000, encrypted: true, updated: false)) };
        alone.Hit(1000, Locked, 1000);
        Assert.Equal((CombatSession.EntityId(Locked), (string?)null, "shared"), (alone.Target!.EntityId, alone.Target.RedirectedFrom, alone.Target.Health!.State));
    }

    [Fact]
    public void DuplicateDamageIsDroppedAndChangedContentIsAConflict()
    {
        var session = Session();
        var hit = Damage("same", 1000) with { Critical = true, Elements = [CombatDamageElement.Fire, CombatDamageElement.Ice] };
        session.Apply(Frame(1, 1000, [hit]));
        session.Apply(Frame(2, 1100, [hit with { ObservedAt = At.AddMilliseconds(1100), MonotonicMs = 1100 }]));
        Assert.DoesNotContain(session.Capture().Gaps, gap => gap.Reason == "damage-conflict");
        foreach (var changed in new[] { hit with { Amount = new(101) }, hit with { Critical = false }, hit with { Critical = null },
            hit with { Elements = [CombatDamageElement.Ice, CombatDamageElement.Fire] }, hit with { Elements = null }, hit with { TargetEntity = A with { RawInstanceId = new(1) } },
            hit with { AdditionalHit = true }, hit with { Classification = CombatDamageClassification.Indirect } })
        {
            var probe = Session();
            probe.Apply(Frame(1, 1000, [hit]));
            probe.Apply(Frame(2, 1100, [changed with { MonotonicMs = 1100 }]));
            Assert.Contains(probe.Capture().Gaps, gap => gap.Reason == "damage-conflict");
        }
    }

    [Fact]
    public void TargetGroupsReceiveALabelOnceTheEntityTypeIsKnown()
    {
        var session = Session();
        session.Apply(Frame(1, 1000, [Damage("a", 1000)], entities: [Entity(Monster, CombatEntityRole.Unknown, Monster.RawInstanceId.ToString())]));
        Assert.Null(session.Capture().Workspace.Encounter!.Targets.Single().ResolvedName);
        session.Apply(Frame(2, 1100, [Damage("b", 1100)], entities: [Entity(Monster, CombatEntityRole.Unknown, Monster.RawInstanceId.ToString()) with
            { ActorType = "npc-companion", SheetId = new(1653633150) }]));
        var label = session.Capture().Workspace.Encounter!.Targets.Single().ResolvedName!;
        Assert.Equal(("npc-companion", 1653633150UL), (label.Kind, label.SheetId.Value));
    }
}
