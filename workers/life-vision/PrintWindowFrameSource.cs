// 移植自 MabiAgent 82bc0ef 的 dotnet/MabiAgent.Windows/PrintWindowCapture.cs 的 PrintWindowCaptureEntry.Capture（MIT，Copyright (c) 2026 MinamiChiwa（南千和））。
// 修改原因：原实现把 PrintWindow 放进一次性的 helper 进程，经管道交换请求与整帧；Moongate 的整个 Worker 本身就是隔离进程，
// 截图改在 Worker 内的专用线程执行，帧不再经任何管道，卡住的 PrintWindow 由宿主按“3 秒无帧”终止整个 Worker。
// 窗口核对沿用原实现（进程号、创建时间、客户区尺寸与屏幕原点），失败原因按契约分为 input.bindingChanged 与 capture.sourceFailed；
// 不再比较 GetDpiForWindow：绑定里的 dpi 是显示器有效 DPI，DPI 虚拟化的窗口自身 DPI 不同。
// 帧缓冲与 BGRA 缓冲只分配一次并复用；GDI 对象每帧创建并在 finally 释放。

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.LifeVision.Worker;

/// <summary>
/// 用 <c>PrintWindow(hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT)</c> 截取游戏窗口客户区：被其他窗口遮挡时仍得到游戏自己的画面。
/// 截图在专用线程执行；<see cref="CaptureAsync"/> 只等待该线程的结果，调用方取消时立即返回，线程继续完成当前调用
/// （PrintWindow 卡住时线程一直占用，之后的调用以 <c>capture.sourceFailed</c> 失败，进程由宿主终止）。线程是后台线程，不阻止进程退出。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrintWindowFrameSource : IFrameSource
{
    private const uint PrintWindowClientOnlyRenderFullContent = 3;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly nint PerMonitorAwareV2 = -4;

    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0);
    private readonly RgbImage frame = new(LifeVisionProfiles.ClientWidth, LifeVisionProfiles.ClientHeight,
        new byte[LifeVisionProfiles.ClientWidth * LifeVisionProfiles.ClientHeight * 3]);
    private readonly byte[] bgra = new byte[LifeVisionProfiles.ClientWidth * LifeVisionProfiles.ClientHeight * 4];
    private Request? pending;
    private bool busy;
    private bool disposed;

    public PrintWindowFrameSource()
    {
        new Thread(Loop) { IsBackground = true, Name = "life-vision-capture" }.Start();
    }

    public ValueTask<RgbImage> CaptureAsync(LifeVisionWatchArguments binding, CancellationToken cancellationToken)
    {
        var request = new Request(binding);
        lock (gate)
        {
            if (disposed || busy) throw new WorkerException("capture.sourceFailed");
            busy = true;
            pending = request;
        }
        wake.Release();
        return new ValueTask<RgbImage>(request.Completion.Task.WaitAsync(cancellationToken));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        wake.Release();
    }

    private void Loop()
    {
        while (true)
        {
            wake.Wait();
            Request? request;
            lock (gate)
            {
                if (disposed) return;
                request = pending;
                pending = null;
            }
            if (request is null) continue;
            RgbImage? result = null;
            Exception? failure = null;
            try { result = CaptureOnce(request.Binding); }
            catch (Exception error) { failure = error; }
            // 先释放占用再完成等待：调用方一旦继续就可能立即发起下一次截图，不能撞上尚未清除的占用标记。
            lock (gate) busy = false;
            if (failure is not null) request.Completion.TrySetException(failure);
            else request.Completion.TrySetResult(result!);
        }
    }

    /// <summary>核对窗口并截取一帧；窗口与请求不符为 input.bindingChanged，系统调用失败为 capture.sourceFailed。</summary>
    private RgbImage CaptureOnce(LifeVisionWatchArguments binding)
    {
        var width = LifeVisionProfiles.ClientWidth;
        var height = LifeVisionProfiles.ClientHeight;
        var previousContext = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        if (previousContext == 0) throw new WorkerException("capture.sourceFailed");
        try
        {
            var hwnd = new nint(unchecked((long)binding.WindowIdentity.Value));
            if (GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId != (uint)binding.ProcessId)
                throw new WorkerException("input.bindingChanged");
            using (var process = OpenProcess(ProcessQueryLimitedInformation, false, processId))
            {
                // 进程已退出视为绑定变化；拒绝访问是系统权限问题，不是窗口变化。
                if (process.IsInvalid) throw new WorkerException(Marshal.GetLastWin32Error() == 5 ? "capture.sourceFailed" : "input.bindingChanged");
                if (!GetProcessTimes(process, out var created, out _, out _, out _) ||
                    DateTime.FromFileTimeUtc(created).Ticks != binding.ProcessStartTime.UtcTicks)
                    throw new WorkerException("input.bindingChanged");
            }
            if (!GetClientRect(hwnd, out var client) || client.Right - client.Left != width || client.Bottom - client.Top != height)
                throw new WorkerException("input.bindingChanged");
            var origin = new Point();
            if (!ClientToScreen(hwnd, ref origin) || origin.X != binding.Client.X || origin.Y != binding.Client.Y)
                throw new WorkerException("input.bindingChanged");

            var windowDc = GetDC(hwnd);
            if (windowDc == 0) throw new WorkerException("capture.sourceFailed");
            nint memoryDc = 0, bitmap = 0, previousBitmap = 0;
            try
            {
                memoryDc = CreateCompatibleDC(windowDc);
                if (memoryDc == 0) throw new WorkerException("capture.sourceFailed");
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32,
                        SizeImage = (uint)(width * height * 4)
                    }
                };
                bitmap = CreateDIBSection(windowDc, ref info, 0, out var data, 0, 0);
                if (bitmap == 0 || data == 0) throw new WorkerException("capture.sourceFailed");
                previousBitmap = SelectObject(memoryDc, bitmap);
                if (previousBitmap == 0 || previousBitmap == -1) throw new WorkerException("capture.sourceFailed");
                if (!PrintWindow(hwnd, memoryDc, PrintWindowClientOnlyRenderFullContent)) throw new WorkerException("capture.sourceFailed");
                Marshal.Copy(data, bgra, 0, bgra.Length);
            }
            finally
            {
                if (previousBitmap != 0 && previousBitmap != -1 && memoryDc != 0) SelectObject(memoryDc, previousBitmap);
                if (memoryDc != 0) DeleteDC(memoryDc);
                if (bitmap != 0) DeleteObject(bitmap);
                ReleaseDC(hwnd, windowDc);
            }
            var rgb = frame.Pixels;
            for (int source = 0, target = 0; source < bgra.Length; source += 4, target += 3)
            {
                rgb[target] = bgra[source + 2];
                rgb[target + 1] = bgra[source + 1];
                rgb[target + 2] = bgra[source];
            }
            return frame;
        }
        finally { SetThreadDpiAwarenessContext(previousContext); }
    }

    private sealed class Request(LifeVisionWatchArguments binding)
    {
        public LifeVisionWatchArguments Binding { get; } = binding;
        public TaskCompletionSource<RgbImage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
}
