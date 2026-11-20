using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MabiTool.Contracts;
using MabiTool.Host.Combat;
using MabiTool.Host.Game;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class ClientIconApiTests
{
    private const string Token = "client_icons_012345678901234567890123456789012345678901";

    [Fact]
    public async Task CatalogFollowsTheBindingAndContentCarriesItsVersion()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var catalog = await Catalog(http);
        Assert.Equal(ClientIconCatalogState.BindingRequired, catalog.State.KnownValue);
        Assert.Equal(ClientIconKeys.All, catalog.Icons.Select(icon => icon.Key));
        Assert.All(catalog.Icons, icon => Assert.Equal((false, ClientIconReasons.BindingRequired), (icon.Available, icon.Reason)));
        await AssertProblem(http, "critical", HttpStatusCode.Conflict, ClientIconReasons.BindingRequired);
        await AssertProblem(http, "unknown", HttpStatusCode.NotFound, ApiErrors.ResourceNotFound);

        var png = PngEncoder.Encode(new ClientIconBitmap(1, 1, [255, 255, 255, 128]));
        var version = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        var release = new ManualResetEventSlim();
        var reads = 0;
        var service = app.Services.GetRequiredService<ClientIconService>();
        service.Reader = (_, ct) =>
        {
            Interlocked.Increment(ref reads);
            release.Wait(ct);
            return new(new string('a', 64), [.. ClientAttributeIcons.Definitions.Select(definition => definition.Key == ClientIconKeys.Critical
                ? new ClientIconEntry(definition.Key, null, "#6CF08F", ClientIconSource.ParameterInfo, definition.FixedPath, 1, 1, png, version, "Critical", "暴擊")
                : new ClientIconEntry(definition.Key, ClientIconReasons.ResourceMissing, null, ClientIconSource.FixedPath, definition.FixedPath, null, null, null, null))], []);
        };
        app.Services.GetRequiredService<GameControlContext>().Bindings.Update(new GameWindowObservation("TW", 4242, DateTimeOffset.UtcNow.AddMinutes(-5),
            new(7), null, new PhysicalWindowRect(0, 0, 800, 600), 96, 96, false));
        Assert.Equal(ClientIconCatalogState.Loading, (await Catalog(http)).State.KnownValue);
        await AssertProblem(http, "critical", HttpStatusCode.ServiceUnavailable, ClientIconReasons.Loading);
        release.Set();
        for (var attempt = 0; attempt < 100 && catalog.State.KnownValue != ClientIconCatalogState.Ready; attempt++)
        {
            await Task.Delay(50);
            catalog = await Catalog(http);
        }
        Assert.Equal((ClientIconCatalogState.Ready, new string('a', 64)), (catalog.State.KnownValue, catalog.ClientId));
        var critical = catalog.Icons.Single(icon => icon.Key == ClientIconKeys.Critical);
        Assert.Equal((true, "#6CF08F", version, 1, ClientIconSource.ParameterInfo), (critical.Available, critical.Tint, critical.Version, critical.Width, critical.Source!.Value.KnownValue));
        Assert.Equal(("Critical", "暴擊"), (critical.NameKey, critical.DisplayName));
        Assert.Equal((false, ClientIconReasons.ResourceMissing), (catalog.Icons[^1].Available, catalog.Icons[^1].Reason));

        using var content = await http.GetAsync("/api/v1/client-icons/critical/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/png", content.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"" + version + "\"", content.Headers.ETag!.Tag);
        Assert.Equal(version, content.Headers.GetValues("X-Content-Sha256").Single());
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/client-icons/critical/content");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"" + version + "\""));
        using var unchanged = await http.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        await AssertProblem(http, "element.soul", (HttpStatusCode)422, ClientIconReasons.ResourceMissing);
        Assert.Equal(1, reads);
        await app.StopAsync();
    }

    private static async Task<ClientIconCatalog> Catalog(HttpClient http) =>
        (await http.GetFromJsonAsync<ClientIconCatalog>(ClientIconApi.Catalog.Path, ContractJson.Options))!;

    private static async Task AssertProblem(HttpClient http, string key, HttpStatusCode status, string code)
    {
        using var response = await http.GetAsync("/api/v1/client-icons/" + key + "/content");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
    }
}
