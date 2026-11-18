using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Contracts.Generation;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class ContractTests
{
    private const string Token = "fixture_only_0123456789012345678901234567890123456789";
    private static string Root => FindRoot();
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }

    [Fact]
    public async Task MaintenanceReturnsTypedRetryableErrorAndKeepsHealthReadable()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new(address) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
            var barrier = app.Services.GetRequiredService<ActivityBarrier>();
            using (var maintenance = barrier.TryEnterMaintenance("updater"))
            {
                Assert.NotNull(maintenance);
                using var response = await http.PutAsJsonAsync(ApiContract.SetRoomReadiness.Path,
                    new RoomReadinessRequest(true, false, true, true), ContractJson.Options);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options);
                Assert.Equal("update.maintenanceActive", problem!.Code);
                Assert.True(problem.Retryable);
                using var health = await http.GetAsync(ApiContract.Health.Path);
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            }
            using var after = await http.PutAsJsonAsync(ApiContract.SetRoomReadiness.Path,
                new RoomReadinessRequest(true, false, true, true), ContractJson.Options);
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void SharedFixtureRoundTripsWithoutLosingIdentifiersNullsOrEnumValues()
    {
        var text = File.ReadAllText(Path.Combine(Root, "contracts/fixtures/combat-summary.v1.json"));
        var envelope = JsonSerializer.Deserialize<EventEnvelope<CombatSummary>>(text, ContractJson.Options)!;
        Assert.Equal(ulong.MaxValue, envelope.ResourceRevision.Value);
        Assert.Equal(9007199254740993UL, envelope.Data.LocalEntity.RawInstanceId.Value);
        Assert.Equal(ObservationQuality.Partial, envelope.Data.Quality.KnownValue);
        Assert.Null(envelope.Data.EncounterDps);
        var roundtrip = JsonSerializer.Serialize(envelope, ContractJson.Options);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(roundtrip)));
        var output = Path.Combine(Root, "artifacts/generated/contracts/fixtures");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "csharp-combat-summary.v1.json"), roundtrip);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9007199254740993")]
    [InlineData("18446744073709551615")]
    public void UInt64UsesCanonicalDecimalStrings(string value)
    {
        var parsed = UInt64String.Parse(value);
        Assert.Equal("\"" + value + "\"", JsonSerializer.Serialize(parsed, ContractJson.Options));
        Assert.Equal(parsed, JsonSerializer.Deserialize<UInt64String>("\"" + value + "\"", ContractJson.Options));
    }

    [Theory]
    [InlineData("\"18446744073709551616\"")]
    [InlineData("9007199254740993")]
    [InlineData("\"01\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"1\\n\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void InvalidUInt64WireValuesAreRejected(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UInt64String>(json, ContractJson.Options));

    [Fact]
    public void RequiredFieldsDuplicatePropertiesAndInvalidEnumsAreRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HealthInfo>("{}", ContractJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HealthInfo>(
            "{\"status\":\"healthy\",\"status\":\"ready\",\"instanceId\":\"x\"}", ContractJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HealthInfo>(
            "{\"status\":null,\"instanceId\":\"x\"}", ContractJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservationQuality>("1", ContractJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservationQuality>("\"invalid\"", ContractJson.Options));
    }

    [Fact]
    public void ExtensibleResponseEnumsPreserveFutureValuesAndRejectIntegerValues()
    {
        var text = File.ReadAllText(Path.Combine(Root, "contracts/fixtures/combat-summary.v1.json"));
        var future = text.Replace("\"partial\"", "\"future-quality\"", StringComparison.Ordinal);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<CombatSummary>>(future, ContractJson.Options)!;
        Assert.False(envelope.Data.Quality.IsKnown);
        Assert.Null(envelope.Data.Quality.KnownValue);
        Assert.Equal("future-quality", envelope.Data.Quality.RawValue);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(future), JsonNode.Parse(JsonSerializer.Serialize(envelope, ContractJson.Options))));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EventEnvelope<CombatSummary>>(
            text.Replace("\"partial\"", "1", StringComparison.Ordinal), ContractJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EventEnvelope<CombatSummary>>(
            text.Replace("\"data\": {", "\"data\": null, \"ignored\": {", StringComparison.Ordinal), ContractJson.Options));
    }

    [Fact]
    public void GeneratedContractsAreDeterministicAndMatchTheCanonicalDefinitions()
    {
        static string Normalize(string value) => value.Replace("\r\n", "\n").TrimEnd();
        Assert.Equal(OpenApiDocument.Create().ToJsonString(), OpenApiDocument.Create().ToJsonString());
        foreach (var (relative, generated) in new[] {
            ("sdk/typescript/client.ts", ClientGenerator.TypeScript()),
            ("sdk/csharp/MabiToolClient.g.cs", ClientGenerator.CSharp()) })
            Assert.Equal(Normalize(generated), Normalize(File.ReadAllText(Path.Combine(Root, "artifacts/generated", relative))));
        Assert.True(JsonNode.DeepEquals(OpenApiDocument.Create(), JsonNode.Parse(File.ReadAllText(
            Path.Combine(Root, "artifacts/generated/contracts/openapi/v1.json")))));
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void ArraysOfCustomConverterScalarsDeclareTheSameElementSchemaAsSingleFields()
    {
        var document = OpenApiDocument.Create(); var schemas = document["components"]!["schemas"]!;
        static JsonNode Property(JsonNode schemas, string schema, string name) => schemas[schema]!["properties"]![name]!;
        var element = Property(schemas, "CombatDamageEvent", "elements")["items"]!.AsObject();
        var single = Property(schemas, "CombatDamageEvent", "classification").AsObject();
        Assert.Equal("string", element["type"]!.GetValue<string>());
        foreach (var key in new[] { "minLength", "maxLength", "pattern" })
            Assert.Equal(single[key]!.ToJsonString(), element[key]!.ToJsonString());
        Assert.Equal(["bleed", "dark", "fire", "holy", "ice", "lightning", "poison", "soul"],
            element["x-known-values"]!.AsArray().Select(value => value!.GetValue<string>()));
        var behaviors = Property(schemas, "ModuleRisk", "highRiskBehaviors")["items"]!.AsObject();
        Assert.Equal("string", behaviors["type"]!.GetValue<string>());
        Assert.NotEmpty(behaviors["x-known-values"]!.AsArray());
        var identifiers = Property(schemas, "ObservedConcert", "orderedCharacterIds")["items"]!.AsObject();
        Assert.Equal(20, identifiers["maxLength"]!.GetValue<int>()); Assert.Equal("string", identifiers["type"]!.GetValue<string>());
        var missing = new List<string>();
        void Walk(JsonNode? node, string path)
        {
            if (node is JsonObject value)
            {
                var type = value["type"];
                if (type is JsonValue { } single && single.GetValue<string>() == "array" ||
                    type is JsonArray many && many.Any(item => item?.GetValue<string>() == "array"))
                    if (!value.ContainsKey("items")) missing.Add(path);
                foreach (var (name, child) in value) Walk(child, path + "/" + name);
            }
            else if (node is JsonArray list) for (var index = 0; index < list.Count; index++) Walk(list[index], path + "/" + index);
        }
        Walk(document, "");
        Assert.Empty(missing);
    }

    [Fact]
    public async Task GeneratedClientQueriesActualHostAndPreservesStructuredErrors()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var uri = new Uri(address);
            using var sdk = new MabiToolClient(uri, _ => ValueTask.FromResult(Token));
            Assert.Equal("Moongate", (await sdk.GetSystemInfoAsync()).Product);
            Assert.Equal("Moongate", typeof(BackendApplication).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyProductAttribute), false)
                .Cast<System.Reflection.AssemblyProductAttribute>().Single().Product);
            Assert.Equal(11, (await sdk.GetModulesAsync()).Items.Count);
            Assert.Contains((await sdk.GetCapabilitiesAsync()).Items, x => x.Id == "system.inspect");
            Assert.Equal("healthy", (await sdk.GetHealthAsync()).Status);
            Assert.Equal("ready", (await sdk.GetReadyAsync()).Status);
            Assert.Equal("object", (await sdk.GetModuleSettingsSchemaAsync("core")).GetProperty("type").GetString());
            var documents = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sdk.GetOpenApiAsync()));
            var document = documents[0];
            var canonicalDocument = OpenApiDocument.Create();
            foreach (var value in documents)
                Assert.True(JsonNode.DeepEquals(canonicalDocument, JsonNode.Parse(value.GetRawText())));
            Assert.Equal("3.1.0", document.GetProperty("openapi").GetString());
            Assert.Equal("Moongate Local API", document.GetProperty("info").GetProperty("title").GetString());
            var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
                .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is not null).ToArray();
            Assert.Equal(ApiContract.Endpoints.Select(e => e.Method + " " + e.Path).Order(),
                routes.SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(m => m + " " + e.RoutePattern.RawText)).Order());
            foreach (var route in routes)
            {
                var definition = Assert.IsType<EndpointDefinition>(route.Metadata.GetMetadata<EndpointDefinition>());
                Assert.Contains(definition, ApiContract.Endpoints);
                Assert.Equal(definition.Path, route.RoutePattern.RawText);
                Assert.Equal([definition.Method], route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            }
            Assert.Equal(ApiContract.Endpoints.Select(e => e.Path).Distinct().Order(), document.GetProperty("paths").EnumerateObject().Select(x => x.Name).Order());
            var missing = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.GetModuleSettingsSchemaAsync("missing"));
            Assert.Equal(404, missing.Status);
            Assert.Equal(ApiErrors.ModuleNotFound, missing.Problem!.Code);
            using var deniedSdk = new MabiToolClient(uri, _ => ValueTask.FromResult("invalid"));
            var denied = await Assert.ThrowsAsync<MabiToolApiException>(() => deniedSdk.GetHealthAsync());
            Assert.Equal(ApiErrors.AuthRequired, denied.Problem!.Code);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sdk.GetHealthAsync(cancelled.Token));
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("http://example.com:19998/")]
    [InlineData("https://localhost:19998/")]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost:19998/path")]
    [InlineData("http://secret@localhost:19998/")]
    [InlineData("http://localhost:19998/?token=secret")]
    public void SdkRejectsNonLocalOrCredentialBearingEndpoints(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new MabiToolClient(new Uri(endpoint), _ => ValueTask.FromResult(Token)));

    /// <summary>本机接口的默认处理器不走系统代理与 HTTP_PROXY，系统设置了代理时本机请求仍直达 127.0.0.1。</summary>
    [Fact]
    public void SdkDefaultHandlerBypassesProxies()
    {
        using var handler = MabiToolClient.CreateLoopbackHandler();
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData("../core")]
    [InlineData("core\n")]
    [InlineData("")]
    public void SdkRejectsInvalidPathIdentifiersBeforeSending(string id)
    {
        using var sdk = new MabiToolClient(new Uri("http://127.0.0.1:19998/"), _ => ValueTask.FromResult(Token));
        Assert.Throws<ArgumentException>(() => { _ = sdk.GetModuleSettingsSchemaAsync(id); });
    }

    [Fact]
    public async Task SdkLimitsResponsesAndHandlesMalformedProblemsAndRedirects()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Redirect })
        {
            using var sdk = new MabiToolClient(new Uri("http://127.0.0.1:19998/"), _ => ValueTask.FromResult(Token),
                new ResponseHandler(() => new HttpResponseMessage(status) { Content = new StringContent("{bad", System.Text.Encoding.UTF8, "application/problem+json") }));
            var error = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.GetHealthAsync());
            Assert.Equal((int)status, error.Status);
            Assert.Null(error.Problem);
        }
        using var large = new MabiToolClient(new Uri("http://127.0.0.1:19998/"), _ => ValueTask.FromResult(Token),
            new ResponseHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => large.GetHealthAsync());
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
}
