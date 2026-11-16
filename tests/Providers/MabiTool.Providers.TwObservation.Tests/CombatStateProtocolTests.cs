using System.Buffers.Binary;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>本机战斗状态 170196：解析、脱敏实采回放的脱战分段（跑开、组队、死亡、Boss）与信号缺失时的原有分段。</summary>
public sealed class CombatStateProtocolTests
{
    private const ulong Base = 1_000_000;

    [Fact]
    public void LocalStatesAreParsedInStreamOrderAndOtherEntitiesAreIgnored()
    {
        var replay = new Replay(combatState: true);
        replay.Accept(Tw20261110CombatProtocol.SetCombatState, State(replay.Local, 2), Base, 100);
        replay.Accept(Tw20261110CombatProtocol.SetCombatState, State(424242, 2), Base + 21, 100);
        replay.Accept(Tw20261110CombatProtocol.DamageFloater, Damage(replay.Local, 900001, 500), Base + 42, 110);
        replay.Accept(Tw20261110CombatProtocol.SetCombatState, State(replay.Local, 3), Base + 104, 141);
        replay.Accept(Tw20261110CombatProtocol.SetCombatState, State(replay.Local, 7), Base + 125, 2500);
        replay.Accept(Tw20261110CombatProtocol.SetCombatState, new byte[11], Base + 146, 2500);
        replay.Observer.Finish(3000, CombatProtocolTests.At(3000));
        var states = replay.Frames.SelectMany(x => x.CombatStates ?? []).ToList();
        Assert.Equal(["in-combat", "combat-end", "unknown-7"], states.Select(x => x.State.RawValue));
        Assert.Equal([Base, Base + 104, Base + 125], states.Select(x => x.Sequence!.Value.Value));
        Assert.Equal(1UL, replay.Observer.ProtocolRejections);
        // 击杀伤害先于其后的 CombatEnd 发布。
        var order = replay.Frames.SelectMany(frame => frame.Damage.Select(x => (x.Sequence!.Value.Value, "damage"))
            .Concat((frame.CombatStates ?? []).Select(x => (x.Sequence!.Value.Value, "state")))).OrderBy(x => x.Item1).Select(x => x.Item2);
        Assert.Equal(["state", "damage", "state", "state"], order);
    }

    public static TheoryData<string> Scenarios => new() { "flee-run-away", "party-stop-and-wipe", "death-no-local-damage", "boss-full" };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void SanitizedSampleReplaySplitsSegmentsWhenLeavingCombat(string scenarioId)
    {
        var scenario = Scenario(scenarioId);
        var session = Run(scenario, combatState: true);
        var expected = scenario.GetProperty("expected").GetProperty("segments").EnumerateArray().ToList();
        var segments = session.Encounters;
        Assert.Equal(expected.Count, segments.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var want = expected[i]; var got = segments[i];
            Assert.Equal(want.GetProperty("startReason").GetString(), got.Segment!.StartReason);
            Assert.Equal(want.GetProperty("boundaryReason").GetString(), got.Summary.BoundaryReason);
            Assert.Equal(want.GetProperty("damage").GetString(), got.Summary.AttributedDamage.ToString());
            Assert.Equal(want.GetProperty("dpsDurationMs").GetDouble(), got.Summary.DpsDurationMs ?? got.Summary.DurationMs);
        }
        Assert.Empty(segments.SelectMany(x => x.Gaps));
    }

    [Fact]
    public void WithoutTheSignalTheSameReplayKeepsTheExistingSegments()
    {
        // v2 兴趣不解析 170196：同一片段只按首击与空闲阈值（默认 300 s）划分，组队片段全程为一个进行中的分段。
        var session = Run(Scenario("party-stop-and-wipe"), combatState: false);
        var segment = Assert.Single(session.Encounters);
        Assert.Equal(("first-damage", "active"), (segment.Segment!.StartReason, segment.Summary.BoundaryReason));
        Assert.Equal((71443 + 217666 + 733542).ToString(), segment.Summary.AttributedDamage.ToString());
        Assert.Null(session.LocalCombatState);
    }

    private static (List<CombatEncounter> Encounters, OpenEnum<CombatLocalState>? LocalCombatState) Run(JsonElement scenario, bool combatState)
    {
        var replay = new Replay(combatState);
        var targets = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var offset = Base;
        foreach (var item in scenario.GetProperty("events").EnumerateArray())
        {
            var mono = 1000 + item[0].GetDouble();
            if (item[1].GetString() == "state")
                replay.Accept(Tw20261110CombatProtocol.SetCombatState, State(replay.Local, item[2].GetInt32()), offset, mono);
            else
            {
                var name = item[2].GetString()!;
                if (!targets.TryGetValue(name, out var target)) targets.Add(name, target = 900_000 + (ulong)targets.Count);
                replay.Accept(Tw20261110CombatProtocol.DamageFloater, Damage(replay.Local, target, item[3].GetInt64()), offset, mono);
            }
            offset += 100;
        }
        var end = 1000 + scenario.GetProperty("events").EnumerateArray().Last()[0].GetDouble() + 1000;
        replay.Observer.Finish(end, CombatProtocolTests.At(end));
        var session = new CombatSession("owner", "observation", replay.Source, replay.Observer.LocalEntity!, new("fixture-game"),
            CombatProtocolTests.At(0), healing: true);
        foreach (var frame in replay.Frames) Assert.True(session.Apply(frame));
        var checkpoint = session.Capture();
        return (checkpoint.Encounters.Where(x => x.Segment is not null).OrderBy(x => x.Segment!.Ordinal).ToList(),
            checkpoint.Workspace.Observation.LocalCombatState);
    }

    private static JsonElement Scenario(string id)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "contracts/fixtures/combat-tw-combat-state.v1.json")));
        return document.RootElement.GetProperty("scenarios").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id).Clone();
    }
    internal static byte[] State(ulong entity, int value)
    {
        var body = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(body, entity); BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), value);
        return body;
    }
    private static byte[] Damage(ulong attacker, ulong target, long amount)
    {
        var body = new byte[53]; BinaryPrimitives.WriteUInt64LittleEndian(body, attacker); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), target);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), amount);
        return body;
    }
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("无法定位仓库");
    }

    /// <summary>以当前构建身份建立本机实体后，帧按给定流偏移直接交给观察器。</summary>
    private sealed class Replay
    {
        public List<CombatFrame> Frames { get; } = [];
        public ObservationSource Source => Tw20261110CombatProtocol.CombatProfile.Source;
        public TwCombatObserver Observer { get; }
        public ulong Local => Observer.LocalEntity!.Key.RawInstanceId.Value;
        public Replay(bool combatState)
        {
            Observer = new("fixture-game", Source, Frames.Add);
            Observer.Configure(new(true, true, true, true, combatState));
            GameTcpStream? stream = null;
            stream = new((code, body) => Observer.Accept(code, body, stream!.FrameStreamOffset, stream.FrameWireLength,
                0, CombatProtocolTests.At(0), stream.FrameEncoding), _ => { }, Tw20261110CombatProtocol.StreamProfile);
            var bytes = CombatProtocolTests.Identity();
            stream.Feed(0, bytes, 0, 0);
        }
        public void Accept(int opcode, byte[] body, ulong offset, double mono) =>
            Observer.Accept(opcode, body, offset, body.Length + 9, mono, CombatProtocolTests.At(mono));
    }
}
