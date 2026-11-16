using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>观察 Worker 捕获诊断计数：共享流的重组、分帧、拒绝、连接切换与上限，以及 Host 的报文校验。</summary>
[SupportedOSPlatform("windows")]
public sealed class CaptureDiagnosticsTests
{
    private static readonly ObservationCombatProfile Profile = new(Tw20261110CombatProtocol.Profile,
        new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
    private static readonly DateTimeOffset At = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SharedStreamCountsSegmentsReorderingRetransmissionAndFrameTypes()
    {
        var context = Context();
        ulong generation = 0;
        var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        var initial = Initial();
        var split = initial.Length / 2;
        // 首段按序到达后，后半段先到（乱序），中段补齐后两帧完成分帧；重复的中段计为重传且不重复分帧。
        stream.Feed(0, initial.AsMemory(0, 9), 0, 0, At);
        stream.Feed((uint)split, initial.AsMemory(split), 1, 1, At);
        stream.Feed(9, initial.AsMemory(9, split - 9), 2, 2, At);
        stream.Feed(9, initial.AsMemory(9, split - 9), 3, 3, At);
        Assert.NotNull(stream.LocalEntity);
        var counters = context.Diagnostics.Snapshot(0, 0);
        Assert.Equal((1UL, 1UL, 0UL), (counters.Connections.Current.Value, counters.Connections.Opened.Value, counters.Connections.Closed.Value));
        Assert.Equal(4UL, counters.Reassembly.Segments.Value);
        Assert.Equal((ulong)(initial.Length + split - 9), counters.Reassembly.Bytes.Value);
        Assert.Equal(1UL, counters.Reassembly.OutOfOrder.Value); Assert.Equal(1UL, counters.Reassembly.Retransmitted.Value);
        Assert.Empty(counters.Reassembly.Resets);
        Assert.Equal(2UL, counters.Frames.Total.Value); Assert.Equal(2UL, counters.Frames.Dispatched.Value);
        Assert.Equal(new[] { "20054", "20590" }, counters.Frames.Types.Keys.Order(StringComparer.Ordinal));
        Assert.All(counters.Frames.Types.Values, value => Assert.Equal(1UL, value.Value));
        Assert.Equal(0UL, counters.Frames.OtherFrames.Value); Assert.Empty(counters.Frames.Rejected);
        // 包计数只来自原生抓包；合成流不经过 Npcap。
        Assert.Equal(0UL, counters.Packets.Received.Value);
        stream.Dispose(); stream.Dispose();
        counters = context.Diagnostics.Snapshot(0, 0);
        Assert.Equal((0UL, 1UL, 1UL), (counters.Connections.Current.Value, counters.Connections.Opened.Value, counters.Connections.Closed.Value));
        AssertSchemaShape(counters);
    }

    [Fact]
    public void ConnectionSwitchKeepsRunTotalsAcrossStreamsAndCountsResetCauses()
    {
        var context = Context();
        ulong generation = 0;
        var first = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        var initial = Initial();
        first.Feed(0, initial, 0, 0, At);
        first.Restart("连接更新", 5, At, GameStreamResetCause.ConnectionSyn);
        first.Restart("连接移除", 6, At, GameStreamResetCause.ConnectionRemoved); first.Dispose();
        using var second = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        second.Feed(1000, initial, 10, 10, At);
        // 缺口：后续段跳过序号，超过 1.5 s 未补齐触发 gapTimeout 重置。
        second.Feed((uint)(1000 + initial.Length + 50), new byte[9], 20, 20, At);
        second.Tick(1600, At);
        var counters = context.Diagnostics.Snapshot(0, 0);
        Assert.Equal((1UL, 2UL, 1UL), (counters.Connections.Current.Value, counters.Connections.Opened.Value, counters.Connections.Closed.Value));
        Assert.Equal(4UL, counters.Frames.Total.Value);
        Assert.Equal(2UL, counters.Frames.Types["20590"].Value);
        Assert.Equal(1UL, counters.Reassembly.Resets["connectionSyn"].Value);
        Assert.Equal(1UL, counters.Reassembly.Resets["connectionRemoved"].Value);
        Assert.Equal(1UL, counters.Reassembly.Resets["gapTimeout"].Value);
        Assert.All(counters.Reassembly.Resets.Keys, key => Assert.Contains(key, ObservationCaptureCounters.ResetCauses));
        AssertSchemaShape(counters);
    }

    [Fact]
    public void DecompressAndParseFailuresAreCountedByFixedBranch()
    {
        var context = Context();
        ulong generation = 0;
        using var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        var initial = Initial();
        // 压缩标记的伤害帧正文不是有效 Brotli：展开失败；长度错误的伤害帧：战斗观察器拒绝。
        var broken = Frame(21196, [1, 2, 3, 4, 5, 6, 7, 8], compressed: false); broken[8] = 1;
        var bytes = initial.Concat(broken).Concat(Frame(21196, new byte[10])).Concat(Frame(20054, Body("characterBodyBase64"), true)).ToArray();
        stream.Feed(0, bytes, 0, 0, At);
        var counters = context.Diagnostics.Snapshot(0, 0);
        Assert.Equal(1UL, counters.Frames.DecompressFailures.Value);
        Assert.Equal(1UL, Assert.Single(counters.Frames.Rejected, pair => pair.Key == "combat").Value.Value);
        Assert.Equal(5UL, counters.Frames.Total.Value); Assert.Equal(2UL, counters.Frames.Types["21196"].Value);
        AssertSchemaShape(counters);
    }

    [Fact]
    public void FrameTypeDistributionIsCappedAndOverflowIsCounted()
    {
        var counters = new GameStreamCounters();
        var known = Tw20261110CombatProtocol.StreamProfile.KnownProtocols.Order().Take(GameStreamCounters.MaximumFrameTypes + 3).ToArray();
        Assert.True(known.Length > GameStreamCounters.MaximumFrameTypes);
        var tcp = new GameTcpStream((_, _) => { }, _ => { }, new GameStreamProfile(known.ToHashSet(), new HashSet<int>()), counters: counters);
        var bytes = known.SelectMany(code => Frame(code, [])).Concat(known.Take(2).SelectMany(code => Frame(code, []))).ToArray();
        tcp.Feed(0, bytes, 0);
        var snapshot = counters.Snapshot();
        Assert.Equal(GameStreamCounters.MaximumFrameTypes, snapshot.Types.Count);
        Assert.Equal((ulong)(known.Length + 2), snapshot.Frames);
        Assert.Equal((ulong)(known.Length - GameStreamCounters.MaximumFrameTypes), snapshot.OtherFrames);
        Assert.Equal(0UL, snapshot.Dispatched);
        Assert.Equal(2UL, snapshot.Types[known[0]]);
    }

    [Fact]
    public void NativePacketAndQueueCountersAccumulateWithoutConnectionIdentity()
    {
        var diagnostics = new CaptureDiagnostics();
        diagnostics.Packet(1500); diagnostics.Packet(60); diagnostics.Filtered(); diagnostics.Truncated(); diagnostics.ReadLimitHit();
        diagnostics.DriverDrops(0, 3); diagnostics.DriverDrops(uint.MaxValue - 1, 2);
        diagnostics.ObserveQueues(5, 1); diagnostics.ObserveQueues(2, 3); diagnostics.LaneRejected();
        diagnostics.Rejected("inventory"); diagnostics.Rejected("music");
        Assert.Throws<ArgumentOutOfRangeException>(() => diagnostics.Rejected("free text"));
        var counters = diagnostics.Snapshot(1, 0);
        Assert.Equal((2UL, 1560UL, 1UL, 1UL, 7UL, 1UL), (counters.Packets.Received.Value, counters.Packets.Bytes.Value,
            counters.Packets.Filtered.Value, counters.Packets.Truncated.Value, counters.Packets.DriverDropped.Value, counters.Packets.ReadLimitHits.Value));
        Assert.Equal((1UL, 5UL, 0UL, 3UL, 1UL), (counters.Queues.CombatDepth.Value, counters.Queues.CombatPeak.Value,
            counters.Queues.InventoryDepth.Value, counters.Queues.InventoryPeak.Value, counters.Queues.InventoryLaneRejected.Value));
        Assert.Equal(new[] { "inventory", "music" }, counters.Frames.Rejected.Keys.Order(StringComparer.Ordinal));
        AssertSchemaShape(counters);
    }

    [Fact]
    public void SharedFixtureMatchesContractAndWorkerEndsAreClassified()
    {
        var fixture = JsonSerializer.Deserialize<ObservationCaptureCounters>(
            File.ReadAllText(Path.Combine(GameObservationApiTests.Area.FindRoot(), "contracts", "fixtures", "observation-capture-diagnostics.v1.json")), ContractJson.Options)!;
        Assert.Equal(2UL, fixture.Connections.Opened.Value); Assert.Equal(320UL, fixture.Frames.Types["21196"].Value);
        Assert.True(GameObservationService.ValidCounters(fixture));
        var wide = Enumerable.Range(20000, ObservationCaptureCounters.MaximumFrameTypes + 1).ToDictionary(code => code.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => new UInt64String(1));
        foreach (var invalid in new[]
        {
            fixture with { Frames = fixture.Frames with { Types = wide } },
            fixture with { Frames = fixture.Frames with { Types = new Dictionary<string, UInt64String> { ["020590"] = new(1) } } },
            fixture with { Frames = fixture.Frames with { Rejected = new Dictionary<string, UInt64String> { ["parser"] = new(1) } } },
            fixture with { Reassembly = fixture.Reassembly with { Resets = new Dictionary<string, UInt64String> { ["freeText"] = new(1) } } },
            fixture with { Connections = fixture.Connections with { Current = new(3) } },
            fixture with { SchemaVersion = 2 }
        }) Assert.False(GameObservationService.ValidCounters(invalid));
        Assert.Equal("normal", GameObservationService.WorkerEndKind(null));
        Assert.Equal("heartbeatTimeout", GameObservationService.WorkerEndKind("worker.heartbeatTimeout"));
        Assert.Equal("protocolError", GameObservationService.WorkerEndKind("worker.frameInvalid"));
        Assert.Equal("protocolError", GameObservationService.WorkerEndKind("worker.outputRateExceeded"));
        Assert.Equal("disconnected", GameObservationService.WorkerEndKind("worker.disconnected"));
        Assert.Equal("other", GameObservationService.WorkerEndKind("observation.captureFailed"));
    }

    private static void AssertSchemaShape(ObservationCaptureCounters counters)
    {
        var text = JsonSerializer.Serialize(counters, ContractJson.Options);
        Assert.Equal(counters.Frames.Types.Count, JsonSerializer.Deserialize<ObservationCaptureCounters>(text, ContractJson.Options)!.Frames.Types.Count);
        Assert.DoesNotContain("127.0.0.1", text); Assert.DoesNotContain("1111", text);
    }
    private static ObservationCaptureContext Context()
    {
        var context = new ObservationCaptureContext([Profile], _ => { }, _ => { });
        context.Configure(new(false, new(new string('a', 32), Profile.Id, true, true, true)));
        return context;
    }
    private static byte[] Initial() => Frame(20590, Body("personalBodyBase64"), true).Concat(Frame(20054, Body("characterBodyBase64"), true)).ToArray();
    private static byte[] Body(string property)
    {
        using var fixture = JsonDocument.Parse(typeof(CaptureDiagnosticsTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        return Convert.FromBase64String(fixture.RootElement.GetProperty(property).GetString()!);
    }
    private static byte[] Frame(int code, byte[] body, bool compressed = false)
    {
        if (compressed)
        {
            using var memory = new MemoryStream(); using (var encoder = new BrotliStream(memory, CompressionLevel.Fastest, true)) encoder.Write(body);
            body = memory.ToArray();
        }
        var bytes = new byte[9 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(bytes, code);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), body.Length); bytes[8] = compressed ? (byte)1 : (byte)0; body.CopyTo(bytes, 9); return bytes;
    }
}
