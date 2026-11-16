using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Platform.Windows;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
internal sealed class NativeDesktopTestWindow : IAsyncDisposable
{
    private sealed record Work(Action Action, CancellationToken Token, TaskCompletionSource Completion);
    private const uint WorkMessage = 0x8001;
    private readonly ConcurrentQueue<Work> queue = new();
    private readonly object gate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private nint window;
    private uint threadId;
    private bool stopping;
    public nint Handle => window;
    public WindowsDesktopSurface Surface { get; private set; } = null!;

    private NativeDesktopTestWindow() => thread = new Thread(Run) { IsBackground = true, Name = "Native desktop surface fixture" };
    public static async Task<NativeDesktopTestWindow> StartAsync()
    {
        var result = new NativeDesktopTestWindow(); result.thread.Start();
        try { await result.ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); return result; }
        catch { if (result.threadId != 0) PostThreadMessage(result.threadId, 0x0012, 0, 0); await result.finished.Task; throw; }
    }
    public ValueTask InvokeAsync(Action action, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (stopping || finished.Task.IsCompleted) throw new InvalidOperationException("测试窗口已关闭");
            queue.Enqueue(new(action, token, done));
            if (!PostThreadMessage(threadId, WorkMessage, 0, 0)) done.TrySetException(new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        return new(done.Task.WaitAsync(token));
    }
    public ValueTask DestroyAsync() => InvokeAsync(() => { if (window != 0) { DestroyWindow(window); window = 0; } });
    private void Run()
    {
        var className = "MabiTool.NativeSurfaceTest." + Guid.NewGuid().ToString("N");
        var callback = new WindowProcedure((handle, message, wparam, lparam) => DefWindowProc(handle, message, wparam, lparam));
        var instance = GetModuleHandle(null);
        var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = callback, Instance = instance, ClassName = className };
        try
        {
            PeekMessage(out _, 0, 0, 0, 0);
            threadId = GetCurrentThreadId();
            if (RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            window = CreateWindowEx(0, className, "Desktop surface fixture", 0x00CF0000, 0, 0, 160, 100, 0, 0, instance, 0);
            if (window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            Surface = new(window, InvokeAsync);
            ready.TrySetResult();
            int status;
            while ((status = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (message.Window == 0 && message.Message == WorkMessage)
                {
                    while (queue.TryDequeue(out var work))
                    {
                        if (work.Completion.Task.IsCompleted) continue;
                        try { work.Token.ThrowIfCancellationRequested(); work.Action(); work.Completion.TrySetResult(); }
                        catch (OperationCanceledException) { work.Completion.TrySetCanceled(work.Token); }
                        catch (Exception error) { work.Completion.TrySetException(error); }
                    }
                }
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
            if (status < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch (Exception error) { ready.TrySetException(error); }
        finally
        {
            if (window != 0) DestroyWindow(window);
            UnregisterClass(className, instance);
            while (queue.TryDequeue(out var work)) work.Completion.TrySetException(new InvalidOperationException("测试窗口已关闭"));
            GC.KeepAlive(callback);
            finished.TrySetResult();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (!finished.Task.IsCompleted)
        {
            await Surface.DisposeAsync();
            lock (gate)
            {
                stopping = true;
                if (!PostThreadMessage(threadId, 0x0012, 0, 0) && !finished.Task.IsCompleted) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private delegate nint WindowProcedure(nint window, uint message, nuint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window; public uint Message; public nuint WParam; public nint LParam;
        public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, nint window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out NativeMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] private static extern bool PostThreadMessage(uint id, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameters);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")] private static extern bool UnregisterClass(string name, nint instance);
}
