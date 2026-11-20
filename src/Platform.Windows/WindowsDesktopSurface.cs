using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Platform.Windows;

/// <summary>在窗口所属 UI 线程创建；具体前端提供自己的异步调度入口。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopSurface : IDesktopSurface, IAsyncDisposable
{
    private const long Layered = 0x80000, Transparent = 0x20, Topmost = 0x8;
    private readonly nint window;
    private readonly uint threadId;
    private readonly string markerName = "MabiTool.desktop." + Guid.NewGuid().ToString("N");
    private readonly nint marker = (nint)Random.Shared.Next(1, int.MaxValue);
    private readonly Func<Action, CancellationToken, ValueTask> dispatch;
    private bool addedLayered;
    private bool disposed;
    public DesktopSurfaceCapabilities Capabilities { get; }

    public WindowsDesktopSurface(nint window, Func<Action, CancellationToken, ValueTask> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        this.window = window; this.dispatch = dispatch;
        threadId = GetWindowThreadProcessId(window, out var process);
        if (threadId == 0 || process != Environment.ProcessId || threadId != GetCurrentThreadId() || GetAncestor(window, 2) != window)
            throw new DesktopSurfaceException("desktop.surfaceUnavailable");
        var style = ReadLong(-20);
        var classStyle = GetClassLongPtr(window, -26).ToInt64();
        Capabilities = new(true, (style & Layered) != 0 || (classStyle & 0x60) == 0,
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), false);
        if (!SetProp(window, markerName, marker)) throw Failure();
    }

    public ValueTask<DesktopSurfaceState> ReadAsync(CancellationToken cancellationToken) =>
        OnThread(ReadState, cancellationToken);

    public ValueTask<DesktopSurfaceState> ApplyAsync(DesktopSurfaceChange change, CancellationToken cancellationToken) =>
        OnThread(() =>
        {
            Validate();
            if (change.Topmost is { } topmost)
            {
                if (!SetWindowPos(window, topmost ? (nint)(-1) : (nint)(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010)) throw Failure();
                // 部分 Windows 版本上，从未调整过层级的窗口第一次置顶会返回成功但不置顶；回读未生效时先取消置顶再置顶一次。
                if (topmost && (ReadLong(-20) & Topmost) == 0 &&
                    (!SetWindowPos(window, -2, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010) || !SetWindowPos(window, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010)))
                    throw Failure();
            }
            if (change.ClickThrough is { } clickThrough)
            {
                Validate();
                var original = ReadLong(-20);
                var style = original;
                if (clickThrough)
                {
                    style |= Transparent | Layered;
                    addedLayered = (original & Layered) == 0 || addedLayered;
                }
                else
                {
                    style &= ~Transparent;
                    if (addedLayered) style &= ~Layered;
                }
                WriteLong(-20, style);
                if (clickThrough && (original & Layered) == 0 && !SetLayeredWindowAttributes(window, 0, 255, 2))
                { WriteLong(-20, original); addedLayered = false; throw Failure(); }
                if (!clickThrough) addedLayered = false;
            }
            if (change.ExcludeFromCapture is { } hidden)
            { Validate(); if (!SetWindowDisplayAffinity(window, hidden ? 0x11u : 0)) throw Failure(); }
            return ReadState();
        }, cancellationToken);

    private async ValueTask<DesktopSurfaceState> OnThread(Func<DesktopSurfaceState> action, CancellationToken token)
    {
        DesktopSurfaceState? result = null;
        await dispatch(() =>
        {
            token.ThrowIfCancellationRequested();
            Validate();
            // 窗口状态调整进入后完成执行和回读，迟到取消由调用方查询实际结果。
            result = action();
        }, token);
        return result ?? throw new DesktopSurfaceException("desktop.threadMismatch");
    }
    private DesktopSurfaceState ReadState()
    {
        Validate();
        var style = ReadLong(-20);
        uint affinity = 0;
        if (Capabilities.ExcludeFromCapture && !GetWindowDisplayAffinity(window, out affinity)) throw Failure();
        return new((style & Topmost) != 0, (style & (Layered | Transparent)) == (Layered | Transparent), affinity == 0x11);
    }
    private void Validate()
    {
        if (disposed || GetCurrentThreadId() != threadId || !IsWindow(window) ||
            GetWindowThreadProcessId(window, out var process) != threadId || process != Environment.ProcessId ||
            GetProp(window, markerName) != marker) throw new DesktopSurfaceException("desktop.surfaceUnavailable");
    }
    private long ReadLong(int index)
    {
        Marshal.SetLastPInvokeError(0);
        var result = GetWindowLongPtr(window, index);
        if (result == 0 && Marshal.GetLastPInvokeError() != 0) throw Failure();
        return result.ToInt64();
    }
    private void WriteLong(int index, long value)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(window, index, (nint)value) == 0 && Marshal.GetLastPInvokeError() != 0) throw Failure();
    }
    private static DesktopSurfaceException Failure() => new(Marshal.GetLastPInvokeError() == 1400
        ? "desktop.surfaceUnavailable" : "desktop.applyFailed");
    public async ValueTask DisposeAsync()
    {
        await dispatch(() =>
        {
            if (disposed) return;
            if (IsWindow(window) && GetProp(window, markerName) == marker) RemoveProp(window, markerName);
            disposed = true;
        }, CancellationToken.None);
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern nint GetClassLongPtr(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
}
