using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed partial class RoomScoreDraftApiTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task TwoMembersChooseTheirOwnSourcesAndReadTheSameDraftWithoutTitleMatching()
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        using var host = RoomInputApiTests.Client(app); var grant = Pair(app);
        using var member = RoomInputApiTests.Client(app, grant.Token);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, default);
        var room = await Create(host, server.LocalEndpoint); var other = await RoomInputApiTests.Post<RoomLifecycleSnapshot>(member, ApiContract.JoinRoom.Path,
            new RoomJoinRequest(server.LocalEndpoint, room.Code!, "成员"));
        var hostSelection = await Seed(app, "owner"); var memberSelection = await Seed(app, grant.Session.ClientId);
        await Part(app, "owner", hostSelection, "lead", 3000); await Part(app, grant.Session.ClientId, memberSelection, "bass", 2000);
        var initial = await Read(host);
        var publication = new RoomScoreDraftRequest(hostSelection, [new(room.MemberId!, "lead", "piano"), new(other.MemberId!, "bass", "flute")]);
        var operation = await Post(host, ApiContract.PublishRoomScoreDraft.Path, publication, "publish-two-members", initial.ETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(host, operation.Id)).State.KnownValue);
        var shared = await WaitScore(member, value => value.Draft is not null);
        Assert.Null(shared.Snapshot.Local); Assert.All(shared.Snapshot.Members, mapping => Assert.Equal("pending", mapping.State));
        Assert.Equal("樂譜：同名曲", shared.Snapshot.Draft!.Score.Entry.RawTitle);
        Assert.Equal(Hash, shared.Snapshot.Draft.Score.ContentSha256); Assert.Equal(5000, shared.Snapshot.Draft.Score.Timeline.FullCycleMs);
        var local = await Map(host, new(hostSelection with { PartId = "lead" }, "piano"), "host-local-score");
        Assert.Equal("mapped", local.Local!.State); Assert.Equal(3000, local.Local.Score!.Timeline.SelectedPartCycleMs);
        var mapped = await Map(member, new(memberSelection with { PartId = "bass" }, "flute"), "member-local-score");
        Assert.Equal("mapped", mapped.Local!.State); Assert.NotEqual(local.Local.Score.Entry.Id, mapped.Local.Score!.Entry.Id);
        Assert.Equal(2000, mapped.Local.Score.Timeline.SelectedPartCycleMs);
        var synchronized = await WaitScore(host, value => value.Members.All(item => item.State == "mapped"));
        Assert.Equal(shared.Snapshot.Draft.Id, synchronized.Snapshot.Draft!.Id); Assert.Equal(shared.Snapshot.Draft.Revision, synchronized.Snapshot.Draft.Revision);
        Assert.Empty(area.Device.Commands);
        Assert.Equal(operation.Id, (await Post(host, ApiContract.PublishRoomScoreDraft.Path, publication, "publish-two-members", initial.ETag)).Id);
        await Error(host, ApiContract.PublishRoomScoreDraft.Path, publication with { Assignments = [new(room.MemberId!, null, null)] },
            "publish-two-members", initial.ETag, HttpStatusCode.Conflict, "idempotency.conflict");
        await Error(host, ApiContract.PublishRoomScoreDraft.Path, publication, "stale-publication", initial.ETag, HttpStatusCode.PreconditionFailed, "revision.stale");
        await Error(member, ApiContract.ClearRoomScoreDraft.Path, new { }, "member-cannot-clear", (await Read(member)).ETag, HttpStatusCode.Forbidden, "permission.denied");
        using var sdk = new MabiToolClient(host.BaseAddress!, _ => ValueTask.FromResult(host.DefaultRequestHeaders.Authorization!.Parameter!));
        var sdkState = await sdk.GetRoomScoreDraftAsync(); Assert.Equal(synchronized.Snapshot.Draft.Id, sdkState.Draft!.Id);
        await Wait(async () => (await sdk.GetEventSnapshotAsync(new(["room.score-draft.changed"], [], null))).Resources.Count > 0);
        var projection = await sdk.GetEventSnapshotAsync(new(["room.score-draft.changed"], [], null));
        Assert.NotEmpty(projection!.Resources); Assert.All(projection.Resources, item => Assert.Equal("owner", item.Data.GetProperty("owner").GetString()));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("missing", "room.scoreMissing")]
    [InlineData("ambiguous", "room.scoreAmbiguous")]
    [InlineData("different-content", "room.scoreContentMismatch")]
    [InlineData("unknown-content", "room.scoreContentUnknown")]
    [InlineData("part", "room.scorePartMismatch")]
    [InlineData("part-evidence", "room.scorePartUnknown")]
    [InlineData("instrument", "room.scoreInstrumentMismatch")]
    [InlineData("unselected", "room.scoreMappingRequired")]
    public async Task UnresolvedLocalChoicesStayPendingAndCannotReserveMusicInput(string issue, string reason)
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, default);
        var room = await Create(client, server.LocalEndpoint); var selection = await Seed(app, "owner");
        var requiresPart = issue is "part" or "part-evidence";
        await Publish(client, new(selection, [new(room.MemberId!, requiresPart ? "lead" : null, "piano")]));
        MusicScoreSelection? chosen = issue switch
        {
            "unselected" => null, "missing" => selection with { ScoreId = "missing-score" },
            "ambiguous" => await Seed(app, "owner", stable: false), "different-content" => await Seed(app, "owner", new string('b', 64)),
            "unknown-content" => await Seed(app, "owner", kind: TimelineEvidenceKind.ImportedHistoryEstimate),
            "part" => selection with { PartId = "bass" }, "part-evidence" => selection with { PartId = "lead" }, _ => selection
        };
        var result = await Map(client, new(chosen, issue == "instrument" ? "violin" : "piano"));
        Assert.Equal("pending", result.Local!.State); Assert.Equal(reason, result.Local.Reason);
        Assert.Equal("pending", result.Members.Single().State); Assert.Equal(reason, result.Members.Single().Reason);
        using var response = await client.PutAsJsonAsync(ApiContract.SetRoomReadiness.Path, new RoomReadinessRequest(true, false, true, true, ReadyMode: "ensemble"), ContractJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal(reason, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        Assert.Null(app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"])); Assert.Empty(area.Device.Commands);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceOrTimelineChangesCancelTheCommittedCountdownAndReleaseInput(bool timelineChanged)
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, default);
        var room = await Create(client, server.LocalEndpoint); var selection = await Seed(app, "owner");
        await Publish(client, new(selection, [new(room.MemberId!, null, "piano")])); await Map(client, new(selection, "piano"));
        await RoomInputApiTests.Bind(client); await RoomInputApiTests.Save(client);
        using (var ready = await client.PutAsJsonAsync(ApiContract.SetRoomReadiness.Path, new RoomReadinessRequest(true, false, true, true, ReadyMode: "ensemble"), ContractJson.Options)) ready.EnsureSuccessStatusCode();
        var roundETag = await RoomInputApiTests.ETag(client, ApiContract.CurrentRoomRound.Path);
        var start = await Post(client, ApiContract.PrepareRoomRound.Path, new RoomRoundRequest("ensemble", [room.MemberId!], 5000), "mapped-round-start", roundETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(client, start.Id)).State.KnownValue);
        await Wait(async () => (await client.GetFromJsonAsync<RoomRoundSnapshot>(ApiContract.CurrentRoomRound.Path, ContractJson.Options))!.Phase == "committed");
        Assert.NotNull(app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"]));
        if (timelineChanged)
            await app.Services.GetRequiredService<ScoreTimelineStore>().RecordAsync("owner", new(selection.ScoreId, null, 6000, null, 0,
                TimelineEvidenceKind.PlaybackObservation, ObservationQuality.Confirmed, "new-content-observation", "verified-test-v2", Hash, DateTimeOffset.UtcNow));
        else
        {
            var catalogs = app.Services.GetRequiredService<ScoreCatalogStore>(); var source = catalogs.ReadByKey(selection.ScopeKey)!;
            await catalogs.RefreshAsync(source.Scope, source.Revision, [new(source.Entries.Single().RawTitle, source.Entries.Single().StableSourceId)], ScoreRefreshStatus.Complete);
        }
        var invalid = await WaitScore(client, value => value.Local?.Reason == "room.scoreSourceChanged" && value.Members.Single().State == "pending");
        Assert.Equal("pending", invalid.Snapshot.Local!.State);
        await Wait(async () => (await client.GetFromJsonAsync<RoomRoundSnapshot>(ApiContract.CurrentRoomRound.Path, ContractJson.Options))!.Phase == "cancelled");
        Assert.Empty(area.Device.Commands); Assert.Null(app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"]));
        var oldSnapshot = await client.GetFromJsonAsync<RoomRoundSnapshot>(ApiContract.CurrentRoomRound.Path, ContractJson.Options);
        Assert.Equal("released", oldSnapshot!.LocalPreparation!.State);
        await app.StopAsync();
    }

    [Fact]
    public async Task ALocalScoreChangeKeepsTheReservedIndependentEmoteInput()
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, default);
        var room = await Create(client, server.LocalEndpoint); var selection = await Seed(app, "owner");
        await Publish(client, new(selection, [new(room.MemberId!, null, "piano")])); await Map(client, new(selection, "piano"));
        await RoomInputApiTests.Bind(client); await RoomInputApiTests.Save(client);
        RoomLifecycleSnapshot prepared;
        using (var response = await client.PutAsJsonAsync(ApiContract.SetRoomReadiness.Path,
            new RoomReadinessRequest(true, false, true, false, ReadyMode: "emote"), ContractJson.Options))
        {
            response.EnsureSuccessStatusCode(); prepared = (await response.Content.ReadFromJsonAsync<RoomLifecycleSnapshot>(ContractJson.Options))!;
        }
        var catalogs = app.Services.GetRequiredService<ScoreCatalogStore>(); var source = catalogs.ReadByKey(selection.ScopeKey)!;
        await catalogs.RefreshAsync(source.Scope, source.Revision, [new(source.Entries.Single().RawTitle, source.Entries.Single().StableSourceId)], ScoreRefreshStatus.Complete);
        await WaitScore(client, value => value.Local?.Reason == "room.scoreSourceChanged" && value.Members.Single().State == "pending");
        var current = (await client.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options))!;
        Assert.Equal(prepared.LocalPreparation!.Id, current.LocalPreparation!.Id);
        Assert.Equal("prepared", current.LocalPreparation.State); Assert.Equal("emote", current.LocalPreparation.Mode);
        Assert.True(current.Room!.Value.GetProperty("members").EnumerateArray().Single(item => item.GetProperty("id").GetString() == room.MemberId).GetProperty("ready").GetBoolean());
        Assert.NotNull(app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"])); Assert.Empty(area.Device.Commands);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(ScoreRefreshStatus.Partial)]
    [InlineData(ScoreRefreshStatus.Failed)]
    public async Task IncompleteSourcesCannotConfirmEvenASavedMatchingContentHash(ScoreRefreshStatus status)
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, default);
        var room = await Create(client, server.LocalEndpoint); var selection = await Seed(app, "owner");
        await Publish(client, new(selection, [new(room.MemberId!, null, "piano")]));
        var catalogs = app.Services.GetRequiredService<ScoreCatalogStore>(); var source = catalogs.ReadByKey(selection.ScopeKey)!;
        var changed = await catalogs.RefreshAsync(source.Scope, source.Revision,
            status == ScoreRefreshStatus.Failed ? [] : [new(source.Entries.Single().RawTitle, source.Entries.Single().StableSourceId)], status);
        var pending = await Map(client, new(selection with { SourceRevision = new(changed.Revision) }, "piano"));
        Assert.Equal(Hash, pending.Local!.Score!.ContentSha256); Assert.Equal("pending", pending.Local.State);
        Assert.Equal("room.scoreSourceStale", pending.Local.Reason); Assert.Equal("pending", pending.Members.Single().State);
        Assert.Null(app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"])); Assert.Empty(area.Device.Commands);
        await app.StopAsync();
    }

    [Fact]
    public async Task AHistoryPackageHashDoesNotBecomeScoreContentAndTheOldServerHasAnExplicitGate()
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, default);
        var room = await Create(client, server.LocalEndpoint); var selection = await Seed(app, "owner", kind: TimelineEvidenceKind.ImportedHistoryEstimate);
        var draft = await Publish(client, new(selection, [new(room.MemberId!, null, null)]));
        Assert.Null(draft.Draft!.Score.ContentSha256); Assert.Equal(Hash, draft.Draft.Score.Timeline.SourceHash);
        Assert.Equal("room.scoreContentUnknown", draft.Members.Single().Reason);
        var cleared = await Post(client, ApiContract.ClearRoomScoreDraft.Path, new { }, "clear-history-draft", (await Read(client)).ETag);
        Assert.Null((await Terminal(client, cleared.Id)).Result!.Value.Deserialize<RoomScoreDraftCommandResult>(ContractJson.Options)!.Snapshot.Draft);
        await RoomInputApiTests.Post<RoomLifecycleSnapshot>(client, ApiContract.LeaveRoom.Path, new RoomLeaveRequest(false));
        await using var old = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
        var legacyRoom = await Create(client, old.Endpoint); Assert.False((await Read(client)).Snapshot.Supported);
        await Error(client, ApiContract.PublishRoomScoreDraft.Path, new RoomScoreDraftRequest(selection, [new(legacyRoom.MemberId!, null, null)]),
            "legacy-draft-gate", (await Read(client)).ETag, HttpStatusCode.UnprocessableEntity, "room.scoreDraftUnsupported");
        Assert.DoesNotContain(old.Commands, message => message.GetProperty("type").GetString() == "publish-score-draft");
        await app.StopAsync();
    }

    private static AccessGrant Pair(WebApplication app)
    {
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var challenge = auth.CreateChallenge(new("乐谱成员", null, [ApiPermissions.SystemRead, ApiPermissions.RoomsCoordinate]));
        return auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
    }
    private static Task<RoomLifecycleSnapshot> Create(HttpClient client, string endpoint) => RoomInputApiTests.Post<RoomLifecycleSnapshot>(client,
        ApiContract.CreateRoom.Path, new RoomCreateRequest(endpoint, "房主"));
    private static async Task<MusicScoreSelection> Seed(WebApplication app, string owner, string hash = Hash,
        TimelineEvidenceKind kind = TimelineEvidenceKind.ImportedMmlEstimate, bool stable = true)
    {
        var scope = new ScoreScope("TW", "1", "200", "mml-" + Guid.NewGuid().ToString("N"), ScoreSourceKind.ImportedMml);
        var source = await app.Services.GetRequiredService<ScoreCatalogStore>().RefreshAsync(scope, 0, [new("樂譜：同名曲", stable ? "file-a" : null)], ScoreRefreshStatus.Complete);
        var score = source.Entries.Single();
        await app.Services.GetRequiredService<ScoreTimelineStore>().RecordAsync(owner,
            new(score.Id, null, 5000, null, 0, kind, ObservationQuality.Estimated, "imported-mml", "mml-v1", hash, DateTimeOffset.UtcNow));
        return new(scope.Key, score.Id, new(source.Revision));
    }
    private static Task Part(WebApplication app, string owner, MusicScoreSelection selection, string part, double duration) =>
        app.Services.GetRequiredService<ScoreTimelineStore>().RecordAsync(owner,
            new(selection.ScoreId, part, 5000, duration, 0, TimelineEvidenceKind.ImportedMmlEstimate, ObservationQuality.Estimated, "imported-part", "mml-v1", Hash, DateTimeOffset.UtcNow));
    private static async Task<RoomScoreDraftSnapshot> Publish(HttpClient client, RoomScoreDraftRequest request)
    {
        var operation = await Post(client, ApiContract.PublishRoomScoreDraft.Path, request, Guid.NewGuid().ToString("N"), (await Read(client)).ETag);
        var terminal = await Terminal(client, operation.Id); Assert.True(terminal.State.KnownValue == OperationState.Succeeded, JsonSerializer.Serialize(terminal, ContractJson.Options));
        return terminal.Result!.Value.Deserialize<RoomScoreDraftCommandResult>(ContractJson.Options)!.Snapshot;
    }
    private static async Task<RoomScoreDraftSnapshot> Map(HttpClient client, RoomScoreMappingRequest request, string? key = null)
    {
        var operation = await Post(client, ApiContract.MapRoomScoreDraft.Path, request, key ?? Guid.NewGuid().ToString("N"), (await Read(client)).ETag);
        var terminal = await Terminal(client, operation.Id); Assert.True(terminal.State.KnownValue == OperationState.Succeeded, JsonSerializer.Serialize(terminal, ContractJson.Options));
        return terminal.Result!.Value.Deserialize<RoomScoreDraftCommandResult>(ContractJson.Options)!.Snapshot;
    }
    private static Task<OperationSnapshot> Post(HttpClient client, string path, object body, string key, string etag) =>
        RoomInputApiTests.Post<OperationSnapshot>(client, path, body, key, etag);
    private static async Task<(RoomScoreDraftSnapshot Snapshot, string ETag)> Read(HttpClient client)
    {
        using var response = await client.GetAsync(ApiContract.RoomScoreDraft.Path); response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<RoomScoreDraftSnapshot>(ContractJson.Options))!, response.Headers.ETag!.ToString());
    }
    private static async Task<(RoomScoreDraftSnapshot Snapshot, string ETag)> WaitScore(HttpClient client, Func<RoomScoreDraftSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true) { var value = await Read(client); if (predicate(value.Snapshot)) return value; await Task.Delay(10, timeout.Token); }
    }
    private static async Task<OperationSnapshot> Terminal(HttpClient client, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var snapshot = (await client.GetFromJsonAsync<OperationSnapshot>(ApiContract.Operation.Path.Replace("{id}", id), ContractJson.Options, timeout.Token))!;
            if (SqliteStore.IsTerminal(snapshot.State.KnownValue)) return snapshot; await Task.Delay(10, timeout.Token);
        }
    }
    private static async Task Wait(Func<Task<bool>> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!await predicate()) await Task.Delay(10, timeout.Token); }
    private static async Task Error(HttpClient client, string path, object body, string key, string? etag, HttpStatusCode status, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        request.Headers.Add("Idempotency-Key", key); if (etag is not null) request.Headers.Add("If-Match", etag);
        using var response = await client.SendAsync(request); Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
    }
}
