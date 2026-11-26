using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Modules.MusicLibrary.Application;

/// <summary>演奏队列持有的活动控制租约（<c>activity-control:{游戏会话}</c>）。</summary>
public interface IPerformanceLease : IDisposable
{
    GameActivityProof Proof { get; }
    bool IsValid { get; }
    /// <summary>续租；租约已被撤销或过期时返回假。</summary>
    bool Renew();
}

public interface IPerformanceActivityLeases
{
    /// <summary>取得租约；资源被占用抛 <c>resource.busy</c>，绑定与会话不符抛 <c>input.bindingChanged</c>。</summary>
    IPerformanceLease Acquire(string ownerId, string operationId, string gameSessionId);
}

public sealed record PerformanceRunnerOptions(TimeSpan PollInterval, TimeSpan StartGrace, int ObservationFailureLimit,
    TimeSpan ScoreListMaxAge, TimeSpan ControlStepTimeout)
{
    public static PerformanceRunnerOptions Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
}

/// <summary>
/// 经游戏连接器驱动独奏演奏队列：队列进入准备态后取得活动锁，按项换乐器、选曲演奏，约每秒读取演奏状态，
/// 剩余时间不超过提前切换边距时停止本曲并按曲间等待前进（游戏内乐曲自动循环，不停止就不会自然结束）。
/// 移植 MobiFolio 的 playIndex/pollAct/advance，但演奏命令只发一次，失败、拒绝与中断都不自动重发（P08/P10）。
/// </summary>
public sealed class PerformanceRunner : IDisposable
{
    private static readonly PerformancePhase[] Active =
        [PerformancePhase.Preparing, PerformancePhase.Armed, PerformancePhase.Executing, PerformancePhase.Performing, PerformancePhase.Waiting, PerformancePhase.Stopping];

    private sealed class Run(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
        public IPerformanceLease? Lease;
        public volatile bool GamePlaying;
        /// <summary>取走并释放租约；重复调用只释放一次。</summary>
        public void ReleaseLease() => Interlocked.Exchange(ref Lease, null)?.Dispose();
    }

    private readonly PerformanceSessionService sessions;
    private readonly IGameMusicControl music;
    private readonly IPerformanceActivityLeases leases;
    private readonly Func<string, string?> scoreTitle;
    private readonly Func<string?, string?> instrumentName;
    private readonly PerformanceRunnerOptions options;
    private readonly TimeProvider clock;
    private readonly DiagnosticTrace? trace;
    private readonly object gate = new();
    private readonly Dictionary<string, Run> runs = new(StringComparer.Ordinal);
    private bool disposed;

    public PerformanceRunner(PerformanceSessionService sessions, IGameMusicControl music, IPerformanceActivityLeases leases,
        Func<string, string?> scoreTitle, Func<string?, string?> instrumentName, PerformanceRunnerOptions? options = null,
        TimeProvider? time = null, DiagnosticTrace? trace = null)
    {
        this.sessions = sessions; this.music = music; this.leases = leases; this.scoreTitle = scoreTitle; this.instrumentName = instrumentName;
        this.options = options ?? PerformanceRunnerOptions.Default; clock = time ?? TimeProvider.System; this.trace = trace;
        sessions.Changed += OnChanged;
    }

    /// <summary>宿主启动时调用：上次运行遗留的非终态会话标为已取消，不自动恢复旧的游戏动作（P15）。</summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        foreach (var state in sessions.ListAll())
        {
            if (state.Phase is PerformancePhase.Idle or PerformancePhase.Stopped or PerformancePhase.Failed or PerformancePhase.Cancelled) continue;
            try
            {
                await sessions.CommandAsync(state.Owner, state.Id, new("stop", StopReason: PerformanceStopReason.Cancelled, Detail: "gameCli.hostRestarted"),
                    PerformanceSessionService.ETag(state.Generation), ct).ConfigureAwait(false);
            }
            catch (OperationException) { }
        }
    }

    /// <summary>紧急停止：取消全部驱动并撤销租约（同步、不等待数据库），再异步把仍在进行的会话置为已停止。游戏动作状态由调用方记为未知。</summary>
    public void EmergencyStop()
    {
        Run[] active;
        lock (gate) active = [.. runs.Values];
        foreach (var run in active)
        {
            run.Cancellation.Cancel();
            run.ReleaseLease();
            run.GamePlaying = false;
        }
        _ = Task.Run(async () =>
        {
            foreach (var state in sessions.ListAll().Where(x => x.Mode == "solo" && Active.Contains(x.Phase)))
                await ForceStoppedAsync(state).ConfigureAwait(false);
        });
    }

    /// <summary>宿主停止时取消驱动并撤销租约，不改写会话状态（下次启动时按遗留会话清理）。</summary>
    public void EmergencyStopSynchronous()
    {
        Run[] active;
        lock (gate) active = [.. runs.Values];
        foreach (var run in active) { run.Cancellation.Cancel(); run.ReleaseLease(); run.GamePlaying = false; }
    }

    private async Task ForceStoppedAsync(PerformanceQueueState state)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var current = sessions.TryGet(state.Owner, state.Id);
                if (current is null || !Active.Contains(current.Phase)) return;
                var tag = PerformanceSessionService.ETag(current.Generation);
                if (current.Phase != PerformancePhase.Stopping)
                    current = await sessions.CommandAsync(state.Owner, state.Id, new("stop", StopReason: PerformanceStopReason.ManualStop, Detail: "gameCli.interrupted"), tag, CancellationToken.None).ConfigureAwait(false);
                await sessions.CommandAsync(state.Owner, state.Id, new("stopped", StopReason: PerformanceStopReason.ManualStop),
                    PerformanceSessionService.ETag(current.Generation), CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (OperationException) { }
        }
    }

    private void OnChanged(PerformanceQueueState state)
    {
        if (state.Mode != "solo" || !Active.Contains(state.Phase)) return;
        var key = Key(state.Owner, state.Id);
        lock (gate)
        {
            if (disposed || runs.ContainsKey(key)) return;
            var run = new Run(new());
            runs[key] = run;
            run.Task = Task.Run(() => RunAsync(state.Owner, state.Id, key, run));
        }
    }

    private static string Key(string owner, string id) => owner + "/" + id;

    private async Task RunAsync(string owner, string id, string key, Run run)
    {
        var ct = run.Cancellation.Token;
        var observationFailures = 0;
        DateTimeOffset? playingSince = null;
        double? lastRemaining = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var state = sessions.TryGet(owner, id);
                if (state is null || !Active.Contains(state.Phase)) return;
                var tag = PerformanceSessionService.ETag(state.Generation);
                switch (state.Phase)
                {
                    case PerformancePhase.Armed or PerformancePhase.Waiting:
                    {
                        if (state.Phase == PerformancePhase.Waiting && state.GapUntil is { } until && clock.GetUtcNow() < until)
                        {
                            await DelayAsync(Min(until - clock.GetUtcNow(), options.PollInterval), ct).ConfigureAwait(false);
                            continue;
                        }
                        if (!EnsureLease(run, owner, id, state, out var busy)) { await AbortAsync(owner, id, state, PerformanceStopReason.ControlFailure, busy, ct).ConfigureAwait(false); continue; }
                        // 下一首、停止后的曲间等待之前，游戏里仍在演奏的上一首先停下。
                        if (run.GamePlaying)
                        {
                            var stopped = await StopGameAsync(run, ct).ConfigureAwait(false);
                            if (ct.IsCancellationRequested) return;
                            if (!stopped) { await AbortAsync(owner, id, state, PerformanceStopReason.ControlFailure, "gameCli.interrupted", ct).ConfigureAwait(false); break; }
                        }
                        await TryAsync(owner, id, new("dispatch"), tag, ct).ConfigureAwait(false);
                        trace?.Emit(DiagnosticEvents.GameCliPerformance, new DiagnosticFields().Value("event", "dispatch").Value("phase", state.Phase.ToString()).Count("position", state.CurrentPosition));
                        playingSince = null; lastRemaining = null; observationFailures = 0;
                        break;
                    }
                    case PerformancePhase.Executing:
                    {
                        if (!EnsureLease(run, owner, id, state, out var busy)) { await AbortAsync(owner, id, state, PerformanceStopReason.ControlFailure, busy, ct).ConfigureAwait(false); break; }
                        if (run.GamePlaying)
                        {
                            // 曲间等待为 0 时下一首直接进入执行：游戏里仍在演奏的上一首先停下。
                            var stopped = await StopGameAsync(run, ct).ConfigureAwait(false);
                            if (ct.IsCancellationRequested) return;
                            if (!stopped) { await AbortAsync(owner, id, state, PerformanceStopReason.ControlFailure, "gameCli.interrupted", ct).ConfigureAwait(false); break; }
                        }
                        var outcome = await StartItemAsync(run, state, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;
                        switch (outcome)
                        {
                            case { Fatal: true }:
                                await AbortAsync(owner, id, state, PerformanceStopReason.ControlFailure, outcome.Code, ct).ConfigureAwait(false);
                                break;
                            case { Code: not null }:
                                trace?.Emit(DiagnosticEvents.GameCliPerformance, new DiagnosticFields().Value("event", "failure").Value("phase", state.Phase.ToString()).Value("code", outcome.Code));
                                await TryAsync(owner, id, new("failure", Detail: outcome.Code), tag, ct).ConfigureAwait(false);
                                break;
                            default:
                                playingSince = clock.GetUtcNow(); lastRemaining = null; observationFailures = 0;
                                await ConfirmPlayingAsync(owner, id, ct).ConfigureAwait(false);
                                break;
                        }
                        break;
                    }
                    case PerformancePhase.Performing:
                    {
                        if (run.Lease is not { IsValid: true }) { await AbortAsync(owner, id, state, PerformanceStopReason.ControlFailure, "gameCli.interrupted", ct).ConfigureAwait(false); break; }
                        run.Lease.Renew();
                        await DelayAsync(options.PollInterval, ct).ConfigureAwait(false);
                        var latest = sessions.TryGet(owner, id);
                        if (latest is null || latest.Generation != state.Generation) break;
                        var activity = await music.ReadActivityAsync(ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;
                        if (activity is null)
                        {
                            if (++observationFailures >= options.ObservationFailureLimit)
                                await AbortAsync(owner, id, state, PerformanceStopReason.ObservationLost, "gameCli.observationLost", ct).ConfigureAwait(false);
                            break;
                        }
                        observationFailures = 0;
                        if (activity.IsPlaying)
                        {
                            var total = activity.TotalSeconds ?? 0;
                            var remaining = activity.RemainingSeconds ?? (total > 0 && activity.ElapsedSeconds is { } elapsed ? total - elapsed : (double?)null);
                            lastRemaining = remaining;
                            if (total > 0 && remaining is { } left && left <= state.AdvanceMargin)
                            {
                                var stopped = await StopGameAsync(run, ct).ConfigureAwait(false);
                                if (ct.IsCancellationRequested) return;
                                await TryAsync(owner, id, stopped ? new("stopped") : new("stopped", StopReason: PerformanceStopReason.ControlFailure, Detail: "gameCli.interrupted"), tag, ct).ConfigureAwait(false);
                            }
                        }
                        else if (playingSince is { } since && clock.GetUtcNow() - since >= options.StartGrace)
                        {
                            run.GamePlaying = false;
                            // 距结束还很久就停了：游戏里被手动停止，不前进到下一首。
                            var early = lastRemaining is { } rest && rest > state.AdvanceMargin + 5;
                            await TryAsync(owner, id, early ? new("stopped", StopReason: PerformanceStopReason.GameStopped) : new("stopped"), tag, ct).ConfigureAwait(false);
                        }
                        break;
                    }
                    case PerformancePhase.Stopping:
                    {
                        string? failure = null;
                        if (run.GamePlaying || run.Lease is not null && state.StopReason is PerformanceStopReason.ManualStop)
                        {
                            var stopped = await StopGameAsync(run, ct).ConfigureAwait(false);
                            if (ct.IsCancellationRequested) return;
                            if (!stopped) failure = "gameCli.interrupted";
                        }
                        var reason = failure is not null ? PerformanceStopReason.ControlFailure : state.StopReason ?? PerformanceStopReason.ManualStop;
                        trace?.Emit(DiagnosticEvents.GameCliPerformance, new DiagnosticFields().Value("event", "stopped").Value("phase", state.Phase.ToString()).Value("code", failure));
                        await TryAsync(owner, id, new("stopped", StopReason: reason, Detail: failure), tag, ct).ConfigureAwait(false);
                        break;
                    }
                    default:
                        await DelayAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                        break;
                }
                // 命令被过期修订挡回时不空转：给控制路径一个很短的间隔再重读状态。
                await DelayAsync(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (OperationException) { }
        finally
        {
            run.ReleaseLease();
            lock (gate) runs.Remove(key);
            // 终止后若会话又被重新开始（事件在清理前到达），再次启动驱动。
            if (!disposed && sessions.TryGet(owner, id) is { } latest) OnChanged(latest);
        }
    }

    private sealed record StartOutcome(string? Code, bool Fatal);

    /// <summary>当前项：换乐器、核对游戏乐谱并发出演奏命令；Code 为空表示已受理。</summary>
    private async Task<StartOutcome> StartItemAsync(Run run, PerformanceQueueState state, CancellationToken ct)
    {
        var item = state.Items[state.Order[state.CurrentPosition]].Item;
        var title = scoreTitle(item.ScoreId);
        if (string.IsNullOrWhiteSpace(title)) return new("gameCli.notFound", false);
        var listing = await music.ListScoresAsync(options.ScoreListMaxAge, ct).ConfigureAwait(false);
        if (!listing.Available) return new(listing.Reason ?? "gameCli.notConnected", true);
        var key = GameMusicNames.Title(title);
        var provided = listing.Scores.FirstOrDefault(x => GameMusicNames.Title(x.DisplayTitle) == key);
        if (provided is null || music.TryIsGameProvided(title) == false) return new("gameCli.userComposedNotAllowed", false);

        if (item.InstrumentId is not null)
        {
            var wanted = instrumentName(item.InstrumentId);
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var owned = await music.ListInstrumentsAsync(ct).ConfigureAwait(false);
                if (owned is null) return new("gameCli.notConnected", true);
                var match = GameMusicNames.MatchInstrument(owned, wanted, out var ambiguous);
                if (match is null) return new(ambiguous ? "gameCli.instrumentAmbiguous" : "gameCli.instrumentNotOwned", false);
                if (!match.Equipped)
                {
                    var changed = await music.ChangeInstrumentAsync(run.Lease!.Proof, match.Name, ct).ConfigureAwait(false);
                    if (!changed.Accepted) return Classify(changed);
                }
            }
        }
        var played = await music.PlayScoreAsync(run.Lease!.Proof, provided.DisplayTitle, ct).ConfigureAwait(false);
        if (played.Accepted) { run.GamePlaying = true; return new(null, false); }
        return Classify(played);
    }

    /// <summary>游戏拒绝与命令确定失败按项处理；未发出（连接器不可用）与被中断使整个队列停止。</summary>
    private static StartOutcome Classify(GameMusicResult result) => result.Outcome switch
    {
        GameMusicOutcome.Rejected or GameMusicOutcome.Failed => new(result.Code ?? "gameCli.failed", false),
        GameMusicOutcome.Interrupted => new("gameCli.interrupted", true),
        _ => new(result.Code ?? "gameCli.notConnected", true)
    };

    private async Task ConfirmPlayingAsync(string owner, string id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            var current = sessions.TryGet(owner, id);
            if (current is null || current.Phase != PerformancePhase.Executing) return;
            if (await TryAsync(owner, id, new("playing"), PerformanceSessionService.ETag(current.Generation), ct).ConfigureAwait(false) is not null)
            {
                trace?.Emit(DiagnosticEvents.GameCliPerformance, new DiagnosticFields().Value("event", "playing").Count("position", current.CurrentPosition));
                return;
            }
        }
    }

    /// <summary>经 stop_action 停止游戏里的演奏；invalid_state（没有可停止的动作）视为已停止。返回假表示结果未知。</summary>
    private async Task<bool> StopGameAsync(Run run, CancellationToken ct)
    {
        if (run.Lease is not { IsValid: true } lease) { run.GamePlaying = false; return !ct.IsCancellationRequested; }
        var result = await music.StopAsync(lease.Proof, ct).ConfigureAwait(false);
        var stopped = result.Accepted || result is { Outcome: GameMusicOutcome.Rejected, Code: "gameCli.invalidState" };
        if (stopped) run.GamePlaying = false;
        return stopped;
    }

    private bool EnsureLease(Run run, string owner, string id, PerformanceQueueState state, out string? code)
    {
        code = null;
        if (run.Lease is { IsValid: true }) { run.Lease.Renew(); return true; }
        run.ReleaseLease();
        try { run.Lease = leases.Acquire("performance-" + id, "performance-" + id, state.GameSessionId); return true; }
        catch (OperationException error) { code = error.Code; return false; }
    }

    private async Task AbortAsync(string owner, string id, PerformanceQueueState state, PerformanceStopReason reason, string? code, CancellationToken ct)
    {
        trace?.Emit(DiagnosticEvents.GameCliPerformance, new DiagnosticFields().Value("event", "lost").Value("phase", state.Phase.ToString()).Value("code", code));
        await TryAsync(owner, id, new("stop", StopReason: reason, Detail: code ?? "gameCli.failed"), PerformanceSessionService.ETag(state.Generation), ct).ConfigureAwait(false);
    }

    /// <summary>发出控制命令；会话已被用户或其他客户端改变（过期修订、状态不允许）时返回空，由调用方重读状态。</summary>
    private async Task<PerformanceQueueState?> TryAsync(string owner, string id, PerformanceSessionCommand command, string tag, CancellationToken ct)
    {
        try { return await sessions.CommandAsync(owner, id, command, tag, ct).ConfigureAwait(false); }
        catch (OperationException error) when (error.Code is "revision.stale" or "performance.invalidState" or "queue.empty") { return null; }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay, clock, ct);
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public void Dispose()
    {
        Run[] active;
        lock (gate) { if (disposed) return; disposed = true; active = [.. runs.Values]; }
        sessions.Changed -= OnChanged;
        foreach (var run in active) { run.Cancellation.Cancel(); }
        foreach (var run in active) { try { run.Task?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { } }
    }
}
