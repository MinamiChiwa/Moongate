using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Combat;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.Replay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>
/// 定时测量与范围汇总的公共接口：没有观测时拒绝、同一时刻只有一个测量、取消不保存、后端按单调时钟计时并把结果存入历史，
/// 状态变化经 combat.measurement.changed 推送；范围查询与保存记录走同一份历史。
/// </summary>
[Collection("Persistent backend")]
public sealed class CombatMeasurementApiTests
{
    private const string Token = "combat_measure_012345678901234567890123456789012345678901";

    [Fact]
    public async Task MeasurementRunsOnTheBackendClockAndIsSavedWhileCancelledRunsAreNot()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Options, mock: true, combatSource: _ => source);
        await app.StartAsync(); using var sdk = Sdk(app);

        string? settingsETag = null;
        var idle = await sdk.GetCombatMeasurementAsync(onResponse: value => settingsETag = value.ETag);
        Assert.Equal(("idle", false, "combat.observationInactive"), (idle.Phase, idle.Available, idle.UnavailableReason));
        Assert.Equal(new CombatMeasurementSettings(60000, 0), idle.Settings);
        var refused = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.StartCombatMeasurementAsync(new("op-missing", 5000, 0), "measure-refused"));
        Assert.Equal((409, "combat.observationInactive"), (refused.Status, refused.Problem!.Code));
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.SaveCombatMeasurementSettingsAsync(new(4000, 0), "measure-settings-invalid", settingsETag!))).Problem!.Code);
        // 预备倒计时已停用：旧客户端送来的预备秒数仍校验范围，但按 0 保存与返回。
        Assert.Equal(new CombatMeasurementSettings(75000, 0), (await sdk.SaveCombatMeasurementSettingsAsync(new(75000, 3000), "measure-settings-save", settingsETag!)).Settings);

        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id), "measure-observe");
        await WorkspaceReady(sdk); source.Send(1);
        await WorkspaceReady(sdk, x => x.Encounter?.Summary.AttributedDamage.ToString() == "1200");
        var ready = await sdk.GetCombatMeasurementAsync();
        Assert.True(ready.Available); Assert.Equal(operation.Id, ready.ObservationId);

        using var listening = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = Changed(sdk, "waiting", connected, listening.Token);
        await connected.Task.WaitAsync(listening.Token);
        // 等待首次伤害时取消：不保存，状态回到空闲。
        var counting = await sdk.StartCombatMeasurementAsync(new(operation.Id, 5000, 10000), "measure-cancelled");
        Assert.Equal("waiting", counting.Phase); Assert.Null(counting.RemainingMs); Assert.Null(counting.Damage);
        Assert.Equal(Json(counting), Json(await sdk.StartCombatMeasurementAsync(new(operation.Id, 5000, 10000), "measure-cancelled")));
        Assert.Equal("combat.measurementActive", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.StartCombatMeasurementAsync(new(operation.Id, 5000, 0), "measure-second"))).Problem!.Code);
        Assert.Equal("idle", (await sdk.CancelCombatMeasurementAsync(new(counting.MeasurementId!), "measure-cancel-command")).Phase);
        Assert.Equal("combat.measurementInactive", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CancelCombatMeasurementAsync(new(counting.MeasurementId!), "measure-cancel-inactive"))).Problem!.Code);
        Assert.Equal(new CombatMeasurementSettings(5000, 0), (await sdk.GetCombatMeasurementAsync()).Settings);
        await pushed;

        // 计时从本机第一次有效伤害开始：受理后处于等待，伤害到达后才转入计时。
        var running = await sdk.StartCombatMeasurementAsync(new(operation.Id, 5000, 0), "measure-run");
        Assert.Equal("waiting", running.Phase);
        source.Send(2);
        Assert.True((await Until(sdk, x => x.Phase == "running", TimeSpan.FromSeconds(5))).RemainingMs is > 0 and <= 5000);
        var finished = await Until(sdk, x => x.Phase == "idle" && x.LastResult is not null, TimeSpan.FromSeconds(15));
        var result = finished.LastResult!;
        Assert.Equal((5000d, "1200"), (result.DurationMs, result.Damage.ToString()));
        Assert.Equal(240d, result.Dps);
        var record = await sdk.GetCombatEncounterAsync(finished.LastResultId!);
        Assert.Equal("timed-measurement", record.Record!.Kind); Assert.True(record.Summary.Final);
        Assert.DoesNotContain((await sdk.GetCombatEncountersAsync()).Items, x => x.Record?.Kind == "timed-measurement" && x.Id != record.Id);

        var range = await sdk.QueryCombatRangeAsync(new("encounter", record.Id));
        Assert.Equal("1200", range.Result.Summary.AttributedDamage.ToString());
        var saved = await sdk.SaveCombatRangeAsync(new(new("encounter", record.Id), "木桩 1 分钟"), "measure-save");
        Assert.Equal(("timed-measurement", "木桩 1 分钟", true), (saved.Record!.Kind, saved.Record.Name, saved.Favorite));

        await sdk.StopCombatObservationAsync(operation.Id);
        Assert.Equal("combat.observationInactive", (await sdk.GetCombatMeasurementAsync()).UnavailableReason);
        Assert.Equal(Json(running), Json(await sdk.StartCombatMeasurementAsync(new(operation.Id, 5000, 0), "measure-run")));
        Assert.Equal("combat.observationInactive", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.StartCombatMeasurementAsync(new(operation.Id, 5000, 0), "measure-after-stop"))).Problem!.Code);
        await app.StopAsync();
    }

    [Fact]
    public async Task MeasurementRestartWaitsForCheckpointWithoutBlockingCancellation()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Options, mock: true, combatSource: _ => source);
        await app.StartAsync(); using var sdk = Sdk(app);
        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id), "measure-concurrent-observe");
        await WorkspaceReady(sdk); source.Send(1);
        await WorkspaceReady(sdk, value => value.Encounter?.Summary.AttributedDamage.ToString() == "1200");
        var cancelled = await sdk.StartCombatMeasurementAsync(new(operation.Id, 5000, 10000), "measure-before-restart");
        await sdk.CancelCombatMeasurementAsync(new(cancelled.MeasurementId!), "measure-before-restart-cancel");

        var combat = app.Services.GetRequiredService<CombatOperations>();
        var database = app.Services.GetRequiredService<SqliteStore>();
        using var resume = new ManualResetEventSlim();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        // 检查点已提交，观察循环仍等回执并持有任务锁。
        database.WriteTimingObserver = timing =>
        {
            if (!timing.Callback.Contains(nameof(OperationStore), StringComparison.Ordinal) ||
                !timing.Callback.Contains(nameof(OperationStore.CommitCombatAsync), StringComparison.Ordinal) ||
                Interlocked.CompareExchange(ref paused, 1, 0) != 0) return;
            reached.TrySetResult(); resume.Wait(TimeSpan.FromSeconds(10));
        };
        Task<CombatMeasurementState>? started = null, duplicate = null;
        try
        {
            source.Send(2);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = new CombatMeasurementStartRequest(operation.Id, 5000, 10000);
            using (var cancellation = new CancellationTokenSource())
            {
                var waiting = combat.StartMeasurementAsync("owner", true, request, "measure-concurrent-restart", cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            started = combat.StartMeasurementAsync("owner", true, request, "measure-concurrent-restart");
            duplicate = combat.StartMeasurementAsync("owner", true, request, "measure-concurrent-restart");
            var refused = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CancelCombatMeasurementAsync(new(cancelled.MeasurementId!), "measure-concurrent-idle-cancel").WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("combat.measurementInactive", refused.Problem!.Code);
            Assert.False(started.IsCompleted); Assert.False(duplicate.IsCompleted);
            resume.Set();
            var counting = await started.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("waiting", counting.Phase);
            Assert.NotEqual(cancelled.MeasurementId, counting.MeasurementId);
            Assert.Equal(Json(counting), Json(await duplicate.WaitAsync(TimeSpan.FromSeconds(5))));
            await sdk.CancelCombatMeasurementAsync(new(counting.MeasurementId!), "measure-concurrent-cancel");
            source.Send(4);
            await WorkspaceReady(sdk, value => value.Encounter?.Summary.AttributedDamage.ToString() == "3600");
            Assert.DoesNotContain((await sdk.GetCombatEncountersAsync()).Items, value => value.Record?.Kind == "timed-measurement");
            await sdk.StopCombatObservationAsync(operation.Id);
        }
        finally
        {
            resume.Set(); database.WriteTimingObserver = null;
            if (started is not null && duplicate is not null) await Task.WhenAll(started, duplicate).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task AutoObservationIsOnByDefaultWaitsForCaptureAndCanBeTurnedOff()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource();
        await using (var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Options, mock: true, combatSource: _ => source))
        {
            await app.StartAsync(); using var sdk = Sdk(app);
            // 模拟宿主没有运行共享抓包：自动观测开启但不开始观测，原因写明抓包未运行。
            string? etag = null;
            var initial = await sdk.GetCombatAutoObservationAsync(onResponse: value => etag = value.ETag);
            Assert.True(initial.Enabled); Assert.Equal("off", initial.State); Assert.NotNull(initial.Reason);
            Assert.Equal(0, source.Opened);
            var off = await sdk.SetCombatAutoObservationAsync(new(false), "measure-auto-off", etag!);
            Assert.Equal((false, "off", "combat.autoDisabled"), (off.Enabled, off.State, off.Reason));
            await app.StopAsync();
        }
        await using var restarted = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Options, mock: true, combatSource: _ => source);
        await restarted.StartAsync(); using var next = Sdk(restarted);
        Assert.False((await next.GetCombatAutoObservationAsync()).Enabled);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task PreferencesUseIndependentRevisionsConditionalWritesAndDurablePrincipalReceipts()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource(); var clock = new StorageClock();
        var receiptTime = clock.GetUtcNow();
        CombatMeasurementState savedSettings; CombatAutoObservationState disabled;
        string originalSettingsETag, originalAutoETag;
        await using (var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), clock: clock, storage: area.Options, mock: true, combatSource: _ => source))
        {
            await app.StartAsync(); using var owner = Sdk(app);
            string? settingsETag = null, autoETag = null;
            var initial = await owner.GetCombatMeasurementAsync(onResponse: value => settingsETag = value.ETag);
            var automatic = await owner.GetCombatAutoObservationAsync(onResponse: value => autoETag = value.ETag);
            Assert.Equal(new UInt64String(0), initial.SettingsRevision); Assert.Equal(new UInt64String(0), automatic.Revision);
            Assert.Equal("\"combat-measurement-settings-0\"", settingsETag);
            Assert.Equal("\"combat-auto-observation-0\"", autoETag);
            originalSettingsETag = settingsETag!; originalAutoETag = autoETag!;

            var missingKey = await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SaveCombatMeasurementSettingsAsync(new(75000, 3000), null!, settingsETag!));
            Assert.Equal((400, "idempotency.required"), (missingKey.Status, missingKey.Problem!.Code));
            var missingCondition = await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SaveCombatMeasurementSettingsAsync(new(75000, 3000), "settings-no-condition", null!));
            Assert.Equal((428, "revision.required"), (missingCondition.Status, missingCondition.Problem!.Code));
            var weakCondition = await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SaveCombatMeasurementSettingsAsync(new(75000, 3000), "settings-weak-condition", "W/" + settingsETag));
            Assert.Equal((400, "request.invalid"), (weakCondition.Status, weakCondition.Problem!.Code));
            var autoMissing = await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SetCombatAutoObservationAsync(new(false), "auto-no-condition", null!));
            Assert.Equal((428, "revision.required"), (autoMissing.Status, autoMissing.Problem!.Code));

            savedSettings = await owner.SaveCombatMeasurementSettingsAsync(new(75000, 3000), "settings-durable", settingsETag!, onResponse: value => settingsETag = value.ETag);
            disabled = await owner.SetCombatAutoObservationAsync(new(false), "auto-durable", autoETag!, onResponse: value => autoETag = value.ETag);
            Assert.Equal(new UInt64String(1), savedSettings.SettingsRevision); Assert.Equal(new UInt64String(1), disabled.Revision);
            Assert.Equal("\"combat-measurement-settings-1\"", settingsETag);
            Assert.Equal("\"combat-auto-observation-1\"", autoETag);
            var settingsStale = await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SaveCombatMeasurementSettingsAsync(new(80000, 0), "settings-stale", originalSettingsETag));
            var autoStale = await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SetCombatAutoObservationAsync(new(true), "auto-stale", originalAutoETag));
            Assert.Equal((412, "revision.stale"), (settingsStale.Status, settingsStale.Problem!.Code));
            Assert.Equal((412, "revision.stale"), (autoStale.Status, autoStale.Problem!.Code));

            var challenge = await owner.CreatePairingChallengeAsync(new("measure-controller", null, [ApiPermissions.SystemRead, ApiPermissions.CombatControl]));
            var grant = await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code)); using var other = Sdk(app, grant.Token);
            // 回执按主体区分；另一主体使用同一键仍须通过当前修订检查。
            Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() => other.SaveCombatMeasurementSettingsAsync(new(75000, 3000), "settings-durable", originalSettingsETag))).Problem!.Code);
            Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() => other.SetCombatAutoObservationAsync(new(false), "auto-durable", originalAutoETag))).Problem!.Code);
            var readerChallenge = await owner.CreatePairingChallengeAsync(new("measure-reader", null, [ApiPermissions.SystemRead]));
            var readerGrant = await owner.RedeemPairingAsync(new(readerChallenge.ChallengeId, readerChallenge.Code)); using var reader = Sdk(app, readerGrant.Token);
            Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.SaveCombatMeasurementSettingsAsync(new(75000, 3000), "settings-durable", originalSettingsETag))).Status);
            Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.SetCombatAutoObservationAsync(new(false), "auto-durable", originalAutoETag))).Status);

            await owner.SaveCombatMeasurementSettingsAsync(new(80000, 0), "settings-later", settingsETag!);
            await owner.SetCombatAutoObservationAsync(new(true), "auto-later", autoETag!);
            Assert.Equal(Json(savedSettings), Json(await owner.SaveCombatMeasurementSettingsAsync(new(75000, 3000), "settings-durable", originalSettingsETag)));
            Assert.Equal(Json(disabled), Json(await owner.SetCombatAutoObservationAsync(new(false), "auto-durable", originalAutoETag)));
            Assert.True((await owner.GetCombatAutoObservationAsync()).Enabled);
            Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SaveCombatMeasurementSettingsAsync(new(90000, 0), "settings-durable", originalSettingsETag))).Problem!.Code);
            Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SetCombatAutoObservationAsync(new(true), "auto-durable", originalAutoETag))).Problem!.Code);

            var operation = await owner.StartCombatObservationAsync(new((await owner.GetGameBindingAsync()).Binding!.Session.Id), "settings-stats-observe");
            await WorkspaceReady(owner); source.Send(1, 0);
            await WorkspaceReady(owner, value => value.Encounter?.Summary.AttributedDamage.ToString() == "1200");
            var counting = await owner.StartCombatMeasurementAsync(new(operation.Id, 5000, 1000), "settings-stats-measure");
            string? runningETag = null;
            var beforeFrame = await owner.GetCombatMeasurementAsync(onResponse: value => runningETag = value.ETag);
            var autoBeforeFrame = await owner.GetCombatAutoObservationAsync();
            clock.Advance(TimeSpan.FromMilliseconds(1000)); source.Send(2, 1000);
            await WorkspaceReady(owner, value => value.Encounter?.Summary.AttributedDamage.ToString() == "2400");
            string? afterFrameETag = null;
            var afterFrame = await owner.GetCombatMeasurementAsync(onResponse: value => afterFrameETag = value.ETag);
            Assert.Equal("running", afterFrame.Phase); Assert.Equal(beforeFrame.SettingsRevision, afterFrame.SettingsRevision);
            Assert.Equal(runningETag, afterFrameETag); Assert.Equal(autoBeforeFrame.Revision, (await owner.GetCombatAutoObservationAsync()).Revision);
            await owner.CancelCombatMeasurementAsync(new(counting.MeasurementId!), "settings-stats-cancel");
            await owner.StopCombatObservationAsync(operation.Id); await app.StopAsync();
        }
        clock.Advance(receiptTime.AddHours(24) - clock.GetUtcNow());
        await using var restarted = BackendApplication.Build(new(0, Token, new HashSet<string>()), clock: clock, storage: area.Options, mock: true, combatSource: _ => source);
        await restarted.StartAsync(); using var next = Sdk(restarted);
        Assert.Equal(Json(savedSettings), Json(await next.SaveCombatMeasurementSettingsAsync(new(75000, 3000), "settings-durable", originalSettingsETag)));
        Assert.Equal(Json(disabled), Json(await next.SetCombatAutoObservationAsync(new(false), "auto-durable", originalAutoETag)));
        Assert.Equal(new CombatMeasurementSettings(5000, 0), (await next.GetCombatMeasurementAsync()).Settings);
        Assert.True((await next.GetCombatAutoObservationAsync()).Enabled);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task MeasurementReceiptsSurviveRestartWithoutReplayingAndOldCancellationKeepsTheNewRun()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource(); var clock = new StorageClock();
        CombatMeasurementState first, cancelled, second; CombatMeasurementStartRequest firstRequest, secondRequest;
        await using (var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), clock: clock, storage: area.Options, mock: true, combatSource: _ => source))
        {
            await app.StartAsync(); using var owner = Sdk(app);
            var operation = await owner.StartCombatObservationAsync(new((await owner.GetGameBindingAsync()).Binding!.Session.Id), "receipt-observe");
            await WorkspaceReady(owner); source.Send(1, 0);
            await WorkspaceReady(owner, value => value.Encounter?.Summary.AttributedDamage.ToString() == "1200");
            firstRequest = new(operation.Id, 5000, 10000); secondRequest = new(operation.Id, 5000, 0);
            first = await owner.StartCombatMeasurementAsync(firstRequest, "receipt-first-start");
            var challenge = await owner.CreatePairingChallengeAsync(new("measure-other", null, [ApiPermissions.SystemRead, ApiPermissions.CombatControl]));
            var grant = await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code)); using var other = Sdk(app, grant.Token);
            Assert.Equal("combat.measurementActive", (await Assert.ThrowsAsync<MabiToolApiException>(() => other.StartCombatMeasurementAsync(firstRequest, "receipt-first-start"))).Problem!.Code);
            cancelled = await owner.CancelCombatMeasurementAsync(new(first.MeasurementId!), "receipt-first-cancel");
            second = await owner.StartCombatMeasurementAsync(secondRequest, "receipt-second-start");
            Assert.NotEqual(first.MeasurementId, second.MeasurementId);
            Assert.Equal(Json(first), Json(await owner.StartCombatMeasurementAsync(firstRequest, "receipt-first-start")));
            Assert.Equal(Json(cancelled), Json(await owner.CancelCombatMeasurementAsync(new(first.MeasurementId!), "receipt-first-cancel")));
            Assert.Equal("combat.measurementInactive", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.CancelCombatMeasurementAsync(new(first.MeasurementId!), "receipt-old-unprocessed"))).Problem!.Code);
            Assert.Equal("combat.measurementInactive", (await Assert.ThrowsAsync<MabiToolApiException>(() => other.CancelCombatMeasurementAsync(new(first.MeasurementId!), "receipt-first-cancel"))).Problem!.Code);
            Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.CancelCombatMeasurementAsync(new(second.MeasurementId!), "receipt-first-cancel"))).Problem!.Code);
            Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.StartCombatMeasurementAsync(firstRequest with { DurationMs = 6000 }, "receipt-first-start"))).Problem!.Code);
            Assert.Equal(second.MeasurementId, (await owner.GetCombatMeasurementAsync()).MeasurementId);
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.CancelCombatMeasurementAsync(new(""), "receipt-no-measurement-id"))).Problem!.Code);
            await owner.CancelCombatMeasurementAsync(new(second.MeasurementId!), "receipt-second-cancel");
            await owner.StopCombatObservationAsync(operation.Id); await app.StopAsync();
        }
        var opened = source.Opened; clock.Advance(TimeSpan.FromHours(24));
        await using var restarted = BackendApplication.Build(new(0, Token, new HashSet<string>()), clock: clock, storage: area.Options, mock: true, combatSource: _ => source);
        await restarted.StartAsync(); using var next = Sdk(restarted);
        Assert.Equal(Json(first), Json(await next.StartCombatMeasurementAsync(firstRequest, "receipt-first-start")));
        Assert.Equal(Json(second), Json(await next.StartCombatMeasurementAsync(secondRequest, "receipt-second-start")));
        Assert.Equal(Json(cancelled), Json(await next.CancelCombatMeasurementAsync(new(first.MeasurementId!), "receipt-first-cancel")));
        var idle = await next.GetCombatMeasurementAsync(); Assert.Equal(("idle", false), (idle.Phase, idle.Available));
        Assert.Equal(opened, source.Opened); Assert.Equal(0, source.ActiveSubscriptions);
        Assert.DoesNotContain((await next.GetCombatEncountersAsync()).Items, value => value.Record?.Kind == "timed-measurement");
        await restarted.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptCapacityRollsBackAdmissionAndKeepsUnexpiredKeys(bool bytes)
    {
        using var area = new Workspace(); var source = new ControlledCombatSource(); var clock = new StorageClock();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), clock: clock, storage: area.Options, mock: true, combatSource: _ => source);
        await app.StartAsync(); using var sdk = Sdk(app);
        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id), "capacity-observe");
        await WorkspaceReady(sdk); source.Send(1, 0);
        var initialWorkspace = await WorkspaceReady(sdk, value => value.Encounter?.Summary.AttributedDamage.ToString() == "1200");
        var request = new CombatMeasurementStartRequest(operation.Id, 5000, 10000);
        var kept = await sdk.StartCombatMeasurementAsync(request, "capacity-kept-start");
        var startedCheckpoint = await WorkspaceReady(sdk, value => value.BoundaryRevision != initialWorkspace.BoundaryRevision);
        var cancelled = await sdk.CancelCombatMeasurementAsync(new(kept.MeasurementId!), "capacity-kept-cancel");
        var before = await sdk.GetCombatMeasurementAsync();
        var workspace = await WorkspaceReady(sdk, value => value.Revision.Value > startedCheckpoint.Revision.Value);
        var database = app.Services.GetRequiredService<SqliteStore>();
        await SeedReceiptCapacity(database, clock, bytes);
        var refused = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.StartCombatMeasurementAsync(request with { DurationMs = 6000 }, "capacity-retry-start"));
        Assert.Equal((429, "resource.capacityExceeded"), (refused.Status, refused.Problem!.Code));
        Assert.Equal(Json(before), Json(await sdk.GetCombatMeasurementAsync()));
        var checkpoint = await WorkspaceReady(sdk, value => value.Revision.Value > workspace.Revision.Value);
        Assert.Equal(workspace.BoundaryRevision, checkpoint.BoundaryRevision);
        Assert.DoesNotContain((await sdk.GetCombatEncountersAsync()).Items, value => value.Record?.Kind == "timed-measurement");
        Assert.Equal(Json(kept), Json(await sdk.StartCombatMeasurementAsync(request, "capacity-kept-start")));
        Assert.Equal(Json(cancelled), Json(await sdk.CancelCombatMeasurementAsync(new(kept.MeasurementId!), "capacity-kept-cancel")));
        await database.WriteAsync(tx => tx.Execute("DELETE FROM combat_receipts WHERE route='capacity-seed'"));
        var next = await sdk.StartCombatMeasurementAsync(request with { DurationMs = 6000 }, "capacity-retry-start");
        await SeedReceiptCapacity(database, clock, bytes);
        var cancelRefused = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CancelCombatMeasurementAsync(new(next.MeasurementId!), "capacity-retry-cancel"));
        Assert.Equal((429, "resource.capacityExceeded"), (cancelRefused.Status, cancelRefused.Problem!.Code));
        var stillRunning = await sdk.GetCombatMeasurementAsync();
        Assert.Equal(next.MeasurementId, stillRunning.MeasurementId); Assert.Equal(next.Revision, stillRunning.Revision);
        await database.WriteAsync(tx => tx.Execute("DELETE FROM combat_receipts WHERE route='capacity-seed'"));
        Assert.Equal("idle", (await sdk.CancelCombatMeasurementAsync(new(next.MeasurementId!), "capacity-retry-cancel")).Phase);
        await sdk.StopCombatObservationAsync(operation.Id); await app.StopAsync();
    }

    [Fact]
    public async Task RevisionMigrationPreservesV3Preferences()
    {
        using var area = new Workspace();
        using (var lease = DataRootLease.Open(area.Options))
        {
            await using var database = SqliteStore.Open(lease, CombatHistoryStore.Migrations.Take(3).ToArray(), mock: true);
            await database.WriteAsync(tx =>
            {
                tx.Execute("INSERT INTO combat_preferences VALUES(1,$json)", ("$json", Json(new CombatMeasurementSettings(95000, 2000))));
                return tx.Execute("INSERT INTO combat_auto_observation VALUES(1,0)");
            });
        }
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Options, mock: true);
        await app.StartAsync(); using var sdk = Sdk(app);
        string? settingsETag = null, autoETag = null;
        var settings = await sdk.GetCombatMeasurementAsync(onResponse: value => settingsETag = value.ETag);
        var automatic = await sdk.GetCombatAutoObservationAsync(onResponse: value => autoETag = value.ETag);
        Assert.Equal(new CombatMeasurementSettings(95000, 0), settings.Settings); Assert.Equal(new UInt64String(0), settings.SettingsRevision);
        Assert.False(automatic.Enabled); Assert.Equal(new UInt64String(0), automatic.Revision);
        Assert.Equal(new UInt64String(1), (await sdk.SaveCombatMeasurementSettingsAsync(new(96000, 3000), "migration-settings-save", settingsETag!)).SettingsRevision);
        Assert.Equal(new UInt64String(1), (await sdk.SetCombatAutoObservationAsync(new(true), "migration-auto-save", autoETag!)).Revision);
        await app.StopAsync();
    }

    [Fact]
    public void MeasurementCandidateAbortRestoresBoundaryAndKeepsEarlierDeletedRecords()
    {
        var at = DateTimeOffset.UtcNow; var local = new EntityKey("game-candidate", new(1), new(20), new(1)); var index = 0;
        var session = new CombatSession("owner", "op-candidate", ReplayProvider.Source,
            CombatReplayFrames.Entity(local, CombatEntityRole.LocalPlayer, "測試角色"), new(local.GameSessionId), at,
            encounterIdFactory: () => "enc-candidate-" + ++index);
        var previous = session.BeginMeasurement(at, 0, 5000, 0); Assert.True(session.CancelMeasurement());
        var before = session.Capture();
        var candidate = session.BeginMeasurementCandidate(at.AddMilliseconds(1000), 1000, 6000, 1000);
        Assert.Equal(candidate.Id, session.MeasurementProgress()!.Id);
        session.AbortMeasurementStart(candidate);
        var after = session.Capture();
        Assert.Null(session.MeasurementProgress()); Assert.Equal(before.Workspace.BoundaryRevision, after.Workspace.BoundaryRevision);
        // 等待首次伤害时取消的测量还没有记录，不登记删除；撤销候选不改变已有的删除登记。
        Assert.DoesNotContain(previous, after.DeletedEncounters); Assert.Equal(before.DeletedEncounters, after.DeletedEncounters);
        Assert.DoesNotContain(after.Encounters, value => value.Id == candidate.Id);
        var accepted = session.BeginMeasurement(at.AddMilliseconds(2000), 2000, 7000, 2000);
        Assert.Equal((accepted, true, 2000d), session.MeasurementProgress() is { } progress ? (progress.Id, progress.Waiting, progress.StartMs) : default);
    }

    [Fact]
    public async Task TailFrameInsideTheSettlePeriodIsCountedAfterLocalTicksPassTheEnd()
    {
        using var area = new Workspace(); var source = new ControlledCombatSource(); var clock = new StorageClock();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), clock: clock, storage: area.Options, mock: true, combatSource: _ => source);
        await app.StartAsync(); using var sdk = Sdk(app);
        var operation = await sdk.StartCombatObservationAsync(new((await sdk.GetGameBindingAsync()).Binding!.Session.Id), "tail-observe");
        await WorkspaceReady(sdk); source.Send(1, 0);
        await WorkspaceReady(sdk, value => value.Encounter?.Summary.AttributedDamage.ToString() == "1200");
        var running = await sdk.StartCombatMeasurementAsync(new(operation.Id, 5000, 0), "tail-measure");
        // 首击在 100 ms：窗口为 [100, 5100]。
        clock.Advance(TimeSpan.FromMilliseconds(100)); source.Send(2, 100);
        await Until(sdk, value => value.Phase == "running", TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(5100));
        var firstTick = await WorkspaceReady(sdk, value => value.Observation.MonotonicMs >= 5200);
        await WorkspaceReady(sdk, value => value.Revision.Value > firstTick.Revision.Value);
        Assert.Equal(running.MeasurementId, (await sdk.GetCombatMeasurementAsync()).MeasurementId);
        // 帧在结束后到达，伤害发生于窗口尾部；帧内只迟到 150 ms，仍受原有 250 ms 校验。
        source.Send(3, 5200, 5050);
        var finished = await Until(sdk, value => value.Phase == "idle" && value.LastResult is not null, TimeSpan.FromSeconds(5));
        Assert.Equal("2400", finished.LastResult!.Damage.ToString()); Assert.Equal(480d, finished.LastResult.Dps);
        Assert.Equal(4950d, finished.LastResult.LastDamageMs); Assert.True(finished.LastResult.Complete);
        await WorkspaceReady(sdk, value => value.Encounter?.Summary.AttributedDamage.ToString() == "3600");
        var record = await sdk.GetCombatEncounterAsync(finished.LastResultId!);
        Assert.Equal("2400", record.Summary.AttributedDamage.ToString());
        await sdk.StopCombatObservationAsync(operation.Id); await app.StopAsync();
    }

    private static Task SeedReceiptCapacity(SqliteStore database, StorageClock clock, bool bytes) => database.WriteAsync(tx =>
    {
        var expiry = clock.GetUtcNow().AddHours(48).ToUnixTimeMilliseconds();
        if (bytes)
        {
            var used = (long)tx.Scalar("SELECT coalesce(sum(length(CAST(result AS BLOB))),0) FROM combat_receipts")!;
            var result = "{\"padding\":\"" + new string('x', checked((int)(16 * 1024 * 1024 - used - 64 - 14))) + "\"}";
            return tx.Execute("INSERT INTO combat_receipts VALUES('owner','capacity-seed','preferences','capacity-bytes','seed',$result,$expiry)", ("$result", result), ("$expiry", expiry));
        }
        var count = 10000 - (long)tx.Scalar("SELECT count(*) FROM combat_receipts")!;
        return tx.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$count) INSERT INTO combat_receipts SELECT 'owner','capacity-seed','preferences','capacity-'||i,'seed','{}',$expiry FROM n WHERE i<=$count",
            ("$count", count), ("$expiry", expiry));
    });

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static MabiToolClient Sdk(WebApplication app, string token = Token) => new(new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), _ => ValueTask.FromResult(token));
    private static async Task<CombatWorkspace> WorkspaceReady(MabiToolClient sdk, Func<CombatWorkspace, bool>? match = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true) { var value = (await sdk.GetCombatSnapshotAsync(timeout.Token)).Data; if (value is not null && (match?.Invoke(value) ?? true)) return value; await Task.Delay(20, timeout.Token); }
    }
    private static async Task<CombatMeasurementState> Until(MabiToolClient sdk, Func<CombatMeasurementState, bool> match, TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        while (true) { var value = await sdk.GetCombatMeasurementAsync(timeout.Token); if (match(value)) return value; await Task.Delay(50, timeout.Token); }
    }
    private static async Task Changed(MabiToolClient sdk, string phase, TaskCompletionSource connected, CancellationToken token)
    {
        await foreach (var message in sdk.StreamEventsAsync(new(["combat.measurement.changed"], [], null), token))
            if (message.Event == "snapshot") connected.TrySetResult();
            else if (message.Event == "combat.measurement.changed" && message.Data.GetProperty("data").GetProperty("phase").GetString() == phase) return;
        throw new InvalidOperationException("缺少定时测量事件");
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string root;
        public StorageOptions Options => new(Path.Combine(root, "bootstrap"), true, PortableRoot: root);
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent!;
            root = Path.Combine(repository.FullName, "artifacts", "tmp", "combat-measure", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(root).Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar + "tmp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }
}
