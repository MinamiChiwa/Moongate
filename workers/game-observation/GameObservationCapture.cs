// 来源：MinamiChiwa/MabiSync，固定提交 2cb69b5ddec8de4fe9d20ec98bbb410ac015e671；捕获迁至隔离进程。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.Observation.Worker;

internal sealed record CaptureObservation(GameObservation Game, long Packets, bool Running, string Status);

internal static partial class CaptureChannels
{
    /// <summary>
    /// 一张抓包设备：一个非阻塞句柄、当前过滤器与本设备的新连接预缓冲。打开失败、链路类型不受支持或读取中断时句柄关闭并记下原因码，
    /// 其他设备照常。
    /// </summary>
    private sealed class DeviceTap(CaptureDevice device, ObservationCaptureContext context) : IDisposable
    {
        public CaptureDevice Device { get; } = device;
        public nint Handle { get; set; }
        public int Link { get; set; } = -1;
        public string? Issue { get; set; }
        public uint Drops { get; set; }
        public PrearmBuffer Prearm { get; } = new(context.Diagnostics);
        public bool Open => Handle != 0;
        public void Fail(string issue) { Issue = issue; Close(); }
        public void Close() { if (Handle != 0) { pcap_close(Handle); Handle = 0; } }
        public void Dispose() { Prearm.Clear(); Close(); }
    }
    /// <summary>常驻捕获同时打开的设备上限；超出的设备记为 limit。</summary>
    internal const int MaximumDevices = 32;
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
    /// <summary>
    /// 捕获绑定游戏进程的游戏流。<paramref name="devices"/> 为自动模式的全部设备或指定模式的那一张；每张设备一个句柄，
    /// 过滤器只放行游戏进程本地端口（与已确认游戏流的远端端口），流经帧头特征确认后才解码（见 <see cref="CaptureStreamRouter"/>）。
    /// </summary>
    internal static async Task ObserveAsync(SelectedGameProcess game, IReadOnlyList<CaptureDevice> devices, string gameSessionId,
        ObservationCaptureContext context, CancellationToken token, CombatProtocolLayout layout = CombatProtocolLayout.MusicOnly,
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
        // 帧头特征使用与分帧相同的协议号集合：未登记构建取运行时读出的协议号。
        IReadOnlySet<int> signature = layout == CombatProtocolLayout.MusicOnly
            ? TwGameProtocol.KnownProtocols.Where(code => code != 0).ToHashSet()
            : (build?.KnownProtocols ?? Tw20261110CombatProtocol.StreamProfile.KnownProtocols).Where(code => code != 0).ToHashSet();
        var timer = Stopwatch.StartNew(); double lastPoll = -1000, lastReport = -1000; long packets = 0;
        var observations = new CaptureObservationSelection();
        long generation = 0; var lastCombatInitializer = double.NegativeInfinity;
        // 本机身份跨连接沿用（每次进图、换频道都换连接），范围限于本次捕获绑定的游戏进程；
        // 初值为宿主下发的同一游戏会话最近确认身份（Worker 重启后恢复）。
        var localIdentities = new LocalIdentityMemory(knownIdentity);
        var lastInventoryInitializer = double.NegativeInfinity;
        // 地图名称表属于本次绑定的客户端，首次进图时读取一次；换绑定即新的捕获与新的缓存。
        var imagePath = game.ImagePath;
        var maps = layout == CombatProtocolLayout.Tw20261110
            ? new MapLocationResolver(ct => ClientMapCatalog.Read(ClientItemCatalogLocation.Resolve(imagePath), ct), token) : null;
        CaptureStreamRouter? router = null;
        bool InventoryPrimary(SharedGameStream stream)
        {
            var latest = router!.Parsers.Where(value => !double.IsNegativeInfinity(value.InventoryInitializerAt))
                .MaxBy(value => value.InventoryInitializerAt);
            if (latest is null || latest.InventoryInitializerAt < lastInventoryInitializer) return false;
            lastInventoryInitializer = latest.InventoryInitializerAt; return ReferenceEquals(latest, stream);
        }
        bool Primary(SharedGameStream stream)
        {
            var latest = router!.Parsers.Where(value => !double.IsNegativeInfinity(value.CombatInitializerAt))
                .MaxBy(value => value.CombatInitializerAt);
            if (latest is null || latest.CombatInitializerAt < lastCombatInitializer) return false;
            lastCombatInitializer = latest.CombatInitializerAt; return ReferenceEquals(latest, stream);
        }
        router = new CaptureStreamRouter(Math.Min(devices.Count, MaximumDevices), signature, context.Diagnostics.Stream,
            () => new(gameSessionId, context, () => checked((ulong)Interlocked.Increment(ref generation)), Primary, layout, InventoryPrimary, localIdentities, build));
        var taps = devices.Take(MaximumDevices).Select(device => OpenTap(device, context)).ToArray();
        var gamePorts = new HashSet<ushort>(); string? filter = null;
        void Publish() => context.Diagnostics.Devices(taps.Select((tap, index) => new CaptureDeviceCounters(NpcapObservationSource.ChannelId(tap.Device.Name),
            tap.Device.Kind, tap.Link, tap.Open, tap.Issue, router.PrimaryDevice == index, new((ulong)router.Devices[index].Packets),
            new((ulong)router.Devices[index].PortPackets), new((ulong)router.Devices[index].GameStreams), new((ulong)router.Devices[index].Frames),
            new((ulong)router.Devices[index].CandidateDropped)))
            .Concat(devices.Skip(MaximumDevices).Take(MaximumDevices).Select(device => new CaptureDeviceCounters(NpcapObservationSource.ChannelId(device.Name),
                device.Kind, -1, false, "limit", false, new(0), new(0), new(0), new(0)))).ToArray());
        try
        {
            if (!taps.Any(tap => tap.Open))
                throw new IOException("Npcap 无法打开网卡：" + string.Join("、", taps.Select(tap => tap.Issue).Distinct()));
            while (!token.IsCancellationRequested)
            {
                var now = timer.Elapsed.TotalMilliseconds;
                if (now - lastPoll >= 500)
                {
                    if (!SelectedGameProcess.IsRunning(game)) throw new IOException("所选游戏进程已退出或重新启动。");
                    var table = Table();
                    gamePorts = table.Where(row => row.ProcessId == game.ProcessId && row.State is >= 3 and <= 8).Select(row => row.Connection.LocalPort).ToHashSet();
                    context.Execute(() => router.Retain(gamePorts, now));
                    // 新连接：本地端口已被连接表列为游戏进程所有的预缓冲四元组按到达顺序交给流路由（时点用抓包时刻），
                    // 列为其他进程所有的丢弃，其余由保留时长兜底。确认归属之前缓冲不交给任何解析器。
                    for (var index = 0; index < taps.Length; index++)
                    {
                        var device = index;
                        context.Execute(() => router.ReleasePrearmed(device, taps[device].Prearm, gamePorts,
                            tuple => table.Any(row => row.Connection == tuple && row.ProcessId != game.ProcessId), Mono()));
                    }
                    var next = NativeCaptureBoundary.Filter(gamePorts.Concat(router.LocalPorts).Distinct(), router.RemotePorts);
                    if (next != filter)
                    {
                        foreach (var tap in taps.Where(tap => tap.Open))
                            if (!Apply(tap.Handle, next)) tap.Fail("filter");
                        filter = next;
                    }
                    for (var index = 0; index < taps.Length; index++)
                    {
                        var tap = taps[index];
                        if (!tap.Open || pcap_stats(tap.Handle, out var stats) != 0) continue;
                        var drops = unchecked(stats.Dropped + stats.InterfaceDropped);
                        if (drops == tap.Drops) continue;
                        context.Diagnostics.DriverDrops(tap.Drops, drops); tap.Drops = drops;
                        var device = index;
                        context.Execute(() => router.DeviceDropped(device, Mono(), DateTimeOffset.UtcNow));
                    }
                    if (!taps.Any(tap => tap.Open)) throw new IOException("Npcap 读取中断，所有网卡都已关闭。");
                    Publish();
                    lastPoll = now;
                }
                for (var index = 0; index < taps.Length; index++)
                {
                    var tap = taps[index];
                    for (var i = 0; i < 200 && tap.Open; i++)
                    {
                        var result = pcap_next_ex(tap.Handle, out var header, out var pointer);
                        if (result == 0) break;
                        if (result != 1) { tap.Fail("read"); break; }
                        if (i == 199) context.Diagnostics.ReadLimitHit();
                        var length = Marshal.ReadInt32(header + 8); var wireLength = Marshal.ReadInt32(header + 12);
                        context.Diagnostics.Packet(length); router.Devices[index].Packets++;
                        if (!NativeCaptureBoundary.CompletePacket(length, wireLength)) { context.Diagnostics.Truncated(); continue; }
                        var raw = new byte[length]; Marshal.Copy(pointer, raw, 0, length); packets++;
                        var packet = NativeCaptureBoundary.Read(tap.Link, raw);
                        if (packet is null) { context.Diagnostics.Filtered(); continue; }
                        // Use Npcap's packet timestamp, not a later UI/parser wake.
                        // An invalid mapping loses only this frame's timing estimate.
                        var mono = Mono(); var (captured, utc) = CapturedAt(header, mono);
                        var device = index; var accepted = false;
                        context.Execute(() => accepted = router.Dispatch(device, packet, gamePorts, tap.Prearm, mono, captured, utc, DateTimeOffset.UtcNow));
                        if (!accepted) context.Diagnostics.Filtered();
                    }
                }
                context.Execute(() => router.Tick(Mono(), DateTimeOffset.UtcNow));
                if (now - lastReport >= 500)
                {
                    var observation = SharedGameStream.ReportIdentity(
                        observations.Select(router.Parsers.Select(parser => (parser.Music, parser.MusicInitializerAt))),
                        router.Parsers, localIdentities);
                    var status = router.ConfirmedCount == 0 ? "等待游戏连接的数据。" : observation.Detail;
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
            try { context.Execute(router.Dispose); }
            finally { NativeCaptureBoundary.DisposeAll(taps); }
        }
    }

    /// <summary>打开一张设备：4 MiB 内核缓冲、先设不放行任何包的过滤器、非阻塞；失败只记原因码（open、link:值、filter），不抛出。</summary>
    private static DeviceTap OpenTap(CaptureDevice device, ObservationCaptureContext context)
    {
        var tap = new DeviceTap(device, context);
        var error = new StringBuilder(256);
        try { tap.Handle = OpenConfigured(device.Name, error, NativeCaptureBoundary.DeviceBufferBytes); }
        catch (IOException) { tap.Issue = "open"; return tap; }
        tap.Link = pcap_datalink(tap.Handle);
        if (!GamePacket.SupportsLink(tap.Link)) { tap.Fail("link:" + tap.Link.ToString(System.Globalization.CultureInfo.InvariantCulture)); return tap; }
        if (!Apply(tap.Handle, NativeCaptureBoundary.Filter([], [])) || pcap_setnonblock(tap.Handle, 1, error) != 0) tap.Fail("filter");
        return tap;
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

