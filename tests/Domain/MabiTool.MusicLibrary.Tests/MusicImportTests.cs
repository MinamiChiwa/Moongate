using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Diagnostics;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using Xunit;

namespace MabiTool.MusicLibrary.Tests;

[Collection(MusicLibraryTestCollection.Name)]
public sealed class MusicImportTests
{
    private static MusicImportRequest Full()
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mobifolio-import.v1.json")));
        return Request(document.RootElement.EnumerateObject().Select(x => File(x.Name, x.Value.GetRawText())).ToArray());
    }
    private static MusicImportRequest Request(params ImportSourceFile[] files) => new("legacy-backup", "舊曲庫", "TW", "realm", new(ulong.MaxValue), files);
    private static ImportSourceFile File(string name, string json) => new(name, Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
    private static MusicImportRequest Settings() => Request(File("settings.json", "{\"gap_sec\":3}"));
    private static string ETag(MusicImportPreview preview) => MusicLibraryStore.ETag(preview.ExpectedLibraryRevision);

    [Fact]
    public async Task SevenFilesPreserveOriginalBytesMappingsEstimatesAndUnknownReferences()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var store = new MusicImportStore(database, "epoch", new Clock());
        var request = Full();
        var originals = request.Files.Select(x => (Path: Path.Combine(workspace.Originals, x.Name), Bytes: Convert.FromBase64String(x.ContentBase64))).ToArray();
        foreach (var original in originals) System.IO.File.WriteAllBytes(original.Path, original.Bytes);
        var preview = await store.PreviewAsync("owner-a", request, "preview-first");
        Assert.Equal("ready", preview.State);
        Assert.Equal(7, preview.Files.Count);
        Assert.Equal(5, preview.Counts.Scores);
        Assert.Equal(2, preview.Counts.UnresolvedReferences);
        Assert.Equal(3, preview.Counts.Durations);
        Assert.Equal(0UL, new MusicLibraryStore(database, "epoch").Read().Revision.Value);
        Assert.Empty(new PlaybackHistoryStore(database).Recent("owner-a"));
        var first = Assert.Single(preview.Mappings, x => x.Kind == "score" && x.OldId == "夕陽#1").NewId!;
        var second = Assert.Single(preview.Mappings, x => x.Kind == "score" && x.OldId == "夕陽#2").NewId!;
        Assert.NotEqual(first, second);
        var unresolved = Assert.Single(preview.Mappings, x => x.Kind == "score" && x.OldId == "夕陽");
        Assert.Equal("title-only", unresolved.Association);
        Assert.Equal(new[] { first, second }, unresolved.CandidateIds);
        Assert.NotEqual(first, unresolved.NewId);
        Assert.Equal("鋼琴 ", preview.Instruments[0].RawName);
        Assert.Equal("piano", preview.Instruments[0].InstrumentId);
        Assert.True(preview.Instruments[0].Original.GetProperty("IsEquipped").GetBoolean());
        Assert.Null(preview.Instruments[1].InstrumentId);
        var receipt = await store.CommitAsync("owner-a", preview.Id, "commit-first", ETag(preview));
        Assert.Equal(1UL, receipt.LibraryRevision.Value);
        var library = new MusicLibraryStore(database, "epoch");
        var state = library.Read();
        var list = Assert.Single(state.Playlists);
        Assert.Equal("第一行\n第二行", list.Notes);
        Assert.Equal(new[] { first, second, unresolved.NewId }, list.Items.Take(3).Select(x => x.ScoreId));
        Assert.Equal(new[] { "piano", "violin", null, null }, list.Items.Select(x => x.InstrumentId));
        Assert.Equal("收藏", state.Folders.Single(x => x.Id == state.Folders.Single(f => f.Id == list.FolderId).ParentId).Name);
        Assert.Equal(2, state.NameSettings.Assignments.Count);
        var source = Assert.Single(library.Info().Sources);
        Assert.Equal("imported-library", source.Kind);
        Assert.Equal(ulong.MaxValue.ToString(), source.CharacterId);
        var page = library.Query(new(MusicCollection.Scores, null, null, source.ScopeKey, "", 200, null));
        Assert.All(page.Scores, x => Assert.Equal("none", Assert.Single(x.SourceRefs).LocatorCapability));
        Assert.Equal("imported", page.Scores.Single(x => x.Id == first).Availability);
        Assert.Equal("uncertain", page.Scores.Single(x => x.Id == unresolved.NewId).Availability);
        var timeline = new ScoreTimelineStore(database).Resolve("owner-a", first, null);
        Assert.Equal(31_250, timeline.FullCycleMs);
        Assert.Equal(ObservationQuality.Estimated, timeline.Quality);
        Assert.False(ScoreTimelinePolicy.EvaluateStop("solo", timeline, true, 100_000).CanStop);
        var history = new PlaybackHistoryStore(database).Recent("owner-a");
        Assert.Equal(2, history.Count);
        Assert.All(history, x => Assert.Equal(TimelineEvidenceKind.ImportedHistoryEstimate, x.EvidenceKind));
        Assert.Empty(new PlaybackHistoryStore(database).Recent("owner-b"));
        Assert.Equal(4.5, store.Preferences("owner-a").GapSeconds);
        Assert.Equal(3, store.Preferences("owner-a").AdvanceMargin);
        Assert.Equal(125, store.Preferences("owner-a").PresentationPreferences.GetProperty("ov_scale").GetInt32());
        Assert.Equal(MusicPreferences.Default.GapSeconds, store.Preferences("owner-b").GapSeconds);
        Assert.Equal("committed", store.Get("owner-a", preview.Id).State);
        Assert.Equal("{}", database.ReadScalar("SELECT plan FROM music_imports WHERE id=$id", ("$id", preview.Id)));
        foreach (var original in originals) Assert.Equal(original.Bytes, System.IO.File.ReadAllBytes(original.Path));
    }

    [Fact]
    public async Task RepeatedImportsReturnOriginalReceiptAcrossRevisionsAndRestart()
    {
        using var workspace = new Workspace();
        MusicImportPreview preview; MusicImportReceipt receipt;
        using (var lease = workspace.Lease())
        await using (var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations))
        {
            var store = new MusicImportStore(database, "epoch", new Clock());
            preview = await store.PreviewAsync("owner", Full(), "preview-repeat");
            Assert.Equal(preview.Id, (await store.PreviewAsync("owner", Full(), "preview-repeat")).Id);
            receipt = await store.CommitAsync("owner", preview.Id, "commit-repeat", ETag(preview));
            var before = database.ReadPendingEvents().Count;
            Assert.Equal(receipt, await store.CommitAsync("owner", preview.Id, "commit-repeat", ETag(preview)));
            Assert.Equal(receipt, await store.CommitAsync("owner", preview.Id, "commit-new-key", ETag(preview)));
            Assert.Equal(preview.Id, (await store.PreviewAsync("owner", Full(), "preview-new-key")).Id);
            Assert.Equal(before, database.ReadPendingEvents().Count);
            var conflict = await Assert.ThrowsAsync<OperationException>(() => store.CommitAsync("owner", preview.Id, "commit-new-key", MusicLibraryStore.ETag(new(1))));
            Assert.Equal("idempotency.conflict", conflict.Code);
        }
        using (var lease = workspace.Lease())
        await using (var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations))
        {
            var clock = new Clock(); clock.Advance(TimeSpan.FromDays(2));
            var store = new MusicImportStore(database, "new-epoch", clock);
            Assert.Equal(receipt, store.Receipt("owner", preview.Id));
            Assert.Equal(receipt, await store.CommitAsync("owner", preview.Id, "restart-commit", ETag(preview)));
            Assert.Equal(preview.Id, (await store.PreviewAsync("owner", Full(), "restart-preview")).Id);
            Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM music_library_catalogs"));
            Assert.Equal(2L, database.ReadScalar("SELECT count(*) FROM music_playback_history"));
        }
    }

    [Fact]
    public async Task OutboxFailureRollsBackAllImportedDataAndCanBeRetried()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var store = new MusicImportStore(database, "epoch", new Clock());
        var preview = await store.PreviewAsync("owner", Full(), "rollback-preview");
        await database.WriteAsync(tx => SqliteStore.AppendEvent(tx, new("music-library-1", "music-library", "music-library", new(1),
            "music.library.changed", JsonSerializer.SerializeToElement(new { collision = true }))));
        var error = await Assert.ThrowsAsync<StorageException>(() => store.CommitAsync("owner", preview.Id, "rollback-commit", ETag(preview)));
        Assert.Equal("storage.eventConflict", error.Code);
        foreach (var table in new[] { "music_library_workspace", "music_library_catalogs", "music_playback_history", "music_score_timeline_evidence", "music_import_preferences" })
            Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM " + table));
        Assert.Null(store.Receipt("owner", preview.Id));
        Assert.Equal("ready", store.Get("owner", preview.Id).State);
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM core_outbox"));
        await database.WriteAsync(tx => tx.Execute("DELETE FROM core_outbox WHERE event_id='music-library-1'"));
        Assert.Equal(1UL, (await store.CommitAsync("owner", preview.Id, "rollback-commit", ETag(preview))).LibraryRevision.Value);
    }

    [Fact]
    public async Task EditsHistoryChangesExpirationAndRestartRejectOldPreviews()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var clock = new Clock(); var store = new MusicImportStore(database, "epoch", clock);
        var preview = await store.PreviewAsync("owner", Settings(), "stale-preview");
        await new PlaybackHistoryStore(database, clock).ImportAsync("owner", new("h", "score", null, null, clock.GetUtcNow(), 1000), "history-change");
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() => store.CommitAsync("owner", preview.Id, "stale-commit", ETag(preview)))).Code);
        var fresh = await store.PreviewAsync("owner", Settings(), "fresh-preview");
        await new MusicLibraryStore(database, "epoch", clock).EditAsync("editor", new([new("f", null, "f", "0")], [], [], [], null), "other-editor", ETag(fresh));
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() => store.CommitAsync("owner", fresh.Id, "fresh-commit", ETag(fresh)))).Code);
        var third = await store.PreviewAsync("owner", Settings(), "third-preview");
        Assert.Equal("import.previewExpired", (await Assert.ThrowsAsync<OperationException>(() => new MusicImportStore(database, "new-epoch", clock)
            .CommitAsync("owner", third.Id, "epoch-commit", ETag(third)))).Code);
        clock.Advance(MobiFolioImport.PreviewLifetime);
        Assert.Equal("import.previewExpired", (await Assert.ThrowsAsync<OperationException>(() => store.CommitAsync("owner", third.Id, "expired-commit", ETag(third)))).Code);
        Assert.Null(store.Receipt("owner", preview.Id));
    }

    [Fact]
    public async Task ArtistConflictRequiresExplicitMappingAndRetainsOriginalName()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        await new MusicLibraryStore(database, "epoch").EditAsync("owner", new([], [], [], [], new([new("existing-artist", "IU", [])], new Dictionary<string, string>(), [])), "artist-seed", MusicLibraryStore.ETag(new(0)));
        var store = new MusicImportStore(database, "epoch", new Clock());
        var preview = await store.PreviewAsync("owner", Full(), "artist-preview");
        Assert.Equal("conflict", preview.State);
        Assert.Contains(preview.Issues, x => x.Code == "import.artistConflict");
        Assert.Equal("import.conflict", (await Assert.ThrowsAsync<OperationException>(() => store.CommitAsync("owner", preview.Id, "artist-commit", ETag(preview)))).Code);
        var mapped = await store.PreviewAsync("owner", Full() with { ArtistMappings = new Dictionary<string, string> { ["artist-old"] = "existing-artist" } }, "mapped-preview");
        Assert.Equal("ready", mapped.State);
        var mapping = Assert.Single(mapped.Mappings, x => x.Kind == "artist");
        Assert.Equal("IU", mapping.RawTitle); Assert.Equal("existing-artist", mapping.NewId); Assert.Equal("explicit-mapping", mapping.Association);
        await store.CommitAsync("owner", mapped.Id, "mapped-commit", ETag(mapped));
        Assert.Single(new MusicLibraryStore(database, "epoch").Read().NameSettings.Artists);
        Assert.All(new MusicLibraryStore(database, "epoch").Read().NameSettings.Assignments.Values, x => Assert.Equal("existing-artist", x));
    }

    [Theory]
    [InlineData("../settings.json", "{}")]
    [InlineData("settings.JSON", "{}")]
    [InlineData("settings.json", "{\"gap_sec\":1,\"gap_sec\":2}")]
    [InlineData("settings.json", "{\"a\":1,\"\\u0061\":2}")]
    [InlineData("settings.json", "{\"nested\":{\"a\":1,\"a\":2}}")]
    [InlineData("settings.json", "{\"gap_sec\":NaN}")]
    [InlineData("scores.json", "{\"items\":[{}]}")]
    [InlineData("durations.json", "{\"x\":0}")]
    [InlineData("durations.json", "{\"x\":-1}")]
    [InlineData("durations.json", "{\"x\":1e999}")]
    public async Task InvalidInputNeverCreatesPreview(string file, string json)
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var error = await Assert.ThrowsAsync<OperationException>(() => new MusicImportStore(database, "epoch", new Clock()).PreviewAsync("owner", Request(File(file, json)), "invalid-preview"));
        Assert.Equal("import.invalidInput", error.Code);
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM music_imports"));
    }

    [Fact]
    public async Task ByteCountFileCountEntryCountUnicodeAndCancellationAreBounded()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var store = new MusicImportStore(database, "epoch", new Clock());
        var huge = Request(new ImportSourceFile("settings.json", Convert.ToBase64String(new byte[MobiFolioImport.MaximumSourceBytes + 1])));
        Assert.Equal("request.bodyTooLarge", (await Assert.ThrowsAsync<OperationException>(() => store.PreviewAsync("owner", huge, "oversized-preview"))).Code);
        var invalidUtf8 = Request(new ImportSourceFile("settings.json", Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0x7B, 0x7D })));
        Assert.Equal("import.invalidInput", (await Assert.ThrowsAsync<OperationException>(() => store.PreviewAsync("owner", invalidUtf8, "unicode-preview"))).Code);
        Assert.Equal("import.invalidInput", (await Assert.ThrowsAsync<OperationException>(() => store.PreviewAsync("owner", Request(File("settings.json", "{}"), File("settings.json", "{}")), "duplicate-preview"))).Code);
        var many = Request(File("scores.json", JsonSerializer.Serialize(new { items = Enumerable.Repeat(new { Name = "x" }, 20_001) })));
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => store.PreviewAsync("owner", many, "many-items-preview"))).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PreviewAsync("owner", Settings(), "cancel-preview", cancellation.Token));
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM music_imports"));
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{}")).ToArray();
        Assert.Equal("ready", (await store.PreviewAsync("owner", Request(new ImportSourceFile("settings.json", Convert.ToBase64String(bom))), "bom-preview")).State);
    }

    [Fact]
    public async Task ImportsAboveEditorBatchLimitStillRespectLibraryAndPreviewCapacity()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var clock = new Clock(); var store = new MusicImportStore(database, "epoch", clock);
        var folders = Enumerable.Range(0, 129).Select(x => new { id = "f" + x, name = "目錄" + x, parent = (string?)null });
        var request = Request(File("playlists.json", JsonSerializer.Serialize(new { folders, playlists = Array.Empty<object>() })));
        var preview = await store.PreviewAsync("owner", request, "large-preview");
        await store.CommitAsync("owner", preview.Id, "large-commit", ETag(preview));
        Assert.Equal(129, new MusicLibraryStore(database, "epoch").Read().Folders.Count);
        for (var i = 0; i < MusicImportStore.MaximumPendingPerOwner; i++)
            await store.PreviewAsync("owner", Settings() with { SourceId = "pending-" + i }, "pending-preview-" + i);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => store.PreviewAsync("owner", Settings() with { SourceId = "overflow" }, "overflow-preview"))).Code);
        clock.Advance(MobiFolioImport.PreviewLifetime + TimeSpan.FromSeconds(1));
        await store.PreviewAsync("owner", Settings() with { SourceId = "after-expiry" }, "after-expiry-preview");
        Assert.Equal(2L, database.ReadScalar("SELECT count(*) FROM music_imports"));
    }

    [Fact]
    public async Task LegacyKeyCollisionUsesExplicitTitleAndNeverChoosesFirstCandidate()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var request = Request(File("scores.json", "{\"items\":[{\"Name\":\"A\"},{\"Name\":\"A\"},{\"Name\":\"A#1\"}]}"),
            File("playlists.json", "{\"folders\":[],\"playlists\":[{\"id\":\"p\",\"name\":\"p\",\"items\":[{\"key\":\"A#1\",\"title\":\"A\"},{\"key\":\"A#1\",\"title\":\"A#1\"},\"A\"]}]}"));
        var store = new MusicImportStore(database, "epoch", new Clock());
        var preview = await store.PreviewAsync("owner", request, "collision-preview");
        Assert.Equal(2, preview.Mappings.Count(x => x.Kind == "score" && x.OldId == "A#1" && x.Association == "legacy-key-collision"));
        await store.CommitAsync("owner", preview.Id, "collision-commit", ETag(preview));
        var items = Assert.Single(new MusicLibraryStore(database, "epoch").Read().Playlists).Items;
        Assert.Equal(3, items.Select(x => x.ScoreId).Distinct().Count());
    }

    [Fact]
    public async Task MaximumScoreAndHistoryBatchCommitsWithShortTransactionBudget()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var clock = new Clock(); var store = new MusicImportStore(database, "epoch", clock);
        var rows = Enumerable.Range(0, 20_000).Select(x => new { Name = "曲目" + x }).ToArray();
        var recent = Enumerable.Range(0, 2000).Select(x => new { key = "曲目" + x, title = "曲目" + x, ts = clock.GetUtcNow().ToUnixTimeSeconds() - x });
        var request = Request(File("scores.json", JsonSerializer.Serialize(new { items = rows })), File("recent.json", JsonSerializer.Serialize(recent)));
        var transactions = new List<SqliteWriteTiming>(); database.WriteTimingObserver = transactions.Add;
        var timer = Stopwatch.StartNew();
        var preview = await store.PreviewAsync("owner", request, "maximum-batch-preview");
        var previewMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
        var receipt = await store.CommitAsync("owner", preview.Id, "maximum-batch-commit", ETag(preview));
        var commitMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(20_000, receipt.Counts.Scores);
        Assert.Equal(2000L, database.ReadScalar("SELECT count(*) FROM music_playback_history WHERE owner='owner'"));
        Assert.Equal(4L, database.ReadScalar("SELECT count(*) FROM core_outbox"));
        var report = Path.Combine(workspace.Root, "artifacts", "reports", "music-import-capacity.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        System.IO.File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            measuredAt = DateTimeOffset.UtcNow, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
            scores = 20_000, history = 2000, previewEndToEndMs = previewMs, commitEndToEndMs = commitMs,
            shortTransactionBudgetMs = SqliteStore.TransactionBudget.TotalMilliseconds,
            transactionHardLimitMs = SqliteStore.TransactionHardLimit.TotalMilliseconds,
            transactions = transactions.Select(x => new { x.Callback, x.CallbackMs, x.CommitMs, x.SqlCommands, x.Outcome }),
            privateSnapshotBytes = database.ReadScalar("SELECT sum(length(CAST(preview AS BLOB))+length(CAST(plan AS BLOB))) FROM music_imports"),
            result = "committed"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task LegacySettingsCoercionRetainsPlaybackValuesAndBounds()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var store = new MusicImportStore(database, "epoch", new Clock());
        var source = Request(File("settings.json", "{\"gap_sec\":\"3.5\",\"advance_margin\":61,\"auto_sync\":\"yes\",\"stop_before_play\":0,\"ensemble\":\"off\"}"));
        var preview = await store.PreviewAsync("owner", source, "coercion-preview");
        Assert.Equal(3.5, preview.Preferences!.GapSeconds);
        Assert.Equal(30, preview.Preferences.AdvanceMargin);
        Assert.True(preview.Preferences.AutoSync);
        Assert.False(preview.Preferences.StopBeforePlay);
        Assert.False(preview.Preferences.Ensemble);
        Assert.Contains(preview.Issues, x => x.Code == "import.settingAdjusted" && x.OldId == "advance_margin");
        await store.CommitAsync("owner", preview.Id, "coercion-commit", ETag(preview));
        Assert.Equal(3.5, store.Preferences("owner").GapSeconds);
    }

    [Fact]
    public async Task PackedSnapshotsRejectCorruptionAndExcessiveExpansionAndReadPreviousJson()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var store = new MusicImportStore(database, "epoch", new Clock());
        var preview = await store.PreviewAsync("owner", Settings(), "packed-preview");
        var packed = (string)database.ReadScalar("SELECT preview FROM music_imports WHERE id=$id", ("$id", preview.Id))!;
        Assert.StartsWith("br1:", packed);
        await database.WriteAsync(tx => tx.Execute("UPDATE music_imports SET preview=$snapshot WHERE id=$id",
            ("$snapshot", "br1:" + new string('0', 64) + packed[68..]), ("$id", preview.Id)));
        Assert.Equal("storage.assetDamaged", Assert.Throws<StorageException>(() => store.Get("owner", preview.Id)).Code);
        var expanded = new byte[MusicImportStore.MaximumPreviewBytes + 1];
        using var compressed = new MemoryStream();
        using (var compressor = new BrotliStream(compressed, CompressionLevel.Fastest, true)) compressor.Write(expanded);
        var bomb = "br1:" + Convert.ToHexString(SHA256.HashData(expanded)) + ":" + Convert.ToBase64String(compressed.ToArray());
        await database.WriteAsync(tx => tx.Execute("UPDATE music_imports SET preview=$snapshot WHERE id=$id", ("$snapshot", bomb), ("$id", preview.Id)));
        Assert.Equal("storage.assetDamaged", Assert.Throws<StorageException>(() => store.Get("owner", preview.Id)).Code);
        await database.WriteAsync(tx => tx.Execute("UPDATE music_imports SET preview=$snapshot WHERE id=$id",
            ("$snapshot", JsonSerializer.Serialize(preview, ContractJson.Options)), ("$id", preview.Id)));
        Assert.Equal(preview.Id, store.Get("owner", preview.Id).Id);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string directory;
        public string Root { get; }
        public string Originals => Path.Combine(directory, "originals");
        public Workspace()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !System.IO.File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
            Root = root!.FullName;
            directory = Path.Combine(root!.FullName, "artifacts", "tmp", "music-import-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Originals);
        }
        public DataRootLease Lease() => DataRootLease.Open(new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
