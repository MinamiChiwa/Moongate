using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;

namespace MabiTool.Desktop.Theme;

/// <summary>
/// 动效的统一开关与时长。窗口带有 motion 类时，共用样式里的过渡与入场动画才生效；
/// 关闭后所有状态立即切换，供偏好减弱动效的用户和无界面测试使用。
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

    private static bool enabled = true;
    private static bool systemReduced;

    public static event EventHandler? Changed;

    /// <summary>设置页的“开启／减弱”选择。</summary>
    public static bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 系统设置里关闭了动画（Windows“显示动画效果”，<c>SPI_GETCLIENTAREAANIMATION</c>）。为真时不论设置页如何选择，都按减弱处理。
    /// 应用启动时由 <see cref="ReadSystemPreference"/> 读取一次；无界面测试不读取，保持为假。
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

    /// <summary>动效实际是否生效：设置页为开启且系统没有关闭动画。</summary>
    public static bool Active => enabled && !systemReduced;

    /// <summary>读取 Windows 的“显示动画效果”；其他平台或读取失败时按未关闭处理。</summary>
    public static void ReadSystemPreference()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var animate = 1;
            if (SystemParametersInfo(0x1042, 0, ref animate, 0)) SystemReduced = animate == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

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
