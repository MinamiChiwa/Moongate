using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class MusicLibraryTests
{
    private const string Token = "music_tests_0123456789012345678901234567890123456789";
    private static readonly ScoreScope Scope = new("TW", "realm-a", "18446744073709551615", "synthetic-fixture", ScoreSourceKind.Inventory);
    private static MusicEditBatch Batch(MusicPlaylist playlist) => new([], [], [playlist], [], null);
    private static MusicQuery Query(MusicCollection collection, int size = 200, string? parent = null, string? list = null,
        string? scope = null, string search = "", string? cursor = null) => new(collection, parent, list, scope, search, size, cursor);
    private static MusicPlaylist List(string id, params PlaylistItem[] items) => new(id, null, id, "", null, id, items);

    [Fact]
    public async Task InstrumentCatalogIsStableAndLocalized()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var client = host.Http();
        var response = await client.GetFromJsonAsync<ApiList<InstrumentDefinition>>(ApiContract.MusicInstruments.Path, ContractJson.Options);
        Assert.NotNull(response);
        var piano = Assert.Single(response!.Items, x => x.Id == "piano");
        Assert.Equal("鋼琴", piano.DisplayNames["zh-TW"]);
        Assert.Equal("music-instruments-1", response.SnapshotRevision);
    }

    [Fact]
    public async Task PlaybackHistoryIsOwnerScopedDurableAndIdempotent()
    {
        using var workspace = new Workspace();
        var input = new MusicPlaybackHistoryInput("history-1", "score-1", "melody", "piano",
            new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero), 45_000);
        MusicPlaybackHistoryItem first;
        await using (var host = await Host.Start(workspace))
        {
            using var owner = host.Sdk();
            using var reader = await host.Pair("history-reader", true);
            first = await owner.ImportMusicPlaybackHistoryAsync(input, "history-key-1");
            var retry = await owner.ImportMusicPlaybackHistoryAsync(input, "history-key-1");
            Assert.Equal(first, retry);
            Assert.Equal(TimelineEvidenceKind.ImportedHistoryEstimate, first.EvidenceKind);
            Assert.Equal(ObservationQuality.Estimated, first.Quality);
            Assert.Single((await owner.GetMusicPlaybackHistoryAsync()).Items);
            Assert.Empty((await reader.GetMusicPlaybackHistoryAsync()).Items);

            var timelineStore = host.App.Services.GetRequiredService<ScoreTimelineStore>();
            var ownerId = (await owner.GetAuthSessionAsync()).ClientId;
            var observedAt = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);
            await timelineStore.RecordAsync(ownerId, new("score-1", null, 120_000, null, 2_000,
                TimelineEvidenceKind.PlaybackObservation, ObservationQuality.Confirmed, "whole", "v1", "whole-hash", observedAt));
            await timelineStore.RecordAsync(ownerId, new("score-1", "melody", 120_000, 75_000, 2_000,
                TimelineEvidenceKind.PlaybackObservation, ObservationQuality.Confirmed, "melody", "v1", "part-hash", observedAt));
            var whole = await owner.QueryMusicTimelineAsync(new("score-1", null));
            var part = await owner.QueryMusicTimelineAsync(new("score-1", "melody"));
            Assert.Equal(120_000, whole.FullCycleMs);
            Assert.Null(whole.SelectedPartCycleMs);
            Assert.Equal(120_000, part.FullCycleMs);
            Assert.Equal(75_000, part.SelectedPartCycleMs);

            var conflict = await Assert.ThrowsAsync<MabiToolApiException>(() => owner.ImportMusicPlaybackHistoryAsync(
                input with { DurationMs = 46_000 }, "history-key-1"));
            Assert.Equal("idempotency.conflict", conflict.Problem!.Code);
            Assert.Empty((await reader.GetEventSnapshotAsync(new(["music.playback-history.changed"], [], null))).Resources);
        }
        await using (var restarted = await Host.Start(workspace))
        {
            using var restartedOwner = restarted.Sdk();
            Assert.Equal(first, Assert.Single((await restartedOwner.GetMusicPlaybackHistoryAsync()).Items));
            var restoredTimeline = await restartedOwner.QueryMusicTimelineAsync(new("score-1", "melody"));
            Assert.Equal(75_000, restoredTimeline.SelectedPartCycleMs);
        }
    }

    [Fact]
    public async Task TwoPairedClientsEditOneLibraryAndRetriesRetainOriginalResult()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var first = await host.Pair("frontend-a", true);
        using var second = await host.Pair("frontend-b", true);
        string? etag = null;
        var info = await first.GetMusicLibraryAsync(onResponse: value => etag = value.ETag);
        Assert.Equal(0UL, info.Revision.Value);
        Assert.Equal("\"music-library-0\"", etag);
        var score = (await host.Seed()).Entries[0].Id;
        var request = new MusicEditBatch([new("f-root", null, "根目錄", "001"), new("f-child", "f-root", "子目錄", "002")], [],
            [List("p-main", new PlaylistItem("i-a", score, null, "002"), new PlaylistItem("i-b", score, "violin", "001")) with
            { FolderId = "f-child", Name = "雙份曲目", Notes = "第一行\n第二行", DefaultInstrumentId = "piano" }], [], null);
        var saved = await first.EditMusicLibraryAsync(request, "initial-batch", etag!);
        Assert.Equal(1UL, saved.Revision.Value);
        var conflict = await Assert.ThrowsAsync<MabiToolApiException>(() => second.EditMusicLibraryAsync(request, "another-edit", etag!));
        Assert.Equal(412, conflict.Status);
        var lists = await second.QueryMusicLibraryAsync(Query(MusicCollection.Playlists, parent: "f-child"));
        var playlist = Assert.Single(lists.Playlists);
        Assert.Equal("雙份曲目", playlist.Name);
        Assert.Equal("第一行\n第二行", playlist.Notes);
        Assert.Equal("piano", playlist.DefaultInstrumentId);
        Assert.Empty(playlist.Items);
        var page = await second.QueryMusicLibraryAsync(Query(MusicCollection.PlaylistItems, list: "p-main"));
        Assert.Equal(new[] { "i-b", "i-a" }, page.Items.Select(x => x.Id));
        Assert.All(page.Items, x => Assert.Equal(score, x.ScoreId));
        var reordered = playlist with { Notes = "updated", Items = page.Items.Select((x, i) => x with { SortKey = (2 - i).ToString() }).ToArray() };
        await second.EditMusicLibraryAsync(Batch(reordered), "reorder-list", MusicLibraryStore.ETag(new(1)));
        Assert.Equal(new[] { "i-a", "i-b" }, (await first.QueryMusicLibraryAsync(Query(MusicCollection.PlaylistItems, list: "p-main"))).Items.Select(x => x.Id));
        Assert.Equal(JsonSerializer.Serialize(saved, ContractJson.Options), JsonSerializer.Serialize(await first.EditMusicLibraryAsync(request, "initial-batch", etag!), ContractJson.Options));
        var wrong = await Assert.ThrowsAsync<MabiToolApiException>(() => first.EditMusicLibraryAsync(Batch(reordered), "initial-batch", etag!));
        Assert.Equal("idempotency.conflict", wrong.Problem!.Code);
        var latest = await first.GetMusicLibraryAsync();
        Assert.Equal(2UL, latest.Revision.Value);
        Assert.Equal(2, latest.ItemCount);
        Assert.Equal(Scope.CharacterId, Assert.Single(latest.Sources).CharacterId);
    }

    [Fact]
    public async Task FolderDeletionReparentsChildrenAndListsWithoutDeletingScores()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var sdk = host.Sdk();
        var score = (await host.Seed()).Entries[0].Id;
        var request = new MusicEditBatch([new("f-root", null, "root", "1"), new("f-child", "f-root", "child", "2"), new("f-leaf", "f-child", "leaf", "3")], [],
            [List("p-one", new PlaylistItem("i-one", score, null, "1")) with { FolderId = "f-child" }, List("p-two") with { FolderId = "f-leaf" }], [], null);
        await sdk.EditMusicLibraryAsync(request, "folder-setup", MusicLibraryStore.ETag(new(0)));
        var result = await sdk.EditMusicLibraryAsync(new([], ["f-child", "f-root"], [], [], null), "folder-delete", MusicLibraryStore.ETag(new(1)));
        Assert.Contains("f-leaf", result.ChangedFolderIds);
        Assert.Contains("p-one", result.ChangedPlaylistIds);
        Assert.Null(Assert.Single((await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Folders))).Folders).ParentId);
        Assert.Equal("p-one", Assert.Single((await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Playlists))).Playlists).Id);
        await sdk.EditMusicLibraryAsync(new([], [], [], ["p-one"], null), "delete-playlist", MusicLibraryStore.ETag(new(2)));
        Assert.Single((await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Scores, scope: Scope.Key))).Scores);
        Assert.Empty((await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Playlists))).Playlists);
    }

    [Fact]
    public async Task InvalidBatchRejectsCyclesDanglingReferencesDuplicateItemsAndPartialChanges()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var sdk = host.Sdk();
        var score = (await host.Seed()).Entries[0].Id;
        MusicEditBatch[] invalid = [
            new([new("f-a", "f-b", "a", "1"), new("f-b", "f-a", "b", "2")], [], [], [], null),
            new([new("f-a", "missing", "a", "1")], [], [], [], null),
            new([new("f-a", null, "a", "1")], ["f-a"], [], [], null),
            Batch(List("p-main", new PlaylistItem("i-a", "unknown-score", null, "1"))),
            Batch(List("p-main", new PlaylistItem("i-a", score, null, "1"), new PlaylistItem("i-a", score, null, "2"))),
            new([], [], [List("p-a", new PlaylistItem("i-same", score, null, "1")), List("p-b", new PlaylistItem("i-same", score, null, "1"))], [], null),
            new([new("f-valid", null, "valid", "1")], [], [List("p-invalid") with { FolderId = "missing" }], [], null),
            Batch(List("p-main") with { Notes = new string('x', 4001) }),
            Batch(List("p-main") with { Name = " " }),
            new([], [], [], [], new([new("artist-a", "IU", []), new("artist-b", "other", ["IU"])], new Dictionary<string,string>(), []))
        ];
        for (var i = 0; i < invalid.Length; i++)
        {
            var error = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.EditMusicLibraryAsync(invalid[i], "invalid-" + i, MusicLibraryStore.ETag(new(0))));
            Assert.Contains(error.Status, new[] { 400, 404 });
            Assert.Equal(0UL, (await sdk.GetMusicLibraryAsync()).Revision.Value);
        }
        Assert.Empty((await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Folders))).Folders);
    }

    [Fact]
    public async Task PaginationHasStableOrderAndRejectsChangedFilterTamperingAndOldRevision()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var sdk = host.Sdk();
        var lists = Enumerable.Range(1, 5).Select(i => List("p-" + i) with { Name = "同名", SortKey = "same" }).ToArray();
        await sdk.EditMusicLibraryAsync(new([], [], lists.Reverse().ToArray(), [], null), "paging-setup", MusicLibraryStore.ETag(new(0)));
        var query = Query(MusicCollection.Playlists, size: 2);
        var first = await sdk.QueryMusicLibraryAsync(query);
        var second = await sdk.QueryMusicLibraryAsync(query with { Cursor = first.NextCursor });
        var third = await sdk.QueryMusicLibraryAsync(query with { Cursor = second.NextCursor });
        Assert.Equal(Enumerable.Range(1, 5).Select(i => "p-" + i), first.Playlists.Concat(second.Playlists).Concat(third.Playlists).Select(x => x.Id));
        Assert.Null(third.NextCursor);
        var wrong = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryMusicLibraryAsync(query with { Search = "different", Cursor = first.NextCursor }));
        Assert.Equal(400, wrong.Status);
        wrong = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryMusicLibraryAsync(query with { Cursor = first.NextCursor + "0" }));
        Assert.Equal(400, wrong.Status);
        await sdk.EditMusicLibraryAsync(new([], [], [], ["p-2"], null), "paging-delete", MusicLibraryStore.ETag(new(1)));
        wrong = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryMusicLibraryAsync(query with { Cursor = first.NextCursor }));
        Assert.Equal("music.cursorStale", wrong.Problem!.Code);
        Assert.Equal(4, (await sdk.QueryMusicLibraryAsync(query with { PageSize = 200 })).Playlists.Count);
        wrong = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryMusicLibraryAsync(query with { PageSize = 201 }));
        Assert.Equal(400, wrong.Status);
    }

    [Fact]
    public async Task ScorePagesPreserveSourcesAndUsePersistedAliasesAssignmentsAndNoise()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var sdk = host.Sdk();
        var source = await host.Seed([new("樂譜：Secret sampleNoise", "a"), new("同名"), new("同名")]);
        var target = source.Entries.Single(x => x.StableSourceId == "a");
        var settings = new MusicNameSettings([new("artist-a", "歌手", ["Alias"])], new Dictionary<string,string> { [target.Id] = "artist-a" }, ["sampleNoise"]);
        await sdk.EditMusicLibraryAsync(new([], [], [], [], settings), "name-settings", MusicLibraryStore.ETag(new(0)));
        var page = await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Scores, scope: Scope.Key, search: "Alias"));
        var score = Assert.Single(page.Scores);
        Assert.Equal(target.Id, score.Id);
        Assert.Equal(target.RawTitle, score.RawTitle);
        Assert.Equal("Secret", score.DisplayTitle);
        Assert.Equal("artist-a", score.ArtistId);
        Assert.Equal(Scope.CharacterId, (await sdk.GetMusicLibraryAsync()).Sources[0].CharacterId);
        var duplicates = await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Scores, scope: Scope.Key, search: "同名"));
        Assert.Equal(2, duplicates.Scores.Count);
        Assert.All(duplicates.Scores, x => Assert.True(Assert.Single(x.SourceRefs).Ambiguous));
        var query = Query(MusicCollection.Scores, size: 1, scope: Scope.Key);
        var cursor = (await sdk.QueryMusicLibraryAsync(query)).NextCursor;
        await host.App.Services.GetRequiredService<ScoreCatalogStore>().RefreshAsync(Scope, 1, [], ScoreRefreshStatus.Complete);
        var stale = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryMusicLibraryAsync(query with { Cursor = cursor }));
        Assert.Equal("music.cursorStale", stale.Problem!.Code);
        Assert.All((await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Scores, scope: Scope.Key))).Scores, x => Assert.Equal("missing", x.Availability));
    }

    [Fact]
    public async Task WritePermissionHeadersStrictBodiesAndModuleDisableAreEnforced()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var reader = await host.Pair("read-only", false);
        using var sdk = host.Sdk();
        var request = Batch(List("p-one"));
        var denied = await Assert.ThrowsAsync<MabiToolApiException>(() => reader.EditMusicLibraryAsync(request, "reader-write", MusicLibraryStore.ETag(new(0))));
        Assert.Equal(403, denied.Status);
        Assert.Equal(0UL, (await reader.GetMusicLibraryAsync()).Revision.Value);
        using var client = host.Http();
        using (var missing = await client.PostAsJsonAsync("/api/v1/music/library/edits", request, ContractJson.Options))
            Assert.Equal("idempotency.required", (await missing.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "missing-etag");
        using (var missing = await client.PostAsJsonAsync("/api/v1/music/library/edits", request, ContractJson.Options))
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        client.DefaultRequestHeaders.Add("If-Match", MusicLibraryStore.ETag(new(0)));
        using (var unknown = await client.PostAsync("/api/v1/music/library/edits", new StringContent("{\"putFolders\":[],\"deleteFolders\":[],\"putPlaylists\":[],\"deletePlaylists\":[],\"nameSettings\":null,\"unknown\":true}", System.Text.Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        OperationSnapshot operation;
        try
        {
            operation = await sdk.ConfigureModuleAsync("music-library", new(false), "disable-music", "\"module-music-library-0\"");
        }
        catch (MabiToolApiException error)
        {
            throw new Xunit.Sdk.XunitException($"Module configuration admission failed: HTTP {error.Status}; problem={JsonSerializer.Serialize(error.Problem, ContractJson.Options)}; exception={error}");
        }
        for (var i = 0; i < 200; i++)
        {
            var state = await sdk.GetOperationAsync(operation.Id);
            if (SqliteStore.IsTerminal(state.State.KnownValue)) { Assert.Equal(OperationState.Succeeded, state.State.KnownValue); break; }
            await Task.Delay(10);
        }
        denied = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.GetMusicLibraryAsync());
        Assert.Equal("module.disabled", denied.Problem!.Code);
        var performanceDenied = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.ListPerformanceSessionsAsync());
        Assert.Equal("module.disabled", performanceDenied.Problem!.Code);
        var performanceCreateDenied = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CreatePerformanceSessionAsync(
            new PerformanceSessionCreateRequest("disabled-performance", "solo", "game-1", [], false, PerformanceRepeatMode.Off),
            "disabled-performance-create"));
        Assert.Equal("module.disabled", performanceCreateDenied.Problem!.Code);
    }

    [Fact]
    public async Task RestartRetainsListsIdsAndIdempotencyAndExposesTypedNotifications()
    {
        using var workspace = new Workspace();
        MusicEditBatch request;
        string? cursor;
        await using (var host = await Host.Start(workspace))
        {
            using var sdk = host.Sdk();
            var score = (await host.Seed()).Entries[0].Id;
            request = new([], [], [List("p-one", new PlaylistItem("i-one", score, "violin", "1")), List("p-two")], [], null);
            await sdk.EditMusicLibraryAsync(request, "durable-edit", MusicLibraryStore.ETag(new(0)));
            cursor = (await sdk.QueryMusicLibraryAsync(Query(MusicCollection.Playlists, size: 1))).NextCursor;
            ProjectionSnapshot? snapshot = null;
            for (var i = 0; i < 200; i++)
            {
                snapshot = await sdk.GetEventSnapshotAsync(new(["music.library.changed"], [], null));
                if (snapshot.Resources.Count > 0) break;
                await Task.Delay(10);
            }
            var notification = Assert.Single(snapshot!.Resources).Data.Deserialize<MusicEditResult>(ContractJson.Options)!;
            Assert.Equal(1UL, notification.Revision.Value);
            Assert.Contains("p-one", notification.ChangedPlaylistIds);
        }
        await using (var host = await Host.Start(workspace))
        {
            using var sdk = host.Sdk();
            Assert.Equal(1UL, (await sdk.GetMusicLibraryAsync()).Revision.Value);
            Assert.Equal("i-one", Assert.Single((await sdk.QueryMusicLibraryAsync(Query(MusicCollection.PlaylistItems, list: "p-one"))).Items).Id);
            Assert.Equal(1UL, (await sdk.EditMusicLibraryAsync(request, "durable-edit", MusicLibraryStore.ETag(new(0)))).Revision.Value);
            Assert.True(Assert.Single((await sdk.GetMusicLibraryAsync()).Sources).Stale);
            var error = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.QueryMusicLibraryAsync(Query(MusicCollection.Playlists, size: 1, cursor: cursor)));
            Assert.Equal(400, error.Status);
        }
    }

    [Fact]
    public async Task PerformanceSessionApiPersistsAcrossRestartAndRejectsStaleCommands()
    {
        using var workspace = new Workspace();
        var item = new PlaylistItem("item-1", "score-1", "piano", "1");
        string id;
        string generation;
        await using (var host = await Host.Start(workspace))
        {
            using var client = host.Http();
            client.DefaultRequestHeaders.TryAddWithoutValidation("Idempotency-Key", "performance-create-api");
            var create = await client.PostAsJsonAsync(ApiContract.CreatePerformanceSession.Path,
                new PerformanceSessionCreateRequest("session-api", "solo", "game-1", [item], false, PerformanceRepeatMode.Off), ContractJson.Options);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var state = await create.Content.ReadFromJsonAsync<PerformanceQueueState>(ContractJson.Options);
            Assert.NotNull(state);
            id = state!.Id;
            generation = state.Generation.ToString();
            Assert.Equal("\"0\"", create.Headers.ETag!.Tag);

            using var malformed = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/music/performance/sessions/{id}/commands")
            {
                Content = JsonContent.Create(new PerformanceSessionCommand("arm"), options: ContractJson.Options)
            };
            malformed.Headers.TryAddWithoutValidation("If-Match", "0");
            var malformedResult = await client.SendAsync(malformed);
            Assert.Equal(HttpStatusCode.BadRequest, malformedResult.StatusCode);
            Assert.Equal("request.invalid", (await malformedResult.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);

            using var command = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/music/performance/sessions/{id}/commands")
            {
                Content = JsonContent.Create(new PerformanceSessionCommand("arm"), options: ContractJson.Options)
            };
            command.Headers.TryAddWithoutValidation("If-Match", "\"" + generation + "\"");
            var armed = await client.SendAsync(command);
            Assert.Equal(HttpStatusCode.OK, armed.StatusCode);
            var armedState = await armed.Content.ReadFromJsonAsync<PerformanceQueueState>(ContractJson.Options);
            Assert.Equal(PerformancePhase.Armed, armedState!.Phase);

            using var stale = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/music/performance/sessions/{id}/commands")
            {
                Content = JsonContent.Create(new PerformanceSessionCommand("dispatch"), options: ContractJson.Options)
            };
            stale.Headers.TryAddWithoutValidation("If-Match", "\"" + generation + "\"");
            var conflict = await client.SendAsync(stale);
            Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
            Assert.Equal("revision.stale", (await conflict.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        }
        await using (var host = await Host.Start(workspace))
        {
            using var client = host.Http();
            var restored = await client.GetFromJsonAsync<PerformanceQueueState>($"/api/v1/music/performance/sessions/{id}", ContractJson.Options);
            Assert.Equal(PerformancePhase.Armed, restored!.Phase);
            Assert.Equal("session-api", restored.Id);
        }
    }

    [Fact]
    public async Task PerformanceSessionManualStopConfirmationDoesNotAdvanceQueue()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var client = host.Http();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Idempotency-Key", "performance-create-stop");
        var items = new[] { new PlaylistItem("item-1", "score-1", null, "1"), new PlaylistItem("item-2", "score-2", null, "2") };
        var response = await client.PostAsJsonAsync(ApiContract.CreatePerformanceSession.Path,
            new PerformanceSessionCreateRequest("stop-session", "solo", "game-1", items, false, PerformanceRepeatMode.All, 0), ContractJson.Options);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var state = await response.Content.ReadFromJsonAsync<PerformanceQueueState>(ContractJson.Options);
        Assert.NotNull(state);
        state = await Command("arm", state!);
        state = await Command("dispatch", state);
        state = await Command("playing", state);
        state = await Command("stop", state);
        Assert.Equal(PerformancePhase.Stopping, state.Phase);

        using (var wrongConfirmation = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/music/performance/sessions/{state.Id}/commands")
        {
            Content = JsonContent.Create(new PerformanceSessionCommand("stopped", StopReason: PerformanceStopReason.NaturalEnd), options: ContractJson.Options)
        })
        {
            wrongConfirmation.Headers.TryAddWithoutValidation("If-Match", "\"" + state.Generation + "\"");
            var rejected = await client.SendAsync(wrongConfirmation);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("performance.invalidState", (await rejected.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        }
        state = await Command("stopped", state);
        Assert.Equal(PerformancePhase.Stopped, state.Phase);
        Assert.Equal(PerformanceStopReason.ManualStop, state.StopReason);
        Assert.Equal("score-1", state.CurrentScoreId);
        var terminalGeneration = state.Generation;
        var terminalEvents = host.App.Services.GetRequiredService<SqliteStore>().ReadScalar(
            "SELECT count(*) FROM core_outbox WHERE event_type='performance.state.changed'");
        state = await Command("stop", state);
        state = await Command("stopped", state);
        Assert.Equal(terminalGeneration, state.Generation);
        Assert.Equal(PerformancePhase.Stopped, state.Phase);
        Assert.Equal("score-1", state.CurrentScoreId);
        Assert.Equal(terminalEvents, host.App.Services.GetRequiredService<SqliteStore>().ReadScalar(
            "SELECT count(*) FROM core_outbox WHERE event_type='performance.state.changed'"));

        async Task<PerformanceQueueState> Command(string action, PerformanceQueueState current)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/music/performance/sessions/{current.Id}/commands")
            {
                Content = JsonContent.Create(new PerformanceSessionCommand(action), options: ContractJson.Options)
            };
            request.Headers.TryAddWithoutValidation("If-Match", "\"" + current.Generation + "\"");
            using var result = await client.SendAsync(request);
            if (!result.IsSuccessStatusCode) throw new InvalidOperationException(await result.Content.ReadAsStringAsync());
            result.EnsureSuccessStatusCode();
            return (await result.Content.ReadFromJsonAsync<PerformanceQueueState>(ContractJson.Options))!;
        }
    }

    [Fact]
    public async Task PerformanceCreateIsIdempotentAndRejectsDifferentBody()
    {
        using var workspace = new Workspace();
        await using var host = await Host.Start(workspace);
        using var client = host.Http();
        var request = new PerformanceSessionCreateRequest(null, "solo", "game-1",
            [new PlaylistItem("item-1", "score-1", null, "1")], false, PerformanceRepeatMode.Off);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, ApiContract.CreatePerformanceSession.Path)
        {
            Content = JsonContent.Create(request, options: ContractJson.Options)
        };
        firstRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "performance-receipt");
        using var first = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstState = await first.Content.ReadFromJsonAsync<PerformanceQueueState>(ContractJson.Options);

        using var retryRequest = new HttpRequestMessage(HttpMethod.Post, ApiContract.CreatePerformanceSession.Path)
        {
            Content = JsonContent.Create(request, options: ContractJson.Options)
        };
        retryRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "performance-receipt");
        using var retry = await client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var retryState = await retry.Content.ReadFromJsonAsync<PerformanceQueueState>(ContractJson.Options);
        Assert.Equal(firstState!.Id, retryState!.Id);
        Assert.Equal(JsonSerializer.Serialize(firstState, ContractJson.Options), JsonSerializer.Serialize(retryState, ContractJson.Options));
        Assert.Equal(1L, host.App.Services.GetRequiredService<SqliteStore>().ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='performance.state.changed'"));

        using var different = new HttpRequestMessage(HttpMethod.Post, ApiContract.CreatePerformanceSession.Path)
        {
            Content = JsonContent.Create(request with { Mode = "room" }, options: ContractJson.Options)
        };
        different.Headers.TryAddWithoutValidation("Idempotency-Key", "performance-receipt");
        using var conflict = await client.SendAsync(different);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency.conflict", (await conflict.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
    }

    [Fact]
    public async Task PerformanceQueriesWithoutStorageReturnStructuredUnavailable()
    {
        await using var host = await Host.Start(null);
        using var sdk = host.Sdk();
        var error = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.ListPerformanceSessionsAsync());
        Assert.Equal("storage.unavailable", error.Problem!.Code);
    }

    [Fact]
    public async Task ConcurrentPerformanceCreateRetriesSurviveRestartAndDoNotCollideWithMusicEdits()
    {
        using var workspace = new Workspace();
        var request = new PerformanceSessionCreateRequest(null, "solo", "game-1",
            [new PlaylistItem("item-1", "score-1", null, "1")], false, PerformanceRepeatMode.Off);
        string sessionId;
        await using (var host = await Host.Start(workspace))
        {
            using var client = host.Http();
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, ApiContract.CreatePerformanceSession.Path)
                { Content = JsonContent.Create(request, options: ContractJson.Options) };
                message.Headers.TryAddWithoutValidation("Idempotency-Key", "shared-performance-key");
                using var response = await client.SendAsync(message);
                return (response.StatusCode, State: await response.Content.ReadFromJsonAsync<PerformanceQueueState>(ContractJson.Options));
            }));
            Assert.All(responses, result => Assert.Equal(HttpStatusCode.Created, result.StatusCode));
            sessionId = Assert.Single(responses.Select(result => result.State!.Id).Distinct(StringComparer.Ordinal));
            Assert.Equal(1L, host.App.Services.GetRequiredService<SqliteStore>().ReadScalar(
                "SELECT count(*) FROM core_outbox WHERE event_type='performance.state.changed'"));
        }

        await using (var host = await Host.Start(workspace))
        {
            using var sdk = host.Sdk();
            var replay = await sdk.CreatePerformanceSessionAsync(request, "shared-performance-key");
            Assert.Equal(sessionId, replay.Id);
            Assert.Single((await sdk.ListPerformanceSessionsAsync()).Items);
            var edit = new MusicEditBatch([new("shared-folder", null, "music", "1")], [], [], [], null);
            await sdk.EditMusicLibraryAsync(edit, "shared-performance-key", MusicLibraryStore.ETag(new(0)));
            Assert.Equal(2L, host.App.Services.GetRequiredService<SqliteStore>().ReadScalar(
                "SELECT count(*) FROM music_library_receipts WHERE principal=$owner", ("$owner", (await sdk.GetAuthSessionAsync()).ClientId)));
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required Uri Address { get; init; }
        public static async Task<Host> Start(Workspace? workspace)
        {
            var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: workspace?.Options);
            await app.StartAsync();
            return new() { App = app, Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        }
        public MabiToolClient Sdk(string? token = null) => new(Address, _ => ValueTask.FromResult(token ?? Token));
        public HttpClient Http()
        {
            var http = new HttpClient { BaseAddress = Address };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return http;
        }
        public async Task<MabiToolClient> Pair(string name, bool edit)
        {
            using var owner = Sdk();
            var challenge = await owner.CreatePairingChallengeAsync(new(name, null, edit ? [ApiPermissions.SystemRead, ApiPermissions.MusicEdit] : [ApiPermissions.SystemRead]));
            var grant = await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
            return Sdk(grant.Token);
        }
        public Task<ScoreCatalogSnapshot> Seed(IReadOnlyList<ScoreSourceItem>? items = null) =>
            App.Services.GetRequiredService<ScoreCatalogStore>().RefreshAsync(Scope, 0, items ?? [new("樂譜：測試", "source-id")], ScoreRefreshStatus.Complete);
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string directory;
        public StorageOptions Options => new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data"));
        public Workspace()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
            directory = Path.Combine(root!.FullName, "artifacts", "tmp", "music-api-tests", Guid.NewGuid().ToString("N"));
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}







