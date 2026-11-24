using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Inventory;

/// <summary>
/// 银币、魔族貢品的补充时刻：上次补充时间与客户端表的补充间隔（秒）。<see cref="Full"/> 为真表示持有量已达到或超过后端给出的补充上限，
/// 游戏不再补充，不倒数。
/// </summary>
public sealed record InventoryRefill(DateTimeOffset LastRefilledAt, double IntervalSeconds, bool Full = false);

/// <summary>
/// 背包页一项资源的一行。<see cref="Observed"/> 为假的行只出现在“未读取”折叠或概况的未读取行里，<see cref="Detail"/> 是未读取的原因。
/// <see cref="Tip"/> 是悬停提示：最近变化与时间、最近一次更新的角色、补充时间、客户端说明原文等。
/// <see cref="Account"/> 为真的行属于“账户资源”组，否则属于 <see cref="CharacterId"/> 角色；<see cref="Summary"/> 为真的行（金币、银币、魔族貢品）显示在“概况”卡片。
/// <see cref="Refill"/> 有值时该行带补充倒计时（<see cref="InventoryRefillText"/>）。
/// </summary>
public sealed record InventoryResourceRow(string Key, string Name, string Value, string Detail, string Tip, bool Observed,
    bool Account = false, string? CharacterId = null, bool Summary = false, InventoryRefill? Refill = null);

/// <summary>
/// 资源视图换成背包页的行（账户级与各角色的角色级，归属范围由后端按客户端 AccountWallet 结构给出）。每项一行：名称与当前值，其余放进悬停提示。
/// 金币按角色合成一行（免费 + 付费），与银币、魔族貢品标为概况行；迁移时找不到所属角色的旧值放在账户组并注明。
/// 货币名称是游戏原文（台服繁体），简体界面按 <see cref="Simplify"/> 换成对应简体字；客户端表读不到名称时以字段名显示。
/// </summary>
public static class InventoryResourceText
{
    /// <summary>概况卡片的三项，按显示顺序。</summary>
    public static readonly string[] SummaryKeys = ["goldTotal", "silverCoin", "passDeep"];

    /// <param name="characterNames">账户下的角色编号 → 名称，用于注明最近一次更新的角色；没有时不注明。</param>
    public static IReadOnlyList<InventoryResourceRow> Rows(InventoryResourcesView? view, IReadOnlyDictionary<string, string>? characterNames = null)
    {
        if (view is null) return [];
        var rows = new List<InventoryResourceRow>();
        foreach (var item in view.Items)
        {
            if (item.Key is "goldFree" or "goldPaid" && item.Scope == InventoryResourceScope.Character && item.CharacterId is not null)
            {
                if (item.Key == "goldFree") rows.Add(Gold(view, item.CharacterId));
                continue;
            }
            var name = Name(item);
            var orphan = item.Reason == "inventory.resourceOwnerUnknown";
            var account = item.Scope == InventoryResourceScope.Account || orphan;
            if (orphan) name += UiText.Pick("（角色未知的历史值）", "（角色未知的歷史值）");
            var summary = !account && item.Key is "silverCoin" or "passDeep";
            var full = item is { Value: { } held, RefillCap: { } cap } && held.Value >= (ulong)cap;
            var refill = item.RefillIntervalSeconds is { } seconds and > 0 && (full || item.LastRefilledAt is not null)
                ? new InventoryRefill(item.LastRefilledAt ?? default, seconds, full) : null;
            if (item.State != "observed" || item.Value is not { } value)
            {
                rows.Add(new(item.Key, name, UiText.Pick("未读取", "未讀取"), Unread(item.Reason), Tip(name, item, null), false, account, item.CharacterId, summary));
                continue;
            }
            rows.Add(new(item.Key, name, value.Value.ToString("N0", CultureInfo.InvariantCulture), string.Empty,
                Tip(name, item, account && item.ObservedCharacterId is { Length: > 0 } who ? characterNames?.GetValueOrDefault(who) : null),
                true, account, item.CharacterId, summary, refill));
        }
        return rows;
    }

    /// <summary>
    /// 概况的三行（金币、银币、魔族貢品），取自 <paramref name="rows"/> 里 <paramref name="characterId"/> 角色的概况行；
    /// 没有该角色或资源视图未读取时给出未读取行，三行始终都在，卡片高度不变。
    /// </summary>
    public static IReadOnlyList<InventoryResourceRow> Summary(IEnumerable<InventoryResourceRow> rows, string? characterId)
    {
        var mine = rows.Where(row => row.Summary && row.CharacterId == characterId).ToDictionary(row => row.Key, StringComparer.Ordinal);
        return SummaryKeys.Select(key => mine.GetValueOrDefault(key) ?? new InventoryResourceRow(key, key switch
        {
            "goldTotal" => UiText.Pick("金币", "金幣"),
            "silverCoin" => UiText.Pick("银币", "銀幣"),
            _ => UiText.Pick("魔族贡品", "魔族貢品")
        }, UiText.Pick("未读取", "未讀取"), Unread(null), UiText.Pick("还没有读到该角色的资源", "還沒有讀到該角色的資源"), false, false, characterId, true)).ToList();
    }

    /// <summary>金币一行：免费与付费都读到时为两者之和，悬停分列两项；任一项未读取时整行未读取，悬停写明已读到的那一项。</summary>
    private static InventoryResourceRow Gold(InventoryResourcesView view, string character)
    {
        var free = view.Items.FirstOrDefault(item => item.Key == "goldFree" && item.CharacterId == character);
        var paid = view.Items.FirstOrDefault(item => item.Key == "goldPaid" && item.CharacterId == character);
        var name = Simplify(free?.RawName ?? paid?.RawName ?? "金幣");
        string Part(InventoryResourceView? item, string label) => label + "：" + (item?.State == "observed" && item.Value is { } value
            ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : UiText.Pick("未读取", "未讀取"));
        var parts = Part(free, UiText.Pick("免费", "免費")) + "　" + Part(paid, UiText.Pick("付费", "付費"));
        var latest = new[] { free, paid }.Where(item => item?.ObservedAt is not null).MaxBy(item => item!.ObservedAt);
        var tip = string.Join("\n", new[] { name, parts }.Concat(latest is null ? [] : Lines(latest)).Where(line => line.Length > 0));
        if (free?.Value is { } a && paid?.Value is { } b && free.State == "observed" && paid.State == "observed")
            return new("goldTotal", name, (a.Value + b.Value).ToString("N0", CultureInfo.InvariantCulture), string.Empty, tip, true, false, character, true);
        return new("goldTotal", name, UiText.Pick("未读取", "未讀取"), Unread((free?.State != "observed" ? free : paid)?.Reason), tip, false, false, character, true);
    }

    /// <summary>未读取的原因一句。</summary>
    private static string Unread(string? reason) => reason switch
    {
        "inventory.identityUnresolved" => UiText.Pick("背包身份尚未确认", "背包身分尚未確認"),
        "inventory.walletUnread" => UiText.Pick("登录数据中没有定位到钱包，等下次换频道或进图", "登入資料中沒有定位到錢包，等下次換頻道或進圖"),
        "inventory.resourceOwnerUnknown" => UiText.Pick("所属角色未知", "所屬角色未知"),
        null => UiText.Pick("等待换频道或进图时同步", "等待換頻道或進圖時同步"),
        var code => code
    };

    private static string Tip(string name, InventoryResourceView item, string? updatedBy) => string.Join("\n",
        new[] { name }.Concat(Lines(item)).Append(updatedBy is null ? string.Empty : UiText.Pick($"最近一次由 {updatedBy} 更新", $"最近一次由 {updatedBy} 更新"))
            .Append(item.State != "observed" ? Unread(item.Reason) : string.Empty).Where(line => line.Length > 0));

    /// <summary>悬停提示的明细行：最近变化与时间、补充时间、说明原文、核实说明。</summary>
    private static IEnumerable<string> Lines(InventoryResourceView item)
    {
        var at = item.ObservedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var change = item.LastIncrease is { } up ? "+" + up.Value.ToString("N0", CultureInfo.InvariantCulture)
            : item.LastDecrease is { } down ? "−" + down.Value.ToString("N0", CultureInfo.InvariantCulture) : null;
        if (at is not null)
            yield return (item.Current ? UiText.Pick($"更新于 {at}", $"更新於 {at}") : UiText.Pick($"上次已知 {at}", $"上次已知 {at}"))
                         + (change is null ? string.Empty : UiText.Pick($"，最近变化 {change}", $"，最近變化 {change}"));
        if (item.LastRefilledAt is { } last)
            yield return UiText.Pick($"上次补充 {last.ToLocalTime():MM-dd HH:mm:ss}", $"上次補充 {last.ToLocalTime():MM-dd HH:mm:ss}");
        if (item.RefillIntervalSeconds is { } seconds and > 0)
        {
            if (item.RefillCap is { } cap)
            {
                yield return UiText.Pick($"补充上限 {cap:N0}：达到或超过上限时不再补充", $"補充上限 {cap:N0}：達到或超過上限時不再補充");
                yield return UiText.Pick($"低于上限时每 {Duration(seconds)} 补充一次；倒计时到零后标“待进图”，游戏在下次进图时结算",
                    $"低於上限時每 {Duration(seconds)} 補充一次；倒數到零後標「待進圖」，遊戲在下次進圖時結算");
            }
            else if (item.LastRefilledAt is not null)
                yield return UiText.Pick($"每 {Duration(seconds)} 补充一次；倒计时到零后标“待进图”：未满时游戏在下次进图时结算，已满时不再增加（登录数据未能确定会员状态时不显示上限）",
                    $"每 {Duration(seconds)} 補充一次；倒數到零後標「待進圖」：未滿時遊戲在下次進圖時結算，已滿時不再增加（登入資料未能確定會員狀態時不顯示上限）");
        }
        if (item.Description is { Length: > 0 } text) yield return Simplify(text);
        if (!item.Verified) yield return UiText.Pick("该资源的变化消息未经样本核实", "該資源的變化訊息未經樣本核實");
    }

    private static string Duration(double seconds) => seconds % 3600 == 0
        ? UiText.Pick($"{seconds / 3600:0} 小时", $"{seconds / 3600:0} 小時")
        : UiText.Pick($"{seconds / 60:0} 分钟", $"{seconds / 60:0} 分鐘");

    private static string Name(InventoryResourceView item)
    {
        // 游戏原文里只有 M幣(免費) 自带半角括号，与界面追加的全角“（免费）／（付费）”统一成全角。
        var raw = item.RawName is { Length: > 0 } name ? Simplify(name).Replace('(', '（').Replace(')', '）') : item.Key;
        var suffix = item.Key switch
        {
            "goldFree" or "wecaFree" or "decaFree" => UiText.Pick("（免费）", "（免費）"),
            "goldPaid" or "wecaPaid" or "decaPaid" or "mCashPaid" => UiText.Pick("（付费）", "（付費）"),
            _ => string.Empty
        };
        return raw + suffix;
    }

    private const string Traditional = "幣銀貢會愛點數組討證遠團礦區淵變異紅韋計條無擴欄場費層時進於購買貨廣爾為圖暫發現處這種紋樣嗎蟲長與從經歷動機開關獲須參將該來個們對後裡麼號寶庫間領單價總額錢誌雜戰鬥舊彈夠緊雖魚鳥龍麗齊歲傳說語讀寫談請謝給綠藍黃銅鐵鑽劍師練習獎勵賽隊員級階際陣線輪車馬飛塊剛獸靈証險";
    private const string Simplified = "币银贡会爱点数组讨证远团矿区渊变异红韦计条无扩栏场费层时进于购买货广尔为图暂发现处这种纹样吗虫长与从经历动机开关获须参将该来个们对后里么号宝库间领单价总额钱志杂战斗旧弹够紧虽鱼鸟龙丽齐岁传说语读写谈请谢给绿蓝黄铜铁钻剑师练习奖励赛队员级阶际阵线轮车马飞块刚兽灵证险";

    /// <summary>简体界面把游戏原文里的繁体字换成对应简体字（只含货币名称与说明里出现的字）；繁体界面原样返回。</summary>
    public static string Simplify(string text)
    {
        if (UiText.Language == UiLanguage.TraditionalChinese) return text;
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var index = Traditional.IndexOf(chars[i]);
            if (index >= 0) chars[i] = Simplified[index];
        }
        return new string(chars);
    }
}

/// <summary>
/// 补充倒计时的文字。游戏按“上次补充时间 + n × 补充间隔”补充，但只在进图等时机结算并下发新值（实采：银币上次补充 07:12:49，
/// 08:42 进图时一次 +2，上次补充时间移到 08:12:49）。所以到零后接着倒数下一次，并标“待进图”；
/// 结算后后端给出新的上次补充时间，倒计时随之重算。持有量达到或超过补充上限（<see cref="InventoryRefill.Full"/>）时写“已满”，不倒数；
/// 后端只在能确定会员状态时给出上限（没有会员为基础上限，全方位会员生效时含会员加成；会员未读取或无法判定时为空），为空时不判断是否已满。
/// </summary>
public static class InventoryRefillText
{
    /// <summary>此刻的倒计时文字与已到而未结算的补充次数。</summary>
    public static (string Text, int Pending) Countdown(InventoryRefill? refill, DateTimeOffset now)
    {
        if (refill is not { IntervalSeconds: > 0 } value) return (string.Empty, 0);
        if (value.Full) return (UiText.Pick("已满", "已滿"), 0);
        var interval = value.IntervalSeconds;
        var elapsed = Math.Max(0, (now - value.LastRefilledAt).TotalSeconds);
        var pending = (int)Math.Min(int.MaxValue, Math.Floor(elapsed / interval));
        var remaining = Math.Ceiling(interval - (elapsed - pending * interval));
        var span = TimeSpan.FromSeconds(Math.Max(0, remaining));
        var time = interval >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes:00}:{span.Seconds:00}");
        return (pending > 0 ? UiText.Pick($"待进图 {time}", $"待進圖 {time}") : time, pending);
    }
}
