using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Platform.Windows;

/// <summary>
/// 后台触控输入（移植自 MabiAgent 的共享触控，MIT 授权，来源登记见 scripts/maintenance/source-baselines.json 的 mabiagent 条目）。
/// 原理：在本进程创建一个合成触控设备，把触点注入到本进程的两个辅助窗口（一个远离手势的锚定小窗、一个只在触点处可点中的覆盖窗，
/// 都不激活、不抢前台），覆盖窗登记为传统触控窗口，系统为每帧生成 WM_TOUCH 句柄；宿主核对整条手势的帧序列与计划一致后，
/// 把这些 WM_TOUCH 消息逐帧转交（PostMessage）给游戏窗口。合成触点从不直达游戏，游戏窗口不被前置，用户的鼠标位置不变。
/// 按键用 WM_KEYDOWN／WM_KEYUP（按目标线程键盘布局取扫描码）投递，按键前先向游戏投递一次激活提示（WM_ACTIVATE），触控前撤回。
/// 前提：Windows 10 1809 及以上（合成指针设备）、游戏窗口存在且未最小化、手势在同一显示器内；不满足时返回原因码，不回退为前台输入。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBackgroundTouchInput : IGameBackgroundInputDevice
{
    private readonly object gate = new();
    private readonly HashSet<(nint Window, ushort Key)> held = [];
    private bool activationHinted;

    public string? Unavailable(GameWindowBinding binding)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return "input.backgroundUnsupported";
        var window = Handle(binding.Session.WindowIdentity);
        if (!TouchNative.IsWindow(window)) return "input.bindingUnavailable";
        if (TouchNative.IsIconic(window) || binding.IsMinimized) return "input.windowMinimized";
        TouchNative.GetWindowThreadProcessId(window, out var pid);
        return pid == (uint)binding.Session.ProcessId ? null : "input.bindingChanged";
    }

    public void Gesture(UInt64String window, IReadOnlyList<PhysicalScreenPoint[]> frames, int holdMilliseconds, int releaseDelayMilliseconds, Action checkpoint)
    {
        var target = Handle(window);
        lock (gate)
        {
            // 键盘激活提示可能来自之前的会话或进程；每次触控前按实际前台撤回，不依赖本实例的记录（来源同 MabiAgent PreparePointerInput）。
            if (TouchNative.Foreground != target) Post(target, 0x0006, 0, TouchNative.Foreground);
            activationHinted = false;
            var plan = new TouchGesturePlan(frames.Select(f => f.Select(p => new PixelPoint(p.X, p.Y)).ToArray()).ToArray(), releaseDelayMilliseconds);
            for (var attempt = 1; ; attempt++)
            {
                checkpoint();
                try { ForwardedTouchGesture.Execute(plan, target, holdMilliseconds, checkpoint); return; }
                catch (LocalTouchFramesUnavailableException) when (attempt < 3) { Thread.Sleep(150); }
                catch (LocalTouchFramesUnavailableException) { throw new GameInputException("input.backgroundFramesUnavailable"); }
            }
        }
    }

    public void Key(UInt64String window, ushort virtualKey, bool down)
    {
        var target = Handle(window);
        lock (gate)
        {
            var thread = TouchNative.GetWindowThreadProcessId(target, out _);
            if (thread == 0) throw new GameInputException("input.bindingChanged");
            if (down && !activationHinted) { Post(target, 0x0006, 1, 0); activationHinted = true; Thread.Sleep(10); }
            var scan = TouchNative.MapVirtualKeyEx(virtualKey, 4, TouchNative.GetKeyboardLayout(thread));
            if (scan == 0) throw new GameInputException("input.keyUnsupported");
            Post(target, down ? 0x0100u : 0x0101u, virtualKey, EncodeKey(scan, !down));
            if (down) held.Add((target, virtualKey)); else held.Remove((target, virtualKey));
        }
    }

    public void ReleaseAll()
    {
        lock (gate)
        {
            foreach (var (window, key) in held.ToArray())
                try
                {
                    var thread = TouchNative.GetWindowThreadProcessId(window, out _);
                    var scan = thread == 0 ? 0 : TouchNative.MapVirtualKeyEx(key, 4, TouchNative.GetKeyboardLayout(thread));
                    if (scan != 0) TouchNative.PostTouch(window, 0x0101, key, EncodeKey(scan, true));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            held.Clear();
        }
    }

    /// <summary>WM_KEYDOWN／WM_KEYUP 的 lParam：重复次数 1、扫描码、扩展键位，松开时置前一状态与转换位。</summary>
    public static nint EncodeKey(uint scan, bool up)
    {
        uint bits = 1 | ((scan & 0xff) << 16);
        if ((scan & 0xff00) == 0xe000) bits |= 1u << 24;
        if (up) bits |= (1u << 30) | (1u << 31);
        return unchecked((nint)(int)bits);
    }

    private static nint Handle(UInt64String window) => unchecked((nint)(long)window.Value);

    private static void Post(nint window, uint message, nuint value, nint flags)
    {
        if (!TouchNative.PostTouch(window, message, value, flags)) throw new GameInputException("input.outcomeUnknown");
    }
}

internal readonly record struct PixelPoint(int X, int Y);
internal readonly record struct PixelBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public PixelPoint Center => new(Left + Width / 2, Top + Height / 2);
    public bool Contains(PixelPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
}

internal sealed class LocalTouchFramesUnavailableException(string message) : Exception(message);

internal static class TouchNative
{
    internal const uint Down = 0x14006, Update = 0x24006, Up = 0x40000;
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(PixelPoint p) { X = p.X; Y = p.Y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointerInfo
    {
        public uint Type, Id, Frame, Flags; public nint Source, Target; public Point Pixel, Himetric, Raw, HimetricRaw;
        public uint Time, History; public int Input; public uint Keys; public ulong Performance; public uint Change;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct TouchInfo { public PointerInfo Pointer; public uint Flags, Mask; public Rect Contact, RawContact; public uint Orientation, Pressure; }
    [StructLayout(LayoutKind.Explicit, Size = 152)] internal struct TypeInfo { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public TouchInfo Touch; }
    [StructLayout(LayoutKind.Sequential)] internal struct LegacyTouchInput { public int X, Y; public nint Source; public uint Id, Flags, Mask, Time; public nuint Extra; public uint Width, Height; }
    internal static TypeInfo Contact(uint id, PixelPoint p, uint flags, nint target = 0) =>
        new() { Type = 2, Touch = new() { Pointer = new() { Type = 2, Id = id, Flags = flags, Pixel = new(p), Raw = new(p), Target = target } } };
    internal static nint Foreground => GetForegroundWindow();
    internal static nint At(PixelPoint p) { var old = SetThreadDpiAwarenessContext(-4); try { return WindowFromPoint(new(p)); } finally { SetThreadDpiAwarenessContext(old); } }
    internal static IDisposable PhysicalDpi() => new Dpi();
    private sealed class Dpi : IDisposable { private readonly nint old = SetThreadDpiAwarenessContext(-4); public Dpi() { if (old == 0) throw new Win32Exception(); } public void Dispose() => SetThreadDpiAwarenessContext(old); }
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint CreateSyntheticPointerDevice(uint type, uint count, uint feedback);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InjectSyntheticPointerInput(nint device, [In] TypeInfo[] pointers, uint count);
    [DllImport("user32.dll")] internal static extern void DestroySyntheticPointerDevice(nint device);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterTouchWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTouchInputInfo(nint input, uint count, [Out] LegacyTouchInput[] contacts, int size);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseTouchInputHandle(nint input);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostTouch(nint hwnd, uint message, nuint w, nint l);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern uint MapVirtualKeyEx(uint code, uint type, nint layout);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

/// <summary>辅助窗口线程的收尾：请求关闭后最多等两次，仍未退出视为异常。</summary>
internal static class TouchThreadCleanup
{
    internal static void Run(Thread thread, Action requestClose, Action complete, string error, int joinMilliseconds = 3000)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            requestClose();
            if (thread.Join(joinMilliseconds)) { complete(); return; }
        }
        throw new TimeoutException(error);
    }
}

/// <summary>一条 WM_TOUCH 消息及其句柄：只转交一次，转交成功后句柄归游戏窗口，失败时本地关闭且禁止重放。</summary>
internal sealed class TouchEnvelope(nuint w, nint handle, IReadOnlyList<(uint Id, uint Flags, int X, int Y)> contacts) : IDisposable
{
    private nint owned = handle;
    private bool attempted;
    internal IReadOnlyList<(uint Id, uint Flags, int X, int Y)> Contacts { get; } = contacts;
    internal void Forward(nint hwnd)
    {
        if (attempted || owned == 0) throw new GameInputException("input.outcomeUnknown");
        attempted = true;
        if (TouchNative.PostTouch(hwnd, 0x0240, w, owned)) { owned = 0; return; }
        Dispose();
        throw new GameInputException("input.outcomeUnknown");
    }
    public void Dispose() { var h = Interlocked.Exchange(ref owned, 0); if (h != 0) TouchNative.CloseTouchInputHandle(h); }
}

/// <summary>本进程的不激活辅助窗口：覆盖窗登记为传统触控窗口并保留 WM_TOUCH 句柄，锚定窗承接锚定触点。</summary>
internal sealed class TouchInputWindow : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WndProc proc;
    private readonly string name = "MoongateTouchInput_" + Guid.NewGuid().ToString("N");
    private readonly PixelBox box;
    private readonly bool legacyTouch;
    private nint hwnd;
    private int cleanupComplete;
    internal ConcurrentQueue<TouchEnvelope> Touches { get; } = new();
    internal nint Hwnd => ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    internal PixelPoint Point => box.Center;

    internal TouchInputWindow(PixelBox box, bool legacyTouch)
    {
        this.box = box; this.legacyTouch = legacyTouch; proc = Handle;
        thread = new Thread(Run) { IsBackground = true, Name = "moongate-touch-window" };
        thread.Start();
        _ = Hwnd;
    }

    /// <summary>覆盖窗只在每个起始触点周围 3×3 像素可点中，其他位置的点击穿到下层窗口。</summary>
    internal void LimitContactRegion(IEnumerable<PixelPoint> points)
    {
        var region = CreateRectRgn(0, 0, 0, 0); if (region == 0) throw new Win32Exception();
        try
        {
            foreach (var p in points)
            {
                var part = CreateRectRgn(p.X - box.Left - 1, p.Y - box.Top - 1, p.X - box.Left + 2, p.Y - box.Top + 2); if (part == 0) throw new Win32Exception();
                try { if (CombineRgn(region, region, part, 2) == 0) throw new Win32Exception(); } finally { DeleteObject(part); }
            }
            if (SetWindowRgn(Hwnd, region, true) == 0) throw new Win32Exception();
            region = 0;
        }
        finally { if (region != 0) DeleteObject(region); }
    }

    private void Run()
    {
        var module = GetModuleHandle(null); using var dpi = TouchNative.PhysicalDpi();
        try
        {
            var c = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = module, Name = name, Procedure = Marshal.GetFunctionPointerForDelegate(proc), Background = 6, Cursor = LoadCursor(0, 32512) };
            if (RegisterClassEx(ref c) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // WS_EX_NOACTIVATE|WS_EX_LAYERED|WS_EX_TOOLWINDOW|WS_EX_TOPMOST，几乎透明，不进任务栏、不抢前台。
            hwnd = CreateWindowEx(0x08080088u, name, "touch", 0x90000000, box.Left, box.Top, box.Width, box.Height, 0, 0, module, 0);
            if (hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetLayeredWindowAttributes(hwnd, 0, 1, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (legacyTouch && !TouchNative.RegisterTouchWindow(hwnd, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            ready.SetResult(hwnd);
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception error) { ready.TrySetException(error); }
        finally { if (hwnd != 0) DestroyWindow(hwnd); UnregisterClass(name, module); }
    }

    private nint Handle(nint h, uint m, nuint w, nint l)
    {
        if (m is 0x024b or 0x0021) return 3; // PA_NOACTIVATE / MA_NOACTIVATE
        if (m == 0x0240)
        {
            var count = (uint)(w & 0xffff); var contacts = new TouchNative.LegacyTouchInput[Math.Min(count, 10)];
            var ok = count is > 0 and <= 10 && TouchNative.GetTouchInputInfo(l, count, contacts, Marshal.SizeOf<TouchNative.LegacyTouchInput>());
            if (ok && Touches.Count < 64) Touches.Enqueue(new(w, l, contacts.Select(t => (t.Id, t.Flags, t.X, t.Y)).ToArray()));
            else TouchNative.CloseTouchInputHandle(l);
            return 0;
        }
        // 传统触控窗口需要默认的指针转换才能拿到 WM_TOUCH 句柄。
        if (m is >= 0x0245 and <= 0x024f) return legacyTouch ? DefWindowProc(h, m, w, l) : 0;
        if (m is 0x0200 or 0x0201 or 0x0202) return 0;
        if (m == 0x0010) { DestroyWindow(h); return 0; }
        if (m == 0x0002) { PostQuitMessage(0); return 0; }
        return DefWindowProc(h, m, w, l);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref cleanupComplete) != 0) return;
        TouchThreadCleanup.Run(thread, () => { if (hwnd != 0) PostMessage(hwnd, 0x0010, 0, 0); },
            () => { while (Touches.TryDequeue(out var touch)) touch.Dispose(); GC.KeepAlive(proc); Volatile.Write(ref cleanupComplete, 1); }, "触控辅助窗口未退出");
    }

    private delegate nint WndProc(nint h, uint m, nuint w, nint l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu, Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public TouchNative.Point Point; public uint Private; }
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint result, nint first, nint second, int mode);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass c);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message m, nint h, uint min, uint max);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message m);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message m);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint h, uint m, nuint w, nint l);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern nint LoadCursor(nint instance, nint resource);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint h, uint m, nuint w, nint l);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string cls, nint instance);
}

/// <summary>合成触控设备：锚定触点按住期间，目标触点只投向本进程的覆盖窗；注入坐标以虚拟桌面左上角为原点（含左侧、上方的显示器）。</summary>
internal sealed class SyntheticTouchDevice : IDisposable
{
    private nint device;
    private readonly PixelPoint anchor;
    private readonly nint anchorWindow;
    private nint targetWindow;
    private readonly PixelBox desktop = PhysicalDesktop.Bounds;
    private PixelPoint[] active = [];
    private bool anchorAttempted, targetAttempted;

    internal SyntheticTouchDevice(PixelPoint anchor, nint anchorWindow)
    {
        RequireLocalWindow(anchorWindow); this.anchor = anchor; this.anchorWindow = anchorWindow;
        using var dpi = TouchNative.PhysicalDpi();
        device = TouchNative.CreateSyntheticPointerDevice(2, 3, 3);
        if (device == 0) throw new GameInputException("input.backgroundUnsupported");
    }
    internal void Begin() { anchorAttempted = true; Send(TouchNative.Contact(1, anchor, TouchNative.Down, anchorWindow)); Thread.Sleep(20); }
    internal void Down(PixelPoint[] points, nint hwnd)
    {
        RequireLocalWindow(hwnd);
        active = points.ToArray(); targetWindow = hwnd; targetAttempted = true; SendTarget(TouchNative.Down);
    }
    internal void Move(PixelPoint[] points) { active = points.ToArray(); SendTarget(TouchNative.Update); }
    internal void Up() { if (!targetAttempted) return; SendTarget(TouchNative.Up); targetAttempted = false; }
    private void SendTarget(uint flags) => Send([TouchNative.Contact(1, anchor, TouchNative.Update, anchorWindow),
        .. active.Select((p, i) => TouchNative.Contact((uint)i + 2, p, flags, targetWindow))]);
    private void Send(params TouchNative.TypeInfo[] frame)
    {
        if (PhysicalDesktop.Bounds != desktop) throw new GameInputException("input.displayChanged");
        // 合成触点只能落在本进程的辅助窗口上，绝不直达游戏。
        foreach (var contact in frame)
        {
            var pointer = contact.Touch.Pointer;
            RequireLocalWindow(pointer.Target);
            if ((pointer.Flags & 0x10000) != 0 && TouchNative.At(new(pointer.Pixel.X, pointer.Pixel.Y)) != pointer.Target)
                throw new GameInputException("input.backgroundRejected");
        }
        var injected = frame.Select(contact =>
        {
            var p = contact.Touch.Pointer.Pixel;
            if (!desktop.Contains(new(p.X, p.Y))) throw new GameInputException("input.pointOutsideWindow");
            contact.Touch.Pointer.Pixel = new(new PixelPoint(checked(p.X - desktop.Left), checked(p.Y - desktop.Top)));
            contact.Touch.Pointer.Raw = contact.Touch.Pointer.Pixel;
            return contact;
        }).ToArray();
        using var dpi = TouchNative.PhysicalDpi();
        if (!TouchNative.InjectSyntheticPointerInput(device, injected, (uint)injected.Length)) throw new GameInputException("input.backgroundRejected");
    }
    private static void RequireLocalWindow(nint hwnd)
    {
        TouchNative.GetWindowThreadProcessId(hwnd, out var owner);
        if (owner != (uint)Environment.ProcessId) throw new GameInputException("input.backgroundRejected");
    }
    public void Dispose()
    {
        if (device == 0) return;
        try { Up(); } catch (GameInputException) { }
        try { if (anchorAttempted) { Thread.Sleep(20); Send(TouchNative.Contact(1, anchor, TouchNative.Up, anchorWindow)); } }
        catch (GameInputException) { }
        finally { TouchNative.DestroySyntheticPointerDevice(device); device = 0; }
    }
}

internal static class PhysicalDesktop
{
    internal static PixelBox MonitorBounds(PixelPoint point)
    {
        using var dpi = TouchNative.PhysicalDpi(); var monitor = MonitorFromPoint(new(point), 0); var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new GameInputException("input.pointOutsideWindow");
        return new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
    }
    internal static PixelBox Bounds { get { using var dpi = TouchNative.PhysicalDpi(); var x = GetSystemMetrics(76); var y = GetSystemMetrics(77); return new(x, y, x + GetSystemMetrics(78), y + GetSystemMetrics(79)); } }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public TouchNative.Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(TouchNative.Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}

/// <summary>一条手势：每帧一至两个触点（屏幕物理像素），结束后停留 <see cref="ReleaseDelayMilliseconds"/> 再松开，避免列表惯性。</summary>
internal sealed record TouchGesturePlan(PixelPoint[][] Points, int ReleaseDelayMilliseconds)
{
    internal int Steps => Points.Length - 1;
    internal int ContactCount => Points[0].Length;
    internal int MessageCount => Steps + 2 * ContactCount;
    internal PixelBox Bounds => new(Points.SelectMany(p => p).Min(p => p.X) - 12, Points.SelectMany(p => p).Min(p => p.Y) - 12,
        Points.SelectMany(p => p).Max(p => p.X) + 12, Points.SelectMany(p => p).Max(p => p.Y) + 12);

    internal void Generate(SyntheticTouchDevice device, nint cover, Action check)
    {
        check(); device.Begin(); check(); device.Down(Points[0], cover);
        foreach (var frame in Points.Skip(1)) { Thread.Sleep(20); check(); device.Move(frame); }
        Thread.Sleep(20); check(); device.Up(); device.Dispose();
    }

    internal List<TouchEnvelope> Collect(TouchInputWindow cover)
    {
        var result = new List<TouchEnvelope>(); var timer = Stopwatch.StartNew(); var ups = new HashSet<uint>();
        do
        {
            while (cover.Touches.TryDequeue(out var envelope))
            {
                result.Add(envelope);
                foreach (var contact in envelope.Contacts) if ((contact.Flags & 7) == 4) ups.Add(contact.Id);
            }
            if (ups.Count >= ContactCount || result.Count > MessageCount) break;
            Thread.Sleep(2);
        } while (timer.ElapsedMilliseconds < 250);
        try
        {
            if (result.Count == 0) throw new LocalTouchFramesUnavailableException("触控帧为空");
            Validate(result);
            return result;
        }
        catch { foreach (var envelope in result) envelope.Dispose(); throw; }
    }

    /// <summary>系统可能合并更新帧；边界帧与坐标仍须构成一条与计划一致、不倒退的完整手势，否则不转交任何一帧。</summary>
    internal void Validate(IReadOnlyList<TouchEnvelope> messages)
    {
        if (messages.Count < 2 * ContactCount || messages.Count > MessageCount) throw new GameInputException("input.backgroundRejected");
        var ids = new Dictionary<uint, int>(); var released = new HashSet<uint>(); var lastStep = 0;
        foreach (var message in messages)
        {
            var contacts = message.Contacts; var starting = ids.Count < ContactCount;
            var ending = contacts.Any(c => (c.Flags & 7) == 4) || released.Count > 0;
            if (starting && ending || contacts.Count != (starting ? ids.Count + 1 : ContactCount - released.Count)) throw new GameInputException("input.backgroundRejected");
            var transitions = 0;
            foreach (var c in contacts)
            {
                var flags = c.Flags & 7;
                if (flags == 2 && starting)
                {
                    var index = Array.FindIndex(Points[0], p => p.X * 100 == c.X && p.Y * 100 == c.Y);
                    if (index < 0 || ids.ContainsKey(c.Id) || ids.ContainsValue(index)) throw new GameInputException("input.backgroundRejected");
                    ids.Add(c.Id, index); transitions++;
                }
                else if (flags == 4 && ending) { if (!ids.ContainsKey(c.Id) || !released.Add(c.Id)) throw new GameInputException("input.backgroundRejected"); transitions++; }
                else if (flags != 1 || released.Contains(c.Id)) throw new GameInputException("input.backgroundRejected");
                if (!ids.ContainsKey(c.Id)) throw new GameInputException("input.backgroundRejected");
            }
            if ((starting || ending) && transitions != 1) throw new GameInputException("input.backgroundRejected");
            bool Matches(int step) => contacts.All(c => Points[step][ids[c.Id]] is var p && p.X * 100 == c.X && p.Y * 100 == c.Y);
            var matched = starting ? Matches(0) ? 0 : -1 : ending ? Matches(Steps) ? Steps : -1 : Enumerable.Range(lastStep, Steps - lastStep + 1).FirstOrDefault(Matches, -1);
            if (matched < 0) throw new GameInputException("input.backgroundRejected");
            lastStep = matched;
        }
        if (ids.Count != ContactCount || released.Count != ContactCount) throw new GameInputException("input.backgroundRejected");
    }
}

/// <summary>
/// 一次后台手势：先在本地辅助窗口上生成并收齐整条手势的 WM_TOUCH 帧、核对与计划一致，再逐帧转交给游戏窗口；
/// 每帧转交前调用 <c>checkpoint</c>（租约、绑定与取消），失败时立即补发剩余的松开帧，释放游戏里的触点。
/// 合成触控会让系统隐藏鼠标指针；结束后在用户没有操作鼠标时以零位移相对移动恢复显示，不改变指针位置。
/// </summary>
internal static class ForwardedTouchGesture
{
    internal static void Execute(TouchGesturePlan plan, nint target, int holdMilliseconds, Action checkpoint)
    {
        var monitor = PhysicalDesktop.MonitorBounds(plan.Points[0][0]);
        if (plan.Points.SelectMany(frame => frame).Any(point => !monitor.Contains(point))) throw new GameInputException("input.gestureCrossesDisplays");
        var cursor = CursorPosition();
        using var anchor = new TouchInputWindow(AnchorAwayFrom(plan.Bounds, cursor, monitor), legacyTouch: false);
        using var cover = new TouchInputWindow(plan.Bounds, legacyTouch: true);
        cover.LimitContactRegion(plan.Points[0]);
        if (!SpinWait.SpinUntil(() => TouchNative.At(anchor.Point) == anchor.Hwnd && plan.Points[0].All(point => TouchNative.At(point) == cover.Hwnd), 150))
            throw new GameInputException("input.backgroundRejected");
        List<TouchEnvelope>? messages = null;
        var releaseIndex = 0;
        var down = false;
        try
        {
            using (var device = new SyntheticTouchDevice(anchor.Point, anchor.Hwnd)) plan.Generate(device, cover.Hwnd, checkpoint);
            messages = plan.Collect(cover);
            releaseIndex = messages.Count - plan.ContactCount;
            for (var i = 0; i < releaseIndex; i++)
            {
                checkpoint();
                messages[i].Forward(target);
                down = true;
                if (i >= plan.ContactCount - 1) Wait(i == 0 && plan.Steps == 0 ? holdMilliseconds : 16, checkpoint);
            }
            Wait(plan.ReleaseDelayMilliseconds, checkpoint);
            for (var i = releaseIndex; i < messages.Count; i++) { releaseIndex = i + 1; messages[i].Forward(target); }
            down = false;
        }
        finally
        {
            // 中途失效（租约撤销、紧急停止、窗口变化）时补发松开帧，游戏里不留按下的触点。
            if (down && messages is not null)
                for (var i = releaseIndex; i < messages.Count; i++)
                    try { messages[i].Forward(target); } catch (GameInputException) { }
            if (messages is not null) foreach (var message in messages) message.Dispose();
            RestoreCursor();
        }
    }

    private static void Wait(int milliseconds, Action check)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { check(); Thread.Sleep(Math.Min(10, Math.Max(1, milliseconds - (int)timer.ElapsedMilliseconds))); }
        check();
    }

    /// <summary>锚定小窗放在显示器四角中离手势最远、且不在鼠标下的一角。</summary>
    internal static PixelBox AnchorAwayFrom(PixelBox bounds, PixelPoint cursor, PixelBox desktop)
    {
        foreach (var p in new[] { new PixelPoint(desktop.Left + 40, desktop.Top + 40), new PixelPoint(desktop.Right - 40, desktop.Top + 40),
                     new PixelPoint(desktop.Left + 40, desktop.Bottom - 40), new PixelPoint(desktop.Right - 40, desktop.Bottom - 40) }
                     .OrderByDescending(p => Math.Pow(p.X - bounds.Center.X, 2) + Math.Pow(p.Y - bounds.Center.Y, 2)))
        {
            var box = new PixelBox(p.X - 12, p.Y - 12, p.X + 12, p.Y + 12);
            if ((box.Right <= bounds.Left || box.Left >= bounds.Right || box.Bottom <= bounds.Top || box.Top >= bounds.Bottom) && !box.Contains(cursor)) return box;
        }
        throw new GameInputException("input.backgroundRejected");
    }

    private static PixelPoint CursorPosition()
    {
        using var dpi = TouchNative.PhysicalDpi();
        var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) ? new(info.Position.X, info.Position.Y) : new(int.MinValue, int.MinValue);
    }

    // 触控后系统把指针置为隐藏（CURSOR_SUPPRESSED）；用户 40 毫秒内没有移动鼠标、也没有按键时补一次零位移相对移动恢复显示。
    private static void RestoreCursor()
    {
        try
        {
            using var dpi = TouchNative.PhysicalDpi();
            var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref info) || (info.Flags & 2) == 0) return;
            var before = info.Position;
            Thread.Sleep(40);
            if (!GetCursorInfo(ref info) || info.Position.X != before.X || info.Position.Y != before.Y) return;
            if (new[] { 1, 2, 4 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) return;
            var packet = new MousePacket { Type = 0, Mouse = new() { Flags = 1u, Extra = 0x4d47544d } };
            SendInput(1, [packet], Marshal.SizeOf<MousePacket>());
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public uint Size, Flags; public nint Cursor; public TouchNative.Point Position; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct MousePacket { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseData Mouse; }
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, MousePacket[] inputs, int size);
}
