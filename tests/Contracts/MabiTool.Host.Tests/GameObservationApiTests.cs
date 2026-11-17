using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class GameObservationApiTests
{
    [Fact]
    public async Task RuntimeQueriesKeepTheWorkerStoppedAndCapturePermissionIsIndependent()
    {
        using var area = new Area(); await using var app = area.Build(installed: false); await app.StartAsync();
        using var owner = Client(app); var grant = Pair(app, false); using var viewer = Client(app, grant.Token);
        var runtime = await owner.GetFromJsonAsync<GameObservationRuntime>(ApiContract.GameObservationRuntime.Path, ContractJson.Options);
        Assert.False(runtime!.Installed); Assert.Null(runtime.WorkerProcessId); Assert.Equal("stopped", runtime.State);
        using var forbidden = await viewer.GetAsync(ApiContract.GameObservation.Path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await Error(owner, ApiContract.QueryObservationChannels.Path, null, null, null, HttpStatusCode.UnprocessableEntity, "observation.workerUnavailable");
        Assert.Empty(Directory.GetFiles(area.Root, "process-start-*")); Assert.Empty(area.Device.Commands);
        await app.StopAsync();
    }
    [Fact]
    public async Task ExplicitChannelEnumerationUsesAndReleasesARealAuthenticatedWorker()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var client = Client(app);
        var channels = await Post<ApiList<CaptureChannelOption>>(client, ApiContract.QueryObservationChannels.Path, null);
        Assert.Equal(new string('1', 32), Assert.Single(channels.Items).Id);
        Assert.Single(Directory.GetFiles(area.Root, "process-start-*"));
        var runtime = app.Services.GetRequiredService<GameObservationService>().Runtime();
        Assert.Null(runtime.WorkerProcessId); Assert.Equal(0, runtime.SubscriberCount);
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("game-access"));
        await app.StopAsync();
    }
    [Fact]
    public async Task TwoPrincipalsShareCaptureAndLastReleaseClosesHandlesAndTheProcessTree()
    {
        using var area = new Area(); await using var app = area.Build("tree"); await app.StartAsync();
        var firstGrant = Pair(app, true); var secondGrant = Pair(app, true);
        using var first = Client(app, firstGrant.Token); using var second = Client(app, secondGrant.Token); using var owner = Client(app);
        var initial = await Read(first);
        var request = Request(area);
        var firstLease = await Post<GameObservationSubscription>(first, ApiContract.StartGameObservation.Path, request, "capture-first", initial.ETag);
        var live = await WaitSnapshot(first, value => value.State == "running" && value.Latest?.Value.Identity is not null);
        Assert.Equal(9007199254740993UL, live.Latest!.Value.Identity!.CharacterId.Value);
        Assert.Equal("observation-fixture", live.Latest.Source.Provider);
        Assert.Equal(ObservationQuality.Estimated, live.Latest.Quality.KnownValue);
        Assert.Equal(1d, live.Latest.Value.Music!.WholeScoreDurationSeconds);
        Assert.Equal(.5, live.Latest.Value.Music.SelectedDurationSeconds); Assert.False(live.Latest.Value.Music.PlaybackTimingVerified);
        var pid = app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId!.Value;
        var modules = app.Services.GetRequiredService<ModuleRuntime>();
        Assert.Equal("observation-" + firstLease.CaptureId, Assert.Single(modules.ActiveOperations("game-access")));
        Assert.Equal("module.inUse", (await Assert.ThrowsAsync<ModuleRuntimeException>(() => modules.SetEnabledAsync("game-access", false))).Code);
        var child = int.Parse(await File.ReadAllTextAsync(Path.Combine(area.Root, "child-pid")), System.Globalization.CultureInfo.InvariantCulture);
        var secondLease = await Post<GameObservationSubscription>(second, ApiContract.StartGameObservation.Path, request, "capture-second", (await Read(second)).ETag);
        Assert.Equal(firstLease.CaptureId, secondLease.CaptureId); Assert.Equal(2, secondLease.Snapshot.SubscriberCount);
        Assert.Equal(firstLease.Id, (await Post<GameObservationSubscription>(first, ApiContract.StartGameObservation.Path, request, "capture-first", initial.ETag)).Id);
        await Error(second, ApiContract.StartGameObservation.Path, request, "stale-observation", initial.ETag, HttpStatusCode.PreconditionFailed, "revision.stale");
        using var otherRelease = await second.DeleteAsync(ApiContract.ReleaseGameObservation.Path.Replace("{id}", firstLease.Id));
        Assert.Equal(HttpStatusCode.Forbidden, otherRelease.StatusCode);
        var released = await Release(first, firstLease.Id); Assert.True(released.Released); Assert.Equal(1, released.Snapshot.SubscriberCount);
        Assert.True(Alive(pid)); Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
        Assert.True((await Release(first, firstLease.Id)).Released);
        var ended = await Release(second, secondLease.Id); Assert.True(ended.Released); Assert.Equal("stopped", ended.Snapshot.State);
        Assert.Null(ended.Snapshot.Latest); Assert.Null(ended.Snapshot.CaptureId);
        await Wait(() => !Alive(pid) && !Alive(child));
        Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*")); Assert.Empty(area.Device.Commands);
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
        Assert.Empty(modules.ActiveOperations("game-access"));
        await modules.SetEnabledAsync("game-access", false);
        await app.StopAsync();
    }
    [Fact]
    public async Task ObservationEventsRequireCapturePermissionAndShareTheBackendSnapshot()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync();
        var grant = Pair(app, true); var viewerGrant = Pair(app, false);
        using var capture = Client(app, grant.Token); using var viewer = Client(app, viewerGrant.Token);
        var lease = await Post<GameObservationSubscription>(capture, ApiContract.StartGameObservation.Path, Request(area), "capture-events", (await Read(capture)).ETag);
        await WaitSnapshot(capture, value => value.State == "running" && value.Latest is not null);
        ProjectionSnapshot? projection = null;
        await Wait(async () =>
        {
            projection = await capture.GetFromJsonAsync<ProjectionSnapshot>(ApiContract.EventSnapshot.Path, ContractJson.Options);
            return projection!.Resources.Any(value => value.Type == "game.observation.changed" && value.Data.GetProperty("state").GetString() == "running");
        });
        var observed = Assert.Single(projection!.Resources, value => value.Type == "game.observation.changed");
        var snapshot = observed.Data.Deserialize<GameObservationSnapshot>(ContractJson.Options)!;
        Assert.Equal(lease.CaptureId, snapshot.CaptureId); Assert.NotNull(snapshot.Latest);
        var readOnly = (await viewer.GetFromJsonAsync<ProjectionSnapshot>(ApiContract.EventSnapshot.Path, ContractJson.Options))!;
        Assert.DoesNotContain(readOnly.Resources, value => value.Type == "game.observation.changed");
        await Release(capture, lease.Id); await app.StopAsync();
    }
    [Fact]
    public async Task RevocationRemovesOnlyThatDemandThenStopsTheLastCapture()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync();
        var firstGrant = Pair(app, true); var secondGrant = Pair(app, true);
        using var first = Client(app, firstGrant.Token); using var second = Client(app, secondGrant.Token); using var owner = Client(app);
        await Post<GameObservationSubscription>(first, ApiContract.StartGameObservation.Path, Request(area), "revoke-first", (await Read(first)).ETag);
        await WaitSnapshot(first, value => value.State == "running");
        await Post<GameObservationSubscription>(second, ApiContract.StartGameObservation.Path, Request(area), "revoke-second", (await Read(second)).ETag);
        var pid = app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId!.Value;
        app.Services.GetRequiredService<LocalAuthService>().Revoke(firstGrant.Session.ClientId);
        await WaitSnapshot(second, value => value.SubscriberCount == 1); Assert.True(Alive(pid));
        app.Services.GetRequiredService<LocalAuthService>().Revoke(secondGrant.Session.ClientId);
        await WaitSnapshot(owner, value => value.State == "stopped" && value.SubscriberCount == 0);
        await Wait(() => !Alive(pid)); Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
        await app.StopAsync();
    }
    [Fact]
    public async Task BindingReplacementStopsTheOldCaptureAndInvalidatesItsEvidence()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var client = Client(app);
        await Post<GameObservationSubscription>(client, ApiContract.StartGameObservation.Path, Request(area), "binding-capture", (await Read(client)).ETag);
        var running = await WaitSnapshot(client, value => value.State == "running" && value.Latest?.Value.Identity is not null);
        var pid = app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId;
        // 窗口几何变化不结束捕获：同一 Worker 继续运行，已识别的本机身份保留。
        area.ChangeWindowGeometry();
        await Task.Delay(1000);
        var kept = (await Read(client)).Snapshot;
        Assert.Equal(("running", running.CaptureId, running.BindingRevision), (kept.State, kept.CaptureId, kept.BindingRevision));
        Assert.Equal(pid, app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId);
        Assert.NotNull(kept.Latest?.Value.Identity);
        Assert.Single(Directory.GetFiles(area.Root, "capture-start-*"));
        // 游戏进程更换（启动时间与构建变化）是新的游戏会话：结束旧捕获并撤销其证据。
        var old = area.Bindings.Current!;
        area.Bindings.Update(new("TW", old.Session.ProcessId, old.Session.ProcessStartTime.AddSeconds(1), old.Session.WindowIdentity,
            "new-fixture", old.Bounds, old.DpiX, old.DpiY, false));
        var stopped = await WaitSnapshot(client, value => value.State == "faulted" && value.CaptureId is null);
        Assert.Equal("input.bindingChanged", stopped.Reason); Assert.Null(stopped.Latest);
        Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
        await app.StopAsync();
    }
    [Theory]
    [InlineData("exit")]
    [InlineData("stale")]
    [InlineData("fail-start")]
    [InlineData("null-value")]
    [InlineData("bad-realm")]
    [InlineData("bad-members")]
    [InlineData("bad-timing")]
    [InlineData("regression")]
    [InlineData("retired")]
    public async Task WorkerFaultsClearEvidenceAndDoNotRestartTheOldDemand(string mode)
    {
        using var area = new Area(); await using var app = area.Build(mode); await app.StartAsync(); using var client = Client(app);
        var initial = await Read(client); var request = Request(area);
        var lease = await Post<GameObservationSubscription>(client, ApiContract.StartGameObservation.Path, request, "fault-capture", initial.ETag);
        var stopped = await WaitSnapshot(client, value => value.State == "faulted" && value.CaptureId is null);
        Assert.Null(stopped.Latest); Assert.Equal(0, stopped.SubscriberCount);
        Assert.Equal(lease.Id, (await Post<GameObservationSubscription>(client, ApiContract.StartGameObservation.Path, request, "fault-capture", initial.ETag)).Id);
        Assert.Single(Directory.GetFiles(area.Root, "process-start-*"));
        Assert.Null(app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId);
        Assert.Empty(app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities);
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("game-access"));
        await app.StopAsync();
    }
    [Theory]
    [InlineData("normal")]
    [InlineData("late")]
    [InlineData("stall")]
    public async Task StopDrainsOrKillsTheCaptureAndRejectsLateReports(string mode)
    {
        // 负载下 Worker 响应取消或心跳可能超过 2 秒：会响应的模式放宽取消宽限，确保判定的是正常排空；
        // stall 保留默认 2 秒宽限并放宽心跳超时，确保终止原因来自取消宽限而非心跳。
        using var area = new Area(); await using var app = area.Build(mode, tune: options => options with
        {
            CancellationGrace = mode == "stall" ? options.CancellationGrace : TimeSpan.FromSeconds(10),
            HeartbeatTimeout = TimeSpan.FromSeconds(10)
        }); await app.StartAsync(); using var client = Client(app);
        var lease = await Post<GameObservationSubscription>(client, ApiContract.StartGameObservation.Path, Request(area), "stop-capture", (await Read(client)).ETag);
        await WaitSnapshot(client, value => value.State == "running");
        var pid = app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId!.Value;
        var stopped = await Release(client, lease.Id);
        Assert.Null(stopped.Snapshot.Latest); Assert.Null(stopped.Snapshot.CaptureId); Assert.Equal(0, stopped.Snapshot.SubscriberCount);
        Assert.Equal(mode == "stall" ? "faulted" : "stopped", stopped.Snapshot.State);
        if (mode == "stall") Assert.Equal("worker.cancelTimeout", stopped.Snapshot.Reason);
        else Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
        await Wait(() => !Alive(pid)); await Task.Delay(150);
        Assert.Null((await Read(client)).Snapshot.Latest); Assert.Empty(area.Device.Commands);
        await app.StopAsync();
    }
    [Fact]
    public async Task PreparationDuringBuildCanBeReleasedBeforeAnyObservationBecomesAvailable()
    {
        using var area = new Area(); await using var app = area.Build("hold-build"); await app.StartAsync(); using var client = Client(app);
        var lease = await Post<GameObservationSubscription>(client, ApiContract.StartGameObservation.Path, Request(area), "hold-build-capture", (await Read(client)).ETag);
        Assert.Equal("starting", lease.Snapshot.State); Assert.Null(lease.Snapshot.Latest);
        var stopped = await Release(client, lease.Id); Assert.Equal("stopped", stopped.Snapshot.State);
        Assert.Null(stopped.Snapshot.Latest); Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
        await app.StopAsync();
    }
    [Fact]
    public async Task UnverifiedProfilesAndOldBindingsDoNotLaunchTheWorker()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var client = Client(app);
        var initial = await Read(client);
        await Error(client, ApiContract.StartGameObservation.Path, Request(area) with { Profile = "kr-combat-v1" }, "invalid-profile", initial.ETag,
            HttpStatusCode.UnprocessableEntity, "observation.profileUnavailable");
        await Error(client, ApiContract.StartGameObservation.Path, Request(area) with { BindingRevision = new(0) }, "invalid-binding", initial.ETag,
            HttpStatusCode.Conflict, "input.bindingChanged");
        Assert.Empty(Directory.GetFiles(area.Root, "process-start-*")); await app.StopAsync();
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("silent")]
    public async Task SharedObservationReachesTheRoomAndExpiryOrReleaseWithdrawsItsEvidence(string mode)
    {
        using var area = new Area(); await using var app = area.Build(mode); await app.StartAsync();
        using var owner = Client(app); var viewerGrant = Pair(app, false, rooms: true); using var viewer = Client(app, viewerGrant.Token);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, CancellationToken.None);
        var created = await Post<RoomLifecycleSnapshot>(owner, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.LocalEndpoint, "房主"), "observed-room");
        var joined = await Post<RoomLifecycleSnapshot>(viewer, ApiContract.JoinRoom.Path, new RoomJoinRequest(server.LocalEndpoint, created.Code!, "房间成员"), "observed-join");
        var lease = await Post<GameObservationSubscription>(owner, ApiContract.StartGameObservation.Path, Request(area), "observed-capture", (await Read(owner)).ETag);
        JsonElement member = default;
        await Wait(async () =>
        {
            var room = (await owner.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options))!;
            member = room.Room!.Value.GetProperty("members").EnumerateArray().Single(item => item.GetProperty("id").GetString() == created.MemberId).Clone();
            // 演奏自动循环：房间按抓包估算的开始时间与整谱时长判定一轮结束，校时完成后上报开始时间。
            return member.GetProperty("gameIdentity").ValueKind == JsonValueKind.Object &&
                member.GetProperty("music").GetProperty("estimatedStartServerMs").ValueKind == JsonValueKind.Number;
        });
        Assert.Equal("9007199254740993", member.GetProperty("gameIdentity").GetProperty("characterId").GetString());
        var music = member.GetProperty("music");
        Assert.Equal("同名合成乐谱", music.GetProperty("title").GetString());
        Assert.Equal(1, music.GetProperty("wholeScoreDurationSeconds").GetDouble());
        Assert.Equal(.5, music.GetProperty("selectedDurationSeconds").GetDouble());
        Assert.True(music.GetProperty("automaticStopAvailable").GetBoolean());
        Assert.Equal("tw-receive-offset-v1", music.GetProperty("timingSource").GetString());
        var peer = (await viewer.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options))!;
        Assert.Equal(JsonValueKind.Null, peer.Room!.Value.GetProperty("members").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == joined.MemberId).GetProperty("gameIdentity").ValueKind);
        if (mode == "silent")
        {
            await WaitSnapshot(owner, value => value.Latest?.Quality.KnownValue == ObservationQuality.Stale);
            await Wait(async () => (await owner.GetFromJsonAsync<ProjectionSnapshot>(ApiContract.EventSnapshot.Path, ContractJson.Options))!.Resources
                .Any(item => item.Type == "game.observation.changed" && item.Data.GetProperty("latest").GetProperty("quality").GetString() == "stale"));
        }
        else await Release(owner, lease.Id);
        await Wait(async () =>
        {
            var room = (await viewer.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options))!;
            return room.Room!.Value.GetProperty("members").EnumerateArray().Single(item => item.GetProperty("id").GetString() == created.MemberId)
                .GetProperty("gameIdentity").ValueKind == JsonValueKind.Null;
        });
        if (mode == "silent") await Release(owner, lease.Id);
        Assert.Empty(area.Device.Commands); await app.StopAsync();
    }

    internal sealed class Area : IDisposable
    {
        private const string Token = "observation-owner-token-123456789012345678901234567890";
        public string Root { get; } = Path.Combine(FindRoot(), "artifacts", "test-data", "observation-" + Guid.NewGuid().ToString("N"));
        public GameSessionBindingTracker Bindings { get; } = new();
        public RoomInputApiTests.Device Device { get; } = new();
        public Area()
        {
            Directory.CreateDirectory(Root);
            Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), "fixture-observation", new(0, 0, 800, 600), 96, 96, false));
        }
        public WorkerProcessOptions Options(string mode = "normal")
        {
            var executable = BuildExecutable("MabiTool.ObservationProbe");
            return new(executable, Path.GetDirectoryName(executable)!, Root, "game-observation")
            { Arguments = [Root, mode], HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(2) };
        }
        public WebApplication Build(string mode = "normal", bool installed = true, ObservationCombatProfile? combatProfile = null,
            StorageOptions? storage = null, Func<WorkerProcessOptions, WorkerProcessOptions>? tune = null, bool automation = false,
            IGameWindowSource? windows = null) => BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: storage, gameAutomation: automation,
            gameWindowSource: windows ?? new NoWindows(),
            gameControls: sp =>
            {
                var leases = sp.GetRequiredService<ResourceLeaseCoordinator>(); return new(Bindings, leases, new(leases, Bindings, Device), false);
            }, observationOptions: _ => installed ? (tune ?? (options => options))(Options(mode)) : null,
            combatSource: combatProfile is null ? null : sp => new MabiTool.Host.Combat.SharedCaptureCombatSource(
                sp.GetRequiredService<GameObservationService>(), sp.GetRequiredService<GameControlContext>(), combatProfile,
                sp.GetService<OperationStore>()));
        public static string FindRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "MabiTool.slnx"))) current = current.Parent;
            return current?.FullName ?? throw new InvalidOperationException("无法定位仓库");
        }
        public static string BuildExecutable(string project)
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            var bin = output.Parent?.Parent ?? throw new InvalidOperationException("无法定位同批构建输出");
            var executable = Path.Combine(bin.FullName, project, output.Name, project + ".exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("缺少同批构建的观察进程", executable);
            return executable;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        /// <summary>同一游戏进程的窗口几何连续变化（拖到另一块屏幕：移动、换 DPI、最小化再恢复），每步递增绑定修订、游戏会话不变。</summary>
        public void ChangeWindowGeometry()
        {
            var old = Bindings.Current!; var session = old.Session;
            foreach (var (bounds, dpi, minimized) in new[] { (old.Bounds with { X = old.Bounds.X + 1920 }, 96d, false),
                (old.Bounds with { X = old.Bounds.X + 1920, Width = old.Bounds.Width * 3 / 2 }, 144d, false),
                (old.Bounds with { X = old.Bounds.X + 1920, Width = old.Bounds.Width * 3 / 2 }, 144d, true), (old.Bounds, old.DpiX, false) })
                Bindings.Update(new(session.Region, session.ProcessId, session.ProcessStartTime, session.WindowIdentity, session.GameBuild, bounds, dpi, dpi, minimized));
            Assert.Equal(session.Id, Bindings.Current!.Session.Id);
            Assert.Equal(session.BindingRevision.Value + 4, Bindings.Current.Session.BindingRevision.Value);
        }
        /// <summary>无界面测试不枚举真实窗口。</summary>
        private sealed class NoWindows : IGameWindowSource
        {
            public IReadOnlyList<SelectableGameWindow> Enumerate() => [];
            public GameWindowObservation? Refresh(GameWindowObservation selected) => null;
            public bool TryActivate(GameWindowObservation selected) => false;
        }
        public static HttpClient Owner(WebApplication app) => Client(app, Token);
    }
    private static GameObservationStartRequest Request(Area area) => new(new string('1', 32), area.Bindings.Current!.Session.BindingRevision);
    internal static async Task<GameObservationSubscription> BeginCapture(HttpClient client, Area area, string key) =>
        await Post<GameObservationSubscription>(client, ApiContract.StartGameObservation.Path, Request(area), key, (await Read(client)).ETag);
    internal static Task<GameObservationRelease> EndCapture(HttpClient client, string id) => Release(client, id);
    private static AccessGrant Pair(WebApplication app, bool observe, bool rooms = false)
    {
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        List<string> permissions = [ApiPermissions.SystemRead];
        if (observe) permissions.Add(ApiPermissions.GameObserve);
        if (rooms) permissions.Add(ApiPermissions.RoomsCoordinate);
        var challenge = auth.CreateChallenge(new("观察客户端", null, permissions));
        return auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
    }
    private static HttpClient Client(WebApplication app, string? token = null)
    {
        if (token is null) return Area.Owner(app);
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", token); return client;
    }
    private static async Task<(GameObservationSnapshot Snapshot, string ETag)> Read(HttpClient client)
    {
        using var response = await client.GetAsync(ApiContract.GameObservation.Path); response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<GameObservationSnapshot>(ContractJson.Options))!, response.Headers.ETag!.ToString());
    }
    private static async Task<GameObservationSnapshot> WaitSnapshot(HttpClient client, Func<GameObservationSnapshot, bool> condition)
    {
        GameObservationSnapshot? snapshot = null;
        await Wait(async () => { snapshot = (await Read(client)).Snapshot; return condition(snapshot); }); return snapshot!;
    }
    private static async Task Wait(Func<bool> condition) => await Wait(() => Task.FromResult(condition()));
    private static async Task Wait(Func<Task<bool>> condition)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, deadline.Token); }
    private static bool Alive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task<T> Post<T>(HttpClient client, string path, object? body, string? key = null, string? etag = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }, options: ContractJson.Options) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key); if (etag is not null) request.Headers.Add("If-Match", etag);
        using var response = await client.SendAsync(request); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }
    private static async Task Error(HttpClient client, string path, object? body, string? key, string? etag, HttpStatusCode status, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }, options: ContractJson.Options) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key); if (etag is not null) request.Headers.Add("If-Match", etag);
        using var response = await client.SendAsync(request); Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
    }
    private static async Task<GameObservationRelease> Release(HttpClient client, string id)
    {
        using var response = await client.DeleteAsync(ApiContract.ReleaseGameObservation.Path.Replace("{id}", id));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GameObservationRelease>(ContractJson.Options))!;
    }
}
