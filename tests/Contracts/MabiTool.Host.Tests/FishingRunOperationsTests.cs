using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Host.Life;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Life.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>钓鱼运行的宿主接入：受理与幂等、活动锁、租约资源、取消与紧急停止的输入释放、重启中断。识别与输入设备均为离线假件。</summary>
[SupportedOSPlatform("windows")]
[Collection("Persistent backend")]
public sealed class FishingRunOperationsTests
{
    private const string Token = "life_only_01234567890123456789012345678901234567";
    private const ushort KeyW = 0x57, KeySpace = 0x20;

    [Fact]
    public async Task ActivityLockHeldByAnotherModuleRejectsStartWithoutStartingTheWorker()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var http = Client(app);
        var binding = area.Bindings.Current!.Session;
        var lease = area.Leases.Acquire(new("color", "op-color", binding.Id, binding.BindingRevision, ["activity-control:" + binding.Id], TimeSpan.FromMinutes(1)));
        using var response = await Start(http, binding, "fishing-conflict");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!;
        Assert.Equal("resource.busy", problem.Code);
        Assert.Equal("color", problem.Details["occupiedBy"].GetString());
        Assert.Equal(0, area.Factory.Created);
        area.Leases.Release(lease.Id);
        await app.StopAsync();
    }

    [Fact]
    public async Task UnsupportedClientSizeIsReportedAsCapabilityReasonAndRejectsStart()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var http = Client(app);
        area.Bindings.Update(new("TW", 100, area.Started, new(500), "offline", new(0, 0, 1920, 1080), 96, 96, false));
        var binding = area.Bindings.Current!.Session;
        var snapshot = (await http.GetFromJsonAsync<FishingSnapshot>(LifeApi.Fishing.Path, ContractJson.Options))!;
        Assert.Equal(CapabilityStatus.Unavailable, snapshot.Capability.Status.KnownValue);
        Assert.Equal(LifeFishingErrors.LayoutUnsupported, snapshot.Capability.ReasonCode);
        using var response = await Start(http, binding, "fishing-layout");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("capability.unavailable", (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        Assert.Equal(0, area.Factory.Created);
        await app.StopAsync();
    }

    [Fact]
    public async Task RunHoldsBackgroundLeaseWithoutForegroundInputAndStopsGracefully()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var http = Client(app);
        var binding = area.Bindings.Current!.Session;
        using var started = await Start(http, binding, "fishing-run-0001");
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var operation = (await started.Content.ReadFromJsonAsync<OperationSnapshot>(ContractJson.Options))!;
        var runId = operation.Result!.Value.GetProperty("id").GetString()!;
        // 相同幂等键、相同请求返回原操作；活动运行存在时换键开始第二个运行被拒绝。
        using (var again = await Start(http, binding, "fishing-run-0001"))
            Assert.Equal(operation.Id, (await again.Content.ReadFromJsonAsync<OperationSnapshot>(ContractJson.Options))!.Id);
        using (var second = await Start(http, binding, "fishing-run-0002"))
            Assert.Equal("resource.busy", (await second.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);

        var run = await WaitRun(http, runId, value => value.Phase == "waiting-bite");
        Assert.True(run.ClockStarted);
        var owners = (await http.GetFromJsonAsync<GameInputOwnershipSnapshot>(ApiContract.GameInputOwnership.Path, ContractJson.Options))!.Owners.Single();
        Assert.Contains(GameBackgroundInputBroker.Resource, owners.Resources);
        Assert.Contains("activity-control:" + binding.Id, owners.Resources);
        Assert.DoesNotContain("foreground-input", owners.Resources);
        Assert.Contains($"key:{KeyW}:True", area.Device.Snapshot()); Assert.Contains($"key:{KeySpace}:True", area.Device.Snapshot());

        using var stop = await http.PostAsJsonAsync(LifeApi.StopFishing.Path.Replace("{id}", runId), new { });
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        var finished = await WaitOperation(http, operation.Id);
        Assert.Equal(OperationState.Succeeded, finished.State.KnownValue);
        var final = (await http.GetFromJsonAsync<FishingRun>(LifeApi.FishingRun.Path.Replace("{id}", runId), ContractJson.Options))!;
        Assert.Equal("user-stop", final.Stop!.Reason);
        Assert.True(final.Stop.InputReleased);
        Assert.Empty((await http.GetFromJsonAsync<GameInputOwnershipSnapshot>(ApiContract.GameInputOwnership.Path, ContractJson.Options))!.Owners);
        Assert.Equal(1, area.Factory.Disposed);
        using var late = await http.PostAsJsonAsync(LifeApi.StopFishing.Path.Replace("{id}", runId), new { });
        Assert.Equal("life.fishing.runInactive", (await late.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        await app.StopAsync();
    }

    [Fact]
    public async Task OperationCancelReleasesInputAndSendsNothingAfterwards()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var http = Client(app);
        var binding = area.Bindings.Current!.Session;
        var (operation, runId) = await StartRun(http, binding, "fishing-cancel-1");
        await WaitRun(http, runId, value => value.Phase == "waiting-bite");
        using var cancel = await http.PostAsJsonAsync(ApiContract.CancelOperation.Path.Replace("{id}", operation.Id), new CancelOperationRequest("用户取消"), ContractJson.Options);
        Assert.True(cancel.IsSuccessStatusCode);
        Assert.Equal(OperationState.Cancelled, (await WaitOperation(http, operation.Id)).State.KnownValue);
        var sent = area.Device.Snapshot().Count;
        await Task.Delay(300);
        Assert.Equal(sent, area.Device.Snapshot().Count);
        var final = (await http.GetFromJsonAsync<FishingRun>(LifeApi.FishingRun.Path.Replace("{id}", runId), ContractJson.Options))!;
        Assert.Equal(FishingPoseState.Unconfirmed, final.Stop!.PoseLeft.KnownValue);
        Assert.Empty(area.Leases.Snapshot());
        Assert.Equal(1, area.Factory.Disposed);
        await app.StopAsync();
    }

    [Fact]
    public async Task EmergencyStopRevokesTheLeaseCancelsTheRunAndEndsTheVisionProcess()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var http = Client(app);
        var binding = area.Bindings.Current!.Session;
        var (operation, runId) = await StartRun(http, binding, "fishing-estop-01");
        await WaitRun(http, runId, value => value.Phase == "waiting-bite");
        var receipt = app.Services.GetRequiredService<GameControlService>().StopLocal();
        Assert.True(receipt.CancellationRequested);
        Assert.Empty(receipt.Failures);
        Assert.Equal(OperationState.Cancelled, (await WaitOperation(http, operation.Id)).State.KnownValue);
        var final = (await http.GetFromJsonAsync<FishingRun>(LifeApi.FishingRun.Path.Replace("{id}", runId), ContractJson.Options))!;
        Assert.Equal("emergency-stop", final.Stop!.Reason);
        Assert.Empty(area.Leases.Snapshot());
        Assert.True(area.Device.Releases > 0);
        Assert.Equal(1, area.Factory.Disposed);
        await app.StopAsync();
    }

    [Fact]
    public async Task RunChangesAreVisibleThroughSnapshotAndEventTopic()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var http = Client(app);
        var binding = area.Bindings.Current!.Session;
        var (operation, runId) = await StartRun(http, binding, "fishing-event-01");
        await WaitRun(http, runId, value => value.Phase == "waiting-bite");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/event-snapshot");
        request.Headers.Add("X-Event-Topics", "life.fishing.changed");
        using var response = await http.SendAsync(request);
        var snapshot = (await response.Content.ReadFromJsonAsync<ProjectionSnapshot>(ContractJson.Options))!;
        Assert.Contains(snapshot.Resources, resource => resource.Type == "life.fishing.changed" && resource.ResourceId == runId);
        var state = (await http.GetFromJsonAsync<FishingSnapshot>(LifeApi.Fishing.Path, ContractJson.Options))!;
        Assert.Equal(runId, state.Run!.Id);
        Assert.Equal(CapabilityStatus.Available, state.Capability.Status.KnownValue);
        await http.PostAsJsonAsync(ApiContract.CancelOperation.Path.Replace("{id}", operation.Id), new CancelOperationRequest(null), ContractJson.Options);
        await WaitOperation(http, operation.Id);
        await app.StopAsync();
    }

    [Fact]
    public async Task HostRestartInterruptsTheRunWithoutResumingOrReplayingInput()
    {
        using var area = new Area();
        var clock = TimeProvider.System;
        string runOperation;
        using (var lease = DataRootLease.Open(area.Storage))
        await using (var store = SqliteStore.Open(lease))
        {
            var operations = new OperationStore(store, clock);
            var binding = area.Bindings.Current!.Session;
            var admission = await operations.AdmitFishingAsync("client", new(binding.Id, binding.BindingRevision, 600), "restart-run-1", () => binding, "epoch", true);
            runOperation = admission.Snapshot.Id;
            await operations.BeginFishingAsync(runOperation);
            await operations.UpdateFishingAsync(runOperation, run => run with { Phase = "fishing", ClockStarted = true, ElapsedSeconds = 42, Rounds = new(3, 2, 1, 0) });
        }
        using (var lease = DataRootLease.Open(area.Storage))
        await using (var store = SqliteStore.Open(lease))
        {
            Assert.Equal(1, await store.RecoverOperationsAsync());
            var operations = new OperationStore(store, clock);
            var operation = operations.Get(runOperation, "client", false);
            Assert.Equal(OperationState.Interrupted, operation.State.KnownValue);
            Assert.Equal(EffectStatus.Unknown, operation.EffectStatus.KnownValue);
            var run = operations.FishingRunByOperation(runOperation, "client", false);
            Assert.Equal("interrupted", run.Phase);
            Assert.Equal("backend-restarted", run.Stop!.Reason);
            Assert.Equal(new FishingRounds(3, 2, 1, 0), run.Rounds);
            Assert.Equal(0, await store.RecoverOperationsAsync());
        }
        Assert.Equal(0, area.Factory.Created);
    }

    private static Task<HttpResponseMessage> Start(HttpClient http, GameSession binding, string key, int? duration = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, LifeApi.StartFishing.Path)
        { Content = JsonContent.Create(new FishingRunRequest(binding.Id, binding.BindingRevision, duration), options: ContractJson.Options) };
        message.Headers.Add("Idempotency-Key", key);
        return http.SendAsync(message);
    }

    private static async Task<(OperationSnapshot Operation, string RunId)> StartRun(HttpClient http, GameSession binding, string key)
    {
        using var response = await Start(http, binding, key);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var operation = (await response.Content.ReadFromJsonAsync<OperationSnapshot>(ContractJson.Options))!;
        return (operation, operation.Result!.Value.GetProperty("id").GetString()!);
    }

    private static HttpClient Client(WebApplication app)
    {
        var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        return http;
    }

    private static async Task<FishingRun> WaitRun(HttpClient http, string runId, Func<FishingRun, bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var value = (await http.GetFromJsonAsync<FishingRun>(LifeApi.FishingRun.Path.Replace("{id}", runId), ContractJson.Options, timeout.Token))!;
            if (ready(value)) return value;
            Assert.DoesNotContain(value.Phase, new[] { "failed", "interrupted", "cancelled" });
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<OperationSnapshot> WaitOperation(HttpClient http, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var value = (await http.GetFromJsonAsync<OperationSnapshot>($"/api/v1/operations/{id}", ContractJson.Options, timeout.Token))!;
            if (SqliteStore.IsTerminal(value.State.KnownValue)) return value;
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class Area : IDisposable
    {
        public string Root { get; }
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public StorageOptions Storage => new(Path.Combine(Root, "bootstrap"), true, DataRoot: Path.Combine(Root, "data"));
        public GameSessionBindingTracker Bindings { get; } = new();
        public ResourceLeaseCoordinator Leases { get; } = new();
        public RecordingDevice Device { get; } = new();
        public FakeFactory Factory { get; }

        public Area()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MabiTool.slnx"))) dir = dir.Parent;
            Root = Path.Combine(dir!.FullName, "artifacts", "tmp", "fishing-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Factory = new(this);
            Bindings.Update(new("TW", 100, Started, new(500), "offline", new(0, 0, 1280, 960), 96, 96, false));
        }

        public WebApplication Build() => BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: Storage,
            gameControls: _ => new GameControlContext(Bindings, Leases, new GameInputBroker(Leases, Bindings, new OfflineInput()), false)
            { Background = new GameBackgroundInputBroker(Leases, Bindings, Device) },
            fishingSessions: _ => Factory);

        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Root, "..")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Root.Contains("fishing-host-")) throw new InvalidOperationException();
            Directory.Delete(Root, true);
        }
    }

    private sealed class OfflineInput : IGameInputDevice
    {
        public UInt64String? ForegroundWindowIdentity => new(500);
        public void Press(InputControl control) { }
        public void Release(InputControl control) { }
        public void MovePointer(PhysicalScreenPoint point) { }
        public void ReleaseAll() { }
    }

    /// <summary>记录后台输入的离线设备：按下与松开的按键、触点点按，以及释放次数。</summary>
    private sealed class RecordingDevice : IGameBackgroundInputDevice
    {
        private readonly object gate = new();
        private readonly List<string> log = [];
        private int releases;
        public int Releases => Volatile.Read(ref releases);
        public List<string> Snapshot() { lock (gate) return [.. log]; }
        public string? Unavailable(GameWindowBinding binding) => null;
        public void Gesture(UInt64String window, IReadOnlyList<PhysicalScreenPoint[]> frames, int holdMilliseconds, int releaseDelayMilliseconds, Action checkpoint)
        {
            checkpoint();
            lock (gate) log.Add($"tap:{frames[0][0].X},{frames[0][0].Y}");
        }
        public void Key(UInt64String window, ushort virtualKey, bool down) { lock (gate) log.Add($"key:{virtualKey}:{down}"); }
        public void ReleaseAll() => Interlocked.Increment(ref releases);
    }

    private sealed class FakeFactory(Area area) : IFishingSessionFactory
    {
        private int created, disposed;
        public int Created => Volatile.Read(ref created);
        public int Disposed => Volatile.Read(ref disposed);
        public string? UnavailableReason => null;

        public Task<IFishingSession> CreateAsync(GameWindowBinding binding, ResourceLeaseSnapshot lease, string runId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref created);
            var input = new FishingInputAdapter(new GameBackgroundInputBroker(area.Leases, area.Bindings, area.Device), area.Leases, lease, binding.Session, TimeProvider.System);
            return Task.FromResult<IFishingSession>(new Session(this, input));
        }

        private sealed class Session(FakeFactory owner, FishingInputAdapter input) : IFishingSession
        {
            public IFishingVision Vision { get; } = new SceneVision();
            public IFishingInput Input => input;
            public void ReleaseInput() => input.Release();
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.disposed); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>只有场景可见的画面：抛竿之后永远没有上钩，运行停在等待上钩。</summary>
    private sealed class SceneVision : IFishingVision
    {
        private ulong frame;
        public async ValueTask<FishingObservation> NextAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new(new(++frame), frame * 10d, true, false, false, null, null, null, 1, 1);
        }
    }
}
