using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class LocalApiTests
{
    private const string Token = "test_only_0123456789012345678901234567890123456789";

    [Fact]
    public async Task ActualLoopbackHostAuthenticatesAllQueriesAndExplainsUnavailableFeatures()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            foreach (var path in new[] { "system/info", "modules", "capabilities", "health", "ready" })
            {
                using var denied = await client.GetAsync("/api/v1/" + path);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
                var problem = await denied.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("auth.required", problem.GetProperty("code").GetString());
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            var modules = await client.GetFromJsonAsync<ApiList<ModuleSnapshot>>("/api/v1/modules");
            Assert.Equal(12, modules!.Items.Count);
            // 环境信息由游戏数据服务提供，登记为已实现；未启用染色运行时的宿主上只有染色不可用。
            Assert.All(modules.Items, module => Assert.Equal(
                module.Id is "color" ? "unavailable" : "idle", module.State));
            var info = await client.GetFromJsonAsync<SystemInfo>("/api/v1/system/info");
            Assert.Equal("1.0", info!.ApiVersion);
            using var schema = await client.GetAsync("/api/v1/modules/color/settings-schema");
            Assert.Equal("application/schema+json", schema.Content.Headers.ContentType?.MediaType);
            using var document = JsonDocument.Parse(await schema.Content.ReadAsStringAsync());
            Assert.Equal("object", document.RootElement.GetProperty("type").GetString());
            var capabilities = await client.GetFromJsonAsync<ApiList<CapabilitySnapshot>>("/api/v1/capabilities");
            Assert.Single(capabilities!.Items, x => x.Status.KnownValue == CapabilityStatus.Available);
            Assert.All(capabilities.Items.Where(x => x.ModuleId == "inventory"), item =>
            {
                Assert.Equal(CapabilityStatus.Unavailable, item.Status.KnownValue);
                Assert.Equal("storage.unavailable", item.ReasonCode);
                Assert.Null(item.ProviderId);
            });
            Assert.Equal(3, capabilities.Items.Count(x => x.ModuleId == "inventory"));
            var inventory = capabilities.Items.Single(x => x.Id == "music.inventory");
            Assert.Equal(CapabilityStatus.Unavailable, inventory.Status.KnownValue);
            Assert.Equal("platform.unsupported", inventory.ReasonCode);
            Assert.Equal("tw-cli", inventory.ProviderId);
            Assert.NotEqual(default, inventory.CheckedAt);

            // 环境、附近、角色与交易所能力由游戏连接器状态生成：不支持连接器的宿主给出连接器的原因。
            var environment = capabilities.Items.Single(x => x.Id == "environment.observe");
            Assert.Equal(CapabilityStatus.Unavailable, environment.Status.KnownValue);
            Assert.Equal("platform.unsupported", environment.ReasonCode);
            Assert.Equal("tw-cli", environment.ProviderId);
            Assert.Equal("environment", environment.ModuleId);
            Assert.All(new[] { "nearby.observe", "character.info", "market.prices" }, id =>
                Assert.Equal((CapabilityStatus.Unavailable, "platform.unsupported"),
                    (capabilities.Items.Single(x => x.Id == id).Status.KnownValue!.Value, capabilities.Items.Single(x => x.Id == id).ReasonCode)));

            // 生活模块总是实现：采集、加工、制作随连接器状态，钓鱼随持久存储与识别组件；模拟宿主上它们都是不可用并写明原因。
            Assert.All(new[] { "life.gather", "life.altering", "life.crafting" }, id =>
                Assert.Equal((CapabilityStatus.Unavailable, "platform.unsupported", "life"),
                    (capabilities.Items.Single(x => x.Id == id).Status.KnownValue!.Value, capabilities.Items.Single(x => x.Id == id).ReasonCode, capabilities.Items.Single(x => x.Id == id).ModuleId)));
            var fishing = capabilities.Items.Single(x => x.Id == "life.fishing");
            Assert.Equal((CapabilityStatus.Unavailable, "storage.unavailable", "life"), (fishing.Status.KnownValue!.Value, fishing.ReasonCode, fishing.ModuleId));
            var playback = capabilities.Items.Single(x => x.Id == "performance.play");
            Assert.Equal(CapabilityStatus.Unavailable, playback.Status.KnownValue);
            Assert.Equal("platform.unsupported", playback.ReasonCode);
            Assert.Equal(CapabilityStatus.Unavailable, capabilities.Items.Single(x => x.Id == "game.connector").Status.KnownValue);
            Assert.Equal(CapabilityStatus.Unavailable, capabilities.Items.Single(x => x.Id == "performance.instrument").Status.KnownValue);
            using var missing = await client.GetAsync("/api/v1/unknown");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task RejectsRebindingOriginsAndUrlCredentialsAndSupportsExplicitBrowserOrigin()
    {
        await using var app = BackendApplication.Build(new(0, Token,
            new HashSet<string>(["http://localhost:5173"], StringComparer.Ordinal)));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var hostileHost = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
            hostileHost.Headers.Host = "attacker.example";
            using var hostResult = await client.SendAsync(hostileHost);
            Assert.Equal(HttpStatusCode.Forbidden, hostResult.StatusCode);
            foreach (var origin in new[] { "https://attacker.example", "null" })
            {
                using var hostileOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
                hostileOrigin.Headers.Add("Origin", origin);
                using var originResult = await client.SendAsync(hostileOrigin);
                Assert.Equal(HttpStatusCode.Forbidden, originResult.StatusCode);
            }
            using var query = await client.GetAsync("/api/v1/health?token=not-a-real-token");
            Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/health");
            preflight.Headers.Add("Origin", "http://localhost:5173");
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            using var preflightResult = await client.SendAsync(preflight);
            Assert.Equal(HttpStatusCode.NoContent, preflightResult.StatusCode);
            using var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
            allowed.Headers.Add("Origin", "http://localhost:5173");
            var auth = app.Services.GetRequiredService<LocalAuthService>();
            var challenge = auth.CreateChallenge(new("browser", "http://localhost:5173", [ApiPermissions.SystemRead]));
            var grant = auth.Redeem(new(challenge.ChallengeId, challenge.Code), "http://localhost:5173");
            allowed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.Token);
            using var allowedResult = await client.SendAsync(allowed);
            Assert.Equal(HttpStatusCode.OK, allowedResult.StatusCode);
            Assert.Equal("http://localhost:5173", allowedResult.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task ExternalKestrelConfigurationCannotAddPublicListeners()
    {
        const string key = "Kestrel__Endpoints__Public__Url";
        var original = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "http://0.0.0.0:0");
        try
        {
            await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
            await app.StartAsync();
            try
            {
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                Assert.Single(addresses);
                Assert.All(addresses, address => Assert.Equal("127.0.0.1", new Uri(address).Host));
            }
            finally { await app.StopAsync(); }
        }
        finally { Environment.SetEnvironmentVariable(key, original); }
    }

    [Fact]
    public async Task RoomLifecycleEndpointsAreAuthenticatedAndKeepCredentialsOutOfResponses()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var denied = await client.GetAsync("/api/v1/rooms/session");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            var empty = await client.GetFromJsonAsync<RoomLifecycleSnapshot>("/api/v1/rooms/session");
            Assert.Equal("disconnected", empty!.State);
            Assert.Null(empty.Room);

            var invitation = new RoomInvitation("ABC123", "wss://rooms.example", DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(invitation);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("secret-with-invalid-character!012345678901234567890123456789")]
    public void InvalidConfigurationIsRejectedBeforeListening(string token) =>
        Assert.Throws<ArgumentException>(() => BackendApplication.Build(new(0, token, new HashSet<string>())));
}
