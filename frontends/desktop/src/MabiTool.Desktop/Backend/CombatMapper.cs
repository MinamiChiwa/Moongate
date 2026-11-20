using System.Globalization;
using System.Numerics;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Combat;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>映射战斗页所需的后端状态。全部字段原样来自后端，这里只做排版，不重新计算统计口径。</summary>
/// <param name="FirstAttacks">本场时间线里标为首击的已归属伤害事件（页面只显示最近的一次）；未知或查不到时为空。</param>
/// <param name="Scope">统计范围：observation（整场）、segment（当前分段）或 measurement:&lt;记录&gt;。</param>
/// <param name="RangeTarget">“选择目标”里选中的目标实体；为空表示全部。</param>
/// <param name="Range">后端按范围与目标重新统计的结果；当前分段且不选目标时不查询，直接用分段摘要。</param>
/// <param name="RangeProblem">范围查询被拒绝时的错误码。</param>
/// <param name="Measurement">定时测量状态；<paramref name="MeasurementAt"/> 是读取时的 Stopwatch 时刻。</param>
/// <param name="TargetEffectsAt">读取怪物效果的本机 Stopwatch 时刻；怪物效果与快照分开读取，剩余时间各按自己的读取时刻递减。为 0 时沿用 <paramref name="EffectsAt"/>。</param>
/// <param name="TargetEffects">怪物效果区当前效果目标（<see cref="CombatMapper.EffectTargetId"/>）的效果；没有或读取失败时为空。</param>
/// <param name="EffectProblem">读取效果目标的效果被拒绝时的错误码（例如 combat.entityOutOfScope）。</param>
/// <param name="HealthUnavailable">能力 combat.health.monster 不可用时的说明（“暂不可用：原因”）；可用或未知时为空。</param>
public sealed record CombatBackendState(bool Connected, bool Bound, CombatWorkspace? Workspace,
    IReadOnlyList<CombatEncounter> History, CombatEntityEffects? TargetEffects, string? EffectProblem,
    IReadOnlyList<CombatEffectCoverage> Coverage, IReadOnlyList<CombatDamageEvent>? FirstAttacks = null,
    string Scope = "segment", string? RangeTarget = null, CombatRangeSummary? Range = null, string? RangeProblem = null,
    CombatMeasurementState? Measurement = null, long MeasurementAt = 0, long EffectsAt = 0, long TargetEffectsAt = 0,
    string? HealthUnavailable = null);

/// <summary>记录明细窗口的后端状态：记录文档、窗口里的目标筛选、按该记录读取的范围汇总及读取失败的错误码。</summary>
/// <param name="Loaded">这条记录已读取过一次（成功或得到错误码）。</param>
/// <param name="Unsupported">后端不提供记录明细。</param>
public sealed record CombatRecordState(bool Connected, string Id, CombatEncounter? Record, string? Target, CombatRangeSummary? Range,
    string? Problem, bool Loaded = true, bool Unsupported = false);

/// <summary>
/// 把后端战斗契约排成页面数据。数值、比率分母和可用性都取自契约字段；
/// 契约为空的项目显示「不适用」或「未知」，不以零代替。
/// </summary>
public static class CombatMapper
{
    private const string Dash = "—";

    // 连接变化后等待识别本机身份期间，观察状态里的原因码。
    private static readonly HashSet<string> WaitingReasons =
    [
        "combat.streamGap.connectionClosed", "combat.streamGap.connectionRemoved", "combat.streamGap.connectionSyn",
        "combat.streamGap.connectionReset", "identity-unresolved", "observation-reconnected"
    ];

    /// <summary>“正在攻击”的时间窗口：最近一次有效伤害距观测的最近时刻不超过这么久，其目标在所有怪物列表中置顶。</summary>
    public static readonly TimeSpan AttackWindow = TimeSpan.FromSeconds(10);

    // 本次映射里目标分组键（实体 ID）到实体原始编号的对照：未解析的目标名称带实体编号，同名的未知个体分得开。
    [ThreadStatic] private static Dictionary<string, string>? targetNumbers;

    public static CombatPageData Map(CombatBackendState state)
    {
        var previous = targetNumbers;
        targetNumbers = (state.Workspace?.Entities ?? []).GroupBy(entity => entity.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key.RawInstanceId.ToString(), StringComparer.Ordinal);
        try { return MapCore(state); }
        finally { targetNumbers = previous; }
    }

    private static CombatPageData MapCore(CombatBackendState state)
    {
        var workspace = state.Workspace;
        var encounter = workspace?.Encounter;
        var observing = workspace?.Observation.State == "observing";
        var entities = workspace?.Entities ?? [];
        var monsters = entities.Where(entity => entity.Role.KnownValue == CombatEntityRole.Monster).ToList();
        var local = workspace?.Observation.LocalEntity;

        var scope = ScopeKind(state.Scope);
        // 当前分段且不选目标时直接用分段摘要；其余范围与目标筛选由后端重新统计。
        var direct = scope == CombatScopeKind.Segment && state.RangeTarget is null;
        var shown = direct ? encounter : state.Range?.Result;
        // 预备中或计时中、页面正显示的测量还没有结果：没有伤害时整页按“暂无数据”显示，不显示 0 或上一次的数值。
        var pending = PendingMeasurement(state, scope);
        if (pending is not null && (shown is null || shown.Summary.AttributedDamage.Value.IsZero)) shown = null;
        // 选中目标时主数据按该目标统计，测量的整段 DPS 不适用。
        var measured = scope == CombatScopeKind.Measurement && state.RangeTarget is null ? shown?.Record?.Measurement : null;
        var ordered = OrderTargets(ShownTargets(workspace, state.Scope, state.RangeTarget, state.Range), Now(workspace));
        var effectTarget = EffectTargetId(workspace, state.Scope, state.RangeTarget, state.Range);
        var selected = monsters.FirstOrDefault(entity => entity.Id == effectTarget);
        List<CombatDamageGroup> groups = shown is null ? [] : [.. shown.Skills, .. shown.Targets];
        return new(
            Status(state, observing),
            Kpis(workspace, shown, direct, measured, state.Range, state.RangeTarget is not null),
            shown is null ? [] : Coverage(shown.Skills),
            shown is null ? [] : Attribution(shown),
            shown is null ? string.Empty : Pick("连携伤害保留在时间轴，不计入统计", "連攜傷害保留在時間軸，不計入統計"),
            shown is null ? [] : Rows(shown.Skills, shown.Summary.AttributedDamage.Value, targets: false),
            shown is null ? [] : Rows(OrderTargets(shown.Targets, Now(workspace)), shown.Summary.AttributedDamage.Value, targets: true, keepOrder: true),
            Effects(workspace?.SelfEffects, entities, local, state.Coverage, EffectsAt(state), "self"),
            EffectChips(ordered, monsters, effectTarget),
            Effects(selected is not null && state.TargetEffects?.Snapshot.EntityKey == selected.Key ? state.TargetEffects : null,
                entities, local, state.Coverage, state.TargetEffectsAt != 0 ? state.TargetEffectsAt : EffectsAt(state), selected?.Id ?? string.Empty),
            state.History.Select(History).ToList(),
            new(state.Connected && state.Bound && !observing, observing, observing && encounter is { Summary.Final: false },
                observing && encounter is { Summary.Final: false }, observing && encounter is not null, state.Connected),
            shown is null ? [] : Traits(shown.Skills),
            shown is null ? null : ElementTotals(shown.Skills),
            groups.Any(HasTraitData),
            shown is null ? null : LatestFirstAttack(state.FirstAttacks, shown, entities,
                state.RangeTarget is null ? null : RangeTargetName(state)),
            shown is null ? null : Healing(shown, state.RangeTarget is not null),
            shown is null ? [] : HealingTargets(shown),
            shown is null ? [] : Classes(shown.Skills),
            Measurement(state),
            Location(state, observing),
            ScopeOf(state, scope, shown, pending),
            RangeTargets(state, direct ? encounter : null, ordered),
            TargetDetail(state, shown),
            shown is null ? null : Break(shown, state.Range, direct),
            shown is not null && (direct ? encounter is not null : state.Range is not null),
            shown is null ? Pick("当前范围没有数据", "目前範圍沒有資料") : string.Empty,
            DefaultRecordName(scope, shown))
        {
            TargetEffectsNote = EffectNote(state, ordered, effectTarget, selected),
            TagIds = ShownTagIds(groups),
            Health = observing ? Health(workspace!.CurrentTarget, state.HealthUnavailable) : null,
            HealthIdle = observing && state.HealthUnavailable is null && workspace!.CurrentTarget is null
        };
    }

    /// <summary>
    /// 当前攻击目标的血量：目标、切换与清空都取后端 currentTarget，这里只排版。能力停用时只给停用说明；没有当前目标时为空。
    /// 百分比与槽的比例按 BigInteger 计算（Int64 范围不经 double 除法）：百分比为 当前×1000/最大 四舍五入到千分位，写成一位小数；
    /// 未读取、最大值未知与血量由首领体现时不给比例，不写 0。
    /// </summary>
    public static TargetHealthView? Health(CombatCurrentTarget? target, string? unavailable)
    {
        if (unavailable is not null)
            return TargetHealthView.Unavailable(Localization.CodeText.Capability(CapabilityIds.MonsterHealth) + unavailable);
        if (target is null) return null;
        var name = CombatNames.Of(target.ResolvedName, CombatNameKind.Target, target.DisplayName, target.EntityId,
            target.Key.RawInstanceId.ToString());
        var health = target.Health;
        var dead = target.Dead == true;
        BigInteger? current = health?.Current?.Value, maximum = health?.Maximum is { Value: > 0 } max ? max.Value : null;
        TargetHealthView View(string percent, string value, double fill, HealthTone tone, string tip = "") =>
            new(target.EntityId, name.Text, name.Tip, percent, value, fill, tone)
            {
                Boss = target.IsBoss == true, Redirected = target.RedirectedFrom is not null, Stale = health?.Stale == true, Tip = tip
            };
        var reason = health?.Reason is { Length: > 0 } code ? HealthReason(code) : string.Empty;
        if (dead && maximum is { } top) return View(PercentTenths(0), $"0 / {Number(top)}", 0, HealthTone.Low, reason);
        if (dead) return View(PercentTenths(0), "0", 0, HealthTone.Low, reason);
        if (health is { State: "shared" })
            return View(Dash, Pick("血量由首领体现", "血量由首領體現"), 0, HealthTone.Empty, HealthReason("combat.healthShared"));
        if (health is not { State: "read" } || current is not { } now)
            return View(Dash, Pick("未读取", "未讀取"), 0, HealthTone.Empty, reason.Length > 0 ? reason : HealthReason("combat.healthUnread"));
        if (maximum is not { } whole)
            return View(Pick("最大值未知", "最大值未知"), Number(now), 0, HealthTone.Empty, HealthReason("combat.healthMaximumUnknown"));
        var left = BigInteger.Clamp(now, BigInteger.Zero, whole);
        var tenths = (int)((left * 2000 + whole) / (whole * 2));
        var fill = (double)(left * 1_000_000 / whole) / 1_000_000;
        // 警示色按精确比例判断（剩余不超过 30%），不受百分比的舍入影响。
        return View(PercentTenths(tenths), $"{Number(left)} / {Number(whole)}", fill, left * 10 <= whole * 3 ? HealthTone.Low : HealthTone.Normal, reason);
    }

    // 千分位整数写成一位小数的百分比：“10.0%”。
    private static string PercentTenths(int tenths) => $"{tenths / 10}.{tenths % 10}%";

    /// <summary>血量原因码的悬停说明。</summary>
    public static string HealthReason(string code) => code switch
    {
        "combat.healthUnread" => Pick("未读取到血量：需要重新进入怪物视野", "未讀取到血量：需要重新進入怪物視野"),
        "combat.healthUndecryptable" => Pick("最近的血量数据无法解开", "最近的血量資料無法解開"),
        "combat.healthMaximumUnknown" => Pick("最大血量未知：怪物出现时没有观测到", "最大血量未知：怪物出現時沒有觀測到"),
        "combat.healthShared" => Pick("这个目标不单独下发血量，由同场首领体现", "這個目標不單獨下發血量，由同場首領體現"),
        var other => Localization.CodeText.Reason(other)
    };

    /// <summary>只有摘要、没有技能分组的记录（有伤害合计却没有分组）：没有可显示的明细。</summary>
    public static bool SummaryOnly(CombatEncounter record) => record.Skills.Count == 0 && !record.Summary.AttributedDamage.Value.IsZero;

    /// <summary>
    /// 记录明细窗口的数据：范围固定为这条记录（与查看定时测量结果同一套映射，取按记录读取的范围汇总），不显示范围切换与范围标题；
    /// 只有摘要的记录只给出主数据并写明没有明细；还没读到或读取失败时写明原因，不给出数值。
    /// </summary>
    public static CombatPageData MapRecord(CombatRecordState state)
    {
        var record = state.Record ?? state.Range?.Result;
        var summaryOnly = state.Record is { } stored && SummaryOnly(stored);
        var scope = "measurement:" + state.Id;
        var data = Map(new(state.Connected, false, null, [], null, null, [], null, scope, summaryOnly ? null : state.Target,
            summaryOnly ? null : state.Range, summaryOnly ? null : state.Problem));
        var problem = summaryOnly ? string.Empty
            : state.Unsupported ? Pick("后端不提供记录明细", "後端不提供紀錄明細")
            : !state.Connected && state.Range is null ? Pick("未连接后端", "未連接後端")
            : !state.Loaded ? Pick("正在读取记录…", "正在讀取紀錄…")
            : data.Scope?.Problem ?? string.Empty;
        if (summaryOnly) data = data with { Kpis = Kpis(null, state.Record, false, state.Record!.Record?.Measurement, null, false) };
        return data with
        {
            Scope = (data.Scope ?? new(CombatScopeKind.Measurement, string.Empty, [], string.Empty, string.Empty)) with
                { Title = string.Empty, TitleTip = string.Empty, Measurements = [], Problem = problem },
            SelectedScope = scope, SelectedTarget = summaryOnly ? null : state.Target,
            // 刚换目标、新目标的结果还没到：窗口保留上一次的数值，只移动选中标记。
            Pending = !summaryOnly && state.Range is { } shown && shown.TargetId != state.Target,
            RecordInfo = RecordHeaderOf(record, summaryOnly)
        };
    }

    private static RecordHeader RecordHeaderOf(CombatEncounter? value, bool summaryOnly)
    {
        if (value is null) return new(string.Empty, string.Empty, false, false, string.Empty);
        static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
        var name = value.Record is not null ? RecordLabel(value)
            : SegmentText(value.Segment, value.Summary) is { Length: > 0 } segment ? segment : Time(value.StartedAt);
        var when = value.Record?.SavedAt is { } saved ? Pick($"保存于 {Time(saved)}", $"儲存於 {Time(saved)}")
            : Pick($"开始于 {Time(value.StartedAt)}", $"開始於 {Time(value.StartedAt)}");
        var incomplete = DataIncomplete(value);
        return new(name, when, incomplete, summaryOnly,
            summaryOnly ? Pick("这条记录保存于旧版本，没有明细", "這筆紀錄儲存於舊版本，沒有明細") : string.Empty)
            { GapNote = incomplete ? GapNote(value.Gaps) : string.Empty };
    }

    /// <summary>记录是否数据不完整：保存时标为不完整，或文档里带缺口（自动分段与定时测量看缺口）。</summary>
    public static bool DataIncomplete(CombatEncounter value) => value.Record?.DataIncomplete == true || value.Gaps.Count > 0;

    // 缺口的影响与时段（相对记录开始，至多列 3 段）；曲线控件不画缺口，时段写在这句里。
    private static string GapNote(IReadOnlyList<CombatGap> gaps)
    {
        var spans = gaps.Select(gap => (gap.FromMs, gap.ToMs)).Distinct().OrderBy(span => span.FromMs).ToList();
        var parts = spans.Take(3).Select(span => span.ToMs is { } to && to > span.FromMs
            ? $"{Clock(span.FromMs)}–{Clock(to)}" : Pick($"{Clock(span.FromMs)} 起", $"{Clock(span.FromMs)} 起")).ToList();
        var more = spans.Count > 3 ? Pick($" 等 {spans.Count} 处", $" 等 {spans.Count} 處") : string.Empty;
        var where = parts.Count == 0 ? string.Empty : Pick($"（{string.Join("、", parts)}{more}）", $"（{string.Join("、", parts)}{more}）");
        return Pick("抓包期间有数据缺失，数值可能偏低", "抓包期間有資料缺失，數值可能偏低") + where;
    }

    /// <summary>
    /// 总表的一个伤害标签：次数、次数占比的分母与带该标签的伤害合计。<paramref name="Kind"/> 为 0 时总显示（暴击、强击、连击、追击），
    /// 1 为攻击特性与首击（有已知数据时显示），2 为元素（有出现时显示）。
    /// </summary>
    public sealed record TagSpec(string Id, string Hans, string Hant, int Kind, Func<CombatDamageGroup, UInt64String?> Count,
        Func<CombatDamageGroup, UInt64String?> Eligible, Func<CombatDamageGroup, DamageAmountString?> Damage)
    {
        public string Name => Pick(Hans, Hant);

        /// <summary>客户端属性图标的键；首击、普攻、追加打击没有图标，为空。</summary>
        public string? Icon => Id switch
        {
            "critical" => ClientIconNames.Critical,
            "powerful" => ClientIconNames.PowerfulHit,
            "rapid" => ClientIconNames.RapidHit,
            "double" => ClientIconNames.DoubleHit,
            "breaked" => ClientIconNames.BreakedHit,
            "break" => ClientIconNames.BreakOccurred,
            "multi" => ClientIconNames.MultiHit,
            _ => Kind == 2 ? ClientIconNames.Element(Id) : null
        };
    }

    private static UInt64String? Element(CombatDamageGroup group, Func<CombatDamageElementCounts, UInt64String> pick) =>
        group.ElementCounts is { } counts ? pick(counts) : null;
    private static DamageAmountString? ElementDamage(CombatDamageGroup group, Func<CombatDamageElementAmounts, DamageAmountString> pick) =>
        group.TagDamage?.Elements is { } amounts ? pick(amounts) : null;

    /// <summary>
    /// 总表的全部标签。一条伤害可同时带多个标签（例如强击、连击与暗属性），各项独立计数、可重叠；次数占比的分母：
    /// 暴击为可暴击次数（criticalEligibleCount），强击、连击、追击为直接伤害命中数（mainHitEligibleCount），其余为全部已归属伤害次数（eventCount）。
    /// </summary>
    public static readonly IReadOnlyList<TagSpec> Tags =
    [
        new("critical", "暴击", "暴擊", 0, group => group.CriticalCount, group => group.CriticalEligibleCount ?? group.HitCount, group => group.TagDamage?.Critical),
        new("powerful", "强击", "強擊", 0, group => group.PowerfulHitCount, group => group.MainHitEligibleCount ?? group.HitCount, group => group.TagDamage?.PowerfulHit),
        new("rapid", "连击", "連擊", 0, group => group.RapidHitCount, group => group.MainHitEligibleCount ?? group.HitCount, group => group.TagDamage?.RapidHit),
        new("double", "追击", "追擊", 0, group => group.DoubleHitCount, group => group.MainHitEligibleCount ?? group.HitCount, group => group.TagDamage?.DoubleHit),
        new("breaked", "无防备", "無防備", 1, group => group.BreakedHitCount, group => group.EventCount, group => group.BreakedHitDamage),
        new("break", "破防", "破防", 1, group => group.BreakOccurredCount, group => group.EventCount, group => group.TagDamage?.BreakOccurred),
        new("first", "首击", "首擊", 1, group => group.FirstAttackCount, group => group.EventCount, group => group.TagDamage?.FirstAttack),
        new("default", "普攻", "普攻", 1, group => group.DefaultAttackCount, group => group.EventCount, group => group.TagDamage?.DefaultAttack),
        new("multi", "多重打击", "多重打擊", 1, group => group.MultiHitCount, group => group.EventCount, group => group.TagDamage?.MultiHit),
        new("additional", "追加打击", "追加打擊", 1, group => group.AdditionalHitCount, group => group.EventCount, group => group.TagDamage?.AdditionalHit),
        new("bleed", "出血", "出血", 2, group => Element(group, e => e.Bleed), group => group.EventCount, group => ElementDamage(group, e => e.Bleed)),
        new("dark", "暗黑", "暗黑", 2, group => Element(group, e => e.Dark), group => group.EventCount, group => ElementDamage(group, e => e.Dark)),
        new("fire", "火焰", "火焰", 2, group => Element(group, e => e.Fire), group => group.EventCount, group => ElementDamage(group, e => e.Fire)),
        new("holy", "神圣", "神聖", 2, group => Element(group, e => e.Holy), group => group.EventCount, group => ElementDamage(group, e => e.Holy)),
        new("ice", "冰冻", "冰凍", 2, group => Element(group, e => e.Ice), group => group.EventCount, group => ElementDamage(group, e => e.Ice)),
        new("lightning", "触电", "觸電", 2, group => Element(group, e => e.Lightning), group => group.EventCount, group => ElementDamage(group, e => e.Lightning)),
        new("poison", "毒", "毒", 2, group => Element(group, e => e.Poison), group => group.EventCount, group => ElementDamage(group, e => e.Poison)),
        new("soul", "精神", "精神", 2, group => Element(group, e => e.Soul), group => group.EventCount, group => ElementDamage(group, e => e.Soul))
    ];

    private static IReadOnlyList<string> ShownTagIds(IReadOnlyList<CombatDamageGroup> groups) => Tags.Where(tag => tag.Kind switch
    {
        0 => true,
        1 => groups.Any(group => tag.Count(group) is not null),
        _ => groups.Any(group => tag.Count(group) is { Value: > 0 })
    }).Select(tag => tag.Id).ToList();

    /// <summary>
    /// 一行的标签单元格：每个标签“占比（n 次）”与“伤害 · 占本行伤害”。次数未知时为“不适用”，金额未知（含旧记录）时为“未知”，不以 0 代替。
    /// </summary>
    public static TagCells TagCellsOf(CombatDamageGroup group)
    {
        var total = group.AttributedDamage.Value;
        var cells = new List<string>(Tags.Count * 2);
        foreach (var tag in Tags)
        {
            var count = tag.Count(group);
            cells.Add(count is { } hit && tag.Eligible(group) is { Value: > 0 } all
                ? $"{Percent((double)hit.Value / all.Value)}（{Number(hit.Value)} {Pick("次", "次")}）" : NotApplicable());
            cells.Add(count is null || tag.Damage(group) is not { } damage ? Pick("未知", "未知")
                : total.IsZero ? Number(damage.Value) : $"{Number(damage.Value)} · {Percent((double)damage.Value / (double)total)}");
        }
        return new(cells);
    }

    /// <summary>
    /// DPS 曲线：后端时间线分桶（只含本人与计入的召唤物伤害）每桶的伤害 ÷ 桶宽。整场跨多个分段、没有战斗记录或读取被拒绝时写明原因。
    /// </summary>
    public static CombatTimelineView TimelineView(CombatTimelinePage? page, string? problem)
    {
        if (problem is not null) return new([], 0, 0, problem switch
        {
            "observation-scope" => Pick("整场跨多段，曲线只按单段提供", "整場跨多段，曲線只按單段提供"),
            "no-record" => Pick("当前范围还没有战斗记录", "目前範圍還沒有戰鬥紀錄"),
            var code => Pick("曲线不可用：", "曲線不可用：") + Localization.CodeText.Reason(code)
        });
        if (page is null) return CombatTimelineView.Pending;
        var buckets = page.DamageBuckets;
        if (buckets.Count == 0) return new([], 0, 0, Pick("当前范围还没有伤害", "目前範圍還沒有傷害"));
        var dps = buckets.Select(bucket => bucket.ToMs > bucket.FromMs
            ? (double)DamageAccumulatorTotal(bucket.Amounts) * 1000 / (bucket.ToMs - bucket.FromMs) : 0).ToList();
        return new(dps, (buckets[0].ToMs - buckets[0].FromMs) / 1000, (buckets[^1].ToMs - buckets[0].FromMs) / 1000, null);
    }

    /// <summary>
    /// 整场曲线：把整场各分段的时间线分桶按本机时钟合并到同一条横轴上（从第一段开始到最后一段的最后一个桶），
    /// 按整场跨度重新取桶宽（约 60 个点），各分段的细桶按中点归入；分段之间没有伤害的时段为 0。
    /// </summary>
    public static CombatTimelineView WholeTimelineView(IReadOnlyList<(DateTimeOffset Start, IReadOnlyList<CombatDamageBucket> Buckets)> segments)
    {
        var parts = segments.Where(part => part.Buckets.Count > 0).ToList();
        if (parts.Count == 0) return new([], 0, 0, Pick("当前范围还没有伤害", "目前範圍還沒有傷害"));
        var origin = parts.Min(part => part.Start);
        var end = parts.Max(part => part.Start.AddMilliseconds(part.Buckets[^1].ToMs));
        var spanMs = Math.Max(1, (end - origin).TotalMilliseconds);
        var width = BucketWidth(spanMs);
        var count = Math.Max(1, (int)Math.Ceiling(spanMs / width));
        var sums = new double[count];
        foreach (var part in parts)
        {
            var offset = (part.Start - origin).TotalMilliseconds;
            foreach (var bucket in part.Buckets)
            {
                var index = (int)Math.Clamp(Math.Floor((offset + (bucket.FromMs + bucket.ToMs) / 2) / width), 0, count - 1);
                sums[index] += (double)Total(bucket.Amounts);
            }
        }
        return new(sums.Select(sum => sum * 1000 / width).ToList(), width / 1000.0, count * width / 1000.0, null, Origin: origin);
    }

    private static BigInteger DamageAccumulatorTotal(CombatDamageTotals totals) => Total(totals);

    /// <summary>曲线的桶宽：约 60 个点，1–60 秒，不超过后端 1000 个桶的上限。</summary>
    public static int BucketWidth(double durationMs) => (int)Math.Clamp(Math.Ceiling(durationMs / 60 / 1000) * 1000, 1000, 60000);

    // 预备中或计时中、且正是页面所选的测量：结果尚未产生。
    private static CombatMeasurementState? PendingMeasurement(CombatBackendState state, CombatScopeKind scope) =>
        scope == CombatScopeKind.Measurement && state.Measurement is { Phase: "waiting" or "countdown" or "running", MeasurementId: { } id } value
            && state.Scope == "measurement:" + id ? value : null;

    // 判定“正在攻击”用的观测时刻：观测进行中时取观察状态的最近观测时刻，其余时候不判定。
    private static DateTimeOffset? Now(CombatWorkspace? workspace) =>
        workspace?.Observation is { State: "observing", LastObservedAt: { } at } ? at : null;

    /// <summary>
    /// 页面列出的目标：当前分段一律取分段的目标（选不选目标都是同一份来源，顺序不随选择变化），其余范围取范围内全部目标
    /// （后端的 rangeTargets 不受目标筛选影响）。
    /// </summary>
    internal static IReadOnlyList<CombatDamageGroup> ShownTargets(CombatWorkspace? workspace, string scope, string? rangeTarget,
        CombatRangeSummary? range) =>
        scope == "segment" ? workspace?.Encounter?.Targets ?? [] : range?.RangeTargets ?? [];

    /// <summary>
    /// 所有怪物列表的统一排序：“正在攻击”的目标（最近一次有效伤害的目标，且该伤害在 <see cref="AttackWindow"/> 内）置顶；
    /// 其余按首次出现（firstDamageAt）新的在前，同一时刻按目标编号；旧记录没有时间字段时按伤害从高到低；合并组排最后。
    /// 顺序不随伤害数值变化。
    /// </summary>
    public static IReadOnlyList<CombatDamageGroup> OrderTargets(IReadOnlyList<CombatDamageGroup> groups, DateTimeOffset? now)
    {
        var attacking = Attacking(groups, now);
        var rest = groups.Where(group => !ReferenceEquals(group, attacking) && group.Key != "other-targets")
            .OrderByDescending(group => group.FirstDamageAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(group => group.FirstDamageAt is null ? group.AttributedDamage.Value : BigInteger.Zero)
            .ThenBy(group => group.Key, StringComparer.Ordinal);
        var result = new List<CombatDamageGroup>();
        if (attacking is not null) result.Add(attacking);
        result.AddRange(rest);
        result.AddRange(groups.Where(group => group.Key == "other-targets"));
        return result;
    }

    /// <summary>“正在攻击”的目标：最近一次有效伤害（lastDamageAt 最新）的目标，且距 <paramref name="now"/> 不超过 <see cref="AttackWindow"/>。</summary>
    public static CombatDamageGroup? Attacking(IReadOnlyList<CombatDamageGroup> groups, DateTimeOffset? now)
    {
        if (now is not { } at) return null;
        var latest = groups.Where(group => group.LastDamageAt is not null && group.Key != "other-targets").MaxBy(group => group.LastDamageAt);
        return latest?.LastDamageAt is { } last && last >= at - AttackWindow ? latest : null;
    }

    /// <summary>
    /// 怪物效果区显示哪个目标：选中的目标；未选时为排序第一个仍在当前观测中的怪物（正在攻击的目标自然排在第一，可观测范围内的优先）。
    /// 页面与后端适配层用同一规则，只向后端查询当前观测里的实体。
    /// </summary>
    public static string? EffectTargetId(CombatWorkspace? workspace, string scope, string? rangeTarget, CombatRangeSummary? range)
    {
        if (rangeTarget is not null) return rangeTarget;
        var monsters = (workspace?.Entities ?? []).Where(entity => entity.Role.KnownValue == CombatEntityRole.Monster)
            .ToDictionary(entity => entity.Id, entity => entity.InScope, StringComparer.Ordinal);
        var ordered = OrderTargets(ShownTargets(workspace, scope, rangeTarget, range), Now(workspace));
        // 先取仍在可观测范围内的怪物，没有时取已离开范围但仍在当前观测里的怪物。
        return (ordered.FirstOrDefault(group => monsters.GetValueOrDefault(group.Key))
            ?? ordered.FirstOrDefault(group => monsters.ContainsKey(group.Key)))?.Key;
    }

    // 怪物效果区的目标行：与“选择目标”同一顺序，当前效果目标标为选中；不在当前观测中的目标以“已离开”样式显示。
    private static IReadOnlyList<TargetChip> EffectChips(IReadOnlyList<CombatDamageGroup> ordered, IReadOnlyList<CombatEntity> monsters, string? current) =>
        ordered.Where(group => group.Key != "other-targets").Select(group =>
        {
            var entity = monsters.FirstOrDefault(item => item.Id == group.Key);
            var name = entity is null ? GroupDisplay(group, target: true) : EntityName(entity, entity.Key);
            var away = entity is not { InScope: true };
            var tip = away ? name.Tip + Pick("（已不在当前观测中）", "（已不在目前觀測中）") : name.Tip;
            return new TargetChip(group.Key, name.Text, group.Key == current, !away, tip);
        }).ToList();

    private static string EffectNote(CombatBackendState state, IReadOnlyList<CombatDamageGroup> ordered, string? target, CombatEntity? entity)
    {
        if (target is null)
            return ordered.Count == 0 ? Pick("命中怪物后显示效果", "命中怪物後顯示效果")
                : Pick("目标已不在观测范围", "目標已不在觀測範圍");
        if (entity is null || state.EffectProblem == "combat.entityOutOfScope")
            return Pick("目标已不在观测范围", "目標已不在觀測範圍");
        return state.EffectProblem is { } code ? Pick("效果不可用：", "效果不可用：") + Localization.CodeText.Reason(code) : string.Empty;
    }

    /// <summary>界面上的范围选择：observation、segment 或 measurement:&lt;记录&gt;。</summary>
    public static CombatScopeKind ScopeKind(string scope) => scope switch
    {
        "segment" => CombatScopeKind.Segment,
        _ when scope.StartsWith("measurement", StringComparison.Ordinal) => CombatScopeKind.Measurement,
        _ => CombatScopeKind.Whole
    };

    /// <summary>测量时长写成“1:00”“1:30”或“60:00”。</summary>
    public static string DurationText(double milliseconds) => CombatPageModel.DurationText(milliseconds);

    private static string DefaultRecordName(CombatScopeKind scope, CombatEncounter? shown)
    {
        var when = (shown?.StartedAt ?? DateTimeOffset.Now).ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
        return scope switch
        {
            CombatScopeKind.Segment => Pick($"分段 {when}", $"分段 {when}"),
            CombatScopeKind.Measurement when shown?.Record?.Measurement is { } measured =>
                Pick($"定时测量 {DurationText(measured.DurationMs)} {when}", $"定時測量 {DurationText(measured.DurationMs)} {when}"),
            _ => Pick($"整场 {when}", $"整場 {when}")
        };
    }

    // 定时测量：阶段、设定、剩余时间与计时中的累计；不能开始时给出原因。
    private static MeasurementView Measurement(CombatBackendState state)
    {
        if (!state.Connected) return MeasurementView.Unavailable(Pick("未连接后端", "未連接後端"));
        if (state.Measurement is not { } value) return MeasurementView.Unavailable(Pick("后端没有提供定时测量", "後端沒有提供定時測量"));
        // waiting：已受理，等待本机第一次有效伤害；旧后端的 countdown 同样按等待显示。
        var phase = value.Phase switch { "waiting" or "countdown" => MeasurementPhase.Waiting, "running" => MeasurementPhase.Running, _ => MeasurementPhase.Idle };
        var last = value.LastResult is { } result && value.LastResultId is { } id
            ? new MeasurementResultView(id, Number(result.Dps), Number(result.Damage.Value), DurationText(result.DurationMs),
                result.ActiveDurationMs is { } active ? Clock(active) : Pick("未知", "未知"), result.Complete ? string.Empty : Incomplete(result.Reasons))
            : null;
        // 计时中还没有伤害时累计与 DPS 为“—”（工具栏写“暂无数据”），不显示 0。
        var running = phase == MeasurementPhase.Running && value.Damage is { Value.IsZero: false };
        return new(phase, value.Settings.DurationMs, phase == MeasurementPhase.Running ? value.RemainingMs : null, state.MeasurementAt,
            running && value.Damage is { } damage ? Number(damage.Value) : Dash, running ? Number(value.CurrentDps) : Dash,
            value.Available, value.Available ? string.Empty : MeasurementHint(value.UnavailableReason),
            phase != MeasurementPhase.Idle, value.Complete == false ? Incomplete(value.Reasons) : string.Empty, last,
            value.SettingsRevision.Value);
    }

    /// <summary>不能开始定时测量的原因。</summary>
    public static string MeasurementHint(string? reason) => reason switch
    {
        "combat.observationInactive" => Pick("需要先开始 DPS 观测", "需要先開始 DPS 觀測"),
        "combat.observationStarting" => Pick("观测尚未收到数据，请稍候", "觀測尚未收到資料，請稍候"),
        "combat.damageDisabled" => Pick("这次观测没有统计伤害", "這次觀測沒有統計傷害"),
        "combat.measurementActive" => Pick("已有定时测量在进行", "已有定時測量在進行"),
        null or "" => string.Empty,
        var other => Pick("暂不可用：", "暫不可用：") + Localization.CodeText.Reason(other)
    };

    /// <summary>不完整的原因：切换地图、身份等待、观测停止、时间线截断等写成短句，其余原因码原样列出。</summary>
    public static string Incomplete(IReadOnlyList<string> reasons)
    {
        var parts = ReasonTexts(reasons);
        return parts.Count == 0 ? string.Empty : Pick("不完整：", "不完整：") + string.Join("、", parts);
    }

    /// <summary>原因码换成短句（去重）；状态行的质量说明、战斗记录与“不完整”说明共用。</summary>
    private static IReadOnlyList<string> ReasonTexts(IReadOnlyList<string> reasons) =>
        reasons.Select(reason => reason switch
        {
            "connection-changed" or "combat.streamGap.connectionClosed" or "combat.streamGap.connectionRemoved"
                or "combat.streamGap.connectionSyn" or "combat.streamGap.connectionReset" => Pick("期间切换了地图", "期間切換了地圖"),
            "identity-unresolved" or "observation-reconnected" => Pick("等待识别本机角色", "等待識別本機角色"),
            "stream-reset" => Pick("抓包数据中断，已另起分段", "抓包資料中斷，已另起分段"),
            "observation-stopped" or "observation-interrupted" => Pick("观测提前停止", "觀測提前停止"),
            "timeline-incomplete" or "timeline-capacity" => Pick("超出事件保留范围", "超出事件保留範圍"),
            "healing-not-retained" => Pick("记录没有保留治疗事件", "紀錄沒有保留治療事件"),
            "insufficientDuration" => Pick("时长为零", "時長為零"),
            "range-segment-limit" => Pick("分段过多，只统计最近 128 段", "分段過多，只統計最近 128 段"),
            var other when Localization.CodeText.TryReason(other, out var known) => known,
            var other when other.StartsWith("combat.", StringComparison.Ordinal) || other.Contains("gap", StringComparison.OrdinalIgnoreCase)
                || other.Contains("capacity", StringComparison.OrdinalIgnoreCase) => Localization.CodeText.Unknown("combat-gap", other, "抓包缺口", "抓包缺口"),
            var other => Localization.CodeText.Reason(other)
        }).Distinct().ToList();

    // 主数据下方的不完整说明：有原因时写原因，后端没给原因时只写“不完整”。
    private static string IncompleteText(IReadOnlyList<string> reasons) => Incomplete(reasons) is { Length: > 0 } text ? text : Pick("不完整", "不完整");

    // 战斗状态：来源给出本机战斗状态（localCombatState）时按它显示战斗中／战斗外；没有该信号时沿用“有进行中的分段为战斗中”。
    // 切图中与未观测由观察状态给出。地图与频道是全局信息，显示在主窗口侧栏。
    private static LocationView Location(CombatBackendState state, bool observing)
    {
        var workspace = state.Workspace;
        var switching = observing && workspace!.Encounter is null or { Summary.Final: true } && workspace.Observation.Reasons.Any(WaitingReasons.Contains);
        var (text, tone) = !observing ? (Pick("未观测", "未觀測"), ActivityTone.Off)
            : switching ? (Pick("切图中", "切圖中"), ActivityTone.Switching)
            : workspace!.Observation.LocalCombatState is { } signal
                ? (CombatStateText(signal), signal.KnownValue == CombatLocalState.InCombat ? ActivityTone.Fighting : ActivityTone.Idle)
            : workspace.Encounter is { Summary.Final: false } ? (Pick("战斗中", "戰鬥中"), ActivityTone.Fighting)
            : (Pick("空闲", "空閒"), ActivityTone.Idle);
        return new(text, tone);
    }

    private static ScopeView ScopeOf(CombatBackendState state, CombatScopeKind scope, CombatEncounter? shown, CombatMeasurementState? pending)
    {
        var current = scope == CombatScopeKind.Measurement && state.Scope.Length > "measurement:".Length ? state.Scope["measurement:".Length..] : null;
        var options = state.History.Where(item => item.Record?.Kind == "timed-measurement")
            .Select(item => new MeasurementOption(item.Id, RecordLabel(item), item.Id == current)).ToList();
        if (current is not null && options.All(option => option.Id != current) && shown?.Record?.Kind == "timed-measurement")
            options.Insert(0, new(current, RecordLabel(shown), true));
        // 进行中的测量（尚未进入历史）作为第一项，可以随时切回。
        var runningLabel = state.Measurement is { Phase: "waiting" or "countdown" or "running", MeasurementId: not null } live
            ? Pick($"定时测量 {DurationText(live.Settings.DurationMs)} · 进行中", $"定時測量 {DurationText(live.Settings.DurationMs)} · 進行中") : null;
        if (runningLabel is not null && state.Measurement!.MeasurementId is { } runningId && options.All(option => option.Id != runningId))
            options.Insert(0, new(runningId, runningLabel, runningId == current));
        // 当前分段已结束、新分段还没开始：范围里显示的是冻结的上一段，标题写明。
        var title = pending is not null ? runningLabel ?? string.Empty
            : scope == CombatScopeKind.Measurement && shown is not null ? RecordLabel(shown)
            : scope == CombatScopeKind.Segment && state.Workspace?.Observation.State == "observing" && state.Workspace.Encounter is { Summary.Final: true } last
                ? Pick($"上一段（{EndReason(last.Summary.BoundaryReason)}）", $"上一段（{EndReason(last.Summary.BoundaryReason)}）")
            // 整场已到期、下一次伤害还没到：显示的仍是上一场。
            : scope == CombatScopeKind.Whole && shown is not null && state.Workspace?.Observation.WholeExpired is not null ? Pick("上一场", "上一場")
            : string.Empty;
        var titleTip = scope == CombatScopeKind.Whole && shown is not null && state.Workspace?.Observation.WholeExpired is { } expired
            ? WholeExpiredText(expired) : string.Empty;
        var reasons = scope == CombatScopeKind.Measurement && shown?.Record?.Measurement is { Complete: false } measured
            ? Incomplete(measured.Reasons)
            : state.Range is { Complete: false } range && !(scope == CombatScopeKind.Segment && state.RangeTarget is null) ? Incomplete(range.Reasons) : string.Empty;
        var problem = pending is not null
            ? pending.Phase is "waiting" or "countdown" ? Pick("等待首次伤害", "等待首次傷害") : Pick("定时测量计时中", "定時測量計時中")
            : state.RangeProblem is { } code
            ? code == "resource.notFound" ? Pick("这条记录已不存在", "這條紀錄已不存在") : Pick("范围统计未完成：", "範圍統計未完成：") + Localization.CodeText.Reason(code)
            : shown is null && state.Connected
                ? scope == CombatScopeKind.Measurement ? Pick("还没有定时测量结果", "還沒有定時測量結果") : Pick("还没有战斗数据", "還沒有戰鬥資料")
                : string.Empty;
        return new(scope, title, options, reasons, problem) { TitleTip = titleTip };
    }

    /// <summary>整场到期的说明：到期原因与“下一次伤害开始新的一场”。</summary>
    public static string WholeExpiredText(string reason) => reason switch
    {
        "map-change" => Pick("已切换地图，这一场已结束；下一次伤害开始新的一场，之前一直显示上一场",
            "已切換地圖，這一場已結束；下一次傷害開始新的一場，之前一直顯示上一場"),
        "idle-out-of-combat" => Pick("脱离战斗后 3 分钟没有战斗动作，这一场已结束；下一次伤害开始新的一场，之前一直显示上一场",
            "脫離戰鬥後 3 分鐘沒有戰鬥動作，這一場已結束；下一次傷害開始新的一場，之前一直顯示上一場"),
        _ => Pick("这一场已结束；下一次伤害开始新的一场，之前一直显示上一场", "這一場已結束；下一次傷害開始新的一場，之前一直顯示上一場")
    };

    /// <summary>记录的显示名：定时测量为“定时测量 1:00 · 时刻”（有名称时用名称），保存的记录用名称。</summary>
    public static string RecordLabel(CombatEncounter value)
    {
        var when = value.StartedAt.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
        if (value.Record?.Name is { Length: > 0 } name) return name;
        return value.Record?.Measurement is { } measured
            ? Pick($"定时测量 {DurationText(measured.DurationMs)} · {when}", $"定時測量 {DurationText(measured.DurationMs)} · {when}")
            : value.Record?.Kind == "timed-measurement" ? Pick($"定时测量 · {when}", $"定時測量 · {when}") : when;
    }

    // “选择目标”：全部加范围内每个目标个体（按统一的怪物排序），显示对它的伤害与占比；同名目标按个体分开。
    private static IReadOnlyList<RangeTargetChip> RangeTargets(CombatBackendState state, CombatEncounter? direct, IReadOnlyList<CombatDamageGroup> ordered)
    {
        var segment = state.Scope == "segment" ? state.Workspace?.Encounter : null;
        if (direct is null && segment is null && state.Range is null) return [];
        // 当前分段选不选目标都以分段合计为分母，标签数值不随选择跳动。
        var total = (direct ?? segment)?.Summary.AttributedDamage.Value ?? state.Range!.RangeDamage.Value;
        var chips = new List<RangeTargetChip> { new(string.Empty, Pick("全部", "全部"), Number(total), total.IsZero ? Dash : "100%", state.RangeTarget is null) };
        chips.AddRange(ordered.Select(group =>
        {
            var name = GroupDisplay(group, target: true);
            return new RangeTargetChip(group.Key, name.Text, Number(group.AttributedDamage.Value),
                total.IsZero ? Dash : Percent((double)group.AttributedDamage.Value / (double)total), group.Key == state.RangeTarget, name.Tip);
        }));
        return chips;
    }

    private static TargetDetailView? TargetDetail(CombatBackendState state, CombatEncounter? shown)
    {
        if (state.RangeTarget is null || state.Range is not { } range || shown is null) return null;
        var damage = shown.Summary.AttributedDamage.Value;
        var total = range.RangeDamage.Value;
        var ratio = total.IsZero ? 0 : (double)damage / (double)total;
        var name = RangeTargetName(state);
        return new(name.Text, Number(damage), total.IsZero ? Dash : Percent(ratio), ratio, name.Tip);
    }

    // 选中目标的名称：范围汇总的 targetResolvedName，没有时取范围内同一目标分组的 resolvedName。
    private static CombatName RangeTargetName(CombatBackendState state)
    {
        var range = state.Range;
        var group = range?.RangeTargets.FirstOrDefault(item => item.Key == state.RangeTarget);
        return CombatNames.Of(range?.TargetResolvedName ?? group?.ResolvedName, CombatNameKind.Target,
            range?.TargetName ?? group?.Name, state.RangeTarget);
    }

    // 破防期间：无防备伤害累计与破防区间（造成破防的一击到目标破防值归零）。当前分段取分段汇总，其余范围取范围汇总；
    // 旧记录没有区间数据时区间项显示“未知”。
    private static BreakView Break(CombatEncounter shown, CombatRangeSummary? range, bool direct)
    {
        var summary = !direct && range is not null ? range.Break : shown.Break;
        UInt64String? count = summary?.Count; DamageAmountString? damage = summary?.Damage;
        if (summary is null)
        {
            var known = shown.Skills.All(group => group.BreakedHitCount is not null && group.BreakedHitDamage is not null);
            count = known ? new(shown.Skills.Aggregate(0UL, (sum, group) => sum + group.BreakedHitCount!.Value.Value)) : null;
            damage = known ? new(shown.Skills.Aggregate(BigInteger.Zero, (sum, group) => sum + group.BreakedHitDamage!.Value.Value)) : null;
        }
        var unknown = Pick("未知", "未知");
        var average = count is { Value: > 0 } hits && damage is { } amount ? Number(amount.Value / hits.Value) : Dash;
        var estimated = summary?.Reasons.Any(reason => reason is "break-start-estimated" or "break-end-estimated") == true;
        var note = Pick("破防区间：本人造成破防的一击起，到目标破防值归零止，按目标分别计；平均 DPS = 无防备伤害 ÷ 区间总时长，占比按范围时长",
            "破防區間：本人造成破防的一擊起，到目標破防值歸零止，按目標分別計；平均 DPS = 無防備傷害 ÷ 區間總時長，佔比按範圍時長")
            + (estimated ? Pick("；部分区间的起点或终点按无防备命中估计", "；部分區間的起點或終點按無防備命中估計") : string.Empty);
        return new(damage is { } d ? Number(d.Value) : unknown, count is { } c ? Number(c.Value) : unknown, average,
            summary?.DurationMs is null ? unknown : summary.Dps is { } dps ? Number(dps) : Dash,
            summary?.DurationMs is { } ms ? Clock(ms) : unknown,
            summary?.Intervals is { } intervals ? Number(intervals.Value) : unknown,
            summary?.TimeShare is { } share ? Percent(share) : summary?.DurationMs is null ? unknown : Dash, note);
    }

    // 间接与持续伤害占全部已归属伤害次数的比例（分类计数之和为分母）；分类未知的分组不参与。
    private static IReadOnlyList<CoverageTile> Classes(IReadOnlyList<CombatDamageGroup> skills)
    {
        var known = skills.Where(group => group.ClassificationCounts is not null).ToList();
        if (known.Count == 0) return [];
        var all = known.Aggregate(0UL, (sum, group) => sum + group.EventCount.Value);
        CoverageTile Tile(string name, Func<CombatDamageClassificationCounts, ulong> pick)
        {
            var hit = known.Aggregate(0UL, (sum, group) => sum + pick(group.ClassificationCounts!));
            if (all == 0) return new(name, Dash, 0, NotApplicable());
            var ratio = (double)hit / all;
            return new(name, Percent(ratio), ratio, $"{Number(hit)} / {Number(all)} {Pick("次", "次")}");
        }
        return [Tile(Pick("间接伤害", "間接傷害"), counts => counts.Indirect.Value), Tile(Pick("持续伤害", "持續傷害"), counts => counts.DamageOverTime.Value)];
    }

    /// <summary>
    /// 治疗：标题行写明口径（本人施放、HPS = 施放量 ÷ 统计时长）；HPS（当前分段另注最近 N 秒）、总治疗量（对自己/对他人）、
    /// 受到（自疗/其他来源与过量）。治疗随范围切换；选中怪物目标时保持整个范围的值并写明与目标无关。
    /// 后端没有治疗块时只标“未提供”；过量为空时写明不可得的原因，不显示 0。
    /// </summary>
    private static HealingView Healing(CombatEncounter encounter, bool targeted)
    {
        if ((encounter.Healing ?? encounter.Summary.Healing) is not { } healing) return new(false, []);
        var window = Seconds(encounter.Summary.RollingWindowMs);
        var times = Pick("次", "次");
        // HPS 的分母：后端治疗块的 hpsDurationMs；旧后端没有该字段时按其定义取同一范围的时长。
        var span = Clock(healing.HpsDurationMs ?? encounter.Summary.DurationMs);
        var header = Pick($"本人施放的治疗（对自己与队友）；HPS = 施放治疗量 ÷ 统计时长 {span}（停手时与 DPS 一样冻结，截至最后一次有效伤害或施放治疗）",
            $"本人施放的治療（對自己與隊友）；HPS = 施放治療量 ÷ 統計時長 {span}（停手時與 DPS 一樣凍結，截至最後一次有效傷害或施放治療）");
        var cast = new HealingTile(Pick("总治疗量", "總治療量"), Number(healing.CastAmount.Value),
            $"{Number(healing.CastCount.Value)} {times}{Pick("，对自己 ", "，對自己 ")}{Number(healing.CastSelfAmount.Value)}" +
            $"{Pick("、对他人 ", "、對他人 ")}{Number(healing.CastOtherAmount.Value)}");
        var perSecond = new HealingTile("HPS", Number(healing.EncounterHps),
            healing.RollingHps is { } rolling ? Pick($"最近 {window} 秒 ", $"最近 {window} 秒 ") + Number(rolling)
                : encounter.Summary.RollingDps is null && healing.EncounterHps is not null ? string.Empty
                : Pick($"最近 {window} 秒 未知", $"最近 {window} 秒 未知"));
        var overheal = healing.ReceivedOverheal is { } extra
            ? Pick($"过量 {Number(extra.Value)}", $"過量 {Number(extra.Value)}")
            : Pick("过量不可得：", "過量不可得：") + EffectiveReason(healing.EffectiveReason);
        var received = new HealingTile(Pick("受到治疗", "受到治療"), Number(healing.ReceivedAmount.Value),
            $"{Number(healing.ReceivedCount.Value)} {times}{Pick("，自疗 ", "，自療 ")}{Number(healing.ReceivedSelfAmount.Value)}" +
            $"{Pick("、其他来源 ", "、其他來源 ")}{Number(healing.ReceivedOtherAmount.Value)}{Pick("（协议不提供来源）", "（協議不提供來源）")}" +
            $"{Pick("，", "，")}{overheal}");
        var effective = healing.ReceivedEffective is { } known && !healing.ReceivedAmount.Value.IsZero
            ? Percent((double)known.Value / (double)healing.ReceivedAmount.Value)
            : healing.ReceivedCount.Value == 0 ? NotApplicable() : Pick("未知", "未知");
        return new(true, [perSecond, cast, received], header,
            targeted ? Pick("治疗与所选怪物无关，按整个范围统计", "治療與所選怪物無關，按整個範圍統計") : string.Empty)
        {
            Hps = Number(healing.EncounterHps), Cast = Number(healing.CastAmount.Value),
            CastCount = Number(healing.CastCount.Value), Effective = effective
        };
    }

    private static string EffectiveReason(string? reason) => reason switch
    {
        "hp-unpaired" => Pick("未与 HP 变化配对", "未與 HP 變化配對"),
        "hp-mismatch" => Pick("HP 变化不一致", "HP 變化不一致"),
        null or "" => Pick("原因未知", "原因未知"),
        var other => Localization.CodeText.Reason(other)
    };

    /// <summary>
    /// 治疗明细：每个施放目标一行（治疗量、占比、次数、HPS = 目标治疗量 ÷ 范围的 HPS 时长），末行为受到的治疗合计及有效与溢出。
    /// 协议不带治疗技能，也没有施放治疗的有效量来源，这两项显示“未知”；受疗未配对时有效与溢出同样为“未知”。
    /// </summary>
    private static IReadOnlyList<HealingTargetRow> HealingTargets(CombatEncounter encounter)
    {
        if ((encounter.Healing ?? encounter.Summary.Healing) is not { } healing) return [];
        var unknown = Pick("未知", "未知");
        var notApplicable = NotApplicable();
        var total = healing.CastAmount.Value;
        var span = healing.HpsDurationMs ?? encounter.Summary.DurationMs;
        string Hps(System.Numerics.BigInteger amount) => span > 0 ? Number((double)amount * 1000 / span) : Dash;
        var rows = (healing.Targets ?? []).OrderByDescending(target => target.Amount.Value).Select(target =>
        {
            // 治疗目标是本人或其他玩家：战斗协议不带其他玩家的名字，名称缺失时为“未知玩家”，实体编号在悬停提示里。
            var name = target.Self ? Fixed(Pick("本人", "本人")) : target.Key == "other-targets" ? Fixed(Pick("其他目标", "其他目標"))
                : CombatNames.Of(null, CombatNameKind.Player, target.Name, target.Key);
            return new HealingTargetRow(name.Text, Number(target.Amount.Value),
                total.IsZero ? Dash : Percent((double)target.Amount.Value / (double)total), Number(target.Count.Value), Hint(name))
            { Hps = Hps(target.Amount.Value), Effective = unknown, Overheal = unknown,
              EffectiveTip = Pick("施放治疗没有有效量来源", "施放治療沒有有效量來源") };
        }).ToList();
        if (healing.ReceivedCount.Value > 0)
        {
            var reason = healing.ReceivedEffective is null ? EffectiveReason(healing.EffectiveReason) : string.Empty;
            rows.Add(new HealingTargetRow(Pick("受到的治疗（合计）", "受到的治療（合計）"), Number(healing.ReceivedAmount.Value), notApplicable,
                Number(healing.ReceivedCount.Value), Pick("本人受到的全部治疗，协议不提供来源", "本人受到的全部治療，協議不提供來源"))
            {
                Hps = notApplicable,
                Effective = healing.ReceivedEffective is { } effective ? Number(effective.Value) : unknown,
                Overheal = healing.ReceivedOverheal is { } overheal ? Number(overheal.Value) : unknown,
                EffectiveTip = reason.Length > 0 ? Pick("有效与溢出不可得：", "有效與溢出不可得：") + reason : string.Empty
            });
        }
        return rows;
    }

    // 只显示本场最近的一次首击：技能取本场同一技能分组的 resolvedName（分组键是技能编号），目标取观测到的实体名称，查不到时不写目标。
    private static FirstAttackRow? LatestFirstAttack(IReadOnlyList<CombatDamageEvent>? events,
        CombatEncounter encounter, IReadOnlyList<CombatEntity> entities, CombatName? selectedTarget = null)
    {
        if (events is null || events.Count == 0) return null;
        var item = events.MaxBy(item => item.MonotonicMs)!;
        var skill = item.SkillId.ToString();
        var group = encounter.Skills.FirstOrDefault(group => group.Key == skill);
        var name = CombatNames.Of(group?.ResolvedName, CombatNameKind.Skill, group?.Name, skill);
        var entity = entities.FirstOrDefault(entity => entity.Key == item.TargetEntity);
        // 目标：选中目标的名称或观测到的实体名称，缺失时为“未知目标”；不知道打中的是谁（实体未观测到）时不写目标。
        var target = selectedTarget ?? (entity is null ? null : EntityName(entity, entity.Key));
        return new FirstAttackRow(name.Text, Number(item.Amount.Value), target?.Text ?? string.Empty, Hint(name), target is { } shown ? Hint(shown) : string.Empty);
    }

    // 攻击特性与元素：统计全部已归属伤害（直接、间接、持续都算），分母是 eventCount。
    private static readonly (string Key, string Hans, string Hant, string Color)[] KnownElements =
    [
        ("bleed", "出血", "出血", "#D0344A"),
        ("dark", "暗黑", "暗黑", "#8A6CC8"),
        ("fire", "火焰", "火焰", "#E0661A"),
        ("holy", "神圣", "神聖", "#A88A1E"),
        ("ice", "冰冻", "冰凍", "#2E95C8"),
        ("lightning", "触电", "觸電", "#5C70F0"),
        ("poison", "毒", "毒", "#3E9A48"),
        ("soul", "精神", "精神", "#C04FB0")
    ];

    /// <summary>元素的显示名称；列表以外的值原样返回。</summary>
    public static string ElementName(string element)
    {
        foreach (var known in KnownElements)
            if (string.Equals(known.Key, element, StringComparison.OrdinalIgnoreCase)) return Pick(known.Hans, known.Hant);
        return element;
    }

    /// <summary>元素色块的固定颜色；列表以外的元素用中性灰。</summary>
    public static string ElementColor(string element)
    {
        foreach (var known in KnownElements)
            if (string.Equals(known.Key, element, StringComparison.OrdinalIgnoreCase)) return known.Color;
        return "#7D8597";
    }

    private static IEnumerable<(string Key, ulong Count)> ElementCounts(CombatDamageElementCounts counts) =>
    [
        ("bleed", counts.Bleed.Value), ("dark", counts.Dark.Value), ("fire", counts.Fire.Value), ("holy", counts.Holy.Value),
        ("ice", counts.Ice.Value), ("lightning", counts.Lightning.Value), ("poison", counts.Poison.Value),
        ("soul", counts.Soul.Value)
    ];

    private static bool HasTraitData(CombatDamageGroup group) =>
        group.BreakedHitCount is not null || group.BreakOccurredCount is not null || group.DefaultAttackCount is not null || group.MultiHitCount is not null || group.AdditionalHitCount is not null || group.ElementCounts is not null;

    /// <summary>
    /// 一行的元素文字：按次数从高到低列出占比大于 0 的元素，最多三项，其余合并为“其他 n%”。
    /// 计数未知或分母为 0 时为“不适用”，已知但全为 0 时为“无元素”。
    /// </summary>
    public static string ElementSummary(IReadOnlyList<(string Element, ulong Count)>? counts, ulong eventCount)
    {
        if (counts is null || eventCount == 0) return NotApplicable();
        var present = counts.Where(item => item.Count > 0).OrderByDescending(item => item.Count).ToList();
        if (present.Count == 0) return Pick("无元素", "無元素");
        var parts = present.Take(3).Select(item => $"{ElementName(item.Element)} {Percent((double)item.Count / eventCount)}").ToList();
        if (present.Count > 3)
        {
            var rest = present.Skip(3).Aggregate(0UL, (total, item) => total + item.Count);
            parts.Add($"{Pick("其他", "其他")} {Percent((double)rest / eventCount)}");
        }
        return string.Join("、", parts);
    }

    private static IReadOnlyList<CoverageTile> Traits(IReadOnlyList<CombatDamageGroup> skills)
    {
        if (!skills.Any(group => group.BreakedHitCount is not null || group.BreakOccurredCount is not null
                || group.DefaultAttackCount is not null || group.MultiHitCount is not null || group.AdditionalHitCount is not null))
            return [];

        CoverageTile Tile(string name, Func<CombatDamageGroup, UInt64String?> count, string? icon = null)
        {
            var rows = skills.Where(group => count(group) is not null).ToList();
            if (rows.Count == 0) return new(name, Dash, 0, NotApplicable(), icon);
            var hit = rows.Aggregate(0UL, (total, group) => total + count(group)!.Value.Value);
            var all = rows.Aggregate(0UL, (total, group) => total + group.EventCount.Value);
            if (all == 0) return new(name, Dash, 0, NotApplicable(), icon);
            var ratio = (double)hit / all;
            return new(name, Percent(ratio), ratio, $"{Number(hit)} / {Number(all)} {Pick("次", "次")}", icon);
        }

        return
        [
            Tile(ClientIconNames.Name(ClientIconNames.BreakedHit), group => group.BreakedHitCount, ClientIconNames.BreakedHit),
            Tile(ClientIconNames.Name(ClientIconNames.BreakOccurred), group => group.BreakOccurredCount, ClientIconNames.BreakOccurred),

            Tile(Pick("普通攻击", "普通攻擊"), group => group.DefaultAttackCount),
            Tile(ClientIconNames.Name(ClientIconNames.MultiHit), group => group.MultiHitCount, ClientIconNames.MultiHit),
            Tile(Pick("追加打击", "追加打擊"), group => group.AdditionalHitCount)
        ];
    }

    private static ElementBreakdown? ElementTotals(IReadOnlyList<CombatDamageGroup> skills)
    {
        var known = skills.Where(group => group.ElementCounts is not null).ToList();
        if (known.Count == 0) return null;
        var all = known.Aggregate(0UL, (total, group) => total + group.EventCount.Value);
        var totals = known.SelectMany(group => ElementCounts(group.ElementCounts!))
            .GroupBy(item => item.Key)
            .Select(item => (Key: item.Key, Count: item.Aggregate(0UL, (total, part) => total + part.Count)))
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count)
            .ToList();
        if (all == 0 || totals.Count == 0) return new([]);
        return new(totals.Select(item => new ElementPart($"{ElementName(item.Key)} {Percent((double)item.Count / all)}",
            item.Count, ElementColor(item.Key), ElementName(item.Key), Percent((double)item.Count / all), ClientIconNames.Element(item.Key))).ToList());
    }

    private static string Status(CombatBackendState state, bool observing)
    {
        if (!state.Connected) return Pick("未连接后端，连接后显示观测数据", "未連接後端，連接後顯示觀測資料");
        if (state.Workspace is not { } workspace)
            return state.Bound
                ? Pick("尚未开始观测，只读取不操作游戏", "尚未開始觀測，只讀取不操作遊戲")
                : Pick("尚未绑定游戏窗口，绑定后才能开始观测", "尚未綁定遊戲視窗，綁定後才能開始觀測");
        // 游戏连接变化后后端结束旧分段并等待识别本机身份：没有进行中的战斗，原因里带连接变化类的码。
        var reasons = workspace.Observation.Reasons;
        var switching = observing && workspace.Encounter is null or { Summary.Final: true } && reasons.Any(WaitingReasons.Contains);
        if (switching) reasons = reasons.Where(reason => !WaitingReasons.Contains(reason)).ToList();
        // 分段结束（脱离战斗、拆段、停手超时、手动结束等）后，后端在下一笔本机伤害到达时才开新分段；
        // 这段时间快照里仍是刚结束的那一段，页面整页显示它的冻结数值，状态行只称“上一段”，不出现新分段序号。
        var ended = observing && !switching && workspace.Encounter is { Summary.Final: true };
        var parts = new List<string>
        {
            switching ? Pick("观测中，切换地图中，等待识别本机角色", "觀測中，切換地圖中，等待識別本機角色")
                : ended ? Pick($"观测中，{EndedText(workspace.Encounter!.Summary.BoundaryReason)}，等待下一次伤害开始新分段",
                    $"觀測中，{EndedText(workspace.Encounter!.Summary.BoundaryReason)}，等待下一次傷害開始新分段")
                : observing ? Pick("观测中", "觀測中") : ObservationEnded(workspace.Observation.State)
        };
        if (ended)
        {
            var last = workspace.Encounter!;
            parts.Add(last.Segment is { } previous
                ? Pick($"上一段为第 {previous.Ordinal} 段，本场 {Clock(last.Summary.DurationMs)}", $"上一段為第 {previous.Ordinal} 段，本場 {Clock(last.Summary.DurationMs)}")
                : Pick($"上一段本场 {Clock(last.Summary.DurationMs)}", $"上一段本場 {Clock(last.Summary.DurationMs)}"));
        }
        else if (!switching && workspace.Encounter is { } encounter)
        {
            // 分段只是后端对一场战斗记录的编号，后端没有地图名，这里不称作地图。
            if (encounter.Segment is { } segment)
                parts.Add(segment.StartReason == "map-change"
                    ? Pick($"第 {segment.Ordinal} 段（切换地图后开始）", $"第 {segment.Ordinal} 段（切換地圖後開始）")
                    : segment.StartReason == "manual-split"
                    ? Pick($"第 {segment.Ordinal} 段（手动拆段后开始）", $"第 {segment.Ordinal} 段（手動拆段後開始）")
                    : segment.StartReason == "combat-entered"
                    ? Pick($"第 {segment.Ordinal} 段（脱战后开始）", $"第 {segment.Ordinal} 段（脫戰後開始）")
                    : Pick($"第 {segment.Ordinal} 段", $"第 {segment.Ordinal} 段"));
            parts.Add(Pick($"本场 {Clock(encounter.Summary.DurationMs)}", $"本場 {Clock(encounter.Summary.DurationMs)}"));
        }
        parts.Add(Quality(workspace.Observation.Quality, reasons));
        parts.Add(Pick("只读取不操作游戏", "只讀取不操作遊戲"));
        return string.Join(Pick("，", "，"), parts);
    }

    /// <summary>
    /// 主数据：DPS、总伤害、统计时长、命中次数。普通分段显示后端 DPS 分母，DPS 下方附最近 N 秒 DPS；
    /// 范围与定时测量保留完整窗口，时长下方附第一击到最后一击的实际输出时间；结果不完整时 DPS 下方注明。
    /// </summary>
    private static IReadOnlyList<KpiTile> Kpis(CombatWorkspace? workspace, CombatEncounter? shown, bool direct,
        CombatMeasurementResult? measured, CombatRangeSummary? range, bool targeted)
    {
        var summary = shown?.Summary;
        // 滑动窗口长度取后端实际使用的值：有本场数据时取汇总里的，否则取观察设置里的。
        double? windowMs = summary?.RollingWindowMs ?? workspace?.Observation.Settings.RollingWindowMs;
        var window = windowMs is { } ms ? Seconds(ms) : null;
        ulong? hits = shown is null ? null : Sum(shown.Skills, group => group.HitCount);
        // 不完整（Partial）时有数值照常显示，并在下方写明“不完整”与原因；数值确实为空才显示“—”与“DPS 未知”。
        var dps = measured is not null ? measured.Dps : summary?.EncounterDps;
        var known = dps is { } value && double.IsFinite(value);
        var incomplete = measured is not null ? measured.Complete ? null : IncompleteText(measured.Reasons)
            : !direct && range is { Complete: false } ? IncompleteText(range.Reasons)
            : direct && summary is { Quality.KnownValue: ObservationQuality.Partial } ? IncompleteText(summary.Reasons) : null;
        var rolling = direct && shown is not null
            ? (window is null ? Pick("最近 ", "最近 ") : Pick($"最近 {window} 秒 ", $"最近 {window} 秒 ")) + Number(summary?.RollingDps)
            : string.Empty;
        var dpsNote = incomplete is not null && !known ? Pick("不完整，DPS 未知", "不完整，DPS 未知")
            : measured is not null
                ? incomplete is null ? Pick("总伤害 ÷ 设定时长", "總傷害 ÷ 設定時長") : incomplete + Pick("，按已观测伤害计算", "，按已觀測傷害計算")
                : incomplete is not null ? (rolling.Length > 0 ? $"{incomplete} · {rolling}" : incomplete)
                : rolling;
        var activeNote = measured?.ActiveDurationMs is { } active ? Pick($"实际输出 {Clock(active)}", $"實際輸出 {Clock(active)}")
            : targeted && summary is not null ? Pick($"范围时长 {Clock(summary.DurationMs)}", $"範圍時長 {Clock(summary.DurationMs)}")
            : range is { FirstDamageMs: { } first, LastDamageMs: { } last } && !direct ? Pick($"实际输出 {Clock(last - first)}", $"實際輸出 {Clock(last - first)}")
            : string.Empty;
        // 选中目标时：DPS 的分母是对该目标第一击到最后一击的时长（后端 dpsDurationMs），时长块随之标为“对目标时长”。
        var durationMs = measured?.DurationMs ?? (direct || targeted ? summary?.DpsDurationMs ?? summary?.DurationMs : summary?.DurationMs);
        var durationLabel = targeted ? Pick("对目标时长", "對目標時長") : direct ? Pick("DPS时长", "DPS時長") : Pick("战斗时间", "戰鬥時間");
        return
        [
            new("DPS", Number(dps), true, dpsNote),
            new(Pick("总伤害", "總傷害"), summary is null ? Dash : Number(summary.AttributedDamage.Value), false),
            new(durationLabel, durationMs is { } duration ? Clock(duration) : Dash, false, activeNote),
            new(Pick("命中次数", "命中次數"), hits is null ? Dash : Number(hits.Value), false)
        ];
    }

    private static IReadOnlyList<CoverageTile> Coverage(IReadOnlyList<CombatDamageGroup> skills)
    {
        // 分母使用后端给出的可计入次数：暴击用 criticalEligibleCount，其余三项用 mainHitEligibleCount。
        CoverageTile Tile(string icon, Func<CombatDamageGroup, UInt64String?> count,
            Func<CombatDamageGroup, UInt64String?> eligible)
        {
            var name = ClientIconNames.Name(icon);
            var rows = skills.Where(group => count(group) is not null && (eligible(group) ?? group.HitCount) is not null).ToList();
            if (rows.Count == 0) return new(name, Dash, 0, Pick("不适用", "不適用"), icon);
            var hit = rows.Aggregate(0UL, (total, group) => total + count(group)!.Value.Value);
            var all = rows.Aggregate(0UL, (total, group) => total + (eligible(group) ?? group.HitCount)!.Value.Value);
            var ratio = all == 0 ? 0 : (double)hit / all;
            return new(name, all == 0 ? Dash : Percent(ratio), ratio, $"{Number(hit)} / {Number(all)} {Pick("次", "次")}", icon);
        }

        return
        [
            Tile(ClientIconNames.Critical, group => group.CriticalCount, group => group.CriticalEligibleCount),
            Tile(ClientIconNames.PowerfulHit, group => group.PowerfulHitCount, group => group.MainHitEligibleCount),
            Tile(ClientIconNames.RapidHit, group => group.RapidHitCount, group => group.MainHitEligibleCount),
            Tile(ClientIconNames.DoubleHit, group => group.DoubleHitCount, group => group.MainHitEligibleCount)
        ];
    }

    private static IReadOnlyList<AttributionPart> Attribution(CombatEncounter encounter)
    {
        var local = Total(encounter.LocalDamage);
        var owned = Total(encounter.OwnedDamage);
        var unknown = Total(encounter.UnknownDamage);
        var all = local + owned + unknown;
        if (all.IsZero) return [];
        string Share(BigInteger value) => Percent((double)value / (double)all);
        return
        [
            new($"{Pick("本人", "本人")} {Share(local)}", (double)local, "#B23A1C", true),
            new($"{Pick("召唤物", "召喚物")} {Share(owned)}", (double)owned, "#E99A7F", true),
            new(Pick($"未归属 {Share(unknown)}，不计入本人", $"未歸屬 {Share(unknown)}，不計入本人"), (double)unknown, "#B9BECC", false)
        ];
    }

    // 技能按伤害从高到低；目标已按统一的怪物排序传入（keepOrder），不随伤害数值跳动。
    private static IReadOnlyList<DamageRow> Rows(IReadOnlyList<CombatDamageGroup> groups, BigInteger total, bool targets, bool keepOrder = false) =>
        (keepOrder ? groups : groups.OrderByDescending(group => group.AttributedDamage.Value).ToList()).Select(group =>
        {
            var damage = group.AttributedDamage.Value;
            var mainHits = group.MainHitEligibleCount ?? group.HitCount;
            return new DamageRow(RowName(group, targets), Number(damage),
                total.IsZero ? Dash : Percent((double)damage / (double)total),
                group.HitCount is { } hits ? Number(hits.Value) : NotApplicable(),
                Rate(group.CriticalCount, group.CriticalEligibleCount ?? group.HitCount),
                Rate(group.PowerfulHitCount, mainHits), Rate(group.RapidHitCount, mainHits),
                Rate(group.DoubleHitCount, mainHits),
                group is { MinimumDamage: { } minimum, MaximumDamage: { } maximum }
                    ? $"{Number(minimum.Value)}～{Number(maximum.Value)}"
                    : NotApplicable(),
                Rate(group.BreakedHitCount, group.EventCount), Rate(group.BreakOccurredCount, group.EventCount),
                Rate(group.DefaultAttackCount, group.EventCount),
                Rate(group.MultiHitCount, group.EventCount),
                ElementSummary(group.ElementCounts is { } elements ? ElementCounts(elements).ToList() : null,
                    group.EventCount.Value), GroupDisplay(group, targets).Tip, TagCellsOf(group),
                total.IsZero ? 0 : (double)damage / (double)total);
        }).ToList();

    // 分组的显示名：后端 resolvedName 的游戏原文；名称缺失时为“未知技能／未知目标”（名称表读取中为“名称读取中”），提示写出编号与原因；
    // 超出分组上限的合并组写成“其他技能／其他目标”。分组的 name 是原始标识，只在没有 resolvedName 的旧后端上参与判断。
    private static CombatName GroupDisplay(CombatDamageGroup group, bool target) => group.Key switch
    {
        "other-targets" => Fixed(Pick("其他目标", "其他目標")),
        "other-skills" => Fixed(Pick("其他技能", "其他技能")),
        _ => CombatNames.Of(group.ResolvedName, target ? CombatNameKind.Target : CombatNameKind.Skill, group.Name, group.Key,
            target ? TargetNumber(group) : null)
    };

    // 目标的稳定编号：当前观测里的实体取原始实体 ID；名称字段是原始编号时取它；其余取分组键的可读形式。
    private static string? TargetNumber(CombatDamageGroup group) =>
        targetNumbers?.GetValueOrDefault(group.Key) ?? (CombatNames.Missing(group.Name) && !string.IsNullOrWhiteSpace(group.Name)
            ? group.Name : CombatNames.Readable(group.Key));

    private static string GroupName(CombatDamageGroup group, bool target) => GroupDisplay(group, target).Text;

    private static CombatName Fixed(string text) => new(text, text);

    // 提示与名称相同时不另存，界面回退为显示名称本身。
    private static string Hint(CombatName name) => name.Tip == name.Text ? string.Empty : name.Tip;

    /// <summary>
    /// 实体的显示名：怪物取 resolvedName 的原文；玩家（战斗协议不带玩家名）取显示名，本机由抓包识别的昵称补上，
    /// 其他玩家缺失时为“未知玩家”；实体未观测到时为“未知目标”。编号都只在悬停提示里。
    /// </summary>
    internal static CombatName EntityName(CombatEntity? entity, EntityKey key)
    {
        var id = key.RawInstanceId.ToString();
        if (entity is null) return CombatNames.Of(null, CombatNameKind.Target, null, id, id);
        return entity.Role.KnownValue is CombatEntityRole.Player or CombatEntityRole.LocalPlayer
            ? CombatNames.Of(null, CombatNameKind.Player, entity.DisplayName, id)
            : CombatNames.Of(entity.ResolvedName, CombatNameKind.Target, entity.DisplayName, id, id);
    }

    // 整组都是间接或持续伤害时，在名称后标出类别。
    private static string RowName(CombatDamageGroup group, bool target)
    {
        var name = GroupName(group, target);
        if (group.ClassificationCounts is not { } counts) return name;
        var (direct, indirect, overTime) = (counts.Direct.Value, counts.Indirect.Value, counts.DamageOverTime.Value);
        if (overTime > 0 && direct == 0 && indirect == 0) return $"{name} (Dot)";
        if (indirect > 0 && direct == 0 && overTime == 0) return $"{name} ({Pick("间接", "間接")})";
        return name;
    }

    // 效果剩余时间从读取快照的本机时刻起按本地时钟递减；测试与样本没有该时刻时取映射时刻。
    private static long EffectsAt(CombatBackendState state) => state.EffectsAt != 0 ? state.EffectsAt : System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>
    /// 实体效果的条目：同一效果编号的实例合成一行，层数为各实例层数之和（实例没有层数时按 1 层），剩余时间取最短的有限实例，
    /// 全部常驻时标为常驻；后端预计到期或已移除的实例不列。顺序与总时长由 <see cref="CombatEffectOrder"/> 在适配层统一给出。
    /// </summary>
    private static IReadOnlyList<EffectRow> Effects(CombatEntityEffects? effects, IReadOnlyList<CombatEntity> entities,
        EntityKey? local, IReadOnlyList<CombatEffectCoverage> coverage, long observedAt, string scope)
    {
        if (effects is null) return [];
        return effects.Views.Where(view => view.Instance.Presence.KnownValue is not (EffectPresence.ConfirmedRemoved or EffectPresence.PredictedExpired))
            .GroupBy(view => view.Instance.EffectId.ToString()).Select(group =>
        {
            var views = group.ToList();
            var first = views[0];
            var instance = first.Instance;
            var finite = views.Where(view => view.Instance.DurationKind.KnownValue != EffectDurationKind.Infinite && view.RemainingMs is not null)
                .Select(view => Math.Max(0, view.RemainingMs!.Value)).ToList();
            var infinite = views.All(view => view.Instance.DurationKind.KnownValue == EffectDurationKind.Infinite);
            int? stacks = views.Count == 1 ? instance.Stacks : views.Sum(view => view.Instance.Stacks is { } count and > 0 ? count : 1);
            var meta = new List<string>();
            if (stacks is { } layers) meta.Add(Pick($"{layers} 层", $"{layers} 層"));
            if (views.Count > 1) meta.Add(Pick($"{views.Count} 个实例", $"{views.Count} 個實例"));
            meta.Add(instance.CasterKey is not { } caster
                ? Pick("施放者未知", "施放者未知")
                : caster == local
                    ? Pick("施放者：本人", "施放者：本人")
                    : $"{Pick("施放者：", "施放者：")}{EntityName(entities.FirstOrDefault(entity => entity.Key == caster), caster).Text}");
            if (views.Any(view => view.Aggregated)) meta.Add(Pick("可能是多人合计", "可能是多人合計"));
            meta.Add(instance.Quality.KnownValue == ObservationQuality.Confirmed
                ? Pick("服务器时长", "伺服器時長")
                : Pick("客户端估计", "用戶端估計"));
            double? remainingMs = infinite || finite.Count == 0 ? null : finite.Min();
            var remaining = infinite ? Pick("持续", "持續")
                : remainingMs is { } ms ? $"{Math.Ceiling(ms / 1000):0}s" : Pick("未知", "未知");
            var ratio = coverage.FirstOrDefault(item => item.EntityId == effects.Id && item.EffectId == instance.EffectId)?.Ratio;
            var covered = views.Any(view => view.Stale)
                ? Pick("数据已过期", "資料已過期")
                : ratio is { } value ? Pick($"覆盖 {Percent(value)}", $"覆蓋 {Percent(value)}") : string.Empty;
            // 效果名称取效果视图的 resolvedName（客户端 Buff 表原文）；缺失时为“未知效果”，编号与原因在悬停提示里。
            var name = CombatNames.Of(first.ResolvedName, CombatNameKind.Effect, null, instance.EffectId.ToString());
            return new EffectRow(string.Empty, name.Text, string.Join(Pick("，", "，"), meta), remaining, covered, instance.EffectId.ToString(),
                name.Tip, Polarity(first.Polarity), stacks, remainingMs, observedAt, infinite, scope);
        }).ToList();
    }

    /// <summary>效果的极性：后端按客户端 Buff 表 AffectType 给出 buff、debuff、none，没有时为未知（单列“未分类”，不推测）。</summary>
    public static EffectPolarity Polarity(string? value) => value switch
    {
        "buff" => EffectPolarity.Positive,
        "debuff" => EffectPolarity.Negative,
        "none" => EffectPolarity.Neutral,
        _ => EffectPolarity.Unknown
    };

    // 破防值是小数（例如 12.07），保留一位。

    // 破防值过期的原因：离开可观测范围、观测停止或抓包缺口。
    private static string GaugeReason(string? reason) => reason switch
    {
        "entity-out-of-scope" => Pick("目标已离开可观测范围", "目標已離開可觀測範圍"),
        "observation-stopped" or "observation-interrupted" => Pick("观测已停止", "觀測已停止"),
        null or "" => Pick("原因未知", "原因未知"),
        var other when other.Contains("connection", StringComparison.OrdinalIgnoreCase) => Pick("期间切换了地图", "期間切換了地圖"),
        var other => Localization.CodeText.Unknown("combat-gap", other, "抓包缺口", "抓包缺口")
    };

    private static EncounterRow History(CombatEncounter encounter)
    {
        var top = encounter.Targets.OrderByDescending(group => group.AttributedDamage.Value).FirstOrDefault();
        var first = top is null ? null : GroupName(top, target: true);
        var target = first is null
            ? Pick("没有目标", "沒有目標")
            : encounter.Targets.Count == 1
                ? first
                : $"{first}{Pick($" 等 {encounter.Targets.Count} 个目标", $" 等 {encounter.Targets.Count} 個目標")}";
        return new(encounter.Id, encounter.StartedAt.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture), target,
            Clock(encounter.Summary.DurationMs),
            (encounter.Record?.Measurement?.Dps ?? encounter.Summary.EncounterDps) is { } dps ? $"DPS {Number(dps)}" : Pick("DPS 未知", "DPS 未知"),
            Quality(encounter.Summary.Quality, encounter.Summary.Reasons), encounter.Favorite, SegmentText(encounter.Segment, encounter.Summary),
            encounter.Record is null ? string.Empty : RecordLabel(encounter), DataIncomplete(encounter));
    }

    // 旧记录没有分段，返回空串，页面不显示这一项。
    private static string SegmentText(CombatSegment? segment, CombatSummary summary)
    {
        if (segment is null) return string.Empty;
        // 已结束的分段在开始原因后附结束原因；进行中的不显示。
        var end = summary.Final && !string.IsNullOrEmpty(summary.BoundaryReason) ? Pick($" · 结束：{EndReason(summary.BoundaryReason)}", $" · 結束：{EndReason(summary.BoundaryReason)}") : string.Empty;
        if (string.IsNullOrEmpty(segment.StartReason)) return Pick($"第 {segment.Ordinal} 段", $"第 {segment.Ordinal} 段") + end;
        var reason = segment.StartReason switch
        {
            "first-damage" => Pick("首次伤害", "首次傷害"),
            "combat-entered" => Pick("脱战后再战", "脫戰後再戰"),
            "manual-start" => Pick("手动开始", "手動開始"),
            "manual-split" => Pick("手动拆段", "手動拆段"),
            "reset" => Pick("重置", "重置"),
            "map-change" => Pick("切换地图", "切換地圖"),
            var other => Localization.CodeText.Reason(other)
        };
        return Pick($"第 {segment.Ordinal} 段 · {reason}", $"第 {segment.Ordinal} 段 · {reason}") + end;
    }

    // 观测已结束的状态行：停止、中断、失败等状态写成中文。
    private static string ObservationEnded(string? state) => state switch
    {
        "stopped" => Pick("观测已停止", "觀測已停止"),
        "interrupted" => Pick("观测已中断", "觀測已中斷"),
        "failed" => Pick("观测失败", "觀測失敗"),
        _ => Localization.CodeText.Unknown("combat-observation", state, "观测已结束", "觀測已結束")
    };

    // 状态行与范围标题里“上一段”的结束说明。
    private static string EndedText(string reason) => reason switch
    {
        "combat-left" => Pick("已脱离战斗", "已脫離戰鬥"),
        "manual-split" => Pick("已拆段", "已拆段"),
        "idle-timeout" => Pick("停手超时，上一段已结束", "停手逾時，上一段已結束"),
        "manual-end" => Pick("本场已手动结束", "本場已手動結束"),
        _ => Pick("上一段已结束", "上一段已結束")
    };

    private static string EndReason(string reason) => reason switch
    {
        "combat-left" => Pick("脱离战斗", "脫離戰鬥"),
        "idle-timeout" => Pick("停手超时", "停手逾時"),
        "connection-changed" => Pick("切换地图", "切換地圖"),
        "manual-split" => Pick("手动拆段", "手動拆段"),
        "manual-end" => Pick("手动结束", "手動結束"),
        "reset" => Pick("重置", "重置"),
        "stream-reset" => Pick("数据流重置", "資料流重置"),
        "observation-reconnected" => Pick("观测重连", "觀測重連"),
        var other => Localization.CodeText.Reason(other)
    };

    /// <summary>本机战斗状态（观察状态的 localCombatState）的显示文字；来源给出列表外的取值时为“战斗状态未知”。</summary>
    internal static string CombatStateText(OpenEnum<CombatLocalState> state) => state.KnownValue switch
    {
        CombatLocalState.InCombat => Pick("战斗中", "戰鬥中"),
        CombatLocalState.NonCombat or CombatLocalState.CombatEnd => Pick("战斗外", "戰鬥外"),
        CombatLocalState.CombatReady => Pick("战斗外（准备）", "戰鬥外（準備）"),
        _ => Pick("战斗状态未知", "戰鬥狀態未知")
    };

    private static string Quality(OpenEnum<ObservationQuality> quality, IReadOnlyList<string> reasons)
    {
        var text = quality.KnownValue switch
        {
            ObservationQuality.Confirmed => Pick("观测完整", "觀測完整"),
            ObservationQuality.Partial => Pick("部分观测", "部分觀測"),
            ObservationQuality.Estimated => Pick("估计值", "估計值"),
            ObservationQuality.Stale => Pick("数据已过期", "資料已過期"),
            ObservationQuality.Conflict => Pick("观测冲突", "觀測衝突"),
            _ => Pick("观测质量未知", "觀測品質未知")
        };
        return reasons.Count == 0 ? text : $"{text}（{string.Join("、", ReasonTexts(reasons))}）";
    }

    private static ulong? Sum(IReadOnlyList<CombatDamageGroup> groups, Func<CombatDamageGroup, UInt64String?> pick)
    {
        var values = groups.Select(pick).Where(value => value is not null).Select(value => value!.Value.Value).ToList();
        return values.Count == 0 ? null : values.Aggregate(0UL, (total, value) => total + value);
    }

    private static BigInteger Total(CombatDamageTotals totals) =>
        totals.ActualHp.Value + totals.Shield.Value + totals.Overkill.Value + totals.Unclassified.Value;

    private static string Rate(UInt64String? count, UInt64String? eligible) =>
        count is { } hit && eligible is { Value: > 0 } all ? Percent((double)hit.Value / all.Value) : NotApplicable();

    private static string NotApplicable() => Pick("不适用", "不適用");

    // 窗口长度按秒显示，整秒不带小数。
    private static string Seconds(double milliseconds) =>
        Math.Round(milliseconds / 1000, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Percent(double ratio) => $"{Math.Round(ratio * 100)}%";

    private static string Number(double? value) =>
        value is { } known && double.IsFinite(known) ? Math.Round(known).ToString("N0", CultureInfo.InvariantCulture) : Dash;

    private static string Number(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Number(BigInteger value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Clock(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }
}
