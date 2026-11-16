using System.Buffers.Binary;
using System.Text.Json;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>当前构建身份与音乐观察：夹具覆盖独奏、合奏及进图快照，保留脱敏与合成绑定来源。</summary>
public sealed class TwMusicObserverTests
{
    private static readonly JsonDocument Fixture = JsonDocument.Parse(typeof(TwMusicObserverTests).Assembly
        .GetManifestResourceStream("MabiTool.TwMusicFixture")!);
    private static readonly TwMusicProtocolMap Map = TwMusicProtocolMap.Tw20261110;
    private static ulong Number(string name) => ulong.Parse(Fixture.RootElement.GetProperty(name).GetString()!);
    private static byte[] Body(string name) => Convert.FromBase64String(Fixture.RootElement.GetProperty(name).GetString()!);
    private static byte[] EnterMapBody(string name = "emptyBody") =>
        Convert.FromBase64String(Fixture.RootElement.GetProperty("enterMap").GetProperty(name).GetString()!);
    private static ulong LocalEntity => Number("localEntity");
    private static ulong TeammateEntity => Number("teammateEntity");
    private static byte[] Personal(ulong character)
    {
        // 当前构建登录正文开头 8 字节为角色 ID，其后为库存等数据。
        var body = new byte[64]; BinaryPrimitives.WriteUInt64LittleEndian(body, character); return body;
    }
    private static IEnumerable<(double At, int Code, byte[] Body)> Frames(string scenario) =>
        Fixture.RootElement.GetProperty(scenario).GetProperty("frames").EnumerateArray()
            .Select(item => (item.GetProperty("atMs").GetDouble(), item.GetProperty("code").GetInt32(), Convert.FromBase64String(item.GetProperty("body").GetString()!)));
    private static TwGameObserver Identified()
    {
        var observer = new TwGameObserver(Map);
        observer.Accept(Map.Entering, EnterMapBody());
        observer.Accept(Map.Personal, Personal(1001));
        observer.Accept(Map.Appearing, Body("localAppearance"));
        observer.Accept(Map.Appearing, Body("teammateAppearance"));
        return observer;
    }

    [Fact]
    public void LocalIdentityComesFromAppearanceWhoseCharacterMatchesPersonalData()
    {
        var observer = new TwGameObserver(Map);
        observer.Accept(Map.Appearing, Body("teammateAppearance"));
        observer.Accept(Map.Appearing, Body("localAppearance"));
        Assert.Null(observer.Current.Identity);
        observer.Accept(Map.Personal, Personal(1001));
        Assert.Equal(new GameIdentity(1001, 3, "本机甲", LocalEntity), observer.Current.Identity);
        // 附近角色的出现帧不会替换本机。
        foreach (var item in Fixture.RootElement.GetProperty("solo").GetProperty("appearances").EnumerateArray())
            observer.Accept(Map.Appearing, Convert.FromBase64String(item.GetProperty("body").GetString()!));
        Assert.Equal(LocalEntity, observer.Current.Identity!.ActorId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalIdentityReturnsWhenOneOfItsConflictingEntitiesDisappears(bool removeOriginal)
    {
        var observer = Identified();
        var original = observer.Current.Identity!;
        var duplicate = original with { ActorId = original.ActorId + 1000 };
        observer.AcceptIdentity(duplicate);
        Assert.Null(observer.Current.Identity);

        var removed = removeOriginal ? original : duplicate;
        var remaining = removeOriginal ? duplicate : original;
        observer.Accept(Map.Disappearing, BitConverter.GetBytes(removed.ActorId));

        Assert.Equal(remaining, observer.Current.Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalIdentityReturnsWhenOneOfItsConflictingEntitiesChangesCharacter(bool changeOriginal)
    {
        var observer = Identified();
        var original = observer.Current.Identity!;
        var duplicate = original with { ActorId = original.ActorId + 1000 };
        observer.AcceptIdentity(duplicate);
        Assert.Null(observer.Current.Identity);

        var changed = changeOriginal ? original : duplicate;
        var remaining = changeOriginal ? duplicate : original;
        observer.AcceptIdentity(changed with { CharacterId = 2001 });

        Assert.Equal(remaining, observer.Current.Identity);
    }

    [Fact]
    public void AppearanceDecodesIdentityAndEmptyInstrumentSnapshotFromLiveBodies()
    {
        foreach (var scenario in new[] { "solo", "ensemble" })
            foreach (var item in Fixture.RootElement.GetProperty(scenario).GetProperty("appearances").EnumerateArray())
            {
                var value = TwCharacterMusicAppearance.Decode(Convert.FromBase64String(item.GetProperty("body").GetString()!));
                Assert.True(value.Identity.CharacterId > 2000 && value.Identity.ActorId >= 0x2000 && value.Identity.RealmId >= 0);
                Assert.StartsWith("路人", value.Identity.Nickname);
                Assert.Null(value.Instrument);
            }
    }

    [Fact]
    public void SoloReplayReportsScoreDurationsLoopAndManualStops()
    {
        var observer = Identified();
        var seen = new List<MusicObservation>();
        foreach (var (at, code, body) in Frames("solo"))
        {
            observer.Accept(code, body, at);
            if (observer.Current.Music is { } music) seen.Add(music);
        }
        var concerts = seen.GroupBy(music => music.ConcertId).Select(group => group.First()).ToArray();
        Assert.Equal(2, concerts.Length);
        foreach (var music in concerts)
        {
            Assert.Equal(1001UL, music.PerformerCharacterId);
            Assert.Equal(1001UL, music.HostCharacterId);
            Assert.True(music.IsLoop);
            Assert.Equal([0], music.SelectedChannels);
            Assert.Equal(64, music.ScoreSha256!.Length);
            Assert.True(music.WholeScoreDurationSeconds > 0 && music.SelectedDurationSeconds <= music.WholeScoreDurationSeconds);
            Assert.True(music.AutomaticStopAvailable);
            Assert.Equal("tw-receive-offset-v1", music.TimingSource);
        }
        // 合奏新增帧的 StartAt 为 +1.5 s：开始时间估计为新增帧时点加 1500 ms。
        var add = Frames("solo").First(frame => frame.Code == Map.ConcertAdd && BitConverter.ToUInt64(frame.Body) == concerts[0].ConcertId);
        Assert.Equal(add.At + 1500, concerts[0].EstimatedStartLocalMs);
        // 两次手动停止后不再报告演奏。
        Assert.Null(observer.Current.Music);
        Assert.NotEqual(concerts[0].ScoreSha256, concerts[1].ScoreSha256);
    }

    [Fact]
    public void EnsembleReplayGivesHostFirstCandidatesWhileLocalPlaysAndFollowsConcertSwitch()
    {
        var observer = Identified();
        var states = new List<GameObservation>();
        foreach (var (at, code, body) in Frames("ensemble")) { observer.Accept(code, body, at); states.Add(observer.Current); }
        var joined = states.Where(state => state.Concert is not null).ToArray();
        Assert.NotEmpty(joined);
        foreach (var state in joined)
        {
            Assert.Equal([1002UL, 1001UL], state.Concert!.Members.Select(member => member.CharacterId));
            Assert.Equal(1002UL, state.Concert.HostCharacterId);
            Assert.Equal(state.Music!.ConcertId, state.Concert.Id);
            Assert.False(state.Music.IsLoop);
        }
        // 队友换曲后本机自动跟随新合奏，成员候选保持。
        Assert.True(joined.Select(state => state.Concert!.Id).Distinct().Count() == 2);
        // 结束时本机已停止：不再有乐谱与候选。
        Assert.Null(states[^1].Music); Assert.Null(states[^1].Concert);
    }

    [Fact]
    public void MusicInterestOffKeepsIdentityAndIgnoresConcertFrames()
    {
        var observer = Identified();
        observer.MusicEnabled = false;
        foreach (var (at, code, body) in Frames("solo").Take(3)) observer.Accept(code, body, at);
        Assert.NotNull(observer.Current.Identity);
        Assert.Null(observer.Current.Music);
        Assert.Equal(0, observer.Usage.Concerts);
    }

    [Fact]
    public void AppearanceSnapshotOfAPlayingLocalCharacterWaitsForScoreAndMapChangeClears()
    {
        var observer = Identified();
        var play = Frames("solo").First(frame => frame.Code == Map.Play);
        var concert = BitConverter.ToUInt64(play.Body.AsSpan(8));
        observer.AcceptIdentity(new(1001, 3, "本机甲", LocalEntity), new(concert, [0], true));
        Assert.Null(observer.Current.Music);
        Assert.Contains("等待乐谱信息", observer.Current.Detail);
        var add = Frames("solo").First(frame => frame.Code == Map.ConcertAdd && BitConverter.ToUInt64(frame.Body) == concert);
        observer.Accept(add.Code, add.Body, add.At);
        Assert.Equal(concert, observer.Current.Music!.ConcertId);
        observer.Accept(Map.Entering, EnterMapBody());
        Assert.Null(observer.Current.Identity); Assert.Null(observer.Current.Music);
        Assert.Equal((0, 0, 0, 0), observer.Usage);
        // 角色 ID 属于连接：切图后本机出现帧即可重新识别。
        observer.Accept(Map.Appearing, Body("localAppearance"));
        Assert.Equal(LocalEntity, observer.Current.Identity!.ActorId);
    }

    [Fact]
    public void EnterMapSnapshotRestoresRunningScoreWithDynamicPrefixAndByteBudget()
    {
        var fixture = Fixture.RootElement.GetProperty("enterMap");
        var character = ulong.Parse(fixture.GetProperty("characterId").GetString()!);
        var received = fixture.GetProperty("atMs").GetDouble();
        var appearanceAt = fixture.GetProperty("appearanceAtMs").GetDouble();
        var expected = new TwGameObserver(Map);
        expected.Accept(Map.Personal, Personal(character));
        expected.Accept(Map.Appearing, EnterMapBody("playingAppearance"), appearanceAt);
        expected.Accept(Map.ConcertAdd, EnterMapBody("concertAddBody"), 41110);
        Assert.NotNull(expected.Current.Music);

        foreach (var variant in new[] { "body", "variablePrefixBody", "largeScoreBody" })
        {
            var observer = Identified();
            observer.Accept(Map.Entering, EnterMapBody(variant), received);
            Assert.Null(observer.Current.Identity);
            Assert.Null(observer.Current.Music);
            Assert.Equal(2, observer.Usage.Concerts);
            observer.Accept(Map.Appearing, EnterMapBody("playingAppearance"), appearanceAt);
            Assert.Equal(ulong.Parse(fixture.GetProperty("actorId").GetString()!), observer.Current.Identity!.ActorId);
            var music = Assert.IsType<MusicObservation>(observer.Current.Music);
            Assert.Equal(ulong.Parse(fixture.GetProperty("concertId").GetString()!), music.ConcertId);
            Assert.Equal(character, music.HostCharacterId);
            Assert.Equal(expected.Current.Music.SelectedChannels, music.SelectedChannels);
            Assert.Equal(received + fixture.GetProperty("startOffsetSeconds").GetDouble() * 1000, music.EstimatedStartLocalMs);
            Assert.Equal("tw-receive-offset-v1", music.TimingSource);
            var large = variant == "largeScoreBody";
            Assert.Equal(fixture.GetProperty(large ? "largeScoreSha256" : "scoreSha256").GetString(), music.ScoreSha256);
            Assert.Equal(large ? 156.25 : expected.Current.Music.WholeScoreDurationSeconds, music.WholeScoreDurationSeconds);
        }

        var invalid = Identified();
        Assert.Throws<InvalidDataException>(() => invalid.Accept(Map.Entering, EnterMapBody("body").Concat(new byte[] { 0 }).ToArray(), received));
        Assert.Null(invalid.Current.Identity);
        Assert.Null(invalid.Current.Music);
        Assert.Equal(0, invalid.Usage.Concerts);

        var disabled = Identified();
        disabled.MusicEnabled = false;
        disabled.Accept(Map.Entering, [], received);
        disabled.Accept(Map.Appearing, EnterMapBody("playingAppearance"), appearanceAt);
        Assert.Equal(ulong.Parse(fixture.GetProperty("actorId").GetString()!), disabled.Current.Identity!.ActorId);
        Assert.Null(disabled.Current.Music);
        Assert.Equal(0, disabled.Usage.Concerts);
    }

    [Fact]
    public void LocalDisappearanceAndStreamResetClearState()
    {
        var observer = Identified();
        foreach (var (at, code, body) in Frames("ensemble").Take(6)) observer.Accept(code, body, at);
        observer.Accept(Map.Disappearing, BitConverter.GetBytes(TeammateEntity));
        Assert.NotNull(observer.Current.Identity);
        observer.Accept(Map.Disappearing, BitConverter.GetBytes(LocalEntity));
        Assert.Null(observer.Current.Identity); Assert.Null(observer.Current.Music);
        observer.Reset("连接已更新");
        observer.Accept(Map.Appearing, Body("localAppearance"));
        Assert.Null(observer.Current.Identity);
    }

    [Fact]
    public void ActorConcertAndMemberStateStaysBounded()
    {
        var observer = Identified();
        for (ulong i = 0; i < TwGameObserver.MaximumActors + 500; i++)
            observer.AcceptIdentity(new(50_000 + i, 3, "路人", 90_000 + i));
        Assert.Equal(TwGameObserver.MaximumActors, observer.Usage.Actors);
        Assert.Equal(LocalEntity, observer.Current.Identity!.ActorId);
        var add = Frames("solo").First(frame => frame.Code == Map.ConcertAdd).Body;
        for (ulong i = 1; i <= TwGameObserver.MaximumConcerts + 50; i++)
        {
            var body = add.ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(body, 10_000 + i);
            observer.Accept(Map.ConcertAdd, body, 1000);
        }
        Assert.Equal(TwGameObserver.MaximumConcerts, observer.Usage.Concerts);
        for (ulong i = 0; i < TwGameObserver.MaximumActors + 10; i++)
        {
            var play = new byte[25]; BinaryPrimitives.WriteUInt64LittleEndian(play, 90_000 + i); BinaryPrimitives.WriteUInt64LittleEndian(play.AsSpan(8), 7);
            observer.Accept(Map.Play, play);
        }
        Assert.Equal(TwGameObserver.MaximumActors, observer.Usage.Plays);
        var members = new byte[4 + 129 * 8]; BinaryPrimitives.WriteInt32LittleEndian(members, 129);
        Assert.Throws<InvalidDataException>(() => observer.Accept(Map.ConcertJoin, members));
    }
}
