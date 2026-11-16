using System.Collections.Concurrent;
using MabiTool.Contracts;
using MabiTool.Modules.Rooms.Client;
using MabiTool.Modules.Rooms.Domain;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;

namespace MabiTool.Host.Rooms;

public sealed partial class RoomSessionManager : BackgroundService, IAsyncDisposable
{
    private sealed class Entry(RoomClient client, RoomClock clock, ActionScheduler scheduler, IInputExecutor input, SessionIdentity? identity)
    {
        public string Owner { get; init; } = "";
        public IMonotonicClock Monotonic { get; init; } = new StopwatchClock();
        // 房主经 configure-input 免就绪上报能力后保留本机准备，直到该轮结束或准备失效。
        public bool HostConfigured { get; set; }
        public string? HostConfiguredRound { get; set; }
        public RoomClient Client { get; } = client;
        public RoomClock Clock { get; } = clock;
        public ActionScheduler Scheduler { get; } = scheduler;
        public IInputExecutor Input { get; } = input;
        public RoomLocalInputController? LocalInput => Input as RoomLocalInputController;
        public SessionIdentity? Identity { get; set; } = identity;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public RoomInvitation? Invitation { get; set; }
        public string CloseStatus { get; set; } = "notApplicable";
        // 房间服务让本机退出的原因（kicked、roomClosed）；下一次建房或加入时清除。
        public string? ExitReason { get; set; }
        public object ReadinessGate { get; } = new();
        public IDisposable? ReadinessActivity { get; set; }
        public bool ReadinessPending { get; set; }
        public bool WithdrawalPending { get; set; }
        public UInt64String LastInputRevision { get; set; }
        public Dictionary<string, ReleaseReceipt> ReleaseReceipts { get; } = new(StringComparer.Ordinal);
        public object RoundGate { get; } = new();
        public string? RoundFingerprint { get; set; }
        public ulong RoundRevision { get; set; }
        public long ObservationSequence;
        public bool HasReportedObservation;
        public object ScoreGate { get; } = new();
        public RoomLocalScoreMapping? LocalScore { get; set; }
        public string? LocalScoreDraftId { get; set; }
        public UInt64String? LocalScoreDraftRevision { get; set; }
        public string? ScoreRoomIdentity { get; set; }
        public string? ScoreFingerprint { get; set; }
        public ulong ScoreRevision { get; set; }
        public bool MappingPending { get; set; }
        public bool ScoreWithdrawalPending { get; set; }
    }

    private readonly Func<string, IInputExecutor>? inputFactory;
    private readonly RoomInputPorts? inputPorts;
    private readonly Func<string, RoomPreferences> preferences;
    private readonly Func<string, bool> authorized;
    private readonly Func<InputBindingPreference, bool>? shortcutConflict;
    private readonly MusicScoreReader? scoreReader;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    // “操作游戏”开关按配对客户端保存在内存中；与原工具相同，每次启动默认开启。
    private readonly ConcurrentDictionary<string, bool> gameOperation = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly ActivityBarrier activities;
    private readonly string backendEpoch;
    public event Action<string>? StateChanged;

    public RoomSessionManager(TimeProvider? timeProvider = null, Func<string, IInputExecutor>? inputFactory = null,
        ActivityBarrier? activityBarrier = null, RoomInputPorts? inputPorts = null,
        Func<string, RoomPreferences>? preferences = null, Func<string, bool>? authorized = null,
        Func<InputBindingPreference, bool>? shortcutConflict = null, string? backendEpoch = null, MusicScoreReader? scoreReader = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.inputFactory = inputFactory;
        this.inputPorts = inputPorts;
        this.preferences = preferences ?? (_ => RoomPreferences.Default);
        this.authorized = authorized ?? (_ => true);
        this.shortcutConflict = shortcutConflict;
        this.scoreReader = scoreReader;
        activities = activityBarrier ?? new();
        this.backendEpoch = backendEpoch ?? Guid.NewGuid().ToString("N");
    }

    public async Task<RoomLifecycleSnapshot> CreateAsync(string owner, RoomCreateRequest request, CancellationToken token)
    {
        using var activity = activities.EnterActivity(owner, "room.connect");
        ValidateEndpoint(request.Endpoint);
        ValidateNickname(request.Nickname);
        var entry = await GetOrCreateAsync(owner);
        await entry.Gate.WaitAsync(token);
        try
        {
            EnsureNoRoom(entry);
            await ConnectEndpointAsync(entry, request.Endpoint, token);
            var identity = await entry.Client.CreateRoomAsync(request.Nickname, token);
            await entry.Client.WaitForRoomSnapshotAsync(token);
            entry.Identity = identity;
            entry.Invitation = new(identity.Code, identity.Endpoint, timeProvider.GetUtcNow());
            entry.CloseStatus = "notApplicable";
            entry.ExitReason = null;
            Notify(owner);
            return Snapshot(entry);
        }
        finally { entry.Gate.Release(); }
    }

    public async Task<RoomLifecycleSnapshot> JoinAsync(string owner, RoomJoinRequest request, CancellationToken token)
    {
        using var activity = activities.EnterActivity(owner, "room.connect");
        ValidateEndpoint(request.Endpoint);
        ValidateNickname(request.Nickname);
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 32 || request.Code.Any(char.IsControl))
            throw new OperationException("request.invalid", "房间代码无效");
        var entry = await GetOrCreateAsync(owner);
        await entry.Gate.WaitAsync(token);
        try
        {
            EnsureNoRoom(entry);
            await ConnectEndpointAsync(entry, request.Endpoint, token);
            var identity = await entry.Client.JoinRoomAsync(request.Nickname, request.Code, token);
            await entry.Client.WaitForRoomSnapshotAsync(token);
            entry.Identity = identity;
            entry.Invitation = new(entry.Identity.Code, entry.Identity.Endpoint, timeProvider.GetUtcNow());
            entry.CloseStatus = "notApplicable";
            entry.ExitReason = null;
            Notify(owner);
            return Snapshot(entry);
        }
        finally { entry.Gate.Release(); }
    }

    public async Task<RoomLifecycleSnapshot> ReconnectAsync(string owner, CancellationToken token)
    {
        using var activity = activities.EnterActivity(owner, "room.connect");
        var entry = Find(owner);
        await entry.Gate.WaitAsync(token);
        try
        {
            if (entry.Identity is null) throw new OperationException("resource.notFound", "没有可恢复的房间身份");
            await entry.Client.ConnectAsync(entry.Identity.Endpoint, token);
            ReconcileReadiness(entry, owner, retainReady: true);
            Notify(owner);
            return Snapshot(entry);
        }
        finally { entry.Gate.Release(); }
    }

    public async Task<RoomLifecycleSnapshot> LeaveAsync(string owner, RoomLeaveRequest request, CancellationToken token)
    {
        var entry = Find(owner);
        await entry.Gate.WaitAsync(token);
        try
        {
            if (entry.Identity is null) throw new OperationException("resource.notFound", "当前没有加入房间");
            if (request.CloseRoom && !entry.Client.IsRoomOwner)
                throw new OperationException("permission.denied", "只有房主可以关闭房间");
            try
            {
                await entry.Client.LeaveRoomAsync(request.CloseRoom, token);
                ReconcileReadiness(entry, owner);
                entry.CloseStatus = request.CloseRoom
                    ? entry.Client.UnconfirmedRoomExits == 0 ? "confirmed" : "unconfirmed"
                    : "notApplicable";
                entry.Identity = null;
                entry.Invitation = null;
            }
            catch (Exception error) when (request.CloseRoom && error is TimeoutException or OperationCanceledException)
            {
                if (!entry.Client.IsConnected)
                {
                    entry.Client.ForgetDisconnectedRoom();
                    entry.Identity = null;
                    entry.Invitation = null;
                }
                entry.CloseStatus = "unconfirmed";
                throw new OperationException("room.closeUnconfirmed", "本机已结束房间关闭等待；服务端关闭尚未确认，请稍后核对房间状态");
            }
            catch (Exception error) when (request.CloseRoom && error is IOException)
            {
                if (!entry.Client.IsConnected)
                {
                    entry.Client.ForgetDisconnectedRoom();
                    entry.Identity = null;
                    entry.Invitation = null;
                    entry.CloseStatus = "unconfirmed";
                    throw new OperationException("room.closeUnconfirmed", "本机已退出房间连接，服务端关闭尚未确认，请稍后核对房间状态");
                }
                throw;
            }
            Notify(owner);
            return Snapshot(entry);
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>房间会话与连接计数；不刷新本地输入或触发重连，不含房间码与令牌。</summary>
    internal (int Sessions, int Connected) DiagnosticCounts()
    {
        var values = entries.Values.ToArray();
        return (values.Length, values.Count(entry => entry.Client.IsConnected));
    }

    public RoomLifecycleSnapshot Get(string owner)
    {
        if (!entries.TryGetValue(owner, out var entry))
            return new("disconnected", null, null, null, false, "notApplicable", 0, null, null, OperateGame(owner));
        entry.LocalInput?.KeepAlive();
        RefreshLocal(entry, owner);
        return Snapshot(entry);
    }

    public RoomClockSample? GetClockSample(string owner) => entries.TryGetValue(owner, out var entry) &&
        entry.Client.IsConnected && entry.Clock.Estimate is { } estimate
        ? new(estimate.RoundTripMs, estimate.UncertaintyMs, estimate.AgeMs, entry.Scheduler.TimingOffsetMs) : null;

    public RoomInvitation GetInvitation(string owner)
    {
        var entry = Find(owner);
        if (entry.Invitation is null || entry.Identity is null)
            throw new OperationException("resource.notFound", "当前没有可分享的房间邀请");
        if (!entry.Client.IsRoomOwner) throw new OperationException("permission.denied", "只有房主可以读取房间邀请");
        return entry.Invitation;
    }

    public RoomInvitation CreateInvitation(string owner)
    {
        var entry = Find(owner);
        if (entry.Identity is null) throw new OperationException("resource.notFound", "当前没有已创建的房间");
        if (!entry.Client.IsRoomOwner) throw new OperationException("permission.denied", "只有房主可以创建房间邀请");
        return entry.Invitation = new(entry.Identity.Code, entry.Identity.Endpoint, timeProvider.GetUtcNow());
    }

    public async Task<RoomLifecycleSnapshot> EditRosterAsync(string owner, RoomRosterEditRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.MemberId)) throw new OperationException("request.invalid", "成员 ID 不能为空");
        if (request.Group is < 1 or > 24 || request.Order is < 1 or > 24 ||
            request.MusicRole is not (null or "initiator" or "joiner") ||
            request.Instrument is { Length: > 40 } || request.Instrument?.Any(char.IsControl) == true ||
            request.Selected is not null && (request.Group is not null || request.Order is not null || request.MusicRole is not null || request.Instrument is not null) ||
            request.Group is null && request.Order is null && request.MusicRole is null && request.Instrument is null && request.Selected is null)
            throw new OperationException("request.invalid", "成员配置无效或没有可应用的修改");
        return await MutateAsync(owner, token, async entry =>
        {
            await entry.Client.UpdateRosterAsync(request.Selected is null ? "edit-ensemble-member" : "set-member-selection", request.MemberId, request.Group, request.Order,
                request.MusicRole, request.Instrument, request.Selected).ConfigureAwait(false);
        });
    }

    public Task<RoomLifecycleSnapshot> SetModeAsync(string owner, RoomModeRequest request, CancellationToken token)
    {
        if (request.Mode is not ("emote" or "ensemble")) throw new OperationException("request.invalid", "房间模式无效");
        return MutateAsync(owner, token, entry => entry.Client.SetRoomModeAsync(request.Mode));
    }

    public Task<RoomLifecycleSnapshot> SetReadinessAsync(string owner, RoomReadinessRequest request, CancellationToken token)
    {
        if (request.TimingOffsetMs is < -2000 or > 2000 || request.MusicRole is not ("initiator" or "joiner") ||
            request.ReadyMode is not (null or "emote" or "independent" or "ensemble") ||
            !double.IsFinite(request.ClockRoundTripMs) || request.ClockRoundTripMs is < 0 or > 30000 ||
            request.WindowActivationMs is < 0 or > 5000)
            throw new OperationException("request.invalid", "就绪能力参数无效");
        return MutateAsync(owner, token, async entry =>
        {
            lock (entry.ReadinessGate)
            {
                if (request.Ready) entry.ReadinessActivity ??= activities.EnterActivity(owner, "room.ready");
                entry.ReadinessPending = true;
            }
            var confirmed = false;
            LocalRoomCapabilities? capabilities = null;
            try
            {
                if (request.Ready)
                {
                    await entry.Client.RefreshClockAsync(token);
                    token.ThrowIfCancellationRequested();
                    var mode = request.ReadyMode ?? (entry.Client.Room is { } room && room.TryGetProperty("mode", out var roomMode) ? roomMode.GetString() : null) ?? "emote";
                    if (mode != "emote") EnsureScoreReady(entry, owner);
                    capabilities = entry.LocalInput?.Arm(request, mode)
                        ?? throw new OperationException("input.actionMappingUnavailable", "宿主尚未装配本地轮次输入");
                }
                await entry.Client.SetReadyAsync(request.Ready, request.Simulation,
                    capabilities?.CanStart ?? false, capabilities?.CanStop ?? false, request.TimingOffsetMs, request.MusicRole, request.ReadyMode,
                    capabilities?.RoundTripMs ?? 0, capabilities?.ActivationMs ?? 0);
                confirmed = true;
            }
            catch (RoomInputException error) { throw InputFailure(error); }
            finally
            {
                lock (entry.ReadinessGate)
                {
                    entry.ReadinessPending = false;
                    if (!confirmed)
                    {
                        entry.Client.CancelLocalPreparations();
                        entry.Scheduler.CancelAll();
                        ReleaseReadiness(entry);
                    }
                    if (confirmed && !request.Ready) ReleaseReadiness(entry);
                    ReconcileReadiness(entry, owner);
                }
            }
        });
    }

    public Task<RoomLifecycleSnapshot> BindCharacterAsync(string owner, RoomCharacterBindingRequest request, CancellationToken token)
    {
        int realm = 0;
        if (string.IsNullOrWhiteSpace(request.MemberId) ||
            (request.CharacterId is null) != (request.RealmId is null) ||
            request.CharacterId is { Length: > 32 } ||
            request.RealmId is not null && !int.TryParse(request.RealmId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out realm))
            throw new OperationException("request.invalid", "角色关联参数无效；realmId 须为十进制数字");
        return MutateAsync(owner, token, entry => Protocol(() => entry.Client.BindGameCharacterAsync(request.MemberId, request.CharacterId,
            request.CharacterId is null ? null : realm)));
    }

    public Task<RoomLifecycleSnapshot> SelectLeaderAsync(string owner, RoomLeaderRequest request, CancellationToken token)
    {
        ValidateMemberId(request.MemberId);
        return MutateAsync(owner, token, entry => { RequireOwner(entry, "指定总指挥"); return Protocol(() => entry.Client.SelectLeaderAsync(request.MemberId)); });
    }

    public Task<RoomLifecycleSnapshot> KickMemberAsync(string owner, RoomMemberKickRequest request, CancellationToken token)
    {
        ValidateMemberId(request.MemberId);
        return MutateAsync(owner, token, entry => { RequireOwner(entry, "移出成员"); return Protocol(() => entry.Client.KickMemberAsync(request.MemberId)); });
    }

    public bool OperateGame(string owner) => gameOperation.GetValueOrDefault(owner, true);
    private bool Simulated(string owner) => (inputPorts?.Simulation ?? false) || !OperateGame(owner);

    /// <summary>切换“操作游戏”。仅就绪未开轮时撤回就绪，本机动作已排定或演奏中拒绝。</summary>
    public async Task<RoomLifecycleSnapshot> SetGameOperationAsync(string owner, RoomGameOperationRequest request, CancellationToken token)
    {
        if (!entries.TryGetValue(owner, out var entry)) { gameOperation[owner] = request.Enabled; Notify(owner); return Get(owner); }
        await entry.Gate.WaitAsync(token);
        try
        {
            if (OperateGame(owner) != request.Enabled)
            {
                if (entry.LocalInput?.Snapshot.State is "armed" or "performing")
                    throw new OperationException("room.activityActive", "本轮本机动作已排定，结束后再切换操作游戏");
                gameOperation[owner] = request.Enabled;
                entry.Client.CancelLocalPreparations();
                entry.Scheduler.CancelPendingStarts();
                lock (entry.ReadinessGate) { entry.HostConfigured = false; ReleaseReadiness(entry); }
                QueueWithdrawal(entry, owner);
            }
            Notify(owner);
            return Snapshot(entry);
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>本机停止：立即取消本机准备，音乐轮次排入仅本机的停止动作，再向房间发送 local-stop 进入中断。</summary>
    public async Task<RoomLocalStopResult> StopLocallyAsync(string owner, CancellationToken token)
    {
        var entry = Find(owner);
        // 不等待入口互斥：停止须立即执行，经调度器与输入代理并受租约约束。
        var (local, stopId) = entry.Client.StopLocally();
        lock (entry.ReadinessGate) entry.HostConfigured = false;
        var confirmed = false;
        if (entry.Client.IsConnected && entry.Client.Session is not null)
        {
            try { await entry.Client.NotifyLocalStopAsync().WaitAsync(token); confirmed = true; }
            catch (Exception error) when (error is IOException or TimeoutException or RoomRequestException or OperationCanceledException or ObjectDisposedException) { }
        }
        Notify(owner);
        return new(local, stopId, confirmed, Snapshot(entry));
    }

    private static void RequireOwner(Entry entry, string action)
    {
        if (!entry.Client.IsRoomOwner) throw new OperationException("permission.denied", "只有房主可以" + action);
    }

    private static void ValidateMemberId(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId) || memberId.Length > 128 || memberId.Any(char.IsControl))
            throw new OperationException("request.invalid", "成员 ID 无效");
    }

    private static async Task Protocol(Func<Task> command)
    {
        try { await command(); }
        catch (RoomRequestException error) { throw ProtocolFailure(error.Code); }
    }

    private static OperationException ProtocolFailure(string code) => new(code switch
    {
        "FORBIDDEN" => "permission.denied", "MEMBER_NOT_FOUND" => "resource.notFound",
        "SESSION_ACTIVE" => "room.activityActive", _ => "room.commandRejected"
    }, "房间服务拒绝了本次命令", new Dictionary<string, System.Text.Json.JsonElement>
        { ["protocolCode"] = System.Text.Json.JsonSerializer.SerializeToElement(code) });

    private async Task<RoomLifecycleSnapshot> MutateAsync(string owner, CancellationToken token, Func<Entry, Task> mutation)
    {
        using var activity = activities.EnterActivity(owner, "room.command");
        var entry = Find(owner);
        await entry.Gate.WaitAsync(token);
        try
        {
            if (entry.Identity is null || !entry.Client.IsConnected) throw new OperationException("resource.conflict", "房间当前未连接");
            token.ThrowIfCancellationRequested();
            // 命令发送后等待有界协议确认，保持入口互斥及活动登记直至实际请求结束。
            await mutation(entry);
            token.ThrowIfCancellationRequested();
            Notify(owner);
            return Snapshot(entry);
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<Entry> GetOrCreateAsync(string owner)
    {
        if (entries.TryGetValue(owner, out var existing))
            return existing;
        var clock = new StopwatchClock();
        var roomClock = new RoomClock(clock);
        var input = inputFactory?.Invoke(owner) ?? (inputPorts is null ? new UnavailableInputExecutor() :
            (IInputExecutor)new RoomLocalInputController(owner, inputPorts, clock, roomClock,
                () => preferences(owner), () => authorized(owner), timeProvider, shortcutConflict, () => OperateGame(owner)));
        var scheduler = new ActionScheduler(clock, roomClock, input);
        var candidate = new Entry(new RoomClient(clock, roomClock, scheduler), roomClock, scheduler, input, null) { Owner = owner, Monotonic = clock };
        // 被房主移出或房主关闭房间时本机房间身份随之失效，回到未加入状态并保留原因；随后的 Changed 发布新状态。
        candidate.Client.RoomEnded += reason => { candidate.Identity = null; candidate.Invitation = null; candidate.ExitReason = reason; };
        candidate.Client.Changed += () => { ReconcileScore(candidate, owner); ReconcileReadiness(candidate, owner); Notify(owner); };
        candidate.Client.ValidateScoreAsync = async (action, token) =>
        {
            await candidate.Gate.WaitAsync(token);
            try
            {
                var draft = candidate.Client.ScoreDraft;
                if (action.ScoreDraftId != draft?.Id || action.ScoreDraftRevision != draft?.Revision.ToString())
                    throw new OperationException("room.scoreDraftStale", "动作引用的乐谱草稿已变化");
                EnsureScoreReady(candidate, owner);
            }
            finally { candidate.Gate.Release(); }
        };
        candidate.Scheduler.Receipt += _ => Notify(owner);
        var entry = entries.GetOrAdd(owner, candidate);
        if (!ReferenceEquals(entry, candidate))
        {
            await candidate.Client.DisposeAsync();
            candidate.Scheduler.Dispose();
        }
        return entry;
    }

    private static Task ConnectEndpointAsync(Entry entry, string endpoint, CancellationToken token) =>
        string.Equals(entry.Client.Endpoint, endpoint, StringComparison.Ordinal) && entry.Client.IsConnected
            ? Task.CompletedTask
            : entry.Client.ConnectAsync(endpoint, token);

    private Entry Find(string owner) => entries.TryGetValue(owner, out var entry)
        ? entry : throw new OperationException("resource.notFound", "当前没有房间会话");

    private static void EnsureNoRoom(Entry entry)
    {
        if (entry.Identity is not null) throw new OperationException("resource.conflict", "请先离开当前房间");
    }

    private void ReconcileReadiness(Entry entry, string owner, bool retainReady = false)
    {
        lock (entry.ReadinessGate)
        {
            if (!entry.Client.IsConnected || entry.Client.Session is null)
            {
                entry.Client.CancelLocalPreparations();
                entry.Scheduler.CancelAll();
                entry.HostConfigured = false;
                ReleaseReadiness(entry);
                return;
            }
            if (entry.ReadinessPending) return;
            if (entry.Client.Room is not { } room || !room.TryGetProperty("members", out var members)) return;
            if (entry.HostConfigured)
            {
                var round = room.TryGetProperty("performance", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Object ? p : (System.Text.Json.JsonElement?)null;
                var ended = entry.HostConfiguredRound is { } configured && (round is null ||
                    round.Value.GetProperty("id").GetString() != configured || round.Value.GetProperty("phase").GetString() is "cancelled" or "stopped");
                if (ended || entry.LocalInput?.Snapshot.State is not ("prepared" or "armed" or "performing"))
                { entry.HostConfigured = false; entry.HostConfiguredRound = null; }
                else return;
            }
            foreach (var member in members.EnumerateArray())
            {
                if (member.GetProperty("id").GetString() != entry.Client.Session.MemberId) continue;
                if (!member.TryGetProperty("ready", out var ready)) return;
                if (ready.ValueKind == System.Text.Json.JsonValueKind.False) ReleaseReadiness(entry);
                else if (retainReady && ready.ValueKind == System.Text.Json.JsonValueKind.True)
                    QueueWithdrawal(entry, owner);
                return;
            }
        }
    }

    private static void ReleaseReadiness(Entry entry)
    {
        entry.ReadinessActivity?.Dispose();
        entry.ReadinessActivity = null;
        entry.LocalInput?.WithdrawReadiness();
    }

    private void Notify(string owner)
    {
        foreach (var handler in StateChanged?.GetInvocationList() ?? [])
            try { ((Action<string>)handler)(owner); } catch { }
    }

    private RoomLifecycleSnapshot Snapshot(Entry entry)
    {
        var identity = entry.Identity;
        var connected = entry.Client.IsConnected;
        var state = identity is null ? "disconnected" : connected ? "connected" : "reconnecting";
        return new(state, identity?.Endpoint, identity?.Code, identity?.MemberId, entry.Client.IsRoomOwner,
            entry.CloseStatus, entry.Client.UnconfirmedRoomExits, entry.Client.Room, entry.LocalInput?.Snapshot, OperateGame(entry.Owner),
            identity is null ? entry.ExitReason : null);
    }

    private static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new OperationException("request.invalid", "房间服务地址必须为不含凭据、查询参数的 ws:// 或 wss:// 地址");
    }

    private static void ValidateNickname(string nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > 32 || nickname.Any(char.IsControl))
            throw new OperationException("request.invalid", "昵称长度须为 1 至 32 个字符且不能包含控制字符");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        foreach (var entry in entries.Values)
        {
            await entry.Client.DisposeAsync();
            lock (entry.ReadinessGate) ReleaseReadiness(entry);
            entry.Scheduler.Dispose();
            entry.Gate.Dispose();
        }
        entries.Clear();
        base.Dispose();
    }
}
