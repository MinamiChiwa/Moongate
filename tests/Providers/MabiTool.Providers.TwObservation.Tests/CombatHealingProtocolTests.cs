using System.Buffers.Binary;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>当前构建治疗三类帧：脱敏样本回放、自疗合并、HP 配对与过量、长度异常及 v1 兼容。</summary>
public sealed class CombatHealingProtocolTests
{
    private const ulong Base = 1_000_000;

    [Fact]
    public void CurrentProfileAddsHealingOpcodesWithoutChangingV1()
    {
        Assert.Equal("tw-combat-20261110-v3", Tw20261110CombatProtocol.CaptureProfile);
        Assert.True(Tw20261110CombatProtocol.CombatProfile.Healing);
        Assert.False(Tw20261110CombatProtocol.CombatProfileV1.Healing);
        Assert.Equal(10, Tw20261110CombatProtocol.ProtocolsV1.Count);
        Assert.Equal(13, Tw20261110CombatProtocol.ProtocolsV2.Count);
        Assert.True(Tw20261110CombatProtocol.ProtocolsV1.IsSubsetOf(Tw20261110CombatProtocol.ProtocolsV2));
        foreach (var code in new[] { 20644, 20645, 170205 })
        {
            Assert.Contains(code, Tw20261110CombatProtocol.StreamProfile.ObservedProtocols);
            Assert.Contains(code, Tw20261110CombatProtocol.StreamProfile.KnownProtocols);
            Assert.DoesNotContain(code, Tw20261110CombatProtocol.ProtocolsV1); Assert.Contains(code, Tw20261110CombatProtocol.ProtocolsV2);
        }
    }

    public static TheoryData<string> Samples => new() { "confirmed-02", "confirmed-04" };

    [Theory]
    [MemberData(nameof(Samples))]
    public void SanitizedSampleReplayMatchesRecordedHealingCounts(string sampleId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "contracts/fixtures/combat-tw-healing.v1.json")));
        var sample = document.RootElement.GetProperty("samples").EnumerateArray().Single(x => x.GetProperty("id").GetString() == sampleId);
        var expected = sample.GetProperty("expected");
        var replay = new Replay();
        foreach (var frame in sample.GetProperty("frames").EnumerateArray())
        {
            var offset = Base + frame.GetProperty("streamOffset").GetUInt64();
            var mono = 1000 + frame.GetProperty("monotonicMs").GetDouble();
            var wire = frame.GetProperty("wireLength").GetInt32();
            var (opcode, body) = frame.GetProperty("kind").GetString() switch
            {
                "received" => (Tw20261110CombatProtocol.HealReceived, Received(frame.GetProperty("flags").GetInt64(), frame.GetProperty("amount").GetInt64())),
                "cast" => (Tw20261110CombatProtocol.HealCasted, Cast(replay.Local, frame.GetProperty("flags").GetInt64(), frame.GetProperty("amount").GetInt64())),
                "health" => (Tw20261110CombatProtocol.SetHealth, Health(replay.Local, frame.GetProperty("hp").GetInt64())),
                var other => throw new InvalidDataException(other)
            };
            Assert.Equal(wire, body.Length + 9);
            replay.Accept(opcode, body, offset, mono);
        }
        replay.Observer.Finish(200_000, CombatProtocolTests.At(200_000));
        Assert.Empty(replay.Frames.SelectMany(x => x.Gaps));
        var heals = replay.Heals;
        var received = heals.Where(x => x.Direction.KnownValue == CombatHealDirection.Received).ToList();
        var casts = heals.Where(x => x.Direction.KnownValue == CombatHealDirection.Cast).ToList();
        var merged = casts.Where(x => x.ReceiptMerged).ToList();
        Assert.Equal(expected.GetProperty("cast").GetInt32(), casts.Count);
        Assert.Equal(expected.GetProperty("castAmount").GetUInt64(), Sum(casts));
        Assert.All(casts, cast => Assert.Equal(replay.Local, cast.TargetEntity!.RawInstanceId.Value));
        Assert.Equal(expected.GetProperty("receivedSelfCount").GetInt32(), merged.Count);
        Assert.Equal(expected.GetProperty("received").GetInt32(), received.Count + merged.Count);
        Assert.Equal(expected.GetProperty("receivedAmount").GetUInt64(), Sum(received) + Sum(merged));
        var pairs = heals.Where(x => x.EffectiveAmount is not null).Select(x => (x.Amount.Value, x.EffectiveAmount!.Value.Value, x.Overheal!.Value.Value)).ToList();
        var expectedPairs = expected.GetProperty("pairs").EnumerateArray().Select(x =>
            (x.GetProperty("amount").GetUInt64(), x.GetProperty("effective").GetUInt64(), x.GetProperty("overheal").GetUInt64())).ToList();
        Assert.Equal(expectedPairs, pairs);
        Assert.All(heals.Where(x => x.EffectiveAmount is null && (x.Direction.KnownValue == CombatHealDirection.Received || x.ReceiptMerged)),
            x => Assert.Equal("hp-unpaired", x.EffectiveReason));

        // 同一回放进入领域：手动分段从首帧开始，受到总量、自疗与施放全部计入，配对次数与有效量按配对情况返回。
        var session = new CombatSession("owner", "observation", replay.Source, replay.Observer.LocalEntity!, new("fixture-game"),
            CombatProtocolTests.At(0), healing: true);
        session.StartManual(CombatProtocolTests.At(0), 0);
        foreach (var frame in replay.Frames) session.Apply(frame);
        var healing = session.Capture().Workspace.Encounter!.Healing!;
        Assert.Equal(expected.GetProperty("receivedAmount").GetUInt64().ToString(), healing.ReceivedAmount.ToString());
        Assert.Equal(expected.GetProperty("received").GetUInt64(), healing.ReceivedCount.Value);
        Assert.Equal(expected.GetProperty("receivedSelfAmount").GetUInt64().ToString(), healing.ReceivedSelfAmount.ToString());
        Assert.Equal(expected.GetProperty("castAmount").GetUInt64().ToString(), healing.CastSelfAmount.ToString());
        Assert.Equal("0", healing.CastOtherAmount.ToString());
        Assert.Equal(expected.GetProperty("pairedReceived").GetUInt64(), healing.PairedReceivedCount.Value);
        Assert.Null(healing.ReceivedEffective); Assert.Null(healing.ReceivedOverheal);
        Assert.Equal("hp-unpaired", healing.EffectiveReason);
    }

    [Fact]
    public void SelfHealIsMergedOnceInEitherArrivalOrder()
    {
        var replay = new Replay();
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2800, 320), Base, 100);
        replay.Accept(Tw20261110CombatProtocol.HealCasted, Cast(replay.Local, 0x2800, 320), Base + 25, 100);
        replay.Accept(Tw20261110CombatProtocol.HealCasted, Cast(replay.Local, 0x2000, 2661), Base + 1000, 200);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 2661), Base + 1033, 200);
        // 不同数值的受疗不与施放合并。
        replay.Accept(Tw20261110CombatProtocol.HealCasted, Cast(replay.Local, 0x2000, 500), Base + 2000, 300);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 499), Base + 2033, 300);
        replay.Observer.Finish(1000, CombatProtocolTests.At(1000));
        var heals = replay.Heals;
        Assert.Equal(4, heals.Count);
        Assert.Equal([320UL, 2661UL], heals.Where(x => x.ReceiptMerged).Select(x => x.Amount.Value));
        Assert.Equal(500UL, heals.Single(x => x.Direction.KnownValue == CombatHealDirection.Cast && !x.ReceiptMerged).Amount.Value);
        Assert.Equal(499UL, heals.Single(x => x.Direction.KnownValue == CombatHealDirection.Received).Amount.Value);
    }

    [Fact]
    public void HealthRiseNextToTheReceiptGivesEffectiveAmountAndCappedOverheal()
    {
        var replay = new Replay();
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 23210), Base, 100);
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 23713), Base + 100, 200);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 503), Base + 130, 200);
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 23781), Base + 500, 300);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 1232), Base + 530, 300);
        // 满血：没有 HP 更新。
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 419), Base + 1500, 500);
        // HP 上升超过治疗量：数值不一致。
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 22000), Base + 2000, 600);
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 23000), Base + 2100, 600);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 100), Base + 2130, 600);
        replay.Observer.Finish(2000, CombatProtocolTests.At(2000));
        var heals = replay.Heals.ToDictionary(x => x.Amount.Value);
        Assert.Equal((503UL, 0UL), (heals[503].EffectiveAmount!.Value.Value, heals[503].Overheal!.Value.Value));
        Assert.Equal((68UL, 1164UL), (heals[1232].EffectiveAmount!.Value.Value, heals[1232].Overheal!.Value.Value));
        Assert.Null(heals[419].EffectiveAmount); Assert.Equal("hp-unpaired", heals[419].EffectiveReason);
        Assert.Null(heals[100].EffectiveAmount); Assert.Equal("hp-mismatch", heals[100].EffectiveReason);
        Assert.Empty(replay.Frames.SelectMany(x => x.Gaps));
    }

    [Fact]
    public void FollowingHealthUpdatePairsWithTheReceiptBeforeIt()
    {
        var replay = new Replay();
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 22921), Base, 100);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 524), Base + 100, 200);
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 23445), Base + 125, 200);
        replay.Observer.Finish(1000, CombatProtocolTests.At(1000));
        var heal = Assert.Single(replay.Heals);
        Assert.Equal((524UL, 0UL), (heal.EffectiveAmount!.Value.Value, heal.Overheal!.Value.Value));
    }

    [Theory]
    [InlineData(20645, 15)]
    [InlineData(20645, 17)]
    [InlineData(20644, 23)]
    [InlineData(20644, 25)]
    [InlineData(170205, 20)]
    [InlineData(170205, 22)]
    public void WrongFixedLengthsAreFieldErrors(int opcode, int length)
    {
        var replay = new Replay();
        var body = new byte[length];
        BinaryPrimitives.WriteUInt64LittleEndian(body, replay.Local);
        if (opcode == 170205 && length >= 12) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 8);
        if (opcode != 170205) body[^2] = 1;
        replay.Accept(opcode, body, Base, 100);
        replay.Observer.Finish(1000, CombatProtocolTests.At(1000));
        Assert.Contains(replay.Frames.SelectMany(x => x.Gaps), gap => gap.Reason == "combat.protocolInvalid");
        Assert.Empty(replay.Heals);
        Assert.Equal(1UL, replay.Observer.ProtocolRejections);
    }

    [Fact]
    public void NegativeAmountsAndZeroTargetsAreFieldErrorsWhileZeroAmountIsIgnored()
    {
        var replay = new Replay();
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, -5), Base, 100);
        replay.Accept(Tw20261110CombatProtocol.HealCasted, Cast(0, 0x2000, 5), Base + 100, 100);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 0), Base + 200, 100);
        replay.Observer.Finish(1000, CombatProtocolTests.At(1000));
        Assert.Equal(2UL, replay.Observer.ProtocolRejections);
        Assert.Empty(replay.Heals);
    }

    [Fact]
    public void EncryptedLengthHealthIsUnknownAndOtherEntitiesAreIgnored()
    {
        var replay = new Replay();
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 1000), Base, 100);
        var encrypted = new byte[12 + 16 + 1];
        BinaryPrimitives.WriteUInt64LittleEndian(encrypted, replay.Local); BinaryPrimitives.WriteUInt32LittleEndian(encrypted.AsSpan(8), 16);
        replay.Accept(Tw20261110CombatProtocol.SetHealth, encrypted, Base + 100, 200);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 300), Base + 100 + 38, 200);
        // 之前 HP 未知：下一次明文更新也无法给出上升量。
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 1300), Base + 500, 300);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 300), Base + 530, 300);
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(424242, 5), Base + 900, 400);
        replay.Observer.Finish(1000, CombatProtocolTests.At(1000));
        Assert.Empty(replay.Frames.SelectMany(x => x.Gaps));
        Assert.All(replay.Heals, heal => { Assert.Null(heal.EffectiveAmount); Assert.Equal("hp-unpaired", heal.EffectiveReason); });
        Assert.Equal(2, replay.Heals.Count);
    }

    [Fact]
    public void V1InterestKeepsHealingFramesOutOfTheObservation()
    {
        var replay = new Replay(healing: false);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 300), Base, 100);
        replay.Accept(Tw20261110CombatProtocol.HealCasted, Cast(replay.Local, 0x2000, 300), Base + 25, 100);
        replay.Observer.Finish(1000, CombatProtocolTests.At(1000));
        Assert.All(replay.Frames, frame => Assert.Null(frame.Healing));
        Assert.Equal(0UL, replay.Observer.ProtocolRejections);
    }

    [Fact]
    public void IdentityResetClearsKnownHealth()
    {
        var replay = new Replay();
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 1000), Base, 100);
        replay.Observer.Reset("combat.streamGap.connectionClosed", 150, CombatProtocolTests.At(150));
        replay.Start(200);
        replay.Accept(Tw20261110CombatProtocol.SetHealth, Health(replay.Local, 1300), Base + 500, 300);
        replay.Accept(Tw20261110CombatProtocol.HealReceived, Received(0x2000, 300), Base + 530, 300);
        replay.Observer.Finish(1000, CombatProtocolTests.At(1000));
        Assert.Equal("hp-unpaired", Assert.Single(replay.Heals).EffectiveReason);
    }

    private static ulong Sum(IEnumerable<CombatHealEvent> values) => values.Aggregate(0UL, (sum, x) => sum + x.Amount.Value);
    internal static byte[] Received(long flags, long amount)
    {
        var body = new byte[16]; BinaryPrimitives.WriteInt64LittleEndian(body, flags); BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), amount);
        return body;
    }
    internal static byte[] Cast(ulong target, long flags, long amount)
    {
        var body = new byte[24]; BinaryPrimitives.WriteUInt64LittleEndian(body, target);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), flags); BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), amount);
        return body;
    }
    internal static byte[] Health(ulong entity, long hp)
    {
        var body = new byte[21]; BinaryPrimitives.WriteUInt64LittleEndian(body, entity);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 8); BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(12), hp);
        return body;
    }
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("无法定位仓库");
    }

    /// <summary>以当前构建身份建立本机实体后，治疗帧按给定流偏移直接交给观察器。</summary>
    private sealed class Replay
    {
        public List<CombatFrame> Frames { get; } = [];
        public List<CombatHealEvent> Heals => Frames.SelectMany(x => x.Healing ?? []).ToList();
        public ObservationSource Source => Tw20261110CombatProtocol.CombatProfile.Source;
        public TwCombatObserver Observer { get; }
        private readonly GameTcpStream stream;
        private uint sequence;
        private double now;
        public ulong Local => Observer.LocalEntity!.Key.RawInstanceId.Value;
        public Replay(bool healing = true)
        {
            Observer = new("fixture-game", Source, Frames.Add);
            Observer.Configure(new(true, true, true, healing));
            stream = new((code, body) => Observer.Accept(code, body, stream!.FrameStreamOffset, stream.FrameWireLength,
                now, CombatProtocolTests.At(now), stream.FrameEncoding), reason => Observer.Reset(reason, now, CombatProtocolTests.At(now)),
                Tw20261110CombatProtocol.StreamProfile);
            Start(0);
        }
        public void Start(double at)
        {
            now = at; var bytes = CombatProtocolTests.Identity();
            stream.Feed(sequence, bytes, at, at); sequence += (uint)bytes.Length;
        }
        public void Accept(int opcode, byte[] body, ulong offset, double mono) =>
            Observer.Accept(opcode, body, offset, body.Length + 9, mono, CombatProtocolTests.At(mono));
    }
}
