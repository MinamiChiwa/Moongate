using System.Diagnostics;
using System.Runtime.InteropServices;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 捕获线程的包处理与周期任务：设备读到的包交给流路由，连接表结果更新游戏端口集合与预缓冲交付，定时推进解析并发布状态报告。
/// 读取循环与离线回放共用；调用方在单一捕获线程上调用。时钟可替换，离线回放按样本时刻驱动。
/// </summary>
internal sealed class CapturePipeline : IDisposable
{
    private readonly ObservationCaptureContext context;
    private readonly CombatProtocolLayout layout;
    private readonly LocalIdentityMemory localIdentities;
    private readonly CaptureObservationSelection observations = new();
    private readonly MapLocationResolver? maps;
    private readonly Func<double> mono;
    private readonly Func<DateTimeOffset> utc;
    private double lastCombatInitializer = double.NegativeInfinity, lastInventoryInitializer = double.NegativeInfinity;
    private long generation;
    /// <summary>逐包复用的读取缓冲：解析只从中复制出 TCP 负载，不保留引用。</summary>
    private readonly byte[] raw = new byte[NativeCaptureBoundary.SnapshotBytes];
    public CaptureStreamRouter Router { get; }
    /// <summary>最近一次连接表读出的游戏进程本地端口。</summary>
    public HashSet<ushort> GamePorts { get; private set; } = [];
    /// <summary>完整读到的包数（状态报告的包计数）。</summary>
    public long Packets { get; private set; }

    public CapturePipeline(int devices, IReadOnlySet<int> signature, string gameSessionId, ObservationCaptureContext context,
        CombatProtocolLayout layout, TwBuildVerdict? build, LocalIdentityMemory localIdentities, MapLocationResolver? maps,
        Func<double>? mono = null, Func<DateTimeOffset>? utc = null)
    {
        this.context = context; this.layout = layout; this.localIdentities = localIdentities; this.maps = maps;
        this.mono = mono ?? Mono; this.utc = utc ?? (() => DateTimeOffset.UtcNow);
        Router = new CaptureStreamRouter(devices, signature, context.Diagnostics.Stream,
            () => new(gameSessionId, context, () => checked((ulong)Interlocked.Increment(ref generation)), Primary, layout, InventoryPrimary, localIdentities, build));
    }

    internal static double Mono() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);

    private bool InventoryPrimary(SharedGameStream stream)
    {
        var latest = Router.Parsers.Where(value => !double.IsNegativeInfinity(value.InventoryInitializerAt))
            .MaxBy(value => value.InventoryInitializerAt);
        if (latest is null || latest.InventoryInitializerAt < lastInventoryInitializer) return false;
        lastInventoryInitializer = latest.InventoryInitializerAt; return ReferenceEquals(latest, stream);
    }
    private bool Primary(SharedGameStream stream)
    {
        var latest = Router.Parsers.Where(value => !double.IsNegativeInfinity(value.CombatInitializerAt))
            .MaxBy(value => value.CombatInitializerAt);
        if (latest is null || latest.CombatInitializerAt < lastCombatInitializer) return false;
        lastCombatInitializer = latest.CombatInitializerAt; return ReferenceEquals(latest, stream);
    }

    /// <summary>Npcap 包头的抓包时刻换成单调毫秒与 UTC；映射无效时单调毫秒为空。</summary>
    private (double? Mono, DateTimeOffset Utc) CapturedAt(nint header, double now)
    {
        // Windows Npcap timeval uses two int32 fields; caplen follows at +8.
        var capturedUtc = Marshal.ReadInt32(header) * 1000d + Marshal.ReadInt32(header + 4) / 1000d;
        var age = utc().ToUnixTimeMilliseconds() - capturedUtc;
        return age is >= -100 and <= 10000
            ? (now - age, DateTimeOffset.FromUnixTimeMilliseconds((long)capturedUtc))
            : (null, utc());
    }

    /// <summary>一张设备读到的一个包（Npcap 包头与数据指针）。</summary>
    public void Packet(int device, int link, PrearmBuffer prearm, nint header, nint pointer)
    {
        var length = Marshal.ReadInt32(header + 8); var wireLength = Marshal.ReadInt32(header + 12);
        context.Diagnostics.Packet(length); Router.Devices[device].Packets++;
        if (!NativeCaptureBoundary.CompletePacket(length, wireLength)) { context.Diagnostics.Truncated(); return; }
        Marshal.Copy(pointer, raw, 0, length); Packets++;
        var packet = NativeCaptureBoundary.Read(link, raw.AsSpan(0, length));
        if (packet is null) { context.Diagnostics.Filtered(); return; }
        // Use Npcap's packet timestamp, not a later UI/parser wake.
        // An invalid mapping loses only this frame's timing estimate.
        var now = mono(); var (captured, capturedUtc) = CapturedAt(header, now);
        var accepted = false;
        context.Execute(() => accepted = Router.Dispatch(device, packet, GamePorts, prearm, now, captured, capturedUtc, utc()));
        if (!accepted) context.Diagnostics.Filtered();
    }

    /// <summary>
    /// 读连接表后：更新游戏端口集合，释放静默的流；预缓冲中本地端口已列为游戏进程所有的四元组按到达顺序交给流路由（时点用抓包时刻），
    /// 列为其他进程所有的丢弃，其余由保留时长兜底。确认归属之前缓冲不交给任何解析器。静默判定与包的到达时点用同一时钟。
    /// </summary>
    public void Refresh(IReadOnlyList<(CaptureConnection Connection, int ProcessId, int State)> table, int processId,
        IReadOnlyList<PrearmBuffer> prearms)
    {
        GamePorts = table.Where(row => row.ProcessId == processId && row.State is >= 3 and <= 8).Select(row => row.Connection.LocalPort).ToHashSet();
        var now = mono();
        context.Execute(() => Router.Retain(GamePorts, now, utc()));
        for (var index = 0; index < prearms.Count; index++)
        {
            var device = index;
            context.Execute(() => Router.ReleasePrearmed(device, prearms[device], GamePorts,
                tuple => table.Any(row => row.Connection == tuple && row.ProcessId != processId), mono()));
        }
    }

    /// <summary>当前应设的过滤器：游戏端口与仍保留的流的本地端口，及已确认游戏流的远端端口。</summary>
    public string Filter() => NativeCaptureBoundary.Filter(GamePorts.Concat(Router.LocalPorts).Distinct(), Router.RemotePorts);

    public void DeviceDropped(int device) => context.Execute(() => Router.DeviceDropped(device, mono(), utc()));

    public void Tick() => context.Execute(() => Router.Tick(mono(), utc()));

    /// <summary>状态报告（含本机身份）始终发送，音乐字段只在音乐兴趣开启时填写。</summary>
    public void Report()
    {
        var observation = SharedGameStream.ReportIdentity(
            observations.Select(Router.Parsers.Select(parser => (parser.Music, parser.MusicInitializerAt))),
            Router.Parsers, localIdentities);
        var status = Router.ConfirmedCount == 0 ? "等待游戏连接的数据。" : observation.Detail;
        var report = NpcapObservationSource.Project(new(observation, Packets, true, status), maps);
        if (layout == CombatProtocolLayout.Tw20261110) report = report with { Source = Tw20261110CombatProtocol.CombatProfile.Source };
        if (observation.Identity is { } reported && SharedGameStream.IdentityConnection(reported, Router.Parsers) is { } connection)
            report = report with { IdentityConnection = new(connection.LocalPort, connection.RemoteAddress, connection.RemotePort) };
        context.Publish(report);
    }

    public void Dispose() => context.Execute(Router.Dispose);
}
