using System.Diagnostics;
using System.Threading.Channels;

namespace MabiTool.Observation.Worker;

public sealed record InventoryLaneStatistics(long Completed, long Rejected, double LongestMs);

/// <summary>
/// 单连接库存解析的串行通道：捕获线程入队，单个后台任务按序执行。待处理最多 256 项、正文合计 64 MiB，
/// 与库存帧队列预算一致；超出时拒绝入队，由调用方撤销当前可信度。
/// </summary>
internal sealed class InventoryLane
{
    public const int MaximumItems = 256;
    public const long MaximumBytes = 64L * 1024 * 1024;
    private readonly Channel<(Action Work, int Bytes)> queue =
        Channel.CreateUnbounded<(Action, int)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object gate = new();
    private readonly Task pump;
    private int pending;
    private long bytes, completed, rejected;
    private double longest;
    private TaskCompletionSource? idle;

    public InventoryLane() => pump = Task.Run(PumpAsync);
    public Task Completion => pump;
    public InventoryLaneStatistics Statistics { get { lock (gate) return new(completed, rejected, longest); } }

    public bool TryPost(Action work, int size)
    {
        lock (gate)
        {
            if (pending >= MaximumItems || bytes + size > MaximumBytes || !queue.Writer.TryWrite((work, size))) { rejected++; return false; }
            pending++; bytes += size; return true;
        }
    }
    public Task IdleAsync()
    {
        lock (gate)
        {
            if (pending == 0) return Task.CompletedTask;
            idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return idle.Task;
        }
    }
    public void Complete() => queue.Writer.TryComplete();

    private async Task PumpAsync()
    {
        await foreach (var (work, size) in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var started = Stopwatch.GetTimestamp();
            try { work(); }
            catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning("inventory lane: " + error.GetType().Name); }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            TaskCompletionSource? done = null;
            lock (gate)
            {
                pending--; bytes -= size; completed++; longest = Math.Max(longest, elapsed);
                if (pending == 0) { done = idle; idle = null; }
            }
            done?.TrySetResult();
        }
        TaskCompletionSource? last;
        lock (gate) { last = idle; idle = null; }
        last?.TrySetResult();
    }
}
