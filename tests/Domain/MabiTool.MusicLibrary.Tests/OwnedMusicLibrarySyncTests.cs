using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using Xunit;

namespace MabiTool.MusicLibrary.Tests;

/// <summary>乐谱库的抓包来源：玩家自制与复制的乐谱（表编号 0）按实例入库，游戏提供的乐谱（表编号非 0）留给游戏列表来源。</summary>
[Collection(MusicLibraryTestCollection.Name)]
public sealed class OwnedMusicLibrarySyncTests
{
    private static readonly ObservationSource Source = new("inventory-test", "1", "TW", "build");
    private static readonly GameSession Binding = new("game", "TW", 1, DateTimeOffset.UnixEpoch, new(1), new(2), "build");
    private static readonly string Character = "25251211640832";

    private sealed class Fake(InventoryReadModelSnapshot value) : IInventoryReadModel
    {
        public InventoryReadModelSnapshot Value = value;
        public int Reads;
        public ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        { Reads++; return ValueTask.FromResult(Value); }
    }

    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    /// <summary>一张乐谱实例；表编号 0 是玩家自制或复制的乐谱，非 0 是游戏提供的乐谱（与台服实采的形状一致）。</summary>
    private static InventoryItemView Note(ulong instance, ulong typeId, string title, InventoryContainer container = InventoryContainer.Backpack)
    {
        var data = JsonSerializer.SerializeToElement(new { data = new {
            Id = new { rawValue = instance.ToString() }, SheetId = new { Id = typeId }, Title = title,
            Composer = typeId == 0 ? "玩家" : "漢斯頓", ComposerRealmId = new { rawValue = 0 }, Arranger = "", ArrangerRealmId = new { rawValue = 0 },
            AcquiredTime = new { rawValue = "1784803465623" }, IsCopyingAllowed = true, IsFavorite = false, IsLocked = false, IsJunked = false } });
        return new(new("MusicNoteItem", container, instance.ToString(), new(typeId), new(instance), new(1), data, "MusicNote"),
            new(new("MusicNoteItem", new(typeId), "MusicNote"), null, null, InventoryNameState.Unknown, null),
            true, DateTimeOffset.UnixEpoch, ObservationQuality.Confirmed, Source);
    }

    private static Fake Model(IEnumerable<InventoryItemView> items, bool complete = true) => new(new(new(3),
        new("workspace", "game", new("1", Character, "角色", "角色"), new(1), new(3), new(2), ObservationQuality.Confirmed,
            true, DateTimeOffset.UnixEpoch, items.ToArray(),
            Enum.GetValues<InventoryContainer>().Select(c => new InventoryCoverage("MusicNoteItem", c, complete || c != InventoryContainer.AccountStorage,
                complete || c != InventoryContainer.AccountStorage ? null : "inventory.storageNotRepresented")).ToArray(), null, [], Source),
        new(new(0), null)));

    private sealed class Workspace : IDisposable
    {
        private readonly string root;
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            root = Path.Combine(repository!.FullName, "artifacts", "tmp", "owned-music-sync-tests", Guid.NewGuid().ToString("N"));
        }
        public DataRootLease Lease() => DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, Path.Combine(root, "data")));
        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private static OwnedMusicLibrarySyncService Service(Fake model, ScoreCatalogStore? store, Clock clock, GameSession? binding) =>
        new(new OwnedScoreProvider(model, "epoch"), store, () => binding, "epoch", clock);

    [Fact]
    public void SelectionKeepsPlayerComposedScoresAndLeavesGameProvidedOnesToTheGameList()
    {
        // 台服实采：自制与别人分享来的乐谱表编号都是 0，游戏商店与奖励的乐谱带表编号；两张同名的游戏乐谱表编号不同。
        var rows = new[]
        {
            Note(20, 0, "自制乙"), Note(10, 0, "自制甲"), Note(11, 0, "自制甲", InventoryContainer.CharacterStorage),
            Note(30, 802704190, "少年冒險家"), Note(31, 125454726, "少年冒險家"), Note(40, 0, "   "), Note(41, 0, "坏\u0007标题"),
        }.Select(view => view.Item).Select(item => new MusicOwnedScore(item.Type, item.Container, item.Key, item.InstanceId, item.Quantity, null,
            item.Attributes.GetProperty("data").GetProperty("Title").GetString(), null, null, null, null, null, null, null, null, null,
            item.Attributes, false, [], true, ObservationQuality.Confirmed, DateTimeOffset.UnixEpoch, Source)).ToArray();
        var selection = OwnedScoreSource.Select(rows);
        Assert.Equal(["10", "11", "20"], selection.Items.Select(item => item.StableSourceId));
        Assert.Equal(["自制甲", "自制甲", "自制乙"], selection.Items.Select(item => item.RawTitle));
        Assert.Equal(2, selection.GameProvided);
        Assert.Equal(2, selection.Unusable);
        Assert.Equal(ScoreSourceKind.Inventory, OwnedScoreSource.ScopeFor(Character).Kind);
        Assert.Equal("tw-capture", OwnedScoreSource.ScopeFor(Character).Provider);
    }

    [Fact]
    public async Task TheCaptureSourceHoldsPlayerComposedScoresPerInstanceAndReportsTheGameProvidedOnesItLeavesOut()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations);
        var store = new ScoreCatalogStore(database, "epoch");
        var model = Model([Note(10, 0, "自制甲"), Note(11, 0, "自制甲", InventoryContainer.CharacterStorage), Note(12, 0, "Victory (合奏)"),
            Note(30, 802704190, "少年冒險家"), Note(31, 125454726, "少年冒險家")]);
        var result = await Service(model, store, new Clock(), Binding).SyncAsync(default);

        Assert.Equal((MusicOwnedSyncStatus.Synced, (string?)null, 3, 2, true), (result.Status.KnownValue!.Value, result.Reason, result.ScoreCount, result.GameProvidedSkipped, result.Complete));
        var snapshot = store.Read(OwnedScoreSource.ScopeFor(Character))!;
        Assert.Equal(result.ScopeKey, snapshot.Scope.Key);
        Assert.Equal(["10", "11", "12"], snapshot.Entries.Select(entry => entry.StableSourceId!).Order());
        Assert.All(snapshot.Entries, entry => { Assert.Equal(ScoreAvailability.Present, entry.Availability); Assert.False(entry.Ambiguous); });
        Assert.DoesNotContain(snapshot.Entries, entry => entry.RawTitle == "少年冒險家");
        // 同名的两张自制乐谱各占一条，属同一候选组。
        Assert.Single(snapshot.Entries.Where(entry => entry.RawTitle == "自制甲").Select(entry => entry.CandidateGroup).Distinct());
        Assert.True(store.IsCurrent(snapshot));
    }

    [Fact]
    public async Task EntriesKeepTheirIdentityWhenContainersChangeAndAreMarkedMissingOnlyWithCompleteCoverage()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations);
        var store = new ScoreCatalogStore(database, "epoch");
        var clock = new Clock();
        var model = Model([Note(10, 0, "甲"), Note(11, 0, "乙"), Note(12, 0, "丙")]);
        var service = Service(model, store, clock, Binding);
        await service.SyncAsync(default);
        var scope = OwnedScoreSource.ScopeFor(Character);
        var ids = store.Read(scope)!.Entries.ToDictionary(entry => entry.StableSourceId!, entry => entry.Id);

        // 乙换了存放位置、丙被删除，覆盖完整：条目身份不变，丙标为已不在游戏里并留在曲库。
        clock.Milliseconds += 10_000;
        model.Value = Model([Note(10, 0, "甲"), Note(11, 0, "乙", InventoryContainer.AccountStorage)]).Value;
        var complete = await service.SyncAsync(default);
        Assert.True(complete.Complete);
        Assert.Equal(2, complete.ScoreCount);
        var after = store.Read(scope)!;
        Assert.All(after.Entries, entry => Assert.Equal(ids[entry.StableSourceId!], entry.Id));
        Assert.Equal(ScoreAvailability.Missing, after.Entries.Single(entry => entry.StableSourceId == "12").Availability);
        Assert.Equal(2UL, after.Revision);

        // 公共保管箱覆盖未知，甲也不见了：局部刷新不能证明它不在游戏里，条目保持 present。
        clock.Milliseconds += 10_000;
        model.Value = Model([Note(11, 0, "乙", InventoryContainer.AccountStorage)], complete: false).Value;
        var partial = await service.SyncAsync(default);
        Assert.False(partial.Complete);
        var kept = store.Read(scope)!;
        Assert.Equal(ScoreAvailability.Present, kept.Entries.Single(entry => entry.StableSourceId == "10").Availability);
        Assert.Equal(ScoreRefreshStatus.Partial, kept.Status);
        Assert.False(store.IsCurrent(kept));
    }

    [Fact]
    public async Task AnInventoryThatIsNotCurrentAndStorageFailuresLeaveTheSourceUntouchedAndReadsAreSpacedOut()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations);
        var store = new ScoreCatalogStore(database, "epoch");
        var clock = new Clock();
        var model = Model([Note(10, 0, "甲")]);
        Assert.Equal(MusicOwnedSyncStatus.Synced, (await Service(model, store, clock, Binding).SyncAsync(default)).Status.KnownValue);
        var revision = store.Read(OwnedScoreSource.ScopeFor(Character))!.Revision;

        // 内容没有变化的同步不写入：来源修订不变，不发出变更通知。间隔内再次同步返回上次结果，不再读取库存。
        var service = Service(model, store, clock, Binding);
        await service.SyncAsync(default);
        Assert.Equal(revision, store.Read(OwnedScoreSource.ScopeFor(Character))!.Revision);
        Assert.Single(database.ReadPendingEvents());
        var reads = model.Reads;
        Assert.Equal(MusicOwnedSyncStatus.Synced, (await service.SyncAsync(default)).Status.KnownValue);
        Assert.Equal(reads, model.Reads);

        // 游戏窗口没有绑定：库存不是当前，返回库存原因码，来源保持，数量取上次结果。
        clock.Milliseconds += 10_000;
        var unbound = await Service(model, store, clock, null).SyncAsync(default);
        Assert.Equal((MusicOwnedSyncStatus.Unavailable, "inventory.bindingChanged"), (unbound.Status.KnownValue!.Value, unbound.Reason));
        Assert.Equal(revision, store.Read(OwnedScoreSource.ScopeFor(Character))!.Revision);

        // 没有存储的宿主与库存还没有读到的宿主都不可用并写明原因。
        var withoutStorage = await Service(model, null, clock, Binding).SyncAsync(default);
        Assert.Equal((MusicOwnedSyncStatus.Unavailable, "storage.unavailable"), (withoutStorage.Status.KnownValue!.Value, withoutStorage.Reason));
        var empty = await new OwnedMusicLibrarySyncService(new OwnedScoreProvider(null, "epoch"), store, () => Binding, "epoch", clock).SyncAsync(default);
        Assert.Equal(MusicOwnedSyncStatus.Unavailable, empty.Status.KnownValue);
        Assert.Equal(revision, store.Read(OwnedScoreSource.ScopeFor(Character))!.Revision);
    }
}
