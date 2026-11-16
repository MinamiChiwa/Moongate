using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Observation.Worker;

/// <summary>按批次和字节限制增量；容量缺口在较早批次排空后发送。</summary>
public sealed class CombatCaptureQueue
{
    public const int MaximumBatchRecords = 256, MaximumBatchBytes = 256 * 1024;
    private const int MaximumLossBytes = 8 * 1024;
    private sealed class Loss(CombatCaptureReport report, int bytes, ulong count)
    {
        public CombatCaptureReport Last = report;
        public int Bytes = bytes;
        public ulong Count = count;
        public double From = report.Frame.MonotonicMs, To = report.Frame.MonotonicMs;
    }
    private readonly object gate = new();
    private readonly List<(CombatCaptureReport Value, int Bytes, double FirstMono)> queue = [];
    private readonly Queue<Loss> losses = [];
    private readonly Dictionary<(string Interest, ulong Generation), Loss> lost = [];
    private readonly int maximumBatches, dataBudget, maximumLosses;
    private int bytes, lossBytes;
    public CombatCaptureQueue(int maximumBatches = 64, int maximumBytes = 4 * 1024 * 1024)
    {
        if (maximumBatches is < 1 or > 64 || maximumBytes is < 16 * 1024 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBatches));
        this.maximumBatches = maximumBatches;
        maximumLosses = Math.Min(64, maximumBytes / (2 * MaximumLossBytes));
        dataBudget = maximumBytes - maximumLosses * MaximumLossBytes;
    }
    public int Count { get { lock (gate) return queue.Count; } }
    public int PendingCount { get { lock (gate) return queue.Count + losses.Count; } }
    public int Bytes { get { lock (gate) return bytes + lossBytes; } }
    private static int Records(CombatFrame value) => Math.Max(1, checked(value.Entities.Count + value.Damage.Count +
        value.Effects.Count + value.Gaps.Count + (value.Healing?.Count ?? 0) + (value.CombatStates?.Count ?? 0) + value.Reconciliations.Sum(x => Math.Max(1, x.Effects.Count))));
    private static bool TimesFit(CombatFrame frame)
    {
        bool Fits(double mono, DateTimeOffset at) => double.IsFinite(mono) && mono >= 0 &&
            mono >= frame.MonotonicMs - 250 && mono <= frame.MonotonicMs &&
            at != default && at.Offset == TimeSpan.Zero && at <= frame.ObservedAt;
        return frame.Damage.All(value => Fits(value.MonotonicMs, value.ObservedAt)) &&
            (frame.Healing ?? []).All(value => Fits(value.MonotonicMs, value.ObservedAt)) &&
            (frame.CombatStates ?? []).All(value => Fits(value.MonotonicMs, value.ObservedAt)) &&
            frame.Effects.All(value => Fits(value.MonotonicMs, value.ObservedAt)) &&
            frame.Reconciliations.All(value => value.Effects.All(effect => Fits(effect.MonotonicMs, effect.ObservedAt)));
    }
    public bool TryWrite(CombatCaptureReport value)
    {
        var records = Records(value.Frame);
        var content = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        lock (gate)
        {
            if (lost.Count == 0 && queue.Count > 0 && queue[^1] is var previousBatch &&
                previousBatch.Value.InterestId == value.InterestId && previousBatch.Value.Frame.Generation == value.Frame.Generation &&
                previousBatch.Value.Frame.Source == value.Frame.Source && previousBatch.Value.Frame.GameSessionId == value.Frame.GameSessionId &&
                value.Frame.Sequence.Value > previousBatch.Value.Frame.Sequence.Value && value.Frame.MonotonicMs >= previousBatch.Value.Frame.MonotonicMs &&
                value.Frame.MonotonicMs - previousBatch.FirstMono <= 100)
            {
                var previous = previousBatch.Value.Frame;
                var merged = value with { Frame = value.Frame with
                    { Damage = previous.Damage.Concat(value.Frame.Damage).ToArray(), Effects = previous.Effects.Concat(value.Frame.Effects).ToArray(),
                      Reconciliations = previous.Reconciliations.Concat(value.Frame.Reconciliations).ToArray(), Gaps = previous.Gaps.Concat(value.Frame.Gaps).ToArray(),
                      Healing = previous.Healing is null && value.Frame.Healing is null ? null : (previous.Healing ?? []).Concat(value.Frame.Healing ?? []).ToArray(),
                      CombatStates = previous.CombatStates is null && value.Frame.CombatStates is null ? null
                          : (previous.CombatStates ?? []).Concat(value.Frame.CombatStates ?? []).ToArray() } };
                if (Records(merged.Frame) <= MaximumBatchRecords && TimesFit(merged.Frame))
                {
                    var encoded = JsonSerializer.SerializeToUtf8Bytes(merged, ContractJson.Options);
                    if (encoded.Length <= MaximumBatchBytes && bytes - previousBatch.Bytes + encoded.Length <= dataBudget)
                    {
                        queue[^1] = (JsonSerializer.Deserialize<CombatCaptureReport>(encoded, ContractJson.Options)!, encoded.Length, previousBatch.FirstMono);
                        bytes += encoded.Length - previousBatch.Bytes; return true;
                    }
                }
            }
            if (lost.Count > 0 || records > MaximumBatchRecords || content.Length > MaximumBatchBytes ||
                queue.Count >= maximumBatches || bytes + content.Length > dataBudget)
            {
                var count = value.Frame.Gaps.Aggregate((ulong)records, (sum, gap) => checked(sum + gap.DroppedEvents.Value));
                var template = value with { Frame = value.Frame with { Entities = value.LocalEntity is { } local ? [local] : [],
                    Damage = [], Effects = [], Reconciliations = [], Gaps = [], Healing = null } };
                var templateBytes = JsonSerializer.SerializeToUtf8Bytes(template, ContractJson.Options).Length;
                if (templateBytes > MaximumLossBytes) throw new WorkerObservationException("combat.observationGap");
                var key = (value.InterestId, value.Frame.Generation.Value);
                if (!lost.TryGetValue(key, out var loss))
                {
                    if (lost.Count >= maximumLosses) throw new WorkerObservationException("combat.observationGap");
                    loss = new(template, templateBytes, 0); lost.Add(key, loss); losses.Enqueue(loss);
                    lossBytes += templateBytes;
                }
                else
                {
                    lossBytes += templateBytes - loss.Bytes; loss.Last = template; loss.Bytes = templateBytes;
                }
                loss.Count = checked(loss.Count + count); loss.From = Math.Min(loss.From, value.Frame.MonotonicMs);
                loss.To = Math.Max(loss.To, value.Frame.MonotonicMs); return false;
            }
            queue.Add((JsonSerializer.Deserialize<CombatCaptureReport>(content, ContractJson.Options)!, content.Length, value.Frame.MonotonicMs));
            bytes += content.Length; return true;
        }
    }
    public IReadOnlyList<CombatCaptureReport> Drain() { lock (gate) return TakeCore(128); }
    public IReadOnlyList<CombatCaptureReport> Take(int maximumBatches = 8)
    {
        if (maximumBatches is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumBatches));
        lock (gate) return TakeCore(maximumBatches);
    }
    private IReadOnlyList<CombatCaptureReport> TakeCore(int maximum)
    {
        var result = new List<CombatCaptureReport>(Math.Min(queue.Count + losses.Count, maximum));
        var take = Math.Min(queue.Count, maximum);
        for (var i = 0; i < take; i++) { result.Add(queue[i].Value); bytes -= queue[i].Bytes; }
        queue.RemoveRange(0, take);
        while (queue.Count == 0 && result.Count < maximum && losses.TryDequeue(out var loss))
        {
            lost.Remove((loss.Last.InterestId, loss.Last.Frame.Generation.Value)); lossBytes -= loss.Bytes;
            result.Add(loss.Last with { Frame = loss.Last.Frame with
                { Gaps = [new("combat.transportCapacity", loss.From, loss.To, new(loss.Count))] } });
        }
        return result;
    }
}
