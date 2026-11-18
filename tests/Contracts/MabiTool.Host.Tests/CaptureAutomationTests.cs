using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>全局常驻抓包：网卡自动选择、手动调试检测、按构建选择 profile、常驻启停及自动选择游戏窗口。</summary>
[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class CaptureAutomationTests
{
    private static readonly string First = new('1', 32), Second = new('2', 32);

    [Fact]
    public void ListedChannelsCountGameConnectionsAndAutomaticModeChoosesNone()
    {
        // 实采（UU 加速器 Wintun 隧道）：数据连接远端 :16000、两条远端 443、一条进程自连；列出网卡只作诊断统计。
        CaptureDevice ethernet = new("eth", "Realtek Gaming 2.5GbE", ["192.168.2.155"]), wintun = new("wintun", "UU Wintun Tunnel", ["172.19.84.237"]),
            hyperv = new("hv", "Hyper-V Virtual Ethernet Adapter", ["172.25.160.1"]), loopback = new("lo", "Adapter for loopback traffic capture", []) { IsLoopback = true };
        CaptureConnection data = new("172.19.84.237", 1714, "43.212.230.170", 16000), tls = new("172.19.84.237", 14408, "18.166.193.141", 443),
            self = new("127.0.0.1", 7740, "127.0.0.1", 7739), peer = new("127.0.0.1", 7739, "127.0.0.1", 7740);
        CaptureConnection[] owned = [data, tls, self, peer];
        var options = NpcapObservationSource.Annotate([ethernet, wintun, hyperv, loopback], owned.Where(value => !NativeCaptureBoundary.SelfConnection(value, owned)).ToArray());
        Assert.Equal([0, 2, 0, 0], options.Select(value => value.GameConnections!.Value));
        Assert.Equal(["physical", "wintun", "hyperv", "loopback"], new[] { ethernet, wintun, hyperv, loopback }.Select(value => value.Kind));
        Assert.Null(NpcapObservationSource.Annotate([ethernet], null)[0].GameConnections);
        // 自动模式不选网卡，设置里留下的网卡（旧版指定或检测确认后切回自动）不读取。
        var leftover = GameCaptureSettings.Default with { ChannelId = First, ChannelDescription = "A" };
        var choice = CaptureChannelSelection.Select(options, leftover);
        Assert.Equal(((string?)null, "automatic"), (choice.ChannelId, choice.Source));
    }

    [Fact]
    public void ManualModeUsesOnlyTheSavedChannel()
    {
        CaptureChannelOption a = new(First, "A", ["10.0.0.2"], false, 1), b = new(Second, "B", [], false, 0);
        var manual = GameCaptureSettings.Default with { ChannelMode = GameCaptureSettings.ManualChannel, ChannelId = Second, ChannelDescription = "B" };
        var chosen = CaptureChannelSelection.Select([a, b], manual);
        Assert.Equal((Second, "saved"), (chosen.ChannelId, chosen.Source));
        Assert.Equal("observation.savedChannelMissing", Assert.Throws<OperationException>(() => CaptureChannelSelection.Select([a], manual)).Code);
    }

    [Fact]
    public void TrafficDetectorPicksTheChannelCarryingGameDownlinkWithinBudget()
    {
        var detector = new ChannelTrafficDetector([(First, "以太网"), (Second, "虚拟网卡")]);
        for (var i = 0; i < 60; i++) detector.Record(0, 1400);
        for (var i = 0; i < 10; i++) detector.Record(1, 1400);
        for (var i = 0; i < 500; i++) detector.Record(1, 0);
        var final = detector.Progress(10, true);
        Assert.Equal(First, final.WinnerChannelId); Assert.Null(final.Reason);
        Assert.Equal(60, final.Channels[0].Packets); Assert.Equal(60 * 1400, final.Channels[0].PayloadBytes); Assert.Equal(10, final.Channels[1].Packets);
        Assert.Null(detector.Progress(10, false).WinnerChannelId);

        var close = new ChannelTrafficDetector([(First, "A"), (Second, "B")]);
        for (var i = 0; i < 40; i++) { close.Record(0, 100); close.Record(1, 100); }
        Assert.Equal("observation.channelUndetermined", close.Progress(5, true).Reason);
        Assert.Equal("observation.channelUndetermined", new ChannelTrafficDetector([(First, "A")]).Progress(5, true).Reason);

        var flood = new ChannelTrafficDetector([(First, "A")]);
        for (var i = 0; i < ChannelTrafficDetector.MaximumPackets + 10; i++) flood.Record(0, 1);
        Assert.True(flood.BudgetExhausted); Assert.Equal(ChannelTrafficDetector.MaximumPackets, flood.TotalPackets);

        // 检测以分帧为准：物理网卡上包更多（加速器对外流量），回环上能分帧，选回环。
        var framed = new ChannelTrafficDetector([(First, "以太网"), (Second, "回环")]);
        for (var i = 0; i < 300; i++) framed.Record(0, 1400);
        for (var i = 0; i < 5; i++) framed.Record(1, 40, framed: true);
        Assert.Equal(Second, framed.Decide(true).Winner);
    }

    [Fact]
    public async Task ResidentCaptureWithoutGameFramesReportsNoFramesWithoutSwitching()
    {
        // 合成 Worker 只发状态报告、不分帧：约 20 秒（此处缩短为 1 秒）收不到游戏数据时自动抓包原因为 noGameFrames，同一捕获继续运行、不换网卡。
        var grace = GameObservationService.HealthGrace;
        GameObservationService.HealthGrace = TimeSpan.FromSeconds(1);
        try
        {
            using var area = Current(); await using var app = area.Build("auto", automation: true); await app.StartAsync();
            using var client = GameObservationApiTests.Area.Owner(app);
            var first = await Wait(client, value => value.State == "running");
            Assert.Null(first.ChannelId); Assert.Equal("automatic", first.Automation!.ChannelSource);
            var hinted = await Wait(client, value => value.State == "running" && value.Automation?.Reason == GameObservationService.NoGameFrames);
            Assert.Equal(("running", first.CaptureId), (hinted.Automation!.State, hinted.CaptureId));
            await app.StopAsync();
        }
        finally { GameObservationService.HealthGrace = grace; }
    }

    [Fact]
    public void IdentitySelectionKeepsWaitingStableAndNeverReusesRetiredConnectionsOrGenerations()
    {
        var selection = new CaptureObservationSelection();
        var a = new TwGameObserver(); var b = new TwGameObserver();
        GameIdentity identityA = new(1, 3, "角色 A", 101), identityB = new(2, 3, "角色 B", 202);
        a.AcceptPersonalId(identityA.CharacterId); a.AcceptIdentity(identityA);
        b.AcceptPersonalId(identityB.CharacterId); b.AcceptIdentity(identityB);
        static void Waiting(GameObservation observation)
        { Assert.Null(observation.Identity); Assert.Null(observation.Concert); Assert.Null(observation.Music); }

        var initial = selection.Select([]); Waiting(initial);
        var initialAgain = selection.Select([]);
        Assert.Equal(initial.Generation, initialAgain.Generation); Assert.True(initialAgain.Sequence > initial.Sequence);

        var first = selection.Select([(a, 10)]); Assert.Equal(identityA, first.Identity);
        var firstAgain = selection.Select([(a, 10)]);
        Assert.Equal(first.Generation, firstAgain.Generation); Assert.True(firstAgain.Sequence > first.Sequence);
        var afterFirstRemoved = selection.Select([]); Waiting(afterFirstRemoved);

        var second = selection.Select([(a, 10), (b, 20)]); Assert.Equal(identityB, second.Identity);
        var afterSecondRemoved = selection.Select([(a, 10)]); Waiting(afterSecondRemoved);
        var stillWaiting = selection.Select([(a, 10)]); Waiting(stillWaiting);
        Assert.Equal(afterSecondRemoved.Generation, stillWaiting.Generation);
        Assert.True(stillWaiting.Sequence > afterSecondRemoved.Sequence);

        var recovered = selection.Select([(a, 30)]); Assert.Equal(identityA, recovered.Identity);
        a.Reset("当前连接重新识别。");
        var reset = selection.Select([(a, 30)]); Waiting(reset);
        var resetAgain = selection.Select([(a, 30)]); Waiting(resetAgain);
        Assert.Equal(reset.Generation, resetAgain.Generation); Assert.True(resetAgain.Sequence > reset.Sequence);
        a.AcceptPersonalId(identityA.CharacterId);
        a.AcceptIdentity(identityA with { ActorId = 303 });
        var sameConnectionRecovered = selection.Select([(a, 30)]);
        Assert.Equal(reset.Generation, sameConnectionRecovered.Generation);
        Assert.Equal(303UL, sameConnectionRecovered.Identity!.ActorId);

        var generations = new[] { initial.Generation, first.Generation, afterFirstRemoved.Generation,
            second.Generation, afterSecondRemoved.Generation, recovered.Generation, reset.Generation };
        Assert.Equal(generations.Length, generations.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RetiredGenerationsStayBoundedAcrossManyResynchronisations()
    {
        var retired = new RetiredGenerations();
        for (var i = 0; i < 10_000; i++) retired.Retire("g" + i);
        Assert.Equal(RetiredGenerations.Maximum, retired.Count);
        Assert.True(retired.Contains("g9999")); Assert.False(retired.Contains("g0"));
    }

    [Fact]
    public async Task ResidentCaptureStartsWithTheBuildProfileAndKeepsRunningWithoutDemands()
    {
        using var area = Current(); await using var app = area.Build("auto", automation: true); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var running = await Wait(client, value => value.State == "running" && value.Automation?.State == "running");
        Assert.Equal(Tw20261110CombatProtocol.CaptureProfile, running.Profile); Assert.Null(running.ChannelId);
        Assert.Equal(0, running.SubscriberCount); Assert.Equal("automatic", running.Automation!.ChannelSource);
        var service = app.Services.GetRequiredService<GameObservationService>();
        var pid = service.Runtime().WorkerProcessId!.Value; Assert.True(service.Resident);
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);

        // 模块需求不指定网卡与 profile，加入同一捕获；释放后捕获继续常驻。
        var demand = await Post<GameObservationSubscription>(client, ApiContract.StartGameObservation.Path,
            new GameObservationStartRequest(null, area.Bindings.Current!.Session.BindingRevision), "resident-demand", (await Read(client)).ETag);
        Assert.Equal(running.CaptureId, demand.CaptureId);
        Assert.Single(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
        var released = await Delete(client, demand.Id);
        Assert.Equal("running", released.Snapshot.State); Assert.Equal(pid, service.Runtime().WorkerProcessId);
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);

        // 关闭自动抓包：停止且不再启动；重新开启后再次启动新捕获。
        var settings = await Settings(client);
        await Save(client, settings.Value with { AutomaticCapture = false }, settings.ETag, "disable-capture");
        var off = await Wait(client, value => value.State == "stopped" && value.Automation?.State == "off");
        Assert.Equal("observation.automaticCaptureDisabled", off.Automation!.Reason);
        await WaitUntil(() => !Alive(pid));
        await Task.Delay(600); Assert.Null(service.Runtime().WorkerProcessId);
        Assert.Equal("observation.automaticCaptureDisabled", await StartError(client, area, "disabled-demand"));
        settings = await Settings(client);
        await Save(client, settings.Value with { AutomaticCapture = true }, settings.ETag, "enable-capture");
        var again = await Wait(client, value => value.State == "running" && value.CaptureId != running.CaptureId);
        Assert.NotEqual(pid, service.Runtime().WorkerProcessId);
        Assert.Equal(again.CaptureId, (await Read(client)).Snapshot.CaptureId);
        await app.StopAsync();
        Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
    }

    [Fact]
    public async Task BindingReplacementStopsTheResidentCaptureAndRestartsOnTheNewBinding()
    {
        using var area = Current(); await using var app = area.Build("auto", automation: true); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var first = await Wait(client, value => value.State == "running");
        var old = area.Bindings.Current!;
        area.Bindings.Update(new("TW", old.Session.ProcessId, old.Session.ProcessStartTime.AddSeconds(5), old.Session.WindowIdentity,
            TwClientFingerprint.GameBuild, old.Bounds, old.DpiX, old.DpiY, false));
        var next = await Wait(client, value => value.State == "running" && value.CaptureId != first.CaptureId);
        Assert.NotEqual(first.GameSessionId, next.GameSessionId);
        Assert.Equal(2, Directory.GetFiles(area.Root, "process-start-*").Length);
        await app.StopAsync();
    }

    [Fact]
    public void CaptureSummaryFieldsAllReachTheDiagnosticRecord()
    {
        // 诊断事件每条至多保留 12 个字段：登记表里不能有超过上限的事件，摘要拆成两条后全部字段都写进记录。
        Assert.All(DiagnosticEvents.All, definition => Assert.True(definition.Fields.Count <= DiagnosticSanitizer.MaximumFields, definition.Code));
        var records = new List<DiagnosticRecord>();
        var trace = new DiagnosticTrace(); trace.AttachPersistence(record => { records.Add(record); return true; });
        var device = new CaptureDeviceCounters(First, "wintun", 101, false, "link:113", true, new(9), new(8), new(1), new(5), new(4096));
        var counters = new CaptureDiagnostics().Snapshot(0, 0) with { Devices = [device] };
        foreach (var (definition, fields) in GameObservationService.SummaryEvents(30_000, "automatic", counters, true, 120, 450, 9000)) trace.Emit(definition, fields);
        var summary = records.Single(record => record.Event == "observation.capture.summary").Fields!;
        Assert.Equal(DiagnosticEvents.CaptureSummary.Fields.Count, summary.Count);
        var parse = records.Single(record => record.Event == "observation.capture.parse").Fields!;
        Assert.Equal(DiagnosticEvents.CaptureParse.Fields.Count, parse.Count);
        Assert.Equal(("true", "120", "450", "9000"), (parse["identity"], parse["reportAgeMs"], parse["combatAgeMs"], parse["inventoryAgeMs"]));
        var perDevice = records.Single(record => record.Event == "observation.capture.device").Fields!;
        Assert.Equal(DiagnosticEvents.CaptureDevice.Fields.Count, perDevice.Count);
        Assert.Equal(("wintun", "101", "link:113", "4096"), (perDevice["kind"], perDevice["linkType"], perDevice["issue"], perDevice["candidateDropped"]));
    }

    [Fact]
    public async Task ChangingTheChannelSettingWhileRunningRestartsTheCaptureWithTheNewChannel()
    {
        using var area = Current(); await using var app = area.Build("auto-many", automation: true); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var automatic = await Wait(client, value => value.State == "running");
        var settings = await Settings(client);
        await Save(client, settings.Value with { ChannelMode = GameCaptureSettings.ManualChannel, ChannelId = Second, ChannelDescription = "合成回环" },
            settings.ETag, "switch-to-manual");
        var manual = await Wait(client, value => value.State == "running" && value.CaptureId != automatic.CaptureId);
        Assert.Equal((Second, "saved"), (manual.ChannelId, manual.Automation!.ChannelSource));
        settings = await Settings(client);
        await Save(client, settings.Value with { ChannelMode = GameCaptureSettings.AutomaticChannel }, settings.ETag, "switch-to-automatic");
        var again = await Wait(client, value => value.State == "running" && value.CaptureId != manual.CaptureId);
        Assert.Equal(((string?)null, "automatic"), (again.ChannelId, again.Automation!.ChannelSource));
        Assert.Equal(["all", Second, "all"], (await File.ReadAllLinesAsync(Path.Combine(area.Root, "capture-channels"))).Where(line => line.Length > 0));
        await app.StopAsync();
    }

    [Fact]
    public async Task MissingSavedChannelIsReportedAndRetriedWithBackoff()
    {
        // 自动模式不枚举网卡即启动（合成网卡列表两张都有游戏连接也不报多网卡）；指定的网卡不在时 savedChannelMissing，改为存在的网卡后立即重试。
        using var area = Current(); await using var app = area.Build("auto-many", automation: true); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var automatic = await Wait(client, value => value.State == "running");
        Assert.Equal(((string?)null, "automatic"), (automatic.ChannelId, automatic.Automation!.ChannelSource));
        Assert.Empty(Directory.GetFiles(area.Root, "channel-query-*"));
        var settings = await Settings(client);
        await Save(client, settings.Value with { AutomaticCapture = false }, settings.ETag, "pause-capture");
        await Wait(client, value => value.State == "stopped");
        settings = await Settings(client);
        await Save(client, settings.Value with { AutomaticCapture = true, ChannelMode = GameCaptureSettings.ManualChannel, ChannelId = new string('3', 32),
            ChannelDescription = "已拔出的网卡" }, settings.ETag, "missing-channel");
        var failed = await Wait(client, value => value.Automation?.State == "failed");
        Assert.Equal("observation.savedChannelMissing", failed.Automation!.Reason); Assert.NotNull(failed.Automation.NextAttemptAt);
        settings = await Settings(client);
        await Save(client, settings.Value with { ChannelId = Second, ChannelDescription = "合成回环" }, settings.ETag, "manual-channel");
        var running = await Wait(client, value => value.State == "running");
        Assert.Equal((Second, "saved"), (running.ChannelId, running.Automation!.ChannelSource));
        await app.StopAsync();
    }

    [Fact]
    public async Task UnregisteredBuildIsVerifiedThenCapturedWithOnlyTheFailedCapabilityDisabled()
    {
        using var area = new GameObservationApiTests.Area();
        var old = area.Bindings.Current!;
        area.Bindings.Update(new("TW", old.Session.ProcessId, old.Session.ProcessStartTime, old.Session.WindowIdentity,
            TwClientFingerprint.AdaptiveBuild(new string('a', 64), new string('b', 64)), old.Bounds, old.DpiX, old.DpiY, false));
        var release = new TaskCompletionSource();
        var verdict = new TwBuildVerdict(area.Bindings.Current!.Session.GameBuild!, false, null, TwCapabilityCheck.Ok, TwCapabilityCheck.Ok,
            new(false, TwBuildVerification.LayoutChanged), Tw20261110CombatProtocol.StreamProfile.KnownProtocols);
        await using var app = area.Build("auto", automation: true,
            builds: _ => new GameBuildAdaptation(async _ => { await release.Task; return verdict; }));
        await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var verifying = await Wait(client, value => value.Automation?.State == "waiting");
        Assert.Equal("gameBuild.verifying", verifying.Automation!.Reason); Assert.Equal("verifying", verifying.Automation.Build!.State);
        Assert.Empty(Directory.GetFiles(area.Root, "process-start-*"));
        release.SetResult();
        var running = await Wait(client, value => value.State == "running" && value.Automation?.Build?.State == "adapted");
        Assert.Equal(Tw20261110CombatProtocol.CaptureProfile, running.Profile);
        var disabled = Assert.Single(running.Automation!.Build!.Disabled);
        Assert.Equal(("combat", TwBuildVerification.LayoutChanged), (disabled.Capability, disabled.Reason));
        var combat = app.Services.GetRequiredService<MabiTool.Modules.CombatObservation.Application.ICombatObservationSource>().Capabilities(null);
        Assert.All(combat.Capabilities, value =>
            Assert.Equal((CapabilityStatus.Unavailable, TwBuildVerification.LayoutChanged), (value.Status.KnownValue, value.Reason)));
        await app.StopAsync();
    }

    [Fact]
    public async Task UnverifiedBuildsAndMissingBindingsWaitWithoutStartingTheWorker()
    {
        using var area = new GameObservationApiTests.Area(); await using var app = area.Build("auto", automation: true); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var waiting = await Wait(client, value => value.Automation?.State == "waiting");
        Assert.Equal("gameBuild.incompatible", waiting.Automation!.Reason);
        await Task.Delay(500);
        Assert.Empty(Directory.GetFiles(area.Root, "process-start-*"));
        await app.StopAsync();
    }

    [Fact]
    public async Task SettingsAreRevisionedAndIdempotent()
    {
        using var area = new GameObservationApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var initial = await Settings(client);
        Assert.Equal(GameCaptureSettings.Default, initial.Value);
        var change = initial.Value with { ChannelMode = GameCaptureSettings.ManualChannel, ChannelId = First, ChannelDescription = "以太网", AutomaticWindowBinding = false };
        var saved = await Save(client, change, initial.ETag, "settings-one");
        Assert.Equal(1UL, saved.Revision.Value); Assert.False(saved.AutomaticWindowBinding);
        Assert.Equal(saved, await Save(client, change, initial.ETag, "settings-one"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, await SaveStatus(client, change, initial.ETag, "settings-two"));
        Assert.Equal(HttpStatusCode.BadRequest, await SaveStatus(client, saved with { ChannelId = null }, (await Settings(client)).ETag, "settings-three"));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("detect")]
    [InlineData("detect-hold")]
    public async Task DetectionReportsProgressAndReleasesItsWorker(string mode)
    {
        using var area = Current(); await using var app = area.Build(mode); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var request = new CaptureChannelDetectionRequest(area.Bindings.Current!.Session.BindingRevision, 5);
        var started = await Post<CaptureChannelDetection>(client, ApiContract.StartChannelDetection.Path, request, "detect-one");
        Assert.Equal("running", started.State);
        Assert.Equal(started.Id, (await Post<CaptureChannelDetection>(client, ApiContract.StartChannelDetection.Path, request, "detect-one")).Id);
        CaptureChannelDetection result;
        if (mode == "detect")
            result = await WaitDetection(client, value => value.State != "running");
        else
        {
            await WaitDetection(client, value => value.Channels.Count == 2 && value.Channels[0].Packets.Value > 0);
            result = await Post<CaptureChannelDetection>(client, ApiContract.CancelChannelDetection.Path, null);
        }
        Assert.Equal(mode == "detect" ? "completed" : "cancelled", result.State);
        Assert.Equal(mode == "detect" ? First : null, result.WinnerChannelId);
        Assert.NotNull(result.EndedAt);
        await WaitUntil(() => File.ReadAllText(Path.Combine(area.Root, "detect-handles")) == "0");
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("game-access"));
        Assert.Null(app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId);
        await app.StopAsync();
    }

    [Fact]
    public async Task UniqueGameWindowIsBoundAutomaticallyAndSeveralStayManual()
    {
        var time = TimeProvider.System;
        var source = new Windows();
        var bindings = new GameSessionBindingTracker(time);
        var leases = new ResourceLeaseCoordinator(time, new ActivityBarrier());
        var controls = new GameControlContext(bindings, leases, new(leases, bindings, new RoomInputApiTests.Device(), time), false);
        var service = new GameBindingService(controls, source, new SystemInfo("Moongate", "0.1.0", "1.0", new string('a', 32), DateTimeOffset.UtcNow, "runtime", "os"), time);
        Assert.False(await service.AutoBindAsync(CancellationToken.None));           // 游戏尚未启动
        source.Add(1); source.Add(2);
        Assert.False(await service.AutoBindAsync(CancellationToken.None));           // 多个窗口保持手动
        Assert.Null(bindings.Current);
        source.Remove(2);
        Assert.True(await service.AutoBindAsync(CancellationToken.None));            // 游戏启动后唯一窗口
        Assert.Equal(1, bindings.Current!.Session.ProcessId); Assert.Equal(1, source.Prepared);
        Assert.False(await service.AutoBindAsync(CancellationToken.None));           // 已绑定不再枚举
        source.Remove(1);
        Assert.Null(bindings.Current);
        source.Add(3);
        Assert.True(await service.AutoBindAsync(CancellationToken.None));            // 游戏重启后重新绑定
        Assert.Equal(3, bindings.Current!.Session.ProcessId); Assert.Equal(2, source.Prepared);
    }

    private sealed class Windows : IGameWindowSource
    {
        private readonly Dictionary<int, GameWindowObservation> windows = [];
        public int Prepared { get; private set; }
        public void Add(int pid) => windows[pid] = new("TW", pid, DateTimeOffset.UnixEpoch.AddSeconds(pid), new((ulong)(100 + pid)), null, new(0, 0, 800, 600), 96, 96, false);
        public void Remove(int pid) => windows.Remove(pid);
        public IReadOnlyList<SelectableGameWindow> Enumerate() => windows.Values.Select(value => new SelectableGameWindow("台服", "MabinogiMobile", value)).ToArray();
        public GameWindowObservation? Refresh(GameWindowObservation selected) => windows.GetValueOrDefault(selected.ProcessId) is { } value &&
            value.ProcessStartTime == selected.ProcessStartTime ? value : null;
        public ValueTask<GameWindowObservation?> PrepareAsync(GameWindowObservation selected, CancellationToken ct)
        { Prepared++; return ValueTask.FromResult(Refresh(selected)); }
        public bool TryActivate(GameWindowObservation selected) => false;
    }

    private static GameObservationApiTests.Area Current()
    {
        var area = new GameObservationApiTests.Area();
        var old = area.Bindings.Current!;
        area.Bindings.Update(new("TW", old.Session.ProcessId, old.Session.ProcessStartTime, old.Session.WindowIdentity,
            TwClientFingerprint.GameBuild, old.Bounds, old.DpiX, old.DpiY, false));
        return area;
    }
    private static async Task<(GameObservationSnapshot Snapshot, string ETag)> Read(HttpClient client)
    {
        using var response = await client.GetAsync(ApiContract.GameObservation.Path); response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<GameObservationSnapshot>(ContractJson.Options))!, response.Headers.ETag!.ToString());
    }
    private static async Task<GameObservationSnapshot> Wait(HttpClient client, Func<GameObservationSnapshot, bool> condition)
    {
        GameObservationSnapshot? value = null;
        await WaitUntil(async () => { value = (await Read(client)).Snapshot; return condition(value); }); return value!;
    }
    private static async Task<CaptureChannelDetection> WaitDetection(HttpClient client, Func<CaptureChannelDetection, bool> condition)
    {
        CaptureChannelDetection? value = null;
        await WaitUntil(async () => { value = await client.GetFromJsonAsync<CaptureChannelDetection>(ApiContract.ChannelDetection.Path, ContractJson.Options); return condition(value!); });
        return value!;
    }
    private static Task WaitUntil(Func<bool> condition) => WaitUntil(() => Task.FromResult(condition()));
    private static async Task WaitUntil(Func<Task<bool>> condition)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!await condition()) await Task.Delay(20, deadline.Token); }
    private static bool Alive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task<(GameCaptureSettings Value, string ETag)> Settings(HttpClient client)
    {
        using var response = await client.GetAsync(ApiContract.GameCaptureSettings.Path); response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<GameCaptureSettings>(ContractJson.Options))!, response.Headers.ETag!.ToString());
    }
    private static async Task<GameCaptureSettings> Save(HttpClient client, GameCaptureSettings value, string etag, string key)
        => await Post<GameCaptureSettings>(client, ApiContract.SaveGameCaptureSettings.Path, value, key, etag);
    private static async Task<HttpStatusCode> SaveStatus(HttpClient client, GameCaptureSettings value, string etag, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiContract.SaveGameCaptureSettings.Path) { Content = JsonContent.Create(value, options: ContractJson.Options) };
        request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", etag);
        using var response = await client.SendAsync(request); return response.StatusCode;
    }
    private static async Task<string> StartError(HttpClient client, GameObservationApiTests.Area area, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiContract.StartGameObservation.Path)
        { Content = JsonContent.Create(new GameObservationStartRequest(null, area.Bindings.Current!.Session.BindingRevision), options: ContractJson.Options) };
        request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", (await Read(client)).ETag);
        using var response = await client.SendAsync(request); Assert.False(response.IsSuccessStatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code;
    }
    private static async Task<T> Post<T>(HttpClient client, string path, object? body, string? key = null, string? etag = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }, options: ContractJson.Options) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key); if (etag is not null) request.Headers.Add("If-Match", etag);
        using var response = await client.SendAsync(request); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }
    private static async Task<GameObservationRelease> Delete(HttpClient client, string id)
    {
        using var response = await client.DeleteAsync(ApiContract.ReleaseGameObservation.Path.Replace("{id}", id));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GameObservationRelease>(ContractJson.Options))!;
    }
}
