using System.Diagnostics;
using MabiTool.Infrastructure;

namespace MabiTool.Providers.TwCli;

/// <summary>命令行调用的目标：已核对的可执行文件、台服游戏目录（工作目录）与当前游戏会话标识（进程 ID 与启动时间，用于目录缓存）。</summary>
public sealed record TwCliTarget(string Executable, string WorkingDirectory, string? SessionKey);

/// <summary>
/// 命令行调用的单条串行通道：不并发下发、不设短超时、不自动重发；<see cref="Interrupt"/> 取消排队的调用并中断在途进程。
/// 指令目录按游戏会话缓存，客户端报告 <c>loading:true</c> 时稍后重取，退出码 4 使缓存失效。
/// </summary>
public sealed class TwCliService(Func<TwCliTarget?> target, ITwCliRunner runner, TimeProvider? time = null, DiagnosticTrace? trace = null)
{
    private static readonly TimeSpan LoadingRetry = TimeSpan.FromSeconds(5);
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly TwCliPriorityGate gate = new();
    private readonly object state = new();
    private readonly Queue<double> latencies = new();
    private CancellationTokenSource generation = new();
    private TwCliCatalog? catalog;
    private string? catalogSession;
    private bool catalogStale;
    private long catalogFetched;

    /// <summary>当前游戏会话标识；没有目标时为空。</summary>
    public string? SessionKey => target()?.SessionKey;

    /// <summary>最近几次调用往返耗时的中位数（毫秒），没有样本时为 0。</summary>
    public double RecentLatencyMs
    {
        get
        {
            lock (state)
            {
                if (latencies.Count == 0) return 0;
                var sorted = latencies.Order().ToArray();
                return sorted[sorted.Length / 2];
            }
        }
    }

    public Task<TwCliResult> StatusAsync(CancellationToken ct = default) => ExecuteAsync("status", null, ct);

    /// <summary>执行类指令（演奏、换乐器、停止、站起、聊天、<c>execute_*</c>、领取加工）在通道里先于排队中的读取。</summary>
    public static bool IsExecute(string command) =>
        command is "play_music_score" or "change_instrument" or "stop_action" or "stand_up" or "write_chat" or "complete_altering_work" ||
        command.StartsWith("execute_", StringComparison.Ordinal);

    /// <summary>调用方没有指定时的优先级：执行类指令为 <see cref="TwCliPriority.Execute"/>，其余为 <see cref="TwCliPriority.Control"/>。</summary>
    public static TwCliPriority PriorityOf(string command) => IsExecute(command) ? TwCliPriority.Execute : TwCliPriority.Control;

    /// <summary>通道里排队等待的调用数量（不含正在执行的）。</summary>
    public int Queued => gate.Waiting;

    /// <param name="priority">出队优先级；数据读取与交易所查询传 <see cref="TwCliPriority.Background"/>，不传时按指令名判定。</param>
    public async Task<TwCliResult> ExecuteAsync(string command, TwCliBody? body, CancellationToken ct = default, TwCliPriority? priority = null)
    {
        var current = target();
        if (current is null) return TwCliResult.Local(command, TwCliResultKind.Unavailable, "gameCli.cliMissing");
        CancellationTokenSource linked;
        lock (state) linked = CancellationTokenSource.CreateLinkedTokenSource(ct, generation.Token);
        try
        {
            try { await gate.WaitAsync(priority ?? PriorityOf(command), linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return TwCliResult.Local(command, TwCliResultKind.Interrupted, "gameCli.interrupted"); }
            try
            {
                // 排队期间被中断或目标已更换（游戏重启）时不再执行。
                if (linked.IsCancellationRequested) return TwCliResult.Local(command, TwCliResultKind.Interrupted, "gameCli.interrupted");
                var args = body is { } value ? new[] { command, value.Argument } : [command];
                var run = await runner.RunAsync(current.Executable, current.WorkingDirectory, args, linked.Token).ConfigureAwait(false);
                var result = TwCliResponse.Classify(command, run);
                Record(result);
                return result;
            }
            finally { gate.Release(); }
        }
        finally { linked.Dispose(); }
    }

    /// <summary>取消排队的调用并请求中断在途进程；游戏动作状态由调用方记为未知并重查。</summary>
    public void Interrupt()
    {
        CancellationTokenSource previous;
        lock (state) { previous = generation; generation = new(); }
        previous.Cancel();
    }

    /// <summary>当前会话已缓存的目录；没有或已属于旧会话时为空。</summary>
    public TwCliCatalog? CachedCatalog()
    {
        var session = target()?.SessionKey;
        lock (state) return catalog is not null && catalogSession == session && !catalogStale ? catalog : null;
    }

    /// <summary>取得指令目录：缓存可用时直接返回；<c>loading</c> 的目录至少隔 5 秒再取；取不到返回空。</summary>
    public async ValueTask<TwCliCatalog?> CatalogAsync(CancellationToken ct = default, bool refresh = false)
    {
        var session = target()?.SessionKey;
        lock (state)
        {
            var sameSession = catalog is not null && catalogSession == session && !catalogStale;
            if (!refresh && sameSession && (!catalog!.Loading || clock.GetElapsedTime(catalogFetched) < LoadingRetry)) return catalog;
        }
        var result = await ExecuteAsync("capabilities", null, ct).ConfigureAwait(false);
        if (!result.IsOk || TwCliCatalog.Parse(result.Body) is not { } parsed) return null;
        lock (state) { catalog = parsed; catalogSession = session; catalogStale = false; catalogFetched = clock.GetTimestamp(); }
        return parsed;
    }

    private void Record(TwCliResult result)
    {
        if (result.Kind != TwCliResultKind.Interrupted)
            lock (state)
            {
                latencies.Enqueue(result.Elapsed.TotalMilliseconds);
                while (latencies.Count > 8) latencies.Dequeue();
            }
        if (result.Kind == TwCliResultKind.UnknownCommand) lock (state) catalogStale = true;
        if (trace is null) return;
        var fields = new DiagnosticFields().Value("command", result.Command).Count("exitCode", result.ExitCode).Value("kind", result.Kind.ToString())
            .Value("error", result.Error).Value("reason", result.Reason).Count("elapsedMs", (long)result.Elapsed.TotalMilliseconds)
            .Count("stdoutChars", result.Stdout.Length).Count("stderrChars", result.Stderr.Length);
        var failed = result.Kind is not (TwCliResultKind.Ok or TwCliResultKind.Rejected);
        // 原始响应只进调试采集的详情，不进默认记录。
        trace.Emit(failed ? DiagnosticEvents.GameCliCallFailed : DiagnosticEvents.GameCliCall, fields,
            detail: $"cmd={result.Command} exit={result.ExitCode}\nstdout={Trim(result.Stdout)}\nstderr={Trim(result.Stderr)}");
    }

    private static string Trim(string text) => text.Length <= 2048 ? text : text[..2048] + "…";
}
