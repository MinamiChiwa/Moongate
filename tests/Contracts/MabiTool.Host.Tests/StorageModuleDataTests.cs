using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Color.Domain;
using MabiTool.Modules.Color.Persistence;
using MabiTool.Modules.MusicLibrary.Persistence;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.Rooms.Persistence;
using MabiTool.Modules.CombatObservation.Adapters;
using MabiTool.Modules.CombatObservation.Domain;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class StorageModuleDataTests
{
    [Fact]
    public async Task SharedBlobSurvivesDeletionAndOtherModuleReferencesBlockIt()
    {
        await using var area = new Area(); await area.SetEnabled("color", false);
        var color = await area.Asset("color", "same-content"); var music = await area.Asset("music-library", "same-content");
        Assert.Equal(color.Sha256, music.Sha256); Assert.NotEqual(color.Id, music.Id);
        await area.Assets.AddModuleReferenceAsync("music-library", "playlist-1", color.Id);
        var blocked = await area.Plans().PreviewAsync("owner", new("color"), "blocked-preview");
        Assert.Equal(new StorageModuleReference("music-library", "playlist-1", color.Id), Assert.Single(blocked.Data.References));
        Assert.Contains("storage.assetInUse", blocked.Data.BlockingReasons);
        Assert.Equal("storage.assetInUse", (await Assert.ThrowsAsync<OperationException>(() => area.Plans().CheckAsync(blocked.Id, "owner", StorageModuleDataStore.ETag(blocked)))).Code);
        Assert.Equal("storage.assetInUse", (await Assert.ThrowsAsync<StorageException>(() => area.Assets.RemoveAsync(color.Id))).Code);
        await area.Assets.RemoveModuleReferenceAsync("music-library", "playlist-1", color.Id);
        var result = await area.Delete("color");
        Assert.Equal(1UL, result.RemovedAssets.Value); Assert.Null(area.Assets.GetMetadata(color.Id));
        await area.Assets.CollectGarbageAsync();
        Assert.Equal("same-content", Encoding.UTF8.GetString(await area.Assets.ReadRangeAsync(music.Id, 0, 100, 65536)));
    }

    [Theory]
    [InlineData("data", "storage.planStale")]
    [InlineData("configuration", "storage.planStale")]
    [InlineData("epoch", "storage.planStale")]
    [InlineData("expired", "storage.planExpired")]
    [InlineData("etag", "revision.stale")]
    [InlineData("missing-etag", "revision.required")]
    [InlineData("owner", "resource.notFound")]
    public async Task PreviewBindsDataConfigurationEpochLifetimeSubjectAndETag(string change, string code)
    {
        await using var area = new Area(); await area.SetEnabled("color", false);
        var store = area.Plans(); var plan = await store.PreviewAsync("owner", new("color"), "original-preview");
        if (change == "data") await area.Asset("color", "new-data");
        if (change == "configuration") await area.SetEnabled("color", false);
        if (change == "epoch") store = area.Plans("new-epoch");
        if (change == "expired") area.Clock.Advance(StorageModuleDataStore.PreviewLifetime);
        var etag = change == "etag" ? "\"old\"" : change == "missing-etag" ? null : StorageModuleDataStore.ETag(plan);
        Assert.Equal(code, (await Assert.ThrowsAsync<OperationException>(() => store.CheckAsync(plan.Id, change == "owner" ? "other" : "owner", etag))).Code);
    }

    [Fact]
    public async Task PreviewIsBoundedIdempotentAndShowsEnabledModuleProtection()
    {
        await using var area = new Area(); var store = area.Plans();
        var first = await store.PreviewAsync("owner", new("color"), "first-preview");
        Assert.Contains("storage.moduleEnabled", first.Data.BlockingReasons);
        Assert.Equal("storage.moduleEnabled", (await Assert.ThrowsAsync<OperationException>(() => store.CheckAsync(first.Id, "owner", StorageModuleDataStore.ETag(first)))).Code);
        var copies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.PreviewAsync("owner", new("color"), "first-preview")));
        Assert.All(copies, x => Assert.Equal(first.Id, x.Id));
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() => store.PreviewAsync("owner", new("music-library"), "first-preview"))).Code);
        for (var i = 1; i < StorageModuleDataStore.MaximumPendingPlans; i++) await store.PreviewAsync("owner", new("color"), "bounded-preview-" + i);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => store.PreviewAsync("owner", new("color"), "overflow-preview"))).Code);
        area.Clock.Advance(StorageModuleDataStore.PreviewLifetime);
        Assert.NotEqual(first.Id, (await store.PreviewAsync("owner", new("color"), "first-preview")).Id);
    }

    [Fact]
    public async Task ActiveAssetPinIsRecheckedBeforeCommit()
    {
        await using var area = new Area(); await area.SetEnabled("color", false);
        var asset = await area.Asset("color", "pinned"); var store = area.Plans();
        var plan = await store.PreviewAsync("owner", new("color"), "pin-preview");
        var pin = await area.Assets.PinAsync(asset.Id, "reading");
        Assert.Equal("storage.assetInUse", (await Assert.ThrowsAsync<OperationException>(() => store.CheckAsync(plan.Id, "owner", StorageModuleDataStore.ETag(plan)))).Code);
        await area.Assets.UnpinAsync(pin);
        Assert.Equal(plan.Id, (await store.CheckAsync(plan.Id, "owner", StorageModuleDataStore.ETag(plan))).Plan.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerOrPlatformFailureRollsBackContentAssetsHistoryAndReceipt(bool platformFailure)
    {
        await using var area = new Area(); await area.SetEnabled("color", false);
        var library = new ColorLibraryStore(area.Database); var profile = ColorProfiles.Default with { Id = "saved-profile", Name = "保留方案" };
        await library.EditAsync("owner", new([profile], [], null), "saved-profile-key", ColorLibraryStore.ETag(new(0)));
        var asset = await area.Asset("color", "saved-color-evidence");
        var history = new OperationSnapshot("historical-color", "color.settings.import", "owner", [], OperationState.Succeeded, "succeeded", new(3), 1,
            new(area.Clock.GetUtcNow(), area.Clock.GetUtcNow(), area.Clock.GetUtcNow()), JsonSerializer.SerializeToElement(new { evidence = "preserved" }), null, new(CancellationState.None, null), EffectStatus.None);
        await area.Database.WriteAsync(tx => { SqliteStore.SaveOperation(tx, history); return 0; });
        var participant = ColorStorageData.Owner(area.Database);
        var failing = new StorageModuleDataOwner(participant.Definition, () =>
        {
            var deletion = participant.PrepareDeletion();
            return new(tx => { deletion.Apply(tx); if (!platformFailure) throw new InvalidOperationException("owner failure"); });
        }, participant.OperationKinds, participant.EventTypes);
        var store = area.Plans(participant: failing, reset: platformFailure ? (_, _) => throw new InvalidOperationException("platform failure") : null);
        var plan = await store.PreviewAsync("owner", new("color"), "rollback-preview");
        var record = await store.CheckAsync(plan.Id, "owner", StorageModuleDataStore.ETag(plan));
        var operation = await area.Admit(plan); using var maintenance = area.Database.Activities.TryEnterMaintenance(operation.Id)!;
        Assert.NotNull(maintenance); await area.Operations.BeginStorageAsync(operation.Id, "applying", maintenance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyAsync(record, operation.Id, maintenance));
        Assert.Equal(1UL, library.Read().Revision.Value); Assert.Equal(profile.Id, Assert.Single(library.Read().Profiles).Id);
        Assert.NotNull(area.Assets.GetMetadata(asset.Id)); Assert.Equal(history.Id, area.Operations.Get(history.Id, "owner", true).Id);
        Assert.Equal("planned", store.Get(plan.Id, "owner").State);
        Assert.Equal(OperationState.Running, area.Operations.Get(operation.Id, "owner", true).State.KnownValue);
        Assert.Equal(0L, area.Database.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='storage.module-data.changed'"));
    }

    [Fact]
    public async Task MusicDeletionKeepsMonotonicLibraryAndPlaybackHistoryRevisions()
    {
        await using var area = new Area(); await area.SetEnabled("music-library", false);
        var library = new MusicLibraryStore(area.Database, "epoch", area.Clock);
        var edit = new MusicEditBatch([new("folder", null, "音乐", "1")], [], [], [], null);
        await library.EditAsync("owner", edit, "music-first-edit", MusicLibraryStore.ETag(new(0)));
        var history = new PlaybackHistoryStore(area.Database, area.Clock);
        await history.ImportAsync("owner", new("old-record", "score-id", null, null, area.Clock.GetUtcNow(), 1000), "first-history");
        var performance = new PerformanceSessionStore(area.Database, area.Clock);
        var performanceState = new PerformanceQueueStateMachine().Create("performance-delete", "owner", "solo", "game-1", [], false, PerformanceRepeatMode.Off);
        await performance.CreateIdempotentAsync(performanceState, "performance-create:shared-key", new string('A', 64));
        Assert.Equal(2L, area.Database.ReadScalar("SELECT count(*) FROM music_library_receipts"));
        await area.Delete("music-library"); Assert.Empty(library.Read().Folders); Assert.Empty(history.Recent("owner"));
        Assert.Equal(0L, area.Database.ReadScalar("SELECT count(*) FROM music_library_receipts"));
        Assert.Equal(2UL, library.Read().Revision.Value);
        await history.ImportAsync("owner", new("new-record", "score-id", null, null, area.Clock.GetUtcNow(), 1000), "second-history");
        Assert.Equal(2L, area.Database.ReadScalar("SELECT revision FROM music_playback_history WHERE id='new-record'"));
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() => library.EditAsync("owner", edit, "old-etag-edit", MusicLibraryStore.ETag(new(1))))).Code);
        Assert.Equal(3UL, (await library.EditAsync("owner", edit, "music-next-edit", MusicLibraryStore.ETag(new(2)))).Revision.Value);
    }

    [Fact]
    public async Task RoomPreferencesResetThroughTheirOwnerAndContinueTheirRevision()
    {
        await using var area = new Area(); var preferences = new RoomPreferenceStore(area.Database);
        var imported = preferences.PrepareImport("owner", new(0), RoomPreferences.Default with { Nickname = "保留昵称", TimingOffsetMs = 25 });
        await area.Database.WriteAsync(tx => { preferences.CommitImport(tx, imported); return 0; });
        await area.SetEnabled("rooms", false); await area.Delete("rooms");
        var value = preferences.Read("owner"); Assert.Equal("", value.Nickname); Assert.Equal(0, value.TimingOffsetMs); Assert.Equal(2UL, value.Revision.Value);
        Assert.True(value.RequiresGameBinding);
    }

    [Fact]
    public async Task SavedTailHoldEqualToTheOldDefaultBecomesZeroOnce()
    {
        // 尾音保留缺省值由 1 秒改为 0 秒：恰为旧缺省 1000 ms 的保存值改为 0，其他值不动；迁移只执行一次，之后再存 1000 保留。
        var root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "tail-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task Use(IReadOnlyList<DatabaseMigration> migrations, Func<SqliteStore, RoomPreferenceStore, Task> work)
        {
            using var lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data")));
            await using var database = SqliteStore.Open(lease, migrations);
            await work(database, new RoomPreferenceStore(database));
        }
        async Task Save(SqliteStore database, RoomPreferenceStore store, string owner, int tail)
        {
            var import = store.PrepareImport(owner, store.Read(owner).Revision, RoomPreferences.Default with { MusicTailHoldMs = tail });
            await database.WriteAsync(tx => { store.CommitImport(tx, import); return 0; });
        }
        try
        {
            Assert.Equal(0, RoomPreferences.Default.MusicTailHoldMs);
            await Use(BackendApplication.DatabaseMigrations.Where(migration => migration != RoomPreferenceStore.TailDefaultMigration).ToArray(),
                async (database, store) => { await Save(database, store, "old-default", 1000); await Save(database, store, "custom", 1500); });
            await Use(BackendApplication.DatabaseMigrations, async (database, store) =>
            {
                Assert.Equal(0, store.Read("old-default").MusicTailHoldMs);
                Assert.Equal(1500, store.Read("custom").MusicTailHoldMs);
                await Save(database, store, "old-default", 1000);
            });
            await Use(BackendApplication.DatabaseMigrations, (_, store) =>
            {
                Assert.Equal(1000, store.Read("old-default").MusicTailHoldMs);
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CombatOwnerClearsHistoryEntitiesAndWorkspaceWithIncreasingRevision()
    {
        await using var area = new Area(); var history = new CombatHistoryStore(area.Database, area.Operations, "epoch", area.Clock);
        var source = new ObservationSource("normalized-test", "1.0", "TW", "fixture-v1");
        var self = new EntityKey("game-test", new(1), new(9007199254740993), new(1));
        var monster = self with { RawInstanceId = new(9007199254740995) }; var request = new CombatObservationRequest(self.GameSessionId);
        var admitted = await area.Operations.AdmitCombatAsync("owner", request, "combat-module-seed");
        var session = new CombatSession("owner", admitted.Snapshot.Id, source,
            new(self, "測試角色", "測試角色", CombatEntityRole.LocalPlayer, null, false, true, ObservationQuality.Confirmed), request, area.Clock.GetUtcNow());
        var initial = session.Capture(); await history.SaveAsync(initial); session.Acknowledge(initial);
        session.Apply(new(1, source, self.GameSessionId, self.ObservationGeneration, new(1), area.Clock.GetUtcNow(), 0,
            [new(monster, "同名目标", "同名目标", CombatEntityRole.Monster, null, false, true, ObservationQuality.Confirmed)],
            [new("attack", source, self, monster, new(1000), new(100), CombatDamageKind.ActualHp, area.Clock.GetUtcNow(), 0, new(1), 0, null, null, ObservationQuality.Confirmed)], [], [], []));
        session.Stop("source-end", area.Clock.GetUtcNow().AddSeconds(1), 1000, false);
        var final = session.Capture(); await history.SaveAsync(final, OperationState.Succeeded);
        var encounter = history.Encounter(final.Workspace.Encounter!.Id, "owner", true); Assert.Equal("100", encounter.Summary.AttributedDamage.ToString());
        var revision = history.HistoryRevision(); await area.SetEnabled("combat-observation", false); await area.Delete("combat-observation");
        Assert.Null(history.Workspace("owner", true));
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => history.Encounter(encounter.Id, "owner", true)).Code);
        Assert.Equal(revision.Value + 1, history.HistoryRevision().Value);
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => area.Operations.Get(admitted.Snapshot.Id, "owner", true)).Code);
    }

    [Fact]
    public async Task ApplyingCancellationCompletesAfterTheAtomicDeletion()
    {
        await using var area = new Area(); await area.SetEnabled("color", false); var asset = await area.Asset("color", "delete-me");
        var store = area.Plans(); var plan = await store.PreviewAsync("owner", new("color"), "deferred-preview");
        var record = await store.CheckAsync(plan.Id, "owner", StorageModuleDataStore.ETag(plan)); var operation = await area.Admit(plan);
        using var maintenance = area.Database.Activities.TryEnterMaintenance(operation.Id)!;
        await area.Operations.BeginStorageAsync(operation.Id, "applying", maintenance);
        var cancelled = await area.Operations.CancelStorageAsync(operation.Id, "owner", true, "late-cancel", maintenance);
        Assert.Equal(CancellationState.Deferred, cancelled.Cancellation.State.KnownValue);
        var result = await store.ApplyAsync(record, operation.Id, maintenance);
        Assert.Equal(OperationState.Succeeded, result.State.KnownValue); Assert.Equal(CancellationState.Completed, result.Cancellation.State.KnownValue);
        Assert.Null(area.Assets.GetMetadata(asset.Id)); Assert.Equal("completed", store.Get(plan.Id, "owner").State);
    }

    private sealed class Area : IAsyncDisposable
    {
        private readonly string root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "module-data-" + Guid.NewGuid().ToString("N"));
        private readonly DataRootLease lease;
        public StorageClock Clock { get; } = new();
        public SqliteStore Database { get; }
        public AssetStore Assets { get; }
        public OperationStore Operations { get; }
        public Area()
        {
            Directory.CreateDirectory(root); lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data")));
            Database = SqliteStore.Open(lease, BackendApplication.DatabaseMigrations, clock: Clock); Assets = new(Database, lease.Paths, Clock); Operations = new(Database, Clock);
        }
        public StorageModuleDataStore Plans(string epoch = "epoch", IStorageModuleDataOwner? participant = null, Action<DatabaseTransaction, string>? reset = null) =>
            new(Database, Assets, new(Database, lease.Paths, BackendApplication.StorageData), Operations,
                participant is null ? [ColorStorageData.Owner(Database), MusicStorageData.Owner(Database), RoomStorageData.Owner(Database), CombatStorageData.Owner(Database)] : [participant],
                id => Operations.Configuration(id, true), epoch, Clock, reset);
        public async Task SetEnabled(string id, bool enabled)
        {
            var config = Operations.Configuration(id, true);
            var operation = await Operations.AdmitConfigurationAsync("owner", id, true, new(enabled), Guid.NewGuid().ToString("N"), OperationStore.ConfigurationETag(config));
            await Operations.BeginConfigurationAsync(operation.Snapshot.Id); await Operations.FinishConfigurationAsync(operation.Snapshot.Id, enabled);
        }
        public Task<AssetSnapshot> Asset(string module, string value) => Assets.PutAsync(module, "saved", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(value)));
        public async Task<OperationSnapshot> Admit(StorageModuleDeletionPlan plan)
        {
            var request = JsonSerializer.SerializeToElement(new { planId = plan.Id });
            return (await Operations.AdmitStorageAsync("owner", "storage.moduleDelete", plan.Id, StorageMaintenancePlanStore.Hash(request), "execute-" + plan.Id, request, true)).Snapshot;
        }
        public async Task<StorageModuleDeletionResult> Delete(string module)
        {
            var store = Plans(); var plan = await store.PreviewAsync("owner", new(module), Guid.NewGuid().ToString("N"));
            var record = await store.CheckAsync(plan.Id, "owner", StorageModuleDataStore.ETag(plan)); var operation = await Admit(plan);
            using var maintenance = Database.Activities.TryEnterMaintenance(operation.Id)!; Assert.NotNull(maintenance);
            await Operations.BeginStorageAsync(operation.Id, "applying", maintenance);
            return (await store.ApplyAsync(record, operation.Id, maintenance)).Result!.Value.Deserialize<StorageModuleDeletionResult>(ContractJson.Options)!;
        }
        public async ValueTask DisposeAsync()
        {
            await Assets.DisposeAsync(); await Database.DisposeAsync(); lease.Dispose();
            Assert.StartsWith(Path.GetFullPath(Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar,
                Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); Directory.Delete(root, true);
        }
    }
}
