using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

public sealed class CombatProtocolTests
{
    [Fact]
    public void EqualHitsAcrossSegmentsAndARetransmittedTailAreEachCountedOnce()
    {
        var replay = new Replay(); var prefix = Identity(); replay.Feed(0, prefix, 0);
        var hits = Join(Damage(), Damage(), Damage()); var start = (uint)prefix.Length;
        replay.Feed(start, hits[..31], 10);
        replay.Feed(start + 31, hits[31..], 20);
        replay.Feed(start + 31, hits[31..], 30);
        replay.Observer.Tick(300, At(300));
        Assert.Equal(3, replay.Damage.Count); Assert.Equal(300UL, replay.Damage.Aggregate(0UL, (sum, item) => sum + item.Amount.Value));
        Assert.Equal(3, replay.Damage.Select(item => item.EventId).Distinct().Count()); Assert.Empty(replay.Gaps);
    }

    [Fact]
    public void OwnersDifferingOnlyAboveTheLow32BitsStayDistinct()
    {
        var replay = new Replay();
        replay.Feed(0, Join(Identity(), Buff(owner: (1UL << 32) | 1111), Buff(owner: (2UL << 32) | 1111)), 0);
        Assert.Equal(2, replay.Effects.Select(x => x.EntityKey).Distinct().Count());
        Assert.All(replay.Effects, item => Assert.True(item.EntityKey.RawInstanceId.Value > uint.MaxValue));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(53)]
    public void EveryFrameBoundaryCanCrossTcpSegmentsWithoutDuplicatingEffectsOrDamage(int segmentSize)
    {
        var replay = new Replay();
        var bytes = Join(Identity(), Buff(), Skill(), Damage(), Buff(Tw20261110CombatProtocol.BuffRefresh, stacks: 3), Buff(Tw20261110CombatProtocol.BuffExpire));
        for (var at = 0; at < bytes.Length; at += segmentSize)
            replay.Feed((uint)at, bytes.AsSpan(at, Math.Min(segmentSize, bytes.Length - at)).ToArray(), 10);
        replay.Observer.Tick(260, At(260));
        Assert.Single(replay.Damage); Assert.Equal(0x12345678UL, replay.Damage[0].SkillId.Value);
        Assert.True(replay.Damage[0].SkillResolved); Assert.Equal(3, replay.Effects.Count); Assert.Empty(replay.Gaps);
        Assert.Equal(replay.Effects[0].CasterKey, replay.Effects[2].CasterKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutOfOrderRetransmissionAndSequenceWrapKeepFullEntityAndInstanceIds(bool wrap)
    {
        var replay = new Replay(); var prefix = Identity(); var start = wrap ? uint.MaxValue - (uint)prefix.Length - 5 : 100u;
        replay.Feed(start, prefix, 0); var next = unchecked(start + (uint)prefix.Length);
        var bytes = Join(Buff(owner: (1UL << 32) | 1111, instance: ulong.MaxValue, caster: ulong.MaxValue),
            Damage(attacker: (2UL << 32) | 1111, target: ulong.MaxValue));
        replay.Feed(unchecked(next + 5), bytes[5..], 20);
        replay.Feed(next, bytes[..5], 30);
        replay.Feed(next, bytes, 40); replay.Observer.Tick(300, At(300));
        Assert.Single(replay.Effects); Assert.Equal(ulong.MaxValue, replay.Effects[0].InstanceId.Value);
        Assert.Equal(ulong.MaxValue, replay.Effects[0].CasterKey!.RawInstanceId.Value);
        Assert.Single(replay.Damage); Assert.Equal((2UL << 32) | 1111, replay.Damage[0].SourceEntity.RawInstanceId.Value);
        Assert.NotEqual(replay.Observer.LocalEntity!.Key, replay.Damage[0].SourceEntity); Assert.Empty(replay.Gaps);
    }

    [Fact]
    public void NearestSkillRequiresExactTargetAndFlagsAndRetainsChainCaster()
    {
        var replay = new Replay(); var flags = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        replay.Feed(0, Join(Identity(), Skill(skill: 10, flags: flags),
            Skill(skill: 20, target: 3333, flags: flags), Damage(flags: flags),
            Skill(skill: 30, caster: 9999, flags: flags)), 0);
        replay.Observer.Tick(250, At(250));
        var damage = Assert.Single(replay.Damage); Assert.Equal(30UL, damage.SkillId.Value);
        Assert.Equal(9999UL, damage.SkillCasterKey!.RawInstanceId.Value); Assert.True(damage.Critical);
    }

    [Fact]
    public void DamageWaitsForLaterSkillAndKeepsFollowingEffectsInSourceOrder()
    {
        var replay = new Replay(); var prefix = Join(Identity(), Damage()); replay.Feed(0, prefix, 0);
        replay.Feed((uint)prefix.Length, Buff(), 100); replay.Observer.Tick(249, At(249));
        Assert.Empty(replay.Damage); Assert.Empty(replay.Effects);
        replay.Feed((uint)(prefix.Length + Buff().Length), Skill(skill: 77), 249);
        replay.Observer.Tick(250, At(250));
        Assert.Equal(77UL, Assert.Single(replay.Damage).SkillId.Value);
        Assert.Single(replay.Effects); Assert.True(replay.Damage[0].MonotonicMs <= replay.Effects[0].MonotonicMs);
        Assert.All(replay.Frames, value => Assert.True(value.Damage.All(item => item.MonotonicMs <= value.MonotonicMs)));
    }

    [Fact]
    public void AChangedEffectOrCasterRemovesTheOldInstanceBeforeRefresh()
    {
        var replay = new Replay(); replay.Feed(0, Join(Identity(), Buff(caster: 1111),
            Buff(Tw20261110CombatProtocol.BuffRefresh, effect: 9, caster: 3333), Buff(Tw20261110CombatProtocol.BuffExpire)), 0);
        Assert.Equal(4, replay.Effects.Count); Assert.Equal(CombatEffectChangeKind.Remove, replay.Effects[1].Kind.KnownValue);
        Assert.Equal(1111UL, replay.Effects[1].CasterKey!.RawInstanceId.Value);
        Assert.Equal(3333UL, replay.Effects[3].CasterKey!.RawInstanceId.Value); Assert.Equal(9UL, replay.Effects[3].EffectId.Value);
    }

    [Fact]
    public void SceneInitializerReleasesOldIdentityAndKeepsFirstBindingWithinTheNewScene()
    {
        var replay = new Replay(); var first = Identity(); replay.Feed(0, first, 0);
        var old = replay.Observer.LocalEntity!.Key;
        var next = Join(Self(), Player(1112), Player(1113)); replay.Feed((uint)first.Length, next, 20);
        Assert.Equal(1112UL, replay.Observer.LocalEntity!.Key.RawInstanceId.Value);
        Assert.True(replay.Observer.Generation.Value > old.ObservationGeneration.Value);
        Assert.Contains(replay.Gaps, item => item.Reason == "combat.sceneChanged");
    }

    [Fact]
    public void MalformedBodiesAndChangedOpcodesFormGapsAndLaterValidFramesRecover()
    {
        var replay = new Replay(); var prefix = Join(Identity(), Message(Tw20261110CombatProtocol.DamageFloater, new byte[52]), Buff());
        replay.Feed(0, prefix, 0); Assert.Contains(replay.Gaps, item => item.Reason == "combat.protocolInvalid" && item.DroppedEvents.Value == 1);
        var changed = Message(0x7fffffff, new byte[36]); replay.Feed((uint)prefix.Length, changed, 10);
        var recovered = Join(Identity(), Buff()); replay.Feed((uint)(prefix.Length + changed.Length), recovered, 20);
        Assert.Equal(2, replay.Effects.Count); Assert.NotNull(replay.Observer.LocalEntity);
        Assert.True(replay.Gaps.Count >= 2);
    }

    [Fact]
    public void ConflictingRetransmissionAndGapTimeoutWithdrawIdentityAndNeverInventPartialBuffs()
    {
        var replay = new Replay(); var prefix = Identity(); replay.Feed(0, prefix, 0);
        var broken = prefix.ToArray(); broken[^1] ^= 1; replay.Feed(0, broken, 10);
        Assert.Null(replay.Observer.LocalEntity); Assert.NotEmpty(replay.Gaps);
        var buff = Buff(); replay.Feed((uint)prefix.Length, buff[..12], 20);
        replay.Feed((uint)(prefix.Length + 20), buff[20..], 30); replay.Stream.CheckGap(1600);
        Assert.Empty(replay.Effects); Assert.Null(replay.Observer.LocalEntity);
    }

    [Fact]
    public void ParserCachesHaveFixedCapacitiesAndOverflowCountsRejectedRecords()
    {
        var replay = new Replay(); var prefix = Identity(); replay.Feed(0, prefix, 0); uint sequence = (uint)prefix.Length;
        for (var index = 0; index < 2300; index++)
        {
            var buff = Buff(instance: (ulong)index + 1); replay.Feed(sequence, buff, 10); sequence += (uint)buff.Length;
        }
        Assert.Equal(2048, replay.Observer.Usage.EffectInstances);
        Assert.Equal(252UL, replay.Gaps.Where(x => x.Reason == "combat.effectCapacity").Aggregate(0UL, (sum, x) => sum + x.DroppedEvents.Value));
        Assert.True(replay.Observer.Usage.Entities <= 128); Assert.True(replay.Observer.Usage.PendingRecords <= 512);
    }

    [Fact]
    public void CorrelatedChainStaysInTimelineAndDoesNotIncreaseLocalStatistics()
    {
        var replay = new Replay(); replay.Feed(0, Join(Identity(), Skill(), Damage(), Skill(caster: 9999), Damage(amount: 50)), 0);
        replay.Observer.Tick(250, At(250));
        var local = replay.Observer.LocalEntity!;
        var session = new CombatSession("owner", "observation", Tw20261110CombatProtocol.CombatProfile.Source, local,
            new("fixture-game"), At(0));
        foreach (var frame in replay.Frames) session.Apply(frame);
        var result = session.Capture();
        Assert.Equal("100", result.Workspace.Encounter!.Summary.AttributedDamage.ToString());
        Assert.Contains(result.Timeline, item => item.Item.Attribution == "chain" && item.Item.Damage!.SkillCasterKey!.RawInstanceId.Value == 9999);
    }

    [Theory]
    [InlineData(4294967295L)]
    [InlineData(4294967296L)]
    [InlineData(long.MaxValue)]
    public void DamagePreservesTheFullPositiveInt64Amount(long amount)
    {
        var replay = new Replay();
        replay.Feed(0, Join(Identity(), Skill(), Damage(amount: amount)), 0);
        replay.Observer.Tick(250, At(250));
        var damage = Assert.Single(replay.Damage);
        Assert.Equal((ulong)amount, damage.Amount.Value);
        Assert.Equal(CombatDamageKind.Unclassified, damage.AmountKind.KnownValue);
        Assert.Empty(replay.Gaps);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void NonpositiveDamageIsNeverReinterpretedAsAnUnsignedAmount(long amount)
    {
        var replay = new Replay();
        replay.Feed(0, Join(Identity(), Damage(amount: amount)), 0);
        replay.Observer.Tick(250, At(250));
        Assert.Empty(replay.Damage); Assert.Empty(replay.Gaps);
    }

    // 浮点位型取自 2026-11-10 留存样本：Add 5 s；Refresh 1.411 s 与 11.359 s 均在该值到期时收到 Expire；
    // -94608474112 与 99999 为常驻效果的取值。
    [Theory]
    [InlineData(Tw20261110CombatProtocol.BuffAdd, 0x40a00000u, EffectDurationKind.Finite, 5000d)]
    [InlineData(Tw20261110CombatProtocol.BuffRefresh, 0x3fb49ba6u, EffectDurationKind.Finite, 1411d)]
    [InlineData(Tw20261110CombatProtocol.BuffRefresh, 0x4135be77u, EffectDurationKind.Finite, 11359d)]
    [InlineData(Tw20261110CombatProtocol.BuffAdd, 0xd1b038d6u, EffectDurationKind.Infinite, null)]
    [InlineData(Tw20261110CombatProtocol.BuffAdd, 0x47c34f80u, EffectDurationKind.Infinite, null)]
    [InlineData(Tw20261110CombatProtocol.BuffAdd, 0x00000000u, EffectDurationKind.Unknown, null)]
    [InlineData(Tw20261110CombatProtocol.BuffAdd, 0xbf800000u, EffectDurationKind.Unknown, null)]
    [InlineData(Tw20261110CombatProtocol.BuffAdd, 0x7fc00000u, EffectDurationKind.Unknown, null)]
    [InlineData(Tw20261110CombatProtocol.BuffAdd, 0x7f800000u, EffectDurationKind.Unknown, null)]
    [InlineData(Tw20261110CombatProtocol.BuffAdd, 0xff800000u, EffectDurationKind.Unknown, null)]
    public void CurrentEndAtCarriesRemainingSecondsAndAPermanentSentinel(int opcode, uint bits, EffectDurationKind expected, double? durationMs)
    {
        var replay = new Replay();
        replay.Feed(0, Join(Identity(), Buff(opcode, durationBits: bits)), 0);
        var effect = Assert.Single(replay.Effects);
        Assert.Equal(expected, effect.DurationKind.KnownValue);
        if (durationMs is null) Assert.Null(effect.DurationMs);
        else Assert.Equal(durationMs.Value, effect.DurationMs!.Value, 3);
        Assert.Empty(replay.Gaps);
    }

    [Fact]
    public void SkillCorrelationComparesTheHighestHitFlagByte()
    {
        var replay = new Replay();
        replay.Feed(0, Join(Identity(), Skill(flags: [1, 2, 3, 4, 5, 6, 7, 8]),
            Damage(flags: [1, 2, 3, 4, 5, 6, 7, 9])), 0);
        replay.Observer.Tick(250, At(250));
        var damage = Assert.Single(replay.Damage);
        Assert.False(damage.SkillResolved); Assert.Equal(0UL, damage.SkillId.Value); Assert.Null(damage.SkillCasterKey);
        Assert.Empty(replay.Gaps);
    }

    internal static DateTimeOffset At(double ms) => new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms);
    internal static byte[] Join(params byte[][] frames) => frames.SelectMany(x => x).ToArray();
    /// <summary>本机角色出现帧使用的实体 ID；角色 ID 取当前构建身份 fixture。</summary>
    internal const ulong LocalEntityId = 1111;
    internal static readonly ulong FixtureCharacterId = ulong.Parse(IdentityFixture("characterId"));
    /// <summary>当前构建 CharacterPublicReplicatedData 前缀中 CharacterIdentity.CharacterId 的字节位置（实体 Id 位于 0）。</summary>
    private const int CharacterIdOffset = 1246;
    internal static byte[] Identity() => Join(Self(), Player());
    /// <summary>个人数据 20590：正文前 8 字节为角色 ID，Brotli 形态。</summary>
    internal static byte[] Self(ulong character = 0)
    {
        var body = Convert.FromBase64String(IdentityFixture("personalBodyBase64"));
        BinaryPrimitives.WriteUInt64LittleEndian(body, character == 0 ? FixtureCharacterId : character);
        return Message(Tw20261110CombatProtocol.PersonalData, body, 1);
    }
    /// <summary>角色出现 20054：以 fixture 前缀改写实体 ID 与角色 ID，Brotli 形态。</summary>
    internal static byte[] Player(ulong entity = LocalEntityId, ulong character = 0)
    {
        var body = Convert.FromBase64String(IdentityFixture("characterBodyBase64"));
        BinaryPrimitives.WriteUInt64LittleEndian(body, entity);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(CharacterIdOffset), character == 0 ? FixtureCharacterId : character);
        return Message(Tw20261110CombatProtocol.AppearingCharacter, body, 1);
    }
    private static string IdentityFixture(string property)
    {
        using var fixture = JsonDocument.Parse(typeof(CombatProtocolTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        return fixture.RootElement.GetProperty(property).GetString()!;
    }
    internal static byte[] Damage(ulong attacker = 1111, ulong target = 2222, long amount = 100, byte[]? flags = null)
    {
        var body = new byte[53]; BinaryPrimitives.WriteUInt64LittleEndian(body, attacker); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), target);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), amount); (flags ?? new byte[8]).CopyTo(body, 32);
        return Message(Tw20261110CombatProtocol.DamageFloater, body);
    }
    internal static byte[] Skill(uint skill = 0x12345678, ulong caster = 1111, ulong target = 2222, byte[]? flags = null)
    {
        var body = new byte[35]; BinaryPrimitives.WriteUInt64LittleEndian(body, caster); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), target);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), skill); (flags ?? new byte[8]).CopyTo(body, 24);
        return Message(Tw20261110CombatProtocol.HitPresentation, body);
    }
    internal static byte[] Buff(int opcode = Tw20261110CombatProtocol.BuffAdd, ulong owner = 1111, ulong instance = 9,
        uint effect = 0x12345678, uint durationBits = 0x40a00000, uint stacks = 1, ulong caster = 1111)
    {
        var remove = opcode == Tw20261110CombatProtocol.BuffExpire; var body = new byte[remove ? 16 : 36];
        BinaryPrimitives.WriteUInt64LittleEndian(body, owner); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), instance);
        if (!remove)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), effect); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), durationBits);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), stacks); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(28), caster);
        }
        return Message(opcode, body);
    }
    internal static byte[] Message(int opcode, byte[] body, byte encoding = 0)
    {
        if (encoding == 1)
        {
            using var output = new MemoryStream(); using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, true)) compressor.Write(body);
            body = output.ToArray();
        }
        var result = new byte[9 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(result, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), body.Length); result[8] = encoding; body.CopyTo(result, 9); return result;
    }
    private sealed class Replay
    {
        public List<CombatFrame> Frames { get; } = [];
        public List<CombatDamageEvent> Damage => Frames.SelectMany(x => x.Damage).ToList();
        public List<CombatEffectObservation> Effects => Frames.SelectMany(x => x.Effects).ToList();
        public List<CombatGapInput> Gaps => Frames.SelectMany(x => x.Gaps).ToList();
        public TwCombatObserver Observer { get; }
        public GameTcpStream Stream { get; }
        private double now;
        public Replay()
        {
            Observer = new("fixture-game", Tw20261110CombatProtocol.CombatProfile.Source, Frames.Add);
            Stream = new((code, body) => Observer.Accept(code, body, Stream!.FrameStreamOffset, Stream.FrameWireLength,
                Stream.FrameReceivedLocalMs ?? now, At(now), Stream.FrameEncoding), reason => Observer.Reset(reason, now, At(now)), Tw20261110CombatProtocol.StreamProfile);
        }
        public void Feed(uint sequence, byte[] bytes, double at) { now = at; Stream.Feed(sequence, bytes, at, at); }
    }
}
