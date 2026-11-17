using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 吸管的真实桌面实现（Windows）：隐藏应用的全部可见窗口，按物理像素读取虚拟桌面边界，
/// 并以 <see cref="ScreenPickLayer"/> 覆盖整个虚拟桌面。
/// </summary>
public sealed class DesktopScreenPickHost : IScreenPickHost
{
    public static DesktopScreenPickHost Instance { get; } = new();

    public IDisposable HideWindows()
    {
        var windows = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .Where(window => window.IsVisible).ToList() ?? [];
        return new HiddenWindows(windows);
    }

    public Task SettleAsync(CancellationToken cancellationToken) => Task.Delay(150, cancellationToken);

    public PixelRect? DesktopBounds()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var physical = PhysicalPixels.Enter();
        if (!physical.Active) return null;
        var rect = new PixelRect(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    public Task<PickLayerResult> ShowLayerAsync(ScreenCapture capture, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new PickLayerResult(null, UiText.Pick("屏幕取色只支持 Windows", "螢幕取色只支援 Windows")));
        return new ScreenPickLayer(capture).RunAsync();
    }

    // 原工具恢复的是隐藏前可见的窗口；期间被关闭的窗口不再显示。
    private sealed class HiddenWindows : IDisposable
    {
        private readonly List<Window> windows;

        public HiddenWindows(List<Window> windows)
        {
            this.windows = windows;
            foreach (var window in windows)
            {
                window.Closed += OnClosed;
                window.Hide();
            }
        }

        public void Dispose()
        {
            foreach (var window in windows.ToList())
            {
                window.Closed -= OnClosed;
                try { window.Show(); }
                catch (InvalidOperationException) { }
            }
            windows.Clear();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (sender is Window window) windows.Remove(window);
        }
    }
}

/// <summary>
/// 全屏取色层（原工具 Eyedropper.qml）：无边框、置顶，客户区与虚拟桌面的物理边界完全重合，冻结画面 1:1 物理像素显示、不做平滑。
/// 画十字线、采样框与跟随标签；左键确认、右键或 Esc 取消（Esc 另以 GetAsyncKeyState 轮询兜底，取色层没有焦点时也有效）。
/// 取色位置一律读物理光标（GetCursorPos），不使用界面的逻辑坐标。
/// <para>
/// 混合缩放的多显示器上，窗口铺满虚拟桌面后系统会按面积最大的显示器发出 <c>WM_DPICHANGED</c>，Avalonia 默认按建议矩形改窗口大小，
/// 客户区随之偏离桌面边界。取色层拦下这条消息、保持创建时的缩放比例：逻辑尺寸 × 该比例始终等于物理客户区，画面仍是 1:1 物理像素。
/// </para>
/// </summary>
public sealed class ScreenPickLayer : Window
{
    private readonly ScreenCapture capture;
    private readonly WriteableBitmap frozen;
    private readonly PickMarks marks;
    private readonly TaskCompletionSource<PickLayerResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer escapeWatch = new() { Interval = TimeSpan.FromMilliseconds(30) };

    public ScreenPickLayer(ScreenCapture capture)
    {
        this.capture = capture;
        frozen = ColorBitmaps.Create(capture.Width, capture.Height, capture.Rgb);
        Title = UiText.Pick("屏幕吸管", "螢幕吸管");
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Shell.AppIcon.Apply(this);
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);
        Position = new PixelPoint(capture.X, capture.Y);
        // 先按原点所在显示器的缩放给出逻辑尺寸，打开后再以 SetWindowPos 按物理像素精确定位。
        var scale = Screens.ScreenFromPoint(Position)?.Scaling is > 0 and var value ? value : 1;
        Width = capture.Width / scale;
        Height = capture.Height / scale;
        var image = new Image { Source = frozen, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        marks = new PickMarks(this);
        Content = new Panel { Children = { image, marks } };
        escapeWatch.Tick += (_, _) =>
        {
            if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) Finish(PickLayerResult.Cancelled);
        };
        Opened += (_, _) => Pin();
        Closed += (_, _) =>
        {
            Finish(PickLayerResult.Cancelled);
            frozen.Dispose();
        };
    }

    public Task<PickLayerResult> RunAsync()
    {
        Show();
        return done.Task;
    }

    internal PixelPoint ClientOrigin => new(capture.X, capture.Y);

    internal string? HoverHex => PhysicalCursor() is { } point ? ScreenPickMath.HexAt(capture, point) : null;

    internal static PixelPoint? PhysicalCursor()
    {
        using var physical = PhysicalPixels.Enter();
        return physical.Active && Native.GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;
    }

    private const uint DpiChanged = 0x02E0;

    private static IntPtr KeepScale(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == DpiChanged) handled = true;
        return IntPtr.Zero;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        marks.InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.RightButtonPressed) Finish(PickLayerResult.Cancelled);
        else if (kind == PointerUpdateKind.LeftButtonPressed)
            Finish(PhysicalCursor() is { } point ? new PickLayerResult(point)
                : new PickLayerResult(null, UiText.Pick("无法读取鼠标物理坐标，请重试。", "無法讀取滑鼠實體座標，請重試。")));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Finish(PickLayerResult.Cancelled);
    }

    // 按物理像素把窗口放到虚拟桌面上，并核对客户区与桌面边界完全一致；不一致时报错而不是取错色。
    private void Pin()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            Finish(new(null, UiText.Pick("无法确认取色窗口，请重试。", "無法確認取色視窗，請重試。")));
            return;
        }
        // 先挂上 WM_DPICHANGED 拦截，再跨屏铺满：SetWindowPos 期间系统同步发出的缩放变化不会改动窗口大小。
        Win32Properties.AddWndProcHookCallback(this, KeepScale);
        using (var physical = PhysicalPixels.Enter())
        {
            if (!physical.Active)
            {
                Finish(new(null, UiText.Pick("无法进入物理像素坐标，请重试。", "無法進入實體像素座標，請重試。")));
                return;
            }
            if (!Native.SetWindowPos(handle, new IntPtr(-1), capture.X, capture.Y, capture.Width, capture.Height, 0x0040))
            {
                Finish(new(null, UiText.Pick("无法显示取色窗口，请重试。", "無法顯示取色視窗，請重試。")));
                return;
            }
            var origin = new Native.Point();
            if (!Native.GetClientRect(handle, out var rect) || !Native.ClientToScreen(handle, ref origin))
            {
                Finish(new(null, UiText.Pick("无法读取取色窗口边界，请重试。", "無法讀取取色視窗邊界，請重試。")));
                return;
            }
            var client = new PixelRect(origin.X, origin.Y, rect.Right, rect.Bottom);
            if (client != ScreenPickMath.Bounds(capture))
            {
                var (actual, expected) = (ScreenPickMath.Describe(client), ScreenPickMath.Describe(ScreenPickMath.Bounds(capture)));
                Finish(new(null, UiText.Pick($"取色窗口 {actual} 与冻结画面 {expected} 不一致，请重试。",
                    $"取色視窗 {actual} 與凍結畫面 {expected} 不一致，請重試。")));
                return;
            }
        }
        Activate();
        escapeWatch.Start();
    }

    private void Finish(PickLayerResult result)
    {
        if (done.Task.IsCompleted) return;
        escapeWatch.Stop();
        done.TrySetResult(result);
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>
    /// 十字线（两条贯穿全屏的 1 物理像素半透明白线）、采样框（9 px 黑框套 7 px 白框）与跟随标签（24 px 色块加等宽字体 HEX，
    /// 距光标 18 px，靠近右边或下边时翻到另一侧）。外观固定，不随主题变化。
    /// </summary>
    private sealed class PickMarks(ScreenPickLayer layer) : Control
    {
        private const double Offset = 18, CapsuleHeight = 40, Swatch = 24, Padding = 12, Spacing = 10;

        public override void Render(DrawingContext context)
        {
            if (PhysicalCursor() is not { } cursor) return;
            var scale = layer.RenderScaling;
            var origin = layer.ClientOrigin;
            // 光标所在物理像素的左上角，换成本控件的逻辑坐标。
            var px = cursor.X - origin.X;
            var py = cursor.Y - origin.Y;
            var pixel = 1 / scale;
            var x = px / scale;
            var y = py / scale;
            var line = Brush("EyedropperLineBrush");
            context.FillRectangle(line, new Rect(x, 0, pixel, Bounds.Height));
            context.FillRectangle(line, new Rect(0, y, Bounds.Width, pixel));
            context.DrawRectangle(new Pen(Brushes.Black, pixel), new Rect((px - 4 + 0.5) / scale, (py - 4 + 0.5) / scale, 8 * pixel, 8 * pixel));
            context.DrawRectangle(new Pen(Brushes.White, pixel), new Rect((px - 3 + 0.5) / scale, (py - 3 + 0.5) / scale, 6 * pixel, 6 * pixel));

            if (layer.HoverHex is not { } hex) return;
            var text = new FormattedText(hex, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas", FontStyle.Normal, FontWeight.SemiBold), 14, Brush("EyedropperTextBrush"));
            var width = Padding + Swatch + Spacing + text.Width + Padding;
            var left = x + Offset + width > Bounds.Width ? x - width - Offset : x + Offset;
            var top = y + Offset + CapsuleHeight > Bounds.Height ? y - CapsuleHeight - Offset : y + Offset;
            context.DrawRectangle(Brush("EyedropperCapsuleBrush"), new Pen(Brush("EyedropperCapsuleEdgeBrush"), 1),
                new RoundedRect(new Rect(left, top, width, CapsuleHeight), 8));
            context.DrawRectangle(new SolidColorBrush(Color.Parse(hex)), new Pen(Brush("EyedropperSwatchEdgeBrush"), 1),
                new RoundedRect(new Rect(left + Padding, top + (CapsuleHeight - Swatch) / 2, Swatch, Swatch), 4));
            context.DrawText(text, new Point(left + Padding + Swatch + Spacing, top + (CapsuleHeight - text.Height) / 2));
        }

        private IBrush Brush(string key) =>
            this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.White;
    }
}

/// <summary>
/// 在调用线程上临时切到每显示器 DPI 感知 v2，使系统度量、光标与窗口位置都是物理像素（原工具 window_dpi.physical_pixel_context）。
/// 切换后回读线程上下文确认确实是 v2（<see cref="Active"/>）；没进入时调用方不读坐标，避免拿到按缩放虚拟化的值。
/// </summary>
internal readonly struct PhysicalPixels : IDisposable
{
    private static readonly IntPtr PerMonitorV2 = new(-4);
    private readonly IntPtr previous;

    private PhysicalPixels(IntPtr previous, bool active)
    {
        this.previous = previous;
        Active = active;
    }

    public bool Active { get; }

    public static PhysicalPixels Enter()
    {
        if (!OperatingSystem.IsWindows()) return default;
        var previous = Native.SetThreadDpiAwarenessContext(PerMonitorV2);
        return new(previous, previous != IntPtr.Zero && Native.AreDpiAwarenessContextsEqual(Native.GetThreadDpiAwarenessContext(), PerMonitorV2));
    }

    public void Dispose()
    {
        if (previous != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(previous);
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr window, ref Point point);
}
