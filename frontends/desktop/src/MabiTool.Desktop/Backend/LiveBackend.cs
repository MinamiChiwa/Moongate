using System.Diagnostics;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.ModuleSdk;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>
/// 通过本机发现文件连接后端，用生成的 SDK 读取状态并转发意图。
/// 后端事件按主题标记要读的对象（到达即读，每秒至多 60 轮，间隔内的事件并入下一轮），界面各处报告可见性，没有可见使用者的数据不读；
/// 倒计时由界面按本机时钟递减；
/// 连接中断后保留最后状态并标记为未连接，每两秒重试，重连后不重放任何旧操作。
/// </summary>
public sealed partial class LiveBackend : IDesktopBackend, IAsyncDisposable
{
    private static readonly EventStreamOptions DesktopEvents = new(
        ["combat.live.changed", "combat.summary.changed", "combat.effects.changed", "combat.encounter.closed", "combat.observation.degraded", "combat.measurement.changed",
            "shortcut.triggered", "inventory.snapshot.changed", "inventory.catalog.changed", "game.observation.changed",
            "room.session.changed", "room.server.changed", "room.preferences.changed", "life.fishing.changed"],
        [], null);

    private sealed record State(bool Connected, string? Problem, GameBindingSnapshot? Binding,
        GameInputOwnershipSnapshot? Ownership, OperationSnapshot? OwnerOperation, CombatWorkspace? Workspace,
        IReadOnlyList<CombatEncounter> History, CombatEntityEffects? TargetEffects,
        IReadOnlyList<CombatEffectCoverage> Coverage, bool Simulation,
        IReadOnlyList<CombatDamageEvent>? FirstAttacks = null, CombatRangeSummary? Range = null, string? RangeProblem = null,
        CombatMeasurementState? Measurement = null, long MeasurementAt = 0, OperationSnapshot? Starting = null, string? EffectProblem = null,
        string? RangeKey = null, long EffectsAt = 0, long TargetEffectsAt = 0, CombatEntityEffects? AimEffects = null, long AimEffectsAt = 0);

    /// <summary>战斗意图等待后端回应的上限；超时后在活动胶囊写明，不在界面上无限等待。</summary>
    internal static readonly TimeSpan CombatRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly string discoveryPath;
    private readonly Action<Action> dispatch;
    private readonly DesktopShortcutNotifications shortcutNotifications;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    /// <summary>战斗循环的唤醒信号与帧间隔计时器（高精度，只作用于本进程）。</summary>
    private readonly SemaphoreSlim combatWake = new(0, 1);
    private readonly ModuleSdk.PreciseTimer combatTimer = new();
    private readonly Lock gate = new();
    private State state = new(false, null, null, null, null, null, [], null, [], false);
    /// <summary>本次握手得到的宿主实际监听地址（发现文件里的地址，可能是系统分配的端口）；未连接时为空。</summary>
    private string? apiAddress;
    private MabiToolClient? client;
    private string? notice;
    // DPS检测页是否显示；切走后不读取只供页面使用的首击时间线。显示时下一轮刷新必定通知页面（清除“更新中”）。
    private bool combatPageVisible, combatPageShown;
    private Task? loop;

    /// <param name="discoveryPath">后端写出的 instance.json。</param>
    /// <param name="dispatch">把状态变化通知送到界面线程。</param>
    public LiveBackend(string discoveryPath, Action<Action> dispatch)
    {
        this.discoveryPath = discoveryPath;
        this.dispatch = dispatch;
        shortcutNotifications = new(dispatch, OnShortcut);
    }

    /// <summary>工具根下宿主写出的发现文件；宿主与前端按同一规则判定工具根。</summary>
    public static string DefaultDiscoveryPath => Infrastructure.DesktopFiles.DiscoveryFile;

    public event EventHandler? Changed;
    public event EventHandler<string>? ShortcutTriggered;

    /// <summary>测试用：替换 HTTP 处理器以观察请求与响应；为空时使用默认处理器。</summary>
    internal Func<HttpMessageHandler>? Handler { get; init; }

    /// <summary>测试用：替换本机发现握手（与 <see cref="Handler"/> 搭配接替身后端）；为空时读取发现文件并经命名管道握手。</summary>
    internal Func<CancellationToken, Task<BootstrapResponse>>? Bootstrap { get; init; }

    public void Start() => loop ??= Task.Run(() => RunAsync(lifetime.Token));

    public ActivitySnapshot GetActivity()
    {
        var (current, message) = Read();
        if (!current.Connected)
        {
            // 宿主启动失败时状态行写原因短句、说明写做法（按宿主的启动失败记录），详细原因在诊断页的启动记录里。
            var failure = Infrastructure.DesktopFiles.Problem is null ? Infrastructure.PackagedHost.Failure : null;
            return new(null, Pick("未连接后端", "未連接後端"), failure?.Status ?? Pick("正在等待后端启动", "正在等待後端啟動"),
                failure?.Hint ?? current.Problem ?? Pick("连接后显示游戏输入的占用状态。", "連接後顯示遊戲輸入的佔用狀態。"),
                EffectStage.Pending, EffectStage.Pending, EffectStage.Pending, false);
        }
        if (current.Ownership is not { Owners.Count: > 0 } ownership)
        {
            // 没有模块持有输入时，活动胶囊可以显示扩展设置的一句状态：标明扩展名称，不填写持有者，
            // 持有输入的显示始终优先，扩展状态不会覆盖它。
            if (ExtensionActivity() is { } extension)
                return new(null, Pick("游戏输入空闲", "遊戲輸入空閒"), extension.Title,
                    message ?? extension.Detail, EffectStage.Pending, EffectStage.Pending, EffectStage.Pending);
            return new(null, Pick("游戏输入空闲", "遊戲輸入空閒"),
                Pick("可以开始演奏、合奏或染色", "可以開始演奏、合奏或染色"),
                message ?? Pick("DPS检测不占用游戏输入", "DPS檢測不佔用遊戲輸入"),
                EffectStage.Pending, EffectStage.Pending, EffectStage.Pending);
        }

        var owner = ownership.Owners[0];
        var effect = current.OwnerOperation?.EffectStatus.KnownValue;
        var others = ownership.Owners.Count > 1
            ? Pick($"，另有 {ownership.Owners.Count - 1} 个占用", $"，另有 {ownership.Owners.Count - 1} 個佔用")
            : string.Empty;
        // 模块、操作类型、阶段与资源都换成界面文字；操作号对用户没有意义，只放进悬停提示（Reference）。
        var module = CodeText.Module(owner.OwnerId);
        return new(owner.OwnerId, Pick($"{module} 持有游戏输入{others}", $"{module} 持有遊戲輸入{others}"),
            current.OwnerOperation is { } operation
                ? $"{CodeText.Operation(operation.Kind)} · {OperationPhase(operation.Kind, operation.Phase)}"
                : Pick($"占用资源：{string.Join("、", owner.Resources.Select(CodeText.Resource))}",
                    $"佔用資源：{string.Join("、", owner.Resources.Select(CodeText.Resource))}"),
            message ?? EffectText(effect),
            EffectStage.Done,
            effect switch
            {
                EffectStatus.InputDispatched or EffectStatus.ObservedApplied => EffectStage.Done,
                EffectStatus.None => EffectStage.Pending,
                _ => EffectStage.Unknown
            },
            effect switch
            {
                EffectStatus.ObservedApplied => EffectStage.Done,
                EffectStatus.None or EffectStatus.InputDispatched => EffectStage.Pending,
                _ => EffectStage.Unknown
            })
        { Reference = owner.OperationId };
    }

    // 染色寻色的阶段用染色页的阶段说明（与原工具一致），其余操作用通用阶段文字。
    private static string OperationPhase(string kind, string? phase) =>
        CodeText.Phase(phase, kind == "color.search" ? ColorPhase : null);

    private static string? ColorPhase(string phase) =>
        Pages.Dyeing.ColorStages.Known.Contains(phase) ? Pages.Dyeing.ColorStages.Name(phase) : null;

    // 活动说明：受理、送出、游戏确认三段里走到了哪一步。
    internal static string EffectText(EffectStatus? effect) => effect switch
    {
        EffectStatus.InputDispatched => Pick("已发送输入，等待游戏确认", "已發送輸入，等待遊戲確認"),
        EffectStatus.ObservedApplied => Pick("游戏已确认生效", "遊戲已確認生效"),
        EffectStatus.Unknown => Pick("已发送，是否生效未知", "已發送，是否生效未知"),
        _ => Pick("已受理，尚未发送输入", "已受理，尚未發送輸入")
    };

    public BindingView GetGameBinding()
    {
        var (current, _) = Read();
        string? nickname;
        lock (gate) nickname = pages.Nickname;
        return MapBinding(current.Connected, current.Binding, current.Simulation, nickname);
    }

    // 游戏观测里有角色昵称时名称用昵称原文，其余沿用窗口名称。
    internal static BindingView MapBinding(bool connected, GameBindingSnapshot? snapshot, bool simulation, string? nickname)
    {
        if (!connected)
        {
            var offline = Pick("未连接后端", "未連接後端");
            return new(offline, Pick("洛奇 Mobile 台服", "洛奇 Mobile 台服"),
                Pick("后端启动后自动连接", "後端啟動後自動連接"), false, false, offline);
        }
        if (snapshot?.Binding is not { } binding)
        {
            var unbound = Pick("未绑定", "未綁定");
            return new(unbound, Pick("洛奇 Mobile 台服", "洛奇 Mobile 台服"),
                Pick("尚未绑定游戏窗口", "尚未綁定遊戲視窗"), false, true, unbound);
        }
        var region = binding.Session.Region == "TW" ? Pick("洛奇 Mobile 台服", "洛奇 Mobile 台服") : binding.Session.Region;
        var window = simulation ? Pick("模拟游戏窗口", "模擬遊戲視窗") : Pick("游戏窗口", "遊戲視窗");
        var name = nickname is { Length: > 0 } ? nickname : window;
        return snapshot.State == "bound"
            ? new(name, region, Pick("已绑定游戏窗口", "已綁定遊戲視窗"), true, true, window)
            : new(name, region, CodeText.State(snapshot.State), false, true, window);
    }

    public Avalonia.PixelRect? GetGameWindowBounds()
    {
        var (current, _) = Read();
        return current.Binding?.Binding is { IsMinimized: false, Bounds: var bounds }
            ? new Avalonia.PixelRect(bounds.X, bounds.Y, bounds.Width, bounds.Height)
            : null;
    }

    public void ReportNotice(string text)
    {
        lock (gate) notice = text;
        Notify();
    }

    public bool IsGameMinimized() => Read().State.Binding?.Binding?.IsMinimized == true;

    public string? GetCapabilityNotice(string capabilityId)
    {
        var (current, _) = Read();
        PageState page;
        lock (gate) page = pages;
        return current.Connected ? CapabilityNotice.Describe(page.Capabilities?.FirstOrDefault(item => item.Id == capabilityId)) : null;
    }

    public CapabilityLevel GetCapabilityLevel(string capabilityId)
    {
        var (current, _) = Read();
        PageState page;
        lock (gate) page = pages;
        var status = page.Capabilities?.FirstOrDefault(item => item.Id == capabilityId)?.Status.KnownValue;
        return !current.Connected ? CapabilityLevel.Unknown : status switch
        {
            CapabilityStatus.Available => CapabilityLevel.Available,
            CapabilityStatus.Degraded => CapabilityLevel.Degraded,
            CapabilityStatus.Unavailable => CapabilityLevel.Unavailable,
            _ => CapabilityLevel.Unknown
        };
    }

    public TopicNotice? GetPresentationNotice(string topic)
    {
        var (current, _) = Read();
        PageState page;
        lock (gate) page = pages;
        return current.Connected ? TopicNoticeText.Describe(page.Sources?.FirstOrDefault(item => item.Topic == topic)) : null;
    }

    public CombatPageData GetCombat() => combatMemo.Get(CombatKey(), MapCombat);

    // 怪物血量能力的停用说明（随页面状态读取的战斗能力表）；可用、未知或未连接时为空。
    private string? HealthNotice() => GetCapabilityNotice(CapabilityIds.MonsterHealth);

    private CombatPageData MapCombat()
    {
        var (current, _) = Read();
        var chosen = selection.Snapshot();
        var (scope, range) = (chosen.Scope, chosen.Target);
        // 最近一轮读到的范围结果针对的是另一个选择（刚切换、读取还在路上或迟到的旧结果）：不采用，页面保留上一次的显示并等新结果。
        // 当前分段且不选目标时直接用分段摘要，不依赖范围结果。
        var pending = current.RangeKey is { } key && key != chosen.Key && !(scope == "segment" && range is null);
        var data = CombatMapper.Map(new(current.Connected, current.Binding?.Binding is not null, current.Workspace,
            current.History, current.TargetEffects, current.EffectProblem, current.Coverage, current.FirstAttacks, scope, range,
            pending ? null : current.Range, pending ? null : current.RangeProblem, current.Measurement, current.MeasurementAt, current.EffectsAt,
            current.TargetEffectsAt, HealthNotice(), current.AimEffects, current.AimEffectsAt))
            with { SelectedScope = scope, SelectedTarget = range, Pending = pending };
        // 开始观测已受理、正在等待识别角色时可取消；失败时写明原因。
        var capture = current.Connected ? GetCaptureStatus() : null;
        var waiting = capture is null ? null : CaptureText.Waiting(capture);
        var starting = current.Connected ? StartView(current.Starting, current.Workspace, waiting) : null;
        CombatAutoObservationState? auto;
        lock (gate) auto = autoObservation;
        var autoView = current.Connected ? AutoView(auto, current.Workspace, waiting,
            capture?.Hint == "observation.identityUnconfirmed" ? waiting : null) : null;
        data = data with { Starting = starting, Auto = autoView };
        // 自动观测开启时由后端保持观测：不需要开始，也不提供停止（停止后会自动重新开始，要暂停请关闭自动观测）。
        // 抓包未运行等原因使自动观测处于 off 时保留手动开始（模拟宿主用回放来源即是这样）。
        if (auto is { Enabled: true, State: not "off" }) data = data with { Actions = data.Actions with { CanStart = false, CanStop = false } };
        else if (starting is { Waiting: true }) data = data with { Actions = data.Actions with { CanStart = false, CanStop = true } };
        // Buff 收藏筛选在这里统一套用，主页面、明细窗口与浮窗拿到同一份结果。
        return CombatEffectFavorites.Apply(data);
    }

    public Task RequestEmergencyStopAsync(CancellationToken cancellationToken)
    {
        // Moongate 在前台时按下按钮：允许宿主把游戏窗口切到前台，合奏的本机停止键才能送达。
        AllowHostForeground();
        return SendAsync(async (api, current, token) =>
        {
            var epoch = current.Ownership?.BackendEpoch ?? current.Binding?.BackendEpoch
                ?? throw new InvalidOperationException(Pick("后端状态尚未就绪", "後端狀態尚未就緒"));
            var receipt = await api.EmergencyStopGameAsync(new(epoch, DateTimeOffset.UtcNow.AddSeconds(5)),
                IdempotencyKey(), token);
            return FollowEmergencyStop(EmergencyStopFollowUp.Button, receipt);
        }, cancellationToken);
    }

    // 快捷键通知：紧急停止已由后端执行，前端只做后续（取消定时测量、界面反馈），不交给外壳；其余动作照常交给外壳分发。
    private void OnShortcut(string action)
    {
        if (action == "emergency-stop")
        {
            var text = FollowEmergencyStop(EmergencyStopFollowUp.Hotkey, null);
            lock (gate) notice = text;
            Notify();
            return;
        }
        ShortcutTriggered?.Invoke(this, action);
    }

    /// <summary>紧急停止之后：进行中的定时测量经战斗页已有的取消入口取消；合奏反馈横幅与活动胶囊写明结果。</summary>
    private string FollowEmergencyStop(string source, GameEmergencyStopReceipt? receipt)
    {
        CombatMeasurementState? measurement;
        lock (gate) measurement = measurementState ?? state.Measurement;
        var plan = EmergencyStopFollowUp.Plan(source, measurement?.Phase, receipt);
        foreach (var intent in plan.Intents) _ = SendIntentAsync(intent, null, CancellationToken.None);
        SetRoomFeedback(new(plan.Text, plan.Failed ? RoomTone.Warning : RoomTone.Success));
        return plan.Text;
    }

    public bool SetCombatPageVisible(bool visible)
    {
        lock (gate)
        {
            combatPageVisible = visible;
            if (visible) combatPageShown = true;
        }
        // 回到页面：先用适配层里的最近结果显示，再立即补读一次快照（不等合并窗口）。
        if (visible) MarkDue(Due.Snapshot | Due.Recompute, immediate: true);
        return visible;
    }

    public Task SendCombatIntentAsync(CombatIntent intent, string? targetId, CancellationToken cancellationToken)
    {
        // 怪物效果区的目标与“选择目标”是同一个选择。
        if (intent == CombatIntent.SelectTarget) return SendCombatPageIntentAsync("combat.target", targetId, cancellationToken);

        // 开始观测、重设后回到当前分段：只在发出请求后用户没有另选范围时生效。
        var issuedAt = selection.Version;
        return SendAsync(async (api, current, token) =>
        {
            var key = IdempotencyKey();
            var observation = current.Workspace?.Observation;
            switch (intent)
            {
                case CombatIntent.StartObservation:
                    var session = current.Binding?.Binding?.Session
                        ?? throw new InvalidOperationException(Pick("尚未绑定游戏窗口", "尚未綁定遊戲視窗"));
                    RememberStart(await api.StartCombatObservationAsync(
                        new(session.Id, ReplayScenarioId: current.Simulation ? "live" : null), key, token));
                    selection.Follow(issuedAt, "segment", liveToo: true);
                    return null;
                case CombatIntent.StopObservation when observation is not null && observation.State == "observing":
                    await api.StopCombatObservationAsync(observation.Id, token);
                    break;
                // 等待识别角色期间取消：停止尚未开始的观测 Operation。
                case CombatIntent.StopObservation when PendingStart() is { } pending:
                    await api.StopCombatObservationAsync(pending, token);
                    lock (gate) { startingObservation = null; startingResult = null; }
                    return Pick("已取消开始观测", "已取消開始觀測");
                case CombatIntent.EndEncounter when observation is not null && current.Workspace?.Encounter is { } encounter:
                    await api.CloseCombatEncounterAsync(encounter.Id, new(observation.Id), key,
                        await ETagAsync(api, encounter.Id, token), token);
                    break;
                case CombatIntent.SplitEncounter when observation is not null && current.Workspace?.Encounter is { } encounter:
                    try
                    {
                        await api.SplitCombatEncounterAsync(encounter.Id, new(observation.Id), key,
                            await ETagAsync(api, encounter.Id, token), token);
                    }
                    catch (MabiToolApiException error) when (SplitRefusal(error.Problem?.Code) is { } refusal)
                    {
                        return refusal;
                    }
                    return Pick("已拆段，下一次伤害开始新分段", "已拆段，下一次傷害開始新分段");
                case CombatIntent.ResetEncounter when observation is not null && current.Workspace?.Encounter is { } encounter:
                    await api.ResetCombatEncounterAsync(encounter.Id, new(observation.Id), key,
                        await ETagAsync(api, encounter.Id, token), token);
                    selection.Follow(issuedAt, "segment", liveToo: true);
                    break;
                case CombatIntent.ToggleFavorite when targetId is not null:
                    var favorite = current.History.FirstOrDefault(item => item.Id == targetId)?.Favorite ?? false;
                    await api.FavoriteCombatEncounterAsync(targetId, new(!favorite), key,
                        await ETagAsync(api, targetId, token), token);
                    break;
                case CombatIntent.CleanHistory:
                    var summary = await api.CleanupCombatHistoryAsync(key, token);
                    return Pick($"已清理 {summary.RemovedEncounters} 场旧记录", $"已清理 {summary.RemovedEncounters} 場舊紀錄");
            }
            return null;
        }, cancellationToken, CombatRequestTimeout);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (loop is not null)
            try { await loop; }
            catch (OperationCanceledException) { }
        client?.Dispose();
        lifetime.Dispose();
        wake.Dispose();
        combatWake.Dispose();
        combatTimer.Dispose();
    }

    // 拆段被拒绝的两种常见原因给出短句，其余错误码照常原样显示。
    internal static string? SplitRefusal(string? code) => code switch
    {
        "combat.segmentInactive" => Pick("当前没有进行中的分段", "目前沒有進行中的分段"),
        "revision.stale" => Pick("分段状态已变化，请再试一次", "分段狀態已變化，請再試一次"),
        _ => null
    };

    private static async Task<string> ETagAsync(MabiToolClient api, string encounterId, CancellationToken token)
    {
        string? tag = null;
        await api.GetCombatEncounterAsync(encounterId, token, metadata => tag = metadata.ETag);
        return tag ?? throw new InvalidOperationException("后端没有返回修订标记");
    }

    // 意图发送一次；后端存储暂时繁忙（503）时以同一幂等键退避重试，其余失败原因直接显示在活动列。
    // timeout 不为空时请求最多等这么久：超时取消请求并在活动胶囊写明，界面不随后端迟迟不回应而停在“进行中”。
    private async Task SendAsync(Func<MabiToolClient, State, CancellationToken, Task<string?>> send,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var (current, _) = Read();
        string? result;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        if (timeout is { } span) limit.CancelAfter(span);
        try
        {
            var api = client ?? throw new InvalidOperationException(Pick("未连接后端", "未連接後端"));
            if (!current.Connected) throw new InvalidOperationException(Pick("未连接后端", "未連接後端"));
            result = await WithStorageRetryAsync(() => send(api, current, limit.Token), limit.Token);
        }
        catch (OperationCanceledException) when (timeout is not null && limit.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            result = Pick("请求未完成：后端无响应", "請求未完成：後端無回應");
        }
        catch (Exception error) when (error is MabiToolApiException or InvalidOperationException or HttpRequestException
                                          or IOException or OperationCanceledException)
        {
            result = Pick("请求未完成：", "請求未完成：") + CodeText.Error(error);
        }
        lock (gate) notice = result;
        // 用户操作之后立即重读：不等合并窗口，也不等各自的例行间隔。
        MarkDue(Due.All, immediate: true);
        Notify();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var handshake = Bootstrap is { } connect ? await connect(token)
                    : await LocalBootstrapClient.ConnectAsync(discoveryPath, "moongate-desktop", token);
                using var api = new MabiToolClient(new(handshake.ApiBaseUri), _ => ValueTask.FromResult(handshake.Grant.Token), Handler?.Invoke());
                client = api;
                lock (gate)
                {
                    diagnosticAccess = DiagnosticAccessOf(handshake.Grant.Session);
                    apiAddress = handshake.ApiBaseUri;
                    inventoryDue = true;
                }
                var simulation = (await api.GetCombatCapabilitiesAsync(token)).Simulation;
                try { await RegisterFrontendShortcutsAsync(api, token); }
                catch (MabiToolApiException error) { Remember(Problem(error)); }
                var backendEpoch = (await api.GetGameInputOwnershipAsync(token)).BackendEpoch;
                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                // 新连接：全部数据重读一次，历史、范围与效果的缓存作废。
                lock (gate)
                {
                    due = Due.All; urgent = true;
                    lastStatusRead = lastSnapshotRead = lastCaptureRead = lastRoomRead = lastColorRunRead = lastSurfacesRead = lastAutoRead = 0;
                    historyPage = null; rangeRead = null; coverageRead = null; targetRead = default; aimRead = default; measurementState = null;
                    pagesDue = true; systemInfo = null; iconCatalogDue = true;
                    ResetConnector();
                    ResetFishing();
                    ResetGameData();
                    ResetLife();
                }
                var events = ListenAsync(api, backendEpoch, session.Token);
                // 战斗数据由独立的循环读取，不排在图标、合奏、曲长等读取之后。
                var combat = RunCombatAsync(api, simulation, events, session.Token);
                try
                {
                    while (!events.IsCompleted && !combat.IsCompleted)
                    {
                        await RefreshStatusAsync(api, simulation, token);
                        await RefreshClientIconsAsync(api, token);
                        await RefreshColorRunAsync(api, token);
                        await RefreshPagesAsync(api, token);
                        await RefreshExtensionSurfacesAsync(api, token);
                        await RefreshCaptureAsync(api, token);
                        await RefreshConnectorAsync(api, token);
                        await RefreshFishingAsync(api, token);
                        await RefreshLifeSnapshotAsync(api, token);
                        RefreshGameData(api, token);
                        await RefreshRoomAsync(api, token);
                        await PumpScoreDurationsAsync(api, token);
                        await RefreshScoreSupportAsync(api, token);
                        // 每类数据按自己的时机到期（事件、用户操作、可见性变化或例行间隔），没有到期的工作时一直等待。
                        await WaitForWorkAsync(token);
                    }
                    // 战斗读取失败或被取消（连接中断、后端错误、请求超时）与主循环一样结束本次连接。
                    if (combat.IsFaulted || combat.IsCanceled) await combat;
                    await events;
                }
                finally
                {
                    await session.CancelAsync();
                    try { await events; }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    try { await combat; }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is MabiToolApiException or HttpRequestException or IOException
                                              or InvalidDataException or InvalidOperationException or TimeoutException
                                              or UnauthorizedAccessException or System.Text.Json.JsonException
                                              or OperationCanceledException)
            {
                // 工具根不可写或参数错误时宿主也无法在此启动；目录包里宿主停止或未能启动时写明宿主的情况。
                var reason = Infrastructure.DesktopFiles.Problem ?? Infrastructure.PackagedHost.Problem ??
                    CodeText.Error(error);
                client = null;
                lock (gate)
                {
                    state = state with { Connected = false, Problem = reason };
                    diagnosticAccess = diagnosticAccess with { Connected = false };
                    apiAddress = null;
                }
                Notify();
            }
            await Task.Delay(TimeSpan.FromSeconds(2), token).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    // 状态事件唤醒快照刷新；快捷键是仅在线有效的 UI 动作。
    internal async Task ListenAsync(MabiToolClient api, string backendEpoch, CancellationToken token)
    {
        var generation = shortcutNotifications.Connect(backendEpoch, token);
        NotifyDiagnosticStream();
        try
        {
            await foreach (var message in api.StreamEventsAsync(StreamOptions(), token))
            {
                if (message.Event == "control") return;
                if (message.Event == "diagnostics.record") ReceiveDiagnosticRecord(message);
                else if (message.Event == "shortcut.triggered") shortcutNotifications.Receive(generation, message);
                else if (message.Event.StartsWith("combat.", StringComparison.Ordinal)) ReceiveCombatEvent(message);
                else if (message.Event.StartsWith("inventory.", StringComparison.Ordinal))
                {
                    lock (gate) inventoryDue = true;
                    Signal();
                }
                else if (message.Event == "game.observation.changed") MarkDue(Due.Capture, immediate: false);
                else if (message.Event == "life.fishing.changed") MarkDue(Due.Fishing, immediate: false);
                else if (message.Event == "room.preferences.changed") MarkRoomPreferencesDue();
                else if (message.Event.StartsWith("room.", StringComparison.Ordinal)) MarkDue(Due.Room, immediate: false);
                else Signal();
            }
        }
        finally
        {
            shortcutNotifications.Disconnect(generation);
            // 事件流结束：唤醒主循环与战斗循环，让它们发现连接已断并重新握手。
            Signal();
            SignalCombat();
        }
    }

    /// <summary>战斗循环：有战斗视图可见时按到期的战斗工作读取，事件流结束或读取失败时结束（失败时主循环随之结束本次连接）。</summary>
    private async Task RunCombatAsync(MabiToolClient api, bool simulation, Task events, CancellationToken token)
    {
        try
        {
            while (!events.IsCompleted)
            {
                await RefreshCombatAsync(api, simulation, token);
                ReportDisplayLatency(api);
                await WaitForCombatAsync(events, token);
            }
        }
        finally { Signal(); }
    }

    private string? statusSignature;

    /// <summary>
    /// 绑定与输入占用：界面有可见处时每秒读取（浮窗跟随游戏窗口、活动胶囊的占用状态），全部隐藏时每五秒；
    /// 开始观测的 Operation 等待结果期间每秒查询。修订、窗口位置与占用都没变时不通知界面。
    /// </summary>
    private async Task RefreshStatusAsync(MabiToolClient api, bool simulation, CancellationToken token)
    {
        lock (gate)
        {
            var interval = AnySurface || startingObservation is not null ? StatusInterval : HiddenStatusInterval;
            if ((due & Due.Status) == 0 && state.Connected && !Elapsed(lastStatusRead, interval)) return;
            due &= ~Due.Status;
            lastStatusRead = Now;
        }
        var bindingRead = api.GetGameBindingAsync(token);
        var ownershipRead = api.GetGameInputOwnershipAsync(token);
        await Task.WhenAll(bindingRead, ownershipRead);
        var binding = bindingRead.Result;
        var ownership = ownershipRead.Result;
        OperationSnapshot? operation = null;
        // 只有 Operation 标识（op- 加 32 位十六进制）才查询操作；合奏本机准备的占用标识为 room-…，没有对应的 Operation，
        // 按秒查询会得到 404 并写满诊断记录。
        if (ownership.Owners.Count > 0 && IsOperationId(ownership.Owners[0].OperationId))
            try { operation = await api.GetOperationAsync(ownership.Owners[0].OperationId, token); }
            catch (MabiToolApiException) { }
        var starting = await RefreshStartAsync(api, Read().State.Workspace, token);
        await RefreshRecordsAsync();
        var next = string.Join('|', binding.Revision, binding.State, binding.Binding?.Bounds, binding.Binding?.IsMinimized, operation?.Revision,
            starting?.Revision, starting?.State.RawValue, string.Join(',', ownership.Owners.Select(owner => owner.OwnerId + owner.OperationId)));
        bool changed;
        lock (gate)
        {
            changed = !state.Connected || next != statusSignature;
            statusSignature = next;
            NoteIconBinding(binding);
            if (changed)
                state = state with { Connected = true, Problem = null, Binding = binding, Ownership = ownership, OwnerOperation = operation,
                    Starting = starting, Simulation = simulation };
        }
        if (changed) Notify();
    }

    private string? combatSignature;
    private long lastAutoRead;
    private string? combatResourceRevision;
    private long selfEffectsAt;

    /// <summary>
    /// 战斗数据：只在有战斗视图可见（DPS检测页、明细窗口或战斗浮窗元件）时读取，按事件标记的对象读：摘要与缺口事件读快照，
    /// 效果事件只读列出的实体（本机效果在快照里，怪物效果单独读），分段结算与用户操作读战斗记录，测量事件直接采用事件里的状态。
    /// 范围汇总、怪物效果与覆盖率按各自的修订键读取，键没变就沿用上次结果；修订与结果都没变时不通知界面。
    /// </summary>
    private async Task RefreshCombatAsync(MabiToolClient api, bool simulation, CancellationToken token)
    {
        Due flags;
        State current;
        bool visible, shown, secondary;
        lock (gate)
        {
            if (!CombatWanted) return;
            var now = Now;
            var fallback = Elapsed(lastSnapshotRead, SnapshotInterval);
            var ready = (due & Due.Combat) != 0 && CombatReadyAt() <= now;
            var followUp = followUpAt != 0 && followUpAt <= now;
            if (!ready && !fallback && !followUp) return;
            flags = due & Due.Combat;
            if (fallback) flags |= Due.Snapshot;
            if (combatPageVisible && Elapsed(lastAutoRead, SlowInterval)) flags |= Due.Auto;
            // 只为最新捕获事件而来的一轮（只读快照，非用户操作）不重读按时间线重新统计的范围、明细与首击，至多每 SecondaryInterval 一次。
            secondary = urgent || followUp || (flags & ~(Due.Snapshot | Due.Measurement | Due.Auto)) != 0 || Elapsed(lastSecondaryRead, SecondaryInterval);
            if (secondary) lastSecondaryRead = now;
            due &= ~Due.Combat;
            urgent = false; followUpAt = 0; lastCombatRound = now;
            if ((flags & Due.Snapshot) != 0) lastSnapshotRead = now;
            if ((flags & Due.Auto) != 0) lastAutoRead = now;
            visible = combatPageVisible; shown = combatPageShown; combatPageShown = false;
            current = state;
        }
        Interlocked.Increment(ref combatRounds);

        // 相互独立的读取并行发出。
        var snapshotRead = (flags & Due.Snapshot) != 0 ? api.GetCombatSnapshotAsync(token) : null;
        var historyRead = (flags & Due.History) != 0 || historyPage is null ? api.GetCombatEncountersAsync(token) : null;
        // 测量事件只在阶段变化时到达；等待或计时中随每轮读取（与快照同一节拍）取得累计伤害与 DPS。
        bool measuring;
        lock (gate) measuring = MeasurementActive;
        var measurementRead = (flags & Due.Measurement) != 0 || measurementState is null || measuring && (flags & Due.Snapshot) != 0
            ? TryRead(() => api.GetCombatMeasurementAsync(token)) : null;
        var autoRead = (flags & Due.Auto) != 0 || autoObservation is null ? TryRead(() => api.GetCombatAutoObservationAsync(token)) : null;
        var workspace = current.Workspace;
        if (snapshotRead is not null)
        {
            var snapshot = await snapshotRead;
            workspace = snapshot.Data;
            lock (gate) { combatResourceRevision = snapshot.ResourceRevision.ToString(); selfEffectsAt = Now; }
        }
        if (historyRead is not null) historyPage = await historyRead;
        // 能否开始测量取决于观察是否在进行并已收到观测帧，宿主不为它推送事件：测量尚不可用、或观察换了或状态变了时随快照补读一次。
        if (measurementRead is null && snapshotRead is not null
            && (measurementState is { Available: false } || workspace?.Observation.Id != current.Workspace?.Observation.Id
                || workspace?.Observation.State != current.Workspace?.Observation.State))
            measurementRead = TryRead(() => api.GetCombatMeasurementAsync(token));
        if (measurementRead is not null && await measurementRead is { } measured) ApplyMeasurement(measured, Now);
        if (autoRead is not null && await autoRead is { } auto) lock (gate) autoObservation = auto;
        var page = historyPage!;
        var history = page.Items;

        CombatMeasurementState? measurement; long measurementTime;
        lock (gate) { measurement = measurementState; measurementTime = measurementAt; }
        var (range, rangeProblem, chosen) = await RangeAsync(api, workspace, page, measurement, token, secondary);
        // 怪物效果：按页面同一规则（所选目标，否则正在攻击或排序第一个仍在当前观测中的怪物）取效果目标，只查询当前观测里的实体；
        // 同一目标只在效果事件列出它、或目标换了时重读。
        var effectTarget = CombatMapper.EffectTargetId(workspace, chosen.Scope, chosen.Target, range);
        var selected = workspace?.Entities.FirstOrDefault(entity => entity.Id == effectTarget && entity.Role.KnownValue == CombatEntityRole.Monster);
        var target = targetRead;
        if (selected is null) target = default;
        else if (target.Id != selected.Id || (flags & Due.TargetEffects) != 0 || target.Effects is null && target.Problem is null)
        {
            CombatEntityEffects? effects = null; string? problem = null;
            try { effects = await api.GetCombatEntityEffectsAsync(selected.Id, token); }
            catch (MabiToolApiException error) { problem = error.Problem?.Code ?? $"http.{error.Status}"; }
            target = (selected.Id, effects, problem, Now);
        }
        lock (gate) targetRead = target;
        // 收藏浮窗的目标项取当前攻击目标（快照 currentTarget）的效果：与效果目标是同一实体时沿用，否则按同一规则另读。
        var aimId = workspace?.CurrentTarget?.EntityId is { } aimEntity && workspace.Entities.Any(entity => entity.Id == aimEntity) ? aimEntity : null;
        var aimEffects = aimRead;
        if (aimId is null) aimEffects = default;
        else if (aimId == target.Id) aimEffects = target;
        else if (aimEffects.Id != aimId || (flags & Due.TargetEffects) != 0 || aimEffects.Effects is null && aimEffects.Problem is null)
        {
            CombatEntityEffects? effects = null; string? problem = null;
            try { effects = await api.GetCombatEntityEffectsAsync(aimId, token); }
            catch (MabiToolApiException error) { problem = error.Problem?.Code ?? $"http.{error.Status}"; }
            aimEffects = (aimId, effects, problem, Now);
        }
        lock (gate) aimRead = aimEffects;
        // 换了范围或目标后的这一轮：范围结果与目标效果一到就先交给界面，覆盖率、首击与明细随后读到再更新。它们按时间线分页查询，
        // 数据多时一轮要数百毫秒，切换不等它们；首击属于旧选择，先清空（显示未读取）。
        if (current.RangeKey != chosen.Key)
        {
            lock (gate)
            {
                state = state with { Workspace = workspace, History = history, TargetEffects = target.Effects, Simulation = simulation,
                    FirstAttacks = null, Range = range, RangeProblem = rangeProblem, Measurement = measurement, MeasurementAt = measurementTime,
                    EffectProblem = target.Problem, RangeKey = chosen.Key, EffectsAt = selfEffectsAt, TargetEffectsAt = target.At,
                    AimEffects = aimEffects.Effects, AimEffectsAt = aimEffects.At };
                AdoptLive(workspace, simulation);
            }
            Notify();
        }
        var coverage = await CoverageAsync(api, workspace, token);
        // 首击只在 DPS检测页显示：页面切走后不读取它的时间线。只为最新捕获事件而来的一轮沿用上次的首击与明细，间隔到时补读。
        var firstAttacks = !visible ? null : secondary || current.RangeKey != chosen.Key ? await FirstAttacksAsync(api, workspace, range, history, chosen, token)
            : current.FirstAttacks;
        if (secondary) await RefreshDetailAsync(api, workspace, history, range, rangeProblem, chosen.Key, token);
        else if (visible || combatDetailOpen || chosen.Scope != "segment" || chosen.Target is not null)
            lock (gate) { var at = lastSecondaryRead + Span(SecondaryInterval); followUpAt = followUpAt == 0 ? at : Math.Min(followUpAt, at); }

        string? resource;
        lock (gate) resource = combatResourceRevision;
        // 分段进行中停手时宿主不再推送摘要事件，快照里的时长与近期 DPS 仍在变：签名纳入它们，兜底读到的变化照常通知界面。
        var summary = workspace?.Encounter?.Summary;
        // 当前攻击目标的血量变化不推进工作区修订：签名纳入目标与血量，例行读取读到的变化照常通知界面。
        var aim = workspace?.CurrentTarget;
        var next = string.Join('|', resource, workspace?.Revision, summary?.DurationMs, summary?.EncounterDps, summary?.RollingDps,
            aim?.EntityId, aim?.Health?.Current, aim?.Health?.Maximum, aim?.Health?.State, aim?.Health?.Reason, aim?.Health?.Stale, aim?.Dead,
            aim?.IsBoss, aim?.RedirectedFrom, aim?.ResolvedName?.State, aim?.ResolvedName?.Text, aim?.ResolvedName?.Reason,
            page.Revision, target.Effects?.Revision, target.Id, target.Problem, aimEffects.Effects?.Revision, aimEffects.Id, coverage.Count,
            string.Join(',', coverage.Select(item => item.Ratio)), firstAttacks?.Count, firstAttacks?.FirstOrDefault()?.MonotonicMs, range?.Result.Revision,
            range?.Result.Summary.AttributedDamage, range?.TargetId, rangeProblem, chosen.Key, measurement?.Revision, measurement?.Phase,
            measurement?.Damage, measurement?.CurrentDps, measurement?.Available, measurement?.UnavailableReason,
            AutoSignature(), TimelineSignature());
        bool changed;
        lock (gate)
        {
            changed = shown || next != combatSignature;
            combatSignature = next;
            if (changed)
            {
                state = state with { Workspace = workspace, History = history, TargetEffects = target.Effects, Coverage = coverage, Simulation = simulation,
                    FirstAttacks = firstAttacks, Range = range, RangeProblem = rangeProblem, Measurement = measurement, MeasurementAt = measurementTime,
                    EffectProblem = target.Problem, RangeKey = chosen.Key, EffectsAt = selfEffectsAt, TargetEffectsAt = target.At,
                    AimEffects = aimEffects.Effects, AimEffectsAt = aimEffects.At };
                AdoptLive(workspace, simulation);
            }
        }
        if (changed) Notify();
    }

    private static async Task<T?> TryRead<T>(Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        catch (MabiToolApiException) { return null; }
    }

    /// <summary>
    /// 当前分段的效果覆盖率：分段换了立即读；同一分段修订变化时至多每 <see cref="CoverageInterval"/> 读一次，
    /// 间隔内的变化安排一次补读。分段的时间线在后端写入前查不到：同一分段得到 404 后等它的修订变化再查。
    /// </summary>
    private async Task<IReadOnlyList<CombatEffectCoverage>> CoverageAsync(MabiToolClient api, CombatWorkspace? workspace, CancellationToken token)
    {
        if (workspace?.Encounter is not { } encounter) return [];
        var revision = encounter.Revision.ToString();
        var marker = $"{encounter.Id}|{revision}";
        lock (gate)
        {
            if (missingTimeline == marker) return [];
            if (coverageRead is { } read && read.EncounterId == encounter.Id)
            {
                if (read.Revision == revision) return read.Coverage;
                if (!Elapsed(read.At, CoverageInterval))
                {
                    var at = read.At + Span(CoverageInterval);
                    followUpAt = followUpAt == 0 ? at : Math.Min(followUpAt, at);
                    return read.Coverage;
                }
            }
        }
        try
        {
            var coverage = (await api.QueryCombatTimelineAsync(encounter.Id, new(PageSize: 1), token)).EffectCoverage;
            lock (gate) coverageRead = (encounter.Id, revision, Now, coverage);
            return coverage;
        }
        catch (MabiToolApiException error)
        {
            if (error.Status == 404) lock (gate) missingTimeline = marker;
            return [];
        }
    }

    internal static bool IsOperationId(string? id) => id is { Length: 35 } && id.StartsWith("op-", StringComparison.Ordinal)
        && id.AsSpan(3).IndexOfAnyExcept("0123456789abcdef") < 0;

    private (State State, string? Notice) Read()
    {
        lock (gate) return (state, notice);
    }

    private void Signal()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void SignalCombat()
    {
        try { combatWake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    // 界面线程上最多排着一次 Changed：后端事件密集时（观测与定时测量中每秒可达十余次）合并成一次整页重读，
    // 界面线程不会被排队的通知占满而失去响应。标记在执行前清除，执行期间到达的变化会再排一次，不丢更新。
    private int notifyQueued;

    private void Notify()
    {
        if (Interlocked.Exchange(ref notifyQueued, 1) == 1) return;
        dispatch(() =>
        {
            Volatile.Write(ref notifyQueued, 0);
            Changed?.Invoke(this, EventArgs.Empty);
            // 界面已按这次通知重读并显示：记下显示的战斗数据距抓包时刻的延迟。
            RecordDisplayed();
        });
    }

    /// <summary>界面线程上尚未执行的状态通知个数（0 或 1），供测试观察通知合并。</summary>
    internal int QueuedNotifications => Volatile.Read(ref notifyQueued);
}
