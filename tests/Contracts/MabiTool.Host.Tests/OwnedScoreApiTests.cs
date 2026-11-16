// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Inventory.Adapters;
using MabiTool.Modules.MusicLibrary.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class OwnedScoreApiTests
{
    private const string Token = "owned_scores_012345678901234567890123456789012345678901";

    [Fact]
    public async Task ReadOnlyClientsShareSameNamedInstancesAndDoNotStartWorkers()
    {
        using var area = new Workspace(); await using var app = Build(area); await app.StartAsync();
        using var anonymous = Http(app, null);
        using var denied = await anonymous.PostAsJsonAsync(ApiContract.QueryOwnedMusicScores.Path, new MusicOwnedScoreQuery(), ContractJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await Seed(app);
        using var sdk = new MabiToolClient(Address(app), _ => ValueTask.FromResult(Token));
        var challenge = await sdk.CreatePairingChallengeAsync(new("score-reader", null, [ApiPermissions.SystemRead]));
        var grant = await sdk.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        using var reader = new MabiToolClient(Address(app), _ => ValueTask.FromResult(grant.Token));
        var page = await sdk.QueryOwnedMusicScoresAsync(new()); var other = await reader.QueryOwnedMusicScoresAsync(new());
        Assert.Equal(JsonSerializer.Serialize(page, ContractJson.Options), JsonSerializer.Serialize(other, ContractJson.Options));
        Assert.True(page.Current); Assert.True(page.Complete); Assert.True(page.Simulation); Assert.Equal(3, page.Items.Count);
        Assert.Equal(3, page.Items.Select(i => i.InstanceId).Distinct().Count());
        Assert.All(page.Items, row => { Assert.Equal("<color=#fff>原始　同名谱</color>", row.RawTitle);
            Assert.Equal(long.MinValue.ToString(), row.AcquiredTimeRaw); Assert.True(row.Current); Assert.True(row.MetadataComplete); });
        Assert.Equal(ulong.MaxValue.ToString(), page.Identity!.CharacterId);
        Assert.Equal(0, (await sdk.GetGameObservationRuntimeAsync()).SubscriberCount);
        Assert.Null((await sdk.GetGameObservationRuntimeAsync()).WorkerProcessId);
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("inventory"));
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("music-library"));
        await app.StopAsync();
    }

    [Fact]
    public async Task PaginationInvalidationKeepsHistoryAndBadRequestsReleaseModules()
    {
        using var area = new Workspace(); await using var app = Build(area); await app.StartAsync();
        using var http = Http(app); await Seed(app);
        var first = await Query(http, new(PageSize: 1)); Assert.NotNull(first.NextCursor);
        var next = await Query(http, new(PageSize: 1, Cursor: first.NextCursor));
        Assert.NotEqual(first.Items[0].InstanceId, next.Items[0].InstanceId);
        using var invalid = await http.PostAsJsonAsync(ApiContract.QueryOwnedMusicScores.Path, new MusicOwnedScoreQuery(PageSize: 1001), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("request.invalid", (await invalid.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        await app.Services.GetRequiredService<InventoryHistoryStore>().InvalidateAsync("inventory.sequenceGap");
        using var stale = await http.PostAsJsonAsync(ApiContract.QueryOwnedMusicScores.Path, new MusicOwnedScoreQuery(PageSize: 1, Cursor: first.NextCursor), ContractJson.Options);
        Assert.Equal("inventory.planStale", (await stale.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        var history = await Query(http, new()); Assert.False(history.Current); Assert.False(history.Complete); Assert.Equal(3, history.Items.Count);
        Assert.Contains("inventory.sequenceGap", history.Reasons);
        var filtered = await Query(http, new(CurrentOnly: true)); Assert.Empty(filtered.Items); Assert.False(filtered.Complete);
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("inventory"));
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("music-library"));
        await app.StopAsync();
    }

    [Fact]
    public async Task RestartedHostReturnsLastMetadataAsHistorical()
    {
        using var area = new Workspace();
        await using (var app = Build(area)) { await app.StartAsync(); await Seed(app); await app.StopAsync(); }
        await using var restarted = Build(area); await restarted.StartAsync(); using var http = Http(restarted);
        var page = await Query(http, new()); Assert.Equal(3, page.Items.Count); Assert.False(page.Current); Assert.False(page.Complete);
        Assert.All(page.Items, row => Assert.Equal("<color=#fff>原始　同名谱</color>", row.RawTitle));
        Assert.Null((await Query(http, new(CurrentOnly: true))).NextCursor);
        Assert.Empty((await Query(http, new(CurrentOnly: true))).Items);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task StorageUnavailableIsAnExplicitIncompleteEmptyRead()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>())); await app.StartAsync(); using var http = Http(app);
        var page = await Query(http, new()); Assert.False(page.Current); Assert.False(page.Complete); Assert.Empty(page.Items);
        Assert.Contains("storage.unavailable", page.Reasons); Assert.Equal(3, page.Coverage.Count); Assert.All(page.Coverage, c => Assert.False(c.Complete));
        await app.StopAsync();
    }

    [Fact]
    public async Task CancelledHttpReadReleasesBothModuleLeases()
    {
        using var area = new Workspace(); await using var app = Build(area);
        var readModel = new BlockedReadModel(); var provider = new OwnedScoreProvider(readModel, "cancel-test", true);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 与公开端点相同的租约及 Provider 边界，通过测试路由注入阻塞公开读端口。
        app.MapPost("/tests/owned-score-cancel", async (HttpContext context, ModuleRuntime runtime) => {
            try
            {
                await using var demand = await runtime.AcquireManyAsync(["music-library", "inventory"], "cancelled-score-query", context.RequestAborted);
                return Results.Ok(await provider.QueryAsync(new(), () => app.Services.GetRequiredService<GameControlContext>().Bindings.Current?.Session, context.RequestAborted));
            }
            finally { finished.TrySetResult(); }
        }).WithMetadata(ApiContract.QueryOwnedMusicScores);
        await app.StartAsync(); using var client = Http(app); using var cancellation = new CancellationTokenSource();
        var request = client.PostAsJsonAsync("/tests/owned-score-cancel", new MusicOwnedScoreQuery(), ContractJson.Options, cancellation.Token);
        await readModel.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var runtime = app.Services.GetRequiredService<ModuleRuntime>();
        Assert.Contains("cancelled-score-query", runtime.ActiveOperations("inventory"));
        Assert.Contains("cancelled-score-query", runtime.ActiveOperations("music-library"));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(runtime.ActiveOperations("inventory")); Assert.Empty(runtime.ActiveOperations("music-library"));
        await app.StopAsync();
    }

    [Fact]
    public void SharedContractFixtureRoundTripsUnknownEnumsAndLosslessNumbers()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        var path = Path.Combine(repository?.FullName ?? throw new InvalidOperationException("找不到测试解决方案"),
            "contracts", "fixtures", "music-owned-score-page.v1.json");
        var json = File.ReadAllText(path); var page = JsonSerializer.Deserialize<MusicOwnedScorePage>(json, ContractJson.Options)!;
        var roundtrip = JsonSerializer.Deserialize<MusicOwnedScorePage>(JsonSerializer.Serialize(page, ContractJson.Options), ContractJson.Options)!;
        Assert.Equal(JsonSerializer.Serialize(page, ContractJson.Options), JsonSerializer.Serialize(roundtrip, ContractJson.Options));
        Assert.Equal(2, page.Items.Count); Assert.Equal(page.Items[0].RawTitle, page.Items[1].RawTitle);
        Assert.NotEqual(page.Items[0].InstanceId, page.Items[1].InstanceId);
        Assert.Contains(page.Items, item => item.InstanceId?.Value == ulong.MaxValue);
        Assert.Contains(page.Items, item => item.AcquiredTimeRaw == long.MinValue.ToString());
        var items = page.Items.ToArray(); items[0] = items[0] with {
            Container = new OpenEnum<InventoryContainer>("future-container"), Quality = new OpenEnum<ObservationQuality>("future-quality") };
        var future = page with { Quality = new OpenEnum<ObservationQuality>("future-quality"), Items = items };
        var restored = JsonSerializer.Deserialize<MusicOwnedScorePage>(JsonSerializer.Serialize(future, ContractJson.Options), ContractJson.Options)!;
        Assert.Equal("future-quality", restored.Quality.RawValue); Assert.Equal("future-quality", restored.Items[0].Quality.RawValue);
        Assert.Equal("future-container", restored.Items[0].Container.RawValue); Assert.Null(restored.Items[1].Quantity);
        Assert.Null(restored.Items[1].Composer);
        Assert.True(JsonElement.DeepEquals(page.Items[1].Attributes, restored.Items[1].Attributes));
    }

    private sealed class BlockedReadModel : IInventoryReadModel
    {
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            Ready.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(new(0), null, new(new(0), null));
        }
    }

    private static async Task<MusicOwnedScorePage> Query(HttpClient client, MusicOwnedScoreQuery request)
    {
        using var response = await client.PostAsJsonAsync(ApiContract.QueryOwnedMusicScores.Path, request, ContractJson.Options);
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<MusicOwnedScorePage>(ContractJson.Options))!;
    }
    private static async Task Seed(WebApplication app)
    {
        var store = app.Services.GetRequiredService<InventoryHistoryStore>();
        var binding = app.Services.GetRequiredService<GameControlContext>().Bindings.Current!.Session;
        var items = Enum.GetValues<InventoryContainer>().Select((container, index) => {
            var id = ulong.MaxValue - (ulong)index;
            return new InventoryItem("MusicNoteItem", container, "score-" + index, new(7), new(id), new(1),
                JsonSerializer.SerializeToElement(new { data = new { Id = new { rawValue = id.ToString() }, SheetId = new { Id = 7 },
                    Title = "<color=#fff>原始　同名谱</color>", Composer = "作曲者", ComposerRealmId = new { rawValue = 11 },
                    Arranger = "编曲者", ArrangerRealmId = new { rawValue = 22 }, AcquiredTime = new { rawValue = long.MinValue.ToString() },
                    IsCopyingAllowed = true, IsFavorite = true, IsLocked = false, IsJunked = false } }), "MusicNote");
        }).ToArray();
        var coverage = Enum.GetValues<InventoryContainer>().Select(c => new InventoryCoverage("MusicNoteItem", c, true)).ToArray();
        await store.ApplyFrameAsync(new(1, new("inventory-metadata-replay", "1", binding.Region, binding.GameBuild), binding.Id,
            new(1), new(1), DateTimeOffset.UtcNow, 1, InventoryFrameKind.Full,
            new("1", ulong.MaxValue.ToString(), "角色", "角色"), items, [], coverage, BindingRevision: binding.BindingRevision));
    }
    private static WebApplication Build(Workspace area) => BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Options, mock: true);
    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static HttpClient Http(WebApplication app, string? token = Token)
    {
        var client = new HttpClient { BaseAddress = Address(app) };
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string temporaryRoot = Path.GetFullPath(Path.Combine("artifacts", "tmp"));
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tmp", "owned-scores-api-" + Guid.NewGuid().ToString("N")));
        public StorageOptions Options => new(Path.Combine(Root, "bootstrap"), true, PortableRoot: Root);
        public void Dispose()
        {
            if (!Root.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试目录越界");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
