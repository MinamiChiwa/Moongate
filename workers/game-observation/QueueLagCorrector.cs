using MabiTool.Contracts;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 写出时校正排队超龄的帧。超过 <see cref="ObservationCaptureRun.MaximumQueueLagMs"/> 的战斗帧改用写出侧时点
/// （不早于已写出帧），帧内事件随之改时，连续超龄段首帧记一条 combat.captureLag 缺口；库存帧只改时点。
/// 记录数已满的战斗帧不再携带事件，其事件计入缺口丢弃数。战斗与库存各自按写出顺序调用，内部加锁。
/// </summary>
internal sealed class QueueLagCorrector
{
    private readonly object gate = new();
    private const int MaximumRecords = 256;
    private double combatFloor = double.NegativeInfinity, inventoryFloor = double.NegativeInfinity;
    private bool combatLagging;
    public int CorrectedCombatFrames { get; private set; }
    public int CorrectedInventoryFrames { get; private set; }

    public CombatCaptureReport Combat(CombatCaptureReport value, double now) { lock (gate) return CombatCore(value, now); }
    public InventoryCaptureReport Inventory(InventoryCaptureReport value, double now) { lock (gate) return InventoryCore(value, now); }
    private CombatCaptureReport CombatCore(CombatCaptureReport value, double now)
    {
        var frame = value.Frame;
        var threshold = now - ObservationCaptureRun.MaximumQueueLagMs;
        if (frame.MonotonicMs >= threshold || !double.IsFinite(frame.MonotonicMs))
        {
            combatLagging = false; combatFloor = Math.Max(combatFloor, frame.MonotonicMs); return value;
        }
        CorrectedCombatFrames++;
        var stamp = Math.Max(threshold, combatFloor);
        combatFloor = stamp;
        var events = frame.Damage.Count + frame.Effects.Count + (frame.Healing?.Count ?? 0) + (frame.CombatStates?.Count ?? 0) +
            frame.Reconciliations.Sum(x => Math.Max(1, x.Effects.Count));
        var records = frame.Entities.Count + frame.Gaps.Count + events;
        var addGap = !combatLagging;
        combatLagging = true;
        if (addGap && records + 1 > MaximumRecords)
            return value with { Frame = frame with { MonotonicMs = stamp, Damage = [], Effects = [], Reconciliations = [], Healing = null, CombatStates = null,
                Gaps = [.. frame.Gaps, new("combat.captureLag", frame.MonotonicMs, stamp, new((ulong)events))] } };
        var damage = frame.Damage.Select(item => item with { MonotonicMs = stamp }).ToArray();
        var effects = frame.Effects.Select(item => item with { MonotonicMs = stamp }).ToArray();
        var healing = frame.Healing?.Select(item => item with { MonotonicMs = stamp }).ToArray();
        var states = frame.CombatStates?.Select(item => item with { MonotonicMs = stamp }).ToArray();
        var reconciliations = frame.Reconciliations.Select(item => item with
            { Effects = item.Effects.Select(effect => effect with { MonotonicMs = stamp }).ToArray() }).ToArray();
        IReadOnlyList<CombatGapInput> gaps = addGap ? [.. frame.Gaps, new("combat.captureLag", frame.MonotonicMs, stamp, new(0))] : frame.Gaps;
        return value with { Frame = frame with { MonotonicMs = stamp, Damage = damage, Effects = effects,
            Reconciliations = reconciliations, Gaps = gaps, Healing = healing, CombatStates = states } };
    }

    private InventoryCaptureReport InventoryCore(InventoryCaptureReport value, double now)
    {
        var frame = value.Frame;
        var threshold = now - ObservationCaptureRun.MaximumQueueLagMs;
        if (frame.MonotonicMs >= threshold || !double.IsFinite(frame.MonotonicMs))
        { inventoryFloor = Math.Max(inventoryFloor, frame.MonotonicMs); return value; }
        CorrectedInventoryFrames++;
        var stamp = Math.Max(threshold, inventoryFloor); inventoryFloor = stamp;
        return value with { Frame = frame with { MonotonicMs = stamp } };
    }
}
