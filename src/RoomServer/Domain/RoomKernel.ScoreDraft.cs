using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.RoomServer.Protocol;

namespace MabiTool.RoomServer.Domain;

internal sealed partial class RoomKernel
{
    private void PublishScoreDraft(RoomState room, RoomMemberState member, JsonElement message)
    {
        if (member.Connection?.SupportsScoreDraft != true) throw new RoomProtocolException("SCORE_DRAFT_UNSUPPORTED");
        UInt64String expected;
        MusicScoreSelection selection; MusicResolvedScore score; RoomScoreAssignment[] assignments;
        try
        {
            expected = message.Field("expectedRevision").Deserialize<UInt64String>(ContractJson.Options);
            selection = message.Field("source").Deserialize<MusicScoreSelection>(ContractJson.Options)!;
            score = message.Field("score").Deserialize<MusicResolvedScore>(ContractJson.Options)!;
            assignments = message.Field("assignments").Deserialize<RoomScoreAssignment[]>(ContractJson.Options)!;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new RoomProtocolException("BAD_SCORE_DRAFT"); }
        if (expected.Value != room.ScoreDraftRevision) throw new RoomProtocolException("STALE_SCORE_DRAFT");
        RoomScoreDraftRules.Validate(selection, score, assignments);
        if (assignments.Any(item => !room.Members.ContainsKey(item.MemberId))) throw new RoomProtocolException("MEMBER_NOT_FOUND");
        if (room.ScoreDraftRevision == ulong.MaxValue) throw new RoomProtocolException("SCORE_DRAFT_CAPACITY");
        EditableScoreDraft(room);
        room.ScoreDraft = new(1, Guid.NewGuid().ToString("N"), new(++room.ScoreDraftRevision), selection, score, assignments);
        foreach (var current in room.Members.Values) { current.ScoreMapping = null; if (current.Capabilities.ReadyMode != "emote") current.Ready = false; }
    }

    private void ClearScoreDraft(RoomState room, JsonElement message)
    {
        RequireScoreDraft(room, message);
        if (room.ScoreDraftRevision == ulong.MaxValue) throw new RoomProtocolException("SCORE_DRAFT_CAPACITY");
        EditableScoreDraft(room);
        room.ScoreDraft = null; room.ScoreDraftRevision++;
        foreach (var current in room.Members.Values) { current.ScoreMapping = null; if (current.Capabilities.ReadyMode != "emote") current.Ready = false; }
    }

    private void MapScoreDraft(RoomState room, RoomMemberState member, JsonElement message)
    {
        if (member.Connection?.SupportsScoreDraft != true) throw new RoomProtocolException("SCORE_DRAFT_UNSUPPORTED");
        var draft = RequireScoreDraft(room, message);
        var assignment = draft.Assignments.SingleOrDefault(item => item.MemberId == member.Id)
            ?? throw new RoomProtocolException("SCORE_DRAFT_UNASSIGNED");
        RoomScoreMemberMapping mapping;
        try { mapping = message.Field("mapping").Deserialize<RoomScoreMemberMapping>(ContractJson.Options)!; }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new RoomProtocolException("BAD_SCORE_MAPPING"); }
        if (mapping is null || mapping.MemberId != member.Id || mapping.State is not ("mapped" or "pending") ||
            mapping.DraftRevision != draft.Revision || mapping.Reason is { Length: > 128 } ||
            mapping.ContentSha256 is not null && !RoomProtocol.Hex(mapping.ContentSha256.ToLowerInvariant(), 64) ||
            mapping.PartId is { Length: > 128 } || mapping.InstrumentId is { Length: > 64 } ||
            mapping.Reason?.Any(char.IsControl) == true || mapping.PartId?.Any(char.IsControl) == true ||
            mapping.InstrumentId?.Any(char.IsControl) == true) throw new RoomProtocolException("BAD_SCORE_MAPPING");
        var reason = RoomScoreDraftRules.Reason(draft, assignment, mapping);
        // 来源失效必须能够撤回映射；已开始的轮次沿用现有中断/停止流程释放输入。
        if (reason is null) EditableScoreDraft(room);
        else if (room.Round is { Terminal: false } round && MusicMode(round.Mode) && round.ParticipantIds.Contains(member.Id))
        {
            if (round.Phase == "preparing" || Now < round.EarliestStartServerMs) Cancel(room, new JsonObject { ["source"] = "score-mapping-invalidated" });
            else Interrupt(room, member, "score-mapping-invalidated");
        }
        member.ScoreMapping = mapping with { State = reason is null ? "mapped" : "pending", Reason = reason };
        if (member.Capabilities.ReadyMode != "emote") member.Ready = false;
    }

    private static RoomScoreDraft RequireScoreDraft(RoomState room, JsonElement message)
    {
        var draft = room.ScoreDraft ?? throw new RoomProtocolException("STALE_SCORE_DRAFT");
        try
        {
            if (message.Text("draftId") != draft.Id || message.Field("draftRevision").Deserialize<UInt64String>(ContractJson.Options) != draft.Revision)
                throw new RoomProtocolException("STALE_SCORE_DRAFT");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new RoomProtocolException("BAD_SCORE_DRAFT"); }
        return draft;
    }

    private void EditableScoreDraft(RoomState room)
    {
        if (room.Round is { Terminal: false } round && MusicMode(round.Mode))
        {
            if (round.Phase != "preparing" && Now >= round.EarliestStartServerMs) throw new RoomProtocolException("SESSION_ACTIVE");
            Cancel(room, new JsonObject { ["source"] = "score-draft-changed" });
        }
    }
}
