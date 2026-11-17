// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Inventory.Adapters;
using Xunit;

namespace MabiTool.Inventory.Tests;

public sealed class InventoryReadModelTests
{
    [Fact]
    public async Task SnapshotPreservesWorkspaceItemsAndCatalogRevision()
    {
        await using var area = new Area();
        await area.Store.ApplyFrameAsync(Frame());
        var catalog = await area.Store.ReplaceCatalogAsync(Catalog("客户端乐器"));
        IInventoryReadModel reader = area.Store;

        var result = await reader.ReadAsync();
        var workspace = Assert.IsType<InventoryWorkspace>(result.Workspace);
        Assert.Equal(area.Store.Revision, result.Revision);
        Assert.Equal(result.Revision, workspace.Revision);
        Assert.Equal("session", workspace.GameSessionId);
        Assert.Equal(new InventoryIdentity("account", "character", "原始角色", "显示角色"), workspace.Identity);
        Assert.True(workspace.Current);
        Assert.Equal(catalog.Revision, result.Catalog.Revision);
        Assert.Equal("客户端乐器", Assert.Single(result.Catalog.Data!.Entries).RawName);
        var item = Assert.Single(workspace.Items);
        Assert.Equal(7UL, item.Item.Quantity!.Value.Value);
        Assert.Equal(11UL, item.Item.InstanceId!.Value.Value);
        Assert.Equal("蓝色", item.Item.Attributes.GetProperty("color").GetString());
        Assert.Equal(InventoryNameState.ClientCatalog, item.Name.State.KnownValue);
        Assert.Equal("客户端乐器", item.Name.DisplayName);
        Assert.True(item.Name.IsInstrument);
    }

    [Fact]
    public async Task ConfirmedAndClearedNamesProjectLatestEditsWithoutChangingEarlierSnapshot()
    {
        await using var area = new Area();
        await area.Store.ApplyFrameAsync(Frame());
        await area.Store.ReplaceCatalogAsync(Catalog("原目录名称"));
        IInventoryReadModel reader = area.Store;
        var original = await reader.ReadAsync();

        await area.Store.EditAssociationsAsync(new("account", [new(Type, "我的乐器")]), original.Revision);
        var confirmed = await reader.ReadAsync();
        Assert.True(confirmed.Revision.Value > original.Revision.Value);
        Assert.Equal(InventoryNameState.UserConfirmed, Assert.Single(confirmed.Workspace!.Items).Name.State.KnownValue);
        Assert.Equal("我的乐器", Assert.Single(confirmed.Workspace.Items).Name.DisplayName);

        await area.Store.EditAssociationsAsync(new("account", [new(Type, null, true)]), confirmed.Revision);
        var cleared = await reader.ReadAsync();
        Assert.True(cleared.Revision.Value > confirmed.Revision.Value);
        var name = Assert.Single(cleared.Workspace!.Items).Name;
        Assert.Equal(InventoryNameState.UserCleared, name.State.KnownValue);
        Assert.Null(name.DisplayName);
        Assert.Equal(confirmed.Catalog.Revision, cleared.Catalog.Revision);
        Assert.Equal("我的乐器", Assert.Single(confirmed.Workspace.Items).Name.DisplayName);
        Assert.Equal("原目录名称", Assert.Single(original.Workspace!.Items).Name.DisplayName);
    }

    [Fact]
    public async Task ReadWaitsForCatalogCommitAndCancelledWaitDoesNotBlockLaterReads()
    {
        await using var area = new Area();
        await area.Store.ApplyFrameAsync(Frame());
        await area.Store.ReplaceCatalogAsync(Catalog("提交前"));
        IInventoryReadModel reader = area.Store;
        var before = await reader.ReadAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = area.Database.WriteAsync<bool>(_ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("写队列测试门未释放");
            throw new QueueBlockReleasedException();
        });
        Task<InventoryCatalogSummary>? replacement = null;
        Task<InventoryReadModelSnapshot>? pendingRead = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            replacement = area.Store.ReplaceCatalogAsync(Catalog("提交后"));
            Assert.False(replacement.IsCompleted);
            pendingRead = reader.ReadAsync().AsTask();
            Assert.NotSame(pendingRead, await Task.WhenAny(pendingRead, Task.Delay(100)));

            using var cancellation = new CancellationTokenSource();
            var cancelledRead = reader.ReadAsync(cancellation.Token).AsTask();
            Assert.False(cancelledRead.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(pendingRead.IsCompleted);
        }
        finally
        {
            release.Set();
            await Assert.ThrowsAsync<QueueBlockReleasedException>(() => blocker.WaitAsync(TimeSpan.FromSeconds(3)));
            if (replacement is not null) await replacement.WaitAsync(TimeSpan.FromSeconds(3));
            if (pendingRead is not null) await pendingRead.WaitAsync(TimeSpan.FromSeconds(3));
        }

        var committed = await pendingRead!;
        Assert.Equal(before.Revision, committed.Revision);
        Assert.Equal(before.Catalog.Revision.Value + 1, committed.Catalog.Revision.Value);
        Assert.Equal("提交后", Assert.Single(committed.Catalog.Data!.Entries).DisplayName);
        Assert.Equal("提交后", Assert.Single(committed.Workspace!.Items).Name.DisplayName);
        Assert.Equal("提交前", Assert.Single(before.Workspace!.Items).Name.DisplayName);
        var next = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(committed.Catalog.Revision, next.Catalog.Revision);
        Assert.Equal("提交后", Assert.Single(next.Workspace!.Items).Name.DisplayName);
    }

    [Fact]
    public async Task ConcurrentColdCatalogReadsAndReloadsKeepCommittedNamesAndReadModelConsistent()
    {
        await using var area = new Area();
        await area.Store.ApplyFrameAsync(Frame());
        InventoryCatalogData ExpandedCatalog(string name) => Catalog(name) with
        {
            Entries = Catalog(name).Entries.Concat(Enumerable.Range(2, 255).Select(id =>
                new InventoryCatalogEntry(new("EquipmentTool", new((ulong)id), "EquipmentTool"),
                    name, name, "EquipmentTool", new(0), new(0), new(0), new(0)))).ToArray()
        };
        await area.Store.ReplaceCatalogAsync(ExpandedCatalog("名称0"));
        area.Store.Reload();
        using var start = new ManualResetEventSlim();
        var catalogReaders = Enumerable.Range(0, 3).Select(_ => Task.Factory.StartNew(() =>
        {
            if (!start.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("目录读测试门未释放");
            for (var i = 0; i < 24; i++)
            {
                var catalog = area.Store.Catalog();
                Assert.Equal(256, catalog.Data!.Entries.Count);
                Assert.All(catalog.Data.Entries, entry => Assert.Equal(catalog.Data.Entries[0].DisplayName, entry.DisplayName));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var modelReader = Task.Factory.StartNew(() =>
        {
            if (!start.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("读模型测试门未释放");
            IInventoryReadModel reader = area.Store;
            for (var i = 0; i < 24; i++)
            {
                var snapshot = reader.ReadAsync().AsTask().GetAwaiter().GetResult();
                Assert.Equal(snapshot.Catalog.Data!.Entries[0].DisplayName, Assert.Single(snapshot.Workspace!.Items).Name.DisplayName);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var allReaders = Task.WhenAll(catalogReaders.Append(modelReader));
        InventoryCatalogSummary? latest = null;
        try
        {
            start.Set();
            for (var i = 1; i <= 6; i++)
            {
                area.Store.Reload();
                latest = await area.Store.ReplaceCatalogAsync(ExpandedCatalog("名称" + i)).WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            start.Set();
            await allReaders.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var committed = area.Store.Catalog();
        Assert.Equal(latest!.Revision, committed.Revision);
        Assert.Equal("名称6", committed.Data!.Entries[0].DisplayName);
        area.Store.Reload();
        var reloaded = await ((IInventoryReadModel)area.Store).ReadAsync();
        Assert.Equal(committed.Revision, reloaded.Catalog.Revision);
        Assert.Equal("名称6", Assert.Single(reloaded.Workspace!.Items).Name.DisplayName);
    }

    [Fact]
    public async Task InvalidatedAndRestartedWorkspacesRetainHistoryAndNames()
    {
        await using var area = new Area();
        await area.Store.ApplyFrameAsync(Frame());
        await area.Store.ReplaceCatalogAsync(Catalog("历史乐器"));
        await area.Store.InvalidateAsync("inventory.observationGap");
        IInventoryReadModel reader = area.Store;
        var invalidated = await reader.ReadAsync();
        Assert.False(invalidated.Workspace!.Current);
        Assert.Contains("inventory.observationGap", invalidated.Workspace.Reasons);
        AssertHistory(invalidated);

        IInventoryReadModel restarted = new InventoryHistoryStore(area.Database, "next-epoch");
        var history = await restarted.ReadAsync();
        Assert.Equal(invalidated.Revision, history.Revision);
        Assert.Equal(invalidated.Workspace.Id, history.Workspace!.Id);
        Assert.Equal(invalidated.Catalog.Revision, history.Catalog.Revision);
        Assert.False(history.Workspace.Current);
        Assert.Contains("inventory.hostRestarted", history.Workspace.Reasons);
        AssertHistory(history);
    }

    [Fact]
    public async Task ScoresTakeTheirOwnTitlesAndOldUntitledEntriesAreReplacedByTheNextFullSync()
    {
        // 合成标题与作曲者；结构按客户端 MusicNoteItem 记录（Id、SheetId 等为只含 Id 的对象）。
        static InventoryItem Score(string key, ulong sheet, ulong instance, object attributes) => new("MusicNoteItem", InventoryContainer.Backpack, key,
            new(sheet), new(instance), new(1), JsonSerializer.SerializeToElement(attributes), "MusicNoteItem");
        static object Data(ulong sheet, ulong instance, string title, string composer) => new { key = instance, data = new {
            Id = new { Id = instance }, SheetId = new { Id = sheet }, AcquiredTime = new { Id = 0 }, Title = title, Composer = composer,
            ComposerRealmId = new { Id = 3 }, Arranger = "", ArrangerRealmId = new { Id = 0 }, IsCopyingAllowed = true, IsJunked = false, IsLocked = true, IsFavorite = false } };
        InventoryFrame Full(ulong sequence, params InventoryItem[] items) => Frame() with { Sequence = new(sequence), Items = items,
            Coverage = Enum.GetValues<InventoryContainer>().Select(c => new InventoryCoverage("MusicNoteItem", c, true)).ToArray() };
        await using var area = new Area();
        // 旧数据：属性里没有乐谱元数据，名称按类型（目录未收录，未知）。
        await area.Store.ApplyFrameAsync(Full(1, Score("a", 0, 21, new { })));
        var old = Assert.Single(area.Store.Workspace()!.Items);
        Assert.Equal(InventoryNameState.Unknown, old.Name.State.KnownValue);
        Assert.Null(old.Score);

        await area.Store.ApplyFrameAsync(Full(2, Score("a", 0, 21, Data(0, 21, "合成自制谱甲", "合成作曲者")),
            Score("b", 0, 22, Data(0, 22, "合成自制谱乙", "合成作曲者")), Score("c", 9001, 23, Data(9001, 23, "合成系统谱", "另一作曲者")),
            Score("d", 0, 24, Data(0, 24, " ", "合成作曲者"))));
        var items = area.Store.Workspace()!.Items.ToDictionary(i => i.Item.Key);
        Assert.Equal(4, items.Count);
        Assert.Equal(4UL, items.Values.Aggregate(0UL, (sum, i) => sum + i.Item.Quantity!.Value.Value));
        Assert.Equal(["合成自制谱甲", "合成自制谱乙", "合成系统谱"], new[] { "a", "b", "c" }.Select(k => items[k].Name.DisplayName));
        Assert.All(new[] { "a", "b", "c" }, k => Assert.Equal(InventoryNameState.ItemInstance, items[k].Name.State.KnownValue));
        var system = items["c"].Score!;
        Assert.Equal(("另一作曲者", 3, true, true, false), (system.Composer, system.ComposerRealmId, system.CopyingAllowed, system.Locked, system.Favorite));
        // 标题为空：按类型的关联（未知），元数据照常返回。
        Assert.Equal(InventoryNameState.Unknown, items["d"].Name.State.KnownValue);
        Assert.Equal(" ", items["d"].Score!.Title);

        var byComposer = area.Store.Items(new(Search: "另一作曲"));
        Assert.Equal("c", Assert.Single(byComposer.Items).Item.Key);
        Assert.Equal("a", Assert.Single(area.Store.Items(new(Search: "自制谱甲")).Items).Item.Key);
    }

    private static void AssertHistory(InventoryReadModelSnapshot snapshot)
    {
        var item = Assert.Single(snapshot.Workspace!.Items);
        Assert.False(item.Current);
        Assert.Equal(ObservationQuality.Stale, item.Quality.KnownValue);
        Assert.Equal(7UL, item.Item.Quantity!.Value.Value);
        Assert.Equal("历史乐器", item.Name.DisplayName);
    }

    private static readonly InventoryTypeKey Type = new("EquipmentTool", new(1), "EquipmentTool");
    private sealed class QueueBlockReleasedException : Exception;
    private static InventoryFrame Frame() => new(1, new("test", "offline", "TW", null), "session",
        new(1), new(1), DateTimeOffset.UtcNow, 1, InventoryFrameKind.Full,
        new("account", "character", "原始角色", "显示角色"),
        [new("EquipmentTool", InventoryContainer.Backpack, "item", new(1), new(11), new(7),
            JsonSerializer.SerializeToElement(new { color = "蓝色" }), "EquipmentTool")], [],
        Enum.GetValues<InventoryContainer>().Select(c => new InventoryCoverage("EquipmentTool", c, true)).ToArray());
    private static InventoryCatalogData Catalog(string name) => new("catalog", "TW", new string('a', 64),
        DateTimeOffset.UtcNow, [], [new(Type, name, name, "EquipmentTool", new(0), new(0), new(0), new(0), true)], [], []);

    private sealed class Area : IAsyncDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine("artifacts", "tmp", "inventory-read-model-" + Guid.NewGuid().ToString("N")));
        private readonly DataRootLease lease;
        public SqliteStore Database { get; }
        public InventoryHistoryStore Store { get; }
        public Area()
        {
            lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, Path.Combine(root, "data")));
            Database = SqliteStore.Open(lease, [InventoryHistoryStore.Migration]);
            Store = new(Database, "epoch");
        }
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            lease.Dispose();
            var boundary = Path.GetFullPath(Path.Combine("artifacts", "tmp")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试数据根越界");
            if (Directory.Exists(root))
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                        .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                    throw new IOException("测试数据根包含链接");
                Directory.Delete(root, true);
            }
        }
    }
}
