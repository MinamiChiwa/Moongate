// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;
using Xunit;

namespace MabiTool.MusicLibrary.Tests;

public sealed class OwnedScoreProviderTests
{
    private static readonly ObservationSource Source = new("inventory-test", "1", "TW", "build");
    private static readonly GameSession Binding = new("game", "TW", 1, DateTimeOffset.UnixEpoch, new(1), new(2), "build");
    private sealed class Fake(InventoryReadModelSnapshot value) : IInventoryReadModel
    {
        public InventoryReadModelSnapshot Value = value;
        public ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Value); }
    }
    private static InventoryItemView Item(ulong id, InventoryContainer container = InventoryContainer.Backpack)
    {
        var data = JsonSerializer.SerializeToElement(new { data = new {
            Id = new { rawValue = id.ToString() }, SheetId = new { Id = 7 }, Title = "<color=#fff>同名　谱</color>",
            Composer = "作曲　者", ComposerRealmId = new { rawValue = 11 }, Arranger = "编曲者", ArrangerRealmId = new { rawValue = 22 },
            AcquiredTime = new { rawValue = long.MinValue.ToString() }, IsCopyingAllowed = true, IsFavorite = false, IsLocked = true, IsJunked = false } });
        return new(new("MusicNoteItem", container, id.ToString(), new(7), new(id), new(1), data, "MusicNote"),
            new(new("MusicNoteItem", new(7), "MusicNote"), "目录通用名称", "目录通用名称", InventoryNameState.ClientCatalog, "catalog"),
            true, DateTimeOffset.UnixEpoch, ObservationQuality.Confirmed, Source);
    }
    private static Fake Model(params InventoryItemView[] items) => new(new(new(3),
        new("workspace", "game", new("1", ulong.MaxValue.ToString(), "角色", "角色"), new(1), new(3), new(2), ObservationQuality.Confirmed,
            true, DateTimeOffset.UnixEpoch, items, Enum.GetValues<InventoryContainer>().Select(c => new InventoryCoverage("MusicNoteItem", c, true)).ToArray(), null, [], Source),
        new(new(0), null)));

    [Fact]
    public async Task SameTitlesKeepInstancesRawMetadataAndThreeContainers()
    {
        var model = Model(Item(ulong.MaxValue), Item(2, InventoryContainer.CharacterStorage), Item(3, InventoryContainer.AccountStorage));
        var page = await new OwnedScoreProvider(model, "epoch", true).QueryAsync(new(), Binding);
        Assert.True(page.Current); Assert.True(page.Complete); Assert.True(page.Simulation);
        Assert.Equal(3, page.Items.Count); Assert.Equal(3, page.Items.Select(i => i.Container).Distinct().Count());
        Assert.Equal(3, page.Items.Select(i => i.InstanceId).Distinct().Count());
        Assert.All(page.Items, row => {
            Assert.Equal("<color=#fff>同名　谱</color>", row.RawTitle); Assert.Equal("作曲　者", row.Composer);
            Assert.Equal(11, row.ComposerRealmId); Assert.Equal(22, row.ArrangerRealmId);
            Assert.Equal(long.MinValue.ToString(), row.AcquiredTimeRaw); Assert.Equal(7UL, row.SheetId!.Value.Value);
            Assert.True(row.MetadataComplete); Assert.True(row.Current); Assert.True(row.IsCopyingAllowed);
            Assert.False(row.IsFavorite); Assert.True(row.IsLocked); Assert.False(row.IsJunked); Assert.Empty(row.Reasons);
            Assert.Equal(Source, row.Source); Assert.Equal(1UL, row.Quantity!.Value.Value);
        });
        Assert.Equal(ulong.MaxValue, page.Items.Single(i => i.Container.KnownValue == InventoryContainer.Backpack).InstanceId!.Value.Value);
        Assert.Equal(ulong.MaxValue.ToString(), page.Identity!.CharacterId); Assert.Equal("workspace", page.WorkspaceId);
    }

    [Fact]
    public async Task MissingInvalidAndConflictingMetadataRetainInventoryEvidence()
    {
        var item = Item(1);
        var malformed = JsonSerializer.SerializeToElement(new { data = new { Id = new { rawValue = "2" }, SheetId = new { Id = 8 }, Title = 12, IsFavorite = "true" } });
        var model = Model(item with { Item = item.Item with { Attributes = malformed } });
        var row = Assert.Single((await new OwnedScoreProvider(model, "epoch").QueryAsync(new(), Binding)).Items);
        Assert.Null(row.RawTitle); Assert.Null(row.Composer); Assert.False(row.MetadataComplete); Assert.False(row.Current);
        Assert.Equal(1UL, row.InstanceId!.Value.Value); Assert.Equal(malformed.GetRawText(), row.Attributes.GetRawText());
        Assert.Contains("music.scoreMetadata.title.invalid", row.Reasons); Assert.Contains("music.scoreMetadata.composer.missing", row.Reasons);
        Assert.Contains("music.scoreMetadata.id.conflict", row.Reasons); Assert.Contains("music.scoreMetadata.sheetId.conflict", row.Reasons);
        Assert.Contains("music.scoreMetadata.isFavorite.invalid", row.Reasons);
        var page = await new OwnedScoreProvider(model, "epoch").QueryAsync(new(CurrentOnly: true), Binding);
        Assert.Empty(page.Items); Assert.False(page.Complete);
    }

    [Fact]
    public async Task CompleteEmptyRequestedContainerAndUnknownCoverageRemainDifferent()
    {
        var model = Model(); var provider = new OwnedScoreProvider(model, "epoch");
        Assert.True((await provider.QueryAsync(new(Container: InventoryContainer.Backpack), Binding)).Complete);
        model.Value = model.Value with { Workspace = model.Value.Workspace! with {
            Coverage = [new("MusicNoteItem", InventoryContainer.Backpack, true)] } };
        var all = await provider.QueryAsync(new(), Binding);
        Assert.True(all.Current); Assert.False(all.Complete); Assert.Equal(3, all.Coverage.Count);
        Assert.Equal(2, all.Coverage.Count(c => !c.Complete));
        Assert.True((await provider.QueryAsync(new(Container: InventoryContainer.Backpack), Binding)).Complete);
        Assert.False((await provider.QueryAsync(new(Container: InventoryContainer.AccountStorage), Binding)).Complete);
        model.Value = model.Value with { Workspace = model.Value.Workspace! with { Current = false, Reasons = ["inventory.observationStopped"] } };
        var historical = await provider.QueryAsync(new(Container: InventoryContainer.Backpack, CurrentOnly: true), Binding);
        Assert.Empty(historical.Items); Assert.False(historical.Current); Assert.False(historical.Complete);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("source")]
    [InlineData("identity")]
    [InlineData("gap")]
    [InlineData("quality")]
    public async Task InvalidBindingIdentityAndObservationKeepHistoricalInstances(string kind)
    {
        var model = Model(Item(1)); GameSession? binding = Binding;
        var workspace = model.Value.Workspace!;
        if (kind == "binding") binding = Binding with { BindingRevision = new(99) };
        if (kind == "source") workspace = workspace with { Source = Source with { GameBuild = "other" } };
        if (kind == "identity") workspace = workspace with { Identity = null };
        if (kind == "gap") workspace = workspace with { Current = false, Reasons = ["inventory.sequenceGap"] };
        if (kind == "quality") workspace = workspace with { Quality = new OpenEnum<ObservationQuality>("future") };
        model.Value = model.Value with { Workspace = workspace };
        var provider = new OwnedScoreProvider(model, "epoch"); var page = await provider.QueryAsync(new(), binding);
        Assert.False(page.Current); Assert.False(page.Complete); Assert.False(Assert.Single(page.Items).Current); Assert.NotEmpty(page.Reasons);
        Assert.Empty((await provider.QueryAsync(new(CurrentOnly: true), binding)).Items);
        if (kind == "quality") Assert.Equal("future", page.Quality.RawValue);
    }

    [Fact]
    public async Task CurrentOnlyCannotHideUnknownQuantityToClaimCompleteness()
    {
        var item = Item(1); var model = Model(item with { Item = item.Item with { Quantity = null } });
        var page = await new OwnedScoreProvider(model, "epoch").QueryAsync(new(CurrentOnly: true), Binding);
        Assert.Empty(page.Items); Assert.False(page.Complete);
        var candidate = Assert.Single((await new OwnedScoreProvider(model, "epoch").QueryAsync(new(), Binding)).Items);
        Assert.Contains("inventory.quantityUnknown", candidate.Reasons); Assert.False(candidate.Current);
    }

    [Theory]
    [InlineData("container")]
    [InlineData("quality")]
    [InlineData("source")]
    public async Task UnknownItemContainerQualityAndForeignSourceRemainVisibleCandidates(string kind)
    {
        var item = Item(1);
        if (kind == "container") item = item with { Item = item.Item with { Container = new OpenEnum<InventoryContainer>("future-container") } };
        if (kind == "quality") item = item with { Quality = new OpenEnum<ObservationQuality>("future-quality") };
        if (kind == "source") item = item with { Source = Source with { Provider = "foreign-provider" } };
        var model = Model(item); var provider = new OwnedScoreProvider(model, "epoch");
        var page = await provider.QueryAsync(new(), Binding);
        Assert.True(page.Current); Assert.False(page.Complete); Assert.False(Assert.Single(page.Items).Current);
        Assert.NotEmpty(page.Items[0].Reasons); Assert.Equal(item.Item.Container.RawValue, page.Items[0].Container.RawValue);
        Assert.Equal(item.Quality.RawValue, page.Items[0].Quality.RawValue); Assert.Equal(item.Source, page.Items[0].Source);
        var filtered = await provider.QueryAsync(new(CurrentOnly: true), Binding); Assert.Empty(filtered.Items); Assert.False(filtered.Complete);
    }

    [Fact]
    public async Task PagingAuthenticatesFilterBindingWorkspaceGenerationAndRevision()
    {
        var model = Model(Item(2), Item(1)); var initial = model.Value;
        var provider = new OwnedScoreProvider(model, "epoch");
        var first = await provider.QueryAsync(new(PageSize: 1), Binding);
        Assert.Equal(1UL, first.Items[0].InstanceId!.Value.Value);
        Assert.Equal(2UL, (await provider.QueryAsync(new(PageSize: 1, Cursor: first.NextCursor), Binding)).Items[0].InstanceId!.Value.Value);
        foreach (var next in new[] { new MusicOwnedScoreQuery(PageSize: 2, Cursor: first.NextCursor), new(PageSize: 1, Cursor: "tampered"),
            new(PageSize: 1, Cursor: first.NextCursor, CurrentOnly: true), new(Container: InventoryContainer.Backpack, PageSize: 1, Cursor: first.NextCursor) })
            Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(next, Binding))).Code);
        foreach (var changed in new[] { initial with { Revision = new(4) },
            initial with { Workspace = initial.Workspace! with { Id = "other" } },
            initial with { Workspace = initial.Workspace! with { Generation = new(2) } },
            initial with { Workspace = initial.Workspace! with { Revision = new(5) } } })
        {
            model.Value = changed;
            Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(new(PageSize: 1, Cursor: first.NextCursor), Binding))).Code);
        }
        model.Value = initial;
        Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(new(PageSize: 1, Cursor: first.NextCursor), Binding with { BindingRevision = new(9) }))).Code);
        Assert.Equal("inventory.planStale", (await Assert.ThrowsAsync<OperationException>(() => new OwnedScoreProvider(model, "next-epoch").QueryAsync(new(PageSize: 1, Cursor: first.NextCursor), Binding))).Code);
    }

    [Fact]
    public async Task BudgetsInvalidFiltersUnavailableStorageAndCancellationAreExplicit()
    {
        var provider = new OwnedScoreProvider(null, "epoch");
        Assert.Contains("storage.unavailable", (await provider.QueryAsync(new(), Binding)).Reasons);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(new(PageSize: 1001), Binding))).Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => provider.QueryAsync(new(Container: new OpenEnum<InventoryContainer>("future")), Binding))).Code);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.QueryAsync(new(), Binding, cts.Token));
        var item = Item(1); var model = Model(item with { Item = item.Item with {
            Attributes = JsonSerializer.SerializeToElement(new { data = new { Title = new string('x', 4 * 1024 * 1024) } }) } });
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => new OwnedScoreProvider(model, "epoch").QueryAsync(new(), Binding))).Code);
    }

    private sealed class Waiting(Fake model) : IInventoryReadModel
    {
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        { Ready.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return model.Value; }
    }
    [Fact]
    public async Task BindingIsReadAfterWaitAndCancelledReadReleasesTheQuery()
    {
        var model = new Waiting(Model(Item(1))); GameSession? binding = Binding;
        var provider = new OwnedScoreProvider(model, "epoch");
        var pending = provider.QueryAsync(new(), () => binding);
        await model.Ready.Task; binding = Binding with { BindingRevision = new(8) }; model.Release.TrySetResult();
        Assert.False((await pending).Current);
        var cancelled = new Waiting(Model(Item(1))); using var cts = new CancellationTokenSource();
        var read = new OwnedScoreProvider(cancelled, "epoch").QueryAsync(new(), Binding, cts.Token);
        await cancelled.Ready.Task; cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
