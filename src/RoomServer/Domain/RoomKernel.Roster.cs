using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.RoomServer.Protocol;

namespace MabiTool.RoomServer.Domain;

internal sealed partial class RoomKernel
{
    private static RoomCapabilities Capabilities(JsonElement value)
    {
        var simulation = value.Boolean("simulation"); var start = value.Boolean("canStart"); var stop = value.Boolean("canStop");
        var offset = value.Field("timingOffsetMs").MissingOrNull() ? 0 : value.Integer("timingOffsetMs");
        var role = value.Field("musicRole").MissingOrNull() ? "initiator" : value.Text("musicRole"); var mode = value.Field("readyMode").Text();
        var rtt = value.Field("clockRoundTripMs").MissingOrNull() ? 0 : value.Number("clockRoundTripMs");
        var activation = value.Field("windowActivationMs").MissingOrNull() ? 0 : value.Integer("windowActivationMs");
        if (simulation is null || start is null || stop is null || offset is null or < -2000 or > 2000 ||
            role is not ("initiator" or "joiner") || !value.Field("readyMode").MissingOrNull() && !Mode(mode) ||
            rtt is null or < 0 or > 30000 || activation is null or < 0 or > 5000) throw new RoomProtocolException("BAD_CAPABILITIES");
        return new(simulation.Value, start.Value, stop.Value, offset.Value, role, mode, rtt.Value, activation.Value);
    }
    private static RoomRosterRow[] Roster(RoomState room)
    {
        var members = room.Members.Values.OrderBy(member => member.Group).ThenBy(member => member.MusicRole == "initiator" ? 0 : 1).ThenBy(member => member.Position).ToArray();
        return members.Select(member => new RoomRosterRow(member.Id, member.Group, member.MusicRole,
            members.Where(other => other.Group == member.Group).TakeWhile(other => other.Id != member.Id).Count() + 1)).ToArray();
    }
    private static void RevokeEnsembleReadiness(RoomState room)
    {
        foreach (var member in room.Members.Values) if (member.Capabilities.ReadyMode is null or "ensemble") member.Ready = false;
    }
    private void EditEnsemble(RoomState room, JsonElement message)
    {
        RequireIdle(room); var member = FindMember(room, message.Text("memberId"));
        if (message.Text("type") == "set-ensemble-role")
        {
            var role = message.Text("musicRole"); if (role is not ("initiator" or "joiner")) throw new RoomProtocolException("BAD_CAPABILITIES");
            if (role == "initiator") foreach (var other in room.Members.Values) if (other.Id != member.Id && other.Group == member.Group) other.MusicRole = "joiner";
            member.MusicRole = role;
        }
        else
        {
            RoomMemberState? target = message.Has("targetMemberId") ? FindMember(room, message.Text("targetMemberId")) : null;
            if (target == member) return;
            var group = target is null ? message.Integer("group") : target.Group;
            if (group is null or < 1 or > 24) throw new RoomProtocolException("BAD_ENSEMBLE_GROUP");
            if (target is not null && message.Text("placement") is not ("before" or "after")) throw new RoomProtocolException("BAD_MESSAGE");
            if (member.Group != group && room.Members.Values.Any(other => other.Id != member.Id && other.Group == group && other.MusicRole == "initiator")) member.MusicRole = "joiner";
            member.Group = group.Value; var list = Roster(room).Select(row => room.Members[row.MemberId]).Where(other => other != member).ToList();
            var at = target is null ? list.Count : list.IndexOf(target) + (message.Text("placement") == "after" ? 1 : 0);
            list.Insert(at, member); for (var index = 0; index < list.Count; index++) list[index].Position = index;
        }
        RevokeEnsembleReadiness(room);
    }
    private void EditMember(RoomState room, JsonElement message)
    {
        RequireIdle(room); var member = FindMember(room, message.Text("memberId"));
        if (message.Text("type") == "set-member-selection")
        { member.Selected = message.Boolean("selected") ?? throw new RoomProtocolException("BAD_MESSAGE"); return; }
        if (message.Has("group") && message.Integer("group") is null or < 1 or > 24) throw new RoomProtocolException("BAD_ENSEMBLE_GROUP");
        if (message.Has("order") && message.Integer("order") is null or < 1 or > 24) throw new RoomProtocolException("BAD_ENSEMBLE_ORDER");
        if (message.Has("musicRole") && message.Text("musicRole") is not ("initiator" or "joiner")) throw new RoomProtocolException("BAD_CAPABILITIES");
        if (message.Has("instrument") && (message.Text("instrument") is not { } instrument || instrument.Trim().EnumerateRunes().Count() > 40 ||
            instrument.EnumerateRunes().Any(rune => System.Text.Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format))) throw new RoomProtocolException("BAD_INSTRUMENT");
        if (!new[] { "group", "order", "musicRole", "instrument" }.Any(name => message.Has(name))) throw new RoomProtocolException("BAD_MESSAGE");
        if (message.Has("group") && message.Integer("group") != member.Group)
        {
            var edit = JsonSerializer.SerializeToElement(new { type = "move-ensemble-member", memberId = member.Id, group = message.Integer("group") }, RoomProtocol.Json);
            EditEnsemble(room, edit);
        }
        if (message.Has("musicRole")) EditEnsemble(room, JsonSerializer.SerializeToElement(new { type = "set-ensemble-role", memberId = member.Id, musicRole = message.Text("musicRole") }, RoomProtocol.Json));
        if (message.Has("order"))
        {
            var others = Roster(room).Where(row => row.Group == member.Group && row.MemberId != member.Id).ToArray();
            var all = Roster(room).Select(row => room.Members[row.MemberId]).Where(other => other != member).ToList();
            var position = Math.Min(message.Integer("order")!.Value - 1, others.Length);
            var insertion = position < others.Length ? all.FindIndex(other => other.Id == others[position].MemberId) :
                others.Length > 0 ? all.FindIndex(other => other.Id == others[^1].MemberId) + 1 : all.Count;
            all.Insert(insertion, member); for (var index = 0; index < all.Count; index++) all[index].Position = index;
        }
        if (message.Has("instrument")) member.Instrument = message.Text("instrument")!.Trim();
        RevokeEnsembleReadiness(room);
    }

    private JsonObject View(RoomState room)
    {
        var layout = Roster(room); var at = Now; var observed = RoomObservationRules.ObservedCharacters(room, at);
        var feedback = RoomObservationRules.Feedback(room, layout, at);
        var members = new JsonArray();
        foreach (var member in room.Members.Values)
            members.Add(new JsonObject
            {
                ["id"] = member.Id, ["nickname"] = member.Nickname, ["role"] = member.Role,
                ["ensembleGroup"] = member.Group, ["ensembleRole"] = member.MusicRole, ["ensembleOrder"] = layout.First(row => row.MemberId == member.Id).Order,
                ["connected"] = member.Connected, ["ready"] = member.Ready, ["capabilities"] = RoomProtocol.Node(member.Capabilities),
                ["selected"] = member.Selected, ["instrument"] = member.Instrument,
                ["gameIdentity"] = RoomProtocol.Node(RoomObservationRules.Identity(member, at, observed)),
                ["music"] = RoomProtocol.Node(RoomObservationRules.Fresh(member, at) ? member.Observation!.Music : null),
                ["ensembleFeedback"] = feedback[member.Id]
            });
        var result = new JsonObject { ["code"] = room.Code, ["hostId"] = room.HostId, ["leaderId"] = room.LeaderId, ["mode"] = room.Mode,
            ["observedCharacters"] = RoomProtocol.Node(observed), ["members"] = members, ["performance"] = RoundView(room.Round) };
        if (room.ScoreDraft is not null)
        {
            result["scoreDraft"] = System.Text.Json.JsonSerializer.SerializeToNode(room.ScoreDraft, MabiTool.Contracts.ContractJson.Options);
            result["scoreMappings"] = System.Text.Json.JsonSerializer.SerializeToNode(room.Members.Values.Select(member => RoomScoreDraftRules.Mapping(room, member)), MabiTool.Contracts.ContractJson.Options);
        }
        result["scoreDraftRevision"] = room.ScoreDraftRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }
    private static JsonObject? RoundView(RoomRoundState? round)
    {
        if (round is null) return null;
        var result = new JsonObject
        {
            ["id"] = round.Id, ["mode"] = round.Mode, ["phase"] = round.Phase, ["leaderId"] = round.LeaderId,
            ["countdownMs"] = round.CountdownMs, ["requestedCountdownMs"] = round.RequestedCountdownMs,
            ["earliestStartServerMs"] = round.EarliestStartServerMs, ["latestStartServerMs"] = round.LatestStartServerMs,
            ["participantIds"] = RoomProtocol.Node(round.ParticipantIds), ["startParticipantIds"] = RoomProtocol.Node(round.StartParticipantIds),
            ["manualJoinerIds"] = RoomProtocol.Node(round.ManualJoinerIds), ["startAction"] = RoomProtocol.Node(round.Start),
            ["stopTimingSource"] = round.Stop is null ? null : round.StopTimingSource ?? "user-duration",
            ["stopRule"] = round.StopRule, ["tailHoldMs"] = round.TailHoldMs, ["cycleStopStatus"] = round.CycleStopStatus,
            ["cycleScore"] = RoomProtocol.Node(round.CycleBinding), ["stopCommitted"] = round.Stop is not null && round.Committed.Contains(round.Stop.Id),
            ["normalEndRule"] = round.Mode == "emote" ? "none" : "leader-first-cycle", ["firstCycleTimingVerified"] = false,
            ["stopTimingUnavailable"] = round.Mode != "emote" && round.Stop is null ? "playback-timing-unverified" : null,
            ["preparedMemberIds"] = RoomProtocol.Node(round.ParticipantIds.Where(round.Prepared).ToArray()),
            ["receipts"] = new JsonArray(round.Receipts.Values.Select(value => (JsonNode)value.DeepClone()).ToArray()),
            ["interruption"] = round.Interruption?.DeepClone(), ["ensembleRoster"] = RoomProtocol.Node(round.Roster), ["automaticGameEndVerified"] = false
        };
        if (round.Stop is not null) result["stopAction"] = RoomProtocol.Node(round.Stop);
        if (round.ScoreDraftId is not null)
        { result["scoreDraftId"] = round.ScoreDraftId; result["scoreDraftRevision"] = round.ScoreDraftRevision?.ToString(); }
        return result;
    }
}
