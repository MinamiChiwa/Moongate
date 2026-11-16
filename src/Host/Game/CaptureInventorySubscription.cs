using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Game;

internal sealed class CaptureInventorySubscription(GameObservationService service, string principal, string id,
    string interestId, InventoryObservationRequest request, ObservationInventoryProfile profile, bool resident = false) : IAsyncDisposable
{
    /// <summary>常驻库存订阅：随常驻抓包自动保持，不占用更新活动屏障。</summary>
    internal bool Resident => resident;
    private readonly object gate = new();
    private readonly Queue<(InventoryFrame Frame, int Bytes)> queue = new();
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Dictionary<ulong, string> fingerprints = [];
    private readonly Queue<ulong> fingerprintOrder = [];
    private InventoryFrame? last;
    private ulong generation, sourceSequence, deliverySequence;
    private bool blocked, completed;
    private volatile bool closing;
    private int bytes, reader;
    private Exception? failure;
    private Task? close;
    internal string Id => id;
    internal string Principal => principal;
    internal string InterestId => interestId;
    internal InventoryObservationRequest Request => request;
    internal ObservationInventoryProfile Profile => profile;
    internal bool Closing { get => closing; set => closing = value; }
    internal void Publish(InventoryCaptureReport report)
    {
        var frame = report.Frame;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(frame, ContractJson.Options);
        lock (gate)
        {
            if (completed || closing || frame.Generation.Value < generation) return;
            if (frame.Generation.Value > generation)
            {
                generation = frame.Generation.Value; sourceSequence = deliverySequence = 0; blocked = false;
                fingerprints.Clear(); fingerprintOrder.Clear();
            }
            var hash = Convert.ToHexStringLower(SHA256.HashData(encoded));
            if (frame.Sequence.Value <= sourceSequence)
            {
                if (fingerprints.TryGetValue(frame.Sequence.Value, out var old) && old != hash) Gap(frame, "inventory.frameConflict");
                return;
            }
            if (blocked && frame.Kind.KnownValue != InventoryFrameKind.Full) return;
            if (frame.Kind.KnownValue != InventoryFrameKind.Full && (sourceSequence == 0 || frame.Sequence.Value != sourceSequence + 1) &&
                frame.Kind.KnownValue != InventoryFrameKind.Invalidated)
            { Gap(frame, "inventory.sequenceGap"); return; }
            if (encoded.Length > 16 * 1024 * 1024 || queue.Count >= 64 || bytes + encoded.Length > 64 * 1024 * 1024)
            { Gap(frame, "inventory.subscriptionCapacity"); return; }
            sourceSequence = frame.Sequence.Value; fingerprints[sourceSequence] = hash; fingerprintOrder.Enqueue(sourceSequence);
            if (fingerprintOrder.Count > 256) fingerprints.Remove(fingerprintOrder.Dequeue());
            if (frame.Kind.KnownValue == InventoryFrameKind.Full) blocked = false;
            if (frame.Kind.KnownValue == InventoryFrameKind.Invalidated) blocked = true;
            last = frame;
            queue.Enqueue((frame with { Sequence = new(++deliverySequence) }, encoded.Length)); bytes += encoded.Length;
            wake.Writer.TryWrite(true);
        }
    }
    private void Gap(InventoryFrame frame, string reason)
    {
        blocked = true; queue.Clear(); bytes = 0; last = frame;
        queue.Enqueue((frame with { Kind = InventoryFrameKind.Invalidated, Sequence = new(++deliverySequence), Items = [], Changes = [], Coverage = [], Reason = reason }, 0));
        wake.Writer.TryWrite(true);
    }
    internal void Complete(Exception? error)
    { lock (gate) { if (completed) return; completed = true; closing = true; queue.Clear(); bytes = 0; failure = error; wake.Writer.TryWrite(true); } }
    internal async IAsyncEnumerable<InventoryFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Exchange(ref reader, 1) != 0) throw new InvalidOperationException("库存订阅只允许一个读取者");
        while (true)
        {
            await wake.Reader.ReadAsync(ct);
            InventoryFrame[] batch; bool ended; Exception? error;
            lock (gate)
            {
                batch = closing ? [] : queue.Select(item => item.Frame).ToArray();
                queue.Clear(); bytes = 0; ended = completed; error = failure;
            }
            foreach (var frame in batch) { ct.ThrowIfCancellationRequested(); if (!closing) yield return frame; }
            if (ended) { if (error is not null) throw error; yield break; }
        }
    }
    public ValueTask DisposeAsync()
    { lock (gate) return new(close ??= Task.Run(() => service.ReleaseInventoryAsync(this))); }
}
