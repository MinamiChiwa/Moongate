using MabiTool.Contracts;

namespace MabiTool.Modules.Rooms.Client;

public sealed partial class RoomClient
{
    public Task PrepareRoundAsync(RoomRoundRequest request, CancellationToken ct = default)
    {
        if (request.StopRule?.KnownValue == MusicStopRule.FirstCycle && !SupportsFirstCycleStop ||
            request.TailHoldMs is > 0 && !SupportsFirstCycleTail)
            throw new RoomRequestException("STOP_RULE_UNSUPPORTED");
        var draft = request.Mode == "emote" ? null : ScoreDraft;
        return ConfirmAsync(new { type = "prepare", request.Mode, request.ParticipantIds, request.CountdownMs,
            stopRule = request.StopRule?.RawValue, request.StopAfterMs, request.TailHoldMs,
            draftId = draft?.Id, draftRevision = draft?.Revision.ToString() }, ct);
    }

    public Task ControlRoundAsync(string roundId, RoomRoundControlRequest request, CancellationToken ct = default) => request.Action.KnownValue switch
    {
        RoomRoundControl.StopAll => ConfirmAsync(new { type = "stop-all", sessionId = roundId }, ct),
        RoomRoundControl.Cancel => ConfirmAsync(new { type = "cancel", sessionId = roundId }, ct),
        RoomRoundControl.Continue => ConfirmAsync(new { type = "decide", sessionId = roundId, decision = "continue" }, ct),
        RoomRoundControl.Interrupt => ConfirmAsync(new { type = "leader-status", sessionId = roundId,
            status = "interrupted", evidenceSource = "player-report", detail = request.Reason }, ct),
        _ => throw new RoomRequestException("BAD_MESSAGE")
    };
}
