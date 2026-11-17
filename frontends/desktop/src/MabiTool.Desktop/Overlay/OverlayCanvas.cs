using Avalonia;
using Avalonia.Controls;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Modules;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 游戏浮窗里按版面摆放元件的画布，每块显示器的浮窗一个。
/// 每个元件的右上角有一把锁：已上锁的元件只有锁图标与关闭按钮接收鼠标；已解锁的元件可以直接拖动、拖右下角把手改变尺寸、
/// 按锁左侧的“－／＋”缩放、按“－／＋”左侧的不透明度按钮调整不透明度，元件内的控件也接收输入。
/// 锁、不透明度按钮、“－／＋”与尺寸手柄只在光标位于元件上时显示（<see cref="HoverAt"/>，由浮窗按光标位置调用），
/// 不论元件是否上锁；隐藏的锁仍在原处接收鼠标，光标移到元件上即显示，可以解锁。
/// 编辑状态（<see cref="OverlayLayoutStore.IsEditing"/>，版面页的“编辑位置”）下全部元件显示并可拖动、改尺寸与缩放，不受锁状态限制，
/// 元件内容不接收输入；显示器顶部有编辑提示条与“完成编辑”。
/// 手柄改变的是元件外框的宽高（缩放前的逻辑像素，<see cref="WidgetPlacement.Width"/>、<see cref="WidgetPlacement.Height"/>），
/// 内容按新的尺寸重新排版；缩放倍率只由“－／＋”与版面页的缩放控件改变。拖动按光标位置跨显示器（<see cref="DragTo"/>）。
/// 元件的内容由模块声明的 <see cref="WidgetDescriptor.CreateView"/> 提供。
/// </summary>
public sealed class OverlayCanvas : Panel
{
    private const double Grid = 8;
    private const double Inset = 12;
    /// <summary>锁图标在元件缩放为 1 时的边长。</summary>
    public const double LockSize = 18;
    /// <summary>锁图标离外框上边与右边的距离（元件缩放为 1 时）。外框圆角 18，这个位置让圆形锁完整落在圆角以内。</summary>
    public const double LockInset = 6;
    /// <summary>缩放手柄在元件缩放为 1 时的边长，以及它离外框下边与右边的距离；圆形手柄同样完整落在圆角以内。</summary>
    public const double HandleSize = 14, HandleInset = 4;
    /// <summary>“－／＋”按钮在元件缩放为 1 时的边长，两个按钮之间以及与锁之间的间隔。</summary>
    public const double ZoomSize = 18, ZoomGap = 4;
    /// <summary>锁左侧一行按钮的宽度（元件缩放为 1 时）：不透明度、“－”、“＋”三个按钮与其间的两个间隔。</summary>
    public const double ZoomRowWidth = ZoomSize * 3 + ZoomGap * 2;
    /// <summary>关闭按钮在元件缩放为 1 时的边长；它在外框右上角最右侧，锁在它左边隔 <see cref="ZoomGap"/>。</summary>
    public const double CloseSize = 18;

    private static readonly Geometry CloseGlyph = Geometry.Parse("M8 8L16 16M16 8L8 16");
    // 移到下一块显示器：向右的箭头。
    private static readonly Geometry NextGlyph = Geometry.Parse("M6 12H17M13 8L17 12L13 16");

    // 开锁与闭锁画在 24×24 画板上：锁体相同，闭锁的锁梁两端都插进锁体，开锁的锁梁抬起、右端离开锁体。
    private static readonly Geometry LockBody = Geometry.Parse("M7 11H17A1 1 0 0 1 18 12V19A1 1 0 0 1 17 20H7A1 1 0 0 1 6 19V12A1 1 0 0 1 7 11Z");
    private static readonly Geometry ShutShackle = Geometry.Parse("M8.5 11V8A3.5 3.5 0 0 1 15.5 8V11");
    private static readonly Geometry OpenShackle = Geometry.Parse("M8.5 11V6A3.5 3.5 0 0 1 15.5 6V7");

    private static readonly Geometry MinusGlyph = Geometry.Parse("M7 12H17");
    private static readonly Geometry PlusGlyph = Geometry.Parse("M7 12H17M12 7V17");
    // 不透明度：圆圈与竖直的分界线，左半填色。
    private static readonly Geometry OpacityGlyph = Geometry.Parse("M12 5A7 7 0 1 0 12 19A7 7 0 1 0 12 5ZM12 5V19");
    private static readonly Geometry OpacityFill = Geometry.Parse("M12 5A7 7 0 0 0 12 19Z");

    /// <summary>一个元件在画布上的几个部件：外框（含缩放）、锁图标、“－／＋”、尺寸手柄，以及元件声明的默认与最小尺寸。</summary>
    private sealed class Slot(WidgetPlacement placement, LayoutTransformControl frame, Border skin,
        LayoutTransformControl lockHost, Border lockFace, LayoutTransformControl handle, OverlayWidgetModel? model,
        LayoutTransformControl zoom, Size? defaultSize, Size minimumSize, LayoutTransformControl close, LayoutTransformControl next)
    {
        public LayoutTransformControl Next { get; } = next;
        public LayoutTransformControl Close { get; } = close;
        public LayoutTransformControl Zoom { get; } = zoom;
        public Size? DefaultSize { get; } = defaultSize;
        public Size MinimumSize { get; } = minimumSize;
        public WidgetPlacement Placement { get; } = placement;
        public LayoutTransformControl Frame { get; } = frame;
        public Border Skin { get; } = skin;
        public LayoutTransformControl Lock { get; } = lockHost;
        public Border LockFace { get; } = lockFace;
        public LayoutTransformControl Handle { get; } = handle;
        public OverlayWidgetModel? Model { get; } = model;
    }

    private readonly List<Slot> slots = [];
    private OverlayLayout? builtLayout;
    private List<(WidgetPlacement Placement, string Skin, string? Image)> builtPlacements = [];
    private OverlayLayoutStore? store;
    private ModuleCatalog? catalog;
    private IDesktopBackend? backend;
    private Slot? dragging;
    private Slot? pressedLock;
    private Point dragOrigin;
    // 按下点在外框里的位置（占外框宽高的比例）：拖动时这一点留在光标下，跨缩放比例不同的显示器也一样。
    private Point grab;
    private Size sizeStart;
    private Slot? pressedZoom;
    private Slot? pressedClose;
    private Slot? pressedNext;
    private string? pressedZoomName;
    private string? hoveredWidgetId;
    private bool resizing;
    // 手势开始前的摆放：手势因数值无效或异常中止时恢复。
    private (double X, double Y, string? Screen, double? Width, double? Height) gestureStart;
    // 手势最近一次有输入（光标移动或指针事件）的时间与轮询到的光标位置；光标不动时轮询不重算，空闲超时由此判定。
    private DateTimeOffset lastActivity;
    private PixelPoint? lastCursor;
    // 正在应用一次拖动或轮询：期间引起的重排、通知再调用进来时直接返回。
    private bool applying, polling;
    private IReadOnlyList<OverlayMonitor> monitors = [];
    private readonly Border banner;

    public OverlayCanvas()
    {
        ClipToBounds = true;
        // 共用的按钮行为：元件上锁（游戏浮窗上鼠标穿透）时，内容里标了 overlayactions 的操作区整块隐藏，不显示成可以点的样子。
        // 内建与扩展元件都按这个类名标出自己的操作按钮行；编辑状态下不上这个类。
        Styles.Add(new Style(selector => selector.OfType<Border>().Class("overlayframe").Class("locked")
            .Descendant().Class("overlayactions"))
        {
            Setters = { new Setter(IsVisibleProperty, false) }
        });
        banner = BuildBanner();
    }

    // 编辑状态的提示条：每块显示器工作区顶部正中，写明怎样摆放，右侧“完成编辑”退出编辑并保存。
    private Border BuildBanner()
    {
        var finish = new Button
        {
            Name = "FinishEditButton", Content = Localization.UiText.Pick("完成编辑", "完成編輯"), Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Classes = { "overlaybutton", "start" }
        };
        finish.Click += (_, _) => store?.EndEdit();
        DockPanel.SetDock(finish, Dock.Right);
        var text = new TextBlock
        {
            Text = Localization.UiText.Pick("正在编辑浮窗：拖动元件摆放，按箭头移到下一块显示器，拖右下角改尺寸，按锁切换上锁",
                "正在編輯浮窗：拖動元件擺放，按箭頭移到下一塊顯示器，拖右下角改尺寸，按鎖切換上鎖"),
            Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        return new Border
        {
            Name = "EditBanner", MaxWidth = 640, Padding = new Thickness(16, 8, 12, 8), IsVisible = false,
            Classes = { "overlayframe", "solid" }, Child = new DockPanel { Children = { finish, text } }
        };
    }

    /// <summary>编辑状态的提示条。</summary>
    public Control Banner => banner;

    /// <summary>版面处于编辑状态（版面页的“编辑位置”）。</summary>
    public bool IsEditing => store?.IsEditing == true;

    /// <summary>
    /// 浮窗覆盖范围里的各块显示器（参照尺寸的逻辑像素）。元件的比例位置取所在显示器的工作区，并按该显示器与参照的缩放比之比放大，
    /// 物理大小在各显示器上一致。为空时整个画布当作一块（测试与读不到显示器信息时）。
    /// </summary>
    public IReadOnlyList<OverlayMonitor> Monitors
    {
        get => monitors;
        set
        {
            if (value.SequenceEqual(monitors)) return;
            monitors = value;
            foreach (var slot in slots) ApplyScale(slot);
            UpdateStates();
            InvalidateMeasure();
        }
    }

    private OverlayArea? area;

    /// <summary>
    /// 本画布所在窗口铺满的显示器范围（物理像素原点与缩放比例），同时给出 <see cref="Monitors"/>。
    /// 拖动轮询按它把系统光标的物理坐标换成本画布的逻辑坐标（<see cref="PollDrag"/>）。
    /// </summary>
    public OverlayArea? Area
    {
        get => area;
        set
        {
            area = value;
            if (value is not null) Monitors = value.Monitors;
        }
    }

    /// <summary>
    /// 系统光标与主按键状态的来源（Windows 上由浮窗给出，读 GetCursorPos 与 GetAsyncKeyState）。
    /// 设置后拖动的位置与结束都按它判定：发起拖动的窗口不在前台时，系统只在光标位于该窗口可见范围内才把鼠标消息交给它，
    /// 光标越过显示器边界后指针事件可能中断、指针捕获可能被收回，所以捕获丢失不再结束拖动，以轮询到的主按键松开为准。
    /// 为空时（无界面测试、其他平台）按指针事件拖动，捕获丢失即结束。
    /// </summary>
    public IOverlayCursor? CursorSource { get; set; }

    /// <summary>
    /// 拖动或改尺寸期间超过这么久没有任何输入（光标不动、没有指针事件）而系统仍报告主按键按住时，按松开处理并保存当时的摆放。
    /// 远程桌面可能丢掉松开，主按键状态停在按住，手势不会自行结束。
    /// </summary>
    public static readonly TimeSpan GestureIdleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>判定空闲超时用的时钟；测试替换。</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    private string? ownMonitor;

    /// <summary>
    /// 游戏浮窗每块显示器一个窗口：这个画布只显示所在显示器（<see cref="OverlayMonitor.Key"/>）上的元件，其余元件留在各自显示器的窗口里。
    /// 为空时显示全部元件（测试）。
    /// </summary>
    public string? OwnMonitorKey
    {
        get => ownMonitor;
        set
        {
            if (value == ownMonitor) return;
            ownMonitor = value;
            UpdateStates();
        }
    }

    private bool OnThisCanvas(WidgetPlacement placement) => ownMonitor is null || MonitorOf(placement)?.Key == ownMonitor;

    private OverlayMonitor? MonitorOf(WidgetPlacement placement) => OverlayArea.Locate(monitors, placement.Screen);

    // 元件所在显示器在画布上的矩形（扣除边距之前）与缩放倍数；没有显示器信息时是整个画布、倍数 1。
    private (Rect Area, double Dpi) RegionOf(WidgetPlacement placement, Size canvas) => MonitorOf(placement) is { } monitor
        ? (monitor.Area, monitor.Dpi)
        : (new Rect(canvas), 1);

    private double Factor(Slot slot) => slot.Placement.Scale * (MonitorOf(slot.Placement)?.Dpi ?? 1);

    /// <summary>正在拖动或缩放元件；开始与结束时触发 <see cref="ManipulationChanged"/>。</summary>
    public bool IsManipulating => dragging is not null;
    public event EventHandler? ManipulationChanged;

    /// <summary>当前显示的元件标识，按版面里的先后顺序。</summary>
    public IReadOnlyList<string> WidgetIds => slots.Select(slot => slot.Placement.WidgetId).ToList();

    public void Attach(OverlayLayoutStore layouts, ModuleCatalog modules, IDesktopBackend source)
    {
        Detach();
        suspended = null;
        (store, catalog, backend) = (layouts, modules, source);
        layouts.Changed += OnStoreChanged;
        layouts.Moved += OnStoreMoved;
        modules.Changed += OnCatalogChanged;
        Rebuild();
    }

    // 扩展模块随扩展运行状态增减元件：目录变化时按当前版面重建，已摆放但暂时没有提供者的元件保留摆放数据。
    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (store is not null && dragging is null) Rebuild();
    }

    public void Detach()
    {
        if (store is not null)
        {
            store.Changed -= OnStoreChanged;
            store.Moved -= OnStoreMoved;
        }
        if (catalog is not null) catalog.Changed -= OnCatalogChanged;
        store = null;
        ClearWidgets();
    }

    /// <summary>元件在画布里的位置，供测试核对。</summary>
    public Rect? BoundsOf(string widgetId) => Find(widgetId)?.Frame.Bounds;

    /// <summary>元件的外框（含内容，按元件的不透明度显示）。</summary>
    public Control? FrameOf(string widgetId) => Find(widgetId)?.Frame;

    /// <summary>元件右上角的锁图标。</summary>
    public Control? LockOf(string widgetId) => Find(widgetId)?.Lock;

    /// <summary>元件右上角最右侧的关闭按钮。</summary>
    public Control? CloseOf(string widgetId) => Find(widgetId)?.Close;

    /// <summary>编辑状态下“－／＋”左侧的“移到下一块显示器”按钮（多于一块显示器时显示）。</summary>
    public Control? NextMonitorButtonOf(string widgetId) => Find(widgetId)?.Next;

    /// <summary>元件右下角的尺寸手柄。</summary>
    public Control? HandleOf(string widgetId) => Find(widgetId)?.Handle;

    /// <summary>锁左侧的“－／＋”一组。</summary>
    public Control? ZoomOf(string widgetId) => Find(widgetId)?.Zoom;

    /// <summary>“－”或“＋”按钮本身。</summary>
    public Control? ZoomButtonOf(string widgetId, bool up) =>
        Find(widgetId)?.Zoom.Child is StackPanel { Children: [_, var minus, var plus] } ? up ? plus : minus : null;

    /// <summary>“－／＋”左侧的不透明度按钮。</summary>
    public Control? OpacityButtonOf(string widgetId) =>
        Find(widgetId)?.Zoom.Child is StackPanel { Children: [var opacity, _, _] } ? opacity : null;

    /// <summary>光标所在的元件（游戏浮窗上它的锁与调整控件显示）；光标不在任何元件上时为空。</summary>
    public string? HoveredWidgetId => hoveredWidgetId;

    /// <summary>
    /// 按光标位置（画布坐标，逻辑像素；光标不在浮窗上时为空）更新悬停的元件：取包含该点的最上层可见元件。
    /// 游戏浮窗的光标轮询调用它：上锁的元件鼠标穿透、收不到指针事件，悬停只按位置判断。悬停元件变化时才更新控件。
    /// </summary>
    public void HoverAt(Point? point)
    {
        var hovered = point is { } at
            ? slots.LastOrDefault(slot => slot.Frame.IsVisible && slot.Frame.Bounds.Contains(at))?.Placement.WidgetId
            : null;
        if (hovered == hoveredWidgetId) return;
        hoveredWidgetId = hovered;
        UpdateControls();
    }

    /// <summary>元件的锁与调整控件当前是否显示。</summary>
    public bool ControlsShown(string widgetId) => Find(widgetId) is { } slot && ControlsShown(slot);

    private bool ControlsShown(Slot slot) => IsEditing || slot == dragging || slot.Placement.WidgetId == hoveredWidgetId;

    /// <summary>元件外框在缩放前的尺寸（逻辑像素）：保存的宽高或默认尺寸，不小于最小尺寸；扩展元件没有声明时为空。</summary>
    public Size? SizeOf(string widgetId) => Find(widgetId) is { } slot ? EffectiveSize(slot) : null;

    /// <summary>元件声明的最小尺寸。</summary>
    public Size? MinimumSizeOf(string widgetId) => Find(widgetId)?.MinimumSize;

    /// <summary>
    /// 各元件的外框、上锁时仍接收鼠标的矩形（锁图标与它右侧的关闭按钮）、“－／＋”矩形与锁状态；
    /// 因展示主题缺失或在浮窗上关闭而隐藏的元件标为不可见。编辑状态下元件都按解锁计（整块接收鼠标），另加编辑提示条。
    /// </summary>
    public IReadOnlyList<WidgetHitArea> HitAreas
    {
        get
        {
            var editing = IsEditing;
            var areas = slots
                .Select(slot => new WidgetHitArea(slot.Frame.Bounds,
                    slot.Close.IsVisible ? slot.Lock.Bounds.Union(slot.Close.Bounds) : slot.Lock.Bounds, slot.Placement.Locked && !editing,
                    slot.Frame.IsVisible, slot.Zoom.IsVisible ? slot.Zoom.Bounds : default))
                .ToList();
            if (banner.IsVisible) areas.Add(new WidgetHitArea(banner.Bounds, default, false, true));
            return areas;
        }
    }

    private Slot? Find(string widgetId) => slots.FirstOrDefault(slot => slot.Placement.WidgetId == widgetId);

    // 离开视觉树时释放元件（元件模型退订后端），但记住版面来源；重新挂上时重建。
    private (OverlayLayoutStore Store, ModuleCatalog Catalog, IDesktopBackend Backend)? suspended;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (store is null || catalog is null || backend is null) return;
        suspended = (store, catalog, backend);
        Detach();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (suspended is not { } source || store is not null) return;
        suspended = null;
        Attach(source.Store, source.Catalog, source.Backend);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(Size.Infinity);
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    // 锁、关闭按钮、“－／＋”与手柄都随元件缩放，留在外框内。
    private void ApplyScale(Slot slot)
    {
        var factor = Factor(slot);
        Scale(slot.Frame, factor);
        Scale(slot.Lock, factor);
        Scale(slot.Close, factor);
        Scale(slot.Handle, factor);
        Scale(slot.Zoom, factor);
        Scale(slot.Next, factor);
        ApplySize(slot);
    }

    private static Size? EffectiveSize(Slot slot) => OverlayWidgetSizes.Resolve(slot.Placement, slot.DefaultSize, slot.MinimumSize);

    // 外框按元件尺寸固定宽高，内容在其中重新排版；没有尺寸的扩展元件按内容的自然尺寸显示。
    private static void ApplySize(Slot slot)
    {
        var size = EffectiveSize(slot);
        var (width, height) = size is { } value ? (value.Width, value.Height) : (double.NaN, double.NaN);
        if (!slot.Skin.Width.Equals(width)) slot.Skin.Width = width;
        if (!slot.Skin.Height.Equals(height)) slot.Skin.Height = height;
    }

    // 倍率没变时不换变换，免得每次状态刷新都重新排版。
    private static void Scale(LayoutTransformControl host, double factor)
    {
        if (host.LayoutTransform is ScaleTransform current && current.ScaleX == factor && current.ScaleY == factor) return;
        host.LayoutTransform = new ScaleTransform(factor, factor);
    }

    /// <summary>
    /// 外框为 <paramref name="frame"/>、元件显示倍率为 <paramref name="factor"/> 时关闭按钮的位置：外框内右上角的最右侧，
    /// 离上边与右边各 <see cref="LockInset"/>。外框右侧的内边距为它留出一列，内容不被盖住。
    /// </summary>
    public static Rect CloseRect(Rect frame, double factor) =>
        new(frame.Right - (LockInset + CloseSize) * factor, frame.Top + LockInset * factor, CloseSize * factor, CloseSize * factor);

    /// <summary>锁图标的位置：关闭按钮左侧，与它同一行，相隔 <see cref="ZoomGap"/>。</summary>
    public static Rect LockRect(Rect frame, double factor) =>
        new(frame.Right - (LockInset + CloseSize + ZoomGap + LockSize) * factor, frame.Top + LockInset * factor, LockSize * factor, LockSize * factor);

    /// <summary>
    /// 游戏浮窗上“－／＋”的位置：锁图标左侧，与锁同一行，离锁 <see cref="ZoomGap"/>；按钮各 <see cref="ZoomSize"/>，
    /// 中间隔 <see cref="ZoomGap"/>，都乘以元件显示倍率。
    /// </summary>
    public static Rect ZoomRect(Rect frame, double factor)
    {
        var width = ZoomRowWidth * factor;
        var right = frame.Right - (LockInset + CloseSize + ZoomGap + LockSize + ZoomGap) * factor;
        return new Rect(right - width, frame.Top + LockInset * factor, width, ZoomSize * factor);
    }

    /// <summary>游戏浮窗上尺寸手柄的位置：外框内的右下角，同样落在外框右侧为锁留出的一列里。</summary>
    public static Rect HandleRect(Rect frame, double factor) =>
        new(frame.Right - (HandleInset + HandleSize) * factor, frame.Bottom - (HandleInset + HandleSize) * factor,
            HandleSize * factor, HandleSize * factor);

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var slot in slots)
        {
            var size = slot.Frame.DesiredSize;
            // 比例位置取元件所在显示器的工作区（没有显示器信息时是整个画布），四周留同样的边距（按那块显示器的逻辑像素计）。
            var (area, dpi) = RegionOf(slot.Placement, finalSize);
            var inset = Inset * dpi;
            var free = new Size(Math.Max(0, area.Width - size.Width - inset * 2), Math.Max(0, area.Height - size.Height - inset * 2));
            var rect = new Rect(area.X + inset + free.Width * slot.Placement.X, area.Y + inset + free.Height * slot.Placement.Y, size.Width, size.Height);
            slot.Frame.Arrange(rect);
            var factor = Factor(slot);
            slot.Lock.Arrange(LockRect(rect, factor));
            slot.Close.Arrange(CloseRect(rect, factor));
            var zoom = ZoomRect(rect, factor);
            slot.Zoom.Arrange(zoom);
            slot.Next.Arrange(new Rect(zoom.X - (ZoomGap + ZoomSize) * factor, zoom.Y, ZoomSize * factor, ZoomSize * factor));
            slot.Handle.Arrange(HandleRect(rect, factor));
        }
        // 提示条在本显示器工作区顶部正中。
        var own = monitors.FirstOrDefault(monitor => monitor.Key == ownMonitor)?.Area
                  ?? OverlayArea.Locate(monitors, null)?.Area ?? new Rect(finalSize);
        var bar = banner.DesiredSize;
        banner.Arrange(new Rect(own.X + Math.Max(0, (own.Width - bar.Width) / 2), own.Y + Inset, bar.Width, bar.Height));
        return finalSize;
    }

    // 拖动中的位置变化（可能来自另一块显示器的浮窗）：只重排，不重建。
    private void OnStoreMoved(object? sender, EventArgs e) => Refresh();

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        // 元件集合与外观没变时（移动、缩放、锁切换）只更新状态并重排：保留指针捕获与锁图标的过渡。
        if (dragging is not null || SameWidgets()) Refresh();
        else Rebuild();
    }

    private bool SameWidgets() =>
        store is not null && store.Active == builtLayout && store.Active.Widgets.Count == builtPlacements.Count
        && store.Active.Widgets.Zip(builtPlacements).All(pair =>
            pair.First == pair.Second.Placement && pair.First.Skin == pair.Second.Skin && pair.First.Image == pair.Second.Image);

    private void Refresh()
    {
        foreach (var slot in slots) ApplyScale(slot);
        UpdateStates();
        InvalidateMeasure();
    }

    private void ClearWidgets()
    {
        foreach (var slot in slots)
        {
            slot.Frame.PointerPressed -= OnFramePressed;
            slot.Frame.PointerMoved -= OnPointerMovedWhileDragging;
            slot.Frame.PointerReleased -= OnPointerReleasedAfterDragging;
            slot.Frame.PointerCaptureLost -= OnCaptureLost;
            slot.Handle.PointerPressed -= OnHandlePressed;
            slot.Handle.PointerMoved -= OnPointerMovedWhileDragging;
            slot.Handle.PointerReleased -= OnPointerReleasedAfterDragging;
            slot.Handle.PointerCaptureLost -= OnCaptureLost;
            slot.Lock.PointerPressed -= OnLockPressed;
            slot.Lock.PointerReleased -= OnLockReleased;
            slot.Close.PointerPressed -= OnClosePressed;
            slot.Close.PointerReleased -= OnCloseReleased;
            slot.Next.PointerPressed -= OnNextPressed;
            slot.Next.PointerReleased -= OnNextReleased;
            foreach (var button in ((StackPanel)slot.Zoom.Child!).Children)
            {
                button.PointerPressed -= OnZoomPressed;
                button.PointerReleased -= OnZoomReleased;
                button.PointerWheelChanged -= OnOpacityWheel;
            }
            // 只释放元件自己的模型：外框继承的 DataContext 不属于元件，不能随画布重建被释放。
            slot.Model?.Dispose();
        }
        slots.Clear();
        Children.Clear();
        builtLayout = null;
        builtPlacements = [];
        if (dragging is not null)
        {
            dragging = null;
            OverlayDragTrace.End("widgets-rebuilt", null);
            ManipulationChanged?.Invoke(this, EventArgs.Empty);
        }
        pressedLock = null;
        pressedZoom = null;
        pressedClose = null;
        pressedNext = null;
        hoveredWidgetId = null;
    }

    private void Rebuild()
    {
        ClearWidgets();
        if (store is null || catalog is null || backend is null) return;
        foreach (var placement in store.Active.Widgets)
        {
            var descriptor = catalog.Modules.SelectMany(module => module.Widgets)
                .FirstOrDefault(widget => widget.Id == placement.WidgetId);
            // 提供元件的模块已卸载时跳过，摆放数据保留，重新安装后恢复。
            if (descriptor is null) continue;
            var model = descriptor.CreateView is null
                ? new OverlayWidgetModel(placement.WidgetId, backend, descriptor.PresentationTopic, descriptor.Title) { Editing = store.IsEditing }
                : null;
            var view = descriptor.CreateView?.Invoke(backend) ?? new OverlayWidgetView { DataContext = model };
            // 外框按元件尺寸固定宽高并裁掉超出的内容；内容自己按尺寸排版（截断、滚动或显示更多行）。
            var skin = new Border { Child = view, ClipToBounds = true, Classes = { "overlayframe", placement.Skin } };
            // 自定外框图片读得到才覆盖样式里的底色。
            if (Skin(placement) is { } brush) skin.Background = brush;
            var frame = new LayoutTransformControl { Child = skin };
            var face = new Border
            {
                Classes = { "overlaylock" }, Width = LockSize, Height = LockSize, CornerRadius = new CornerRadius(LockSize / 2),
                Child = new Viewbox
                {
                    Width = 12, Height = 12,
                    Child = new Canvas
                    {
                        Width = 24, Height = 24,
                        Children =
                        {
                            new ShapePath { Data = LockBody },
                            new ShapePath { Data = ShutShackle, Classes = { "shut" } },
                            new ShapePath { Data = OpenShackle, Classes = { "open" } }
                        }
                    }
                }
            };
            var lockHost = new LayoutTransformControl { Child = face, Cursor = new Cursor(StandardCursorType.Hand), Classes = { "overlaycontrol" } };
            var handle = new LayoutTransformControl
            {
                Child = new Border
                {
                    Width = HandleSize, Height = HandleSize, CornerRadius = new CornerRadius(HandleSize / 2), Classes = { "overlayhandle" }
                },
                Cursor = new Cursor(StandardCursorType.BottomRightCorner), IsVisible = false, Classes = { "overlaycontrol" }
            };
            var opacity = ZoomButton(OpacityGlyph, "opacity", OpacityFill);
            var minus = ZoomButton(MinusGlyph, "minus");
            var plus = ZoomButton(PlusGlyph, "plus");
            var zoom = new LayoutTransformControl
            {
                Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = ZoomGap, Children = { opacity, minus, plus } },
                IsVisible = false, Classes = { "overlaycontrol" }
            };
            foreach (var button in new[] { opacity, minus, plus })
            {
                button.PointerPressed += OnZoomPressed;
                button.PointerReleased += OnZoomReleased;
            }
            opacity.PointerWheelChanged += OnOpacityWheel;
            // 关闭按钮：与锁一样不论锁状态都接收鼠标（上锁时也能关），外观同“－／＋”。
            var close = new LayoutTransformControl
            {
                Child = ZoomButton(CloseGlyph, "close"), Cursor = new Cursor(StandardCursorType.Hand), Classes = { "overlaycontrol" }
            };
            ToolTip.SetTip(close, this.TryFindResource("O.CloseWidget", out var tip) ? tip : null);
            close.PointerPressed += OnClosePressed;
            close.PointerReleased += OnCloseReleased;
            // 编辑状态下“移到下一块显示器”：不需要拖过显示器边界也能换屏（远程桌面一次只显示一块屏幕时拖不过去）。
            var next = new LayoutTransformControl
            {
                Child = ZoomButton(NextGlyph, "next"), Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false, Classes = { "overlaycontrol" }
            };
            ToolTip.SetTip(next, Localization.UiText.Pick("移到下一块显示器", "移到下一塊顯示器"));
            next.PointerPressed += OnNextPressed;
            next.PointerReleased += OnNextReleased;
            var slot = new Slot(placement, frame, skin, lockHost, face, handle, model, zoom,
                OverlayWidgetSizes.DefaultOf(descriptor), OverlayWidgetSizes.MinimumOf(descriptor), close, next);
            // 依赖的展示主题缺失时元件在游戏浮窗里隐藏；状态随后端刷新变化。
            if (model is not null) model.PropertyChanged += (_, _) => UpdateStates();
            frame.PointerPressed += OnFramePressed;
            frame.PointerMoved += OnPointerMovedWhileDragging;
            frame.PointerReleased += OnPointerReleasedAfterDragging;
            frame.PointerCaptureLost += OnCaptureLost;
            handle.PointerPressed += OnHandlePressed;
            handle.PointerMoved += OnPointerMovedWhileDragging;
            handle.PointerReleased += OnPointerReleasedAfterDragging;
            handle.PointerCaptureLost += OnCaptureLost;
            lockHost.PointerPressed += OnLockPressed;
            lockHost.PointerReleased += OnLockReleased;
            slots.Add(slot);
            ApplyScale(slot);
        }
        // 锁图标、“－／＋”与手柄画在所有外框之上，元件重叠时也不被别的元件盖住；编辑提示条在最上层。
        foreach (var slot in slots) Children.Add(slot.Frame);
        foreach (var slot in slots) Children.Add(slot.Lock);
        foreach (var slot in slots) Children.Add(slot.Close);
        foreach (var slot in slots) Children.Add(slot.Zoom);
        foreach (var slot in slots) Children.Add(slot.Next);
        foreach (var slot in slots) Children.Add(slot.Handle);
        Children.Add(banner);
        builtLayout = store.Active;
        builtPlacements = store.Active.Widgets.Select(placement => (placement, placement.Skin, placement.Image)).ToList();
        UpdateStates();
    }

    private static Border ZoomButton(Geometry glyph, string name, Geometry? fill = null)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (fill is not null) canvas.Children.Add(new ShapePath { Data = fill, Classes = { "fill" } });
        canvas.Children.Add(new ShapePath { Data = glyph });
        return new Border
        {
            Name = name, Classes = { "overlayzoom" }, Width = ZoomSize, Height = ZoomSize, CornerRadius = new CornerRadius(ZoomSize / 2),
            Cursor = new Cursor(StandardCursorType.Hand), Child = new Viewbox { Width = 12, Height = 12, Child = canvas }
        };
    }

    private static IBrush? Skin(WidgetPlacement placement)
    {
        if (placement is not { Skin: "image", Image: { Length: > 0 } path } || !File.Exists(path)) return null;
        try { return new ImageBrush(new Bitmap(path)) { Stretch = Stretch.Fill }; }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException) { return null; }
    }


    /// <summary>
    /// 按编辑状态与锁状态更新各元件：已上锁的元件只有锁图标与关闭按钮接收鼠标，已解锁的元件整块接收鼠标并显示手柄与“－／＋”。
    /// 编辑状态下全部元件（包括关闭的与展示主题缺失的）显示、整块可拖动并显示手柄与“－／＋”，内容不接收输入。
    /// 外框按元件的不透明度显示，编辑状态下关闭的元件变淡。拖动中的元件在发起拖动的画布上一直画着，跨到别的显示器时那块显示器的画布同时显示它。
    /// </summary>
    private void UpdateStates()
    {
        var editing = IsEditing;
        foreach (var slot in slots)
        {
            if (slot.Model is { } model) model.Editing = editing;
            // 在浮窗上关闭的元件只在编辑状态下留着（变淡），其余时候不显示。
            var closed = slot.Placement.Closed;
            slot.Frame.IsVisible = (slot.Model?.FrameVisible ?? true) && (editing || !closed) && (OnThisCanvas(slot.Placement) || slot == dragging);
            var visible = slot.Frame.IsVisible;
            var locked = slot.Placement.Locked;
            var movable = editing || !locked;
            slot.Skin.Classes.Set("selected", editing);
            // 上锁时内容里的操作区（overlayactions）隐藏；编辑状态下不隐藏，但内容整块不接收输入，按哪里都是拖动。
            slot.Skin.Classes.Set("locked", locked && !editing);
            if (slot.Skin.Child is { } content) content.IsHitTestVisible = !editing;
            slot.Frame.IsHitTestVisible = movable;
            slot.LockFace.Classes.Set("open", !locked);
            slot.Lock.IsVisible = visible;
            slot.Close.IsVisible = visible;
            ApplyScale(slot);
            slot.Handle.IsVisible = visible && movable;
            slot.Zoom.IsVisible = visible && movable;
            slot.Next.IsVisible = visible && editing && OverlayArea.Next(monitors, MonitorOf(slot.Placement)?.Key) is not null;
            slot.Frame.Opacity = editing && closed ? slot.Placement.Opacity * 0.4 : slot.Placement.Opacity;
        }
        banner.IsVisible = editing;
        UpdateControls();
        InvalidateArrange();
    }

    /// <summary>
    /// 锁、不透明度按钮、“－／＋”与尺寸手柄只在光标位于元件上（或正在拖动该元件、或处于编辑状态）时显示，其余时候完全透明；
    /// 只改不透明度，接收鼠标的范围不变，隐藏的锁照样可以点。
    /// </summary>
    private void UpdateControls()
    {
        foreach (var slot in slots)
        {
            var opacity = ControlsShown(slot) ? 1 : 0;
            slot.Lock.Opacity = opacity;
            // 关闭入口常驻（18.14）：不随锁状态或悬停隐藏。
            slot.Close.Opacity = 1;
            slot.Zoom.Opacity = opacity;
            slot.Next.Opacity = opacity;
            slot.Handle.Opacity = opacity;
        }
    }

    private void BeginManipulation(Slot slot, bool resize, PointerPressedEventArgs e, Control captureTarget)
    {
        (dragging, resizing) = (slot, resize);
        var placement = slot.Placement;
        gestureStart = (placement.X, placement.Y, placement.Screen, placement.Width, placement.Height);
        (lastActivity, lastCursor) = (Clock.GetUtcNow(), null);
        dragOrigin = e.GetPosition(this);
        var frame = slot.Frame.Bounds;
        grab = frame is { Width: > 0, Height: > 0 }
            ? new Point((dragOrigin.X - frame.X) / frame.Width, (dragOrigin.Y - frame.Y) / frame.Height)
            : default;
        // 尺寸按缩放前的逻辑像素计：当前外框尺寸除以显示倍率。
        var factor = Factor(slot);
        sizeStart = EffectiveSize(slot) ?? new Size(frame.Width / factor, frame.Height / factor);
        // 指针交给按下的部件：拖出元件或窗口区域时仍收到移动与松开。
        e.Pointer.Capture(captureTarget);
        e.Handled = true;
        ManipulationChanged?.Invoke(this, EventArgs.Empty);
        OverlayDragTrace.Begin(this, slot.Placement, resize, dragOrigin);
    }

    /// <summary>
    /// 按 <see cref="CursorSource"/> 取一次系统光标：光标移动了就拖动到光标所在位置（物理像素换成本画布的逻辑坐标），
    /// 主按键已松开时结束并保存，超过 <see cref="GestureIdleTimeout"/> 没有输入时同样结束。
    /// 浮窗的光标定时器在拖动期间每 16 ms 调用；指针移动事件也调用它，位置始终取自同一个来源。
    /// 光标没动时不重算摆放、不要求重新排版，所以排版之后再次轮询不会引起又一次排版。
    /// </summary>
    public void PollDrag()
    {
        if (dragging is null || polling || CursorSource is not { } cursor || area is not { } own) return;
        polling = true;
        try
        {
            if (cursor.Position is { } at && at != lastCursor)
            {
                (lastCursor, lastActivity) = (at, Clock.GetUtcNow());
                if (double.IsFinite(own.Scaling) && own.Scaling > 0)
                    DragTo(new Point((at.X - own.Pixels.X) / own.Scaling, (at.Y - own.Pixels.Y) / own.Scaling), "poll", at);
            }
            if (dragging is not { } slot) return;
            if (!cursor.PrimaryButtonDown) Finish("released");
            else if (Clock.GetUtcNow() - lastActivity >= GestureIdleTimeout)
            {
                OverlayDragTrace.Fault(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{(resizing ? "resize" : "drag")} {slot.Placement.WidgetId} ended: no input for {GestureIdleTimeout.TotalSeconds:0} s while the primary button stayed down"));
                Finish("idle-timeout");
            }
        }
        finally { polling = false; }
    }

    private void OnFramePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (slots.FirstOrDefault(item => item.Frame == sender) is not { } slot) return;
        if (!IsEditing && (slot.Placement.Locked || InScrollableList(e.Source as Visual, slot.Frame))) return;
        BeginManipulation(slot, false, e, slot.Frame);
    }

    /// <summary>
    /// 按在元件内放不下而可以滚动的列表上：游戏浮窗上不开始拖动，滚轮滚动列表；拖动从标题、空白处或放得下的列表开始。
    /// </summary>
    private static bool InScrollableList(Visual? source, Visual frame)
    {
        for (var visual = source; visual is not null && visual != frame; visual = visual.GetVisualParent())
            if (visual is ScrollViewer scroller
                && (scroller.Extent.Height > scroller.Viewport.Height + 0.5 || scroller.Extent.Width > scroller.Viewport.Width + 0.5))
                return true;
        return false;
    }

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (slots.FirstOrDefault(item => item.Handle == sender) is not { } slot) return;
        if (IsEditing || !slot.Placement.Locked) BeginManipulation(slot, true, e, slot.Handle);
    }

    // 有系统光标来源时位置取自它（与轮询同一来源，不受指针事件来自哪个窗口影响），否则取指针事件的位置。
    private void OnPointerMovedWhileDragging(object? sender, PointerEventArgs e)
    {
        if (dragging is null) return;
        lastActivity = Clock.GetUtcNow();
        if (CursorSource is not null && area is not null) PollDrag();
        else DragTo(e.GetPosition(this), "pointer", null);
    }

    /// <summary>拖动或改尺寸到光标位置（本画布坐标，逻辑像素）；见 <see cref="DragTo(Point, string, PixelPoint?)"/>。</summary>
    public void DragTo(Point point) => DragTo(point, "pointer", null);

    /// <summary>
    /// 拖动或改尺寸到光标位置（本画布坐标，逻辑像素，可以在画布之外、落在别的显示器上）。指针事件与浮窗的光标轮询都调用它：
    /// 光标移出本窗口后收不到指针移动时，轮询按 GetCursorPos 换算的位置接着拖动。
    /// 位置每次按光标的绝对位置与按下时的抓取点重新计算，不累加增量，来回拖动不漂移。
    /// 光标所在的显示器就是元件的显示器；元件在那里的大小、边距与 8 px 格线都按那块显示器的逻辑像素计（乘以它与本窗口的缩放比之比）。
    /// 元件跨到别的显示器后，那块显示器的浮窗随拖动通知显示它，本画布继续处理移动直到松开。
    /// </summary>
    private void DragTo(Point point, string source, PixelPoint? physical)
    {
        // 应用中再进来（重排或通知回调）直接返回；无效的坐标不参与计算。
        if (dragging is not { } slot || applying || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        applying = true;
        try
        {
            if (!Apply(slot, point, source, physical)) Abort(slot, "invalid-value", null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 任何异常都结束手势并恢复到开始前的摆放，不让它在下一次轮询里重复发生。
            Abort(slot, "exception", error);
        }
        finally { applying = false; }
    }

    // 按光标位置更新摆放；算出的数值无效（非有限、非正）时返回假。
    private bool Apply(Slot slot, Point point, string source, PixelPoint? physical)
    {
        OverlayDragTrace.Sample(source, physical, point, MonitorAt(point)?.Key);
        var placement = slot.Placement;
        if (resizing)
        {
            // 手柄改变外框在缩放前的宽高：拖动距离除以显示倍率，对齐 8 px，不小于元件的最小尺寸，也不超出所在显示器的工作区。
            var delta = point - dragOrigin;
            var factor = Factor(slot);
            if (!double.IsFinite(factor) || factor <= 0) return false;
            var (area, dpi) = RegionOf(placement, Bounds.Size);
            var room = new Size(Math.Max(0, area.Width - Inset * dpi * 2) / factor, Math.Max(0, area.Height - Inset * dpi * 2) / factor);
            double Side(double start, double moved, double minimum, double limit) =>
                Math.Max(minimum, Math.Min(Math.Round((start + moved / factor) / Grid) * Grid, Math.Max(minimum, limit)));
            var width = Math.Min(Side(sizeStart.Width, delta.X, slot.MinimumSize.Width, room.Width), WidgetPlacement.MaximumSide);
            var height = Math.Min(Side(sizeStart.Height, delta.Y, slot.MinimumSize.Height, room.Height), WidgetPlacement.MaximumSide);
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return false;
            // 尺寸没变时不改外框、不要求重新排版：排版后的光标轮询不会再引起一次排版。
            if (width.Equals(placement.Width) && height.Equals(placement.Height)) return true;
            (placement.Width, placement.Height) = (width, height);
            ApplySize(slot);
            InvalidateMeasure();
            return true;
        }
        var screen = placement.Screen;
        if (MonitorAt(point) is { } monitor && monitor.Key != MonitorOf(placement)?.Key) placement.Screen = monitor.Key;
        var (region, scale) = RegionOf(placement, Bounds.Size);
        var size = new Size(sizeStart.Width * placement.Scale * scale, sizeStart.Height * placement.Scale * scale);
        var (inset, grid) = (Inset * scale, Grid * scale);
        // 抓取点留在光标下；外框左上角相对工作区对齐格线。
        var left = region.X + Math.Round((point.X - grab.X * size.Width - region.X) / grid) * grid;
        var top = region.Y + Math.Round((point.Y - grab.Y * size.Height - region.Y) / grid) * grid;
        var free = new Size(region.Width - size.Width - inset * 2, region.Height - size.Height - inset * 2);
        var (x, y) = (free.Width <= 0 ? 0 : Math.Clamp((left - region.X - inset) / free.Width, 0, 1),
            free.Height <= 0 ? 0 : Math.Clamp((top - region.Y - inset) / free.Height, 0, 1));
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        if (x.Equals(placement.X) && y.Equals(placement.Y) && screen == placement.Screen) return true;
        (placement.X, placement.Y) = (x, y);
        // 各显示器的浮窗（包括本画布）随之重排；元件跨到另一块显示器时由那块显示器的浮窗接着显示。
        if (store is not null) store.NotifyMoved();
        else Refresh();
        return true;
    }

    // 中止手势：摆放恢复到手势开始前，记一条诊断，按结束处理（各浮窗随之重排）。
    private void Abort(Slot slot, string reason, Exception? error)
    {
        var placement = slot.Placement;
        (placement.X, placement.Y, placement.Screen, placement.Width, placement.Height) = gestureStart;
        OverlayDragTrace.Fault($"{(resizing ? "resize" : "drag")} {placement.WidgetId} aborted ({reason}) and restored"
                               + (error is null ? string.Empty : $": {error.GetType().Name}: {error.Message}"));
        ApplySize(slot);
        Finish(reason);
    }

    // 画布上某一点所在的显示器；点落在显示器之间的空隙里时取距离最近的一块。
    private OverlayMonitor? MonitorAt(Point point)
    {
        if (monitors.Count == 0) return null;
        double Distance(Rect rect) => Math.Max(Math.Max(rect.X - point.X, point.X - rect.Right), 0) + Math.Max(Math.Max(rect.Y - point.Y, point.Y - rect.Bottom), 0);
        return monitors.FirstOrDefault(monitor => monitor.Area.Contains(point)) ?? monitors.MinBy(monitor => Distance(monitor.Area));
    }

    private void OnPointerReleasedAfterDragging(object? sender, PointerReleasedEventArgs e)
    {
        if (dragging is null) return;
        lastActivity = Clock.GetUtcNow();
        // 松开处的位置按同一来源再取一次，再结束并保存。
        if (CursorSource is not null && area is not null) PollDrag();
        if (dragging is not null) Finish("pointer-released");
        e.Pointer.Capture(null);
    }

    // 指针捕获被收回：没有系统光标来源时按松开处理并保存；有来源时只记录，拖动继续由轮询驱动，主按键松开才结束。
    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (dragging is null) return;
        OverlayDragTrace.Note("capture-lost");
        if (CursorSource is null) Finish("capture-lost");
    }

    /// <summary>结束正在进行的拖动或缩放并保存。</summary>
    public void FinishManipulation()
    {
        if (dragging is not null) Finish("finished");
    }

    // 松开后保存；拖到别的显示器的元件此后只留在那块显示器的浮窗里。
    private void Finish(string reason)
    {
        var placement = dragging?.Placement;
        dragging = null;
        OverlayDragTrace.End(reason, placement);
        ManipulationChanged?.Invoke(this, EventArgs.Empty);
        if (store is not null) store.Commit();
        else UpdateStates();
    }

    private void OnLockPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        pressedLock = slots.FirstOrDefault(item => item.Lock == sender);
        if (pressedLock is null) return;
        e.Pointer.Capture(pressedLock.Lock);
        e.Handled = true;
    }

    // 在锁图标上按下并在锁图标上松开才切换，与按钮的点击一致。
    private void OnLockReleased(object? sender, PointerReleasedEventArgs e)
    {
        var slot = pressedLock;
        pressedLock = null;
        if (slot is null || slot.Lock != sender) return;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (new Rect(slot.Lock.Bounds.Size).Contains(e.GetPosition(slot.Lock))) store?.ToggleLock(slot.Placement);
    }

    // 关闭按钮：不论锁状态都接收鼠标；按下并在按钮上松开才生效。平时关闭元件（从浮窗上消失，摆放保留），
    // 编辑状态下按在已关闭（变淡）的元件上则重新显示它。
    private void OnClosePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        pressedClose = slots.FirstOrDefault(item => item.Close == sender);
        if (pressedClose is null) return;
        e.Pointer.Capture(pressedClose.Close);
        e.Handled = true;
    }

    private void OnCloseReleased(object? sender, PointerReleasedEventArgs e)
    {
        var slot = pressedClose;
        pressedClose = null;
        if (slot is null || slot.Close != sender) return;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (!new Rect(slot.Close.Bounds.Size).Contains(e.GetPosition(slot.Close))) return;
        if (slot.Placement.Closed) store?.Reopen(slot.Placement);
        else store?.Close(slot.Placement);
    }

    // “移到下一块显示器”：编辑状态下按下并在按钮上松开，元件改属下一块显示器（比例位置不变）并保存。
    private void OnNextPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEditing || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        pressedNext = slots.FirstOrDefault(item => item.Next == sender);
        if (pressedNext is null) return;
        e.Pointer.Capture(pressedNext.Next);
        e.Handled = true;
    }

    private void OnNextReleased(object? sender, PointerReleasedEventArgs e)
    {
        var slot = pressedNext;
        pressedNext = null;
        if (slot is null || slot.Next != sender) return;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (!new Rect(slot.Next.Bounds.Size).Contains(e.GetPosition(slot.Next))) return;
        if (OverlayArea.Next(monitors, MonitorOf(slot.Placement)?.Key) is { } target) store?.MoveTo(slot.Placement, target.Key);
    }

    // “－／＋”：只在元件解锁或处于编辑状态时显示并接收鼠标；按下并在同一按钮上松开才改变缩放。
    private void OnZoomPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var slot = slots.FirstOrDefault(item => ((StackPanel)item.Zoom.Child!).Children.Contains(button));
        if (slot is null || (!IsEditing && slot.Placement.Locked)) return;
        (pressedZoom, pressedZoomName) = (slot, button.Name);
        e.Pointer.Capture(button);
        e.Handled = true;
    }

    private void OnZoomReleased(object? sender, PointerReleasedEventArgs e)
    {
        var slot = pressedZoom;
        pressedZoom = null;
        if (slot is null || sender is not Control button) return;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (button.Name != pressedZoomName || !new Rect(button.Bounds.Size).Contains(e.GetPosition(button))) return;
        if (button.Name == "opacity")
        {
            slot.Placement.Opacity = WidgetPlacement.CycleOpacity(slot.Placement.Opacity);
            store?.Commit();
            return;
        }
        var next = WidgetPlacement.Step(slot.Placement.Scale, button.Name == "plus");
        if (next.Equals(slot.Placement.Scale)) return;
        slot.Placement.Scale = next;
        store?.Commit();
    }

    // 在不透明度按钮上滚动滚轮：向上加 10%，向下减 10%，限制在 20%–100%。
    private void OnOpacityWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Control button || e.Delta.Y == 0) return;
        var slot = slots.FirstOrDefault(item => ((StackPanel)item.Zoom.Child!).Children.Contains(button));
        if (slot is null || (!IsEditing && slot.Placement.Locked)) return;
        e.Handled = true;
        var next = WidgetPlacement.StepOpacity(slot.Placement.Opacity, e.Delta.Y > 0);
        if (next.Equals(slot.Placement.Opacity)) return;
        slot.Placement.Opacity = next;
        store?.Commit();
    }
}
