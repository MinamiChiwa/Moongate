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
    internal sealed class DeviceTap(CaptureDevice device, ObservationCaptureContext context, PrearmPool? pool = null) : IDisposable
    {
        public CaptureDevice Device { get; } = device;
        public nint Handle { get; set; }
        /// <summary>句柄的读事件（即时模式下有包即触发）；取不到为 0。</summary>
        public nint Event { get; set; }
        public int Link { get; set; } = -1;
        public string? Issue { get; set; }
        public uint Drops { get; set; }
        public PrearmBuffer Prearm { get; } = pool?.Create(context.Diagnostics) ?? new(context.Diagnostics);
        public bool Open => Handle != 0;
        public void Fail(string issue) { Issue = issue; Close(); }
        /// <summary>换用已按新过滤器打开的句柄：旧句柄已读空，关闭；驱动丢包计数随新句柄从零开始。</summary>
        public void Swap(nint fresh, nint readEvent) { Close(); Handle = fresh; Event = readEvent; Drops = 0; }
        public void Close() { if (Handle != 0) { pcap_close(Handle); Handle = 0; Event = 0; } }
        public void Dispose() { Prearm.Clear(); Close(); }
    }
    /// <summary>常驻捕获同时打开的设备上限；超出的设备记为 limit。</summary>
    internal const int MaximumDevices = 32;
    /// <summary>有已确认游戏流时推进解析（技能关联、保活帧、缺口超时）的间隔。</summary>
    internal const double TickMs = 20;
    /// <summary>连接表读取、过滤器更新、驱动丢包检查与状态报告的间隔。</summary>
    internal const double HousekeepingMs = 500;
    /// <summary>
    /// 捕获绑定游戏进程的游戏流。<paramref name="devices"/> 为自动模式的全部设备或指定模式的那一张；每张设备一个句柄，
    /// 过滤器只放行游戏进程本地端口（与已确认游戏流的远端端口），流经帧头特征确认后才解码（见 <see cref="CaptureStreamRouter"/>）。
    /// </summary>
    internal static async Task ObserveAsync(SelectedGameProcess game, IReadOnlyList<CaptureDevice> devices, string gameSessionId,
        ObservationCaptureContext context, CancellationToken token, CombatProtocolLayout layout = CombatProtocolLayout.MusicOnly,
        GameIdentity? knownIdentity = null, ObservedConnection? knownConnection = null)
    {
        if (!SelectedGameProcess.IsRunning(game)) throw new CaptureFaultException("capture.processExited");
        // 当前布局：已登记构建直接启用；未登记构建在本进程内独立读取客户端事实核对，分帧改用读到的协议号，
        // 核对不过的身份、音乐或战斗解析单独停用。读不全客户端时不启动。
        TwBuildVerdict? build = null;
        if (layout == CombatProtocolLayout.Tw20261110)
        {
            build = await TwBuildVerification.EvaluateAsync(game.ImagePath, ObservationCache.Directory("game-access", "client-builds"), token).ConfigureAwait(false);
            if (build is not { CaptureUsable: true }) throw new WorkerObservationException("gameBuild.incompatible");
        }
        else await VerifyClientAsync(game.ImagePath, token).ConfigureAwait(false);
        // 帧头特征使用与分帧相同的协议号集合：未登记构建取运行时读出的协议号。
        IReadOnlySet<int> signature = layout == CombatProtocolLayout.MusicOnly
            ? TwGameProtocol.KnownProtocols.Where(code => code != 0).ToHashSet()
            : (build?.KnownProtocols ?? Tw20261110CombatProtocol.StreamProfile.KnownProtocols).Where(code => code != 0).ToHashSet();
        var timer = Stopwatch.StartNew();
        // 本机身份跨连接沿用（每次进图、换频道都换连接），范围限于本次捕获绑定的游戏进程；
        // 初值为宿主下发的同一游戏进程最近确认身份及其实体所属的连接（捕获重启后恢复）。
        var localIdentities = new LocalIdentityMemory(knownIdentity, knownConnection);
        // 地图名称表属于本次绑定的客户端，首次进图时读取一次；换绑定即新的捕获与新的缓存。
        var imagePath = game.ImagePath;
        var maps = layout == CombatProtocolLayout.Tw20261110
            ? new MapLocationResolver(ct => ClientMapCatalog.Read(ClientItemCatalogLocation.Resolve(imagePath), ct), token) : null;
        var pipeline = new CapturePipeline(Math.Min(devices.Count, MaximumDevices), signature, gameSessionId, context, layout, build, localIdentities, maps);
        var router = pipeline.Router;
        var config = CaptureReadConfig.Resident;
        var pool = new PrearmPool();
        var taps = devices.Take(MaximumDevices).Select(device => OpenTap(device, context, config, pool)).ToArray();
        var prearms = taps.Select(tap => tap.Prearm).ToArray();
        // 全部打开的设备都取到读事件时等待读事件；否则（旧版 Npcap）整次捕获按固定间隔轮询。
        var fallback = config.Mode != CaptureReadMode.Poll && taps.Any(tap => tap.Open && tap.Event == 0);
        if (fallback) config = config.Fallback;
        context.ReadConfig = config;
        context.Diagnostics.ReadMode(config.Counters(fallback));
        string? filter = null;
        void Publish() => context.Diagnostics.Devices(taps.Select((tap, index) => new CaptureDeviceCounters(NpcapObservationSource.ChannelId(tap.Device.Name),
            tap.Device.Kind, tap.Link, tap.Open, tap.Issue, router.PrimaryDevice == index, new((ulong)router.Devices[index].Packets),
            new((ulong)router.Devices[index].PortPackets), new((ulong)router.Devices[index].GameStreams), new((ulong)router.Devices[index].Frames),
            new((ulong)router.Devices[index].CandidateDropped)))
            .Concat(devices.Skip(MaximumDevices).Take(MaximumDevices).Select(device => new CaptureDeviceCounters(NpcapObservationSource.ChannelId(device.Name),
                device.Kind, -1, false, "limit", false, new(0), new(0), new(0), new(0)))).ToArray());
        int ReadTap(int index, DeviceTap tap) => CaptureChannels.ReadTap(index, tap, context, pipeline);
        void Housekeeping(double now)
        {
            if (!SelectedGameProcess.IsRunning(game)) throw new CaptureFaultException("capture.processExited");
            pipeline.Refresh(Table(), game.ProcessId, prearms);
            var next = pipeline.Filter();
            if (next != filter)
            {
                // Npcap 设置过滤器时清空该句柄的内核缓冲（本机回环实验：每次重设丢一段约 25 KB 的已捕获数据，pcap_stats 也随之清零），
                // 游戏连接增减越频繁缺口越多。首次之后改为交接：以新过滤器另开句柄，旧句柄读空后关闭，重叠部分由重组按序号去重。
                for (var index = 0; index < taps.Length; index++)
                {
                    var tap = taps[index];
                    if (!tap.Open) continue;
                    if (filter is null) { if (!Apply(tap.Handle, next)) tap.Fail("filter"); continue; }
                    var (fresh, readEvent) = OpenHandle(tap, next, config);
                    if (fresh == 0) { if (!Apply(tap.Handle, next)) tap.Fail("filter"); continue; }
                    for (var round = 0; round < 64 && ReadTap(index, tap) == 200; round++) { }
                    tap.Swap(fresh, readEvent);
                }
                filter = next;
            }
            for (var index = 0; index < taps.Length; index++)
            {
                var tap = taps[index];
                if (!tap.Open || pcap_stats(tap.Handle, out var stats) != 0) continue;
                var drops = unchecked(stats.Dropped + stats.InterfaceDropped);
                if (drops == tap.Drops) continue;
                context.Diagnostics.DriverDrops(tap.Drops, drops); tap.Drops = drops;
                pipeline.DeviceDropped(index);
            }
            if (!taps.Any(tap => tap.Open)) throw new CaptureFaultException("capture.allDevicesClosed");
            Publish();
        }
        try
        {
            if (!taps.Any(tap => tap.Open))
                throw new CaptureFaultException("capture.devicesUnavailable");
            // 读取循环在独立线程上运行：等待读事件会阻塞线程，不占用线程池。
            await Task.Factory.StartNew(() => ReadLoop(taps, config, timer, token, Housekeeping, ReadTap, pipeline),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            try { pipeline.Dispose(); }
            finally { NativeCaptureBoundary.DisposeAll(taps); }
        }
    }

    /// <summary>读一张设备至多 200 个包交给管线，返回读到的包数；读取失败时关闭该设备。</summary>
    internal static int ReadTap(int index, DeviceTap tap, ObservationCaptureContext context, CapturePipeline pipeline)
    {
        var read = 0;
        for (var i = 0; i < 200 && tap.Open; i++)
        {
            var result = pcap_next_ex(tap.Handle, out var header, out var pointer);
            if (result == 0) break;
            if (result != 1) { tap.Fail("read"); break; }
            read++;
            if (i == 199) context.Diagnostics.ReadLimitHit();
            pipeline.Packet(index, tap.Link, tap.Prearm, header, pointer);
        }
        return read;
    }

    /// <summary>
    /// 读取循环。事件方式：等待任一设备的读事件（或取消），被唤醒后读空已触发的设备；持续有包时（距上次唤醒不足合批间隔）先等合批间隔再读，
    /// 空闲时只按连接表读取与状态报告的间隔醒来。有已确认游戏流时每 <see cref="TickMs"/> 推进一次解析。轮询方式（旧版 Npcap）：每轮读全部设备、
    /// 推进解析后等待轮询间隔。
    /// </summary>
    internal static void ReadLoop(DeviceTap[] taps, CaptureReadConfig config, Stopwatch timer, CancellationToken token,
        Action<double> housekeeping, Func<int, DeviceTap, int> read, CapturePipeline pipeline)
    {
        var events = new nint[taps.Length + 1]; var owners = new int[taps.Length];
        var cancel = token.WaitHandle;
        double lastHousekeeping = double.NegativeInfinity, lastReport = double.NegativeInfinity, lastTick = double.NegativeInfinity, lastWake = double.NegativeInfinity;
        while (!token.IsCancellationRequested)
        {
            var now = timer.Elapsed.TotalMilliseconds;
            if (now - lastHousekeeping >= HousekeepingMs) { housekeeping(now); lastHousekeeping = now; }
            var saturated = false; var packets = false;
            if (config.Mode == CaptureReadMode.Poll)
                for (var index = 0; index < taps.Length; index++) { var count = read(index, taps[index]); saturated |= count == 200; packets |= count > 0; }
            now = timer.Elapsed.TotalMilliseconds;
            if (config.Mode == CaptureReadMode.Poll || pipeline.Router.ConfirmedCount > 0 && now - lastTick >= TickMs) { pipeline.Tick(); lastTick = now; }
            if (now - lastReport >= HousekeepingMs) { pipeline.Report(); lastReport = now; }
            if (config.Mode == CaptureReadMode.Poll) { cancel.WaitOne(config.TimeoutMs); continue; }
            var due = Math.Min(lastHousekeeping, lastReport) + HousekeepingMs;
            if (pipeline.Router.ConfirmedCount > 0) due = Math.Min(due, lastTick + TickMs);
            var waiting = 0;
            for (var index = 0; index < taps.Length; index++)
                if (taps[index].Open && taps[index].Event != 0) { owners[waiting] = index; events[waiting++] = taps[index].Event; }
            events[waiting] = cancel.SafeWaitHandle.DangerousGetHandle();
            var signaled = CaptureChannels.WaitAny(events, waiting + 1, (int)Math.Ceiling(Math.Max(0, due - timer.Elapsed.TotalMilliseconds)));
            if (signaled == waiting) break;
            // 读空已触发的设备：读完一张后以零超时再查，直到没有设备触发。
            for (var guard = 0; signaled >= 0 && guard <= taps.Length; guard++)
            {
                var count = read(owners[signaled], taps[owners[signaled]]); saturated |= count == 200; packets |= count > 0;
                signaled = waiting == 0 ? -1 : CaptureChannels.WaitAny(events, waiting, 0);
            }
            var wake = timer.Elapsed.TotalMilliseconds;
            // 持续有包：距上次唤醒不足合批间隔时先等合批间隔，让高流量下成批读取而不是逐包唤醒。
            if (packets && !saturated && wake - lastWake < config.CoalesceMs) cancel.WaitOne(config.CoalesceMs);
            if (packets) lastWake = wake;
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>打开一张设备：按读取方式设置内核缓冲与读事件、先设不放行任何包的过滤器、非阻塞；失败只记原因码（open、link:值、filter），不抛出。</summary>
    internal static DeviceTap OpenTap(CaptureDevice device, ObservationCaptureContext context, CaptureReadConfig config, PrearmPool? pool = null)
    {
        var tap = new DeviceTap(device, context, pool);
        var error = new StringBuilder(256);
        try
        {
            var (handle, readEvent, issue) = OpenReadHandle(device.Name, config, NativeCaptureBoundary.Filter([], []), error, out var link);
            tap.Link = link;
            if (issue is not null) { tap.Issue = issue; return tap; }
            (tap.Handle, tap.Event) = (handle, readEvent);
        }
        catch (IOException) { tap.Issue = "open"; }
        return tap;
    }
    /// <summary>原地更换一张设备的过滤器（首次设置与交接失败时的回退）。</summary>
    internal static bool ApplyFilter(DeviceTap tap, string filter) => tap.Open && Apply(tap.Handle, filter);
    /// <summary>为过滤器交接另开同一设备的句柄并直接设好新过滤器；打不开、链路类型变化、过滤器设置失败或（事件方式下）取不到读事件时返回 0。</summary>
    private static (nint Handle, nint Event) OpenHandle(DeviceTap tap, string filter, CaptureReadConfig config)
    {
        var error = new StringBuilder(256);
        try
        {
            var (handle, readEvent, issue) = OpenReadHandle(tap.Device.Name, config, filter, error, out var link);
            if (issue is not null) return (0, 0);
            if (link != tap.Link || config.Mode != CaptureReadMode.Poll && readEvent == 0) { pcap_close(handle); return (0, 0); }
            return (handle, readEvent);
        }
        catch (IOException) { return (0, 0); }
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

