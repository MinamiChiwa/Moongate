using System.Numerics;
using MabiTool.Client;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Overlay;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.Desktop.Pages.Settings;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>战斗契约对象到页面数据的映射：口径、分段、攻击特性与元素、DPS 标签、首击与效果时长。</summary>
public sealed class CombatMapperTests
{
    private static readonly ObservationSource Source = new("test", "1", "TW", "synthetic");
    private static readonly EntityKey Local = Key(1);
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static EntityKey Key(ulong id) => new("game", new(1), new(id), new(1));

    private static CombatDamageTotals Totals(long value) => new(new(new BigInteger(value)), new(0), new(0), new(0));

    private static CombatDamageGroup Group(string name, long damage, ulong hits, ulong? critical, ulong? powerful,
        ulong? criticalEligible, ulong? mainEligible, CombatDamageClassificationCounts? kinds, ulong? minimum = 10,
        ulong? maximum = 90) => new(name, name, Totals(damage), new(new BigInteger(damage)), new(hits), new(hits),
        critical is { } c ? new UInt64String(c) : null, powerful is { } p ? new UInt64String(p) : null, null, null,
        minimum is { } low ? new UInt64String(low) : null, maximum is { } high ? new UInt64String(high) : null, kinds,
        criticalEligible is { } ce ? new UInt64String(ce) : null, mainEligible is { } me ? new UInt64String(me) : null);

    private static CombatWorkspace Workspace(IReadOnlyList<CombatEntity> entities, string state = "observing")
    {
        IReadOnlyList<CombatDamageGroup> skills =
        [
            Group("重擊", 600, 100, 25, 10, 100, 100, new(new(100), new(0), new(0), new(0))),
            Group("火球術", 300, 50, 10, null, 50, null, new(new(0), new(50), new(0), new(0))),
            Group("創傷", 100, 40, null, null, null, null, new(new(0), new(0), new(40), new(0)), null, null)
        ];
        var summary = new CombatSummary("enc", Local, "琉特", new(new BigInteger(1000)), 151_000, 18420.4, 22310, 5000,
            "v1", false, "none", ObservationQuality.Confirmed, []);
        var encounter = new CombatEncounter("enc", "owner", "obs", "game", new(1), new(3), summary, Now, null, 0, null,
            true, Totals(1000), Totals(860), Totals(90), Totals(50), skills, [Group("巨魔首領", 1000, 190, 35, 10, 150, 100, null)],
            [], new(0, 0, 0, 0, new(0), new(0), new(0), new(0)), false);
        var status = new CombatObservationStatus("obs", "owner", "game", state, Source, Local, new("game"), new(3), Now,
            Now, 0, ObservationQuality.Confirmed, [], new(0));
        return new(status, encounter, entities, null, new(3));
    }

    private static CombatEntity Monster(string id, ulong raw, CombatBreakGauge? gauge, bool inScope = true) => new(id,
        Key(raw), id, id, CombatEntityRole.Monster, null, false, inScope, Now, ObservationQuality.Confirmed, gauge);

    private static CombatPageData Map(CombatWorkspace? workspace, bool connected = true, bool bound = true,
        string? target = null) => CombatMapper.Map(new(connected, bound, workspace, [], null, null, [], RangeTarget: target));

    public CombatMapperTests() => SetLanguage();

    private static void SetLanguage()
    {
        // 映射文字依界面语言取值；这些测试固定在默认的简体中文。
        if (UiText.Language != UiLanguage.SimplifiedChinese && Avalonia.Application.Current is { } application)
            UiText.Apply(application, UiLanguage.SimplifiedChinese);
    }

    [AvaloniaFact]
    public void CoverageUsesBackendEligibleCountsAsDenominators()
    {
        var data = Map(Workspace([]));
        // 暴击：重擊 25/100 加火球術 10/50；持续伤害没有暴击计数，不进分母。
        Assert.Equal("35 / 150 次", data.Coverage[0].Note);
        Assert.Equal("23%", data.Coverage[0].Percent);
        // 强击只有重擊提供计数，分母是它的主命中可计入次数。
        Assert.Equal("10 / 100 次", data.Coverage[1].Note);
        // 连击、追击没有任何技能提供计数。
        Assert.Equal("不适用", data.Coverage[2].Note);
        Assert.Equal("不适用", data.Coverage[3].Note);
    }

    [AvaloniaFact]
    public void SkillRowsMarkIndirectAndDotAndShowNotApplicable()
    {
        var rows = Map(Workspace([])).Skills;
        Assert.Equal(["重擊", "火球術 (间接)", "創傷 (Dot)"], rows.Select(row => row.Name));
        Assert.Equal(new DamageRow("重擊", "600", "60%", "100", "25%", "10%", "不适用", "不适用", "10～90",
            "不适用", "不适用", "不适用", "不适用", "不适用", "重擊"), rows[0] with { Tags = null, ShareRatio = 0 });
        Assert.Equal(0.6, rows[0].ShareRatio, 6);
        // 总表标签：暴击“占比（次数）”与带该标签的伤害；旧记录没有标签伤害时为“未知”，计数未知的标签为“不适用”。
        Assert.Equal(("25%（25 次）", "未知", "不适用", "未知"), (rows[0].TagCell(0), rows[0].TagCell(1), rows[0].TagCell(4), rows[0].TagCell(5)));
        Assert.Equal("20%", rows[1].Critical);
        Assert.Equal("不适用", rows[1].Strong);
        Assert.Equal("不适用", rows[2].Critical);
        Assert.Equal("不适用", rows[2].Range);
    }

    [AvaloniaFact]
    public void SummaryNumbersAndAttributionComeFromEncounter()
    {
        var data = Map(Workspace([]));
        // 主数据：DPS、总伤害、DPS时长、命中次数；旧记录缺少新分母时回退原时长，窗口长度取后端汇总的实际值。
        Assert.Equal(["18,420", "1,000", "2:31", "190"], data.Kpis.Select(tile => tile.Value));
        Assert.Equal(["DPS", "总伤害", "DPS时长", "命中次数"], data.Kpis.Select(tile => tile.Label));
        Assert.Equal("最近 5 秒 22,310", data.Kpis[0].Note);
        Assert.Equal(["本人 86%", "召唤物 9%", "未归属 5%，不计入本人"], data.Attribution.Select(part => part.Label));
        Assert.Contains("本场 2:31", data.Status);
        Assert.Contains("观测完整", data.Status);
    }

    [AvaloniaFact]
    public void MonsterListsShareOneStableOrderWithTheAttackedTargetFirst()
    {
        // A 0:00 首次被打、B 1:00、C 2:00；最近一次有效伤害（2:30）打在 A 上。
        CombatDamageGroup Target(string id, long damage, int first, int last) => Group(id, damage, 1, null, null, null, null, null) with
            { FirstDamageAt = Now.AddMinutes(first), LastDamageAt = Now.AddMinutes(first).AddSeconds(last) };
        IReadOnlyList<CombatDamageGroup> targets = [Target("ce-b", 900, 1, 5), Target("ce-a", 100, 0, 150), Target("ce-c", 500, 2, 1)];
        var workspace = Workspace([Monster("ce-a", 2, null), Monster("ce-b", 3, null), Monster("ce-c", 4, null, inScope: false)]);
        CombatWorkspace At(int secondsAfterLastHit) => workspace with
        {
            Encounter = workspace.Encounter! with { Targets = targets },
            Observation = workspace.Observation with { LastObservedAt = Now.AddSeconds(150 + secondsAfterLastHit) }
        };
        var fighting = Map(At(3));
        // 选择目标、明细表按目标与怪物效果区同一顺序：正在攻击的 A 置顶，其余按首次出现新的在前；不按伤害排序。
        Assert.Equal(["", "ce-a", "ce-c", "ce-b"], fighting.RangeTargets!.Select(chip => chip.Id));
        Assert.Equal(["ce-a", "ce-c", "ce-b"], fighting.TargetChips.Select(chip => chip.Id));
        Assert.Equal(["ce-a", "ce-c", "ce-b"], fighting.Targets.Select(row => row.Name));
        // 未选目标时怪物效果区显示正在攻击的目标；不在当前观测中的目标标为离开。
        Assert.True(fighting.TargetChips[0].Current);
        Assert.False(fighting.TargetChips.Single(chip => chip.Id == "ce-c").InScope);
        // 停手超过 10 秒：没有置顶目标，按首次出现排列；效果目标为排序第一个仍在观测中的怪物。
        var idle = Map(At(12));
        Assert.Equal(["ce-c", "ce-b", "ce-a"], idle.TargetChips.Select(chip => chip.Id));
        Assert.True(idle.TargetChips.Single(chip => chip.Id == "ce-b").Current);
        Assert.Equal("", idle.TargetEffectsNote);
        // 选中的目标已不在当前观测里（例如整场范围里上一张地图的怪物）：写明没有实时效果。
        Assert.Equal("目标已不在观测范围", Map(At(3), target: "ce-old").TargetEffectsNote);
        // 伤害数值变化不改变顺序。
        var grown = At(3) with { Encounter = At(3).Encounter! with { Targets = [.. targets.Select(group => group with { AttributedDamage = new(new BigInteger(5000 - (long)group.AttributedDamage.Value)) })] } };
        Assert.Equal(["ce-a", "ce-c", "ce-b"], Map(grown).TargetChips.Select(chip => chip.Id));
        Assert.Equal(TimeSpan.FromSeconds(10), CombatMapper.AttackWindow);
    }

    [AvaloniaFact]
    public void ActionsFollowConnectionBindingAndObservationState()
    {
        Assert.Equal(new CombatActions(false, false, false, false, false, false), Map(null, connected: false).Actions);
        Assert.Equal(new CombatActions(false, false, false, false, false, true), Map(null, bound: false).Actions);
        Assert.Equal(new CombatActions(true, false, false, false, false, true), Map(null).Actions);
        Assert.Equal(new CombatActions(false, true, true, true, true, true), Map(Workspace([])).Actions);
        Assert.Equal(new CombatActions(true, false, false, false, false, true), Map(Workspace([], "stopped")).Actions);
    }

    private static CombatEncounter Past(string id, CombatSegment? segment) =>
        Workspace([]).Encounter! with { Id = id, Segment = segment };

    private static CombatWorkspace WithSegment(CombatSegment? segment) =>
        Workspace([]) with { Encounter = Workspace([]).Encounter! with { Segment = segment } };

    [AvaloniaFact]
    public void StatusLineShowsSegmentBeforeEncounterClock()
    {
        Assert.Contains("观测中，第 2 段，本场 2:31", Map(WithSegment(new(2, "first-damage"))).Status);
        Assert.Contains("第 3 段（切换地图后开始），本场 2:31", Map(WithSegment(new(3, "map-change"))).Status);
        // 旧记录没有分段：状态行不出现“第 N 段”。
        var status = Map(WithSegment(null)).Status;
        Assert.DoesNotContain("段", status);
        Assert.Contains("本场 2:31", status);
    }

    private static CombatWorkspace Waiting(bool finalEncounter, params string[] reasons)
    {
        var workspace = Workspace([]);
        var encounter = finalEncounter
            ? workspace.Encounter! with { Summary = workspace.Encounter.Summary with { Final = true } }
            : null;
        return workspace with { Encounter = encounter, Observation = workspace.Observation with { Reasons = reasons } };
    }

    [AvaloniaTheory]
    [InlineData("combat.streamGap.connectionClosed")]
    [InlineData("combat.streamGap.connectionRemoved")]
    [InlineData("combat.streamGap.connectionSyn")]
    [InlineData("combat.streamGap.connectionReset")]
    [InlineData("identity-unresolved")]
    [InlineData("observation-reconnected")]
    public void StatusWaitsForIdentityAfterConnectionChange(string reason)
    {
        foreach (var final in new[] { false, true })
        {
            var status = Map(Waiting(final, reason, "combat.other")).Status;
            Assert.StartsWith("观测中，切换地图中，等待识别本机角色", status);
            Assert.DoesNotContain(reason, status);
            Assert.DoesNotContain("本场", status);
            // 其余原因照常列在质量说明里；没有文案的 combat.* 码写成“抓包缺口”，不露出原码。
            Assert.Equal("观测中，切换地图中，等待识别本机角色，观测完整（抓包缺口），只读取不操作游戏", status);
        }
    }

    [AvaloniaFact]
    public void StatusReturnsToNormalOnceIdentityIsRecovered()
    {
        Assert.DoesNotContain("切换地图中", Map(Waiting(false)).Status);
        Assert.Contains("观测中，第 3 段（切换地图后开始），本场 2:31", Map(WithSegment(new(3, "map-change"))).Status);
        // 战斗仍在进行时，同样的原因码不触发等待文案，原因照旧显示（写成短句，不露出原因码）。
        var running = Workspace([]);
        var status = Map(running with { Observation = running.Observation with { Reasons = ["identity-unresolved"] } }).Status;
        Assert.DoesNotContain("切换地图中", status);
        Assert.Contains("等待识别本机角色", status);
        Assert.DoesNotContain("identity-unresolved", status);
    }

    [AvaloniaFact]
    public void WaitingStatusFollowsTheInterfaceLanguage()
    {
        try
        {
            UiText.Apply(Avalonia.Application.Current!, UiLanguage.TraditionalChinese);
            Assert.StartsWith("觀測中，切換地圖中，等待識別本機角色", Map(Waiting(true, "identity-unresolved")).Status);
        }
        finally { SetLanguage(); }
    }

    [AvaloniaFact]
    public void UnusualSegmentValuesDoNotThrow()
    {
        var history = CombatMapper.Map(new(true, true, WithSegment(new(1, string.Empty)),
            [Past("a", new(1, string.Empty)), Past("b", new(2, "???")), Past("c", null)], null, null, [])).History;
        // 没有文案的开始原因只写“未知原因”，不显示原码。
        Assert.Equal(["第 1 段", "第 2 段 · 未知原因", ""], history.Select(row => row.Segment));
        Assert.Contains("第 1 段", Map(WithSegment(new(1, string.Empty))).Status);
        Assert.DoesNotContain("段", Map(WithSegment(null)).Status);
    }

    [AvaloniaFact]
    public void HistoryRowsNameTheSegmentAndItsStartReason()
    {
        var history = CombatMapper.Map(new(true, true, null, [
            Past("a", new(1, "first-damage")), Past("b", new(2, "manual-start")), Past("c", new(3, "reset")),
            Past("d", new(4, "map-change")), Past("e", new(5, "future-reason")), Past("f", null)], null, null, [])).History;
        Assert.Equal(["第 1 段 · 首次伤害", "第 2 段 · 手动开始", "第 3 段 · 重置", "第 4 段 · 切换地图", "第 5 段 · 未知原因", ""],
            history.Select(row => row.Segment));
        Assert.False(history[^1].HasSegment);
        Assert.True(history[0].HasSegment);
        // 分段只是后端的记录编号，没有地图名，界面不把它称作某张地图。
        Assert.DoesNotContain(history, row => row.Segment.Contains("地图名"));
    }

    [AvaloniaFact]
    public void SegmentTextFollowsTheInterfaceLanguage()
    {
        try
        {
            UiText.Apply(Avalonia.Application.Current!, UiLanguage.TraditionalChinese);
            var data = CombatMapper.Map(new(true, true, WithSegment(new(3, "map-change")),
                [Past("a", new(1, "first-damage")), Past("b", new(2, "manual-start")), Past("c", new(4, "map-change"))], null, null, []));
            Assert.Contains("第 3 段（切換地圖後開始），本場 2:31", data.Status);
            Assert.Equal(["第 1 段 · 首次傷害", "第 2 段 · 手動開始", "第 4 段 · 切換地圖"], data.History.Select(row => row.Segment));
        }
        finally { SetLanguage(); }
    }

    private static CombatWorkspace Ended(string boundaryReason, params string[] reasons)
    {
        var workspace = Workspace([]);
        var encounter = workspace.Encounter! with
        {
            Segment = new(2, "first-damage"),
            Summary = workspace.Encounter.Summary with { Final = true, BoundaryReason = boundaryReason }
        };
        return workspace with { Encounter = encounter, Observation = workspace.Observation with { Reasons = reasons } };
    }

    [AvaloniaFact]
    public void LocalCombatStateDrivesTheActivityAndCombatLeftWaitsForDamage()
    {
        var left = Ended("combat-left");
        var data = Map(left with { Observation = left.Observation with { LocalCombatState = CombatLocalState.NonCombat } });
        Assert.StartsWith("观测中，已脱离战斗，等待下一次伤害开始新分段，上一段为第 2 段，本场 2:31", data.Status);
        Assert.NotEmpty(data.Skills);
        Assert.Equal("战斗外", data.Location!.Activity);
        var fighting = Workspace([]);
        Assert.Equal("战斗中", Map(fighting with { Observation = fighting.Observation with { LocalCombatState = CombatLocalState.InCombat } }).Location!.Activity);
        // 没有该字段时沿用分段规则：已结束的分段为空闲。
        Assert.Equal("空闲", Map(left).Location!.Activity);
        var history = CombatMapper.Map(new(true, true, null, [left.Encounter! with { Segment = new(3, "combat-entered") }], null, null, [])).History;
        Assert.Equal(["第 3 段 · 脱战后再战 · 结束：脱离战斗"], history.Select(row => row.Segment));
    }

    [AvaloniaFact]
    public void EndedSegmentStaysWholeUntilTheNextSegmentStarts()
    {
        // 脱战后、下一笔伤害之前：快照里仍是刚结束的第 2 段，主数据、攻击特性、明细全部取自它，状态行不出现新分段序号。
        var data = Map(Ended("combat-left"));
        Assert.Equal(["DPS", "总伤害", "DPS时长", "命中次数"], data.Kpis.Select(tile => tile.Label));
        Assert.Equal("1,000", data.Kpis[1].Value);
        Assert.DoesNotContain(data.Kpis, tile => tile.Value == "—");
        Assert.Equal(4, data.Coverage.Count);
        Assert.NotEmpty(data.Skills); Assert.NotEmpty(data.Targets);
        Assert.Equal("上一段（脱离战斗）", data.Scope!.Title);
        Assert.DoesNotContain("段（", data.Status);
        Assert.Equal("上一段（停手超时）", Map(Ended("idle-timeout")).Scope!.Title);
        Assert.StartsWith("观测中，停手超时，上一段已结束，等待下一次伤害开始新分段，上一段为第 2 段", Map(Ended("idle-timeout")).Status);

        // 新分段的第一笔伤害到达：整页切到第 3 段。
        var next = Workspace([]);
        var started = next with { Encounter = next.Encounter! with { Id = "enc-3", Segment = new(3, "combat-entered"),
            Summary = next.Encounter.Summary with { EncounterId = "enc-3", AttributedDamage = new(new System.Numerics.BigInteger(50)) } } };
        var live = Map(started);
        Assert.Equal("50", live.Kpis[1].Value);
        Assert.Equal(string.Empty, live.Scope!.Title);
        Assert.Contains("第 3 段（脱战后开始）", live.Status);
    }

    [AvaloniaFact]
    public void StatusWaitsForDamageAfterManualSplit()
    {
        var data = Map(Ended("manual-split"));
        Assert.StartsWith("观测中，已拆段，等待下一次伤害开始新分段，上一段为第 2 段，本场 2:31", data.Status);
        // 刚结束分段的数值继续显示；拆段与结束本场停用，重置仍可用。
        Assert.NotEmpty(data.Skills);
        Assert.Equal(new CombatActions(false, true, false, false, true, true), data.Actions);

        // 空闲后自动结束的分段不是拆段的过渡态。
        var idle = Map(Ended("idle-timeout"));
        Assert.DoesNotContain("已拆段", idle.Status);
        Assert.False(idle.Actions.CanSplit);

        // 拆段后先切换地图：显示切换地图的等待文案。
        var switching = Map(Ended("manual-split", "identity-unresolved")).Status;
        Assert.StartsWith("观测中，切换地图中，等待识别本机角色", switching);
        Assert.DoesNotContain("已拆段", switching);

        // 观测停止后不再等待。
        var stopped = Ended("manual-split");
        Assert.DoesNotContain("已拆段", Map(stopped with { Observation = stopped.Observation with { State = "stopped" } }).Status);
    }

    [AvaloniaFact]
    public void ManualSplitNamesTheNewSegment()
    {
        Assert.Contains("观测中，第 4 段（手动拆段后开始），本场 2:31", Map(WithSegment(new(4, "manual-split"))).Status);
        Assert.True(Map(WithSegment(new(4, "manual-split"))).Actions.CanSplit);
        var history = CombatMapper.Map(new(true, true, null, [Past("a", new(4, "manual-split"))], null, null, [])).History;
        Assert.Equal(["第 4 段 · 手动拆段"], history.Select(row => row.Segment));
        Assert.Equal("当前没有进行中的分段", LiveBackend.SplitRefusal("combat.segmentInactive"));
        Assert.Equal("分段状态已变化，请再试一次", LiveBackend.SplitRefusal("revision.stale"));
        Assert.Null(LiveBackend.SplitRefusal("auth.forbidden"));
        Assert.Null(LiveBackend.SplitRefusal(null));
        try
        {
            UiText.Apply(Avalonia.Application.Current!, UiLanguage.TraditionalChinese);
            Assert.StartsWith("觀測中，已拆段，等待下一次傷害開始新分段", Map(Ended("manual-split")).Status);
            Assert.Contains("第 4 段（手動拆段後開始）", Map(WithSegment(new(4, "manual-split"))).Status);
            Assert.Equal("目前沒有進行中的分段", LiveBackend.SplitRefusal("combat.segmentInactive"));
        }
        finally { SetLanguage(); }
    }

    [AvaloniaFact]
    public void DisconnectedStateShowsPlaceholdersInsteadOfZeros()
    {
        var data = Map(null, connected: false);
        Assert.All(data.Kpis, tile => Assert.Equal("—", tile.Value));
        Assert.Empty(data.Coverage);
        Assert.Empty(data.Skills);
        Assert.Contains("未连接后端", data.Status);
    }

    private static UInt64String? Count(ulong? value) => value is { } known ? new UInt64String(known) : null;

    private static CombatDamageElementCounts Elements(ulong bleed = 0, ulong dark = 0, ulong fire = 0, ulong holy = 0,
        ulong ice = 0, ulong lightning = 0, ulong poison = 0, ulong soul = 0) =>
        new(new(bleed), new(dark), new(fire), new(holy), new(ice), new(lightning), new(poison), new(soul));

    // 攻击特性与元素按 eventCount 计算；这里让事件数与命中数不同，确认分母取的是事件数。
    private static CombatDamageGroup Traits(CombatDamageGroup group, ulong events, ulong? unguarded, ulong? breakOccurred,
        ulong? first, ulong? normal, ulong? multi, CombatDamageElementCounts? elements) => group with
    {
        EventCount = new(events), BreakedHitCount = Count(unguarded), BreakOccurredCount = Count(breakOccurred),
        FirstAttackCount = Count(first), DefaultAttackCount = Count(normal), MultiHitCount = Count(multi), ElementCounts = elements
    };

    private static CombatWorkspace WithGroups(IReadOnlyList<CombatDamageGroup> skills, IReadOnlyList<CombatDamageGroup>? targets = null)
    {
        var workspace = Workspace([]);
        return workspace with
        {
            Encounter = workspace.Encounter! with { Skills = skills, Targets = targets ?? workspace.Encounter.Targets }
        };
    }

    private static CombatWorkspace TraitWorkspace() => WithGroups(
    [
        Traits(Group("重擊", 600, 90, 25, 10, 100, 100, null), 100, 25, 5, 10, 0, null, Elements(fire: 62, ice: 10)),
        Traits(Group("火球術", 300, 0, null, null, null, null, null), 0, 0, 0, 0, 0, 0, Elements()),
        Traits(Group("創傷", 100, 40, null, null, null, null, new(new(0), new(0), new(40), new(0))), 40, 8, null, null, 40, 12,
            Elements())
    ]);

    [AvaloniaFact]
    public void AttackTraitColumnsUseEventCountAndShowNotApplicable()
    {
        var data = Map(TraitWorkspace());
        Assert.True(data.ShowTraitColumns);
        var heavy = data.Skills.Single(row => row.Name == "重擊");
        // 分母是 eventCount 100，不是命中数 90。
        Assert.Equal(("25%", "5%", "0%", "不适用"),
            (heavy.Unguarded, heavy.BreakOccurred, heavy.DefaultAttack, heavy.MultiHit));
        Assert.Equal("火焰 62%、冰冻 10%", heavy.Elements);
        // 分母为 0：全部“不适用”，不显示 0%。
        var empty = data.Skills.Single(row => row.Name.StartsWith("火球術"));
        Assert.All(new[] { empty.Unguarded, empty.BreakOccurred, empty.DefaultAttack, empty.MultiHit, empty.Elements },
            text => Assert.Equal("不适用", text));
        // 持续伤害也计入攻击特性；已知但没有元素显示“无元素”。
        var dot = data.Skills.Single(row => row.Name == "創傷 (Dot)");
        Assert.Equal(("20%", "不适用", "100%", "30%"),
            (dot.Unguarded, dot.BreakOccurred, dot.DefaultAttack, dot.MultiHit));
        Assert.Equal("无元素", dot.Elements);
    }

    [AvaloniaFact]
    public void ElementsAreSortedAndTheRestIsMergedIntoOther()
    {
        var data = Map(WithGroups([Traits(Group("連續射擊", 100, 100, null, null, null, null, null), 100, null, null, null, null,
            null, Elements(fire: 5, ice: 30, holy: 20, dark: 10, soul: 3))]));
        Assert.Equal("冰冻 30%、神圣 20%、暗黑 10%、其他 8%", data.Skills[0].Elements);
        // 次数相同时按固定顺序（出血、暗黑、火焰、神圣、冰冻、触电、毒、精神）。
        var ties = Map(WithGroups([Traits(Group("連續射擊", 100, 100, null, null, null, null, null), 100, null, null, null, null,
            null, Elements(soul: 10, lightning: 10, bleed: 10, poison: 10, ice: 10))]));
        Assert.Equal("出血 10%、冰冻 10%、触电 10%、其他 20%", ties.Skills[0].Elements);
        // 列表以外的元素原样显示。
        Assert.Equal("wind", CombatMapper.ElementName("wind"));
        Assert.Equal("wind 40%、火焰 10%", CombatMapper.ElementSummary([("wind", 4), ("fire", 1)], 10));
        Assert.Equal("无元素", CombatMapper.ElementSummary([("fire", 0)], 10));
        Assert.Equal("不适用", CombatMapper.ElementSummary(null, 10));
        Assert.Equal("不适用", CombatMapper.ElementSummary([("fire", 0)], 0));
    }

    [AvaloniaFact]
    public void TraitTilesSumSkillsWithKnownCounts()
    {
        var data = Map(TraitWorkspace());
        // 首击不算比例，不在攻击特性块里。
        Assert.Equal(["无防备", "破防", "普通攻击", "多重打击", "追加打击"], data.Traits!.Select(tile => tile.Name));
        // 无防备：25 + 0 + 8，分母 100 + 0 + 40。
        Assert.Equal(("24%", "33 / 140 次"), (data.Traits![0].Percent, data.Traits[0].Note));
        // 破防：重擊 5 与火球術 0，分母 100 + 0。
        Assert.Equal(("5%", "5 / 100 次"), (data.Traits[1].Percent, data.Traits[1].Note));
        Assert.Equal(("29%", "40 / 140 次"), (data.Traits[2].Percent, data.Traits[2].Note));
        // 多重打击：只有創傷有计数。
        Assert.Equal(("30%", "12 / 40 次"), (data.Traits[3].Percent, data.Traits[3].Note));

        var elements = Assert.IsType<ElementBreakdown>(data.Elements);
        // 整场分母是三组 eventCount 之和 140。
        Assert.Equal(["火焰 44%", "冰冻 7%"], elements.Parts.Select(part => part.Label));
        Assert.Equal([62d, 10d], elements.Parts.Select(part => part.Weight));
        Assert.Equal(CombatMapper.ElementColor("fire"), elements.Parts[0].ColorHex);
    }

    [AvaloniaFact]
    public void TraitTileWithZeroDenominatorIsNotApplicable()
    {
        var data = Map(WithGroups([Traits(Group("火球術", 300, 0, null, null, null, null, null), 0, 0, null, null, null, null,
            Elements())]));
        Assert.Equal("不适用", data.Traits![0].Note);
        Assert.Equal("—", data.Traits[0].Percent);
        Assert.Equal("不适用", data.Traits[1].Note);
        Assert.True(Assert.IsType<ElementBreakdown>(data.Elements).None);
    }

    [AvaloniaFact]
    public void AllUnknownHidesTheColumnsTilesAndElementBar()
    {
        var data = Map(Workspace([]));
        Assert.False(data.ShowTraitColumns);
        Assert.Empty(data.Traits!);
        Assert.Null(data.Elements);
        // 只有目标分组有值时同样显示五列。
        var targetOnly = Map(WithGroups(Workspace([]).Encounter!.Skills,
            [Traits(Group("巨魔首領", 1000, 190, 35, 10, 150, 100, null), 190, 3, null, null, null, null, null)]));
        Assert.True(targetOnly.ShowTraitColumns);
        Assert.Empty(targetOnly.Traits!);
        Assert.Equal("2%", targetOnly.Targets[0].Unguarded);
    }

    [AvaloniaFact]
    public void AttackTraitTextFollowsTheInterfaceLanguage()
    {
        try
        {
            UiText.Apply(Avalonia.Application.Current!, UiLanguage.TraditionalChinese);
            var data = Map(TraitWorkspace());
            Assert.Equal(["無防備", "破防", "普通攻擊", "多重打擊", "追加打擊"], data.Traits!.Select(tile => tile.Name));
            Assert.Equal("無元素", data.Skills.Single(row => row.Name == "創傷 (Dot)").Elements);
            Assert.Equal("不適用", data.Skills.Single(row => row.Name == "重擊").MultiHit);
            Assert.Equal("神聖", CombatMapper.ElementName("holy"));
            Assert.Equal("冰凍 30%、神聖 20%、暗黑 10%、其他 8%",
                CombatMapper.ElementSummary([("fire", 5), ("ice", 30), ("holy", 20), ("dark", 10), ("soul", 3)], 100));
        }
        finally { SetLanguage(); }
    }

    // 元素色块在两套主题的面板上都要能分辨：与面板实际底色的对比不低于 3:1。
    [Fact]
    public void ElementColorsStandOutOnBothPanels()
    {
        static double Luminance(Avalonia.Media.Color color)
        {
            static double Channel(byte value)
            {
                var c = value / 255d;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        static Avalonia.Media.Color Over(Avalonia.Media.Color top, double alpha, Avalonia.Media.Color bottom) => Avalonia.Media.Color.FromRgb(
            (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)), (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
            (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));
        static double Contrast(Avalonia.Media.Color a, Avalonia.Media.Color b)
        {
            var (x, y) = (Luminance(a), Luminance(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        var white = Avalonia.Media.Colors.White;
        var light = Over(white, 0.62, Avalonia.Media.Color.Parse("#EEF0F6"));
        var dark = Over(white, 0.07, Avalonia.Media.Color.Parse("#0F1220"));
        var colors = new[] { "bleed", "dark", "fire", "holy", "ice", "lightning", "poison", "soul" }.Select(CombatMapper.ElementColor).ToList();
        Assert.Equal(8, colors.Distinct().Count());
        foreach (var hex in colors)
        {
            var color = Avalonia.Media.Color.Parse(hex);
            Assert.True(Contrast(color, light) >= 3, $"{hex} 在浅色面板上对比 {Contrast(color, light):F2}");
            Assert.True(Contrast(color, dark) >= 3, $"{hex} 在深色面板上对比 {Contrast(color, dark):F2}");
        }
    }

    private static CombatEffectView Effect(ulong id, EffectDurationKind kind, double? remaining) => new(
        new(Local, new(id), new(id), Local, Now, null, kind, null, null, EffectPresence.Observed, ObservationQuality.Confirmed, new(1)),
        remaining, Now, false, false);

    [AvaloniaFact]
    public void EffectDurationShowsFiniteInfiniteAndUnknown()
    {
        var workspace = Workspace([]);
        var effects = new CombatEntityEffects("self", "owner", "obs", new(1),
            new(Local, true, [], Now, ObservationQuality.Confirmed),
            [Effect(1, EffectDurationKind.Finite, 12_300), Effect(2, EffectDurationKind.Infinite, null),
                Effect(3, EffectDurationKind.Unknown, null), Effect(4, EffectDurationKind.Finite, null)]);
        var rows = Map(workspace with { SelfEffects = effects }).SelfEffects;
        Assert.Equal(["13s", "持续", "未知", "未知"], rows.Select(row => row.Remaining));
        // 契约只有效果编号：名称显示“未知效果”并带效果编号，同名的未知效果分得开；原因在悬停提示里。
        Assert.Equal(["未知效果 #1", "未知效果 #2", "未知效果 #3", "未知效果 #4"], rows.Select(row => row.Name));
        Assert.Equal("未知效果（编号 1）", rows[0].Tip);
        try
        {
            UiText.Apply(Avalonia.Application.Current!, UiLanguage.TraditionalChinese);
            Assert.Equal(["13s", "持續", "未知", "未知"],
                Map(workspace with { SelfEffects = effects }).SelfEffects.Select(row => row.Remaining));
        }
        finally { SetLanguage(); }
    }

    [AvaloniaFact]
    public void DpsLabelsUseTheBackendWindowAndHistorySaysDps()
    {
        var workspace = Workspace([]);
        var sixty = workspace with
        {
            Encounter = workspace.Encounter! with { Summary = workspace.Encounter.Summary with { RollingWindowMs = 60_000 } }
        };
        Assert.StartsWith("最近 60 秒 ", Map(sixty).Kpis[0].Note);
        // 还没有本场数据时取观察设置里的窗口长度。
        var waiting = workspace with
        {
            Encounter = null,
            Observation = workspace.Observation with { Settings = new("game", RollingWindowMs: 10_000) }
        };
        Assert.Equal(["DPS", "总伤害"], Map(waiting).Kpis.Take(2).Select(tile => tile.Label));
        Assert.Equal(["—", "—"], Map(waiting).Kpis.Take(2).Select(tile => tile.Value));

        var unknown = Past("u", null) with { Summary = Past("u", null).Summary with { EncounterDps = null } };
        var history = CombatMapper.Map(new(true, true, null, [Past("a", null), unknown], null, null, [])).History;
        Assert.Equal(["DPS 18,420", "DPS 未知"], history.Select(row => row.Dps));
        try
        {
            UiText.Apply(Avalonia.Application.Current!, UiLanguage.TraditionalChinese);
            Assert.Equal(["DPS", "總傷害"], Map(sixty).Kpis.Take(2).Select(tile => tile.Label));
            Assert.Equal("DPS 未知", CombatMapper.Map(new(true, true, null, [unknown], null, null, [])).History[0].Dps);
        }
        finally { SetLanguage(); }
    }

    [AvaloniaFact]
    public void ResolvedNamesShowClientTextAndUnknownOrLoadingNamesKeepIdAndReasonInTheTip()
    {
        static CombatResolvedName Name(string kind, ulong id, string? text, string state, string? reason = null) => new(kind, new(id), text, state, reason);
        var workspace = Workspace([Monster("boss", 7, null) with { ResolvedName = Name("monster", 202498206, "深層棕毛鼠", "resolved") }]);
        workspace = workspace with
        {
            Encounter = workspace.Encounter! with
            {
                Targets = [Group("boss", 1000, 190, null, null, null, null, null)],
                Skills =
                [
                    Group("1320061445", 600, 10, null, null, null, null, null) with { ResolvedName = Name("skill", 1320061445, "召喚連結者", "resolved") },
                    Group("20101", 300, 5, null, null, null, null, null) with { ResolvedName = Name("skill", 20101, null, "unknown", "combat.nameNotFound") },
                    Group("30202", 100, 2, null, null, null, null, null) with { ResolvedName = Name("skill", 30202, null, "unavailable", "combat.nameCatalogLoading") }
                ]
            },
            SelfEffects = new("self", "owner", "obs", new(1), new(Local, true, [], Now, ObservationQuality.Confirmed),
                [Effect(134623881, EffectDurationKind.Finite, 5_000) with { ResolvedName = Name("effect", 134623881, "召喚連結者", "resolved") },
                    Effect(5, EffectDurationKind.Finite, 5_000) with { ResolvedName = Name("effect", 5, null, "unavailable", "game.bindingRequired") }])
        };
        var data = Map(workspace);
        // resolved 显示客户端原文、不带编号；unknown 显示“未知技能 #表 ID”，读取中显示“名称读取中 #表 ID”，提示写出表 ID 与原因；分组的原始标识不当名称。
        Assert.Equal(["召喚連結者", "未知技能 #20101", "名称读取中 #30202"], data.Skills.Select(row => row.Name));
        Assert.Equal(["召喚連結者", "未知技能（编号 20101，客户端名称表里没有这一项）", "名称读取中（编号 30202，正在读取客户端名称表）"],
            data.Skills.Select(row => row.Tip));
        Assert.Equal(("召喚連結者", "召喚連結者"), (data.SelfEffects[0].Name, data.SelfEffects[0].Tip));
        Assert.Equal(("未知效果 #5", "未知效果（编号 5，尚未绑定游戏窗口，无法读取名称表）"), (data.SelfEffects[1].Name, data.SelfEffects[1].Tip));
        Assert.Equal("深層棕毛鼠", Assert.Single(data.TargetChips).Label);
    }

    private static CombatDamageEvent FirstHit(string id, ulong skill, ulong amount, ulong target, double at) =>
        new(id, Source, Local, Key(target), new(skill), new(amount), CombatDamageKind.ActualHp, Now, at, null, 0,
            false, true, ObservationQuality.Confirmed, FirstAttack: true);

    [AvaloniaFact]
    public void OnlyTheLatestFirstAttackIsShownWithSkillDamageAndTarget()
    {
        var workspace = Workspace([Monster("boss", 7, null)]);
        workspace = workspace with
        {
            Encounter = workspace.Encounter! with
            {
                Skills = [.. workspace.Encounter.Skills, Group("重擊", 10, 1, null, null, null, null, null) with { Key = "1001" }]
            }
        };
        // 只取最近的一次；没有分组名称时显示“未知技能 #技能编号”，目标没有观测到时不写。
        var data = CombatMapper.Map(new(true, true, workspace, [], null, null, [],
            [FirstHit("a", 1001, 21480, 7, 100), FirstHit("b", 2002, 9870, 99, 900)]));
        Assert.Equal(new FirstAttackRow("未知技能 #2002", "9,870", "", "未知技能（编号 2002）"), data.FirstAttack);
        // 技能取分组名称，目标取观测到的名称；事件顺序不影响取最近的一次。
        Assert.Equal(new FirstAttackRow("重擊", "21,480", "boss"), CombatMapper.Map(new(true, true, workspace, [], null, null, [],
            [FirstHit("a", 2002, 9870, 99, 100), FirstHit("b", 1001, 21480, 7, 900), FirstHit("c", 2002, 1, 99, 400)])).FirstAttack);
        // 首击计数不再进入攻击特性块与明细表。
        Assert.DoesNotContain(data.Traits ?? [], tile => tile.Name == "首击");

        // 事件未知、查不到或本场没有首击时没有这一行。
        Assert.Null(Map(workspace).FirstAttack);
        Assert.Null(CombatMapper.Map(new(true, true, workspace, [], null, null, [], [])).FirstAttack);
    }
    [AvaloniaFact]
    public void EffectsAreSplitIntoPositiveNegativeAndUnclassifiedRowsByTheBackendPolarity()
    {
        var workspace = Workspace([]);
        var effects = new CombatEntityEffects("self", "owner", "obs", new(1), new(Local, true, [], Now, ObservationQuality.Confirmed),
            [Effect(1, EffectDurationKind.Finite, 5_000) with { Polarity = "buff" }, Effect(2, EffectDurationKind.Finite, 5_000) with { Polarity = "debuff" },
                Effect(3, EffectDurationKind.Finite, 5_000) with { Polarity = "none" }, Effect(4, EffectDurationKind.Finite, 5_000)]);
        var rows = Map(workspace with { SelfEffects = effects }).SelfEffects;
        // 极性只取后端给出的 Buff 表 AffectType；没有时为未知，不推测。
        Assert.Equal([EffectPolarity.Positive, EffectPolarity.Negative, EffectPolarity.Neutral, EffectPolarity.Unknown], rows.Select(row => row.Polarity));
        Assert.Equal([0d, 180d, 0d, 0d], rows.Select(row => row.Angle));

        // 页面分三行：正面、负面、未分类（表里标为无与没有来源的都在这一行）。
        var backend = new SampleBackend();
        using var model = new CombatPageModel(backend);
        Assert.Equal(["戰鬥吟唱", "迅捷"], model.SelfPositive.All.Select(item => item.Row.Name));
        Assert.Equal(["疲勞"], model.SelfNegative.All.Select(item => item.Row.Name));
        Assert.Equal(["未知效果 #40217"], model.SelfOther.All.Select(item => item.Row.Name));
        Assert.Equal(["破甲", "緩速"], model.TargetNegative.All.Select(item => item.Row.Name));
        Assert.Equal(["狂暴"], model.TargetPositive.All.Select(item => item.Row.Name));
        Assert.True(model.TargetOther.Empty);
    }

    [AvaloniaFact]
    public void EffectFavoritesFilterThePageAndTheOverlayFromOneState()
    {
        CombatEffectFavorites.Use(null);
        try
        {
            var backend = new SampleBackend();
            using var page = new CombatPageModel(backend);
            using var overlay = new MabiTool.Desktop.Overlay.OverlayWidgetModel("combat.self-effects", backend);
            // 观测到的效果进入“已提取到的 Buff”目录，按名称或编号都能找到。
            Assert.Equal("1201", Assert.Single(CombatEffectFavorites.Search("戰鬥")).Id);
            Assert.Equal("戰鬥吟唱", Assert.Single(CombatEffectFavorites.Search("1201")).Name);

            CombatEffectFavorites.Toggle("1201");
            CombatEffectFavorites.SetSelfOnly(true);
            Assert.True(page.SelfFavoritesOnly);
            Assert.Equal(["戰鬥吟唱"], page.SelfPositive.All.Select(item => item.Row.Name));
            Assert.True(page.SelfNegative.Empty);
            Assert.Equal(["1201"], overlay.SelfEffects.Select(row => row.EffectId));
            // 只作用于自身效果卡片；怪物效果另有自己的开关。
            Assert.Equal(3, page.Data.TargetEffects.Count);
            Assert.True(Assert.Single(page.FavoriteCatalog, entry => entry.Id == "1201").Favorite);

            // 浮窗的效果元件跟随“只显示收藏”：关闭后列出全部效果，收藏的排在最前。
            CombatEffectFavorites.SetSelfOnly(false);
            Assert.Equal(4, overlay.SelfEffects.Count);
            Assert.Equal("1201", overlay.SelfEffects[0].EffectId);
            Assert.False(overlay.SelfFavoriteHint);
        }
        finally { CombatEffectFavorites.Use(null); }
    }

    [AvaloniaFact]
    public void CurrentTargetHealthShowsEachStateWithoutZeroForUnknownValues()
    {
        static CombatCurrentTarget Target(string? state, ulong? current, ulong? maximum, string? reason = null) =>
            SampleBackend.SampleTarget("ce-a", 0, null, "深層雷霆光靈") with
            {
                Health = state is null ? null : new(current is { } c ? new(c) : null, maximum is { } m ? new(m) : null, Now, state, reason),
                IsBoss = null
            };
        TargetHealthView Show(CombatCurrentTarget? target, string? unavailable = null) =>
            CombatMapper.Map(new(true, true, Workspace([]) with { CurrentTarget = target }, [], null, null, [], HealthUnavailable: unavailable)).Health!;

        // 已读且有最大值：整数计算的一位小数百分比与比例，不超过 30% 用警示色。
        var read = Show(Target("read", 6_170_000, 61_700_000));
        Assert.Equal(("深層雷霆光靈", "10.0%", "6,170,000 / 61,700,000", HealthTone.Low), (read.Name, read.Percent, read.Value, read.Tone));
        Assert.Equal(0.1, read.Fill, 6);
        Assert.Equal(HealthTone.Normal, Show(Target("read", 30_001, 100_000)).Tone);
        Assert.Equal("30.0%", Show(Target("read", 29_999, 100_000)).Percent);
        // Int64 范围的大数：数字逐位显示，百分比与比例不经 double 除法。
        var huge = Show(Target("read", 4_611_686_018_427_387_905, 9_223_372_036_854_775_807));
        Assert.Equal(("50.0%", "4,611,686,018,427,387,905 / 9,223,372,036,854,775,807"), (huge.Percent, huge.Value));
        Assert.Equal(0.5, huge.Fill, 6);
        Assert.Equal("100.0%", Show(Target("read", 9_223_372_036_854_775_807, 9_223_372_036_854_775_807)).Percent);

        // 最大值未知、未读取、血量由首领体现：不给比例、不写 0。
        var noMaximum = Show(Target("read", 1_234_567, null, "combat.healthMaximumUnknown"));
        Assert.Equal(("最大值未知", "1,234,567", HealthTone.Empty), (noMaximum.Percent, noMaximum.Value, noMaximum.Tone));
        var unread = Show(Target("unread", null, 61_700_000, "combat.healthUnread"));
        Assert.Equal(("—", "未读取", HealthTone.Empty), (unread.Percent, unread.Value, unread.Tone));
        Assert.Contains("重新进入怪物视野", unread.Tip);
        Assert.Equal("未读取", Show(Target(null, null, null)).Value);
        var shared = Show(Target("shared", 500, 1_000, "combat.healthShared"));
        Assert.Equal(("—", "血量由首领体现", HealthTone.Empty), (shared.Percent, shared.Value, shared.Tone));
        foreach (var view in new[] { noMaximum, unread, shared })
        {
            Assert.DoesNotContain("0%", view.Percent);
            Assert.NotEqual("0", view.Value);
        }

        // 死亡为 0%；过期照常显示并带标记；改指首领时带首领与同场首领标记。
        var dead = Show(Target("read", 12, 1_000) with { Dead = true });
        Assert.Equal(("0.0%", "0 / 1,000", 0d), (dead.Percent, dead.Value, dead.Fill));
        Assert.True(Show(Target("read", 1, 2) with { Health = new(new(1), new(2), Now, "read", Stale: true) }).Stale);
        var redirected = Show(Target("read", 1, 2) with { IsBoss = true, RedirectedFrom = "ce-black" });
        Assert.True(redirected.Boss && redirected.Redirected);

        // 能力停用只给停用原因；没有当前目标时为空，观测中写“当前没有攻击目标”。
        var disabled = Show(Target("read", 1, 2), "暂不可用：客户端版本不支持");
        Assert.Equal("怪物血量暂不可用：客户端版本不支持", disabled.Disabled);
        Assert.False(disabled.IsTarget);
        var idle = CombatMapper.Map(new(true, true, Workspace([]), [], null, null, []));
        Assert.Null(idle.Health);
        Assert.True(idle.HealthIdle);
        Assert.Null(CombatMapper.Map(new(true, true, Workspace([], "stopped") with { CurrentTarget = Target("read", 1, 2) }, [], null, null, [])).Health);
        // 战斗能力表里的停用并入能力表，说明与其他能力同一规则。
        var capabilities = LiveBackend.WithCombatCapabilities([], new(false,
            [new(CapabilityIds.MonsterHealth, CapabilityStatus.Unavailable, Source, "combat.buildUnsupported", null, null)]));
        Assert.Equal("暂不可用：客户端版本不支持", CapabilityNotice.Describe(capabilities!.Single()));

        // 新的名称种类：AI 同伴为固定文案带实体编号，客户端名称为空的怪物为未知目标并写明原因。
        var companion = CombatNames.Of(new("ai-companion", new(0), null, "generic", "combat.nameGeneric"), CombatNameKind.Target, number: "77");
        Assert.Equal("AI 同伴 #77", companion.Text);
        var empty = CombatNames.Of(new("monster", new(762736525), null, "unknown", "combat.nameEmpty"), CombatNameKind.Target, number: "9");
        Assert.Equal("未知目标 #9", empty.Text);
        Assert.Contains("客户端名称为空", empty.Tip);
    }
}
