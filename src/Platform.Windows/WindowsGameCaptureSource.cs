using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Platform.Windows;

/// <summary>
/// 截取绑定游戏窗口的客户区（与原工具 Color Studio <c>Game.capture</c> 相同：按屏幕区域截取，所见即所得）。
/// 截屏在每显示器 DPI 感知 v2 上下文按物理像素进行；返回的画面尺寸与绑定的客户区尺寸一致，
/// 绑定坐标为宿主进程 DPI 虚拟化后的逻辑尺寸时按面积平均缩到该尺寸，使截图、手势坐标与绑定处于同一坐标系。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameCaptureSource : IGameCaptureSource
{
    private const int MaximumSide = 16384;

    public ValueTask<GameCapture> CaptureAsync(GameWindowBinding binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var window = unchecked((nint)binding.Session.WindowIdentity.Value);
        var previous = SetThreadDpiAwarenessContext(new(-4));
        if (previous == 0) throw new GameInputException("capture.sourceFailed");
        nint screen = 0, memory = 0, bitmap = 0, selected = 0;
        try
        {
            if (!IsWindow(window)) throw new GameInputException("input.bindingChanged");
            if (IsIconic(window)) throw new GameInputException("input.bindingChanged");
            var area = ClientArea(window);
            if (area.Width <= 0 || area.Height <= 0 || area.Width > MaximumSide || area.Height > MaximumSide ||
                area.Width < binding.Bounds.Width || area.Height < binding.Bounds.Height)
                throw new GameInputException("capture.frameInvalid");
            screen = GetDC(0);
            if (screen == 0) throw new GameInputException("capture.sourceFailed");
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw new GameInputException("capture.sourceFailed");
            var info = new BitmapInfo { Size = 40, Width = area.Width, Height = -area.Height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new GameInputException("capture.sourceFailed");
            selected = SelectObject(memory, bitmap);
            if (selected == 0 || selected == new nint(-1)) throw new GameInputException("capture.sourceFailed");
            // CAPTUREBLT 与原工具 ImageGrab 的 all_screens 截图一致，包含分层窗口。
            if (!BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, 0x40CC0020) || !GdiFlush())
                throw new GameInputException("capture.sourceFailed");
            // 截图期间窗口移动或改变尺寸时，画面与绑定不再对应。
            if (ClientArea(window) != area) throw new GameInputException("input.bindingChanged");
            token.ThrowIfCancellationRequested();
            var bgra = new byte[checked(area.Width * area.Height * 4)];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            var rgb = area.Width == binding.Bounds.Width && area.Height == binding.Bounds.Height
                ? ToRgb(bgra, area.Width, area.Height)
                : Downsample(bgra, area.Width, area.Height, binding.Bounds.Width, binding.Bounds.Height);
            return ValueTask.FromResult(new GameCapture(binding.Bounds.Width, binding.Bounds.Height, rgb));
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

    private static PhysicalWindowRect ClientArea(nint window)
    {
        if (!GetClientRect(window, out var rect)) throw new GameInputException("input.bindingChanged");
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) throw new GameInputException("input.bindingChanged");
        return new(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static byte[] ToRgb(byte[] bgra, int width, int height)
    {
        var rgb = new byte[checked(width * height * 3)];
        for (int source = 0, target = 0; target < rgb.Length; source += 4)
        { rgb[target++] = bgra[source + 2]; rgb[target++] = bgra[source + 1]; rgb[target++] = bgra[source]; }
        return rgb;
    }

    /// <summary>按源像素覆盖面积加权平均缩小，纯色区域的颜色保持不变。</summary>
    private static byte[] Downsample(byte[] bgra, int width, int height, int targetWidth, int targetHeight)
    {
        var rgb = new byte[checked(targetWidth * targetHeight * 3)];
        var scaleX = (double)width / targetWidth;
        var scaleY = (double)height / targetHeight;
        var sums = new double[3];
        for (var y = 0; y < targetHeight; y++)
        {
            var top = y * scaleY; var bottom = top + scaleY;
            for (var x = 0; x < targetWidth; x++)
            {
                var left = x * scaleX; var right = left + scaleX;
                sums[0] = sums[1] = sums[2] = 0;
                var total = 0d;
                for (var sy = (int)top; sy < Math.Min(height, (int)Math.Ceiling(bottom)); sy++)
                {
                    var wy = Math.Min(bottom, sy + 1) - Math.Max(top, sy);
                    if (wy <= 0) continue;
                    for (var sx = (int)left; sx < Math.Min(width, (int)Math.Ceiling(right)); sx++)
                    {
                        var weight = wy * (Math.Min(right, sx + 1) - Math.Max(left, sx));
                        if (weight <= 0) continue;
                        var offset = (sy * width + sx) * 4;
                        sums[0] += bgra[offset + 2] * weight; sums[1] += bgra[offset + 1] * weight; sums[2] += bgra[offset] * weight;
                        total += weight;
                    }
                }
                var target = (y * targetWidth + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                    rgb[target + channel] = (byte)Math.Clamp((int)Math.Round(sums[channel] / total), 0, 255);
            }
        }
        return rgb;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
        public uint Color;
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Point point);
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
