namespace MabiTool.Providers.TwCli;

/// <summary>
/// 命令行通道的出队优先级：<see cref="Execute"/> 执行类指令先于 <see cref="Control"/> 演奏与合奏需要的控制读取（状态、目录、演奏状态、乐谱与乐器列表），
/// 后者先于 <see cref="Background"/> 数据读取与交易所查询。同一优先级先到先得；已开始的进程不被抢占。
/// </summary>
public enum TwCliPriority { Execute = 0, Control = 1, Background = 2 }

/// <summary>单个持有者的优先级互斥：释放时直接交给最高优先级的最早等待者；等待可被取消，取消后不再参与出队。</summary>
internal sealed class TwCliPriorityGate
{
    private sealed class Waiter
    {
        public readonly TaskCompletionSource Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
    }

    private readonly object sync = new();
    private readonly LinkedList<Waiter>[] queues = [new(), new(), new()];
    private bool held;

    /// <summary>等待取得通道；取消时抛出 <see cref="OperationCanceledException"/> 且不持有通道。</summary>
    public async Task WaitAsync(TwCliPriority priority, CancellationToken ct)
    {
        Waiter waiter;
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (!held) { held = true; return; }
            waiter = new();
            waiter.Node = queues[(int)priority].AddLast(waiter);
        }
        await using var registration = ct.Register(() =>
        {
            lock (sync)
            {
                if (waiter.Node?.List is not { } list) return;
                list.Remove(waiter.Node);
                waiter.Node = null;
                waiter.Source.TrySetCanceled(ct);
            }
        });
        await waiter.Source.Task.ConfigureAwait(false);
    }

    public void Release()
    {
        lock (sync)
        {
            foreach (var queue in queues)
                if (queue.First is { } node)
                {
                    queue.RemoveFirst();
                    node.Value.Node = null;
                    node.Value.Source.TrySetResult();
                    return;
                }
            held = false;
        }
    }

    /// <summary>排队中的等待者数量（诊断与测试用）。</summary>
    public int Waiting { get { lock (sync) return queues.Sum(queue => queue.Count); } }
}
