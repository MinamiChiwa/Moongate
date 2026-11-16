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
/// <summary>SetHealth 170205：实体与明文 HP（长度 8）；加密形态时为空。</summary>
internal readonly record struct TwHealthUpdate(ulong Entity, long? PlainValue);
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
        if (length != 8) return new(entity, null);
        var value = BinaryPrimitives.ReadInt64LittleEndian(body[12..]);
        return new(entity, value >= 0 ? value : null);
    }

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
