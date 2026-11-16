using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.RoomServer.Protocol;

namespace MabiTool.RoomServer.Domain;

// 来源：MabiSync 2cb69b5，server/ensemble-observation.js；保留身份、时效及冲突判定。
internal static class RoomObservationRules
{
    internal static void Clear(RoomMemberState member)
    {
        member.Observation = null; member.CharacterBinding = null;
        member.ObservationGeneration = null; member.ObservationSequence = 0;
        member.RetiredObservationGenerations.Clear();
    }

    internal static void Accept(RoomMemberState member, JsonElement message, double now)
    {
        var generation = message.Text("generation"); var sequence = message.Number("sequence");
        var status = message.Text("status");
        if (generation is null || generation.Length is < 1 or > 64 || !RoomProtocol.SafeInteger(sequence) || sequence < 1 ||
            status is not ("identified" or "unavailable")) throw Bad();
        if (sequence <= member.ObservationSequence || member.RetiredObservationGenerations.Contains(generation))
            throw new RoomProtocolException("STALE_OBSERVATION");
        if (member.ObservationGeneration is { } old && old != generation && member.RetiredObservationGenerations.Count >= 1024)
        {
            member.Observation = null;
            throw new RoomProtocolException("OBSERVATION_RECONNECT_REQUIRED");
        }
        RoomObservation? observation = null;
        if (status == "identified")
        {
            var identity = Character(message.Field("identity"));
            RoomConcert? concert = null;
            if (!message.Field("concert").MissingOrNull())
            {
                var source = message.Field("concert");
                var ordered = RoomProtocol.Strings(source.Field("orderedCharacterIds"), "BAD_OBSERVATION");
                if (ordered.Length is < 1 or > 128 || ordered.Distinct(StringComparer.Ordinal).Count() != ordered.Length) throw Bad();
                var id = RoomProtocol.Identity(source.Field("id")); var host = RoomProtocol.Identity(source.Field("hostCharacterId"));
                foreach (var characterId in source.Field("orderedCharacterIds").EnumerateArray()) _ = RoomProtocol.Identity(characterId);
                if (ordered[0] != host || !ordered.Contains(identity.CharacterId)) throw Bad();
                RoomCharacter[]? members = null;
                if (source.Has("members"))
                {
                    var list = source.Field("members");
                    if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != ordered.Length) throw Bad();
                    members = list.EnumerateArray().Select(Character).ToArray();
                    if (members.Where((item, index) => item.CharacterId != ordered[index] || item.RealmId != identity.RealmId).Any()) throw Bad();
                }
                concert = new(id, host, ordered, members);
            }
            var session = message.Field("sessionId");
            if (!session.MissingOrNull() && (session.Text() is not { Length: <= 64 })) throw Bad();
            observation = new(generation, sequence!.Value, now, identity, concert, Music(message.Field("music"), identity), session.Text());
        }
        if (member.ObservationGeneration is { } previous && previous != generation) member.RetiredObservationGenerations.Add(previous);
        member.ObservationGeneration = generation; member.ObservationSequence = sequence!.Value; member.Observation = observation;
        if (observation is not null) member.Nickname = observation.Identity.Nickname;
    }

    private static RoomCharacter Character(JsonElement value)
    {
        var realm = value.Integer("realmId"); var name = value.Text("nickname")?.Trim();
        if (realm is null or < 0 || string.IsNullOrEmpty(name) || name.EnumerateRunes().Count() > 20 ||
            name.Any(ch => ch <= 31 || ch == 127)) throw Bad();
        return new(RoomProtocol.Identity(value.Field("characterId")), realm.Value, name);
    }

    private static RoomMusic? Music(JsonElement value, RoomCharacter identity)
    {
        if (value.MissingOrNull()) return null;
        var performer = RoomProtocol.Identity(value.Field("performerCharacterId")); var title = value.Text("title");
        var loop = value.Boolean("isLoop"); var channels = value.Field("selectedChannels");
        var sha = value.Field("scoreSha256"); var issue = value.Field("issue");
        if (performer != identity.CharacterId || title is null || title.Length > 512 || title.Any(ch => ch <= 31 || ch == 127) ||
            loop is null || channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() > 128 ||
            !sha.MissingOrNull() && !RoomProtocol.Hex(sha.Text(), 64) || !issue.MissingOrNull() && issue.Text() is not { Length: <= 256 }) throw Bad();
        var selectedChannels = new List<int>();
        foreach (var channel in channels.EnumerateArray())
        {
            if (channel.ValueKind != JsonValueKind.Number || !channel.TryGetInt32(out var index) || index is < 0 or >= 128 || selectedChannels.Contains(index)) throw Bad();
            selectedChannels.Add(index);
        }
        var whole = Duration(value.Field("wholeScoreDurationSeconds")); var selected = Duration(value.Field("selectedDurationSeconds"));
        if (selected is not null && (whole is null || selected > whole + .000001)) throw Bad();
        var estimatedField = value.Field("estimatedStartServerMs"); var estimated = estimatedField.Number();
        if (!estimatedField.MissingOrNull() && (estimated is null || value.Text("timingSource") != "tw-receive-offset-v1")) throw Bad();
        return new(RoomProtocol.Identity(value.Field("concertId")), performer, RoomProtocol.Identity(value.Field("hostCharacterId")),
            title, loop.Value, selectedChannels.ToArray(), sha.Text(), whole, selected, issue.Text(), estimated,
            estimated is null ? null : "tw-receive-offset-v1", estimated is not null && whole is > 0 and <= 86400 && selected is not null);
    }

    private static double? Duration(JsonElement value)
    {
        if (value.MissingOrNull()) return null;
        return value.Number() is { } number && number is >= 0 and <= 604800 ? number : throw Bad();
    }

    internal static bool Fresh(RoomMemberState member, double now) => member.Connected && member.Observation is { } value && now >= value.At && now - value.At <= 15000;
    internal static bool Expire(RoomState room, double now)
    {
        var changed = false;
        foreach (var member in room.Members.Values)
            if (member.Observation is not null && !Fresh(member, now)) { member.Observation = null; changed = true; }
        return changed;
    }
    internal static RoomCharacter[] ObservedCharacters(RoomState room, double now)
    {
        var found = new Dictionary<string, RoomCharacter>(StringComparer.Ordinal); var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in room.Members.Values.Where(member => Fresh(member, now)))
            foreach (var identity in member.Observation!.Concert?.Members ?? [])
            {
                var key = identity.RealmId + ":" + identity.CharacterId;
                if (found.TryGetValue(key, out var old) && old.Nickname != identity.Nickname) conflicts.Add(key);
                found[key] = identity;
            }
        return found.Where(item => !conflicts.Contains(item.Key)).Take(128).Select(item => item.Value).ToArray();
    }

    internal static RoomCharacter? Identity(RoomMemberState member, double now, RoomCharacter[] observed)
    {
        if (!member.Connected) return null;
        if (Fresh(member, now)) return member.Observation!.Identity;
        return member.CharacterBinding is { } binding
            ? observed.FirstOrDefault(item => item.CharacterId == binding.CharacterId && item.RealmId == binding.RealmId) : null;
    }

    internal static void Bind(RoomState room, string? memberId, JsonElement characterId, JsonElement realmId, double now)
    {
        if (memberId is null || !room.Members.TryGetValue(memberId, out var member)) throw new RoomProtocolException("MEMBER_NOT_FOUND");
        if (characterId.ValueKind == JsonValueKind.Null) { member.CharacterBinding = null; return; }
        var observed = ObservedCharacters(room, now);
        var identity = observed.FirstOrDefault(item => item.CharacterId == characterId.Text() && item.RealmId == realmId.Number());
        if (identity is null) throw new RoomProtocolException("CHARACTER_NOT_OBSERVED");
        if (Fresh(member, now) && (member.Observation!.Identity.CharacterId != identity.CharacterId || member.Observation.Identity.RealmId != identity.RealmId))
            throw new RoomProtocolException("CAPTURE_IDENTITY_CONFLICT");
        member.CharacterBinding = new(identity.CharacterId, identity.RealmId); member.Nickname = identity.Nickname;
    }

    internal static Dictionary<string, JsonObject> Feedback(RoomState room, RoomRosterRow[] layout, double now)
    {
        var result = room.Members.Keys.ToDictionary(key => key, _ => new JsonObject { ["state"] = "unconfirmed", ["text"] = "等待识别加入顺序" }, StringComparer.Ordinal);
        var round = room.Round; var active = round is not null && !round.Terminal;
        if (active && round!.Mode != "ensemble") return result;
        var planned = active ? round!.Roster : layout; var observed = ObservedCharacters(room, now);
        var identities = room.Members.Values.ToDictionary(member => member.Id, member => Identity(member, now, observed), StringComparer.Ordinal);
        var owners = identities.Values.OfType<RoomCharacter>().GroupBy(identity => identity.RealmId + ":" + identity.CharacterId)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var group in planned.Select(row => row.Group).Distinct())
        {
            var rows = planned.Where(row => row.Group == group).OrderBy(row => row.Role == "initiator" ? 0 : 1).ThenBy(row => row.Order).ToArray();
            if (rows.Count(row => row.Role == "initiator") != 1 || !identities.TryGetValue(rows[0].MemberId, out var leader) || leader is null || owners[leader.RealmId + ":" + leader.CharacterId] != 1) continue;
            var expected = rows.Select(row => identities.GetValueOrDefault(row.MemberId)).ToArray();
            if (expected.Any(identity => identity is null || identity.RealmId != leader.RealmId || owners[identity.RealmId + ":" + identity.CharacterId] != 1)) continue;
            var reports = rows.Select(row => room.Members.GetValueOrDefault(row.MemberId)).OfType<RoomMemberState>()
                .Where(member => Fresh(member, now) && member.Observation?.Concert is not null).Select(member => member.Observation!).ToArray();
            if (reports.Length == 0 || reports.Any(value => value.Identity.RealmId != leader.RealmId || value.Concert!.HostCharacterId != leader.CharacterId)) continue;
            var concert = reports[0].Concert!;
            if (reports.Any(value => value.Concert!.Id != concert.Id || value.Concert.HostCharacterId != concert.HostCharacterId || !value.Concert.OrderedCharacterIds.SequenceEqual(concert.OrderedCharacterIds))) continue;
            if (round?.ObservationBaseline.Contains(leader.RealmId + ":" + concert.Id + ":" + concert.HostCharacterId) == true ||
                concert.OrderedCharacterIds.Any(value => !expected.Any(identity => identity!.CharacterId == value))) continue;
            for (var index = 0; index < rows.Length; index++)
            {
                var at = Array.IndexOf(concert.OrderedCharacterIds, expected[index]!.CharacterId);
                result[rows[index].MemberId] = at < 0 ? new JsonObject { ["state"] = "pending", ["text"] = "尚未加入" } :
                    new JsonObject { ["state"] = at == index ? "correct" : "wrong", ["text"] = at == index ? "加入顺序正确" : "加入顺序不符", ["actualOrder"] = at + 1 };
            }
        }
        return result;
    }

    private static RoomProtocolException Bad() => new("BAD_OBSERVATION");
}
