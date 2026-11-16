using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.RoomServer.Protocol;

namespace MabiTool.RoomServer.Domain;

internal sealed partial class RoomKernel
{
    private void Prepare(RoomState room, JsonElement message)
    {
        RequireIdle(room);
        var mode = message.Text("mode"); var ids = RoomProtocol.Strings(message.Field("participantIds"), "BAD_PARTICIPANTS");
        if (!Mode(mode) || ids.Length is < 1 or > 24 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new RoomProtocolException("BAD_PARTICIPANTS");
        if (mode != "emote" && !ids.Contains(room.LeaderId)) throw new RoomProtocolException("LEADER_NOT_PARTICIPATING");
        if (mode != "emote" && room.ScoreDraft is not null)
        {
            RequireScoreDraft(room, message);
            var pending = ids.Where(id => !room.Members.TryGetValue(id, out var participant) || !RoomScoreDraftRules.Ready(room, participant)).ToArray();
            if (pending.Length != 0) throw new RoomProtocolException("SCORE_MAPPING_REQUIRED", new JsonObject { ["memberIds"] = RoomProtocol.Node(pending) });
        }
        var countdown = message.Field("countdownMs").MissingOrNull() ? 5000 : message.Integer("countdownMs");
        if (countdown is null or < 1000 or > 60000) throw new RoomProtocolException("BAD_COUNTDOWN");
        var hasDuration = message.Has("stopAfterMs"); var duration = message.Integer("stopAfterMs");
        var stopRule = message.Field("stopRule").MissingOrNull() ? hasDuration ? "duration" : "manual" : message.Text("stopRule");
        if (stopRule is not ("manual" or "duration" or "first-cycle") || stopRule == "first-cycle" && hasDuration) throw new RoomProtocolException("BAD_STOP_RULE");
        if (mode == "emote" && (stopRule == "first-cycle" || message.Has("tailHoldMs") || hasDuration)) throw new RoomProtocolException("STOP_NOT_APPLICABLE");
        var tail = message.Has("tailHoldMs") ? message.Integer("tailHoldMs") : 0;
        if (tail is null or < 0 or > 10000 || stopRule != "first-cycle" && message.Has("tailHoldMs")) throw new RoomProtocolException("BAD_TAIL_HOLD");
        if (hasDuration && duration is null or < 1 or > 86400000) throw new RoomProtocolException("BAD_DURATION");
        var manual = mode == "ensemble" ? ids.Where(id => room.Members.GetValueOrDefault(id)?.MusicRole == "joiner").ToArray() : [];
        var starters = ids.Except(manual, StringComparer.Ordinal).ToArray();
        if (starters.Length == 0) throw new RoomProtocolException("NO_ENSEMBLE_INITIATOR");
        var missing = ids.Where(id => !room.Members.TryGetValue(id, out var member) || !member.Connected || id != room.HostId && !member.Ready ||
            member.Capabilities.ReadyMode is { } readyMode && readyMode != mode || starters.Contains(id) && !member.Capabilities.CanStart || mode != "emote" && !member.Capabilities.CanStop).ToArray();
        if (missing.Length != 0) throw new RoomProtocolException("MEMBER_NOT_READY", new JsonObject { ["memberIds"] = RoomProtocol.Node(missing) });
        var startOffsets = starters.Select(id => room.Members[id].Capabilities.TimingOffsetMs).ToArray();
        var advance = Math.Max(0, -startOffsets.Min()); var delay = Math.Max(0, startOffsets.Max());
        var rtt = ids.Max(id => room.Members[id].Capabilities.ClockRoundTripMs); var activation = ids.Max(id => room.Members[id].Capabilities.WindowActivationMs);
        var preparation = Math.Max(500, activation + Math.Min(5000, (int)Math.Ceiling(rtt * 3 + 250)));
        var effective = Math.Max(countdown.Value, advance + preparation); var idValue = Guid.NewGuid().ToString(); var target = Now + effective;
        RoomAction Action(string kind, double at) => new(Guid.NewGuid().ToString(), idValue, kind, at, Epoch)
            { ScoreDraftId = mode == "emote" ? null : room.ScoreDraft?.Id, ScoreDraftRevision = mode == "emote" ? null : room.ScoreDraft?.Revision.ToString() };
        var round = new RoomRoundState
        {
            Id = idValue, Mode = mode!, LeaderId = mode == "emote" ? null : room.LeaderId, CountdownMs = effective,
            RequestedCountdownMs = countdown.Value, ParticipantIds = ids, StartParticipantIds = starters, ManualJoinerIds = manual,
            Start = Action("start", target), Stop = hasDuration ? Action("stop", target + duration!.Value) : null,
            EarliestStartServerMs = target - advance, LatestStartServerMs = target + delay,
            DelayMs = Math.Max(0, ids.Max(id => room.Members[id].Capabilities.TimingOffsetMs)),
            MinimumOffsetMs = Math.Min(0, ids.Min(id => room.Members[id].Capabilities.TimingOffsetMs)),
            LeaderOffsetMs = room.Members[room.LeaderId].Capabilities.TimingOffsetMs,
            StopRule = stopRule!, TailHoldMs = tail.Value, CycleStopStatus = stopRule == "first-cycle" ? "waiting-for-score" : null,
            ScoreDraftId = mode == "emote" ? null : room.ScoreDraft?.Id, ScoreDraftRevision = mode == "emote" ? null : room.ScoreDraft?.Revision,
            MusicBaseline = room.Members.Values.Where(member => member.Observation?.Music is not null)
                .Select(member => member.Observation!.Identity.RealmId + ":" + member.Observation.Music!.ConcertId).ToHashSet(StringComparer.Ordinal),
            ObservationBaseline = room.Members.Values.Where(member => member.Observation?.Concert is not null)
                .Select(member => member.Observation!.Identity.RealmId + ":" + member.Observation.Concert!.Id + ":" + member.Observation.Concert.HostCharacterId).ToHashSet(StringComparer.Ordinal),
            Roster = mode == "ensemble" ? Roster(room).Where(row => ids.Contains(row.MemberId)).ToArray() : []
        };
        Retire(room); room.Round = round;
        if (round.ScoreDraftId is null) Participants(room, round, new { type = "prepare-session", sessionId = round.Id, serverEpoch = Epoch, mode }, manual);
        else Participants(room, round, new { type = "prepare-session", sessionId = round.Id, serverEpoch = Epoch, mode,
            scoreDraftId = round.ScoreDraftId, scoreDraftRevision = round.ScoreDraftRevision?.ToString() }, manual);
        foreach (var action in round.Actions)
        {
            round.Acks[action.Id] = new(StringComparer.Ordinal);
            Participants(room, round, new { type = "prepare-action", action, mode }, round.ActionMembers(action));
        }
        round.Timers.Add(Later(() =>
        {
            if (room.Round != round || round.Phase != "preparing") return;
            Cancel(room, new JsonObject { ["source"] = "prepare-timeout", ["memberIds"] = RoomProtocol.Node(round.ParticipantIds.Where(id => !round.Prepared(id)).ToArray()) }); Broadcast(room);
        }, round.EarliestStartServerMs - Now - 50));
    }

    private void Acknowledge(RoomState room, RoomMemberState member, JsonElement message)
    {
        var id = message.Text("actionId");
        if (id is not null && room.RetiredActions.GetValueOrDefault(id)?.Members.Contains(member.Id) == true) return;
        var round = room.Round; var action = round?.Actions.FirstOrDefault(action => action.Id == id);
        if (action is null && id is not null && round?.CancelledActions.Contains(id) == true && round.ParticipantIds.Contains(member.Id)) return;
        if (round is null || action is null || !round.ActionMembers(action).Contains(member.Id)) throw new RoomProtocolException("STALE_ACTION");
        var ready = message.Boolean("ready");
        if (round.Phase == "cancelled" && ready == false) return;
        if (round.Terminal) throw new RoomProtocolException("STALE_ACTION");
        if (round.Phase != "preparing")
        {
            if (round.StopRule != "first-cycle" || action != round.Stop || round.Committed.Contains(action.Id)) return;
            if (ready is null) throw new RoomProtocolException("BAD_MESSAGE");
            if (!ready.Value) { AbandonCycleStop(room, round, "participant-not-ready"); return; }
            round.Acks[action.Id].Add(member.Id); CommitCycleStop(room, round); return;
        }
        if (ready is null) throw new RoomProtocolException("BAD_MESSAGE");
        if (!ready.Value) { Cancel(room, new JsonObject { ["source"] = "participant-not-ready", ["memberId"] = member.Id, ["detail"] = message.Field("error").Display() }); return; }
        round.Acks[action.Id].Add(member.Id); CommitWhenPrepared(room, round);
    }

    private void AcknowledgeSession(RoomState room, RoomMemberState member, JsonElement message)
    {
        var id = message.Text("sessionId");
        if (id is not null && room.RetiredSessionAcks.GetValueOrDefault(id)?.Contains(member.Id) == true) return;
        var round = room.Round;
        if (round is null || round.Id != id || !round.ManualJoinerIds.Contains(member.Id)) throw new RoomProtocolException("STALE_SESSION");
        var ready = message.Boolean("ready");
        if (round.Phase == "cancelled" && ready == false) return;
        if (round.Terminal) throw new RoomProtocolException("STALE_SESSION");
        if (round.Phase != "preparing") return;
        if (ready is null) throw new RoomProtocolException("BAD_MESSAGE");
        if (!ready.Value) { Cancel(room, new JsonObject { ["source"] = "participant-not-ready", ["memberId"] = member.Id, ["detail"] = message.Field("error").Display() }); return; }
        round.SessionAcks.Add(member.Id); CommitWhenPrepared(room, round);
    }

    private void CommitWhenPrepared(RoomState room, RoomRoundState round)
    {
        if (!round.ParticipantIds.All(round.Prepared)) return;
        if (Now + 50 >= round.EarliestStartServerMs) { Cancel(room, new JsonObject { ["source"] = "prepare-too-late" }); return; }
        round.ClearTimers(); round.Phase = "committed";
        foreach (var action in round.Actions) { round.Committed.Add(action.Id); Participants(room, round, new { type = "commit-action", action }, round.ActionMembers(action)); }
        round.Timers.Add(Later(() =>
        {
            if (room.Round != round || round.Phase != "committed") return;
            round.Phase = round.Mode == "emote" ? "stopped" : "performing"; Broadcast(room);
        }, round.LatestStartServerMs - Now));
        if (round.Stop is not null) FinishScheduledStop(room, round);
    }

    private void ObserveCycle(RoomState room, RoomMemberState member)
    {
        var round = room.Round;
        if (round is null || round.StopRule != "first-cycle" || round.Terminal || member.Id != round.LeaderId) return;
        var observation = member.Observation; var music = observation?.Music;
        if (round.CycleBinding is { } binding)
        {
            var same = observation?.Generation == binding.Generation && observation.Identity.CharacterId == binding.CharacterId && observation.Identity.RealmId == binding.RealmId &&
                music?.ConcertId == binding.ConcertId && music.ScoreSha256 == binding.ScoreSha256 && music.WholeScoreDurationSeconds == binding.DurationSeconds;
            if (!same && !round.CycleInterruptionReported && Now < binding.EstimatedEndServerMs) { round.CycleInterruptionReported = true; Interrupt(room, member, "game-observation"); }
            return;
        }
        if (round.Phase is not ("committed" or "performing")) return;
        if (observation is null || observation.SessionId != round.Id || music?.AutomaticStopAvailable != true) { round.CycleStopStatus = "waiting-for-score"; return; }
        var start = music.EstimatedStartServerMs!.Value;
        if (round.MusicBaseline.Contains(observation.Identity.RealmId + ":" + music.ConcertId) || start < round.EarliestStartServerMs - 250 || start > Now + 60000) return;
        var end = start + music.WholeScoreDurationSeconds!.Value * 1000; var target = end + round.TailHoldMs - round.LeaderOffsetMs;
        round.CycleBinding = new(observation.Generation, observation.Identity.RealmId, observation.Identity.CharacterId, music.ConcertId,
            music.ScoreSha256, music.Title, music.WholeScoreDurationSeconds.Value, end);
        if (target + round.MinimumOffsetMs <= Now + 50) { round.CycleStopStatus = "too-late"; return; }
        round.Stop = new(Guid.NewGuid().ToString(), round.Id, "stop", target, Epoch);
        round.StopTimingSource = "leader-capture-estimate"; round.CycleStopStatus = "preparing-stop"; round.Acks[round.Stop.Id] = new(StringComparer.Ordinal);
        Participants(room, round, new { type = "prepare-action", action = round.Stop, mode = round.Mode });
        round.CycleTimer = Later(() => { if (room.Round == round) { AbandonCycleStop(room, round, "prepare-timeout"); Broadcast(room); } }, target + round.MinimumOffsetMs - Now - 50);
        round.Timers.Add(round.CycleTimer);
    }

    private void AbandonCycleStop(RoomState room, RoomRoundState round, string reason)
    {
        if (round.Stop is not { } stop || round.Committed.Contains(stop.Id)) return;
        Participants(room, round, new { type = "cancel-action", actionId = stop.Id, sessionId = round.Id, serverEpoch = Epoch });
        round.CancelledActions.Add(stop.Id); round.Acks.Remove(stop.Id); round.Stop = null; round.CycleStopStatus = reason;
        round.CycleTimer?.Dispose(); round.CycleTimer = null;
    }
    private void CommitCycleStop(RoomState room, RoomRoundState round)
    {
        if (round.Stop is not { } stop || round.Committed.Contains(stop.Id) || !round.ParticipantIds.All(id => round.Acks[stop.Id].Contains(id))) return;
        if (Now + 50 >= stop.TargetServerMs + round.MinimumOffsetMs) { AbandonCycleStop(room, round, "too-late"); return; }
        round.CycleTimer?.Dispose(); round.CycleTimer = null; round.Committed.Add(stop.Id); round.CycleStopStatus = "scheduled";
        Participants(room, round, new { type = "commit-action", action = stop }); FinishScheduledStop(room, round);
    }
    private void FinishScheduledStop(RoomState room, RoomRoundState round)
    {
        round.Timers.Add(Later(() =>
        {
            if (room.Round != round || round.Terminal) return;
            round.Phase = "stopped"; ConsumeReadiness(room, round); Broadcast(room);
        }, round.Stop!.TargetServerMs + round.DelayMs - Now));
    }
    private static void ConsumeReadiness(RoomState room, RoomRoundState round)
    {
        if (round.Mode == "emote") return;
        foreach (var id in round.ParticipantIds) if (room.Members.TryGetValue(id, out var member))
        { member.Ready = false; if (id == room.HostId) member.Capabilities = member.Capabilities with { CanStart = false }; }
    }
    private static void Retire(RoomState room)
    {
        if (room.Round is not { } round) return;
        round.ClearTimers(); room.RetiredSessionAcks[round.Id] = round.ManualJoinerIds.ToHashSet(StringComparer.Ordinal);
        while (room.RetiredSessionAcks.Count > 64) room.RetiredSessionAcks.Remove(room.RetiredSessionAcks.Keys.First());
        foreach (var id in round.Actions.Select(action => action.Id).Concat(round.Committed).Concat(round.CancelledActions).Distinct())
        {
            var action = round.Actions.FirstOrDefault(action => action.Id == id);
            room.RetiredActions[id] = new((action is null ? round.ParticipantIds : round.ActionMembers(action)).ToHashSet(StringComparer.Ordinal), round.Committed.Contains(id));
        }
        while (room.RetiredActions.Count > 128) room.RetiredActions.Remove(room.RetiredActions.Keys.First());
    }
    private void Cancel(RoomState room, JsonObject detail)
    {
        if (room.Round is not { Terminal: false } round) return;
        round.ClearTimers(); round.Phase = "cancelled"; round.Interruption = detail; ConsumeReadiness(room, round);
        foreach (var action in round.Actions) Participants(room, round, new { type = "cancel-action", actionId = action.Id, sessionId = round.Id, serverEpoch = Epoch }, round.ActionMembers(action));
        Participants(room, round, new { type = "cancel-session", sessionId = round.Id, serverEpoch = Epoch }, round.ManualJoinerIds);
    }
    private void StopAll(RoomState room)
    {
        if (room.Round is not { Terminal: false } round) return;
        if (round.Mode == "emote") { Cancel(room, new JsonObject { ["source"] = "host-cancel" }); return; }
        var id = Guid.NewGuid().ToString(); round.Committed.Add(id);
        Participants(room, round, new { type = "stop-now", id, sessionId = round.Id, serverEpoch = Epoch });
        round.ClearTimers(); round.Phase = "stopped"; ConsumeReadiness(room, round);
        foreach (var action in round.Actions) Participants(room, round, new { type = "cancel-action", actionId = action.Id, sessionId = round.Id, serverEpoch = Epoch }, round.ActionMembers(action));
    }
    private void Interrupt(RoomState room, RoomMemberState member, string source)
    {
        if (room.Round is not { Terminal: false } round || !round.ParticipantIds.Contains(member.Id)) return;
        if (round.Mode == "emote" && round.Receipts.GetValueOrDefault(round.Start.Id + ":" + member.Id)?["status"]?.GetValue<string>() == "executed") return;
        var detail = new JsonObject { ["memberId"] = member.Id, ["source"] = source };
        if (round.Mode == "emote" || round.Phase == "preparing") Cancel(room, detail);
        else { if (round.StopRule == "first-cycle") AbandonCycleStop(room, round, "interrupted-before-stop-commit"); round.Phase = "interrupted"; round.Interruption = detail; }
    }
    private void Receipt(RoomState room, RoomMemberState member, JsonElement message)
    {
        var status = message.Text("status"); var id = message.Text("actionId");
        if (status is not ("executed" or "cancelled" or "missed" or "failed") || message.Has("actualServerMs") && message.Number("actualServerMs") is null) throw new RoomProtocolException("BAD_RECEIPT");
        if (id is not null && room.RetiredActions.GetValueOrDefault(id) is { } retired && retired.Members.Contains(member.Id) && (retired.Committed || status == "cancelled")) return;
        var round = room.Round; var action = round?.Actions.FirstOrDefault(action => action.Id == id);
        if (round is null || id is null || !round.ParticipantIds.Contains(member.Id) || action is not null && !round.ActionMembers(action).Contains(member.Id) ||
            !round.Committed.Contains(id) && !(status == "cancelled" && (action is not null || round.CancelledActions.Contains(id)))) throw new RoomProtocolException("STALE_ACTION");
        var key = id + ":" + member.Id;
        if (round.Receipts.ContainsKey(key)) return;
        var receipt = new JsonObject { ["actionId"] = id, ["memberId"] = member.Id, ["status"] = status,
            ["detail"] = message.Field("detail").Display(), ["simulated"] = member.Capabilities.Simulation || message.Boolean("simulated") != false, ["gameResponseVerified"] = false };
        if (message.Has("actualServerMs")) receipt["actualServerMs"] = message.Number("actualServerMs"); round.Receipts[key] = receipt;
        if (round.Mode != "emote" && status is "failed" or "missed" && !round.Terminal)
        { Interrupt(room, member, "input-failure"); if (round.Interruption is not null) round.Interruption["detail"] = message.Field("detail").Display(); }
    }
    private void LeaderStatus(RoomState room, RoomMemberState member, JsonElement message)
    {
        if (room.Round is not { } round || round.Id != message.Text("sessionId") || member.Id != round.LeaderId || round.Terminal) throw new RoomProtocolException("STALE_SESSION");
        if (round.Mode == "emote") throw new RoomProtocolException("BAD_MESSAGE");
        var status = message.Text("status"); if (status == "natural-end") throw new RoomProtocolException("AUTOMATIC_END_UNVERIFIED");
        if (status is not ("interrupted" or "disconnected")) throw new RoomProtocolException("BAD_MESSAGE");
        Interrupt(room, member, "game-observation");
        if (round.Interruption is not null)
        { round.Interruption["evidenceSource"] = message.Text("evidenceSource") == "player-report" ? "player-report" : "unverified-report"; round.Interruption["status"] = status; round.Interruption["detail"] = message.Field("detail").Display(); }
    }
    private void Decide(RoomState room, JsonElement message)
    {
        if (room.Round is not { Phase: "interrupted" } round) throw new RoomProtocolException("NO_INTERRUPTION");
        if (message.Text("decision") == "stop") StopAll(room);
        else if (message.Text("decision") == "continue") { round.Phase = Now >= round.Start.TargetServerMs ? "performing" : "committed"; round.Interruption = null; }
        else throw new RoomProtocolException("BAD_MESSAGE");
    }
}
