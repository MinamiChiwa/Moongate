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

    /// <summary>
    /// 捕获重启后接入的仍是确认身份的那条连接（四元组相同，同一地图实例）：收不到个人数据与本机出现帧，以宿主下发的身份（角色 ID 与实体 ID）为本机起点，
    /// 伤害继续归属本机且状态标为沿用；本机以新实体出现时改绑；出现帧证明沿用的实体属于其他角色时作废，之后的伤害不归属本机。
    /// </summary>
    [Fact]
    public void CaptureRestartedMidConnectionCarriesTheIdentityWithoutClaimingOtherCharacters()
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0; var identities = new LocalIdentityMemory();
        using var fixture = JsonDocument.Parse(typeof(CombatMapSegmentStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var self = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var player = Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true);
        var character = ulong.Parse(fixture.RootElement.GetProperty("characterId").GetString()!);
        var hits = Hits();
        var at = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        SharedGameStream Open(LocalIdentityMemory memory) =>
            new("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110, localIdentities: memory) { Connection = ConnectionA };
        using (var before = Open(identities)) before.Feed(0, Frame(20590, self, true).Concat(player).ToArray(), 0, 0, at);
        var known = identities.Last!;
        Assert.Equal(character, known.CharacterId);

        // 同一连接中途接入：只有伤害帧。沿用身份确认本机，伤害归属本机，状态报告标为沿用。
        reports.Clear();
        var carried = new LocalIdentityMemory(known, ObservedA);
        using var restarted = Open(carried);
        restarted.Feed(1000, hits, 10000, 10000, at.AddMilliseconds(10000)); restarted.Tick(10400, at.AddMilliseconds(10400));
        Assert.Equal(known.ActorId, restarted.LocalEntity?.Key.RawInstanceId.Value);
        var reported = SharedGameStream.ReportIdentity(restarted.Music.Current, [restarted], carried);
        Assert.Equal(known.Nickname, reported.Identity?.Nickname); Assert.Contains("沿用", reported.Detail);
        Assert.Equal(ConnectionA, SharedGameStream.IdentityConnection(reported.Identity!, [restarted]));
        Validate(reports, profile);
        var session = new CombatSession("owner", "obs", profile.Source, restarted.LocalEntity!, new("fixture-current"), at.AddMilliseconds(10000));
        foreach (var report in reports) session.Apply(report.Frame);
        session.Stop("observation-stopped", at.AddMilliseconds(11000), 11000);
        Assert.Equal("246", session.Capture().Encounters.Single().Summary.AttributedDamage.ToString());

        // 沿用的实体与本连接不符：本机以新实体出现时改绑到新实体。
        using var moved = Open(new LocalIdentityMemory(known with { ActorId = 77 }, ObservedA));
        moved.Feed(1000, hits, 20000, 20000, at.AddMilliseconds(20000));
        Assert.Equal(77UL, moved.LocalEntity?.Key.RawInstanceId.Value);
        moved.Feed((uint)(1000 + hits.Length), player.Concat(player).ToArray(), 20100, 20100, at.AddMilliseconds(20100));
        Assert.Equal(known.ActorId, moved.LocalEntity?.Key.RawInstanceId.Value);

        // 沿用的实体出现为其他角色：作废沿用，本机未识别，之后的伤害不归属本机。
        var other = new LocalIdentityMemory(known with { CharacterId = character + 1 }, ObservedA);
        using var contradicted = Open(other);
        contradicted.Feed(1000, hits, 30000, 30000, at.AddMilliseconds(30000));
        contradicted.Feed((uint)(1000 + hits.Length), player.Concat(hits).ToArray(), 30100, 30100, at.AddMilliseconds(30100));
        Assert.Null(contradicted.LocalEntity); Assert.Null(other.Last); Assert.False(other.Carried);
        Assert.Null(SharedGameStream.ReportIdentity(contradicted.Music.Current, [contradicted], other).Identity);
    }

    /// <summary>
    /// 沿用身份的实体来自连接 A；新捕获接入连接 B（另一张图），B 上另一个角色恰好用了同一个实体 ID 并造成伤害：只沿用角色 ID，
    /// 本机实体保持未知，伤害不归属本机、身份待确认。受疗与紧邻的 SetHealth 170205 不足以确认（组队时紧邻的可以是队友的 HP）；
    /// 本机自疗（施放 20644 与 400 字节内同旗标、同数值的受疗 20645）给出施放目标后才归属。同一连接 A 上沿用的实体与自疗目标不符时改绑。
    /// </summary>
    [Fact]
    public void CarriedEntityIsUsedOnlyOnItsOwnConnectionAndOtherConnectionsWaitForASelfHeal()
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0; var identities = new LocalIdentityMemory();
        using var fixture = JsonDocument.Parse(typeof(CombatMapSegmentStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var self = Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!);
        var player = Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true);
        var hits = Hits();
        var at = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        SharedGameStream Open(LocalIdentityMemory memory, CaptureConnection connection) =>
            new("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110, localIdentities: memory) { Connection = connection };
        using (var before = Open(identities, ConnectionA)) before.Feed(0, Frame(20590, self, true).Concat(player).ToArray(), 0, 0, at);
        var known = identities.Last!;
        // 伤害帧的攻击者恰为沿用的实体 ID：在连接 B 上它是另一个角色。
        Assert.Equal(0xabcdef0100000457UL, known.ActorId);
        // 队友的 HP 更新与本机受疗首尾相接（combat-evidence-20261110-confirmed-02 的形态）。
        var teammateHealth = new byte[21]; BinaryPrimitives.WriteUInt64LittleEndian(teammateHealth, 0x5C4900);
        BinaryPrimitives.WriteUInt32LittleEndian(teammateHealth.AsSpan(8), 8); BinaryPrimitives.WriteInt64LittleEndian(teammateHealth.AsSpan(12), 900);
        var teammateHeal = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(teammateHeal, 0x2000); BinaryPrimitives.WriteInt64LittleEndian(teammateHeal.AsSpan(8), 60);
        var adjacentOnly = Frame(170205, teammateHealth).Concat(Frame(20645, teammateHeal)).ToArray();
        // 本机自疗：受疗之后紧接施放，施放目标 0x5C48CE。
        var heal = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(heal, 0x2000); BinaryPrimitives.WriteInt64LittleEndian(heal.AsSpan(8), 50);
        var cast = new byte[24]; BinaryPrimitives.WriteUInt64LittleEndian(cast, 0x5C48CE); BinaryPrimitives.WriteUInt64LittleEndian(cast.AsSpan(8), 0x2000);
        BinaryPrimitives.WriteInt64LittleEndian(cast.AsSpan(16), 50);
        var selfHeal = Frame(20645, heal).Concat(Frame(20644, cast)).ToArray();

        reports.Clear();
        var carried = new LocalIdentityMemory(known, ObservedA);
        using var otherMap = Open(carried, ConnectionB);
        uint sequence = 1000;
        void Feed(SharedGameStream stream, byte[] bytes, double ms) { stream.Feed(sequence, bytes, ms, ms, at.AddMilliseconds(ms)); sequence += (uint)bytes.Length; }
        Feed(otherMap, hits, 10000); otherMap.Tick(10400, at.AddMilliseconds(10400));
        Assert.Null(otherMap.LocalEntity); Assert.True(carried.Carried);
        Assert.Null(SharedGameStream.ReportIdentity(otherMap.Music.Current, [otherMap], carried).Identity);
        // 只有受疗与紧邻的 HP：证据不足，保持未确认。
        Feed(otherMap, adjacentOnly.Concat(hits).ToArray(), 10450); otherMap.Tick(10480, at.AddMilliseconds(10480));
        Assert.Null(otherMap.LocalEntity);
        Assert.DoesNotContain(reports, report => report.LocalEntity is not null);
        // 本机自疗确认本机实体（0x5C48CE），之后本机的伤害才归属本机。
        var mine = new byte[53];
        BinaryPrimitives.WriteUInt64LittleEndian(mine, 0x5C48CE); BinaryPrimitives.WriteUInt64LittleEndian(mine.AsSpan(8), 0xabcdef0100000458);
        BinaryPrimitives.WriteInt64LittleEndian(mine.AsSpan(16), 77);
        Feed(otherMap, selfHeal.Concat(hits).Concat(Frame(21196, mine)).Concat(Frame(21196, mine)).ToArray(), 10500);
        otherMap.Tick(10900, at.AddMilliseconds(10900));
        Assert.Equal(0x5C48CEUL, otherMap.LocalEntity?.Key.RawInstanceId.Value);
        Validate(reports, profile);
        var session = new CombatSession("owner", "obs", profile.Source, otherMap.LocalEntity!, new("fixture-current"), at.AddMilliseconds(10500));
        foreach (var report in reports) session.Apply(report.Frame);
        session.Stop("observation-stopped", at.AddMilliseconds(12000), 12000);
        Assert.Equal("154", session.Capture().Encounters.Single().Summary.AttributedDamage.ToString());

        // 同一连接 A、沿用实体与自疗目标不符：紧邻的 HP 不改绑，本机自疗改绑到施放目标。
        using var sameConnection = Open(new LocalIdentityMemory(known with { ActorId = 77 }, ObservedA), ConnectionA);
        sequence = 1000;
        Feed(sameConnection, hits, 20000);
        Assert.Equal(77UL, sameConnection.LocalEntity?.Key.RawInstanceId.Value);
        Feed(sameConnection, adjacentOnly.Concat(hits).ToArray(), 20050);
        Assert.Equal(77UL, sameConnection.LocalEntity?.Key.RawInstanceId.Value);
        Feed(sameConnection, selfHeal.Concat(hits).ToArray(), 20100);
        Assert.Equal(0x5C48CEUL, sameConnection.LocalEntity?.Key.RawInstanceId.Value);
    }

    /// <summary>
    /// 捕获重启后宿主下发的实体延续记录只在同一连接上补足：首领的加密 HP 照常解出，报告带上本连接的延续记录（宿主按进程保存）；
    /// 其他连接不补足。延续记录与解码信息不出现在帧的实体快照中。
    /// </summary>
    [Fact]
    public void CarriedBossDecodingIsUsedOnlyOnItsOwnConnection()
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0;
        using var fixture = JsonDocument.Parse(typeof(CombatMapSegmentStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var identity = Frame(20590, Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!), true)
            .Concat(Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true)).ToArray();
        const ulong boss = 25178995;
        var iv = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        using var aes = System.Security.Cryptography.Aes.Create();
        var key = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(key, boss); BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), boss); aes.Key = key;
        var plain = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(plain, 1_234_567);
        var cipher = aes.EncryptCbc(plain, iv, System.Security.Cryptography.PaddingMode.PKCS7);
        var health = new byte[13 + cipher.Length]; BinaryPrimitives.WriteUInt64LittleEndian(health, boss);
        BinaryPrimitives.WriteUInt32LittleEndian(health.AsSpan(8), (uint)cipher.Length); cipher.CopyTo(health, 12);
        context.CombatCarry = new(ObservedA, [new(new(boss), 3, new(528684636), new(17_616_548), true, new(boss), Convert.ToBase64String(iv), true)]);
        var at = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

        using (var other = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110) { Connection = ConnectionB })
        {
            other.Feed(0, identity.Concat(Frame(170207, health)).ToArray(), 0, 0, at); other.Tick(400, at.AddMilliseconds(400));
            Assert.DoesNotContain(reports.SelectMany(report => report.Frame.Entities), entity => entity.Key.RawInstanceId.Value == boss);
        }
        reports.Clear();
        using var same = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110) { Connection = ConnectionA };
        same.Feed(0, identity.Concat(Frame(170207, health)).ToArray(), 1000, 1000, at.AddMilliseconds(1000)); same.Tick(1400, at.AddMilliseconds(1400));
        var entity = reports.Select(report => report.Frame.Entities.Single(value => value.Key.RawInstanceId.Value == boss)).Last(value => value.Health is not null);
        Assert.Equal((1_234_567UL, 17_616_548UL, true, "monster"), (entity.Health!.Current!.Value.Value, entity.Health.Maximum!.Value.Value, entity.IsBoss == true, entity.ActorType));
        Validate(reports, profile);
        var carried = reports.Last(report => report.Carry is not null).Carry!;
        Assert.Equal(ObservedA, carried.Connection);
        Assert.Equal(Convert.ToBase64String(iv), carried.Entities.Single().Iv);
        Assert.DoesNotContain(Convert.ToBase64String(iv), JsonSerializer.Serialize(reports[^1].Frame, ContractJson.Options));

        var memory = new MabiTool.Host.Combat.CombatEntityCarryMemory();
        var game = new GameSession("fixture-current", "TW", 1234, at, new(1), new(1), null);
        memory.Record(game, carried);
        Assert.Same(carried, memory.For(game));
        Assert.Null(memory.For(game with { ProcessStartTime = at.AddSeconds(1) }));
    }

    private static readonly CaptureConnection ConnectionA = new("192.168.0.105", 50128, "192.168.0.105", 55331);
    private static readonly CaptureConnection ConnectionB = new("192.168.0.105", 50131, "192.168.0.105", 55331);
    private static readonly ObservedConnection ObservedA = new(50128, "192.168.0.105", 55331);
    private static byte[] Hits()
    {
        var hit = new byte[53];
        BinaryPrimitives.WriteUInt64LittleEndian(hit, 0xabcdef0100000457);
        BinaryPrimitives.WriteUInt64LittleEndian(hit.AsSpan(8), 0xabcdef0100000458);
        BinaryPrimitives.WriteInt64LittleEndian(hit.AsSpan(16), 123);
        return Frame(21196, hit).Concat(Frame(21196, hit)).ToArray();
    }

    /// <summary>
    /// 同一连接内的重组缺口（缺口超时、驱动丢包）只丢这一段帧：本机身份、实体映射与代次保留，分段不结束、标记缺口，缺口后的伤害继续归属本机；
    /// 重传内容不一致仍整体重置。
    /// </summary>
    [Theory]
    [InlineData(GameStreamResetCause.GapTimeout, true)]
    [InlineData(GameStreamResetCause.CaptureDrops, true)]
    [InlineData(GameStreamResetCause.ConflictingRetransmission, false)]
    public void GapInsideTheConnectionKeepsTheSegmentAndTheLocalPlayer(GameStreamResetCause cause, bool kept)
    {
        List<CombatCaptureReport> reports = [];
        var profile = new ObservationCombatProfile(Tw20261110CombatProtocol.Profile,
            new("tw-combat-test", "1", "TW", "synthetic-current-layout"), Simulation: true);
        var context = new ObservationCaptureContext([profile], _ => { }, reports.Add);
        context.Configure(new(false, new(new string('a', 32), profile.Id, true, true, true)));
        ulong generation = 0;
        using var fixture = JsonDocument.Parse(typeof(CombatMapSegmentStreamTests).Assembly.GetManifestResourceStream("MabiTool.CurrentCombatIdentity")!);
        var identity = Frame(20590, Convert.FromBase64String(fixture.RootElement.GetProperty("personalBodyBase64").GetString()!), true)
            .Concat(Frame(20054, Convert.FromBase64String(fixture.RootElement.GetProperty("characterBodyBase64").GetString()!), true)).ToArray();
        var hit = new byte[53];
        BinaryPrimitives.WriteUInt64LittleEndian(hit, 0xabcdef0100000457);
        BinaryPrimitives.WriteUInt64LittleEndian(hit.AsSpan(8), 0xabcdef0100000458);
        BinaryPrimitives.WriteInt64LittleEndian(hit.AsSpan(16), 123);
        var hits = Frame(21196, hit).Concat(Frame(21196, hit)).ToArray();
        var at = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        using var stream = new SharedGameStream("fixture-current", context, () => ++generation, layout: CombatProtocolLayout.Tw20261110);
        stream.Feed(0, identity.Concat(hits).ToArray(), 0, 0, at); stream.Tick(300, at.AddMilliseconds(300));
        var local = stream.LocalEntity!;
        stream.Restart("private native detail", 1000, at.AddMilliseconds(1000), cause);
        // 缺口后从新位置对齐继续（缺口内的字节没收到）。
        stream.Feed(50_000, hits, 1100, 1100, at.AddMilliseconds(1100)); stream.Tick(1400, at.AddMilliseconds(1400));
        Assert.Equal(kept, stream.LocalEntity?.Key == local.Key);
        Assert.NotNull(stream.Music.Current.Identity);
        Validate(reports, profile);
        var session = new CombatSession("owner", "obs", profile.Source, reports.First(x => x.LocalEntity is not null).LocalEntity!, new("fixture-current"), at);
        foreach (var report in reports) session.Apply(report.Frame);
        session.Stop("observation-stopped", at.AddMilliseconds(2000), 2000);
        var encounters = session.Capture().Encounters;
        if (kept)
        {
            var single = Assert.Single(encounters);
            Assert.Equal("492", single.Summary.AttributedDamage.ToString());
            Assert.Contains(single.Gaps, gap => gap.Reason.StartsWith("combat.streamGap", StringComparison.Ordinal));
        }
        else Assert.Equal("stream-reset", encounters.OrderBy(x => x.Segment!.Ordinal).First().Summary.BoundaryReason);
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
