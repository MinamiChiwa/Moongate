using System.Threading.Channels;
using System.Collections.Concurrent;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Providers.Replay;

namespace MabiTool.Host;

public sealed class ReplayOperations(OperationStore operations, ReplayProvider provider, TimeProvider clock) : BackgroundService
{
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Channel<(string Id, ReplayRequest Request, ReplayScenario Scenario)> queue =
        Channel.CreateBounded<(string, ReplayRequest, ReplayScenario)>(new BoundedChannelOptions(OperationStore.MaximumActiveOperations) { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> active = new();
    private bool accepting;

    public async Task<OperationSnapshot> SubmitAsync(string principal, ReplayRequest request, string? key, CancellationToken ct)
    {
        ReplayScenario scenario;
        try { scenario = ReplayProvider.Validate(request); }
        catch (ArgumentException error) { throw new OperationException("request.invalid", error.Message); }
        await admission.WaitAsync(ct);
        try
        {
            if (!accepting) throw new OperationException("provider.unavailable", "回放执行器正在关闭或等待恢复");
            // 队列槽位先检查；受理后即使 HTTP 连接断开，任务仍由宿主持有。
            var result = await operations.AdmitReplayAsync(principal, request, key, ct,
                queue.Reader.Count < OperationStore.MaximumActiveOperations);
            if (result.Created)
            {
                active.TryAdd(result.Snapshot.Id, 0);
                if (!queue.Writer.TryWrite((result.Snapshot.Id, request, scenario)))
                    throw new InvalidOperationException("回放队列容量与持久操作不一致");
            }
            return result.Snapshot;
        }
        finally { admission.Release(); }
    }
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                for (var step = 1; step <= job.Scenario.FrameCount; step++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(job.Request.IntervalMs), clock, stoppingToken);
                    if (!await operations.CommitReplayFrameAsync(job.Id, provider.Frame(job.Id, job.Scenario, step), job.Scenario.FrameCount,
                        step == job.Scenario.FrameCount && !job.Scenario.Fails, stoppingToken)) break;
                }
                if (job.Scenario.Fails) await operations.FailReplayAsync(job.Id);
                active.TryRemove(job.Id, out _);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // 写入失败保留未完成记录，重启统一恢复为 Interrupted；本进程停止受理。
            await admission.WaitAsync();
            try { accepting = false; queue.Writer.TryComplete(); }
            finally { admission.Release(); }
        }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try { accepting = false; queue.Writer.TryComplete(); }
        finally { admission.Release(); }
        await base.StopAsync(ct);
        if (ExecuteTask?.IsCompleted == true)
            foreach (var id in active.Keys)
                await operations.CancelAsync(id, "owner", true, "backend-stopping", ct);
    }
}
