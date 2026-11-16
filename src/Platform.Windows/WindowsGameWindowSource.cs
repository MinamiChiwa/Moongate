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
    private sealed record PreparedClient(int ProcessId, DateTimeOffset StartedAt, string ImagePath, IReadOnlyList<TwClientFileStamp> Files);
    private readonly object preparedGate = new();
    private readonly Dictionary<(int Pid, DateTimeOffset StartedAt), PreparedClient> prepared = [];
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
    public async ValueTask<GameWindowObservation?> PrepareAsync(GameWindowObservation selected, CancellationToken ct)
    {
        var fresh = Refresh(selected);
        if (fresh is null) return null;
        ct.ThrowIfCancellationRequested();
        if (fresh.GameBuild == TwClientFingerprint.GameBuild) return fresh;
        var key = (selected.ProcessId, selected.ProcessStartTime);
        lock (preparedGate) prepared.Remove(key);
        string? image;
        try
        {
            using var process = Process.GetProcessById(selected.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != selected.ProcessStartTime.UtcDateTime) return null;
            image = ImagePath(process);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        if (image is null) return fresh with { GameBuild = null };
        var before = TwClientFingerprint.ReadFileStamps(image);
        if (before is null || !await TwClientFingerprint.MatchesAsync(image, ct).ConfigureAwait(false)) return Refresh(selected);
        var after = TwClientFingerprint.ReadFileStamps(image);
        fresh = Refresh(selected);
        if (fresh is null || after is null || !before.SequenceEqual(after)) return fresh;
        lock (preparedGate)
        {
            if (prepared.Count >= 32) prepared.Remove(prepared.Keys.First());
            prepared[key] = new(selected.ProcessId, selected.ProcessStartTime, image, after);
        }
        return Refresh(selected);
    }
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
            if (!GetClientRect(window, out var rect)) return null;
            var point = new Point(); if (!ClientToScreen(window, ref point)) return null;
            var minimized = IsIconic(window); var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                if (expected is null || !minimized) return null;
                width = expected.Bounds.Width; height = expected.Bounds.Height; point.X = expected.Bounds.X; point.Y = expected.Bounds.Y;
            }
            var dpi = GetDpiForWindow(window); if (dpi == 0) return null;
            string? build = null;
            lock (preparedGate)
                if (prepared.TryGetValue(((int)pid, start), out var client))
                {
                    var image = ImagePath(process);
                    var stamps = image is not null && image.Equals(client.ImagePath, StringComparison.OrdinalIgnoreCase)
                        ? TwClientFingerprint.ReadFileStamps(image) : null;
                    if (stamps is not null && client.Files.SequenceEqual(stamps)) build = TwClientFingerprint.GameBuild;
                    else prepared.Remove(((int)pid, start));
                }
            return new("TW", (int)pid, start, new(unchecked((ulong)window)), build, new(point.X, point.Y, width, height), dpi, dpi, minimized);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException) { return null; }
    }
    private static string? ImagePath(Process process)
    {
        var path = new StringBuilder(32768); var length = path.Capacity;
        return QueryFullProcessImageName(process.Handle, 0, path, ref length) ? path.ToString() : null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindow(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int length);
}
