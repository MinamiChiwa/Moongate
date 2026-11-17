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
/// 后端事件到达时立即刷新，另有每秒一次的例行刷新维持倒计时与输入占用状态；
/// 连接中断后保留最后状态并标记为未连接，每两秒重试，重连后不重放任何旧操作。
/// </summary>
public sealed partial class LiveBackend : IDesktopBackend, IAsyncDisposable
{
    private static readonly EventStreamOptions DesktopEvents = new(
        ["combat.summary.changed", "combat.effects.changed", "combat.encounter.closed", "combat.observation.degraded", "combat.measurement.changed",
            "shortcut.triggered", "inventory.snapshot.changed", "inventory.catalog.changed"],
        [], null);

    private sealed record State(bool Connected, string? Problem, GameBindingSnapshot? Binding,
        GameInputOwnershipSnapshot? Ownership, OperationSnapshot? OwnerOperation, CombatWorkspace? Workspace,
        IReadOnlyList<CombatEncounter> History, CombatEntityEffects? TargetEffects,
        IReadOnlyList<CombatEffectCoverage> Coverage, bool Simulation,
        IReadOnlyList<CombatDamageEvent>? FirstAttacks = null, CombatRangeSummary? Range = null, string? RangeProblem = null,
        CombatMeasurementState? Measurement = null, long MeasurementAt = 0, OperationSnapshot? Starting = null, string? EffectProblem = null,
        string? RangeKey = null, long EffectsAt = 0);

    /// <summary>两轮例行刷新的最小间隔；倒计时由界面按本机时钟递减，不依赖刷新频率。</summary>
    internal static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>战斗意图等待后端回应的上限；超时后在活动胶囊写明，不在界面上无限等待。</summary>
    internal static readonly TimeSpan CombatRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly string discoveryPath;
    private readonly Action<Action> dispatch;
    private readonly DesktopShortcutNotifications shortcutNotifications;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Lock gate = new();
    private State state = new(false, null, null, null, null, null, [], null, [], false);
    /// <summary>本次握手得到的宿主实际监听地址（发现文件里的地址，可能是系统分配的端口）；未连接时为空。</summary>
    private string? apiAddress;
    private MabiToolClient? client;
    private string? notice;
    // DPS检测页是否显示；切走后不读取只供页面使用的首击时间线。显示时下一轮刷新必定通知页面（清除“更新中”）。
    private bool combatPageVisible, combatPageShown;
    private string? signature;
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

    public void Start() => loop ??= Task.Run(() => RunAsync(lifetime.Token));

    public ActivitySnapshot GetActivity()
    {
        var (current, message) = Read();
        if (!current.Connected)
            return new(null, Pick("未连接后端", "未連接後端"), Pick("正在等待后端启动", "正在等待後端啟動"),
                current.Problem ?? Pick("连接后显示游戏输入的占用状态。", "連接後顯示遊戲輸入的佔用狀態。"),
                EffectStage.Pending, EffectStage.Pending, EffectStage.Pending, false);
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

    public CombatPageData GetCombat()
    {
        var (current, _) = Read();
        var chosen = selection.Snapshot();
        var (scope, range) = (chosen.Scope, chosen.Target);
        // 最近一轮读到的范围结果针对的是另一个选择（刚切换、读取还在路上或迟到的旧结果）：不采用，页面保留上一次的显示并等新结果。
        // 当前分段且不选目标时直接用分段摘要，不依赖范围结果。
        var pending = current.RangeKey is { } key && key != chosen.Key && !(scope == "segment" && range is null);
        var data = CombatMapper.Map(new(current.Connected, current.Binding?.Binding is not null, current.Workspace,
            current.History, current.TargetEffects, current.EffectProblem, current.Coverage, current.FirstAttacks, scope, range,
            pending ? null : current.Range, pending ? null : current.RangeProblem, current.Measurement, current.MeasurementAt, current.EffectsAt))
            with { SelectedScope = scope, SelectedTarget = range, Pending = pending };
        // 开始观测已受理、正在等待识别角色时可取消；失败时写明原因。
        var starting = current.Connected ? StartView(current.Starting, current.Workspace) : null;
        CombatAutoObservationState? auto;
        lock (gate) auto = autoObservation;
        var autoView = current.Connected ? AutoView(auto, current.Workspace) : null;
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
        lock (gate) measurement = state.Measurement;
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
        if (visible) Signal();
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
        Signal();
        Notify();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var handshake = await LocalBootstrapClient.ConnectAsync(discoveryPath, "moongate-desktop", token);
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
                var events = ListenAsync(api, backendEpoch, session.Token);
                try
                {
                    while (!events.IsCompleted)
                    {
                        var round = Stopwatch.StartNew();
                        await RefreshAsync(api, simulation, token);
                        await RefreshColorRunAsync(api, token);
                        await RefreshPagesAsync(api, token);
                        await RefreshExtensionSurfacesAsync(api, token);
                        await RefreshCaptureAsync(api, token);
                        await RefreshRoomAsync(api, token);
                        await PumpScoreDurationsAsync(api, token);
                        await wake.WaitAsync(TimeSpan.FromSeconds(1), token);
                        // 事件唤醒的刷新至少间隔 MinimumRefreshInterval：观测中后端事件很密，不限速时一秒要跑近十轮完整读取。
                        if (round.Elapsed < MinimumRefreshInterval) await Task.Delay(MinimumRefreshInterval - round.Elapsed, token);
                    }
                    await events;
                }
                finally
                {
                    await session.CancelAsync();
                    try { await events; }
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
                else if (message.Event.StartsWith("inventory.", StringComparison.Ordinal))
                {
                    lock (gate) inventoryDue = true;
                    Signal();
                }
                else Signal();
            }
        }
        finally { shortcutNotifications.Disconnect(generation); }
    }

    private async Task RefreshAsync(MabiToolClient api, bool simulation, CancellationToken token)
    {
        // 相互独立的读取并行发出，一轮刷新的等待取最慢的一个而不是逐个相加。
        var bindingRead = api.GetGameBindingAsync(token);
        var ownershipRead = api.GetGameInputOwnershipAsync(token);
        var combatRead = api.GetCombatSnapshotAsync(token);
        var pageRead = api.GetCombatEncountersAsync(token);
        await Task.WhenAll(bindingRead, ownershipRead, combatRead, pageRead);
        var binding = bindingRead.Result;
        var ownership = ownershipRead.Result;
        OperationSnapshot? operation = null;
        // 只有 Operation 标识（op- 加 32 位十六进制）才查询操作；合奏本机准备的占用标识为 room-…，没有对应的 Operation，
        // 按秒查询会得到 404 并写满诊断记录。
        if (ownership.Owners.Count > 0 && IsOperationId(ownership.Owners[0].OperationId))
            try { operation = await api.GetOperationAsync(ownership.Owners[0].OperationId, token); }
            catch (MabiToolApiException) { }

        var combat = combatRead.Result;
        var page = pageRead.Result;
        var history = page.Items;
        IReadOnlyList<CombatEffectCoverage> coverage = [];
        // 分段的时间线在后端写入前查不到：同一分段得到 404 后等它的修订变化再查，不按每轮刷新反复请求。
        if (combat.Data?.Encounter is { } encounter)
        {
            var marker = $"{encounter.Id}|{encounter.Revision}";
            bool skip;
            lock (gate) skip = missingTimeline == marker;
            if (!skip)
                try { coverage = (await api.QueryCombatTimelineAsync(encounter.Id, new(PageSize: 1), token)).EffectCoverage; }
                catch (MabiToolApiException error) { if (error.Status == 404) lock (gate) missingTimeline = marker; }
        }
        var (range, rangeProblem, measurement, measurementAt, chosen) = await RefreshRangeAsync(api, combat.Data, page, token);
        // 怪物效果：按页面同一规则（所选目标，否则正在攻击或排序第一个仍在当前观测中的怪物）取效果目标，只查询当前观测里的实体。
        // 这一轮的效果目标、首击与时间线都按读取范围时的同一个选择，不在中途换成新的选择。
        var (scope, target) = (chosen.Scope, chosen.Target);
        bool visible, shown;
        lock (gate) { visible = combatPageVisible; shown = combatPageShown; combatPageShown = false; }
        var effectTarget = CombatMapper.EffectTargetId(combat.Data, scope, target, range);
        var selected = combat.Data?.Entities.FirstOrDefault(entity => entity.Id == effectTarget && entity.Role.KnownValue == CombatEntityRole.Monster);
        CombatEntityEffects? effects = null;
        string? effectProblem = null;
        if (selected is not null)
            try { effects = await api.GetCombatEntityEffectsAsync(selected.Id, token); }
            catch (MabiToolApiException error) { effectProblem = error.Problem?.Code ?? $"http.{error.Status}"; }
        // 首击只在 DPS检测页显示：页面切走后不读取它的时间线。
        var firstAttacks = visible ? await FirstAttacksAsync(api, combat.Data, range, history, chosen, token) : null;
        await RefreshDetailAsync(api, combat.Data, history, range, rangeProblem, chosen.Key, token);
        var starting = await RefreshStartAsync(api, combat.Data, token);

        // 观测进行中每次刷新都通知（倒计时在走）；其余时候只在修订变化时通知，避免页面无谓重排。
        var next = string.Join('|', combat.ResourceRevision, binding.Revision, binding.State, page.Revision,
            operation?.Revision, effects?.Revision, selected?.Id, effectProblem, coverage.Count, firstAttacks?.Count,
            range?.Result.Revision, range?.Result.Summary.AttributedDamage, range?.TargetId, rangeProblem, chosen.Key, measurement?.Revision, measurement?.Phase, starting?.Revision, AutoSignature(), TimelineSignature(),
            string.Join(',', ownership.Owners.Select(owner => owner.OwnerId + owner.OperationId)));
        bool changed;
        lock (gate)
        {
            changed = !state.Connected || shown || next != signature || combat.Data?.Observation.State == "observing" || measurement?.Phase is "waiting" or "running";
            signature = next;
            state = new(true, null, binding, ownership, operation, combat.Data, history, effects, coverage, simulation, firstAttacks,
                range, rangeProblem, measurement, measurementAt, starting, effectProblem, chosen.Key, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        if (changed) Notify();
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
        });
    }

    /// <summary>界面线程上尚未执行的状态通知个数（0 或 1），供测试观察通知合并。</summary>
    internal int QueuedNotifications => Volatile.Read(ref notifyQueued);
}
