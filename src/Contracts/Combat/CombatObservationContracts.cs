using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public enum CombatEntityRole { LocalPlayer, Player, Owned, Monster, Unknown }
public enum CombatDamageKind { ActualHp, Shield, Overkill, Unclassified }
public enum CombatDamageClassification { Direct, Indirect, DamageOverTime, Unknown }
/// <summary>客户端 HitFlags 的元素属性位（Elemental*）。</summary>
public enum CombatDamageElement { Bleed, Dark, Fire, Holy, Ice, Lightning, Poison, Soul }
public enum CombatEffectChangeKind { Apply, Refresh, StackChanged, Remove }

/// <summary>
/// 怪物、技能或效果的游戏原文名称。Kind 为 monster、skill、effect；SheetId 为客户端表的原始 ID（十进制）。
/// State：resolved 时 Text 为当前客户端表的显示名称；unknown 为表中没有该 ID 或名称为空；unavailable 为名称表未读取或读取失败，
/// Reason 说明原因（例如 combat.nameNotFound、combat.nameCatalogLoading、combat.nameCatalogUnavailable）。Text 为空时不得以 ID 代替名称显示。
/// </summary>
public sealed record CombatResolvedName(string Kind, UInt64String SheetId, string? Text, string State, string? Reason = null);
public sealed record CombatBreakGauge(double? Current, UInt64String? Maximum, DateTimeOffset ObservedAt,
    OpenEnum<ObservationQuality> Quality, bool Stale = false, string? Reason = null);
public sealed record CombatEntity(string Id, EntityKey Key, string RawName, string DisplayName,
    OpenEnum<CombatEntityRole> Role, EntityKey? OwnerKey, bool OwnerConfirmed, bool InScope,
    DateTimeOffset LastObservedAt, OpenEnum<ObservationQuality> Quality,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatBreakGauge? BreakGauge = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatResolvedName? ResolvedName = null);
public sealed record CombatEntityObservation(EntityKey Key, string RawName, string DisplayName,
    OpenEnum<CombatEntityRole> Role, EntityKey? OwnerKey, bool OwnerConfirmed, bool InScope,
    OpenEnum<ObservationQuality> Quality,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatBreakGauge? BreakGauge = null);
/// <summary>
/// 单条伤害事件。BreakedHit 为游戏内的“无防备”（命中处于破防状态的目标），BreakOccurred 为“破防”（本击造成破防），
/// MultiHit 为“多重打擊”，AdditionalHit 为“追加打擊”（HitFlags 0x800）。
/// </summary>
public sealed record CombatDamageEvent(string EventId, ObservationSource Source, EntityKey SourceEntity,
    EntityKey TargetEntity, UInt64String SkillId, UInt64String Amount, OpenEnum<CombatDamageKind> AmountKind,
    DateTimeOffset ObservedAt, double MonotonicMs, UInt64String? Sequence, int SubEventIndex,
    bool? Critical, bool? Hit, OpenEnum<ObservationQuality> Quality,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntityKey? SkillCasterKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SkillResolved = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Chain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PowerfulHit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RapidHit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DoubleHit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpenEnum<CombatDamageClassification>? Classification = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BreakedHit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BreakOccurred = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FirstAttack = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DefaultAttack = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MultiHit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OpenEnum<CombatDamageElement>>? Elements = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AdditionalHit = null);
public sealed record CombatEffectObservation(OpenEnum<CombatEffectChangeKind> Kind, EntityKey EntityKey,
    UInt64String EffectId, UInt64String InstanceId, EntityKey? CasterKey, OpenEnum<EffectDurationKind> DurationKind,
    double? DurationMs, int? Stacks, string? Category, bool Aggregated, DateTimeOffset ObservedAt,
    double MonotonicMs, OpenEnum<ObservationQuality> Quality);
public sealed record CombatEffectReconciliation(EntityKey EntityKey, bool Complete,
    IReadOnlyList<CombatEffectObservation> Effects);
public sealed record CombatGapInput(string Reason, double FromMs, double? ToMs, UInt64String DroppedEvents);
/// <summary>治疗方向：cast 为本机施放（带目标），received 为本机受到（协议不带来源）。</summary>
public enum CombatHealDirection { Cast, Received }
/// <summary>
/// 单条治疗。Amount 为含过量的毛治疗量；EffectiveAmount 与 Overheal 只在受疗与紧邻的本机 HP 上升配对时给出，
/// 否则为空并以 EffectiveReason 说明。ReceiptMerged 表示本机对自己的施放已与同次受疗合并，受疗不再单列。
/// Flags 为客户端 HitFlags（十进制）。
/// </summary>
public sealed record CombatHealEvent(string EventId, OpenEnum<CombatHealDirection> Direction, EntityKey? TargetEntity,
    UInt64String Flags, UInt64String Amount, UInt64String? EffectiveAmount, UInt64String? Overheal, bool ReceiptMerged,
    DateTimeOffset ObservedAt, double MonotonicMs, UInt64String? Sequence, OpenEnum<ObservationQuality> Quality,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveReason = null);
/// <summary>
/// 客户端 <c>Shared.GameLogic.ChannelObjects.Actors.CombatState</c>：0 NonCombat、1 CombatReady、2 InCombat、3 CombatEnd。
/// 序列化为 non-combat、combat-ready、in-combat、combat-end；来源出现其他取值时原样保留为 unknown-&lt;值&gt;。
/// </summary>
public enum CombatLocalState { NonCombat, CombatReady, InCombat, CombatEnd }
/// <summary>本机实体的一次战斗状态变化（170196），Sequence 为流偏移，与伤害事件同一序列，用于同一时点的先后。</summary>
public sealed record CombatStateChange(OpenEnum<CombatLocalState> State, DateTimeOffset ObservedAt, double MonotonicMs, UInt64String? Sequence);
public sealed record CombatFrame(int SchemaVersion, ObservationSource Source, string GameSessionId,
    UInt64String Generation, UInt64String Sequence, DateTimeOffset ObservedAt, double MonotonicMs,
    IReadOnlyList<CombatEntityObservation> Entities, IReadOnlyList<CombatDamageEvent> Damage,
    IReadOnlyList<CombatEffectObservation> Effects, IReadOnlyList<CombatEffectReconciliation> Reconciliations,
    IReadOnlyList<CombatGapInput> Gaps, bool ObservationResumed = false, bool EncounterEnded = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CombatHealEvent>? Healing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CombatStateChange>? CombatStates = null);

public sealed record CombatObservationRequest(string GameSessionId, bool DamageEnabled = true,
    bool SelfEffectsEnabled = true, bool MonsterEffectsEnabled = true, bool IncludeOwnedEntities = true,
    int RollingWindowMs = 60000, int IdleTimeoutMs = 300000, string? ReplayScenarioId = null, int ReplayIntervalMs = 250);
public sealed record CombatCapability(string Id, OpenEnum<CapabilityStatus> Status, ObservationSource? Source,
    string? Reason, UInt64String? Generation, DateTimeOffset? LastObservedAt);
public sealed record CombatCapabilities(bool Simulation, IReadOnlyList<CombatCapability> Capabilities);
/// <summary>LocalCombatState 为本机当前战斗状态（来源 170196）；来源未提供、旧配置或换连接后尚未收到时为空。</summary>
public sealed record CombatObservationStatus(string Id, string Owner, string GameSessionId, string State,
    ObservationSource Source, EntityKey LocalEntity, CombatObservationRequest Settings, UInt64String Revision,
    DateTimeOffset StartedAt, DateTimeOffset? LastObservedAt, double MonotonicMs,
    OpenEnum<ObservationQuality> Quality, IReadOnlyList<string> Reasons, UInt64String DroppedEvents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpenEnum<CombatLocalState>? LocalCombatState = null);
public sealed record CombatDamageTotals(DamageAmountString ActualHp, DamageAmountString Shield,
    DamageAmountString Overkill, DamageAmountString Unclassified);
public sealed record CombatDamageClassificationCounts(UInt64String Direct, UInt64String Indirect,
    UInt64String DamageOverTime, UInt64String Unknown);
/// <summary>
/// 技能或目标分组统计。Name 保留分组的原始标识（技能为技能 ID，目标为观测显示名）；ResolvedName 为技能或怪物目标的游戏原文名称，
/// 玩家与 other-* 分组为空。breakedHitCount 为“无防备”次数，breakOccurredCount 为“破防”次数；两者与首击、普通攻击、多段及元素次数
/// 统计全部已归属伤害（直接、间接、持续），分母为 eventCount；AdditionalHitCount（“追加打擊”）同此口径，旧历史为空。FirstDamageAt／LastDamageAt 为本组第一次与最近一次计入伤害的观测时刻
/// （目标分组即该目标在本段或范围内首次出现与最近受击的时刻），旧历史为空。
/// </summary>
public sealed record CombatDamageGroup(string Key, string Name, CombatDamageTotals Amounts,
    DamageAmountString AttributedDamage, UInt64String EventCount, UInt64String? HitCount, UInt64String? CriticalCount,
    UInt64String? PowerfulHitCount = null, UInt64String? RapidHitCount = null, UInt64String? DoubleHitCount = null,
    UInt64String? MinimumDamage = null, UInt64String? MaximumDamage = null,
    CombatDamageClassificationCounts? ClassificationCounts = null,
    UInt64String? CriticalEligibleCount = null, UInt64String? MainHitEligibleCount = null,
    UInt64String? BreakedHitCount = null, UInt64String? BreakOccurredCount = null, UInt64String? FirstAttackCount = null,
    UInt64String? DefaultAttackCount = null, UInt64String? MultiHitCount = null,
    CombatDamageElementCounts? ElementCounts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DamageAmountString? BreakedHitDamage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatResolvedName? ResolvedName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? FirstDamageAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastDamageAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatDamageTagAmounts? TagDamage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UInt64String? AdditionalHitCount = null);
/// <summary>
/// 带各标签的伤害合计（与对应次数同一口径：暴击只计直接与间接伤害，强击、连击、追击只计直接伤害，其余计全部已归属伤害）。
/// 一条伤害可同时带多个标签，各项独立累计、可重叠，不改变总伤害；任一事件该标记未知时对应金额为空。旧记录没有该块。
/// 无防备的伤害合计沿用分组的 BreakedHitDamage。
/// </summary>
public sealed record CombatDamageTagAmounts(DamageAmountString? Critical, DamageAmountString? PowerfulHit, DamageAmountString? RapidHit,
    DamageAmountString? DoubleHit, DamageAmountString? BreakOccurred, DamageAmountString? FirstAttack, DamageAmountString? DefaultAttack,
    DamageAmountString? MultiHit, CombatDamageElementAmounts? Elements, DamageAmountString? AdditionalHit = null);
/// <summary>带各元素属性的伤害合计；同一条伤害带多个元素时分别计入。</summary>
public sealed record CombatDamageElementAmounts(DamageAmountString Bleed, DamageAmountString Dark, DamageAmountString Fire, DamageAmountString Holy,
    DamageAmountString Ice, DamageAmountString Lightning, DamageAmountString Poison, DamageAmountString Soul);
public sealed record CombatDamageElementCounts(UInt64String Bleed, UInt64String Dark, UInt64String Fire, UInt64String Holy,
    UInt64String Ice, UInt64String Lightning, UInt64String Poison, UInt64String Soul);
public sealed record CombatGap(string Reason, double FromMs, double? ToMs, UInt64String DroppedEvents);
public sealed record CombatResourceUsage(int Entities, int Effects, int DeduplicationEntries, int TimelineItems,
    UInt64String CacheBytes, UInt64String TimelineBytes, UInt64String DroppedEvents, UInt64String TruncatedTimelineItems);
/// <summary>按目标的施放治疗分组；Key 为实体 ID（超出上限时为 other-targets），Self 表示目标为本机。</summary>
public sealed record CombatHealingTarget(string Key, string Name, DamageAmountString Amount, UInt64String Count, bool Self);
/// <summary>
/// 分段治疗统计（口径 observed-healing-v1）。受到的有效量与过量只在本段全部受疗都与 HP 上升配对时返回，否则为空并附 EffectiveReason；
/// 每秒治疗量按施放量计算：EncounterHps = CastAmount ÷ HpsDurationMs（分段为实际分段时长，范围汇总为范围时长，定时测量为设定时长）。
/// Targets 只在分段及历史中返回，摘要事件为空。
/// </summary>
public sealed record CombatHealingSummary(string CalculationVersion,
    DamageAmountString CastAmount, UInt64String CastCount, DamageAmountString CastSelfAmount, DamageAmountString CastOtherAmount,
    DamageAmountString ReceivedAmount, UInt64String ReceivedCount, DamageAmountString ReceivedSelfAmount, DamageAmountString ReceivedOtherAmount,
    DamageAmountString? ReceivedEffective, DamageAmountString? ReceivedOverheal, UInt64String PairedReceivedCount, string? EffectiveReason,
    double? EncounterHps, double? RollingHps, IReadOnlyList<string> Reasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CombatHealingTarget>? Targets = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? HpsDurationMs = null);
public sealed record CombatEncounter(string Id, string Owner, string ObservationId, string GameSessionId,
    UInt64String Generation, UInt64String Revision, CombatSummary Summary, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, double StartedMonotonicMs, double? EndedMonotonicMs, bool IncludeOwnedEntities,
    CombatDamageTotals Amounts, CombatDamageTotals LocalDamage, CombatDamageTotals OwnedDamage,
    CombatDamageTotals UnknownDamage, IReadOnlyList<CombatDamageGroup> Skills, IReadOnlyList<CombatDamageGroup> Targets,
    IReadOnlyList<CombatGap> Gaps, CombatResourceUsage Resources, bool Favorite,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UInt64String? BoundaryRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatSegment? Segment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatHealingSummary? Healing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatRecordInfo? Record = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatBreakSummary? Break = null);
/// <summary>
/// 不是自动分段的历史记录：<c>timed-measurement</c> 为定时测量的结果，<c>saved-range</c> 为“保存记录”按范围存下的副本。
/// 自动分段与旧历史为空。Name 为用户起的名称；Source 记录保存时的来源范围。
/// </summary>
public sealed record CombatRecordInfo(string Kind, string? Name, CombatMeasurementResult? Measurement, CombatRangeSource? Source);
/// <summary>保存记录的来源：一个或多个战斗记录及其中的时间范围（相对各记录开始的毫秒）。</summary>
public sealed record CombatRangeSource(string Scope, IReadOnlyList<string> EncounterIds, double? FromMs, double? ToMs);
/// <summary>
/// 定时测量结果（口径 timed-measurement-v1）。Dps 为总伤害除以设定时长；ActiveDurationMs 为窗口内第一击到最后一击。
/// Complete 为假时 Reasons 说明原因（切图、抓包缺口、观测停止等），数值仍按已观测伤害给出。
/// </summary>
public sealed record CombatMeasurementResult(string CalculationVersion, double DurationMs, double CountdownMs, DamageAmountString Damage,
    double? Dps, double? FirstDamageMs, double? LastDamageMs, double? ActiveDurationMs, bool Complete, IReadOnlyList<string> Reasons,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt);
/// <summary>战斗分段：观察内从 1 开始的序号及开始原因（first-damage、combat-entered、manual-start、manual-split、reset、map-change）。旧历史为空。</summary>
public sealed record CombatSegment(int Ordinal, string StartReason);
/// <summary>
/// 实体的一个当前效果。Polarity 为客户端 Buff 表 AffectType 给出的极性：buff（增益）、debuff（减益）、none（表里标为无）；
/// 名称表未读取、表里没有该效果或列结构不符时为空，前端单列为“未分类”，不推测。
/// </summary>
public sealed record CombatEffectView(EffectInstance Instance, double? RemainingMs,
    DateTimeOffset LastObservedAt, bool Aggregated, bool Stale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatResolvedName? ResolvedName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Polarity = null);
public sealed record CombatEntityEffects(string Id, string Owner, string ObservationId, UInt64String Revision,
    EffectsSnapshot Snapshot, IReadOnlyList<CombatEffectView> Views);
public sealed record CombatStorageSummary(int Encounters, UInt64String Bytes, int Favorites,
    UInt64String ProtectedBytes, bool OverBudget, int RemovedEncounters, UInt64String RemovedBytes);
public sealed record CombatWorkspace(CombatObservationStatus Observation, CombatEncounter? Encounter,
    IReadOnlyList<CombatEntity> Entities, CombatEntityEffects? SelfEffects, UInt64String Revision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UInt64String? BoundaryRevision = null);
public sealed record CombatSnapshot(string BackendEpoch, string EventCursor, UInt64String ResourceRevision,
    CombatWorkspace? Data, CombatStorageSummary Storage);
public sealed record CombatEncounterQuery(int PageSize = 25, string? Cursor = null,
    string? GameSessionId = null, bool FavoritesOnly = false);
public sealed record CombatEncounterPage(IReadOnlyList<CombatEncounter> Items, string? NextCursor, UInt64String Revision);
public sealed record CombatTimelineItem(UInt64String Index, double OffsetMs, DateTimeOffset ObservedAt,
    string Kind, CombatDamageEvent? Damage, CombatEffectObservation? Effect, CombatGap? Gap, string? Attribution, CombatEntity? Entity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatHealEvent? Heal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatResolvedName? SkillName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatResolvedName? EffectName = null);
public sealed record CombatTimelineQuery(int PageSize = 200, string? Cursor = null, double? FromMs = null,
    double? ToMs = null, int? BucketWidthMs = null, string? EntityId = null);
public sealed record CombatDamageBucket(double FromMs, double ToMs, CombatDamageTotals Amounts, UInt64String EventCount);
public sealed record CombatEffectCoverage(string EntityId, UInt64String EffectId, double FromMs, double ToMs,
    double CoveredMs, double ObservableMs, double? Ratio, IReadOnlyList<CombatGap> Gaps);
public sealed record CombatTimelinePage(string EncounterId, UInt64String Revision, IReadOnlyList<CombatTimelineItem> Items,
    string? NextCursor, IReadOnlyList<CombatDamageBucket> DamageBuckets, IReadOnlyList<CombatEffectCoverage> EffectCoverage,
    IReadOnlyList<CombatGap> Gaps, bool Truncated);
public sealed record CombatBoundaryRequest(string ObservationId);
public sealed record CombatFavoriteRequest(bool Favorite);
public sealed record CombatEncounterClosed(string Owner, string ObservationId, string EncounterId,
    UInt64String Revision, CombatSummary Summary, string BoundaryReason, bool Deleted = false);
public sealed record CombatObservationDegraded(string Owner, string ObservationId, string GameSessionId,
    UInt64String Generation, UInt64String Revision, IReadOnlyList<CombatGap> Gaps, UInt64String DroppedEvents, bool SnapshotRequired = false);

/// <summary>
/// 按“时间范围 × 目标”重新统计的查询。Scope 为 <c>encounter</c>（一个战斗记录，含定时测量与保存的记录）或
/// <c>observation</c>（一次观察里自最近一次重设起的全部自动分段）。FromMs/ToMs 相对记录开始，省略为整段；TargetId 为目标实体 ID。
/// </summary>
public sealed record CombatRangeQuery(string Scope, string? EncounterId = null, string? ObservationId = null,
    double? FromMs = null, double? ToMs = null, string? TargetId = null);
/// <summary>
/// 破防期间（命中带无防备标记的伤害）的累计与区间。区间按目标划分：本机造成破防的一击开始，目标破防值归零结束；
/// DurationMs 为各区间并集长度，Intervals 为区间个数，TimeShare 为并集长度占范围时长的比例，Dps 为无防备伤害 ÷ 并集长度。
/// Reasons：break-start-estimated、break-end-estimated（起点或终点按无防备命中估计）、break-flags-unknown（标记未知，区间项为空）。
/// </summary>
public sealed record CombatBreakSummary(DamageAmountString? Damage, UInt64String? Count, DamageAmountString? AverageDamage,
    double? DurationMs, UInt64String? Intervals, double? Dps, double? TimeShare, IReadOnlyList<string> Reasons);
/// <summary>
/// 范围汇总（口径 observed-range-v1）。Result 与战斗记录同形，由后端用保留的事件重新统计（目标筛选只作用于伤害）；
/// RangeTargets 为范围内全部目标（不受目标筛选影响），供目标标签使用。超出事件保留范围或范围内有观测缺口时 Complete 为假，
/// Reasons 写明原因；DPS 仍按保留的事件计算并随不完整标注返回。
/// </summary>
public sealed record CombatRangeSummary(string CalculationVersion, string Scope, string? ObservationId, IReadOnlyList<string> EncounterIds,
    double FromMs, double ToMs, string? TargetId, string? TargetName, CombatEncounter Result, CombatDamageGroup Totals,
    IReadOnlyList<CombatDamageGroup> RangeTargets, DamageAmountString RangeDamage, double? FirstDamageMs, double? LastDamageMs,
    CombatBreakSummary Break, bool Complete, IReadOnlyList<string> Reasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatResolvedName? TargetResolvedName = null);
/// <summary>把一个范围存成新的战斗历史记录：名称 1–60 字，记录自动收藏（不被清理）。</summary>
public sealed record CombatRangeSaveRequest(CombatRangeQuery Range, string Name);
/// <summary>
/// 常驻自动观测：Enabled 为后端偏好（默认开启）；State 为 off（未运行，Reason 说明原因，例如抓包未运行、未绑定窗口或已关闭）、
/// waiting（观测已开始，等待识别本机角色）、observing（正在统计）或 failed（开始失败，Reason 为错误码，稍后自动重试）。
/// Revision 仅在开关保存时变化，对应开关偏好的强 ETag。
/// </summary>
public sealed record CombatAutoObservationState(bool Enabled, string State, string? Reason, string? ObservationId,
    UInt64String Revision);
public sealed record CombatAutoObservationRequest(bool Enabled);
/// <summary>
/// 定时测量设定：时长 5,000–3,600,000 ms。预备倒计时已停用（测量从本机第一次有效伤害起计时）：CountdownMs 仍按 0–60,000 校验以兼容旧客户端，
/// 后端一律按 0 保存与返回。
/// </summary>
public sealed record CombatMeasurementSettings(int DurationMs = 60000, int CountdownMs = 0);
public sealed record CombatMeasurementStartRequest(string ObservationId, int DurationMs, int CountdownMs);
public sealed record CombatMeasurementCancelRequest(string MeasurementId);
/// <summary>
/// 定时测量状态。Phase 为 idle、waiting（已受理，等待本机第一次有效伤害）、running（从首击起计时）；旧版本的 countdown 不再出现。
/// Available 为假时 UnavailableReason 说明为何不能开始（例如没有进行中的 DPS 观测）。
/// RemainingMs 为计时中距结束的剩余，以 Revision 对应的后端单调时刻计，等待中为空；Damage 与 CurrentDps 为计时中的累计。
/// LastResult 为最近一次完成的测量（取消的不保存）；SettingsRevision 仅在设定保存时变化，对应设定的强 ETag。
/// </summary>
public sealed record CombatMeasurementState(string Phase, CombatMeasurementSettings Settings, UInt64String Revision, bool Available,
    string? UnavailableReason, string? ObservationId, string? MeasurementId, double? RemainingMs, double? ElapsedMs,
    DamageAmountString? Damage, double? CurrentDps, bool? Complete, IReadOnlyList<string> Reasons, CombatMeasurementResult? LastResult,
    string? LastResultId, UInt64String SettingsRevision);
