// 来源：MinamiChiwa/MabiSync，固定提交 2cb69b5ddec8de4fe9d20ec98bbb410ac015e671；按协议表支持当前台服构建，去掉加入顺序核对。
namespace MabiTool.Providers.TwObservation;

/// <summary>
/// 每条接收连接一个实例，只读观察，不发出任何输入。身份帧始终解码；合奏、演奏帧只在 <see cref="MusicEnabled"/> 时解码。
/// 游戏内演奏自动循环、不会自然停止：一轮结束由开始时间与乐谱时长推算，停止帧只表示手动停止。
/// 常驻状态全部有界：角色与演奏各至多 <see cref="MaximumActors"/> 个，合奏至多 <see cref="MaximumConcerts"/> 个，
/// 合奏成员至多 128 个；切图、本机消失和连接重置清空。当前构建的进图通知另记录当前频道（一项），
/// 进图时替换、连接重置时清空。
/// </summary>
public sealed class TwGameObserver
{
    public const int MaximumActors = 4096, MaximumConcerts = 256;
    private sealed record Score(ulong Host, string Title, bool Loop, string WireHash, TwScoreTimeline Content, double? EstimatedStart);
    private sealed record Play(ulong Concert, int[] Channels);
    private readonly TwMusicProtocolMap map;
    private readonly Dictionary<ulong, GameIdentity> _actors = [];
    private readonly Queue<ulong> _actorOrder = new();
    private readonly Dictionary<ulong, Score> _concerts = [];
    private readonly Queue<ulong> _concertOrder = new();
    private readonly Dictionary<ulong, Play> _playing = [];
    private readonly Queue<ulong> _playOrder = new();
    private ulong? _personal;
    private GameChannel? _channel;
    private ulong[] _members = [];
    private bool _music = true;
    private string _generation = Guid.NewGuid().ToString("N");
    private long _sequence;
    public GameObservation Current { get; private set; }
    public event Action<GameObservation>? Changed;
    public TwGameObserver() : this(TwMusicProtocolMap.Reference) { }
    public TwGameObserver(TwMusicProtocolMap protocols)
    {
        map = protocols;
        Current = new(_generation, ++_sequence, null, null, IdentityPending);
    }
    public TwMusicProtocolMap Protocols => map;
    /// <summary>当前持有的角色、合奏与演奏条目数，供资源测试核对上限。</summary>
    public (int Actors, int Concerts, int Plays, int Members) Usage => (_actors.Count, _concerts.Count, _playing.Count, _members.Length);
    private const string IdentityPending = "等待识别本机角色，请切换一次地图或重新登录角色。";

    /// <summary>音乐兴趣开关；关闭时清空合奏与演奏状态，身份保留。</summary>
    public bool MusicEnabled
    {
        get => _music;
        set { if (_music == value) return; _music = value; ClearMusic(); Publish(); }
    }
    public bool Observes(int code) => map.IdentityCodes.Contains(code) || _music && map.MusicCodes.Contains(code);
    /// <summary>本连接的本机角色 ID：来自个人数据帧，或按 <see cref="RememberedCharacterId"/> 由本机出现帧补足；未知为空。</summary>
    public ulong? PersonalId => _personal;
    /// <summary>
    /// 同一游戏进程内已确认的本机角色 ID，由捕获侧设置。个人数据帧（20590）只在连接建立后约 0.3–1.3 s 内发送一次，
    /// 而进程连接表常晚于此才列出新连接，抓包过滤器打开时这一帧可能已经过去；本连接没有个人数据时，
    /// 出现帧的角色 ID 等于该值即补作本连接的角色 ID。条件：同一游戏进程（捕获按进程绑定），同一角色同一时刻只能在线一处，
    /// 因此该角色在本连接出现即是本机；个人数据帧随后给出不同的角色 ID 时以个人数据为准并重置。
    /// </summary>
    public ulong? RememberedCharacterId { get; set; }
    /// <summary>连接级重置：清空身份、地图状态与当前频道，开始新的观察代次。</summary>
    public void Reset(string reason)
    {
        Clear(); _personal = null; _channel = null; Publish(reason);
    }
    /// <summary>
    /// 同一 TCP 连接内的流重同步（丢包、缺口超时、帧头失步等）：角色 ID 属于连接，保留角色 ID、角色表与当前频道，
    /// 只清空合奏与演奏状态（缺口内可能漏掉停止或移除帧）。缺口内若恰好换了角色，下一次进图清空角色表后
    /// 旧角色不再出现，身份随之转为未识别，不会长期报告旧角色；换角色时的新个人数据帧照常重置。
    /// </summary>
    public void Resync(string reason)
    {
        ClearMusic(); Publish(reason);
    }
    /// <summary>
    /// 单帧解析失败：丢弃该帧。合奏、演奏帧失败时清空合奏与演奏状态（可能漏掉成员或停止变化）；
    /// 身份、角色表与频道保持，已确认的本机身份不因一帧失败清空。
    /// </summary>
    public void RejectFrame(int code)
    {
        if (!map.MusicCodes.Contains(code) && code != map.Entering) return;
        ClearMusic(); Publish();
    }
    private void Clear()
    {
        _actors.Clear(); _actorOrder.Clear(); _localDirty = true; ClearMusic();
        _generation = Guid.NewGuid().ToString("N"); _sequence = 0;
    }
    private void ClearMusic()
    {
        _concerts.Clear(); _concertOrder.Clear(); _playing.Clear(); _playOrder.Clear(); _members = [];
    }
    /// <summary>
    /// 角色 ID 属于连接；实体 ID 与合奏属于地图，切图后等待新的出现帧再确认本机。
    /// 当前频道由进图通知替换；本机消失时保留，等待随后的进图通知。
    /// </summary>
    private void ResetMap(string reason, GameChannel? channel)
    {
        Clear(); _channel = channel; Publish(reason);
    }
    public void AcceptIdentity(GameIdentity identity, InstrumentSnapshot? instrument = null)
    {
        if (identity.ActorId == 0 || identity.CharacterId == 0 || identity.RealmId < 0 || string.IsNullOrWhiteSpace(identity.Nickname) || identity.Nickname.Length > 64)
            throw new InvalidDataException("Invalid identity");
        if (!_actors.ContainsKey(identity.ActorId))
        {
            if (_actors.Count >= MaximumActors) _localDirty = true;
            Bound(_actors, _actorOrder, MaximumActors, _actors.Count >= MaximumActors ? Local()?.ActorId : null);
            _actorOrder.Enqueue(identity.ActorId);
        }
        if (_personal is null && RememberedCharacterId is { } remembered && identity.CharacterId == remembered)
            _personal = remembered;
        if (identity.CharacterId == _personal || _local?.ActorId == identity.ActorId ||
            _actors.TryGetValue(identity.ActorId, out var previous) && previous.CharacterId == _personal) _localDirty = true;
        _actors[identity.ActorId] = identity;
        if (_music && instrument is { ConcertId: not 0 } play) SetPlay(identity.ActorId, new(play.ConcertId, play.Channels.ToArray()));
        Publish();
    }
    public void AcceptPersonalId(ulong id)
    {
        if (id == 0) throw new InvalidDataException("Invalid local character ID");
        if (_personal is { } old && old != id) Reset("角色已更换，等待重新识别。");
        _personal = id; _localDirty = true; Publish();
    }
    public void Accept(int code, ReadOnlySpan<byte> bytes, double? receivedLocalMs = null)
    {
        if (!Observes(code)) return;
        if (code == map.Entering)
        {
            ResetMap("游戏正在切换地图，等待重新识别角色。", map == TwMusicProtocolMap.Tw20261110 ? TwChannelMusicSnapshot.ReadChannel(bytes) : null);
            if (_music && map == TwMusicProtocolMap.Tw20261110)
            {
                try { TwChannelMusicSnapshot.Read(bytes, concertReader => AcceptConcert(concertReader, receivedLocalMs, false)); }
                catch (InvalidDataException) { ClearMusic(); throw; }
                Publish();
            }
            return;
        }
        var reader = new TwGameProtocol.WireReader(bytes.ToArray());
        if (code == map.Appearing)
        {
            if (map == TwMusicProtocolMap.Reference) AcceptIdentity(TwGameProtocol.DecodeCharacter(bytes));
            else { var appearance = TwCharacterMusicAppearance.Decode(bytes); AcceptIdentity(appearance.Identity, appearance.Instrument); }
            return;
        }
        if (code == map.Personal)
        {
            // 个人数据开头即角色 ID；当前构建的登录正文另含库存，只读前 8 字节。
            if (bytes.Length < 8) throw new InvalidDataException("Truncated personal data");
            AcceptPersonalId(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes)); return;
        }
        if (code == map.Disappearing)
        {
            var gone = reader.ULong(); reader.Finish();
            if (Local()?.ActorId == gone) { ResetMap("角色正在切换地图，等待重新识别。", _channel); return; }
            if (_actors.Remove(gone, out var removed) && removed.CharacterId == _personal) _localDirty = true;
            _playing.Remove(gone);
            if (_members.Contains(gone)) _members = _members.Where(id => id != gone).ToArray();
        }
        else if (code == map.ConcertJoin)
        {
            var count = reader.Count(128); var members = new ulong[count];
            for (var i = 0; i < count; i++) members[i] = reader.ULong();
            reader.Finish();
            if (members.Any(id => id == 0) || members.Distinct().Count() != count) throw new InvalidDataException("Duplicate/invalid concert member");
            _members = members;
        }
        else if (code == map.ConcertAdd) AcceptConcert(reader, receivedLocalMs);
        else if (code == map.Play)
        {
            var actor = reader.ULong(); var concert = reader.ULong(); var channels = reader.Count(128);
            var parts = new int[channels];
            for (var i = 0; i < channels; i++) { var channel = reader.Int(); if (channel is < 0 or >= 128) throw new InvalidDataException("Invalid instrument channel"); parts[i] = channel; }
            reader.Int(); reader.Bool(); reader.Finish();
            if (actor == 0 || concert == 0) throw new InvalidDataException("Invalid Play identity");
            SetPlay(actor, new(concert, parts));
        }
        else if (code == map.Stop)
        {
            var actor = reader.ULong(); var stopped = reader.Remaining == 8 ? reader.ULong() : (ulong?)null; reader.Finish();
            if (stopped is null || _playing.GetValueOrDefault(actor)?.Concert == stopped)
            {
                _playing.Remove(actor);
                // 手动停止即离开合奏；再次加入时游戏重新发送成员表。
                if (actor == Local()?.ActorId) _members = [];
            }
        }
        else if (code == map.ConcertRemove)
        {
            var concert = reader.ULong(); reader.Finish(); _concerts.Remove(concert);
            foreach (var id in _playing.Where(p => p.Value.Concert == concert).Select(p => p.Key).ToArray()) _playing.Remove(id);
        }
        Publish();
    }
    private void AcceptConcert(TwGameProtocol.WireReader reader, double? receivedLocalMs, bool finish = true)
    {
        var concert = reader.ULong(); var host = reader.ULong(); var title = reader.Text(); reader.Text();
        var score = reader.Take(reader.Int()).ToArray(); reader.Bool(); var loop = reader.Bool();
        // ReplicatedTime 在线上只有 offsetInSeconds：相对本帧的秒数，新建合奏为正，进图时已开始的合奏为负。
        var start = BitConverter.Int32BitsToSingle(reader.Int());
        if (!float.IsFinite(start)) throw new InvalidDataException("Invalid concert offset");
        if (finish) reader.Finish();
        if (concert == 0 || host == 0) throw new InvalidDataException("Invalid concert identity");
        var wireHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(score));
        title = string.Concat(title.Where(c => !char.IsControl(c))).Trim();
        if (title.Length > 512) title = title[..512];
        var estimated = receivedLocalMs is { } received && double.IsFinite(received) ? received + start * 1000d : (double?)null;
        if (!_concerts.TryGetValue(concert, out var cached))
        {
            Bound(_concerts, _concertOrder, MaximumConcerts, LocalPlay()?.Concert);
            _concertOrder.Enqueue(concert);
        }
        // 同一合奏刷新时保留首次的开始时间估计，只有乐谱内容或循环设置变化才重新解码。
        _concerts[concert] = cached is not null && cached.WireHash == wireHash && cached.Loop == loop
            ? cached with { Title = title, Host = host, EstimatedStart = cached.EstimatedStart ?? estimated }
            : new(host, title, loop, wireHash, TwScoreTimeline.Decode(score, loop), estimated);
    }
    private void SetPlay(ulong actor, Play play)
    {
        if (!_playing.ContainsKey(actor))
        {
            Bound(_playing, _playOrder, MaximumActors, Local()?.ActorId);
            _playOrder.Enqueue(actor);
        }
        _playing[actor] = play;
    }
    /// <summary>达到上限时按加入顺序淘汰最早的条目（保留 <paramref name="keep"/>）；顺序队列超过两倍上限时按现存键重建。</summary>
    private static void Bound<T>(Dictionary<ulong, T> values, Queue<ulong> order, int maximum, ulong? keep)
    {
        var guard = order.Count;
        while (values.Count >= maximum && guard-- > 0 && order.TryDequeue(out var oldest))
        {
            if (!values.ContainsKey(oldest)) continue;
            if (oldest == keep) { order.Enqueue(oldest); continue; }
            values.Remove(oldest);
        }
        if (order.Count > maximum * 2)
        {
            var live = order.Where(values.ContainsKey).Distinct().ToArray(); order.Clear();
            foreach (var key in live) order.Enqueue(key);
        }
    }
    private bool _localDirty = true;
    private GameIdentity? _local;
    /// <summary>本机角色：角色 ID 等于个人数据且唯一的出现帧。只在相关条目变化时重算，避免每帧遍历角色表。</summary>
    private GameIdentity? Local()
    {
        if (!_localDirty) return _local;
        _localDirty = false; _local = null;
        if (_personal is not { } personal) return null;
        foreach (var actor in _actors.Values)
        {
            if (actor.CharacterId != personal) continue;
            if (_local is not null) { _local = null; break; }
            _local = actor;
        }
        return _local;
    }
    private Play? LocalPlay() => Local() is { } local ? _playing.GetValueOrDefault(local.ActorId) : null;
    private MusicObservation? LocalMusic(GameIdentity local)
    {
        if (!_playing.TryGetValue(local.ActorId, out var play) || !_concerts.TryGetValue(play.Concert, out var score)) return null;
        var selected = play.Channels.Distinct().ToArray();
        var timeline = score.Content.Timeline;
        var issue = score.Content.Issue;
        double? whole = null, subset = null;
        if (timeline is { TimingSupported: true })
        {
            whole = timeline.Tracks.Max(t => t.LastEventEndSeconds);
            if (selected.Length > 0 && selected.Length == play.Channels.Length && selected.All(i => i < timeline.Tracks.Count))
                subset = selected.Max(i => timeline.Tracks[i].LastEventEndSeconds);
            else issue = "尚未确认本次演奏包含哪些音轨。";
            if (whole > 7 * 24 * 3600 || subset > 7 * 24 * 3600)
            { whole = subset = null; issue = "乐谱过长，暂时无法计算一轮结束时间。"; }
        }
        issue ??= score.EstimatedStart is null ? "乐谱时长已识别，等待识别本轮演奏的开始时间。" : null;
        return new(play.Concert, local.CharacterId, score.Host, score.Title, score.Loop, Array.AsReadOnly(selected), score.Content.Sha256, whole, subset, issue)
            { EstimatedStartLocalMs = score.EstimatedStart };
    }
    /// <summary>合奏成员候选：本机正在演奏且在成员表内时，取已识别的成员角色，发起者在前；只用于给房主列出可匹配的角色。</summary>
    private ConcertObservation? Candidates(GameIdentity local, MusicObservation? music)
    {
        if (music is null || !_members.Contains(local.ActorId)) return null;
        var members = _members.Select(id => _actors.GetValueOrDefault(id)).OfType<GameIdentity>()
            .Where(member => member.RealmId == local.RealmId).DistinctBy(member => member.CharacterId).ToList();
        var host = members.FindIndex(member => member.CharacterId == music.HostCharacterId);
        if (host < 0) return null;
        var first = members[host]; members.RemoveAt(host); members.Insert(0, first);
        return new(music.ConcertId, music.HostCharacterId, members.AsReadOnly());
    }
    private void Publish(string? detail = null)
    {
        var local = Local();
        var music = local is not null && _music ? LocalMusic(local) : null;
        var concert = local is not null && _music ? Candidates(local, music) : null;
        var playing = local is not null && _music && music is null && _playing.ContainsKey(local.ActorId);
        Current = new(_generation, ++_sequence, local, concert, detail ?? (local is null ? IdentityPending
            : playing ? "正在演奏，等待乐谱信息（切换地图后可识别正在进行的合奏）。"
            : music is null ? "已识别本机角色。" : "已识别本机角色与当前乐谱。")) { Music = music, Channel = _channel };
        Changed?.Invoke(Current);
    }
}
