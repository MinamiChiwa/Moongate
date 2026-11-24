using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;
using static MabiTool.CombatObservation.Tests.CombatSessionTests;

namespace MabiTool.CombatObservation.Tests;

public sealed class CombatHistoryTests
{
    [Fact]
    public async Task LegacyPersistedEncounterKeepsMissingClassificationAndEligibilityUnknown()
    {
        await using var area = new Area(); var encounter = await area.Closed();
        var legacy = JsonSerializer.SerializeToNode(encounter, ContractJson.Options)!.AsObject();
        foreach (var name in new[] { "skills", "targets" })
            foreach (var group in legacy[name]!.AsArray())
                foreach (var field in new[] { "classificationCounts", "criticalEligibleCount", "mainHitEligibleCount" })
                    group!.AsObject().Remove(field);
        var json = legacy.ToJsonString();
        await area.Database.WriteAsync(tx => tx.Execute("UPDATE combat_encounters SET document=$json,bytes=$bytes WHERE id=$id",
            ("$json", json), ("$bytes", System.Text.Encoding.UTF8.GetByteCount(json)), ("$id", encounter.Id)));
        await area.History.RecoverAsync();
        var restored = area.History.Encounter(encounter.Id, "client-a", false);
        Assert.Equal(encounter.Summary.AttributedDamage, restored.Summary.AttributedDamage);
        Assert.All(restored.Skills.Concat(restored.Targets), group =>
        {
            Assert.Null(group.ClassificationCounts); Assert.Null(group.CriticalEligibleCount); Assert.Null(group.MainHitEligibleCount);
        });
    }

    [Fact]
    public async Task SnapshotReadsDoNotSlowDownWithAFullHistory()
    {
        // 快照读取的两条查询（选当前观察、存储统计）不随历史规模变慢：写入 100 条已结束观察与战斗记录（正文各约 300 KB）前后各读 20 轮，
        // 耗时相当。漏建索引时每次读取要走完全部正文的溢出页，本机 20 轮由约 0.4 s 变为约 5.8 s。
        await using var area = new Area();
        await area.Start();
        long Rounds()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                Assert.NotNull(area.History.Workspace("client-a", true)); Assert.NotNull(area.History.Workspace("client-a", false));
                area.History.Storage("client-a", true); area.History.Storage("client-a", false);
            }
            return clock.ElapsedMilliseconds;
        }
        Rounds();
        var empty = Rounds();
        var padding = "{\"pad\":\"" + new string('x', 300 * 1024) + "\"}";
        await area.Database.WriteAsync(tx =>
        {
            for (var i = 0; i < 100; i++)
            {
                tx.Execute("INSERT INTO combat_observations(id,owner,game_session,revision,workspace,terminal,updated_at,encounter_id) VALUES($id,'client-a','seed','1',$w,1,$at,$e)",
                    ("$id", $"seed-observation-{i}"), ("$w", padding), ("$at", 1_000L + i), ("$e", $"seed-encounter-{i}"));
                tx.Execute("INSERT INTO combat_encounters(id,owner,observation_id,revision,closed,quality,favorite,started_at,document,bytes,timeline_bytes) VALUES($id,'client-a',$o,'1',1,'confirmed',0,$at,$d,$b,0)",
                    ("$id", $"seed-encounter-{i}"), ("$o", $"seed-observation-{i}"), ("$at", 1_000L + i), ("$d", padding), ("$b", (long)padding.Length));
            }
            return 0;
        });
        Assert.DoesNotContain("seed", area.History.Workspace("client-a", true)!.Observation.Id, StringComparison.Ordinal);
        Assert.Equal(100, area.History.Storage("client-a", false).Encounters);
        var full = Rounds();
        Assert.True(full < empty * 3 + 200, $"20 轮快照读取查询：空历史 {empty} ms，满额历史 {full} ms");
    }

    [Fact]
    public async Task PersistedHitFlagTraitsRoundTripAndLegacyDocumentsReadThemAsUnknown()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 0, [Damage("flagged", 0) with { Classification = CombatDamageClassification.Direct,
            Critical = true, PowerfulHit = true, RapidHit = false, DoubleHit = false, BreakedHit = true, BreakOccurred = false,
            FirstAttack = true, DefaultAttack = false, MultiHit = true, Elements = [CombatDamageElement.Ice] }]));
        session.Stop("source-end", At.AddMilliseconds(1000), 1000, false); var checkpoint = session.Capture();
        Assert.True(await area.History.SaveAsync(checkpoint, OperationState.Succeeded));
        var encounter = checkpoint.Workspace.Encounter!;
        var saved = Assert.Single(area.History.Encounter(encounter.Id, "client-a", false).Skills);
        Assert.Equal(new UInt64String(1), saved.BreakedHitCount); Assert.Equal(new UInt64String(1), saved.MultiHitCount);
        Assert.Equal(new UInt64String(1), saved.ElementCounts!.Ice);
        var hit = Assert.Single(area.History.Timeline("client-a", false, encounter.Id, new()).Items, x => x.Damage is not null).Damage!;
        Assert.Equal("ice", Assert.Single(hit.Elements!).RawValue); Assert.True(hit.FirstAttack);

        var legacy = JsonSerializer.SerializeToNode(encounter, ContractJson.Options)!.AsObject();
        foreach (var name in new[] { "skills", "targets" })
            foreach (var group in legacy[name]!.AsArray())
                foreach (var field in new[] { "breakedHitCount", "breakOccurredCount", "firstAttackCount", "defaultAttackCount",
                    "multiHitCount", "elementCounts" }) group!.AsObject().Remove(field);
        var json = legacy.ToJsonString();
        await area.Database.WriteAsync(tx => tx.Execute("UPDATE combat_encounters SET document=$json,bytes=$bytes WHERE id=$id",
            ("$json", json), ("$bytes", System.Text.Encoding.UTF8.GetByteCount(json)), ("$id", encounter.Id)));
        await area.History.RecoverAsync();
        var restored = area.History.Encounter(encounter.Id, "client-a", false);
        Assert.Equal(encounter.Summary.AttributedDamage, restored.Summary.AttributedDamage);
        Assert.All(restored.Skills.Concat(restored.Targets), group =>
        {
            Assert.Null(group.BreakedHitCount); Assert.Null(group.BreakOccurredCount); Assert.Null(group.FirstAttackCount);
            Assert.Null(group.DefaultAttackCount); Assert.Null(group.MultiHitCount); Assert.Null(group.ElementCounts);
            Assert.Equal(new UInt64String(1), group.PowerfulHitCount); Assert.Equal(new UInt64String(1), group.MainHitEligibleCount);
        });
    }

    [Fact]
    public async Task CheckpointsAreAtomicIdempotentAndKeepExactTimelineAndTotals()
    {
        await using var area = new Area(); var session = await area.Start();
        session.Apply(Frame(1, 0, [Damage("first", 0, ulong.MaxValue)]));
        var checkpoint = session.Capture(); Assert.True(await area.History.SaveAsync(checkpoint)); Assert.True(await area.History.SaveAsync(checkpoint));
        session.Acknowledge(checkpoint);
        var value = area.History.Workspace("client-a", false)!; Assert.Equal(ulong.MaxValue.ToString(), value.Encounter!.Summary.AttributedDamage.ToString());
        var timeline = area.History.Timeline("client-a", false, value.Encounter.Id, new()); Assert.Single(timeline.Items, x => x.Damage is not null);
        Assert.Equal(checkpoint.Workspace.Revision, area.Operations.Get(session.Capture().Workspace.Observation.Id, "client-a", false).Result!.Value.Deserialize<CombatObservationStatus>(ContractJson.Options)!.Revision);
    }
    [Fact]
    public async Task CancelRejectsLaterCheckpointAndFinalizesOnlyTheCommittedDamage()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 0, [Damage("first", 0)])); var id = area.History.Workspace("client-a", false)!.Observation.Id;
        await area.Operations.CancelAsync(id, "client-a", false, "cancel-test");
        session.Apply(Frame(2, 1000, [Damage("late", 1000, 500)])); Assert.False(await area.History.SaveAsync(session.Capture()));
        Assert.True(await area.History.StopCommittedAsync(id, "cancel-test", false, OperationState.Cancelled));
        var value = area.History.Workspace("client-a", false)!;
        Assert.Equal("100", value.Encounter!.Summary.AttributedDamage.ToString()); Assert.True(value.Encounter.Summary.Final); Assert.Equal("cancel-test", value.Encounter.Summary.BoundaryReason);
        Assert.Equal(OperationState.Cancelled, area.Operations.Get(id, "client-a", false).State.KnownValue);
        Assert.All(value.SelfEffects!.Views, x => Assert.True(x.Stale));
    }
    [Fact]
    public async Task TransactionFailureLeavesOperationWorkspaceAndTimelineAtThePreviousWatermark()
    {
        await using var area = new Area(); var session = await area.Start(); var initial = area.History.Workspace("client-a", false)!;
        await area.Database.WriteAsync(tx => tx.Execute("CREATE TRIGGER combat_fail BEFORE INSERT ON combat_timeline_pages BEGIN SELECT RAISE(ABORT,'injected-checkpoint-failure'); END;"));
        session.Apply(Frame(1, 0, [Damage("first", 0)])); var checkpoint = session.Capture();
        await Assert.ThrowsAnyAsync<Exception>(() => area.History.SaveAsync(checkpoint));
        Assert.Equal(initial.Revision, area.History.Workspace("client-a", false)!.Revision); Assert.Null(area.History.Workspace("client-a", false)!.Encounter);
        Assert.Equal(2UL, area.Operations.Get(initial.Observation.Id, "client-a", false).Revision.Value);
        await area.Database.WriteAsync(tx => tx.Execute("DROP TRIGGER combat_fail"));
        Assert.True(await area.History.SaveAsync(checkpoint));
        Assert.Single(area.History.Timeline("client-a", false, checkpoint.Workspace.Encounter!.Id, new()).Items, x => x.Damage is not null);
    }
    // 宿主先发布最新捕获、再在后台保存检查点：保存滞后于捕获期间崩溃，重启后从最近提交的检查点恢复，
    // 不重复计入已提交的伤害，也不丢已随该检查点提交的通知。
    [Fact]
    public async Task CrashWhileTheCheckpointLagsBehindLaterCapturesRecoversTheLastCommitExactlyOnce()
    {
        await using var area = new Area(); var session = await area.Start();
        session.Apply(Frame(1, 0, [Damage("first", 0)], [Effect(Self, 0)]));
        var saving = session.Capture();
        session.Apply(Frame(2, 1000, [Damage("second", 1000, 50)], [Effect(Self, 1000, CombatEffectChangeKind.Apply, 201)]));
        _ = session.Capture();
        Assert.True(await area.History.SaveAsync(saving)); session.Acknowledge(saving);
        // 确认较早的检查点只清除它已包含的待写条目与效果变化：下一次捕获只带第二帧的伤害与效果。
        var next = session.Capture();
        Assert.True(next.Material);
        Assert.Equal(["second"], next.Timeline.Where(x => x.Item.Damage is not null).Select(x => x.Item.Damage!.EventId));
        Assert.Single(next.Effects);
        var encounter = saving.Workspace.Encounter!.Id;
        var notified = area.Database.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='combat.summary.changed' AND revision=$r",
            ("$r", saving.Workspace.Revision.ToString()));
        Assert.Equal(1L, notified);
        // 崩溃：next 未提交。重启恢复以 saving 为准。
        await area.Database.RecoverOperationsAsync(); await area.History.RecoverAsync();
        var value = area.History.Encounter(encounter, "client-a", false);
        Assert.True(value.Summary.Final); Assert.Equal("backend-restarted", value.Summary.BoundaryReason);
        Assert.Equal("100", value.Summary.AttributedDamage.ToString());
        Assert.Equal(["first"], area.History.Timeline("client-a", false, encounter, new()).Items.Where(x => x.Damage is not null).Select(x => x.Damage!.EventId));
        Assert.Equal(1L, area.Database.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='combat.summary.changed' AND revision=$r",
            ("$r", saving.Workspace.Revision.ToString())));
    }
    [Fact]
    public async Task SummaryNotificationIsAppendedOnlyWhenTheSummaryContentChanges()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 0, [Damage("first", 0)]));
        long Count() => (long)area.Database.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='combat.summary.changed'")!;
        Assert.Equal(1L, Count());
        // 只有时间推进（分段时长变长）：检查点照常提交，摘要通知不再追加。
        session.Tick(At.AddMilliseconds(3000), 3000); var later = session.Capture();
        Assert.True(await area.History.SaveAsync(later)); session.Acknowledge(later);
        Assert.Equal(1L, Count());
        Assert.True(area.History.Workspace("client-a", false)!.Encounter!.Summary.DurationMs >= 3000);
        await area.Commit(session, Frame(2, 3500, [Damage("second", 3500)]));
        Assert.Equal(2L, Count());
    }
    [Fact]
    public async Task RestartClosesCommittedEncounterAsPartialAndKeepsHistoryAndEffects()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 0, [Damage("first", 0)], [Effect(Self, 0)]));
        await area.Commit(session, Frame(2, 1000, [Damage("second", 1000)]));
        var id = area.History.Workspace("client-a", false)!.Encounter!.Id;
        await area.Database.RecoverOperationsAsync(); await area.History.RecoverAsync();
        var value = area.History.Encounter(id, "client-a", false);
        Assert.True(value.Summary.Final); Assert.Equal(ObservationQuality.Partial, value.Summary.Quality.KnownValue);
        Assert.Equal(200, value.Summary.EncounterDps); Assert.Equal("backend-restarted", value.Summary.BoundaryReason); Assert.Equal("200", value.Summary.AttributedDamage.ToString());
        Assert.True(Assert.Single(area.History.EntityEffects(CombatSession.EntityId(Self), "client-a", false).Views).Stale);
        Assert.Single(area.History.Query("client-a", false, new()).Items);
        await area.History.RecoverAsync(); Assert.Equal(value.Revision, area.History.Encounter(id, "client-a", false).Revision);
    }
    [Fact]
    public async Task FavoritesAndActiveEncounterSurviveQuantityAndByteCleanup()
    {
        await using var area = new Area(new(MaximumEncounters: 2));
        var first = await area.Closed(); await area.History.FavoriteAsync("client-a", false, first.Id, new(true), "favorite-first", CombatHistoryStore.ETag(first));
        var second = await area.Closed(); await area.History.FavoriteAsync("client-a", false, second.Id, new(true), "favorite-second", CombatHistoryStore.ETag(second));
        var active = await area.Start(); await area.Commit(active, Frame(1, 0, [Damage("active", 0)]));
        var before = area.History.Storage("client-a", false); Assert.Equal(3, before.Encounters); Assert.True(before.OverBudget);
        var clean = await area.History.CleanAsync("client-a", "clean-active"); Assert.Equal(0, clean.RemovedEncounters); Assert.True(clean.OverBudget);
        Assert.NotNull(area.History.Encounter(first.Id, "client-a", false)); Assert.NotNull(area.History.Encounter(second.Id, "client-a", false));
        var tight = new CombatHistoryStore(area.Database, area.Operations, "epoch", retention: new(MaximumEncounters: 2, SoftBytes: 1024));
        var protectedResult = await tight.CleanAsync("client-a", "clean-protected"); Assert.True(protectedResult.OverBudget); Assert.True(protectedResult.ProtectedBytes.Value > 1024);
    }
    [Fact]
    public async Task CleanupPrefersOldCompleteHistoryAndReportsActualReclaimedBytes()
    {
        await using var area = new Area(new(MaximumEncounters: 2)); var partial = await area.Closed(partial: true); var complete = await area.Closed(); var latest = await area.Closed();
        Assert.Equal(2, area.History.Storage("client-a", false).Encounters);
        Assert.NotNull(area.History.Encounter(partial.Id, "client-a", false)); Assert.NotNull(area.History.Encounter(latest.Id, "client-a", false));
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => area.History.Encounter(complete.Id, "client-a", false)).Code);
        var small = new CombatHistoryStore(area.Database, area.Operations, "epoch", retention: new(MaximumEncounters: 1));
        var result = await small.CleanAsync("client-a", "clean-one-more"); Assert.Equal(1, result.RemovedEncounters); Assert.True(result.RemovedBytes.Value > 0);
        Assert.Equal(result, await small.CleanAsync("client-a", "clean-one-more"));
    }
    [Fact]
    public async Task HistoryCursorsBindRevisionAndTimelineCursorsSurviveCheckpoints()
    {
        await using var area = new Area(); var first = await area.Closed(); await area.Closed();
        var query = new CombatEncounterQuery(PageSize: 1); var page = area.History.Query("client-a", false, query); Assert.NotNull(page.NextCursor);
        var next = area.History.Query("client-a", false, query with { Cursor = page.NextCursor }); Assert.Single(next.Items); Assert.NotEqual(page.Items[0].Id, next.Items[0].Id);
        Assert.Equal("combat.planStale", Assert.Throws<OperationException>(() => area.History.Query("client-b", false, query with { Cursor = page.NextCursor })).Code);
        Assert.Equal("combat.planStale", Assert.Throws<OperationException>(() => area.History.Query("client-a", false, query with { Cursor = page.NextCursor, FavoritesOnly = true })).Code);
        var timelineQuery = new CombatTimelineQuery(PageSize: 1); var timeline = area.History.Timeline("client-a", false, first.Id, timelineQuery); Assert.NotNull(timeline.NextCursor);
        await area.History.FavoriteAsync("client-a", false, first.Id, new(true), "favorite-cursor", CombatHistoryStore.ETag(first));
        Assert.Equal("combat.planStale", Assert.Throws<OperationException>(() => area.History.Query("client-a", false, query with { Cursor = page.NextCursor })).Code);
        // 时间线只追加：记录修订变化后续页仍有效，过滤条件不同的游标仍被拒绝。
        var resumed = area.History.Timeline("client-a", false, first.Id, timelineQuery with { Cursor = timeline.NextCursor });
        Assert.True(Assert.Single(resumed.Items).Index.Value > Assert.Single(timeline.Items).Index.Value);
        Assert.Equal("combat.planStale", Assert.Throws<OperationException>(() => area.History.Timeline("client-a", false, first.Id, timelineQuery with { Cursor = timeline.NextCursor, FromMs = 1 })).Code);
        Assert.Empty(area.History.Query("client-b", false, new()).Items);
    }
    [Fact]
    public async Task EffectCoverageUsesUnionObservableIntervalsAndScopeWhileBucketsStayAuthoritative()
    {
        await using var area = new Area(); var session = await area.Start(); var caster = Self with { RawInstanceId = new(80) };
        await area.Commit(session, Frame(1, 0, [Damage("first", 0)], [Effect(Self, 0, caster: Self, ms: 2000), Effect(Self, 0, caster: caster, ms: 4000)]));
        await area.Commit(session, Frame(2, 1000, [Damage("second", 1000, 200)], [Effect(Self, 1000, CombatEffectChangeKind.Refresh, caster: Self, ms: 3000)],
            gaps: [new("connection-gap", 1000, 2000, new(0))]));
        await area.Commit(session, Frame(3, 3000, effects: [Effect(Self, 3000, CombatEffectChangeKind.Remove, caster: Self)]));
        session.Stop("source-end", At.AddMilliseconds(4000), 4000); var checkpoint = session.Capture(); await area.History.SaveAsync(checkpoint, OperationState.Succeeded);
        var timeline = area.History.Timeline("client-a", false, checkpoint.Workspace.Encounter!.Id, new(BucketWidthMs: 1000));
        var coverage = Assert.Single(timeline.EffectCoverage); Assert.Equal(3000, coverage.CoveredMs); Assert.Equal(3000, coverage.ObservableMs); Assert.Equal(1, coverage.Ratio);
        Assert.Equal("300", timeline.DamageBuckets.Aggregate(System.Numerics.BigInteger.Zero, (sum, x) => sum + x.Amounts.ActualHp.Value).ToString());
        var clipped = area.History.Timeline("client-a", false, checkpoint.Workspace.Encounter.Id, new(FromMs: 0, ToMs: 0)); Assert.Null(Assert.Single(clipped.EffectCoverage).Ratio);
    }

    [Fact]
    public async Task FavoriteRetryReturnsItsReceiptAfterTheEncounterWasCleaned()
    {
        await using var area = new Area(new(MaximumEncounters: 1)); var first = await area.Closed();
        var favored = await area.History.FavoriteAsync("client-a", false, first.Id, new(true), "favorite-original", CombatHistoryStore.ETag(first));
        var etag = CombatHistoryStore.ETag(favored);
        var unfavored = await area.History.FavoriteAsync("client-a", false, first.Id, new(false), "unfavorite-original", etag);
        await area.Closed(); Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => area.History.Encounter(first.Id, "client-a", false)).Code);
        Assert.Equal(JsonSerializer.Serialize(unfavored, ContractJson.Options), JsonSerializer.Serialize(
            await area.History.FavoriteAsync("client-a", false, first.Id, new(false), "unfavorite-original", etag), ContractJson.Options));
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() => area.History.FavoriteAsync("client-a", false, first.Id, new(true), "unfavorite-original", etag))).Code);
        Assert.Single(area.History.Query("client-a", false, new()).Items);
    }
    [Fact]
    public async Task CleanupClearsTheLatestClosedWorkspaceAndKeepsScopedCounts()
    {
        await using var area = new Area(new(MaximumEncounters: 2));
        var partial = await area.Closed(partial: true, owner: "client-b"); var latest = await area.Closed();
        var small = new CombatHistoryStore(area.Database, area.Operations, "epoch", retention: new(MaximumEncounters: 1));
        var before = area.History.HistoryRevision(); var result = await small.CleanAsync("client-a", "cleanup-scoped");
        Assert.Equal(0, result.Encounters); Assert.Equal(1, result.RemovedEncounters);
        Assert.True(area.History.HistoryRevision().Value > before.Value);
        Assert.Null(area.History.Workspace("client-a", false, latest.ObservationId)!.Encounter);
        Assert.NotNull(area.History.Encounter(partial.Id, "client-b", false));
    }
    [Fact]
    public async Task CancellationKeepsPartialRatesMarkedIncomplete()
    {
        await using var area = new Area(); var session = await area.Start();
        await area.Commit(session, Frame(1, 0, [Damage("first", 0)]));
        await area.Commit(session, Frame(2, 1000, [Damage("second", 1000)], gaps: [new("capture-gap", 500, 1000, new(1))]));
        var id = area.History.Workspace("client-a", false)!.Observation.Id;
        await area.Operations.CancelAsync(id, "client-a", false, "cancel-partial");
        await area.History.StopCommittedAsync(id, "cancel-partial", false, OperationState.Cancelled);
        var summary = area.History.Workspace("client-a", false)!.Encounter!.Summary;
        Assert.Equal(ObservationQuality.Partial, summary.Quality.KnownValue); Assert.Equal(200, summary.EncounterDps); Assert.Equal(200, summary.RollingDps);
        Assert.Contains("capture-gap", summary.Reasons);
        Assert.Equal("200", summary.AttributedDamage.ToString());
    }

    [Fact]
    public async Task UpgradeBackfillsActiveEncounterReferenceSoCleanupStillProtectsIt()
    {
        var root = Path.Combine(Area.RepositoryRoot(), "artifacts", "tmp", "combat-history", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, PortableRoot: root));
            await using (var legacy = SqliteStore.Open(lease, [CombatHistoryStore.Migration], mock: true))
                await legacy.WriteAsync(tx =>
                {
                    // 升级前的数据：活动工作区引用已关闭的 enc-active，终态工作区引用 enc-old。
                    tx.Execute("INSERT INTO combat_observations VALUES('obs-active','client-a','game','1','{\"encounter\":{\"id\":\"enc-active\"}}',0,2)");
                    tx.Execute("INSERT INTO combat_observations VALUES('obs-old','client-a','game','1','{\"encounter\":{\"id\":\"enc-old\"}}',1,1)");
                    tx.Execute("INSERT INTO combat_observations VALUES('obs-empty','client-a','game','1','{\"encounter\":null}',1,0)");
                    foreach (var (id, observation, at) in new[] { ("enc-active", "obs-active", 2), ("enc-old", "obs-old", 1) })
                        tx.Execute("INSERT INTO combat_encounters(id,owner,observation_id,revision,closed,quality,favorite,started_at,document,bytes) VALUES($id,'client-a',$o,'1',1,'confirmed',0,$at,'{}',100)",
                            ("$id", id), ("$o", observation), ("$at", at));
                    return true;
                });
            await using var upgraded = SqliteStore.Open(lease, CombatHistoryStore.Migrations, mock: true);
            Assert.Equal("enc-active", upgraded.ReadScalar("SELECT encounter_id FROM combat_observations WHERE id='obs-active'"));
            Assert.Equal("enc-old", upgraded.ReadScalar("SELECT encounter_id FROM combat_observations WHERE id='obs-old'"));
            Assert.Null(upgraded.ReadScalar("SELECT encounter_id FROM combat_observations WHERE id='obs-empty'"));
            // 版本 3、4 新增偏好表与配置修订；版本 5 为快照读取的两条查询补建索引，已有数据原样保留。
            Assert.Equal(5L, upgraded.ReadScalar("SELECT max(version) FROM core_migrations WHERE module_id='combat-observation'"));
            Assert.Equal(2L, upgraded.ReadScalar("SELECT count(*) FROM sqlite_master WHERE type='index' AND name IN ('combat_observations_recent','combat_encounters_storage')"));
            var history = new CombatHistoryStore(upgraded, new OperationStore(upgraded), "epoch");
            var storage = history.Storage("client-a", false);
            Assert.Equal(2, storage.Encounters); Assert.Equal(100UL, storage.ProtectedBytes.Value);
            Assert.Equal("obs-active", upgraded.ReadScalar("SELECT id FROM combat_observations ORDER BY updated_at DESC,id DESC LIMIT 1"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StorageAndCleanupStayShortAtDefaultLimitWithLargeActiveWorkspace()
    {
        await using var area = new Area();
        for (var i = 0; i < 100; i++) await area.Closed();
        var active = await area.Start(); await area.Commit(active, Frame(1, 0, [Damage("active", 0)]));
        var activeId = area.History.Workspace("client-a", false)!.Encounter!.Id;
        // 活动工作区填充至约 3.5 MiB，接近 4 MiB 快照上限。
        await area.Database.WriteAsync(tx => tx.Execute("UPDATE combat_observations SET workspace=json_set(workspace,'$.padding',$pad) WHERE terminal=0",
            ("$pad", new string('x', 3_500_000))));
        Assert.Equal(101, area.History.Storage("client-a", false).Encounters);
        const string legacyStorageSql = "SELECT count(*),coalesce(sum(bytes+timeline_bytes),0),coalesce(sum(favorite),0),coalesce(sum(CASE WHEN favorite=1 OR closed=0 OR EXISTS (SELECT 1 FROM combat_observations o WHERE o.terminal=0 AND json_extract(o.workspace,'$.encounter.id')=combat_encounters.id) THEN bytes+timeline_bytes ELSE 0 END),0) FROM combat_encounters WHERE ($owner=1 OR owner=$p)";
        var storage = new List<double>(); var legacy = new List<double>(); var cleanup = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew(); var summary = area.History.Storage("client-a", false); storage.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Equal(101, summary.Encounters);
        }
        for (var i = 0; i < 5; i++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            area.Database.Read(connection =>
            {
                using var command = connection.CreateCommand(); command.CommandText = legacyStorageSql;
                command.Parameters.AddWithValue("$owner", 0); command.Parameters.AddWithValue("$p", "client-a"); return command.ExecuteScalar();
            });
            legacy.Add(timer.Elapsed.TotalMilliseconds);
        }
        var callback = new List<double>(); var commit = new List<double>();
        area.Database.WriteTimingObserver = timing => { if (timing.Callback.Contains(nameof(CombatHistoryStore.CleanAsync))) { callback.Add(timing.CallbackMs); commit.Add(timing.CommitMs); } };
        for (var i = 1; i <= 30; i++)
        {
            // 每次以低一档的上限清理，覆盖逐条删除及活动战斗保护判定路径。
            var store = new CombatHistoryStore(area.Database, area.Operations, "epoch", retention: new(MaximumEncounters: 101 - i));
            var timer = System.Diagnostics.Stopwatch.StartNew(); var result = await store.CleanAsync("client-a", "cleanup-timing-" + i, owner: true);
            cleanup.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Equal(1, result.RemovedEncounters);
        }
        Assert.NotNull(area.History.Encounter(activeId, "client-a", false));
        static object Stats(List<double> values)
        {
            var sorted = values.Order().ToArray();
            double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
            return new { samples = sorted.Length, p50Ms = At(0.5), p99Ms = At(0.99), maxMs = sorted[^1] };
        }
        var report = Path.Combine(Area.RepositoryRoot(), "artifacts", "test-results", "storage-budget", "combat-history-timing.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            measuredAt = DateTimeOffset.UtcNow, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
            encounters = 101, activeWorkspaceBytes = area.Database.ReadScalar("SELECT length(CAST(workspace AS BLOB)) FROM combat_observations WHERE terminal=0"),
            storage = Stats(storage), legacyStorageQuery = Stats(legacy), cleanupEndToEnd = Stats(cleanup),
            cleanupTransactionCallback = Stats(callback), cleanupFullCommit = Stats(commit),
            dataRootDrive = Path.GetPathRoot(Area.RepositoryRoot())
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(storage.Order().ElementAt(storage.Count / 2) < SqliteStore.TransactionBudget.TotalMilliseconds);
        Assert.Equal(30, callback.Count);
        Assert.True(callback.Order().ElementAt(callback.Count / 2) < SqliteStore.TransactionBudget.TotalMilliseconds);
    }

    [Fact]
    public async Task IdentityPendingIsQueryableAndTheFirstCheckpointClearsIt()
    {
        await using var area = new Area();
        var request = new CombatObservationRequest(Self.GameSessionId);
        var operation = (await area.Operations.AdmitCombatAsync("client-a", request, Guid.NewGuid().ToString("N"))).Snapshot;
        await area.Operations.CombatIdentityPendingAsync(operation.Id);
        var pending = area.Operations.Get(operation.Id, "client-a", true);
        Assert.Equal((OperationState.Running, "identity-pending", "combat.identityPending"), (pending.State.KnownValue, pending.Phase, pending.Problem?.Code));
        var session = new CombatSession("client-a", operation.Id, Source, Entity(Self, CombatEntityRole.LocalPlayer), request, At);
        var initial = session.Capture(); Assert.True(await area.History.SaveAsync(initial));
        var observing = area.Operations.Get(operation.Id, "client-a", true);
        Assert.Equal((OperationState.Running, "observing"), (observing.State.KnownValue, observing.Phase));
        Assert.Null(observing.Problem);
        // 已有检查点后不再回到等待状态。
        await area.Operations.CombatIdentityPendingAsync(operation.Id);
        Assert.Equal("observing", area.Operations.Get(operation.Id, "client-a", true).Phase);
    }

    private sealed class Area : IAsyncDisposable
    {
        private readonly string root;
        private readonly DataRootLease lease;
        public SqliteStore Database { get; }
        public OperationStore Operations { get; }
        public CombatHistoryStore History { get; }
        public Area(CombatRetentionLimits? limits = null)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent!;
            root = Path.Combine(repository.FullName, "artifacts", "tmp", "combat-history", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, PortableRoot: root));
            Database = SqliteStore.Open(lease, CombatHistoryStore.Migrations, mock: true); Operations = new(Database); History = new(Database, Operations, "epoch", retention: limits);
        }
        public static string RepositoryRoot()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent!;
            return repository.FullName;
        }
        public async Task<CombatSession> Start(string owner = "client-a")
        {
            var request = new CombatObservationRequest(Self.GameSessionId); var operation = await Operations.AdmitCombatAsync(owner, request, Guid.NewGuid().ToString("N"));
            var session = new CombatSession(owner, operation.Snapshot.Id, Source, Entity(Self, CombatEntityRole.LocalPlayer), request, At);
            var initial = session.Capture(); await History.SaveAsync(initial); session.Acknowledge(initial); return session;
        }
        public async Task Commit(CombatSession session, CombatFrame frame)
        {
            session.Apply(frame); var checkpoint = session.Capture(); Assert.True(await History.SaveAsync(checkpoint)); session.Acknowledge(checkpoint);
        }
        public async Task<CombatEncounter> Closed(bool partial = false, string owner = "client-a")
        {
            var session = await Start(owner); await Commit(session, Frame(1, 0, [Damage("begin", 0)]));
            session.Stop(partial ? "connection-lost" : "source-end", At.AddMilliseconds(1000), 1000, partial); var checkpoint = session.Capture();
            Assert.True(await History.SaveAsync(checkpoint, partial ? OperationState.Interrupted : OperationState.Succeeded)); return checkpoint.Workspace.Encounter!;
        }
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync(); lease.Dispose();
            if (!Path.GetFullPath(root).Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar + "tmp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }
}
