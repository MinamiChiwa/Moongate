using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>共享流按 v2/v1 兴趣输出或省略治疗条目，宿主校验受理合法条目、拒绝不一致条目，写出队列合并时保留治疗。</summary>
[SupportedOSPlatform("windows")]
public sealed class CurrentCombatHealingStreamTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly MethodInfo Validate = typeof(MabiTool.Host.Game.GameObservationService).Assembly
        .GetType("MabiTool.Host.Game.CaptureCombatValidation")!.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void V2InterestPublishesMergedSelfHealAndPairedReceiptThatTheHostAccepts()
    {
        var (reports, profile, stream, local) = Open(Tw20261110CombatProtocol.Profile);
        using (stream)
        {
            var bytes = Join(Health(local, 23713), Health(local, 23781), Received(0x2000, 1232),
                Received(0x2800, 320), Cast(local, 0x2800, 320));
            stream.Feed(bytes, 100);
            stream.Tick(400, At.AddMilliseconds(400));
            var heals = reports.SelectMany(x => x.Frame.Healing ?? []).ToList();
            Assert.Equal(2, heals.Count);
            var merged = Assert.Single(heals, x => x.ReceiptMerged);
            Assert.Equal((CombatHealDirection.Cast, 320UL, local), (merged.Direction.KnownValue!.Value, merged.Amount.Value, merged.TargetEntity!.RawInstanceId.Value));
            var paired = Assert.Single(heals, x => x.Amount.Value == 1232);
            // 紧前的 HP 由 23,713 升至上限 23,781：有效 68、过量 1,164；自疗没有紧邻的 HP 更新，有效量未知。
            Assert.Equal((68UL, 1164UL), (paired.EffectiveAmount!.Value.Value, paired.Overheal!.Value.Value));
            Assert.Equal("hp-unpaired", merged.EffectiveReason);
            foreach (var report in reports)
            {
                Validate.Invoke(null, [report, profile.Source, "fixture-current"]);
                var json = JsonSerializer.Serialize(report, ContractJson.Options);
                Assert.Equal(report.Frame.Healing?.Count, JsonSerializer.Deserialize<CombatCaptureReport>(json,
                    MabiTool.Host.Game.CaptureCombatValidation.Json)!.Frame.Healing?.Count);
            }
        }
    }

    [Fact]
    public void V1InterestFromAnOlderHostLeavesHealingOutOfTheWireFormat()
    {
        var (reports, _, stream, local) = Open(Tw20261110CombatProtocol.ProfileV1);
        using (stream)
        {
            stream.Feed(Join(Received(0x2000, 300), Cast(local, 0x2000, 300)), 100);
            stream.Tick(400, At.AddMilliseconds(400));
            Assert.DoesNotContain("healing", JsonSerializer.Serialize(reports, ContractJson.Options));
            Assert.Empty(reports.SelectMany(x => x.Frame.Gaps));
        }
    }

    [Fact]
    public void HostRejectsInconsistentHealEntries()
    {
        var (reports, profile, stream, local) = Open(Tw20261110CombatProtocol.Profile);
        using (stream)
        {
            stream.Feed(Received(0x2000, 300), 100);
            stream.Tick(400, At.AddMilliseconds(400));
            var report = Assert.Single(reports, x => x.Frame.Healing is not null);
            var heal = report.Frame.Healing![0];
            foreach (var invalid in new[]
            {
                heal with { EffectiveAmount = new(301), Overheal = new(0), EffectiveReason = null },
                heal with { EffectiveAmount = new(100), Overheal = null, EffectiveReason = null },
                heal with { Direction = CombatHealDirection.Cast },
                heal with { ReceiptMerged = true },
                heal with { Amount = new(0) }
            })
            {
                var error = Assert.Throws<TargetInvocationException>(() => Validate.Invoke(null,
                    [report with { Frame = report.Frame with { Healing = [invalid] } }, profile.Source, "fixture-current"]));
                Assert.Equal("worker.frameInvalid", Assert.IsType<WorkerException>(error.InnerException).Code);
            }
            _ = local;
        }
    }

    [Fact]
    public void WriteQueueMergesHealingWithinOneBatch()
    {
        var (reports, _, stream, _) = Open(Tw20261110CombatProtocol.Profile);
        using (stream)
        {
            stream.Feed(Received(0x2000, 300), 100);
            stream.Tick(400, At.AddMilliseconds(400));
            var first = Assert.Single(reports, x => x.Frame.Healing is not null);
            var heal = first.Frame.Healing![0];
            // 同一代次、100 ms 内的下一帧只带一条本机施放。
            var second = first with { Frame = first.Frame with { Sequence = new(first.Frame.Sequence.Value + 1),
                MonotonicMs = first.Frame.MonotonicMs + 50, Entities = [], Gaps = [],
                Healing = [heal with { EventId = "heal-next", Direction = CombatHealDirection.Cast, Amount = new(77),
                    TargetEntity = first.LocalEntity!.Key, EffectiveReason = null, MonotonicMs = first.Frame.MonotonicMs + 50 }] } };
            var queue = new CombatCaptureQueue();
            foreach (var report in new[] { first, second }) Assert.True(queue.TryWrite(report));
            var batch = Assert.Single(queue.Drain());
            Assert.Equal([300UL, 77UL], batch.Frame.Healing!.Select(x => x.Amount.Value));
        }
    }

    /// <summary>按序续接 TCP 序号的共享流。</summary>
    private sealed class Feeder(SharedGameStream stream, uint sequence) : IDisposable
    {
        private uint sequence = sequence;
        public void Feed(byte[] bytes, double at)
        { stream.Feed(sequence, bytes, at, at, At.AddMilliseconds(at)); sequence += (uint)bytes.Length; }
        public void Tick(double at, DateTimeOffset utc) => stream.Tick(at, utc);
        public void Dispose() => stream.Dispose();
    }

    private static (List<CombatCaptureReport> Reports, ObservationCombatProfile Profile, Feeder Stream, ulong Local) Open(string profileId)
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(profileId, new("tw-combat-test", "1", "TW", "synthetic-current-layout"),
            Simulation: true, Healing: profileId == Tw20261110CombatProtocol.Profile);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0;
        var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var initial = Join(Frame(20590, Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!), true),
            Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true));
        stream.Feed(0, initial, 0, 0, At);
        Assert.NotNull(stream.LocalEntity);
        reports.Clear();
        return (reports, profile, new(stream, (uint)initial.Length), stream.LocalEntity!.Key.RawInstanceId.Value);
    }

    private static byte[] Received(long flags, long amount)
    {
        var body = new byte[16]; BinaryPrimitives.WriteInt64LittleEndian(body, flags); BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), amount);
        return Frame(Tw20261110CombatProtocol.HealReceived, body);
    }
    private static byte[] Cast(ulong target, long flags, long amount)
    {
        var body = new byte[24]; BinaryPrimitives.WriteUInt64LittleEndian(body, target);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), flags); BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), amount);
        return Frame(Tw20261110CombatProtocol.HealCasted, body);
    }
    private static byte[] Health(ulong entity, long hp)
    {
        var body = new byte[21]; BinaryPrimitives.WriteUInt64LittleEndian(body, entity);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 8); BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(12), hp);
        return Frame(Tw20261110CombatProtocol.SetHealth, body);
    }
    private static byte[] Join(params byte[][] frames) => frames.SelectMany(x => x).ToArray();
    private static byte[] Frame(int opcode, byte[] body, bool compressed = false)
    {
        if (compressed)
        {
            using var memory = new MemoryStream();
            using (var encoder = new BrotliStream(memory, CompressionLevel.Optimal, true)) encoder.Write(body);
            body = memory.ToArray();
        }
        var bytes = new byte[9 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(bytes, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), body.Length); bytes[8] = compressed ? (byte)1 : (byte)0;
        body.CopyTo(bytes, 9); return bytes;
    }
}
