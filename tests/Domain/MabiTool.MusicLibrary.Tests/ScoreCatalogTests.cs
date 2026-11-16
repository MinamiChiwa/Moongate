using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using Xunit;

namespace MabiTool.MusicLibrary.Tests;

[Collection(MusicLibraryTestCollection.Name)]
public sealed class ScoreCatalogTests
{
    private static readonly ScoreScope Scope = new("TW", "server-1", "18446744073709551615", "fixture", ScoreSourceKind.Inventory);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static ScoreCatalogSnapshot Refresh(ScoreCatalogSnapshot? old, params ScoreSourceItem[] items) =>
        ScoreCatalog.Refresh(Scope, old, items, ScoreRefreshStatus.Complete, "host-1", Now);

    [Fact]
    public void StableIdsSurviveRenameReorderMissingAndReappearance()
    {
        var first = Refresh(null, new ScoreSourceItem("同名", "a"), new ScoreSourceItem("同名", "b"));
        var ids = first.Entries.ToDictionary(x => x.StableSourceId!, x => x.Id);
        var next = Refresh(first, new ScoreSourceItem("改名", "b"), new ScoreSourceItem("同名", "a"));
        Assert.All(next.Entries, x => Assert.Equal(ids[x.StableSourceId!], x.Id));
        Assert.All(next.Entries, x => Assert.False(x.Ambiguous));
        var missing = Refresh(next, new ScoreSourceItem("改名", "b"));
        Assert.Equal(ScoreAvailability.Missing, missing.Entries.Single(x => x.StableSourceId == "a").Availability);
        var back = Refresh(missing, new ScoreSourceItem("同名", "a"), new ScoreSourceItem("改名", "b"));
        Assert.Equal(ids["a"], back.Entries.Single(x => x.StableSourceId == "a").Id);
        Assert.All(back.Entries, x => Assert.Equal(ScoreAvailability.Present, x.Availability));
    }

    [Fact]
    public void AnonymousDuplicatesRetainReferencesWithoutChoosingSurvivor()
    {
        var first = Refresh(null, new ScoreSourceItem("同名"), new ScoreSourceItem("別曲"), new ScoreSourceItem("同名"));
        var reordered = Refresh(first, new ScoreSourceItem("同名"), new ScoreSourceItem("同名"), new ScoreSourceItem("別曲"));
        Assert.Equal(first.Entries.Select(x => x.Id), reordered.Entries.Select(x => x.Id));
        var one = Refresh(reordered, new ScoreSourceItem("同名"));
        var candidates = one.Entries.Where(x => x.RawTitle == "同名").ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.All(candidates, x => { Assert.True(x.Ambiguous); Assert.Equal(ScoreAvailability.Uncertain, x.Availability); });
        var group = one.Groups.Single(x => x.RawTitle == "同名");
        Assert.Equal(1, group.ReportedCount);
        Assert.True(group.CountIsExact);
        Assert.Equal(2, group.CandidateIds.Count);
        Assert.Equal(ScoreAvailability.Missing, one.Entries.Single(x => x.RawTitle == "別曲").Availability);
        var none = Refresh(one);
        Assert.All(none.Entries, x => Assert.Equal(ScoreAvailability.Missing, x.Availability));
        var restored = Refresh(none, new ScoreSourceItem("同名"), new ScoreSourceItem("同名"));
        Assert.Equal(candidates.Select(x => x.Id), restored.Entries.Where(x => x.RawTitle == "同名").Select(x => x.Id));
    }

    [Fact]
    public void RawTitlesAreNotNormalizedIntoIdentityAndAnonymousIdsAreNotPromoted()
    {
        var first = Refresh(null, new ScoreSourceItem("Ａ"), new ScoreSourceItem("A"));
        Assert.Equal(2, first.Groups.Count);
        var next = Refresh(first, new ScoreSourceItem("A", "real-id"));
        Assert.Equal(3, next.Entries.Count);
        Assert.DoesNotContain(next.Entries.Single(x => x.StableSourceId is not null).Id, first.Entries.Select(x => x.Id));
    }

    [Fact]
    public void ScopeSeparatesRegionRealmCharacterProviderAndSourceKind()
    {
        ScoreScope[] scopes = [Scope, Scope with { Region = "KR" }, Scope with { RealmId = "server-2" },
            Scope with { CharacterId = "2" }, Scope with { Provider = "other" },
            Scope with { Kind = ScoreSourceKind.ObservationSummary }, Scope with { Kind = ScoreSourceKind.ImportedMml }];
        var results = scopes.Select(s => ScoreCatalog.Refresh(s, null, [new ScoreSourceItem("同名", "same-id")], ScoreRefreshStatus.Complete, "host", Now)).ToArray();
        Assert.Equal(scopes.Length, scopes.Select(s => s.Key).Distinct().Count());
        Assert.Equal(scopes.Length, results.SelectMany(s => s.Entries).Select(x => x.Id).Distinct().Count());
        Assert.Equal(ScoreAvailability.Observed, results[^2].Entries[0].Availability);
        Assert.Equal(ScoreAvailability.Imported, results[^1].Entries[0].Availability);
        Assert.False(results[^2].Groups[0].CountIsExact);
        Assert.Throws<ArgumentException>(() => ScoreCatalog.Refresh(scopes[1], results[0], [], ScoreRefreshStatus.Complete, "host", Now));
        Assert.NotEqual((Scope with { RealmId = "a|b", Provider = "c" }).Key, (Scope with { RealmId = "a", Provider = "b|c" }).Key);
    }

    [Fact]
    public void PartialFailedAndRestartedSnapshotsPreserveCacheAndReportStaleness()
    {
        var first = Refresh(null, new ScoreSourceItem("甲", "a"), new ScoreSourceItem("乙", "b"));
        var partial = ScoreCatalog.Refresh(Scope, first, [new ScoreSourceItem("甲", "a")], ScoreRefreshStatus.Partial, "host-1", Now.AddSeconds(1));
        Assert.All(partial.Entries, x => Assert.Equal(ScoreAvailability.Present, x.Availability));
        Assert.All(partial.Groups, x => Assert.False(x.CountIsExact));
        Assert.Equal(first.LastCompleteAt, partial.LastCompleteAt);
        var failed = ScoreCatalog.Refresh(Scope, partial, [], ScoreRefreshStatus.Failed, "host-1", Now.AddSeconds(2));
        Assert.Equal(partial.Entries, failed.Entries);
        Assert.True(failed.IsStale("host-1", Now.AddSeconds(3), TimeSpan.FromMinutes(1)));
        Assert.False(first.IsStale("host-1", Now, TimeSpan.FromMinutes(1)));
        Assert.True(first.IsStale("host-2", Now, TimeSpan.FromMinutes(1)));
        Assert.True(first.IsStale("host-1", Now.AddMinutes(2), TimeSpan.FromMinutes(1)));
        Assert.True(first.IsStale("host-1", Now.AddSeconds(-1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ValidationRejectsInvalidIdentifiersDuplicateSourceIdsAndCapacityOverflow()
    {
        Assert.Throws<ArgumentException>(() => Refresh(null, new ScoreSourceItem("x", "id"), new ScoreSourceItem("y", "id")));
        Assert.Throws<ArgumentException>(() => Refresh(null, new ScoreSourceItem("\ud800")));
        Assert.Throws<ArgumentException>(() => Refresh(null, new ScoreSourceItem("x\n")));
        Assert.Throws<ArgumentException>(() => Refresh(null, new ScoreSourceItem(new string('x', 4097))));
        Assert.Throws<ArgumentException>(() => Refresh(null, Enumerable.Repeat(new ScoreSourceItem("x"), ScoreCatalog.MaximumEntries + 1).ToArray()));
        Assert.Throws<ArgumentException>(() => ScoreCatalog.Refresh(Scope, null, [new ScoreSourceItem("x")], ScoreRefreshStatus.Failed, "host", Now));
        Assert.ThrowsAny<Exception>(() => ScoreCatalog.ValidateScope(Scope with { CharacterId = "01" }));
        Assert.Throws<ArgumentException>(() => ScoreCatalog.ValidateScope(Scope with { Kind = (ScoreSourceKind)99 }));
    }

    [Fact]
    public async Task PersistenceSurvivesRestartAndKeepsInventorySeparateFromSummary()
    {
        using var workspace = new Workspace();
        string id;
        using (var lease = workspace.Lease())
        await using (var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations))
        {
            var store = new ScoreCatalogStore(database, "host-1");
            var first = await store.RefreshAsync(Scope, 0, [new ScoreSourceItem("樂譜", "stable")], ScoreRefreshStatus.Complete);
            id = first.Entries[0].Id;
            var summaryScope = Scope with { Kind = ScoreSourceKind.ObservationSummary };
            await store.RefreshAsync(summaryScope, 0, [new ScoreSourceItem("樂譜", "stable")], ScoreRefreshStatus.Complete);
            await store.RefreshAsync(summaryScope, 1, [], ScoreRefreshStatus.Complete);
            Assert.Single(store.Read(Scope)!.Entries);
            Assert.Equal(1UL, store.Read(Scope)!.Revision);
            Assert.Equal(3, database.ReadPendingEvents().Count);
        }
        using (var lease = workspace.Lease())
        await using (var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations))
        {
            var store = new ScoreCatalogStore(database, "host-2");
            var cached = store.Read(Scope)!;
            Assert.Equal(id, cached.Entries[0].Id);
            Assert.True(cached.IsStale("host-2", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
            var next = await store.RefreshAsync(Scope, 1, [new ScoreSourceItem("新名稱", "stable")], ScoreRefreshStatus.Complete);
            Assert.Equal(id, next.Entries[0].Id);
            Assert.Equal("新名稱", store.Read(Scope)!.Entries[0].RawTitle);
            Assert.Equal(4, database.ReadPendingEvents().Count);
        }
    }

    [Fact]
    public async Task ConcurrentRefreshRejectsOneWriterAndCancellationDoesNotPublish()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations);
        var first = new ScoreCatalogStore(database, "host");
        var second = new ScoreCatalogStore(database, "host");
        var results = await Task.WhenAll(new[] { "a", "b" }.Select(async (title, index) =>
        {
            try
            {
                await (index == 0 ? first : second).RefreshAsync(Scope, 0, [new(title, "id")], ScoreRefreshStatus.Complete);
                return true;
            }
            catch (ScoreCatalogConflictException error) { Assert.Equal(1UL, error.CurrentRevision); return false; }
        }));
        Assert.Single(results, x => x);
        Assert.Single(database.ReadPendingEvents());
        await Assert.ThrowsAsync<ScoreCatalogConflictException>(() => first.RefreshAsync(Scope, 0, [], ScoreRefreshStatus.Complete));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RefreshAsync(Scope, 1, [], ScoreRefreshStatus.Complete, cancellation.Token));
        Assert.Single(first.Read(Scope)!.Entries);
        Assert.Equal(1UL, first.Read(Scope)!.Revision);
        Assert.Single(database.ReadPendingEvents());
    }

    [Fact]
    public async Task CatalogNotificationIsTypedAndAvailableThroughFilteredEventSnapshots()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations);
        var store = new ScoreCatalogStore(database, "host");
        await store.RefreshAsync(Scope, 0, [new("title", "id")], ScoreRefreshStatus.Complete);
        var hub = new StateEventHub(database, "host");
        Assert.Equal(1, await database.DispatchPendingAsync(hub));
        var owner = new AuthSession("owner", "owner", [ApiPermissions.SystemRead], null, null, true);
        var snapshot = await hub.SnapshotAsync(owner, EventFilter.Create(["music.catalog.changed"], [Scope.Key]));
        var resource = Assert.Single(snapshot.Resources);
        Assert.Equal(1UL, resource.ResourceRevision.Value);
        var data = resource.Data.Deserialize<MusicCatalogChanged>(ContractJson.Options)!;
        Assert.Equal(Scope.Key, data.ScopeKey);
        Assert.Equal("complete", data.Status);
        Assert.Equal(1, data.EntryCount);
        Assert.Equal(1UL, data.Revision.Value);
    }

    [Fact]
    public async Task OutboxFailureRollsBackCatalogAndScopeCapacityRejectsWithoutMutation()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, ScoreCatalogStore.Migrations);
        var store = new ScoreCatalogStore(database, "host");
        await database.WriteAsync(tx => SqliteStore.AppendEvent(tx, new("music-catalog-" + Scope.Key + "-1",
            "music-catalog", Scope.Key, new(1), "music.catalog.changed", JsonSerializer.SerializeToElement(new { different = true }))));
        var error = await Assert.ThrowsAsync<StorageException>(() => store.RefreshAsync(Scope, 0, [new("x")], ScoreRefreshStatus.Complete));
        Assert.Equal("storage.eventConflict", error.Code);
        Assert.Null(store.Read(Scope));
        Assert.Single(database.ReadPendingEvents());
        await database.WriteAsync(tx => tx.Execute("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<256)
            INSERT INTO music_library_catalogs SELECT 'capacity-fixture-'||x,'1','{}' FROM n
            """));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RefreshAsync(Scope, 0, [], ScoreRefreshStatus.Complete));
        Assert.Null(store.Read(Scope));
        Assert.Equal(256L, database.ReadScalar("SELECT count(*) FROM music_library_catalogs"));
        Assert.Single(database.ReadPendingEvents());
    }

    [Fact]
    public void HistoricalCapacityIsBoundedWithoutDeletingOldReferences()
    {
        var source = Enumerable.Range(0, ScoreCatalog.MaximumEntries).Select(i => new ScoreSourceItem("title", "id-" + i)).ToArray();
        var full = Refresh(null, source);
        Assert.Equal(ScoreCatalog.MaximumEntries, full.Entries.Count);
        Assert.Throws<ArgumentException>(() => Refresh(full, new ScoreSourceItem("new", "another-id")));
        Assert.All(full.Entries, x => Assert.Equal(ScoreAvailability.Present, x.Availability));
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string root;
        public Workspace()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
            root = Path.Combine(repository!.FullName, "artifacts", "tmp", "music-catalog-tests", Guid.NewGuid().ToString("N"));
        }
        public DataRootLease Lease() => DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, Path.Combine(root, "data")));
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
