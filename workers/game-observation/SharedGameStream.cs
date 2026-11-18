using System.Diagnostics;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation;
using MabiTool.Providers.TwObservation.Combat;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 每条连接共用一份 TCP 重组；身份/音乐、战斗和库存分别维护解析状态。本机身份帧不依赖模块兴趣、始终解码，
/// 合奏与演奏帧只在音乐兴趣开启时解码；战斗兴趣建立晚于身份帧时，以已确认的本机身份为战斗解析补足起点。
/// 单帧解析失败只丢弃该帧；同一连接内的流重同步保留本机身份，新连接才清空（见 <see cref="NewConnection"/>）。
/// </summary>
public sealed class SharedGameStream : IDisposable
{
    private readonly ObservationCaptureContext context;
    private readonly string gameSession;
    private readonly Func<ulong> generations;
    private readonly IDisposable registration;
    private readonly Func<SharedGameStream, bool> selected;
    private readonly CombatProtocolLayout layout;
    private TwCombatObserver? combat;
    private InventoryProtocolObserver? inventory;
    private readonly Func<SharedGameStream, bool> inventorySelected;
    private string? interestId;
    private double now;
    private DateTimeOffset utc;
    private ulong delivered;
    public GameTcpStream Stream { get; }
    public TwGameObserver Music { get; }
    public double MusicInitializerAt { get; private set; } = double.NegativeInfinity;
    public double CombatInitializerAt { get; private set; } = double.NegativeInfinity;
    public double InventoryInitializerAt { get; private set; } = double.NegativeInfinity;
    public CombatEntityObservation? LocalEntity => combat?.LocalEntity;
    public ulong? LocalCharacterId => combat?.LocalCharacterId;
    private readonly LocalIdentityMemory identities;
    private readonly bool identityEnabled, musicEnabled, combatEnabled;
    /// <param name="localIdentities">同一游戏进程的捕获内各连接共用的本机身份记录；为空时只在本连接内保留。</param>
    /// <param name="build">当前布局下客户端构建的核对结果；为空按已登记构建处理。未登记构建的分帧使用读到的协议号，
    /// 核对不过的身份（含地图频道）、音乐与战斗解析分别停用：身份帧不解码，音乐兴趣不生效，战斗兴趣以 combat.buildUnsupported 拒绝。</param>
    public SharedGameStream(string gameSessionId, ObservationCaptureContext capture, Func<ulong> nextGeneration,
        Func<SharedGameStream, bool>? primary = null, CombatProtocolLayout layout = CombatProtocolLayout.MusicOnly,
        Func<SharedGameStream, bool>? inventoryPrimary = null, LocalIdentityMemory? localIdentities = null, TwBuildVerdict? build = null)
    {
        context = capture; gameSession = gameSessionId; generations = nextGeneration; selected = primary ?? (_ => true);
        identities = localIdentities ?? new();
        this.layout = layout;
        identityEnabled = build?.Identity.Enabled ?? true; musicEnabled = build?.Music.Enabled ?? true; combatEnabled = build?.Combat.Enabled ?? true;
        Music = new(layout == CombatProtocolLayout.MusicOnly ? TwMusicProtocolMap.Reference : TwMusicProtocolMap.Tw20261110);
        inventorySelected = inventoryPrimary ?? (_ => true);
        // 共用流按当前兴趣分发：战斗 profile 只列出战斗协议号，库存协议号来自运行时客户端布局，
        // 因此候选集取全部已知协议号，由下方兴趣判定决定是否展开和分发。
        var known = layout == CombatProtocolLayout.MusicOnly ? MusicOnlyProtocols
            : build?.KnownProtocols ?? Tw20261110CombatProtocol.StreamProfile.KnownProtocols;
        Stream = new(Dispatch, ResetParsers, new GameStreamProfile(known, known),
            code => ObserveMusic(code) || ObserveCombat(code) || inventory?.Observes(code) == true,
            (code, reason) =>
            {
                // 展开失败的身份或音乐帧只丢弃该帧，已确认的本机身份保持。
                if (ObserveMusic(code)) { context.Diagnostics.Rejected("music"); Music.RejectFrame(code); }
                if (ObserveCombat(code)) combat!.Reject("combat.protocolInvalid", now, utc);
                if (inventory?.Observes(code) == true) PostInventoryReset("inventory.protocolInvalid");
            }, context.Diagnostics.Stream);
        context.Diagnostics.ConnectionOpened();
        registration = context.OnChanging(Configure);
        context.Execute(() => Configure(new(false, null), context.Current));
    }
    public void Feed(uint sequence, ReadOnlyMemory<byte> bytes, double monotonicMs, double? receivedMs, DateTimeOffset observedAt)
    {
        context.Execute(() => { now = monotonicMs; utc = observedAt; Stream.Feed(sequence, bytes.Span, now, receivedMs); });
    }
    public void Tick(double monotonicMs, DateTimeOffset observedAt)
    {
        context.Execute(() =>
        {
            now = monotonicMs; utc = observedAt; Stream.CheckGap(now); combat?.Tick(now, utc);
            if (inventory is not null && now - inventoryTickAt >= 200) { inventoryTickAt = now; var (m, u) = (now, utc); PostInventory(o => o.Tick(m, u), 0); }
        });
    }
    /// <summary>等待已提交的库存解析全部完成；供离线验证和停止交接使用。</summary>
    public Task InventoryIdleAsync() => lane?.IdleAsync() ?? Task.CompletedTask;
    /// <summary>库存解析通道的诊断计数：已执行、因容量拒绝的工作项，以及单项最长耗时。</summary>
    public InventoryLaneStatistics InventoryStatistics => lane?.Statistics ?? new(0, 0, 0);
    private InventoryLane? lane;
    private bool inventoryOverflow;
    private double inventoryTickAt = double.NegativeInfinity;
    private readonly object generationGate = new();
    private ulong NextGeneration() { lock (generationGate) return generations(); }
    /// <summary>
    /// 库存解析在独立串行通道执行，捕获线程只复制引用并入队：完整登录体解码耗时数百毫秒至秒级，
    /// 不能占用抓包读取与战斗分发。通道满时丢弃后续工作并在恢复后先发送 inventory.streamGap 失效。
    /// </summary>
    private void PostInventory(Action<InventoryProtocolObserver> work, int bytes)
    {
        if (inventory is not { } target) return;
        lane ??= new();
        if (inventoryOverflow)
        {
            var (m, u) = (now, utc);
            if (!lane.TryPost(() => target.Reset("inventory.streamGap", m, u), 0)) return;
            inventoryOverflow = false;
        }
        var (at, atUtc) = (now, utc);
        var diagnostics = context.Diagnostics;
        Action item = () =>
        {
            try { work(target); }
            catch (Exception error) when (error is not OutOfMemoryException && atUtc != default)
            { diagnostics.Rejected("inventory"); target.Reset("inventory.protocolInvalid", at, atUtc); }
        };
        if (!lane.TryPost(item, bytes)) { inventoryOverflow = true; diagnostics.LaneRejected(); }
    }
    private void PostInventoryReset(string reason)
    {
        if (utc == default) return;
        var (m, u) = (now, utc); PostInventory(o => o.Reset(reason, m, u), 0);
    }
    public void Restart(string reason, double monotonicMs, DateTimeOffset observedAt, GameStreamResetCause cause = GameStreamResetCause.Unknown)
    { context.Execute(() => { now = monotonicMs; utc = observedAt; Stream.Restart(reason, cause); }); }
    private void Configure(ObservationCaptureInterests old, ObservationCaptureInterests next)
    {
        if (next.Combat is { } check && (layout == CombatProtocolLayout.MusicOnly || !combatEnabled || !Tw20261110CombatProtocol.IsCaptureProfile(check.Profile)))
            throw new WorkerObservationException("combat.buildUnsupported");
        if (old != next && combat is not null && utc != default) combat.Finish(now, utc);
        // 身份不随兴趣变化清空；音乐兴趣关闭时只清空合奏与演奏状态。
        Music.MusicEnabled = next.MusicEnabled && musicEnabled;
        if (old.Combat?.Id != next.Combat?.Id || old.Combat?.Profile != next.Combat?.Profile)
        {
            combat = null; interestId = null; CombatInitializerAt = double.NegativeInfinity; delivered = 0;
            if (next.Combat is { } requested && context.Profile(requested) is { } profile)
            {
                interestId = requested.Id;
                combat = new(gameSession, profile.Source, frame =>
                {
                    if (selected(this)) context.Publish(LocalName(new CombatCaptureReport(1, interestId!, combat!.LocalEntity,
                        frame with { Sequence = new(++delivered) }),
                        LocalIdentity(Music.Current.Identity, identities.Last, combat.LocalEntity, combat.LocalCharacterId)));
                }, NextGeneration);
                SeedCombatIdentity();
            }
        }
        // 治疗随伤害开关，只在 v2、v3 兴趣解析；v1 兴趣（旧宿主）的帧不含治疗条目。本机战斗状态只在 v3 兴趣解析。
        if (next.Combat is { } value) combat?.Configure(new(value.DamageEnabled, value.SelfEffectsEnabled, value.MonsterEffectsEnabled,
            value.DamageEnabled && value.Profile is Tw20261110CombatProtocol.Profile or Tw20261110CombatProtocol.ProfileV2,
            value.Profile == Tw20261110CombatProtocol.Profile));
        if (old.Inventory != next.Inventory)
        {
            PostInventoryReset("inventory.interestChanged");
            inventory = null; InventoryInitializerAt = double.NegativeInfinity; inventoryOverflow = false;
            // 发布回调在解析通道线程执行，进入捕获串行边界后再判定主连接并发布。
            if (next.Inventory is { } wanted && context.InventoryLayout is { } inventoryLayout && context.InventoryProfile is { } inventoryProfile)
                inventory = new(gameSession, inventoryProfile.Source, inventoryLayout,
                    frame =>
                    {
                        var chosen = false; context.Execute(() => chosen = inventorySelected(this));
                        if (chosen) context.Publish(new InventoryCaptureReport(1, wanted.Id, frame));
                    },
                    NextGeneration,
                    // 身份关联超时后的“按角色归属”只用同一游戏进程已确认、角色 ID 相同的本机昵称。
                    character => identities.Last is { } known && known.CharacterId == character ? known.Nickname : null,
                    // 账户主角色对应随本机身份记录跨连接共用，换频道、进图后的登录数据可立即确定账户。
                    identities.Accounts);
        }
    }
    private void Dispatch(int code, byte[] body)
    {
        // 战斗先于库存处理同一帧（登录个人数据 20590 两者共用）：完整库存解码较重，不推迟战斗身份与帧发布。
        if (combat is not null && ObserveCombat(code))
        {
            var before = combat.Generation; var rejections = combat.ProtocolRejections;
            // 观察器内部拒绝的帧按计数差记入战斗分支；逃出的解析异常照常交给重组器的拒绝分支，筛选只计数。
            try { combat.Accept(code, body, Stream.FrameStreamOffset, Stream.FrameWireLength, CombatReceipt(), utc, Stream.FrameEncoding); }
            catch (Exception error) when ((error is InvalidDataException or DecoderFallbackException) && RejectedCombat()) { throw; }
            if (combat.ProtocolRejections != rejections) context.Diagnostics.Rejected("combat");
            if (code == Tw20261110CombatProtocol.PersonalData && combat.Generation != before) CombatInitializerAt = Stopwatch.GetTimestamp();
        }
        if (ObserveMusic(code))
        {
            try
            {
                // 本连接没有个人数据时，出现帧可按同一游戏进程已确认的角色 ID 补足（见 TwGameObserver.RememberedCharacterId）。
                var personal = Music.PersonalId;
                Music.RememberedCharacterId = identities.Last?.CharacterId;
                Music.Accept(code, body, Stream.FrameReceivedLocalMs);
                // 主连接按初始化时点选择：个人数据帧，或本连接首次由出现帧补足角色 ID 的时刻。
                if (code == Music.Protocols.Personal || personal is null && Music.PersonalId is not null) MusicInitializerAt = Stopwatch.GetTimestamp();
                if (code == Music.Protocols.Appearing) SeedCombatIdentity();
                if (Music.Current.Identity is { } confirmed) identities.Remember(confirmed);
            }
            catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
            { context.Diagnostics.Rejected("music"); Music.RejectFrame(code); }
        }
        if (inventory?.Observes(code) == true)
        {
            if ((uint)code == context.InventoryLayout?.Protocol.Login) InventoryInitializerAt = Stopwatch.GetTimestamp();
            // 展开后的正文为本帧独有数组，战斗与音乐只读不改，可直接交给解析通道。
            var (m, u) = (InventoryReceipt(), utc);
            PostInventory(o => o.Accept(code, body, m, u), body.Length);
        }
    }
    /// <summary>库存帧沿用战斗的抓包时点预算：积压超过预算的包改用处理时点，避免 Host 把整段库存帧判为过期。</summary>
    private double InventoryReceipt() =>
        Stream.FrameReceivedLocalMs is { } value && now - value <= MaximumCombatReceiptLagMs ? value : now;
    /// <summary>
    /// Host 拒绝超过 2 s 的战斗帧，队列与管道另需约 1 s；捕获停顿后读到的积压包若 Npcap 时点超出
    /// 此预算，按无效时点映射处理并改用 Worker 处理时点。
    /// </summary>
    public const double MaximumCombatReceiptLagMs = 1000;
    private double? lagSince;
    private double lagLast;
    /// <summary>
    /// 连续积压段只记一条 combat.captureLag：段内首个超预算包触发；在预算内的包到达、流重置，
    /// 或超过一个预算周期没有再改时点的包时，积压段结束。
    /// </summary>
    private double CombatReceipt()
    {
        var received = Stream.FrameReceivedLocalMs;
        if (received is { } value && now - value <= MaximumCombatReceiptLagMs) { lagSince = null; return value; }
        if (received is null) return now;
        if (lagSince is null || now - lagLast > MaximumCombatReceiptLagMs) { lagSince = now; combat?.CaptureLag(received.Value, now, utc); }
        lagLast = now; return now;
    }
    /// <summary>异常筛选中记录战斗解析拒绝；返回 false，异常照常传给重组器。</summary>
    private bool RejectedCombat() { context.Diagnostics.Rejected("combat"); return false; }
    private bool ObserveMusic(int code) => identityEnabled && Music.Observes(code);
    /// <summary>
    /// 战斗解析尚无本机实体而共享身份已确认时，以该身份补足战斗起点：兴趣晚于登录或切图建立时不必等待下一次切图。
    /// 主连接判定沿用身份初始化时点。
    /// </summary>
    private void SeedCombatIdentity()
    {
        if (combat is null || combat.LocalEntity is not null || utc == default ||
            Music.Current.Identity is not { } identity) return;
        // 主连接判定在发布回调内读取初始化时点，须先于补足时的首帧发布设定。
        var previous = CombatInitializerAt;
        CombatInitializerAt = double.IsNegativeInfinity(MusicInitializerAt) ? Stopwatch.GetTimestamp() : MusicInitializerAt;
        if (!combat.SeedLocal(identity.CharacterId, identity.ActorId, now, utc)) CombatInitializerAt = previous;
    }
    /// <summary>
    /// 战斗协议不带玩家名，本机实体的显示名默认是实体 ID。共享身份已识别本机（ActorId 与本机实体一致）时，
    /// 以本机昵称作本机实体的显示名，帧内实体与 LocalEntity 同步替换；其他玩家保持原样。
    /// </summary>
    internal static CombatCaptureReport LocalName(CombatCaptureReport report, GameIdentity? identity)
    {
        if (report.LocalEntity is not { } local || identity is not { Nickname.Length: > 0 and <= 64 } known ||
            known.ActorId != local.Key.RawInstanceId.Value || local.DisplayName != local.Key.RawInstanceId.ToString() ||
            known.Nickname.Any(char.IsControl)) return report;
        var named = local with { DisplayName = known.Nickname };
        return report with
        {
            LocalEntity = named,
            Frame = report.Frame with { Entities = report.Frame.Entities.Select(entity => entity == local ? named : entity).ToArray() }
        };
    }
    /// <summary>
    /// 本机显示名所用的身份。本连接共享身份的 ActorId 与本机实体一致时直接使用。否则沿用同一捕获内最近确认的身份，
    /// 条件是本连接战斗解析由个人数据确认的本机角色 ID 与之相同：进地下城、换频道时游戏更换连接，新连接的身份解析从空开始，
    /// 在个人数据帧被错过且本机出现帧尚未到达时确认不了（出现帧到达后按记住的角色 ID 补足，见 TwGameObserver.RememberedCharacterId）。
    /// 角色 ID 属于角色而非连接，同一游戏进程内相同即同一角色；实体 ID 每张地图不同，沿用时换成当前本机实体。
    /// 角色 ID 不同或尚未确认时不沿用，显示名保持实体 ID，避免把其他角色认成本机。
    /// </summary>
    internal static GameIdentity? LocalIdentity(GameIdentity? current, GameIdentity? remembered,
        CombatEntityObservation? local, ulong? localCharacterId)
    {
        if (local is null) return current;
        var entity = local.Key.RawInstanceId.Value;
        if (current is not null && current.ActorId == entity) return current;
        return remembered is not null && localCharacterId is { } character && remembered.CharacterId == character
            ? remembered with { ActorId = entity } : current;
    }
    /// <summary>
    /// 状态报告的本机身份按 <see cref="LocalIdentity"/> 同一规则沿用：所选连接的共享身份未确认时，取战斗主连接
    /// （初始化时点最新且已确认本机实体）判定。只替换对外报告的身份与说明；合奏、演奏字段仍来自所选连接，
    /// 本连接身份未确认时它们为空，音乐识别对本连接身份的依赖不变。
    /// </summary>
    internal static GameObservation ReportIdentity(GameObservation observation, IEnumerable<SharedGameStream> streams, LocalIdentityMemory memory)
    {
        if (observation.Identity is not null) return observation;
        var primary = streams.Where(stream => stream.LocalEntity is not null && !double.IsNegativeInfinity(stream.CombatInitializerAt))
            .MaxBy(stream => stream.CombatInitializerAt);
        return primary is not null && LocalIdentity(null, memory.Last, primary.LocalEntity, primary.LocalCharacterId) is { } carried
            ? observation with { Identity = carried, Detail = "已识别本机角色。" } : observation;
    }
    /// <summary>战斗解析按线上协议号接收 v3 协议集合；v1、v2 兴趣的差异由 <see cref="CombatProtocolInterest"/> 开关决定。</summary>
    private bool ObserveCombat(int code) => combat is not null && Tw20261110CombatProtocol.ProtocolsV3.Contains(code);
    /// <summary>tw-music-v1 捕获的已知协议号：固定 MabiSync 构建的全部 GCProtocol（不含 0）。</summary>
    private static readonly IReadOnlySet<int> MusicOnlyProtocols = TwGameProtocol.KnownProtocols.Where(code => code != 0).ToHashSet();
    /// <summary>
    /// 新的 TCP 连接（SYN、结束、复位、移除或原因未知）清空本连接身份；同一连接内的重同步（丢包、缺口超时、帧失步、
    /// 截断包等）保留角色 ID 与本机身份，只清空合奏与演奏状态。战斗与库存解析两种情形都重置。
    /// </summary>
    internal static bool NewConnection(GameStreamResetCause cause) => cause is GameStreamResetCause.Unknown or
        GameStreamResetCause.ConnectionSyn or GameStreamResetCause.ConnectionClosed or GameStreamResetCause.ConnectionReset or
        GameStreamResetCause.ConnectionRemoved || !Enum.IsDefined(cause);
    private void ResetParsers(string reason)
    {
        var cause = Stream.LastResetCause;
        if (NewConnection(cause)) Music.Reset(reason); else Music.Resync(reason);
        var code = Enum.IsDefined(cause) && cause != GameStreamResetCause.Unknown
            ? "combat.streamGap." + char.ToLowerInvariant(cause.ToString()[0]) + cause.ToString()[1..]
            : "combat.streamGap";
        combat?.Reset(code, now, utc); lagSince = null;
        PostInventoryReset("inventory.streamGap");
        // 同一连接内仍是同一地图与角色（当前构建每次进图都换连接），以保留的身份重新补足战斗起点。
        if (!NewConnection(cause)) SeedCombatIdentity();
    }
    private int disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        context.Diagnostics.ConnectionClosed();
        // 停止项排在已提交解析之后；通道线程处理完即结束，此处不等待以免在捕获串行边界内互等。
        context.Execute(() => { if (utc != default) { combat?.Finish(now, utc); PostInventoryReset("inventory.captureStopped"); }
            combat = null; inventory = null; interestId = null; registration.Dispose(); lane?.Complete(); });
    }
}

/// <summary>
/// 一次捕获（绑定一个游戏进程）内最近确认的本机身份，由各连接共用；新的确认身份直接替换旧值。
/// 游戏进程变化或解绑会结束捕获，记录随之丢弃。Worker 意外重启后由宿主随捕获请求下发同一游戏会话（同一进程）
/// 最近确认的身份作为初值。
/// </summary>
public sealed class LocalIdentityMemory(GameIdentity? initial = null)
{
    private readonly object gate = new();
    private GameIdentity? last = initial;
    public GameIdentity? Last { get { lock (gate) return last; } }
    public void Remember(GameIdentity identity) { lock (gate) last = identity; }
    /// <summary>库存的账户主角色对应（见 <see cref="InventoryAccountMemory"/>），范围同本机身份记录。</summary>
    public InventoryAccountMemory Accounts { get; } = new();
}
