using MabiTool.Providers.TwObservation;

namespace MabiTool.Observation.Worker;

/// <summary>一张抓包设备的累计计数（只含整数），供诊断分项与主来源判断。</summary>
internal sealed class DeviceCounters
{
    public long Packets, PortPackets, GameStreams, Frames;
    /// <summary>确认为游戏流之前超出候选缓冲而丢弃的负载字节（只计最终确认的流）。</summary>
    public long CandidateDropped;
    /// <summary>最近一次把游戏流数据交给解析器的时点（单调毫秒）；从未交付为负无穷。</summary>
    public double LastDelivered = double.NegativeInfinity;
}

/// <summary>
/// 多设备抓包的流路由：按包的四元组（本机 = 目的、远端 = 源）归流。未确认的流在每张设备上各自有界缓冲，
/// 某张设备上的负载按游戏帧头特征（<see cref="GameFrameSignature"/>）匹配时，该设备成为这条流的主来源，缓冲按到达顺序交给新建的解析器；
/// 匹配不上的流（TLS、进程自连等）不建解析器：一张设备上累计 <see cref="CandidateBytes"/> 字节或 <see cref="CandidatePackets"/> 个段仍不能确认时
/// 判为非游戏流，释放其候选缓冲，此后该四元组的包不再匹配、返回假（计为过滤），直到 SYN（端口复用）、FIN/RST 或流被移除。
/// 全部未确认流的候选缓冲合计至多 <see cref="CandidateBudgetBytes"/>，超出时从占用最多的流丢最早的段（确认后计入 candidateDropped）。
/// 同一四元组在其他设备上的副本丢弃；主来源 <see cref="FailoverMs"/> 内没有该流的包
/// 而另一张设备仍有时改由那张接续，重叠部分由重组按序号去重。调用方在捕获上下文的锁内调用。
/// </summary>
internal sealed class CaptureStreamRouter(int devices, IReadOnlySet<int> signature, GameStreamCounters counters, Func<SharedGameStream> create) : IDisposable
{
    internal const double FailoverMs = 1000;
    internal const int MaximumStreams = ConnectionRetention.MaximumChannels;
    /// <summary>
    /// 未确认流在一张设备上的候选上限，同时是判为非游戏流的门槛。依据：12 份实采的游戏连接从任一段起接入，到第一个能按帧头确认的段之前
    /// 至多累计 228,160 字节、109 个段；已知最大的单帧（登录个人数据 20590）约 356 KB，在它正文中途接入也在 512 KiB 内确认。
    /// </summary>
    internal const int CandidateBytes = 512 * 1024, CandidatePackets = 2048;
    /// <summary>全部未确认流、全部设备的候选缓冲合计上限。</summary>
    internal const long CandidateBudgetBytes = 4L * 1024 * 1024;
    /// <summary>为接住新连接开头而放进过滤器的已确认游戏流远端端口个数上限。</summary>
    internal const int MaximumRemotePorts = 4;

    private sealed record Pending(uint Sequence, byte Flags, byte[] Payload, double Mono, double Receipt, DateTimeOffset Utc);
    private sealed class Stream(int devices)
    {
        public ConnectionFeed? Feed;
        /// <summary>已判为非游戏流：不再缓冲与匹配。</summary>
        public bool Rejected;
        public int Primary = -1;
        public double LastPrimary, LastPacket, LastListed;
        public bool Listed = true;
        public readonly Queue<Pending>?[] Candidates = new Queue<Pending>?[devices];
        public readonly long[] CandidateBytes = new long[devices];
        public readonly long[] CandidateDropped = new long[devices];
    }

    private readonly Dictionary<CaptureConnection, Stream> streams = [];
    private readonly HashSet<ushort> remotePorts = [];
    public DeviceCounters[] Devices { get; } = Enumerable.Range(0, devices).Select(_ => new DeviceCounters()).ToArray();
    public long Duplicates { get; private set; }
    public long Failovers { get; private set; }
    /// <summary>单条流解析抛出非预期异常、被单独重置的次数（重组计数里记为原因不明的重置）。</summary>
    public long StreamFaults { get; private set; }
    /// <summary>判为非游戏流的次数。</summary>
    public long RejectedStreams { get; private set; }
    private long candidateTotal;

    public IEnumerable<SharedGameStream> Parsers => streams.Values.Where(value => value.Feed is not null).Select(value => value.Feed!.Parser);
    public int Count => streams.Count;
    public int ConfirmedCount => streams.Values.Count(value => value.Feed is not null);
    public IReadOnlySet<ushort> RemotePorts => remotePorts;
    /// <summary>仍保留的流的本地端口；连接表短暂漏列时过滤器继续放行这些端口。</summary>
    public IEnumerable<ushort> LocalPorts => streams.Keys.Select(value => value.LocalPort);
    public bool Contains(CaptureConnection key) => streams.ContainsKey(key);
    /// <summary>未确认流在各设备上的候选缓冲负载字节合计。</summary>
    public long CandidateBufferedBytes => candidateTotal;
    /// <summary>当前主来源：最近交付过游戏流数据的设备；还没有时为空。</summary>
    public int? PrimaryDevice
    {
        get
        {
            var best = -1;
            for (var i = 0; i < Devices.Length; i++)
                if (!double.IsNegativeInfinity(Devices[i].LastDelivered) && (best < 0 || Devices[i].LastDelivered > Devices[best].LastDelivered)) best = i;
            return best < 0 ? null : best;
        }
    }

    /// <summary>
    /// 一张设备读到的包：目的端口属于游戏进程（或已是保留中的流）时交给流路由；源端口是已确认游戏流的远端端口、本地端口尚未列入连接表时
    /// 放进该设备的预缓冲（时点为抓包时刻）；其余返回假，由调用方计为过滤。
    /// </summary>
    public bool Dispatch(int device, GameTcpPacket packet, IReadOnlySet<ushort> gamePorts, PrearmBuffer prearm,
        double mono, double? captured, DateTimeOffset capturedUtc, DateTimeOffset now)
    {
        var key = new CaptureConnection(packet.DestinationAddress, packet.DestinationPort, packet.SourceAddress, packet.SourcePort);
        if (gamePorts.Contains(packet.DestinationPort) || streams.ContainsKey(key))
            return Route(device, packet, mono, captured ?? double.NaN, now);
        if (!remotePorts.Contains(packet.SourcePort) || packet.Payload.Length == 0 && (packet.Flags & 7) == 0) return false;
        prearm.Add(key, new(packet.Sequence, packet.Flags, packet.Payload, captured ?? mono, capturedUtc), mono);
        return true;
    }

    /// <summary>
    /// 读连接表后：预缓冲中本地端口已属于游戏进程的四元组按到达顺序交给流路由（时点用抓包时刻），
    /// <paramref name="foreign"/> 判定属于其他进程的丢弃，其余由保留时长兜底。
    /// </summary>
    public void ReleasePrearmed(int device, PrearmBuffer prearm, IReadOnlySet<ushort> gamePorts, Func<CaptureConnection, bool> foreign, double mono)
    {
        prearm.Expire(mono);
        foreach (var tuple in prearm.Connections.ToArray())
        {
            if (gamePorts.Contains(tuple.LocalPort))
                foreach (var early in prearm.Take(tuple, mono))
                    Route(device, new(tuple.RemoteAddress, tuple.RemotePort, tuple.LocalAddress, tuple.LocalPort, early.Sequence, early.Flags, early.Payload),
                        early.CapturedMono, early.CapturedMono, early.CapturedUtc);
            else if (foreign(tuple)) prearm.DiscardForeign(tuple);
        }
    }

    /// <returns>该包是否交给了流路由（已判为非游戏流的四元组返回假）。</returns>
    public bool Route(int device, GameTcpPacket packet, double mono, double receipt, DateTimeOffset utc)
    {
        var key = new CaptureConnection(packet.DestinationAddress, packet.DestinationPort, packet.SourceAddress, packet.SourcePort);
        Devices[device].PortPackets++;
        if (streams.TryGetValue(key, out var stream) && stream.Rejected)
        {
            // 非游戏流：SYN 说明端口被新连接复用，重新开始匹配；FIN/RST 结束后释放；其余直接丢弃。
            if ((packet.Flags & 2) == 0) { if ((packet.Flags & 5) != 0) streams.Remove(key); return false; }
            streams.Remove(key); stream = null;
        }
        if (stream is null)
        {
            if (packet.Payload.Length == 0 && (packet.Flags & 7) == 0) return true;
            if (streams.Count >= MaximumStreams && !Evict(mono, utc)) return true;
            streams[key] = stream = new(Devices.Length) { LastListed = mono };
        }
        stream.LastPacket = mono;
        if (stream.Feed is { } feed)
        {
            if (device != stream.Primary)
            {
                if (mono - stream.LastPrimary < FailoverMs) { Duplicates++; return true; }
                stream.Primary = device; Failovers++;
            }
            stream.LastPrimary = mono;
            Deliver(device, feed, packet.Sequence, packet.Flags, packet.Payload, mono, receipt, utc);
            return true;
        }
        var queue = stream.Candidates[device] ??= new();
        queue.Enqueue(new(packet.Sequence, packet.Flags, packet.Payload, mono, receipt, utc));
        stream.CandidateBytes[device] += packet.Payload.Length; candidateTotal += packet.Payload.Length;
        if (packet.Payload.Length > 0 && GameFrameSignature.Matches(packet.Payload, signature)) { Confirm(key, stream, device, mono); return true; }
        if (stream.CandidateBytes[device] >= CandidateBytes || queue.Count >= CandidatePackets) { Reject(stream); return true; }
        while (candidateTotal > CandidateBudgetBytes && Trim()) { }
        return true;
    }

    /// <summary>判为非游戏流：释放各设备的候选缓冲，此后不再匹配。</summary>
    private void Reject(Stream stream)
    {
        Release(stream); stream.Rejected = true; RejectedStreams++;
    }
    private void Release(Stream stream)
    {
        candidateTotal -= stream.CandidateBytes.Sum();
        Array.Clear(stream.Candidates); Array.Clear(stream.CandidateBytes); Array.Clear(stream.CandidateDropped);
    }
    /// <summary>候选缓冲合计超出预算：从占用最多的未确认流、其占用最多的设备上丢最早的段。</summary>
    private bool Trim()
    {
        Stream? victim = null; var device = -1;
        foreach (var stream in streams.Values)
            for (var index = 0; index < stream.CandidateBytes.Length; index++)
                if (stream.Candidates[index] is { Count: > 0 } && (victim is null || stream.CandidateBytes[index] > victim.CandidateBytes[device]))
                    (victim, device) = (stream, index);
        if (victim is null) return false;
        var dropped = victim.Candidates[device]!.Dequeue().Payload.Length;
        victim.CandidateBytes[device] -= dropped; victim.CandidateDropped[device] += dropped; candidateTotal -= dropped;
        return true;
    }

    private void Confirm(CaptureConnection key, Stream stream, int device, double mono)
    {
        var parser = create(); parser.Connection = key;
        var feed = stream.Feed = new ConnectionFeed(parser);
        stream.Primary = device; stream.LastPrimary = mono;
        Devices[device].GameStreams++;
        // 开头被裁掉时解析从最早保留的段开始：重组以它为起点，分帧对齐跳过半截帧。
        Devices[device].CandidateDropped += stream.CandidateDropped[device];
        if (remotePorts.Count < MaximumRemotePorts) remotePorts.Add(key.RemotePort);
        var pending = stream.Candidates[device]!;
        Release(stream);
        foreach (var item in pending) Deliver(device, feed, item.Sequence, item.Flags, item.Payload, item.Mono, item.Receipt, item.Utc);
    }

    private void Deliver(int device, ConnectionFeed feed, uint sequence, byte flags, byte[] payload, double mono, double receipt, DateTimeOffset utc)
    {
        var before = counters.Frames;
        // 单条流的解析出现非预期异常只重置这一条（按原因不明整体重置），不让整个捕获故障。
        try { feed.Accept(sequence, flags, payload, mono, receipt, utc); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            StreamFaults++; feed.Ingress.Reset();
            try { feed.Parser.Restart("游戏数据解析出错，正在重新识别。", mono, utc, GameStreamResetCause.Unknown); }
            catch (Exception nested) when (nested is not OutOfMemoryException) { }
        }
        Devices[device].Frames += (long)(counters.Frames - before);
        Devices[device].LastDelivered = mono;
    }

    /// <summary>容量满：先让出未确认或未列于连接表的流中静默最久的一条；都已确认且在表中时不接收新流。</summary>
    private bool Evict(double mono, DateTimeOffset utc)
    {
        var idle = streams.Where(pair => pair.Value.Feed is null || !pair.Value.Listed).OrderBy(pair => pair.Value.LastPacket).Take(1).ToArray();
        if (idle.Length == 0) return false;
        Remove(idle[0].Key, idle[0].Value, mono, utc);
        return true;
    }

    /// <summary>
    /// 每次读连接表后调用：本地端口仍属于游戏进程的流记为在表中；不在表中且 <see cref="ConnectionRetention.GraceMs"/> 内没有包的流释放
    /// （连接表会短暂漏列仍在传输的游戏连接）。<paramref name="now"/> 与包的到达时点同一单调时钟（<see cref="Route"/> 的 mono）。
    /// </summary>
    public void Retain(IReadOnlySet<ushort> gamePorts, double now, DateTimeOffset utc)
    {
        foreach (var (key, stream) in streams)
        {
            stream.Listed = gamePorts.Contains(key.LocalPort);
            if (stream.Listed) stream.LastListed = now;
        }
        foreach (var (key, stream) in streams.Where(pair => !pair.Value.Listed &&
            ConnectionRetention.Expired(now, pair.Value.LastListed, pair.Value.LastPacket)).ToArray())
            Remove(key, stream, now, utc);
    }

    /// <summary>设备报告了驱动丢包：以它为主来源的流重置重组与解析。</summary>
    public void DeviceDropped(int device, double mono, DateTimeOffset utc)
    {
        foreach (var stream in streams.Values.Where(value => value.Feed is not null && value.Primary == device))
        {
            stream.Feed!.Ingress.Reset();
            stream.Feed.Parser.Restart("抓包数据不完整，需要重新识别本机角色。", mono, utc, GameStreamResetCause.CaptureDrops);
        }
    }

    public void Tick(double mono, DateTimeOffset utc)
    {
        foreach (var parser in Parsers.ToArray()) parser.Tick(mono, utc);
    }

    private void Remove(CaptureConnection key, Stream stream, double mono, DateTimeOffset utc)
    {
        streams.Remove(key);
        if (stream.Feed is null) Release(stream);
        if (stream.Feed is not { } feed) return;
        feed.Parser.Restart("游戏连接已移除。", mono, utc, GameStreamResetCause.ConnectionRemoved);
        feed.Parser.Dispose();
    }

    public void Dispose()
    {
        candidateTotal = 0;
        Exception? failure = null;
        foreach (var stream in streams.Values)
        {
            try { stream.Feed?.Parser.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        streams.Clear();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

}
