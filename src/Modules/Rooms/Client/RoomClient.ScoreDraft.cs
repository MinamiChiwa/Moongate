using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Modules.Rooms.Client;

public sealed partial class RoomClient
{
    public RoomScoreDraft? ScoreDraft => Room is { } room && room.TryGetProperty("scoreDraft", out var draft) && draft.ValueKind == JsonValueKind.Object
        ? draft.Deserialize<RoomScoreDraft>(ContractJson.Options) : null;
    public UInt64String ScoreDraftRevision => Room is { } room && room.TryGetProperty("scoreDraftRevision", out var revision)
        ? revision.Deserialize<UInt64String>(ContractJson.Options) : new(0);
    public IReadOnlyList<RoomScoreMemberMapping> ScoreMappings => Room is { } room && room.TryGetProperty("scoreMappings", out var mappings)
        ? mappings.Deserialize<RoomScoreMemberMapping[]>(ContractJson.Options)! : [];

    public Task PublishScoreDraftAsync(MusicScoreSelection source, MusicResolvedScore score, IReadOnlyList<RoomScoreAssignment> assignments,
        UInt64String expectedRevision, CancellationToken ct)
    {
        RequireScoreDraftSupport();
        return ConfirmAsync(new { type = "publish-score-draft", expectedRevision = expectedRevision.ToString(),
            source = JsonSerializer.SerializeToElement(source, ContractJson.Options),
            score = JsonSerializer.SerializeToElement(score, ContractJson.Options),
            assignments = JsonSerializer.SerializeToElement(assignments, ContractJson.Options) }, ct);
    }

    public Task MapScoreDraftAsync(RoomScoreDraft draft, RoomScoreMemberMapping mapping, CancellationToken ct)
    {
        RequireScoreDraftSupport();
        return ConfirmAsync(new { type = "map-score-draft", draftId = draft.Id, draftRevision = draft.Revision.ToString(),
            mapping = JsonSerializer.SerializeToElement(mapping, ContractJson.Options) }, ct);
    }

    public Task ClearScoreDraftAsync(RoomScoreDraft draft, CancellationToken ct)
    {
        RequireScoreDraftSupport();
        return ConfirmAsync(new { type = "clear-score-draft", draftId = draft.Id, draftRevision = draft.Revision.ToString() }, ct);
    }

    private void RequireScoreDraftSupport()
    {
        if (!SupportsScoreDraft) throw new RoomRequestException("SCORE_DRAFT_UNSUPPORTED");
    }
}
