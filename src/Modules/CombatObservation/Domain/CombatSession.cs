using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

public sealed record CombatTimelineRecord(string EncounterId, CombatTimelineItem Item)
{
    /// <summary>条目加入时已算出的 JSON 编码（UTF-8），保存时直接写入时间线页；为空时保存方自行编码。</summary>
    internal byte[]? Encoded { get; init; }
}
public sealed record CombatCheckpoint(CombatWorkspace Workspace, IReadOnlyList<CombatEncounter> Encounters,
    IReadOnlyList<CombatEntityEffects> Effects, IReadOnlyList<CombatTimelineRecord> Timeline,
    IReadOnlyList<CombatGap> Gaps, UInt64String TimelineWatermark)
{
    /// <summary>自上次确认的检查点以来观察级缺口有无变化；未变化时不再追加 degraded 事件。</summary>
    public bool GapsChanged { get; init; } = true;
    public ulong GapRevision { get; init; }
    /// <summary>已取消的定时测量：检查点提交时删除其未结束的记录与时间线。</summary>
    public IReadOnlyList<string> DeletedEncounters { get; init; } = [];
    /// <summary>
    /// 自上次确认的检查点以来有需要持久保存的变化：待写时间线、效果、缺口、待保存的已结束分段或删除，
    /// 或实体集合与观察状态（不含观测时刻与单调时点的推进）有变。只有时间推进时为假。
    /// </summary>
    public bool Material { get; init; } = true;
    /// <summary>需要在本次检查点立即保存：分段进行中、定时测量计时中、有待保存的已结束分段或删除，或观察已停止。</summary>
    public bool Urgent { get; init; } = true;
    internal CombatSession.PersistedShape? Shape { get; init; }
    /// <summary>捕获时的效果变化序号；确认时清除不晚于它的效果标脏。</summary>
    internal ulong EffectWatermark { get; init; }
}

/// <summary>
/// 进行中定时测量的累计：窗口的观察单调时点、已归属伤害、第一击与最后一击，以及缺口原因。
/// <see cref="Waiting"/> 为真表示还在等待本机第一次有效伤害，窗口尚未开始（StartMs、EndMs 为受理时点）。
/// </summary>
public sealed record CombatMeasurementProgress(string Id, double StartMs, double EndMs, System.Numerics.BigInteger Damage,
    double? FirstDamageMs, double? LastDamageMs, bool Partial, IReadOnlyList<string> Reasons, bool Waiting = false);

/// <summary>规范化观测的唯一战斗状态；时间、身份和归属规则可脱离捕获来源执行。</summary>
public sealed class CombatSession
{
    private readonly object gate = new();
    private ulong gapRevision, acknowledgedGapRevision;
    /// <summary>最近一帧来源实体快照（来源每批携带完整的有界实体集）；容量满时先淘汰已不在快照中的实体。</summary>
    private HashSet<EntityKey> sourceEntities = [];
    private readonly string owner, observationId;
    private readonly CombatObservationRequest settings;
    private readonly ObservationSource source;
    private readonly CombatLimits limits;
    private readonly DateTimeOffset startedAt;
    private readonly Func<string> encounterIdFactory;
    private readonly bool healing;
    private readonly Dictionary<EntityKey, CombatEntity> entities = [];
    /// <summary>去重表：事件身份 → 内容（伤害为 <see cref="DamageContent"/>，治疗为内容摘要）与登记时点。</summary>
    private readonly Dictionary<string, (object Content, double At)> deduplication = new(StringComparer.Ordinal);
    // 当前攻击目标：本机有效伤害、主目标与实体状态由选取器评估，结果随快照投影；localHits 为本机命中过的实体，healthDrops 为最近一次掉血时点。
    private readonly CurrentTargetSelector targets = new();
    private CurrentTargetSelection? selection;
    private readonly HashSet<EntityKey> localHits = [];
    private readonly Dictionary<EntityKey, double> healthDrops = [];
    private const int MaximumLocalHits = 2048;
    private readonly Queue<(string Key, double At)> deduplicationOrder = [];
    private readonly EffectTracker effects;
    private readonly Dictionary<EntityKey, ulong> dirtyEffects = [];
    /// <summary>效果变化序号：标脏时记下当时的序号，确认时只清除不晚于该检查点的标记，后台保存期间的新变化保留。</summary>
    private ulong effectChanges;
    private void MarkEffect(EntityKey key) => dirtyEffects[key] = ++effectChanges;
    private readonly List<EncounterState> closed = [];
    private readonly List<CombatTimelineRecord> pendingTimeline = [];
    private readonly List<CombatGap> gaps = [];
    private readonly HashSet<string> reasons = new(StringComparer.Ordinal);
    // 切图边界后等待重新取得本机身份；期间的连接缺口及身份等待原因只在恢复前进入观察状态。
    private readonly HashSet<string> awaitingReasons = new(StringComparer.Ordinal);
    private bool awaitingIdentity, mapChanged, splitPending;
    // 本机战斗状态（来源 170196）；未收到、旧配置或换代次/换连接后为空，此时分段规则与未接入信号时相同。
    // combatLeft：上一分段因脱离战斗结束，下一笔有效本机伤害开始的分段记 combat-entered。
    // combatLeftAt：本机离开 in-combat 的来源时点；余量内回到战斗或续击时取消，余量结束才结束分段。
    private OpenEnum<CombatLocalState>? localCombatState;
    private bool combatLeft;
    private double? combatLeftAt;
    // 整场：分段按整场序号分组；到期（切图、脱战后无战斗动作）只作标记，下一个分段开始时才开启新的一场。
    // resetPending：重设后第一个分段的开始原因记 reset。lastActionMs：本机最近一次战斗动作的来源时点。
    private int wholeOrdinal = 1;
    private string? wholeExpired;
    private bool resetPending;
    private double? lastActionMs;
    private double resetFloorMs = double.NegativeInfinity;
    // 同一连接内的流重置（重组超时、解码失败、捕获丢包等，combat.streamGap.* 中连接变化以外的原因）：Worker 随之换代次，
    // 本段以 stream-reset 结束，与连接更换（connection-changed）及原因不明的 observation-reconnected 区分。
    private string? streamReset;
    private UInt64String? awaitingGeneration;
    private int segmentOrdinal;
    private EntityKey local;
    private EncounterState? encounter;
    private double mono;
    /// <summary>
    /// 已接受帧的来源时点上限。<see cref="mono"/> 还会被宿主按收帧后的流逝时间推进（Tick、手动边界），
    /// 而 Worker 帧携带事件的来源时点，配对等待与写出/管道延迟使其晚到；迟到判定只与来源时点比较，避免误拒合法帧。
    /// </summary>
    private double sourceMono;
    private readonly Dictionary<string, ulong> frameRejections = new(StringComparer.Ordinal);
    private DateTimeOffset? lastObserved;
    private ulong frameSequence, revision, timelineIndex, dropped;
    private ulong boundaryRevision = 1;
    private int pendingBytes;
    /// <summary>移除待写时间线条目并按各条目加入时的编码长度扣减待写字节。</summary>
    private void RemovePending(Predicate<CombatTimelineRecord> match)
    {
        foreach (var record in pendingTimeline) if (match(record)) pendingBytes -= Size(record);
        pendingTimeline.RemoveAll(match);
    }
    private static int Size(CombatTimelineRecord record) => record.Encoded?.Length ?? JsonSerializer.SerializeToUtf8Bytes(record.Item, ContractJson.Options).Length;
    /// <summary>最近一帧（已规范化）；同一序号的帧再次到达时才按编码比较内容，正常帧不计算哈希。</summary>
    private CombatFrame? lastFrame;
    private static string FrameHash(CombatFrame value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options)));
    private string state = "observing";
    // 定时测量：与分段并行统计窗口内的伤害与治疗，不受分段边界影响；取消的记录在下一个检查点删除。
    // 受理后先等待本机第一次有效伤害（measurementPending），窗口从那一击开始按设定时长计时。
    private EncounterState? measurement;
    private (string Id, DateTimeOffset RequestedAt, double RequestedMs)? measurementPending;
    private double measurementEndMs, measurementDurationMs, measurementCountdownMs;
    private readonly List<string> deletedRecords = [];

    public CombatSession(string owner, string observationId, ObservationSource source, CombatEntityObservation localEntity,
        CombatObservationRequest settings, DateTimeOffset startedAt, CombatLimits? limits = null, Func<string>? encounterIdFactory = null,
        bool healing = false)
    {
        ValidateSettings(settings); this.limits = limits ?? new(); this.limits.Validate();
        // 治疗随伤害开关统计；来源不支持治疗时分段不返回治疗块。
        this.healing = healing && settings.DamageEnabled;
        if (localEntity.Key.GameSessionId != settings.GameSessionId || localEntity.Role.KnownValue != CombatEntityRole.LocalPlayer ||
            localEntity.Quality.KnownValue != ObservationQuality.Confirmed) throw new ArgumentException("本机身份缺少当前会话的确认依据");
        this.owner = owner; this.observationId = observationId; this.source = source; this.settings = settings;
        this.startedAt = startedAt; local = localEntity.Key;
        this.encounterIdFactory = encounterIdFactory ?? (() => "enc-" + Guid.NewGuid().ToString("N"));
        effects = new(this.limits, OtherCacheBytes, reason => MarkGap(reason, mono, mono, 1));
        if (!PutEntity(localEntity, startedAt)) throw new ArgumentException("本机实体观测无效");
    }
    public const int MaximumRollingWindowMs = 60000;
    /// <summary>本机离开 in-combat 后的余量：期间回到战斗或续击仍属于同一分段，超过才以 combat-left 结束。</summary>
    public const double CombatLeftGraceMs = 10000;
    /// <summary>整场到期：本机不在 in-combat 且距最后一次战斗动作满这么久时，下一个分段开启新的一场。</summary>
    public const double WholeIdleMs = 180000;
    public static void ValidateSettings(CombatObservationRequest value)
    {
        if (value is null || !ValidId(value.GameSessionId) || !value.DamageEnabled && !value.SelfEffectsEnabled && !value.MonsterEffectsEnabled ||
            value.RollingWindowMs is < 100 or > 60000 || value.IdleTimeoutMs is < 1000 or > 300000 ||
            value.ReplayIntervalMs is < 25 or > 2000 || value.ReplayScenarioId is { } scenario && !ValidId(scenario))
            throw new ArgumentException("观察需要有效会话和至少一项能力；滑动窗口为 100–60000 ms，空闲边界为 1000–300000 ms");
    }
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    public static string EntityId(EntityKey key) => "ce-" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(key, ContractJson.Options)))[..32].ToLowerInvariant();
    public EntityKey LocalEntity { get { lock (gate) return local; } }
    public double MonotonicMs { get { lock (gate) return mono; } }
    public string? CurrentEncounterId { get { lock (gate) return encounter?.Id; } }
    public bool Active { get { lock (gate) return state == "observing"; } }
    /// <summary>
    /// 自上次确认以来有不必构建快照即可判定的待保存变化（待写时间线、效果、缺口、已结束分段或删除）；实体集合与观察状态的变化只在
    /// <see cref="Capture"/> 时比较。宿主据此决定空闲时是否需要立即捕获。
    /// </summary>
    public bool PendingChanges
    {
        get { lock (gate) return pendingTimeline.Count > 0 || dirtyEffects.Count > 0 || gapRevision != acknowledgedGapRevision || closed.Count > 0 || deletedRecords.Count > 0; }
    }
    private long OtherCacheBytes() => entities.Count * 1536L + deduplication.Count * 768L +
        (encounter?.GroupBytes ?? 0) + (measurement?.GroupBytes ?? 0) +
        closed.Where(x => !ReferenceEquals(x, encounter) && !ReferenceEquals(x, measurement)).Sum(x => x.GroupBytes);
    private CombatResourceUsage Resources() => new(entities.Count, effects.Count, deduplication.Count,
        checked((int)(encounter?.TimelineCount ?? 0)), new((ulong)(OtherCacheBytes() + effects.Bytes)),
        new(encounter?.TimelineBytes ?? 0), new(dropped), new(encounter?.Truncated ?? 0));
    private bool Scope(EntityKey key) => key.GameSessionId == settings.GameSessionId && key.ObservationGeneration == local.ObservationGeneration;
    private bool PutEntity(CombatEntityObservation value, DateTimeOffset at)
    {
        if (!Scope(value.Key) || value.RawName is null || value.DisplayName is null || value.RawName.Length > 256 ||
            value.DisplayName.Length > 256 || value.RawName.Any(char.IsControl) || value.DisplayName.Any(char.IsControl) ||
            value.OwnerKey is { } ownerKey && !Scope(ownerKey) || !ValidActor(value))
        { MarkGap("entity-invalid", mono, mono, 1); return false; }
        if (!entities.ContainsKey(value.Key) && (entities.Count >= limits.MaximumEntities || OtherCacheBytes() + effects.Bytes + 1664 > limits.MaximumCacheBytes) &&
            !EvictAbsentEntity())
        { MarkGap("entity-capacity", mono, mono, 1); return false; }
        entities.TryGetValue(value.Key, out var previous);
        var gauge = value.BreakGauge ?? previous?.BreakGauge;
        if (previous?.BreakGauge is { } previousGauge && value.BreakGauge is { } nextGauge &&
            (nextGauge.ObservedAt < previousGauge.ObservedAt || previousGauge.Stale && nextGauge.ObservedAt == previousGauge.ObservedAt))
            gauge = previousGauge;
        if (gauge is not null && !value.InScope)
            gauge = gauge with { Stale = true, Reason = "entity-out-of-scope" };
        if (gauge is not null && gauge.Quality.KnownValue is not (ObservationQuality.Confirmed or ObservationQuality.Estimated))
            gauge = gauge with { Stale = true };
        // 来源只在 HP 变化后（及定期全量）随帧附带 health：未附带时沿用同一实体上次的值，快照对外仍给出完整 health。
        var entity = new CombatEntity(EntityId(value.Key), value.Key, value.RawName, value.DisplayName, value.Role,
            value.OwnerKey, value.OwnerConfirmed && value.Quality.KnownValue == ObservationQuality.Confirmed, value.InScope, at, value.Quality, gauge,
            null, value.Health ?? previous?.Health, value.IsBoss, value.Dead, value.ActorType, value.SheetId);
        if (previous?.Health?.Current is { } before && entity.Health?.Current is { } after && after.Value < before.Value) healthDrops[value.Key] = mono;
        var changedScope = previous is null || previous.InScope != entity.InScope;
        var changedGauge = previous?.BreakGauge != entity.BreakGauge;
        // 效果文档只随效果或范围变化重写；来源每批重报的实体（只有观测时刻不同）不使其效果文档变脏。
        entities[value.Key] = entity; effects.Scope(value.Key, value.InScope);
        if (changedScope) MarkEffect(value.Key);
        if (value.Key == local) RenameLocal(entity.DisplayName);
        if (changedScope || changedGauge) Timeline("entity", null, null, null, null, TimelineEntity(entity));
        // 破防区间在目标破防值归零时结束（进行中的分段与定时测量各自记录）。
        if (changedGauge && entity.BreakGauge is { Current: 0, Stale: false })
            foreach (var state in new[] { encounter, measurement })
                if (state is { Final: false }) state.Breaks.GaugeZero(entity.Id, Math.Max(0, mono - state.StartedMs));
        return true;
    }
    /// <summary>实体的 HP 与类型字段：状态、原因与类型为有界文本，当前值不超过 Int64。</summary>
    private static bool ValidActor(CombatEntityObservation value) =>
        value.ActorType is null or { Length: <= 32 } && value.ActorType?.Any(char.IsControl) != true &&
        (value.Health is null || value.Health is { State: { Length: > 0 and <= 32 } state } health && !state.Any(char.IsControl) &&
            health.Reason?.Length is not > 128 && health.Reason?.Any(char.IsControl) != true &&
            health.Current is null or { Value: <= long.MaxValue } && health.Maximum is null or { Value: <= long.MaxValue });
    /// <summary>时间线中的实体条目不带 HP：HP 只随实体快照返回，不为高频变化写时间线。</summary>
    private static CombatEntity TimelineEntity(CombatEntity entity) => entity.Health is null ? entity : entity with { Health = null };
    /// <summary>
    /// 实体的有效血量：加密形态、本机命中过、出现后始终没有 HP 更新的实体（服务器不下发其血量）标为 shared，由同场首领体现。
    /// </summary>
    private CombatEntityHealth? EffectiveHealth(CombatEntity entity) =>
        entity.Health is { Encrypted: true, Updated: not true } health && localHits.Contains(entity.Key)
            ? health with { State = "shared", Reason = "combat.healthShared" } : entity.Health;
    private CombatEntityHealth? EffectiveHealth(EntityKey key) => entities.TryGetValue(key, out var entity) ? EffectiveHealth(entity) : null;
    private void EvaluateTarget()
    {
        if (!settings.DamageEnabled) { selection = null; return; }
        selection = targets.Evaluate(mono, entities, EffectiveHealth, key => healthDrops.TryGetValue(key, out var at) ? at : null, localCombatState);
    }
    /// <summary>当前目标切换次数（不含清空），供离线回放统计。</summary>
    public int TargetSwitches { get { lock (gate) return targets.Switches; } }
    private CombatCurrentTarget? CurrentTarget()
    {
        if (selection is not { } chosen || !entities.TryGetValue(chosen.Target, out var entity)) return null;
        return new(entity.Id, entity.Key, entity.DisplayName, chosen.Basis, SelectedAt(chosen.SelectedMs), lastObserved ?? startedAt, EffectiveHealth(entity), entity.IsBoss, entity.Dead, null,
            chosen.RedirectedFrom is { } from ? EntityId(from) : null, entity.ActorType);
    }
    /// <summary>观察单调时点换算为观测时刻：以最近一帧的观测时刻与单调时点为基准。</summary>
    private DateTimeOffset SelectedAt(double ms) => (lastObserved ?? startedAt).AddMilliseconds(ms - mono);
    /// <summary>
    /// 本机昵称可能晚于分段开始才识别（Worker 在识别前以实体 ID 作本机显示名）：进行中的分段与定时测量仍是实体 ID 占位时，
    /// 改用本机实体的新显示名。已结束的分段与已有昵称的摘要不改。
    /// </summary>
    private void RenameLocal(string name)
    {
        var placeholder = local.RawInstanceId.ToString();
        if (name == placeholder) return;
        if (encounter is { Final: false } current && current.DisplayName == placeholder) current.DisplayName = name;
        if (measurement is { Final: false } window && window.DisplayName == placeholder) window.DisplayName = name;
    }

    /// <summary>淘汰一个来源已不再报告的非本机实体及其效果；长时间停留同一地图时缓存保持在上限内。</summary>
    private bool EvictAbsentEntity()
    {
        foreach (var key in entities.Keys)
        {
            if (key == local || sourceEntities.Contains(key)) continue;
            entities.Remove(key); effects.Remove(key); dirtyEffects.Remove(key); healthDrops.Remove(key); localHits.Remove(key);
            return true;
        }
        return false;
    }
    public bool Apply(CombatFrame frame)
    {
        lock (gate)
        {
            if (state != "observing") return false;
            if (RejectionBranch(frame) is { } branch) { RejectFrame(branch); return false; }
            frame = CombatFrameNormalization.Normalize(frame);
            if (frame.Generation != local.ObservationGeneration)
            {
                if (frame.Generation.Value < local.ObservationGeneration.Value) return false;
                // 切图边界已结束旧分段：新代次只用于等待身份，同一代次只记一次等待原因。
                var awaiting = awaitingIdentity;
                var firstOfGeneration = awaitingGeneration != frame.Generation;
                if (awaiting) { if (firstOfGeneration) BoundaryGap("observation-reconnected", mono, frame.MonotonicMs, 0); }
                else
                {
                    var boundary = streamReset is null ? "observation-reconnected" : "stream-reset";
                    MarkGap(boundary, mono, frame.MonotonicMs, 0);
                    CloseCore(boundary, frame.ObservedAt, frame.MonotonicMs);
                }
                streamReset = null; localCombatState = null; combatLeftAt = null;
                var identities = frame.Entities.Where(x => x.Role.KnownValue == CombatEntityRole.LocalPlayer && x.Key.GameSessionId == settings.GameSessionId &&
                    x.Key.ObservationGeneration == frame.Generation && x.Quality.KnownValue == ObservationQuality.Confirmed).Take(2).ToArray();
                if (identities.Length != 1)
                {
                    MeasurementBoundary("identity-unresolved");
                    if (!awaiting) MarkGap("identity-unresolved", mono, mono, 0);
                    else
                    {
                        if (firstOfGeneration) BoundaryGap("identity-unresolved", mono, mono, 0);
                        // 等待身份期间的缺口不属于任何分段，仍保留在观察诊断中。
                        foreach (var gap in frame.Gaps ?? [])
                            if (ConnectionChange(gap.Reason) && gap.DroppedEvents.Value == 0) BoundaryGap(gap.Reason, gap.FromMs, gap.ToMs, 0);
                            else MarkGap(gap.Reason, gap.FromMs, gap.ToMs, gap.DroppedEvents.Value);
                        awaitingGeneration = frame.Generation;
                    }
                    return false;
                }
                var identity = identities[0];
                local = identity.Key; entities.Clear(); effects.Clear(); deduplication.Clear(); deduplicationOrder.Clear();
                dirtyEffects.Clear(); frameSequence = 0; lastFrame = null;
                targets.Clear(); selection = null; localHits.Clear(); healthDrops.Clear();
                if (awaiting)
                {
                    // 连接变化后重新取得本机身份，视为进入新地图。
                    awaitingIdentity = false; awaitingGeneration = null; awaitingReasons.Clear(); mapChanged = true;
                }
            }
            if (frame.Damage.Any(x => !double.IsFinite(x.MonotonicMs)) || frame.Effects.Any(x => !double.IsFinite(x.MonotonicMs)))
            { RejectFrame("frame.eventTime"); return false; }
            if (frame.Sequence.Value <= frameSequence)
            {
                if (frame.Sequence.Value == frameSequence && (lastFrame is null || FrameHash(lastFrame) != FrameHash(frame))) MarkGap("frame-conflict", mono, frame.MonotonicMs, 1);
                return false;
            }
            mono = Math.Max(mono, frame.MonotonicMs); sourceMono = Math.Max(sourceMono, frame.MonotonicMs); lastObserved = frame.ObservedAt; frameSequence = frame.Sequence.Value; lastFrame = frame;
            if (frame.ObservationResumed)
            {
                for (var i = 0; i < gaps.Count; i++) if (gaps[i].ToMs is null) gaps[i] = gaps[i] with { ToMs = mono };
                if (encounter is { Final: false })
                    for (var i = 0; i < encounter.Gaps.Count; i++)
                        if (encounter.Gaps[i].ToMs is null) encounter.Gaps[i] = encounter.Gaps[i] with { ToMs = Math.Max(0, mono - encounter.StartedMs) };
            }
            foreach (var gap in frame.Gaps.Where(x => !ConnectionChange(x.Reason)))
            {
                MarkGap(gap.Reason, gap.FromMs, gap.ToMs, gap.DroppedEvents.Value);
                if (gap.Reason.StartsWith("combat.streamGap", StringComparison.Ordinal)) streamReset = gap.Reason;
            }
            if (frame.Entities.Count > 0) sourceEntities = frame.Entities.Select(value => value.Key).ToHashSet();
            foreach (var value in frame.Entities)
                PutEntity(value, frame.ObservedAt);
            if (frame.MainTarget is { } main && (main.Target is null || Scope(main.Target))) targets.MainTarget(main.Target);
            if (frame.CombatStates is { Count: > 0 } states) ApplyInStreamOrder(frame, states);
            else
            {
                if (settings.DamageEnabled)
                    foreach (var value in frame.Damage.OrderBy(x => x.MonotonicMs)) Damage(value);
                if (healing && frame.Healing is { } heals)
                    foreach (var value in heals.OrderBy(x => x.MonotonicMs)) Heal(value);
            }
            foreach (var value in frame.Effects) ApplyEffect(value);
            foreach (var reconciliation in frame.Reconciliations)
            {
                if (!EffectAllowed(reconciliation.EntityKey)) continue;
                if (reconciliation.Effects.Any(value => !EffectValid(value))) { MarkGap("effect-invalid", mono, mono, 1); continue; }
                foreach (var value in effects.Reconcile(reconciliation, frame.ObservedAt, mono)) Timeline("effect", null, value, null, null);
                MarkEffect(reconciliation.EntityKey);
            }
            // 同帧伤害与效果先归入旧分段，再处理切图边界。
            foreach (var gap in frame.Gaps.Where(x => ConnectionChange(x.Reason)))
                ConnectionBoundary(gap, frame.ObservedAt);
            foreach (var key in effects.Tick(mono)) MarkEffect(key);
            if (frame.EncounterEnded) CloseCore("source-end", frame.ObservedAt, mono);
            CloseCombatLeft(mono); ExpireWhole(mono); EvaluateTarget();
            return true;
        }
    }
    /// <summary>
    /// 帧内带本机战斗状态时，伤害、治疗与状态按来源时点、同一时点按流偏移交错处理：击杀伤害先计入，其后的脱战再结束分段。
    /// 没有状态的帧沿用原处理顺序。
    /// </summary>
    private void ApplyInStreamOrder(CombatFrame frame, IReadOnlyList<CombatStateChange> states)
    {
        var items = new List<(double Mono, ulong Offset, int Kind, int Index)>();
        if (settings.DamageEnabled)
            for (var i = 0; i < frame.Damage.Count; i++) items.Add((frame.Damage[i].MonotonicMs, frame.Damage[i].Sequence?.Value ?? ulong.MaxValue, 0, i));
        if (healing && frame.Healing is { } heals)
            for (var i = 0; i < heals.Count; i++) items.Add((heals[i].MonotonicMs, heals[i].Sequence?.Value ?? ulong.MaxValue, 1, i));
        for (var i = 0; i < states.Count; i++) items.Add((states[i].MonotonicMs, states[i].Sequence?.Value ?? ulong.MaxValue, 2, i));
        foreach (var item in items.OrderBy(x => x.Mono).ThenBy(x => x.Offset).ThenBy(x => x.Kind).ThenBy(x => x.Index))
        {
            if (item.Kind == 0) Damage(frame.Damage[item.Index]);
            else if (item.Kind == 1) Heal(frame.Healing![item.Index]);
            else LocalState(states[item.Index]);
        }
    }
    /// <summary>
    /// 本机战斗状态从 in-combat 变为其他值即脱离战斗：进行中的自动分段以 combat-left 结束并截止最后有效伤害（与空闲边界同一截止）。
    /// 手动开始的分段只由用户结束；进入 in-combat 不开始分段，分段仍由下一笔有效本机伤害开始。
    /// </summary>
    private void LocalState(CombatStateChange value)
    {
        if (!double.IsFinite(value.MonotonicMs) || value.MonotonicMs > mono || value.MonotonicMs < sourceMono - 250)
        { MarkGap("combat-state-invalid-or-late", Math.Max(0, double.IsFinite(value.MonotonicMs) ? value.MonotonicMs : mono), mono, 0); return; }
        var previous = localCombatState;
        localCombatState = value.State;
        // 余量内回到战斗：分段照常延续。
        if (value.State.KnownValue == CombatLocalState.InCombat) { combatLeftAt = null; return; }
        if (previous?.KnownValue != CombatLocalState.InCombat) return;
        if (encounter is { Final: false, Manual: false, LastDamageMs: not null }) combatLeftAt = value.MonotonicMs;
    }
    /// <summary>离开 in-combat 的余量已过且期间没有续上：分段以 combat-left 结束，截止最后有效伤害。</summary>
    private void CloseCombatLeft(double time)
    {
        if (combatLeftAt is not { } left || time - left <= CombatLeftGraceMs) return;
        combatLeftAt = null;
        if (encounter is { Final: false, Manual: false, LastDamageMs: { } last } current)
        {
            CloseCore("combat-left", current.LastDamageAt!.Value, last);
            combatLeft = true;
        }
    }
    /// <summary>本机战斗动作：本机或计入的召唤物的有效伤害、本机受到的正数伤害、本机施放或受到的治疗。</summary>
    private void CombatAction(double time) => lastActionMs = Math.Max(lastActionMs ?? time, time);
    /// <summary>
    /// 整场到期判定：本机不在 in-combat（含未知）且距最后一次战斗动作满 <see cref="WholeIdleMs"/>。
    /// 仍在进行的自动分段以 idle-timeout 结束（截止最后有效伤害）；已有数据保留，下一个分段开始时才开启新的一场。
    /// </summary>
    private void ExpireWhole(double time)
    {
        if (wholeExpired is not null || lastActionMs is not { } action || time - action < WholeIdleMs ||
            localCombatState?.KnownValue == CombatLocalState.InCombat) return;
        wholeExpired = "idle-out-of-combat";
        if (encounter is { Final: false, Manual: false, LastDamageMs: { } last } current)
            CloseCore("idle-timeout", current.LastDamageAt!.Value, last);
    }
    /// <summary>帧级拒绝的固定分支；未拒绝时为空。来源时点只与已接受帧比较（见 <see cref="sourceMono"/>）。</summary>
    private string? RejectionBranch(CombatFrame frame)
    {
        if (!CombatFrameNormalization.IsWellFormed(frame)) return "frame.structure";
        if (frame.SchemaVersion != 1) return "frame.schema";
        if (frame.Source != source || frame.GameSessionId != settings.GameSessionId) return "frame.scope";
        if (!double.IsFinite(frame.MonotonicMs) || frame.MonotonicMs > 1e12) return "frame.time";
        if (frame.MonotonicMs < sourceMono - 250) return "frame.late";
        return null;
    }
    private void RejectFrame(string branch)
    {
        frameRejections[branch] = frameRejections.GetValueOrDefault(branch) + 1;
        MarkGap("frame-invalid", mono, mono, 1);
    }
    /// <summary>按固定分支累计的帧级拒绝次数（frame.structure、frame.schema、frame.scope、frame.time、frame.late、frame.eventTime）。</summary>
    public IReadOnlyDictionary<string, ulong> FrameRejections { get { lock (gate) return new Dictionary<string, ulong>(frameRejections, StringComparer.Ordinal); } }
    /// <summary>台服切图时游戏更换数据连接；这些原因是分段边界，不表示分段内数据缺失。</summary>
    public static bool ConnectionChange(string? reason) => reason is "combat.streamGap.connectionClosed" or
        "combat.streamGap.connectionRemoved" or "combat.streamGap.connectionSyn" or "combat.streamGap.connectionReset";
    private void ConnectionBoundary(CombatGapInput gap, DateTimeOffset at)
    {
        var lost = gap.DroppedEvents.Value;
        if (lost > 0) MarkGap(gap.Reason, gap.FromMs, gap.ToMs, lost);
        MeasurementBoundary("connection-changed");
        if (!awaitingIdentity && encounter is { Final: false } current)
        {
            if (current is { Manual: false, LastDamageMs: { } last }) CloseCore("connection-changed", current.LastDamageAt!.Value, last);
            else CloseCore("connection-changed", at, double.IsFinite(gap.FromMs) ? Math.Min(gap.FromMs, mono) : mono);
        }
        awaitingIdentity = true; mapChanged = false; splitPending = false; combatLeft = false; localCombatState = null; combatLeftAt = null;
        targets.Clear(); selection = null;
        // 切图使整场到期：已有数据保留，新地图的第一个分段开启新的一场。
        if (lastActionMs is not null || encounter is not null) wholeExpired ??= "map-change";
        if (lost == 0) BoundaryGap(gap.Reason, gap.FromMs, gap.ToMs, 0);
        else awaitingReasons.Add(gap.Reason);
    }
    /// <summary>观察级缺口：进入缺口列表及效果/破防过期处理，不进入任何分段；等待身份期间进入观察状态原因。</summary>
    private void BoundaryGap(string reason, double fromMs, double? toMs, ulong lost)
    {
        fromMs = double.IsFinite(fromMs) ? Math.Max(0, Math.Min(fromMs, mono)) : mono;
        // 边界是瞬时事件；不留开放区间，避免后续分段继承。
        if (toMs is not { } end || !double.IsFinite(end) || end < fromMs) toMs = fromMs;
        var gap = new CombatGap(reason, fromMs, toMs, new(lost));
        if (gaps.Count < 256) gaps.Add(gap);
        else gaps[^1] = CombatGapAccumulator.Merge(gaps[^1], gap);
        gapRevision++;
        if (awaitingReasons.Count < 128) awaitingReasons.Add(reason);
        effects.Gap(); foreach (var key in entities.Keys) MarkEffect(key);
        StaleGauges(reason);
    }
    /// <summary>
    /// 伤害按攻击者归属：本机为 local；所有者已确认为本机的所属实体为 owned，所有者已确认为其他实体的为 other；
    /// 玩家与怪物为 other，其余为 unknown。技能表现的施放者不改变归属（连携只影响技能分组）。
    /// </summary>
    private string Attribution(EntityKey key)
    {
        if (key == local) return "local";
        if (!entities.TryGetValue(key, out var entity)) return "unknown";
        if (entity.OwnerConfirmed && entity.OwnerKey is { } owner && entity.Role.KnownValue == CombatEntityRole.Owned) return owner == local ? "owned" : "other";
        return entity.Role.KnownValue is CombatEntityRole.Player or CombatEntityRole.Monster ? "other" : "unknown";
    }
    private void Damage(CombatDamageEvent value)
    {
        if (!Scope(value.SourceEntity) || !Scope(value.TargetEntity) || value.Source != source ||
            value.SkillCasterKey is { } caster && !Scope(caster) || value.Chain == true &&
            (value.SkillCasterKey is null || value.SkillCasterKey == value.SourceEntity) ||
            value.Quality.KnownValue != ObservationQuality.Confirmed || !double.IsFinite(value.MonotonicMs) ||
            value.MonotonicMs > mono || value.MonotonicMs < sourceMono - 250 ||
            value.EventId is null || value.EventId.Length > 128 || value.EventId.Length == 0 && value.Sequence is null ||
            value.EventId.Any(char.IsControl) || value.SubEventIndex is < 0 or > 10000)
        { MarkGap("damage-invalid-or-late", Math.Max(0, value.MonotonicMs), mono, 1); return; }
        while (deduplicationOrder.TryPeek(out var expired) && expired.At < mono - 30000)
        { deduplicationOrder.Dequeue(); deduplication.Remove(expired.Key); }
        var key = (value.EventId.Length == 0 ? "seq:" + value.Sequence : "id:" + value.EventId) + ":" + value.SubEventIndex;
        var content = DamageContent.Of(value);
        if (deduplication.TryGetValue(key, out var previous))
        { if (!previous.Content.Equals(content)) MarkGap("damage-conflict", value.MonotonicMs, mono, 0); return; }
        if (deduplication.Count >= limits.MaximumDeduplicationEntries || OtherCacheBytes() + effects.Bytes + 768 > limits.MaximumCacheBytes)
        { MarkGap("deduplication-capacity", value.MonotonicMs, mono, 1); return; }
        deduplication.Add(key, (content, mono)); deduplicationOrder.Enqueue((key, mono));
        var attribution = Attribution(value.SourceEntity);
        if (measurementPending is { } waiting && value.Amount.Value > 0 && value.MonotonicMs >= waiting.RequestedMs &&
            (attribution == "local" || attribution == "owned" && settings.IncludeOwnedEntities))
            StartMeasurementWindow(waiting.Id, value.ObservedAt, value.MonotonicMs);
        if (measurement is { Final: false } window && value.Amount.Value > 0 && attribution is "local" or "owned" or "unknown" &&
            value.MonotonicMs >= window.StartedMs && value.MonotonicMs <= measurementEndMs)
        {
            window.Add(value, attribution, EntityId(value.TargetEntity), entities.GetValueOrDefault(value.TargetEntity)?.DisplayName ?? value.TargetEntity.RawInstanceId.ToString(),
                () => OtherCacheBytes() + effects.Bytes + 512 <= limits.MaximumCacheBytes, CombatNames.MonsterLabel(entities.GetValueOrDefault(value.TargetEntity)));
            Timeline(window, "damage", value, null, null, attribution);
            if (attribution == "local" || attribution == "owned" && settings.IncludeOwnedEntities)
                window.Breaks.Damage(EntityId(value.TargetEntity), value.MonotonicMs - window.StartedMs, value.BreakOccurred, value.BreakedHit);
        }
        // 重设时点之前的迟到伤害不属于重设后的任何分段。
        if (value.MonotonicMs < resetFloorMs) { MarkGap("damage-before-boundary", value.MonotonicMs, mono, 1); return; }
        var attributed = attribution == "local" || attribution == "owned" && settings.IncludeOwnedEntities;
        if (value.Amount.Value > 0 && (attributed || value.TargetEntity == local)) CombatAction(value.MonotonicMs);
        if (attributed && value.Amount.Value > 0 && value.TargetEntity != local)
        {
            targets.Hit(value.MonotonicMs, value.TargetEntity, value.Amount.Value);
            if (localHits.Count < MaximumLocalHits) localHits.Add(value.TargetEntity);
        }
        if (attributed && value.Amount.Value > 0)
        {
            // 离开 in-combat 超过余量后的伤害属于下一段；余量内的伤害计入本段，回到 in-combat 才取消余量。
            CloseCombatLeft(value.MonotonicMs);
            if (encounter is { Final: false, Manual: false, LastDamageMs: { } last } && value.MonotonicMs - last >= settings.IdleTimeoutMs)
                CloseCore("idle-timeout", encounter.LastDamageAt!.Value, last);
            if (encounter is null || encounter.Final) Begin(value.ObservedAt, value.MonotonicMs, false, "first-damage");
        }
        if (encounter is null || encounter.Final) return;
        if (value.MonotonicMs < encounter.StartedMs) { MarkGap("damage-before-boundary", value.MonotonicMs, mono, 1); return; }
        if (value.Amount.Value > 0)
        {
            encounter.Add(value, attribution, EntityId(value.TargetEntity), entities.GetValueOrDefault(value.TargetEntity)?.DisplayName ?? value.TargetEntity.RawInstanceId.ToString(),
                () => OtherCacheBytes() + effects.Bytes + 512 <= limits.MaximumCacheBytes, CombatNames.MonsterLabel(entities.GetValueOrDefault(value.TargetEntity)));
            if (attributed) encounter.Breaks.Damage(EntityId(value.TargetEntity), value.MonotonicMs - encounter.StartedMs, value.BreakOccurred, value.BreakedHit);
        }
        Timeline("damage", value, null, null, attribution);
    }
    /// <summary>治疗只计入进行中的分段，不开始或延续分段，也不推进最后伤害时点。</summary>
    private void Heal(CombatHealEvent value)
    {
        var direction = value.Direction.KnownValue;
        if (direction is null || value.TargetEntity is { } checkedTarget && !Scope(checkedTarget) ||
            direction == CombatHealDirection.Cast && value.TargetEntity is null ||
            value.Quality.KnownValue != ObservationQuality.Confirmed || value.Amount.Value == 0 || !double.IsFinite(value.MonotonicMs) ||
            value.MonotonicMs > mono || value.MonotonicMs < sourceMono - 250 || value.EventId is not { Length: > 0 and <= 128 } ||
            value.EventId.Any(char.IsControl) || (value.EffectiveAmount is null) != (value.Overheal is null))
        { MarkGap("heal-invalid-or-late", Math.Max(0, double.IsFinite(value.MonotonicMs) ? value.MonotonicMs : mono), mono, 1); return; }
        while (deduplicationOrder.TryPeek(out var expired) && expired.At < mono - 30000)
        { deduplicationOrder.Dequeue(); deduplication.Remove(expired.Key); }
        var key = "heal:" + value.EventId;
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value with { ObservedAt = default, MonotonicMs = 0 }, ContractJson.Options)));
        if (deduplication.TryGetValue(key, out var previous))
        { if (!previous.Content.Equals(hash)) MarkGap("heal-conflict", value.MonotonicMs, mono, 0); return; }
        if (deduplication.Count >= limits.MaximumDeduplicationEntries || OtherCacheBytes() + effects.Bytes + 768 > limits.MaximumCacheBytes)
        { MarkGap("deduplication-capacity", value.MonotonicMs, mono, 1); return; }
        deduplication.Add(key, (hash, mono)); deduplicationOrder.Enqueue((key, mono));
        CombatAction(value.MonotonicMs);
        if (measurement is { Final: false, Healing: { } measured } window && value.MonotonicMs >= window.StartedMs && value.MonotonicMs <= measurementEndMs)
        {
            AddHeal(measured, value);
            Timeline(window, "heal", null, null, null, null, heal: value);
        }
        if (encounter is not { Final: false, Healing: { } accumulator } current || value.MonotonicMs < current.StartedMs ||
            current is { Manual: false, LastDamageMs: { } last } && value.MonotonicMs - last >= settings.IdleTimeoutMs) return;
        AddHeal(accumulator, value);
        Timeline("heal", null, null, null, null, heal: value);
    }
    private void AddHeal(HealingAccumulator accumulator, CombatHealEvent value)
    {
        var target = value.TargetEntity;
        accumulator.Add(value, target == local, target is null ? "" : EntityId(target),
            target is null ? "" : entities.GetValueOrDefault(target)?.DisplayName ?? target.RawInstanceId.ToString(),
            () => OtherCacheBytes() + effects.Bytes + 1024 <= limits.MaximumCacheBytes);
    }
    private bool EffectAllowed(EntityKey key) => Scope(key) && entities.TryGetValue(key, out var entity) &&
        (key == local ? settings.SelfEffectsEnabled : settings.MonsterEffectsEnabled && entity.Role.KnownValue == CombatEntityRole.Monster);
    private bool EffectValid(CombatEffectObservation value) => Scope(value.EntityKey) &&
        (value.CasterKey is null || Scope(value.CasterKey)) && double.IsFinite(value.MonotonicMs) &&
        value.MonotonicMs <= mono && value.MonotonicMs >= sourceMono - 250 && value.Stacks is null or >= 0 &&
        value.Category?.Length is not > 128 && value.Quality.KnownValue == ObservationQuality.Confirmed;
    private void ApplyEffect(CombatEffectObservation value)
    {
        if (!EffectAllowed(value.EntityKey)) return;
        if (!EffectValid(value)) { MarkGap("effect-invalid-or-late", mono, mono, 1); return; }
        if (!effects.Apply(value)) return;
        MarkEffect(value.EntityKey); Timeline("effect", null, value, null, null);
    }
    private void Begin(DateTimeOffset at, double time, bool manual, string startReason)
    {
        // 切图优先；手动拆段只标记其后由首笔伤害自动开始的分段。
        // 脱战后的首击记 combat-entered，切图与手动拆段优先。
        var reason = mapChanged ? "map-change" : resetPending ? "reset" : splitPending && startReason == "first-damage" ? "manual-split"
            : combatLeft && startReason == "first-damage" ? "combat-entered" : startReason;
        // 整场已到期：这个分段开启新的一场。
        if (wholeExpired is not null) { wholeOrdinal = checked(wholeOrdinal + 1); wholeExpired = null; }
        var segment = new CombatSegment(checked(++segmentOrdinal), reason, wholeOrdinal);
        mapChanged = false; splitPending = false; combatLeft = false; resetPending = false; combatLeftAt = null;
        encounter = new(encounterIdFactory(), owner, observationId, local,
            entities.GetValueOrDefault(local)?.DisplayName ?? local.RawInstanceId.ToString(), at, time, manual, settings.IncludeOwnedEntities, limits, segment,
            healing);
        encounter.BoundaryRevision = new(checked(++boundaryRevision));
        foreach (var gap in gaps.Where(x => x.ToMs is null)) encounter.Gap(gap with { FromMs = 0 });
        foreach (var entity in entities.Values) Timeline("entity-baseline", null, null, null, null, TimelineEntity(entity));
        foreach (var value in effects.Baseline(at, time)) Timeline("effect-baseline", null, value, null, null);
    }
    private void CloseCore(string reason, DateTimeOffset at, double time)
    {
        if (encounter is null || encounter.Final) return;
        encounter.BoundaryRevision = new(checked(++boundaryRevision));
        encounter.EndedMs = Math.Max(encounter.StartedMs, time); encounter.EndedAt = at; encounter.Boundary = reason;
        if (closed.Count < 128) closed.Add(encounter);
        else { encounter.Gap(new("boundary-capacity", 0, Math.Max(0, time - encounter.StartedMs), new(1))); dropped++; }
    }
    public void Tick(DateTimeOffset at, double time)
    {
        lock (gate)
        {
            if (state != "observing" || !double.IsFinite(time) || time < mono) return;
            mono = time;
            foreach (var key in effects.Tick(mono)) MarkEffect(key);
            if (encounter is { Final: false, Manual: false, LastDamageMs: { } last } && mono - last >= settings.IdleTimeoutMs)
                CloseCore("idle-timeout", encounter.LastDamageAt!.Value, last);
            CloseCombatLeft(mono); ExpireWhole(mono); EvaluateTarget();
        }
    }
    public void StartManual(DateTimeOffset at, double time)
    {
        lock (gate)
        {
            if (state != "observing" || encounter is { Final: false }) throw new InvalidOperationException("观察未活动或当前战斗仍在进行");
            mono = Math.Max(mono, time); Begin(at, mono, true, "manual-start");
        }
    }
    /// <summary>手动拆段：结束进行中的分段，下一笔有效本机伤害开始 manual-split 分段。没有进行中的分段时不改变状态并返回 false。</summary>
    public bool Split(DateTimeOffset at, double time)
    {
        lock (gate)
        {
            if (state != "observing" || encounter is not { Final: false } current) return false;
            mono = Math.Max(mono, time);
            // 自动开始的分段与空闲/切图边界相同，截止最后有效伤害，不把拆段前的停手时间计入分母。
            if (current is { Manual: false, LastDamageMs: { } last }) CloseCore("manual-split", current.LastDamageAt!.Value, last);
            else CloseCore("manual-split", at, mono);
            splitPending = true;
            return true;
        }
    }
    public void Close(string reason, DateTimeOffset at, double time, bool partial = false)
    {
        lock (gate)
        {
            mono = Math.Max(mono, time);
            if (partial) MarkGap(reason, mono, null, 0);
            CloseCore(reason, at, mono);
        }
    }
    /// <summary>
    /// 重设：结束进行中的分段（reset）并立即开启新的一场，不建立空分段；进行中的定时测量取消且不保存。
    /// 下一个分段的开始原因为 reset。返回被结束的分段（没有进行中的分段时为空）。
    /// </summary>
    public CombatEncounter? Reset(DateTimeOffset at, double time)
    {
        lock (gate)
        {
            mono = Math.Max(mono, time);
            var closing = encounter is { Final: false } ? encounter : null;
            CloseCore("reset", at, mono);
            var result = closing?.Snapshot(new(revision), mono, settings.RollingWindowMs, Resources());
            encounter = null;
            if (measurementPending is not null || measurement is { Final: false }) CancelMeasurementCore();
            wholeOrdinal = checked(wholeOrdinal + 1); wholeExpired = null; lastActionMs = null;
            resetPending = true; splitPending = false; combatLeft = false; combatLeftAt = null; resetFloorMs = mono;
            boundaryRevision = checked(boundaryRevision + 1);
            return result;
        }
    }
    public void Stop(string reason, DateTimeOffset at, double time, bool partial = false)
    {
        lock (gate)
        {
            if (state != "observing") return;
            Close(reason, at, time, partial);
            // 观察停止时还在等待首次伤害的测量不保存；已开始的以停止时点结束并标为不完整。
            if (measurementPending is not null) CancelMeasurementCore();
            else if (measurement is { Final: false }) FinishMeasurementCore(at, "observation-stopped");
            state = partial ? "interrupted" : "stopped";
            boundaryRevision = checked(boundaryRevision + 1);
            effects.Gap(); foreach (var key in entities.Keys) MarkEffect(key);
            StaleGauges(reason);
        }
    }
    public void ReportGap(string reason, double fromMs, double? toMs, ulong lost)
    { lock (gate) MarkGap(reason, fromMs, toMs, lost); }
    private void MarkGap(string reason, double fromMs, double? toMs, ulong lost)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl)) reason = "observation-gap";
        fromMs = double.IsFinite(fromMs) ? Math.Max(0, Math.Min(fromMs, mono)) : mono;
        if (toMs is { } end && (!double.IsFinite(end) || end < fromMs)) toMs = mono;
        var gap = new CombatGap(reason, fromMs, toMs, new(lost)); dropped = checked(dropped + lost);
        if (reasons.Count < 128) reasons.Add(reason);
        gapRevision++;
        if (gaps.Count > 0 && gaps[^1].Reason == reason && gaps[^1].FromMs == fromMs && gaps[^1].ToMs == toMs)
        {
            gaps[^1] = gaps[^1] with { DroppedEvents = new(checked(gaps[^1].DroppedEvents.Value + lost)) };
            if (encounter is { Final: false } && encounter.Gaps.Count > 0 && encounter.Gaps[^1].Reason == reason)
                encounter.Gaps[^1] = encounter.Gaps[^1] with { DroppedEvents = new(checked(encounter.Gaps[^1].DroppedEvents.Value + lost)) };
            return;
        }
        if (gaps.Count < 256) gaps.Add(gap);
        else gaps[^1] = CombatGapAccumulator.Merge(gaps[^1], gap);
        effects.Gap(); foreach (var key in entities.Keys) MarkEffect(key);
        StaleGauges(reason);
        if (encounter is { Final: false })
        {
            var relative = gap with { FromMs = Math.Max(0, fromMs - encounter.StartedMs), ToMs = toMs is { } endMs ? Math.Max(0, endMs - encounter.StartedMs) : null };
            encounter.Gap(relative); Timeline("gap", null, null, relative, null);
        }
        if (measurement is { Final: false } window && (toMs ?? mono) >= window.StartedMs && fromMs <= measurementEndMs)
        {
            var relative = gap with { FromMs = Math.Max(0, fromMs - window.StartedMs), ToMs = toMs is { } endMs ? Math.Max(0, endMs - window.StartedMs) : null };
            window.Gap(relative); Timeline(window, "gap", null, null, relative, null);
        }
    }
    /// <summary>窗口内的切图或身份等待：测量继续计时，结果标为不完整；同一原因连续出现只记一次。</summary>
    private void MeasurementBoundary(string reason)
    {
        if (measurement is not { Final: false } window || mono < window.StartedMs || mono > measurementEndMs) return;
        if (window.Gaps.Count > 0 && window.Gaps[^1].Reason == reason) return;
        var offset = Math.Max(0, mono - window.StartedMs);
        var gap = new CombatGap(reason, offset, offset, new(0));
        window.Gap(gap); Timeline(window, "gap", null, null, gap, null);
    }
    private void StaleGauges(string reason)
    {
        foreach (var (key, entity) in entities.ToArray())
        {
            if (entity.BreakGauge is not { } gauge) continue;
            var stale = entity with { BreakGauge = gauge with { Stale = true, Reason = reason } };
            entities[key] = stale;
            if (stale.BreakGauge != gauge) Timeline("entity", null, null, null, null, TimelineEntity(stale));
        }
    }
    private void Timeline(string kind, CombatDamageEvent? damage, CombatEffectObservation? effect, CombatGap? gap, string? attribution,
        CombatEntity? entity = null, CombatHealEvent? heal = null)
    {
        if (encounter is null || encounter.Final) return;
        Timeline(encounter, kind, damage, effect, gap, attribution, entity, heal);
    }
    /// <summary>
    /// 效果与实体变化至多占单场条数的 2/5 与字节的 3/8，超出时只计截断数；伤害、治疗与缺口使用其余空间，
    /// 超出总配额时记 timeline-capacity 缺口（范围汇总据此标为不完整）。
    /// </summary>
    private void Timeline(EncounterState target, string kind, CombatDamageEvent? damage, CombatEffectObservation? effect, CombatGap? gap,
        string? attribution, CombatEntity? entity = null, CombatHealEvent? heal = null)
    {
        var at = damage?.ObservedAt ?? effect?.ObservedAt ?? heal?.ObservedAt ?? lastObserved ?? startedAt;
        var time = damage?.MonotonicMs ?? effect?.MonotonicMs ?? heal?.MonotonicMs ?? mono;
        var item = new CombatTimelineItem(new(checked(timelineIndex + 1)), Math.Max(0, time - target.StartedMs), at, kind, damage, effect, gap, attribution, entity, heal);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(item, ContractJson.Options); var bytes = encoded.Length;
        var effectKind = kind is "effect" or "effect-baseline" or "entity" or "entity-baseline";
        if (effectKind && (target.EffectTimelineCount >= (ulong)(limits.MaximumTimelineItems * 2 / 5) ||
            target.EffectTimelineBytes + (ulong)bytes > (ulong)(limits.MaximumTimelineBytes / 8 * 3)))
        { target.Truncated++; return; }
        if (target.TimelineCount >= (ulong)limits.MaximumTimelineItems || target.TimelineBytes + (ulong)bytes > (ulong)limits.MaximumTimelineBytes ||
            pendingTimeline.Count >= limits.MaximumTimelineItems || pendingBytes + bytes > limits.MaximumTimelineBytes)
        {
            target.Truncated++;
            if (!effectKind) target.Gap(new("timeline-capacity", item.OffsetMs, item.OffsetMs, new(0)));
            return;
        }
        timelineIndex++;
        pendingTimeline.Add(new(target.Id, item) { Encoded = encoded }); pendingBytes += bytes;
        target.TimelineCount++; target.TimelineBytes += (ulong)bytes;
        if (effectKind) { target.EffectTimelineCount++; target.EffectTimelineBytes += (ulong)bytes; }
    }
    public CombatCheckpoint Capture()
    {
        lock (gate)
        {
            var next = new UInt64String(checked(++revision)); var resources = Resources();
            CombatEncounter Snapshot(EncounterState value) => value.Snapshot(next, mono, settings.RollingWindowMs,
                resources with { TimelineItems = checked((int)value.TimelineCount), TimelineBytes = new(value.TimelineBytes), TruncatedTimelineItems = new(value.Truncated) });
            var changed = closed.Concat(encounter is null ? [] : new[] { encounter })
                .Concat(measurement is { Final: false } running ? new[] { running } : []).Distinct().Select(Snapshot).ToArray();
            var statusReasons = reasons.Union(awaitingReasons, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var status = new CombatObservationStatus(observationId, owner, settings.GameSessionId, state, source, local, settings, next,
                startedAt, lastObserved, mono, statusReasons.Length == 0 ? ObservationQuality.Confirmed : ObservationQuality.Partial,
                statusReasons, new(dropped), localCombatState, wholeOrdinal, wholeExpired);
            var workspace = new CombatWorkspace(status, encounter is null ? null : Snapshot(encounter),
                entities.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => EffectiveHealth(x) is var health && ReferenceEquals(health, x.Health) ? x : x with { Health = health }).ToArray(),
                settings.SelfEffectsEnabled ? effects.Snapshot(local, owner, observationId, mono, next) : null, next, new(boundaryRevision), CurrentTarget());
            // HP 与当前目标只随快照返回：只有它们变化时不算需要保存的变化。
            var shape = new PersistedShape(workspace.Entities.Select(x => x with { LastObservedAt = default, Health = null }).ToArray(),
                status with { Revision = default, LastObservedAt = null, MonotonicMs = 0 }, boundaryRevision, encounter?.Id, encounter?.Final, measurement?.Id, measurement?.Final);
            var material = pendingTimeline.Count > 0 || dirtyEffects.Count > 0 || gapRevision != acknowledgedGapRevision || deletedRecords.Count > 0 ||
                closed.Count > 0 || acknowledgedShape is null || !shape.Equals(acknowledgedShape);
            var urgent = closed.Count > 0 || deletedRecords.Count > 0 || encounter is { Final: false } || measurement is { Final: false } || state != "observing";
            return new(workspace, changed, dirtyEffects.Keys.Select(key => effects.Snapshot(key, owner, observationId, mono, next)).ToArray(),
                pendingTimeline.ToArray(), gaps.ToArray(), new(timelineIndex))
            { GapsChanged = gapRevision != acknowledgedGapRevision, GapRevision = gapRevision, DeletedEncounters = deletedRecords.ToArray(),
              Material = material, Urgent = urgent, Shape = shape, EffectWatermark = effectChanges };
        }
    }
    /// <summary>检查点中决定是否需要持久保存的部分：实体（不含观测时刻）与观察状态（不含修订、观测时刻与单调时点）及分段身份。</summary>
    internal sealed record PersistedShape(CombatEntity[] Entities, CombatObservationStatus Status, ulong BoundaryRevision,
        string? EncounterId, bool? EncounterFinal, string? MeasurementId, bool? MeasurementFinal)
    {
        public bool Equals(PersistedShape? other) => other is not null && Entities.AsSpan().SequenceEqual(other.Entities) &&
            StatusEquals(Status, other.Status) && BoundaryRevision == other.BoundaryRevision && EncounterId == other.EncounterId &&
            EncounterFinal == other.EncounterFinal && MeasurementId == other.MeasurementId && MeasurementFinal == other.MeasurementFinal;
        public override int GetHashCode() => HashCode.Combine(Entities.Length, BoundaryRevision, EncounterId);
        private static bool StatusEquals(CombatObservationStatus a, CombatObservationStatus b) =>
            a.State == b.State && a.LocalEntity == b.LocalEntity && a.Quality == b.Quality && a.DroppedEvents == b.DroppedEvents &&
            a.LocalCombatState == b.LocalCombatState && a.WholeOrdinal == b.WholeOrdinal && a.WholeExpired == b.WholeExpired && a.Reasons.SequenceEqual(b.Reasons, StringComparer.Ordinal);
    }
    private PersistedShape? acknowledgedShape;

    /// <summary>当前观测中实体的效果视图，按当前单调时点计算剩余时间；实体不在当前工作区时为空。</summary>
    public CombatEntityEffects? EntityEffects(string entityId)
    {
        lock (gate)
        {
            foreach (var (key, entity) in entities)
                if (entity.Id == entityId) return effects.Snapshot(key, owner, observationId, mono, new(revision));
            return null;
        }
    }
    public void Acknowledge(CombatCheckpoint checkpoint)
    {
        lock (gate)
        {
            RemovePending(x => x.Item.Index.Value <= checkpoint.TimelineWatermark.Value);
            foreach (var value in checkpoint.Encounters.Where(x => x.Summary.Final)) closed.RemoveAll(x => x.Id == value.Id);
            foreach (var (key, mark) in dirtyEffects.ToArray()) if (mark <= checkpoint.EffectWatermark) dirtyEffects.Remove(key);
            acknowledgedGapRevision = Math.Max(acknowledgedGapRevision, checkpoint.GapRevision);
            if (checkpoint.Shape is { } shape) acknowledgedShape = shape;
            deletedRecords.RemoveAll(checkpoint.DeletedEncounters.Contains);
        }
    }

    /// <summary>
    /// 开始定时测量：受理后等待本机第一次有效伤害（不早于 <paramref name="startMs"/>，本机或计入的召唤物造成的正数伤害），
    /// 窗口为 [首击时点, 首击时点 + durationMs]（观察单调时点）。同一时刻只能有一个测量；预备倒计时已停用，<paramref name="countdownMs"/> 只随结果原样记录。
    /// </summary>
    public string BeginMeasurement(DateTimeOffset startsAt, double startMs, double durationMs, double countdownMs)
    {
        lock (gate)
        {
            if (state != "observing") throw new InvalidOperationException("观察未活动");
            if (measurement is { Final: false } || measurementPending is not null) throw new InvalidOperationException("已有定时测量在进行");
            if (!double.IsFinite(startMs) || !(durationMs > 0) || !(countdownMs >= 0)) throw new ArgumentException("测量窗口无效");
            var id = encounterIdFactory();
            // 受理即推进边界修订（与开始、拆段等边界命令一致）；窗口在首击时建立时再推进一次。
            boundaryRevision = checked(boundaryRevision + 1);
            measurementPending = (id, startsAt, startMs);
            measurementEndMs = double.PositiveInfinity; measurementDurationMs = durationMs; measurementCountdownMs = countdownMs;
            return id;
        }
    }
    /// <summary>本机第一次有效伤害到达：以该击为窗口起点建立测量记录。</summary>
    private void StartMeasurementWindow(string id, DateTimeOffset at, double startMs)
    {
        var window = new EncounterState(id, owner, observationId, local,
            entities.GetValueOrDefault(local)?.DisplayName ?? local.RawInstanceId.ToString(), at, startMs, true,
            settings.IncludeOwnedEntities, limits, null, healing, trackRolling: false, useCompleteWindowDps: true)
        { Record = new("timed-measurement", null, null, null), Boundary = "measurement-running" };
        window.BoundaryRevision = new(checked(++boundaryRevision));
        measurement = window; measurementPending = null; measurementEndMs = startMs + measurementDurationMs;
        foreach (var gap in gaps.Where(x => x.ToMs is null)) window.Gap(gap with { FromMs = 0 });
    }
    /// <summary>尚未公开的测量候选；宿主在任务锁内持久提交开始回执，失败时撤销候选。</summary>
    public sealed class MeasurementStartCandidate
    {
        internal CombatSession Session { get; }
        internal EncounterState? Previous { get; }
        internal ulong BoundaryRevision { get; }
        internal double EndMs { get; }
        internal double DurationMs { get; }
        internal double CountdownMs { get; }
        public string Id { get; }
        internal MeasurementStartCandidate(CombatSession session, EncounterState? previous, ulong boundaryRevision,
            double endMs, double durationMs, double countdownMs, string id)
        {
            Session = session; Previous = previous; BoundaryRevision = boundaryRevision;
            EndMs = endMs; DurationMs = durationMs; CountdownMs = countdownMs; Id = id;
        }
    }
    public MeasurementStartCandidate BeginMeasurementCandidate(DateTimeOffset startsAt, double startMs, double durationMs, double countdownMs)
    {
        lock (gate)
        {
            var previous = measurement; var boundary = boundaryRevision;
            var end = measurementEndMs; var duration = measurementDurationMs; var countdown = measurementCountdownMs;
            var id = BeginMeasurement(startsAt, startMs, durationMs, countdownMs);
            return new(this, previous, boundary, end, duration, countdown, id);
        }
    }
    /// <summary>候选尚未进入检查点时恢复原状态，不登记取消记录。</summary>
    public void AbortMeasurementStart(MeasurementStartCandidate candidate)
    {
        lock (gate)
        {
            var currentId = measurementPending?.Id ?? (measurement is { Final: false } current ? current.Id : null);
            if (candidate.Session != this || currentId != candidate.Id || boundaryRevision != candidate.BoundaryRevision + 1 ||
                pendingTimeline.Any(x => x.EncounterId == candidate.Id))
                throw new InvalidOperationException("只能撤销尚未公开的测量候选");
            measurement = candidate.Previous; measurementPending = null; boundaryRevision = candidate.BoundaryRevision;
            measurementEndMs = candidate.EndMs; measurementDurationMs = candidate.DurationMs; measurementCountdownMs = candidate.CountdownMs;
        }
    }
    /// <summary>进行中测量的累计；等待首次伤害时 <see cref="CombatMeasurementProgress.Waiting"/> 为真；没有测量或已结束时为空。</summary>
    public CombatMeasurementProgress? MeasurementProgress()
    {
        lock (gate)
        {
            if (measurementPending is { } waiting)
                return new(waiting.Id, waiting.RequestedMs, waiting.RequestedMs, System.Numerics.BigInteger.Zero, null, null, false, [], Waiting: true);
            if (measurement is not { Final: false } window) return null;
            return new(window.Id, window.StartedMs, measurementEndMs, window.Attributed.Total, window.FirstDamageMs, window.LastDamageMs,
                window.Partial, window.Gaps.Select(x => x.Reason).Distinct(StringComparer.Ordinal).ToArray());
        }
    }
    /// <summary>按设定时长结束测量并转入历史；reason 非空时（例如观测停止）提前结束并标为不完整。</summary>
    public CombatEncounter? FinishMeasurement(DateTimeOffset at, string? reason = null)
    {
        lock (gate)
        {
            // 还在等待首次伤害：没有可保存的数据，提前结束时按取消处理。
            if (measurementPending is not null) { if (reason is not null) CancelMeasurementCore(); return null; }
            if (measurement is not { Final: false } window) return null;
            if (reason is not null && mono < window.StartedMs) { CancelMeasurementCore(); return null; }
            return FinishMeasurementCore(at, reason);
        }
    }
    /// <summary>取消测量（等待中或计时中）：不保存，已写入的未结束记录在下一个检查点删除。</summary>
    public bool CancelMeasurement()
    {
        lock (gate)
        {
            if (measurementPending is null && measurement is not { Final: false }) return false;
            CancelMeasurementCore(); return true;
        }
    }
    private void CancelMeasurementCore()
    {
        if (measurementPending is not null) { measurementPending = null; return; }
        var id = measurement!.Id;
        RemovePending(x => x.EncounterId == id);
        if (deletedRecords.Count < 64) deletedRecords.Add(id);
        measurement = null;
    }
    private CombatEncounter FinishMeasurementCore(DateTimeOffset at, string? reason)
    {
        var window = measurement!;
        var end = reason is null ? measurementEndMs : Math.Clamp(mono, window.StartedMs, measurementEndMs);
        if (reason is not null) window.Gap(new(reason, Math.Max(0, end - window.StartedMs), null, new(0)));
        var reasons = window.Gaps.Select(x => x.Reason).Distinct(StringComparer.Ordinal).ToArray();
        var result = new CombatMeasurementResult("timed-measurement-v1", measurementDurationMs, measurementCountdownMs, new(window.Attributed.Total),
            CombatRate.Calculate(window.Attributed.Total, measurementDurationMs),
            window.FirstDamageMs is { } first ? first - window.StartedMs : null, window.LastDamageMs is { } last ? last - window.StartedMs : null,
            window is { FirstDamageMs: { } a, LastDamageMs: { } b } ? b - a : null, !window.Partial, reasons,
            window.StartedAt, window.StartedAt.AddMilliseconds(end - window.StartedMs));
        window.Record = new("timed-measurement", null, result, null);
        window.BoundaryRevision = new(checked(++boundaryRevision));
        window.EndedMs = end; window.EndedAt = result.EndedAt; window.Boundary = reason ?? "measurement-complete";
        if (closed.Count < 128) closed.Add(window);
        measurement = null;
        return window.Snapshot(new(revision), mono, settings.RollingWindowMs, Resources());
    }
}
