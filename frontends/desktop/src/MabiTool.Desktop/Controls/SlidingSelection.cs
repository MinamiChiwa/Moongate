using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Controls;

/// <summary>一次滑动的起点与终点，见 <see cref="SlidingSelection.Started"/>。</summary>
public sealed class SlideStartedEventArgs(Button target, Rect from, Rect to) : EventArgs
{
    /// <summary>新选中的按钮。</summary>
    public Button Target { get; } = target;

    /// <summary>起点：原选中项（或滑动中途滑块）在容器坐标里的位置。</summary>
    public Rect From { get; } = from;

    /// <summary>终点：新选中项在容器坐标里的位置。</summary>
    public Rect To { get; } = to;
}

/// <summary>
/// 选择容器：外观与 <see cref="Border"/> 相同（沿用 Border 的样式），并在自己的背景之上、所有子元素之下画选中底色的滑块。
/// 容器内的按钮选中时设 <see cref="SlidingSelection.IsSelectedProperty"/>；共用样式已让容器内带 <c>active</c>
/// 或 <c>selected</c> 类的 <c>Button.segment</c>、<c>Button.nav</c>、<c>Button.chip</c>、<c>Button.tile</c> 成为选中项。
/// </summary>
public class SelectionBorder : Border
{
    private readonly SliderLayer layer;

    public SelectionBorder()
    {
        // 样式按 Border 匹配，用伪类 :selectionhost 区分选择容器。
        PseudoClasses.Add(":selectionhost");
        // 滑块层是第一个可视子元素：画在 Border 自己的背景之上、内容之下，铺满整个容器。
        layer = new SliderLayer(this);
        VisualChildren.Insert(0, layer);
        SlidingSelection.Attach(this);
    }

    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>重画滑块。</summary>
    internal void InvalidateSlider() => layer.InvalidateVisual();

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        layer.Measure(finalSize);
        layer.Arrange(new Rect(finalSize));
        return result;
    }

    private sealed class SliderLayer : Control
    {
        private readonly SelectionBorder host;

        public SliderLayer(SelectionBorder host)
        {
            this.host = host;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context) => SlidingSelection.Paint(host, context);
    }
}

/// <summary>
/// 选中底色的滑动：点击或用键盘切换选中项时，选中底色从原选中项的位置和尺寸滑到新选中项，
/// 而不是原地淡出、新处淡入。
/// <para>
/// 每个容器（<see cref="SelectionBorder"/>）只有一个滑块，由容器在自己的坐标里画在所有子元素之下，
/// 不受单个按钮或其祖先的裁剪影响。滑动期间新选中项自己的底色与边框暂时透明，滑块取它选中时的底色、边框与圆角；
/// 滑动结束后恢复，静止时选中底色由按钮自己画出。
/// </para>
/// <para>
/// 只有窗口带 motion 类、且选中变化发生在同一容器内一次点击之后 <see cref="ArmWindow"/> 以内时才滑动；
/// 首次显示、窗口尺寸变化、语言切换和后端状态带来的变化都直接到位。
/// </para>
/// </summary>
public static class SlidingSelection
{
    /// <summary>滑动时长，取“选中、数值变化”一档。</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(260);

    /// <summary>点击之后多久以内发生的选中变化算作这次点击的结果。</summary>
    public static readonly TimeSpan ArmWindow = TimeSpan.FromMilliseconds(600);

    public static readonly AttachedProperty<bool> IsSelectedProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsSelected", typeof(SlidingSelection));

    private static readonly ConditionalWeakTable<SelectionBorder, HostState> States = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>测试用：设定后滑动停在这个进度（0–1），不随时间前进。</summary>
    internal static double? FrozenProgress { get; set; }

    static SlidingSelection()
    {
        IsSelectedProperty.Changed.AddClassHandler<Button>((button, change) =>
        {
            if (change.NewValue is true) Schedule(button);
            else Deselected(button);
        });
    }

    public static bool GetIsSelected(Button button) => button.GetValue(IsSelectedProperty);
    public static void SetIsSelected(Button button, bool value) => button.SetValue(IsSelectedProperty, value);

    /// <summary>
    /// 一次滑动开始时触发，发送者是容器；此时滑块已放在起点。供测试与调试观察滑动的起点和终点。
    /// </summary>
    public static event EventHandler<SlideStartedEventArgs>? Started;

    /// <summary>该容器此刻是否有底色在滑动。</summary>
    public static bool IsSliding(Control host) => host is SelectionBorder border && States.TryGetValue(border, out var state)
        && state.Flight is not null;

    /// <summary>滑块此刻在容器坐标里的位置；没有滑动时为空。</summary>
    public static Rect? SliderRect(Control host) => host is SelectionBorder border && States.TryGetValue(border, out var state)
        && state.Flight is { } flight ? flight.Current : null;

    /// <summary>
    /// 减速曲线，末尾带约 1% 的轻微回弹：进度 0 时为 0，1 时为 1。
    /// </summary>
    public static double Ease(double progress)
    {
        const double back = 0.6;
        var p = Math.Clamp(progress, 0, 1) - 1;
        return 1 + (back + 1) * p * p * p + back * p * p;
    }

    /// <summary>按钮最近的选择容器；按钮不在任何容器里时为空。</summary>
    public static SelectionBorder? HostOf(Visual visual) =>
        visual.GetVisualAncestors().OfType<SelectionBorder>().FirstOrDefault();

    internal static void Attach(SelectionBorder host) =>
        host.AddHandler(Button.ClickEvent, OnHostClick, RoutingStrategies.Bubble, handledEventsToo: true);

    // 点击时记下原选中项的位置（正在滑动时取滑块此刻的位置），在之后的选中变化里作为起点。
    private static void OnHostClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not SelectionBorder host || e.Source is not Visual source) return;
        var clicked = source as Button ?? source.FindAncestorOfType<Button>();
        if (clicked is null || HostOf(clicked) != host) return;
        var state = States.GetOrCreateValue(host);
        state.From = state.Flight is { } flight ? flight.Current : SelectedRect(host);
        state.ArmedAt = Clock.Elapsed;
    }

    private static Rect? SelectedRect(SelectionBorder host)
    {
        var selected = host.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => GetIsSelected(button) && button.IsEffectivelyVisible && HostOf(button) == host);
        return selected is null ? null : LayoutRect(selected, host);
    }

    // 选中项改变后等这一轮排版结束再决定是否滑动：新生成的按钮此时才有位置。
    private static void Schedule(Button button)
    {
        EventHandler? handler = null;
        var deadline = Clock.Elapsed + ArmWindow;
        handler = (_, _) =>
        {
            if (button.IsAttachedToVisualTree() && button.Bounds.Width <= 0 && Clock.Elapsed < deadline) return;
            button.LayoutUpdated -= handler;
            Resolve(button);
        };
        button.LayoutUpdated += handler;
        button.InvalidateMeasure();
    }

    private static void Deselected(Button button)
    {
        if (!button.IsAttachedToVisualTree() || HostOf(button) is not { } host
            || !States.TryGetValue(host, out var state) || state.Flight?.Button != button)
            return;
        // 滑动中途又换了选中项：从滑块此刻的位置继续滑向新选中项。
        state.From = state.Flight.Current;
        state.ArmedAt = Clock.Elapsed;
        Stop(host, state);
    }

    private static void Resolve(Button button)
    {
        if (!GetIsSelected(button) || !button.IsAttachedToVisualTree() || HostOf(button) is not { } host) return;
        var state = States.GetOrCreateValue(host);
        var from = state.From;
        var armed = from is not null && Clock.Elapsed - state.ArmedAt <= ArmWindow;
        // 列表重新生成时，正在滑动的按钮被新按钮替换：新按钮从滑块此刻的位置接着滑。
        if (!armed && state.Flight is { } running && running.Button != button && Clock.Elapsed - running.LastFrame <= ArmWindow)
        {
            from = running.Current;
            armed = true;
        }
        state.From = null;
        if (state.Flight is not null) Stop(host, state);
        if (!armed || !MotionEnabled(host) || !button.IsEffectivelyVisible) return;

        var to = LayoutRect(button, host);
        if (to.Width <= 0 || to.Height <= 0 || from!.Value.Width <= 0 || from.Value.Height <= 0) return;
        if (Math.Abs(from.Value.X - to.X) < 0.5 && Math.Abs(from.Value.Y - to.Y) < 0.5
            && Math.Abs(from.Value.Width - to.Width) < 0.5 && Math.Abs(from.Value.Height - to.Height) < 0.5)
            return;
        Start(host, state, button, from.Value, to);
    }

    private static bool MotionEnabled(Control host) => TopLevel.GetTopLevel(host)?.Classes.Contains("motion") == true;

    private static void Start(SelectionBorder host, HostState state, Button button, Rect from, Rect to)
    {
        // 滑块取新选中项选中时的外观；随后按钮自己的底色与边框在滑动期间透明，避免终点先亮起。
        var flight = new Flight(button, from, to, button.Background, button.BorderBrush, button.BorderThickness.Left,
            button.CornerRadius) { LastFrame = Clock.Elapsed };
        flight.Suppress();
        state.Flight = flight;
        Apply(host, flight, FrozenProgress ?? 0);
        Started?.Invoke(host, new SlideStartedEventArgs(button, from, to));
        TopLevel.GetTopLevel(host)?.RequestAnimationFrame(time => Tick(host, state, flight, time));
    }

    private static void Tick(SelectionBorder host, HostState state, Flight flight, TimeSpan time)
    {
        if (state.Flight != flight) return;
        flight.Started ??= time;
        var progress = FrozenProgress ?? (time - flight.Started.Value).TotalMilliseconds / Duration.TotalMilliseconds;
        if (progress >= 1 || !MotionEnabled(host))
        {
            Stop(host, state);
            return;
        }
        // 按钮已被替换（列表重新生成）：停在这里，等新按钮接着滑；没有接替时由下一次选中变化收尾。
        if (!flight.Button.IsAttachedToVisualTree()) return;
        Apply(host, flight, progress);
        if (TopLevel.GetTopLevel(host) is { } top) top.RequestAnimationFrame(next => Tick(host, state, flight, next));
        else Stop(host, state);
    }

    private static void Apply(SelectionBorder host, Flight flight, double progress)
    {
        flight.Eased = Ease(progress);
        flight.LastFrame = Clock.Elapsed;
        // 终点按此刻的排版重取：滑动期间滚动或排版变化时仍落在新选中项上。
        if (flight.Button.IsAttachedToVisualTree() && HostOf(flight.Button) == host) flight.To = LayoutRect(flight.Button, host);
        host.InvalidateSlider();
    }

    private static void Stop(SelectionBorder host, HostState state)
    {
        if (state.Flight is not { } flight) return;
        state.Flight = null;
        flight.Restore();
        host.InvalidateSlider();
    }

    internal static void Paint(SelectionBorder host, DrawingContext context)
    {
        if (!States.TryGetValue(host, out var state) || state.Flight is not { } flight) return;
        var rect = flight.Current;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var thickness = flight.BorderThickness;
        var pen = flight.BorderBrush is { } edge && thickness > 0 ? new Pen(edge, thickness) : null;
        var inner = pen is null ? rect : rect.Deflate(thickness / 2);
        var radius = Math.Min(flight.CornerRadius.TopLeft, Math.Min(inner.Width, inner.Height) / 2);
        context.DrawRectangle(flight.Background, pen, new RoundedRect(inner, radius));
    }

    // 按钮在容器坐标里的排版位置（含祖先与按钮自身的变换）。
    private static Rect LayoutRect(Button button, Visual host) =>
        new Rect(button.Bounds.Size).TransformToAABB(button.TransformToVisual(host) ?? Matrix.Identity);

    private sealed class HostState
    {
        public Rect? From;
        public TimeSpan ArmedAt;
        public Flight? Flight;
    }

    private sealed class Flight(Button button, Rect from, Rect to, IBrush? background, IBrush? borderBrush,
        double borderThickness, CornerRadius cornerRadius)
    {
        private IDisposable?[] suppressed = [];

        public Button Button { get; } = button;
        public Rect To { get; set; } = to;
        public IBrush? Background { get; } = background;
        public IBrush? BorderBrush { get; } = borderBrush;
        public double BorderThickness { get; } = borderThickness;
        public CornerRadius CornerRadius { get; } = cornerRadius;
        public double Eased { get; set; }
        public TimeSpan LastFrame { get; set; }
        public TimeSpan? Started { get; set; }

        /// <summary>滑块此刻在容器坐标里的位置。</summary>
        public Rect Current
        {
            get
            {
                var t = Eased;
                return new Rect(from.X + (To.X - from.X) * t, from.Y + (To.Y - from.Y) * t,
                    Math.Max(0, from.Width + (To.Width - from.Width) * t), Math.Max(0, from.Height + (To.Height - from.Height) * t));
            }
        }

        // 以动画优先级压住按钮自己的底色与边框：盖过样式与本地值，结束时撤销即回到原值。
        public void Suppress()
        {
            suppressed =
            [
                Button.SetValue(TemplatedControl.BackgroundProperty, Brushes.Transparent, BindingPriority.Animation),
                Button.SetValue(TemplatedControl.BorderBrushProperty, Brushes.Transparent, BindingPriority.Animation)
            ];
        }

        public void Restore()
        {
            foreach (var handle in suppressed) handle?.Dispose();
            suppressed = [];
        }
    }
}
