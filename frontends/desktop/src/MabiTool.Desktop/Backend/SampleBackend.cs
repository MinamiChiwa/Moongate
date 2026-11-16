using MabiTool.Desktop.Pages.Combat;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>
/// 界面开发与无界面测试使用的固定样本，以 --sample 启动时使用。
/// 界面自己的说明文字随界面语言给出；角色、技能、怪物、效果名称是游戏原文，保持繁体。
/// </summary>
public sealed partial class SampleBackend : IDesktopBackend
{
    private readonly HashSet<string> favorites = ["e1"];

    public event EventHandler? Changed;
    public event EventHandler<string>? ShortcutTriggered;

    internal void TriggerShortcut(string actionId) => ShortcutTriggered?.Invoke(this, actionId);

    public int EmergencyStopRequests { get; private set; }

    public List<(CombatIntent Intent, string? TargetId)> Intents { get; } = [];

    public ActivitySnapshot GetActivity() => Offline
        ? new(null, Pick("未连接后端", "未連接後端"), Pick("正在等待后端启动", "正在等待後端啟動"),
            Pick("连接后显示游戏输入的占用状态。", "連接後顯示遊戲輸入的佔用狀態。"),
            EffectStage.Pending, EffectStage.Pending, EffectStage.Pending, false)
        : new(null,
            Pick("没有模块持有游戏输入", "沒有模組持有遊戲輸入"),
            Pick("可以开始演奏、合奏或染色", "可以開始演奏、合奏或染色"),
            notice ?? Pick("DPS检测只读取数据，不占用输入，可与其他模块同时运行。", "DPS檢測只讀取資料，不佔用輸入，可與其他模組同時執行。"),
            EffectStage.Pending, EffectStage.Pending, EffectStage.Pending);

    public BindingView GetGameBinding()
    {
        if (Offline)
            return new(Pick("未连接后端", "未連接後端"), Pick("洛奇 Mobile 台服", "洛奇 Mobile 台服"),
                Pick("后端启动后自动连接", "後端啟動後自動連接"), false, false, Pick("未连接后端", "未連接後端"));
        var window = sampleWindows.First(item => item.Id == boundWindow);
        return new(window.Nickname ?? Pick("游戏窗口", "遊戲視窗"),
            Pick("洛奇 Mobile 台服", "洛奇 Mobile 台服"), Pick("已绑定游戏窗口", "已綁定遊戲視窗"), true);
    }

    /// <summary>测试用：后端报告的游戏窗口位置（物理像素）；默认没有。浮窗的位置与它无关。</summary>
    internal Avalonia.PixelRect? GameWindowBounds { get; set; }

    public Avalonia.PixelRect? GetGameWindowBounds() => GameWindowBounds;

    /// <summary>测试用：游戏窗口是否最小化。</summary>
    internal bool GameMinimized { get; set; }

    public bool IsGameMinimized() => GameMinimized;

    /// <summary>测试用：最近一次写进活动胶囊的前端操作结果。</summary>
    internal string? LastNotice { get; private set; }

    public void ReportNotice(string text) => LastNotice = text;

    /// <summary>测试用：让指定能力显示不可用说明；默认所有能力都可用。</summary>
    internal Func<string, string?>? CapabilityNotices { get; set; }

    public string? GetCapabilityNotice(string capabilityId) => CapabilityNotices?.Invoke(capabilityId);

    /// <summary>测试用：覆盖能力状态（例如降级）；默认有说明为不可用，否则可用。</summary>
    internal Func<string, CapabilityLevel>? CapabilityLevels { get; set; }

    public CapabilityLevel GetCapabilityLevel(string capabilityId) =>
        CapabilityLevels?.Invoke(capabilityId)
        ?? (CapabilityNotices?.Invoke(capabilityId) is not null ? CapabilityLevel.Unavailable : CapabilityLevel.Available);

    /// <summary>测试用：让指定展示主题显示缺失说明；默认所有主题都可用。</summary>
    internal Func<string, TopicNotice?>? PresentationNotices { get; set; }

    public TopicNotice? GetPresentationNotice(string topic) => PresentationNotices?.Invoke(topic);

    /// <summary>测试用：在界面线程上通知页面重读，等同后端状态变化。</summary>
    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public Task RequestEmergencyStopAsync(CancellationToken cancellationToken)
    {
        EmergencyStopRequests++;
        return Task.CompletedTask;
    }

    public Task SendCombatIntentAsync(CombatIntent intent, string? targetId, CancellationToken cancellationToken)
    {
        Intents.Add((intent, targetId));
        // 怪物效果区的目标与“选择目标”是同一个选择。
        if (intent == CombatIntent.SelectTarget) rangeTarget = string.IsNullOrEmpty(targetId) ? null : targetId;
        if (intent == CombatIntent.ToggleFavorite && targetId is not null && !favorites.Remove(targetId))
            favorites.Add(targetId);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 测试用：攻击特性与元素全部未知的形态（模拟宿主的参考回放、旧版 Worker、旧历史都是这样），
    /// 明细表不显示新增五列，攻击特性块、首击与元素构成都不出现。
    /// </summary>
    internal bool UnknownAttackTraits { get; set; }

    /// <summary>测试用：元素已知但整场没有任何元素，元素构成只显示“无元素”。</summary>
    internal bool NoElements { get; set; }

    /// <summary>测试用：后端没有治疗块（来源不支持治疗或旧记录），治疗行只显示“未提供治疗数据”。</summary>
    internal bool NoHealingData { get; set; }

    /// <summary>Buff 收藏筛选与真实后端一样在这里统一套用。</summary>
    public CombatPageData GetCombat() => CombatEffectFavorites.Apply(SampleData());

    /// <summary>测试用：自身效果再加这么多个正面效果，用来检查卡片高度不随效果数量变化。</summary>
    internal int ExtraSelfEffects { get; set; }

    private CombatPageData SampleData()
    {
        var data = WithRange(SampleCombat());
        if (NoElements) data = data with { Elements = new ElementBreakdown([]) };
        if (NoHealingData) data = data with { Healing = new HealingView(false, []), HealingTargets = [] };
        if (UnknownAttackTraits)
            return data with
            {
                Skills = data.Skills.Select(Unknown).ToList(), Targets = data.Targets.Select(Unknown).ToList(),
                Traits = [], Elements = null, ShowTraitColumns = false, FirstAttack = null,
                TagIds = ["critical", "powerful", "rapid", "double"]
            };
        return data with
        {
            Skills = data.Skills.Select(row => Tagged(row, true)).ToList(), Targets = data.Targets.Select(row => Tagged(row, true)).ToList(),
            TagIds = ["critical", "powerful", "rapid", "double", "breaked", "break", "first", "default", "multi", "additional", "bleed", "fire", "ice"]
        };

        DamageRow Unknown(DamageRow row) => Tagged(row with
        {
            Unguarded = string.Empty, BreakOccurred = string.Empty,
            DefaultAttack = string.Empty, MultiHit = string.Empty, Elements = string.Empty
        }, false);
    }

    // 样本的标签单元格：次数与占比沿用各比率列，伤害合计示例为本行伤害乘占比；攻击特性未知时为“不适用／未知”。
    private static DamageRow Tagged(DamageRow row, bool traits)
    {
        var unknown = Pick("未知", "未知");
        var notApplicable = Pick("不适用", "不適用");
        var damage = double.Parse(row.Damage.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        string[] ratios = [row.Critical, row.Strong, row.Combo, row.Chase,
            traits ? row.Unguarded : "", traits ? row.BreakOccurred : "", traits ? "4%" : "", traits ? row.DefaultAttack : "", traits ? row.MultiHit : "", traits ? "3%" : "",
            "", "", "", "", "", "", "", ""];
        var cells = new List<string>();
        foreach (var ratio in ratios)
        {
            if (ratio.Length == 0 || ratio == notApplicable) { cells.Add(notApplicable); cells.Add(unknown); continue; }
            var share = double.Parse(ratio.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100;
            cells.Add(ratio);
            cells.Add($"{Math.Round(damage * share).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} · {ratio}");
        }
        return row with { Tags = new(cells), ShareRatio = double.Parse(row.Share.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100 };
    }

    private CombatPageData SampleCombat()
    {
        var notApplicable = Pick("不适用", "不適用");
        var noElements = Pick("无元素", "無元素");
        var times = Pick("次", "次");
        // 怪物效果区的目标与“选择目标”是同一个选择；未选目标时显示正在攻击的巨魔首領。
        var effectTarget = rangeTarget ?? "boss";
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        var extraSelf = Enumerable.Range(1, ExtraSelfEffects).Select(i => new EffectRow(string.Empty, $"測試效果 {i}", string.Empty, "30s", string.Empty,
            (90000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty, EffectPolarity.Positive, RemainingMs: 30000, ObservedAt: at, Scope: "self")).ToList();
        string Element(string hans, string hant) => Pick(hans, hant);
        // 名称样例与真实后端走同一映射：客户端表里没有的效果（unknown）与战斗协议不带名字的其他玩家显示“未知…”，编号与原因在提示里。
        var unknownEffect = CombatNames.Of(new("effect", new(40217), null, "unknown", "combat.nameNotFound"), CombatNameKind.Effect);
        var otherPlayer = CombatNames.Of(null, CombatNameKind.Player, "8817324", "ce-8817324");
        return new(
            Pick("观测中，第 3 段（切换地图后开始），本场 2:31，观测完整，只读取不操作游戏",
                "觀測中，第 3 段（切換地圖後開始），本場 2:31，觀測完整，只讀取不操作遊戲"),
            [
                new(Pick("整场 DPS", "整場 DPS"), "18,420", true),
                new(Pick("最近 60 秒 DPS", "最近 60 秒 DPS"), "22,310", false),
                new(Pick("累计伤害", "累計傷害"), "2,781,420", false),
                new(Pick("命中次数", "命中次數"), "412", false)
            ],
            [
                new(Pick("暴击", "暴擊"), "32%", 0.32, $"133 / 412 {times}"),
                new(Pick("强击", "強擊"), "18%", 0.18, $"61 / 338 {times}"),
                new(Pick("连击", "連擊"), "41%", 0.41, $"139 / 338 {times}"),
                new(Pick("追击", "追擊"), "9%", 0.09, $"30 / 338 {times}")
            ],
            [
                new(Pick("本人 86%", "本人 86%"), 86, "#B23A1C", true),
                new(Pick("召唤物 9%", "召喚物 9%"), 9, "#E99A7F", true),
                new(Pick("未归属 5%，不计入本人", "未歸屬 5%，不計入本人"), 5, "#B9BECC", false)
            ],
            Pick("连携伤害保留在时间轴，不计入统计", "連攜傷害保留在時間軸，不計入統計"),
            [
                new("重擊", "1,140,382", "41%", "96", "38%", "31%", "44%", "8%", "6,210～21,480",
                    "24%", "6%", "0%", "33%", noElements),
                new("連續射擊", "750,983", "27%", "142", "30%", "12%", "52%", "11%", "2,105～9,870",
                    "18%", "3%", "71%", "52%", $"{Element("冰冻", "冰凍")} 10%"),
                new("火球術", "528,470", "19%", "58", "33%", "16%", "29%", "7%", "4,880～15,300",
                    "21%", "9%", "0%", "0%", $"{Element("火焰", "火焰")} 100%"),
                new($"火球術 ({Pick("间接", "間接")})", "203,410", "7%", "42", "26%", notApplicable, notApplicable,
                    notApplicable, "2,010～8,140", "19%", "0%", "0%", "0%", $"{Element("火焰", "火焰")} 100%"),
                new("創傷 (Dot)", "158,175", "6%", "74", notApplicable, notApplicable, notApplicable, notApplicable,
                    "1,640～2,480", notApplicable, notApplicable, notApplicable, notApplicable,
                    $"{Element("出血", "出血")} 100%")
            ],
            [
                new("巨魔首領", "1,982,640", "71%", "268", "34%", "20%", "43%", "9%", "1,640～21,480",
                    "26%", "6%", "38%", "30%",
                    $"{Element("火焰", "火焰")} 34%、{Element("出血", "出血")} 19%、{Element("冰冻", "冰凍")} 6%、{Element("其他", "其他")} 2%"),
                new("巨魔弓手", "561,300", "20%", "104", "29%", "15%", "38%", "8%", "2,105～15,300",
                    "12%", "2%", "41%", "35%", $"{Element("火焰", "火焰")} 22%、{Element("冰冻", "冰凍")} 4%"),
                new("石像", "237,480", "9%", "40", "28%", "13%", "35%", "10%", "2,010～9,870",
                    "8%", "0%", "45%", "28%", noElements)
            ],
            [
                new("吟", "戰鬥吟唱", Pick("2 层，施放者：本人，服务器时长", "2 層，施放者：本人，伺服器時長"),
                    Pick("剩 12 秒", "剩 12 秒"), Pick("覆盖 78%", "覆蓋 78%"), "1201", Polarity: EffectPolarity.Positive, Stacks: 2, RemainingMs: 12000, ObservedAt: at, Scope: "self"),
                new("迅", "迅捷", Pick($"施放者：{otherPlayer.Text}，客户端估计", $"施放者：{otherPlayer.Text}，用戶端估計"),
                    Pick("剩 4 秒", "剩 4 秒"), Pick("覆盖 46%", "覆蓋 46%"), "1304", Polarity: EffectPolarity.Positive, RemainingMs: 4000, ObservedAt: at, Scope: "self"),
                new("疲", "疲勞", Pick("施放者未知，服务器时长", "施放者未知，伺服器時長"),
                    Pick("剩 20 秒", "剩 20 秒"), string.Empty, "1410", Polarity: EffectPolarity.Negative, RemainingMs: 20000, ObservedAt: at, Scope: "self"),
                new("效", unknownEffect.Text, Pick("施放者未知，客户端估计", "施放者未知，用戶端估計"),
                    Pick("时长未知", "時長未知"), Pick("覆盖 12%", "覆蓋 12%"), "40217", unknownEffect.Tip, ObservedAt: at, Scope: "self"),
                .. extraSelf
            ],
            [
                new("boss", "巨魔首領", effectTarget == "boss", true),
                new("archer", "巨魔弓手", effectTarget == "archer", true),
                new("archer-2", "巨魔弓手", effectTarget == "archer-2", false, Pick("巨魔弓手（已不在当前观测中）", "巨魔弓手（已不在目前觀測中）"))
            ],
            [
                new("破", "破甲", Pick("3 层，施放者：本人，服务器时长", "3 層，施放者：本人，伺服器時長"),
                    Pick("剩 8 秒", "剩 8 秒"), Pick("覆盖 64%", "覆蓋 64%"), "2207", Polarity: EffectPolarity.Negative, Stacks: 3, RemainingMs: 8000, ObservedAt: at, Scope: effectTarget),
                new("緩", "緩速", Pick("施放者未知，可能是多人合计", "施放者未知，可能是多人合計"),
                    Pick("3s", "3s"), Pick("未确认移除", "未確認移除"), "2310", Polarity: EffectPolarity.Negative, RemainingMs: 2500, ObservedAt: at, Scope: effectTarget),
                new("怒", "狂暴", Pick("施放者未知，服务器时长", "施放者未知，伺服器時長"),
                    Pick("持续", "持續"), string.Empty, "2401", Polarity: EffectPolarity.Positive, ObservedAt: at, Infinite: true, Scope: effectTarget)
            ],
            [
                new("e1", Pick("今天 21:14", "今天 21:14"), $"巨魔首領{Pick(" 等 3 个目标", " 等 3 個目標")}", "4:02",
                    "DPS 17,905", Pick("观测完整", "觀測完整"), favorites.Contains("e1"),
                    Pick("第 2 段 · 切换地图", "第 2 段 · 切換地圖")),
                new("e2", Pick("今天 20:51", "今天 20:51"), "石像", "1:37", "DPS 15,220",
                    Pick("部分观测，中途断线 6 秒", "部分觀測，中途斷線 6 秒"), favorites.Contains("e2"),
                    Pick("第 1 段 · 首次伤害", "第 1 段 · 首次傷害")),
                new("e3", Pick("昨天 22:40", "昨天 22:40"), "巨魔弓手", "2:15", "DPS 14,060",
                    Pick("观测完整", "觀測完整"), favorites.Contains("e3"))
            ],
            new(false, true, true, true, true, true),
            [
                new(Pick("无防备", "無防備"), "21%", 0.21, $"87 / 412 {times}"),
                new(Pick("破防", "破防"), "5%", 0.05, $"19 / 412 {times}"),
                new(Pick("普通攻击", "普通攻擊"), "25%", 0.25, $"101 / 412 {times}"),
                new(Pick("多重打击", "多重打擊"), "26%", 0.26, $"108 / 412 {times}")
            ],
            new([
                new($"{Element("火焰", "火焰")} 25%", 103, "#E0661A"),
                new($"{Element("出血", "出血")} 18%", 74, "#D0344A"),
                new($"{Element("冰冻", "冰凍")} 3%", 14, "#2E95C8"),
                new($"{Element("触电", "觸電")} 1%", 4, "#5C70F0")
            ]),
            true,
            new FirstAttackRow("重擊", "21,480", "巨魔首領"),
            new HealingView(true,
            [
                new("HPS", "123", Pick("最近 60 秒 186", "最近 60 秒 186")),
                new(Pick("总治疗量", "總治療量"), "18,640", Pick("24 次，对自己 6,210、对他人 12,430", "24 次，對自己 6,210、對他人 12,430")),
                new(Pick("受到治疗", "受到治療"), "21,905",
                    Pick("31 次，自疗 6,210、其他来源 15,695（协议不提供来源），过量不可得：未与 HP 变化配对",
                        "31 次，自療 6,210、其他來源 15,695（協議不提供來源），過量不可得：未與 HP 變化配對"))
            ], Pick("本人施放的治疗（对自己与队友）；HPS = 施放治疗量 ÷ 统计时长 2:31", "本人施放的治療（對自己與隊友）；HPS = 施放治療量 ÷ 統計時長 2:31"),
                rangeTarget is null ? string.Empty : Pick("治疗与所选怪物无关，按整个范围统计", "治療與所選怪物無關，按整個範圍統計")),
            [
                new(otherPlayer.Text, "9,820", "53%", "11", otherPlayer.Tip),
                new(Pick("本人", "本人"), "6,210", "33%", "9"),
                new(Pick("宠物", "寵物"), "2,610", "14%", "4")
            ]);
    }
}
