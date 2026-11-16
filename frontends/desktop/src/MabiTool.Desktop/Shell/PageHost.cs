using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 主窗口的页面区：显示当前模块的页面外框（<see cref="PageFrame"/>），切换时两层交叉淡入淡出。
/// <para>
/// 显示过的外框一直留在这里的逻辑树上，只有当前（与正在淡出的）外框在视觉树上。切走的页面因此不脱离逻辑树：
/// 回来时不重新套用样式、不重新解析资源，只需重新挂上视觉树并排版。用 <c>TransitioningContentControl</c> 换内容时，
/// 旧页面整棵脱离逻辑树，回来时上千个控件的样式全部重新匹配，这是回到保留页面仍要几百毫秒的主要原因。
/// 视觉树上只有当前页面，所以按视觉树查找控件、指针命中与页面的挂上／取下事件（例如诊断页停止轮询）都与以前相同。
/// </para>
/// </summary>
public sealed class PageHost : Control
{
    public static readonly StyledProperty<Control?> CurrentProperty =
        AvaloniaProperty.Register<PageHost, Control?>(nameof(Current));

    private CancellationTokenSource? transition;

    /// <summary>当前显示的外框。</summary>
    public Control? Current { get => GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

    /// <summary>切换动效；为空时直接切换。</summary>
    public IPageTransition? PageTransition { get; set; }

    /// <summary>留在逻辑树上的外框（包括当前的）；测试据此确认切走的页面没有被取下。</summary>
    public IReadOnlyList<ILogical> Kept => LogicalChildren;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CurrentProperty) Switch(change.GetOldValue<Control?>(), change.GetNewValue<Control?>());
    }

    private void Switch(Control? from, Control? to)
    {
        transition?.Cancel();
        transition = null;
        if (to is not null)
        {
            if (!LogicalChildren.Contains(to)) LogicalChildren.Add(to);
            if (!VisualChildren.Contains(to)) VisualChildren.Add(to);
            to.IsVisible = true;
        }
        // 上一次切换还没淡完时留下的第三层直接取下。
        foreach (var stray in VisualChildren.OfType<Control>().Where(child => child != from && child != to).ToList())
            VisualChildren.Remove(stray);
        InvalidateMeasure();
        if (from is null || from == to || !VisualChildren.Contains(from)) return;
        if (PageTransition is not { } fade)
        {
            VisualChildren.Remove(from);
            return;
        }
        var cancel = transition = new CancellationTokenSource();
        _ = Run(fade, from, to, cancel);
    }

    private async Task Run(IPageTransition fade, Control from, Control? to, CancellationTokenSource cancel)
    {
        await fade.Start(from, to, true, cancel.Token);
        if (cancel.IsCancellationRequested || Current == from) return;
        VisualChildren.Remove(from);
        from.IsVisible = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = default(Size);
        foreach (var child in VisualChildren.OfType<Control>())
        {
            child.Measure(availableSize);
            size = new Size(Math.Max(size.Width, child.DesiredSize.Width), Math.Max(size.Height, child.DesiredSize.Height));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in VisualChildren.OfType<Control>()) child.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
