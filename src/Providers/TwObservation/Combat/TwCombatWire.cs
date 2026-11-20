using System.Buffers.Binary;

namespace MabiTool.Providers.TwObservation.Combat;

/// <summary>HitPresentation 20526：施放者、目标、技能表 ID 与该次命中的 HitFlags。</summary>
internal readonly record struct TwHitPresentation(ulong Caster, ulong Target, uint SkillId, ulong HitFlags);
/// <summary>伤害飘字 21196：攻击者、目标、带符号伤害值与 HitFlags。</summary>
internal readonly record struct TwDamageFloater(ulong Attacker, ulong Target, long Amount, ulong HitFlags);
/// <summary>Buff Add/Refresh 170069/170071；层数保持线上 UInt32，由调用方在登记所有者后校验范围。</summary>
internal readonly record struct TwBuffUpdate(ulong Owner, ulong Instance, uint BuffId, float EndAtSeconds, uint Stacks, ulong Caster);
/// <summary>Buff Expire 170070：所有者与实例。</summary>
internal readonly record struct TwBuffExpire(ulong Owner, ulong Instance);
/// <summary>SetBreakPoint 170204：实体与当前破防值。</summary>
internal readonly record struct TwBreakPointUpdate(ulong Entity, float Current);
/// <summary>NotifyHealCasted 20644：目标、HitFlags 与毛治疗量。</summary>
internal readonly record struct TwHealCast(ulong Target, ulong HitFlags, long Amount);
/// <summary>NotifyHealReceived 20645：HitFlags 与毛治疗量，不含来源。</summary>
internal readonly record struct TwHealReceipt(ulong HitFlags, long Amount);
/// <summary>SetHealth 170205–170210：实体与明文 HP（长度 8，负值为空）；Encrypted 为长度 16 的正整数倍的加密形态，此时 PlainValue 为空。</summary>
internal readonly record struct TwHealthUpdate(ulong Entity, long? PlainValue, bool Encrypted = false);
/// <summary>SetDead 170197：实体与死亡标志（复活时为假）。</summary>
internal readonly record struct TwDeathUpdate(ulong Entity, bool Dead);
/// <summary>MainTarget.SetTarget 170739：复制对象（其主目标）与目标；-1 表示没有目标。</summary>
internal readonly record struct TwMainTargetUpdate(ulong Owner, long Target);
/// <summary>SetCombatState 170196：实体与客户端 CombatState 原值。</summary>
internal readonly record struct TwCombatStateUpdate(ulong Entity, int State);

/// <summary>
/// 台服 tw-20261110 战斗消息的定长字段读取。只做结构校验：长度、编码与必填 ID 不符时抛出 <see cref="InvalidDataException"/>；
/// 取值语义（非正伤害、零治疗、实体归属）由 <see cref="TwCombatObserver"/> 判定。
/// </summary>
internal static class TwCombatWire
{
    public const int HitPresentationBytes = 35, DamageFloaterBytes = 53, BuffUpdateBytes = 36, BuffExpireBytes = 16;
    public const int BreakPointBytes = 13, HealCastBytes = 24, HealReceiptBytes = 16, CombatStateBytes = 12;
    /// <summary>SetHealth 的固定部分：实体 8 字节、值长度 4 字节、末尾 skipUIFx 1 字节。</summary>
    public const int HealthFixedBytes = 13;
    public const int HealthMaximumValueBytes = 4096;
    public const int DeathFixedBytes = 34, MainTargetBytes = 16, BroadcastKeyBytes = 40;

    public static TwHitPresentation HitPresentation(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, HitPresentationBytes);
        var caster = U64(body, 0); var target = U64(body, 8);
        Require(caster != 0 && target != 0);
        return new(caster, target, BinaryPrimitives.ReadUInt32LittleEndian(body[16..]), U64(body, 24));
    }

    public static TwDamageFloater DamageFloater(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, DamageFloaterBytes);
        return new(U64(body, 0), U64(body, 8), BinaryPrimitives.ReadInt64LittleEndian(body[16..]), U64(body, 32));
    }

    public static TwBuffUpdate BuffUpdate(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, BuffUpdateBytes);
        var owner = U64(body, 0); var instance = U64(body, 8);
        Require(owner != 0 && instance != 0);
        return new(owner, instance, BinaryPrimitives.ReadUInt32LittleEndian(body[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(body[20..]), BinaryPrimitives.ReadUInt32LittleEndian(body[24..]), U64(body, 28));
    }

    public static TwBuffExpire BuffExpire(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, BuffExpireBytes);
        var owner = U64(body, 0); var instance = U64(body, 8);
        Require(owner != 0 && instance != 0);
        return new(owner, instance);
    }

    public static TwBreakPointUpdate BreakPoint(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, BreakPointBytes);
        var entity = U64(body, 0); var current = BinaryPrimitives.ReadSingleLittleEndian(body[8..]);
        Require(body[12] <= 1 && entity != 0 && float.IsFinite(current) && current >= 0);
        return new(entity, current);
    }

    public static TwHealCast HealCast(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, HealCastBytes);
        var target = U64(body, 0); var amount = BinaryPrimitives.ReadInt64LittleEndian(body[16..]);
        Require(target != 0 && amount >= 0);
        return new(target, U64(body, 8), amount);
    }

    public static TwHealReceipt HealReceipt(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, HealReceiptBytes);
        var amount = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
        Require(amount >= 0);
        return new(U64(body, 0), amount);
    }

    /// <summary>值长度为 8 时为明文 Int64，为 16 的正整数倍时视为加密形态；其余长度、负载与长度不符或尾字节不是 bool 均为结构错误。</summary>
    public static TwHealthUpdate Health(ReadOnlySpan<byte> body, byte encoding)
    {
        Require(encoding == 0 && body.Length >= HealthFixedBytes);
        var entity = U64(body, 0); var length = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
        Require(entity != 0 && length <= HealthMaximumValueBytes && body.Length == HealthFixedBytes + (int)length && body[^1] <= 1);
        Require(length == 8 || length != 0 && length % 16 == 0);
        if (length != 8) return new(entity, null, Encrypted: true);
        var value = BinaryPrimitives.ReadInt64LittleEndian(body[12..]);
        return new(entity, value >= 0 ? value : null);
    }

    /// <summary>已通过 <see cref="Health"/> 校验的 SetHealth 消息的值字节（明文 8 字节或密文）。</summary>
    public static ReadOnlySpan<byte> HealthValue(ReadOnlySpan<byte> body) => body.Slice(12, (int)BinaryPrimitives.ReadUInt32LittleEndian(body[8..]));

    /// <summary>
    /// SetDead 170197：实体 @0、isDead bool @8、deadTime Int64 @9、DeathReason Int32 @17、killerName（Int32 字节数加 UTF-16）@21，
    /// 其后 killer MonsterSheetId、NPCSheetId 各 4 字节与 killedByBreakAttack bool；总长 34 加名字字节数。
    /// </summary>
    public static TwDeathUpdate Death(ReadOnlySpan<byte> body, byte encoding)
    {
        Require(encoding == 0 && body.Length >= DeathFixedBytes);
        var entity = U64(body, 0); var name = BinaryPrimitives.ReadInt32LittleEndian(body[21..]);
        Require(entity != 0 && body[8] <= 1 && name >= 0 && (name & 1) == 0 && name <= 512 && body.Length == DeathFixedBytes + name && body[^1] <= 1);
        return new(entity, body[8] == 1);
    }

    /// <summary>DisappearingChannelObject 20272：8 字节实体 ID。</summary>
    public static ulong Disappearing(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, 8);
        var entity = U64(body, 0);
        Require(entity != 0);
        return entity;
    }

    public static TwMainTargetUpdate MainTarget(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, MainTargetBytes);
        var owner = U64(body, 0); var target = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
        Require(owner != 0 && target != 0 && target >= -1);
        return new(owner, target);
    }

    /// <summary>SendBroadcastPacketAES 210022：两个 uint8[]（Int32 长度加内容），依次为 16 字节 AES 密钥与 16 字节 IV。</summary>
    public static (byte[] Key, byte[] Iv) BroadcastKey(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, BroadcastKeyBytes);
        Require(BinaryPrimitives.ReadInt32LittleEndian(body) == 16 && BinaryPrimitives.ReadInt32LittleEndian(body[20..]) == 16);
        return (body.Slice(4, 16).ToArray(), body.Slice(24, 16).ToArray());
    }

    /// <summary>UpdateHealthMax 170151：以广播密钥加密的 16 字节倍数密文（明文为实体 UInt64 与最大值 Int64）。</summary>
    public static void RequireHealthMaxCipher(ReadOnlySpan<byte> body, byte encoding) =>
        Require(encoding == 0 && body.Length is > 0 and <= 64 && body.Length % 16 == 0);

    public static TwCombatStateUpdate CombatState(ReadOnlySpan<byte> body, byte encoding)
    {
        Plain(body, encoding, CombatStateBytes);
        var entity = U64(body, 0);
        Require(entity != 0);
        return new(entity, BinaryPrimitives.ReadInt32LittleEndian(body[8..]));
    }

    /// <summary>登录个人数据 20590 与角色出现 20054 只以 Brotli 形态出现，展开后至少含 8 字节前缀。</summary>
    public static void RequireCompressedPrefix(ReadOnlySpan<byte> body, byte encoding) => Require(encoding == 1 && body.Length >= 8);
    /// <summary>自动体出现 20051 只以 Brotli 形态出现。</summary>
    public static void RequireCompressed(byte encoding) => Require(encoding == 1);

    private static void Plain(ReadOnlySpan<byte> body, byte encoding, int length) => Require(encoding == 0 && body.Length == length);
    private static ulong U64(ReadOnlySpan<byte> body, int at) => BinaryPrimitives.ReadUInt64LittleEndian(body[at..]);
    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("战斗消息字段不符合当前台服布局");
    }
}
