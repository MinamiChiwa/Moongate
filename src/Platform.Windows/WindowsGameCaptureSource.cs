using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Platform.Windows;

/// <summary>
/// 截取绑定游戏窗口的客户区（与原工具 Color Studio <c>Game.capture</c> 相同：按屏幕区域截取，所见即所得）。
/// 截屏在每显示器 DPI 感知 v2 上下文按物理像素进行，绑定的客户区同样是物理像素（<see cref="WindowsClientGeometry"/>），
/// 画面逐像素对应游戏实际画面与手势坐标；客户区尺寸与绑定不一致时窗口已改变，按绑定变化结束本次截图。
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
            if (area.Width <= 0 || area.Height <= 0 || area.Width > MaximumSide || area.Height > MaximumSide)
                throw new GameInputException("capture.frameInvalid");
            if (area != binding.Bounds) throw new GameInputException("input.bindingChanged");
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
            return ValueTask.FromResult(new GameCapture(area.Width, area.Height, ToRgb(bgra, area.Width, area.Height)));
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

    // 与绑定同一口径：物理像素，游戏窗口被系统 DPI 虚拟化时同样成立（见 WindowsClientGeometry）。
    private static PhysicalWindowRect ClientArea(nint window) =>
        WindowsClientGeometry.Read(window)?.Bounds ?? throw new GameInputException("input.bindingChanged");

    private static byte[] ToRgb(byte[] bgra, int width, int height)
    {
        var rgb = new byte[checked(width * height * 3)];
        for (int source = 0, target = 0; target < rgb.Length; source += 4)
        { rgb[target++] = bgra[source + 2]; rgb[target++] = bgra[source + 1]; rgb[target++] = bgra[source]; }
        return rgb;
    }

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
