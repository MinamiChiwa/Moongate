using System.Buffers.Binary;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class CombatDetailProtocolTests
{
    [Theory]
    [InlineData(0UL, CombatDamageClassification.Direct)]
    [InlineData(0x18800UL, CombatDamageClassification.Indirect)]
    [InlineData(0x1000000000UL, CombatDamageClassification.DamageOverTime)]
    [InlineData(0x1000018800UL, CombatDamageClassification.DamageOverTime)]
    [InlineData(0x8800UL, CombatDamageClassification.Direct)]
    [InlineData(0x10800UL, CombatDamageClassification.Direct)]
    [InlineData(0x18000UL, CombatDamageClassification.Direct)]
    public void ClassificationUsesVerifiedFlagPriority(ulong flags, CombatDamageClassification expected)
    {
        var frames = new List<CombatFrame>();
        var observer = new TwCombatObserver("classification-test", new("classification-test", "1", "TW", "synthetic"), frames.Add);
        var flagBytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(flagBytes, flags);
        var message = CombatProtocolTests.Damage(flags: flagBytes);
        observer.Accept(Tw20261110CombatProtocol.DamageFloater, message.AsSpan(9), 0, message.Length, 0, CombatProtocolTests.At(0));
        observer.Tick(250, CombatProtocolTests.At(250));
        var damage = Assert.Single(frames.SelectMany(value => value.Damage));
        Assert.Equal(expected, damage.Classification!.Value.KnownValue);
        Assert.Equal(100UL, damage.Amount.Value);
        Assert.True(damage.Hit);
    }

    [Fact]
    public void CurrentProfileIncludesVerifiedGaugeAndOpaqueMaximumNotifications()
    {
        Assert.Contains(170204, Tw20261110CombatProtocol.ProtocolsV1);
        Assert.Contains(170117, Tw20261110CombatProtocol.ProtocolsV1);
        Assert.Contains(170204, Tw20261110CombatProtocol.StreamProfile.ObservedProtocols);
        Assert.Contains(170117, Tw20261110CombatProtocol.StreamProfile.ObservedProtocols);
    }

    [Theory]
    [InlineData(0x200UL, true, false, false)]
    [InlineData(0x400UL, false, true, false)]
    [InlineData(0x08000000UL, false, false, true)]
    [InlineData(0x08000600UL, true, true, true)]
    [InlineData(0x800UL, false, false, false)]
    public void CurrentHitFlagsKeepIndependentPowerfulRapidAndDoubleBits(ulong flags, bool powerful, bool rapid, bool doubleHit)
    {
        var replay = new Replay(); replay.Start();
        var bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, flags);
        replay.Feed(CombatProtocolTests.Damage(attacker: replay.Local, flags: bytes), 0);
        replay.Observer.Tick(250, CombatProtocolTests.At(250));
        var damage = Assert.Single(replay.Damage);
        Assert.Equal(powerful, damage.PowerfulHit); Assert.Equal(rapid, damage.RapidHit); Assert.Equal(doubleHit, damage.DoubleHit);
    }

    // 前七行为 2026-11-10 留存实采中出现过的完整 HitFlags 位型，后三行补齐样本未出现的圣、精神及多元素组合。
    [Theory]
    [InlineData(0x000000004000072DUL, true, true, false, false, true, "fire")]
    [InlineData(0x0000000100000741UL, false, false, true, false, true, "ice")]
    [InlineData(0x0000000002000080UL, false, false, false, true, false, "")]
    [InlineData(0x0000001200018800UL, false, false, false, false, false, "lightning")]
    [InlineData(0x0000001400018800UL, false, false, false, false, false, "poison")]
    [InlineData(0x0000000012000000UL, false, false, false, false, false, "bleed")]
    [InlineData(0x0000000020008400UL, false, false, false, false, false, "dark")]
    [InlineData(0x0000000080000000UL, false, false, false, false, false, "holy")]
    [InlineData(0x0000000800000000UL, false, false, false, false, false, "soul")]
    [InlineData(0x0000000FF000010CUL, true, true, false, false, true, "bleed,dark,fire,holy,ice,lightning,poison,soul")]
    public void CurrentHitFlagsProjectBreakAttackTypeAndElementBits(ulong flags, bool breaked, bool breakOccurred,
        bool firstAttack, bool defaultAttack, bool multiHit, string elements)
    {
        var replay = new Replay(); replay.Start();
        var bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, flags);
        replay.Feed(CombatProtocolTests.Damage(attacker: replay.Local, flags: bytes), 0);
        replay.Observer.Tick(250, CombatProtocolTests.At(250));
        var damage = Assert.Single(replay.Damage);
        Assert.Equal(breaked, damage.BreakedHit); Assert.Equal(breakOccurred, damage.BreakOccurred);
        Assert.Equal(firstAttack, damage.FirstAttack); Assert.Equal(defaultAttack, damage.DefaultAttack);
        Assert.Equal(multiHit, damage.MultiHit);
        // 追加打擊（0x800）：留存实采位型 0x...8800 中出现。
        Assert.Equal((flags & 0x800) != 0, damage.AdditionalHit);
        Assert.Equal(elements, string.Join(',', damage.Elements!.Select(value => value.RawValue)));
        // 既有标记的判定保持原位定义。
        Assert.Equal((flags & 1) != 0, damage.Critical); Assert.Equal((flags & 0x200) != 0, damage.PowerfulHit);
        Assert.Equal((flags & 0x400) != 0, damage.RapidHit); Assert.Equal((flags & 0x8000000) != 0, damage.DoubleHit);
    }

    [Fact]
    public void PrefixGaugePreservesFullMaximumAndIndependentSameNamedTargets()
    {
        var replay = new Replay(); replay.Start();
        replay.Appearance(2222, 10.5f, long.MaxValue); replay.Appearance(3333, 20.5f, 2000);
        replay.Feed(Gauge(2222, 30.5f), 100);
        var entities = replay.Frames[^1].Entities.Where(value => value.Role.KnownValue == CombatEntityRole.Monster).ToArray();
        Assert.Equal(2, entities.Length); Assert.Single(entities.Select(value => value.DisplayName).Distinct());
        Assert.Equal(30.5, entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!.Current);
        Assert.Equal(long.MaxValue.ToString(), entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!.Maximum!.ToString());
        Assert.Equal(20.5, entities.Single(value => value.Key.RawInstanceId.Value == 3333).BreakGauge!.Current);
    }

    [Fact]
    public void DelayedDamageAndNewGaugeShareAFrameAtTheLatestObservationTime()
    {
        var replay = new Replay(); replay.Start(); replay.Appearance(2222, 10.5f, 1000);
        replay.Feed(CombatProtocolTests.Damage(attacker: replay.Local), 100);
        replay.Feed(Gauge(2222, 30.5f), 150);
        replay.Observer.Tick(350, CombatProtocolTests.At(350));
        var frame = replay.Frames.Single(value => value.Damage.Count > 0);
        var damage = Assert.Single(frame.Damage);
        var gauge = frame.Entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!;
        Assert.Equal(CombatProtocolTests.At(100), damage.ObservedAt);
        Assert.Equal(100, damage.MonotonicMs);
        Assert.Equal(CombatProtocolTests.At(150), gauge.ObservedAt);
        Assert.Equal(CombatProtocolTests.At(350), frame.ObservedAt);
        Assert.Equal(150, frame.MonotonicMs);
        Assert.False(gauge.Stale);
    }

    [Fact]
    public void DelayedDamageWithoutANewerSnapshotKeepsItsOriginalFrameMonotonicTime()
    {
        var replay = new Replay(); replay.Start(); replay.Appearance(2222, 10.5f, 1000);
        replay.Feed(CombatProtocolTests.Damage(attacker: replay.Local), 100);
        replay.Observer.Tick(400, CombatProtocolTests.At(400));
        var frame = replay.Frames.Single(value => value.Damage.Count > 0);
        Assert.Equal(100, Assert.Single(frame.Damage).MonotonicMs);
        Assert.Equal(100, frame.MonotonicMs);
    }

    [Fact]
    public void OpaqueMaximumUpdateLeavesMonsterGaugesUnchanged()
    {
        var replay = new Replay(); replay.Start(); replay.Appearance(2222, 10.5f, 1000);
        replay.Feed(CombatProtocolTests.Message(Tw20261110CombatProtocol.UpdateBreakPointMax, Enumerable.Repeat((byte)255, 32).ToArray()), 100);
        replay.Feed(Gauge(2222, 30.5f), 200);
        var gauge = replay.Frames[^1].Entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!;
        Assert.Equal(30.5, gauge.Current); Assert.Equal("1000", gauge.Maximum!.ToString()); Assert.False(gauge.Stale);
        Assert.Null(gauge.Reason);
    }

    [Fact]
    public void ANewGaugeObservationRestoresCurrentStateAfterAGapWithoutInventingDamage()
    {
        var replay = new Replay(); replay.Start(); replay.Appearance(2222, 10.5f, 1000);
        replay.Observer.Reject("test-gap", 100, CombatProtocolTests.At(100));
        Assert.True(replay.Frames[^1].Entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!.Stale);
        replay.Feed(Gauge(2222, 30.5f), 200);
        var gauge = replay.Frames[^1].Entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!;
        Assert.Equal(30.5, gauge.Current); Assert.Equal(CombatProtocolTests.At(200), gauge.ObservedAt); Assert.False(gauge.Stale);
        Assert.Equal("1000", gauge.Maximum!.ToString()); Assert.Null(gauge.Reason);
        replay.Appearance(2222, 40.5f, 2000);
        gauge = replay.Frames[^1].Entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!;
        Assert.Equal(40.5, gauge.Current); Assert.Equal("2000", gauge.Maximum!.ToString());
        Assert.False(gauge.Stale); Assert.Null(gauge.Reason);
        Assert.Empty(replay.Damage);
    }

    [Fact]
    public void SceneResetDoesNotCarryTheOldGaugeIntoTheNewGeneration()
    {
        var replay = new Replay(); replay.Start(); replay.Appearance(2222, 10.5f, 1000);
        var previous = replay.Observer.Generation;
        replay.Observer.Reset("test-reset", 100, CombatProtocolTests.At(100));
        replay.Start(); replay.Feed(Gauge(2222, 30.5f), 200);
        Assert.True(replay.Observer.Generation.Value > previous.Value);
        Assert.DoesNotContain(replay.Frames[^1].Entities, value => value.Key.RawInstanceId.Value == 2222);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidGaugeUpdateKeepsLastValueStaleAndFormsAProtocolGap(float current)
    {
        var replay = new Replay(); replay.Start(); replay.Appearance(2222, 10.5f, 1000);
        replay.Feed(Gauge(2222, current), 100);
        Assert.Contains(replay.Frames.SelectMany(value => value.Gaps), value => value.Reason == "combat.protocolInvalid");
        var gauge = replay.Frames[^1].Entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!;
        Assert.Equal(10.5, gauge.Current); Assert.True(gauge.Stale);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void CurrentMappedGaugeFramesCrossSegmentsAndRetransmissions(int segment)
    {
        var replay = new Replay(); replay.Start(); replay.Appearance(2222, 10.5f, 1000);
        var bytes = Gauge(2222, 30.5f); var start = replay.Sequence;
        for (var offset = 0; offset < bytes.Length; offset += segment)
            replay.Stream.Feed(start + (uint)offset, bytes.AsSpan(offset, Math.Min(segment, bytes.Length - offset)).ToArray(), 100, 100);
        replay.Stream.Feed(start, bytes, 100, 100);
        Assert.Equal(30.5, replay.Frames[^1].Entities.Single(value => value.Key.RawInstanceId.Value == 2222).BreakGauge!.Current);
        Assert.Empty(replay.Frames.SelectMany(value => value.Gaps));
    }

    private static byte[] Gauge(ulong id, float current)
    {
        var body = new byte[13]; BinaryPrimitives.WriteUInt64LittleEndian(body, id);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), BitConverter.SingleToInt32Bits(current));
        return CombatProtocolTests.Message(Tw20261110CombatProtocol.SetBreakPoint, body);
    }
    private sealed class Replay
    {
        public List<CombatFrame> Frames { get; } = [];
        public List<CombatDamageEvent> Damage => Frames.SelectMany(value => value.Damage).ToList();
        public TwCombatObserver Observer { get; }
        public GameTcpStream Stream { get; }
        public uint Sequence { get; private set; }
        public ulong Local => Observer.LocalEntity!.Key.RawInstanceId.Value;
        private double now;
        public Replay()
        {
            Observer = new("fixture-game", Tw20261110CombatProtocol.CombatProfile.Source, Frames.Add);
            Stream = new((code, body) => Observer.Accept(code, body, Stream!.FrameStreamOffset, Stream.FrameWireLength,
                Stream.FrameReceivedLocalMs ?? now, CombatProtocolTests.At(now), Stream.FrameEncoding),
                reason => Observer.Reset(reason, now, CombatProtocolTests.At(now)), Tw20261110CombatProtocol.StreamProfile);
        }
        public void Start() => Feed(CombatProtocolTests.Identity(), 0);
        public void Appearance(ulong id, float current, long maximum) => Feed(CombatProtocolTests.Message(Tw20261110CombatProtocol.AppearingAutomaton,
            TwAutomatonAppearanceTests.Prefix(entity: id, breakPoint: current, breakPointMax: maximum), 1), now);
        public void Feed(byte[] bytes, double at) { now = at; Stream.Feed(Sequence, bytes, at, at); Sequence += (uint)bytes.Length; }
    }
}
