using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>
/// 方案 A 的新连接预缓冲：缓冲只按四元组有界保存、确认后按序交付；交付保留抓包时刻，
/// 与逐连接句柄的重叠部分按序号跳过，战斗帧与直接接收时一致。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrearmCaptureTests
{
    private static readonly ObservationCombatProfile Profile = SyntheticCombat.Profile;
    private static readonly CaptureConnection Game = new("127.0.0.1", 50001, "127.0.0.1", 1111);
    private static readonly CaptureConnection Other = Game with { LocalPort = 50002 };

    [Fact]
    public void BufferDeliversInOrderOnceAndDropsExpiredForeignOverBudgetAndOnClear()
    {
        var diagnostics = new CaptureDiagnostics();
        var buffer = new PrearmBuffer(diagnostics);
        for (uint i = 0; i < 3; i++) buffer.Add(Game, Packet(i * 10, 10, 1000 + i), 1000 + i);
        buffer.Add(Other, Packet(0, 10, 1001), 1001);
        Assert.Equal(new uint[] { 0, 10, 20 }, buffer.Take(Game, 1500).Select(p => p.Sequence));
        Assert.Empty(buffer.Take(Game, 1500));
        // 确认属于其他进程即丢弃。
        buffer.DiscardForeign(Other);
        Assert.Equal(0, buffer.Packets);
        // 超过保留时长仍未确认：丢弃。
        buffer.Add(Other, Packet(0, 10, 2000), 2000);
        Assert.Empty(buffer.Take(Other, 2000 + PrearmBuffer.WindowMs + 1));
        // 字节预算：丢最旧的未确认包，不阻塞。
        var large = (int)(PrearmBuffer.MaximumBytes / 3) + 1;
        for (uint i = 0; i < 3; i++) buffer.Add(Game, Packet(i * (uint)large, large, 3000), 3000);
        Assert.True(buffer.Bytes <= PrearmBuffer.MaximumBytes);
        Assert.Equal(new[] { (uint)large, 2u * (uint)large }, buffer.Take(Game, 3000).Select(p => p.Sequence));
        // 捕获结束：全部清空。
        buffer.Add(Game, Packet(0, 10, 4000), 4000);
        buffer.Clear();
        Assert.Equal(0, buffer.Packets); Assert.Equal(0, buffer.Bytes); Assert.Empty(buffer.Connections);
        var counters = diagnostics.Snapshot(0, 0).Prearm!;
        Assert.Equal(9UL, counters.Buffered.Value); Assert.Equal(5UL, counters.Delivered.Value);
        Assert.Equal(1UL, counters.Expired.Value); Assert.Equal(1UL, counters.Foreign.Value); Assert.Equal(1UL, counters.Budget.Value);
    }

    [Fact]
    public void BufferedDamageKeepsCaptureTimeAndOverlapWithTheConnectionHandleCountsOnce()
    {
        // 同一组下行段：身份帧与一次伤害，随后一次实时到达的伤害。
        const uint isn = 7000;
        var segments = SyntheticCombat.Identity().Append(SyntheticCombat.Damage(100)).ToList();
        var times = segments.Select((_, i) => 10_000d + i * 20).ToList();
        var live = SyntheticCombat.Damage(250); const double liveAt = 12_000;

        // 直接接收：逐连接句柄自 SYN-ACK 起在收。
        var (direct, directReports, _) = Stream();
        var directFeed = new ConnectionFeed(direct);
        directFeed.Accept(isn, 0x12, default, times[0] - 5, times[0] - 5, At(times[0] - 5));
        var sequence = isn + 1;
        for (var i = 0; i < segments.Count; i++) { directFeed.Accept(sequence, 0x18, segments[i], times[i], times[i], At(times[i])); sequence += (uint)segments[i].Length; }
        directFeed.Accept(sequence, 0x18, live, liveAt, liveAt, At(liveAt));
        direct.Tick(liveAt + 300, At(liveAt + 300));

        // 经预缓冲：连接表 1.5 s 后才确认，期间的包放在缓冲里；确认后按抓包时刻交付，逐连接句柄又收到最后两段的副本。
        var (buffered, bufferedReports, context) = Stream();
        var buffer = new PrearmBuffer(context.Diagnostics);
        buffer.Add(Game, new(isn, 0x12, [], times[0] - 5, At(times[0] - 5)), times[0] - 5);
        sequence = isn + 1;
        var starts = new List<uint>();
        for (var i = 0; i < segments.Count; i++) { starts.Add(sequence); buffer.Add(Game, new(sequence, 0x18, segments[i], times[i], At(times[i])), times[i]); sequence += (uint)segments[i].Length; }
        var confirmedAt = times[^1] + 1500;
        var feed = new ConnectionFeed(buffered);
        foreach (var early in buffer.Take(Game, confirmedAt))
            Assert.True(feed.Accept(early.Sequence, early.Flags, early.Payload, early.CapturedMono, early.CapturedMono, early.CapturedUtc, prearmed: true));
        for (var i = segments.Count - 2; i < segments.Count; i++)
            Assert.False(feed.Accept(starts[i], 0x18, segments[i], confirmedAt, confirmedAt, At(confirmedAt)));
        Assert.True(feed.Accept(sequence, 0x18, live, liveAt, liveAt, At(liveAt)));
        buffered.Tick(liveAt + 300, At(liveAt + 300));

        var expected = directReports.SelectMany(r => r.Frame.Damage).ToList();
        var actual = bufferedReports.SelectMany(r => r.Frame.Damage).ToList();
        Assert.Equal(2, expected.Count);
        Assert.Equal(expected.Select(d => (d.MonotonicMs, d.ObservedAt, d.Amount.Value)), actual.Select(d => (d.MonotonicMs, d.ObservedAt, d.Amount.Value)));
        // 事件时刻等于原始抓包时刻，不是交付时刻。
        Assert.Equal(times[^1], actual[0].MonotonicMs);
        Assert.Equal(2, feed.Duplicates);
        // 流无缺口：只有新连接本身的 SYN 重置。
        Assert.Equal(["connectionSyn"], context.Diagnostics.Stream.Snapshot().Resets.Keys);
        // 宿主对每份报告的校验（含事件与帧的时点关系）都通过。
        foreach (var report in bufferedReports) CaptureCombatValidation.Validate(report, Profile.Source, "fixture-game");
    }

    private static (SharedGameStream Stream, List<CombatCaptureReport> Reports, ObservationCaptureContext Context) Stream()
    {
        List<CombatCaptureReport> reports = [];
        var context = new ObservationCaptureContext([Profile], _ => { }, reports.Add);
        context.Configure(new(true, new(new string('a', 32), Profile.Id, true, true, true)));
        ulong generation = 0;
        return (new SharedGameStream("fixture-game", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110), reports, context);
    }
    private static PrearmedPacket Packet(uint sequence, int length, double at) => new(sequence, 0x18, new byte[length], at, At(at));
    private static DateTimeOffset At(double ms) => new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms);
}
