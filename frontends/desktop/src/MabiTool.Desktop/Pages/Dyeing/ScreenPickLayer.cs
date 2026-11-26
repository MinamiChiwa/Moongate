using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Overlay;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>
/// 吸管的真实桌面实现（Windows）：隐藏应用的全部可见窗口，按物理像素列出显示器，
/// 并在每块显示器上铺一层 <see cref="ScreenPickLayer"/>（由 <see cref="ScreenPickLayers"/> 统一管理）。
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

    public IReadOnlyList<PixelRect>? Monitors()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var physical = PhysicalPixels.Enter();
        if (!physical.Active) return null;
        var monitors = new List<PixelRect>();
        var ok = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                var rect = info.Monitor;
                monitors.Add(new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            }
            return true;
        }, IntPtr.Zero);
        // 固定顺序，前后两次读取可以逐项比较。
        return ok && monitors.Count > 0 ? monitors.OrderBy(rect => rect.X).ThenBy(rect => rect.Y).ToList() : null;
    }

    public IPickLayers ShowLayers(IReadOnlyList<PixelRect> monitors)
    {
        if (!OperatingSystem.IsWindows())
            return new RefusedLayers(UiText.Pick("屏幕取色只支持 Windows", "螢幕取色只支援 Windows"));
        // 取色层要避开截图（WDA_EXCLUDEFROMCAPTURE），Windows 10 2004 起才有。
        if (!OverlayWindow.CaptureExclusionSupported)
            return new RefusedLayers(UiText.Pick("屏幕取色需要 Windows 10 2004 或更新的系统", "螢幕取色需要 Windows 10 2004 或更新的系統"));
        return new ScreenPickLayers(monitors);
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

    /// <summary>不能显示取色层时只报告原因。</summary>
    private sealed class RefusedLayers(string reason) : IPickLayers
    {
        public ValueTask<PickEvent> NextAsync(CancellationToken cancellationToken) => ValueTask.FromResult<PickEvent>(new PickEvent.Failed(reason));
        public void ShowCapturing(int monitor) { }
        public void ShowFrozen(int monitor, ScreenCapture capture) { }
        public void ShowNotice(int monitor, string message) { }
        public void Dispose() { }
    }
}

/// <summary>
/// 每块显示器一层取色层。每 30 ms 读一次物理光标：移到另一块显示器（且那层已就绪）时报告进入，并刷新十字线；
/// Esc 另以 <c>GetAsyncKeyState</c> 轮询，取色层没有焦点时也能取消。层的点击、取消与错误都汇入同一个事件队列。
/// </summary>
internal sealed class ScreenPickLayers : IPickLayers
{
    private readonly IReadOnlyList<PixelRect> monitors;
    private readonly List<ScreenPickLayer> layers = [];
    private readonly Channel<PickEvent> events = Channel.CreateUnbounded<PickEvent>();
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private int current = -1;
    private PixelPoint? lastCursor;
    private bool closed;

    public ScreenPickLayers(IReadOnlyList<PixelRect> monitors)
    {
        this.monitors = monitors;
        foreach (var monitor in monitors) layers.Add(new ScreenPickLayer(this, monitor));
        foreach (var layer in layers) layer.Show();
        // 光标所在的那层取得焦点，键盘 Esc 直接送到取色层。
        var start = ScreenPickLayer.PhysicalCursor() is { } cursor ? ScreenPickMath.MonitorAt(monitors, cursor) : -1;
        if (start >= 0 && layers[start].Ready) layers[start].Activate();
        watch.Tick += (_, _) => Watch();
        watch.Start();
    }

    internal void Post(PickEvent value)
    {
        if (!closed) events.Writer.TryWrite(value);
    }

    private void Watch()
    {
        if (closed) return;
        if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0)
        {
            Post(PickEvent.Cancelled.Instance);
            return;
        }
        var cursor = ScreenPickLayer.PhysicalCursor();
        var index = cursor is { } point ? ScreenPickMath.MonitorAt(monitors, point) : -1;
        // 还没就绪（尚未避开截图）的层不报告进入，下一轮再判断。
        if (index != current && (index < 0 || layers[index].Ready))
        {
            if (current >= 0) layers[current].Refresh();
            current = index;
            if (index >= 0) Post(new PickEvent.Entered(index));
        }
        if (cursor != lastCursor && current >= 0) layers[current].Refresh();
        lastCursor = cursor;
    }

    public ValueTask<PickEvent> NextAsync(CancellationToken cancellationToken) => events.Reader.ReadAsync(cancellationToken);

    public void ShowCapturing(int monitor) => OnUi(() => layers[monitor].ShowNotice(UiText.Pick("正在截取这块屏幕…", "正在擷取這塊螢幕…")));

    public void ShowFrozen(int monitor, ScreenCapture capture) => OnUi(() => layers[monitor].Freeze(capture));

    public void ShowNotice(int monitor, string message) => OnUi(() => layers[monitor].ShowNotice(message));

    public void Dispose() => OnUi(() =>
    {
        if (closed) return;
        closed = true;
        watch.Stop();
        foreach (var layer in layers) layer.CloseByOwner();
        events.Writer.TryComplete();
    });

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>
/// 一块显示器上的取色层（原工具 Eyedropper.qml）：无边框、置顶，客户区与这块显示器的物理边界完全重合。
/// <para>
/// 冻结前是几乎透明（不透明度 1/255）的一层，接住这块显示器上的全部点击，不让它们落到游戏或其他程序；这时窗口设为
/// <c>WDA_EXCLUDEFROMCAPTURE</c>，截取这块显示器时画面里没有这一层与“正在截取”提示，像素与屏幕完全一致。设置后回读确认，
/// 没生效时报错而不截屏。冻结后显示 1:1 物理像素的冻结画面（最近邻、不平滑），并取消截图排除，基于屏幕采集的远程桌面也能看到冻结画面；
/// 各层只覆盖自己的显示器，截取别的显示器时不在区域内。
/// </para>
/// <para>
/// 窗口的物理矩形由 <c>WM_WINDOWPOSCHANGING</c> 钉在这块显示器上：从创建起，任何移动或改尺寸（包括 Avalonia 按逻辑尺寸回算的布局调整、
/// 缩放变化时系统建议的矩形）都改成这块显示器的物理矩形。Avalonia 12 的窗口创建在主屏，隐藏状态下移到另一块缩放不同的显示器时，
/// 平台层改用目标缩放，界面却仍按创建时的缩放布局与绘制（收不到缩放变化通知）；所以打开后以 <c>SetWindowPos</c> 就位并核对客户区，
/// 再读窗口 DPI，界面缩放不同就补发一次 <c>WM_DPICHANGED</c>（建议矩形即这块显示器）让界面同步，同步后再核对缩放与客户区。
/// 就位后再收到 <c>WM_DPICHANGED</c>（DPI 与就位时不同）或 <c>WM_DISPLAYCHANGE</c> 说明显示布局在取色期间变化，报错结束。
/// </para>
/// 冻结画面、十字线、采样框与跟随标签都按物理像素 ÷ 当前界面缩放换算，不依赖窗口逻辑尺寸。
/// 取色位置一律读物理光标（GetCursorPos），不使用界面的逻辑坐标。左键确认、右键或 Esc 取消。
/// </summary>
internal sealed class ScreenPickLayer : Window
{
    private const uint ExcludeFromCapture = 0x11, NoAffinity = 0, DpiChanged = 0x02E0, DisplayChanged = 0x007E, PositionChanging = 0x0046;
    private const uint NoSize = 0x0001, NoMove = 0x0002;
    private readonly ScreenPickLayers owner;
    private readonly PixelRect monitor;
    private readonly FrozenView view;
    private readonly Border notice;
    private readonly TextBlock noticeText = new() { Foreground = Brushes.White, FontSize = 16, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
    private readonly PickMarks marks;
    private ScreenCapture? capture;
    private WriteableBitmap? frozen;
    private IntPtr handle;
    private uint pinnedDpi;
    private bool closingByOwner;

    public ScreenPickLayer(ScreenPickLayers owner, PixelRect monitor)
    {
        this.owner = owner;
        this.monitor = monitor;
        Title = UiText.Pick("屏幕吸管", "螢幕吸管");
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Shell.AppIcon.Apply(this);
        Cursor = new Cursor(StandardCursorType.Cross);
        // 先挂钩子：从隐藏状态的第一次移动起，窗口矩形就等于这块显示器的物理矩形，窗口 DPI 随之取这块显示器的。
        Win32Properties.AddWndProcHookCallback(this, WatchLayout);
        Position = monitor.Position;
        // 逻辑尺寸按这块显示器的缩放给出；物理尺寸由钩子钉住，打开后再以 SetWindowPos 就位并核对。
        var scale = Screens.ScreenFromPoint(monitor.Position)?.Scaling is > 0 and var value ? value : 1;
        var size = ScreenPickMath.LogicalSize(monitor.Size, scale);
        Width = size.Width;
        Height = size.Height;
        view = new FrozenView(this);
        notice = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(230, 24, 28, 36)),
            Child = noticeText
        };
        marks = new PickMarks(this);
        Content = new Panel { Children = { view, notice, marks } };
        ScalingChanged += (_, _) =>
        {
            view.InvalidateVisual();
            marks.InvalidateVisual();
        };
        Opened += (_, _) => Pin();
        Closed += (_, _) =>
        {
            if (!closingByOwner) owner.Post(PickEvent.Cancelled.Instance);
            frozen?.Dispose();
        };
    }

    /// <summary>已就位并避开截图，可以截取这块显示器。</summary>
    internal bool Ready { get; private set; }

    internal string? HoverHex => capture is { } frame && PhysicalCursor() is { } point ? ScreenPickMath.HexAt(frame, point) : null;

    internal static PixelPoint? PhysicalCursor()
    {
        using var physical = PhysicalPixels.Enter();
        return physical.Active && Native.GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;
    }

    internal void Refresh() => marks.InvalidateVisual();

    internal void ShowNotice(string text)
    {
        noticeText.Text = text;
        notice.IsVisible = true;
    }

    internal void Freeze(ScreenCapture frame)
    {
        capture = frame;
        frozen = ColorBitmaps.Create(frame.Width, frame.Height, frame.Rgb);
        view.InvalidateVisual();
        notice.IsVisible = false;
        Background = Brushes.Black;
        if (handle != IntPtr.Zero) Native.SetWindowDisplayAffinity(handle, NoAffinity);
        marks.InvalidateVisual();
    }

    internal void CloseByOwner()
    {
        closingByOwner = true;
        Close();
    }

    private void Fail(string reason) => owner.Post(new PickEvent.Failed(reason));

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
        if (kind == PointerUpdateKind.RightButtonPressed) owner.Post(PickEvent.Cancelled.Instance);
        // 还没冻结（正在截取、超过上限或尚未进入）的显示器上，左键只被挡下、不算确认：用户还没看到冻结画面。
        else if (kind == PointerUpdateKind.LeftButtonPressed && capture is not null)
            owner.Post(PhysicalCursor() is { } point ? new PickEvent.Confirmed(point)
                : new PickEvent.Failed(UiText.Pick("无法读取鼠标物理坐标，请重试。", "無法讀取滑鼠實體座標，請重試。")));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        owner.Post(PickEvent.Cancelled.Instance);
    }

    // 避开截图，移到这块显示器并按物理像素铺满，核对客户区与显示器边界完全一致，界面缩放与窗口 DPI 一致；任一步不成立时报错而不是截错或取错色。
    private void Pin()
    {
        handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            Fail(UiText.Pick("无法确认取色窗口，请重试。", "無法確認取色視窗，請重試。"));
            return;
        }
        if (!Native.SetWindowDisplayAffinity(handle, ExcludeFromCapture) ||
            !Native.GetWindowDisplayAffinity(handle, out var affinity) || affinity != ExcludeFromCapture)
        {
            Fail(UiText.Pick("无法让取色层避开截图，请重试。", "無法讓取色層避開截圖，請重試。"));
            return;
        }
        using (var physical = PhysicalPixels.Enter())
        {
            if (!physical.Active)
            {
                Fail(UiText.Pick("无法进入物理像素坐标，请重试。", "無法進入實體像素座標，請重試。"));
                return;
            }
            if (!Native.SetWindowPos(handle, new IntPtr(-1), monitor.X, monitor.Y, monitor.Width, monitor.Height, 0x0010 | 0x0040))
            {
                Fail(UiText.Pick("无法显示取色窗口，请重试。", "無法顯示取色視窗，請重試。"));
                return;
            }
            if (!ClientMatches()) return;
            var dpi = Native.GetDpiForWindow(handle);
            if (dpi == 0)
            {
                Fail(UiText.Pick("无法读取取色窗口缩放，请重试。", "無法讀取取色視窗縮放，請重試。"));
                return;
            }
            if (!ScreenPickMath.SameScaling(RenderScaling, dpi))
            {
                // 隐藏时移屏，平台层已改用这块显示器的缩放，界面没收到通知；补发一次缩放变化，建议矩形就是这块显示器。
                var target = new Native.Rect { Left = monitor.X, Top = monitor.Y, Right = monitor.Right, Bottom = monitor.Bottom };
                Native.SendMessage(handle, DpiChanged, new IntPtr(dpi << 16 | dpi), ref target);
                if (!ScreenPickMath.SameScaling(RenderScaling, dpi))
                {
                    var (actual, expected) = (RenderScaling.ToString("0.###", CultureInfo.InvariantCulture), (dpi / 96.0).ToString("0.###", CultureInfo.InvariantCulture));
                    Fail(UiText.Pick($"取色窗口缩放 {actual} 与屏幕缩放 {expected} 不一致，请重试。", $"取色視窗縮放 {actual} 與螢幕縮放 {expected} 不一致，請重試。"));
                    return;
                }
                if (!ClientMatches()) return;
            }
            pinnedDpi = dpi;
        }
        Ready = true;
    }

    private bool ClientMatches()
    {
        var origin = new Native.Point();
        if (!Native.GetClientRect(handle, out var rect) || !Native.ClientToScreen(handle, ref origin))
        {
            Fail(UiText.Pick("无法读取取色窗口边界，请重试。", "無法讀取取色視窗邊界，請重試。"));
            return false;
        }
        var client = new PixelRect(origin.X, origin.Y, rect.Right, rect.Bottom);
        if (client == monitor) return true;
        var (actual, expected) = (ScreenPickMath.Describe(client), ScreenPickMath.Describe(monitor));
        Fail(UiText.Pick($"取色窗口 {actual} 与屏幕 {expected} 不一致，请重试。", $"取色視窗 {actual} 與螢幕 {expected} 不一致，請重試。"));
        return false;
    }

    private IntPtr WatchLayout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            // 任何移动或改尺寸都改成这块显示器的物理矩形；只改层次或显隐的调用（同时带 NOMOVE 与 NOSIZE）不动。
            case PositionChanging when lParam != IntPtr.Zero:
                var position = Marshal.PtrToStructure<Native.WindowPos>(lParam);
                if ((position.Flags & (NoMove | NoSize)) != (NoMove | NoSize))
                {
                    (position.X, position.Y, position.Width, position.Height) = (monitor.X, monitor.Y, monitor.Width, monitor.Height);
                    position.Flags &= ~(NoMove | NoSize);
                    Marshal.StructureToPtr(position, lParam, false);
                }
                break;
            // 就位前的缩放变化交给 Avalonia 同步界面缩放（它按建议矩形改尺寸，也被上面钉住）；就位后 DPI 变了即显示布局变化。
            case DpiChanged when Ready && (uint)(wParam.ToInt64() & 0xFFFF) != pinnedDpi:
                handled = true;
                Fail(ScreenPickMath.LayoutChanged);
                break;
            case DisplayChanged when Ready:
                Fail(ScreenPickMath.LayoutChanged);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>冻结画面：从客户区左上角起按物理像素 1:1 绘制（最近邻、不平滑），逻辑尺寸 = 物理尺寸 ÷ 当前界面缩放。</summary>
    private sealed class FrozenView(ScreenPickLayer layer) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (layer.frozen is not { } bitmap || layer.capture is not { } frame) return;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                context.DrawImage(bitmap, new Rect(0, 0, frame.Width, frame.Height), ScreenPickMath.FrozenRect(frame, layer.RenderScaling));
        }
    }

    /// <summary>
    /// 十字线（两条贯穿本显示器的 1 物理像素半透明白线）、采样框（9 px 黑框套 7 px 白框）与跟随标签（24 px 色块加等宽字体 HEX，
    /// 距光标 18 px，靠近右边或下边时翻到另一侧）。只在本显示器已冻结且光标在本显示器上时画。外观固定，不随主题变化。
    /// </summary>
    private sealed class PickMarks(ScreenPickLayer layer) : Control
    {
        private const double Offset = 18, CapsuleHeight = 40, Swatch = 24, Padding = 12, Spacing = 10;

        public override void Render(DrawingContext context)
        {
            var scale = layer.RenderScaling;
            if (layer.capture is null || PhysicalCursor() is not { } cursor || ScreenPickMath.CursorCell(cursor, layer.monitor, scale) is not { } cell) return;
            // 光标所在物理像素的左上角与 1 物理像素边长，按当前界面缩放换成逻辑坐标；画布是这块显示器的逻辑尺寸。
            var (x, y, pixel) = (cell.X, cell.Y, cell.Width);
            var area = ScreenPickMath.LogicalSize(layer.monitor.Size, scale);
            var line = Brush("EyedropperLineBrush");
            context.FillRectangle(line, new Rect(x, 0, pixel, area.Height));
            context.FillRectangle(line, new Rect(0, y, area.Width, pixel));
            context.DrawRectangle(new Pen(Brushes.Black, pixel), new Rect(x - 3.5 * pixel, y - 3.5 * pixel, 8 * pixel, 8 * pixel));
            context.DrawRectangle(new Pen(Brushes.White, pixel), new Rect(x - 2.5 * pixel, y - 2.5 * pixel, 6 * pixel, 6 * pixel));

            if (layer.HoverHex is not { } hex) return;
            var text = new FormattedText(hex, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas", FontStyle.Normal, FontWeight.SemiBold), 14, Brush("EyedropperTextBrush"));
            var width = Padding + Swatch + Spacing + text.Width + Padding;
            var left = x + Offset + width > area.Width ? x - width - Offset : x + Offset;
            var top = y + Offset + CapsuleHeight > area.Height ? y - CapsuleHeight - Offset : y + Offset;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPos
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, ref Rect lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
}
