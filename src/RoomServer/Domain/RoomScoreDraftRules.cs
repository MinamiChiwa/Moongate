using MabiTool.Contracts;
using MabiTool.RoomServer.Protocol;

namespace MabiTool.RoomServer.Domain;

internal static class RoomScoreDraftRules
{
    internal static void Validate(MusicScoreSelection selection, MusicResolvedScore score, IReadOnlyList<RoomScoreAssignment> assignments)
    {
        static bool Text(string? value, int maximum) => value is not null && value.Length is > 0 && value.Length <= maximum && !value.Any(char.IsControl);
        static bool Duration(double? value) => value is null || double.IsFinite(value.Value) && value.Value is > 0 and <= 86400000;
        if (selection is null || score?.Entry is null || score.Timeline is null || selection.ScopeKey is not { Length: 64 } ||
            selection.ScopeKey.Any(ch => ch is not (>= 'A' and <= 'F' or >= '0' and <= '9')) ||
            !Text(selection.ScoreId, 128) || selection.SourceRevision != score.Entry.Revision || selection.ScoreId != score.Entry.Id ||
            selection.PartId is not null && !Text(selection.PartId, 128) || selection.PartId != score.Timeline.PartId ||
            !Text(score.Entry.RawTitle, 4096) || !Text(score.Entry.DisplayTitle, 4096) || score.Entry.SourceRefs is null ||
            score.Entry.Tags is null || score.Entry.Tags.Count > 256 || score.Entry.Tags.Any(tag => !Text(tag, 128)) ||
            score.Entry.Availability is not ("present" or "missing" or "uncertain" or "observed" or "imported") ||
            score.Entry.SourceRefs.Count is < 1 or > 16 || score.Entry.SourceRefs.Any(source => source is null || !Text(source.Provider, 128) ||
                source.Region is not ("TW" or "KR") || !Text(source.CharacterScope, 256) || source.SourceId is not null && !Text(source.SourceId, 256)) ||
            score.ContentSha256 is not null && !RoomProtocol.Hex(score.ContentSha256.ToLowerInvariant(), 64) || !RoomProtocol.Hex(score.Fingerprint, 64) ||
            score.Timeline.Id != score.Entry.Id || !Duration(score.Timeline.FullCycleMs) || !Duration(score.Timeline.SelectedPartCycleMs) ||
            score.Timeline.SelectedPartCycleMs is { } part && score.Timeline.FullCycleMs is { } full && part > full + 1 ||
            !double.IsFinite(score.Timeline.TailMs) || score.Timeline.TailMs is < 0 or > 600000 ||
            !Text(score.Timeline.CalculatorVersion, 64) || score.Timeline.SourceHash is null || score.Timeline.SourceHash.Length > 128 ||
            score.Timeline.SourceHash.Any(char.IsControl) || score.UnavailableReason is not null && !Text(score.UnavailableReason, 128) ||
            !Enum.IsDefined(score.Timeline.Quality) || !Enum.IsDefined(score.Timeline.EvidenceKind) || assignments is null ||
            assignments.Count is < 1 or > 24 || assignments.Any(item => item is null || !Text(item.MemberId, 128) ||
                item.PartId is not null && !Text(item.PartId, 128) || item.InstrumentId is not null && !Text(item.InstrumentId, 64)) ||
            assignments.Select(item => item.MemberId).Distinct(StringComparer.Ordinal).Count() != assignments.Count)
            throw new RoomProtocolException("BAD_SCORE_DRAFT");
        var hash = score.Timeline.SourceHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? score.Timeline.SourceHash[7..] : score.Timeline.SourceHash;
        if (score.ContentSha256 is not null && (score.Timeline.EvidenceKind is not (TimelineEvidenceKind.ImportedMmlEstimate or TimelineEvidenceKind.PlaybackObservation) ||
            score.Timeline.Quality is not (ObservationQuality.Estimated or ObservationQuality.Confirmed) || !string.Equals(hash, score.ContentSha256, StringComparison.OrdinalIgnoreCase)))
            throw new RoomProtocolException("BAD_SCORE_DRAFT");
    }

    internal static RoomScoreMemberMapping Mapping(RoomState room, RoomMemberState member)
    {
        var draft = room.ScoreDraft;
        var assignment = draft?.Assignments.SingleOrDefault(item => item.MemberId == member.Id);
        if (draft is null || assignment is null) return new(member.Id, "unassigned", null, null, null, null, null);
        if (member.Connection?.SupportsScoreDraft != true) return new(member.Id, "pending", "room.scoreDraftUnsupported", null, null, assignment.PartId, assignment.InstrumentId);
        if (member.ScoreMapping is { } mapping && mapping.DraftRevision == draft.Revision)
        {
            var reason = Reason(draft, assignment, mapping);
            return mapping with { State = reason is null ? "mapped" : "pending", Reason = reason };
        }
        return
            new(member.Id, "pending", draft.Score.ContentSha256 is null ? "room.scoreContentUnknown" : "room.scoreMappingRequired", draft.Revision,
                null, assignment.PartId, assignment.InstrumentId);
    }

    internal static string? Reason(RoomScoreDraft draft, RoomScoreAssignment assignment, RoomScoreMemberMapping mapping)
    {
        if (mapping.DraftRevision != draft.Revision) return "room.scoreDraftStale";
        if (draft.Score.UnavailableReason is { } unavailable) return unavailable;
        if (draft.Score.Entry.SourceRefs.Any(source => source.Ambiguous)) return "room.scoreAmbiguous";
        if (draft.Score.Entry.Availability is "missing" or "uncertain") return "room.scoreMissing";
        if (draft.Score.ContentSha256 is null) return "room.scoreContentUnknown";
        if (mapping.State != "mapped") return mapping.Reason ?? "room.scoreMappingRequired";
        if (!string.Equals(mapping.ContentSha256, draft.Score.ContentSha256, StringComparison.OrdinalIgnoreCase)) return "room.scoreContentMismatch";
        if (mapping.PartId != assignment.PartId) return "room.scorePartMismatch";
        if (assignment.InstrumentId is not null && mapping.InstrumentId != assignment.InstrumentId) return "room.scoreInstrumentMismatch";
        return null;
    }

    internal static bool Ready(RoomState room, RoomMemberState member) => room.ScoreDraft is null ||
        room.ScoreDraft.Assignments.Any(item => item.MemberId == member.Id) && Mapping(room, member).State == "mapped";
}
