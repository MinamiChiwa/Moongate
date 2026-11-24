using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MabiTool.Modules.CombatObservation.Domain;

/// <summary>
/// Season01 符文觉醒相关效果的识别清单（2026-11-22 tw-20261118 客户端离线核对）。
/// 判定只按 Buff Id：客户端里另有多个同名“覺醒”的任务、首领与职业效果，按名称会误报。
/// 表行 Id 由开发名决定：MD5(UTF-8 开发名) 前 4 字节小端 &amp; 0x7fffffff。字符串池里找得到开发名的项按开发名换算，
/// 当前客户端 Buff 表里有该 Id 时采用；表不可用、开发名不在表里或没有开发名时退回内置 Id。
/// </summary>
public static class CombatAwakening
{
    public const string Badge = "badge", BadgeCooldown = "badge-cooldown", Class = "class", ClassCooldown = "class-cooldown";

    /// <summary>清单中的一项：标记、内置 Id 与开发名（客户端字符串池里没有时为空）。</summary>
    public sealed record Definition(string Role, uint BuiltinId, string? DevName = null);

    public static readonly IReadOnlyList<Definition> Definitions =
    [
        // 十二个徽章觉醒效果（雨雲、首領、追蹤者、快劍、無情掠食者、破碎天空、大魔法師、飛舞之劍、山脈君主、遙遠之光、龜裂之地、熔化大地）。
        new(Badge, 2090044810, "Rune_Emblem_S2_Tier7_RapidHit_Buff"),
        new(Badge, 776688384),
        new(Badge, 1472610738),
        new(Badge, 290201663),
        new(Badge, 1147080616),
        new(Badge, 1703435864),
        new(Badge, 1913477790),
        new(Badge, 355098955),
        new(Badge, 2024838942, "Rune_Emblem_S2_Tier8_PowerfulHit_Buff"),
        new(Badge, 1590198662),
        new(Badge, 1184371696, "Rune_Emblem_S2_Tier8_AdditionalHit_Buff"),
        new(Badge, 1604065994),
        // 徽章符文冷却（90 秒，任一徽章通用；游戏自身把“获得它”当作觉醒发生）。
        new(BadgeCooldown, 315606915, "Rune_Common_S2_EmblemCooldown_Buff"),
        // 职业 Slot02 觉醒（15 秒）与冷却（60 秒）；另一组同内容的 777034889／1520277253 实际用哪组待实机确认，两组都识别。
        new(Class, 674865859, "S2_Common_Slot02_Awakening"),
        new(Class, 777034889, "Test_S2_Common_Slot02_Awakening"),
        new(ClassCooldown, 1986807074),
        new(ClassCooldown, 1520277253)
    ];

    /// <summary>只用内置 Id 的结果（名称表不可用时）。</summary>
    public static IReadOnlyDictionary<uint, string> Builtin { get; } = Resolve(null);

    /// <summary>开发名对应的表行 Id：MD5(UTF-8 开发名) 前 4 字节小端 &amp; 0x7fffffff。</summary>
    public static uint SheetId(string devName)
    {
        // 客户端用 MD5 生成表键，这里只为得到同一个键，不用于安全用途。
#pragma warning disable CA5351
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(devName));
#pragma warning restore CA5351
        return BinaryPrimitives.ReadUInt32LittleEndian(digest) & 0x7fffffff;
    }

    /// <summary>
    /// Id → 标记。<paramref name="exists"/> 为当前客户端 Buff 表的查询（为空表示表不可用）：有开发名且换算出的 Id 在表里时用它，否则用内置 Id。
    /// </summary>
    public static IReadOnlyDictionary<uint, string> Resolve(Func<uint, bool>? exists)
    {
        var result = new Dictionary<uint, string>();
        foreach (var definition in Definitions)
        {
            var id = definition.DevName is { } name && exists is not null && SheetId(name) is var derived && exists(derived) ? derived : definition.BuiltinId;
            result.TryAdd(id, definition.Role);
        }
        return result;
    }
}
