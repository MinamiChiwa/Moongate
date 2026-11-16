using System.Globalization;
using MabiTool.Contracts;
using Op = MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol;

namespace MabiTool.Providers.TwObservation.Combat;

/// <param name="CombatState">解析本机战斗状态 170196（仅 v3 兴趣）。</param>
public sealed record CombatProtocolInterest(bool Damage, bool SelfEffects, bool MonsterEffects, bool Healing = false, bool CombatState = false);
public sealed record CombatProtocolUsage(int Entities, int AppearanceEntries, int AppearanceBytes,
    int PendingRecords, int Skills, int EffectInstances);

/// <summary>
/// 台服 tw-20261110 战斗协议的连接级观察器：输入已重组、已展开的帧（线上协议号），输出规范化 <see cref="CombatFrame"/>。
/// 分三层：<see cref="TwCombatWire"/> 读取定长字段；本类维护身份、实体、效果实例与治疗配对状态；
/// 待发记录按流内顺序排队，伤害等待技能近邻、治疗等待 HP 配对，到期或越过字节窗口后成批发布。
/// 捕获、业务统计与来源授权由调用方维护。
/// </summary>
public sealed partial class TwCombatObserver
{
    public const int MaximumEntities = 128;
    public const int MaximumEffects = 2048;
    /// <summary>实体满额时可淘汰的最短未出现时长：长时间停留同一地图时新实体仍可进入。</summary>
    public const double EntityIdleMs = 60_000;
    private const int MaximumAppearances = 64, MaximumAppearanceBytes = 4 * 1024 * 1024;
    private const int MaximumSkills = 128, SkillRetentionBytes = 1024;
    private const int MaximumPending = 512, MaximumGaps = 32, MaximumBatchRecords = 128, MaximumHealthChanges = 8;
    private const double KeepAliveMs = 100;

    private readonly string session;
    private readonly ObservationSource source;
    private readonly Action<CombatFrame> publish;
    private readonly Func<ulong>? generations;
    private readonly IReadOnlyDictionary<string, string> monsterNames;

    // 身份与实体
    private ulong? characterId;
    private ulong? localEntity;
    private readonly Dictionary<ulong, CombatEntityObservation> entities = [];
    private readonly Dictionary<ulong, double> lastSeen = [];
    /// <summary>个人数据到达前出现的角色，按到达顺序暂存解码结果与正文字节数（字节数只用于容量）。</summary>
    private readonly Dictionary<ulong, (TwCharacterIdentity Identity, int Bytes)> unboundCharacters = [];
    private int unboundBytes;
    private bool snapshotDirty;
    private double snapshotMs;

    // 关联状态
    private readonly List<SkillCast> recentSkills = [];
    private readonly Dictionary<(ulong Owner, ulong Instance), (uint Buff, ulong Caster)> activeBuffs = [];
    private readonly List<HealthStep> healthSteps = [];
    private long? localHealth;

    // 发布
    private readonly Queue<PendingRecord> queue = [];
    private readonly List<CombatGapInput> gaps = [];
    private ulong generation, sequence, lastStreamOffset;
    private double inputMs, publishedMs;
    private DateTimeOffset clockUtc;
    private CombatProtocolInterest interest = new(true, true, true);

    public TwCombatObserver(string gameSessionId, ObservationSource observationSource, Action<CombatFrame> onFrame,
        Func<ulong>? nextGeneration = null, IReadOnlyDictionary<string, string>? names = null)
    {
        if (string.IsNullOrWhiteSpace(gameSessionId) || gameSessionId.Length > 128) throw new ArgumentException("游戏会话无效");
        session = gameSessionId; source = observationSource; publish = onFrame; generations = nextGeneration;
        monsterNames = names is null ? new Dictionary<string, string>() : new Dictionary<string, string>(names);
        generation = AdvanceGeneration();
    }

    public UInt64String Generation => new(generation);
    public CombatEntityObservation? LocalEntity => localEntity is { } id ? entities.GetValueOrDefault(id) : null;
    /// <summary>本机实体已确认时，本连接个人数据给出的本机角色 ID；否则为空。</summary>
    public ulong? LocalCharacterId => localEntity is null ? null : characterId;
    public CombatProtocolUsage Usage => new(entities.Count, unboundCharacters.Count, unboundBytes, queue.Count, recentSkills.Count, activeBuffs.Count);
    /// <summary>因帧内容无法解析而记为 combat.protocolInvalid 缺口的帧数，供 Worker 诊断计数。</summary>
    public ulong ProtocolRejections { get; private set; }
    public void Configure(CombatProtocolInterest value) => interest = value;

    public void Accept(int opcode, ReadOnlySpan<byte> body, ulong streamOffset, int wireLength,
        double monotonicMs, DateTimeOffset utc, byte encoding = 0)
    {
        if (!double.IsFinite(monotonicMs) || monotonicMs < 0 || !IsUtc(utc) || wireLength < 9)
            throw new ArgumentException("战斗帧时点或长度无效");
        // 队首已超过关联等待期时先发布，新帧的实体快照不会把旧事件推出批内时间窗口。
        if (queue.TryPeek(out var head) && monotonicMs - head.Mono > Op.CorrelationWaitMs) Publish(monotonicMs);
        inputMs = Math.Max(inputMs, monotonicMs); clockUtc = utc; lastStreamOffset = streamOffset;
        try { Route(opcode, body, streamOffset, wireLength, encoding); }
        catch (InvalidDataException)
        {
            ProtocolRejections++; Publish(inputMs, force: true); AddGap("combat.protocolInvalid", 1);
        }
        Publish(inputMs);
    }

    private void Route(int opcode, ReadOnlySpan<byte> body, ulong offset, int wireLength, byte encoding)
    {
        var effects = interest.SelfEffects || interest.MonsterEffects;
        switch (opcode)
        {
            case Op.PersonalData: OnPersonalData(body, encoding); break;
            case Op.AppearingCharacter: OnCharacterAppeared(body, encoding); break;
            case Op.AppearingAutomaton: OnAutomatonAppeared(body, encoding); break;
            case Op.HitPresentation when interest.Damage: OnHitPresentation(TwCombatWire.HitPresentation(body, encoding), offset); break;
            case Op.DamageFloater when interest.Damage: OnDamage(TwCombatWire.DamageFloater(body, encoding), offset, wireLength); break;
            case Op.SetBreakPoint when interest.Damage: OnBreakPoint(TwCombatWire.BreakPoint(body, encoding)); break;
            case Op.BuffAdd when effects: OnBuffUpdate(TwCombatWire.BuffUpdate(body, encoding), CombatEffectChangeKind.Apply); break;
            case Op.BuffRefresh when effects: OnBuffUpdate(TwCombatWire.BuffUpdate(body, encoding), CombatEffectChangeKind.Refresh); break;
            case Op.BuffExpire when effects: OnBuffExpire(TwCombatWire.BuffExpire(body, encoding)); break;
            case Op.HealReceived when interest.Healing: OnHealReceived(TwCombatWire.HealReceipt(body, encoding), offset, wireLength); break;
            case Op.HealCasted when interest.Healing: OnHealCast(TwCombatWire.HealCast(body, encoding), offset, wireLength); break;
            case Op.SetHealth when interest.Healing: OnHealth(TwCombatWire.Health(body, encoding), offset, wireLength); break;
            case Op.SetCombatState when interest.CombatState: OnCombatState(TwCombatWire.CombatState(body, encoding), offset); break;
            // UpdateBreakPointMax 170117 的负载在留存样本中为加密形态，读不出实体与数值；帧照常推进时钟，内容不使用。
        }
    }

    public void Tick(double nowMs, DateTimeOffset utc)
    {
        RequireClock(nowMs, utc);
        clockUtc = utc; Publish(Math.Max(nowMs, inputMs));
    }

    public void Reject(string reason, double nowMs, DateTimeOffset utc)
    {
        inputMs = Math.Max(inputMs, nowMs); clockUtc = utc;
        Publish(inputMs, force: true); AddGap(reason, 1); Publish(inputMs);
    }

    /// <summary>流重置：先发布全部待发记录与缺口，再换代次并清空连接内的全部解析状态。</summary>
    public void Reset(string reason, double nowMs, DateTimeOffset utc)
    {
        inputMs = Math.Max(inputMs, nowMs); clockUtc = utc;
        Publish(inputMs, force: true); AddGap(reason, 0); Publish(inputMs, force: true);
        StartScene(null);
    }

    /// <summary>
    /// 捕获积压段的首个包改用处理时点前调用：记录 combat.captureLag 缺口，区间为该包的抓包时点至处理时点，
    /// 不计丢弃；解析状态与身份保持不变。
    /// </summary>
    public void CaptureLag(double receivedMs, double nowMs, DateTimeOffset utc)
    {
        RequireClock(nowMs, utc);
        inputMs = Math.Max(inputMs, nowMs); clockUtc = utc;
        var from = double.IsFinite(receivedMs) ? Math.Clamp(receivedMs, 0, inputMs) : inputMs;
        if (gaps.Count >= MaximumGaps) { gaps[^1] = gaps[^1] with { ToMs = inputMs }; return; }
        gaps.Add(new("combat.captureLag", from, inputMs, new(0)));
    }

    public void Finish(double nowMs, DateTimeOffset utc)
    {
        clockUtc = utc; Publish(nowMs, force: true);
    }

    /// <summary>
    /// 以共享捕获已确认的本机身份（个人数据角色 ID 与本机出现帧的实体 ID）补足起点，用于战斗兴趣晚于登录或切图建立的情形。
    /// 已有本机实体或角色 ID 不一致时不改变状态；补足后发布实体快照。
    /// </summary>
    public bool SeedLocal(ulong seededCharacter, ulong seededEntity, double monotonicMs, DateTimeOffset utc)
    {
        if (seededCharacter == 0 || seededEntity == 0 || localEntity is not null ||
            characterId is { } known && known != seededCharacter) return false;
        RequireClock(monotonicMs, utc);
        inputMs = Math.Max(inputMs, monotonicMs); clockUtc = utc;
        if (characterId is null)
        {
            var waiting = TakeUnbound();
            StartScene(seededCharacter);
            localEntity = seededEntity; Observe(seededEntity, CombatEntityRole.LocalPlayer);
            foreach (var character in waiting) Bind(character);
        }
        else { localEntity = seededEntity; Observe(seededEntity, CombatEntityRole.LocalPlayer, replaceRole: true); }
        Publish(inputMs);
        return true;
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static void RequireClock(double ms, DateTimeOffset utc)
    {
        if (!double.IsFinite(ms) || ms < 0 || !IsUtc(utc)) throw new ArgumentException("战斗时钟无效");
    }
    private ulong AdvanceGeneration() => generations?.Invoke() ?? checked(generation + 1);
    private EntityKey Key(ulong id) => new(session, new(generation), new(id), null);

    /// <summary>开始新场景：换代次、序号归零，清空本机绑定与连接内的全部实体、关联和 HP 状态；角色 ID 取个人数据或共享身份，流重置时为空。</summary>
    private void StartScene(ulong? character)
    {
        generation = AdvanceGeneration(); sequence = 0; localEntity = null; characterId = character;
        entities.Clear(); lastSeen.Clear(); unboundCharacters.Clear(); unboundBytes = 0;
        recentSkills.Clear(); activeBuffs.Clear(); localHealth = null; healthSteps.Clear();
    }

    /// <summary>取出个人数据到达前暂存的角色（保持到达顺序），供新场景归属。</summary>
    private TwCharacterIdentity[] TakeUnbound() => unboundCharacters.Values.Select(entry => entry.Identity).ToArray();

    // ---- 身份与实体 ----

    /// <summary>登录或切图的个人数据：已有身份时以 combat.sceneChanged 结束旧场景（未发布记录计为丢弃），再换代次；暂存的出现帧随后归属。</summary>
    private void OnPersonalData(ReadOnlySpan<byte> body, byte encoding)
    {
        TwCombatWire.RequireCompressedPrefix(body, encoding);
        var personal = TwCharacterAppearance.DecodePersonalId(body);
        var waiting = characterId is null ? TakeUnbound() : [];
        if (characterId is not null || localEntity is not null)
        {
            AddGap("combat.sceneChanged", checked((ulong)queue.Count)); queue.Clear(); Publish(inputMs, force: true);
        }
        StartScene(personal);
        foreach (var character in waiting) Bind(character);
    }

    private void OnCharacterAppeared(ReadOnlySpan<byte> body, byte encoding)
    {
        TwCombatWire.RequireCompressedPrefix(body, encoding);
        var character = TwCharacterAppearance.Decode(body);
        if (characterId is not null) { Bind(character); return; }
        if (unboundCharacters.Remove(character.EntityId, out var replaced)) unboundBytes -= replaced.Bytes;
        while (unboundCharacters.Count >= MaximumAppearances || unboundBytes + body.Length > MaximumAppearanceBytes)
        {
            if (unboundCharacters.Count == 0) { AddGap("combat.identityCapacity", 1); return; }
            var oldest = unboundCharacters.First();
            unboundCharacters.Remove(oldest.Key); unboundBytes -= oldest.Value.Bytes;
            AddGap("combat.identityCapacity", 1);
        }
        unboundCharacters[character.EntityId] = (character, body.Length); unboundBytes += body.Length;
    }

    /// <summary>角色 ID 与个人数据一致的第一个出现实体为本机，同一场景内不再改绑；其余角色为其他玩家。</summary>
    private void Bind(TwCharacterIdentity character)
    {
        if (characterId is not { } own) return;
        if (character.CharacterId != own) { Observe(character.EntityId, CombatEntityRole.Player); return; }
        if (localEntity is { } bound && bound != character.EntityId) return;
        localEntity = character.EntityId; Observe(character.EntityId, CombatEntityRole.LocalPlayer);
    }

    /// <summary>只有 Type=Monster 的自动体登记为怪物并带出现帧的破防值；其他类型撤销先前的怪物角色。</summary>
    private void OnAutomatonAppeared(ReadOnlySpan<byte> body, byte encoding)
    {
        TwCombatWire.RequireCompressed(encoding);
        var automaton = TwAutomatonAppearance.Decode(body);
        if (!automaton.IsMonster) { Observe(automaton.EntityId, replaceRole: true); return; }
        var code = automaton.MonsterTypeCode!;
        Observe(automaton.EntityId, CombatEntityRole.Monster, code, monsterNames.GetValueOrDefault(code) ?? code);
        if (!entities.TryGetValue(automaton.EntityId, out var monster)) return;
        UInt64String? maximum = automaton.BreakPointMax is { } value ? new((ulong)value) : null;
        var reason = automaton.BreakPoint is null || automaton.BreakPointMax is null ? "combat.breakGaugeUnknown" : null;
        entities[automaton.EntityId] = monster with
            { BreakGauge = new(automaton.BreakPoint, maximum, clockUtc, ObservationQuality.Confirmed, Reason: reason) };
    }

    /// <summary>
    /// 当前破防值只更新已登记的怪物。上限沿用同一实体出现帧的值（2026-11-10 两份留存样本中当前值从未超过出现帧上限）；
    /// 缺口只使当前值过期，同一实体重新取得当前值时沿用上限。
    /// </summary>
    private void OnBreakPoint(TwBreakPointUpdate update)
    {
        if (!entities.TryGetValue(update.Entity, out var entity) || entity.Role.KnownValue != CombatEntityRole.Monster) return;
        var maximum = entity.BreakGauge?.Maximum;
        entities[update.Entity] = entity with { BreakGauge = new(update.Current, maximum, clockUtc, ObservationQuality.Confirmed,
            Reason: maximum is null ? "combat.breakMaximumUnknown" : null) };
        MarkSnapshot();
    }

    /// <summary>
    /// 记录实体出现。已知实体只刷新出现时点，除非带来明确角色或要求覆盖角色；新实体满额时先淘汰空闲实体，
    /// 仍无空位记 combat.entityCapacity。登记或改写都使下一批携带实体快照。
    /// </summary>
    private void Observe(ulong id, CombatEntityRole role = CombatEntityRole.Unknown, string rawName = "", string? displayName = null,
        bool replaceRole = false)
    {
        if (id == 0) return;
        var known = entities.ContainsKey(id);
        if (known)
        {
            lastSeen[id] = inputMs;
            if (role == CombatEntityRole.Unknown && !replaceRole) return;
        }
        else if (entities.Count >= MaximumEntities && !EvictIdle()) { AddGap("combat.entityCapacity", 1); return; }
        lastSeen[id] = inputMs;
        entities[id] = new(Key(id), rawName, displayName ?? (rawName.Length > 0 ? rawName : id.ToString(CultureInfo.InvariantCulture)),
            role, null, false, true, ObservationQuality.Confirmed);
        MarkSnapshot();
    }

    private void MarkSnapshot() { snapshotDirty = true; snapshotMs = inputMs; }

    /// <summary>淘汰最久未出现且超过 <see cref="EntityIdleMs"/> 的非本机实体及其效果实例；没有可淘汰实体时返回假。</summary>
    private bool EvictIdle()
    {
        ulong? victim = null; var earliest = double.PositiveInfinity;
        foreach (var (id, seen) in lastSeen)
        {
            if (id == localEntity || id == characterId || inputMs - seen < EntityIdleMs || seen >= earliest) continue;
            victim = id; earliest = seen;
        }
        if (victim is not { } evicted) return false;
        entities.Remove(evicted); lastSeen.Remove(evicted);
        foreach (var key in activeBuffs.Keys.Where(key => key.Owner == evicted).ToArray()) activeBuffs.Remove(key);
        snapshotDirty = true;
        return true;
    }
}
