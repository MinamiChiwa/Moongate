using System.Numerics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Combat;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>
/// 范围、目标、定时测量与常驻观测到页面数据的映射：后端重新统计的结果原样显示，未知与不完整写明，不以 0 代替。
/// </summary>
public sealed class CombatRangeMapperTests
{
    private static readonly ObservationSource Source = new("test", "1", "TW", "synthetic");
    private static readonly EntityKey Local = new("game", new(1), new(1), new(1));
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static CombatDamageTotals Totals(long value) => new(new(new BigInteger(value)), new(0), new(0), new(0));
    private static CombatDamageGroup Group(string key, string name, long damage, ulong hits, ulong? breaked = null, long? breakDamage = null) =>
        new(key, name, Totals(damage), new(new BigInteger(damage)), new(hits), new(hits), null, null,
            BreakedHitCount: breaked is { } b ? new UInt64String(b) : null, BreakedHitDamage: breakDamage is { } d ? new DamageAmountString(new BigInteger(d)) : null);

    private static CombatEncounter Encounter(string id, long damage, double durationMs, IReadOnlyList<CombatDamageGroup> targets, double? dps,
        CombatRecordInfo? record = null) => new(id, "owner", "obs", "game", new(1), new(3),
        new(id, Local, "琉特", new(new BigInteger(damage)), durationMs, dps, null, 60000, "v1", true, "none",
            ObservationQuality.Confirmed, []), Now, Now, 0, durationMs, true, Totals(damage), Totals(damage), Totals(0), Totals(0),
        [Group("1000", "重擊", damage, 10, 3, 600)], targets, [], new(0, 0, 0, 0, new(0), new(0), new(0), new(0)), false, Record: record);

    private static CombatWorkspace Workspace(CombatEncounter? encounter, string state = "observing") => new(
        new("obs", "owner", "game", state, Source, Local, new("game"), new(3), Now, Now, 0, ObservationQuality.Confirmed, [], new(0)),
        encounter, [], null, new(3));

    private static CombatRangeSummary Range(CombatEncounter result, string? target, bool complete = true, params string[] reasons) => new(
        "observed-range-v1", "observation", "obs", [result.Id], 0, result.Summary.DurationMs, target, target is null ? null : "巨魔弓手",
        result, Group("total", "total", (long)result.Summary.AttributedDamage.Value, 10, 3, 600),
        [Group("ce-a", "巨魔弓手", 300, 3), Group("ce-b", "巨魔弓手", 700, 7)], new(new BigInteger(1000)), 1000, 9000,
        new(new(new BigInteger(600)), new(3), new(new BigInteger(200)), null, null, null, null, ["break-interval-unobserved"]), complete, reasons);

    public CombatRangeMapperTests()
    {
        if (UiText.Language != UiLanguage.SimplifiedChinese && Avalonia.Application.Current is { } application)
            UiText.Apply(application, UiLanguage.SimplifiedChinese);
    }

    [AvaloniaFact]
    public void TargetChipsKeepSameNameIndividualsApartAndTheDetailShowsMyContribution()
    {
        var live = Encounter("enc", 1000, 10000, [], 100);
        var filtered = Encounter("enc", 300, 10000, [Group("ce-a", "巨魔弓手", 300, 3)], 30);
        var data = CombatMapper.Map(new(true, true, Workspace(live), [], null, null, [], Scope: "observation", RangeTarget: "ce-a",
            Range: Range(filtered, "ce-a")));
        Assert.Equal(["全部", "巨魔弓手", "巨魔弓手"], data.RangeTargets!.Select(chip => chip.Name));
        // 名称缺失（后端沿用原始编号作名称）时显示“未知目标 #实体编号”，同名的未知目标因此分得开。
        var unnamed = CombatMapper.Map(new(true, true, Workspace(Encounter("enc", 1000, 10000, [Group("ce-c", "4021740217", 1000, 3)], 100)),
            [], null, null, [], Scope: "segment"));
        Assert.Equal(("未知目标 #4021740217", "未知目标（编号 4021740217）"), (unnamed.RangeTargets![1].Name, unnamed.RangeTargets[1].Tip));
        Assert.Equal("未知目标 #4021740217", unnamed.Targets[0].Name);
        Assert.Equal(["1,000", "700", "300"], data.RangeTargets!.Select(chip => chip.Damage));
        Assert.True(data.RangeTargets!.Single(chip => chip.Id == "ce-a").Current);
        Assert.False(data.RangeTargets![0].Current);
        Assert.Equal(("巨魔弓手", "300", "30%"), (data.TargetDetail!.Name, data.TargetDetail.Damage, data.TargetDetail.Contribution));
        // 主数据取后端按目标重新统计的结果；时长块为对目标时长（后端没有给出时回退范围时长），下方注明范围时长。
        Assert.Equal(["30", "300", "0:10", "10"], data.Kpis.Select(tile => tile.Value));
        Assert.Equal(("对目标时长", "范围时长 0:10"), (data.Kpis[2].Label, data.Kpis[2].Note));
        // 破防：伤害、次数与每击平均来自后端；持续时间等未观测，显示未知而不是 0。
        Assert.Equal(("600", "3", "200", "未知", "未知"), (data.Break!.Damage, data.Break.Count, data.Break.Average, data.Break.Dps, data.Break.Duration));
    }

    [AvaloniaFact]
    public void IncompleteRangesSayWhyAndKeepDpsUnknown()
    {
        var result = Encounter("enc", 1000, 10000, [], null);
        var data = CombatMapper.Map(new(true, true, Workspace(result), [], null, null, [], Scope: "observation",
            Range: Range(result, null, false, "connection-changed", "timeline-incomplete")));
        Assert.Equal("不完整：期间切换了地图、超出事件保留范围", data.Scope!.Incomplete);
        Assert.Equal("—", data.Kpis[0].Value);
        Assert.Equal("不完整，DPS 未知", data.Kpis[0].Note);
        // 后端在不完整时仍给出 DPS：照常显示数值，下方写明不完整与原因。
        var partial = Encounter("enc", 1000, 10000, [], 100);
        var shown = CombatMapper.Map(new(true, true, Workspace(partial), [], null, null, [], Scope: "observation",
            Range: Range(partial, null, false, "combat.frameInvalid")));
        Assert.Equal(("100", "不完整：抓包缺口"), (shown.Kpis[0].Value, shown.Kpis[0].Note));
        // 当前分段且不选目标时直接用分段摘要，不受上一次范围结果影响。
        var segment = CombatMapper.Map(new(true, true, Workspace(Encounter("enc", 1000, 10000, [], 100)), [], null, null, [], Scope: "segment",
            Range: Range(result, null, false, "connection-changed")));
        Assert.Equal("", segment.Scope!.Incomplete);
        Assert.Equal("100", segment.Kpis[0].Value);
    }

    [AvaloniaFact]
    public void MeasurementScopeUsesTheMeasuredDpsAndListsSavedMeasurements()
    {
        var measured = new CombatMeasurementResult("timed-measurement-v1", 60000, 5000, new(new BigInteger(1200000)), 20000, 500, 58500, 58000,
            false, ["connection-changed"], Now, Now.AddMinutes(1));
        var record = Encounter("m1", 1200000, 60000, [], null, new("timed-measurement", null, measured, null));
        var saved = Encounter("m2", 600000, 30000, [], 20000, new("timed-measurement", "木桩", measured with { DurationMs = 30000, Complete = true, Reasons = [] }, null));
        var data = CombatMapper.Map(new(true, true, Workspace(null), [record, saved], null, null, [], Scope: "measurement:m1",
            Range: Range(record, null, false, "connection-changed")));
        Assert.Equal(CombatScopeKind.Measurement, data.Scope!.Kind);
        Assert.StartsWith("定时测量 1:00 · ", data.Scope.Title);
        Assert.Equal(["m1", "m2"], data.Scope.Measurements.Select(option => option.Id));
        Assert.Equal("木桩", data.Scope.Measurements[1].Label);
        Assert.True(data.Scope.Measurements[0].Current);
        // DPS 为总伤害 ÷ 设定时长，结果不完整时仍按已观测伤害给出并注明。
        Assert.Equal(("20,000", "不完整：期间切换了地图，按已观测伤害计算"), (data.Kpis[0].Value, data.Kpis[0].Note));
        Assert.Equal(("1:00", "实际输出 0:58"), (data.Kpis[2].Value, data.Kpis[2].Note));
        Assert.Equal("DPS 20,000", data.History[0].Dps);
        Assert.StartsWith("定时测量 1:00", data.History[0].Record);
    }

    [AvaloniaFact]
    public void MeasurementStateExplainsWhyItCannotStartAndCarriesTheRemainingTime()
    {
        var idle = new CombatMeasurementState("idle", new(90000, 0), new(1), false, "combat.observationInactive", null, null, null, null,
            null, null, null, [], null, null, new(7));
        var view = CombatMapper.Map(new(true, true, null, [], null, null, [], Measurement: idle, MeasurementAt: 1)).Measurement!;
        Assert.Equal((MeasurementPhase.Idle, false, "需要先开始 DPS 观测"), (view.Phase, view.CanStart, view.StartHint));
        Assert.Equal("1:30 · 从首次伤害开始计时", CombatPageModel.SettingsText(view.DurationMs));
        Assert.Equal(7UL, view.SettingsRevision);

        var running = idle with { Phase = "running", Available = false, UnavailableReason = "combat.measurementActive", RemainingMs = 42000,
            Damage = new(new BigInteger(123456)), CurrentDps = 2939.4, Complete = false, Reasons = ["identity-unresolved"] };
        var live = CombatMapper.Map(new(true, true, null, [], null, null, [], Measurement: running, MeasurementAt: 1)).Measurement!;
        Assert.Equal((MeasurementPhase.Running, "123,456", "2,939", true), (live.Phase, live.Damage, live.Dps, live.CanCancel));
        Assert.Equal("不完整：等待识别本机角色", live.Incomplete);
        Assert.Equal(7UL, live.SettingsRevision);
        // 未连接时不能开始，也不显示数值。
        Assert.False(CombatMapper.Map(new(false, true, null, [], null, null, [])).Measurement!.CanStart);
    }

    [AvaloniaFact]
    public void TimerInputIsParsedWithinTheBackendRange()
    {
        Assert.Equal(60000, CombatPageModel.ParseTimer("1", "0"));
        Assert.Equal(5000, CombatPageModel.ParseTimer("", "5"));
        Assert.Equal(3_600_000, CombatPageModel.ParseTimer("60", "0"));
        Assert.Null(CombatPageModel.ParseTimer("0", "4"));
        Assert.Null(CombatPageModel.ParseTimer("60", "1"));
        Assert.Null(CombatPageModel.ParseTimer("1", "60"));
        Assert.Null(CombatPageModel.ParseTimer("一", "0"));
        Assert.Equal("1:00", CombatPageModel.DurationText(60000));
        Assert.Equal("60:00", CombatPageModel.DurationText(3_600_000));
    }

    [AvaloniaTheory]
    [InlineData("observation", null)]
    [InlineData("segment", "ce-a")]
    [InlineData("measurement:m1", null)]
    public void EveryDisplayedRangeKeepsTheLatestFirstAttack(string scope, string? target)
    {
        var encounter = Encounter("m1", 1000, 10000, [Group("ce-a", "巨魔弓手", 1000, 10)], 100);
        var first = new CombatDamageEvent("first", Source, Local, new("game", new(1), new(2), new(1)), new(1000), new(9870), CombatDamageKind.ActualHp,
            Now, 5000, new(1), 0, false, true, ObservationQuality.Confirmed, FirstAttack: true);
        var data = CombatMapper.Map(new(true, true, Workspace(encounter), [], null, null, [], [first], scope, target,
            Range(encounter, target)));
        Assert.Equal(("重擊", "9,870"), (data.FirstAttack!.Skill, data.FirstAttack.Damage));
        if (target is not null) Assert.Equal("巨魔弓手", data.FirstAttack.Target);
    }

    [AvaloniaFact]
    public void ObservationStartAndAutoObservationStatesAreTextNotCodesOnly()
    {
        var operation = new OperationSnapshot("op-1", "combat.observe", "owner", [], OperationState.Running, "observing", new(1), null,
            new(Now, Now, null), null, null, new(CancellationState.None, null), EffectStatus.None);
        Assert.True(LiveBackend.StartView(operation, null)!.Waiting);
        var failed = operation with { State = OperationState.Failed, Problem = ApiErrors.Create("combat.sourceUnavailable", "x", "t") };
        Assert.Equal((false, "开始观测失败：战斗来源不可用"), (LiveBackend.StartView(failed, null)!.Waiting, LiveBackend.StartView(failed, null)!.Text));
        Assert.Null(LiveBackend.StartView(operation, Workspace(null) with { Observation = Workspace(null).Observation with { Id = "op-1" } }));

        Assert.Equal("等待识别角色，请切换一次地图", LiveBackend.AutoView(new(true, "waiting", null, "op-2", new(0)), null)!.Text);
        Assert.Equal(("未抓包：抓包已停止", true),
            (LiveBackend.AutoView(new(true, "off", "observation.captureStopped", null, new(0)), null)!.Text, LiveBackend.AutoView(new(true, "off", "observation.captureStopped", null, new(0)), null)!.Problem));
        Assert.Equal("", LiveBackend.AutoView(new(true, "observing", null, "obs", new(0)), Workspace(null))!.Text);
        Assert.Equal("自动观测已关闭", LiveBackend.AutoView(new(false, "off", "combat.autoDisabled", null, new(0)), null)!.Text);
    }

    [AvaloniaFact]
    public void MeasurementScopeShowsTheFullSummaryAndANewTimerClearsTheLastResult()
    {
        var healing = new CombatHealingSummary("observed-healing-v1", new(new BigInteger(6000)), new(3), new(new BigInteger(6000)), new(new BigInteger(0)),
            new(new BigInteger(0)), new(0), new(new BigInteger(0)), new(new BigInteger(0)), null, null, new(0), null, 100, null, [], HpsDurationMs: 60000);
        var measured = new CombatMeasurementResult("timed-measurement-v1", 60000, 5000, new(new BigInteger(1000)), 16.7, 500, 58500, 58000,
            true, [], Now, Now.AddMinutes(1));
        var record = Encounter("m1", 1000, 60000, [Group("ce-a", "巨魔弓手", 300, 3), Group("ce-b", "巨魔弓手", 700, 7)], 16.7,
            new("timed-measurement", null, measured, null)) with { Healing = healing };
        // 已完成的测量与整场、分段一样显示技能、目标、治疗与破防，治疗写明 HPS 的分母为设定时长。
        var done = CombatMapper.Map(new(true, true, Workspace(null), [record], null, null, [], Scope: "measurement:m1", Range: Range(record, null)));
        Assert.Equal(["重擊"], done.Skills.Select(row => row.Name));
        Assert.Equal(2, done.Targets.Count);
        Assert.Equal(3, done.RangeTargets!.Count);
        Assert.True(done.Healing!.Available);
        Assert.EndsWith("HPS = 施放治疗量 ÷ 统计时长 1:00（停手时与 DPS 一样冻结，截至最后一次有效伤害或施放治疗）", done.Healing.Header);
        Assert.NotNull(done.Break);
        // 选中目标时治疗保持整个范围的值并写明口径。
        var targeted = CombatMapper.Map(new(true, true, Workspace(null), [record], null, null, [], Scope: "measurement:m1", RangeTarget: "ce-a",
            Range: Range(record with { Healing = healing }, "ce-a")));
        Assert.Equal(("6,000", "治疗与所选怪物无关，按整个范围统计"), (targeted.Healing!.Tiles[1].Value, targeted.Healing.Note));

        // 按下“开始计时”后范围切到新的测量（m2）；等待首次伤害与计时中没有伤害时显示“暂无数据”，不显示上一次的数值或 0。
        Assert.Equal("measurement:m2", LiveBackend.FollowMeasurement("measurement:m1", "", "m1", Measurement("waiting", "m2", null)).Scope);
        // 首次读取（启动时已在进行的测量）只记下当前值，不切换范围。
        Assert.Equal("segment", LiveBackend.FollowMeasurement("segment", null, null, Measurement("running", "m1", null)).Scope);
        foreach (var (phase, text) in new[] { ("waiting", "等待首次伤害"), ("running", "定时测量计时中") })
        {
            var fresh = Encounter("m2", 0, 0, [], null, new("timed-measurement", null, null, null));
            var pending = CombatMapper.Map(new(true, true, Workspace(null), [record], null, null, [], Scope: "measurement:m2",
                Range: Range(fresh, null), Measurement: Measurement(phase, "m2", "m1"), MeasurementAt: 1));
            Assert.Equal(text, pending.Scope!.Problem);
            Assert.Equal(["—", "—", "—", "—"], pending.Kpis.Select(tile => tile.Value));
            Assert.Empty(pending.Skills);
        }
        var running = CombatMapper.Map(new(true, true, Workspace(null), [], null, null, [], Measurement: Measurement("running", "m2", "m1") with { Damage = new(new BigInteger(0)) },
            MeasurementAt: 1)).Measurement!;
        Assert.Equal(("—", "—"), (running.Damage, running.Dps));
    }

    private static CombatMeasurementState Measurement(string phase, string? id, string? last) => new(phase, new(60000, 5000), new(2), false,
        "combat.measurementActive", "obs", id, 30000, 1000, null, null, null, [], null, last, new(1));

    [AvaloniaFact]
    public void DetailWindowCurveUsesBackendBucketsAndLinksTableRowsWithRankings()
    {
        // 曲线：每桶伤害 ÷ 桶宽；单段按相对时间、整场按本机时钟合并各分段，悬停提示写时间与 DPS。
        var page = new CombatTimelinePage("enc", new(1), [], null,
            [new(0, 2000, Totals(4000), new(2)), new(2000, 4000, Totals(1000), new(1))], [], [], false);
        var view = CombatMapper.TimelineView(page, null);
        Assert.Equal([2000d, 500d], view.Dps);
        Assert.Equal((2d, 4d), (view.BucketSeconds, view.SpanSeconds));
        Assert.Equal(["0:00 · DPS 2,000", "0:02 · DPS 500"], view.Tips);
        Assert.Null(LiveBackend.TimelineSource(Workspace(null), "observation", null).Problem);
        // 两段相隔 50 秒：横轴从第一段开始，分段之间为 0，提示写本机时刻。
        var origin = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        IReadOnlyList<CombatDamageBucket> seconds = [new(0, 1000, Totals(2000), new(1)), new(1000, 2000, Totals(500), new(1))];
        var whole = CombatMapper.WholeTimelineView([(origin, seconds), (origin.AddSeconds(54), seconds)]);
        Assert.Equal(origin, whole.Origin);
        Assert.Equal(56, whole.Dps.Count);
        Assert.Equal([2000d, 500d, 0d], whole.Dps.Take(3));
        Assert.Equal([2000d, 500d], whole.Dps.Skip(54).Take(2));
        Assert.Equal(origin.AddSeconds(54).ToLocalTime().ToString("HH:mm:ss") + " · DPS 2,000", whole.Tips[54]);
        Assert.Equal(1000, CombatMapper.BucketWidth(10_000));

        // 表格选中“重擊”时技能排行同名条目高亮；再按一次取消。
        var model = new CombatPageModel(new SampleBackend()).Companion();
        try
        {
            model.Highlight("重擊");
            Assert.Equal(["重擊"], model.SkillBars.Where(bar => bar.Highlighted).Select(bar => bar.Label));
            model.HighlightCommand.Execute(model.SkillBars.Single(bar => bar.Label == "重擊"));
            Assert.Null(model.Highlighted);
            Assert.DoesNotContain(model.SkillBars, bar => bar.Highlighted);
            Assert.NotEmpty(model.TagBars);
        }
        finally { model.Dispose(); }
    }
    [AvaloniaFact]
    public void SelectionAcceptsOnlyTheLatestChoiceAndLateResultsAreDiscarded()
    {
        var selection = new CombatSelection();
        selection.SelectScope("observation");
        var first = selection.Snapshot();
        selection.SelectTarget("ce-a");
        var second = selection.Snapshot();
        // 先发出的整场读取迟到：已不是当前选择，丢弃；后发的选择才被采用。
        Assert.False(selection.Accepts(first.Key));
        Assert.True(selection.Accepts(second.Key));
        // 发出测量请求后用户切回当前分段：请求回应里的“切到测量”不再覆盖用户的选择。
        var issued = selection.Version;
        selection.SelectScope("segment");
        Assert.False(selection.Follow(issued, "measurement:m1"));
        Assert.Equal(("segment", (string?)null), (selection.Scope, selection.Target));
        Assert.True(selection.Follow(selection.Version, "measurement:m1"));
        selection.BackToLive();
        Assert.Equal("segment", selection.Scope);

        // 新选择的结果到达前：数值保持上一次，选中标记移到新目标，不清空重建。
        var shown = new SampleBackend().GetCombat();
        var next = shown with { Kpis = [], RangeTargets = [], SelectedTarget = "archer", Pending = true };
        var held = CombatPageModel.Hold(shown, next);
        Assert.Same(shown.Kpis, held.Kpis);
        Assert.Equal("archer", Assert.Single(held.RangeTargets!, chip => chip.Current).Id);
        Assert.True(held.Pending);
    }

    [AvaloniaFact]
    public void RecordDetailShowsTheSavedRecordAndSummaryOnlyRecordsSayThereIsNoDetail()
    {
        var saved = Encounter("enc-r", 1000, 10000, [Group("ce-a", "巨魔弓手", 300, 3), Group("ce-b", "巨魔弓手", 700, 7)], 100,
            new("saved-range", "首领战", null, null, SavedAt: Now, DataIncomplete: true));
        var data = CombatMapper.MapRecord(new(true, saved.Id, saved, null, Range(saved, null) with { Scope = "encounter" }, null));
        Assert.Equal(("首领战", true, false), (data.RecordInfo!.Name, data.RecordInfo.DataIncomplete, data.RecordInfo.SummaryOnly));
        Assert.StartsWith("保存于 ", data.RecordInfo.When);
        Assert.Equal(["100", "1,000", "0:10", "10"], data.Kpis.Select(tile => tile.Value));
        Assert.Equal(["重擊"], data.Skills.Select(row => row.Name));
        Assert.Equal(2, data.Targets.Count);
        Assert.Equal(["全部", "巨魔弓手", "巨魔弓手"], data.RangeTargets!.Select(chip => chip.Name));
        Assert.Equal((string.Empty, string.Empty), (data.Scope!.Title, data.Scope.Problem));
        Assert.True(CombatMapper.Map(new(true, true, null, [saved], null, null, [])).History[0].DataIncomplete);
        // 保存时标为不完整、文档里没有缺口时段：只写影响。
        Assert.Equal("抓包期间有数据缺失，数值可能偏低", data.RecordInfo.GapNote);

        // 没有标记但文档带缺口（自动分段与旧记录）：同样标为数据不完整，并写出缺口的影响与时段。
        var segment = saved with { Record = null, Gaps = [new("capture-gap", 2000, 3500, new(2)), new("observation-stopped", 8000, null, new(0))] };
        var gapped = CombatMapper.MapRecord(new(true, segment.Id, segment, null, Range(segment, null) with { Scope = "encounter" }, null));
        Assert.True(gapped.RecordInfo!.DataIncomplete);
        Assert.Equal("抓包期间有数据缺失，数值可能偏低（0:02–0:03、0:08 起）", gapped.RecordInfo.GapNote);
        Assert.True(CombatMapper.Map(new(true, true, null, [segment], null, null, [])).History[0].DataIncomplete);

        // 本版本之前保存、只有摘要的记录：没有保存时刻与不完整标记，只给出主数据并写明没有明细，命中次数为未知而不是 0。
        var legacy = JsonSerializer.Deserialize<CombatEncounter>(JsonSerializer.Serialize(
            saved with { Skills = [], Targets = [], Record = new("saved-range", "旧记录", null, null) }, ContractJson.Options), ContractJson.Options)!;
        var old = CombatMapper.MapRecord(new(true, legacy.Id, legacy, null, null, null));
        Assert.Equal((true, false, "这条记录保存于旧版本，没有明细"), (old.RecordInfo!.SummaryOnly, old.RecordInfo.DataIncomplete, old.RecordInfo.Note));
        Assert.StartsWith("开始于 ", old.RecordInfo.When);
        Assert.Equal(["100", "1,000", "0:10", "—"], old.Kpis.Select(tile => tile.Value));
        Assert.Empty(old.Skills);
        Assert.Null(old.Healing);
        Assert.Equal(string.Empty, old.Scope!.Problem);
        Assert.Equal(string.Empty, old.RecordInfo.GapNote);
    }

    [Fact]
    public void RecordWindowsTakeTheSmallestFreeSlotAndStayInsideTheWorkArea()
    {
        var area = new Avalonia.PixelRect(0, 0, 1920, 1040);
        var size = new Avalonia.PixelSize(1200, 820);
        // 关掉第 2 个后再开：取空出的 2 号，不与 3 号重合。
        Assert.Equal((2, new Avalonia.PixelPoint(164, 164)), CombatDetailWindow.RecordPlacement([1, 3], new(100, 100), size, area));
        // 错开后超出工作区：拉回工作区内。
        Assert.Equal((1, new Avalonia.PixelPoint(720, 220)), CombatDetailWindow.RecordPlacement([], new(700, 200), size, area));
        // 窗口比工作区大：贴左上角。
        Assert.Equal((1, new Avalonia.PixelPoint(0, 0)), CombatDetailWindow.RecordPlacement([], new(50, 50), new(2000, 1200), area));
    }
}
