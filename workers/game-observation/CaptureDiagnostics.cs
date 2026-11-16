using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 一次捕获运行的累计诊断计数。捕获线程、库存解析通道与写出循环并发更新；只保存整数与固定码，
/// 不记录地址、端口、角色或包内容。连接数按共享流的创建与释放计算，原生抓包另行累加包计数。
/// </summary>
public sealed class CaptureDiagnostics
{
    private long opened, closed, packets, packetBytes, filtered, truncated, driverDropped, readLimitHits;
    private long combatPeak, inventoryPeak, laneRejected, rejectedCombat, rejectedMusic, rejectedInventory;
    private long prearmBuffered, prearmDelivered, prearmExpired, prearmForeign, prearmBudget;
    public GameStreamCounters Stream { get; } = new();

    internal void ConnectionOpened() => Interlocked.Increment(ref opened);
    internal void ConnectionClosed() => Interlocked.Increment(ref closed);
    internal void Packet(int capturedLength) { Interlocked.Increment(ref packets); Interlocked.Add(ref packetBytes, Math.Max(0, capturedLength)); }
    internal void Filtered() => Interlocked.Increment(ref filtered);
    internal void Truncated() => Interlocked.Increment(ref truncated);
    internal void ReadLimitHit() => Interlocked.Increment(ref readLimitHits);
    /// <summary>累加驱动丢包读数的增量；Npcap 计数为 32 位，回绕按无符号差计算。</summary>
    internal void DriverDrops(uint previous, uint current) => Interlocked.Add(ref driverDropped, unchecked(current - previous));
    internal void PrearmBuffered() => Interlocked.Increment(ref prearmBuffered);
    internal void PrearmDelivered(int count) => Interlocked.Add(ref prearmDelivered, count);
    internal void PrearmExpired(int count) => Interlocked.Add(ref prearmExpired, count);
    internal void PrearmForeign(int count) => Interlocked.Add(ref prearmForeign, count);
    internal void PrearmBudget(int count) => Interlocked.Add(ref prearmBudget, count);
    internal void LaneRejected() => Interlocked.Increment(ref laneRejected);
    internal void Rejected(string branch)
    {
        switch (branch)
        {
            case "combat": Interlocked.Increment(ref rejectedCombat); break;
            case "music": Interlocked.Increment(ref rejectedMusic); break;
            case "inventory": Interlocked.Increment(ref rejectedInventory); break;
            default: throw new ArgumentOutOfRangeException(nameof(branch));
        }
    }
    /// <summary>写出循环每次计时采样队列深度，维护本次运行的高水位。</summary>
    internal void ObserveQueues(int combatDepth, int inventoryDepth)
    {
        Max(ref combatPeak, combatDepth); Max(ref inventoryPeak, inventoryDepth);
        static void Max(ref long target, long value)
        {
            for (var current = Volatile.Read(ref target); value > current; current = Volatile.Read(ref target))
                if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    public ObservationCaptureCounters Snapshot(int combatDepth, int inventoryDepth)
    {
        ObserveQueues(combatDepth, inventoryDepth);
        var stream = Stream.Snapshot();
        static UInt64String U(long value) => new((ulong)Math.Max(0, value));
        var (open, close) = (Interlocked.Read(ref opened), Interlocked.Read(ref closed));
        var rejected = new SortedDictionary<string, UInt64String>(StringComparer.Ordinal);
        foreach (var (branch, count) in new[] { ("combat", Interlocked.Read(ref rejectedCombat)),
            ("inventory", Interlocked.Read(ref rejectedInventory)), ("music", Interlocked.Read(ref rejectedMusic)) })
            if (count > 0) rejected.Add(branch, U(count));
        return new(1, new(U(open - close), U(open), U(close)),
            new(U(Interlocked.Read(ref packets)), U(Interlocked.Read(ref packetBytes)), U(Interlocked.Read(ref filtered)),
                U(Interlocked.Read(ref truncated)), U(Interlocked.Read(ref driverDropped)), U(Interlocked.Read(ref readLimitHits))),
            new(U(combatDepth), U(Interlocked.Read(ref combatPeak)), U(inventoryDepth), U(Interlocked.Read(ref inventoryPeak)),
                U(Interlocked.Read(ref laneRejected))),
            new(new(stream.Segments), new(stream.Bytes), new(stream.OutOfOrder), new(stream.Retransmitted),
                stream.Resets.ToDictionary(pair => pair.Key, pair => new UInt64String(pair.Value), StringComparer.Ordinal)),
            new(new(stream.Frames), new(stream.Dispatched), new(stream.DecompressFailures), rejected,
                stream.Types.ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => new UInt64String(pair.Value), StringComparer.Ordinal),
                new(stream.OtherFrames)),
            new(U(Interlocked.Read(ref prearmBuffered)), U(Interlocked.Read(ref prearmDelivered)), U(Interlocked.Read(ref prearmExpired)),
                U(Interlocked.Read(ref prearmForeign)), U(Interlocked.Read(ref prearmBudget))));
    }
}
