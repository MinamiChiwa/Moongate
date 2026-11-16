using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Host.Shortcuts;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class FrontendGameControlSdkTests
{
    [Fact]
    public async Task FrontendShortcutUsesRegisteredChordAndDeliversOneOnlineSdkNotification()
    {
        var root = Path.Combine(Path.GetTempPath(), "mabitool-frontend-shortcut-" + Guid.NewGuid().ToString("N"));
        var token = LocalAuthService.NewSecret();
        await using var shortcuts = new ShortcutSource();
        try
        {
            await using var app = BackendApplication.Build(new(0, token, new HashSet<string>()),
                storage: new(Path.Combine(root, "bootstrap"), true, PortableRoot: root), mock: true, shortcutSource: shortcuts);
            await app.StartAsync();
            try
            {
                var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var owner = new MabiToolClient(uri, _ => ValueTask.FromResult(token));
                var initial = await owner.GetShortcutSettingsAsync();
                var configured = await owner.ChangeShortcutSettingsAsync(new([
                    new("overlay-toggle", new("F8", false, false, false, false)),
                    new("overlay-click-through-toggle", new("F7", false, false, false, false))]),
                    "sdk-frontend-shortcuts", ShortcutSettingsStore.ETag(initial.Revision));
                Assert.Equal(3, configured.Registrations.Count);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using (var stream = owner.StreamEventsAsync(new(["shortcut.triggered"], [], null), timeout.Token).GetAsyncEnumerator())
                {
                    Assert.True(await stream.MoveNextAsync()); Assert.Equal("snapshot", stream.Current.Event);
                    shortcuts.Emit("unknown-action");
                    shortcuts.Emit("overlay-toggle");
                    Assert.True(await stream.MoveNextAsync());
                    Assert.Equal("shortcut.triggered", stream.Current.Event);
                    var envelope = stream.Current.Data.Deserialize<EventEnvelope<ShortcutTriggered>>(ContractJson.Options)!;
                    Assert.Equal("presentation", envelope.Module);
                    Assert.Equal("overlay-toggle", envelope.Data.ActionId);
                    Assert.Equal("F8", envelope.Data.Chord.Key);
                    Assert.Equal(TimeSpan.FromSeconds(2), envelope.Data.ExpiresAt - envelope.Data.TriggeredAt);
                    shortcuts.Emit("overlay-click-through-toggle");
                    Assert.True(await stream.MoveNextAsync());
                    Assert.Equal("F7", stream.Current.Data.Deserialize<EventEnvelope<ShortcutTriggered>>(ContractJson.Options)!.Data.Chord.Key);
                }
                var hub = app.Services.GetRequiredService<StateEventHub>();
                var session = app.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session;
                await using var after = await hub.SubscribeAsync(session, EventFilter.Create(["shortcut.triggered"]));
                await app.Services.GetRequiredService<ShortcutService>().StopAsync(default);
                shortcuts.Emit("overlay-toggle");
                Assert.False(after.TryRead(out _));
                Assert.All(shortcuts.GetRegistrations(), registration => Assert.Null(registration.Registered));
            }
            finally { await app.StopAsync(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GeneratedSdkConnectsControlsAndCustomKeysWhileTheHotkeyUsesTheSameStopPath()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        var root = Path.Combine(repository!.FullName, "artifacts", "tmp", "frontend-controls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var shortcuts = new ShortcutSource();
            var token = LocalAuthService.NewSecret();
            await using var app = BackendApplication.Build(new(0, token, new HashSet<string>()),
                storage: new(Path.Combine(root, "bootstrap"), true, PortableRoot: root), mock: true, shortcutSource: shortcuts);
            await app.StartAsync();
            try
            {
                var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var owner = new MabiToolClient(uri, _ => ValueTask.FromResult(token));
                var auth = app.Services.GetRequiredService<LocalAuthService>();
                var challenge = auth.CreateChallenge(new("viewer", null, [ApiPermissions.SystemRead]));
                var grant = auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
                using var viewer = new MabiToolClient(uri, _ => ValueTask.FromResult(grant.Token));

                ApiResponseMetadata? initialKeysMetadata = null;
                var initialKeys = await owner.GetShortcutSettingsAsync(onResponse: metadata => initialKeysMetadata = metadata);
                Assert.NotNull(initialKeysMetadata?.ETag);
                Assert.Equal("F9", Assert.Single(initialKeys.Bindings).Chord.Key);
                var keysRequest = new ShortcutSettingsRequest([new("emergency-stop", new("K", true, false, false, false))]);
                ApiResponseMetadata? changedKeysMetadata = null;
                var changedKeys = await owner.ChangeShortcutSettingsAsync(keysRequest, "sdk-custom-shortcut", initialKeysMetadata!.ETag!,
                    onResponse: metadata => changedKeysMetadata = metadata);
                Assert.NotEqual(initialKeysMetadata.ETag, changedKeysMetadata!.ETag);
                Assert.Equal(new("K", true, false, false, false), Assert.Single(changedKeys.Registrations).Registered);
                Assert.Equal(new(ShortcutKey.K, Control: true), Assert.Single(shortcuts.GetRegistrations()).Registered);
                var deniedKeys = await Assert.ThrowsAsync<MabiToolApiException>(() =>
                    viewer.ChangeShortcutSettingsAsync(keysRequest, "viewer-custom-shortcut", changedKeysMetadata.ETag!));
                Assert.Equal(403, deniedKeys.Status);
                Assert.Equal("permission.denied", deniedKeys.Problem!.Code);
                var staleKeys = await Assert.ThrowsAsync<MabiToolApiException>(() =>
                    owner.ChangeShortcutSettingsAsync(keysRequest, "sdk-stale-shortcut", initialKeysMetadata.ETag!));
                Assert.Equal(412, staleKeys.Status);
                Assert.Equal("revision.stale", staleKeys.Problem!.Code);
                Assert.Equal(changedKeys.Revision, (await viewer.GetShortcutSettingsAsync()).Revision);

                ApiResponseMetadata? initialPreferencesMetadata = null;
                var preferences = await owner.GetRoomPreferencesAsync(onResponse: metadata => initialPreferencesMetadata = metadata);
                var modes = preferences.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                modes["ensemble"] = new(new(LocalBindingKind.Key, Key: 0x4C, Control: true),
                    new(LocalBindingKind.Key, Key: 0x4D, Alt: true), false);
                var preferencesRequest = preferences with { Bindings = modes };
                ApiResponseMetadata? savedPreferencesMetadata = null;
                var savedPreferences = await owner.EditRoomPreferencesAsync(preferencesRequest, "sdk-custom-room-keys", initialPreferencesMetadata!.ETag!,
                    onResponse: metadata => savedPreferencesMetadata = metadata);
                ApiResponseMetadata? readPreferencesMetadata = null;
                var readPreferences = await owner.GetRoomPreferencesAsync(onResponse: metadata => readPreferencesMetadata = metadata);
                Assert.Equal(savedPreferencesMetadata!.ETag, readPreferencesMetadata!.ETag);
                Assert.NotEqual(initialPreferencesMetadata.ETag, readPreferencesMetadata.ETag);
                Assert.Equal(savedPreferences.Revision, readPreferences.Revision);
                Assert.Equal(modes["ensemble"], readPreferences.Bindings["ensemble"]);
                var deniedPreferences = await Assert.ThrowsAsync<MabiToolApiException>(() =>
                    viewer.EditRoomPreferencesAsync(preferencesRequest, "viewer-custom-room-keys", initialPreferencesMetadata.ETag!));
                Assert.Equal(403, deniedPreferences.Status);
                Assert.Equal("permission.denied", deniedPreferences.Problem!.Code);

                var controls = app.Services.GetRequiredService<GameControlContext>();
                var binding = controls.Bindings.Current!.Session;
                controls.Leases.Acquire(new("color", "sdk-active-operation", binding.Id, binding.BindingRevision,
                    ["foreground-input", "activity-control:" + binding.Id], TimeSpan.FromMinutes(1)));
                var firstOwnership = await owner.GetGameInputOwnershipAsync();
                var sharedOwnership = await viewer.GetGameInputOwnershipAsync();
                Assert.Equal(firstOwnership.BackendEpoch, sharedOwnership.BackendEpoch);
                Assert.Equal(JsonSerializer.Serialize(firstOwnership.Owners, ContractJson.Options),
                    JsonSerializer.Serialize(sharedOwnership.Owners, ContractJson.Options));
                Assert.Equal("sdk-active-operation", Assert.Single(firstOwnership.Owners).OperationId);
                var stopRequest = new GameEmergencyStopRequest(firstOwnership.BackendEpoch, DateTimeOffset.UtcNow.AddSeconds(30));
                var deniedStop = await Assert.ThrowsAsync<MabiToolApiException>(() =>
                    viewer.EmergencyStopGameAsync(stopRequest, "viewer-emergency-stop"));
                Assert.Equal(403, deniedStop.Status);
                Assert.Equal("permission.denied", deniedStop.Problem!.Code);
                Assert.Single((await owner.GetGameInputOwnershipAsync()).Owners);
                var stopped = await owner.EmergencyStopGameAsync(stopRequest, "sdk-emergency-stop");
                Assert.Equal("released", stopped.InputReleaseState);
                Assert.Equal(1, stopped.RevokedLeases);
                Assert.Equal(EffectStatus.Unknown, stopped.EffectStatus.KnownValue);
                Assert.Empty((await owner.GetGameInputOwnershipAsync()).Owners);

                var next = controls.Leases.Acquire(new("rooms", "sdk-new-operation", binding.Id, binding.BindingRevision,
                    ["foreground-input", "activity-control:" + binding.Id], TimeSpan.FromMinutes(1)));
                var repeated = await owner.EmergencyStopGameAsync(stopRequest, "sdk-emergency-stop");
                Assert.Equal(JsonSerializer.Serialize(stopped, ContractJson.Options), JsonSerializer.Serialize(repeated, ContractJson.Options));
                Assert.True(controls.Leases.Validate(next.Id, "foreground-input", next.FencingToken, next.GameSessionId, next.BindingRevision));
                Assert.Equal("sdk-new-operation", Assert.Single((await viewer.GetGameInputOwnershipAsync()).Owners).OperationId);

                var activity = controls.Leases.Acquire(new("cli", "sdk-hotkey-activity", "other-game", binding.BindingRevision,
                    ["activity-control:other-game"], TimeSpan.FromMinutes(1)));
                shortcuts.Emit("emergency-stop");
                Assert.Empty((await owner.GetGameInputOwnershipAsync()).Owners);
                Assert.False(controls.Leases.Validate(activity.Id, "activity-control:other-game", activity.FencingToken,
                    activity.GameSessionId, activity.BindingRevision));
            }
            finally { await app.StopAsync(); }
            Assert.Null(Assert.Single(shortcuts.GetRegistrations()).Registered);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ShortcutSource : IGlobalShortcutSource
    {
        private readonly GlobalShortcutRegistry registry = new(new Registrar());
        private bool stopped;
        public bool CanReconfigure => true;
        public event Action<string>? ActionReceived;
        public void Emit(string action) => ActionReceived?.Invoke(action);
        public Task StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { stopped = true; return Task.CompletedTask; }
        public IReadOnlyList<ShortcutRegistrationResult> GetRegistrations() => registry.GetRegistrations().Select(value =>
            stopped ? value with { Registered = null, Status = ShortcutRegistrationStatus.Unavailable, FailureCode = "shortcut.stopped" } : value).ToArray();
        public Task<IReadOnlyList<ShortcutRegistrationResult>> ReconfigureAsync(IReadOnlyList<ShortcutRequest> requests, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(registry.Configure(requests));
        }
        public ValueTask DisposeAsync() { registry.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Registrar : IGlobalShortcutRegistrar
    {
        public bool TryRegister(int id, ShortcutChord chord, out string? failure) { failure = null; return true; }
        public void Unregister(int id) { }
    }
}
