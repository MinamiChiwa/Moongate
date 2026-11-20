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
        // 规范化已复制全部列表，入队的帧不与生产者共享可变集合；字节数按编码长度计，不保留编码结果。
        int length;
        try { length = EncodedLength(frame); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { return Reject(Lost()); }
        if (length > limits.MaximumQueueBytes) return Reject(Lost());
        lock (gate)
        {
            if (completed) return false;
            if (events + count > limits.MaximumQueueEvents || bytes + length > limits.MaximumQueueBytes)
            { dropped = checked(dropped + Lost()); return false; }
            frames.Enqueue((frame, count, length, clock.GetTimestamp()));
            events += count; bytes += length; return true;
        }
    }
    [ThreadStatic] private static CountingWriter? counter;
    [ThreadStatic] private static Utf8JsonWriter? writer;
    /// <summary>帧的 JSON 编码字节数；编码写入只计数的缓冲，不分配结果数组。</summary>
    private static int EncodedLength(CombatFrame frame)
    {
        var target = counter ??= new CountingWriter();
        var json = writer ??= new Utf8JsonWriter(target, new JsonWriterOptions { Encoder = ContractJson.Options.Encoder, SkipValidation = true });
        target.Reset(); json.Reset(target);
        JsonSerializer.Serialize(json, frame, ContractJson.Options); json.Flush();
        return checked((int)target.Written);
    }
    private sealed class CountingWriter : System.Buffers.IBufferWriter<byte>
    {
        private readonly byte[] scratch = new byte[16 * 1024];
        public long Written { get; private set; }
        public void Reset() => Written = 0;
        public void Advance(int count) => Written += count;
        public Memory<byte> GetMemory(int sizeHint = 0) => sizeHint <= scratch.Length ? scratch : new byte[sizeHint];
        public Span<byte> GetSpan(int sizeHint = 0) => sizeHint <= scratch.Length ? scratch : new byte[sizeHint];
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
