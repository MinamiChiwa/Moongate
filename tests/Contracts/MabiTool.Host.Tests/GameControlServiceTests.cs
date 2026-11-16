using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class GameControlServiceTests
{
    [Fact]
    public void OwnershipContainsOnlyValidGameControlLeasesAndPreservesIndependentActivities()
    {
        using var area = new Area();
        var input = area.Lease("color", "foreground-input", "activity-control:" + area.Binding.Session.Id);
        var activity = area.Lease("cli", "activity-control:other-session");
        area.Lease("observer", "provider-channel:passive");
        var snapshot = area.Service.Snapshot();
        Assert.Equal(area.Info.InstanceId, snapshot.BackendEpoch);
        Assert.True(snapshot.Simulation);
        Assert.Equal(2, snapshot.Owners.Count);
        Assert.Contains(snapshot.Owners, owner => owner.OwnerId == "color" && owner.OperationId == input.OperationId);
        Assert.Contains(snapshot.Owners, owner => owner.OwnerId == "cli" && owner.OperationId == activity.OperationId);
        var json = JsonSerializer.Serialize(snapshot, ContractJson.Options);
        Assert.DoesNotContain("fencingToken", json);
        Assert.DoesNotContain("leaseId", json);
        area.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(area.Service.Snapshot().Owners);
        Assert.Equal(2, snapshot.Owners.Count);
    }

    [Fact]
    public void StopReleasesHeldInputAndActivityOnlyLeasesWhileLateActionsLeaveANewOwnerIntact()
    {
        using var area = new Area();
        var old = area.Lease("color", "foreground-input", "activity-control:" + area.Binding.Session.Id);
        area.Lease("cli", "activity-control:other-session");
        var passive = area.Lease("observer", "provider-channel:passive");
        area.Controls.Input.Execute(area.Action(old, InputControl.MouseLeft));
        var receipt = area.Service.EmergencyStop("owner", area.Request(), "stop-old-input");
        Assert.Equal(2, receipt.RevokedLeases);
        Assert.Equal(1, receipt.ReleasedControls);
        Assert.Equal("released", receipt.InputReleaseState);
        Assert.Equal(EffectStatus.Unknown, receipt.EffectStatus.KnownValue);
        Assert.Empty(receipt.Failures);
        Assert.True(area.Leases.Validate(passive.Id, "provider-channel:passive", passive.FencingToken,
            passive.GameSessionId, passive.BindingRevision));
        var current = area.Lease("room", "foreground-input");
        Assert.Equal("input.leaseInvalid", Assert.Throws<GameInputException>(() =>
            area.Controls.Input.Execute(area.Action(old, InputControl.Space))).Code);
        Assert.True(area.Leases.Validate(current.Id, "foreground-input", current.FencingToken,
            current.GameSessionId, current.BindingRevision));
        Assert.Equal([InputControl.MouseLeft], area.Device.Pressed);
        Assert.Equal(1, area.Device.ReleaseAllCalls);
    }

    [Fact]
    public void RetryReturnsTheOriginalReceiptWithoutStoppingNewActivity()
    {
        using var area = new Area();
        area.Lease("color", "foreground-input");
        var request = area.Request();
        var original = area.Service.EmergencyStop("owner", request, "same-stop-request");
        var current = area.Lease("room", "foreground-input");
        var repeated = area.Service.EmergencyStop("owner", request, "same-stop-request");
        Assert.Same(original, repeated);
        Assert.Equal(1, area.Device.ReleaseAllCalls);
        Assert.True(area.Leases.Validate(current.Id, "foreground-input", current.FencingToken,
            current.GameSessionId, current.BindingRevision));
        Assert.Equal("idempotency.conflict", Assert.Throws<OperationException>(() =>
            area.Service.EmergencyStop("owner", request with { ExpiresAt = request.ExpiresAt.AddSeconds(1) }, "same-stop-request")).Code);
        Assert.Equal(1, area.Device.ReleaseAllCalls);
    }

    [Fact]
    public void DeviceFailureStillRevokesGameLeasesAndRequestsCancellation()
    {
        using var area = new Area();
        area.Lease("color", "foreground-input");
        using var rooms = new RoomSessionManager();
        var service = new GameControlService(area.Controls, area.Info, area.Clock, area.Auth, rooms);
        area.Device.FailRelease = true;
        var receipt = service.EmergencyStop("owner", area.Request(), "failed-input-release");
        Assert.Equal("failed", receipt.InputReleaseState);
        Assert.Null(receipt.ReleasedControls);
        Assert.True(receipt.CancellationRequested);
        Assert.Equal(EffectStatus.Unknown, receipt.EffectStatus.KnownValue);
        Assert.Equal(1, receipt.RevokedLeases);
        var failure = Assert.Single(receipt.Failures);
        Assert.Equal("input", failure.Component);
        Assert.Equal("input.releaseFailed", failure.Code);
        Assert.Empty(service.Snapshot().Owners);
    }

    [Fact]
    public void InvalidPermissionEpochAndLifetimeFailBeforeInputIsTouched()
    {
        using var area = new Area();
        var challenge = area.Auth.CreateChallenge(new("viewer", null, [ApiPermissions.SystemRead]));
        var viewer = area.Auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
        var lease = area.Lease("color", "foreground-input");
        Assert.Equal("permission.denied", Assert.Throws<OperationException>(() =>
            area.Service.EmergencyStop(viewer.Session.ClientId, area.Request(), "denied-stop-request")).Code);
        Assert.Equal("game.controlEpochChanged", Assert.Throws<OperationException>(() =>
            area.Service.EmergencyStop("owner", area.Request() with { BackendEpoch = "old-epoch" }, "old-epoch-stop")).Code);
        Assert.Equal("game.stopRequestExpired", Assert.Throws<OperationException>(() =>
            area.Service.EmergencyStop("owner", area.Request() with { ExpiresAt = area.Clock.GetUtcNow() }, "expired-stop-request")).Code);
        Assert.Equal("request.invalid", Assert.Throws<OperationException>(() =>
            area.Service.EmergencyStop("owner", area.Request() with { ExpiresAt = area.Clock.GetUtcNow().AddMinutes(6) }, "too-long-stop-request")).Code);
        Assert.Equal("idempotency.required", Assert.Throws<OperationException>(() =>
            area.Service.EmergencyStop("owner", area.Request(), null)).Code);
        Assert.Equal(0, area.Device.ReleaseAllCalls);
        Assert.True(area.Leases.Validate(lease.Id, "foreground-input", lease.FencingToken, lease.GameSessionId, lease.BindingRevision));
    }

    [Fact]
    public void ExpiredReceiptsAreReclaimedButOldRequestsCannotReviveAfterAClockRollback()
    {
        using var area = new Area();
        var request = area.Request();
        area.Service.EmergencyStop("owner", request, "expiry-clock-stop");
        area.Clock.Advance(TimeSpan.FromSeconds(31));
        area.Service.EmergencyStop("owner", area.Request(), "prune-expiry-stop");
        area.Clock.ChangeUtc(TimeSpan.FromSeconds(-31));
        Assert.Equal("game.stopRequestExpired", Assert.Throws<OperationException>(() =>
            area.Service.EmergencyStop("owner", request, "expiry-clock-stop")).Code);
        Assert.Equal(2, area.Device.ReleaseAllCalls);
    }

    [Fact]
    public void ReceiptCapacityIsBoundedAndTheLocalHotkeyPathRemainsAvailable()
    {
        using var area = new Area();
        var request = area.Request();
        for (var i = 0; i < GameControlService.MaximumReceipts; i++)
            area.Service.EmergencyStop("owner", request, "stop-capacity-" + i);
        Assert.Equal("resource.capacityExceeded", Assert.Throws<OperationException>(() =>
            area.Service.EmergencyStop("owner", request, "stop-capacity-full")).Code);
        Assert.Equal(GameControlService.MaximumReceipts, area.Device.ReleaseAllCalls);
        area.Service.StopLocal();
        Assert.Equal(GameControlService.MaximumReceipts + 1, area.Device.ReleaseAllCalls);
        area.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("released", area.Service.EmergencyStop("owner", area.Request(), "stop-after-expiry").InputReleaseState);
    }

    [Fact]
    public async Task PublicApiSharesOwnershipAndEnforcesStopPermissionWithoutPersistentStorage()
    {
        var token = LocalAuthService.NewSecret();
        await using var app = BackendApplication.Build(new(0, token, new HashSet<string>()),
            gameControls: services => GameControlContext.Create(services.GetRequiredService<ResourceLeaseCoordinator>(), TimeProvider.System, true));
        await app.StartAsync();
        try
        {
            var auth = app.Services.GetRequiredService<LocalAuthService>();
            var challenge = auth.CreateChallenge(new("viewer", null, [ApiPermissions.SystemRead]));
            var grant = auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
            var controls = app.Services.GetRequiredService<GameControlContext>();
            var binding = controls.Bindings.Current!.Session;
            controls.Leases.Acquire(new("color", "active-public-operation", binding.Id, binding.BindingRevision,
                ["foreground-input", "activity-control:" + binding.Id], TimeSpan.FromSeconds(30)));
            var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var owner = Client(uri, token);
            using var viewer = Client(uri, grant.Token);
            var first = await owner.GetFromJsonAsync<GameInputOwnershipSnapshot>(ApiContract.GameInputOwnership.Path, ContractJson.Options);
            var second = await viewer.GetFromJsonAsync<GameInputOwnershipSnapshot>(ApiContract.GameInputOwnership.Path, ContractJson.Options);
            Assert.Equal(JsonSerializer.Serialize(first! with { ObservedAt = second!.ObservedAt }, ContractJson.Options),
                JsonSerializer.Serialize(second, ContractJson.Options));
            Assert.Equal("active-public-operation", Assert.Single(second.Owners).OperationId);
            var request = new GameEmergencyStopRequest(first.BackendEpoch, DateTimeOffset.UtcNow.AddSeconds(30));
            using var denied = await Stop(viewer, request, "viewer-cannot-stop");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var accepted = await Stop(owner, request, "owner-public-stop");
            accepted.EnsureSuccessStatusCode();
            var receipt = await accepted.Content.ReadFromJsonAsync<GameEmergencyStopReceipt>(ContractJson.Options);
            Assert.Equal(1, receipt!.RevokedLeases);
            Assert.Equal(EffectStatus.Unknown, receipt.EffectStatus.KnownValue);
            Assert.Empty((await viewer.GetFromJsonAsync<GameInputOwnershipSnapshot>(ApiContract.GameInputOwnership.Path, ContractJson.Options))!.Owners);
        }
        finally { await app.StopAsync(); }
    }

    private static HttpClient Client(Uri uri, string token)
    {
        var client = new HttpClient { BaseAddress = uri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private static async Task<HttpResponseMessage> Stop(HttpClient client, GameEmergencyStopRequest request, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, ApiContract.GameEmergencyStop.Path)
        { Content = JsonContent.Create(request, options: ContractJson.Options) };
        message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message);
    }

    private sealed class Area : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public ResourceLeaseCoordinator Leases { get; }
        public Device Device { get; } = new();
        public GameWindowBinding Binding { get; }
        public GameControlContext Controls { get; }
        public SystemInfo Info { get; }
        public LocalAuthService Auth { get; }
        public GameControlService Service { get; }
        public Area()
        {
            var bindings = new GameSessionBindingTracker();
            Binding = bindings.Update(new("TW", 100, Clock.GetUtcNow(), new(500), "mock-build",
                new(0, 0, 800, 600), 96, 96, false));
            Device.ForegroundWindowIdentity = Binding.Session.WindowIdentity;
            Leases = new(Clock);
            Controls = new(bindings, Leases, new(Leases, bindings, Device, Clock), true);
            Info = new("MabiTool", "0.1.0", "1.0", "current-epoch", Clock.GetUtcNow(), ".NET", "test");
            Auth = new(new(0, LocalAuthService.NewSecret(), new HashSet<string>()), Info.InstanceId, Clock);
            Service = new(Controls, Info, Clock, Auth);
        }
        public ResourceLeaseSnapshot Lease(string owner, params string[] resources) => Leases.Acquire(new(owner,
            "operation-" + owner, Binding.Session.Id, Binding.Session.BindingRevision, resources, TimeSpan.FromSeconds(10)));
        public GameInputAction Action(ResourceLeaseSnapshot lease, InputControl control) => new(lease.OwnerId,
            lease.Id, lease.FencingToken, Binding.Session.Id, Binding.Session.BindingRevision, Binding.Session.WindowIdentity,
            InputActionKind.Press, control);
        public GameEmergencyStopRequest Request() => new(Info.InstanceId, Clock.GetUtcNow().AddSeconds(30));
        public void Dispose() { Leases.RevokeAll(); }
    }
    private sealed class Device : IGameInputDevice
    {
        public UInt64String? ForegroundWindowIdentity { get; set; }
        public List<InputControl> Pressed { get; } = [];
        public int ReleaseAllCalls { get; private set; }
        public bool FailRelease { get; set; }
        public void Press(InputControl control) => Pressed.Add(control);
        public void Release(InputControl control) { }
        public void MovePointer(PhysicalScreenPoint point) { }
        public void ReleaseAll() { ReleaseAllCalls++; if (FailRelease) throw new IOException("device release failure"); }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utc = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => utc;
        public void Advance(TimeSpan value) { timestamp += value.Ticks; utc += value; }
        public void ChangeUtc(TimeSpan value) => utc += value;
    }
}
