using System.Runtime.Versioning;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Providers.TwObservation.Combat;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
[Collection("Persistent backend")]
public sealed class NativeCurrentHostTests
{
    [Fact]
    public async Task CurrentProfilePassesThroughAuthenticatedWorkerToDefaultHostSource()
    {
        using var area = new GameObservationApiTests.Area(); SetBuild(area, Tw20261110CombatProtocol.GameBuild);
        await using var app = area.Build("current-combat"); await app.StartAsync(); using var client = GameObservationApiTests.Area.Owner(app);
        using var snapshot = await client.GetAsync(ApiContract.GameObservation.Path);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiContract.StartGameObservation.Path)
        { Content = JsonContent.Create(new GameObservationStartRequest(new string('1', 32), area.Bindings.Current!.Session.BindingRevision,
            Tw20261110CombatProtocol.CaptureProfile), options: ContractJson.Options) };
        request.Headers.Add("Idempotency-Key", "current-capture"); request.Headers.Add("If-Match", snapshot.Headers.ETag!.ToString());
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        var capture = (await response.Content.ReadFromJsonAsync<GameObservationSubscription>(ContractJson.Options))!;
        var source = app.Services.GetRequiredService<ICombatObservationSource>();
        var owner = app.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session.ClientId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var subscription = await source.OpenAsync(owner, "current-observation", new(area.Bindings.Current!.Session.Id), timeout.Token);
        Assert.Equal(0xabcdef0100000457UL, subscription.LocalEntity.Key.RawInstanceId.Value);
        List<CombatFrame> frames = [];
        await foreach (var frame in subscription.ReadAsync(timeout.Token))
        {
            frames.Add(frame);
            if (frames.SelectMany(value => value.Damage).Any() && frames.SelectMany(value => value.Effects).Any()) break;
        }
        Assert.Equal(4294967395UL, Assert.Single(frames.SelectMany(value => value.Damage)).Amount.Value);
        var effect = Assert.Single(frames.SelectMany(value => value.Effects));
        Assert.Equal(EffectDurationKind.Finite, effect.DurationKind.KnownValue); Assert.Equal(5000d, effect.DurationMs);
        Assert.Empty(frames.SelectMany(value => value.Gaps));
        await subscription.CloseAsync(); await GameObservationApiTests.EndCapture(client, capture.Id);
        Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(area.Root, "handles")));
        Assert.Null(app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId);
        await app.StopAsync();
    }

    [Fact]
    public async Task DefaultCompositionRequiresVerifiedBuildAndInstalledWorkerWithoutStartingCapture()
    {
        using var area = new GameObservationApiTests.Area(); await using var app = area.Build();
        Assert.NotEmpty(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
        var source = app.Services.GetRequiredService<ICombatObservationSource>();
        Assert.All(source.Capabilities(area.Bindings.Current!.Session.Id).Capabilities, capability =>
            Assert.Equal(CapabilityStatus.Unavailable, capability.Status.KnownValue));
        var current = area.Bindings.Current!;
        area.Bindings.Update(new("TW", current.Session.ProcessId, current.Session.ProcessStartTime, current.Session.WindowIdentity,
            Tw20261110CombatProtocol.GameBuild, current.Bounds, current.DpiX, current.DpiY, false));
        Assert.All(source.Capabilities(area.Bindings.Current!.Session.Id).Capabilities, capability =>
            Assert.Equal(CapabilityStatus.Available, capability.Status.KnownValue));
        Assert.Null(app.Services.GetRequiredService<GameObservationService>().Runtime().WorkerProcessId);
    }
    private static void SetBuild(GameObservationApiTests.Area area, string build)
    {
        var current = area.Bindings.Current!;
        area.Bindings.Update(new("TW", current.Session.ProcessId, current.Session.ProcessStartTime, current.Session.WindowIdentity,
            build, current.Bounds, current.DpiX, current.DpiY, false));
    }
}
