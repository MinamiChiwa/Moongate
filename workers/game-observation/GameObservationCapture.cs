// 来源：MinamiChiwa/MabiSync，固定提交 2cb69b5ddec8de4fe9d20ec98bbb410ac015e671；捕获迁至隔离进程。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.Observation.Worker;

internal sealed record CaptureObservation(GameObservation Game, long Packets, bool Running, string Status);

internal static partial class CaptureChannels
{
    private sealed class ReceiveChannel(nint handle, int link, CaptureConnection connection, double opened, SharedGameStream parser) : IDisposable
    {
        public nint Handle { get; } = handle;
        public int Link { get; } = link;
        public CaptureConnection Connection { get; } = connection;
        public ConnectionFeed Feed { get; } = new(parser);
        public SharedGameStream Parser => Feed.Parser;
        public NativeTcpIngress Ingress => Feed.Ingress;
        public double Opened { get; } = opened;
        /// <summary>最近一次出现在进程连接表中的时点（捕获计时毫秒）。</summary>
        public double LastListed { get; set; } = opened;
        /// <summary>最近一次收到该连接下行包的时点（捕获计时毫秒）。</summary>
        public double LastPacket { get; set; } = opened;
        public uint Drops { get; set; }
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { Parser.Dispose(); } finally { pcap_close(Handle); }
        }
    }
    /// <summary>
    /// 游戏服务端点：由绑定进程已确认的接收连接得出（远端地址与端口、本机地址）。端点过滤器只收该端点发往本机地址的下行包。
    /// </summary>
    private sealed record GameEndpoint(string RemoteAddress, ushort RemotePort, string LocalAddress)
    {
        public bool Matches(GameTcpPacket packet) => packet.SourceAddress == RemoteAddress && packet.SourcePort == RemotePort && packet.DestinationAddress == LocalAddress;
    }
    private sealed class EndpointHandle(nint handle, int link) : IDisposable
    {
        public nint Handle { get; } = handle;
        public int Link { get; } = link;
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) pcap_close(Handle); }
    }
    /// <summary>同时保持的端点过滤器上限；当前台服构建只有一个游戏服务端点（回环 1111）。</summary>
    internal const int MaximumEndpoints = 4;
    /// <summary>每轮从端点过滤器读取的包数上限，不拖慢已有连接的读取；新连接交付前的排空另有上限。</summary>
    internal const int EndpointReadLimit = 256, EndpointDrainLimit = 4096;
    private static double Mono() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
    /// <summary>Npcap 包头的抓包时刻换成单调毫秒与 UTC；映射无效时单调毫秒为空。</summary>
    private static (double? Mono, DateTimeOffset Utc) CapturedAt(nint header, double mono)
    {
        // Windows Npcap timeval uses two int32 fields; caplen follows at +8.
        var capturedUtc = Marshal.ReadInt32(header) * 1000d + Marshal.ReadInt32(header + 4) / 1000d;
        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - capturedUtc;
        return age is >= -100 and <= 10000
            ? (mono - age, DateTimeOffset.FromUnixTimeMilliseconds((long)capturedUtc))
            : (null, DateTimeOffset.UtcNow);
    }
    internal static async Task ObserveAsync(SelectedGameProcess game, CaptureDevice device, string gameSessionId,
        ObservationCaptureContext context, CancellationToken token, CombatProtocolLayout layout = CombatProtocolLayout.MusicOnly, bool anyAddress = false,
        GameIdentity? knownIdentity = null)
    {
        if (!SelectedGameProcess.IsRunning(game)) throw new IOException("所选游戏进程已变化，请重新选择。");
        // 当前布局：已登记构建直接启用；未登记构建在本进程内独立读取客户端事实核对，分帧改用读到的协议号，
        // 核对不过的身份、音乐或战斗解析单独停用。读不全客户端时不启动。
        TwBuildVerdict? build = null;
        if (layout == CombatProtocolLayout.Tw20261110)
        {
            build = await TwBuildVerification.EvaluateAsync(game.ImagePath, null, token).ConfigureAwait(false);
            if (build is { Registered: false }) NpcapObservationSource.ReleaseLayoutBuffers();
            if (build is not { CaptureUsable: true }) throw new WorkerObservationException("gameBuild.incompatible");
        }
        else await VerifyClientAsync(game.ImagePath, token).ConfigureAwait(false);
        var channels = new Dictionary<CaptureConnection, ReceiveChannel>();
        // 方案 A：端点过滤器与预缓冲只存在于本次捕获（绑定的游戏进程）内；解绑、换进程、关闭抓包结束捕获时一并关闭与清空。
        var endpoints = new Dictionary<GameEndpoint, EndpointHandle>();
        var prearm = new PrearmBuffer(context.Diagnostics);
        var timer = Stopwatch.StartNew(); double lastPoll = -1000, lastReport = -1000; long packets = 0;
        var observations = new CaptureObservationSelection(); string? lastError = null;
        long generation = 0; var lastCombatInitializer = double.NegativeInfinity;
        // 本机身份跨连接沿用（每次进图、换频道都换连接），范围限于本次捕获绑定的游戏进程；
        // 初值为宿主下发的同一游戏会话最近确认身份（Worker 重启后恢复）。
        var localIdentities = new LocalIdentityMemory(knownIdentity);
        var lastInventoryInitializer = double.NegativeInfinity;
        // 地图名称表属于本次绑定的客户端，首次进图时读取一次；换绑定即新的捕获与新的缓存。
        var imagePath = game.ImagePath;
        var maps = layout == CombatProtocolLayout.Tw20261110
            ? new MapLocationResolver(ct => ClientMapCatalog.Read(ClientItemCatalogLocation.Resolve(imagePath), ct), token) : null;
        bool InventoryPrimary(SharedGameStream stream)
        {
            var latest = channels.Values.Where(value => !double.IsNegativeInfinity(value.Parser.InventoryInitializerAt))
                .MaxBy(value => value.Parser.InventoryInitializerAt)?.Parser;
            if (latest is null || latest.InventoryInitializerAt < lastInventoryInitializer) return false;
            lastInventoryInitializer = latest.InventoryInitializerAt; return ReferenceEquals(latest, stream);
        }
        bool Primary(SharedGameStream stream)
        {
            var latest = channels.Values.Where(value => !double.IsNegativeInfinity(value.Parser.CombatInitializerAt))
                .MaxBy(value => value.Parser.CombatInitializerAt)?.Parser;
            if (latest is null || latest.CombatInitializerAt < lastCombatInitializer) return false;
            lastCombatInitializer = latest.CombatInitializerAt; return ReferenceEquals(latest, stream);
        }
        // 读端点过滤器：已有接收连接的包直接丢弃（逐连接句柄在收），其余按四元组放进预缓冲，保留抓包时刻。
        void ReadEndpoints(int limit)
        {
            foreach (var (endpoint, handle) in endpoints)
                for (var i = 0; i < limit; i++)
                {
                    var result = pcap_next_ex(handle.Handle, out var header, out var pointer);
                    if (result == 0) break;
                    if (result != 1) throw new IOException("Npcap 读取中断，请重新校对通道。");
                    var length = Marshal.ReadInt32(header + 8); var wireLength = Marshal.ReadInt32(header + 12);
                    context.Diagnostics.Packet(length);
                    if (!NativeCaptureBoundary.CompletePacket(length, wireLength)) { context.Diagnostics.Truncated(); continue; }
                    var raw = new byte[length]; Marshal.Copy(pointer, raw, 0, length); packets++;
                    var packet = NativeCaptureBoundary.Read(handle.Link, raw);
                    if (packet is null || !endpoint.Matches(packet)) { context.Diagnostics.Filtered(); continue; }
                    var tuple = new CaptureConnection(packet.DestinationAddress, packet.DestinationPort, packet.SourceAddress, packet.SourcePort);
                    if (channels.ContainsKey(tuple)) continue;
                    if (packet.Payload.Length == 0 && (packet.Flags & 7) == 0) continue;
                    var mono = Mono(); var (captured, utc) = CapturedAt(header, mono);
                    prearm.Add(tuple, new(packet.Sequence, packet.Flags, packet.Payload.ToArray(), captured ?? mono, utc), mono);
                }
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = timer.Elapsed.TotalMilliseconds;
                if (now - lastPoll >= 500)
                {
                    if (!SelectedGameProcess.IsRunning(game)) throw new IOException("所选游戏进程已退出或重新启动。");
                    var table = Table();
                    var owned = table.Where(row => row.ProcessId == game.ProcessId && row.State is >= 3 and <= 8).Select(row => row.Connection);
                    var current = NativeCaptureBoundary.SelectConnections(owned, device, layout == CombatProtocolLayout.Tw20261110, anyAddress);
                    var opened = new List<ReceiveChannel>();
                    context.Execute(() =>
                    {
                        void Remove(CaptureConnection old)
                        {
                            channels[old].Parser.Restart("游戏连接已移除。", Mono(), DateTimeOffset.UtcNow, GameStreamResetCause.ConnectionRemoved);
                            channels[old].Dispose(); channels.Remove(old);
                        }
                        foreach (var (connection, channel) in channels)
                            if (current.Contains(connection)) channel.LastListed = now;
                        // 连接表会短暂漏列仍在传输的游戏连接（实采：同一四元组的 TCP 序号连续，表中却反复消失或显示 FIN_WAIT1），
                        // 只按单次轮询移除会在流中途重建解析，丢掉只在连接开头发送的个人数据。不在表中且静默满宽限期才移除。
                        foreach (var old in channels.Where(pair => !current.Contains(pair.Key) &&
                            ConnectionRetention.Expired(now, pair.Value.LastListed, pair.Value.LastPacket)).Select(pair => pair.Key).ToArray())
                            Remove(old);
                        foreach (var connection in current.Where(c => !channels.ContainsKey(c)))
                        {
                            if (channels.Count >= ConnectionRetention.MaximumChannels)
                            {
                                // 容量满时先让出未列于表中、静默最久的保留连接；都在表中时本轮不再打开。
                                var idle = channels.Where(pair => !current.Contains(pair.Key))
                                    .MinBy(pair => Math.Max(pair.Value.LastListed, pair.Value.LastPacket)).Key;
                                if (idle is null) break;
                                Remove(idle);
                            }
                            try
                            {
                                channels[connection] = OpenReceive(device, connection, now, gameSessionId, context,
                                    () => checked((ulong)Interlocked.Increment(ref generation)), Primary, layout, InventoryPrimary, localIdentities, build);
                                opened.Add(channels[connection]); lastError = null;
                            }
                            catch (IOException error) { lastError = error.Message; }
                        }
                    });
                    // 新连接的逐连接句柄已打开：先排空端点过滤器（此前到达、仍在驱动缓冲里的包），再把该四元组的缓冲包从 SYN 起按序交付，
                    // 时点用抓包时刻；之后逐连接句柄收到的重复副本由 ConnectionFeed 按序号跳过。确认归属之前缓冲不交给任何解析器。
                    if (opened.Count > 0 && endpoints.Count > 0)
                    {
                        // 排空期间新连接暂不算“已有接收连接”，它在驱动缓冲里的早到包因此进入预缓冲而不是被丢弃。
                        context.Execute(() =>
                        {
                            foreach (var channel in opened) channels.Remove(channel.Connection);
                            try { ReadEndpoints(EndpointDrainLimit); }
                            finally { foreach (var channel in opened) channels[channel.Connection] = channel; }
                        });
                    }
                    foreach (var channel in opened)
                        foreach (var early in prearm.Take(channel.Connection, Mono()))
                        {
                            channel.Feed.Accept(early.Sequence, early.Flags, early.Payload, early.CapturedMono, early.CapturedMono, early.CapturedUtc, prearmed: true);
                            channel.LastPacket = now;
                        }
                    // 缓冲中的四元组若被连接表列为其他进程所有，立即丢弃；其余由保留时长兜底。
                    prearm.Expire(Mono());
                    foreach (var tuple in prearm.Connections.ToArray())
                        if (table.Any(row => row.Connection == tuple && row.ProcessId != game.ProcessId)) prearm.DiscardForeign(tuple);
                    // 端点只从绑定进程已确认的接收连接得出，至多 MaximumEndpoints 个，捕获结束前保留（旧连接关闭后新连接仍连这里）。
                    foreach (var endpoint in channels.Keys.Select(c => new GameEndpoint(c.RemoteAddress, c.RemotePort, c.LocalAddress)).Distinct().ToArray())
                    {
                        if (endpoints.ContainsKey(endpoint) || endpoints.Count >= MaximumEndpoints) continue;
                        try { endpoints[endpoint] = OpenEndpoint(device, endpoint); }
                        catch (IOException error) { lastError = error.Message; }
                    }
                    foreach (var channel in channels.Values)
                    {
                        if (pcap_stats(channel.Handle, out var stats) == 0)
                        {
                            var drops = unchecked(stats.Dropped + stats.InterfaceDropped);
                            if (drops != channel.Drops) { context.Diagnostics.DriverDrops(channel.Drops, drops); channel.Ingress.Reset(); channel.Parser.Restart("抓包数据不完整，需要重新识别本机角色。",
                                Mono(), DateTimeOffset.UtcNow, GameStreamResetCause.CaptureDrops); channel.Drops = drops; }
                        }
                    }
                    lastPoll = now;
                }
                ReadEndpoints(EndpointReadLimit);
                foreach (var channel in channels.Values)
                {
                    for (var i = 0; i < 200; i++)
                    {
                        var result = pcap_next_ex(channel.Handle, out var header, out var pointer);
                        if (result == 0) break;
                        if (result != 1) throw new IOException("Npcap 读取中断，请重新校对通道。");
                        if (i == 199) context.Diagnostics.ReadLimitHit();
                        var length = Marshal.ReadInt32(header + 8); var wireLength = Marshal.ReadInt32(header + 12);
                        context.Diagnostics.Packet(length);
                        if (!NativeCaptureBoundary.CompletePacket(length, wireLength)) { context.Diagnostics.Truncated(); channel.Ingress.Reset(); channel.Parser.Restart("抓到的数据长度异常，需要重新识别。",
                            Mono(), DateTimeOffset.UtcNow, GameStreamResetCause.TruncatedPacket); continue; }
                        var raw = new byte[length]; Marshal.Copy(pointer, raw, 0, length); packets++;
                        var packet = NativeCaptureBoundary.Read(channel.Link, raw);
                        if (packet is null || packet.SourceAddress != channel.Connection.RemoteAddress || packet.SourcePort != channel.Connection.RemotePort || packet.DestinationAddress != channel.Connection.LocalAddress || packet.DestinationPort != channel.Connection.LocalPort)
                        { context.Diagnostics.Filtered(); continue; }
                        channel.LastPacket = now;
                        var mono = Mono();
                        // Use Npcap's packet timestamp, not a later UI/parser wake.
                        // An invalid mapping loses only this frame's timing estimate.
                        var receipt = CapturedAt(header, mono).Mono ?? double.NaN;
                        channel.Feed.Accept(packet.Sequence, packet.Flags, packet.Payload, mono, receipt, DateTimeOffset.UtcNow);
                    }
                    channel.Parser.Tick(Mono(), DateTimeOffset.UtcNow);
                }
                if (now - lastReport >= 500)
                {
                    var observation = SharedGameStream.ReportIdentity(
                        observations.Select(channels.Values.Select(c => (c.Parser.Music, c.Parser.MusicInitializerAt))),
                        channels.Values.Select(c => c.Parser), localIdentities);
                    var status = channels.Count == 0 ? lastError ?? "等待所选网卡上的游戏连接。" : observation.Detail;
                    // 本机身份不依赖模块兴趣：状态报告（含身份）始终发送，音乐字段只在音乐兴趣开启时填写。
                    var report = NpcapObservationSource.Project(new(observation, packets, true, status), maps);
                    if (layout == CombatProtocolLayout.Tw20261110) report = report with { Source = Tw20261110CombatProtocol.CombatProfile.Source };
                    context.Publish(report);
                    lastReport = now;
                }
                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }
        finally
        {
            prearm.Clear();
            try { NativeCaptureBoundary.DisposeAll(endpoints.Values); }
            finally { NativeCaptureBoundary.DisposeAll(channels.Values); }
        }
    }
    private static ReceiveChannel OpenReceive(CaptureDevice device, CaptureConnection connection, double now,
        string gameSessionId, ObservationCaptureContext context, Func<ulong> generations, Func<SharedGameStream, bool> primary, CombatProtocolLayout layout,
        Func<SharedGameStream, bool> inventoryPrimary, LocalIdentityMemory localIdentities, TwBuildVerdict? build)
    {
        var error = new StringBuilder(256); var handle = OpenConfigured(device.Name, error);
        if (handle == 0) throw new IOException("无法打开所选网卡：" + error);
        try
        {
            // Immutable filter per connection, downlink only. Never open a broad machine filter.
            var expression = $"tcp and src host {connection.RemoteAddress} and src port {connection.RemotePort} and dst host {connection.LocalAddress} and dst port {connection.LocalPort}";
            if (pcap_compile(handle, out var filter, expression, 1, uint.MaxValue) != 0) throw new IOException("无法建立连接过滤器。");
            try { if (pcap_setfilter(handle, ref filter) != 0) throw new IOException("无法应用连接过滤器。"); }
            finally { pcap_freecode(ref filter); }
            if (pcap_setnonblock(handle, 1, error) != 0) throw new IOException("Npcap 无法启动抓包，请检查安装或尝试其他网卡。");
            var link = pcap_datalink(handle);
            if (link is not (0 or 1 or 12 or 101 or 228)) throw new IOException("暂不支持此网卡，请选择其他网卡重试。");
            return new ReceiveChannel(handle, link, connection, now, new(gameSessionId, context, generations, primary, layout, inventoryPrimary, localIdentities, build));
        }
        catch { pcap_close(handle); throw; }
    }
    /// <summary>
    /// 端点过滤器：只收 <paramref name="endpoint"/> 发往本机地址的 TCP 下行包，不限本机端口，使同一游戏进程换用的新连接自 SYN-ACK 起即在捕获范围内。
    /// 端点只取自绑定进程已确认的接收连接；不对任意端口或任意主机开过滤器。
    /// </summary>
    private static EndpointHandle OpenEndpoint(CaptureDevice device, GameEndpoint endpoint)
    {
        var error = new StringBuilder(256); var handle = OpenConfigured(device.Name, error);
        if (handle == 0) throw new IOException("无法打开所选网卡：" + error);
        try
        {
            var expression = $"tcp and src host {endpoint.RemoteAddress} and src port {endpoint.RemotePort} and dst host {endpoint.LocalAddress}";
            if (pcap_compile(handle, out var filter, expression, 1, uint.MaxValue) != 0) throw new IOException("无法建立端点过滤器。");
            try { if (pcap_setfilter(handle, ref filter) != 0) throw new IOException("无法应用端点过滤器。"); }
            finally { pcap_freecode(ref filter); }
            if (pcap_setnonblock(handle, 1, error) != 0) throw new IOException("Npcap 无法启动抓包，请检查安装或尝试其他网卡。");
            var link = pcap_datalink(handle);
            if (link is not (0 or 1 or 12 or 101 or 228)) throw new IOException("暂不支持此网卡，请选择其他网卡重试。");
            return new EndpointHandle(handle, link);
        }
        catch { pcap_close(handle); throw; }
    }
    internal static async Task VerifyClientAsync(string imagePath, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(imagePath)!;
        var files = new[] { (Path.Combine(directory, "GameAssembly.dll"), TwGameProtocol.AssemblySha256),
            (Path.Combine(directory, Path.GetFileNameWithoutExtension(imagePath) + "_Data", "il2cpp_data", "Metadata", "global-metadata.dat"), TwGameProtocol.MetadataSha256) };
        foreach (var (path, expected) in files)
        {
            MabiTool.ModuleSdk.LocalDiscoverySecurity.RejectLinks(path);
            var before = new FileInfo(path);
            if (!before.Exists) throw new WorkerObservationException("gameBuild.incompatible");
            var length = before.Length; var modified = before.LastWriteTimeUtc;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            var after = new FileInfo(path);
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase) || after.Length != length || after.LastWriteTimeUtc != modified)
                throw new WorkerObservationException("gameBuild.incompatible");
        }
    }
    // Reserve Windows WinPcap/Npcap extension fields too; never pass a smaller native buffer.
    [StructLayout(LayoutKind.Sequential)] private struct PcapStats { public uint Received, Dropped, InterfaceDropped, Captured, Sent, NetworkDropped; }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_datalink(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_stats(nint handle, out PcapStats stats);
}

