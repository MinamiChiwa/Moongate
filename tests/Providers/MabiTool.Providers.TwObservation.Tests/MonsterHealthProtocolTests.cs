using System.Buffers.Binary;
using System.Security.Cryptography;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;
using Op = MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>v4 实体血量：明文与加密 HP、初值、最大值更新、解密失败不更新不清零、死亡与离场、本机主目标、实体延续与容量淘汰。帧均为自行构造。</summary>
public sealed class MonsterHealthProtocolTests
{
    private const ulong Local = CombatProtocolTests.LocalEntityId, Monster = 5001, Boss = 5002;
    private static readonly byte[] Iv = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();

    [Fact]
    public void PlainHealthComesFromTheAppearanceAndLaterUpdates()
    {
        var replay = new Replay();
        replay.Appear(Monster, healthMax: 1000, payload: Plain(800));
        replay.Raw(Op.SetMainTarget, MainTargetBody(Local, (long)Monster));
        Assert.Equal((800UL, 1000UL, "read"), Health(replay, Monster));
        replay.Health(Op.SetHealth, Monster, PlainValue(700));
        var health = replay.Entity(Monster).Health!;
        Assert.Equal((700UL, true, (bool?)null), (health.Current!.Value.Value, health.Updated == true, health.Encrypted));
        Assert.Equal("monster", replay.Entity(Monster).ActorType);
    }

    [Fact]
    public void EncryptedHealthIsDecryptedWithTheEntityKeyAndAppearanceIv()
    {
        var replay = new Replay();
        replay.Appear(Boss, healthMax: 60_000_000, boss: true, encrypted: true, payload: Cipher(Boss, 60_000_000));
        Assert.Equal((60_000_000UL, 60_000_000UL, "read"), Health(replay, Boss));
        replay.Health(Op.SetHealth2 + 1, Boss, Cipher(Boss, 59_000_123));
        var entity = replay.Entity(Boss);
        Assert.Equal(59_000_123UL, entity.Health!.Current!.Value.Value);
        Assert.True(entity.Health.Encrypted); Assert.True(entity.IsBoss);
        Assert.Equal((2UL, 0UL), replay.Observer.HealthDecryption);
    }

    [Fact]
    public void UndecryptableOrOutOfRangeUpdatesKeepTheLastValue()
    {
        var replay = new Replay();
        replay.Appear(Boss, healthMax: 1000, boss: true, encrypted: true, payload: Cipher(Boss, 900));
        replay.Health(Op.SetHealth6, Boss, Cipher(Boss + 1, 800));             // 密钥不符：填充不合法
        Assert.Equal((900UL, TwCombatObserver.HealthUndecryptable), (replay.Entity(Boss).Health!.Current!.Value.Value, replay.Entity(Boss).Health!.Reason));
        replay.Health(Op.SetHealth6, Boss, Cipher(Boss, 5000));                // 超过最大值
        replay.Health(Op.SetHealth6, Boss, Cipher(Boss, -1));                  // 负值
        replay.Health(Op.SetHealth6, Boss, CipherBytes(Boss, new byte[12]));   // 明文不是 8 字节
        Assert.Equal(900UL, replay.Entity(Boss).Health!.Current!.Value.Value);
        replay.Health(Op.SetHealth6, Boss, Cipher(Boss, 850));
        Assert.Equal((850UL, (string?)null), (replay.Entity(Boss).Health!.Current!.Value.Value, replay.Entity(Boss).Health!.Reason));
        // 没有出现帧的实体：本机未命中过时不记录；命中过后加密 HP 为未读取。
        replay.Health(Op.SetHealth2, 7777, Cipher(7777, 10));
        Assert.DoesNotContain(replay.Last.Entities, entity => entity.Key.RawInstanceId.Value == 7777);
        replay.Feed(CombatProtocolTests.Damage(Local, 7777, 5));
        replay.Health(Op.SetHealth2, 7777, Cipher(7777, 10));
        var unknown = replay.Entity(7777).Health!;
        Assert.Equal(("unread", TwCombatObserver.HealthUnread), (unknown.State, unknown.Reason));
    }

    [Fact]
    public void HealthMaximumFollowsTheBroadcastKeyedUpdate()
    {
        var replay = new Replay();
        replay.Appear(Monster, healthMax: 1000, payload: Plain(1000));
        replay.Raw(Op.SetMainTarget, MainTargetBody(Local, (long)Monster));
        var key = Enumerable.Range(40, 16).Select(value => (byte)value).ToArray();
        replay.Raw(Op.UpdateHealthMax, MaxCipher(key, Iv, Monster, 2000));    // 还没有广播密钥：忽略
        Assert.Equal(1000UL, replay.Entity(Monster).Health!.Maximum!.Value.Value);
        replay.Raw(Op.BroadcastKey, BroadcastBody(key, Iv));
        replay.Raw(Op.UpdateHealthMax, MaxCipher(key, Iv, Monster, 2000));
        Assert.Equal(2000UL, replay.Entity(Monster).Health!.Maximum!.Value.Value);
    }

    [Fact]
    public void DeathMarksTheEntityAndDisappearanceRemovesItAfterOneScopeFrame()
    {
        var replay = new Replay();
        replay.Appear(Monster, healthMax: 1000, payload: Plain(10));
        replay.Raw(Op.SetDead, DeathBody(Monster, true));
        Assert.True(replay.Entity(Monster).Dead);
        Assert.Empty(replay.Observer.ExportCarry());
        replay.Raw(Op.SetDead, DeathBody(Monster, false));
        Assert.NotEqual(true, replay.Entity(Monster).Dead);
        replay.Raw(Op.Disappearing, BitConverter.GetBytes(Monster));
        Assert.False(replay.Entity(Monster).InScope);
        replay.Tick(replay.Now + 3500);
        Assert.DoesNotContain(replay.Last.Entities, entity => entity.Key.RawInstanceId.Value == Monster);
    }

    [Fact]
    public void LocalMainTargetIsPublishedWithEveryFrame()
    {
        var replay = new Replay();
        replay.Appear(Monster, healthMax: 1000, payload: Plain(1000));
        replay.Raw(Op.SetMainTarget, MainTargetBody(9999, (long)Monster));     // 其他实体的主目标
        Assert.Null(replay.Last.MainTarget);
        replay.Raw(Op.SetMainTarget, MainTargetBody(Local, (long)Monster));
        Assert.Equal(Monster, replay.Last.MainTarget!.Target!.RawInstanceId.Value);
        replay.Raw(Op.SetMainTarget, MainTargetBody(Local, -1));
        Assert.NotNull(replay.Last.MainTarget); Assert.Null(replay.Last.MainTarget!.Target);
    }

    [Fact]
    public void OlderInterestsDoNotPublishHealthFields()
    {
        var replay = new Replay(health: false);
        replay.Appear(Boss, healthMax: 1000, boss: true, encrypted: true, payload: Cipher(Boss, 900));
        replay.Raw(Op.SetMainTarget, MainTargetBody(Local, (long)Boss));
        var entity = replay.Entity(Boss);
        Assert.Null(entity.Health); Assert.Null(entity.IsBoss); Assert.Null(entity.ActorType);
        Assert.Null(replay.Last.MainTarget);
    }

    [Fact]
    public void CarriedEntitiesKeepDecodingAcrossRestartAndSameConnectionReset()
    {
        var first = new Replay();
        first.Appear(Boss, healthMax: 1000, boss: true, encrypted: true, payload: Cipher(Boss, 900));
        first.Appear(Monster, healthMax: 500, payload: Plain(500));
        var carry = first.Observer.ExportCarry();
        Assert.Equal(Boss, carry[0].Id.Value);
        Assert.NotNull(carry[0].Iv);

        // Worker 重启：新观察器以延续记录补足后，下一条加密 HP 照常解出。
        var restarted = new Replay();
        Assert.Equal(2, restarted.Observer.SeedEntities(carry, restarted.Now, CombatProtocolTests.At(restarted.Now)));
        restarted.Tick(restarted.Now + 1);
        Assert.Equal("unread", restarted.Entity(Boss).Health!.State);
        restarted.Health(Op.SetHealth2 + 2, Boss, Cipher(Boss, 700));
        Assert.Equal((700UL, true), (restarted.Entity(Boss).Health!.Current!.Value.Value, restarted.Entity(Boss).IsBoss == true));
        Assert.Equal(CombatEntityRole.Monster, restarted.Entity(Boss).Role.KnownValue);

        // 同一连接内容矛盾的重置延续实体；新连接不延续。
        restarted.Observer.Reset("combat.streamGap.conflictingRetransmission", restarted.Now, CombatProtocolTests.At(restarted.Now), keepEntities: true);
        restarted.Feed(CombatProtocolTests.Identity());
        restarted.Health(Op.SetHealth2 + 2, Boss, Cipher(Boss, 650));
        Assert.Equal(650UL, restarted.Entity(Boss).Health!.Current!.Value.Value);
        restarted.Observer.Reset("combat.streamGap.connectionSyn", restarted.Now, CombatProtocolTests.At(restarted.Now));
        restarted.Feed(CombatProtocolTests.Identity());
        restarted.Health(Op.SetHealth2 + 2, Boss, Cipher(Boss, 600));
        Assert.DoesNotContain(restarted.Last.Entities, entity => entity.Key.RawInstanceId.Value == Boss);
    }

    [Fact]
    public void MonsterAppearanceEvictsAnotherPlayerWhenTheTableIsFull()
    {
        var replay = new Replay();
        for (ulong index = 0; index < TwCombatObserver.MaximumEntities - 1; index++)
            replay.Feed(CombatProtocolTests.Player(20_000 + index, 9_000_000 + index));
        Assert.Equal(TwCombatObserver.MaximumEntities, replay.Last.Entities.Count);
        replay.Appear(Monster, healthMax: 1000, payload: Plain(1000));
        Assert.Equal(CombatEntityRole.Monster, replay.Entity(Monster).Role.KnownValue);
        Assert.DoesNotContain(replay.Frames.SelectMany(frame => frame.Gaps), gap => gap.Reason == "combat.entityCapacity");
        Assert.Contains(replay.Last.Entities, entity => entity.Key.RawInstanceId.Value == Local);
    }

    [Fact]
    public void EvictedMonsterKeepsItsTypeWhenItReturns()
    {
        var replay = new Replay();
        replay.Appear(Monster, healthMax: 1000, payload: Plain(1000));
        replay.Tick(replay.Now + TwCombatObserver.EntityIdleMs + 1);
        for (ulong index = 0; index < TwCombatObserver.MaximumEntities - 1; index++) replay.Feed(CombatProtocolTests.Damage(30_000 + index, 40_000, 1));
        Assert.DoesNotContain(replay.Last.Entities, entity => entity.Key.RawInstanceId.Value == Monster);
        replay.Tick(replay.Now + TwCombatObserver.EntityIdleMs + 1);
        replay.Feed(CombatProtocolTests.Damage(Local, Monster, 10));
        replay.Tick(replay.Now + 300);
        var back = replay.Entity(Monster);
        Assert.Equal((CombatEntityRole.Monster, "monster", 1000UL), (back.Role.KnownValue!.Value, back.ActorType, back.Health!.Maximum!.Value.Value));
    }

    [Fact]
    public void HealthIsReportedOnlyForCandidatesWhenChangedAndTheHostSnapshotStaysComplete()
    {
        var replay = new Replay();
        replay.Appear(Boss, healthMax: 1000, boss: true, encrypted: true, payload: Cipher(Boss, 900));
        replay.Appear(Monster, healthMax: 1000, payload: Plain(1000));
        var session = new MabiTool.Modules.CombatObservation.Domain.CombatSession("owner", "obs", Op.CombatProfile.Source, replay.Observer.LocalEntity!,
            new("fixture-game"), CombatProtocolTests.At(replay.Now));
        replay.Health(Op.SetHealth2, Boss, Cipher(Boss, 800));
        Assert.Equal(800UL, replay.Last.Entities.Single(entity => entity.Key.RawInstanceId.Value == Boss).Health!.Current!.Value.Value);
        // 不是候选的怪物从不带 health；首领未变化时不重复附带，到全量周期再附带一次。
        Assert.DoesNotContain(replay.Frames.SelectMany(frame => frame.Entities), entity => entity.Key.RawInstanceId.Value == Monster && entity.Health is not null);
        var changedAt = replay.Frames.Count;
        replay.Tick(replay.Now + 600);
        Assert.Null(replay.Last.Entities.Single(entity => entity.Key.RawInstanceId.Value == Boss).Health);
        replay.Tick(replay.Now + 2100);
        Assert.Equal(800UL, replay.Last.Entities.Single(entity => entity.Key.RawInstanceId.Value == Boss).Health!.Current!.Value.Value);
        // 宿主沿用上次附带的值：中间不带 health 的帧不清空快照中的血量。
        var sequence = 0UL;
        foreach (var frame in replay.Frames.Where(frame => frame.Generation == replay.Last.Generation)) session.Apply(frame with { Sequence = new(++sequence) });
        var boss = session.Capture().Workspace.Entities.Single(entity => entity.Key.RawInstanceId.Value == Boss);
        Assert.Equal((800UL, 1000UL), (boss.Health!.Current!.Value.Value, boss.Health.Maximum!.Value.Value));
        Assert.True(replay.Frames.Count > changedAt);

        // 新代次（同一连接的内容矛盾重置后补回实体）：新代次的首帧即带全量。
        replay.Observer.Reset("combat.streamGap.conflictingRetransmission", replay.Now, CombatProtocolTests.At(replay.Now), keepEntities: true);
        var before = replay.Frames.Count;
        replay.Feed(CombatProtocolTests.Identity());
        var first = replay.Frames.Skip(before).First(frame => frame.Generation == replay.Last.Generation && frame.Entities.Any(entity => entity.Key.RawInstanceId.Value == Boss));
        Assert.NotNull(first.Entities.Single(entity => entity.Key.RawInstanceId.Value == Boss).Health);
    }

    private static (ulong, ulong, string) Health(Replay replay, ulong id)
    {
        var health = replay.Entity(id).Health!;
        return (health.Current!.Value.Value, health.Maximum!.Value.Value, health.State);
    }

    internal static byte[] Plain(long value) { var bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value); return bytes; }
    private static byte[] PlainValue(long value) => Plain(value);
    internal static byte[] Cipher(ulong entity, long value) => CipherBytes(entity, Plain(value));
    internal static byte[] CipherBytes(ulong entity, byte[] plain)
    {
        using var aes = Aes.Create();
        var key = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(key, entity); BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), entity);
        aes.Key = key;
        return aes.EncryptCbc(plain, Iv, PaddingMode.PKCS7);
    }
    private static byte[] MaxCipher(byte[] key, byte[] iv, ulong entity, long value)
    {
        using var aes = Aes.Create(); aes.Key = key;
        var plain = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(plain, entity); BinaryPrimitives.WriteInt64LittleEndian(plain.AsSpan(8), value);
        return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
    }
    private static byte[] BroadcastBody(byte[] key, byte[] iv)
    {
        var body = new byte[40]; BinaryPrimitives.WriteInt32LittleEndian(body, 16); key.CopyTo(body, 4);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), 16); iv.CopyTo(body, 24); return body;
    }
    private static byte[] DeathBody(ulong entity, bool dead)
    {
        var body = new byte[34]; BinaryPrimitives.WriteUInt64LittleEndian(body, entity); body[8] = dead ? (byte)1 : (byte)0; return body;
    }
    private static byte[] MainTargetBody(ulong owner, long target)
    {
        var body = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(body, owner); BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), target); return body;
    }
    internal static byte[] HealthBody(ulong entity, byte[] value)
    {
        var body = new byte[13 + value.Length]; BinaryPrimitives.WriteUInt64LittleEndian(body, entity);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)value.Length); value.CopyTo(body, 12); return body;
    }

    /// <summary>以当前构建身份建立本机实体，其余帧按递增流偏移交给 v4 兴趣的观察器。</summary>
    private sealed class Replay
    {
        public List<CombatFrame> Frames { get; } = [];
        public TwCombatObserver Observer { get; }
        public double Now { get; private set; } = 1;
        private readonly GameTcpStream stream;
        private uint sequence;
        private ulong offset = 1_000_000;
        public CombatFrame Last => Frames[^1];
        public Replay(bool health = true)
        {
            Observer = new("fixture-game", Op.CombatProfile.Source, Frames.Add);
            Observer.Configure(new(true, true, true, true, true, health));
            stream = new((code, body) => Observer.Accept(code, body, stream!.FrameStreamOffset, stream.FrameWireLength, Now,
                CombatProtocolTests.At(Now), stream.FrameEncoding), reason => Observer.Reset(reason, Now, CombatProtocolTests.At(Now)), Op.StreamProfile);
            Feed(CombatProtocolTests.Identity());
        }
        public void Feed(byte[] bytes) { Now += 10; stream.Feed(sequence, bytes, Now, Now); sequence += (uint)bytes.Length; Observer.Tick(Now, CombatProtocolTests.At(Now)); }
        public void Raw(int opcode, byte[] body)
        {
            Now += 10; offset += 1000;
            Observer.Accept(opcode, body, offset, body.Length + 9, Now, CombatProtocolTests.At(Now));
            Observer.Tick(Now + 300, CombatProtocolTests.At(Now + 300)); Now += 300;
        }
        public void Health(int opcode, ulong entity, byte[] value) => Raw(opcode, HealthBody(entity, value));
        public void Appear(ulong entity, long healthMax, byte[] payload, bool boss = false, bool encrypted = false) =>
            Feed(CombatProtocolTests.Message(Op.AppearingAutomaton, TwAutomatonAppearanceTests.Prefix(entity: entity, healthMax: healthMax, boss: boss ? (byte)1 : (byte)0,
                encrypted: encrypted, payload: payload, iv: Iv), 1));
        public void Tick(double at) { Now = at; Observer.Tick(Now, CombatProtocolTests.At(Now)); Observer.Finish(Now, CombatProtocolTests.At(Now)); }
        /// <summary>最后一帧中的实体；未随帧附带 health 时按宿主的做法沿用同一代次最近一次附带的值。</summary>
        public CombatEntityObservation Entity(ulong id)
        {
            var entity = Last.Entities.Single(value => value.Key.RawInstanceId.Value == id);
            return entity.Health is not null ? entity : entity with { Health = Frames.Where(frame => frame.Generation == Last.Generation)
                .SelectMany(frame => frame.Entities).LastOrDefault(value => value.Key.RawInstanceId.Value == id && value.Health is not null)?.Health };
        }
    }
}
