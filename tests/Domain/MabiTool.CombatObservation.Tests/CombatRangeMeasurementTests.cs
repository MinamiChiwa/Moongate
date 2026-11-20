using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

/// <summary>
/// 按“时间范围 × 目标”的重新统计、保存记录、整场范围与定时测量：沿用分段归属与分组规则，DPS 使用完整范围，目标筛选不合并同名个体、
/// 超出事件保留时标为不完整，测量窗口独立于分段，取消不保存。
/// </summary>
public sealed class CombatRangeMeasurementTests
{
    private static readonly EntityKey Twin = Monster with { RawInstanceId = new(Monster.RawInstanceId.Value + (1UL << 32)) };
    private static readonly EntityKey Ally = Self with { RawInstanceId = new(77) };
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static CombatDamageEvent Hit(string id, double mono, ulong amount, EntityKey target, bool breaked = false) =>
        Damage(id, mono, amount, target: target) with { Classification = CombatDamageClassification.Direct, Hit = true, Critical = amount > 150,
            PowerfulHit = false, RapidHit = false, DoubleHit = false, BreakedHit = breaked, BreakOccurred = false, FirstAttack = false,
            DefaultAttack = true, MultiHit = false, Elements = [CombatDamageElement.Fire] };
    private static CombatEntityObservation[] Monsters => [Entity(Monster, CombatEntityRole.Monster, "同名目标"), Entity(Twin, CombatEntityRole.Monster, "同名目标"),
        Entity(Ally, CombatEntityRole.Player, "芙蕾雅")];
    private static CombatHealEvent Cast(string id, double mono, ulong amount, EntityKey target) => new(id, CombatHealDirection.Cast, target, new(0x2000),
        new(amount), null, null, false, At.AddMilliseconds(mono), mono, null, ObservationQuality.Confirmed, "hp-unpaired");

    [Fact]
    public async Task BreakIntervalRunsFromTheBreakingHitUntilTheGaugeEmpties()
    {
        await using var area = new Area(); var session = await area.Start();
        CombatEntityObservation Gauge(EntityKey key, double current, double mono) => Entity(key, CombatEntityRole.Monster, "同名目标") with
            { BreakGauge = new(current, new(150), At.AddMilliseconds(mono), ObservationQuality.Confirmed) };
        await area.Commit(session, Frame(1, 0, [Hit("a", 0, 100, Monster)], entities: [Gauge(Monster, 140, 0), Gauge(Twin, 20, 0)]));
        // 造成破防的一击（1000），破防期间两次无防备命中，破防值在 4000 归零；之后的命中不在区间内。
        await area.Commit(session, Frame(2, 1000, [Hit("b", 1000, 300, Monster, breaked: true) with { BreakOccurred = true }],
            entities: [Gauge(Monster, 150, 1000), Gauge(Twin, 20, 0)]));
        await area.Commit(session, Frame(3, 3000, [Hit("c", 3000, 300, Monster, breaked: true)], entities: [Gauge(Monster, 150, 1000), Gauge(Twin, 20, 0)]));
        // 击杀的一击造成破防：破防值同一时刻归零，记为零时长区间。
        await area.Commit(session, Frame(4, 4000, [Hit("d", 4000, 900, Twin, breaked: true) with { BreakOccurred = true }],
            entities: [Gauge(Monster, 0, 4000), Gauge(Twin, 0, 4000)]));
        await area.Commit(session, Frame(5, 5000, [Hit("e", 5000, 100, Monster)], entities: [Gauge(Monster, 3, 5000), Gauge(Twin, 0, 4000)]));
        var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
        var encounter = checkpoint.Workspace.Encounter!;

        // 分段汇总与范围汇总同一规则：区间 1000–4000 与 4000 处零时长，并集 3000 ms，平均 DPS = 无防备伤害 1500 ÷ 3 秒。
        foreach (var summary in new[] { encounter.Break!, area.History.QueryRange("client-a", false, new("encounter", encounter.Id)).Break })
        {
            Assert.Equal((3000d, 2UL), (summary.DurationMs!.Value, summary.Intervals!.Value.Value));
            Assert.Equal(500, summary.Dps!.Value, 6);
            Assert.Empty(summary.Reasons);
        }
        var monster = area.History.QueryRange("client-a", false, new("encounter", encounter.Id, TargetId: CombatSession.EntityId(Monster))).Break;
        Assert.Equal((3000d, 1UL, 200d), (monster.DurationMs!.Value, monster.Intervals!.Value.Value, monster.Dps!.Value));
    }

    [Fact]
    public async Task FullRangeMatchesTheSegmentAndTargetFilterKeepsSameNameMonstersApart()
    {
        await using var area = new Area(); var session = await area.Start(healing: true);
        await area.Commit(session, Frame(1, 0, [Hit("a", 0, 100, Monster, breaked: true), Hit("b", 0, 300, Twin)], entities: Monsters));
        await area.Commit(session, Frame(2, 2000, [Hit("c", 2000, 200, Monster, breaked: true), Hit("d", 2000, 50, Twin, breaked: true)], entities: Monsters)
            with { Healing = [Cast("h1", 2000, 400, Ally)] });
        session.Tick(At.AddMilliseconds(4000), 4000);
        var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
        var encounter = checkpoint.Workspace.Encounter!;

        var full = area.History.QueryRange("client-a", false, new("encounter", encounter.Id));
        Assert.True(full.Complete); Assert.Empty(full.Reasons);
        Assert.Equal(encounter.Summary.AttributedDamage, full.Result.Summary.AttributedDamage);
        Assert.Equal(4000, encounter.Summary.DurationMs); Assert.Equal(2000, encounter.Summary.DpsDurationMs);
        Assert.Equal(325, encounter.Summary.EncounterDps);
        Assert.Equal(4000, full.Result.Summary.DurationMs); Assert.Equal(4000, full.Result.Summary.DpsDurationMs);
        Assert.Equal(162.5, full.Result.Summary.EncounterDps);
        Assert.Equal(Json(encounter.Skills), Json(full.Result.Skills));
        Assert.Equal(Json(encounter.Targets), Json(full.Result.Targets));
        Assert.Equal(Json(encounter.Healing!.Targets), Json(full.Result.Healing!.Targets));
        Assert.Equal(encounter.Healing.CastAmount, full.Result.Healing.CastAmount);
        Assert.Equal((0d, 2000d), (full.FirstDamageMs!.Value, full.LastDamageMs!.Value));
        Assert.Equal(("350", 3UL), (full.Break.Damage!.ToString(), full.Break.Count!.Value.Value));
        // 没有造成破防的一击、也没有破防值归零：起点与终点都按无防备命中估计（Monster 0–2000，Twin 2000 处零时长）。
        Assert.Equal((2000d, 2UL, 0.5), (full.Break.DurationMs!.Value, full.Break.Intervals!.Value.Value, full.Break.TimeShare!.Value));
        Assert.Equal(175, full.Break.Dps!.Value, 6);
        Assert.Equal(["break-start-estimated", "break-end-estimated"], full.Break.Reasons);

        var twin = CombatSession.EntityId(Twin);
        var filtered = area.History.QueryRange("client-a", false, new("encounter", encounter.Id, TargetId: twin));
        Assert.Equal(("同名目标", "350"), (filtered.TargetName, filtered.Result.Summary.AttributedDamage.ToString()));
        Assert.Equal(2, filtered.RangeTargets.Count);
        Assert.Equal(new[] { "同名目标", "同名目标" }, filtered.RangeTargets.Select(x => x.Name));
        Assert.Equal("650", filtered.RangeDamage.ToString());
        Assert.Equal(("50", 1UL), (filtered.Break.Damage!.ToString(), filtered.Break.Count!.Value.Value));
        Assert.Equal((0d, 1UL), (filtered.Break.DurationMs!.Value, filtered.Break.Intervals!.Value.Value));
        Assert.Null(filtered.Break.Dps);
        // 选中目标时 DPS 以对该目标第一击（0）到最后一击（2000）的时长为分母，范围时长不变。
        Assert.Equal((2000d, 4000d), (filtered.Result.Summary.DpsDurationMs!.Value, filtered.Result.Summary.DurationMs));
        Assert.Equal(350 * 1000d / 2000, filtered.Result.Summary.EncounterDps!.Value, 6);
        // 目标分组带首次出现与最近受击的时刻，供桌面统一排序；治疗块写明每秒治疗量的分母。
        var twinGroup = full.RangeTargets.Single(x => x.Key == twin);
        Assert.Equal((At, At.AddMilliseconds(2000)), (twinGroup.FirstDamageAt!.Value, twinGroup.LastDamageAt!.Value));
        Assert.Equal(4000d, full.Result.Healing.HpsDurationMs);
        // 治疗目标是队友，目标筛选只作用于伤害。
        Assert.Equal("400", filtered.Result.Healing!.CastAmount.ToString());

        var window = area.History.QueryRange("client-a", false, new("encounter", encounter.Id, FromMs: 1000, ToMs: 2500));
        Assert.Equal(("250", 1500d), (window.Result.Summary.AttributedDamage.ToString(), window.Result.Summary.DurationMs));
        Assert.Equal(1000d, window.FirstDamageMs);
        var outsideWindow = Assert.Throws<OperationException>(() => area.History.QueryRange("client-a", false,
            new("encounter", encounter.Id, FromMs: 500, ToMs: 1500, TargetId: CombatSession.EntityId(Monster))));
        Assert.Equal("combat.entityOutOfScope", outsideWindow.Code);
        var error = Assert.Throws<OperationException>(() => area.History.QueryRange("client-a", false, new("encounter", encounter.Id, TargetId: "ce-missing")));
        Assert.Equal("combat.entityOutOfScope", error.Code);
    }

    [Fact]
    public async Task OverlappingTagsCountEachTagWhileTheTotalAddsTheDamageOnce()
    {
        await using var area = new Area(); var session = await area.Start();
        // 一条 10,000 伤害同时是强击、连击与暗属性；另一条 500 不带标签。
        var tagged = Hit("tagged", 0, 10000, Monster) with { PowerfulHit = true, RapidHit = true, Critical = false, Elements = [CombatDamageElement.Dark], AdditionalHit = true };
        var plain = Hit("plain", 1000, 500, Monster) with { Critical = false, Elements = [], AdditionalHit = false };
        await area.Commit(session, Frame(1, 0, [tagged], entities: Monsters));
        await area.Commit(session, Frame(2, 1000, [plain], entities: Monsters));
        session.Tick(At.AddMilliseconds(2000), 2000);
        var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
        var group = Assert.Single(checkpoint.Workspace.Encounter!.Skills);
        Assert.Equal("10500", group.AttributedDamage.ToString());
        Assert.Equal((1UL, 1UL, 0UL, 1UL), (group.PowerfulHitCount!.Value.Value, group.RapidHitCount!.Value.Value, group.DoubleHitCount!.Value.Value, group.ElementCounts!.Dark.Value));
        var tags = group.TagDamage!;
        Assert.Equal(("10000", "10000", "0", "0", "10000", "0"), (tags.PowerfulHit!.ToString(), tags.RapidHit!.ToString(), tags.DoubleHit!.ToString(),
            tags.Critical!.ToString(), tags.Elements!.Dark.ToString(), tags.Elements.Fire.ToString()));
        Assert.Equal("10500", tags.DefaultAttack!.ToString());
        Assert.Equal(("1", "10000"), (group.AdditionalHitCount!.Value.ToString(), tags.AdditionalHit!.ToString()));
        // 范围重放按分段合并（Merge）后与分段摘要一致。
        var range = area.History.QueryRange("client-a", false, new("encounter", checkpoint.Workspace.Encounter!.Id));
        Assert.Equal(Json(group.TagDamage), Json(Assert.Single(range.Result.Skills).TagDamage));
    }

    [Fact]
    public async Task DamageBeyondRetainedTimelineMarksTheRangeIncompleteAndEffectsHaveTheirOwnQuota()
    {
        await using var area = new Area();
        // 10 条的时间线：效果与实体至多 4 条，超出只计截断，不使伤害统计降级。
        var limits = new CombatLimits(MaximumTimelineItems: 10);
        var session = await area.Start(limits: limits);
        await area.Commit(session, Frame(1, 0, [Hit("first", 0, 100, Monster)], entities: Monsters,
            effects: [.. Enumerable.Range(0, 8).Select(i => Effect(Monster, 0, instance: (ulong)(300 + i)))]));
        var effectsOnly = session.Capture().Workspace.Encounter!;
        Assert.Equal(ObservationQuality.Confirmed, effectsOnly.Summary.Quality.KnownValue);
        Assert.True(effectsOnly.Resources.TruncatedTimelineItems.Value > 0);
        for (var i = 1; i <= 12; i++)
            await area.Commit(session, Frame((ulong)i + 1, i * 100, [Hit("more-" + i, i * 100, 100, Monster)], entities: Monsters));
        var encounter = session.Capture().Workspace.Encounter!;
        var range = area.History.QueryRange("client-a", false, new("encounter", encounter.Id));
        Assert.False(range.Complete);
        Assert.Contains("timeline-incomplete", range.Reasons);
        Assert.Equal((double?)(range.Result.Summary.DpsDurationMs is > 0 ? (double)range.Result.Summary.AttributedDamage.Value * 1000 / range.Result.Summary.DpsDurationMs.Value : null), range.Result.Summary.EncounterDps);
        // 分段本身在内存里累计，总量完整；范围只能重放保留下来的事件，不悄悄少算成完整结果。
        Assert.Equal("1300", encounter.Summary.AttributedDamage.ToString());
        Assert.True(range.Result.Summary.AttributedDamage.Value < encounter.Summary.AttributedDamage.Value);
    }

    [Fact]
    public async Task ObservationScopeKeepsTheExpiredWholeUntilTheNextDamage()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 1000, [Hit("old", 1000, 500, Monster)], entities: Monsters));
        session.Tick(At.AddMilliseconds(181000), 181000);
        await area.Commit(session, Frame(2, 181100, entities: Monsters));
        var observation = session.Capture().Workspace.Observation;
        Assert.Equal("idle-out-of-combat", observation.WholeExpired);
        var kept = area.History.QueryRange("client-a", false, new("observation", ObservationId: observation.Id));
        Assert.Equal("500", kept.Result.Summary.AttributedDamage.ToString());
        await area.Commit(session, Frame(3, 190000, [Hit("new", 190000, 70, Monster)], entities: Monsters));
        var renewed = area.History.QueryRange("client-a", false, new("observation", ObservationId: observation.Id));
        Assert.Equal("70", renewed.Result.Summary.AttributedDamage.ToString());
        Assert.Single(renewed.EncounterIds);
    }

    [Fact]
    public async Task ObservationScopeMergesSegmentsSinceTheLastResetAndSavedRangesBecomeFavoriteRecords()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 0, [Hit("old", 0, 999, Monster)], entities: Monsters));
        session.Reset(At.AddMilliseconds(1000), 1000);
        await area.Commit(session, Frame(2, 2000, [Hit("r1", 2000, 100, Monster)], entities: Monsters));
        await area.Commit(session, Frame(3, 3000, [Hit("r2", 3000, 100, Monster)], entities: Monsters));
        Assert.True(session.Split(At.AddMilliseconds(3500), 3500));
        await area.Commit(session, Frame(4, 5000, [Hit("s1", 5000, 300, Twin)], entities: Monsters));
        await area.Commit(session, Frame(5, 6000, [Hit("s2", 6000, 300, Twin)], entities: Monsters));
        var observation = session.Capture().Workspace.Observation.Id;

        var whole = area.History.QueryRange("client-a", false, new("observation", ObservationId: observation));
        Assert.Equal(2, whole.EncounterIds.Count);
        Assert.Equal("800", whole.Result.Summary.AttributedDamage.ToString());
        // 重设后由首击开始的分段从 2000 到最后一击 3000（拆段截止最后有效伤害），拆段后的分段从 5000 进行到 6000：分母为两段之和。
        Assert.Equal(2000d, whole.Result.Summary.DurationMs);
        Assert.Equal(800 * 1000d / 2000, whole.Result.Summary.EncounterDps!.Value, 6);
        Assert.Equal(2, whole.RangeTargets.Count);

        var saved = await area.History.SaveRangeAsync("client-a", false, new(new("observation", ObservationId: observation), " 首领战 "), "save-range-1");
        Assert.True(saved.Favorite);
        Assert.Equal(("saved-range", "首领战"), (saved.Record!.Kind, saved.Record.Name));
        Assert.Equal("800", saved.Summary.AttributedDamage.ToString());
        Assert.Equal(Json(saved), Json(await area.History.SaveRangeAsync("client-a", false, new(new("observation", ObservationId: observation), " 首领战 "), "save-range-1")));
        Assert.Contains(area.History.Query("client-a", false, new()).Items, x => x.Id == saved.Id);
        var reopened = area.History.QueryRange("client-a", false, new("encounter", saved.Id));
        Assert.True(reopened.Complete);
        Assert.Equal(whole.Result.Summary.AttributedDamage, reopened.Result.Summary.AttributedDamage);
        Assert.Equal(Json(whole.Result.Skills), Json(reopened.Result.Skills));
        var invalid = await Assert.ThrowsAsync<OperationException>(() => area.History.SaveRangeAsync("client-a", false,
            new(new("observation", ObservationId: observation), new string('长', 61)), "save-range-2"));
        Assert.Equal("request.invalid", invalid.Code);
    }

    [Fact]
    public async Task ObservationRangesBeyondTheSegmentLimitRemainPartialWhenSavedAndReopened()
    {
        await using var area = new Area(); var session = await area.Start();
        for (var i = 0; i <= CombatHistoryStore.MaximumRangeEncounters; i++)
        {
            session.Apply(Frame((ulong)i * 2 + 1, i * 2000, [Hit("limit-a-" + i, i * 2000, 100, Monster)], entities: Monsters));
            session.Apply(Frame((ulong)i * 2 + 2, i * 2000 + 1000, [Hit("limit-b-" + i, i * 2000 + 1000, 100, Monster)], entities: Monsters));
            Assert.True(session.Split(At.AddMilliseconds(i * 2000 + 1050), i * 2000 + 1050));
            var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
            var segment = checkpoint.Workspace.Encounter!;
            await area.History.FavoriteAsync("client-a", false, segment.Id, new(true), "limit-favorite-" + i, CombatHistoryStore.ETag(segment));
        }
        var observation = session.Capture().Workspace.Observation.Id;
        var query = new CombatRangeQuery("observation", ObservationId: observation);
        var range = area.History.QueryRange("client-a", false, query);
        Assert.False(range.Complete);
        Assert.Equal(ObservationQuality.Partial, range.Result.Summary.Quality.KnownValue);
        Assert.Contains("range-segment-limit", range.Reasons);
        Assert.Equal((double?)(range.Result.Summary.DpsDurationMs is > 0 ? (double)range.Result.Summary.AttributedDamage.Value * 1000 / range.Result.Summary.DpsDurationMs.Value : null), range.Result.Summary.EncounterDps);

        var saved = await area.History.SaveRangeAsync("client-a", false, new(query, "超过范围上限"), "save-range-limit");
        Assert.True(saved.Favorite);
        Assert.Equal(CombatHistoryStore.MaximumRangeEncounters + 1, saved.Record!.Source!.EncounterIds.Count);
        Assert.Equal(ObservationQuality.Partial, saved.Summary.Quality.KnownValue);
        Assert.Contains("range-segment-limit", saved.Summary.Reasons);
        Assert.Equal((double?)(saved.Summary.DpsDurationMs is > 0 ? (double)saved.Summary.AttributedDamage.Value * 1000 / saved.Summary.DpsDurationMs.Value : null), saved.Summary.EncounterDps);
        var reopened = area.History.QueryRange("client-a", false, new("encounter", saved.Id));
        Assert.False(reopened.Complete);
        Assert.Contains("range-segment-limit", reopened.Reasons);
        Assert.Equal((double?)(reopened.Result.Summary.DpsDurationMs is > 0 ? (double)reopened.Result.Summary.AttributedDamage.Value * 1000 / reopened.Result.Summary.DpsDurationMs.Value : null), reopened.Result.Summary.EncounterDps);
        Assert.Equal(range.Result.Summary.AttributedDamage, reopened.Result.Summary.AttributedDamage);
    }

    [Fact]
    public async Task SavingLegacyHealingWithoutTimelineKeepsTheMissingDataReasonAfterReopening()
    {
        await using var area = new Area(); var session = await area.Start(healing: true);
        await area.Commit(session, Frame(1, 0, [Hit("legacy-a", 0, 100, Monster)], entities: Monsters));
        session.Apply(Frame(2, 2000, [Hit("legacy-b", 2000, 200, Monster)], entities: Monsters)
            with { Healing = [Cast("legacy-heal", 2000, 400, Ally)] });
        session.Tick(At.AddMilliseconds(4000), 4000);
        var checkpoint = session.Capture();
        // 旧版保留治疗摘要，时间线中尚未保存治疗事件。
        var legacy = checkpoint with { Timeline = checkpoint.Timeline.Where(value => value.Item.Heal is null).ToArray() };
        Assert.True(await area.History.SaveAsync(legacy)); session.Acknowledge(legacy);
        var query = new CombatRangeQuery("encounter", checkpoint.Workspace.Encounter!.Id);
        var original = area.History.QueryRange("client-a", false, query);
        Assert.False(original.Complete);
        Assert.Contains("healing-not-retained", original.Result.Healing!.Reasons);
        Assert.Null(original.Result.Healing.EncounterHps);

        var saved = await area.History.SaveRangeAsync("client-a", false, new(query, "旧治疗记录"), "save-legacy-healing");
        Assert.Equal(ObservationQuality.Partial, saved.Summary.Quality.KnownValue);
        Assert.Contains("healing-not-retained", saved.Summary.Reasons);
        Assert.Contains("healing-not-retained", saved.Healing!.Reasons);
        Assert.Null(saved.Healing.EncounterHps); Assert.Null(saved.Healing.RollingHps);
        Assert.Null(saved.Summary.Healing!.EncounterHps);
        var reopened = area.History.QueryRange("client-a", false, new("encounter", saved.Id));
        Assert.False(reopened.Complete);
        Assert.Contains("healing-not-retained", reopened.Reasons);
        Assert.Contains("healing-not-retained", reopened.Result.Healing!.Reasons);
        Assert.Null(reopened.Result.Healing.EncounterHps); Assert.Null(reopened.Result.Healing.RollingHps);
        Assert.Equal("300", reopened.Result.Summary.AttributedDamage.ToString());
    }

    [Fact]
    public async Task MeasurementCountsOnlyItsWindowAcrossSegmentBoundariesAndIsSavedAsATimedRecord()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 0, [Hit("before", 0, 100, Monster)], entities: Monsters));
        var id = session.BeginMeasurement(At.AddMilliseconds(1000), 1000, 5000, 1000);
        Assert.Throws<InvalidOperationException>(() => session.BeginMeasurement(At, 1000, 5000, 0));
        // 受理前的伤害不计入；窗口从受理后的第一次有效伤害开始。
        await area.Commit(session, Frame(2, 500, [Hit("countdown", 500, 100, Monster)], entities: Monsters));
        await area.Commit(session, Frame(3, 1000, [Hit("in-1", 1000, 300, Monster)], entities: Monsters));
        Assert.True(session.Split(At.AddMilliseconds(1500), 1500));
        await area.Commit(session, Frame(4, 3000, [Hit("in-2", 3000, 200, Twin)], entities: Monsters));
        Assert.Equal(500, (int)session.MeasurementProgress()!.Damage);
        session.Tick(At.AddMilliseconds(5000), 5000);
        var paused = session.Capture();
        Assert.Equal(0, paused.Workspace.Encounter!.Summary.DpsDurationMs); Assert.Null(paused.Workspace.Encounter.Summary.EncounterDps);
        var measuring = paused.Encounters.Single(value => value.Id == id).Summary;
        Assert.Equal("observed-damage-v1", measuring.CalculationVersion);
        Assert.Equal(4000, measuring.DurationMs); Assert.Equal(4000, measuring.DpsDurationMs); Assert.Equal(125, measuring.EncounterDps);
        await area.Commit(session, Frame(5, 6500, [Hit("after", 6500, 900, Monster)], entities: Monsters));
        var finished = session.FinishMeasurement(At.AddMilliseconds(6500))!;
        var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);

        var result = finished.Record!.Measurement!;
        Assert.Equal(("timed-measurement", id), (finished.Record.Kind, finished.Id));
        Assert.Equal(("500", 100d), (result.Damage.ToString(), result.Dps!.Value));
        Assert.Equal((0d, 2000d, 2000d), (result.FirstDamageMs!.Value, result.LastDamageMs!.Value, result.ActiveDurationMs!.Value));
        Assert.True(result.Complete);
        var saved = area.History.Encounter(id, "client-a", false);
        Assert.True(saved.Summary.Final); Assert.Equal(5000, saved.Summary.DurationMs);
        Assert.Equal(5000, saved.Summary.DpsDurationMs); Assert.Equal(100, saved.Summary.EncounterDps);
        Assert.Equal("observed-damage-v1", saved.Summary.CalculationVersion);
        Assert.Contains(area.History.Query("client-a", false, new()).Items, x => x.Id == id);
        var query = new CombatRangeQuery("encounter", id);
        var range = area.History.QueryRange("client-a", false, query);
        Assert.True(range.Complete); Assert.Equal("500", range.Result.Summary.AttributedDamage.ToString());
        Assert.Equal(5000, range.Result.Summary.DpsDurationMs); Assert.Equal(100, range.Result.Summary.EncounterDps);
        Assert.Equal(2, range.RangeTargets.Count);
        Assert.Null(session.MeasurementProgress());

        var selected = area.History.QueryRange("client-a", false, query with { FromMs = 1000, ToMs = 4000 });
        Assert.Equal("200", selected.Result.Summary.AttributedDamage.ToString());
        Assert.Equal(3000, selected.Result.Summary.DurationMs); Assert.Equal(3000, selected.Result.Summary.DpsDurationMs);
        Assert.Equal(200 * 1000d / 3000, selected.Result.Summary.EncounterDps!.Value, 6);
        var copy = await area.History.SaveRangeAsync("client-a", false, new(query, "测量副本"), "complete-window-copy");
        Assert.Equal("timed-measurement", copy.Record!.Kind); Assert.Equal(Json(result), Json(copy.Record.Measurement));
        Assert.Equal(5000, copy.Summary.DurationMs); Assert.Equal(5000, copy.Summary.DpsDurationMs); Assert.Equal(100, copy.Summary.EncounterDps);
        Assert.Equal("observed-damage-v1", copy.Summary.CalculationVersion);

        var legacyJson = Json(saved.Summary with { DpsDurationMs = null });
        Assert.DoesNotContain("dpsDurationMs", legacyJson);
        var legacy = JsonSerializer.Deserialize<CombatSummary>(legacyJson, ContractJson.Options)!;
        Assert.Null(legacy.DpsDurationMs); Assert.Equal(saved.Summary.EncounterDps, legacy.EncounterDps);
        Assert.Equal(saved.Summary.DurationMs, legacy.DurationMs); Assert.Equal(saved.Summary.CalculationVersion, legacy.CalculationVersion);

        session.Tick(At.AddMilliseconds(7000), 7000);
        var stopping = session.Capture(); Assert.True(await area.History.SaveAsync(stopping)); session.Acknowledge(stopping);
        Assert.True(await area.History.StopCommittedAsync(stopping.Workspace.Observation.Id, "cancel-test", false, OperationState.Cancelled));
        var stopped = area.History.Encounter(stopping.Workspace.Encounter!.Id, "client-a", false);
        Assert.True(stopped.Summary.Final); Assert.Equal(4000, stopped.Summary.DurationMs); Assert.Equal(3500, stopped.Summary.DpsDurationMs);
        Assert.Equal(stopping.Workspace.Encounter.Summary.EncounterDps, stopped.Summary.EncounterDps);
    }

    [Fact]
    public async Task CancelledMeasurementIsDeletedAndMapChangeOrStopMarksTheResultIncomplete()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 0, [Hit("a", 0, 100, Monster)], entities: Monsters));
        var cancelled = session.BeginMeasurement(At, 0, 5000, 0);
        await area.Commit(session, Frame(2, 100, [Hit("b", 100, 100, Monster)], entities: Monsters));
        Assert.Equal("timed-measurement", area.History.Encounter(cancelled, "client-a", false).Record!.Kind);
        Assert.True(session.CancelMeasurement()); Assert.False(session.CancelMeasurement());
        var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => area.History.Encounter(cancelled, "client-a", false)).Code);

        // 切图：连接缺口后身份重新识别，计时继续，结果标为不完整。
        var measured = session.BeginMeasurement(At.AddMilliseconds(200), 200, 10000, 0);
        await area.Commit(session, Frame(3, 300, [Hit("c", 300, 100, Monster)], entities: Monsters,
            gaps: [new("combat.streamGap.connectionClosed", 300, 300, new(0))]));
        var next = Self with { ObservationGeneration = new(2) }; var nextMonster = Monster with { ObservationGeneration = new(2) };
        await area.Commit(session, Frame(1, 2000, [Damage("d", 2000, 400, next, nextMonster)],
            entities: [Entity(next, CombatEntityRole.LocalPlayer, "測試角色"), Entity(nextMonster, CombatEntityRole.Monster)]) with { Generation = new(2) });
        var progress = session.MeasurementProgress()!;
        Assert.Equal(500, (int)progress.Damage); Assert.True(progress.Partial); Assert.Contains("connection-changed", progress.Reasons);
        // 观测停止：已开始的测量以停止时点结束并保存，标为不完整；DPS 仍按设定时长计算。
        session.Stop("observation-stopped", At.AddMilliseconds(4000), 4000);
        var stopped = session.Capture(); Assert.True(await area.History.SaveAsync(stopped, OperationState.Cancelled));
        var record = area.History.Encounter(measured, "client-a", false);
        Assert.True(record.Summary.Final);
        Assert.False(record.Record!.Measurement!.Complete);
        Assert.Contains("observation-stopped", record.Record.Measurement.Reasons);
        Assert.Equal(50d, record.Record.Measurement.Dps);

        // 还在等待首次伤害时观测停止：没有窗口数据，不保存。
        var other = await area.Start();
        await area.Commit(other, Frame(1, 0, [Hit("x", 0, 100, Monster)], entities: Monsters));
        var pending = other.BeginMeasurement(At.AddMilliseconds(5000), 5000, 5000, 5000);
        await area.Commit(other, Frame(2, 100, [], entities: Monsters));
        other.Stop("observation-stopped", At.AddMilliseconds(200), 200);
        var final = other.Capture(); Assert.True(await area.History.SaveAsync(final, OperationState.Cancelled));
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => area.History.Encounter(pending, "client-a", false)).Code);
    }

    [Fact]
    public async Task SavingAMeasurementAcrossGenerationsKeepsSelfHealingAndItsGapTimeline()
    {
        await using var area = new Area(); var session = await area.Start(healing: true);
        await area.Commit(session, Frame(1, 0, [Hit("seed", 0, 100, Monster)], entities: Monsters));
        var measured = session.BeginMeasurement(At.AddMilliseconds(1000), 1000, 5000, 0);
        await area.Commit(session, Frame(2, 1000, [Hit("before-map", 1000, 200, Monster)], entities: Monsters)
            with { Healing = [Cast("self-before-map", 1000, 100, Self) with { ReceiptMerged = true, EffectiveAmount = new(80), Overheal = new(20) }] });
        await area.Commit(session, Frame(3, 1500, [], entities: Monsters,
            gaps: [new("combat.streamGap.connectionClosed", 1500, 1500, new(0))]));
        var next = Self with { ObservationGeneration = new(2) };
        var nextMonster = Monster with { ObservationGeneration = new(2) };
        var nextAlly = Ally with { ObservationGeneration = new(2) };
        var entities = new[] { Entity(next, CombatEntityRole.LocalPlayer, "測試角色"),
            Entity(nextMonster, CombatEntityRole.Monster, "同名目标"), Entity(nextAlly, CombatEntityRole.Player, "芙蕾雅") };
        await area.Commit(session, Frame(1, 3000, [Damage("after-map", 3000, 300, next, nextMonster)], entities: entities)
            with { Generation = new(2), Healing = [Cast("self-after-map", 3000, 300, next)
                with { ReceiptMerged = true, EffectiveAmount = new(250), Overheal = new(50) }, Cast("ally-after-map", 3000, 200, nextAlly)] });
        await area.Commit(session, Frame(2, 5000, [Damage("last", 5000, 400, next, nextMonster)], entities: entities)
            with { Generation = new(2) });
        session.Tick(At.AddMilliseconds(6000), 6000);
        Assert.NotNull(session.FinishMeasurement(At.AddMilliseconds(6000)));
        var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
        var original = area.History.Encounter(measured, "client-a", false);
        Assert.Equal(("400", "200", "400"), (original.Healing!.CastSelfAmount.ToString(),
            original.Healing.CastOtherAmount.ToString(), original.Healing.ReceivedSelfAmount.ToString()));
        var query = new CombatRangeQuery("encounter", measured);
        var range = area.History.QueryRange("client-a", false, query);
        Assert.Equal(Json(original.Healing.Targets), Json(range.Result.Healing!.Targets));
        Assert.Equal(original.Healing.CastSelfAmount, range.Result.Healing.CastSelfAmount);
        Assert.Equal(original.Healing.CastOtherAmount, range.Result.Healing.CastOtherAmount);

        var saved = await area.History.SaveRangeAsync("client-a", false, new(query, "跨地图测量"), "save-map-healing");
        Assert.Equal("timed-measurement", saved.Record!.Kind);
        Assert.Equal(Json(original.Record!.Measurement), Json(saved.Record.Measurement));
        Assert.Equal(Json(original.Healing.Targets), Json(saved.Healing!.Targets));
        Assert.Equal(original.Healing.CastSelfAmount, saved.Healing.CastSelfAmount);
        Assert.Equal(original.Healing.CastOtherAmount, saved.Healing.CastOtherAmount);
        Assert.Equal(original.Healing.ReceivedSelfAmount, saved.Healing.ReceivedSelfAmount);
        var timeline = area.History.Timeline("client-a", false, saved.Id, new()).Items;
        var retained = area.History.Timeline("client-a", false, measured, new()).Items
            .Where(item => item.Damage is not null || item.Heal is not null || item.Gap is not null)
            .Select((item, index) => item with { Index = new((ulong)index + 1) }).ToArray();
        Assert.Contains(timeline, item => item.Gap?.Reason == "connection-changed");
        Assert.Equal(Json(retained), Json(timeline));
        var reopened = area.History.QueryRange("client-a", false, new("encounter", saved.Id));
        Assert.False(reopened.Complete);
        Assert.Contains("connection-changed", reopened.Reasons);
        Assert.Equal(Json(saved.Healing.Targets), Json(reopened.Result.Healing!.Targets));
        Assert.Equal(saved.Healing.CastSelfAmount, reopened.Result.Healing.CastSelfAmount);
    }

    [Fact]
    public async Task SavingAWindowKeepsAndRebasesAGapReportedAfterTheWindow()
    {
        await using var area = new Area(); var session = await area.Start(healing: true);
        await area.Commit(session, Frame(1, 1000, [Hit("open", 1000, 100, Monster)], entities: Monsters));
        await area.Commit(session, Frame(2, 2000, [Hit("inside", 2000, 200, Monster)], entities: Monsters)
            with { Healing = [Cast("inside-heal", 2000, 400, Ally)] });
        await area.Commit(session, Frame(3, 3000, [Hit("after", 3000, 300, Monster)], entities: Monsters,
            gaps: [new("capture-gap", 1500, 2500, new(3))]));
        session.Tick(At.AddMilliseconds(4000), 4000);
        var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
        var encounter = checkpoint.Workspace.Encounter!;
        var originalGap = Assert.Single(area.History.Timeline("client-a", false, encounter.Id, new()).Items, item => item.Gap is not null);
        Assert.Equal(2000d, originalGap.OffsetMs);
        var query = new CombatRangeQuery("encounter", encounter.Id, FromMs: 1000, ToMs: 1500);
        var original = area.History.QueryRange("client-a", false, query);
        var saved = await area.History.SaveRangeAsync("client-a", false, new(query, "缺口窗口"), "save-gap-window");
        Assert.Equal(("200", "400", 500d), (saved.Summary.AttributedDamage.ToString(), saved.Healing!.CastAmount.ToString(), saved.Summary.DurationMs));
        Assert.Equal(ObservationQuality.Partial, saved.Summary.Quality.KnownValue);
        var timeline = area.History.Timeline("client-a", false, saved.Id, new()).Items;
        Assert.Equal(new[] { "damage", "heal", "gap" }, timeline.Select(item => item.Kind));
        Assert.Equal(new ulong[] { 1, 2, 3 }, timeline.Select(item => item.Index.Value));
        Assert.Equal(new double[] { 0, 0, 500 }, timeline.Select(item => item.OffsetMs));
        var copied = Assert.Single(timeline, item => item.Gap is not null).Gap!;
        Assert.Equal(("capture-gap", 0d, 500d, 3UL), (copied.Reason, copied.FromMs, copied.ToMs!.Value, copied.DroppedEvents.Value));
        Assert.Equal(copied, Assert.Single(saved.Gaps));
        var reopened = area.History.QueryRange("client-a", false, new("encounter", saved.Id));
        Assert.False(reopened.Complete);
        Assert.Contains("capture-gap", reopened.Reasons);
        Assert.Equal(original.Result.Summary.AttributedDamage, reopened.Result.Summary.AttributedDamage);
        Assert.Equal(original.Result.Healing!.CastAmount, reopened.Result.Healing!.CastAmount);
        Assert.Equal(Json(original.Result.Skills), Json(reopened.Result.Skills));
    }

    [Fact]
    public async Task MeasurementSettingsPersistAndDefaultToOneMinuteWithoutCountdown()
    {
        await using var area = new Area();
        Assert.Equal(new CombatMeasurementSettings(60000, 0), area.History.MeasurementSettings());
        await area.History.SaveMeasurementSettingsAsync(new(90000, 0));
        Assert.Equal(new CombatMeasurementSettings(90000, 0), area.History.MeasurementSettings());
    }

    [Fact]
    public async Task SavedSegmentReopensWithTheSameDetailAndCurve()
    {
        await using var area = new Area(); var session = await area.Start(healing: true);
        await area.Commit(session, Frame(1, 0, [Hit("a", 0, 100, Monster, breaked: true), Hit("b", 0, 300, Twin)], entities: Monsters));
        await area.Commit(session, Frame(2, 2000, [Hit("c", 2000, 200, Monster), Hit("d", 2000, 50, Twin)], entities: Monsters)
            with { Healing = [Cast("h1", 2000, 400, Ally)] });
        await area.Commit(session, Frame(3, 5000, [Hit("e", 5000, 160, Monster)], entities: Monsters));
        var segment = session.Capture().Workspace.Encounter!;
        var query = new CombatRangeQuery("encounter", segment.Id);
        var before = area.History.QueryRange("client-a", false, query);
        var curve = new CombatTimelineQuery(PageSize: 1, BucketWidthMs: 1000);

        var saved = await area.History.SaveRangeAsync("client-a", false, new(query, "对比用"), "save-detail");
        Assert.NotNull(saved.Record!.SavedAt); Assert.Null(saved.Record.DataIncomplete);
        var reopened = area.History.QueryRange("client-a", false, new("encounter", saved.Id));
        Assert.True(reopened.Complete);
        Assert.Equal(before.Result.Summary.AttributedDamage, reopened.Result.Summary.AttributedDamage);
        Assert.Equal(Json(before.Result.Skills), Json(reopened.Result.Skills));
        Assert.Equal(Json(before.Result.Targets), Json(reopened.Result.Targets));
        Assert.Equal(Json(before.Totals), Json(reopened.Totals));
        Assert.Equal(Json(before.Break), Json(reopened.Break));
        Assert.Equal(Json(before.Result.Healing!.Targets), Json(reopened.Result.Healing!.Targets));
        var sourceBuckets = area.History.Timeline("client-a", false, segment.Id, curve).DamageBuckets;
        Assert.NotEmpty(sourceBuckets);
        Assert.Equal(Json(sourceBuckets), Json(area.History.Timeline("client-a", false, saved.Id, curve).DamageBuckets));

        // 分段随后出现抓包缺口：再保存的记录标为数据不完整，并带同一缺口。
        await area.Commit(session, Frame(4, 6000, [Hit("f", 6000, 120, Monster)], entities: Monsters, gaps: [new("capture-gap", 5500, 5800, new(2))]));
        var gapped = await area.History.SaveRangeAsync("client-a", false, new(query, "有缺口"), "save-detail-gap");
        Assert.True(gapped.Record!.DataIncomplete);
        Assert.Contains(gapped.Gaps, gap => gap.Reason == "capture-gap");

        // 本版本之前保存的记录没有保存时刻与不完整标记，读回为空。
        var legacy = JsonSerializer.Deserialize<CombatRecordInfo>("""{"kind":"saved-range","name":"旧记录","measurement":null,"source":null}""", ContractJson.Options)!;
        Assert.Null(legacy.SavedAt); Assert.Null(legacy.DataIncomplete);
    }

    private sealed class Area : IAsyncDisposable
    {
        private readonly string root;
        private readonly DataRootLease lease;
        public SqliteStore Database { get; }
        public OperationStore Operations { get; }
        public CombatHistoryStore History { get; }
        public Area()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent!;
            root = Path.Combine(repository.FullName, "artifacts", "tmp", "combat-range", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, PortableRoot: root));
            Database = SqliteStore.Open(lease, CombatHistoryStore.Migrations, mock: true); Operations = new(Database); History = new(Database, Operations, "epoch");
        }
        public async Task<CombatSession> Start(bool healing = false, CombatLimits? limits = null)
        {
            var request = new CombatObservationRequest(Self.GameSessionId); var operation = await Operations.AdmitCombatAsync("client-a", request, Guid.NewGuid().ToString("N"));
            var session = new CombatSession("client-a", operation.Snapshot.Id, Source, Entity(Self, CombatEntityRole.LocalPlayer, "測試角色"), request, At, limits, healing: healing);
            var initial = session.Capture(); await History.SaveAsync(initial); session.Acknowledge(initial); return session;
        }
        public async Task Commit(CombatSession session, CombatFrame frame)
        {
            session.Apply(frame); var checkpoint = session.Capture(); Assert.True(await History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
        }
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync(); lease.Dispose();
            if (!Path.GetFullPath(root).Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar + "tmp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }
}
