using System.Runtime.Versioning;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.Host.Game;

public sealed partial class GameObservationService(GameControlContext controls, LocalAuthService auth, ActivityBarrier barrier,
    SystemInfo system, TimeProvider clock, Func<WorkerProcessOptions?> options, ModuleRuntime modules, DiagnosticTrace? trace = null,
    GameCaptureSettingsStore? captureSettings = null, bool automation = false) : BackgroundService
{
    private sealed record Demand(string Owner, string Id);
    private sealed record Receipt(string Hash, GameObservationSubscription Value, long Created);
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly object gate = new();
    private readonly Dictionary<string, Demand> demands = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Key), Receipt> receipts = [];
    private readonly Dictionary<string, string> releases = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;
    private WorkerSession? worker;
    private WorkerCall? call;
    private int? workerPid;
    private Task pump = Task.CompletedTask;
    private IDisposable? activity;
    private IAsyncDisposable? moduleDemand;
    private string? captureId;
    private GameSession? game;
    private string? channelId;
    private string? profile;
    private string state = "stopped";
    private string? reason;
    private string? sourceGeneration;
    private ulong sourceSequence;
    private readonly RetiredGenerations retiredGenerations = new();
    private ulong generation;
    private ulong sequence;
    private ulong revision;
    private double lastReportMono = double.NegativeInfinity;
    private long startingAt;
    private Observation<GameObservationValue>? latest;
    public event Action? Changed;

    public static Func<WorkerProcessOptions?> FromEnvironment(PathService paths)
    {
        if (!OperatingSystem.IsWindows()) return () => null;
        var executable = Environment.GetEnvironmentVariable("MABITOOL_OBSERVATION_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable)) return () => null;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("MABITOOL_OBSERVATION_EXECUTABLE 需要存在的绝对路径。");
        LocalDiscoverySecurity.RejectLinks(executable);
        var folder = Path.GetDirectoryName(executable)!;
        var assets = Path.Combine(paths.Temporary, "observation-worker");
        return () =>
        {
            Directory.CreateDirectory(assets);
            return new(executable, folder, assets, "game-observation");
        };
    }
    public GameObservationRuntime Runtime()
    {
        var npcap = NpcapInstallation.Probe();
        lock (gate) return new(options() is not null, state, TotalDemands, workerPid, 8, 32, reason, npcap);
    }
    public GameObservationSnapshot Snapshot()
    {
        lock (gate)
        {
            var value = latest;
            if (value is not null && Mono() - value.MonotonicMs > value.ValidForMs)
                value = value with { Quality = ObservationQuality.Stale };
            return new(system.InstanceId, new(revision), captureId, state, game?.Id, game?.BindingRevision,
                channelId, profile, TotalDemands, value, reason, AutomationSnapshot());
        }
    }
    public static string ETag(GameObservationSnapshot value) => $"\"game-observation-{value.BackendEpoch}-{value.Revision}\"";
    public IReadOnlyList<string> Principals() { lock (gate) return demands.Values.Select(value => value.Owner).ToArray(); }
    public async Task<ApiList<CaptureChannelOption>> ChannelsAsync(string owner, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("observation.workerUnavailable", "观察组件需要 Windows");
        Authorize(owner);
        using var demand = barrier.EnterActivity(owner, "game.observation.channels");
        await using var module = await modules.AcquireAsync("game-access", "observation-channels-" + Guid.NewGuid().ToString("N"), ct);
        await lifecycle.WaitAsync(ct);
        try
        {
            // 捕获运行期间也可枚举：另起短时 Worker，至多 30 秒，不影响常驻捕获。
            var configuration = Configuration();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await using var session = await WorkerSession.StartAsync(configuration, _ => { }, deadline.Token);
            var channels = await QueryChannelsAsync(session, controls.Bindings.Recent?.Session, deadline.Token);
            Authorize(owner);
            return new(channels, null, system.InstanceId);
        }
        catch (WorkerException error) { throw new OperationException("observation.captureFailed", error.Code); }
        finally { lifecycle.Release(); }
    }
    private static async Task DrainAsync(WorkerCall call, CancellationToken ct)
    { await foreach (var _ in call.Events.ReadAllAsync(ct)) { } }
    /// <summary>在给定 Worker 上枚举网卡；有游戏绑定时附带进程身份，Worker 为每张网卡统计游戏数据连接数。</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<CaptureChannelOption[]> QueryChannelsAsync(WorkerSession session, GameSession? game, CancellationToken ct)
    {
        object arguments = game is null || game.Region != "TW" ? new { } : new { processId = game.ProcessId, processStartTime = game.ProcessStartTime };
        var request = await session.BeginAsync(new("observation.channels", JsonSerializer.SerializeToElement(arguments, ContractJson.Options),
            Guid.NewGuid().ToString("N"), "0", "0"), ct);
        var events = DrainAsync(request, ct);
        var result = await request.Completion.WaitAsync(ct);
        await events;
        if (result.Kind != "result") throw Failure(result);
        var channels = result.Payload.GetProperty("channels").Deserialize<CaptureChannelOption[]>(ContractJson.Options)
            ?? throw new OperationException("observation.captureFailed", "观察组件未返回通道");
        if (channels.Length > 128 || channels.Any(value => value is null || !WorkerProtocol.IsId(value.Id) ||
            value.Description is null or { Length: > 512 } || value.Addresses is null || value.Addresses.Count > 32 ||
            value.GameConnections is < 0 or > 32 ||
            value.Addresses.Any(address => !IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)) ||
            channels.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != channels.Length)
            throw new OperationException("observation.captureFailed", "观察组件通道格式无效");
        return channels;
    }

    public async Task<GameObservationSubscription> StartAsync(string owner, GameObservationStartRequest request,
        string? key, string? etag, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("observation.workerUnavailable", "观察组件需要 Windows");
        Authorize(owner);
        if (key is null) throw new OperationException("idempotency.required", "启动观察需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z") || request.ChannelId is not null && !WorkerProtocol.IsId(request.ChannelId))
            throw new OperationException("request.invalid", "观察通道或幂等键无效");
        if (etag is null) throw new OperationException("revision.required", "启动观察需要 If-Match");
        if (!Regex.IsMatch(etag, @"\A""game-observation-[0-9a-f]{32}-[0-9]{1,20}""\z"))
            throw new OperationException("request.invalid", "需要单个强观察 ETag");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, ContractJson.Options) + "\n" + etag)));
        await lifecycle.WaitAsync(ct);
        try
        {
            lock (gate)
            {
                foreach (var expired in receipts.Where(value => clock.GetElapsedTime(value.Value.Created) >= TimeSpan.FromMinutes(5)).Select(value => value.Key).ToArray())
                    receipts.Remove(expired);
                if (receipts.TryGetValue((owner, key), out var old))
                    return old.Hash == hash ? old.Value : throw new OperationException("idempotency.conflict", "幂等键已用于其他观察请求或修订");
                if (etag != ETag(Snapshot())) throw new OperationException("revision.stale", "观察需求已变化，请读取最新状态");
                if (TotalDemands >= 8 && !demands.Values.Any(value => value.Owner == owner) || receipts.Count >= 1000)
                    throw new OperationException("resource.capacityExceeded", "观察需求或回执容量已满");
            }
            var binding = controls.Bindings.Current?.Session ?? throw new OperationException("input.bindingUnavailable", "请先选择游戏窗口");
            if (binding.BindingRevision != request.BindingRevision) throw new OperationException("input.bindingChanged", "游戏绑定修订已变化");
            // 未指定 profile 时按绑定构建选择：当前已核验台服构建使用战斗 profile，音乐、战斗与库存共用同一捕获；
            // 其他构建沿用旧默认音乐配置，由 Worker 的文件指纹核对决定能否启动。
            var requestedProfile = request.Profile ?? (binding.GameBuild == TwClientFingerprint.GameBuild
                ? Tw20261110CombatProtocol.CaptureProfile : MabiTool.Providers.TwObservation.TwGameProtocol.CaptureProfile);
            if (binding.Region != "TW" || requestedProfile is not (MabiTool.Providers.TwObservation.TwGameProtocol.CaptureProfile or Tw20261110CombatProtocol.CaptureProfile))
                throw new OperationException("observation.profileUnavailable", "当前地区或观察协议尚未验证");
            if (requestedProfile == Tw20261110CombatProtocol.CaptureProfile && binding.GameBuild != Tw20261110CombatProtocol.GameBuild)
                throw new OperationException("gameBuild.incompatible", "当前战斗观察需要已核验的台服构建");
            lock (gate)
                if (worker is not null && (!SameGameSession(binding, game) ||
                    request.ChannelId is not null && channelId != request.ChannelId || request.Profile is not null && profile != request.Profile ||
                    state is "stopping" or "faulted"))
                    throw new OperationException("resource.busy", "当前捕获使用其他绑定或通道，请先释放已有需求");
            HoldActivity(owner);
            if (worker is null)
            {
                var settings = captureSettings?.Read() ?? GameCaptureSettings.Default;
                if (request.ChannelId is null && !settings.AutomaticCapture)
                    throw new OperationException("observation.automaticCaptureDisabled", "自动抓包已关闭，请在设置中开启");
                try
                {
                    await LaunchAsync(binding, requestedProfile, request.ChannelId is { } chosen ? (_, _) => Task.FromResult(new CaptureChannelChoice(chosen, chosen, "request"))
                        : (session, token) => ResolveChannelAsync(session, binding, settings, token), ct);
                    resident = automation && request.ChannelId is null && captureSettings is not null;
                }
                catch
                {
                    await StopCoreAsync("observation.captureFailed");
                    throw;
                }
            }
            Authorize(owner);
            if (!SameGameSession(controls.Bindings.Current?.Session, binding))
            {
                await StopCoreAsync("input.bindingChanged");
                throw new OperationException("input.bindingChanged", "捕获启动期间游戏绑定已变化");
            }
            GameObservationSubscription result;
            lock (gate)
            {
                var own = demands.Values.FirstOrDefault(value => value.Owner == owner);
                if (own is null)
                {
                    own = new(owner, Guid.NewGuid().ToString("N")); demands.Add(own.Id, own); revision++;
                }
                result = new(own.Id, captureId!, Snapshot());
                receipts.Add((owner, key), new(hash, result, clock.GetTimestamp()));
                if (pump.IsCompleted)
                {
                    ResetCombatDiagnostics(); ResetInventoryDiagnostics(); ResetCaptureCounters();
                    pump = PumpAsync(worker!, call!, captureId!);
                }
            }
            await ConfigureInterestsAsync(ct); Notify(); return result;
        }
        catch (WorkerException error) { throw new OperationException("observation.captureFailed", error.Code); }
        finally { lifecycle.Release(); }
    }
    public async Task<GameObservationRelease> ReleaseAsync(string owner, bool hostOwner, string id, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            bool released;
            lock (gate)
            {
                released = demands.TryGetValue(id, out var demand);
                var principal = demand?.Owner ?? releases.GetValueOrDefault(id);
                if (principal is not null && principal != owner && !hostOwner)
                    throw new OperationException("permission.denied", "该观察需求属于其他客户端");
                if (released)
                {
                    demands.Remove(id); if (releases.Count >= 1000) releases.Remove(releases.Keys.First());
                    releases[id] = demand!.Owner; revision++;
                }
                else released = principal is not null;
            }
            if (TotalDemands == 0 && worker is not null && !resident) await StopCoreAsync(null);
            else if (worker is not null) { await ConfigureInterestsAsync(ct); ReleaseIdleActivity(); }
            Notify(); return new(id, released, Snapshot());
        }
        finally { lifecycle.Release(); }
    }
    private async Task PumpAsync(WorkerSession session, WorkerCall request, string id)
    {
        try
        {
            await foreach (var frame in request.Events.ReadAllAsync(shutdown.Token))
            {
                if (frame.Kind == "accepted") continue;
                // 诊断计数不属于观察帧，不更新到达间隔与上一帧时点。
                if (AcceptCaptureDiagnostics(frame, session, id)) continue;
                RecordArrival();
                if (AcceptCombatEvent(frame)) continue;
                if (OperatingSystem.IsWindows() && await AcceptInventoryEventAsync(frame, shutdown.Token)) continue;
                if (frame.Payload.EnumerateObject().Count() != 1 || !frame.Payload.TryGetProperty("report", out var value))
                    throw new WorkerException("worker.frameInvalid");
                var report = value.Deserialize<GameCaptureReport>(ContractJson.Options) ?? throw new WorkerException("worker.frameInvalid");
                Validate(report);
                lock (gate)
                {
                    if (worker != session || captureId != id || state == "stopping") continue;
                    if (report.MonotonicMs < lastReportMono || report.MonotonicMs > Mono() + 200 || Mono() - report.MonotonicMs > 2000)
                        throw new WorkerException("worker.sequenceInvalid");
                    if (sourceGeneration != report.Value.CaptureGeneration)
                    {
                        if (retiredGenerations.Contains(report.Value.CaptureGeneration))
                            throw new WorkerException("worker.sequenceInvalid");
                        if (sourceGeneration is not null) retiredGenerations.Retire(sourceGeneration);
                        sourceGeneration = report.Value.CaptureGeneration; sourceSequence = 0; generation++;
                    }
                    if (report.Sequence.Value < sourceSequence) throw new WorkerException("worker.sequenceInvalid");
                    sourceSequence = report.Sequence.Value;
                    lastReportMono = report.MonotonicMs;
                    if (report.Value.Identity is { } identity && game is { } current) lastIdentity = (current.Id, identity);
                    var source = report.Source ?? new ObservationSource("unknown-observation", "0.0.0", "TW", null);
                    latest = report.Running ? new(report.Value, source, report.ObservedAt, system.InstanceId, report.MonotonicMs, 2000,
                        new(generation), new(++sequence), report.Value.Identity is null || report.Source is null ? ObservationQuality.Unknown :
                            report.Value.Music is not null ? ObservationQuality.Estimated : ObservationQuality.Confirmed) : null;
                    if (state != "running") revision++;
                    state = report.Running ? "running" : "faulted"; reason = report.Reason;
                }
                Notify();
            }
            var terminal = await request.Completion;
            if (terminal.Kind == "error") OnFault(id, Failure(terminal).Code);
            else OnFault(id, "observation.sourceEnded");
        }
        catch (Exception error) when (error is WorkerException or OperationException or JsonException or OperationCanceledException or IOException)
        { OnFault(id, error is WorkerException failure ? failure.Code : error is OperationException operation ? operation.Code : "observation.captureFailed"); }
    }
    private void OnFault(string id, string code)
    {
        WorkerSession? faulted;
        lock (gate)
        {
            if (captureId != id || state == "stopping") return;
            faulted = worker;
            state = "faulted"; reason = code; latest = null; revision++; RecordFault(code);
            interestAcknowledgement?.TrySetException(new WorkerException(code));
            foreach (var subscription in combatDemands.Values) subscription.Complete(new OperationException("combat.observationGap", code));
            foreach (var subscription in inventoryDemands.Values) subscription.Complete(new OperationException("inventory.observationGap", code));
        }
        trace?.Emit(DiagnosticEvents.WorkerFaulted, new DiagnosticFields().Value("reason", code), code: code, detail: OperatingSystem.IsWindows() ? faulted?.DiagnosticTail : null);
        Notify();
    }
    private async Task StopCoreAsync(string? stoppedReason, bool fault = true)
    {
        WorkerSession? old;
        WorkerCall? oldCall;
        string? priorFault;
        var wasResident = false;
        lock (gate)
        {
            wasResident = resident;
            priorFault = state == "faulted" ? reason ?? "observation.captureFailed" : null;
            old = worker; oldCall = call; state = "stopping"; latest = null; revision++;
            foreach (var demand in demands.Values)
            { if (releases.Count >= 1000) releases.Remove(releases.Keys.First()); releases[demand.Id] = demand.Owner; }
            demands.Clear();
        }
        try
        {
            if (old is not null && OperatingSystem.IsWindows())
            {
                if (oldCall is not null) try { await old.CancelAsync(oldCall); }
                    catch (Exception error) when (error is WorkerException or IOException or OperationCanceledException)
                    { stoppedReason ??= error is WorkerException typed ? typed.Code : "observation.captureFailed"; }
                await old.DisposeAsync();
            }
            await pump.ConfigureAwait(false);
            if (OperatingSystem.IsWindows() && old is not null) InventoryWorkerAssets.CleanDirectory(Configuration().AssetRoot);
        }
        finally
        {
            try { if (moduleDemand is not null) await moduleDemand.DisposeAsync(); }
            finally { moduleDemand = null; activity?.Dispose(); activity = null; }
            lock (gate)
            {
                worker = null; workerPid = null; call = null; captureId = null; game = null; channelId = null; profile = null; resident = false; autoChoice = null;
                state = stoppedReason is null || !fault ? "stopped" : "faulted"; reason = stoppedReason; revision++;
                if (stoppedReason is not null && fault) RecordFault(stoppedReason, stop: true);
                if (old is not null && OperatingSystem.IsWindows()) RecordWorkerEnd(priorFault ?? stoppedReason, old.ExitCode, old.TerminatedByHost);
                foreach (var subscription in combatDemands.Values) subscription.Complete(stoppedReason is null ? null : new OperationException("combat.observationGap", stoppedReason));
                foreach (var subscription in inventoryDemands.Values) subscription.Complete(stoppedReason is null ? null : new OperationException("inventory.observationGap", stoppedReason));
                combatDemands.Clear(); inventoryDemands.Clear(); inventoryProfile = null;
                appliedInterests = new(true, null); interestAcknowledgement?.TrySetCanceled();
            }
            if (old is not null && OperatingSystem.IsWindows())
            {
                var end = priorFault ?? stoppedReason;
                trace?.Emit(DiagnosticEvents.WorkerStopped, new DiagnosticFields().Value("end", WorkerEndKind(end))
                    .Count("exitCode", old.ExitCode is { } exit ? unchecked((uint)exit) : 0UL).Flag("terminatedByHost", old.TerminatedByHost),
                    code: end, detail: end is not null ? old.DiagnosticTail : null);
                trace?.Emit(DiagnosticEvents.CaptureEnded, new DiagnosticFields().Value("reason", end ?? "normal"), code: end);
            }
            if (wasResident && fault && (priorFault ?? stoppedReason) is { } failed && automation) ScheduleRetry(failed);
            Notify();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), clock, stoppingToken);
            await lifecycle.WaitAsync(stoppingToken);
            try
            {
                string? failure;
                var changed = false;
                lock (gate)
                {
                    foreach (var item in demands.Values.Where(value => !auth.HasPermission(value.Owner, ApiPermissions.GameObserve)).ToArray())
                    { demands.Remove(item.Id); revision++; changed = true; }
                    if (latest is not null && latest.Quality.KnownValue != ObservationQuality.Stale && Mono() - latest.MonotonicMs > latest.ValidForMs)
                    { latest = latest with { Quality = ObservationQuality.Stale }; changed = true; }
                    failure = worker is null ? null : state == "faulted" ? reason ?? "observation.captureFailed" :
                        !SameGameSession(controls.Bindings.Current?.Session, game) ? "input.bindingChanged" :
                        state == "starting" && clock.GetElapsedTime(startingAt) >= TimeSpan.FromSeconds(30) ? "observation.readyTimeout" : null;
                }
                await ReleaseUnauthorizedCombatAsync();
                await ReleaseUnauthorizedInventoryAsync();
                if (worker is not null && (failure is not null || TotalDemands == 0 && !resident)) await StopCoreAsync(failure);
                else if (changed) { await ConfigureInterestsAsync(stoppingToken); ReleaseIdleActivity(); Notify(); }
                await AutomateAsync(stoppingToken);
            }
            finally { lifecycle.Release(); }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        shutdown.Cancel(); await base.StopAsync(cancellationToken);
        Task running; lock (gate) running = detectionTask;
        await running.ConfigureAwait(false);
        await lifecycle.WaitAsync(CancellationToken.None);
        try { if (worker is not null) await StopCoreAsync(null); }
        finally { lifecycle.Release(); }
    }
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel(); base.Dispose(); shutdown.Dispose();
    }
    /// <summary>启动环境声明本宿主受理 captureDiagnostics 事件；旧 Worker 忽略该变量，不发送计数。</summary>
    private WorkerProcessOptions Configuration()
    {
        var configured = options() ?? throw new OperationException("observation.workerUnavailable", "请安装并配置可选游戏观测组件");
        return configured with { EnvironmentVariables = new Dictionary<string, string>(configured.EnvironmentVariables, StringComparer.Ordinal)
            { [ObservationCaptureCounters.EnvironmentVariable] = "1" }, Trace = trace, DiagnosticModule = "game-access" };
    }
    private void Authorize(string principal)
    { if (!auth.HasPermission(principal, ApiPermissions.GameObserve)) throw new OperationException("permission.denied", "需要独立游戏抓包权限"); }
    private static OperationException Failure(WorkerFrame frame)
    {
        var code = frame.Payload.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new(code is not null && ApiErrors.Definitions.Any(value => value.Code == code) ? code : "observation.captureFailed", "游戏观测组件未完成请求");
    }
    /// <summary>
    /// 抓包、观察 Worker 与各兴趣只读网络数据，归属按游戏会话判定（进程 ID、启动时间、地区、构建，见
    /// <see cref="GameSessionBindingTracker"/>）。窗口移动、缩放、换显示器、DPI、最小化与恢复只递增绑定修订，
    /// 不结束捕获，已识别的本机身份与已同步的背包、战斗状态因此保留；输入与截图仍按修订核对。
    /// </summary>
    internal static bool SameGameSession(GameSession? current, GameSession? captured) =>
        current is not null && captured is not null && current.Id == captured.Id;
    private double Mono() => clock.GetTimestamp() * (1000d / clock.TimestampFrequency);
    private void Notify() => Changed?.Invoke();

    private static void Validate(GameCaptureReport report)
    {
        static bool Text(string? value, int limit, bool empty = false) => value is not null && value.Length <= limit &&
            (empty || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);
        static bool Identity(ObservedGameIdentity? value) => value is not null && value.CharacterId.Value != 0 && value.ActorId.Value != 0 &&
            value.RealmId is not null && int.TryParse(value.RealmId, NumberStyles.None, CultureInfo.InvariantCulture, out var realm) &&
            realm >= 0 && realm.ToString(CultureInfo.InvariantCulture) == value.RealmId && Text(value.Nickname, 64);
        static bool Duration(double? value) => value is null || double.IsFinite(value.Value) && value.Value is >= 0 and <= 604800;
        var value = report.Value;
        if (value is null || !WorkerProtocol.IsId(value.CaptureGeneration) || report.Sequence.Value == 0 ||
            !double.IsFinite(report.MonotonicMs) || report.MonotonicMs < 0 || report.ObservedAt == default ||
            report.ObservedAt.Offset != TimeSpan.Zero || !Text(value.Detail, 1024, true) ||
            report.Reason is not null && !Text(report.Reason, 128) || value.Identity is not null && !Identity(value.Identity))
            throw new WorkerException("worker.frameInvalid");
        if (value.Location is { } location && (location.ChannelSheetId == 0 || location.MapName is not null && !Text(location.MapName, 128) ||
            location.MapReason is not null && !Text(location.MapReason, 64) || location.ChannelReason is not null && !Text(location.ChannelReason, 64) ||
            (location.MapName is null) == (location.MapReason is null) || location.PermanentChannelIndex is < 0 or > 9999))
            throw new WorkerException("worker.frameInvalid");
        if (report.Source is { } source && (!Text(source.Provider, 128) || !Text(source.ProviderVersion, 64) || source.Region != "TW" ||
            source.GameBuild is not null && !Text(source.GameBuild, 256))) throw new WorkerException("worker.frameInvalid");
        if (value.Concert is { } concert)
        {
            if (concert.Id.Value == 0 || concert.HostCharacterId.Value == 0 || concert.Members is null ||
                concert.Members.Count is < 1 or > 128 || concert.Members[0].CharacterId != concert.HostCharacterId ||
                concert.Members.Any(member => !Identity(member)) || concert.Members.Select(member => member.CharacterId).Distinct().Count() != concert.Members.Count ||
                value.Identity is null || !concert.Members.Any(member => member.CharacterId == value.Identity.CharacterId) ||
                concert.Members.Any(member => member.RealmId != value.Identity.RealmId) ||
                concert.OrderedCharacterIds is { } ordered && !ordered.SequenceEqual(concert.Members.Select(member => member.CharacterId)))
                throw new WorkerException("worker.frameInvalid");
        }
        if (value.Music is { } music && (value.Identity is null || music.ConcertId.Value == 0 || music.HostCharacterId.Value == 0 ||
            music.PerformerCharacterId != value.Identity.CharacterId || !Text(music.Title, 512, true) ||
            music.SelectedChannels is null || music.SelectedChannels.Count > 128 || music.SelectedChannels.Any(index => index is < 0 or >= 128) ||
            music.SelectedChannels.Distinct().Count() != music.SelectedChannels.Count ||
            music.ScoreSha256 is not null && (music.ScoreSha256.Length != 64 || !music.ScoreSha256.All(char.IsAsciiHexDigit)) ||
            !Duration(music.WholeScoreDurationSeconds) || !Duration(music.SelectedDurationSeconds) ||
            music.SelectedDurationSeconds is { } selected && (music.WholeScoreDurationSeconds is null || selected > music.WholeScoreDurationSeconds + .000001) ||
            music.EstimatedStartMonotonicMs is { } start && !double.IsFinite(start) ||
            !Text(music.TimingSource, 64, true) || music.PlaybackTimingVerified || music.Issue is not null && !Text(music.Issue, 256, true)))
            throw new WorkerException("worker.frameInvalid");
    }
}

/// <summary>已退役观察代次：至多保留 <see cref="Maximum"/> 个，超出时淘汰最早的一个，常驻捕获期间反复切图与重同步不会耗尽。</summary>
internal sealed class RetiredGenerations
{
    public const int Maximum = 256;
    private readonly HashSet<string> values = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    public int Count => values.Count;
    public bool Contains(string value) => values.Contains(value);
    public void Retire(string value)
    {
        if (!values.Add(value)) return;
        order.Enqueue(value);
        if (order.Count > Maximum) values.Remove(order.Dequeue());
    }
    public void Clear() { values.Clear(); order.Clear(); }
}
