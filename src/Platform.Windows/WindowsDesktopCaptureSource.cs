using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopCaptureSource : IDesktopCaptureSource
{
    /// <summary>
    /// 在每显示器 DPI 感知 v2 上下文里按物理像素 BitBlt 指定区域（为空时整个虚拟桌面）。
    /// 截屏前核对区域完整落在虚拟桌面内，截屏后再读一次虚拟桌面边界，期间变化视为显示布局变化。
    /// </summary>
    public ValueTask<DesktopRgbFrame> CaptureAsync(PhysicalWindowRect? region, int maximumPixels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = SetThreadDpiAwarenessContext(new(-4));
        if (previous == 0) throw new InvalidOperationException("无法进入物理像素 DPI 上下文");
        nint screen = 0, memory = 0, bitmap = 0, selected = 0;
        try
        {
            var desktop = Bounds();
            var bounds = region ?? desktop;
            if (region is { } r && !Contains(desktop, r))
                throw new ArgumentOutOfRangeException(nameof(region), "截屏区域没有完整落在当前虚拟桌面内");
            if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > maximumPixels)
                throw new ArgumentException("桌面截图超出像素预算");
            screen = GetDC(0);
            if (screen == 0) throw new InvalidOperationException("无法读取桌面设备");
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw new InvalidOperationException("无法建立截图设备");
            var info = new BitmapInfo { Size = 40, Width = bounds.Width, Height = -bounds.Height,
                Planes = 1, BitCount = 32, Compression = 0 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new InvalidOperationException("无法建立截图缓冲区");
            selected = SelectObject(memory, bitmap);
            if (selected == 0 || selected == new nint(-1)) throw new InvalidOperationException("无法选择截图缓冲区");
            if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020) || !GdiFlush())
                throw new InvalidOperationException("桌面截图失败");
            if (Bounds() != desktop) throw new InvalidOperationException("截图期间显示器布局发生变化");
            cancellationToken.ThrowIfCancellationRequested();
            var rgb = new byte[checked(bounds.Width * bounds.Height * 3)];
            var row = new byte[checked(bounds.Width * 4)];
            for (var y = 0; y < bounds.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(bits + checked(y * row.Length), row, 0, row.Length);
                var target = y * bounds.Width * 3;
                for (var x = 0; x < bounds.Width; x++)
                { rgb[target++] = row[x * 4 + 2]; rgb[target++] = row[x * 4 + 1]; rgb[target++] = row[x * 4]; }
            }
            return ValueTask.FromResult(new DesktopRgbFrame(bounds, rgb));
        }
        finally
        {
            if (selected != 0 && selected != new nint(-1) && memory != 0) SelectObject(memory, selected);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private static bool Contains(PhysicalWindowRect desktop, PhysicalWindowRect region) =>
        region.Width > 0 && region.Height > 0 && region.X >= desktop.X && region.Y >= desktop.Y &&
        (long)region.X + region.Width <= (long)desktop.X + desktop.Width && (long)region.Y + region.Height <= (long)desktop.Y + desktop.Height;

    private static PhysicalWindowRect Bounds() => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
        public uint Color;
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
