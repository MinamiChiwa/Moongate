using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.RoomServer.Domain;
using MabiTool.RoomServer.Protocol;
using Xunit;

namespace MabiTool.Rooms.Tests;

public sealed class RoomServerObservationTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var source = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "room-observation.v1.json")));
        return source.RootElement.GetProperty("cases").EnumerateArray().Select(value => new object[] { value.GetProperty("name").GetString()!, value.Clone() }).ToArray();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesFixedOriginalObservationRules(string name, JsonElement testCase)
    {
        var room = new RoomState("ABC123", Member("a"), 100);
        room.Members.Add("b", Member("b")); room.Members.Add("c", Member("c"));
        RoomRosterRow[] roster = [new("a", 1, "initiator", 1), new("b", 1, "joiner", 2), new("c", 1, "joiner", 3)];
        var ids = new Dictionary<string, string> { ["a"] = "101", ["b"] = "102", ["c"] = "103" };
        foreach (var member in room.Members.Values)
            RoomObservationRules.Accept(member, JsonSerializer.SerializeToElement(new
            {
                generation = "g", sequence = 1, status = "identified",
                identity = new { characterId = ids[member.Id], realmId = 3, nickname = "SameName" }
            }), 100);
        var index = 0;
        foreach (var step in testCase.GetProperty("steps").EnumerateArray())
        {
            var command = step.GetProperty("command"); var now = command.Number("now") ?? 100;
            string? error = null; bool? expired = null;
            try
            {
                switch (command.Text("action"))
                {
                    case "accept": RoomObservationRules.Accept(room.Members[command.Text("member")!], command.Field("message"), now); break;
                    case "clear": RoomObservationRules.Clear(room.Members[command.Text("member")!]); break;
                    case "expire": expired = RoomObservationRules.Expire(room, now); break;
                    case "bind": RoomObservationRules.Bind(room, command.Text("member"), command.Field("characterId"), command.Field("realmId"), now); break;
                    case "roster": roster = command.Field("roster").Deserialize<RoomRosterRow[]>(RoomProtocol.Json)!; break;
                    case "round": room.Round = Round(command.Text("mode")!, command.Text("phase")!, roster,
                        command.Field("baseline").MissingOrNull() ? [] : command.Field("baseline").Deserialize<string[]>()!); break;
                }
            }
            catch (RoomProtocolException failure) { error = failure.Code; }
            var observed = RoomObservationRules.ObservedCharacters(room, now);
            var actual = JsonSerializer.SerializeToNode(new
            {
                error, expired, observedCharacters = observed, feedback = RoomObservationRules.Feedback(room, roster, now),
                members = room.Members.Values.Select(member => new
                {
                    id = member.Id, nickname = member.Nickname, ready = member.Ready,
                    sequence = member.ObservationSequence, generation = member.ObservationGeneration, binding = member.CharacterBinding,
                    identity = RoomObservationRules.Identity(member, now, observed),
                    music = RoomObservationRules.Fresh(member, now) ? member.Observation!.Music : null
                })
            }, RoomProtocol.Json);
            var expected = JsonNode.Parse(step.GetProperty("expected").GetRawText());
            Assert.True(JsonNode.DeepEquals(expected, actual), name + " step " + index + "\nExpected: " + expected + "\nActual: " + actual);
            index++;
        }
    }

    private static RoomMemberState Member(string id) => new(id, id, "test-token", id == "a" ? "host" : "member")
    { Connection = new(new Peer()), Ready = true };
    private sealed class Peer : IRoomPeer
    {
        public bool Open => true;
        public void Send(byte[] bytes) { }
        public void Close(int status, string reason) { }
    }
    private static RoomRoundState Round(string mode, string phase, RoomRosterRow[] roster, string[] baseline) => new()
    {
        Id = "round", Mode = mode, Phase = phase, LeaderId = "a", CountdownMs = 500, RequestedCountdownMs = 500,
        ParticipantIds = ["a", "b", "c"], StartParticipantIds = ["a", "b", "c"], ManualJoinerIds = [],
        Start = new("action", "round", "start", 500, "epoch"), EarliestStartServerMs = 500, LatestStartServerMs = 500,
        DelayMs = 0, MinimumOffsetMs = 0, LeaderOffsetMs = 0, StopRule = "manual", TailHoldMs = 0,
        MusicBaseline = [], ObservationBaseline = baseline.ToHashSet(StringComparer.Ordinal), Roster = roster.ToArray()
    };
}
