using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

public sealed record CombatBufferedBatch(IReadOnlyList<CombatFrame> Frames, UInt64String DroppedEvents, IReadOnlyList<long> ReceivedAt);

/// <summary>按记录数量和编码字节同时限制输入；生产者不等待统计或数据库。</summary>
public sealed class CombatFrameBuffer(CombatLimits? configured = null, TimeProvider? timeProvider = null)
{
    private readonly CombatLimits limits = Validate(configured ?? new());
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Queue<(CombatFrame Frame, int Events, int Bytes, long ReceivedAt)> frames = [];
    private int events, bytes;
    private ulong dropped;
    private bool completed;
    private static CombatLimits Validate(CombatLimits value) { value.Validate(); return value; }
    public int Count { get { lock (gate) return events; } }
    public int Bytes { get { lock (gate) return bytes; } }
    public bool Completed { get { lock (gate) return completed && frames.Count == 0; } }
    public bool TryWrite(CombatFrame frame)
    {
        if (!CombatFrameNormalization.TryNormalize(frame, out frame)) return Reject(1);
        var size = (long)frame.Entities.Count + frame.Damage.Count + frame.Effects.Count + frame.Gaps.Count +
            frame.Reconciliations.Sum(x => (long)Math.Max(1, x.Effects.Count));
        size = Math.Max(1, size);
        ulong Lost() => frame.Gaps.Aggregate((ulong)size, (sum, value) => checked(sum + value.DroppedEvents.Value));
        if (size > limits.MaximumQueueEvents) return Reject(Lost());
        var count = (int)size;
        byte[] content;
        try { content = JsonSerializer.SerializeToUtf8Bytes(frame, ContractJson.Options); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { return Reject(Lost()); }
        if (content.Length > limits.MaximumQueueBytes) return Reject(Lost());
        lock (gate)
        {
            if (completed) return false;
            if (events + count > limits.MaximumQueueEvents || bytes + content.Length > limits.MaximumQueueBytes)
            { dropped = checked(dropped + Lost()); return false; }
            frames.Enqueue((JsonSerializer.Deserialize<CombatFrame>(content, ContractJson.Options)!, count, content.Length, clock.GetTimestamp()));
            events += count; bytes += content.Length; return true;
        }
    }
    private bool Reject(ulong count)
    {
        lock (gate) { if (!completed) dropped = checked(dropped + count); return false; }
    }
    public CombatBufferedBatch Take(int maximumFrames = 256)
    {
        if (maximumFrames is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        lock (gate)
        {
            var result = new List<CombatFrame>(Math.Min(frames.Count, maximumFrames));
            var received = new List<long>(Math.Min(frames.Count, maximumFrames));
            while (result.Count < maximumFrames && frames.TryDequeue(out var frame))
            { result.Add(frame.Frame); received.Add(frame.ReceivedAt); events -= frame.Events; bytes -= frame.Bytes; }
            var lost = dropped; dropped = 0; return new(result, new(lost), received);
        }
    }
    public void Complete() { lock (gate) completed = true; }
    public IReadOnlyList<CombatGapInput> DrainGaps(string reason, double monotonicMs)
    {
        lock (gate)
        {
            completed = true;
            var result = new CombatGapAccumulator();
            foreach (var item in frames)
            {
                result.AddRange(item.Frame.Gaps);
                var unread = checked((ulong)item.Frame.Damage.Count + (ulong)item.Frame.Effects.Count +
                    (ulong)item.Frame.Reconciliations.Sum(value => Math.Max(1, value.Effects.Count)));
                if (unread > 0) result.Add(new(reason, item.Frame.MonotonicMs, item.Frame.MonotonicMs, new(unread)));
            }
            if (dropped > 0) result.Add(new("queue-capacity", monotonicMs, monotonicMs, new(dropped)));
            frames.Clear(); events = 0; bytes = 0; dropped = 0; return result.Snapshot();
        }
    }
    public void Discard() { lock (gate) { completed = true; frames.Clear(); events = 0; bytes = 0; dropped = 0; } }
}
