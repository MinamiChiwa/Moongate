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
    /// <summary>最近一份状态报告确认的本机身份及其游戏会话；捕获停止后保留，供同一会话的下一次 Worker 启动下发。</summary>
    private (string SessionId, ObservedGameIdentity Identity)? lastIdentity;
    private CaptureChannelChoice? autoChoice;
    private string autoState = "off";
    private string? autoReason;
    private bool? lastAutomaticCapture;
    private (string SessionId, ulong Settings)? attemptKey;
    private int attemptFailures;
    private long? nextAttemptAt;
    private DateTimeOffset? nextAttemptUtc;
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    /// <summary>常驻捕获期间（含模块需求为零）是否由宿主持有。</summary>
    public bool Resident { get { lock (gate) return resident; } }

    private GameCaptureAutomation? AutomationSnapshot()
    {
        if (!automation || captureSettings is null) return null;
        var enabled = captureSettings.Read().AutomaticCapture;
        var choice = autoChoice;
        return new(enabled, autoState, autoReason, choice?.ChannelId ?? channelId, choice?.Description, choice?.Source, nextAttemptUtc);
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
        if (changed) Notify();
    }
    private int BlockingDemands => demands.Count + inventoryDemands.Values.Count(value => !value.Resident) + combatDemands.Values.Count(value => !value.Resident);
    /// <summary>手动模块需求占用更新活动屏障；只剩常驻捕获与自动 DPS 时释放。</summary>
    private void HoldActivity(string owner) { if (activity is null) activity = barrier.EnterActivity(owner, "game.observation"); }
    private void ReleaseIdleActivity()
    {
        if (BlockingDemands != 0 || !resident || activity is null) return;
        activity.Dispose(); activity = null;
    }

    private static async Task<CaptureChannelChoice> ResolveChannelAsync(WorkerSession session, GameSession binding, GameCaptureSettings settings, CancellationToken ct)
        => OperatingSystem.IsWindows() ? CaptureChannelSelection.Select(await QueryChannelsAsync(session, binding, ct), settings)
            : throw new OperationException("observation.workerUnavailable", "观察组件需要 Windows");

    /// <summary>启动捕获 Worker，在同一进程内先确定网卡再开始捕获；调用方持有 lifecycle，失败由调用方停止。</summary>
    [SupportedOSPlatform("windows")]
    private async Task LaunchAsync(GameSession binding, string requestedProfile,
        Func<WorkerSession, CancellationToken, Task<CaptureChannelChoice>> resolve, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
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
        // Worker 重启（故障、维护后恢复）时随捕获请求下发同一游戏会话最近确认的本机身份，作为跨连接沿用的初值；
        // 游戏会话按进程 ID 与启动时间区分，进程重启即新会话，不下发。
        ObservedGameIdentity? known;
        lock (gate) known = lastIdentity is { } remembered && remembered.SessionId == binding.Id ? remembered.Identity : null;
        var arguments = known is null
            ? JsonSerializer.SerializeToElement(new { gameSessionId = binding.Id, region = binding.Region,
                processId = binding.ProcessId, processStartTime = binding.ProcessStartTime,
                channelId = choice.ChannelId, profile = requestedProfile, anyAddress = choice.AnyAddress }, ContractJson.Options)
            : JsonSerializer.SerializeToElement(new { gameSessionId = binding.Id, region = binding.Region,
                processId = binding.ProcessId, processStartTime = binding.ProcessStartTime,
                channelId = choice.ChannelId, profile = requestedProfile, anyAddress = choice.AnyAddress, localIdentity = known }, ContractJson.Options);
        call = await session.BeginAsync(new("observation.capture", arguments, id, binding.BindingRevision.ToString(), "0"), deadline.Token);
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
        if (worker is not null)
        {
            lock (gate) if (!resident && state is "running" or "starting") resident = true;
            SetAutomation(state == "running" ? "running" : "starting", null); return;
        }
        if (DetectionRunning) { SetAutomation("waiting", "observation.channelDetecting"); return; }
        var binding = controls.Bindings.Recent?.Session;
        if (binding is null) { SetAutomation("waiting", "input.bindingUnavailable"); return; }
        if (binding.Region != "TW" || binding.GameBuild != TwClientFingerprint.GameBuild) { SetAutomation("waiting", "gameBuild.incompatible"); return; }
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
            await StopCoreAsync(code);
            ScheduleRetry(code);
        }
    }
    /// <summary>自动启动失败或常驻捕获故障后按退避时间再试；游戏会话或设置修订变化时由 <see cref="AutomateAsync"/> 立即重试。</summary>
    private void ScheduleRetry(string code)
    {
        var delay = RetryDelays[Math.Min(attemptFailures, RetryDelays.Length - 1)];
        attemptFailures++;
        SetAutomation("failed", code);
        lock (gate) { nextAttemptAt = clock.GetTimestamp() + (long)(delay.TotalSeconds * clock.TimestampFrequency); nextAttemptUtc = clock.GetUtcNow() + delay; revision++; }
        Notify();
    }
}
