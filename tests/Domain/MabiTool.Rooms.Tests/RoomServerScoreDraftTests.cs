using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.RoomServer.Domain;
using MabiTool.RoomServer.Protocol;
using Xunit;

namespace MabiTool.Rooms.Tests;

public sealed class RoomServerScoreDraftTests
{
    [Fact]
    public void ExplicitMemberMappingsFreezeTheDraftOnBothPreparedActions()
    {
        using var room = new Fixture(); room.Publish();
        Assert.All(room.Mappings, mapping => Assert.Equal("pending", mapping.State));
        room.Map(room.Host); room.Map(room.Member); room.Ready(room.Host); room.Ready(room.Member);
        room.Prepare();
        var round = room.State.Round!; Assert.Equal(room.State.ScoreDraft!.Id, round.ScoreDraftId);
        Assert.Equal(room.State.ScoreDraft.Revision, round.ScoreDraftRevision);
        foreach (var connection in new[] { room.Host, room.Member })
        {
            var action = room.Peer(connection).Messages.Last(message => message.Text("type") == "prepare-action").Field("action");
            Assert.Equal(round.ScoreDraftId, action.Text("scoreDraftId"));
            Assert.Equal(round.ScoreDraftRevision.ToString(), action.Text("scoreDraftRevision"));
            room.Send(connection, new { type = "action-ready", actionId = round.Start.Id, ready = true });
        }
        Assert.Equal("committed", round.Phase); Assert.All(room.Mappings, mapping => Assert.Equal("mapped", mapping.State));
    }

    [Theory]
    [InlineData("content", "room.scoreContentMismatch")]
    [InlineData("part", "room.scorePartMismatch")]
    [InlineData("instrument", "room.scoreInstrumentMismatch")]
    [InlineData("unknown", "room.scoreContentUnknown")]
    [InlineData("stale-source", "room.scoreSourceStale")]
    public void PendingMappingsCannotAuthorizeMusic(string issue, string reason)
    {
        using var room = new Fixture(); room.Publish(issue == "unknown" ? null : Fixture.Hash,
            issue == "stale-source" ? "room.scoreSourceStale" : null);
        room.Map(room.Host, hash: issue == "content" ? new string('b', 64) : Fixture.Hash,
            part: issue == "part" ? "different" : "lead", instrument: issue == "instrument" ? "violin" : "piano");
        var mapping = room.Mappings.Single(item => item.MemberId == room.Host.Member!.Id);
        Assert.Equal("pending", mapping.State); Assert.Equal(reason, mapping.Reason);
        room.Ready(room.Host); Assert.Equal("SCORE_MAPPING_REQUIRED", room.LastCode(room.Host));
        room.Prepare(); Assert.Equal("SCORE_MAPPING_REQUIRED", room.LastCode(room.Host)); Assert.Null(room.State.Round);
    }

    [Fact]
    public void SameTitleAfterPublicationRequiresNewExplicitContentMappings()
    {
        using var room = new Fixture(); room.Publish(); room.Map(room.Host); room.Map(room.Member);
        var original = room.State.ScoreDraft!;
        room.Publish(new string('b', 64));
        Assert.Equal(original.Score.Entry.RawTitle, room.State.ScoreDraft!.Score.Entry.RawTitle);
        Assert.NotEqual(original.Id, room.State.ScoreDraft.Id); Assert.All(room.Mappings, item => Assert.Equal("pending", item.State));
        room.Map(room.Host); Assert.Equal("room.scoreContentMismatch", room.Mappings.Single(item => item.MemberId == room.Host.Member!.Id).Reason);
        room.Send(room.Host, new { type = "clear-score-draft", draftId = original.Id, draftRevision = original.Revision });
        Assert.Equal("STALE_SCORE_DRAFT", room.LastCode(room.Host)); Assert.NotNull(room.State.ScoreDraft);
    }

    [Fact]
    public void OnlyTheOwnerPublishesAndEveryMemberCanMapOnlyThemself()
    {
        using var room = new Fixture(); room.Publish();
        room.Send(room.Member, new { type = "clear-score-draft", draftId = room.State.ScoreDraft!.Id, draftRevision = room.State.ScoreDraft.Revision });
        Assert.Equal("FORBIDDEN", room.LastCode(room.Member));
        room.Send(room.Member, new { type = "map-score-draft", draftId = room.State.ScoreDraft.Id, draftRevision = room.State.ScoreDraft.Revision,
            mapping = new RoomScoreMemberMapping(room.Host.Member!.Id, "mapped", null, room.State.ScoreDraft.Revision, Fixture.Hash, "lead", "piano") });
        Assert.Equal("BAD_SCORE_MAPPING", room.LastCode(room.Member)); Assert.Null(room.Host.Member.ScoreMapping); Assert.Null(room.Member.Member!.ScoreMapping);
    }

    [Fact]
    public void OlderMembersStayPendingAndEmoteReadinessKeepsItsOwnMappingRules()
    {
        using var room = new Fixture(memberSupportsDraft: false); room.Publish(); room.Map(room.Host);
        Assert.Equal("room.scoreDraftUnsupported", room.Mappings.Single(item => item.MemberId == room.Member.Member!.Id).Reason);
        room.Ready(room.Member, "emote"); Assert.True(room.Member.Member!.Ready);
        room.Prepare(); Assert.Equal("SCORE_MAPPING_REQUIRED", room.LastCode(room.Host));
        room.Ready(room.Host, "emote");
        room.Send(room.Host, new { type = "prepare", mode = "emote", participantIds = new[] { room.Host.Member!.Id, room.Member.Member.Id }, countdownMs = 5000 });
        Assert.Equal("emote", room.State.Round!.Mode); Assert.Null(room.State.Round.ScoreDraftId);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("map")]
    [InlineData("clear")]
    public void ChangesDuringCommittedCountdownCancelAllOldActions(string change)
    {
        using var room = new Fixture(); room.Publish(); room.Map(room.Host); room.Map(room.Member);
        room.Ready(room.Host); room.Ready(room.Member); room.Prepare();
        var round = room.State.Round!;
        room.Send(room.Host, new { type = "action-ready", actionId = round.Start.Id, ready = true });
        room.Send(room.Member, new { type = "action-ready", actionId = round.Start.Id, ready = true });
        Assert.Equal("committed", round.Phase);
        if (change == "publish") room.Publish();
        else if (change == "map") room.Map(room.Member);
        else room.Send(room.Host, new { type = "clear-score-draft", draftId = room.State.ScoreDraft!.Id, draftRevision = room.State.ScoreDraft.Revision });
        Assert.Equal("cancelled", round.Phase); Assert.Empty(round.Timers);
        Assert.All(new[] { room.HostPeer, room.MemberPeer }, peer => Assert.Contains(peer.Messages,
            item => item.Text("type") == "cancel-action" && item.Text("actionId") == round.Start.Id && item.Text("sessionId") == round.Id));
    }

    [Fact]
    public void PerformingDraftEditsAreRejectedAndInvalidationCanWithdrawTheLocalMapping()
    {
        using var room = new Fixture(); room.Publish(); room.Map(room.Host); room.Map(room.Member);
        room.Ready(room.Host); room.Ready(room.Member); room.Prepare();
        var round = room.State.Round!; room.Time.Advance(6000); round.Phase = "performing";
        room.Publish(); Assert.Equal("SESSION_ACTIVE", room.LastCode(room.Host));
        room.Map(room.Member, state: "pending", reason: "room.scoreSourceChanged");
        Assert.Equal("pending", room.Mappings.Single(item => item.MemberId == room.Member.Member!.Id).State);
        Assert.False(room.Member.Member!.Ready); Assert.Equal("interrupted", round.Phase);
    }

    [Fact]
    public void DisconnectAndResumeRemoveThePreviouslyConfirmedMapping()
    {
        using var room = new Fixture(); room.Publish(); room.Map(room.Member);
        var member = room.Member.Member!; var id = member.Id; var token = member.Token;
        room.Kernel.Disconnect(room.Member);
        Assert.Null(member.ScoreMapping);
        var peer = new Peer(); var resumed = room.Kernel.Connect(peer)!;
        room.Send(resumed, new { type = "hello", product = "MabiSync", protocolVersion = 1, features = new { roundScoreDraft = 1 } });
        room.Send(resumed, new { type = "resume", code = room.State.Code, token });
        Assert.Equal(id, resumed.Member!.Id); Assert.Null(resumed.Member.ScoreMapping); Assert.False(resumed.Member.Ready);
        Assert.Equal("pending", room.Mappings.Single(item => item.MemberId == id).State);
    }

    [Theory]
    [InlineData("expectedRevision", null)]
    [InlineData("expectedRevision", "-1")]
    [InlineData("source", null)]
    [InlineData("score", null)]
    [InlineData("assignments", null)]
    public void InvalidDraftFieldsReturnProtocolErrorsWithoutBreakingTheConnection(string field, string? value)
    {
        using var room = new Fixture(); var document = JsonSerializer.SerializeToNode(room.Publication(), ContractJson.Options)!;
        document[field] = value;
        var error = Record.Exception(() => room.Kernel.Receive(room.Host, JsonSerializer.SerializeToElement(document)));
        Assert.Null(error); Assert.Equal("BAD_SCORE_DRAFT", room.LastCode(room.Host)); Assert.Null(room.State.ScoreDraft);
        room.Publish(); Assert.NotNull(room.State.ScoreDraft);
    }

    [Fact]
    public void MissingAssignmentsAndOldDraftRevisionsCannotPrepare()
    {
        using var room = new Fixture(); room.Publish(); room.Map(room.Host); room.Map(room.Member); room.Ready(room.Host); room.Ready(room.Member);
        room.Send(room.Host, new { type = "prepare", mode = "independent", countdownMs = 5000, participantIds = new[] { room.Host.Member!.Id, room.Member.Member!.Id } });
        Assert.Equal("STALE_SCORE_DRAFT", room.LastCode(room.Host)); Assert.Null(room.State.Round);
        room.State.ScoreDraftRevision = ulong.MaxValue;
        room.Send(room.Host, new { type = "clear-score-draft", draftId = room.State.ScoreDraft!.Id, draftRevision = room.State.ScoreDraft.Revision });
        Assert.Equal("SCORE_DRAFT_CAPACITY", room.LastCode(room.Host)); Assert.NotNull(room.State.ScoreDraft);
    }

    [Fact]
    public void ScoreEditsAndInvalidationKeepAnIndependentEmotePreparation()
    {
        using var room = new Fixture(); room.Publish(); room.Ready(room.Host, "emote"); room.Ready(room.Member, "emote");
        room.Send(room.Host, new { type = "prepare", mode = "emote", countdownMs = 5000, participantIds = new[] { room.Host.Member!.Id, room.Member.Member!.Id } });
        var round = room.State.Round!; room.Publish(new string('b', 64));
        Assert.Equal("preparing", round.Phase); Assert.True(room.Host.Member.Ready); Assert.True(room.Member.Member.Ready);
        room.Map(room.Member, state: "pending", reason: "room.scoreMissing");
        Assert.Equal("preparing", round.Phase); Assert.True(room.Member.Member.Ready);
        Assert.DoesNotContain(room.MemberPeer.Messages, item => item.Text("type") == "cancel-action");
    }

    [Theory]
    [InlineData("imported-history-estimate")]
    [InlineData("manual-estimate")]
    public void AClaimedContentHashRequiresContentEvidence(string evidenceKind)
    {
        using var room = new Fixture(); var message = JsonSerializer.SerializeToNode(room.Publication(), ContractJson.Options)!;
        message["score"]!["timeline"]!["evidenceKind"] = evidenceKind;
        room.Send(room.Host, message); Assert.Equal("BAD_SCORE_DRAFT", room.LastCode(room.Host)); Assert.Null(room.State.ScoreDraft);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal Clock Time { get; } = new();
        internal RoomKernel Kernel { get; }
        internal Peer HostPeer { get; } = new(); internal Peer MemberPeer { get; } = new();
        internal RoomConnection Host { get; } internal RoomConnection Member { get; }
        internal RoomState State => Host.Room!;
        internal IReadOnlyList<RoomScoreMemberMapping> Mappings => HostPeer.Messages.Last(item => item.Text("type") == "state").Field("room").Field("scoreMappings")
            .Deserialize<RoomScoreMemberMapping[]>(ContractJson.Options)!;
        internal Fixture(bool memberSupportsDraft = true)
        {
            Kernel = new(new(), Time); Host = Kernel.Connect(HostPeer)!; Member = Kernel.Connect(MemberPeer)!;
            Send(Host, new { type = "hello", product = "MabiSync", protocolVersion = 1, features = new { roundScoreDraft = 1 } });
            Send(Member, new { type = "hello", product = "MabiSync", protocolVersion = 1, features = new { roundScoreDraft = memberSupportsDraft ? 1 : 0 } });
            Send(Host, new { type = "create", nickname = "房主" }); Send(Member, new { type = "join", code = State.Code, nickname = "成员" });
        }
        internal object Publication(string? hash = Hash, string? unavailable = null) => new
        {
            type = "publish-score-draft", expectedRevision = new UInt64String(State.ScoreDraftRevision),
            source = new MusicScoreSelection(new string('A', 64), "score-a", new(1)),
            score = new MusicResolvedScore(new("score-a", "同名曲", "同名曲", null, [], [new("mml", "TW", "1:18446744073709551615", "file-a", null, "none", false)], null, "imported", new(1)),
                hash, new("score-a", 5000, null, false, 0, "mml-v1", hash ?? "", hash is null ? ObservationQuality.Unknown : ObservationQuality.Estimated,
                    hash is null ? TimelineEvidenceKind.Unknown : TimelineEvidenceKind.ImportedMmlEstimate), new string('f', 64), unavailable),
            assignments = new[] { new RoomScoreAssignment(Host.Member!.Id, "lead", "piano"), new RoomScoreAssignment(Member.Member!.Id, "lead", "piano") }
        };
        internal void Publish(string? hash = Hash, string? unavailable = null) => Send(Host, Publication(hash, unavailable));
        internal void Map(RoomConnection connection, string hash = Hash, string part = "lead", string instrument = "piano", string state = "mapped", string? reason = null) =>
            Send(connection, new { type = "map-score-draft", draftId = State.ScoreDraft!.Id, draftRevision = State.ScoreDraft.Revision,
                mapping = new RoomScoreMemberMapping(connection.Member!.Id, state, reason, State.ScoreDraft.Revision, hash, part, instrument) });
        internal void Ready(RoomConnection connection, string mode = "independent") => Send(connection,
            new { type = "set-ready", ready = true, capabilities = new { simulation = true, canStart = true, canStop = true, readyMode = mode } });
        internal void Prepare() => Send(Host, new { type = "prepare", mode = "independent", countdownMs = 5000,
            participantIds = new[] { Host.Member!.Id, Member.Member!.Id }, draftId = State.ScoreDraft!.Id, draftRevision = State.ScoreDraft.Revision });
        internal void Send(RoomConnection connection, object message) => Kernel.Receive(connection, JsonSerializer.SerializeToElement(message, ContractJson.Options));
        internal Peer Peer(RoomConnection connection) => connection == Host ? HostPeer : MemberPeer;
        internal string? LastCode(RoomConnection connection) => Peer(connection).Messages.Last().Text("code");
        public void Dispose() => Kernel.Dispose();
    }
    private sealed class Peer : IRoomPeer
    {
        public bool Open { get; private set; } = true;
        internal List<JsonElement> Messages { get; } = [];
        public void Send(byte[] bytes) { using var doc = JsonDocument.Parse(bytes); Messages.Add(doc.RootElement.Clone()); }
        public void Close(int status, string reason) => Open = false;
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(timestamp);
        internal void Advance(long milliseconds) => timestamp += milliseconds;
    }
}
