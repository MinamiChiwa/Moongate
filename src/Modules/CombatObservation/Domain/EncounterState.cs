using System.Numerics;
using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

internal sealed class EncounterState(string id, string owner, string observation, EntityKey local, string displayName,
    DateTimeOffset startedAt, double startedMs, bool manual, bool includeOwned, CombatLimits limits, CombatSegment? segment = null,
    bool healing = false, bool trackRolling = true, bool useCompleteWindowDps = false)
{
    public string Id { get; } = id;
    public readonly DamageAccumulator Attributed = new(), Direct = new(), Owned = new(), Unknown = new();
    private readonly Dictionary<string, (DamageAccumulator Value, string Name, CombatResolvedName? Label)> skills = [], targets = [];
    private readonly Queue<(double At, BigInteger Amount)> rolling = [];
    public readonly List<CombatGap> Gaps = [];
    public double StartedMs { get; } = startedMs;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public bool Manual { get; } = manual;
    public CombatSegment? Segment { get; } = segment;
    public double? FirstDamageMs;
    public double? LastDamageMs;
    public DateTimeOffset? LastDamageAt;
    public double? EndedMs;
    public DateTimeOffset? EndedAt;
    public string Boundary = manual ? "manual-start" : "active";
    public ulong TimelineCount, TimelineBytes, Truncated;
    /// <summary>效果与实体变化在本段时间线里占用的条数与字节；有独立配额，超出时只计截断，不挤占伤害与治疗。</summary>
    public ulong EffectTimelineCount, EffectTimelineBytes;
    public UInt64String BoundaryRevision;
    /// <summary>定时测量或保存的记录；自动分段为空。</summary>
    public CombatRecordInfo? Record;
    public bool Partial => partial;
    /// <summary>来源支持治疗时的分段治疗统计；不支持时为空，快照不返回治疗块。</summary>
    public HealingAccumulator? Healing { get; } = healing ? new(limits) : null;
    public long GroupBytes => skills.Count * 512L + targets.Count * 1024L + rolling.Count * 128L + (Healing?.Bytes ?? 0);
    public bool Final => EndedMs is not null;
    /// <summary>摘要中的本机显示名；本机昵称晚于分段开始才识别时由会话替换实体 ID 占位。</summary>
    public string DisplayName { get; set; } = displayName;
    private bool partial;
    /// <summary>本段的破防区间，时点相对本段开始；由会话按本机伤害标记与目标破防值归零写入。</summary>
    public BreakIntervals Breaks { get; } = new();
    public void Gap(CombatGap gap)
    {
        partial = true;
        if (Gaps.Count < 256) Gaps.Add(gap);
        else Gaps[^1] = CombatGapAccumulator.Merge(Gaps[^1], gap);
    }
    /// <param name="targetLabel">怪物目标的名称占位（客户端表 ID），由读取侧按当前名称表解析；玩家与未知目标为空。</param>
    public void Add(CombatDamageEvent value, string attribution, string targetId, string targetName, Func<bool> groupCapacity,
        CombatResolvedName? targetLabel = null)
    {
        switch (attribution)
        {
            case "local": Direct.Add(value); break;
            case "owned": Owned.Add(value); break;
            case "unknown": Unknown.Add(value); return;
            default: return;
        }
        if (attribution == "owned" && !includeOwned) return;
        Attributed.Add(value); LastDamageMs = Math.Max(LastDamageMs ?? value.MonotonicMs, value.MonotonicMs);
        FirstDamageMs = Math.Min(FirstDamageMs ?? value.MonotonicMs, value.MonotonicMs);
        if (LastDamageAt is null || value.MonotonicMs >= LastDamageMs) LastDamageAt = value.ObservedAt;
        var skill = value.SkillId.ToString();
        AddGroup(skills, skill, skill, null, limits.MaximumSkills, "other-skills");
        AddGroup(targets, targetId, targetName, targetLabel, limits.MaximumEntities, "other-targets");
        if (!trackRolling) return;
        while (rolling.TryPeek(out var first) && first.At < value.MonotonicMs - CombatSession.MaximumRollingWindowMs) rolling.Dequeue();
        if (rolling.Count >= limits.MaximumDeduplicationEntries)
        {
            rolling.Dequeue(); Gap(new("rolling-capacity", value.MonotonicMs, value.MonotonicMs, new(0)));
        }
        rolling.Enqueue((value.MonotonicMs, value.Amount.Value));
        void AddGroup(Dictionary<string, (DamageAccumulator Value, string Name, CombatResolvedName? Label)> groups, string key, string name,
            CombatResolvedName? label, int maximum, string fallback)
        {
            if (!groups.ContainsKey(key) && (groups.Count >= maximum - 1 || !groupCapacity()))
            { key = fallback; name = fallback; label = null; Gap(new("group-capacity", value.MonotonicMs, value.MonotonicMs, new(0))); }
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = (new(), name, label));
            // 分组创建时实体还没有类型信息：之后的命中带来名称占位时补写。
            else if (group.Label is null && label is not null) groups[key] = group with { Label = label };
            group.Value.Add(value);
        }
    }
    public CombatEncounter Snapshot(UInt64String revision, double now, int window, CombatResourceUsage resources)
    {
        var end = EndedMs ?? now; var duration = Math.Max(0, end - StartedMs);
        var dpsEnd = useCompleteWindowDps ? end : LastDamageMs ?? StartedMs;
        var dpsDuration = Math.Max(0, dpsEnd - StartedMs);
        var rollingStart = Math.Max(StartedMs, dpsEnd - window); var rollingDuration = Math.Max(0, dpsEnd - rollingStart);
        var rollingTotal = rolling.Where(x => x.At >= rollingStart && x.At <= dpsEnd).Aggregate(BigInteger.Zero, (sum, x) => sum + x.Amount);
        var reasons = Gaps.Select(x => x.Reason).Distinct(StringComparer.Ordinal).ToList();
        if (dpsDuration == 0) reasons.Add("insufficientDuration");
        // 不完整的分段仍按已接受事件给出速率，quality 与 reasons 标注不完整；只有滚动缓存被挤出时近期速率无从计算。
        var rollingLost = Gaps.Any(x => x.Reason == "rolling-capacity");
        var dps = CombatRate.Calculate(Attributed.Total, dpsDuration);
        var rollingDps = rollingLost ? null : CombatRate.Calculate(rollingTotal, rollingDuration);
        if (dpsDuration > 0 && dps is null || !rollingLost && rollingDuration > 0 && rollingDps is null) reasons.Add("rateOutOfRange");
        // HPS 与 DPS 同一停手冻结：普通分段以最后有效伤害与最后计入的施放治疗中较晚者为截止点，停手期间分母与近期窗口冻结；
        // 定时测量与范围汇总（完整窗口口径）仍按整个窗口。
        var hpsEnd = useCompleteWindowDps ? end
            : Math.Max(LastDamageMs ?? StartedMs, Healing?.LastCastMs ?? StartedMs);
        var healing = Healing?.Snapshot(Math.Max(0, hpsEnd - StartedMs), Math.Max(StartedMs, hpsEnd - window), hpsEnd, false, true);
        var summary = new CombatSummary(Id, local, DisplayName, new(Attributed.Total), duration,
            dps, rollingDps,
            window, useCompleteWindowDps ? "observed-damage-v1" : "observed-damage-v2", Final, Boundary, partial ? ObservationQuality.Partial : ObservationQuality.Confirmed,
            reasons.ToArray(), owner, observation, revision, healing is null ? null : healing with { Targets = null }, dpsDuration);
        return new(Id, owner, observation, local.GameSessionId, local.ObservationGeneration, revision, summary, StartedAt, EndedAt,
            StartedMs, EndedMs, includeOwned, Attributed.Snapshot(), Direct.Snapshot(), Owned.Snapshot(), Unknown.Snapshot(),
            skills.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value.Value.Group(x.Key, x.Value.Name)).ToArray(),
            targets.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value.Value.Group(x.Key, x.Value.Name) with { ResolvedName = x.Value.Label }).ToArray(),
            Gaps.ToArray(), resources, false, BoundaryRevision, Segment, healing, Record, BreakSummary(end));
    }
    // 进行中的区间截止到当前时点；本段结束后截止到最后一次无防备命中（估计终点）。
    private CombatBreakSummary BreakSummary(double end)
    {
        var total = Attributed.Group("total", "total");
        return BreakIntervals.Summarize(Breaks.All(Final ? null : Math.Max(0, end - StartedMs)), total.BreakedHitDamage, total.BreakedHitCount,
            Math.Max(0, end - StartedMs), Breaks.Truncated);
    }
    /// <summary>
    /// 并入另一段同口径的累计（范围汇总跨分段时使用）。分组按键合并，超出上限的并入 other-* 并记容量缺口；
    /// 缺口与不完整状态一并带入。滚动窗口不合并，范围汇总不使用滚动 DPS。
    /// </summary>
    public void Merge(EncounterState other)
    {
        Attributed.Merge(other.Attributed); Direct.Merge(other.Direct); Owned.Merge(other.Owned); Unknown.Merge(other.Unknown);
        MergeGroups(skills, other.skills, limits.MaximumSkills, "other-skills");
        MergeGroups(targets, other.targets, limits.MaximumEntities, "other-targets");
        if (other.Healing is { } heal) Healing?.Merge(heal);
        foreach (var gap in other.Gaps) Gap(gap);
        partial |= other.partial;
        if (other.FirstDamageMs is { } first && (FirstDamageMs is null || first < FirstDamageMs)) FirstDamageMs = first;
        if (other.LastDamageMs is { } last && (LastDamageMs is null || last >= LastDamageMs)) { LastDamageMs = last; LastDamageAt = other.LastDamageAt; }
        void MergeGroups(Dictionary<string, (DamageAccumulator Value, string Name, CombatResolvedName? Label)> into, Dictionary<string, (DamageAccumulator Value, string Name, CombatResolvedName? Label)> from, int maximum, string fallback)
        {
            foreach (var (key, value) in from)
            {
                var (target, name, label) = (key, value.Name, value.Label);
                if (!into.ContainsKey(target) && into.Count >= maximum - 1)
                { target = fallback; name = fallback; label = null; Gap(new("group-capacity", 0, 0, new(0))); }
                if (!into.TryGetValue(target, out var group)) into.Add(target, group = (new(), name, label));
                group.Value.Merge(value.Value);
            }
        }
    }
}

internal static class CombatRate
{
    public static double? Calculate(BigInteger amount, double durationMs)
    {
        if (durationMs <= 0) return null;
        var result = (double)amount * 1000 / durationMs;
        return double.IsFinite(result) ? result : null;
    }
}
