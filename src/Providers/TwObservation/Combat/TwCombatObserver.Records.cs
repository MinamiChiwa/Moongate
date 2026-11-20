using System.Globalization;
using MabiTool.Contracts;
using Op = MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol;

namespace MabiTool.Providers.TwObservation.Combat;

public sealed partial class TwCombatObserver
{
    /// <summary>待发记录。伤害与治疗在流内位置附近等待关联，其余记录随队列顺序发布。</summary>
    private abstract class PendingRecord(double mono, DateTimeOffset at)
    {
        public double Mono { get; } = mono;
        public DateTimeOffset At { get; } = at;
    }
    private sealed class PendingEffect(CombatEffectObservation effect, double mono, DateTimeOffset at) : PendingRecord(mono, at)
    {
        public CombatEffectObservation Effect { get; } = effect;
    }
    private sealed class PendingState(CombatStateChange state, double mono, DateTimeOffset at) : PendingRecord(mono, at)
    {
        public CombatStateChange State { get; } = state;
    }
    /// <summary>需要等待流内后续字节的记录：关联窗口为自身起点之后线长再加 <see cref="Op.CorrelationWindowBytes"/>。</summary>
    private abstract class AnchoredRecord(ulong offset, int wireLength, double mono, DateTimeOffset at) : PendingRecord(mono, at)
    {
        public ulong Offset { get; } = offset;
        public int WireLength { get; } = wireLength;
        public ulong WindowEnd => Offset + (ulong)WireLength + Op.CorrelationWindowBytes;
    }
    private sealed class PendingDamage(ulong offset, int wireLength, ulong attacker, ulong target, ulong amount, ulong hitFlags,
        double mono, DateTimeOffset at) : AnchoredRecord(offset, wireLength, mono, at)
    {
        public ulong Attacker { get; } = attacker;
        public ulong Target { get; } = target;
        public ulong Amount { get; } = amount;
        public ulong HitFlags { get; } = hitFlags;
    }
    /// <summary>一条施放或受疗。受疗带 <see cref="Receipt"/>；自疗合并时施放引用受疗的回执，被合并的受疗标记为已吸收、不再单独发布。</summary>
    private sealed class PendingHeal(ulong offset, int wireLength, CombatHealDirection direction, ulong? target, ulong hitFlags,
        ulong amount, double mono, DateTimeOffset at) : AnchoredRecord(offset, wireLength, mono, at)
    {
        public CombatHealDirection Direction { get; } = direction;
        public ulong? Target { get; } = target;
        public ulong HitFlags { get; } = hitFlags;
        public ulong Amount { get; } = amount;
        public HealReceipt? Receipt { get; set; }
        public bool Absorbed { get; set; }
    }
    /// <summary>受疗与紧邻本机 HP 更新的配对结果。</summary>
    private sealed class HealReceipt(ulong offset, int wireLength, ulong amount)
    {
        public ulong Offset { get; } = offset;
        public ulong End => Offset + (ulong)wireLength;
        public ulong Amount { get; } = amount;
        public ulong? Effective { get; private set; }
        public ulong? Overheal { get; private set; }
        public string? Mismatch { get; private set; }
        public bool Settled => Effective is not null || Mismatch is not null;
        /// <summary>HP 前后值均已知时判定：上升量在 1 至治疗量之间给出有效量与过量，否则记 hp-mismatch；有一端未知时保持未配对。</summary>
        public void Settle(HealthStep step)
        {
            if (step.Before is not { } before || step.After is not { } after) return;
            var rise = after - before;
            if (rise <= 0 || (ulong)rise > Amount) { Mismatch = "hp-mismatch"; return; }
            Effective = (ulong)rise; Overheal = Amount - (ulong)rise; Mismatch = null;
        }
    }
    private sealed record SkillCast(ulong Offset, ulong Caster, ulong Target, uint SkillId, ulong HitFlags);
    private sealed record HealthStep(ulong Offset, int WireLength, long? Before, long? After)
    {
        public ulong End => Offset + (ulong)WireLength;
    }

    // ---- 技能与伤害 ----

    private void OnHitPresentation(TwHitPresentation hit, ulong offset)
    {
        recentSkills.RemoveAll(skill => offset > skill.Offset && offset - skill.Offset > SkillRetentionBytes);
        if (recentSkills.Count >= MaximumSkills) { recentSkills.RemoveAt(0); AddGap("combat.skillCapacity", 1); }
        recentSkills.Add(new(offset, hit.Caster, hit.Target, hit.SkillId, hit.HitFlags));
        Observe(hit.Caster); Observe(hit.Target);
    }

    /// <summary>非正伤害（含 -1 无敌）、缺实体与自伤不产生事件。</summary>
    private void OnDamage(TwDamageFloater damage, ulong offset, int wireLength)
    {
        if (damage.Attacker == 0 || damage.Target == 0 || damage.Attacker == damage.Target || damage.Amount <= 0) return;
        Observe(damage.Attacker); Observe(damage.Target);
        if (damage.Attacker == localEntity) NoteLocalTarget(damage.Target);
        Enqueue(new PendingDamage(offset, wireLength, damage.Attacker, damage.Target, (ulong)damage.Amount, damage.HitFlags, inputMs, clockUtc));
    }

    /// <summary>技能近邻：伤害前后 400 字节内、目标与 HitFlags 完全相同的技能表现，取流内距离最近者，等距取较早者。</summary>
    private SkillCast? NearestSkill(PendingDamage damage)
    {
        var low = damage.Offset > Op.CorrelationWindowBytes ? damage.Offset - Op.CorrelationWindowBytes : 0;
        var high = checked(damage.WindowEnd);
        SkillCast? best = null; var bestDistance = ulong.MaxValue;
        foreach (var skill in recentSkills)
        {
            if (skill.Offset < low || skill.Offset >= high || skill.Target != damage.Target || skill.HitFlags != damage.HitFlags) continue;
            var distance = Distance(skill.Offset, damage.Offset);
            if (distance < bestDistance || distance == bestDistance && skill.Offset < best!.Offset) { best = skill; bestDistance = distance; }
        }
        return best;
    }

    private CombatDamageEvent DamageEvent(PendingDamage damage)
    {
        var skill = NearestSkill(damage);
        var flags = new TwHitFlags(damage.HitFlags);
        return new($"dmg-{generation}-{damage.Offset}", source, Key(damage.Attacker), Key(damage.Target),
            new(skill?.SkillId ?? 0), new(damage.Amount), CombatDamageKind.Unclassified, damage.At, damage.Mono,
            new(damage.Offset), 0, flags.Critical, true, ObservationQuality.Confirmed)
        {
            SkillCasterKey = skill is null ? null : Key(skill.Caster), SkillResolved = skill is not null,
            Chain = skill is null ? null : skill.Caster != damage.Attacker,
            PowerfulHit = flags.PowerfulHit, RapidHit = flags.RapidHit, DoubleHit = flags.DoubleHit,
            Classification = flags.Classification,
            BreakedHit = flags.BreakedHit, BreakOccurred = flags.BreakOccurred, FirstAttack = flags.FirstAttack,
            DefaultAttack = flags.DefaultAttack, MultiHit = flags.MultiHit, AdditionalHit = flags.AdditionalHit,
            Elements = flags.Elements()
        };
    }

    // ---- 效果 ----

    /// <summary>
    /// Add/Refresh：所有者先登记出现；实例满额且无可淘汰实体时记 combat.effectCapacity。同一实例的效果或施放者改变时，
    /// 先发布旧实例的移除，再发布本次变化。
    /// </summary>
    private void OnBuffUpdate(TwBuffUpdate buff, CombatEffectChangeKind change)
    {
        Observe(buff.Owner);
        if (buff.Stacks > int.MaxValue) throw new InvalidDataException("效果层数超出范围");
        var (durationKind, durationMs) = TwBuffRemaining.Classify(buff.EndAtSeconds);
        var key = (buff.Owner, buff.Instance);
        if (!activeBuffs.ContainsKey(key) && activeBuffs.Count >= MaximumEffects && !EvictIdle()) { AddGap("combat.effectCapacity", 1); return; }
        if (activeBuffs.TryGetValue(key, out var previous) && previous != (buff.BuffId, buff.Caster))
            EnqueueEffect(CombatEffectChangeKind.Remove, buff.Owner, previous.Buff, buff.Instance, previous.Caster, EffectDurationKind.Unknown, null, null);
        activeBuffs[key] = (buff.BuffId, buff.Caster);
        Observe(buff.Caster);
        EnqueueEffect(change, buff.Owner, buff.BuffId, buff.Instance, buff.Caster, durationKind, durationMs, (int)buff.Stacks);
    }

    /// <summary>Expire 沿用登记时的效果与施放者；未登记的实例以效果 0、无施放者发布。</summary>
    private void OnBuffExpire(TwBuffExpire expire)
    {
        Observe(expire.Owner);
        var key = (expire.Owner, expire.Instance);
        var previous = activeBuffs.GetValueOrDefault(key);
        activeBuffs.Remove(key);
        EnqueueEffect(CombatEffectChangeKind.Remove, expire.Owner, previous.Buff, expire.Instance, previous.Caster, EffectDurationKind.Unknown, null, null);
    }

    private void EnqueueEffect(CombatEffectChangeKind change, ulong owner, uint buff, ulong instance, ulong caster,
        EffectDurationKind durationKind, double? durationMs, int? stacks) =>
        Enqueue(new PendingEffect(new(change, Key(owner), new(buff), new(instance), caster == 0 ? null : Key(caster),
            durationKind, durationMs, stacks, null, false, clockUtc, inputMs, ObservationQuality.Confirmed), inputMs, clockUtc));

    // ---- 治疗与本机状态 ----

    /// <summary>本机受疗：先与紧前的本机 HP 更新配对；400 字节内有同旗标、同数值、尚未合并的本机自疗施放时并入该施放。</summary>
    private void OnHealReceived(TwHealReceipt heal, ulong offset, int wireLength)
    {
        if (heal.Amount == 0) return;
        var amount = (ulong)heal.Amount;
        var receipt = new HealReceipt(offset, wireLength, amount);
        if (healthSteps.Count > 0 && healthSteps[^1].End == offset) receipt.Settle(healthSteps[^1]);
        if (localEntity is not null && NearestHeal(offset, candidate => candidate.Direction == CombatHealDirection.Cast &&
            candidate.Receipt is null && candidate.Target == localEntity && candidate.HitFlags == heal.HitFlags && candidate.Amount == amount) is { } cast)
        { cast.Receipt = receipt; return; }
        Enqueue(new PendingHeal(offset, wireLength, CombatHealDirection.Received, null, heal.HitFlags, amount, inputMs, clockUtc) { Receipt = receipt });
    }

    /// <summary>本机施放：目标为本机时吸收 400 字节内同旗标、同数值、尚未吸收的受疗，沿用其回执。</summary>
    private void OnHealCast(TwHealCast heal, ulong offset, int wireLength)
    {
        if (heal.Amount == 0) return;
        var amount = (ulong)heal.Amount;
        Observe(heal.Target);
        var cast = new PendingHeal(offset, wireLength, CombatHealDirection.Cast, heal.Target, heal.HitFlags, amount, inputMs, clockUtc);
        if (localEntity == heal.Target && NearestHeal(offset, candidate => candidate.Direction == CombatHealDirection.Received &&
            !candidate.Absorbed && candidate.HitFlags == heal.HitFlags && candidate.Amount == amount) is { } received)
        { received.Absorbed = true; cast.Receipt = received.Receipt; }
        Enqueue(cast);
    }

    private PendingHeal? NearestHeal(ulong offset, Func<PendingHeal, bool> match)
    {
        PendingHeal? best = null; var bestDistance = ulong.MaxValue;
        foreach (var record in queue)
        {
            if (record is not PendingHeal heal) continue;
            var distance = Distance(heal.Offset, offset);
            if (distance > Op.CorrelationWindowBytes || distance >= bestDistance || !match(heal)) continue;
            best = heal; bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// 本机 HP：记录前后值（加密形态与负值为未知），最多保留最近 <see cref="MaximumHealthChanges"/> 条；
    /// 紧接在尚未配对的受疗之后到达时补配。其他实体的 HP 不保存。
    /// </summary>
    private void OnHealth(TwHealthUpdate health, ulong offset, int wireLength)
    {
        if (localEntity is not { } local || health.Entity != local) return;
        var step = new HealthStep(offset, wireLength, localHealth, health.PlainValue);
        localHealth = health.PlainValue;
        if (healthSteps.Count >= MaximumHealthChanges) healthSteps.RemoveAt(0);
        healthSteps.Add(step);
        foreach (var record in queue)
            if (record is PendingHeal { Receipt: { Settled: false } receipt } && receipt.End == offset) receipt.Settle(step);
    }

    /// <summary>本机战斗状态与伤害共用队列，保持流内先后（击杀伤害先于其后的 CombatEnd 发布）；0–3 以外的取值原样保留为未知成员。</summary>
    private void OnCombatState(TwCombatStateUpdate update, ulong offset)
    {
        if (localEntity is not { } local || update.Entity != local) return;
        OpenEnum<CombatLocalState> state = update.State is >= 0 and <= 3 ? (CombatLocalState)update.State
            : new OpenEnum<CombatLocalState>("unknown-" + update.State.ToString(CultureInfo.InvariantCulture));
        Enqueue(new PendingState(new(state, clockUtc, inputMs, new(offset)), inputMs, clockUtc));
    }

    private CombatHealEvent HealEvent(PendingHeal heal)
    {
        var receipt = heal.Receipt;
        UInt64String? effective = receipt?.Effective is { } value ? new(value) : null;
        UInt64String? overheal = receipt?.Overheal is { } extra ? new(extra) : null;
        var reason = receipt is null || effective is not null ? null : receipt.Mismatch ?? "hp-unpaired";
        return new($"heal-{generation}-{heal.Offset}", heal.Direction, heal.Target is { } target ? Key(target) : null,
            new(heal.HitFlags), new(heal.Amount), effective, overheal, heal.Direction == CombatHealDirection.Cast && receipt is not null,
            heal.At, heal.Mono, new(heal.Offset), ObservationQuality.Confirmed, reason);
    }

    private static ulong Distance(ulong left, ulong right) => left > right ? left - right : right - left;

    // ---- 发布 ----

    private void Enqueue(PendingRecord record)
    {
        if (queue.Count >= MaximumPending) { AddGap("combat.parserCapacity", 1); return; }
        queue.Enqueue(record);
    }

    /// <summary>缺口使所有怪物的破防值与已读 HP 过期；缺口列表满 32 条后并入最后一条并累计丢弃数。</summary>
    private void AddGap(string reason, ulong dropped)
    {
        foreach (var (id, entity) in entities.ToArray())
            if (entity.BreakGauge is { } gauge) entities[id] = entity with { BreakGauge = gauge with { Stale = true, Reason = reason } };
        MarkHealthStale();
        if (gaps.Count < MaximumGaps) { gaps.Add(new(reason, inputMs, inputMs, new(dropped))); return; }
        var last = gaps[^1];
        gaps[^1] = last with { ToMs = inputMs, DroppedEvents = new(checked(last.DroppedEvents.Value + dropped)) };
    }

    private bool Waiting(AnchoredRecord record, double nowMs, bool force) =>
        !force && nowMs - record.Mono < Op.CorrelationWaitMs && lastStreamOffset < record.WindowEnd;

    /// <summary>一次发布所收集的记录；时点取已发布下限、快照时点与所含记录的最大值。</summary>
    private sealed class Batch(double mono, DateTimeOffset at)
    {
        public List<CombatDamageEvent> Damage { get; } = [];
        public List<CombatEffectObservation> Effects { get; } = [];
        public List<CombatHealEvent> Healing { get; } = [];
        public List<CombatStateChange> States { get; } = [];
        public double Mono { get; set; } = mono;
        public DateTimeOffset At { get; private set; } = at;
        public int Count => Damage.Count + Effects.Count + Healing.Count + States.Count;
        public void Include(PendingRecord record)
        {
            Mono = Math.Max(Mono, record.Mono);
            if (record.At > At) At = record.At;
        }
    }

    /// <summary>
    /// 按队列顺序取出可发布的记录，至多 <see cref="MaximumBatchRecords"/> 条；队首伤害或治疗仍在等待期且后续字节未越过关联窗口时停止。
    /// 被自疗施放吸收的受疗出队但不进入批次。
    /// </summary>
    private Batch Drain(double nowMs, bool force)
    {
        var batch = new Batch(Math.Max(publishedMs, snapshotMs), clockUtc);
        while (batch.Count < MaximumBatchRecords && queue.TryPeek(out var record))
        {
            if (record is AnchoredRecord anchored && Waiting(anchored, nowMs, force)) break;
            switch (record)
            {
                case PendingDamage hit: batch.Damage.Add(DamageEvent(hit)); break;
                case PendingHeal { Absorbed: false } heal: batch.Healing.Add(HealEvent(heal)); break;
                case PendingEffect effect: batch.Effects.Add(effect.Effect); break;
                case PendingState state: batch.States.Add(state.State); break;
            }
            queue.Dequeue(); batch.Include(record);
        }
        return batch;
    }

    /// <summary>
    /// 没有记录与缺口时的快照帧：队列已空，且实体快照有变化、只有 HP 变化且距上次发布满 200 ms，或本机已识别且距上次发布满 100 ms。
    /// </summary>
    private bool SnapshotDue(double nowMs) =>
        queue.Count == 0 && (snapshotDirty || healthDirty && nowMs - publishedMs >= HealthPublishMs || LocalEntity is not null && nowMs - publishedMs >= KeepAliveMs);

    /// <summary>队首记录已超过关联等待期（或强制发布）时继续成批发布。</summary>
    private bool MoreDue(double nowMs, bool force) =>
        queue.TryPeek(out var head) && (force || nowMs - head.Mono >= Op.CorrelationWaitMs);

    /// <summary>成批发布待发记录与缺口；每帧附有界实体快照，使新订阅取得本机及目标归属。</summary>
    private void Publish(double nowMs, bool force = false)
    {
        PurgeDeparted(nowMs);
        do
        {
            var batch = Drain(nowMs, force);
            if (batch.Count == 0 && gaps.Count == 0)
            {
                if (!SnapshotDue(nowMs)) return;
                batch.Mono = Math.Max(batch.Mono, nowMs);
            }
            var batchGaps = gaps.ToArray(); gaps.Clear();
            foreach (var gap in batchGaps) batch.Mono = Math.Max(batch.Mono, gap.ToMs ?? gap.FromMs);
            // 已发布的帧时点是后续输入的下限：保活帧之后才读到的较早包不能落到 Host 的 250 ms 批内窗口之外。
            publishedMs = batch.Mono; inputMs = Math.Max(inputMs, batch.Mono); snapshotDirty = false; healthDirty = false;
            if (localEntity is not null) reseed = null;
            publish(new(1, source, session, new(generation), new(checked(++sequence)), batch.At, batch.Mono,
                ReportEntities(batch.Mono), batch.Damage, batch.Effects, [], batchGaps, LocalEntity is not null,
                Healing: batch.Healing.Count > 0 ? batch.Healing : null, CombatStates: batch.States.Count > 0 ? batch.States : null,
                MainTarget: MainTargetField()));
        } while (MoreDue(nowMs, force));
    }
}
