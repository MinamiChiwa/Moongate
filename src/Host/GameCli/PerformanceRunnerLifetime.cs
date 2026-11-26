using MabiTool.Modules.MusicLibrary.Application;

namespace MabiTool.Host.GameCli;

/// <summary>宿主启动时清理上次运行遗留的非终态演奏会话（不自动恢复旧的游戏动作），停止时释放演奏驱动与租约。</summary>
public sealed class PerformanceRunnerLifetime(PerformanceRunner runner) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => runner.RecoverAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        runner.EmergencyStopSynchronous();
        runner.Dispose();
        return Task.CompletedTask;
    }
}
