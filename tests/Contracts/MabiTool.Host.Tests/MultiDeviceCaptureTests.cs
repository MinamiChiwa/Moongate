using System.Buffers.Binary;
using System.Net;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>
/// 全部网卡同时抓：按游戏帧头确认流、同一流在多张设备上只解码一份、端口集合变化后接住新连接开头，以及 IPv6 与原始 IP 链路。
/// 包按各链路的真实字节构造，经 <see cref="NativeCaptureBoundary.Read"/> 解析后交给流路由，路由之后的解析与生产路径相同。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiDeviceCaptureTests
{
    private static readonly ObservationCombatProfile Profile = SyntheticCombat.Profile;
    private const int Ethernet = 1, Null = 0, Raw = 12;

    [Fact]
    public void GameFlowOnlyOnTheLoopbackIsDecodedWhileTlsAndSelfConnectionsAreNot()
    {
        // 三张设备同时打开：物理网卡上只有 TLS（游戏进程的 443 连接），回环上有本机代理转来的游戏流与进程自连。
        var (router, reports, context) = Router(3);
        var game = Flow("192.168.0.105", 50128, "192.168.0.105", 55331);
        var tls = Flow("18.166.193.141", 443, "172.19.84.237", 14408);
        var self = Flow("127.0.0.1", 7739, "127.0.0.1", 7740);
        double at = 1000;
        Send(router, 0, Ethernet, tls.Syn(), at); Send(router, 1, Null, game.Syn(), at); Send(router, 1, Null, self.Syn(), at);
        var record = new byte[64]; record[0] = 0x17; record[1] = 3; record[2] = 3; record.AsSpan(5).Fill(0xab);
        for (var i = 0; i < 5; i++) { Send(router, 0, Ethernet, tls.Data(record), at += 10); Send(router, 1, Null, self.Data(new byte[16]), at); }
        // 代理握手字节先到，之后是身份帧与一次伤害。
        Send(router, 1, Null, game.Data([5, 0, 0, 1]), at += 10);
        foreach (var frame in SyntheticCombat.Identity().Append(SyntheticCombat.Damage(100))) Send(router, 1, Null, game.Data(frame), at += 10);
        router.Tick(at + 300, At(at + 300));

        Assert.Equal(100UL, Assert.Single(reports.SelectMany(report => report.Frame.Damage)).Amount.Value);
        Assert.Equal((1, 3), (router.ConfirmedCount, router.Count));
        Assert.Equal([0L, 1L, 0L], router.Devices.Select(device => device.GameStreams));
        Assert.True(router.Devices[1].Frames >= 4); Assert.Equal(0, router.Devices[0].Frames);
        Assert.Equal(1, router.PrimaryDevice);
        var stream = context.Diagnostics.Stream.Snapshot();
        Assert.Equal(0UL, stream.DecompressFailures);
        Assert.Equal(["connectionSyn"], stream.Resets.Keys);
    }

    [Fact]
    public void SameFlowOnTwoDevicesIsDecodedOnceAndAnotherDeviceTakesOverWhenThePrimaryGoesQuiet()
    {
        // 隧道网卡（DLT_RAW）与另一张以太网卡同时看到同一条流：只解码一份；隧道停了以后由以太网卡接续。
        var (router, reports, _) = Router(2);
        var game = Flow("43.212.230.170", 16000, "172.19.84.237", 1714);
        double at = 1000;
        void Both(Segment segment) { Send(router, 0, Raw, segment, at); Send(router, 1, Ethernet, segment, at + 1); at += 10; }
        Both(game.Syn());
        foreach (var frame in SyntheticCombat.Identity().Append(SyntheticCombat.Damage(100))) Both(game.Data(frame));
        Send(router, 1, Ethernet, game.Data(SyntheticCombat.Damage(250)), at += CaptureStreamRouter.FailoverMs + 500);
        router.Tick(at + 300, At(at + 300));

        Assert.Equal([100UL, 250UL], reports.SelectMany(report => report.Frame.Damage).Select(damage => damage.Amount.Value));
        Assert.True(router.Duplicates >= 4); Assert.Equal(1, router.Failovers);
        Assert.Equal([1L, 0L], router.Devices.Select(device => device.GameStreams));
        Assert.Equal(1, router.PrimaryDevice);
    }

    [Fact]
    public void NewIpv6ConnectionIsPickedUpFromItsStartAfterThePortSetChanges()
    {
        var (router, reports, context) = Router(1);
        var prearm = new PrearmBuffer(context.Diagnostics);
        var ports = new HashSet<ushort> { 1714 };
        var first = Flow("2001:db8::10", 16000, "2001:db8::2", 1714);
        double at = 1000;
        Assert.True(Dispatch(router, ports, prearm, first.Syn(), at));
        foreach (var frame in SyntheticCombat.Identity()) Assert.True(Dispatch(router, ports, prearm, first.Data(frame), at += 10));
        Assert.Contains((ushort)16000, router.RemotePorts);
        Assert.Equal("tcp and (dst port 1714 or src port 16000)", NativeCaptureBoundary.Filter(ports, router.RemotePorts));

        // 换图后的新连接：连接表还没列出新本地端口，开头（含 SYN 与伤害）进预缓冲；列出后从 SYN 起按序交付。
        var next = Flow("2001:db8::11", 16000, "2001:db8::2", 1800);
        Assert.True(Dispatch(router, ports, prearm, next.Syn(), at += 10));
        foreach (var frame in SyntheticCombat.Identity().Append(SyntheticCombat.Damage(300))) Assert.True(Dispatch(router, ports, prearm, next.Data(frame), at += 10));
        Assert.False(Dispatch(router, ports, prearm, Flow("2001:db8::99", 8080, "2001:db8::2", 1900).Data([1, 2, 3]), at));
        Assert.Empty(reports.SelectMany(report => report.Frame.Damage));
        ports.Add(1800);
        router.ReleasePrearmed(0, prearm, ports, _ => false, at + 500);
        router.Tick(at + 800, At(at + 800));

        Assert.Equal(300UL, Assert.Single(reports.SelectMany(report => report.Frame.Damage)).Amount.Value);
        Assert.Equal(2, router.ConfirmedCount); Assert.Equal(0, prearm.Packets);
        // 旧连接不在连接表中且静默满宽限期后释放。
        ports.Remove(1714);
        router.Retain(ports, at + 800 + ConnectionRetention.GraceMs, At(at + 800 + ConnectionRetention.GraceMs));
        Assert.Equal(1, router.Count);
    }

    [Fact]
    public void UnlistedConnectionIsKeptWhileItsDataFlowsAndAnEndedOneIsReleasedAfterTheGrace()
    {
        // 包时点与连接表读取用同一单调时钟（从开机一天后的读数起）：连接表漏列仍在传输的连接时保留，已结束的连接静默满宽限期后释放。
        List<CombatCaptureReport> reports = [];
        var context = new ObservationCaptureContext([Profile], _ => { }, reports.Add);
        context.Configure(new(true, new(new string('a', 32), Profile.Id, true, true, true)));
        var clock = 86_400_000d;
        using var pipeline = new CapturePipeline(1, Tw20261110CombatProtocol.StreamProfile.KnownProtocols, "fixture-game", context,
            CombatProtocolLayout.Tw20261110, null, new LocalIdentityMemory(), null, () => clock, () => At(clock));
        var prearm = new PrearmBuffer(context.Diagnostics);
        static (CaptureConnection, int, int) Row(ushort port) => (new("172.19.84.237", port, "43.212.230.170", 16000), 4242, 5);
        var active = Flow("43.212.230.170", 16000, "172.19.84.237", 1714); var ended = Flow("43.212.230.170", 16000, "172.19.84.237", 1800);
        pipeline.Refresh([Row(1714), Row(1800)], 4242, [prearm]);
        foreach (var flow in new[] { active, ended })
        {
            Assert.True(Dispatch(pipeline.Router, pipeline.GamePorts, prearm, flow.Syn(), clock += 10));
            foreach (var frame in SyntheticCombat.Identity()) Assert.True(Dispatch(pipeline.Router, pipeline.GamePorts, prearm, flow.Data(frame), clock += 10));
        }
        Assert.Equal(2, pipeline.Router.ConfirmedCount);
        Assert.True(Dispatch(pipeline.Router, pipeline.GamePorts, prearm, ended.Fin(), clock += 10));
        // 连接表此后两条都不列出；仍在传输的连接每 500 ms 有包。
        for (var step = 0; step < 14; step++)
        {
            clock += 500;
            Assert.True(Dispatch(pipeline.Router, pipeline.GamePorts, prearm, active.Data(SyntheticCombat.Damage(100)), clock));
            pipeline.Refresh([], 4242, [prearm]);
            Assert.Equal(step < 9 ? 2 : 1, pipeline.Router.Count);
        }
        Assert.True(pipeline.Router.Contains(new("172.19.84.237", 1714, "43.212.230.170", 16000)));
        Assert.False(pipeline.Router.Contains(new("172.19.84.237", 1800, "43.212.230.170", 16000)));
    }

    [Fact]
    public void LargeFirstFrameSplitAcrossSegmentsIsKeptWhenTheFlowIsConfirmedLater()
    {
        // 连接开头：代理握手字节之后是一帧约 5000 字节、跨 4 个段的大帧（首段里第二个帧头不在段内，单段判断确认不了），随后是身份帧与伤害。
        var (router, reports, context) = Router(1);
        var game = Flow("192.168.0.105", 50128, "192.168.0.105", 55331);
        var large = LargeFrame(5000, out var code);
        byte[] first = [5, 0, 0, 1, .. large];
        double at = 1000;
        Send(router, 0, Null, game.Syn(), at);
        for (var offset = 0; offset < first.Length; offset += 1260) Send(router, 0, Null, game.Data(first[offset..Math.Min(first.Length, offset + 1260)]), at += 5);
        Assert.Equal(0, router.ConfirmedCount);
        foreach (var frame in SyntheticCombat.Identity().Append(SyntheticCombat.Damage(100))) Send(router, 0, Null, game.Data(frame), at += 5);
        router.Tick(at + 300, At(at + 300));

        Assert.Equal(1, router.ConfirmedCount);
        Assert.Equal(100UL, Assert.Single(reports.SelectMany(report => report.Frame.Damage)).Amount.Value);
        var stream = context.Diagnostics.Stream.Snapshot();
        Assert.Equal(1UL, stream.Types[code]); Assert.Equal(5UL, stream.Frames);
        Assert.Equal(0UL, stream.DecompressFailures); Assert.Equal(["connectionSyn"], stream.Resets.Keys);
    }

    [Fact]
    public void FlowJoinedInsideALargeFrameIsConfirmedWithinTheCandidateLimit()
    {
        // 中途接入一帧约 400 KB 大帧的正文（已知最大单帧约 356 KB）：正文段不能确认，之后的游戏帧到达时确认，从最早的段开始重组并对齐到游戏帧。
        var (router, reports, context) = Router(1);
        var game = Flow("43.212.230.170", 16000, "172.19.84.237", 1714);
        double at = 1000;
        var body = new byte[57000];
        for (var i = 0; i < 7; i++) Send(router, 0, Raw, game.Data(body), at += 5);
        Assert.Equal((0, 0L), (router.ConfirmedCount, router.RejectedStreams));
        foreach (var frame in SyntheticCombat.Identity().Append(SyntheticCombat.Damage(100))) Send(router, 0, Raw, game.Data(frame), at += 5);
        router.Tick(at + 300, At(at + 300));

        Assert.Equal(100UL, Assert.Single(reports.SelectMany(report => report.Frame.Damage)).Amount.Value);
        Assert.Equal((0L, 0L), (router.Devices[0].CandidateDropped, router.CandidateBufferedBytes));
        var stream = context.Diagnostics.Stream.Snapshot();
        Assert.Equal(4UL, stream.Frames); Assert.Equal(0UL, stream.DecompressFailures);
    }

    [Fact]
    public void NonGameFlowIsDroppedAfterTheCandidateLimitUntilThePortIsReused()
    {
        // 游戏进程的 TLS 连接：512 KiB 内不能按帧头确认即判为非游戏流，释放候选缓冲，之后的包不再匹配（返回假，计为过滤）；
        // 同一四元组的 SYN（端口复用）后重新匹配。
        var (router, reports, _) = Router(1);
        var ports = new HashSet<ushort> { 14408 };
        var prearm = new PrearmBuffer();
        var tls = Flow("18.166.193.141", 443, "172.19.84.237", 14408);
        var record = new byte[1400]; record[0] = 0x17; record[1] = 3; record[2] = 3; record.AsSpan(5).Fill(0xab);
        double at = 1000;
        var sent = 0;
        while (router.RejectedStreams == 0 && sent < 1000) { Assert.True(Dispatch(router, ports, prearm, tls.Data(record), at += 1)); sent++; }
        Assert.Equal((1L, 0L), (router.RejectedStreams, router.CandidateBufferedBytes));
        Assert.Equal((CaptureStreamRouter.CandidateBytes + record.Length - 1) / record.Length, sent);
        foreach (var frame in SyntheticCombat.Identity().Append(SyntheticCombat.Damage(100))) Assert.False(Dispatch(router, ports, prearm, tls.Data(frame), at += 1));
        Assert.True(Dispatch(router, ports, prearm, tls.Syn(), at += 1));
        foreach (var frame in SyntheticCombat.Identity().Append(SyntheticCombat.Damage(250))) Assert.True(Dispatch(router, ports, prearm, tls.Data(frame), at += 1));
        router.Tick(at + 300, At(at + 300));

        Assert.Equal(250UL, Assert.Single(reports.SelectMany(report => report.Frame.Damage)).Amount.Value);
        Assert.Equal(1, router.ConfirmedCount);
    }

    [Fact]
    public void CandidateBudgetAcrossFlowsDropsTheOldestSegmentsOfTheLargestFlow()
    {
        // 9 条未确认流各约 480–510 KB（低于单流门槛），合计超出 4 MiB：从占用最多的流（第一条）丢最早的段；该流确认后丢弃的字节计入 candidateDropped。
        var (router, reports, _) = Router(1);
        var flows = Enumerable.Range(0, 9).Select(i => Flow("43.212.230.170", 16000, "172.19.84.237", (ushort)(1714 + i))).ToArray();
        var filler = new byte[60000];
        double at = 1000;
        Send(router, 0, Raw, flows[0].Data(new byte[30000]), at += 1);
        for (var round = 0; round < 8; round++) foreach (var flow in flows) Send(router, 0, Raw, flow.Data(filler), at += 1);
        Assert.True(router.CandidateBufferedBytes <= CaptureStreamRouter.CandidateBudgetBytes);
        Assert.Equal(0L, router.RejectedStreams);
        foreach (var frame in SyntheticCombat.Identity().Append(SyntheticCombat.Damage(100))) Send(router, 0, Raw, flows[0].Data(frame), at += 1);
        router.Tick(at + 300, At(at + 300));

        Assert.Equal(100UL, Assert.Single(reports.SelectMany(report => report.Frame.Damage)).Amount.Value);
        Assert.True(router.Devices[0].CandidateDropped > 0);
    }

    /// <summary>一帧未压缩的已知、但不被当前兴趣解码的协议帧（正文全零）。</summary>
    private static byte[] LargeFrame(int body, out int code)
    {
        var observed = Tw20261110CombatProtocol.StreamProfile.ObservedProtocols;
        code = Tw20261110CombatProtocol.StreamProfile.KnownProtocols.Where(value => value > 0 && !observed.Contains(value) && !TwGameProtocol.KnownProtocols.Contains(value)).Min();
        var frame = new byte[9 + body];
        BinaryPrimitives.WriteInt32LittleEndian(frame, code); BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body);
        return frame;
    }

    [Fact]
    public void LinkTypesAndIpv6ExtensionHeadersAreParsed()
    {
        var segment = Flow("2001:db8::10", 16000, "2001:db8::2", 1714).Data([9, 8, 7]);
        // 逐跳选项扩展头之后才是 TCP。
        var tcp = Tcp(segment);
        var ip = new byte[40 + 8 + tcp.Length];
        ip[0] = 0x60; BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), (ushort)(8 + tcp.Length)); ip[6] = 0; ip[7] = 64;
        IPAddress.Parse(segment.Source).GetAddressBytes().CopyTo(ip, 8); IPAddress.Parse(segment.Destination).GetAddressBytes().CopyTo(ip, 24);
        ip[40] = 6; ip[41] = 0; tcp.CopyTo(ip, 48);
        foreach (var (link, frame) in new[] { (101, ip), (229, ip), (Ethernet, Wrap(Ethernet, ip, true)), (Null, Wrap(Null, ip, true)) })
        {
            var packet = Assert.IsType<GameTcpPacket>(NativeCaptureBoundary.Read(link, frame));
            Assert.Equal(("2001:db8::10", (ushort)16000, (ushort)1714), (packet.SourceAddress, packet.SourcePort, packet.DestinationPort));
            Assert.Equal(new byte[] { 9, 8, 7 }, packet.Payload);
        }
        // 分片包与不受支持的链路类型不解析；Wintun 的原始 IP（12、101、228）与 IPv6（229）受支持。
        var fragment = ip.ToArray(); fragment[6] = 44; fragment[40] = 6; BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(42), 8);
        Assert.Null(NativeCaptureBoundary.Read(101, fragment));
        Assert.All(new[] { 0, 1, 12, 101, 228, 229 }, link => Assert.True(GamePacket.SupportsLink(link)));
        Assert.False(GamePacket.SupportsLink(113));
    }

    private sealed record Segment(string Source, ushort SourcePort, string Destination, ushort DestinationPort, uint Sequence, byte Flags, byte[] Payload);
    private sealed class FlowState(string source, ushort sourcePort, string destination, ushort destinationPort)
    {
        private uint next = 7000;
        public Segment Syn() { var value = new Segment(source, sourcePort, destination, destinationPort, next, 0x12, []); next++; return value; }
        public Segment Data(byte[] payload) { var value = new Segment(source, sourcePort, destination, destinationPort, next, 0x18, payload); next += (uint)payload.Length; return value; }
        public Segment Fin() { var value = new Segment(source, sourcePort, destination, destinationPort, next, 0x11, []); next++; return value; }
    }
    private static FlowState Flow(string source, ushort sourcePort, string destination, ushort destinationPort) => new(source, sourcePort, destination, destinationPort);

    private static byte[] Tcp(Segment segment)
    {
        var tcp = new byte[20 + segment.Payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, segment.SourcePort); BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), segment.DestinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), segment.Sequence); tcp[12] = 0x50; tcp[13] = segment.Flags;
        segment.Payload.CopyTo(tcp, 20);
        return tcp;
    }
    private static byte[] Ip(Segment segment)
    {
        var tcp = Tcp(segment);
        var source = IPAddress.Parse(segment.Source).GetAddressBytes(); var destination = IPAddress.Parse(segment.Destination).GetAddressBytes();
        if (source.Length == 4)
        {
            var ip = new byte[20 + tcp.Length];
            ip[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length); ip[8] = 64; ip[9] = 6;
            source.CopyTo(ip, 12); destination.CopyTo(ip, 16); tcp.CopyTo(ip, 20);
            return ip;
        }
        var ipv6 = new byte[40 + tcp.Length];
        ipv6[0] = 0x60; BinaryPrimitives.WriteUInt16BigEndian(ipv6.AsSpan(4), (ushort)tcp.Length); ipv6[6] = 6; ipv6[7] = 64;
        source.CopyTo(ipv6, 8); destination.CopyTo(ipv6, 24); tcp.CopyTo(ipv6, 40);
        return ipv6;
    }
    private static byte[] Wrap(int link, byte[] ip, bool ipv6) => link switch
    {
        Ethernet => [.. new byte[12], .. ipv6 ? new byte[] { 0x86, 0xdd } : [0x08, 0x00], .. ip],
        Null => [(byte)(ipv6 ? 24 : 2), 0, 0, 0, .. ip],
        _ => ip
    };
    private static void Send(CaptureStreamRouter router, int device, int link, Segment segment, double at)
    {
        var ip = Ip(segment);
        var packet = Assert.IsType<GameTcpPacket>(NativeCaptureBoundary.Read(link, Wrap(link, ip, ip[0] >> 4 == 6)));
        router.Route(device, packet, at, at, At(at));
    }
    private static bool Dispatch(CaptureStreamRouter router, IReadOnlySet<ushort> ports, PrearmBuffer prearm, Segment segment, double at)
    {
        var packet = Assert.IsType<GameTcpPacket>(NativeCaptureBoundary.Read(Ethernet, Wrap(Ethernet, Ip(segment), segment.Source.Contains(':'))));
        return router.Dispatch(0, packet, ports, prearm, at, at, At(at), At(at));
    }
    private static (CaptureStreamRouter Router, List<CombatCaptureReport> Reports, ObservationCaptureContext Context) Router(int devices)
    {
        List<CombatCaptureReport> reports = [];
        var context = new ObservationCaptureContext([Profile], _ => { }, reports.Add);
        context.Configure(new(true, new(new string('a', 32), Profile.Id, true, true, true)));
        ulong generation = 0;
        var router = new CaptureStreamRouter(devices, Tw20261110CombatProtocol.StreamProfile.KnownProtocols, context.Diagnostics.Stream,
            () => new SharedGameStream("fixture-game", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110));
        return (router, reports, context);
    }
    private static DateTimeOffset At(double ms) => new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms);
}
