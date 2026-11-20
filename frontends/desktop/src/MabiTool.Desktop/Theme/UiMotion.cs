using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MabiTool.Desktop.Theme;

/// <summary>
/// 动效的统一开关与时长。窗口带有 motion 类时，共用样式里的过渡与入场动画才生效；
/// 关闭后所有状态立即切换，供偏好减弱动效的用户和无界面测试使用。
/// 用户在设置页明确选择过时以选择为准；从未选择时跟随 Windows“显示动画效果”。
/// </summary>
public static class UiMotion
{
    /// <summary>悬停、按下等即时反馈。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);
    /// <summary>选中、切换、数值变化。</summary>
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(200);
    /// <summary>页面切换与面板入场。</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(280);
    /// <summary>图标的一次动效。</summary>
    public static readonly TimeSpan Icon = TimeSpan.FromMilliseconds(520);

    /// <summary>展开与收起：高度与不透明度的过渡（见 <see cref="Controls.Expander"/>）。</summary>
    public static readonly TimeSpan Expand = TimeSpan.FromMilliseconds(220);

    private static bool? choice;
    private static bool systemReduced;

    public static event EventHandler? Changed;

    /// <summary>用户在设置页明确做出的选择；为空表示从未选择，此时跟随系统。保存在偏好文件里。</summary>
    public static bool? Choice => choice;

    /// <summary>
    /// 设置页显示的“开启／减弱”：用户选择过时为该选择，否则跟随系统（系统关闭动画时为减弱）。
    /// 赋值即用户的明确选择，之后系统设置变化不再改变它。
    /// </summary>
    public static bool Enabled
    {
        get => choice ?? !systemReduced;
        set => Restore(value);
    }

    /// <summary>恢复保存的选择（为空即跟随系统）。</summary>
    public static void Restore(bool? value)
    {
        if (choice == value) return;
        choice = value;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 系统设置里关闭了动画（Windows“显示动画效果”，<c>SPI_GETCLIENTAREAANIMATION</c>）。只决定用户从未选择时的取值。
    /// 由 <see cref="ReadSystemPreference"/> 在启动时与系统设置变化时读取；无界面测试不读取，保持为假。
    /// </summary>
    public static bool SystemReduced
    {
        get => systemReduced;
        set
        {
            if (systemReduced == value) return;
            systemReduced = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>是否读到过系统的动画设置；非 Windows 或读取失败时为假。</summary>
    public static bool SystemKnown { get; private set; }

    /// <summary>动效实际是否生效，与 <see cref="Enabled"/> 相同。</summary>
    public static bool Active => Enabled;

    /// <summary>读取 Windows 的“显示动画效果”；其他平台或读取失败时保持原值。</summary>
    public static void ReadSystemPreference()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var animate = 1;
            if (!SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref animate, 0)) return;
            SystemKnown = true;
            SystemReduced = animate == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>
    /// 在窗口上跟随系统设置的变化：系统设置广播、显示设置变化、会话切换（连上或断开远程桌面）与窗口激活时重新读取。
    /// 用户选择过时读取结果只用于诊断，不改变动效。
    /// </summary>
    public static void FollowSystem(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        Win32Properties.AddWndProcHookCallback(window, (IntPtr _, uint message, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (message is WmSettingChange or WmDisplayChange or WmSessionChange) Dispatcher.UIThread.Post(ReadSystemPreference);
            return IntPtr.Zero;
        });
        window.Activated += (_, _) => ReadSystemPreference();
        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
            try { WTSRegisterSessionNotification(handle, 0); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        };
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSessionChange = 0x02B1;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);

    /// <summary>让窗口的 motion 类跟随开关。</summary>
    public static void Attach(StyledElement window)
    {
        void Apply() => window.Classes.Set("motion", Active);
        Apply();
        EventHandler handler = (_, _) => Apply();
        Changed += handler;
        window.DetachedFromLogicalTree += (_, _) => Changed -= handler;
    }

    /// <summary>页面切换的交叉淡入淡出。关闭动效时不过渡。</summary>
    public static IPageTransition? PageTransition => Active ? new PageFade(Base) : null;

    /// <summary>诊断用的一行：动效的设置值、系统动画是否开启、实际是否生效。</summary>
    public static string Describe() =>
        $"设置{(choice switch { true => "开启", false => "减弱", null => "未选择（跟随系统）" })}，"
        + $"系统动画{(SystemKnown ? (systemReduced ? "关闭" : "开启") : "未读取")}，实际{(Active ? "生效" : "不生效")}";
}

/// <summary>前端的渲染方式：主窗口打开后读取一次，供诊断导出。</summary>
public static class UiRendering
{
    /// <summary>为真时硬件加速，为假时软件渲染，为空时尚未读取。</summary>
    public static bool? Hardware { get; private set; }

    public static string Describe() => Hardware switch { true => "硬件加速", false => "软件渲染", null => "未读取" };

    /// <summary>按窗口合成器是否提供 GPU 互操作判断：软件渲染没有 GPU 上下文。</summary>
    public static async Task DetectAsync(Visual window)
    {
        if (Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(window)?.Compositor is not { } compositor) return;
        try { Hardware = await compositor.TryGetCompositionGpuInterop() is not null; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException) { }
    }
}

/// <summary>
/// 页面切换的交叉淡入淡出，透明度逐帧写成本地值，结束、取消或中途减弱动效时两页都回到不透明。
/// <para>
/// 不用 Avalonia 的 CrossFade：它的淡出动画结束后把透明度 0 保留在动画优先级上，
/// 切换容器的两个内容层轮流使用，之后不经过渡（减弱动效）放进那一层的页面整页不可见。
/// </para>
/// </summary>
public sealed class PageFade(TimeSpan duration) : IPageTransition
{
    private static readonly CubicEaseOut FadeIn = new();
    private static readonly CubicEaseIn FadeOut = new();

    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (to is not null) to.IsVisible = true;
        // 淡出中的旧页面不再接收指针：两个内容层叠放，旧页面所在的层可能在上面，切换后的第一次点击会落到正在淡出的旧页面上。
        if (from is InputElement leaving) leaving.IsHitTestVisible = false;
        if (to is InputElement entering) entering.ClearValue(InputElement.IsHitTestVisibleProperty);
        var top = TopLevel.GetTopLevel(to ?? from);
        if (top is null || !UiMotion.Active || cancellationToken.IsCancellationRequested)
        {
            Finish(from, to, hideFrom: !cancellationToken.IsCancellationRequested);
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource();
        TimeSpan? started = null;
        var registration = cancellationToken.Register(() =>
        {
            Finish(from, to, hideFrom: false);
            done.TrySetResult();
        });
        if (to is not null) to.Opacity = 0;

        void Frame(TimeSpan time)
        {
            if (done.Task.IsCompleted) return;
            started ??= time;
            var progress = (time - started.Value).TotalMilliseconds / duration.TotalMilliseconds;
            // 动效在切换途中被减弱：直接到终态。
            if (progress >= 1 || !UiMotion.Active)
            {
                registration.Dispose();
                Finish(from, to, hideFrom: true);
                done.TrySetResult();
                return;
            }
            if (to is not null) to.Opacity = FadeIn.Ease(progress);
            if (from is not null) from.Opacity = 1 - FadeOut.Ease(progress);
            top.RequestAnimationFrame(Frame);
        }

        top.RequestAnimationFrame(Frame);
        return done.Task;
    }

    private static void Finish(Visual? from, Visual? to, bool hideFrom)
    {
        if (to is not null)
        {
            to.ClearValue(Visual.OpacityProperty);
            to.ClearValue(InputElement.IsHitTestVisibleProperty);
            to.IsVisible = true;
        }
        if (from is null) return;
        from.ClearValue(Visual.OpacityProperty);
        from.ClearValue(InputElement.IsHitTestVisibleProperty);
        if (hideFrom) from.IsVisible = false;
    }
}
