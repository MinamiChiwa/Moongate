using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>当前布局共享流按实采顺序重置三次（FIN 后约 0.23 s 移除、单独移除、再次成对），帧经宿主校验后进入 CombatSession。</summary>
[SupportedOSPlatform("windows")]
public sealed class CombatMapSegmentStreamTests
{
    [Fact]
    public void ThreeMapChangesFromTheSharedStreamYieldFourCompleteSegments()
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0;
        using var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        using var fixture = JsonDocument.Parse(typeof(CombatMapSegmentStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var identity = Frame(20590, Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!), true)
            .Concat(Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true)).ToArray();
        var hit = new byte[53];
        BinaryPrimitives.WriteUInt64LittleEndian(hit, 0xabcdef0100000457);
        BinaryPrimitives.WriteUInt64LittleEndian(hit.AsSpan(8), 0xabcdef0100000458);
        BinaryPrimitives.WriteInt64LittleEndian(hit.AsSpan(16), 123);
        var hitFrame = Frame(21196, hit);
        var at = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        void Feed(uint sequence, byte[] bytes, double ms) => stream.Feed(sequence, bytes, ms, ms, at.AddMilliseconds(ms));
        void Reset(double ms, GameStreamResetCause cause) => stream.Restart("private native detail", ms, at.AddMilliseconds(ms), cause);
        GameStreamResetCause[][] switches =
        [
            [GameStreamResetCause.ConnectionClosed, GameStreamResetCause.ConnectionRemoved],
            [GameStreamResetCause.ConnectionRemoved],
            [GameStreamResetCause.ConnectionClosed, GameStreamResetCause.ConnectionRemoved],
        ];
        for (var map = 0; map < 4; map++)
        {
            var start = map * 20000d;
            Feed(0, identity, start);
            Assert.NotNull(stream.LocalEntity);
            Feed((uint)identity.Length, hitFrame, start + 100); stream.Tick(start + 400, at.AddMilliseconds(start + 400));
            Feed((uint)(identity.Length + hitFrame.Length), hitFrame, start + 1100); stream.Tick(start + 1400, at.AddMilliseconds(start + 1400));
            if (map < 3)
                for (var i = 0; i < switches[map].Length; i++) Reset(start + 1500 + i * 234, switches[map][i]);
        }
        Validate(reports, profile);
        Assert.Equal(5, reports.SelectMany(x => x.Frame.Gaps).Count(x => CombatSession.ConnectionChange(x.Reason)));
        Assert.DoesNotContain("private native detail", JsonSerializer.Serialize(reports, ContractJson.Options));

        var first = reports.First(x => x.LocalEntity is not null).LocalEntity!;
        var session = new CombatSession("owner", "obs", profile.Source, first, new("fixture-current"), at);
        foreach (var report in reports) session.Apply(report.Frame);
        session.Stop("observation-stopped", at.AddMilliseconds(62000), 62000);
        var checkpoint = session.Capture();
        var segments = checkpoint.Encounters.OrderBy(x => x.Segment!.Ordinal).ToArray();
        Assert.Equal([1, 2, 3, 4], segments.Select(x => x.Segment!.Ordinal));
        Assert.Equal(["first-damage", "map-change", "map-change", "map-change"], segments.Select(x => x.Segment!.StartReason));
        Assert.Equal(["connection-changed", "connection-changed", "connection-changed"], segments.Take(3).Select(x => x.Summary.BoundaryReason));
        Assert.All(segments, segment =>
        {
            Assert.Equal("246", segment.Summary.AttributedDamage.ToString());
            Assert.Equal(ObservationQuality.Confirmed, segment.Summary.Quality.KnownValue);
            Assert.NotNull(segment.Summary.EncounterDps);
        });
        Assert.All(segments.Take(3), segment => Assert.Equal(246, segment.Summary.EncounterDps));
        Assert.Equal(ObservationQuality.Confirmed, checkpoint.Workspace.Observation.Quality.KnownValue);
        Assert.Equal(5, checkpoint.Gaps.Count(x => CombatSession.ConnectionChange(x.Reason)));
        // 生成跨语言契约样本，TypeScript 契约测试按 OpenAPI 校验分段字段。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
        var output = Path.Combine(directory!.FullName, "artifacts/generated/contracts/fixtures");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "csharp-combat-segments.v1.json"), JsonSerializer.Serialize(segments, ContractJson.Options));
    }

    /// <summary>进地下城换连接：新连接的共享身份被清空，同一角色 ID 沿用上一连接识别的昵称；分段补名覆盖昵称晚到的情形。</summary>
    [Fact]
    public void LocalNicknameSurvivesConnectionChangeForTheSameCharacter()
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0; var identities = new LocalIdentityMemory();
        using var fixture = JsonDocument.Parse(typeof(CombatMapSegmentStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var self = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var player = Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!);
        var character = ulong.Parse(fixture.RootElement.GetProperty("characterId").GetString()!);
        var identity = Frame(20590, self, true).Concat(Frame(20054, player, true)).ToArray();
        var hit = new byte[53];
        BinaryPrimitives.WriteUInt64LittleEndian(hit, 0xabcdef0100000457);
        BinaryPrimitives.WriteUInt64LittleEndian(hit.AsSpan(8), 0xabcdef0100000458);
        BinaryPrimitives.WriteInt64LittleEndian(hit.AsSpan(16), 123);
        var hitFrame = Frame(21196, hit);
        var at = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

        var first = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110, localIdentities: identities);
        first.Feed(0, identity, 0, 0, at);
        first.Feed((uint)identity.Length, hitFrame, 100, 100, at.AddMilliseconds(100)); first.Tick(400, at.AddMilliseconds(400));
        first.Restart("private native detail", 1500, at.AddMilliseconds(1500), GameStreamResetCause.ConnectionClosed);
        first.Dispose();
        // 新连接：个人数据与本机出现帧确认身份后，一帧无法解析的角色出现帧只被丢弃，已确认的身份保留。
        using var second = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110, localIdentities: identities);
        var entry = identity.Concat(Frame(20054, player[..(player.Length / 2)], true)).ToArray();
        second.Feed(0, entry, 20000, 20000, at.AddMilliseconds(20000));
        Assert.Equal("Fixture玩家", second.Music.Current.Identity?.Nickname);
        second.Feed((uint)entry.Length, hitFrame, 20100, 20100, at.AddMilliseconds(20100)); second.Tick(20400, at.AddMilliseconds(20400));
        var latest = reports[^1].LocalEntity!;
        Assert.Equal("Fixture玩家", latest.DisplayName);
        Assert.Contains(reports[^1].Frame.Entities, entity => entity == latest);
        // 状态报告同样沿用身份（实体换成当前本机实体），合奏与演奏仍随本连接为空。
        var reported = SharedGameStream.ReportIdentity(second.Music.Current, [second], identities);
        Assert.Equal(("Fixture玩家", latest.Key.RawInstanceId.Value), (reported.Identity!.Nickname, reported.Identity.ActorId));
        Assert.Null(reported.Music); Assert.Null(reported.Concert);
        // 角色 ID 不同时不沿用。
        Assert.Null(SharedGameStream.LocalIdentity(null, identities.Last! with { CharacterId = character + 1 }, second.LocalEntity, character));
        Validate(reports, profile);

        var local = reports.First(x => x.LocalEntity is not null).LocalEntity!;
        var session = new CombatSession("owner", "obs", profile.Source, local, new("fixture-current"), at);
        foreach (var report in reports) session.Apply(report.Frame);
        session.Stop("observation-stopped", at.AddMilliseconds(21000), 21000);
        Assert.Equal(["Fixture玩家", "Fixture玩家"], session.Capture().Encounters.OrderBy(x => x.Segment!.Ordinal).Select(x => x.Summary.DisplayName));

        // 昵称晚于分段开始：首笔伤害所在帧及之前本机仍是实体 ID，之后的帧带昵称时进行中的分段改名。
        var placeholder = local.Key.RawInstanceId.ToString();
        var firstDamage = reports.FindIndex(x => x.Frame.Damage.Count > 0);
        var late = new CombatSession("owner", "obs-late", profile.Source, local with { DisplayName = placeholder }, new("fixture-current"), at);
        for (var i = 0; i < reports.Count; i++)
            late.Apply(i > firstDamage ? reports[i].Frame : reports[i].Frame with
                { Entities = reports[i].Frame.Entities.Select(entity => entity.Key == local.Key ? entity with { DisplayName = placeholder } : entity).ToArray() });
        late.Stop("observation-stopped", at.AddMilliseconds(21000), 21000);
        Assert.Equal("Fixture玩家", late.Capture().Encounters.Single(x => x.Segment!.Ordinal == 1).Summary.DisplayName);
    }

    /// <summary>
    /// 退出地下城回城：新连接的个人数据帧在过滤器打开前已经过去（实采约在连接后 0.3–1.3 s 发送，连接表常晚于此列出），
    /// 进图后的本机出现帧按同一进程记住的角色 ID 确认身份，并成为主连接；同一连接内的丢包重同步保留身份；
    /// Worker 重启后以宿主下发的身份为初值同样恢复；记住的角色 ID 不同则不确认。
    /// </summary>
    [Fact]
    public void ConnectionWithoutPersonalDataRecoversIdentityFromTheSameProcess()
    {
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, _ => { });
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0; var identities = new LocalIdentityMemory();
        using var fixture = JsonDocument.Parse(typeof(CombatMapSegmentStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var self = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var player = Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true);
        // 分帧需要看到下一帧帧头才确认边界，进图后的本机出现帧连发两次。
        var entering = player.Concat(player).ToArray();
        var character = ulong.Parse(fixture.RootElement.GetProperty("characterId").GetString()!);
        var at = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        SharedGameStream Open(LocalIdentityMemory memory) =>
            new("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110, localIdentities: memory);

        using var dungeon = Open(identities);
        dungeon.Feed(0, Frame(20590, self, true).Concat(player).ToArray(), 0, 0, at);
        Assert.Equal(character, identities.Last?.CharacterId);
        dungeon.Restart("private native detail", 1500, at.AddMilliseconds(1500), GameStreamResetCause.ConnectionClosed);
        Assert.Null(dungeon.Music.Current.Identity);

        using var town = Open(identities);
        town.Feed(0, entering, 20000, 20000, at.AddMilliseconds(20000));
        Assert.Equal("Fixture玩家", town.Music.Current.Identity?.Nickname);
        Assert.NotNull(town.LocalEntity);
        var selected = new CaptureObservationSelection().Select([(dungeon.Music, dungeon.MusicInitializerAt), (town.Music, town.MusicInitializerAt)]);
        Assert.Equal("Fixture玩家", selected.Identity?.Nickname);

        town.Restart("private native detail", 20500, at.AddMilliseconds(20500), GameStreamResetCause.CaptureDrops);
        Assert.Equal("Fixture玩家", town.Music.Current.Identity?.Nickname);
        Assert.NotNull(town.LocalEntity);

        using var restarted = Open(new LocalIdentityMemory(identities.Last));
        restarted.Feed(0, entering, 30000, 30000, at.AddMilliseconds(30000));
        Assert.Equal("Fixture玩家", restarted.Music.Current.Identity?.Nickname);

        using var other = Open(new LocalIdentityMemory(identities.Last! with { CharacterId = character + 1 }));
        other.Feed(0, entering, 40000, 40000, at.AddMilliseconds(40000));
        Assert.Null(other.Music.Current.Identity);
        Assert.Null(other.LocalEntity);
    }

    private static void Validate(IEnumerable<CombatCaptureReport> reports, ObservationCombatProfile profile)
    {
        var validate = typeof(MabiTool.Host.Game.GameObservationService).Assembly
            .GetType("MabiTool.Host.Game.CaptureCombatValidation")!
            .GetMethod("Validate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var report in reports) validate.Invoke(null, [report, profile.Source, "fixture-current"]);
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
