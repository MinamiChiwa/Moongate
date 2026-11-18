using System.Net;
using MabiTool.Providers.TwObservation;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 接收流的保留规则：本地端口不在进程连接表中、且宽限期内没有收到下行包时才移除。连接真正结束时下行 FIN/RST
/// 先使解析重置，随后静默满宽限期即释放；同时保留的接收流至多 <see cref="MaximumChannels"/> 条。
/// </summary>
internal static class ConnectionRetention
{
    internal const double GraceMs = 5000;
    internal const int MaximumChannels = 32;
    internal static bool Expired(double nowMs, double lastListedMs, double lastPacketMs) => nowMs - Math.Max(lastListedMs, lastPacketMs) >= GraceMs;
}

/// <summary>端点过滤器收到、尚未确认归属的一个下行包；时点为抓包时刻（单调毫秒与 UTC 毫秒），交付时原样使用。</summary>
internal sealed record PrearmedPacket(uint Sequence, byte Flags, byte[] Payload, double CapturedMono, DateTimeOffset CapturedUtc);

/// <summary>
/// 新连接预缓冲（方案 A，每张设备一份）。过滤器另放行已确认游戏流远端端口发来的包；本地端口尚未被连接表确认属于绑定进程时，
/// 包只放在这里：按四元组分队、保持到达顺序，至多 <see cref="WindowMs"/> 毫秒、合计 <see cref="MaximumBytes"/> 字节、
/// <see cref="MaximumPackets"/> 包、<see cref="MaximumConnections"/> 个四元组；超出时丢最旧的包，不阻塞读取。
/// 不落盘、不交给任何解析器；连接表确认属于绑定进程时 <see cref="Take"/> 按原顺序取出，确认属于其他进程或超时即丢弃。
/// 依据：20590 约 345–361 KB，在 SYN-ACK 后约 0.5–0.9 s 发出；连接表首次列出新连接实测晚 0.03–2.5 s（ESTABLISHED 最晚 3.7 s），
/// 再加 500 ms 读表周期，5 秒覆盖确认前的全部数据，4 MiB 容纳 20590 与其前后的其他帧。
/// </summary>
internal sealed class PrearmBuffer(CaptureDiagnostics? diagnostics = null)
{
    internal const double WindowMs = 5000;
    internal const long MaximumBytes = 4L * 1024 * 1024;
    internal const int MaximumPackets = 8192;
    internal const int MaximumConnections = 16;
    private readonly Dictionary<CaptureConnection, Queue<PrearmedPacket>> pending = [];
    // 跨四元组的到达顺序，用于预算超出时丢最旧的包；已被取出或丢弃的项在出队时跳过，超过两倍包数上限时重建。
    private readonly Queue<(CaptureConnection Connection, PrearmedPacket Packet)> order = new();
    private long bytes;
    private int packets;
    internal long Bytes => bytes;
    internal int Packets => packets;
    internal IReadOnlyCollection<CaptureConnection> Connections => pending.Keys;

    internal void Add(CaptureConnection connection, PrearmedPacket packet, double nowMono)
    {
        Expire(nowMono);
        if (packet.Payload.Length > MaximumBytes) { diagnostics?.PrearmBudget(1); return; }
        if (!pending.TryGetValue(connection, out var queue))
        {
            while (pending.Count >= MaximumConnections) DropConnection(OldestConnection(), budget: true);
            pending[connection] = queue = new();
        }
        while (bytes + packet.Payload.Length > MaximumBytes || packets + 1 > MaximumPackets) DropOldest();
        queue.Enqueue(packet); order.Enqueue((connection, packet));
        if (order.Count > 2 * MaximumPackets) Compact();
        bytes += packet.Payload.Length; packets++;
        diagnostics?.PrearmBuffered();
    }

    /// <summary>连接表确认 <paramref name="connection"/> 属于绑定进程：按到达顺序取出它的缓冲包（已过期的先丢弃）。</summary>
    internal IReadOnlyList<PrearmedPacket> Take(CaptureConnection connection, double nowMono)
    {
        Expire(nowMono);
        if (!pending.Remove(connection, out var queue)) return [];
        foreach (var packet in queue) { bytes -= packet.Payload.Length; packets--; }
        diagnostics?.PrearmDelivered(queue.Count);
        return queue.ToArray();
    }

    /// <summary>连接表确认该四元组属于其他进程：丢弃。</summary>
    internal void DiscardForeign(CaptureConnection connection)
    {
        if (!pending.Remove(connection, out var queue)) return;
        foreach (var packet in queue) { bytes -= packet.Payload.Length; packets--; }
        diagnostics?.PrearmForeign(queue.Count);
    }

    internal void Expire(double nowMono)
    {
        foreach (var (connection, queue) in pending.ToArray())
        {
            var dropped = 0;
            while (queue.TryPeek(out var head) && nowMono - head.CapturedMono > WindowMs)
            { queue.Dequeue(); bytes -= head.Payload.Length; packets--; dropped++; }
            if (dropped > 0) diagnostics?.PrearmExpired(dropped);
            if (queue.Count == 0) pending.Remove(connection);
        }
    }

    /// <summary>捕获结束（解绑、换进程、抓包关闭）：全部丢弃。</summary>
    internal void Clear()
    {
        pending.Clear(); order.Clear(); bytes = 0; packets = 0;
    }

    private bool Live((CaptureConnection Connection, PrearmedPacket Packet) entry) =>
        pending.TryGetValue(entry.Connection, out var queue) && queue.TryPeek(out var head) && ReferenceEquals(head, entry.Packet);

    private CaptureConnection OldestConnection()
    {
        while (order.TryPeek(out var head) && !Live(head)) order.Dequeue();
        return order.TryPeek(out var oldest) ? oldest.Connection : pending.Keys.First();
    }

    private void DropOldest()
    {
        var connection = OldestConnection();
        var queue = pending[connection];
        var head = queue.Dequeue(); bytes -= head.Payload.Length; packets--;
        if (order.TryPeek(out var entry) && ReferenceEquals(entry.Packet, head)) order.Dequeue();
        diagnostics?.PrearmBudget(1);
        if (queue.Count == 0) pending.Remove(connection);
    }

    private void Compact()
    {
        var live = order.Where(entry => pending.TryGetValue(entry.Connection, out var queue) && queue.Contains(entry.Packet)).ToArray();
        order.Clear(); foreach (var entry in live) order.Enqueue(entry);
    }

    private void DropConnection(CaptureConnection connection, bool budget)
    {
        if (!pending.Remove(connection, out var queue)) return;
        foreach (var packet in queue) { bytes -= packet.Payload.Length; packets--; }
        if (budget) diagnostics?.PrearmBudget(queue.Count);
    }
}

/// <summary>
/// 一条接收流的包处理：SYN 重置、TCP 入口、结束复位，与解析器之间的唯一入口。缓冲交付与实时接收都经这里，
/// 时点按调用方给出的抓包时刻传入解析器。缓冲交付后记下已交付的序号终点，逐连接句柄随后收到的、完全落在其之前的
/// 无标志数据段视为同一批数据的重复副本而跳过；部分重叠的段照常交给重组器按序号裁剪。
/// </summary>
internal sealed class ConnectionFeed(SharedGameStream parser)
{
    public SharedGameStream Parser { get; } = parser;
    public NativeTcpIngress Ingress { get; } = new();
    public uint? LastSyn { get; set; }
    /// <summary>缓冲交付的序号终点（下一个期望序号）；没有缓冲交付时为空。</summary>
    public uint? DeliveredUntil { get; private set; }
    public long Duplicates { get; private set; }

    /// <returns>该包是否交给了解析器（重复副本返回假）。</returns>
    public bool Accept(uint sequence, byte flags, ReadOnlyMemory<byte> payload, double mono, double receipt, DateTimeOffset utc, bool prearmed = false)
    {
        var syn = (flags & 2) != 0;
        var end = unchecked(sequence + (uint)payload.Length + (syn ? 1u : 0u));
        if (!prearmed && DeliveredUntil is { } until && (flags & 7) == 0 && payload.Length > 0 && unchecked((int)(end - until)) <= 0)
        { Duplicates++; return false; }
        if (syn && LastSyn != sequence)
        { LastSyn = sequence; Ingress.Reset(); Parser.Restart("游戏连接已更新，正在重新识别角色昵称。", mono, utc, GameStreamResetCause.ConnectionSyn); }
        else if (syn && !prearmed && DeliveredUntil is not null) return false;
        Ingress.Feed(unchecked(sequence + (syn ? 1u : 0u)), flags, payload, (at, bytes) => Parser.Feed(at, bytes, mono, receipt, utc));
        if ((flags & 5) != 0)
        {
            Ingress.Reset();
            Parser.Restart("游戏连接已结束，等待新连接。", mono, utc, (flags & 4) != 0 ? GameStreamResetCause.ConnectionReset : GameStreamResetCause.ConnectionClosed);
        }
        if (prearmed && (DeliveredUntil is not { } previous || unchecked((int)(end - previous)) > 0)) DeliveredUntil = end;
        return true;
    }
}

internal static class NativeCaptureBoundary
{
    internal const int SnapshotBytes = 262144;
    internal const int BufferBytes = 16 * 1024 * 1024;
    /// <summary>常驻捕获每张设备一个句柄的内核缓冲；只放行游戏端口，游戏下行峰值（单帧约 360 KB）在 10 ms 读取周期内远小于此值。</summary>
    internal const int DeviceBufferBytes = 4 * 1024 * 1024;
    internal static bool CompletePacket(int captured, int wire) => captured is >= 0 and <= SnapshotBytes && captured == wire;
    /// <summary>
    /// 远端是本机地址（回环地址，或与本机地址相同、或是本机任一网卡地址）的连接，即本机代理型加速器的形态
    /// （实采 192.168.0.105:55331 → 192.168.0.105:50128），其包只出现在 Npcap 回环适配器上。只用于列出网卡时的诊断统计。
    /// </summary>
    internal static bool LocalProxy(CaptureConnection connection, IReadOnlySet<string>? localAddresses = null) =>
        IPAddress.TryParse(connection.RemoteAddress, out var remote) &&
        (IPAddress.IsLoopback(remote) || connection.RemoteAddress == connection.LocalAddress || localAddresses?.Contains(connection.RemoteAddress) == true);
    /// <summary>诊断：连接的本机地址在哪张网卡上（远端是本机地址时记在回环适配器上）。</summary>
    internal static bool MatchesDevice(CaptureDevice device, CaptureConnection connection, IReadOnlySet<string>? localAddresses = null) =>
        LocalProxy(connection, localAddresses) ? device.IsLoopback : device.Addresses.Contains(connection.LocalAddress);
    /// <summary>进程连向自己另一个套接字的自连（实采 127.0.0.1:7740 ↔ 127.0.0.1:7739）；只用于诊断统计。</summary>
    internal static bool SelfConnection(CaptureConnection connection, IReadOnlyCollection<CaptureConnection> owned) =>
        owned.Any(other => other.LocalPort == connection.RemotePort && other.LocalAddress == connection.RemoteAddress);
    /// <summary>
    /// 每张设备的 BPF：目的端口属于游戏进程本地端口集合，或源端口属于已确认游戏流的远端端口（接住连接表列出之前的新连接开头）。
    /// 端口原语同时匹配 IPv4 与 IPv6；集合为空时不放行任何包。
    /// </summary>
    internal static string Filter(IEnumerable<ushort> localPorts, IEnumerable<ushort> remotePorts)
    {
        var terms = localPorts.Order().Select(port => "dst port " + port).Concat(remotePorts.Order().Select(port => "src port " + port)).ToArray();
        return terms.Length == 0 ? "tcp and dst port 0 and src port 0" : "tcp and (" + string.Join(" or ", terms) + ")";
    }
    internal static GameTcpPacket? Read(int link, ReadOnlySpan<byte> bytes) => GamePacket.Read(link, bytes);
    internal static void DisposeAll(IEnumerable<IDisposable> resources)
    {
        Exception? failure = null;
        foreach (var resource in resources)
        {
            try { resource.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static nint OpenConfigured(Func<nint> create, Func<nint, int, int> snap,
        Func<nint, int, int> buffer, Func<nint, int, int> promiscuous, Func<nint, int, int> timeout,
        Func<nint, int> activate, Action<nint> close, int snapshotBytes = SnapshotBytes, int bufferBytes = BufferBytes, bool required = true)
    {
        var handle = create();
        if (handle == 0) return required ? throw new IOException("无法创建 Npcap 捕获句柄。") : 0;
        try
        {
            if (snap(handle, snapshotBytes) != 0 || buffer(handle, bufferBytes) != 0 ||
                promiscuous(handle, 0) != 0 || timeout(handle, 100) != 0 || activate(handle) < 0)
                throw new IOException("Npcap 捕获参数设置失败。");
            return handle;
        }
        catch (IOException) when (!required) { close(handle); return 0; }
        catch { close(handle); throw; }
    }
}

internal sealed class NativeTcpIngress
{
    private uint? endSequence;
    internal void Reset() => endSequence = null;
    internal void Feed(uint sequence, byte flags, ReadOnlyMemory<byte> payload, Action<uint, ReadOnlyMemory<byte>> feed)
    {
        // RFC 9293 的 ACK 保活允许窗口前一字节任意值；普通重传仍交给重组器逐字节比较。
        if ((flags & 16) != 0 && payload.Length == 1 && endSequence is { } end && sequence == unchecked(end - 1)) return;
        var next = unchecked(sequence + (uint)payload.Length);
        if (endSequence is null || unchecked((int)(next - endSequence.Value)) > 0) endSequence = next;
        for (var offset = 0; offset < payload.Length; offset += 65536)
            feed(unchecked(sequence + (uint)offset), payload.Slice(offset, Math.Min(65536, payload.Length - offset)));
    }
}
