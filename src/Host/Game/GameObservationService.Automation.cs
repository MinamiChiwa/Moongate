using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.Host.Game;

/// <summary>
/// 常驻抓包：开启“自动抓包”后，绑定到已核验的台服构建且能确定网卡时由宿主自行启动共享捕获并保持运行，
/// 不等待模块需求；模块仍以需求/兴趣登记要解码的数据。游戏会话结束或更换时停止，新会话绑定后重新启动；窗口几何变化不影响捕获。
/// 失败按 5、15、30、60 秒退避重试，游戏会话或设置修订变化时立即重试。关闭开关时停止捕获且不再自动启动。
/// </summary>
public sealed partial class GameObservationService
{
    private bool resident;
    /// <summary>
    /// 最近一份状态报告确认的本机身份及其游戏进程（进程 ID、启动时间、地区）；捕获停止或绑定重建后保留，
    /// 供同一进程的下一次捕获启动下发。
    /// </summary>
    private (int ProcessId, DateTimeOffset StartedAt, string Region, ObservedGameIdentity Identity, ObservedConnection? Connection)? lastIdentity;
    private static bool SameProcess((int ProcessId, DateTimeOffset StartedAt, string Region, ObservedGameIdentity Identity, ObservedConnection? Connection) remembered, GameSession game) =>
        remembered.ProcessId == game.ProcessId && remembered.StartedAt == game.ProcessStartTime && remembered.Region == game.Region;
    private CaptureChannelChoice? autoChoice;
    private string autoState = "off";
    private string? autoReason;
    private bool? lastAutomaticCapture;
    private (string SessionId, ulong Settings)? attemptKey;
    private int attemptFailures;
    private long? nextAttemptAt;
    private DateTimeOffset? nextAttemptUtc;
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    /// <summary>运行了一段时间（至少 <see cref="QuickRetryAfterMs"/>）后才故障的捕获，首次重试的间隔。</summary>
    internal static readonly TimeSpan QuickRetryDelay = TimeSpan.FromSeconds(2);
    internal static double QuickRetryAfterMs { get; set; } = 30_000;

    /// <summary>常驻捕获期间（含模块需求为零）是否由宿主持有。</summary>
    public bool Resident { get { lock (gate) return resident; } }

    private GameCaptureAutomation? AutomationSnapshot()
    {
        if (!automation || captureSettings is null) return null;
        var enabled = captureSettings.Read().AutomaticCapture;
        var choice = autoChoice;
        return new(enabled, autoState, autoReason, choice?.ChannelId ?? channelId, choice?.Description, choice?.Source, nextAttemptUtc,
            BuildCheck(controls.Bindings.LastKnown?.Session), state == "running" ? PrimarySourceKind(captureCounters) : null);
    }
    /// <summary>快照里的构建核对摘要；按最近一次已刷新的绑定读取，不查询窗口或客户端文件。</summary>
    private GameBuildCheck? BuildCheck(GameSession? binding)
    {
        if (controls.Simulation || !GameBuildAdaptation.Recognized(binding)) return null;
        var verdict = Builds.Verdict(binding);
        if (verdict is null) return new(Builds.Verifying(binding) ? "verifying" : "failed", []);
        if (verdict.Registered) return new("registered", []);
        var disabled = new[] { ("identity", verdict.Identity), ("music", verdict.Music), ("combat", verdict.Combat) }
            .Where(value => !value.Item2.Enabled).Select(value => new GameBuildCapabilityCheck(value.Item1, value.Item2.Reason ?? TwBuildVerification.VerifyFailed)).ToArray();
        return new(verdict.CaptureUsable ? "adapted" : "failed", disabled);
    }
    private void SetAutomation(string next, string? code)
    {
        bool changed;
        lock (gate)
        {
            changed = autoState != next || autoReason != code;
            if (changed) { autoState = next; autoReason = code; revision++; }
            if (next is "running" or "starting" or "off" or "waiting") nextAttemptUtc = null;
        }
        if (changed)
        {
            TraceAutomation(next, code);
            Notify();
        }
    }
    /// <summary>
    /// 手动需求数（常驻库存与自动 DPS 订阅不计入）：决定更新活动屏障，也决定非常驻捕获何时停止——手动需求全部释放后停止，
    /// 跟随捕获的常驻订阅随之结束，不让自动 DPS 把已关闭自动抓包时手动开始的捕获一直留着。
    /// </summary>
    private int BlockingDemands => demands.Count + inventoryDemands.Values.Count(value => !value.Resident) + combatDemands.Values.Count(value => !value.Resident);
    /// <summary>手动模块需求占用更新活动屏障；只剩常驻捕获与自动 DPS 时释放。</summary>
    private void HoldActivity(string owner) { if (activity is null) activity = barrier.EnterActivity(owner, "game.observation"); }
    private void ReleaseIdleActivity()
    {
        if (BlockingDemands != 0 || !resident || activity is null) return;
        activity.Dispose(); activity = null;
    }

    /// <summary>
    /// 按设置确定抓包设备。自动模式不选网卡（Worker 打开全部设备，不读取设置里留下的网卡）；指定模式先在同一 Worker 内枚举网卡，
    /// 保存的网卡不在时改为全部设备同时抓（来源 fallback，设置不改写），并在诊断事件 observation.channel.selected 记下 savedChannelMissing；
    /// 成功时的打开结果由运行后的计数写出。
    /// </summary>
    private async Task<CaptureChannelChoice> ResolveChannelAsync(WorkerSession session, GameSession binding, GameCaptureSettings settings, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("observation.workerUnavailable", "观察组件需要 Windows");
        if (settings.ChannelMode != GameCaptureSettings.ManualChannel) return CaptureChannelChoice.Automatic;
        try { return CaptureChannelSelection.Select(await QueryChannelsAsync(session, binding, ct), settings); }
        catch (OperationException error)
        {
            trace?.Emit(DiagnosticEvents.ChannelSelected, new DiagnosticFields().Value("source", "saved").Value("reason", error.Code));
            if (error.Code == "observation.savedChannelMissing") return CaptureChannelChoice.Fallback(settings.ChannelId);
            throw;
        }
    }

    /// <summary>启动捕获 Worker，在同一进程内先确定网卡再开始捕获；调用方持有 lifecycle，失败由调用方停止。</summary>
    [SupportedOSPlatform("windows")]
    private async Task LaunchAsync(GameSession binding, string requestedProfile,
        Func<WorkerSession, CancellationToken, Task<CaptureChannelChoice>> resolve, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
        // 含 Worker 启动握手（至多 60 秒）与确定网卡。
        deadline.CancelAfter(ChannelListTimeout);
        moduleDemand = await modules.AcquireAsync("game-access", "observation-" + id, deadline.Token);
        var configuration = Configuration();
        InventoryWorkerAssets.CleanDirectory(configuration.AssetRoot);
        var session = await WorkerSession.StartAsync(configuration, failure => OnFault(id, failure.Code), deadline.Token);
        lock (gate)
        {
            workerStarts++;
            worker = session; workerPid = session.ProcessId; captureId = id; game = binding; profile = requestedProfile;
            state = "starting"; reason = null; latest = null; sourceGeneration = null; sourceSequence = 0; sequence = 0;
            retiredGenerations.Clear();
            lastReportMono = double.NegativeInfinity; revision++;
            startingAt = clock.GetTimestamp();
        }
        trace?.Emit(DiagnosticEvents.WorkerStarted, new DiagnosticFields().Count("pid", session.ProcessId));
        var choice = await resolve(session, deadline.Token);
        lock (gate) { channelId = choice.ChannelId; autoChoice = choice.Source == "request" ? null : choice; revision++; }
        trace?.Emit(DiagnosticEvents.CaptureStarted, new DiagnosticFields().Value("profile", requestedProfile));
        // 捕获重启（Worker 故障、维护后恢复、改网卡、同一进程重新绑定）时随捕获请求下发同一游戏进程最近确认的本机身份，
        // 作为中途接入时沿用的初值；进程重启即不同进程，不下发。
        ObservedGameIdentity? known; ObservedConnection? knownConnection;
        var carriedEntities = CombatCarry.For(binding);
        lock (gate)
        {
            known = lastIdentity is { } remembered && SameProcess(remembered, binding) ? remembered.Identity : null;
            knownConnection = known is null ? null : lastIdentity!.Value.Connection;
            carriedIdentitySent = known is not null;
        }
        var arguments = known is null
            ? JsonSerializer.SerializeToElement(new { gameSessionId = binding.Id, region = binding.Region,
                processId = binding.ProcessId, processStartTime = binding.ProcessStartTime,
                channelId = choice.ChannelId, profile = requestedProfile }, ContractJson.Options)
            : JsonSerializer.SerializeToElement(new { gameSessionId = binding.Id, region = binding.Region,
                processId = binding.ProcessId, processStartTime = binding.ProcessStartTime,
                channelId = choice.ChannelId, profile = requestedProfile, localIdentity = known, localIdentityConnection = knownConnection }, ContractJson.Options);
        // 实体延续记录只在有记录时附加：旧 Worker 按未知成员拒绝该字段。
        if (carriedEntities is not null)
        {
            var extended = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())!.AsObject();
            extended["combatEntities"] = JsonSerializer.SerializeToNode(carriedEntities, ContractJson.Options);
            arguments = JsonSerializer.SerializeToElement(extended, ContractJson.Options);
        }
        call = await session.BeginAsync(new("observation.capture", arguments, id, binding.BindingRevision.ToString(), "0"), deadline.Token);
        lock (gate) CaptureRequested();
    }

    private async Task AutomateAsync(CancellationToken ct)
    {
        ReleaseIdleActivity();
        if (!OperatingSystem.IsWindows() || !automation || captureSettings is null || controls.Simulation) return;
        var settings = captureSettings.Read();
        var wasEnabled = lastAutomaticCapture; lastAutomaticCapture = settings.AutomaticCapture;
        if (!settings.AutomaticCapture)
        {
            // 关闭开关：停止当前捕获（含模块需求），此后不再自动启动。
            if (wasEnabled == true && worker is not null) await StopCoreAsync("observation.automaticCaptureDisabled", fault: false);
            SetAutomation("off", "observation.automaticCaptureDisabled"); return;
        }
        if (barrier.Snapshot().MaintenanceOwner is not null)
        {
            if (worker is not null && BlockingDemands == 0 && resident) await StopCoreAsync("update.maintenanceActive", fault: false);
            SetAutomation("waiting", "update.maintenanceActive"); return;
        }
        CaptureChannelChoice? current;
        lock (gate) current = worker is not null && resident ? autoChoice : null;
        // 运行中改了网卡设置（指定网卡、换一张、切回自动）：按受控停止结束当前捕获，随后在本轮按新设置重新启动；模块订阅以观察缺口结束后照常重新登记。
        if (current is not null && !Matches(current, settings)) await StopCoreAsync(ChannelChanged, fault: false);
        if (worker is not null)
        {
            lock (gate) if (!resident && state is "running" or "starting") resident = true;
            SetAutomation(state == "running" ? "running" : "starting", state == "running" ? RunningHint : null); return;
        }
        if (DetectionRunning) { SetAutomation("waiting", "observation.channelDetecting"); return; }
        var binding = controls.Bindings.Recent?.Session;
        if (binding is null) { SetAutomation("waiting", "input.bindingUnavailable"); return; }
        // 记录本会话的构建指纹摘要；未登记构建先在后台核对客户端，核对中等待，读不到客户端或分帧所需的协议表时不启动。
        var checkedBuild = Builds.Ensure(binding);
        if (!GameBuildAdaptation.Recognized(binding)) { SetAutomation("waiting", "gameBuild.incompatible"); return; }
        if (checkedBuild is not { } verdict)
        { SetAutomation("waiting", Builds.Verifying(binding) ? "gameBuild.verifying" : TwBuildVerification.VerifyFailed); return; }
        if (!verdict.CaptureUsable) { SetAutomation("waiting", verdict.Identity.Reason ?? "gameBuild.incompatible"); return; }
        if (options() is null) { SetAutomation("waiting", "observation.workerUnavailable"); return; }
        var key = (binding.Id, settings.Revision.Value);
        if (attemptKey != key) { attemptKey = key; attemptFailures = 0; nextAttemptAt = null; }
        if (nextAttemptAt is { } at && clock.GetTimestamp() < at) return;
        SetAutomation("starting", null);
        try
        {
            await LaunchAsync(binding, Tw20261110CombatProtocol.CaptureProfile, (session, token) => ResolveChannelAsync(session, binding, settings, token), ct);
            lock (gate)
            {
                resident = true; nextAttemptAt = null;
                if (pump.IsCompleted)
                {
                    ResetCombatDiagnostics(); ResetInventoryDiagnostics(); ResetCaptureCounters();
                    pump = PumpAsync(worker!, call!, captureId!);
                }
            }
            attemptFailures = 0;
            await ConfigureInterestsAsync(ct);
            SetAutomation("starting", null);
        }
        catch (Exception error) when (error is OperationException or WorkerException or ModuleRuntimeException or IOException ||
            error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            var code = error switch
            {
                OperationException value => value.Code, WorkerException value => value.Code,
                ModuleRuntimeException value => value.Code, _ => "observation.captureFailed"
            };
            await StopCoreAsync(code, stoppedKind: code == "observation.captureFailed" ? "host.launch" : null);
            ScheduleRetry(code);
        }
    }
    internal const string ChannelChanged = "observation.channelChanged";
    /// <summary>当前捕获的设备选择是否仍符合设置：自动模式为全部设备，指定模式为保存的那张网卡（或它不存在时改用的全部设备）。</summary>
    private static bool Matches(CaptureChannelChoice choice, GameCaptureSettings settings) => settings.ChannelMode == GameCaptureSettings.ManualChannel
        ? choice.Source == "saved" && choice.ChannelId == settings.ChannelId || choice.Source == "fallback" && choice.Missing == settings.ChannelId
        : choice.Source == "automatic";
    /// <summary>自动启动失败或常驻捕获故障后按退避时间再试；游戏会话或设置修订变化时由 <see cref="AutomateAsync"/> 立即重试。</summary>
    private void ScheduleRetry(string code, bool quick = false)
    {
        // 运行满 QuickRetryAfterMs 后才故障：首次 2 秒重试，之后的连续失败从 15 秒起按原规则退避。
        var delay = quick ? QuickRetryDelay : RetryDelays[Math.Min(attemptFailures, RetryDelays.Length - 1)];
        attemptFailures = quick ? 1 : attemptFailures + 1;
        SetAutomation("failed", code);
        lock (gate) { nextAttemptAt = clock.GetTimestamp() + (long)(delay.TotalSeconds * clock.TimestampFrequency); nextAttemptUtc = clock.GetUtcNow() + delay; revision++; }
        Notify();
    }
}
