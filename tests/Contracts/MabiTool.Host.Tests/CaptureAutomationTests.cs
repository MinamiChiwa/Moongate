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
    public void ChannelsAreMatchedByTheGameConnectionLocalAddress()
    {
        CaptureDevice ethernet = new("eth", "以太网", ["192.168.1.5"]), vpn = new("vpn", "虚拟网卡", ["10.8.0.2"]),
            loopback = new("lo", "回环", []) { IsLoopback = true };
        var data = CaptureChannels.DataConnections([new("192.168.1.5", 50000, "203.0.113.9", 1111), new("192.168.1.5", 50001, "203.0.113.9", 443)]);
        Assert.Single(data);
        var options = NpcapObservationSource.Annotate([ethernet, vpn, loopback], data);
        Assert.Equal([1, 0, 0], options.Select(value => value.GameConnections!.Value));
        Assert.True(options[2].Loopback);
        var choice = CaptureChannelSelection.Select(options, GameCaptureSettings.Default);
        Assert.Equal(NpcapObservationSource.ChannelId("eth"), choice.ChannelId); Assert.Equal("automatic", choice.Source); Assert.False(choice.AnyAddress);

        var proxied = NpcapObservationSource.Annotate([ethernet, loopback], CaptureChannels.DataConnections([new("127.0.0.1", 50000, "127.0.0.1", 1111)]));
        Assert.Equal(NpcapObservationSource.ChannelId("lo"), CaptureChannelSelection.Select(proxied, GameCaptureSettings.Default).ChannelId);
        // 没有数据端口时取进程全部连接。
        Assert.Single(CaptureChannels.DataConnections([new("10.8.0.2", 50000, "203.0.113.9", 443)]));
        Assert.Null(NpcapObservationSource.Annotate([ethernet], null)[0].GameConnections);
    }

    [Fact]
    public void SelectionReportsNoMatchAmbiguityAndSavedChannels()
    {
        CaptureChannelOption a = new(First, "A", ["10.0.0.2"], false, 1), b = new(Second, "B", ["10.0.0.3"], false, 1), none = new(Second, "B", [], false, 0);
        Assert.Equal("observation.channelNotFound", Assert.Throws<OperationException>(() =>
            CaptureChannelSelection.Select([a with { GameConnections = 0 }, none], GameCaptureSettings.Default)).Code);
        Assert.Equal("observation.channelAmbiguous", Assert.Throws<OperationException>(() =>
            CaptureChannelSelection.Select([a, b], GameCaptureSettings.Default)).Code);
        var saved = GameCaptureSettings.Default with { ChannelId = Second, ChannelDescription = "B" };
        Assert.Equal(Second, CaptureChannelSelection.Select([a, b], saved).ChannelId);
        var manual = saved with { ChannelMode = GameCaptureSettings.ManualChannel };
        var chosen = CaptureChannelSelection.Select([a, none], manual);
        Assert.Equal(Second, chosen.ChannelId); Assert.True(chosen.AnyAddress);
        Assert.Equal("observation.savedChannelMissing", Assert.Throws<OperationException>(() => CaptureChannelSelection.Select([a], manual)).Code);
        // 用户指定网卡的地址对不上游戏连接时仍按连接五元组捕获；自动选择保持地址匹配。
        var device = new CaptureDevice("wintun", "虚拟网卡", []);
        CaptureConnection connection = new("10.8.0.2", 50000, "203.0.113.9", 1111);
        Assert.Empty(NativeCaptureBoundary.SelectConnections([connection], device, true));
        Assert.Equal(connection, Assert.Single(NativeCaptureBoundary.SelectConnections([connection], device, true, anyAddress: true)));
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
    }

    [Fact]
    public void DetectionPacketReadsPayloadFromHeadersOfTruncatedFrames()
    {
        var ip = new byte[40]; ip[0] = 0x45; ip[9] = 6; ip[2] = 0x05; ip[3] = 0xdc; ip[32] = 0x50; // 1500 字节 IP，20 字节 TCP 头
        var ethernet = new byte[14 + 40]; ethernet[12] = 0x08; ip.CopyTo(ethernet, 14);
        Assert.Equal(1460, DetectionPacket.Payload(1, ethernet));
        var loopback = new byte[4 + 40]; ip.CopyTo(loopback, 4);
        Assert.Equal(1460, DetectionPacket.Payload(0, loopback));
        Assert.Equal(1460, DetectionPacket.Payload(228, ip));
        ethernet[12] = 0x86; Assert.Equal(0, DetectionPacket.Payload(1, ethernet));
        Assert.Equal(0, DetectionPacket.Payload(1, new byte[10]));
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
        Assert.Equal(Tw20261110CombatProtocol.CaptureProfile, running.Profile); Assert.Equal(First, running.ChannelId);
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

    [Theory]
    [InlineData("auto-none", "observation.channelNotFound")]
    [InlineData("auto-many", "observation.channelAmbiguous")]
    public async Task AutomaticSelectionFailuresAreReportedAndRetriedWithBackoff(string mode, string code)
    {
        using var area = Current(); await using var app = area.Build(mode, automation: true); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app);
        var failed = await Wait(client, value => value.Automation?.State == "failed");
        Assert.Equal(code, failed.Automation!.Reason); Assert.NotNull(failed.Automation.NextAttemptAt);
        Assert.Null(app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId);
        Assert.Empty(Directory.GetFiles(area.Root, "capture-start-*"));
        if (mode == "auto-many")
        {
            // 用户指定后立即按新设置重试，使用保存的网卡。
            var settings = await Settings(client);
            await Save(client, settings.Value with { ChannelMode = GameCaptureSettings.ManualChannel, ChannelId = Second, ChannelDescription = "合成回环" },
                settings.ETag, "manual-channel");
            var running = await Wait(client, value => value.State == "running");
            Assert.Equal(Second, running.ChannelId); Assert.Equal("saved", running.Automation!.ChannelSource);
        }
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
