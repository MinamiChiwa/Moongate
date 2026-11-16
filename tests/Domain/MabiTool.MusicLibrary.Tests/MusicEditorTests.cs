using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using Xunit;

namespace MabiTool.MusicLibrary.Tests;

[Collection(MusicLibraryTestCollection.Name)]
public sealed class MusicEditorTests
{
    private static MusicEditBatch Folder(string id = "f-one") => new([new(id, null, id, "1")], [], [], [], null);

    [Fact]
    public void PerformanceQueueAdvancesWithRepeatShuffleAndGapWithoutExternalCalls()
    {
        var machine = new PerformanceQueueStateMachine(7);
        var items = new[] { new PlaylistItem("i-a", "score-a", null, "1"), new PlaylistItem("i-b", "score-b", "violin", "2") };
        var now = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var state = machine.Create("performance-1", "client-a", "solo", "game-1", items, shuffle: true, PerformanceRepeatMode.All, gapSeconds: 2);
        state = machine.Dispatch(machine.Arm(state), now);
        Assert.Equal(PerformancePhase.Executing, state.Phase);
        var current = state.CurrentScoreId;
        state = machine.Playing(state);
        state = machine.ObserveStopped(state, PerformanceStopReason.NaturalEnd, now.AddSeconds(10));
        Assert.Equal(PerformancePhase.Waiting, state.Phase);
        Assert.Equal(now.AddSeconds(12), state.GapUntil);
        state = machine.Dispatch(state, state.GapUntil!.Value);
        Assert.NotEqual(current, state.CurrentScoreId);
        Assert.Equal(PerformancePhase.Executing, state.Phase);
    }

    [Fact]
    public void PerformanceQueueSkipsTransientFailuresAndStopsAfterBoundedFailures()
    {
        var machine = new PerformanceQueueStateMachine();
        var items = new[] { new PlaylistItem("i-a", "score-a", null, "1"), new PlaylistItem("i-b", "score-b", null, "2"), new PlaylistItem("i-c", "score-c", null, "3") };
        var now = DateTimeOffset.UtcNow;
        var state = machine.Dispatch(machine.Arm(machine.Create("p", "o", "solo", "g", items, false, PerformanceRepeatMode.Off, 0)), now);
        state = machine.MarkFailure(state, now);
        Assert.Equal(PerformancePhase.Executing, state.Phase);
        Assert.Equal("score-b", state.CurrentScoreId);
        state = machine.MarkFailure(state, now);
        Assert.Equal("score-c", state.CurrentScoreId);
        state = machine.MarkFailure(state, now);
        Assert.Equal(PerformancePhase.Failed, state.Phase);
    }

    [Fact]
    public void PerformanceQueueDistinguishesManualStopAndRejectsBusyQueueEdits()
    {
        var machine = new PerformanceQueueStateMachine();
        var now = DateTimeOffset.UtcNow;
        var state = machine.Dispatch(machine.Arm(machine.Create("p", "o", "solo", "g", [new("i", "score", null, "1")], false, PerformanceRepeatMode.Off, 0)), now);
        state = machine.Playing(state);
        state = machine.ObserveStopped(state, PerformanceStopReason.ManualStop, now.AddSeconds(1));
        Assert.Equal(PerformanceStopReason.ManualStop, state.StopReason);
        Assert.Equal(PerformancePhase.Stopped, state.Phase);
        Assert.Equal("performance.queueBusy", Assert.Throws<OperationException>(() => machine.ApplyQueueEdit(state with { Phase = PerformancePhase.Performing }, [])).Code);
    }

    [Fact]
    public void PerformanceStateChangesAlwaysAdvanceGeneration()
    {
        var machine = new PerformanceQueueStateMachine();
        var now = DateTimeOffset.UtcNow;
        var state = machine.Dispatch(machine.Arm(machine.Create("p-gen", "owner", "solo", "game", [new("i", "s", null, "1")], false, PerformanceRepeatMode.Off)), now);
        var playing = machine.Playing(state);
        Assert.Equal(state.Generation.Value + 1, playing.Generation.Value);
        var stopped = machine.ObserveStopped(playing, PerformanceStopReason.ManualStop, now.AddSeconds(1));
        Assert.Equal(playing.Generation.Value + 1, stopped.Generation.Value);
    }

    [Fact]
    public void TerminalFailureAndQueueExhaustionAdvanceGeneration()
    {
        var machine = new PerformanceQueueStateMachine();
        var now = DateTimeOffset.UtcNow;
        var failureState = machine.Dispatch(machine.Arm(machine.Create("p-failure", "owner", "solo", "game",
            [new("item-1", "score-1", null, "1"), new("item-2", "score-2", null, "2"),
                new("item-3", "score-3", null, "3")], false, PerformanceRepeatMode.Off, 0)), now);
        failureState = machine.MarkFailure(failureState, now);
        failureState = machine.MarkFailure(failureState, now);
        var beforeTerminalFailure = failureState.Generation.Value;
        failureState = machine.MarkFailure(failureState, now);
        Assert.Equal(PerformancePhase.Failed, failureState.Phase);
        Assert.Equal(beforeTerminalFailure + 1, failureState.Generation.Value);

        var exhausted = machine.Dispatch(machine.Arm(machine.Create("p-exhausted", "owner", "solo", "game",
            [new("item", "score", null, "1")], false, PerformanceRepeatMode.Off, 0)), now);
        var previousGeneration = exhausted.Generation.Value;
        exhausted = machine.ObserveStopped(exhausted, PerformanceStopReason.NaturalEnd, now.AddSeconds(1));
        Assert.Equal(PerformancePhase.Stopped, exhausted.Phase);
        Assert.Equal(PerformanceStopReason.QueueExhausted, exhausted.StopReason);
        Assert.Equal(previousGeneration + 1, exhausted.Generation.Value);
    }

    [Fact]
    public void TimelineKeepsFullScoreAndPartDurationsSeparateAndPrefersObservedEvidence()
    {
        var at = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var evidence = new[]
        {
            new ScoreTimelineEvidence("score", "part-a", 90_000, 60_000, 2_000,
                TimelineEvidenceKind.ImportedMmlEstimate, ObservationQuality.Estimated, "mml", "calc-1", "hash-a", at),
            new ScoreTimelineEvidence("score", "part-a", 120_000, 55_000, 3_000,
                TimelineEvidenceKind.PlaybackObservation, ObservationQuality.Confirmed, "playback", "calc-2", "hash-b", at.AddMinutes(1)),
            new ScoreTimelineEvidence("score", "part-b", 120_000, 30_000, 0,
                TimelineEvidenceKind.PlaybackObservation, ObservationQuality.Confirmed, "playback", "calc-2", "hash-c", at.AddMinutes(1))
        };

        var selected = ScoreTimelinePolicy.Resolve("score", "part-a", evidence);
        var otherPart = ScoreTimelinePolicy.Resolve("score", "part-b", evidence);

        Assert.Equal(120_000, selected.FullCycleMs);
        Assert.Equal(55_000, selected.SelectedPartCycleMs);
        Assert.Equal(TimelineEvidenceKind.PlaybackObservation, selected.EvidenceKind);
        Assert.Equal(30_000, otherPart.SelectedPartCycleMs);
        Assert.Equal(120_000, otherPart.FullCycleMs);
    }

    [Fact]
    public void MmlEstimateCannotTriggerStopAndLegacyMarginOnlyAppliesToSolo()
    {
        var estimate = new ScoreTimeline("score", 90_000, 60_000, false, 2_000,
            "calc-1", "hash", ObservationQuality.Estimated, TimelineEvidenceKind.ImportedMmlEstimate);
        var observed = estimate with
        {
            Quality = ObservationQuality.Confirmed,
            EvidenceKind = TimelineEvidenceKind.PlaybackObservation
        };

        Assert.Equal("music.timelineUnconfirmed", ScoreTimelinePolicy.EvaluateStop("solo", estimate, true, 90_000).ReasonCode);
        Assert.Equal("performance.startEvidenceMissing", ScoreTimelinePolicy.EvaluateStop("solo", observed, false, 90_000).ReasonCode);
        Assert.True(ScoreTimelinePolicy.EvaluateStop("solo", observed, true, 89_000, 3_000).CanStop);
        var room = ScoreTimelinePolicy.EvaluateStop("room", observed, true, 89_000, 30_000);
        Assert.False(room.CanStop);
        Assert.Equal(92_000, room.ThresholdMs);
    }

    [Fact]
    public void ConflictingPlaybackDurationsBecomeUnknownForAutomaticStop()
    {
        var at = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var evidence = new[]
            {
                new ScoreTimelineEvidence("score", null, 90_000, null, 0, TimelineEvidenceKind.PlaybackObservation,
                    ObservationQuality.Confirmed, "source-a", "calc", "a", at),
                new ScoreTimelineEvidence("score", null, 95_000, null, 0, TimelineEvidenceKind.PlaybackObservation,
                    ObservationQuality.Confirmed, "source-b", "calc", "b", at.AddSeconds(1))
            };

        var timeline = ScoreTimelinePolicy.Resolve("score", null, evidence);
        var decision = ScoreTimelinePolicy.EvaluateStop("solo", timeline, true, 100_000);

        Assert.Equal(ObservationQuality.Conflict, timeline.Quality);
        Assert.Null(timeline.FullCycleMs);
        Assert.False(decision.CanStop);
    }

    [Fact]
    public async Task PerformanceSessionStorePersistsOwnerScopedCasAndEvents()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var store = new PerformanceSessionStore(database);
        var state = new PerformanceQueueStateMachine().Create("session-1", "owner-a", "solo", "game-1", [], false, PerformanceRepeatMode.Off);
        await store.CreateAsync(state);
        var loaded = store.Get("owner-a", state.Id)!;
        Assert.Equal(state.Id, loaded.Id);
        Assert.Equal(state.Owner, loaded.Owner);
        Assert.Equal(state.Generation, loaded.Generation);
        Assert.Null(store.Get("owner-b", state.Id));
        var next = state with { Generation = new(1), Phase = PerformancePhase.Armed };
        await store.UpdateAsync("owner-a", state.Id, new(0), next);
        Assert.Equal(next.Generation, store.Get("owner-a", state.Id)!.Generation);
        var stale = await Assert.ThrowsAsync<OperationException>(() => store.UpdateAsync("owner-a", state.Id, new(0), next with { Generation = new(2) }));
        Assert.Equal("revision.stale", stale.Code);
        var foreign = await Assert.ThrowsAsync<OperationException>(() => store.DeleteAsync("owner-b", state.Id));
        Assert.Equal("resource.notFound", foreign.Code);
        Assert.Equal(2L, database.ReadScalar("SELECT count(*) FROM core_outbox"));
    }

    [Fact]
    public async Task PerformanceCreateReceiptExpiresAndStateCasIsAtomicWithNoOpEventsSuppressed()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        var clock = new Clock();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations, clock: clock);
        var store = new PerformanceSessionStore(database, clock);
        var machine = new PerformanceQueueStateMachine();
        var state = machine.Create("performance-receipt-1", "owner", "solo", "game-1", [], false, PerformanceRepeatMode.Off);
        var hash = new string('A', 64);
        var first = await store.CreateIdempotentAsync(state, "performance-create:shared-key", hash);
        Assert.Equal(state.Id, (await store.CreateIdempotentAsync(state, "performance-create:shared-key", hash)).Id);
        Assert.Equal(1L, database.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='performance.state.changed'"));

        var library = new MusicLibraryStore(database, "epoch", clock);
        var edit = new MusicEditBatch([new("shared-folder", null, "music", "1")], [], [], [], null);
        await library.EditAsync("owner", edit, "shared-key", MusicLibraryStore.ETag(new(0)));

        var armed = first with { Phase = PerformancePhase.Armed, Generation = new(1) };
        var failed = first with { Phase = PerformancePhase.Failed, StopReason = PerformanceStopReason.ControlFailure, Generation = new(1) };
        var outcomes = await Task.WhenAll(new[] { armed, failed }.Select(async next =>
        {
            try { await store.UpdateAsync("owner", first.Id, new(0), next); return "saved"; }
            catch (OperationException error) { return error.Code; }
        }));
        Assert.Single(outcomes, value => value == "saved");
        Assert.Single(outcomes, value => value == "revision.stale");
        var current = store.Get("owner", first.Id)!;
        var noOp = await store.UpdateAsync("owner", first.Id, current.Generation, current);
        Assert.Equal(JsonSerializer.Serialize(current, ContractJson.Options), JsonSerializer.Serialize(noOp, ContractJson.Options));
        Assert.Equal(2L, database.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='performance.state.changed'"));

        clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMilliseconds(1));
        var nextState = machine.Create("performance-receipt-2", "owner", "solo", "game-1", [], false, PerformanceRepeatMode.Off);
        Assert.Equal(nextState.Id, (await store.CreateIdempotentAsync(nextState, "performance-create:shared-key", new string('B', 64))).Id);

        var deletion = MusicStorageData.Owner(database).PrepareDeletion();
        await database.WriteAsync(tx => { deletion.Apply(tx); return 0; });
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM performance_sessions"));
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM music_library_receipts"));
    }

    [Fact]
    public void LimitsAndUnicodeValidationRejectInvalidFinalState()
    {
        var empty = MusicLibraryState.Empty;
        var known = new HashSet<string>();
        var many = new MusicEditBatch(Enumerable.Range(0, 129).Select(i => new MusicFolder("f-" + i, null, "x", "1")).ToArray(), [], [], [], null);
        Assert.Equal("request.invalid", Assert.Throws<OperationException>(() => MusicLibraryEditor.Apply(empty, many, known)).Code);
        var full = empty with { Folders = Enumerable.Range(0, 500).Select(i => new MusicFolder("f-" + i, null, "x", "1")).ToArray() };
        Assert.Equal("resource.capacityExceeded", Assert.Throws<OperationException>(() => MusicLibraryEditor.Apply(full, Folder("f-overflow"), known)).Code);
        Assert.Throws<OperationException>(() => MusicLibraryEditor.Apply(empty, Folder() with { PutFolders = [new("f-one", null, "\ud800", "1")] }, known));
        Assert.Throws<OperationException>(() => MusicLibraryEditor.Apply(empty, Folder() with { PutFolders = [null!] }, known));
        Assert.Throws<OperationException>(() => MusicLibraryEditor.Apply(empty, new([], [], [], [], null), known));
    }

    [Fact]
    public void InstrumentAssignmentsMustReferenceStandardCatalog()
    {
        var known = new HashSet<string> { "score-1" };
        var valid = new MusicEditBatch([], [], [new MusicPlaylist("p", null, "p", "", "piano", "1", [new PlaylistItem("i", "score-1", "violin", "1")])], [], null);
        Assert.Equal("violin", MusicLibraryEditor.Apply(MusicLibraryState.Empty, valid, known).State.Playlists[0].Items[0].InstrumentId);
        var invalid = valid with { PutPlaylists = [valid.PutPlaylists[0] with { DefaultInstrumentId = "unknown" }] };
        var error = Assert.Throws<OperationException>(() => MusicLibraryEditor.Apply(MusicLibraryState.Empty, invalid, known));
        Assert.Equal("resource.notFound", error.Code);
    }

    [Fact]
    public async Task OutboxFailureRollsBackWorkspaceAndReceiptTogether()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        await database.WriteAsync(tx => SqliteStore.AppendEvent(tx, new("music-library-1", "music-library", "music-library", new(1),
            "music.library.changed", JsonSerializer.SerializeToElement(new { conflicting = true }))));
        var store = new MusicLibraryStore(database, "epoch");
        var error = await Assert.ThrowsAsync<StorageException>(() => store.EditAsync("owner", Folder(), "rollback-edit", MusicLibraryStore.ETag(new(0))));
        Assert.Equal("storage.eventConflict", error.Code);
        Assert.Equal(0UL, store.Read().Revision.Value);
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM music_library_receipts"));
        Assert.Single(database.ReadPendingEvents());
    }

    [Fact]
    public async Task IdempotencySurvivesDayBoundaryAndReusesExpiredKeyWithoutEvictingLiveKeys()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var clock = new Clock();
        var store = new MusicLibraryStore(database, "epoch", clock);
        var first = await store.EditAsync("owner", Folder(), "retained-key", MusicLibraryStore.ETag(new(0)));
        clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(first.Revision, (await store.EditAsync("owner", Folder(), "retained-key", MusicLibraryStore.ETag(new(0)))).Revision);
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(2UL, (await store.EditAsync("owner", Folder("f-next"), "retained-key", MusicLibraryStore.ETag(new(1)))).Revision.Value);
        await database.WriteAsync(tx => tx.Execute("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<9999)
            INSERT INTO music_library_receipts SELECT 'capacity','key-'||x,'hash','{}',$expiry FROM n
            """, ("$expiry", clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds())));
        var error = await Assert.ThrowsAsync<OperationException>(() => store.EditAsync("owner", Folder("f-capacity"), "capacity-edit", MusicLibraryStore.ETag(new(2))));
        Assert.Equal("resource.capacityExceeded", error.Code);
        Assert.Equal(2UL, store.Read().Revision.Value);
        Assert.Equal(10_000L, database.ReadScalar("SELECT count(*) FROM music_library_receipts"));
        Assert.Equal(2UL, (await store.EditAsync("owner", Folder("f-next"), "retained-key", MusicLibraryStore.ETag(new(1)))).Revision.Value);
    }

    [Fact]
    public async Task ConcurrentWritersAndCancellationPreserveSingleRevision()
    {
        using var workspace = new Workspace();
        using var lease = workspace.Lease();
        await using var database = SqliteStore.Open(lease, MusicLibraryStore.Migrations);
        var first = new MusicLibraryStore(database, "epoch");
        var second = new MusicLibraryStore(database, "epoch");
        var results = await Task.WhenAll(new[] { first, second }.Select(async (store, i) =>
        {
            try { await store.EditAsync("client-" + i, Folder("f-" + i), "concurrent-edit", MusicLibraryStore.ETag(new(0))); return true; }
            catch (OperationException e) { Assert.Equal("revision.stale", e.Code); return false; }
        }));
        Assert.Single(results, x => x);
        Assert.Equal(1UL, first.Read().Revision.Value);
        Assert.Single(first.Read().Folders);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.EditAsync("owner", Folder("f-cancel"), "cancelled-edit", MusicLibraryStore.ETag(new(1)), cancellation.Token));
        Assert.Equal(1UL, first.Read().Revision.Value);
        Assert.Single(database.ReadPendingEvents());
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string directory;
        public Workspace()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
            directory = Path.Combine(root!.FullName, "artifacts", "tmp", "music-editor-tests", Guid.NewGuid().ToString("N"));
        }
        public DataRootLease Lease() => DataRootLease.Open(new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data")));
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
