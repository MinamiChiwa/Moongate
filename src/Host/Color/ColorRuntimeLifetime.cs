using MabiTool.Infrastructure.Workers;

namespace MabiTool.Host.Color;

/// <summary>启动时固定预算，退出时在运行收尾之后回收保温进程。</summary>
internal sealed class ColorRuntimeLifetime(ColorWorkerPool workers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken) => await workers.DisposeAsync();
}
