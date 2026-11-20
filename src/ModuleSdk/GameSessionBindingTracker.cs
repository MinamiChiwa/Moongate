using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record GameWindowObservation(string Region, int ProcessId, DateTimeOffset ProcessStartTime,
    UInt64String WindowIdentity, string? GameBuild, PhysicalWindowRect Bounds, double DpiX, double DpiY,
    bool IsMinimized);

public sealed class GameSessionBindingTracker(TimeProvider? clock = null)
{
    /// <summary>只读查询复用上次窗口核对结果的最长间隔；短于观测有效期两秒和前端五秒的能力刷新周期。</summary>
    public static readonly TimeSpan ReadRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private Func<GameWindowObservation?>? source;
    private Func<bool>? exited;
    private GameWindowBinding? current;
    private string? currentRegion;
    private string? currentBuild;
    private long? refreshedAt;
    /// <summary>绑定修订变化或绑定清除时触发（参数为新绑定及是否换了游戏会话）；在内部锁内调用，处理方须不阻塞。</summary>
    public event Action<GameWindowBinding?, bool>? Changed;

    /// <summary>即时核对窗口来源后的绑定；输入、租约、启动采集或运行及绑定写入使用。</summary>
    public GameWindowBinding? Current
    {
        get { lock (gate) { RefreshCore(); return current; } }
    }

    /// <summary>只读查询使用的绑定：距上次核对不足 <see cref="ReadRefreshInterval"/> 时返回该结果，否则即时核对。</summary>
    public GameWindowBinding? Recent
    {
        get
        {
            lock (gate)
            {
                if (refreshedAt is not { } last || time.GetElapsedTime(last) >= ReadRefreshInterval) RefreshCore();
                return current;
            }
        }
    }

    /// <summary>最近一次刷新得到的绑定；不调用窗口来源，供诊断等只读路径使用。</summary>
    public GameWindowBinding? LastKnown { get { lock (gate) return current; } }

    /// <summary>
    /// 以窗口来源维持绑定。来源返回空表示当前读不到窗口：给出 <paramref name="processExited"/> 时，进程仍在则保留原绑定、
    /// 停止接收输入并递增修订（同一游戏会话），进程已退出才清除；不给出时读不到即清除。
    /// </summary>
    public GameWindowBinding? BindSource(Func<GameWindowObservation?> refresh, Func<bool>? processExited = null)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        lock (gate)
        {
            source = refresh; exited = processExited;
            RefreshCore();
            return current;
        }
    }

    private void RefreshCore()
    {
        if (source is null) return;
        var observed = source();
        if (observed is not null) UpdateCore(observed);
        // 清除后不再沿用这个来源：游戏进程已结束，重新绑定由用户选择或自动绑定决定。
        else if (current is null || exited is null || exited()) { ClearCore(); source = null; exited = null; }
        else HoldCore();
        refreshedAt = time.GetTimestamp();
    }

    /// <summary>进程仍在而窗口读不到（加载画面无响应、几何中间态、换窗口途中）：会话与几何不变，只停止接收输入。</summary>
    private void HoldCore()
    {
        if (current is not { CanAcceptInput: true } held) return;
        current = held with { CanAcceptInput = false,
            Session = held.Session with { BindingRevision = new(checked(held.Session.BindingRevision.Value + 1)) } };
        Raise(current, false);
    }

    public GameWindowBinding Update(GameWindowObservation observation)
    {
        lock (gate) { source = null; exited = null; return UpdateCore(observation); }
    }

    private GameWindowBinding UpdateCore(GameWindowObservation observation)
    {
        Validate(observation);
        var processRestarted = current is null || current.Session.ProcessId != observation.ProcessId ||
            current.Session.ProcessStartTime != observation.ProcessStartTime ||
            !string.Equals(currentRegion, observation.Region, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentBuild, observation.GameBuild, StringComparison.Ordinal);
        var bindingChanged = processRestarted || current!.Session.WindowIdentity != observation.WindowIdentity ||
            current.Bounds != observation.Bounds || current.DpiX != observation.DpiX || current.DpiY != observation.DpiY ||
            current.IsMinimized != observation.IsMinimized || current.CanAcceptInput == observation.IsMinimized;

        var sessionId = processRestarted ? Guid.NewGuid().ToString("N") : current!.Session.Id;
        var revision = bindingChanged ? checked((current?.Session.BindingRevision.Value ?? 0) + 1) : current!.Session.BindingRevision.Value;
        var session = new GameSession(sessionId, observation.Region, observation.ProcessId,
            observation.ProcessStartTime, observation.WindowIdentity, new UInt64String(revision), observation.GameBuild);
        current = new GameWindowBinding(session, observation.Bounds, observation.DpiX, observation.DpiY,
            observation.IsMinimized, !observation.IsMinimized);
        currentRegion = observation.Region;
        currentBuild = observation.GameBuild;
        if (bindingChanged) Raise(current, processRestarted);
        return current;
    }

    public void Clear()
    {
        lock (gate) { source = null; exited = null; ClearCore(); }
    }

    private void ClearCore()
    {
        if (current is not null) Raise(null, true);
        current = null;
        currentRegion = null;
        currentBuild = null;
    }

    private void Raise(GameWindowBinding? binding, bool sessionChanged)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action<GameWindowBinding?, bool>)handler)(binding, sessionChanged); } catch { }
    }

    private static void Validate(GameWindowObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.Region) || observation.ProcessId <= 0 ||
            observation.ProcessStartTime.Offset != TimeSpan.Zero || observation.ProcessStartTime == default ||
            observation.WindowIdentity.Value == 0 || observation.Bounds.Width <= 0 || observation.Bounds.Height <= 0 ||
            !double.IsFinite(observation.DpiX) || observation.DpiX <= 0 ||
            !double.IsFinite(observation.DpiY) || observation.DpiY <= 0 ||
            observation.GameBuild is { Length: > 128 })
            throw new ArgumentException("游戏窗口观测无效", nameof(observation));
    }
}
