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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class OwnedInstrumentApiTests
{
    private const string Token = "owned_instruments_0123456789012345678901234567890123456789";

    [Fact]
    public async Task ReadOnlyClientsShareInventoryIdentityAndDoNotStartObservation()
    {
        using var area = new Workspace();
        await using var app = Build(area);
        await app.StartAsync();
        using var owner = Sdk(app);
        using var anonymous = Http(app, null);
        using var denied = await anonymous.PostAsJsonAsync(ApiContract.QueryOwnedMusicInstruments.Path, new MusicOwnedInstrumentQuery(), ContractJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await Seed(app);
        var challenge = await owner.CreatePairingChallengeAsync(new("instrument-reader", null, [ApiPermissions.SystemRead]));
        var grant = await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        using var reader = Sdk(app, grant.Token);
        var first = await owner.QueryOwnedMusicInstrumentsAsync(new());
        var second = await reader.QueryOwnedMusicInstrumentsAsync(new());
        Assert.Equal(Json(first), Json(second));
        Assert.True(first.Current);
        Assert.True(first.Simulation);
        Assert.Equal("mabiagent-inventory-replay", first.Source!.Provider);
        Assert.Equal("18446744073709551615", first.Identity!.CharacterId);
        Assert.Equal(3, first.Items.Count);
        Assert.Equal(3, first.Items.Select(item => item.Container).Distinct().Count());
        Assert.All(first.Items, item =>
        {
            Assert.True(item.Current);
            Assert.Equal("client:TW:Instrument:9007199254740997", item.ClientInstrumentId);
            Assert.Equal(MusicInstrumentMappingState.ClientCatalog, item.Mapping.KnownValue);
            Assert.Equal("<color=#ffffff>原始乐器</color>", item.Name.RawName);
            Assert.Equal(1UL, item.Quantity!.Value.Value);
        });
        var equipped = Assert.Single(first.Items, item => item.Equipped);
        Assert.Equal(ulong.MaxValue, equipped.InstanceId!.Value.Value);
        Assert.Equal(InventoryContainer.Backpack, equipped.Container.KnownValue);
        var inventory = await owner.GetInventorySnapshotAsync();
        Assert.Equal(inventory.ResourceRevision, first.InventoryRevision);
        Assert.Equal(inventory.Catalog.Revision, first.CatalogRevision);
        Assert.Equal(inventory.Data!.GameSessionId, first.GameSessionId);
        Assert.Equal(0, (await owner.GetGameObservationRuntimeAsync()).SubscriberCount);
        Assert.Null((await owner.GetGameObservationRuntimeAsync()).WorkerProcessId);
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("inventory"));
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("music-library"));
        await app.StopAsync();
    }

    [Fact]
    public async Task NameAndCatalogChangesInvalidatePaginationAndPreserveUserEdits()
    {
        using var area = new Workspace();
        await using var app = Build(area);
        await app.StartAsync();
        using var sdk = Sdk(app);
        await Seed(app);
        var first = await sdk.QueryOwnedMusicInstrumentsAsync(new(PageSize: 1));
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);
        var second = await sdk.QueryOwnedMusicInstrumentsAsync(new(PageSize: 1, Cursor: first.NextCursor));
        Assert.NotEqual(first.Items[0].InstanceId, second.Items[0].InstanceId);
        var mismatch = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryOwnedMusicInstrumentsAsync(
            new(Container: InventoryContainer.Backpack, PageSize: 1, Cursor: first.NextCursor)));
        Assert.Equal("inventory.planStale", mismatch.Problem!.Code);

        var store = app.Services.GetRequiredService<InventoryHistoryStore>();
        await store.EditAssociationsAsync(new("account-instrument", [new(Type, "我的乐器")]), store.Revision);
        var stale = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryOwnedMusicInstrumentsAsync(new(PageSize: 1, Cursor: first.NextCursor)));
        Assert.Equal("inventory.planStale", stale.Problem!.Code);
        var renamed = await sdk.QueryOwnedMusicInstrumentsAsync(new());
        Assert.All(renamed.Items, item => Assert.Equal("我的乐器", item.Name.DisplayName));
        var beforeCatalog = await sdk.QueryOwnedMusicInstrumentsAsync(new(PageSize: 1));
        await store.ReplaceCatalogAsync(Catalog("新目录", 9007199254740998));
        Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryOwnedMusicInstrumentsAsync(
            new(PageSize: 1, Cursor: beforeCatalog.NextCursor)))).Problem!.Code);
        var remapped = await sdk.QueryOwnedMusicInstrumentsAsync(new());
        Assert.All(remapped.Items, item =>
        {
            Assert.Equal("我的乐器", item.Name.DisplayName);
            Assert.Equal("client:TW:Instrument:9007199254740998", item.ClientInstrumentId);
        });
        await store.EditAssociationsAsync(new("account-instrument", [new(Type, null, true)]), store.Revision);
        var cleared = await sdk.QueryOwnedMusicInstrumentsAsync(new());
        Assert.All(cleared.Items, item =>
        {
            Assert.Null(item.Name.RawName);
            Assert.Null(item.Name.DisplayName);
            Assert.Equal(InventoryNameState.UserCleared, item.Name.State.KnownValue);
            Assert.Equal(MusicInstrumentMappingState.ClientCatalog, item.Mapping.KnownValue);
        });
        await app.StopAsync();
    }

    [Fact]
    public async Task BindingChangesAndRestartRetainHistoryAndRejectOldCursor()
    {
        using var area = new Workspace();
        string oldCursor;
        await using (var app = Build(area))
        {
            await app.StartAsync();
            using var sdk = Sdk(app);
            await Seed(app);
            var page = await sdk.QueryOwnedMusicInstrumentsAsync(new(PageSize: 1));
            oldCursor = page.NextCursor!;
            var controls = app.Services.GetRequiredService<GameControlContext>();
            var binding = controls.Bindings.Current!;
            // 窗口移动只递增绑定修订：同一游戏会话的持有乐器仍是当前，游标继续有效。
            controls.Bindings.Update(new(binding.Session.Region, binding.Session.ProcessId, binding.Session.ProcessStartTime,
                binding.Session.WindowIdentity, binding.Session.GameBuild, binding.Bounds with { X = binding.Bounds.X + 10 }, 96, 96, false));
            var moved = await sdk.QueryOwnedMusicInstrumentsAsync(new());
            Assert.True(moved.Current);
            Assert.DoesNotContain("inventory.bindingChanged", moved.Reasons);
            Assert.Single((await sdk.QueryOwnedMusicInstrumentsAsync(new(PageSize: 1, Cursor: oldCursor))).Items);
            // 游戏进程重启即新的游戏会话：保留历史实例，撤销当前状态并拒绝旧游标。
            controls.Bindings.Update(new(binding.Session.Region, binding.Session.ProcessId, binding.Session.ProcessStartTime.AddSeconds(1),
                binding.Session.WindowIdentity, binding.Session.GameBuild, binding.Bounds, 96, 96, false));
            var restartedGame = await sdk.QueryOwnedMusicInstrumentsAsync(new());
            Assert.False(restartedGame.Current);
            Assert.Contains("inventory.bindingChanged", restartedGame.Reasons);
            Assert.All(restartedGame.Items, item => Assert.False(item.Current));
            Assert.Empty((await sdk.QueryOwnedMusicInstrumentsAsync(new(CurrentOnly: true))).Items);
            Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryOwnedMusicInstrumentsAsync(
                new(PageSize: 1, Cursor: oldCursor)))).Problem!.Code);
            await app.StopAsync();
        }
        await using var restarted = Build(area);
        await restarted.StartAsync();
        using var next = Sdk(restarted);
        var history = await next.QueryOwnedMusicInstrumentsAsync(new());
        Assert.Equal(3, history.Items.Count);
        Assert.False(history.Current);
        Assert.Contains("inventory.hostRestarted", history.Reasons);
        Assert.All(history.Items, item => Assert.Equal(ObservationQuality.Stale, item.Quality.KnownValue));
        Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<MabiToolApiException>(() => next.QueryOwnedMusicInstrumentsAsync(
            new(PageSize: 1, Cursor: oldCursor)))).Problem!.Code);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task DisabledInventoryAndInvalidQueriesReturnStructuredErrors()
    {
        using var area = new Workspace();
        await using var app = Build(area);
        await app.StartAsync();
        using var sdk = Sdk(app);
        var invalid = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryOwnedMusicInstrumentsAsync(new(PageSize: 1001)));
        Assert.Equal("request.invalid", invalid.Problem!.Code);
        using var http = Http(app);
        using var extra = await http.PostAsJsonAsync(ApiContract.QueryOwnedMusicInstruments.Path, new { pageSize = 1, rawInput = true });
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        await app.Services.GetRequiredService<ModuleRuntime>().SetEnabledAsync("inventory", false);
        var disabled = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryOwnedMusicInstrumentsAsync(new()));
        Assert.Equal("module.disabled", disabled.Problem!.Code);
        Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("music-library"));
        await app.StopAsync();
    }

    [Fact]
    public async Task HostWithoutStorageReturnsEmptyUnavailableSource()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
        await app.StartAsync();
        using var sdk = Sdk(app);
        var page = await sdk.QueryOwnedMusicInstrumentsAsync(new());
        Assert.False(page.Current);
        Assert.False(page.Simulation);
        Assert.Empty(page.Items);
        Assert.Contains("storage.unavailable", page.Reasons);
        Assert.Null(page.Source);
        Assert.Null(page.Identity);
        Assert.Equal(ObservationQuality.Unknown, page.Quality.KnownValue);
        Assert.Null((await sdk.GetGameObservationRuntimeAsync()).WorkerProcessId);
        await app.StopAsync();
    }

    private static readonly InventoryTypeKey Type = new("EquipmentTool", new(1001), "EquipmentTool");
    private static async Task Seed(WebApplication app)
    {
        var store = app.Services.GetRequiredService<InventoryHistoryStore>();
        var binding = app.Services.GetRequiredService<GameControlContext>().Bindings.Current!.Session;
        var items = Enum.GetValues<InventoryContainer>().Select((container, index) => new InventoryItem("EquipmentTool", container,
            "instrument-" + index, Type.TypeId, new(ulong.MaxValue - (ulong)index), new(1), JsonSerializer.SerializeToElement(new { }),
            "EquipmentTool", Equipped: container == InventoryContainer.Backpack)).ToList();
        items.Add(new("EquipmentTool", InventoryContainer.Backpack, "non-instrument", new(1002), new(2), new(1), JsonSerializer.SerializeToElement(new { }), "EquipmentTool"));
        var coverage = Enum.GetValues<InventoryContainer>().Select(container => new InventoryCoverage("EquipmentTool", container, true)).ToArray();
        await store.ApplyFrameAsync(new(1, new("mabiagent-inventory-replay", "1.0.0", binding.Region, binding.GameBuild), binding.Id,
            new(1), new(1), DateTimeOffset.UtcNow, 1, InventoryFrameKind.Full,
            new("account-instrument", "18446744073709551615", "原始角色", "原始角色"), items, [], coverage,
            BindingRevision: binding.BindingRevision));
        await store.ReplaceCatalogAsync(Catalog("原始乐器", 9007199254740997));
    }
    private static InventoryCatalogData Catalog(string name, ulong instrument) => new("instrument-catalog", "TW", new string('a', 64), DateTimeOffset.UtcNow,
        [], [new(Type, "<color=#ffffff>" + name + "</color>", name, "EquipmentTool", new(1), new(0), new(0), new(0), true, new(instrument), new(7)),
            new(new("EquipmentTool", new(1002), "EquipmentTool"), "工具", "工具", "EquipmentTool", new(2), new(0), new(0), new(0), false)], [],
        [new("EquipmentTool", "EquipmentTool", true, 2)]);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static WebApplication Build(Workspace area) => BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Options, mock: true);
    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static MabiToolClient Sdk(WebApplication app, string token = Token) => new(Address(app), _ => ValueTask.FromResult(token));
    private static HttpClient Http(WebApplication app, string? token = Token)
    {
        var client = new HttpClient { BaseAddress = Address(app) };
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string temporaryRoot = Path.GetFullPath(Path.Combine("artifacts", "tmp"));
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tmp", "owned-instruments-api-" + Guid.NewGuid().ToString("N")));
        public StorageOptions Options => new(Path.Combine(Root, "bootstrap"), true, PortableRoot: Root);
        public void Dispose()
        {
            if (!Root.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试目录越界");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
