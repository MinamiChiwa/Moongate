using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

/// <summary>选定的当前攻击目标：Target 为显示的实体，RedirectedFrom 为改指前的原目标（血量由同场首领体现时）。</summary>
internal sealed record CurrentTargetSelection(EntityKey Target, string Basis, double SelectedMs, EntityKey? RedirectedFrom);

/// <summary>
/// 当前攻击目标的选取（时间均为观察单调毫秒）：
/// 最近 3 秒内本机有正数伤害时，主目标在这 3 秒内被本机命中过就取主目标，否则取最近 2 秒内本机伤害量最大的目标；
/// 新候选持续领先 1.5 秒才切换，主目标改变、当前目标死亡满 3 秒或离场时立即切换；
/// 没有本机伤害时依次回退到怪物主目标、上一个目标（至多 10 秒）、HP 正在更新且最近掉血的首领；
/// 本机不在战斗中且 10 秒内没有本机伤害与主目标时清空。选中目标的血量由同场首领体现时改指该首领。
/// </summary>
internal sealed class CurrentTargetSelector
{
    public const double ActiveWindowMs = 3000, LeadWindowMs = 2000, DebounceMs = 1500, PreviousHoldMs = 10000, DeathHoldMs = 3000, IdleClearMs = 10000;
    private const int MaximumHits = 4096;
    public const string MainTargetBasis = "main-target", RecentDamage = "recent-damage", FallbackMainTarget = "fallback-main-target",
        FallbackPrevious = "fallback-previous", FallbackBoss = "fallback-boss";

    private readonly Queue<(double At, EntityKey Target, ulong Amount)> hits = [];
    private double lastDamageMs = double.NegativeInfinity;
    private EntityKey? mainTarget;
    private bool mainKnown, mainChanged;
    private EntityKey? current;
    private string basis = RecentDamage;
    private double selectedMs, activeMs;
    private double? deadSince;
    private EntityKey? pending;
    private double pendingSince;
    /// <summary>切换次数（不含清空），供离线回放统计抖动。</summary>
    public int Switches { get; private set; }

    public void Hit(double at, EntityKey target, ulong amount)
    {
        if (amount == 0 || !double.IsFinite(at)) return;
        if (hits.Count >= MaximumHits) hits.Dequeue();
        hits.Enqueue((at, target, amount));
        lastDamageMs = Math.Max(lastDamageMs, at);
    }

    public void MainTarget(EntityKey? target)
    {
        if (mainKnown && target == mainTarget) return;
        mainChanged = mainKnown || target is not null; mainTarget = target; mainKnown = true;
    }

    public void Clear()
    {
        hits.Clear(); current = null; pending = null; deadSince = null; mainTarget = null; mainKnown = false; mainChanged = false;
        lastDamageMs = double.NegativeInfinity;
    }

    /// <param name="health">实体当前的有效血量（已按“由同场首领体现”调整）。</param>
    /// <param name="lastDrop">实体最近一次 HP 下降的时点；没有时为空。</param>
    public CurrentTargetSelection? Evaluate(double now, IReadOnlyDictionary<EntityKey, CombatEntity> entities,
        Func<EntityKey, CombatEntityHealth?> health, Func<EntityKey, double?> lastDrop, OpenEnum<CombatLocalState>? state)
    {
        while (hits.TryPeek(out var oldest) && oldest.At < now - ActiveWindowMs) hits.Dequeue();
        bool Present(EntityKey key) => entities.TryGetValue(key, out var entity) && entity.InScope;
        bool Alive(EntityKey key) => entities.TryGetValue(key, out var entity) && entity.InScope && entity.Dead != true;
        var changed = mainChanged; mainChanged = false;

        if (current is { } shown)
        {
            if (!Present(shown)) { current = null; pending = null; deadSince = null; }
            else if (!Alive(shown)) deadSince ??= now;
        }
        if (current is not null && deadSince is { } died && now - died < DeathHoldMs) return Selection(entities, health, lastDrop);
        var currentGone = current is not null && deadSince is not null;

        EntityKey? candidate = null; var candidateBasis = RecentDamage;
        if (hits.Count > 0 && hits.Any(hit => hit.At >= now - ActiveWindowMs))
        {
            if (mainTarget is { } main && Alive(main) && hits.Any(hit => hit.Target == main)) { candidate = main; candidateBasis = MainTargetBasis; }
            else
            {
                var lead = hits.Where(hit => hit.At >= now - LeadWindowMs && Alive(hit.Target))
                    .GroupBy(hit => hit.Target).Select(group => (Key: group.Key, Sum: group.Aggregate(0UL, (sum, hit) => sum + hit.Amount), Last: group.Max(hit => hit.At)))
                    .OrderByDescending(group => group.Sum).ThenByDescending(group => group.Last).FirstOrDefault();
                candidate = lead.Key ?? hits.Where(hit => Alive(hit.Target)).OrderByDescending(hit => hit.At).Select(hit => (EntityKey?)hit.Target).FirstOrDefault();
            }
        }
        else if (state is { } known && known.KnownValue != CombatLocalState.InCombat && now - lastDamageMs >= IdleClearMs && mainTarget is null)
            candidate = null;
        else if (mainTarget is { } fallback && Alive(fallback) && entities[fallback].Role.KnownValue == CombatEntityRole.Monster)
        { candidate = fallback; candidateBasis = FallbackMainTarget; }
        else if (current is { } previous && !currentGone && now - activeMs <= PreviousHoldMs)
        { candidate = previous; candidateBasis = FallbackPrevious; }
        else if (Boss(entities, health, lastDrop, Alive) is { } boss) { candidate = boss; candidateBasis = FallbackBoss; }

        if (candidate == current && current is not null)
        {
            if (candidateBasis != FallbackPrevious) basis = candidateBasis;
            if (candidateBasis is MainTargetBasis or RecentDamage) activeMs = now;
            pending = null;
            return Selection(entities, health, lastDrop);
        }
        var immediate = current is null || currentGone || candidate is null || changed && candidate == mainTarget;
        if (!immediate)
        {
            if (pending != candidate) { pending = candidate; pendingSince = now; }
            if (now - pendingSince < DebounceMs) return Selection(entities, health, lastDrop);
        }
        if (candidate is not null && current is not null) Switches++;
        current = candidate; basis = candidateBasis; selectedMs = now; deadSince = null; pending = null;
        if (candidateBasis is MainTargetBasis or RecentDamage) activeMs = now;
        if (current is { } chosen && !Alive(chosen)) deadSince = now;
        return Selection(entities, health, lastDrop);
    }

    private CurrentTargetSelection? Selection(IReadOnlyDictionary<EntityKey, CombatEntity> entities, Func<EntityKey, CombatEntityHealth?> health,
        Func<EntityKey, double?> lastDrop)
    {
        if (current is not { } target) return null;
        if (health(target)?.State == "shared" &&
            Boss(entities, health, lastDrop, key => entities.TryGetValue(key, out var entity) && entity.InScope && entity.Dead != true) is { } boss && boss != target)
            return new(boss, basis, selectedMs, target);
        return new(target, basis, selectedMs, null);
    }

    /// <summary>视野内存活、首领标志为真、HP 已读且出现后收到过更新的实体中最近一次掉血最晚者。</summary>
    private static EntityKey? Boss(IReadOnlyDictionary<EntityKey, CombatEntity> entities, Func<EntityKey, CombatEntityHealth?> health,
        Func<EntityKey, double?> lastDrop, Func<EntityKey, bool> alive) =>
        entities.Values.Where(entity => entity.IsBoss == true && alive(entity.Key) && health(entity.Key) is { State: "read", Updated: true })
            .OrderByDescending(entity => lastDrop(entity.Key) ?? double.NegativeInfinity).Select(entity => (EntityKey?)entity.Key).FirstOrDefault();
}
