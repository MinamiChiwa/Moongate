using System.Buffers.Binary;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.Combat;

public sealed partial class TwCombatObserver
{
    public const string HealthUnread = "combat.healthUnread", HealthUndecryptable = "combat.healthUndecryptable",
        HealthMaximumUnknown = "combat.healthMaximumUnknown";
    /// <summary>延续记录（Worker 重启后随捕获请求回传）至多保留的实体数：首领优先，其次有解码信息的怪物。</summary>
    public const int MaximumCarriedEntities = 64;
    /// <summary>被淘汰实体的身份备忘上限：同一连接内再次出现时恢复类型、表 ID、最大 HP 与解码信息。</summary>
    private const int MaximumMemo = 256, MaximumLocalTargets = 512;
    /// <summary>只有 HP 变化时的实体快照发布间隔下限：高频 HP 更新按实体合并，不为每次更新发布一帧。</summary>
    private const double HealthPublishMs = 200;
    /// <summary>
    /// 当前目标候选（本机命中过的目标、本机主目标、首领）的血量、最大值、死亡或离场变化后，或本机主目标改变后，距上次发布满该间隔即发布实体快照帧
    /// （与界面每秒至多 60 次的刷新上限相当）；其他自动体只有 HP 变化时仍按 <see cref="HealthPublishMs"/>。
    /// </summary>
    private const double HealthUrgentMs = 16;
    /// <summary>离场实体以 inScope=false 发布后，从实体表移除前的保留时长。</summary>
    private const double DepartedRetentionMs = 3000;

    /// <summary>自动体（非玩家角色）的类型、表 ID 与 HP 状态；HP 解码信息只在内存中。</summary>
    private sealed class ActorState
    {
        public int Type;
        public uint Sheet;
        public bool Boss, Encrypted, Updated, Dead, Stale;
        public long? Max, Current;
        public DateTimeOffset? At;
        public ulong KeyId;
        public byte[]? Iv;
        public string? Failure;
        public ActorState Copy() => (ActorState)MemberwiseClone();
    }
    private readonly Dictionary<ulong, ActorState> actors = [];
    /// <summary>被淘汰实体的记录（不含观测时点），按淘汰顺序；同一连接内再次出现时恢复。</summary>
    private readonly Dictionary<ulong, (CombatEntityObservation Entity, ActorState? Actor)> memo = [];
    private readonly Queue<ulong> memoOrder = [];
    private readonly Dictionary<ulong, double> departed = [];
    private readonly HashSet<ulong> localTargets = [];
    private readonly TwHealthCipher cipher = new();
    private (byte[] Key, byte[] Iv)? broadcast;
    private (ulong? Target, double Mono, DateTimeOffset At)? mainTarget;
    private bool healthDirty, healthUrgent;
    private ulong carryRevision;
    /// <summary>
    /// 实体 HP 的随帧上报：只有本机命中过的目标、本机主目标与首领（当前目标规则用到的候选）带 health，且只在相对上次上报有变化后的
    /// <see cref="HealthRepeatMs"/> 内随帧附上（不短于 Worker 合批窗口 100 ms，合并只保留最后一份实体快照时不会丢失变化），
    /// 每 <see cref="HealthKeyframeMs"/> 全量附带一次（新订阅、丢帧后恢复）。其余帧中这些实体不带 health，宿主沿用上次的值；
    /// 新场景（换代次）后上报记录清空，首帧即为全量。剥离 health 的实体副本按原对象缓存，未变化的实体不重复编码。
    /// </summary>
    private const double HealthRepeatMs = 250, HealthKeyframeMs = 2000;
    private readonly Dictionary<ulong, (CombatEntityHealth Health, double At)> reported = [];
    private readonly Dictionary<ulong, (CombatEntityObservation Full, CombatEntityObservation Stripped)> stripped = [];
    private double keyframeMs = double.NegativeInfinity;

    private bool HealthCandidate(ulong id) =>
        localTargets.Contains(id) || mainTarget?.Target == id || actors.TryGetValue(id, out var actor) && actor.Boss;

    private IReadOnlyList<CombatEntityObservation> ReportEntities(double nowMs)
    {
        if (!interest.Health) return entities.Values.ToArray();
        var keyframe = nowMs - keyframeMs >= HealthKeyframeMs;
        if (keyframe) keyframeMs = nowMs;
        var result = new CombatEntityObservation[entities.Count];
        var index = 0;
        foreach (var (id, entity) in entities)
        {
            var include = entity.Health is not { } health || HealthCandidate(id) && (Changed(id, health, nowMs) | keyframe);
            result[index++] = include ? entity : Strip(id, entity);
        }
        if (reported.Count > 2 * MaximumEntities)
            foreach (var id in reported.Keys.Where(id => !entities.ContainsKey(id)).ToArray()) { reported.Remove(id); stripped.Remove(id); }
        return result;
    }

    /// <summary>与上次上报的 health 不同（含首次）时登记并返回真；相同时在变化后的重复窗口内仍返回真。</summary>
    private bool Changed(ulong id, CombatEntityHealth health, double nowMs)
    {
        if (!reported.TryGetValue(id, out var last) || last.Health != health) { reported[id] = (health, nowMs); return true; }
        return nowMs - last.At < HealthRepeatMs;
    }

    private static bool SameExceptHealth(CombatEntityObservation left, CombatEntityObservation right) =>
        left.Key == right.Key && left.RawName == right.RawName && left.DisplayName == right.DisplayName && left.Role == right.Role &&
        left.OwnerKey == right.OwnerKey && left.OwnerConfirmed == right.OwnerConfirmed && left.InScope == right.InScope && left.Quality == right.Quality &&
        left.BreakGauge == right.BreakGauge && left.IsBoss == right.IsBoss && left.Dead == right.Dead && left.ActorType == right.ActorType &&
        left.SheetId == right.SheetId;

    private CombatEntityObservation Strip(ulong id, CombatEntityObservation entity)
    {
        if (stripped.TryGetValue(id, out var cached))
        {
            if (ReferenceEquals(cached.Full, entity)) return cached.Stripped;
            // 只有 HP 变化时沿用上一个剥离副本：同一对象的编码已缓存，不为每次 HP 更新重新编码一份不带 HP 的实体。
            if (SameExceptHealth(cached.Full, entity)) { stripped[id] = (entity, cached.Stripped); return cached.Stripped; }
        }
        var copy = entity with { Health = null };
        stripped[id] = (entity, copy);
        return copy;
    }

    /// <summary>补足后尚未随本机身份发布过的延续记录；其间开始新场景时重新补回。</summary>
    private IReadOnlyList<CombatCarriedEntity>? reseed;

    /// <summary>实体延续记录的修订：自动体登记、死亡、离场、最大值更新或淘汰时递增。</summary>
    public ulong CarryRevision => carryRevision;
    /// <summary>首领 HP 解密的累计结果（成功、失败），供离线回放统计。</summary>
    public (ulong Decrypted, ulong Failed) HealthDecryption => (decrypted, failedDecryption);
    private ulong decrypted, failedDecryption;

    private static string? ActorTypeName(int type) => type switch
    {
        1 => "npc", 2 => "npc-companion", 3 => "monster", 4 => "pet", 5 => "summoned-automaton", 6 => "gimmick",
        7 => "ai-companion", 8 => "memorial-saga-minion", _ => null
    };

    /// <summary>
    /// 按自动体状态补全实体快照的 HP、首领、死亡、类型与表 ID；v4 以前的兴趣不输出这些字段（旧宿主按未知成员拒绝）。
    /// 布尔字段只在为真时输出：实体快照随每帧发送，省去常见的假值。
    /// </summary>
    private CombatEntityObservation Describe(ulong id, CombatEntityObservation entity) => Owned(id, DescribeActor(id, entity));

    /// <summary>出现帧登记了所有者的自动体：角色为 Owned，所有者为同一代次的玩家实体，标为已确认；不随 v4 血量兴趣变化。</summary>
    private CombatEntityObservation Owned(ulong id, CombatEntityObservation entity)
    {
        if (!owners.TryGetValue(id, out var owner) || entity.Role.KnownValue is not (CombatEntityRole.Unknown or CombatEntityRole.Owned)) return entity;
        var key = Key(owner);
        return entity.Role.KnownValue == CombatEntityRole.Owned && entity.OwnerKey == key && entity.OwnerConfirmed
            ? entity : entity with { Role = CombatEntityRole.Owned, OwnerKey = key, OwnerConfirmed = true };
    }

    private CombatEntityObservation DescribeActor(ulong id, CombatEntityObservation entity)
    {
        if (!interest.Health || !actors.TryGetValue(id, out var actor))
            return entity.Health is null && entity.IsBoss is null && entity.Dead is null && entity.ActorType is null && entity.SheetId is null
                ? entity : entity with { Health = null, IsBoss = null, Dead = null, ActorType = null, SheetId = null };
        UInt64String? maximum = actor.Max is { } max ? new((ulong)max) : null;
        var health = actor.Current is { } current
            ? new CombatEntityHealth(new((ulong)current), maximum, actor.At, "read", actor.Failure ?? (maximum is null ? HealthMaximumUnknown : null),
                actor.Encrypted ? true : null, actor.Updated ? true : null, actor.Stale ? true : null)
            : new CombatEntityHealth(null, maximum, actor.At, "unread", actor.Failure ?? HealthUnread, actor.Encrypted ? true : null, actor.Updated ? true : null,
                actor.Stale ? true : null);
        return entity with
        {
            Health = health, IsBoss = actor.Boss ? true : null, Dead = actor.Dead ? true : null,
            ActorType = ActorTypeName(actor.Type), SheetId = actor.Sheet != 0 ? new(actor.Sheet) : null
        };
    }

    private void Refresh(ulong id)
    {
        if (entities.TryGetValue(id, out var entity)) entities[id] = Describe(id, entity);
    }

    /// <summary>出现帧：登记类型、表 ID、首领标志、最大 HP 与解码信息，并解出 HP 初值（明文 8 字节或加密形态）。</summary>
    private void RecordActor(TwAutomatonIdentity automaton)
    {
        if (!interest.Health) return;
        var actor = new ActorState
        {
            Type = automaton.ActorType, Sheet = automaton.SheetId, Boss = automaton.IsBoss, Max = automaton.HealthMax,
            Encrypted = automaton.HealthEncrypted, KeyId = automaton.HealthKeyId, Iv = automaton.HealthIv, At = clockUtc
        };
        long value;
        if (automaton.HealthEncrypted)
        {
            if (actor.Iv is null) actor.Failure = HealthUnread;
            else if (cipher.TryHealth(actor.KeyId, actor.Iv, automaton.HealthPayload, out value) && Valid(actor, value)) { actor.Current = value; decrypted++; }
            else { actor.Failure = HealthUndecryptable; failedDecryption++; }
        }
        else if (automaton.HealthPayload.Length == 8 && Valid(actor, value = BinaryPrimitives.ReadInt64LittleEndian(automaton.HealthPayload)))
            actor.Current = value;
        actors[automaton.EntityId] = actor;
        departed.Remove(automaton.EntityId);
        carryRevision++;
        Refresh(automaton.EntityId);
    }

    private static bool Valid(ActorState actor, long value) => value >= 0 && (actor.Max is not { } max || value <= max);

    /// <summary>
    /// SetHealth 170205–170210：明文直接取值，加密形态以出现帧的密钥来源与 IV 解出。没有该实体的出现帧与解码信息、填充非法、
    /// 明文不是 8 字节、值为负或超过最大值时不更新也不清零，只记原因。没有出现帧的实体只在本机命中过时记录明文 HP。
    /// </summary>
    private void OnEntityHealth(TwHealthUpdate update, ReadOnlySpan<byte> body)
    {
        if (!interest.Health || update.Entity == localEntity) return;
        if (!actors.TryGetValue(update.Entity, out var actor))
        {
            if (!localTargets.Contains(update.Entity) || !entities.TryGetValue(update.Entity, out var entity) ||
                entity.Role.KnownValue is CombatEntityRole.Player or CombatEntityRole.LocalPlayer) return;
            actors[update.Entity] = actor = new ActorState { At = clockUtc };
        }
        // 收到过 HP 更新（不论能否解出）：区分“服务器不下发血量”的锁血实体与缺解码信息的实体。
        actor.Updated = true;
        long value;
        if (update.Encrypted)
        {
            actor.Encrypted = true;
            if (actor.Iv is null) actor.Failure = HealthUnread;
            else if (cipher.TryHealth(actor.KeyId, actor.Iv, TwCombatWire.HealthValue(body), out value) && Valid(actor, value)) { Set(actor, value); decrypted++; }
            else { actor.Failure = HealthUndecryptable; failedDecryption++; }
        }
        else if (update.PlainValue is { } plain && Valid(actor, plain)) Set(actor, plain);
        else actor.Failure = HealthUndecryptable;
        healthDirty = true; healthUrgent |= HealthCandidate(update.Entity);
        Refresh(update.Entity);
    }

    private void Set(ActorState actor, long value)
    {
        actor.Current = value; actor.At = clockUtc; actor.Updated = true; actor.Failure = null; actor.Stale = false;
    }

    /// <summary>UpdateHealthMax 170151：以本连接的广播密钥解出实体与新最大值；没有广播密钥或解不出时忽略。</summary>
    private void OnHealthMax(ReadOnlySpan<byte> body, byte encoding)
    {
        TwCombatWire.RequireHealthMaxCipher(body, encoding);
        if (!interest.Health || broadcast is not { } key || !cipher.TryHealthMax(key.Key, key.Iv, body, out var entity, out var value) || value <= 0 ||
            !actors.TryGetValue(entity, out var actor)) return;
        actor.Max = value; carryRevision++; healthDirty = true; healthUrgent |= HealthCandidate(entity);
        Refresh(entity);
    }

    /// <summary>SetDead 170197：记录死亡或复活；死亡实体不再进入延续记录。</summary>
    private void OnDeath(TwDeathUpdate update)
    {
        if (!interest.Health || update.Entity == localEntity) return;
        if (!actors.TryGetValue(update.Entity, out var actor))
        {
            if (!entities.TryGetValue(update.Entity, out var entity) || entity.Role.KnownValue is CombatEntityRole.Player or CombatEntityRole.LocalPlayer) return;
            actors[update.Entity] = actor = new ActorState();
        }
        if (actor.Dead == update.Dead) return;
        actor.Dead = update.Dead; carryRevision++;
        Refresh(update.Entity); healthDirty = true; healthUrgent |= HealthCandidate(update.Entity);
    }

    /// <summary>对象离场 20272：实体以 inScope=false 发布一次，保留 <see cref="DepartedRetentionMs"/> 后移除；身份备忘与延续记录作废。</summary>
    private void OnDisappearing(ulong entity)
    {
        if (!interest.Health || entity == localEntity) return;
        memo.Remove(entity);
        if (!entities.TryGetValue(entity, out var known)) { actors.Remove(entity); return; }
        entities[entity] = known with { InScope = false };
        departed[entity] = inputMs; carryRevision++; healthDirty = true; healthUrgent |= HealthCandidate(entity);
        nextPurgeMs = Math.Min(nextPurgeMs, inputMs + DepartedRetentionMs);
    }

    /// <summary>移除离场满保留期的实体及其效果实例与自动体状态。</summary>
    private void PurgeDeparted(double nowMs)
    {
        var now = Math.Max(nowMs, inputMs);
        if (departed.Count == 0 || now < nextPurgeMs) return;
        nextPurgeMs = double.PositiveInfinity;
        foreach (var (id, at) in departed.ToArray())
        {
            if (now - at < DepartedRetentionMs) { nextPurgeMs = Math.Min(nextPurgeMs, at + DepartedRetentionMs); continue; }
            departed.Remove(id);
            if (entities.TryGetValue(id, out var entity) && entity.InScope) continue;
            entities.Remove(id); lastSeen.Remove(id); actors.Remove(id); owners.Remove(id);
            foreach (var key in activeBuffs.Keys.Where(key => key.Owner == id).ToArray()) activeBuffs.Remove(key);
            healthDirty = true;
        }
    }
    /// <summary>最早一个离场实体到期移除的时点；离场、死亡与主目标变化随下一次推进或下一帧发布（当前目标候选距上次发布满 16 ms，其余至多 200 ms）。</summary>
    private double nextPurgeMs = double.PositiveInfinity;

    private void OnMainTarget(TwMainTargetUpdate update)
    {
        if (!interest.Health || localEntity is not { } local || update.Owner != local) return;
        ulong? target = update.Target == -1 ? null : (ulong)update.Target;
        if (target is { } id) Observe(id);
        mainTarget = (target, inputMs, clockUtc);
        healthDirty = true; healthUrgent = true;
    }

    private void OnBroadcastKey(ReadOnlySpan<byte> body, byte encoding)
    {
        var value = TwCombatWire.BroadcastKey(body, encoding);
        if (interest.Health) broadcast = value;
    }

    /// <summary>本机主目标的帧字段；v4 以前的兴趣或尚未收到时为空。</summary>
    private CombatMainTarget? MainTargetField() => interest.Health && mainTarget is { } value
        ? new(value.Target is { } target ? Key(target) : null, value.At, value.Mono) : null;

    /// <summary>缺口后尚未重新取得的 HP 标为过期。</summary>
    private void MarkHealthStale()
    {
        foreach (var (id, actor) in actors)
        {
            if (actor.Stale || actor.Current is null) continue;
            actor.Stale = true; Refresh(id);
        }
    }

    private void Remember(ulong id, CombatEntityObservation entity)
    {
        if (!interest.Health) return;
        var actor = actors.GetValueOrDefault(id);
        if (actor is { Dead: true } || departed.ContainsKey(id)) return;
        if (!memo.ContainsKey(id)) memoOrder.Enqueue(id);
        memo[id] = (entity, actor?.Copy());
        while (memo.Count > MaximumMemo && memoOrder.TryDequeue(out var oldest)) memo.Remove(oldest);
    }

    /// <summary>被淘汰的实体再次以未知角色出现时恢复其记录；返回是否已恢复。</summary>
    private bool Restore(ulong id)
    {
        if (!memo.Remove(id, out var saved)) return false;
        entities[id] = saved.Entity with { Key = Key(id) };
        if (saved.Actor is { } actor) actors[id] = actor;
        return true;
    }

    /// <summary>
    /// 本连接的实体延续记录：存活、在视野内的自动体（首领优先，其次有解码信息的怪物，至多 <paramref name="maximum"/> 条）。
    /// 解码信息只用于 Worker 与宿主之间的私有通道。
    /// </summary>
    public IReadOnlyList<CombatCarriedEntity> ExportCarry(int maximum = MaximumCarriedEntities)
    {
        if (!interest.Health) return [];
        return actors.Where(pair => !pair.Value.Dead && pair.Value.Type is > 0 and <= 8 && !departed.ContainsKey(pair.Key) &&
                entities.TryGetValue(pair.Key, out var entity) && entity.InScope)
            .OrderByDescending(pair => pair.Value.Boss).ThenByDescending(pair => pair.Value.Iv is not null && pair.Value.Type == 3)
            .ThenByDescending(pair => lastSeen.GetValueOrDefault(pair.Key))
            .Take(maximum)
            .Select(pair => new CombatCarriedEntity(new(pair.Key), pair.Value.Type, pair.Value.Sheet != 0 ? new(pair.Value.Sheet) : null,
                pair.Value.Max is { } max ? new((ulong)max) : null, pair.Value.Boss, pair.Value.Iv is null ? null : new(pair.Value.KeyId),
                pair.Value.Iv is { } iv ? Convert.ToBase64String(iv) : null, pair.Value.Encrypted ? true : null, pair.Value.Updated ? true : null))
            .ToArray();
    }

    /// <summary>
    /// 以同一连接先前确认的实体延续记录补足实体表：类型、表 ID、最大 HP、首领标志与解码信息；当前值等待下一次 HP 更新。
    /// 已登记的实体不覆盖，字段不合规的条目忽略。返回补足的条数。
    /// </summary>
    public int SeedEntities(IEnumerable<CombatCarriedEntity> carried, double monotonicMs, DateTimeOffset utc)
    {
        RequireClock(monotonicMs, utc);
        if (!interest.Health) return 0;
        inputMs = Math.Max(inputMs, monotonicMs); clockUtc = utc;
        var count = 0;
        Span<byte> code = stackalloc byte[4];
        foreach (var value in carried.Take(MaximumEntities))
        {
            var id = value.Id.Value;
            if (id == 0 || id == localEntity || entities.ContainsKey(id) || value.ActorType is < 1 or > 8 ||
                value.HealthMaximum is { Value: > long.MaxValue } || value.SheetId is { Value: > uint.MaxValue }) continue;
            byte[]? iv = null;
            if (value.Iv is { } text)
            {
                var buffer = new byte[16];
                if (!Convert.TryFromBase64String(text, buffer, out var length) || length != 16 || value.KeyId is null) continue;
                iv = buffer;
            }
            var sheet = (uint)(value.SheetId?.Value ?? 0);
            if (value.ActorType == 3)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(code, sheet);
                var raw = Convert.ToHexString(code);
                Observe(id, CombatEntityRole.Monster, raw, monsterNames.GetValueOrDefault(raw) ?? raw);
            }
            else Observe(id, replaceRole: true);
            if (!entities.ContainsKey(id)) continue;
            actors[id] = new ActorState
            {
                Type = value.ActorType, Sheet = sheet, Boss = value.Boss, Max = value.HealthMaximum is { } max && max.Value > 0 ? (long)max.Value : null,
                Encrypted = value.Encrypted == true, Updated = value.Updated == true, KeyId = value.KeyId?.Value ?? 0, Iv = iv
            };
            Refresh(id); count++;
        }
        if (count > 0) { carryRevision++; MarkSnapshot(); }
        if (localEntity is null) reseed = carried as IReadOnlyList<CombatCarriedEntity> ?? carried.ToArray();
        return count;
    }

    private void ClearActors(bool keepBroadcast)
    {
        actors.Clear(); memo.Clear(); memoOrder.Clear(); departed.Clear(); nextPurgeMs = double.PositiveInfinity; localTargets.Clear(); mainTarget = null; healthDirty = false; healthUrgent = false;
        reported.Clear(); stripped.Clear(); keyframeMs = double.NegativeInfinity;
        if (!keepBroadcast) broadcast = null;
        carryRevision++;
    }

    private void NoteLocalTarget(ulong target)
    {
        if (localTargets.Count < MaximumLocalTargets) localTargets.Add(target);
    }

}
