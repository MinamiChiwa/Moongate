using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Observation.Worker;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class SharedCombatCaptureTests
{
    private static readonly ObservationCombatProfile Profile = SyntheticCombat.Profile;

    [Fact]
    public async Task RealWorkerSharesMusicAndCombatAndLastDemandReleasesTheProcessTree()
    {
        using var area = Area(); await using var app = area.Build("combat-tree", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-music-first");
        var service = app.Services.GetRequiredService<GameObservationService>();
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var source = app.Services.GetRequiredService<ICombatObservationSource>();
        Assert.True(source.Capabilities(area.Bindings.Current!.Session.Id).Simulation);
        await using var first = await source.OpenAsync(auth.OwnerGrant().Session.ClientId, "combat-first", Request(area), CancellationToken.None);
        await using var second = await source.OpenAsync(auth.OwnerGrant().Session.ClientId, "combat-second", Request(area) with
            { DamageEnabled = false, SelfEffectsEnabled = false }, CancellationToken.None);
        Assert.Equal(3, service.Runtime().SubscriberCount);
        var pid = service.Runtime().WorkerProcessId!.Value;
        var full = new ConcurrentQueue<CombatFrame>(); var effectsOnly = new ConcurrentQueue<CombatFrame>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var readFirst = Read(first, full, timeout.Token); var readSecond = Read(second, effectsOnly, timeout.Token);
        await Wait(() => full.Any(x => x.Damage.Count > 0 && x.Effects.Any(y => y.EntityKey != first.LocalEntity.Key)) &&
            effectsOnly.Any(x => x.Effects.Count > 0));
        Assert.All(effectsOnly, frame => { Assert.Empty(frame.Damage); Assert.DoesNotContain(frame.Effects, x => x.EntityKey == second.LocalEntity.Key); });
        Assert.True(Alive(pid)); Assert.NotNull(service.Snapshot().Latest);
        await first.CloseAsync(); await readFirst;
        Assert.Equal(2, service.Runtime().SubscriberCount);
        var beforeMusicRelease = Assert.IsType<Observation<GameObservationValue>>(service.Snapshot().Latest);
        Assert.Equal(new ObservedGameIdentity(new(9007199254740993), "3", "共享玩家", new(11)), beforeMusicRelease.Value.Identity);
        await GameObservationApiTests.EndCapture(client, capture.Id);
        Assert.Equal(1, service.Runtime().SubscriberCount); Assert.True(Alive(pid));
        var retained = Assert.IsType<Observation<GameObservationValue>>(service.Snapshot().Latest);
        Assert.Equal(beforeMusicRelease.Value.Identity, retained.Value.Identity);
        Assert.Equal(beforeMusicRelease.Source, retained.Source);
        Assert.Equal(beforeMusicRelease.Value.CaptureGeneration, retained.Value.CaptureGeneration);
        Assert.Equal(beforeMusicRelease.Generation, retained.Generation);
        Assert.Equal(beforeMusicRelease.HostClockEpoch, retained.HostClockEpoch);
        Assert.True(retained.Sequence.Value >= beforeMusicRelease.Sequence.Value);
        Assert.True(retained.MonotonicMs >= beforeMusicRelease.MonotonicMs);
        Assert.Null(retained.Value.Music);
        Assert.Null(retained.Value.Concert);
        var count = effectsOnly.Count; await Wait(() => effectsOnly.Count > count);
        var child = int.Parse(await File.ReadAllTextAsync(Path.Combine(area.Root, "child-pid")));
        await second.CloseAsync(); await readSecond;
        Assert.Equal("stopped", service.Runtime().State);
        await Wait(() => !Alive(pid) && !Alive(child));
        Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*")); Assert.Empty(area.Device.Commands);
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
        await app.StopAsync();
    }

    [Fact]
    public async Task CombatReadyCallbackRunsAfterInterestAckAndBeforeIdentity()
    {
        using var area = Area(); await using var app = area.Build("combat-interest-hold", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-ready-callback");
        var service = app.Services.GetRequiredService<GameObservationService>();
        // 合成捕获先登记兴趣闸门再发出第一份报告；捕获启动后、闸门登记前送达的兴趣变化不经过闸门，因此等到第一份报告再登记战斗兴趣。
        await Wait(() => service.Snapshot().Latest is not null);
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = service.OpenCombatAsync(auth.OwnerGrant().Session.ClientId, "ready-callback", Request(area), Profile,
            cancellation.Token, () => ready.TrySetResult()).AsTask();

        await Wait(() => File.Exists(Path.Combine(area.Root, "interest-requested")));
        Assert.False(ready.Task.IsCompleted);
        await File.WriteAllTextAsync(Path.Combine(area.Root, "release-interest"), "release");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        Assert.Equal(2, service.Runtime().SubscriberCount);
        Assert.True(File.Exists(Path.Combine(area.Root, "interests")));
        Assert.Contains("combat", await File.ReadAllTextAsync(Path.Combine(area.Root, "interests")));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, service.Runtime().SubscriberCount);
        await GameObservationApiTests.EndCapture(client, capture.Id);
        await app.StopAsync();
    }

    [Fact]
    public async Task CancellationBeforeInterestAckNeverPublishesCombatReady()
    {
        using var area = Area(); await using var app = area.Build("combat-interest-hold", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-ready-cancelled");
        var service = app.Services.GetRequiredService<GameObservationService>();
        // 同上：等到第一份报告，兴趣变化才经过合成捕获的闸门。
        await Wait(() => service.Snapshot().Latest is not null);
        var owner = app.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session.ClientId;
        using var cancellation = new CancellationTokenSource();
        var callbacks = 0;
        var pending = service.OpenCombatAsync(owner, "ready-cancelled", Request(area), Profile, cancellation.Token,
            () => callbacks++).AsTask();

        await Wait(() => File.Exists(Path.Combine(area.Root, "interest-requested")));
        cancellation.Cancel();
        Assert.Equal(0, callbacks);
        Assert.False(pending.IsCompleted);
        await File.WriteAllTextAsync(Path.Combine(area.Root, "release-interest"), "release");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, callbacks);
        Assert.Equal(1, service.Runtime().SubscriberCount);
        await GameObservationApiTests.EndCapture(client, capture.Id);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("combat-unsupported")]
    public async Task CombatReadyCallbackIsSkippedWhenPreparationDoesNotComplete(string mode)
    {
        using var area = Area(); await using var app = area.Build(mode, combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-ready-skipped-" + mode);
        var service = app.Services.GetRequiredService<GameObservationService>();
        var owner = app.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session.ClientId;
        using var cancellation = new CancellationTokenSource();
        if (mode == "cancel") cancellation.Cancel();
        var callbacks = 0;

        if (mode == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.OpenCombatAsync(owner, "ready-skipped", Request(area),
                Profile, cancellation.Token, () => callbacks++).AsTask());
        else
            Assert.Equal("combat.buildUnsupported", (await Assert.ThrowsAsync<OperationException>(() => service.OpenCombatAsync(owner,
                "ready-skipped", Request(area), Profile, cancellation.Token, () => callbacks++).AsTask())).Code);

        Assert.Equal(0, callbacks);
        Assert.Equal(1, service.Runtime().SubscriberCount);
        await GameObservationApiTests.EndCapture(client, capture.Id);
        await app.StopAsync();
    }

    [Fact]
    public async Task CombatReadyCallbackFailureReleasesTheCombatInterest()
    {
        using var area = Area(); await using var app = area.Build("combat-hold", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-ready-callback-failure");
        var service = app.Services.GetRequiredService<GameObservationService>();
        var owner = app.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session.ClientId;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenCombatAsync(owner, "ready-failure",
            Request(area), Profile, CancellationToken.None, () => throw new InvalidOperationException("probe callback failure")).AsTask());

        Assert.Equal("probe callback failure", failure.Message);
        Assert.Equal(1, service.Runtime().SubscriberCount);
        Assert.Equal("running", service.Runtime().State);
        await GameObservationApiTests.EndCapture(client, capture.Id);
        await app.StopAsync();
    }

    [Fact]
    public async Task CombatWaitsForIdentityWithoutDeadlineAndStartsWhenItAppears()
    {
        var interval = GameObservationService.IdentityPollInterval;
        GameObservationService.IdentityPollInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            using var area = Area(); await using var app = area.Build("combat-identity-later", combatProfile: Profile);
            await using var cleanup = new StopApplication(app); await app.StartAsync();
            using var client = GameObservationApiTests.Area.Owner(app);
            var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-identity-later");
            var service = app.Services.GetRequiredService<GameObservationService>();
            var owner = app.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session.ClientId;
            var pendingReports = 0;
            var pending = service.OpenCombatAsync(owner, "identity-later", Request(area), Profile, CancellationToken.None,
                onIdentityPending: () => { Interlocked.Increment(ref pendingReports); return Task.CompletedTask; }).AsTask();
            await Wait(() => Volatile.Read(ref pendingReports) == 1);
            // 远超身份帧通常的到达间隔仍保持等待，等待原因只公开一次。
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref pendingReports));
            await File.WriteAllTextAsync(Path.Combine(area.Root, "identity-now"), "map changed");
            var subscription = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CombatEntityRole.LocalPlayer, subscription.LocalEntity.Role.KnownValue);
            await subscription.CloseAsync();
            await GameObservationApiTests.EndCapture(client, capture.Id);
            await app.StopAsync();
        }
        finally { GameObservationService.IdentityPollInterval = interval; }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("revoke")]
    [InlineData("binding")]
    public async Task WaitingForIdentityCanBeCancelledOrInvalidated(string action)
    {
        using var area = Area(); await using var app = area.Build("combat-hold", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-wait-capture");
        var service = app.Services.GetRequiredService<GameObservationService>();
        await Wait(() => service.Runtime().State == "running");
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var challenge = auth.CreateChallenge(new("战斗观察", null,
            [ApiPermissions.SystemRead, ApiPermissions.GameObserve, ApiPermissions.CombatControl]));
        var grant = auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
        using var cancel = new CancellationTokenSource();
        var pending = app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(grant.Session.ClientId,
            "waiting-identity", Request(area), cancel.Token).AsTask();
        await Wait(() => service.Runtime().SubscriberCount == 2);
        switch (action)
        {
            case "cancel": cancel.Cancel(); break;
            case "revoke": auth.Revoke(grant.Session.ClientId); break;
            case "binding": area.Bindings.Clear(); break;
        }
        var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(failure); Assert.IsNotType<TimeoutException>(failure);
        if (action == "binding") await Wait(() => service.Runtime().WorkerProcessId is null);
        else
        {
            Assert.True(service.Runtime().SubscriberCount == 1, JsonSerializer.Serialize(service.Runtime(), ContractJson.Options)); Assert.Equal("running", service.Runtime().State);
            Assert.NotNull(service.Snapshot().Latest);
            await GameObservationApiTests.EndCapture(client, capture.Id);
        }
        Assert.Empty(area.Device.Commands); await app.StopAsync();
    }

    [Theory]
    [InlineData("combat-conflict")]
    [InlineData("combat-old-generation")]
    public async Task ConflictsFormOneGapAndRetiredGenerationsNeverDeliverDamage(string mode)
    {
        using var area = Area(); await using var app = area.Build(mode, combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-sequence-capture");
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        await using var subscription = await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(
            auth.OwnerGrant().Session.ClientId, "sequence-test", Request(area), CancellationToken.None);
        var frames = new ConcurrentQueue<CombatFrame>(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = Read(subscription, frames, timeout.Token);
        await Wait(() => mode == "combat-conflict" ? frames.Any(x => x.Gaps.Count > 0) : frames.Sum(x => x.Damage.Count) >= 2);
        await subscription.CloseAsync(); await reading;
        Assert.DoesNotContain(frames.SelectMany(x => x.Damage), x => x.Amount.Value == 999999);
        if (mode == "combat-conflict")
        {
            Assert.Single(frames.SelectMany(x => x.Damage));
            Assert.Equal(1UL, Assert.Single(frames.SelectMany(x => x.Gaps), x => x.Reason == "combat.frameConflict").DroppedEvents.Value);
        }
        Assert.All(frames, x => Assert.Equal(10UL, x.Generation.Value));
        await GameObservationApiTests.EndCapture(client, capture.Id); await app.StopAsync();
    }

    [Theory]
    [InlineData("combat-null-element")]
    [InlineData("combat-bad-scope")]
    [InlineData("combat-bad-time")]
    public async Task InvalidWorkerRecordsStopCaptureAndPreserveTheFailure(string mode)
    {
        using var area = Area(); await using var app = area.Build(mode, combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        await GameObservationApiTests.BeginCapture(client, area, "combat-invalid-capture");
        var service = app.Services.GetRequiredService<GameObservationService>();
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        await using var subscription = await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(
            auth.OwnerGrant().Session.ClientId, "invalid-record", Request(area), CancellationToken.None);
        var frames = new ConcurrentQueue<CombatFrame>(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = Read(subscription, frames, timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(area.Root, "inject-fault"), "1");
        var error = await Assert.ThrowsAsync<OperationException>(() => reading);
        Assert.Equal("combat.observationGap", error.Code);
        await Wait(() => service.Runtime().WorkerProcessId is null);
        Assert.Equal("worker.frameInvalid", service.Runtime().Reason);
        Assert.Equal(mode switch { "combat-null-element" or "combat-bad-scope" => "damage.fields", _ => "damage.time" },
            service.CombatDiagnostics().FrameRejection?.Branch);
        Assert.Empty(area.Device.Commands); await app.StopAsync();
    }

    [Fact]
    public async Task IdentityAndMapChangeReadAfterCaptureStallKeepTheCaptureRunning()
    {
        using var area = Area(); await using var app = area.Build("combat-capture-lag", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-capture-lag-capture");
        var service = app.Services.GetRequiredService<GameObservationService>();
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var started = Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
        ICombatObservationSubscription subscription;
        try
        {
            subscription = await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(
                auth.OwnerGrant().Session.ClientId, "capture-lag", Request(area), CancellationToken.None);
        }
        catch (OperationException error)
        {
            throw new Xunit.Sdk.XunitException(error.Code + ":" + error.Message + " " +
                JsonSerializer.Serialize(service.CombatDiagnostics(), ContractJson.Options));
        }
        await using var opened = subscription;
        var frames = new ConcurrentQueue<CombatFrame>(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = Read(subscription, frames, timeout.Token);
        await Wait(() => frames.Any(x => x.Gaps.Any(gap => gap.Reason == "combat.streamGap.connectionRemoved")) &&
            frames.Where(x => x.Generation.Value > frames.First().Generation.Value).Sum(x => x.Damage.Count) >= 2);
        await subscription.CloseAsync(); await reading;
        Assert.Equal("running", service.Runtime().State); Assert.Null(service.Runtime().Reason);
        var diagnostics = service.CombatDiagnostics();
        Assert.Null(diagnostics.FrameRejection);
        var gap = Assert.Single(diagnostics.Gaps, x => x.Reason == "combat.streamGap.connectionRemoved");
        Assert.InRange(gap.FromMs, started, Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
        // 两段积压（准备阶段身份包、切图后的身份包）各记恰好一条 captureLag，区间覆盖 2.5 s 滞后，不计丢弃。
        var lags = diagnostics.Gaps.Where(x => x.Reason == "combat.captureLag").ToArray();
        Assert.Equal(2, lags.Length);
        Assert.True(lags[0].ToMs < gap.FromMs && lags[1].ToMs >= gap.FromMs, JsonSerializer.Serialize(diagnostics, ContractJson.Options));
        Assert.All(lags, x => { Assert.Equal(0UL, x.DroppedEvents); Assert.InRange(x.ToMs!.Value - x.FromMs, 2500, 3500); });
        await GameObservationApiTests.EndCapture(client, capture.Id); await app.StopAsync();
    }

    [Fact]
    public async Task UnsupportedWorkerProfileRejectsOnlyCombatAndLeavesMusicRunning()
    {
        using var area = Area(); await using var app = area.Build("combat-unsupported", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-unsupported-capture");
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var failure = await Assert.ThrowsAsync<OperationException>(async () =>
            await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(auth.OwnerGrant().Session.ClientId,
                "unsupported-profile", Request(area), CancellationToken.None));
        Assert.Equal("combat.buildUnsupported", failure.Code);
        var service = app.Services.GetRequiredService<GameObservationService>();
        await Wait(() => service.Runtime().State == "running");
        Assert.True(service.Runtime().SubscriberCount == 1, JsonSerializer.Serialize(service.Runtime(), ContractJson.Options)); Assert.Equal("running", service.Runtime().State);
        Assert.NotNull(service.Snapshot().Latest);
        await GameObservationApiTests.EndCapture(client, capture.Id); await app.StopAsync();
    }

    [Fact]
    public async Task CombatApiPersistsSharedWorkerStatisticsAndStopKeepsMusicDemand()
    {
        using var area = Area(); await using var app = area.Build("combat-normal", combatProfile: Profile,
            storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: Path.Combine(area.Root, "portable")));
        await using var cleanup = new StopApplication(app); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-api-capture");
        using var sdk = new MabiToolClient(client.BaseAddress!, _ => ValueTask.FromResult(client.DefaultRequestHeaders.Authorization!.Parameter!));
        var operation = await sdk.StartCombatObservationAsync(Request(area), "combat-shared-api");
        CombatWorkspace? workspace = null;
        await Wait(async () =>
        {
            workspace = (await sdk.GetCombatSnapshotAsync()).Data;
            return workspace?.Encounter?.Summary.AttributedDamage.ToString() is { } amount && amount != "0" &&
                workspace.Entities.Any(x => x.Role.KnownValue == CombatEntityRole.Monster);
        });
        var stopped = await sdk.StopCombatObservationAsync(operation.Id);
        Assert.Equal(OperationState.Cancelled, stopped.State.KnownValue);
        Assert.True((await sdk.GetCombatSnapshotAsync()).Data!.Encounter!.Summary.Final);
        Assert.Single((await sdk.GetCombatEncountersAsync()).Items);
        var service = app.Services.GetRequiredService<GameObservationService>();
        Assert.True(service.Runtime().SubscriberCount == 1, JsonSerializer.Serialize(service.Runtime(), ContractJson.Options)); Assert.Equal("running", service.Runtime().State);
        Assert.NotNull(service.Snapshot().Latest);
        await GameObservationApiTests.EndCapture(client, capture.Id); await app.StopAsync();
    }

    [Fact]
    public async Task OverflowAndUnreadRecordsRemainInTheStopCheckpoint()
    {
        using var area = Area(); await using var app = area.Build("combat-overflow", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-overflow-capture");
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        await using var subscription = await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(
            auth.OwnerGrant().Session.ClientId, "overflow-checkpoint", Request(area), CancellationToken.None);
        await Wait(() => File.Exists(Path.Combine(area.Root, "burst-finished")));
        var gaps = await subscription.CloseAsync();
        Assert.True(gaps.Aggregate(0UL, (sum, x) => sum + x.DroppedEvents.Value) >= 19201,
            JsonSerializer.Serialize(gaps, ContractJson.Options));
        Assert.Contains(gaps, x => x.Reason == "combat.transportCapacity" || x.Reason == "queue-capacity");
        Assert.Equal(1, app.Services.GetRequiredService<GameObservationService>().Runtime().SubscriberCount);
        await GameObservationApiTests.EndCapture(client, capture.Id); await app.StopAsync();
    }

    [Fact]
    public async Task ReplacedInterestRejectsRetiredFramesAndKeepsTheCapture()
    {
        using var area = Area(); await using var app = area.Build("combat-late", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var capture = await GameObservationApiTests.BeginCapture(client, area, "combat-late-capture");
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var source = app.Services.GetRequiredService<ICombatObservationSource>();
        await using var first = await source.OpenAsync(auth.OwnerGrant().Session.ClientId, "retired-interest", Request(area), CancellationToken.None);
        await first.CloseAsync();
        await using var next = await source.OpenAsync(auth.OwnerGrant().Session.ClientId, "current-interest", Request(area), CancellationToken.None);
        var frames = new ConcurrentQueue<CombatFrame>(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = Read(next, frames, timeout.Token);
        await Wait(() => File.Exists(Path.Combine(area.Root, "late-attempt")) && frames.Any(x => x.Damage.Count > 0));
        await next.CloseAsync(); await reading;
        Assert.DoesNotContain(frames.SelectMany(x => x.Damage), x => x.Amount.Value == 999999);
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*"));
        await GameObservationApiTests.EndCapture(client, capture.Id); await app.StopAsync();
    }

    [Fact]
    public async Task WorkerExitEndsCombatAndDoesNotRestartTheCapture()
    {
        using var area = Area(); await using var app = area.Build("combat-exit", combatProfile: Profile);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        await GameObservationApiTests.BeginCapture(client, area, "combat-exit-capture");
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        await using var subscription = await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(
            auth.OwnerGrant().Session.ClientId, "worker-exit", Request(area), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var failure = await Assert.ThrowsAsync<OperationException>(() => Read(subscription, new(), timeout.Token));
        Assert.Equal("combat.observationGap", failure.Code);
        var service = app.Services.GetRequiredService<GameObservationService>();
        await Wait(() => service.Runtime().WorkerProcessId is null);
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*"));
        Assert.Contains(await subscription.CloseAsync(), x => x.Reason == "combat.captureEnded");
        await app.StopAsync();
    }

    [Fact]
    public void NativeSourceRejectsTheSyntheticProfileWithoutOpeningCapture()
    {
        IObservationCaptureSource source = new NpcapObservationSource();
        Assert.Equal(Tw20261110CombatProtocol.Profile, Assert.Single(source.CombatProfiles).Id);
        var context = new ObservationCaptureContext(source.CombatProfiles, _ => { }, _ => { });
        var requested = new ObservationCaptureInterests(true, new(new string('a', 32), Profile.Id, true, true, true));
        Assert.False(context.CanConfigure(requested));
        Assert.Equal("combat.buildUnsupported", Assert.Throws<WorkerObservationException>(() => context.Configure(requested)).Code);
        Assert.Null(context.Current.Combat);
    }

    [Fact]
    public void QueuePreservesEarlierBatchesThenReportsExactLossesForEachGeneration()
    {
        var queue = new CombatCaptureQueue(maximumBatches: 1, maximumBytes: 16 * 1024);
        Assert.True(queue.TryWrite(Report(1, 1, 0)));
        Assert.False(queue.TryWrite(Report(1, 2, 200)));
        Assert.False(queue.TryWrite(Report(1, 3, 300)));
        var first = Assert.Single(queue.Take(1)); Assert.Single(first.Frame.Damage); Assert.Equal("fixture.gap", Assert.Single(first.Frame.Gaps).Reason);
        var loss = Assert.Single(queue.Take(1)); Assert.Empty(loss.Frame.Damage);
        Assert.Equal(8UL, Assert.Single(loss.Frame.Gaps).DroppedEvents.Value);
        Assert.Equal(3UL, loss.Frame.Sequence.Value); Assert.Equal(0, queue.Bytes); Assert.Equal(0, queue.PendingCount);
        Assert.True(queue.TryWrite(Report(2, 1, 400)));
        Assert.Single(queue.Drain());
    }

    [Fact]
    public void QueueWritesMergedBatchesWithTheSameJsonAsEncodingTheMergedReport()
    {
        // 治疗与战斗状态在各份报告中有无不一：批次的拼接编码、字节计数与事件正文，同直接编码合并后的报告一致；越出 100 ms 合并窗口另起一批。
        var first = Report(1, 1, 100);
        var heal = new CombatHealEvent("heal-2", CombatHealDirection.Received, first.LocalEntity!.Key, new(0), new(30), null, null, false,
            At(120), 120, new(2), ObservationQuality.Confirmed, "hp-unpaired");
        var second = Report(1, 2, 120); second = second with { Frame = second.Frame with { Healing = [heal] } };
        var third = Report(1, 3, 150); third = third with { Frame = third.Frame with { CombatStates = [new(CombatLocalState.InCombat, At(150), 150, new(3))] } };
        var queue = new CombatCaptureQueue();
        foreach (var report in new[] { first, second, third, Report(1, 4, 400) }) Assert.True(queue.TryWrite(report));
        var queued = queue.Bytes;
        var batches = queue.DrainBatches();
        Assert.Equal(2, batches.Count);
        Assert.Equal([3, 1], batches.Select(batch => batch.Report.Frame.Damage.Count));
        Assert.Equal(["heal-2"], batches[0].Report.Frame.Healing!.Select(value => value.EventId));
        Assert.Single(batches[0].Report.Frame.CombatStates!);
        Assert.Null(batches[1].Report.Frame.Healing); Assert.Null(batches[1].Report.Frame.CombatStates);
        var encoded = batches.Select(batch => batch.WriteEnvelope([], [])).ToArray();
        for (var i = 0; i < batches.Count; i++) Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(batches[i].Report, ContractJson.Options), encoded[i]);
        Assert.Equal(encoded.Sum(value => value.Length), queued); Assert.Equal(0, queue.Bytes);
        // 事件正文：此前由报告对象经 Worker 协议选项编码；现由入队编码拼成后解析交给连接，写出的字节相同。
        using var document = JsonDocument.Parse(batches[0].WriteEnvelope(EncodedEvent.CombatPrefix, EncodedEvent.Suffix));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.SerializeToElement(new { combat = batches[0].Report }, WorkerProtocol.Json), WorkerProtocol.Json),
            JsonSerializer.SerializeToUtf8Bytes(document.RootElement, WorkerProtocol.Json));
    }

    [Theory]
    [InlineData("damage", 250, 1)]
    [InlineData("damage", 251, 2)]
    [InlineData("effect", 251, 2)]
    [InlineData("reconciliation", 251, 2)]
    public void QueueKeepsDelayedEventsWithinHostTimeWindow(string kind, int elapsed, int expectedBatches)
    {
        var first = Report(1, 1, 200);
        var hit = first.Frame.Damage[0] with { MonotonicMs = 0, ObservedAt = At(0) };
        var effect = new CombatEffectObservation(CombatEffectChangeKind.Apply, first.LocalEntity!.Key,
            new(10), new(20), null, EffectDurationKind.Unknown, null, null, null, true,
            At(0), 0, ObservationQuality.Confirmed);
        first = first with { Frame = first.Frame with
        {
            Damage = kind == "damage" ? [hit] : [],
            Effects = kind == "effect" ? [effect] : [],
            Reconciliations = kind == "reconciliation" ? [new(first.LocalEntity.Key, true, [effect])] : []
        } };
        var second = Report(1, 2, elapsed);
        var validator = typeof(GameObservationService).Assembly.GetType("MabiTool.Host.Game.CaptureCombatValidation")!
            .GetMethod("Validate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        void Validate(CombatCaptureReport value) => validator.Invoke(null, [value, Profile.Source, "fixture-game"]);
        Validate(first); Validate(second);
        var queue = new CombatCaptureQueue();
        Assert.True(queue.TryWrite(first)); Assert.True(queue.TryWrite(second));
        var delivered = queue.Drain();
        Assert.Equal(expectedBatches, delivered.Count);
        Assert.All(delivered, Validate);
        Assert.Equal(kind == "damage" ? 2 : 1, delivered.Sum(x => x.Frame.Damage.Count));
        if (kind == "damage") Assert.Equal(At(0), delivered[0].Frame.Damage[0].ObservedAt);
        else if (kind == "effect") Assert.Equal(At(0), Assert.Single(delivered.SelectMany(x => x.Frame.Effects)).ObservedAt);
        else Assert.Equal(At(0), Assert.Single(delivered.SelectMany(x => x.Frame.Reconciliations).SelectMany(x => x.Effects)).ObservedAt);
    }

    [Fact]
    public void InterestBarrierFinishesOldPendingDamageBeforePublishingTheNewInterest()
    {
        List<CombatCaptureReport> reports = []; List<CombatCaptureReport> drained = [];
        var context = new ObservationCaptureContext([Profile], _ => { }, reports.Add);
        context.Configure(new(true, new(new string('a', 32), Profile.Id, true, true, true)));
        ulong generation = 0;
        using var stream = new SharedGameStream("fixture-game", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        var identity = SyntheticCombat.Identity(); uint sequence = 0;
        foreach (var bytes in identity) { stream.Feed(sequence, bytes, 10, 10, At(10)); sequence += (uint)bytes.Length; }
        stream.Feed(sequence, SyntheticCombat.Damage(100), 20, 20, At(20));
        Assert.Empty(reports.SelectMany(x => x.Frame.Damage));
        context.Configure(new(true, new(new string('b', 32), Profile.Id, true, true, true)), () =>
        { drained.AddRange(reports); reports.Clear(); });
        Assert.Single(drained.SelectMany(x => x.Frame.Damage));
        Assert.All(drained, x => Assert.Equal(new string('a', 32), x.InterestId));
        Assert.Empty(reports); Assert.Null(stream.LocalEntity);
        Assert.True(context.Current.MusicEnabled);
        context.Publish(drained[^1]); Assert.Empty(reports);
    }

    private static GameObservationApiTests.Area Area()
    {
        var area = new GameObservationApiTests.Area();
        area.Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), Profile.Source.GameBuild!, new(0, 0, 800, 600), 96, 96, false));
        return area;
    }
    private static CombatObservationRequest Request(GameObservationApiTests.Area area) => new(area.Bindings.Current!.Session.Id);
    private static async Task Read(ICombatObservationSubscription source, ConcurrentQueue<CombatFrame> frames, CancellationToken ct)
    { await foreach (var frame in source.ReadAsync(ct)) frames.Enqueue(frame); }
    private static async Task Wait(Func<bool> condition) => await Wait(() => Task.FromResult(condition()));
    private static async Task Wait(Func<Task<bool>> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, timeout.Token); }
    private static bool Alive(int id)
    { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static DateTimeOffset At(double ms) => new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms);
    private static CombatCaptureReport Report(ulong generation, ulong sequence, double mono)
    {
        var key = new EntityKey("fixture-game", new(generation), new(1111), null);
        var local = new CombatEntityObservation(key, "玩家", "玩家", CombatEntityRole.LocalPlayer, null, false, true, ObservationQuality.Confirmed);
        var damage = new CombatDamageEvent("hit-" + sequence, Profile.Source, key, key with { RawInstanceId = new(2222) },
            new(77), new(100), CombatDamageKind.Unclassified, At(mono), mono, new(sequence), 0, false, true, ObservationQuality.Confirmed);
        return new(1, new string('a', 32), local, new(1, Profile.Source, "fixture-game", new(generation), new(sequence),
            At(mono), mono, [local], [damage], [], [], [new("fixture.gap", mono, mono, new(1))], true));
    }
    private sealed class StopApplication(WebApplication app) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(app.StopAsync());
    }
}

/// <summary>宿主模拟测试共用：tw-20261110 v1 模拟战斗配置（不含治疗与战斗状态）及自行构造的身份、伤害帧。</summary>
internal static class SyntheticCombat
{
    public static ObservationCombatProfile Profile { get; } = Tw20261110CombatProtocol.CombatProfileV1 with { Simulation = true };

    /// <summary>个人数据与两次同一角色的出现（实体 1111、1112）：第一次出现绑定为本机。</summary>
    public static byte[][] Identity()
    {
        using var fixture = JsonDocument.Parse(typeof(SyntheticCombat).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var personal = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var character = Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!);
        byte[] Player(ulong entity)
        {
            var body = character.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body, entity);
            return Frame(Tw20261110CombatProtocol.AppearingCharacter, body, true);
        }
        return [Frame(Tw20261110CombatProtocol.PersonalData, personal, true), Player(1111), Player(1112)];
    }

    /// <summary>本机实体 1111 对实体 2222 的伤害飘字 21196。</summary>
    public static byte[] Damage(long amount)
    {
        var body = new byte[53];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body, 1111);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), 2222);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), amount);
        return Frame(Tw20261110CombatProtocol.DamageFloater, body);
    }

    private static byte[] Frame(int code, byte[] body, bool compressed = false)
    {
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var encoder = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) encoder.Write(body);
            body = output.ToArray();
        }
        var frame = new byte[9 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, code);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        frame[8] = compressed ? (byte)1 : (byte)0; body.CopyTo(frame, 9);
        return frame;
    }
}
