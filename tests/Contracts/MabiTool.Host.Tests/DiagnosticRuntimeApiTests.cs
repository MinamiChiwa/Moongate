using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Diagnostics;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class DiagnosticRuntimeApiTests
{
    private static readonly string[] ProbeIds =
        ["host", "game.binding", "game.input", "observation.capture", "music", "combat", "inventory", "color", "rooms", "diagnostics"];

    [Fact]
    public async Task RuntimeOverviewIsReadOnlyWhitelistedAndFollowsSharedCaptureState()
    {
        using var area = new GameObservationApiTests.Area();
        area.Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), TwClientFingerprint.GameBuild, new(0, 0, 800, 600), 96, 96, false));
        await using var app = BuildInventoryHost(area);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        var service = app.Services.GetRequiredService<GameObservationService>();
        var info = app.Services.GetRequiredService<SystemInfo>();

        var idle = await sdk.GetDiagnosticRuntimeAsync();
        Assert.Equal(1, idle.SchemaVersion); Assert.Equal(info.InstanceId, idle.BackendEpoch);
        Assert.Equal(ProbeIds, idle.Probes.Select(probe => probe.Id));
        AssertWhitelisted(idle);
        Assert.DoesNotContain(idle.Probes, probe => probe.State == "unavailable");
        var capture = Probe(idle, "observation.capture");
        Assert.Equal("stopped", capture.State); Assert.False(capture.Counters.ContainsKey("worker.pid"));
        Assert.Equal(0UL, capture.Counters["worker.starts"].Value);
        Assert.Equal("true", Probe(idle, "game.binding").Values["buildRecognized"]);
        Assert.Equal("tw-20261110", Probe(idle, "game.binding").Values["build"]);
        Assert.Equal(0UL, Probe(idle, "color").Counters["pool.starts"].Value);
        // 读取总览不启动观察或染色 Worker，也不产生采集起点。
        Assert.Null(service.Runtime().WorkerProcessId); Assert.Empty(Directory.GetFiles(area.Root, "process-start-*"));
        Assert.Equal(0UL, app.Services.GetRequiredService<ColorWorkerPool>().DiagnosticState().Starts);
        var text = JsonSerializer.Serialize(idle, ContractJson.Options);
        Assert.DoesNotContain(area.Root, text); Assert.DoesNotContain(area.Bindings.Current!.Session.Id, text);
        Assert.DoesNotContain(TwClientFingerprint.GameBuild, text);

        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var challenge = auth.CreateChallenge(new("无系统读取", null, [ApiPermissions.GameObserve]));
        using var denied = new MabiToolClient(client.BaseAddress!, _ => ValueTask.FromResult(auth.Redeem(new(challenge.ChallengeId, challenge.Code), null).Token));
        Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() => denied.GetDiagnosticRuntimeAsync())).Problem!.Code);

        var music = await GameObservationApiTests.BeginCapture(client, area, "runtime-music");
        await using var combat = await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(auth.OwnerGrant().Session.ClientId,
            "runtime-combat", new(area.Bindings.Current!.Session.Id), CancellationToken.None);
        var operation = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "runtime-inventory");
        await Wait(async () => (await sdk.GetInventorySnapshotAsync()).Data?.Current == true);
        var active = await sdk.GetDiagnosticRuntimeAsync();
        AssertWhitelisted(active);
        capture = Probe(active, "observation.capture");
        Assert.Equal("running", capture.State);
        Assert.Equal(3UL, capture.Counters["subscriptions"].Value); Assert.Equal(1UL, capture.Counters["worker.starts"].Value);
        Assert.Equal((ulong)service.Runtime().WorkerProcessId!.Value, capture.Counters["worker.pid"].Value);
        Assert.True(capture.Counters.ContainsKey("lastFrameAgeMs"));
        Assert.Equal("true", capture.Values["interests.inventory"]);
        Assert.Equal(1UL, Probe(active, "combat").Counters["capture.subscriptions"].Value);
        Assert.Equal(1UL, Probe(active, "music").Counters["subscriptions"].Value);
        var inventory = Probe(active, "inventory");
        Assert.Equal("synced", inventory.State); Assert.Equal("current", inventory.Values["fullSync"]);
        Assert.Equal(1UL, inventory.Counters["operations.observing"].Value); Assert.Equal("true", inventory.Values["capture.layoutPrepared"]);
        Assert.True(inventory.Counters["capture.frames"].Value >= 1); Assert.True(inventory.Counters["capture.frames.full"].Value >= 1);
        Assert.Equal("true", inventory.Values["workspace.identityPresent"]);
        text = JsonSerializer.Serialize(active, ContractJson.Options);
        Assert.DoesNotContain(area.Root, text); Assert.DoesNotContain("南千和", text);

        await sdk.StopInventoryObservationAsync(operation.Id); await combat.CloseAsync();
        await GameObservationApiTests.EndCapture(client, music.Id);
        await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
        var stopped = await sdk.GetDiagnosticRuntimeAsync();
        capture = Probe(stopped, "observation.capture");
        Assert.Equal("stopped", capture.State); Assert.Equal(0UL, capture.Counters["subscriptions"].Value);
        Assert.Equal(1UL, capture.Counters["worker.starts"].Value); Assert.False(capture.Counters.ContainsKey("worker.pid"));
        await Wait(async () => Probe(await sdk.GetDiagnosticRuntimeAsync(), "inventory").State == "idle");
        Assert.Equal("stale", Probe(await sdk.GetDiagnosticRuntimeAsync(), "inventory").Values["fullSync"]);
        await app.StopAsync();
    }

    [Fact]
    public async Task RejectedCombatFrameIsReportedByBranchAndCaptureFault()
    {
        using var area = new GameObservationApiTests.Area();
        var profile = SyntheticCombat.Profile;
        area.Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), profile.Source.GameBuild!, new(0, 0, 800, 600), 96, 96, false));
        await using var app = area.Build("combat-bad-time", combatProfile: profile,
            storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: Path.Combine(area.Root, "portable")));
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        await GameObservationApiTests.BeginCapture(client, area, "runtime-combat-capture");
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var service = app.Services.GetRequiredService<GameObservationService>();
        await using var subscription = await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(
            auth.OwnerGrant().Session.ClientId, "runtime-invalid", new(area.Bindings.Current!.Session.Id), CancellationToken.None);
        var before = Probe(await sdk.GetDiagnosticRuntimeAsync(), "combat");
        Assert.False(before.Values.ContainsKey("capture.frameRejection"));
        Assert.Equal(1UL, before.Counters["capture.subscriptions"].Value);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = Drain(subscription, timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(area.Root, "inject-fault"), "1");
        await Assert.ThrowsAsync<OperationException>(() => reading);
        await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
        var after = await sdk.GetDiagnosticRuntimeAsync();
        AssertWhitelisted(after);
        Assert.Equal("damage.time", Probe(after, "combat").Values["capture.frameRejection"]);
        var capture = Probe(after, "observation.capture");
        // worker.frameInvalid 是 Worker 内部码，未登记为 API 错误码：reason 为空，状态原因以短值报告。
        Assert.Equal("faulted", capture.State); Assert.Null(capture.Reason); Assert.Equal("worker.frameInvalid", capture.Values["stateReason"]);
        Assert.Equal("worker.frameInvalid", capture.Values["lastFault"]);
        Assert.True(capture.Counters["worker.faults"].Value + capture.Counters["worker.failedStops"].Value >= 1);
        // 首个分支之外报告拒绝总数与按分支分布；Worker 结束归为协议错误。
        var combatProbe = Probe(after, "combat");
        Assert.Equal(1UL, combatProbe.Counters["capture.frameRejections"].Value);
        Assert.Equal(1UL, combatProbe.Counters["capture.frameRejections.damage.time"].Value);
        Assert.Equal(1UL, capture.Counters["worker.ends.protocolError"].Value); Assert.Equal(0UL, capture.Counters["worker.ends.normal"].Value);
        Assert.Equal("protocolError", capture.Values["worker.lastEnd"]);
        await app.StopAsync();
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task WorkerCaptureCountersEnterOverviewFollowTrafficAndConnectionSwitch()
    {
        using var area = new GameObservationApiTests.Area();
        await using var app = CurrentCombatHost(area, "current-combat-stream");
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        var service = app.Services.GetRequiredService<GameObservationService>();
        var capture = await BeginCurrentCapture(client, area, "runtime-counters");
        await using var subscription = await OpenCurrentCombat(app, area);
        using var readStop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var reading = Task.Run(async () => { try { await Drain(subscription, readStop.Token); } catch (Exception) { } });
        await Wait(async () => Counter(await sdk.GetDiagnosticRuntimeAsync(), "frames.types.op21196") >= 1);
        var first = Probe(await sdk.GetDiagnosticRuntimeAsync(), "observation.capture");
        AssertWhitelisted(await sdk.GetDiagnosticRuntimeAsync());
        Assert.True(first.Counters["workerCounters.reports"].Value >= 1);
        Assert.Equal((1UL, 1UL, 0UL), (first.Counters["connections.current"].Value, first.Counters["connections.opened"].Value, first.Counters["connections.closed"].Value));
        Assert.True(first.Counters["frames.types.op20590"].Value >= 1); Assert.True(first.Counters["reassembly.segments"].Value >= 1);
        Assert.True(first.Counters["frames.dispatched"].Value <= first.Counters["frames.total"].Value);
        Assert.Equal(0UL, first.Counters["packets.received"].Value); Assert.Equal(0UL, first.Counters["frames.decompressFailures"].Value);
        Assert.Equal(0UL, first.Counters["frames.types.other"].Value);
        // 合成流持续追加伤害与效果帧：计数随流量增长，至多每秒刷新一次。
        await Wait(async () => Counter(await sdk.GetDiagnosticRuntimeAsync(), "frames.total") > first.Counters["frames.total"].Value);
        await File.WriteAllTextAsync(Path.Combine(area.Root, "switch-connection"), "1");
        await Wait(async () => Counter(await sdk.GetDiagnosticRuntimeAsync(), "connections.opened") == 2);
        var switched = Probe(await sdk.GetDiagnosticRuntimeAsync(), "observation.capture");
        Assert.Equal(1UL, switched.Counters["connections.closed"].Value); Assert.Equal(1UL, switched.Counters["connections.current"].Value);
        var text = JsonSerializer.Serialize(await sdk.GetDiagnosticRuntimeAsync(), ContractJson.Options);
        Assert.DoesNotContain("127.0.0.1", text); Assert.DoesNotContain(area.Root, text);

        await subscription.CloseAsync(); await reading; await GameObservationApiTests.EndCapture(client, capture.Id);
        await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
        var stopped = Probe(await sdk.GetDiagnosticRuntimeAsync(), "observation.capture");
        // 停止后保留最后一份计数（最终值中连接已全部释放）及结束分类。
        Assert.Equal(0UL, stopped.Counters["connections.current"].Value); Assert.Equal(2UL, stopped.Counters["connections.closed"].Value);
        Assert.Equal(1UL, stopped.Counters["worker.ends.normal"].Value); Assert.Equal("normal", stopped.Values["worker.lastEnd"]);
        // 正常停止经 shutdown 让 Worker 自行退出，保留其退出码。
        Assert.Equal(0UL, stopped.Counters["worker.lastExitCode"].Value); Assert.Equal("false", stopped.Values["worker.lastTerminatedByHost"]);
        // 新捕获开始时清空上一轮 Worker 计数。
        var again = await BeginCurrentCapture(client, area, "runtime-counters-again");
        Assert.Equal(0UL, Counter(await sdk.GetDiagnosticRuntimeAsync(), "connections.closed"));
        await GameObservationApiTests.EndCapture(client, again.Id);
        await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
        await app.StopAsync();
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task LegacyWorkerWithoutCountersOmitsWorkerKeysAndKeepsHostEndInfo()
    {
        using var area = new GameObservationApiTests.Area();
        await using var app = CurrentCombatHost(area, "current-combat-legacy");
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        var service = app.Services.GetRequiredService<GameObservationService>();
        var capture = await BeginCurrentCapture(client, area, "runtime-legacy");
        await using var subscription = await OpenCurrentCombat(app, area);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var frame in subscription.ReadAsync(timeout.Token)) if (frame.Damage.Count > 0) break;
        await Task.Delay(1500);
        var probe = Probe(await sdk.GetDiagnosticRuntimeAsync(), "observation.capture");
        Assert.Equal("running", probe.State);
        Assert.DoesNotContain(probe.Counters.Keys, key => key.Split('.')[0] is "workerCounters" or "connections" or "packets" or "queues" or "reassembly" or "frames");
        Assert.Equal(0UL, probe.Counters["worker.ends.normal"].Value); Assert.False(probe.Values.ContainsKey("worker.lastEnd"));
        await subscription.CloseAsync(); await GameObservationApiTests.EndCapture(client, capture.Id);
        await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
        probe = Probe(await sdk.GetDiagnosticRuntimeAsync(), "observation.capture");
        Assert.Equal(1UL, probe.Counters["worker.ends.normal"].Value); Assert.False(probe.Counters.ContainsKey("frames.total"));
        await app.StopAsync();
    }

    [Fact]
    public async Task WorkerExitIsClassifiedWithExitCode()
    {
        using var area = new GameObservationApiTests.Area();
        await using var app = area.Build("exit", storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: Path.Combine(area.Root, "portable")));
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        var service = app.Services.GetRequiredService<GameObservationService>();
        await GameObservationApiTests.BeginCapture(client, area, "runtime-exit");
        await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
        var probe = Probe(await sdk.GetDiagnosticRuntimeAsync(), "observation.capture");
        Assert.Equal(1UL, probe.Counters["worker.ends.disconnected"].Value); Assert.Equal("disconnected", probe.Values["worker.lastEnd"]);
        // 进程自行退出时报告其退出码；断开检测早于进程结束时由宿主终止。
        if (probe.Values["worker.lastTerminatedByHost"] == "false") Assert.Equal(12UL, probe.Counters["worker.lastExitCode"].Value);
        else Assert.Contains(probe.Counters["worker.lastExitCode"].Value, new[] { 12UL, uint.MaxValue });
        await app.StopAsync();
    }

    [Fact]
    public async Task InventoryFrameDiagnosticsResetWhenCaptureRestartsAndSurviveStop()
    {
        using var area = new GameObservationApiTests.Area();
        area.Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), TwClientFingerprint.GameBuild, new(0, 0, 800, 600), 96, 96, false));
        await using var app = BuildInventoryHost(area);
        await using var cleanup = new StopApplication(app); await app.StartAsync();
        using var client = GameObservationApiTests.Area.Owner(app); using var sdk = Sdk(client);
        var service = app.Services.GetRequiredService<GameObservationService>();
        var music = await GameObservationApiTests.BeginCapture(client, area, "inventory-reset-1");
        var operation = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-reset-op-1");
        await Wait(() => Task.FromResult(service.InventoryDiagnostics().Frames.Count >= 1));
        await sdk.StopInventoryObservationAsync(operation.Id); await GameObservationApiTests.EndCapture(client, music.Id);
        await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
        // 捕获停止后保留上一轮读数，调试工具在停止后读取。
        var retained = service.InventoryDiagnostics();
        Assert.NotEmpty(retained.Frames); Assert.NotEmpty(retained.Counts);
        Assert.True(Probe(await sdk.GetDiagnosticRuntimeAsync(), "inventory").Counters["capture.frames"].Value >= 1);

        var restarted = await GameObservationApiTests.BeginCapture(client, area, "inventory-reset-2");
        var cleared = service.InventoryDiagnostics();
        Assert.Empty(cleared.Frames); Assert.Empty(cleared.Counts); Assert.Equal(0, cleared.FramesTruncated);
        var inventory = Probe(await sdk.GetDiagnosticRuntimeAsync(), "inventory");
        Assert.Equal(0UL, inventory.Counters["capture.frames"].Value); Assert.False(inventory.Values.ContainsKey("capture.lastFrameKind"));
        var second = await sdk.StartInventoryObservationAsync(new(area.Bindings.Current!.Session.Id), "inventory-reset-op-2");
        await Wait(() => Task.FromResult(service.InventoryDiagnostics().Frames.Count >= 1));
        await sdk.StopInventoryObservationAsync(second.Id); await GameObservationApiTests.EndCapture(client, restarted.Id);
        await Wait(() => Task.FromResult(service.Runtime().WorkerProcessId is null));
        await app.StopAsync();
    }

    private static WebApplication CurrentCombatHost(GameObservationApiTests.Area area, string mode)
    {
        var current = area.Bindings.Current!;
        area.Bindings.Update(new("TW", current.Session.ProcessId, current.Session.ProcessStartTime, current.Session.WindowIdentity,
            Tw20261110CombatProtocol.GameBuild, current.Bounds, current.DpiX, current.DpiY, false));
        return area.Build(mode, storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: Path.Combine(area.Root, "portable")));
    }
    private static async Task<GameObservationSubscription> BeginCurrentCapture(HttpClient client, GameObservationApiTests.Area area, string key)
    {
        using var snapshot = await client.GetAsync(ApiContract.GameObservation.Path);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiContract.StartGameObservation.Path)
        { Content = System.Net.Http.Json.JsonContent.Create(new GameObservationStartRequest(new string('1', 32), area.Bindings.Current!.Session.BindingRevision,
            Tw20261110CombatProtocol.CaptureProfile), options: ContractJson.Options) };
        request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", snapshot.Headers.ETag!.ToString());
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        return (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<GameObservationSubscription>(response.Content, ContractJson.Options))!;
    }
    private static async Task<ICombatObservationSubscription> OpenCurrentCombat(WebApplication app, GameObservationApiTests.Area area)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await app.Services.GetRequiredService<ICombatObservationSource>().OpenAsync(
            app.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session.ClientId, "runtime-" + Guid.NewGuid().ToString("N"),
            new(area.Bindings.Current!.Session.Id), timeout.Token);
    }
    private static ulong Counter(DiagnosticRuntimeSnapshot snapshot, string key) =>
        Probe(snapshot, "observation.capture").Counters.TryGetValue(key, out var value) ? value.Value : 0;

    [Fact]
    public void FailingProbesAreIsolatedAndEntriesAreWhitelistedAndBounded()
    {
        var info = new SystemInfo("Moongate", "0.1.0", "1.0", new string('a', 32), DateTimeOffset.UtcNow, "runtime", "os");
        var wide = new DiagnosticProbeReading("running");
        for (var i = 0; i < DiagnosticRuntimeService.MaximumCounters + 72; i++) wide.Count("item" + i, (ulong)i);
        for (var i = 0; i < DiagnosticRuntimeService.MaximumValues + 8; i++) wide.Value("value" + i, "code" + i);
        var unsafeReading = new DiagnosticProbeReading("running", "free text reason")
            .Count("ok", 1).Count("bad key", 2).Count("C:\\data", 3)
            .Value("path", @"C:\Users\someone\data").Value("name", "Alice Smith").Value("token", new string('f', 32))
            .Value("message", "boom: failed").Value("code", "worker.frameInvalid").Value("version", "1.2.3");
        List<IDiagnosticProbe> probes =
        [
            new Fake("healthy", () => new DiagnosticProbeReading("idle").Count("value", ulong.MaxValue)),
            new Fake("throws", () => throw new InvalidOperationException(@"secret C:\Users\someone")),
            new Fake("bad-state", () => new DiagnosticProbeReading("Running With Spaces")),
            new Fake("wide", () => wide), new Fake("unsafe", () => unsafeReading),
            new Fake("healthy", () => new DiagnosticProbeReading("idle")), new Fake("Bad Id", () => new DiagnosticProbeReading("idle"))
        ];
        for (var i = 0; i < DiagnosticRuntimeService.MaximumProbes; i++) probes.Add(new Fake("extra" + i, () => new DiagnosticProbeReading("idle")));
        var snapshot = new DiagnosticRuntimeService(probes, info, TimeProvider.System).Capture();

        Assert.Equal(DiagnosticRuntimeService.MaximumProbes, snapshot.Probes.Count);
        Assert.Equal(5, snapshot.TruncatedProbes); Assert.Equal(2, snapshot.RejectedProbes);
        Assert.Equal(ulong.MaxValue, Probe(snapshot, "healthy").Counters["value"].Value);
        foreach (var id in new[] { "throws", "bad-state" })
        {
            var failed = Probe(snapshot, id);
            Assert.Equal("unavailable", failed.State); Assert.Equal(ApiErrors.UnexpectedError, failed.Reason);
            Assert.Empty(failed.Counters); Assert.Empty(failed.Values);
        }
        var bounded = Probe(snapshot, "wide");
        Assert.Equal(DiagnosticRuntimeService.MaximumCounters, bounded.Counters.Count); Assert.Equal(DiagnosticRuntimeService.MaximumValues, bounded.Values.Count);
        Assert.Equal(80, bounded.TruncatedEntries); Assert.True(bounded.Counters.ContainsKey("item0")); Assert.False(bounded.Counters.ContainsKey("item199"));
        var filtered = Probe(snapshot, "unsafe");
        Assert.Null(filtered.Reason); Assert.Equal(6, filtered.RejectedEntries);
        Assert.Equal(new[] { "ok" }, filtered.Counters.Keys); Assert.Equal(new[] { "code", "version" }, filtered.Values.Keys.Order(StringComparer.Ordinal));
        var text = JsonSerializer.Serialize(snapshot, ContractJson.Options);
        Assert.DoesNotContain("someone", text); Assert.DoesNotContain("Alice", text); Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain(new string('f', 32), text);
    }

    private sealed class Fake(string id, Func<DiagnosticProbeReading> capture) : IDiagnosticProbe
    {
        public string Id => id;
        public string Module => "core";
        public DiagnosticProbeReading Capture() => capture();
    }
    private static DiagnosticProbeSnapshot Probe(DiagnosticRuntimeSnapshot snapshot, string id) => Assert.Single(snapshot.Probes, probe => probe.Id == id);
    private static void AssertWhitelisted(DiagnosticRuntimeSnapshot snapshot)
    {
        Assert.All(snapshot.Probes, probe =>
        {
            Assert.True(DiagnosticSanitizer.IsProbeId(probe.Id) && DiagnosticSanitizer.IsProbeState(probe.State), probe.Id);
            Assert.Equal(probe.Reason, DiagnosticSanitizer.ProbeReason(probe.Reason));
            Assert.Equal(0, probe.RejectedEntries);
            Assert.All(probe.Counters.Keys, key => Assert.True(DiagnosticSanitizer.IsProbeKey(key), probe.Id + ":" + key));
            Assert.All(probe.Values, pair => Assert.True(DiagnosticSanitizer.IsProbeKey(pair.Key) && DiagnosticSanitizer.IsProbeValue(pair.Value),
                probe.Id + ":" + pair.Key + "=" + pair.Value));
        });
    }
    private static WebApplication BuildInventoryHost(GameObservationApiTests.Area area)
    {
        var executable = GameObservationApiTests.Area.BuildExecutable("MabiTool.InventoryObservationProbe");
        return BackendApplication.Build(new(0, "observation-owner-token-123456789012345678901234567890", new HashSet<string>()),
            storage: new(Path.Combine(area.Root, "bootstrap"), true, PortableRoot: Path.Combine(area.Root, "portable")),
            gameControls: sp => { var leases = sp.GetRequiredService<ResourceLeaseCoordinator>(); return new(area.Bindings, leases, new(leases, area.Bindings, area.Device), false); },
            observationOptions: _ => new(executable, Path.GetDirectoryName(executable)!, area.Root, "game-observation")
            { Arguments = [area.Root, "host-late"], HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(2) },
            combatSource: sp => new MabiTool.Host.Combat.SharedCaptureCombatSource(sp.GetRequiredService<GameObservationService>(), sp.GetRequiredService<GameControlContext>(),
                new("fixture-combat", new("mabiagent-inventory", "1.0.0", "TW", TwClientFingerprint.GameBuild), Simulation: true)));
    }
    private sealed class StopApplication(WebApplication app) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(app.StopAsync()); }
    private static MabiToolClient Sdk(HttpClient client) => new(client.BaseAddress!, _ => ValueTask.FromResult(client.DefaultRequestHeaders.Authorization!.Parameter!));
    private static async Task Drain(ICombatObservationSubscription source, CancellationToken ct) { await foreach (var _ in source.ReadAsync(ct)) { } }
    private static async Task Wait(Func<Task<bool>> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, timeout.Token); }
}
