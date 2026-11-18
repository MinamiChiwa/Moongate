using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsGameWindowSource : IGameWindowSource
{
    private readonly HashSet<string> processes = new(["MabinogiMobile"], StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 显式准备时核对过的客户端：文件戳不变时沿用构建标识（已登记构建为布局标识，其余为未登记构建的哈希标识）。
    /// <see cref="ImagePath"/> 是进程报告的映像路径，<see cref="FinalPath"/> 是解析联接与符号链接后的路径，文件戳与哈希按后者读取。
    /// </summary>
    private sealed record PreparedFiles(int ProcessId, DateTimeOffset StartedAt, string ImagePath, string FinalPath, IReadOnlyList<TwClientFileStamp> Files,
        TwClientHashes Hashes, string Build);
    private readonly object preparedGate = new();
    private readonly Dictionary<(int Pid, DateTimeOffset StartedAt), PreparedFiles> prepared = [];
    public IReadOnlyList<SelectableGameWindow> Enumerate()
    {
        var result = new List<SelectableGameWindow>();
        EnumWindows((window, _) =>
        {
            if (result.Count >= 32) return false;
            if (!IsWindowVisible(window) || GetWindow(window, 4) != 0) return true;
            if (Read(window, null) is { } sample)
            {
                var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
                result.Add(new(title.ToString(), "MabinogiMobile", sample));
            }
            return true;
        }, 0);
        return result;
    }
    public GameWindowObservation? Refresh(GameWindowObservation selected) => Read(unchecked((nint)selected.WindowIdentity.Value), selected);
    public GameClientFiles? PreparedClient(GameWindowObservation selected)
    {
        lock (preparedGate)
            return prepared.TryGetValue((selected.ProcessId, selected.ProcessStartTime), out var client) && client.Build == selected.GameBuild
                ? new(client.FinalPath, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [TwClientFingerprint.AssemblyFile] = client.Hashes.AssemblySha256, [TwClientFingerprint.MetadataFile] = client.Hashes.MetadataSha256,
                    [TwClientFingerprint.UnityFile] = client.Hashes.UnitySha256
                }) : null;
    }
    public async ValueTask<GameWindowObservation?> PrepareAsync(GameWindowObservation selected, CancellationToken ct)
    {
        var fresh = Refresh(selected);
        if (fresh is null) return null;
        ct.ThrowIfCancellationRequested();
        if (fresh.GameBuild is not null) return fresh;
        var key = (selected.ProcessId, selected.ProcessStartTime);
        lock (preparedGate) prepared.Remove(key);
        string? image;
        try
        {
            using var process = Process.GetProcessById(selected.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != selected.ProcessStartTime.UtcDateTime) return null;
            image = WindowsProcessAccess.ImagePath(selected.ProcessId);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        // 安装经联接或符号链接挂到原路径时按最终路径核对文件；解析不了时按原路径核对（含链接的路径会被拒绝）。
        var final = image is null ? null : TwClientFingerprint.ResolveFinalPath(image) ?? image;
        if (image is null || final is null) return fresh with { GameBuild = null };
        var before = TwClientFingerprint.ReadFileStamps(final);
        var hashes = before is null ? null : await TwClientFingerprint.HashAsync(final, ct).ConfigureAwait(false);
        if (before is null || hashes is null) return Refresh(selected);
        var after = TwClientFingerprint.ReadFileStamps(final);
        fresh = Refresh(selected);
        if (fresh is null || after is null || !before.SequenceEqual(after)) return fresh;
        lock (preparedGate)
        {
            if (prepared.Count >= 32) prepared.Remove(prepared.Keys.First());
            prepared[key] = new(selected.ProcessId, selected.ProcessStartTime, image, final, after, hashes, TwClientFingerprint.BuildId(hashes));
        }
        return Refresh(selected);
    }

    /// <summary>
    /// 游戏进程完整性级别高于 Moongate（游戏以管理员身份运行）时为 <c>game.elevated</c>：系统会丢弃发往它的模拟输入。
    /// Moongate 已以管理员身份运行却读不到游戏令牌时为 <c>game.integrityUnreadable</c>，只记诊断，不拦截绑定。
    /// </summary>
    public string? AccessProblem(GameWindowObservation selected) => WindowsProcessAccess.CompareIntegrity(selected.ProcessId) switch
    {
        ElevationVerdict.Higher => "game.elevated",
        ElevationVerdict.UnreadableWhileElevated => "game.integrityUnreadable",
        _ => null
    };
    /// <summary>轮次输入准备与停止前调用：按实际回读的前台状态判断，不只看 SetForegroundWindow 的返回值。</summary>
    public bool TryActivate(GameWindowObservation selected)
    {
        // 到点发送前的激活：游戏已在前台时立即返回，不读进程与客户端文件（已在到点前完成切换，见 ActionScheduler.PrimeLeadMs）。
        // 窗口身份由输入代理按绑定修订与窗口标识另行核对。
        var window = unchecked((nint)selected.WindowIdentity.Value);
        if (GetForegroundWindow() == window && IsWindow(window) && !IsIconic(window)) return true;
        return Activate(selected).Foreground;
    }
    public GameWindowActivation Activate(GameWindowObservation selected)
    {
        var window = unchecked((nint)selected.WindowIdentity.Value);
        if (Refresh(selected) is null) return new(false, false, false);
        if (IsIconic(window)) ShowWindow(window, 9);
        if (GetForegroundWindow() != window) BringToForeground(window);
        // 系统在恢复动画和前台切换后才更新状态，短暂轮询后按实际读到的结果回报。
        for (var attempt = 0; attempt < 5 && (IsIconic(window) || GetForegroundWindow() != window); attempt++)
            Thread.Sleep(40);
        return new(true, IsWindow(window) && !IsIconic(window), GetForegroundWindow() == window);
    }
    /// <summary>
    /// 宿主是后台进程，Windows 的前台锁会让它直接调用 SetForegroundWindow 失败（合奏开始时的 input.focusChanged）。
    /// 与原工具 WindowsGamePlatform.BringToForeground 相同：临时把本线程的输入队列挂到当前前台线程上，再置顶并前置游戏窗口，结束后解除。
    /// </summary>
    private static void BringToForeground(nint window)
    {
        var own = GetCurrentThreadId();
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = foreground != 0 && foreground != own && AttachThreadInput(own, foreground, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(own, foreground, false); }
    }
    private GameWindowObservation? Read(nint window, GameWindowObservation? expected)
    {
        if (!IsWindow(window)) return null;
        GetWindowThreadProcessId(window, out var pid);
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            if (!processes.Contains(process.ProcessName)) return null;
            var start = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if (expected is not null && (expected.ProcessId != (int)pid || expected.ProcessStartTime != start)) return null;
            // 客户区原点与尺寸取物理像素、DPI 取显示器有效 DPI（见 WindowsClientGeometry），与截图和鼠标坐标处于同一坐标系，
            // 游戏窗口未声明 DPI 感知或为系统级感知时同样成立，负坐标的副屏照常。
            if (WindowsClientGeometry.Read(window) is not { } client) return null;
            var minimized = IsIconic(window); var bounds = client.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                if (expected is null || !minimized) return null;
                bounds = expected.Bounds;
            }
            string? build = null;
            lock (preparedGate)
                if (prepared.TryGetValue(((int)pid, start), out var files))
                {
                    var image = WindowsProcessAccess.ImagePath((int)pid);
                    var stamps = image is not null && image.Equals(files.ImagePath, StringComparison.OrdinalIgnoreCase)
                        ? TwClientFingerprint.ReadFileStamps(files.FinalPath) : null;
                    if (stamps is not null && files.Files.SequenceEqual(stamps)) build = files.Build;
                    else prepared.Remove(((int)pid, start));
                }
            return new("TW", (int)pid, start, new(unchecked((ulong)window)), build, bounds, client.Dpi, client.Dpi, minimized);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException) { return null; }
    }
    private delegate bool EnumWindow(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
