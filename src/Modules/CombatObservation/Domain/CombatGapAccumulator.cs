using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

/// <summary>缺口明细有界，合并保留完整区间和丢弃总数。</summary>
public sealed class CombatGapAccumulator
{
    private readonly List<CombatGapInput> values = [];
    public void Add(CombatGapInput value)
    {
        if (values.Count < 256) values.Add(value);
        else
        {
            var previous = values[^1];
            values[^1] = new("combat.gapCapacity", Math.Min(previous.FromMs, value.FromMs),
                previous.ToMs is { } before && value.ToMs is { } after ? Math.Max(before, after) : null,
                new(checked(previous.DroppedEvents.Value + value.DroppedEvents.Value)));
        }
    }
    public void AddRange(IEnumerable<CombatGapInput> gaps) { foreach (var value in gaps) Add(value); }
    public IReadOnlyList<CombatGapInput> Snapshot() => values.ToArray();
    public static CombatGap Merge(CombatGap before, CombatGap after) => new("combat.gapCapacity",
        Math.Min(before.FromMs, after.FromMs), before.ToMs is { } first && after.ToMs is { } second ? Math.Max(first, second) : null,
        new(checked(before.DroppedEvents.Value + after.DroppedEvents.Value)));
    public static IReadOnlyList<CombatGap> Compact(IEnumerable<CombatGap> gaps)
    {
        var result = new CombatGapAccumulator();
        result.AddRange(gaps.Select(value => new CombatGapInput(value.Reason, value.FromMs, value.ToMs, value.DroppedEvents)));
        return result.Snapshot().Select(value => new CombatGap(value.Reason, value.FromMs, value.ToMs, value.DroppedEvents)).ToArray();
    }
}
