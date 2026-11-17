using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;
using Xunit;

namespace MabiTool.MusicLibrary.Tests;

public sealed class OwnedInstrumentProviderTests
{
    private static readonly ObservationSource Source = new("test", "1", "TW", "build");
    private static readonly GameSession Binding = new("game", "TW", 1, DateTimeOffset.UnixEpoch, new(1), new(2), "build");
    private sealed class Fake(InventoryReadModelSnapshot value) : IInventoryReadModel
    {
        public InventoryReadModelSnapshot Value = value;
        public ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Value); }
    }
    private static InventoryItemView Item(string key, InventoryContainer container = InventoryContainer.Backpack, ulong? quantity = 1, ulong type = 7, bool equipped = false)
        => new(new("EquipmentTool", container, key, new(type), new(type + 100), quantity is { } n ? new(n) : null,
            JsonSerializer.SerializeToElement(new { }), "EquipmentTool", equipped),
            new(new("EquipmentTool", new(type), "EquipmentTool"), "同名", "同名", InventoryNameState.UserConfirmed, "user"),
            true, DateTimeOffset.UnixEpoch, ObservationQuality.Confirmed, Source);
    private static Fake Model(params InventoryItemView[] items)
    {
        var entry = new InventoryCatalogEntry(new("EquipmentTool", new(7), "EquipmentTool"), "琴", "琴", "EquipmentTool", new(1), new(1), new(1), new(1), true, new(5), new(8));
        var catalog = new InventoryCatalogData("catalog", "TW", "hash", DateTimeOffset.UnixEpoch, [], [entry], [], []);
        var workspace = new InventoryWorkspace("workspace", "game", new("account", "character", "名字", "名字"), new(1), new(3), new(2),
            ObservationQuality.Partial, true, DateTimeOffset.UnixEpoch, items, [new("EquipmentTool", InventoryContainer.Backpack, false, "partial")], null, [], Source);
        return new(new(new(3), workspace, new(new(4), catalog)));
    }
    [Fact]
    public async Task InstancesContainersEquipmentAndNamesRemainDistinct()
    {
        var model = Model(Item("b", equipped: true), Item("a"), Item("c", InventoryContainer.CharacterStorage), Item("d", InventoryContainer.AccountStorage));
        var page = await new OwnedInstrumentProvider(model, "epoch", true).QueryAsync(new(), Binding);
        Assert.Equal(4, page.Items.Count); Assert.True(page.Current); Assert.True(page.Simulation); Assert.Equal(Source, page.Source);
        Assert.Single(page.Items, x => x.Equipped); Assert.All(page.Items, x => Assert.Equal("client:TW:Instrument:5", x.ClientInstrumentId));
        Assert.All(page.Items, x => Assert.Equal("同名", x.Name.RawName)); Assert.False(page.Coverage[0].Complete);
        Assert.Equal(ObservationQuality.Partial, page.Quality.KnownValue);
    }
    [Fact]
    public async Task UnknownMappingAndQuantityStayCandidatesWhileZeroAndNonInstrumentAreExcluded()
    {
        var model = Model(Item("zero", quantity: 0), Item("unknown", quantity: null, type: 9), Item("tool", type: 10));
        var non = model.Value.Catalog.Data!.Entries[0] with { Type = new("EquipmentTool", new(10), "EquipmentTool"), IsInstrument = false };
        model.Value = model.Value with { Catalog = model.Value.Catalog with { Data = model.Value.Catalog.Data with { Entries = [model.Value.Catalog.Data.Entries[0], non] } } };
        var page = await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), Binding);
        var item = Assert.Single(page.Items); Assert.Null(item.Quantity); Assert.False(item.Current); Assert.Null(item.ClientInstrumentId);
        Assert.Equal(MusicInstrumentMappingState.Unknown, item.Mapping.KnownValue); Assert.Equal(ObservationQuality.Partial, item.Quality.KnownValue);
    }
    [Theory]
    [InlineData("none")][InlineData("session")][InlineData("region")][InlineData("build")][InlineData("stale")][InlineData("source")]
    public async Task BindingAndObservationValidityGateCurrent(string kind)
    {
        var model = Model(Item("a")); GameSession? binding = Binding;
        if (kind == "none") binding = null;
        if (kind == "session") binding = Binding with { Id = "other-game" };
        if (kind == "region") binding = Binding with { Region = "KR" };
        if (kind == "build") binding = Binding with { GameBuild = "other" };
        if (kind == "stale") model.Value = model.Value with { Workspace = model.Value.Workspace! with { Current = false, Reasons = ["inventory.sequenceGap"] } };
        if (kind == "source") model.Value = model.Value with { Workspace = model.Value.Workspace! with { Source = null } };
        var page = await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), binding);
        Assert.False(page.Current); Assert.False(Assert.Single(page.Items).Current); Assert.NotEmpty(page.Reasons);
        Assert.Empty((await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(CurrentOnly: true), binding)).Items);
    }
    [Fact]
    public async Task MissingStorageWorkspaceCatalogAndClearedNameAreExplicit()
    {
        Assert.Contains("storage.unavailable", (await new OwnedInstrumentProvider(null, "epoch").QueryAsync(new(), Binding)).Reasons);
        var model = Model(Item("a") with { Name = Item("a").Name with { RawName = null, DisplayName = null, State = InventoryNameState.UserCleared } });
        model.Value = model.Value with { Catalog = new(new(5), null) };
        var item = Assert.Single((await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), Binding)).Items);
        Assert.Null(item.Name.RawName); Assert.Equal(InventoryNameState.UserCleared, item.Name.State.KnownValue); Assert.Null(item.ClientInstrumentId);
        model.Value = model.Value with { Workspace = null };
        Assert.Contains("inventory.sourceUnavailable", (await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), Binding)).Reasons);
    }
    [Fact]
    public async Task CursorAuthenticatesConditionsBindingAndRevisions()
    {
        var model = Model(Item("b"), Item("a")); var provider = new OwnedInstrumentProvider(model, "epoch");
        var page = await provider.QueryAsync(new(PageSize: 1), Binding);
        Assert.Equal("a", Assert.Single(page.Items).Key);
        Assert.Equal("b", Assert.Single((await provider.QueryAsync(new(PageSize: 1, Cursor: page.NextCursor), Binding)).Items).Key);
        foreach (var query in new[] { new MusicOwnedInstrumentQuery(PageSize: 2, Cursor: page.NextCursor), new(PageSize: 1, Cursor: "tampered"), new(PageSize: 1, Cursor: page.NextCursor, CurrentOnly: true) })
            Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(query, Binding))).Code);
        model.Value = model.Value with { Revision = new(8) };
        Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(new(PageSize: 1, Cursor: page.NextCursor), Binding))).Code);
    }
    [Fact]
    public async Task ValidationCancellationAndPageBudgetAreEnforced()
    {
        var model = Model(Item("a")); var provider = new OwnedInstrumentProvider(model, "epoch");
        foreach (var query in new MusicOwnedInstrumentQuery[] { null!, new(PageSize: 0), new(PageSize: 1001), new(Container: new OpenEnum<InventoryContainer>("future")) })
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(query, Binding))).Code);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.QueryAsync(new(), Binding, cts.Token));
        model.Value = model.Value with { Workspace = model.Value.Workspace! with { Items = [Item("a") with { Name = Item("a").Name with { RawName = new string('x', 4 * 1024 * 1024) } }] } };
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(new(), Binding))).Code);
    }
    [Fact]
    public async Task SheetMismatchAndInvalidReferencesRemainUnknown()
    {
        var model = Model(Item("a"), Item("sheet") with { Item = Item("sheet").Item with { Sheet = null } });
        var provider = new OwnedInstrumentProvider(model, "epoch");
        Assert.Null((await provider.QueryAsync(new(), Binding)).Items.Single(x => x.Key == "sheet").ClientInstrumentId);
        foreach (var entry in new[] { model.Value.Catalog.Data!.Entries[0] with { InstrumentTypeId = new(0) },
            model.Value.Catalog.Data.Entries[0] with { EquipmentToolTypeId = null }, model.Value.Catalog.Data.Entries[0] with { Table = "Instrument" } })
        {
            model.Value = model.Value with { Catalog = model.Value.Catalog with { Data = model.Value.Catalog.Data with { Entries = [entry] } } };
            Assert.All((await provider.QueryAsync(new(), Binding)).Items, x => Assert.Null(x.ClientInstrumentId));
        }
    }
    [Fact]
    public async Task CursorRejectsCatalogWorkspaceAndBindingChanges()
    {
        var model = Model(Item("a"), Item("b")); var original = model.Value;
        var provider = new OwnedInstrumentProvider(model, "epoch");
        var cursor = (await provider.QueryAsync(new(PageSize: 1), Binding)).NextCursor;
        foreach (var value in new[] { original with { Catalog = original.Catalog with { Revision = new(9) } },
            original with { Workspace = original.Workspace! with { Identity = new("other", "character", "名字", "名字") } } })
        {
            model.Value = value;
            Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(new(PageSize: 1, Cursor: cursor), Binding))).Code);
        }
        model.Value = original;
        Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(new(PageSize: 1, Cursor: cursor), Binding with { Id = "other-game" }))).Code);
        Assert.Single((await provider.QueryAsync(new(Container: InventoryContainer.Backpack, PageSize: 1), Binding)).Items);
    }
    private sealed class Waiting : IInventoryReadModel
    {
        public async ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }
    [Fact]
    public async Task CancellationReachesReadRequestAndSnapshotCapacityIsBounded()
    {
        using var cts = new CancellationTokenSource();
        var request = new OwnedInstrumentProvider(new Waiting(), "epoch").QueryAsync(new(), Binding, cts.Token);
        cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var model = Model(Item("a"));
        model.Value = model.Value with { Workspace = model.Value.Workspace! with { Items = Enumerable.Repeat(Item("a"), 10001).ToArray() } };
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), Binding))).Code);
    }
    [Fact]
    public async Task UnknownQualityAndMismatchedSourcesAreNotCurrent()
    {
        var model = Model(Item("a") with { Quality = new OpenEnum<ObservationQuality>("future-quality") });
        model.Value = model.Value with { Workspace = model.Value.Workspace! with { Quality = new OpenEnum<ObservationQuality>("future-quality") } };
        var page = await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), Binding);
        Assert.False(page.Current); Assert.Equal("future-quality", page.Quality.RawValue);
        Assert.False(Assert.Single(page.Items).Current); Assert.Equal("future-quality", page.Items[0].Quality.RawValue);
        model = Model(Item("a") with { Source = Source with { Region = "KR" } });
        page = await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), Binding);
        Assert.False(Assert.Single(page.Items).Current);
        model = Model(Item("a"));
        model.Value = model.Value with { Catalog = model.Value.Catalog with { Data = model.Value.Catalog.Data! with { Region = "KR" } } };
        Assert.Null(Assert.Single((await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), Binding)).Items).ClientInstrumentId);
    }
    private sealed class Delayed(Fake model) : IInventoryReadModel
    {
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        { Ready.SetResult(); await Release.Task.WaitAsync(cancellationToken); return model.Value; }
    }
    [Fact]
    public async Task BindingIsReadAfterInventoryWaitCompletes()
    {
        var model = new Delayed(Model(Item("a"))); GameSession? binding = Binding;
        var request = new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), () => binding);
        await model.Ready.Task; binding = Binding with { Id = "other-game" }; model.Release.SetResult();
        var page = await request; Assert.False(page.Current); Assert.Contains("inventory.bindingChanged", page.Reasons);
    }
    [Fact]
    public async Task ForeignCatalogCannotExcludeOrAttachReferencesToCandidate()
    {
        var model = Model(Item("a"));
        var catalog = model.Value.Catalog.Data!;
        model.Value = model.Value with { Catalog = model.Value.Catalog with { Data = catalog with {
            Region = "KR", Entries = [catalog.Entries[0] with { IsInstrument = false }] } } };
        var item = Assert.Single((await new OwnedInstrumentProvider(model, "epoch").QueryAsync(new(), Binding)).Items);
        Assert.Equal(MusicInstrumentMappingState.Unknown, item.Mapping.KnownValue);
        Assert.Null(item.ClientInstrumentId); Assert.Null(item.InstrumentTypeId); Assert.Null(item.EquipmentToolTypeId);
    }
}
