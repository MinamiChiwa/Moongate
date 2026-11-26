// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Buffers.Binary;

namespace MabiTool.Providers.TwObservation.Inventory;

/// <summary>登录数据里定位到的钱包与补充时间；读不到的部分为空并给出原因。</summary>
/// <param name="Values">钱包字段（<see cref="WalletProtocol.Fields"/> 的键，深渊副本货币为 <see cref="WalletProtocol.AbyssPrefix"/> 加类型名）→ 数值。</param>
/// <param name="SilverCoinRefilledAt">根字段 <c>SilverCoin.SilverCoinRefillData.LastUpdateTime</c>（Unix 毫秒）。</param>
/// <param name="PassDeepRefilledAt">根字段 <c>PassDeep.LastUpdatedTime</c>（Unix 毫秒）。</param>
/// <param name="Memberships">根字段 <c>Membership.ActivatedMemberships</c>（与补充时间同一段，随补充时间一起定位）；该段没有定位到或布局里没有这个字段时为空，定位到而没有会员时为空列表。</param>
/// <param name="MasterCharacterId">根字段 <c>AccountProfile.MasterCharacterId</c>（账户主角色 ID，与补充时间同一段读出）；该段没有定位到或值为 0 时为空。</param>
public sealed record LoginWallet(IReadOnlyDictionary<string, ulong>? Values, DateTimeOffset? SilverCoinRefilledAt, DateTimeOffset? PassDeepRefilledAt,
    string? WalletReason, string? RefillReason, IReadOnlyList<LoginMembership>? Memberships = null, ulong? MasterCharacterId = null);

/// <summary>登录数据里的一项会员：<c>MembershipSheetId</c> 与到期时刻（<c>ExpireTime</c>，Unix 毫秒；0 时为空）。</summary>
public sealed record LoginMembership(uint SheetId, DateTimeOffset? ExpiresAt);

/// <summary>定位结果：钱包与补充时间，以及钱包段之前紧邻的 <c>JamItemInventory</c>（读不到时为空并给出原因）。</summary>
internal sealed record LocatedLogin(LoginWallet Wallet, InventoryWireValue? JamItems, string? JamReason);

/// <summary>
/// 在登录个人数据（20590）里定位钱包（第 83 个根字段 <c>Wallet</c>）、银币与魔族貢品的补充时间（第 145、146 个根字段）和 <c>JamItemInventory</c>（第 68 个根字段）。
/// <para>
/// 台服 2026-11 构建的登录数据按客户端元数据顺序读到第 48 个根字段 <c>Quest</c> 为止（见 <see cref="InventoryWireReader.RootPrefix"/>），
/// 其后的任务、剧情字段含客户端元数据看不出的序列化规则，顺序读取到不了钱包。
/// 钱包所在的一段（<c>Wallet</c> 到 <c>FollowCameraForceAngle</c>，13 个根字段）和补充时间所在的一段（<c>SilverCoin</c> 到
/// <c>AccountProfile</c> 之后，约 60 KiB、60 多个根字段）各自能按元数据完整读通，因此按“候选位置 + 整段类型化读取”定位：
/// </para>
/// <list type="bullet">
/// <item>钱包：22 个 int64 都在 [0, 10^12]、深渊副本货币字典项数 ≤ 10 且键不重复，并且从该处起 <c>Wallet</c> 到 <c>FollowCameraForceAngle</c>
/// 全部读通。只有一个位置读通时直接采用；多个位置读通时取技能栏 <c>SkillSlot</c> 至少一项的那一个；都没有技能（新角色）时，
/// 取其前面恰好接着读通 <c>JamItemInventory</c> 到 <c>SkillReserve</c>、止于该位置的那一个（全零的钱包容易在错位处也读通）。</item>
/// <item>补充时间：两个 RealTime（Unix 毫秒）为 0 或在 2020–2040 年之间且不同时为 0，并且从该处起 <c>SilverCoin</c> 读到 <c>AccountProfile</c> 的下一个根字段全部读通。</item>
/// <item>JamItem：钱包定位后，在钱包之前 8 KiB 内找从该处起 <c>JamItemInventory</c> 到 <c>SkillReserve</c>（钱包前一个根字段）全部读通、且恰好止于钱包起点的位置。
/// 非空的 JamItem 字典末尾常是数量的高 4 字节 0，从那里读成“空字典”也能止于钱包起点；因此多个位置满足时，其余位置都落在最前一个位置读出的
/// <c>JamItemInventory</c> 内部才取最前一个，否则按多于一个处理。</item>
/// </list>
/// 每段都要求在停止处之后恰好一个位置满足；没有或多于一个时该部分为空（未读取），不给出猜测值。
/// 台服 2026-11-15 留存实采六份共 9 次登录数据中各段都只有一个位置满足，钱包数值与其后的 Change 消息、增减通知连续，
/// 补充时间与 171115 连续。
/// </summary>
internal static class LoginWalletLocator
{
    private const ulong ValueLimit = 1_000_000_000_000;
    private const long TimeFloor = 1_577_836_800_000, TimeCeiling = 2_208_988_800_000;
    /// <summary>每段最多做完整类型化读取的候选数；超出时按读不到处理，避免异常数据拖长解码。</summary>
    private const int CandidateBudget = 4096;
    /// <summary>钱包段读通的位置最多记下的个数；超过时按技能栏非空的位置取舍。</summary>
    private const int WalletCandidateLimit = 64;
    /// <summary>JamItem 段的搜索窗口：实采中 <c>JamItemInventory</c> 到钱包之间 846–912 字节。</summary>
    private const int JamWindow = 8 * 1024;

    public static LocatedLogin Locate(byte[] body, InventoryWireSchema schema, string rootType, int from, CancellationToken cancellation)
    {
        var root = schema.Type(rootType).Fields;
        int Index(string name) => Array.FindIndex(root, field => field.Name == name);
        int wallet = Index("Wallet"), followCamera = Index("FollowCameraForceAngle"), skillSlot = Index("SkillSlot");
        int silver = Index("SilverCoin"), passDeep = Index("PassDeep"), profile = Index("AccountProfile"), jam = Index("JamItemInventory");
        var membership = Index("Membership"); IReadOnlyList<LoginMembership>? memberships = null; ulong? masterCharacter = null;
        IReadOnlyDictionary<string, ulong>? values = null; string? walletReason = "inventory.walletLayoutUnsupported";
        DateTimeOffset? silverAt = null, passAt = null; string? refillReason = "inventory.walletLayoutUnsupported";
        var walletAt = -1;
        if (wallet >= 0 && followCamera > wallet && skillSlot > wallet && skillSlot < followCamera &&
            root[wallet].Type is { } walletType && schema.Type(walletType).Fields.Length == 23)
        {
            // 读通的位置先全部记下；技能栏非空只用来在多个位置之间取舍（新角色的技能栏可能为空）。
            var found = new List<(int Offset, InventoryWireValue Value, bool Skills)>(); var tested = 0;
            for (var at = from; at <= body.Length - 180 && found.Count(item => item.Skills) < 2 && found.Count < WalletCandidateLimit; at++)
            {
                if ((at & 0xfff) == 0) cancellation.ThrowIfCancellationRequested();
                if (!WalletShape(body, at)) continue;
                if (++tested > CandidateBudget) { found.Clear(); walletReason = "inventory.walletCandidateBudget"; break; }
                var reader = new InventoryWireReader(body, schema, cancellation);
                reader.Seek(at);
                try
                {
                    InventoryWireValue? captured = null; var skills = false;
                    for (var i = wallet; i <= followCamera; i++)
                    {
                        var value = reader.Value(root[i].Type, 1, i == wallet || i == skillSlot);
                        if (i == wallet) captured = value;
                        if (i == skillSlot) skills = value.Field("SkillSlots")?.Pairs is { Count: > 0 };
                    }
                    found.Add((at, captured!, skills));
                }
                catch (InvalidDataException) { }
            }
            var chosen = found.Count == 1 ? found : found.Where(item => item.Skills).ToList();
            // 多个位置读通且都没有技能（新角色）：取前面恰好接着读通 JamItemInventory 到 SkillReserve、止于该位置的那一个。
            if (chosen.Count == 0 && found.Count > 1 && jam >= 0 && jam < wallet)
                chosen = found.Where(item => JamCandidates(body, schema, root, jam, wallet, from, item.Offset, cancellation).Count > 0).ToList();
            if (chosen.Count == 1) { walletAt = chosen[0].Offset; values = Values(chosen[0].Value); walletReason = values is null ? "inventory.walletInvalid" : null; }
            else if (walletReason != "inventory.walletCandidateBudget") walletReason = found.Count == 0 ? "inventory.walletNotFound" : "inventory.walletAmbiguous";
        }
        if (silver >= 0 && passDeep == silver + 1 && profile > passDeep && profile + 1 < root.Length)
        {
            var found = new List<(int Offset, InventoryWireValue? Membership, InventoryWireValue? Profile)>(); var tested = 0;
            for (var at = Math.Max(from, walletAt); at <= body.Length - 16 && found.Count < 2; at++)
            {
                if ((at & 0xfff) == 0) cancellation.ThrowIfCancellationRequested();
                long first = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(at)), second = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(at + 8));
                if (!Time(first) || !Time(second) || first == 0 && second == 0) continue;
                if (++tested > CandidateBudget) { found.Clear(); refillReason = "inventory.walletCandidateBudget"; break; }
                var reader = new InventoryWireReader(body, schema, cancellation);
                reader.Seek(at);
                try
                {
                    InventoryWireValue? captured = null, account = null;
                    for (var i = silver; i <= profile + 1; i++)
                    {
                        var value = reader.Value(root[i].Type, 1, i == membership || i == profile);
                        if (i == membership) captured = value;
                        if (i == profile) account = value;
                    }
                    found.Add((at, captured, account));
                }
                catch (InvalidDataException) { }
            }
            if (found.Count == 1)
            {
                long first = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(found[0].Offset)), second = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(found[0].Offset + 8));
                silverAt = first == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(first);
                passAt = second == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(second);
                refillReason = null;
                memberships = Memberships(body, found[0].Membership);
                masterCharacter = found[0].Profile?.Field("MasterCharacterId")?.Unsigned() is { } master and > 0 and <= long.MaxValue ? master : null;
            }
            else if (refillReason != "inventory.walletCandidateBudget") refillReason = found.Count == 0 ? "inventory.refillNotFound" : "inventory.refillAmbiguous";
        }
        InventoryWireValue? jamItems = null; string? jamReason = walletAt < 0 ? "inventory.walletNotFound" : "inventory.walletLayoutUnsupported";
        if (walletAt >= 0 && jam >= 0 && jam < wallet)
        {
            var found = JamCandidates(body, schema, root, jam, wallet, from, walletAt, cancellation);
            // 其余位置都在最前一个读出的 JamItemInventory 之内：是从真字典尾部读成的空字典，取最前一个。
            if (found.Count is > 0 and < 16 && found.Skip(1).All(value => value.Start > found[0].Start && value.Start < found[0].End)) { jamItems = found[0]; jamReason = null; }
            else jamReason = found.Count == 0 ? "inventory.jamItemNotFound" : "inventory.jamItemAmbiguous";
        }
        return new(new(values, silverAt, passAt, walletReason, refillReason, memberships, masterCharacter), jamItems, jamReason);
    }

    /// <summary>钱包起点之前 <see cref="JamWindow"/> 内，从该处起 <c>JamItemInventory</c> 到钱包前一个根字段全部读通、且恰好止于钱包起点的位置（至多 16 个）。</summary>
    private static List<InventoryWireValue> JamCandidates(byte[] body, InventoryWireSchema schema, (string Name, string? Type)[] root, int jam, int wallet,
        int from, int walletAt, CancellationToken cancellation)
    {
        var found = new List<InventoryWireValue>();
        for (var at = Math.Max(from, walletAt - JamWindow); at < walletAt && found.Count < 16; at++)
        {
            if ((at & 0xfff) == 0) cancellation.ThrowIfCancellationRequested();
            var reader = new InventoryWireReader(body, schema, cancellation);
            reader.Seek(at);
            try
            {
                InventoryWireValue? captured = null;
                for (var i = jam; i < wallet; i++)
                {
                    var value = reader.Value(root[i].Type, 1, i == jam);
                    if (i == jam) captured = value;
                }
                if (reader.Offset == walletAt && PlausibleJam(captured!)) found.Add(captured!);
            }
            catch (InvalidDataException) { }
        }
        return found;
    }

    /// <summary>
    /// JamItem 字典的每一项表编号为正、数量在 [0, 10^12]。错位处也可能整段读通并止于钱包起点（台服 2026-11-25 构建的重新登录样本里，
    /// 最前一个候选读出 4 项负编号、负数量），这类位置不算候选。
    /// </summary>
    private static bool PlausibleJam(InventoryWireValue jam)
    {
        foreach (var field in jam.Fields?.Values ?? [])
            foreach (var (key, value, _) in field.Pairs ?? [])
                if (key.Unsigned() is not > 0 || value.Unsigned() is not { } amount || amount > ValueLimit) return false;
        return true;
    }

    /// <summary>会员字典：键为 MembershipSheetId（按线上 4 字节读成无符号），值里的 ExpireTime 为 Unix 毫秒；形状不符时整体为空（未知）。</summary>
    private static IReadOnlyList<LoginMembership>? Memberships(byte[] body, InventoryWireValue? value)
    {
        if (value?.Field("ActivatedMemberships")?.Pairs is not { } pairs) return null;
        var result = new List<LoginMembership>();
        foreach (var (key, info, _) in pairs)
        {
            if (key.End - key.Start != 4 || info.Field("ExpireTime") is not { } expire || expire.End - expire.Start != 8) return null;
            var ms = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(expire.Start));
            if (!Time(ms)) return null;
            result.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(key.Start)), ms == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms)));
        }
        return result;
    }

    private static bool Time(long value) => value == 0 || value is >= TimeFloor and <= TimeCeiling;

    /// <summary>22 个 int64 都在范围内，其后的深渊副本货币字典项数 0–10。</summary>
    private static bool WalletShape(byte[] body, int at)
    {
        for (var k = 0; k < 22; k++)
        {
            var value = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(at + k * 8));
            if (value < 0 || (ulong)value > ValueLimit) return false;
        }
        var count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at + 176));
        return count is >= 0 and <= 10;
    }

    private static IReadOnlyDictionary<string, ulong>? Values(InventoryWireValue wallet)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (key, _, _) in WalletProtocol.Fields)
        {
            if (wallet.Field(key)?.Unsigned() is not { } value || value > ValueLimit) return null;
            result[key] = value;
        }
        if (wallet.Field("AbyssRaidMoneySet")?.Pairs is { } pairs)
            foreach (var (key, value, _) in pairs)
            {
                if (key.Unsigned() is not { } type || type >= (ulong)WalletProtocol.AbyssRaidTypes.Count || value.Unsigned() is not { } amount || amount > ValueLimit) return null;
                result[WalletProtocol.AbyssPrefix + WalletProtocol.AbyssRaidTypes[(int)type]] = amount;
            }
        return result;
    }
}
