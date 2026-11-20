using MabiTool.Infrastructure;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Game;

/// <summary>
/// 自动选择游戏窗口：设置开启（默认）且当前没有有效绑定时，每 2 秒枚举一次台服窗口，只有唯一窗口时自动绑定；
/// 多个窗口保持手动选择。同一窗口（PID、进程启动时间、句柄）只准备一次，构建指纹不重复计算。
/// </summary>
public sealed class GameWindowAutoBinder(GameBindingService bindings, GameCaptureSettingsStore settings,
    GameControlContext controls, TimeProvider clock) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (controls.Simulation) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (settings.Read().AutomaticWindowBinding) await bindings.AutoBindAsync(stoppingToken);
            }
            catch (Exception error) when (error is OperationException or IOException or InvalidOperationException) { }
            await Task.Delay(Interval, clock, stoppingToken);
        }
    }
}

public sealed partial class GameBindingService
{
    /// <summary>
    /// 最近一次自动绑定尝试的窗口及是否绑定成功。没成功的窗口（提权、准备失败、准备期间变化）不再反复准备（整读客户端约 370 MiB）；
    /// 绑定成功后又被清除（进程退出，或进程退出被误判）时记录作废，同一窗口仍在即可重新自动绑定。
    /// </summary>
    private ((int ProcessId, DateTimeOffset StartedAt, UInt64String Window) Identity, bool Bound)? autoAttempted;

    /// <summary>没有有效绑定且恰有一个台服窗口时绑定它；走与手动选择相同的准备、即时核对及修订规则。返回是否新建了绑定。</summary>
    public async Task<bool> AutoBindAsync(CancellationToken ct)
    {
        if (source is null || controls.Simulation || controls.Bindings.Recent is not null) return false;
        lock (gate) if (autoAttempted is { Bound: true }) autoAttempted = null;
        var windows = source.Enumerate().Where(value => value.Observation.Region == "TW").Take(2).ToArray();
        if (windows.Length != 1) return false;
        var observation = windows[0].Observation;
        var identity = (observation.ProcessId, observation.ProcessStartTime, observation.WindowIdentity);
        lock (gate) { if (autoAttempted?.Identity == identity) return false; autoAttempted = (identity, false); }
        // 游戏以管理员身份运行时不自动绑定；手动选择时给出原因（game.elevated）。
        if (source.AccessProblem(observation) is "game.elevated") return false;
        var prepared = await source.PrepareAsync(observation, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!SameIdentity(prepared, observation)) return false;
        lock (gate)
        {
            if (controls.Bindings.Current is not null) return false;
            var fresh = source.Refresh(observation);
            if (!SameIdentity(fresh, observation) || fresh!.GameBuild != prepared!.GameBuild) return false;
            controls.Input.EmergencyStop();
            if (BindProcess(observation) is null) return false;
            autoAttempted = (identity, true);
            selected = true;
            return true;
        }
    }
}
