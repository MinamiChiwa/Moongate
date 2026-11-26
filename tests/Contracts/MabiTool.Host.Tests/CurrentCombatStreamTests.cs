using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class CurrentCombatStreamTests
{
    [Theory]
    [InlineData("gapTimeout")]
    [InlineData("conflictingRetransmission")]
    [InlineData("invalidFrameHeader")]
    [InlineData("payloadTooLarge")]
    [InlineData("pendingCapacity")]
    [InlineData("connectionRemoved")]
    [InlineData("connectionSyn")]
    [InlineData("connectionClosed")]
    [InlineData("connectionReset")]
    [InlineData("captureDrops")]
    [InlineData("truncatedPacket")]
    [InlineData("unknown")]
    public void StreamResetPreservesBranchWithoutLeakingNativeReason(string cause)
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0;
        using var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var initial = Frame(20590, Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!), true)
            .Concat(Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true)).ToArray();
        var at = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        stream.Feed(0, initial, 0, 0, at);
        var local = stream.LocalEntity!.Key;
        reports.Clear();
        switch (cause)
        {
            case "gapTimeout":
                stream.Feed((uint)initial.Length + 10, new byte[9], 10, 10, at.AddMilliseconds(10));
                stream.Tick(1510, at.AddMilliseconds(1510)); break;
            case "conflictingRetransmission":
                var changed = initial.ToArray(); changed[0] ^= 1;
                stream.Feed(0, changed, 10, 10, at.AddMilliseconds(10)); break;
            case "invalidFrameHeader":
                stream.Feed((uint)initial.Length, new byte[9], 10, 10, at.AddMilliseconds(10)); break;
            case "payloadTooLarge":
                stream.Feed((uint)initial.Length, new byte[65537], 10, 10, at.AddMilliseconds(10)); break;
            case "pendingCapacity":
                for (var i = 0; i < 129; i++)
                    stream.Feed((uint)(initial.Length + 100 + i * 10), new byte[9], 10, 10, at.AddMilliseconds(10));
                break;
            default:
                stream.Restart("private native detail", 10, at.AddMilliseconds(10), Enum.Parse<GameStreamResetCause>(cause, true)); break;
        }
        var gap = Assert.Single(reports.SelectMany(value => value.Frame.Gaps));
        Assert.Equal(cause == "unknown" ? "combat.streamGap" : "combat.streamGap." + cause, gap.Reason);
        // 同一连接内、数据本身一致的缺口保留本机实体与代次（分段不结束）；重传不一致、连接变化与原因不明时整体重置（换代次）。
        var kept = cause is "gapTimeout" or "invalidFrameHeader" or "payloadTooLarge" or "pendingCapacity" or "captureDrops" or "truncatedPacket";
        Assert.Equal(kept, stream.LocalEntity?.Key == local);
        Assert.DoesNotContain("private native detail", JsonSerializer.Serialize(reports, ContractJson.Options));
        var validate = typeof(MabiTool.Host.Game.GameObservationService).Assembly.GetType("MabiTool.Host.Game.CaptureCombatValidation")!
            .GetMethod("Validate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var report in reports) validate.Invoke(null, [report, profile.Source, "fixture-current"]);
    }

    [Fact]
    public void UnregisteredBuildVerdictDisablesOnlyTheParsersThatFailedVerification()
    {
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var build = TwClientFingerprint.AdaptiveBuild(new string('a', 64), new string('b', 64));
        var combatOff = new TwBuildVerdict(build, false, null, TwCapabilityCheck.Ok, TwCapabilityCheck.Ok,
            new(false, TwBuildVerification.LayoutChanged), Tw20261110CombatProtocol.StreamProfile.KnownProtocols);
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var initial = Frame(20590, Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!), true)
            .Concat(Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true)).ToArray();
        var at = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        ulong generation = 0;

        // 战斗核对不过：战斗兴趣被拒绝，身份照常识别。
        var withCombat = new ObservationCaptureContext([profile], _ => { }, _ => { });
        withCombat.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        var rejected = Assert.Throws<WorkerObservationException>(() => new SharedGameStream("fixture-current", withCombat, () => ++generation,
            layout: CombatProtocolLayout.Tw20261110, build: combatOff));
        Assert.Equal("combat.buildUnsupported", rejected.Code);
        using (var identityOnly = new SharedGameStream("fixture-current", new([profile], _ => { }, _ => { }), () => ++generation,
            layout: CombatProtocolLayout.Tw20261110, build: combatOff))
        {
            identityOnly.Feed(0, initial, 0, 0, at);
            Assert.NotNull(identityOnly.Music.PersonalId);
        }

        // 身份核对不过：身份帧不解码，不输出可能错位的角色。
        var identityOff = combatOff with { Identity = new(false, TwBuildVerification.LayoutChanged),
            Music = new(false, TwBuildVerification.IdentityUnavailable), Combat = new(false, TwBuildVerification.IdentityUnavailable) };
        using var silent = new SharedGameStream("fixture-current", new([profile], _ => { }, _ => { }), () => ++generation,
            layout: CombatProtocolLayout.Tw20261110, build: identityOff);
        silent.Feed(0, initial, 0, 0, at);
        Assert.Null(silent.Music.PersonalId); Assert.Null(silent.Music.Current.Identity);
    }

    /// <summary>
    /// 未登记构建里协议换了编号（按名称对应，签名与布局不变）：解析前换回基线编号，结果与基线编号的帧相同；
    /// 占用基线旧编号的其他协议不再被当作该消息。
    /// </summary>
    [Fact]
    public void RenumberedProtocolsAreTranslatedBeforeParsing()
    {
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var build = TwClientFingerprint.AdaptiveBuild(new string('a', 64), new string('b', 64));
        const int Moved = 20999;
        var renumbered = new TwBuildVerdict(build, false, null, TwCapabilityCheck.Ok, TwCapabilityCheck.Ok, TwCapabilityCheck.Ok,
            Tw20261110CombatProtocol.StreamProfile.KnownProtocols.Append(Moved).ToHashSet(),
            Translation: new(new Dictionary<int, int> { [Moved] = Tw20261110CombatProtocol.AppearingCharacter }));
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var personal = Frame(20590, Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!), true);
        var character = Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!);
        var at = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        ulong generation = 0;
        GameIdentity? Identity(TwBuildVerdict? verdict, int characterCode)
        {
            using var stream = new SharedGameStream("fixture-current", new([profile], _ => { }, _ => { }), () => ++generation,
                layout: CombatProtocolLayout.Tw20261110, build: verdict);
            stream.Feed(0, personal.Concat(Frame(characterCode, character, true)).ToArray(), 0, 0, at);
            return stream.Music.Current.Identity;
        }
        var expected = Identity(null, Tw20261110CombatProtocol.AppearingCharacter);
        Assert.NotNull(expected);
        Assert.Equal(expected, Identity(renumbered, Moved));
        // 基线旧编号在新构建里属于别的协议：不当作角色出现解析。
        Assert.Null(Identity(renumbered, Tw20261110CombatProtocol.AppearingCharacter));
    }

    [Fact]
    public void IdentityDecodedWithoutInterestSeedsALaterCombatInterest()
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        ulong generation = 0;
        using var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var entity = ulong.Parse(fixture.RootElement.GetProperty("entityId").GetString()!);
        var character = ulong.Parse(fixture.RootElement.GetProperty("characterId").GetString()!);
        var initial = Frame(20590, Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!), true)
            .Concat(Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true)).ToArray();
        var at = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        // 没有任何模块兴趣时身份照常解码。
        stream.Feed(0, initial, 0, 0, at);
        Assert.Equal((character, entity, "Fixture玩家"), (stream.Music.Current.Identity!.CharacterId, stream.Music.Current.Identity.ActorId, stream.Music.Current.Identity.Nickname));
        Assert.Null(stream.LocalEntity);
        // 之后才登记战斗兴趣：不等下一次切图即可确认本机实体并发布。
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        Assert.Equal(entity, stream.LocalEntity!.Key.RawInstanceId.Value);
        stream.Tick(200, at.AddMilliseconds(200));
        Assert.Contains(reports, report => report.LocalEntity?.Key.RawInstanceId.Value == entity);
        Assert.False(double.IsNegativeInfinity(stream.CombatInitializerAt));
    }

    [Theory]
    [InlineData(250)]
    [InlineData(251)]
    [InlineData(300)]
    public void EntityUpdateAfterSkillWaitKeepsDamageWithinHostTimeWindow(int elapsed)
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0;
        using var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var self = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var player = Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!);
        var hit = new byte[53];
        BinaryPrimitives.WriteUInt64LittleEndian(hit, 0xabcdef0100000457);
        BinaryPrimitives.WriteUInt64LittleEndian(hit.AsSpan(8), 0xabcdef0100000458);
        BinaryPrimitives.WriteInt64LittleEndian(hit.AsSpan(16), 123);
        var initial = Frame(20590, self, true).Concat(Frame(20054, player, true)).Concat(Frame(21196, hit)).ToArray();
        var at = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        stream.Feed(0, initial, 0, 0, at);
        Assert.Empty(reports.SelectMany(x => x.Frame.Damage));
        stream.Feed((uint)initial.Length, Frame(20054, player, true), elapsed, elapsed, at.AddMilliseconds(elapsed));
        stream.Tick(elapsed + 1, at.AddMilliseconds(elapsed + 1));
        var damage = Assert.Single(reports.SelectMany(x => x.Frame.Damage));
        Assert.Equal(0, damage.MonotonicMs); Assert.Equal(at, damage.ObservedAt);
        Assert.Equal(123UL, damage.Amount.Value);
        var validate = typeof(MabiTool.Host.Game.GameObservationService).Assembly
            .GetType("MabiTool.Host.Game.CaptureCombatValidation")!
            .GetMethod("Validate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var report in reports) validate.Invoke(null, [report, profile.Source, "fixture-current"]);
        Assert.Empty(reports.SelectMany(x => x.Frame.Gaps));
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.Frame.MonotonicMs <= pair.Second.Frame.MonotonicMs));
    }

    [Theory]
    [InlineData(2600)]
    [InlineData(1001)]
    [InlineData(400)]
    public void IdentityReadAfterCaptureStallStaysWithinHostFreshness(int lag)
    {
        var (stream, _, reports, profile, self, player) = Current();
        using var owned = stream;
        var at = new DateTimeOffset(2026, 11, 10, 0, 0, 10, TimeSpan.Zero);
        // 捕获循环停顿后才读取切图时的身份包：Npcap 时点比处理时点早 lag 毫秒；两个包属于同一积压段。
        var first = Frame(20590, self, true);
        stream.Feed(0, first, 10000, 10000 - lag, at);
        stream.Feed((uint)first.Length, Frame(20054, player, true), 10010, 10010 - lag, at.AddMilliseconds(10));
        Assert.NotNull(stream.LocalEntity); Assert.NotEmpty(reports);
        var processed = lag > SharedGameStream.MaximumCombatReceiptLagMs;
        Assert.All(reports, report => Assert.InRange(10010 - report.Frame.MonotonicMs, 0, SharedGameStream.MaximumCombatReceiptLagMs));
        Validate(reports, profile);
        var lagGaps = reports.SelectMany(x => x.Frame.Gaps).Where(x => x.Reason == "combat.captureLag").ToArray();
        Assert.DoesNotContain(reports.SelectMany(x => x.Frame.Gaps), x => x.Reason != "combat.captureLag");
        if (!processed) { Assert.Empty(lagGaps); return; }
        var gap = Assert.Single(lagGaps);
        // 首帧在第二个包到达后才完成分帧，因此缺口止于第二次处理时点。
        Assert.Equal(10000 - lag, gap.FromMs); Assert.Equal(10010, gap.ToMs); Assert.Equal(0UL, gap.DroppedEvents.Value);
        // 预算内的包结束积压段；之后的新积压段再记一条。
        var appearance = Frame(20054, player, true);
        stream.Feed((uint)(first.Length + appearance.Length), appearance, 10020, 10020, at.AddMilliseconds(20));
        stream.Feed((uint)(first.Length + 2 * appearance.Length), appearance, 10030, 10030 - lag, at.AddMilliseconds(30));
        Assert.Equal(2, reports.SelectMany(x => x.Frame.Gaps).Count(x => x.Reason == "combat.captureLag"));
        Assert.NotNull(stream.LocalEntity);
        Validate(reports, profile);
    }

    [Fact]
    public void BacklogReadAfterKeepaliveStaysInsideHostFrameWindow()
    {
        var (stream, _, reports, profile, self, player) = Current();
        using var owned = stream;
        var at = new DateTimeOffset(2026, 11, 10, 0, 0, 10, TimeSpan.Zero);
        var initial = Frame(20590, self, true).Concat(Frame(20054, player, true)).ToArray();
        stream.Feed(0, initial, 0, 0, at);
        stream.Tick(500, at.AddMilliseconds(500));
        Assert.Equal(500, reports[^1].Frame.MonotonicMs);
        // 保活帧之后才读到的积压伤害，Npcap 时点早于保活帧 400 ms。
        var hit = new byte[53];
        BinaryPrimitives.WriteUInt64LittleEndian(hit, 0xabcdef0100000457);
        BinaryPrimitives.WriteUInt64LittleEndian(hit.AsSpan(8), 0xabcdef0100000458);
        BinaryPrimitives.WriteInt64LittleEndian(hit.AsSpan(16), 123);
        stream.Feed((uint)initial.Length, Frame(21196, hit), 520, 100, at.AddMilliseconds(520));
        stream.Tick(900, at.AddMilliseconds(900));
        Validate(reports, profile);
        var damage = Assert.Single(reports.SelectMany(x => x.Frame.Damage));
        Assert.Equal(123UL, damage.Amount.Value);
        Assert.Equal(500, damage.MonotonicMs);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.Frame.MonotonicMs <= pair.Second.Frame.MonotonicMs));
    }

    private static (SharedGameStream Stream, ObservationCaptureContext Context, List<CombatCaptureReport> Reports,
        ObservationCombatProfile Profile, byte[] Self, byte[] Player) Current()
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0;
        var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        return (stream, context, reports, profile,
            Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!),
            Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!));
    }

    private static void Validate(IEnumerable<CombatCaptureReport> reports, ObservationCombatProfile profile)
    {
        var validate = typeof(MabiTool.Host.Game.GameObservationService).Assembly
            .GetType("MabiTool.Host.Game.CaptureCombatValidation")!
            .GetMethod("Validate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var report in reports) validate.Invoke(null, [report, profile.Source, "fixture-current"]);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(61)]
    public void CurrentOpcodesAcrossSegmentsKeepFullIdentityDamageAndUnknownEndAt(int segment)
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0;
        using var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        const ulong actor = 0xabcdef0100000457;
        using var fixture = JsonDocument.Parse(typeof(CurrentCombatStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var self = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var player = Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!);
        var hit = new byte[53]; BinaryPrimitives.WriteUInt64LittleEndian(hit, actor);
        BinaryPrimitives.WriteUInt64LittleEndian(hit.AsSpan(8), actor + 1);
        BinaryPrimitives.WriteInt64LittleEndian(hit.AsSpan(16), (long)uint.MaxValue + 100);
        var effect = new byte[36]; BinaryPrimitives.WriteUInt64LittleEndian(effect, actor);
        BinaryPrimitives.WriteUInt64LittleEndian(effect.AsSpan(8), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(effect.AsSpan(16), 7);
        BinaryPrimitives.WriteSingleLittleEndian(effect.AsSpan(20), 5);
        BinaryPrimitives.WriteInt32LittleEndian(effect.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(effect.AsSpan(28), actor);
        var bytes = Frame(20590, self, true).Concat(Frame(20054, player, true))
            .Concat(Frame(21196, hit)).Concat(Frame(170069, effect)).ToArray();
        var at = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        for (var offset = 0; offset < bytes.Length; offset += segment)
            stream.Feed((uint)offset, bytes.AsMemory(offset, Math.Min(segment, bytes.Length - offset)), 0, 0, at);
        stream.Tick(300, at.AddMilliseconds(300));
        Assert.Equal(actor, stream.LocalEntity!.Key.RawInstanceId.Value);
        var damage = Assert.Single(reports.SelectMany(x => x.Frame.Damage));
        Assert.Equal((ulong)uint.MaxValue + 100, damage.Amount.Value);
        Assert.Equal(stream.LocalEntity.Key, damage.SourceEntity);
        var buff = Assert.Single(reports.SelectMany(x => x.Frame.Effects));
        Assert.Equal(EffectDurationKind.Finite, buff.DurationKind.KnownValue); Assert.Equal(5000d, buff.DurationMs);
        Assert.Empty(reports.SelectMany(x => x.Frame.Gaps));
        var count = reports.SelectMany(x => x.Frame.Damage).Count();
        // 完整重传须保持幂等，不能重复累计当前协议的大额伤害。
        stream.Feed(0, bytes, 310, 310, at.AddMilliseconds(310));
        Assert.Equal(count, reports.SelectMany(x => x.Frame.Damage).Count());
    }

    [Fact]
    public void ReferenceStreamRejectsCurrentLayoutInterestBeforeChangingState()
    {
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("test", "1", "TW", "synthetic"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, _ => { });
        using var stream = new SharedGameStream("fixture", context, () => 1);
        Assert.Throws<WorkerObservationException>(() => context.Configure(new(false,
            new(new string('a', 32), profile.Id, true, true, true))));
        Assert.Null(context.Current.Combat); Assert.True(context.Current.MusicEnabled);
    }

    private static byte[] Frame(int opcode, byte[] body, bool compressed = false)
    {
        if (compressed)
        {
            using var memory = new MemoryStream();
            using (var encoder = new BrotliStream(memory, CompressionLevel.Optimal, true)) encoder.Write(body);
            body = memory.ToArray();
        }
        var bytes = new byte[9 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(bytes, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), body.Length); bytes[8] = compressed ? (byte)1 : (byte)0;
        body.CopyTo(bytes, 9); return bytes;
    }
}
