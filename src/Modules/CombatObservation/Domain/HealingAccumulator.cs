using System.Numerics;
using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

/// <summary>
/// 单个分段的治疗统计（口径 observed-healing-v1）。施放按目标分组（至多 <see cref="MaximumTargets"/> 组），
/// 滚动缓存与伤害同为 60 秒并受去重身份上限约束；受到的有效量与过量只在全部受疗都已配对 HP 上升时返回。
/// </summary>
internal sealed class HealingAccumulator(CombatLimits limits)
{
    public const string CalculationVersion = "observed-healing-v1";
    public const int MaximumTargets = 64;
    private const string OtherTargets = "other-targets";
    private sealed class Target(string name, bool self)
    {
        public string Name { get; } = name;
        public bool Self { get; } = self;
        public BigInteger Amount;
        public ulong Count;
    }
    private readonly Dictionary<string, Target> targets = new(StringComparer.Ordinal);
    private readonly Queue<(double At, BigInteger Amount)> rolling = [];
    private BigInteger cast, castSelf, castOther, received, receivedSelf, receivedOther, effective, overheal;
    private ulong castCount, receivedCount, paired;
    private string? unpairedReason;
    private bool rollingOverflow, groupOverflow;
    public long Bytes => targets.Count * 1024L + rolling.Count * 128L;

    public void Add(CombatHealEvent value, bool targetIsLocal, string targetId, string targetName, Func<bool> groupCapacity)
    {
        BigInteger amount = value.Amount.Value;
        if (value.Direction.KnownValue != CombatHealDirection.Cast) { Receive(value, false); return; }
        cast += amount; castCount = checked(castCount + 1);
        LastCastMs = Math.Max(LastCastMs ?? value.MonotonicMs, value.MonotonicMs);
        if (targetIsLocal) castSelf += amount; else castOther += amount;
        var key = targetId;
        if (!targets.ContainsKey(key) && (targets.Count >= MaximumTargets - 1 || !groupCapacity()))
        { key = OtherTargets; groupOverflow = true; }
        if (!targets.TryGetValue(key, out var target))
            targets.Add(key, target = key == OtherTargets ? new(OtherTargets, false) : new(targetName, targetIsLocal));
        target.Amount += amount; target.Count = checked(target.Count + 1);
        while (rolling.TryPeek(out var first) && first.At < value.MonotonicMs - CombatSession.MaximumRollingWindowMs) rolling.Dequeue();
        if (rolling.Count >= limits.MaximumDeduplicationEntries) { rolling.Dequeue(); rollingOverflow = true; }
        rolling.Enqueue((value.MonotonicMs, amount));
        if (value.ReceiptMerged) Receive(value, true);
    }
    private void Receive(CombatHealEvent value, bool self)
    {
        BigInteger amount = value.Amount.Value;
        received += amount; receivedCount = checked(receivedCount + 1);
        if (self) receivedSelf += amount; else receivedOther += amount;
        if (value.EffectiveAmount is { } known && value.Overheal is { } extra)
        { effective += known.Value; overheal += extra.Value; paired = checked(paired + 1); return; }
        // 有 HP 变化但数值不一致优先说明；否则为未配对。
        unpairedReason = value.EffectiveReason == "hp-mismatch" || unpairedReason == "hp-mismatch" ? "hp-mismatch" : "hp-unpaired";
    }
    public ulong CastCount => castCount;
    /// <summary>本段最后一次计入的施放治疗的单调时点；HPS 的停手冻结以它与最后有效伤害中较晚者为截止点。</summary>
    public double? LastCastMs { get; private set; }
    public ulong ReceivedCount => receivedCount;
    /// <summary>并入另一段的治疗累计（范围汇总跨分段时使用）；目标按键合并，超出上限的并入 other-targets。</summary>
    public void Merge(HealingAccumulator other)
    {
        cast += other.cast; castSelf += other.castSelf; castOther += other.castOther;
        received += other.received; receivedSelf += other.receivedSelf; receivedOther += other.receivedOther;
        effective += other.effective; overheal += other.overheal;
        castCount = checked(castCount + other.castCount); receivedCount = checked(receivedCount + other.receivedCount);
        paired = checked(paired + other.paired);
        if (other.unpairedReason is { } reason)
            unpairedReason = reason == "hp-mismatch" || unpairedReason == "hp-mismatch" ? "hp-mismatch" : "hp-unpaired";
        rollingOverflow |= other.rollingOverflow; groupOverflow |= other.groupOverflow;
        if (other.LastCastMs is { } last && (LastCastMs is null || last > LastCastMs)) LastCastMs = last;
        foreach (var (key, value) in other.targets)
        {
            var name = key;
            if (!targets.ContainsKey(name) && targets.Count >= MaximumTargets - 1) { name = OtherTargets; groupOverflow = true; }
            if (!targets.TryGetValue(name, out var target))
                targets.Add(name, target = name == OtherTargets ? new(OtherTargets, false) : new(value.Name, value.Self));
            target.Amount += value.Amount; target.Count = checked(target.Count + value.Count);
        }
    }
    public CombatHealingSummary Snapshot(double durationMs, double rollingStart, double end, bool partial, bool includeTargets)
    {
        var reasons = new List<string>();
        if (rollingOverflow) reasons.Add("healing-rolling-capacity");
        if (groupOverflow) reasons.Add("healing-group-capacity");
        var rollingDuration = Math.Max(0, end - rollingStart);
        var rollingTotal = rolling.Where(x => x.At >= rollingStart && x.At <= end).Aggregate(BigInteger.Zero, (sum, x) => sum + x.Amount);
        var hps = partial ? null : CombatRate.Calculate(cast, durationMs);
        var rollingHps = partial || rollingOverflow ? null : CombatRate.Calculate(rollingTotal, rollingDuration);
        if (!partial && (durationMs > 0 && hps is null || !rollingOverflow && rollingDuration > 0 && rollingHps is null)) reasons.Add("rateOutOfRange");
        var complete = unpairedReason is null;
        return new(CalculationVersion, new(cast), new(castCount), new(castSelf), new(castOther),
            new(received), new(receivedCount), new(receivedSelf), new(receivedOther),
            complete ? new(effective) : null, complete ? new(overheal) : null, new(paired), unpairedReason,
            hps, rollingHps, reasons,
            includeTargets ? targets.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new CombatHealingTarget(x.Key, x.Value.Name, new(x.Value.Amount), new(x.Value.Count), x.Value.Self)).ToArray() : null,
            durationMs);
    }
}
