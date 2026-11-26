using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.GameCli;

/// <summary>
/// 游戏连接器：台服命令行工具的启用状态、连接状态与指令目录，以及启用/关闭/恢复的偏好写入。
/// 状态由本机文件与进程核对、命令行 <c>status</c> 与目录推出；未启用时不调用命令行。
/// 能力表与乐谱可演奏性读取同一份快照，写操作在单一命令门内串行。
/// </summary>
public sealed class GameConnectorService : BackgroundService
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(15), LoopInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumReceipts = 256;
    private static readonly string[] Actions = ["enable", "disable", "restore", "refresh", "set-directory"];

    private sealed record CliStatus(OpenEnum<GameConnectorStatus> Status, string Reason);
    private sealed record Receipt(string Hash, GameConnectorSnapshot Snapshot, long Created);

    private readonly GameControlContext controls;
    private readonly SystemInfo info;
    private readonly GameConnectorSettingsStore settings;
    private readonly GameConnectorEnvironment environment;
    private readonly TimeProvider clock;
    private readonly DiagnosticTrace? trace;
    private readonly TwCliFiles files;
    private readonly MmPlayerPrefs? prefs;
    private readonly object gate = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1), commandGate = new(1, 1);
    private readonly Dictionary<(string Principal, string Key), Receipt> receipts = [];
    private GameConnectorSnapshot snapshot;
    private string snapshotFingerprint = string.Empty;
    private ulong revision;
    private TwCliTarget? target;
    private CliStatus? cliStatus;
    private string? cliStatusSession;
    private long cliStatusAt;
    private DateTimeOffset? lastConnectedAt;
    private string? lastStateKey;

    public GameConnectorService(GameControlContext controls, SystemInfo info, GameConnectorSettingsStore settings,
        GameConnectorEnvironment environment, TimeProvider? time = null, DiagnosticTrace? trace = null)
    {
        this.controls = controls; this.info = info; this.settings = settings; this.environment = environment;
        clock = time ?? TimeProvider.System; this.trace = trace;
        files = new(environment.PackageCliPath, environment.KoreanCliCandidates, environment.ExpectedCliSha256 ?? TwCliFile.RegisteredSha256);
        if (environment.SettingsDirectory is { } directory)
            prefs = new(environment.PreferencesDirectory, directory, environment.Probe, clock);
        Cli = new(() => Volatile.Read(ref target), environment.Runner, clock, trace);
        snapshot = Initial();
        // 没有设置目录（模拟宿主、测试宿主、非 Windows）时连接器不可用，不等第一次刷新就如实报告。
        if (environment.SettingsDirectory is null)
        {
            var reason = environment.UnavailableReason ?? "storage.unavailable";
            snapshot = snapshot with { Status = GameConnectorStatus.Unavailable, Reason = reason, RiskAcknowledged = settings.Read().RiskAcknowledgedAt is not null,
                Actions = new(false, reason, false, reason, false, reason) };
        }
    }

    /// <summary>命令行单通道服务；乐谱库队列与合奏经 <see cref="IGameMusicControl"/> 使用它。</summary>
    public TwCliService Cli { get; }

    public GameConnectorSnapshot Current { get { lock (gate) return snapshot; } }

    public static string ETag(GameConnectorSnapshot value) => "\"game-connector-" + value.BackendEpoch + "-" + value.Revision + "\"";

    /// <summary>音乐控制端口的可用性：已连接且目录已取得、不在加载中。</summary>
    public GameMusicAvailability MusicAvailability()
    {
        var current = Current;
        return current.Status.KnownValue == GameConnectorStatus.Connected
            ? new(true, "available") : new(false, StatusReason(current));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(LoopInterval, clock);
        try
        {
            await RefreshAsync(false, stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RefreshAsync(false, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Cli.Interrupt(); }
    }

    /// <summary>紧急停止：取消排队的命令行调用并中断在途进程，游戏动作状态由调用方记为未知。</summary>
    public void EmergencyStop()
    {
        Cli.Interrupt();
        // 被中断的命令是否已让游戏动作生效未知：稍后重查一次演奏状态（结果只进诊断，不推进任何队列）。
        if (Current.Status.KnownValue == GameConnectorStatus.Connected)
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), clock, CancellationToken.None).ConfigureAwait(false); await Cli.ExecuteAsync("get_activity", null).ConfigureAwait(false); }
                catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
            });
    }

    // ───────────────────────── 状态 ─────────────────────────

    public async Task<GameConnectorSnapshot> RefreshAsync(bool force, CancellationToken ct = default)
    {
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = await Task.Run(() => ComputeAsync(force, ct), ct).ConfigureAwait(false);
            return Publish(next);
        }
        finally { refreshGate.Release(); }
    }

    private async Task<GameConnectorSnapshot> ComputeAsync(bool force, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var stored = settings.Read();
        var storageOk = environment.SettingsDirectory is not null && prefs is not null;
        var (directory, directorySource) = ResolveDirectory(stored);
        if (directorySource == GameConnectorDirectorySource.Bound && directory is not null &&
            !string.Equals(directory, stored.RememberedDirectory, StringComparison.OrdinalIgnoreCase))
            await settings.UpdateAsync(x => x with { RememberedDirectory = directory }, ct).ConfigureAwait(false);

        var source = files.Resolve();
        var deployment = files.Inspect(directory);
        MmPlayerPrefsValues? values = null;
        string? prefsProblem = null;
        try { values = prefs?.Read(); }
        catch (TwCliPreferencesException error) { prefsProblem = error.Code; }
        var process = directory is null || !storageOk ? new GameProcessInfo(GameProcessState.NotRunning) : environment.Probe.Probe(directory);
        var enabled = values?.EnabledFlag;
        var cliUsable = source.State == GameConnectorCliFileState.Verified || deployment.Verified;

        // 目标：游戏运行且部署的命令行工具核对通过时才可调用。
        var next = process.State == GameProcessState.Running && deployment.Verified && directory is not null && enabled == true
            ? new TwCliTarget(deployment.Path!, directory, process.SessionKey) : null;
        Volatile.Write(ref target, next);

        OpenEnum<GameConnectorStatus> status; string reason;
        if (!storageOk) (status, reason) = (GameConnectorStatus.Unavailable, environment.UnavailableReason ?? "storage.unavailable");
        else if (!cliUsable) (status, reason) = (GameConnectorStatus.Unavailable,
            source.State == GameConnectorCliFileState.HashMismatch ? "gameCli.cliHashMismatch" : "gameCli.cliMissing");
        else if (prefsProblem is not null) (status, reason) = (GameConnectorStatus.Unavailable, prefsProblem);
        else if (enabled != true) (status, reason) = (GameConnectorStatus.Disabled, "gameCli.disabled");
        else if (directory is null || process.State == GameProcessState.NotRunning) (status, reason) = (GameConnectorStatus.GameNotRunning, "gameCli.gameNotRunning");
        else if (process.State == GameProcessState.Unverifiable) (status, reason) = (GameConnectorStatus.GameNotRunning, "gameCli.processUnverifiable");
        else if (!deployment.Verified) (status, reason) = (GameConnectorStatus.Unavailable, deployment.Differs ? "gameCli.deployedDiffers" : "gameCli.notDeployed");
        else (status, reason) = await CliStatusAsync(process.SessionKey, process.StartTicks, force, ct).ConfigureAwait(false);
        if (process.State != GameProcessState.Running) { cliStatus = null; cliStatusSession = null; }

        var catalog = new GameConnectorCatalogInfo(false, false, 0);
        if (status.KnownValue == GameConnectorStatus.Connected)
        {
            var loaded = await Cli.CatalogAsync(ct).ConfigureAwait(false);
            if (loaded is not null) catalog = new(!loaded.Loading, loaded.Loading, loaded.Count);
            lastConnectedAt = now;
            if (loaded is { Loading: true }) reason = "gameCli.catalogLoading";
            else if (loaded is null) reason = "gameCli.catalogLoading";
            else reason = "available";
        }

        var running = process.State switch { GameProcessState.Running => true, GameProcessState.NotRunning => directory is null ? (bool?)null : false, _ => (bool?)null };
        var runBlock = process.State == GameProcessState.Running ? "gameCli.gameRunning" : process.State == GameProcessState.Unverifiable ? "gameCli.processUnverifiable" : null;
        string? enableBlocked = !storageOk ? environment.UnavailableReason ?? "storage.unavailable" : directory is null ? "gameCli.directoryUnknown"
            : !cliUsable ? "gameCli.cliUnavailable" : deployment.Differs ? "gameCli.deployedDiffers"
            : prefs is { PreferencesExist: false } ? "gameCli.preferencesMissing" : runBlock
            ?? (status.KnownValue == GameConnectorStatus.Connected ? "gameCli.alreadyEnabled" : null);
        string? disableBlocked = !storageOk ? environment.UnavailableReason ?? "storage.unavailable" : enabled != true ? "gameCli.notEnabled"
            : directory is null ? "gameCli.directoryUnknown" : runBlock;
        string? restoreBlocked = !storageOk ? environment.UnavailableReason ?? "storage.unavailable" : prefs is { HasBackup: false } ? "gameCli.noBackup"
            : directory is null ? "gameCli.directoryUnknown" : runBlock;

        return new(info.InstanceId, new(0), status, reason, enabled, stored.RiskAcknowledgedAt is not null,
            new(source.Source, source.State, source.Version, deployment.Verified, deployment.Differs),
            new(running, directory, directorySource),
            new(enableBlocked is null, enableBlocked, disableBlocked is null, disableBlocked, restoreBlocked is null, restoreBlocked),
            catalog, now, lastConnectedAt, values?.LastUsedAt);
    }

    /// <summary>游戏刚启动、尚未进入游戏时命令行同样以 option_off 返回：进程启动后这段时间内按“游戏启动中”处理，之后才算已失效。</summary>
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(3);

    private async Task<(OpenEnum<GameConnectorStatus>, string)> CliStatusAsync(string? session, long processStartTicks, bool force, CancellationToken ct)
    {
        var fresh = cliStatus is not null && cliStatusSession == session && clock.GetElapsedTime(cliStatusAt) < StatusInterval;
        if (fresh && !force) return (cliStatus!.Status, cliStatus.Reason);
        var result = await Cli.StatusAsync(ct).ConfigureAwait(false);
        CliStatus status = result.Kind switch
        {
            TwCliResultKind.Ok => new(GameConnectorStatus.Connected, "available"),
            TwCliResultKind.NotConnected when result.Reason == "option_off" && Starting(processStartTicks) => new(GameConnectorStatus.Checking, "gameCli.gameStarting"),
            TwCliResultKind.NotConnected when result.Reason == "option_off" => new(GameConnectorStatus.Expired, "gameCli.expired"),
            TwCliResultKind.NotConnected => new(GameConnectorStatus.GameNotRunning, "gameCli.pipeUnavailable"),
            TwCliResultKind.Interrupted => cliStatus ?? new(GameConnectorStatus.Checking, "gameCli.checking"),
            _ => new(GameConnectorStatus.Unavailable, "gameCli.cliError")
        };
        if (result.Kind != TwCliResultKind.Interrupted) { cliStatus = status; cliStatusSession = session; cliStatusAt = clock.GetTimestamp(); }
        return (status.Status, status.Reason);
    }

    // 进程启动时刻为 UTC Ticks（WindowsGameProcessProbe）；读不到（0）时不按启动中处理。
    private bool Starting(long processStartTicks) =>
        processStartTicks > 0 && clock.GetUtcNow().UtcTicks - processStartTicks is >= 0 and var age && age < StartupGrace.Ticks;

    private (string? Directory, OpenEnum<GameConnectorDirectorySource> Source) ResolveDirectory(GameConnectorSettings stored)
    {
        static bool Valid(string? path) => path is { Length: > 0 } && Path.IsPathFullyQualified(path) && File.Exists(Path.Combine(path, TwCliFile.GameExecutable));
        if (controls.Bindings.Recent is { } binding && binding.Session.Region == "TW" && environment.ProcessImagePath(binding.Session.ProcessId) is { } image &&
            string.Equals(Path.GetFileName(image), TwCliFile.GameExecutable, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(image) is { } bound && Valid(bound))
            return (bound, GameConnectorDirectorySource.Bound);
        if (Valid(stored.ManualDirectory)) return (stored.ManualDirectory, GameConnectorDirectorySource.Manual);
        if (Valid(stored.RememberedDirectory)) return (stored.RememberedDirectory, GameConnectorDirectorySource.Remembered);
        foreach (var candidate in environment.DefaultGameDirectories)
            if (Valid(candidate)) return (candidate, GameConnectorDirectorySource.Default);
        return (null, GameConnectorDirectorySource.None);
    }

    private GameConnectorSnapshot Publish(GameConnectorSnapshot next)
    {
        // 指纹不含检查时间与修订：只有内容变化才递增修订，前端据此判断是否需要重新查询乐谱可演奏性。
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            next with { CheckedAt = default, Revision = new(0), LastConnectedAt = null }, ContractJson.Options)));
        lock (gate)
        {
            if (fingerprint != snapshotFingerprint)
            {
                snapshotFingerprint = fingerprint;
                revision = checked(revision + 1);
            }
            snapshot = next with { Revision = new(revision) };
            var key = snapshot.Status.RawValue + "|" + snapshot.Reason;
            if (key != lastStateKey)
            {
                lastStateKey = key;
                trace?.Emit(DiagnosticEvents.GameCliState, new DiagnosticFields().Value("status", snapshot.Status.RawValue).Value("reason", snapshot.Reason)
                    .Value("preference", snapshot.PreferenceEnabled is { } on ? (on ? "true" : "false") : "unknown")
                    .Flag("running", snapshot.Game.Running == true).Count("commands", snapshot.Catalog.Loaded ? snapshot.Catalog.CommandCount : -1));
            }
            return snapshot;
        }
    }

    private GameConnectorSnapshot Initial() => new(info.InstanceId, new(0), GameConnectorStatus.Checking, "gameCli.checking", null, false,
        new(GameConnectorCliSource.None, GameConnectorCliFileState.Missing, null, false, false),
        new(null, null, GameConnectorDirectorySource.None), new(false, "gameCli.checking", false, "gameCli.checking", false, "gameCli.checking"),
        new(false, false, 0), clock.GetUtcNow(), null, null);

    private static string StatusReason(GameConnectorSnapshot value) => value.Status.KnownValue switch
    {
        GameConnectorStatus.Disabled => "gameCli.disabled",
        GameConnectorStatus.GameNotRunning => "gameCli.gameNotRunning",
        GameConnectorStatus.Expired => "gameCli.expired",
        GameConnectorStatus.Checking => value.Reason == "gameCli.gameStarting" ? value.Reason : "gameCli.checking",
        _ => value.Reason
    };

    // ───────────────────────── 命令 ─────────────────────────

    public async Task<GameConnectorSnapshot> CommandAsync(string principal, GameConnectorCommand command, string? idempotencyKey, CancellationToken ct = default)
    {
        if (command is null || !Actions.Contains(command.Action, StringComparer.Ordinal)) throw new OperationException("request.invalid", "连接器命令无效");
        if (command.GameDirectory is { } given && (given.Length > 512 || given.Any(char.IsControl))) throw new OperationException("request.invalid", "游戏目录无效");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, ContractJson.Options)));
        if (command.Action != "refresh")
        {
            if (idempotencyKey is null) throw new OperationException("idempotency.required", "连接器命令需要 Idempotency-Key");
            lock (gate)
            {
                Prune();
                if (receipts.TryGetValue((principal, idempotencyKey), out var previous))
                    return previous.Hash == hash ? previous.Snapshot : throw new OperationException("idempotency.conflict", "幂等键已用于其他连接器命令");
            }
        }
        await commandGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() => RunCommandAsync(command, ct), ct).ConfigureAwait(false);
            if (command.Action != "refresh")
                lock (gate)
                {
                    Prune();
                    if (receipts.Count >= MaximumReceipts) throw new OperationException("resource.capacityExceeded", "连接器命令回执达到上限");
                    receipts[(principal, idempotencyKey!)] = new(hash, result, clock.GetTimestamp());
                }
            return result;
        }
        finally { commandGate.Release(); }
    }

    private async Task<GameConnectorSnapshot> RunCommandAsync(GameConnectorCommand command, CancellationToken ct)
    {
        try
        {
            switch (command.Action)
            {
                case "refresh": return await RefreshAsync(true, ct).ConfigureAwait(false);
                case "set-directory":
                    await SetDirectoryAsync(command.GameDirectory, ct).ConfigureAwait(false);
                    Report("set-directory", null);
                    return await RefreshAsync(true, ct).ConfigureAwait(false);
            }
            var current = await RefreshAsync(true, ct).ConfigureAwait(false);
            if (command.GameDirectory is not null) await SetDirectoryAsync(command.GameDirectory, ct).ConfigureAwait(false);
            if (command.GameDirectory is not null) current = await RefreshAsync(true, ct).ConfigureAwait(false);
            var directory = current.Game.Directory;
            switch (command.Action)
            {
                case "enable":
                    Require(current.Actions.EnableBlocked);
                    var stored = settings.Read();
                    if (stored.RiskAcknowledgedAt is null)
                    {
                        if (!command.RiskAcknowledged) throw Fail("gameCli.riskNotAcknowledged");
                        await settings.UpdateAsync(x => x with { RiskAcknowledgedAt = clock.GetUtcNow() }, ct).ConfigureAwait(false);
                    }
                    files.Deploy(directory!);
                    Report("deploy", null);
                    prefs!.Enable(directory!);
                    break;
                case "disable":
                    Require(current.Actions.DisableBlocked);
                    prefs!.Disable(directory!);
                    break;
                case "restore":
                    Require(current.Actions.RestoreBlocked);
                    prefs!.Restore(directory!);
                    break;
            }
            Report(command.Action, null);
            return await RefreshAsync(true, ct).ConfigureAwait(false);
        }
        catch (TwCliPreferencesException error) { Report(command.Action, error.Code); throw Fail(error.Code, error.Message); }
        catch (TwCliFileException error) { Report(command.Action, error.Code); throw Fail(error.Code, error.Message); }
        catch (OperationException error) { Report(command.Action, error.Code); throw; }
    }

    private async Task SetDirectoryAsync(string? directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory)) { await settings.UpdateAsync(x => x with { ManualDirectory = null }, ct).ConfigureAwait(false); return; }
        var full = Path.GetFullPath(directory.Trim()).TrimEnd('\\', '/');
        if (!Path.IsPathFullyQualified(directory.Trim()) || !File.Exists(Path.Combine(full, TwCliFile.GameExecutable)))
            throw Fail("gameCli.directoryInvalid");
        if (OperatingSystem.IsWindows()) LocalDiscoverySecurity.RejectLinks(full);
        await settings.UpdateAsync(x => x with { ManualDirectory = full }, ct).ConfigureAwait(false);
    }

    private void Report(string action, string? code) => trace?.Emit(DiagnosticEvents.GameCliAction,
        new DiagnosticFields().Value("action", action).Value("result", code is null ? "ok" : "failed").Value("code", code));

    private static void Require(string? blocked) { if (blocked is not null) throw Fail(blocked); }

    private static OperationException Fail(string code, string? message = null) => new(code, message ?? code switch
    {
        "gameCli.gameRunning" => "请先正常退出台服游戏",
        "gameCli.processUnverifiable" => "无法确认游戏进程，请先退出游戏",
        "gameCli.directoryUnknown" => "未找到台服游戏目录",
        "gameCli.directoryInvalid" => "所选目录里没有台服游戏程序",
        "gameCli.cliUnavailable" => "命令行工具文件不可用",
        "gameCli.deployedDiffers" => "台服目录已有不同的命令行工具",
        "gameCli.noBackup" => "没有可恢复的连接器配置记录",
        "gameCli.alreadyEnabled" => "连接器已经启用并已连接",
        "gameCli.notEnabled" => "连接器尚未启用",
        "gameCli.riskNotAcknowledged" => "首次开启需要先确认风险",
        "gameCli.preferencesMissing" => "未找到台服设置，请先正常启动台服一次",
        "storage.unavailable" => "当前宿主没有数据根，无法启用连接器",
        "platform.unsupported" => "当前平台不支持游戏连接器",
        _ => "连接器操作无法完成"
    });

    private void Prune()
    {
        foreach (var pair in receipts.Where(x => clock.GetElapsedTime(x.Value.Created) >= ReceiptLifetime).ToArray()) receipts.Remove(pair.Key);
    }
}
