using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 屏幕级的浮窗，每块显示器一个窗口：无边框、透明、置顶、不抢焦点，铺满所在显示器，与游戏窗口无关。
/// 窗口完全落在一块显示器里，缩放比例就是这块显示器的比例（Avalonia 在设置位置与收到 WM_DPICHANGED 时按窗口所在显示器取比例，
/// 窗口横跨缩放不同的显示器时取到的比例不确定，浮窗错位、无法操作即源于此），所以不拦缩放消息、不另行换算。
/// 画布拿到全部显示器（以本窗口为原点与比例）用来定位元件，只显示属于本显示器的元件（<see cref="OverlayCanvas.OwnMonitorKey"/>）；
/// 元件记住的显示器不在了就回到主显示器。版面设置了“游戏最小化时隐藏”时，游戏窗口最小化期间浮窗不显示（缺省不隐藏）。
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly OverlayCanvas canvas = new();
    private readonly IDesktopBackend backend;
    private readonly OverlayLayoutStore store;

    /// <param name="screenKey">所在显示器的标识（<see cref="ScreenInfo.Key"/>）；为空时是主显示器。</param>
    public OverlayWindow(OverlayLayoutStore store, ModuleCatalog catalog, IDesktopBackend backend, string? screenKey = null)
    {
        this.backend = backend;
        this.store = store;
        ScreenKey = screenKey;
        Title = "Moongate Overlay";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        // 浮窗盖在游戏画面上，外观固定，不随界面主题切换。
        RequestedThemeVariant = ThemeVariant.Light;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Content = canvas;
        UiMotion.Attach(this);
        canvas.Attach(store, catalog, backend);
        backend.Changed += OnBackendChanged;
        store.Changed += OnStoreChanged;
        // 元件移动、缩放、锁切换、版面切换、元件因主题缺失隐藏都会引起重新排版；缩放比例变化时物理像素区域也要重算。
        canvas.LayoutUpdated += (_, _) => UpdateRegions();
        canvas.ManipulationChanged += (_, _) => UpdateRegions();
        // 显示器缩放改变（或窗口被系统移到别的显示器）后按新的比例重新铺满。
        ScalingChanged += (_, _) => { Follow(); UpdateRegions(); };
        cursorWatch.Tick += (_, _) => FollowCursor();
        Opened += (_, _) =>
        {
            Follow();
            ApplyWindowFlags();
            UpdateVisibility();
            if (Screens is { } screens) screens.Changed += OnScreensChanged;
        };
        Closed += (_, _) =>
        {
            cursorWatch.Stop();
            backend.Changed -= OnBackendChanged;
            store.Changed -= OnStoreChanged;
            if (Screens is { } screens) screens.Changed -= OnScreensChanged;
        };
    }

    /// <summary>所在显示器的标识；为空表示主显示器。</summary>
    public string? ScreenKey { get; }

    /// <summary>显示器布局变化（插拔、改排列、改分辨率或缩放）；控制器据此增减各显示器的窗口。</summary>
    public event EventHandler? ScreensChanged;

    private void OnScreensChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        Follow();
        ScreensChanged?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>当前铺满的显示器（物理像素）与它的比例，供测试核对。</summary>
    public OverlayArea? Area { get; private set; }

    public OverlayCanvas Canvas => canvas;

    /// <summary>当前接收鼠标的物理像素矩形（窗口坐标）：已解锁元件的整块与已上锁元件的锁图标、关闭按钮。</summary>
    public IReadOnlyList<PixelRect> InteractiveRegion { get; private set; } = [];

    // 后端变化只影响“游戏最小化时隐藏”；浮窗位置与游戏窗口无关。
    private void OnBackendChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateVisibility);

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        ApplyWindowFlags();
        UpdateVisibility();
    }

    /// <summary>设置了“游戏最小化时隐藏”且游戏窗口已最小化时为真。</summary>
    public bool HiddenWithGame => store.HideWhenGameMinimized && backend.IsGameMinimized();

    private void UpdateVisibility()
    {
        var visible = !HiddenWithGame;
        if (canvas.IsVisible == visible) return;
        canvas.IsVisible = visible;
        UpdateRegions();
    }

    /// <summary>
    /// 铺满所在显示器：先把窗口移到这块显示器的左上角（窗口随之取这块显示器的比例），再按窗口实际的比例给出逻辑尺寸，
    /// 物理尺寸正好等于显示器；画布拿到以本显示器为原点与比例的全部显示器。读不到显示器时不动（测试按给定尺寸）。
    /// </summary>
    private void Follow()
    {
        var screens = OverlayArea.Describe(Screens);
        var screen = screens.FirstOrDefault(item => item.Key == ScreenKey) ?? screens.FirstOrDefault(item => item.Primary) ?? screens.FirstOrDefault();
        if (screen is null) return;
        // 先缩到很小再移动，旧尺寸不会让窗口在移动时横跨别的显示器。
        Width = 1;
        Height = 1;
        Position = screen.Bounds.Position;
        var area = OverlayArea.ForScreen(screens, screen, RenderScaling);
        Area = area;
        Width = area.Logical.Width;
        Height = area.Logical.Height;
        canvas.Monitors = area.Monitors;
        canvas.OwnMonitorKey = screen.Key;
    }

    private IntPtr NativeHandle =>
        OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } handle ? handle : IntPtr.Zero;

    // 不激活、工具窗口、分层和录屏隐藏是 Windows 的窗口属性；其他平台上浮窗照常显示，只是不具备这些属性。
    private void ApplyWindowFlags()
    {
        var handle = NativeHandle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, ExtendedStyle).ToInt64() | Layered | NoActivate | ToolWindow;
        SetWindowLongPtr(handle, ExtendedStyle, new IntPtr(style));
        SetWindowDisplayAffinity(handle, store.HideFromCapture ? 0x11u : 0u);
        UpdateRegions();
    }

    private const int ExtendedStyle = -20, Layered = 0x80000, Transparent = 0x20, NoActivate = 0x08000000, ToolWindow = 0x80;
    private readonly DispatcherTimer cursorWatch = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private IReadOnlyList<PixelRect>? appliedRegion;
    private bool? appliedTransparent;

    /// <summary>
    /// 两层区域。
    /// 窗口区域（SetWindowRgn）是画出内容的部分：可见元件的外框（锁图标与缩放手柄都在外框内）。区域外的点不属于这个窗口，
    /// 鼠标直接落到下层的游戏或桌面；元件外本来就透明，裁掉不改变画面。窗口区域同时限制绘制，所以它不能只留锁图标，
    /// 否则已上锁元件的内容画不出来。
    /// 窗口区域之内，已上锁元件的内容也要让鼠标落到下层：光标每 33 ms 取一次位置，在接收鼠标的矩形
    /// （<see cref="OverlayHitRegion.Interactive"/>）外时给窗口加 WS_EX_TRANSPARENT，进入时去掉。
    /// WM_NCHITTEST 返回 HTTRANSPARENT 只对同一线程的窗口有效，盖不住其他进程，所以不用它。
    /// 拖动或缩放期间去掉窗口区域、保持接收鼠标，指针移出元件时仍收到移动与松开，结束后恢复。
    /// 同一个 33 ms 轮询还按光标位置更新悬停的元件（<see cref="OverlayCanvas.HoverAt"/>），不另设定时器；
    /// 轮询只在有可见元件时运行，每次是一次 GetCursorPos 与对各元件外框的矩形判断，悬停元件变化时才改控件的不透明度。
    /// </summary>
    private void UpdateRegions()
    {
        var scaling = RenderScaling;
        var size = new PixelSize((int)Math.Ceiling(Bounds.Width * scaling), (int)Math.Ceiling(Bounds.Height * scaling));
        // 随游戏最小化隐藏时没有画出内容的区域，整窗让鼠标落到下层。
        IReadOnlyList<WidgetHitArea> areas = canvas.IsVisible ? canvas.HitAreas : [];
        InteractiveRegion = OverlayHitRegion.Interactive(areas, scaling, size);
        var handle = NativeHandle;
        if (handle == IntPtr.Zero) return;
        IReadOnlyList<PixelRect>? region = canvas.IsManipulating ? null : OverlayHitRegion.Painted(areas, scaling, size);
        if (InteractiveRegion.Count > 0) cursorWatch.Start();
        else cursorWatch.Stop();
        FollowCursor();
        if (region is null ? appliedRegion is null : appliedRegion is not null && region.SequenceEqual(appliedRegion)) return;
        appliedRegion = region;
        if (region is null)
        {
            SetWindowRgn(handle, IntPtr.Zero, true);
            return;
        }
        var combined = CreateRectRgn(0, 0, 0, 0);
        foreach (var rect in region)
        {
            var part = CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
            CombineRgn(combined, combined, part, 2);
            DeleteObject(part);
        }
        // 设置成功后区域归系统所有，不再删除。
        if (SetWindowRgn(handle, combined, true) == 0) DeleteObject(combined);
    }

    // 光标在接收鼠标的矩形内（或正在拖动）时浮窗接收鼠标，其余时候整窗穿透。只在状态变化时改窗口样式。
    // 拖动中主按键已经松开却没收到松开消息（松开发生在其他窗口上）时，按松开处理并保存，不让整窗一直拦截鼠标。
    private void FollowCursor()
    {
        var handle = NativeHandle;
        if (handle == IntPtr.Zero) return;
        if (canvas.IsManipulating && (GetAsyncKeyState(GetSystemMetrics(SwapButtons) != 0 ? RightButton : LeftButton) & 0x8000) == 0)
            canvas.FinishManipulation();
        // 同一次取到的光标位置也决定悬停的元件（显示它的锁与调整控件）：物理像素减去窗口位置，再按窗口缩放比例换成逻辑像素。
        // 浮窗铺满虚拟桌面并固定打开时的缩放比例，所以任一显示器上的光标都按同一比例换算。
        var located = GetCursorPos(out var cursor);
        var local = new PixelPoint(cursor.X - Position.X, cursor.Y - Position.Y);
        canvas.HoverAt(located ? new Point(local.X / RenderScaling, local.Y / RenderScaling) : null);
        var inside = canvas.IsManipulating || located && OverlayHitRegion.Accepts(InteractiveRegion, local);
        var transparent = !inside;
        if (appliedTransparent == transparent) return;
        appliedTransparent = transparent;
        var style = GetWindowLongPtr(handle, ExtendedStyle).ToInt64();
        SetWindowLongPtr(handle, ExtendedStyle, new IntPtr(transparent ? style | Transparent : style & ~Transparent));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    private const int LeftButton = 0x01, RightButton = 0x02, SwapButtons = 23;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);


    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

/// <summary>
/// 浮窗的显示与隐藏。显示时主显示器先开一个窗口，打开后按读到的显示器为其余每块显示器各开一个；显示器布局变化时增减。
/// 版面编辑页与快捷键都通过它控制；显隐与全部上锁的结果写进活动胶囊（<see cref="IDesktopBackend.ReportNotice"/>）。
/// </summary>
public sealed class OverlayController(OverlayLayoutStore store, ModuleCatalog catalog, IDesktopBackend backend)
{
    private OverlayWindow? primary;
    private readonly List<OverlayWindow> others = [];
    private bool closing;

    public event EventHandler? Changed;

    public OverlayLayoutStore Store => store;
    public IDesktopBackend Backend => backend;
    public bool IsShown => primary is not null;

    /// <summary>当前打开的全部浮窗窗口（主显示器在前）。</summary>
    public IReadOnlyList<OverlayWindow> Windows => primary is null ? [] : [primary, .. others];

    public void Show()
    {
        if (primary is not null) return;
        primary = new OverlayWindow(store, catalog, backend);
        primary.Opened += (_, _) => SyncScreens();
        primary.ScreensChanged += (_, _) => SyncScreens();
        primary.Closed += (_, _) =>
        {
            primary = null;
            CloseOthers();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        primary.Show();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Hide() => primary?.Close();

    // 其余显示器各一个窗口：已有的保留，拔掉的显示器的窗口关闭；元件记住的显示器不在时由主显示器的窗口显示。
    private void SyncScreens()
    {
        if (primary is null) return;
        var screens = OverlayArea.Describe(primary.Screens);
        var main = screens.FirstOrDefault(screen => screen.Primary) ?? screens.FirstOrDefault();
        var wanted = screens.Where(screen => screen != main).Select(screen => screen.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in others.Where(window => !wanted.Contains(window.ScreenKey!)).ToList())
        {
            others.Remove(stale);
            stale.Close();
        }
        foreach (var key in wanted.Where(key => others.All(window => window.ScreenKey != key)))
        {
            var window = new OverlayWindow(store, catalog, backend, key);
            window.Closed += (_, _) => { if (!closing) others.Remove(window); };
            others.Add(window);
            window.Show();
        }
    }

    private void CloseOthers()
    {
        closing = true;
        try { foreach (var window in others.ToList()) window.Close(); }
        finally { others.Clear(); closing = false; }
    }

    /// <summary>当前版面有任一元件解锁时全部上锁，否则全部解锁（版面编辑页的“全部上锁”与快捷键 overlay-click-through-toggle）。</summary>
    public void ToggleAllLocks()
    {
        store.ToggleAllLocks();
        backend.ReportNotice(store.AllLocked
            ? Localization.UiText.Pick("浮窗元件已全部上锁", "浮窗元件已全部上鎖")
            : Localization.UiText.Pick("浮窗元件已全部解锁", "浮窗元件已全部解鎖"));
    }

    public void Toggle()
    {
        if (IsShown) Hide();
        else Show();
        backend.ReportNotice(IsShown ? Localization.UiText.Pick("浮窗已显示", "浮窗已顯示") : Localization.UiText.Pick("浮窗已隐藏", "浮窗已隱藏"));
    }
}
