using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Providers.Replay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class CombatApiTests
{
    private const string Token = "combat_tests_012345678901234567890123456789012345678901";
    private static readonly string[] Topics = ["combat.summary.changed", "combat.effects.changed", "combat.encounter.closed", "combat.observation.degraded"];

    [Fact]
    public async Task DetailMetricsAgreeAcrossSdkQueriesEventsAndRestart()
    {
        using var area = new Workspace(); string encounterId;
        await using (var app = Build(area))
        {
            await app.StartAsync(); using var first = Sdk(app); using var second = Sdk(app);
            var before = await first.GetCombatSnapshotAsync();
            var operation = await first.StartCombatObservationAsync(new((await first.GetGameBindingAsync()).Binding!.Session.Id,
                ReplayIntervalMs: 25), "combat-detail-metrics");
            await Terminal(first, operation.Id);
            await ReadClosed(second, new(Topics, [], before.EventCursor), (await first.GetCombatSnapshotAsync()).Data!.Encounter!.Id);
            var snapshot = await second.GetCombatSnapshotAsync();
            var encounter = snapshot.Data!.Encounter!; encounterId = encounter.Id;
            AssertDetail(encounter);
            Assert.Equal(Json(encounter), Json((await first.GetCombatSnapshotAsync()).Data!.Encounter));
            Assert.Equal(Json(encounter), Json(await second.GetCombatEncounterAsync(encounter.Id)));
            Assert.Equal(Json(encounter), Json(Assert.Single((await first.GetCombatEncountersAsync()).Items)));
            Assert.Equal(Json(encounter), Json(Assert.Single((await second.QueryCombatEncountersAsync(new())).Items)));
            var monsters = snapshot.Data.Entities.Where(x => x.Role.KnownValue == CombatEntityRole.Monster).OrderBy(x => x.Key.RawInstanceId.Value).ToArray();
            Assert.Equal(2, monsters.Length); Assert.Equal(monsters[0].RawName, monsters[1].RawName);
            Assert.Equal(37.5, monsters[0].BreakGauge!.Current); Assert.Equal("1000", monsters[0].BreakGauge!.Maximum.ToString());
            Assert.Equal(76.5, monsters[1].BreakGauge!.Current); Assert.Equal("2000", monsters[1].BreakGauge!.Maximum.ToString());
            Assert.All(monsters, monster => Assert.True(monster.BreakGauge!.Stale));
            var timeline = await second.QueryCombatTimelineAsync(encounter.Id, new());
            var hits = timeline.Items.Where(x => x.Damage is not null).Select(x => x.Damage!).ToArray();
            Assert.Equal(3, hits.Length);
            Assert.Equal(new bool?[] { true, false, true }, hits.Select(x => x.PowerfulHit));
            Assert.Equal(new bool?[] { false, true, true }, hits.Select(x => x.RapidHit));
            Assert.Equal(new bool?[] { false, false, true }, hits.Select(x => x.DoubleHit));
            Assert.Equal(new CombatDamageClassification?[] { CombatDamageClassification.Direct,
                CombatDamageClassification.Indirect, CombatDamageClassification.DamageOverTime }, hits.Select(x => x.Classification!.Value.KnownValue));
            await first.StopCombatObservationAsync(operation.Id); await app.StopAsync();
        }
        await using var restarted = Build(area); await restarted.StartAsync(); using var next = Sdk(restarted);
        AssertDetail(await next.GetCombatEncounterAsync(encounterId));
        AssertDetail(Assert.Single((await next.GetCombatEncountersAsync()).Items));
        var restored = (await next.GetCombatSnapshotAsync()).Data!;
        var restoredMonsters = restored.Entities.Where(x => x.Role.KnownValue == CombatEntityRole.Monster).OrderBy(x => x.Key.RawInstanceId.Value).ToArray();
        Assert.Equal(2, restoredMonsters.Length);
        Assert.Equal(new double?[] { 37.5, 76.5 }, restoredMonsters.Select(x => x.BreakGauge!.Current));
        Assert.All(restoredMonsters, x => Assert.True(x.BreakGauge!.Stale));
        Assert.Equal(0, ((CombatReplaySource)restarted.Services.GetRequiredService<MabiTool.Modules.CombatObservation.Application.ICombatObservationSource>()).ActiveSubscriptions);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task RealtimeEffectEventsAreRevisionNoticesAndTheFollowUpReadsCarryNames()
    {
        using var area = new Workspace(); await using var app = Build(area); await app.StartAsync(); using var sdk = Sdk(app);
        var before = await sdk.GetCombatSnapshotAsync();
        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id, ReplayIntervalMs: 25), "combat-realtime-names");
        await Terminal(sdk, operation.Id);
        var workspace = (await sdk.GetCombatSnapshotAsync()).Data!;
        // 效果事件只是修订通知：不带效果条目，客户端按 changedEntityIds 重读实体效果，名称随这次读取返回。
        var notices = await ReadEffectNotices(sdk, new(Topics, [], before.EventCursor), workspace.Encounter!.Id);
        Assert.NotEmpty(notices);
        Assert.All(notices, notice =>
        {
            Assert.True(notice.GetProperty("snapshotRequired").GetBoolean());
            Assert.Equal(0, notice.GetProperty("effects").GetArrayLength());
        });
        var changed = notices.SelectMany(notice => notice.GetProperty("changedEntityIds").EnumerateArray().Select(id => id.GetString()!)).ToHashSet();
        var current = workspace.Entities.Where(entity => changed.Contains(entity.Id)).ToArray();
        Assert.NotEmpty(current);
        foreach (var entity in current)
            Assert.All((await sdk.GetCombatEntityEffectsAsync(entity.Id)).Views, view =>
            {
                var name = Assert.IsType<CombatResolvedName>(view.ResolvedName);
                Assert.Equal(("effect", view.Instance.EffectId), (name.Kind, name.SheetId));
                Assert.True(name.State == "resolved" ? name.Text is { Length: > 0 } : name.Text is null && name.Reason is not null);
            });
        await sdk.StopCombatObservationAsync(operation.Id); await app.StopAsync();
    }

    [Fact]
    public async Task LiveNoticesFollowEachCaptureStayOutOfReplayAndDisplayLatencyIsRateLimited()
    {
        using var area = new Workspace(); await using var app = Build(area); await app.StartAsync(); using var sdk = Sdk(app);
        string[] topics = ["combat.live.changed", "combat.encounter.closed"];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var notices = new List<(ulong Sequence, JsonElement Data)>(); string? cursor = null;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listening = Task.Run(async () =>
        {
            await foreach (var message in sdk.StreamEventsAsync(new(topics, [], null), timeout.Token))
            {
                cursor ??= message.Id;
                if (message.Event == "combat.live.changed")
                {
                    notices.Add((ulong.Parse(message.Data.GetProperty("resourceRevision").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                        message.Data.GetProperty("data").Clone()));
                    if (notices.Count >= 5) enough.TrySetResult();
                }
                if (message.Event == "combat.encounter.closed") return;
            }
        }, timeout.Token);
        await Task.Delay(200, timeout.Token);
        // 持续的模拟场景：每 25 ms 一帧、伤害按三步周期重复，首领血量随之下降。
        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id,
            ReplayScenarioId: "live", ReplayIntervalMs: 25), "combat-live-notices");
        await enough.Task.WaitAsync(timeout.Token);
        await sdk.StopCombatObservationAsync(operation.Id, timeout.Token);
        await listening;
        // 每条通知指向本次观察、工作区修订随捕获递增，通知序号严格递增；伤害带来的变化带抓包时点。
        Assert.All(notices, notice => Assert.Equal(operation.Id, notice.Data.GetProperty("observationId").GetString()));
        Assert.True(notices.Zip(notices.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        var revisions = notices.Select(notice => ulong.Parse(notice.Data.GetProperty("workspaceRevision").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(revisions.Zip(revisions.Skip(1)).All(pair => pair.First < pair.Second));
        Assert.Contains(notices, notice => notice.Data.TryGetProperty("capturedMonotonicMs", out var value) && value.ValueKind == JsonValueKind.Number);
        // 带游标重连：重放中有终态事件，没有即时通知。
        using var resume = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var replayed = new List<string>();
        await foreach (var message in sdk.StreamEventsAsync(new(topics, [], cursor), resume.Token))
        {
            replayed.Add(message.Event);
            if (message.Event == "combat.encounter.closed") break;
        }
        Assert.Contains("combat.encounter.closed", replayed);
        Assert.DoesNotContain("combat.live.changed", replayed);

        // 显示延迟报告：同一客户端 10 秒内只记录第一次；统计值不合法时拒绝。
        Assert.True((await sdk.ReportCombatDisplayLatencyAsync(new(12, 40, 21.5, 48, 70))).Recorded);
        Assert.False((await sdk.ReportCombatDisplayLatencyAsync(new(12, 52, 22, 50, 71))).Recorded);
        var invalid = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.ReportCombatDisplayLatencyAsync(new(3, 3, 80, 20, 90)));
        Assert.Equal("request.invalid", invalid.Problem?.Code);
        await app.StopAsync();
    }

    private static async Task<List<JsonElement>> ReadEffectNotices(MabiToolClient sdk, EventStreamOptions filter, string encounter)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var notices = new List<JsonElement>();
        await foreach (var message in sdk.StreamEventsAsync(filter, timeout.Token))
        {
            if (message.Event == "combat.effects.changed") notices.Add(message.Data.GetProperty("data").Clone());
            if (message.Event == "combat.encounter.closed" && message.Data.GetProperty("data").GetProperty("encounterId").GetString() == encounter) return notices;
        }
        throw new InvalidOperationException("缺少战斗终态事件");
    }

    private static void AssertDetail(CombatEncounter encounter)
    {
        var skill = Assert.Single(encounter.Skills);
        Assert.Equal("1", skill.PowerfulHitCount.ToString()); Assert.Equal("0", skill.RapidHitCount.ToString());
        Assert.Equal("0", skill.DoubleHitCount.ToString());
        Assert.Equal("1", skill.CriticalCount.ToString());
        Assert.Equal("2", skill.CriticalEligibleCount.ToString()); Assert.Equal("1", skill.MainHitEligibleCount.ToString());
        Assert.Equal(new CombatDamageClassificationCounts(new(1), new(1), new(1), new(0)), skill.ClassificationCounts);
        Assert.Equal("1200", skill.MinimumDamage.ToString()); Assert.Equal("1200", skill.MaximumDamage.ToString());
        // 无防备、破防等五项特性与元素统计均包含间接及持续伤害。
        Assert.Equal("2", skill.BreakedHitCount.ToString()); Assert.Equal("0", skill.BreakOccurredCount.ToString());
        Assert.Equal("1", skill.FirstAttackCount.ToString()); Assert.Equal("0", skill.DefaultAttackCount.ToString());
        Assert.Equal("1", skill.MultiHitCount.ToString());
        Assert.Equal(new CombatDamageElementCounts(new(0), new(0), new(1), new(0), new(1), new(1), new(0), new(0)), skill.ElementCounts);
    }

    [Fact]
    public async Task TwoClientsShareSnapshotsResumeWithoutAccumulationAndKeepTargetAndPrincipalIdentity()
    {
        using var area = new Workspace(); await using var app = Build(area); await app.StartAsync(); using var owner = Sdk(app);
        var source = (CombatReplaySource)app.Services.GetRequiredService<MabiTool.Modules.CombatObservation.Application.ICombatObservationSource>();
        Assert.Equal(0, source.ActiveSubscriptions);
        var capabilities = (await owner.GetCombatCapabilitiesAsync()).Capabilities;
        Assert.All(capabilities.Where(x => x.Id != "combat.healing.local"), x => Assert.Equal(CapabilityStatus.Available, x.Status.KnownValue));
        // 合成回放不产生治疗，治疗能力单独声明不可用。
        Assert.Equal(("combat.sourceUnavailable", CapabilityStatus.Unavailable), capabilities.Single(x => x.Id == "combat.healing.local") is var healing
            ? (healing.Reason, healing.Status.KnownValue!.Value) : default);
        var challenge = await owner.CreatePairingChallengeAsync(new("combat-shared", null, [ApiPermissions.SystemRead, ApiPermissions.CombatControl]));
        var grant = await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        using var first = Sdk(app, grant.Token); using var second = Sdk(app, grant.Token);
        var before = await first.GetCombatSnapshotAsync(); var request = new CombatObservationRequest((await first.GetGameBindingAsync()).Binding!.Session.Id, ReplayIntervalMs: 400);
        var operation = await first.StartCombatObservationAsync(request, "combat-shared-run");
        Assert.Equal(operation.Id, (await second.StartCombatObservationAsync(request, "combat-shared-run")).Id);
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() => first.StartCombatObservationAsync(request with { IncludeOwnedEntities = false }, "combat-shared-run"))).Problem!.Code);
        var final = await Terminal(first, operation.Id); Assert.Equal(OperationState.Succeeded, final.State.KnownValue);
        // 名称在读取时投影：名称表首次读取结束前后，同一修订的名称原因会从 loading 变为读取结果，先等首次读取结束再比较两个客户端。
        var names = app.Services.GetRequiredService<MabiTool.Host.Combat.CombatNameCatalog>();
        using (var settle = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (names.Resolve(MabiTool.Modules.CombatObservation.Domain.CombatNames.Monster, 1).Reason == "combat.nameCatalogLoading")
                await Task.Delay(20, settle.Token);
        var snapshot = await first.GetCombatSnapshotAsync(); var other = await second.GetCombatSnapshotAsync();
        Assert.Equal(Json(snapshot.Data), Json(other.Data)); Assert.Equal(snapshot.ResourceRevision, other.ResourceRevision);
        Assert.Equal(snapshot.Data!.Revision, snapshot.ResourceRevision); Assert.Equal(other.Data!.Revision, other.ResourceRevision);
        var encounter = snapshot.Data!.Encounter!; Assert.Equal("3600", encounter.Summary.AttributedDamage.ToString());
        // 伤害在回放时点 0、1000、2000：DPS 分母截止最后一次伤害，确定为 2000。实际时长（durationMs）截止观察停止时点：
        // 来源结束前若例行检查已按经过时间推进观察时点（回放 400 ms 一步即 2.5 倍速），停止时点会晚于最后一帧，因此按停止时点核对。
        var stoppedAt = snapshot.Data.Observation.MonotonicMs;
        Assert.Equal(((double?)2000, (double?)1800), (encounter.Summary.DpsDurationMs, encounter.Summary.EncounterDps));
        Assert.Equal((0d, (double?)stoppedAt, stoppedAt), (encounter.StartedMonotonicMs, encounter.EndedMonotonicMs, encounter.Summary.DurationMs));
        Assert.True(stoppedAt >= 2000, stoppedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2, encounter.Targets.Count); Assert.Equal(0, source.ActiveSubscriptions);
        var monsters = snapshot.Data.Entities.Where(x => x.Role.KnownValue == CombatEntityRole.Monster).ToArray();
        Assert.Equal(2, monsters.Length); Assert.Equal(monsters[0].RawName, monsters[1].RawName); Assert.NotEqual(monsters[0].Key.RawInstanceId, monsters[1].Key.RawInstanceId);
        var a = await first.GetCombatEntityEffectsAsync(monsters[0].Id); var b = await second.GetCombatEntityEffectsAsync(monsters[1].Id);
        Assert.Equal(monsters[0].Key, a.Snapshot.EntityKey); Assert.Equal(monsters[1].Key, b.Snapshot.EntityKey);
        Assert.NotEqual(Assert.Single(a.Views).Instance.InstanceId, Assert.Single(b.Views).Instance.InstanceId);
        Assert.Equal(Json(a), Json(await second.GetCombatEntityEffectsAsync(monsters[0].Id)));
        var timeline = await first.QueryCombatTimelineAsync(encounter.Id, new(EntityId: monsters[0].Id, BucketWidthMs: 1000));
        Assert.All(timeline.EffectCoverage, x => Assert.Equal(monsters[0].Id, x.EntityId));
        var filter = new EventStreamOptions(Topics, [], before.EventCursor);
        Assert.Equal(await ReadClosed(first, filter, encounter.Id), await ReadClosed(second, filter, encounter.Id));
        Assert.Equal("3600", (await second.GetCombatEncounterAsync(encounter.Id)).Summary.AttributedDamage.ToString());
        var readerChallenge = await owner.CreatePairingChallengeAsync(new("other-principal", null, [ApiPermissions.SystemRead]));
        var readerGrant = await owner.RedeemPairingAsync(new(readerChallenge.ChallengeId, readerChallenge.Code)); using var reader = Sdk(app, readerGrant.Token);
        Assert.Null((await reader.GetCombatSnapshotAsync()).Data); Assert.Empty((await reader.GetCombatEncountersAsync()).Items);
        Assert.Empty((await reader.GetEventSnapshotAsync(new(Topics, [], null))).Resources);
        Assert.Equal(404, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.GetCombatEncounterAsync(encounter.Id))).Status);
        Assert.Equal("combat.entityOutOfScope", (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.GetCombatEntityEffectsAsync(monsters[0].Id))).Problem!.Code);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.StartCombatObservationAsync(request, "combat-reader-denied"))).Status);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("partial", OperationState.Succeeded, "3600")]
    [InlineData("failure", OperationState.Interrupted, "1200")]
    public async Task SourceGapsAndFailuresKeepCommittedHistoryAndReleaseSubscriptions(string scenario, OperationState state, string amount)
    {
        using var area = new Workspace(); await using var app = Build(area); await app.StartAsync(); using var sdk = Sdk(app);
        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id, ReplayScenarioId: scenario, ReplayIntervalMs: 25), "combat-" + scenario);
        var final = await Terminal(sdk, operation.Id); Assert.Equal(state, final.State.KnownValue);
        var encounter = (await sdk.GetCombatSnapshotAsync()).Data!.Encounter!;
        Assert.True(encounter.Summary.Final); Assert.Equal(ObservationQuality.Partial, encounter.Summary.Quality.KnownValue);
        Assert.Equal(amount, encounter.Summary.AttributedDamage.ToString());
        Assert.Equal((double?)(encounter.Summary.DpsDurationMs is > 0 ? (double)encounter.Summary.AttributedDamage.Value * 1000 / encounter.Summary.DpsDurationMs.Value : null), encounter.Summary.EncounterDps);
        Assert.Single((await sdk.GetCombatEncountersAsync()).Items);
        Assert.Equal(0, ((CombatReplaySource)app.Services.GetRequiredService<MabiTool.Modules.CombatObservation.Application.ICombatObservationSource>()).ActiveSubscriptions);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothStopRoutesFenceLateFramesAndKeepOtherModuleDemand(bool generic)
    {
        using var area = new Workspace(); var source = new ControlledCombatSource(); await using var app = Build(area, source); await app.StartAsync(); using var sdk = Sdk(app);
        using var shared = source.HoldOtherModuleSubscription(); var runtime = app.Services.GetRequiredService<ModuleRuntime>();
        await using var otherModule = await runtime.AcquireAsync("game-access", "music-shared-observation");
        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id), "combat-stop-" + generic);
        await WorkspaceReady(sdk); source.Send(1); var committed = await WorkspaceReady(sdk, x => x.Encounter?.Summary.AttributedDamage.ToString() == "1200");
        var final = generic ? await sdk.CancelOperationAsync(operation.Id, new("user-stopped")) : await sdk.StopCombatObservationAsync(operation.Id);
        Assert.Equal(OperationState.Cancelled, final.State.KnownValue); Assert.Equal(1, source.ActiveSubscriptions); Assert.True(source.CancelObserved);
        source.Send(4); await Task.Delay(250);
        var result = (await sdk.GetCombatSnapshotAsync()).Data!; Assert.True(result.Encounter!.Summary.Final);
        Assert.Equal(committed.Encounter!.Summary.AttributedDamage, result.Encounter.Summary.AttributedDamage);
        var modules = await runtime.SnapshotAsync(); Assert.Empty(modules.Items.Single(x => x.Id == "combat-observation").ActiveOperationIds);
        Assert.Contains("music-shared-observation", modules.Items.Single(x => x.Id == "game-access").ActiveOperationIds);
        Assert.DoesNotContain(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities, x => x.Kind == "combat-observation");
        Assert.Equal(final.Id, (await sdk.StopCombatObservationAsync(operation.Id)).Id); await app.StopAsync();
    }

    [Fact]
    public async Task ManualBoundariesAndReceiptsSurviveRestartWithoutReplayingObservation()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource(); string operationId, oldId, resetEtag; CombatEncounter reset;
        await using (var app = Build(area, source))
        {
            await app.StartAsync(); using var sdk = Sdk(app); var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id), "combat-manual-start"); operationId = operation.Id;
            await WorkspaceReady(sdk); var started = await Boundary(sdk, operation.Id, "start", "combat-boundary-start"); oldId = started.Id;
            source.Send(1); await WorkspaceReady(sdk, x => x.Encounter?.Summary.AttributedDamage.ToString() == "1200");
            var pair = await Reset(sdk, operation.Id, "combat-boundary-reset"); reset = pair.Value; resetEtag = pair.ETag;
            Assert.Equal(oldId, reset.Id); Assert.Equal("reset", (await sdk.GetCombatEncounterAsync(oldId)).Summary.BoundaryReason);
            source.Send(2); await WorkspaceReady(sdk, x => x.Encounter?.Summary.AttributedDamage.ToString() == "1200");
            var closed = await Boundary(sdk, operation.Id, "close", "combat-boundary-close");
            Assert.True(closed.Summary.Final); Assert.Equal("manual-end", closed.Summary.BoundaryReason);
            var favored = await sdk.FavoriteCombatEncounterAsync(closed.Id, new(true), "combat-manual-favorite", CombatHistoryStore.ETag(closed)); Assert.True(favored.Favorite);
            Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.FavoriteCombatEncounterAsync(closed.Id, new(false), "combat-stale-favorite", CombatHistoryStore.ETag(closed)))).Problem!.Code);
            await sdk.StopCombatObservationAsync(operation.Id); await app.StopAsync();
        }
        var fresh = new ControlledCombatSource(); await using var restarted = Build(area, fresh); await restarted.StartAsync(); using var next = Sdk(restarted);
        Assert.Equal(2, (await next.GetCombatEncountersAsync()).Items.Count); Assert.Equal(0, fresh.Opened);
        var receipt = await next.ResetCombatEncounterAsync(oldId, new(operationId), "combat-boundary-reset", resetEtag); Assert.Equal(Json(reset), Json(receipt));
        var duplicate = await next.StartCombatObservationAsync(new((await next.GetCombatObservationAsync(operationId)).ResourceRefs[0].Id), "combat-manual-start");
        Assert.Equal(operationId, duplicate.Id); Assert.Equal(OperationState.Cancelled, duplicate.State.KnownValue); Assert.Equal(0, fresh.Opened);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task ManualSplitIsIdempotentSerializedAndOpensTheNextSegmentOnDamage()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource(); await using var app = Build(area, source); await app.StartAsync(); using var sdk = Sdk(app);
        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id), "combat-split-run");
        await WorkspaceReady(sdk);
        var noSegment = (await sdk.GetCombatSnapshotAsync()).Data!; Assert.Null(noSegment.Encounter);
        source.Send(1); var active = (await WorkspaceReady(sdk, x => x.Encounter?.Summary.AttributedDamage.ToString() == "1200")).Encounter!;
        var etag = CombatHistoryStore.ETag(active);

        var challenge = await sdk.CreatePairingChallengeAsync(new("combat-split-reader", null, [ApiPermissions.SystemRead]));
        using var reader = Sdk(app, (await sdk.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code))).Token);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.SplitCombatEncounterAsync(active.Id, new(operation.Id), "combat-split-denied", etag))).Status);

        // 同一边界修订的两个不同幂等键并发提交：只有一个生效。
        var attempts = await Task.WhenAll(new[] { "combat-split-a", "combat-split-b" }.Select(async key =>
        {
            try { return (Key: key, Value: await sdk.SplitCombatEncounterAsync(active.Id, new(operation.Id), key, etag), Code: (string?)null); }
            catch (MabiToolApiException error) { return (Key: key, Value: (CombatEncounter?)null, Code: error.Problem?.Code); }
        }));
        var winner = Assert.Single(attempts, x => x.Value is not null); Assert.Equal("revision.stale", Assert.Single(attempts, x => x.Value is null).Code);
        var ended = winner.Value!;
        Assert.Equal(active.Id, ended.Id); Assert.True(ended.Summary.Final); Assert.Equal("manual-split", ended.Summary.BoundaryReason);
        Assert.Equal((1, "first-damage"), (ended.Segment!.Ordinal, ended.Segment!.StartReason));
        Assert.Equal(Json(ended), Json(await sdk.SplitCombatEncounterAsync(active.Id, new(operation.Id), winner.Key, etag)));
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.SplitCombatEncounterAsync(active.Id, new(operation.Id), winner.Key, "\"combat-boundary-other\""))).Problem!.Code);

        // 已结束的分段再拆段：明确拒绝且不推进边界修订。
        var current = (await sdk.GetCombatSnapshotAsync()).Data!; Assert.Equal(ended.Id, current.Encounter!.Id);
        var inactive = await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.SplitCombatEncounterAsync(ended.Id, new(operation.Id), "combat-split-inactive", CombatHistoryStore.ETag(current.Encounter)));
        Assert.Equal((409, "combat.segmentInactive"), (inactive.Status, inactive.Problem!.Code));
        Assert.Equal(current.BoundaryRevision, (await sdk.GetCombatSnapshotAsync()).Data!.BoundaryRevision);

        source.Send(2);
        var next = (await WorkspaceReady(sdk, x => x.Encounter is { } value && value.Id != ended.Id && value.Summary.AttributedDamage.ToString() == "1200")).Encounter!;
        Assert.Equal((2, "manual-split"), (next.Segment!.Ordinal, next.Segment!.StartReason)); Assert.False(next.Summary.Final);
        Assert.Equal("manual-split", (await sdk.GetCombatEncounterAsync(ended.Id)).Summary.BoundaryReason);

        await sdk.StopCombatObservationAsync(operation.Id);
        var stopped = (await sdk.GetCombatSnapshotAsync()).Data!.Encounter!;
        Assert.Equal("combat.planStale", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.SplitCombatEncounterAsync(stopped.Id, new(operation.Id), "combat-split-stopped", CombatHistoryStore.ETag(stopped)))).Problem!.Code);
        await app.StopAsync();
    }

    [Fact]
    public async Task CapabilityFailuresAreIndependentAndQueriesDoNotStartObservation()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource { SelfEffectsUnavailable = true };
        await using var app = Build(area, source); await app.StartAsync(); using var sdk = Sdk(app);
        var capabilities = await sdk.GetCombatCapabilitiesAsync(); Assert.Equal(CapabilityStatus.Unavailable, capabilities.Capabilities.Single(x => x.Id == "combat.effects.self").Status.KnownValue);
        Assert.Equal(0, source.Opened); var game = (await sdk.GetGameBindingAsync()).Binding!.Session.Id;
        var denied = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.StartCombatObservationAsync(new(game), "combat-capability-denied"));
        Assert.Equal("combat.sourceUnavailable", denied.Problem!.Code); Assert.Equal(0, source.Opened);
        var request = new CombatObservationRequest(game, SelfEffectsEnabled: false, MonsterEffectsEnabled: false);
        var operation = await sdk.StartCombatObservationAsync(request, "combat-damage-only"); await WorkspaceReady(sdk); source.Send(1);
        Assert.Null((await WorkspaceReady(sdk, x => x.Encounter is not null)).SelfEffects);
        await sdk.StopCombatObservationAsync(operation.Id); Assert.Equal(0, source.ActiveSubscriptions); await app.StopAsync();
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static WebApplication Build(Workspace area, ControlledCombatSource? source = null) => BackendApplication.Build(new(0, Token, new HashSet<string>()),
        storage: area.Options, mock: true, combatSource: source is null ? null : _ => source);
    private static MabiToolClient Sdk(WebApplication app, string token = Token) => new(new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), _ => ValueTask.FromResult(token));
    private static async Task<OperationSnapshot> Terminal(MabiToolClient sdk, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true) { var value = await sdk.GetCombatObservationAsync(id, timeout.Token); if (SqliteStore.IsTerminal(value.State.KnownValue)) return value; await Task.Delay(20, timeout.Token); }
    }
    private static async Task<CombatWorkspace> WorkspaceReady(MabiToolClient sdk, Func<CombatWorkspace, bool>? match = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true) { var value = (await sdk.GetCombatSnapshotAsync(timeout.Token)).Data; if (value is not null && (match?.Invoke(value) ?? true)) return value; await Task.Delay(20, timeout.Token); }
    }
    private static async Task<string[]> ReadClosed(MabiToolClient sdk, EventStreamOptions filter, string encounter)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var events = new List<string>();
        await foreach (var message in sdk.StreamEventsAsync(filter, timeout.Token))
        {
            Assert.NotEqual("control", message.Event); events.Add(message.Id + ":" + message.Event);
            if (message.Event == "combat.encounter.closed" && message.Data.GetProperty("data").GetProperty("encounterId").GetString() == encounter) return events.ToArray();
        }
        throw new InvalidOperationException("缺少战斗终态事件");
    }
    private static async Task<CombatEncounter> Boundary(MabiToolClient sdk, string observation, string route, string key)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            string? etag = null; var snapshot = await sdk.GetCombatSnapshotAsync(onResponse: metadata => etag = metadata.ETag);
            try { return route == "start" ? await sdk.StartCombatEncounterAsync(new(observation), key, etag!) :
                await sdk.CloseCombatEncounterAsync(snapshot.Data!.Encounter!.Id, new(observation), key, CombatHistoryStore.ETag(snapshot.Data.Encounter)); }
            catch (MabiToolApiException error) when (error.Problem?.Code == "revision.stale") { }
        }
        throw new InvalidOperationException("战斗边界持续冲突");
    }
    private static async Task<(CombatEncounter Value, string ETag)> Reset(MabiToolClient sdk, string observation, string key)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var value = (await sdk.GetCombatSnapshotAsync()).Data!.Encounter!; var etag = CombatHistoryStore.ETag(value);
            try { return (await sdk.ResetCombatEncounterAsync(value.Id, new(observation), key, etag), etag); }
            catch (MabiToolApiException error) when (error.Problem?.Code == "revision.stale") { }
        }
        throw new InvalidOperationException("战斗重置持续冲突");
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string root;
        public StorageOptions Options => new(Path.Combine(root, "bootstrap"), true, PortableRoot: root);
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent!;
            root = Path.Combine(repository.FullName, "artifacts", "tmp", "combat-api", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(root).Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar + "tmp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }
}
