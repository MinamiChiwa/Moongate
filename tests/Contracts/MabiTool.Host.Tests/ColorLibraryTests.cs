using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Color.Domain;
using MabiTool.Modules.Color.Persistence;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class ColorLibraryTests
{
    private const string Token = "color_library_0123456789012345678901234567890123456789";
    private static ColorProfile Profile => ColorProfiles.Default with { Id = "123saved", Name = " 台服黑色 " };

    [Fact]
    public async Task SavedProfilesAndDraftPersistIndependentlyWithRevisionAndIdempotency()
    {
        using var area = new Area();
        ColorLibraryEdit save = new([Profile], [], null);
        await using (var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Storage))
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var sdk = new MabiToolClient(new(address), _ => ValueTask.FromResult(Token));
            ApiResponseMetadata? metadata = null;
            var initial = await sdk.GetColorLibraryAsync(onResponse: value => metadata = value);
            var result = await sdk.EditColorLibraryAsync(save, "save-profile", metadata!.ETag!);
            Assert.Equal(new UInt64String(1), result.Revision);
            var loaded = await sdk.GetColorProfileAsync("123saved");
            Assert.Equal("台服黑色", loaded.Name);
            Assert.Equal(new UInt64String(1), loaded.Revision);
            Assert.Equal(result, await sdk.EditColorLibraryAsync(save, "save-profile", metadata.ETag!));
            Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                sdk.EditColorLibraryAsync(save with { DeleteProfileIds = ["missing"] }, "save-profile", metadata.ETag!))).Problem!.Code);
            Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                sdk.EditColorLibraryAsync(save, "save-other", metadata.ETag!))).Problem!.Code);
            var draft = loaded with { Id = "draft", Name = "未完成草稿", Regions = loaded.Regions.Select(r => r with { Enabled = false }).ToArray() };
            await sdk.EditColorLibraryAsync(new([], [], draft), "edit-draft", ColorLibraryStore.ETag(result.Revision));
            var library = await sdk.GetColorLibraryAsync();
            Assert.Equal(initial.Draft, library.Draft with { Name = initial.Draft.Name, Regions = initial.Draft.Regions,
                Revision = initial.Draft.Revision, Id = initial.Draft.Id });
            Assert.All(library.Draft.Regions, r => Assert.False(r.Enabled));
            Assert.All((await sdk.GetColorProfileAsync("123saved")).Regions, r => Assert.True(r.Enabled));
            await sdk.EditColorLibraryAsync(new([], ["123saved"], null), "delete-profile", ColorLibraryStore.ETag(library.Revision));
            Assert.Equal(404, (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.GetColorProfileAsync("123saved"))).Status);
            await app.StopAsync();
        }
        await using var reopened = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Storage);
        await reopened.StartAsync();
        var store = reopened.Services.GetRequiredService<ColorLibraryStore>();
        Assert.Empty(store.Read().Profiles);
        Assert.Equal("未完成草稿", store.Read().Draft.Name);
        Assert.Equal(new UInt64String(3), store.Read().Revision);
        Assert.Equal(new UInt64String(1), (await store.EditAsync("owner", save, "save-profile", ColorLibraryStore.ETag(new(0)))).Revision);
        await reopened.StopAsync();
    }

    [Fact]
    public async Task ConcurrentEditsNamesAndFailedBatchesAreAtomic()
    {
        using var area = new Area(); using var lease = DataRootLease.Open(area.Storage);
        await using var database = SqliteStore.Open(lease, [ColorLibraryStore.Migration]);
        var store = new ColorLibraryStore(database);
        var first = store.EditAsync("owner", new([Profile], [], null), "first-key", ColorLibraryStore.ETag(new(0)));
        var second = store.EditAsync("owner", new([Profile with { Id = "other" }], [], null), "second-key", ColorLibraryStore.ETag(new(0)));
        var outcomes = await Task.WhenAll(Outcome(first), Outcome(second));
        Assert.Equal(1, outcomes.Count(c => c == "ok")); Assert.Contains("revision.stale", outcomes);
        var saved = Assert.Single(store.Read().Profiles);
        await Error("resource.conflict", new([saved with { Id = "duplicate", Name = saved.Name.ToUpperInvariant() }], [], null));
        await Error("resource.notFound", new([saved with { Name = "其他" }], ["missing"], null));
        await Error("request.invalid", new([saved with { Regions = [] }], [], null));
        await Error("request.invalid", new([saved with { Name = " " }], [], null));
        Assert.Equal(new UInt64String(1), store.Read().Revision); Assert.Equal(saved.Name, Assert.Single(store.Read().Profiles).Name);
        async Task Error(string code, ColorLibraryEdit edit) => Assert.Equal(code, (await Assert.ThrowsAsync<OperationException>(() =>
            store.EditAsync("owner", edit, Guid.NewGuid().ToString("N"), ColorLibraryStore.ETag(new(1))))).Code);
        static async Task<string> Outcome(Task<ColorLibraryChanged> task) { try { await task; return "ok"; } catch (OperationException e) { return e.Code; } }
    }

    [Fact]
    public async Task EditingPermissionDoesNotGrantGameControlAndPublishesRecoverableEvent()
    {
        using var area = new Area();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Storage);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var owner = new MabiToolClient(new(address), _ => ValueTask.FromResult(Token));
        var challenge = await owner.CreatePairingChallengeAsync(new("配色编辑器", null, [ApiPermissions.SystemRead, ApiPermissions.ColorEdit]));
        var grant = await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        using var editor = new MabiToolClient(new(address), _ => ValueTask.FromResult(grant.Token));
        await editor.EditColorLibraryAsync(new([Profile], [], null), "editor-save", ColorLibraryStore.ETag(new(0)));
        ProjectionSnapshot events;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do
        {
            events = await editor.GetEventSnapshotAsync(new(["color.library.changed"], [], null), cancellationToken: timeout.Token);
            if (events.Resources.Count > 0) break;
            await Task.Delay(20, timeout.Token);
        } while (true);
        Assert.Contains(events.Resources, r => r.Type == "color.library.changed" && r.Data.GetProperty("revision").GetString() == "1");
        var error = await Assert.ThrowsAsync<MabiToolApiException>(() => editor.StartColorRunAsync(new("game", new(1), Profile), "denied-run"));
        Assert.Equal("permission.denied", error.Problem!.Code);
        var readerChallenge = await owner.CreatePairingChallengeAsync(new("配色读者", null, [ApiPermissions.SystemRead]));
        var readerGrant = await owner.RedeemPairingAsync(new(readerChallenge.ChallengeId, readerChallenge.Code));
        using var reader = new MabiToolClient(new(address), _ => ValueTask.FromResult(readerGrant.Token));
        Assert.Single((await reader.GetColorLibraryAsync()).Profiles);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.EditColorLibraryAsync(new([], ["123saved"], null), "reader-denied", ColorLibraryStore.ETag(new(1))))).Status);
        await app.StopAsync();
    }

    private sealed class Area : IDisposable
    {
        private readonly string root;
        public StorageOptions Storage => new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data"));
        public Area()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "MabiTool.slnx"))) repo = repo.Parent;
            root = Path.Combine(repo!.FullName, "artifacts", "tmp", "color-library-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
