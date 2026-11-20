using System.Buffers.Binary;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class GameTcpStreamTests
{
    /// <summary>
    /// 重传比对历史只保留最近 64 KiB：交付超过 64 KiB 之后，与最近数据重叠的重传内容相同只计重传，内容不同按重传不一致重置；
    /// 早于历史窗口的重叠部分不再比较。
    /// </summary>
    [Fact]
    public void RetransmissionsAreComparedAgainstTheLatestSixtyFourKibibytes()
    {
        var random = new Random(7);
        var data = new byte[3 * 30_000]; random.NextBytes(data);
        var counters = new GameStreamCounters();
        var stream = new GameTcpStream((_, _) => { }, _ => { }, new GameStreamProfile(new HashSet<int>(), new HashSet<int>()), counters: counters);
        for (var i = 0; i < 3; i++) stream.Feed((uint)(i * 30_000), data.AsSpan(i * 30_000, 30_000), i);
        stream.Feed(89_000, data.AsSpan(89_000, 1000), 3);
        var tail = data.AsSpan(89_000, 1000).ToArray(); tail[999] ^= 0xff;
        // 比历史窗口更早（0–999）的重叠不比较，内容不同也只计重传。
        var early = data.AsSpan(0, 1000).ToArray(); early[0] ^= 0xff;
        stream.Feed(0, early, 4);
        Assert.Empty(counters.Snapshot().Resets);
        stream.Feed(89_000, tail, 5);
        Assert.Equal(["conflictingRetransmission"], counters.Snapshot().Resets.Keys);
        Assert.Equal(3UL, counters.Snapshot().Retransmitted);
    }

    /// <summary>
    /// 捕获重启后从已有连接的中间接入（首段落在帧中间），接收中又漏抓一个段：中途接入从第一个能对齐的帧头起分帧；
    /// 缺口等满 1.5 秒后跳过，漏抓段之后已收到的段全部继续分帧，只丢与漏抓段重叠的帧，不再反复重置。
    /// </summary>
    [Fact]
    public void MidStreamJoinAlignsAtOnceAndAMissedSegmentCostsOnlyTheFramesItCovers()
    {
        var known = Tw20261110CombatProtocol.StreamProfile.KnownProtocols;
        var codes = known.Where(code => code > 0).Order().Take(6).ToArray();
        var random = new Random(20261119);
        var wire = new List<byte>(); var frames = new List<(long Start, long End)>();
        while (wire.Count < 600_000)
        {
            var body = new byte[random.Next(20, 400)]; random.NextBytes(body);
            var header = new byte[9];
            BinaryPrimitives.WriteInt32LittleEndian(header, codes[random.Next(codes.Length)]);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
            frames.Add((wire.Count, wire.Count + 9 + body.Length));
            wire.AddRange(header); wire.AddRange(body);
        }
        var bytes = wire.ToArray();
        const int Mss = 1460; const uint Base = 4_000_000_000;
        var segments = Enumerable.Range(0, bytes.Length / Mss).Select(index => (Offset: (long)index * Mss, Data: bytes.AsSpan(index * Mss, Mss).ToArray())).ToArray();
        // 第 40 段起接入（落在帧中间），第 100 段漏抓；每 50 ms 一段（实采战斗中约 16–24 段每秒），捕获循环每 10 ms 检查一次缺口。
        const int Join = 40, Missed = 100, Spacing = 50;
        var counters = new GameStreamCounters();
        var emitted = new List<double>(); var now = 0d;
        var stream = new GameTcpStream((_, _) => emitted.Add(now), _ => { }, new GameStreamProfile(known, known), counters: counters);
        int? firstFrameSegment = null;
        for (var index = Join; index < segments.Length; index++)
        {
            for (var tick = (index - Join) * Spacing - Spacing + 10; tick <= (index - Join) * Spacing; tick += 10) { now = Math.Max(0, tick); stream.CheckGap(now); }
            if (index == Missed) continue;
            stream.Feed(unchecked(Base + (uint)segments[index].Offset), segments[index].Data, now, now);
            firstFrameSegment ??= emitted.Count > 0 ? index - Join : null;
        }
        for (var tick = 1; tick <= 200; tick++) { now += 10; stream.CheckGap(now); }

        Assert.True(firstFrameSegment <= 1);
        var resets = counters.Snapshot().Resets;
        Assert.Equal(1UL, resets.GetValueOrDefault("gapTimeout")); Assert.Single(resets);
        long joined = segments[Join].Offset, holeStart = segments[Missed].Offset, holeEnd = holeStart + Mss;
        var complete = frames.Count(frame => frame.Start >= joined && frame.End <= segments[^1].Offset + Mss &&
            (frame.End <= holeStart || frame.Start >= holeEnd));
        // 缺口两侧各至多再丢一帧：缺口后的第一帧需与下一帧头连成两个有效帧头才确认对齐。
        Assert.InRange(emitted.Count, complete - 2, complete);
        var missedAt = (Missed - Join) * (double)Spacing;
        var resumed = emitted.First(at => at > missedAt);
        Assert.InRange(resumed, missedAt, missedAt + Spacing + 1500 + 10);
    }
}
