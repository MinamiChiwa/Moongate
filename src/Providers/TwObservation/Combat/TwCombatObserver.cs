using System.Globalization;
using MabiTool.Contracts;
using Op = MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol;

namespace MabiTool.Providers.TwObservation.Combat;

/// <param name="CombatState">解析本机战斗状态 170196（v3 起）。</param>
/// <param name="Health">解析实体 HP、死亡、离场与本机主目标（仅 v4 兴趣），独立于治疗开关。</param>
public sealed record CombatProtocolInterest(bool Damage, bool SelfEffects, bool MonsterEffects, bool Healing = false, bool CombatState = false,
    bool Health = false);
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
    /// <summary>出现帧声明的所有者：自动体实体 ID 到所有者玩家实体 ID；随实体移除或新场景清空。</summary>
    private readonly Dictionary<ulong, ulong> owners = [];
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
    private bool localCarried, carriedConfirmed;
    private (ulong Entity, ulong End)? lastHealth;
    private ulong? healReceiptEnd;
    /// <summary>沿用的本机起点曾被出现帧证明属于其他角色；供共用身份记录作废沿用值。</summary>
    public bool CarriedContradicted { get; private set; }

    /// <summary>
    /// 捕获中途接入另一条连接时只沿用角色 ID：实体 ID 由各地图实例按计数器分配、不同连接间会复用，不沿用。
    /// 本机实体待本机自疗或本机出现帧确认（见 <see cref="ConfirmBySelfHeal"/>）；确认前不归属任何伤害。
    /// </summary>
    public bool SeedCharacter(ulong character, double monotonicMs, DateTimeOffset utc)
    {
        if (character == 0 || characterId is not null || localEntity is not null) return false;
        RequireClock(monotonicMs, utc);
        inputMs = Math.Max(inputMs, monotonicMs); clockUtc = utc;
        var waiting = TakeUnbound();
        StartScene(character); localCarried = true;
        foreach (var appeared in waiting) Bind(appeared);
        return true;
    }

    /// <summary>
    /// 本机自疗确认沿用起点：施放 20644 只发给施放者、受疗 20645 只发给受疗者，两者在 400 字节内同旗标、同数值时是本机治疗自己，
    /// 施放目标即本机实体（实采 299 次自疗的施放目标全部为本机）。受疗与 SetHealth 170205 首尾相接只作旁证：组队时紧邻的 HP 可以是队友的
    /// （combat-evidence-20261110-confirmed-02 中两名角色各 5 次），紧邻 HP 属于其他实体时不确认。
    /// 沿用起点尚未由本连接观测确认时，还没有本机实体即采用施放目标，实体不同即改绑到它。
    /// </summary>
    private void ConfirmBySelfHeal(ulong target, HealReceipt? receipt)
    {
        if (!localCarried || carriedConfirmed || target == 0 || characterId is null || receipt?.HealthEntity is { } health && health != target) return;
        if (localEntity is { } local && local != target) Observe(local, CombatEntityRole.Unknown, replaceRole: true);
        localEntity = target; Observe(target, CombatEntityRole.LocalPlayer, replaceRole: true);
        carriedConfirmed = true; localCarried = false;
    }
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
        // 只改实体状态的帧（HP、死亡、离场、主目标、密钥广播）不单独触发发布：变化随下一次推进或下一帧发布
        // （当前目标候选距上次发布满 16 ms，其余至多 200 ms；读取循环按 NextDueMs 唤醒推进）。
        if (!StateOnly(opcode)) Publish(inputMs);
    }
    private static bool StateOnly(int opcode) => opcode is >= Op.SetHealth2 and <= Op.SetHealth6 or Op.UpdateHealthMax or Op.BroadcastKey or
        Op.SetDead or Op.Disappearing or Op.SetMainTarget;

    private void Route(int opcode, ReadOnlySpan<byte> body, ulong offset, int wireLength, byte encoding)
    {
        var effects = interest.SelfEffects || interest.MonsterEffects;
        switch (opcode)
        {
            case Op.PersonalData: OnPersonalData(body, encoding); break;
            case Op.AppearingCharacter: OnCharacterAppeared(body, encoding); break;
            case Op.AppearingAutomaton: OnAutomatonAppeared(body, encoding); break;
            case Op.HitPresentation when interest.Damage:
                OnHitPresentation(TwCombatWire.HitPresentation(body, encoding), offset); break;
            case Op.DamageFloater when interest.Damage:
                OnDamage(TwCombatWire.DamageFloater(body, encoding), offset, wireLength); break;
            case Op.SetBreakPoint when interest.Damage: OnBreakPoint(TwCombatWire.BreakPoint(body, encoding)); break;
            case Op.BuffAdd when effects: OnBuffUpdate(TwCombatWire.BuffUpdate(body, encoding), CombatEffectChangeKind.Apply); break;
            case Op.BuffRefresh when effects: OnBuffUpdate(TwCombatWire.BuffUpdate(body, encoding), CombatEffectChangeKind.Refresh); break;
            case Op.BuffExpire when effects: OnBuffExpire(TwCombatWire.BuffExpire(body, encoding)); break;
            case Op.HealReceived when interest.Healing:
                var receipt = TwCombatWire.HealReceipt(body, encoding);
                ulong? adjacent = lastHealth is { } previous && previous.End == offset ? previous.Entity : null;
                healReceiptEnd = adjacent is null ? offset + (ulong)wireLength : null;
                OnHealReceived(receipt, offset, wireLength, adjacent); break;
            case Op.HealCasted when interest.Healing: OnHealCast(TwCombatWire.HealCast(body, encoding), offset, wireLength); break;
            case Op.SetHealth when interest.Healing || interest.Health:
                var health = TwCombatWire.Health(body, encoding);
                // 治疗配对只看 170205 与受疗的首尾相接，规则不随实体 HP 解析改变。
                if (interest.Healing)
                {
                    if (healReceiptEnd == offset) NoteReceiptHealth(offset, health.Entity);
                    lastHealth = (health.Entity, offset + (ulong)wireLength); healReceiptEnd = null;
                    OnHealth(health, offset, wireLength);
                }
                OnEntityHealth(health, body); break;
            case >= Op.SetHealth2 and <= Op.SetHealth6 when interest.Health: OnEntityHealth(TwCombatWire.Health(body, encoding), body); break;
            case Op.UpdateHealthMax when interest.Health: OnHealthMax(body, encoding); break;
            case Op.BroadcastKey when interest.Health: OnBroadcastKey(body, encoding); break;
            case Op.SetDead when interest.Health: OnDeath(TwCombatWire.Death(body, encoding)); break;
            case Op.Disappearing when interest.Health: OnDisappearing(TwCombatWire.Disappearing(body, encoding)); break;
            case Op.SetMainTarget when interest.Health: OnMainTarget(TwCombatWire.MainTarget(body, encoding)); break;
            case Op.SetCombatState when interest.CombatState:
                OnCombatState(TwCombatWire.CombatState(body, encoding), offset); break;
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

    /// <summary>
    /// 流重置：先发布全部待发记录与缺口，再换代次并清空连接内的全部解析状态。
    /// <paramref name="keepEntities"/> 为真（同一连接内容矛盾的重置）时，自动体的类型、表 ID、最大 HP、首领标志与解码信息在新代次延续。
    /// </summary>
    public void Reset(string reason, double nowMs, DateTimeOffset utc, bool keepEntities = false)
    {
        inputMs = Math.Max(inputMs, nowMs); clockUtc = utc;
        Publish(inputMs, force: true); AddGap(reason, 0); Publish(inputMs, force: true);
        var carried = keepEntities ? ExportCarry(MaximumEntities) : [];
        reseed = null;
        StartScene(null);
        if (!keepEntities) broadcast = null;
        else if (carried.Count > 0) SeedEntities(carried, inputMs, clockUtc);
    }

    /// <summary>
    /// 同一连接内的数据缺口（缺口超时、驱动丢包、帧失步等）：先发布待发记录并记缺口，代次、本机与实体映射保留（同一连接内地图与角色不变），
    /// 缺口后的帧继续归属；依赖帧连续性的技能关联与本机 HP 前后值清空，破防值随缺口过期，效果连续性由宿主按缺口处理。
    /// 数据前后矛盾（重传内容不一致）与连接更换仍用 <see cref="Reset"/>。
    /// </summary>
    public void Resync(string reason, double nowMs, DateTimeOffset utc)
    {
        inputMs = Math.Max(inputMs, nowMs); clockUtc = utc;
        Publish(inputMs, force: true); AddGap(reason, 0); Publish(inputMs, force: true);
        recentSkills.Clear(); localHealth = null; healthSteps.Clear(); lastHealth = null; healReceiptEnd = null;
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
    /// <param name="carried">起点来自同一游戏进程先前捕获确认的身份（中途接入、本连接没有个人数据与本机出现帧）：
    /// 之后出现同一角色的其他实体时改绑到该实体；出现帧显示该实体属于其他角色时作废起点（<see cref="CarriedContradicted"/>）。</param>
    public bool SeedLocal(ulong seededCharacter, ulong seededEntity, double monotonicMs, DateTimeOffset utc, bool carried = false)
    {
        if (seededCharacter == 0 || seededEntity == 0 || localEntity is not null ||
            characterId is { } known && known != seededCharacter) return false;
        RequireClock(monotonicMs, utc);
        inputMs = Math.Max(inputMs, monotonicMs); clockUtc = utc;
        if (characterId is null)
        {
            var waiting = TakeUnbound();
            StartScene(seededCharacter);
            localCarried = carried;
            localEntity = seededEntity; Observe(seededEntity, CombatEntityRole.LocalPlayer);
            foreach (var character in waiting) Bind(character);
        }
        else { localCarried = carried; carriedConfirmed = false; localEntity = seededEntity; Observe(seededEntity, CombatEntityRole.LocalPlayer, replaceRole: true); }
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
        generation = AdvanceGeneration(); sequence = 0; localEntity = null; characterId = character; localCarried = false; carriedConfirmed = false;
        entities.Clear(); lastSeen.Clear(); owners.Clear(); unboundCharacters.Clear(); unboundBytes = 0;
        recentSkills.Clear(); activeBuffs.Clear(); localHealth = null; healthSteps.Clear();
        ClearActors(keepBroadcast: true);
        // 延续的实体在本机身份重新确认（补足起点或个人数据开始新场景）之前一直补回新代次。
        if (reseed is { } pending) { reseed = null; SeedEntities(pending, inputMs, clockUtc); }
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
        if (character.CharacterId != own)
        {
            // 沿用的本机实体出现为其他角色：起点与观测矛盾，作废沿用，不把该角色记为本机。
            if (localCarried && localEntity == character.EntityId)
            { localEntity = null; characterId = null; localCarried = false; CarriedContradicted = true; }
            Observe(character.EntityId, CombatEntityRole.Player, replaceRole: true); return;
        }
        if (localEntity is { } bound && bound != character.EntityId)
        {
            if (!localCarried) return;
            // 沿用的实体是旧地图的：同一角色的新实体出现，改绑本机。
            Observe(bound, CombatEntityRole.Unknown, replaceRole: true);
        }
        localEntity = character.EntityId; localCarried = false; Observe(character.EntityId, CombatEntityRole.LocalPlayer, replaceRole: true);
    }

    /// <summary>
    /// 只有 Type=Monster 的自动体登记为怪物并带出现帧的破防值；其他类型撤销先前的怪物角色。
    /// 非怪物自动体带有效 OwnerPlayerId（非 0、非 -1、不是自身）时登记所有者，实体以 Owned 角色与已确认所有者发布。
    /// </summary>
    private void OnAutomatonAppeared(ReadOnlySpan<byte> body, byte encoding)
    {
        TwCombatWire.RequireCompressed(encoding);
        var automaton = TwAutomatonAppearance.Decode(body);
        memo.Remove(automaton.EntityId);
        if (!automaton.IsMonster && automaton.OwnerPlayerId is not (0 or ulong.MaxValue) && automaton.OwnerPlayerId != automaton.EntityId)
            owners[automaton.EntityId] = automaton.OwnerPlayerId;
        else owners.Remove(automaton.EntityId);
        if (!automaton.IsMonster)
        {
            Observe(automaton.EntityId, replaceRole: true, appearance: true);
            if (!entities.ContainsKey(automaton.EntityId)) owners.Remove(automaton.EntityId);
            RecordActor(automaton); return;
        }
        var code = automaton.MonsterTypeCode!;
        Observe(automaton.EntityId, CombatEntityRole.Monster, code, monsterNames.GetValueOrDefault(code) ?? code, appearance: true);
        if (!entities.TryGetValue(automaton.EntityId, out var monster)) return;
        UInt64String? maximum = automaton.BreakPointMax is { } value ? new((ulong)value) : null;
        var reason = automaton.BreakPoint is null || automaton.BreakPointMax is null ? "combat.breakGaugeUnknown" : null;
        entities[automaton.EntityId] = monster with
            { BreakGauge = new(automaton.BreakPoint, maximum, clockUtc, ObservationQuality.Confirmed, Reason: reason) };
        RecordActor(automaton);
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
    /// 记录实体出现。已知实体只刷新出现时点，除非带来明确角色或要求覆盖角色；新实体满额时先按 <see cref="EvictFor"/> 腾出空位，
    /// 仍无空位记 combat.entityCapacity。被淘汰过的实体以未知角色再次出现时恢复其类型与 HP 记录。登记或改写都使下一批携带实体快照。
    /// </summary>
    /// <param name="appearance">来自自动体出现帧：容量满时可淘汰其他玩家，保证怪物出现帧不被拒。</param>
    private void Observe(ulong id, CombatEntityRole role = CombatEntityRole.Unknown, string rawName = "", string? displayName = null,
        bool replaceRole = false, bool appearance = false)
    {
        if (id == 0) return;
        var known = entities.ContainsKey(id);
        if (known)
        {
            lastSeen[id] = inputMs;
            if (role == CombatEntityRole.Unknown && !replaceRole) return;
        }
        else if (entities.Count >= MaximumEntities && !EvictFor(appearance)) { AddGap("combat.entityCapacity", 1); return; }
        lastSeen[id] = inputMs;
        if (!known && role == CombatEntityRole.Unknown && !replaceRole && Restore(id)) { MarkSnapshot(); return; }
        entities[id] = Describe(id, new(Key(id), rawName, displayName ?? (rawName.Length > 0 ? rawName : id.ToString(CultureInfo.InvariantCulture)),
            role, null, false, true, ObservationQuality.Confirmed));
        departed.Remove(id);
        MarkSnapshot();
    }

    private void MarkSnapshot() { snapshotDirty = true; snapshotMs = inputMs; }

    /// <summary>
    /// 实体满额时腾出一个空位：先淘汰已离场的实体；新实体来自自动体出现帧时再淘汰最久未出现的其他玩家；
    /// 最后淘汰最久未出现且超过 <see cref="EntityIdleMs"/> 的非本机实体。被淘汰实体的记录进入身份备忘，效果实例一并移除。
    /// 没有可淘汰实体时返回假。
    /// </summary>
    private bool EvictFor(bool appearance)
    {
        ulong? victim = null; var earliest = double.PositiveInfinity; var rank = int.MaxValue;
        foreach (var (id, seen) in lastSeen)
        {
            if (id == localEntity || id == characterId || !entities.TryGetValue(id, out var entity)) continue;
            var candidate = !entity.InScope ? 0 : appearance && entity.Role.KnownValue == CombatEntityRole.Player ? 1 : inputMs - seen >= EntityIdleMs ? 2 : int.MaxValue;
            if (candidate > rank || candidate == rank && seen >= earliest || candidate == int.MaxValue) continue;
            victim = id; earliest = seen; rank = candidate;
        }
        if (victim is not { } evicted) return false;
        if (entities.Remove(evicted, out var removed)) Remember(evicted, removed);
        lastSeen.Remove(evicted); actors.Remove(evicted); departed.Remove(evicted); owners.Remove(evicted);
        foreach (var key in activeBuffs.Keys.Where(key => key.Owner == evicted).ToArray()) activeBuffs.Remove(key);
        snapshotDirty = true; carryRevision++;
        return true;
    }
    private bool EvictIdle() => EvictFor(false);
}
