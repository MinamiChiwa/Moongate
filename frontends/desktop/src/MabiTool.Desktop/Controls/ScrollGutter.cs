using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 全前端滚动区的滚动条槽位。Fluent 主题的滚动条浮在内容之上、不占布局宽度，内容会被滚动条压住；
/// 这里对每个滚动区按方向固定留出槽位：纵向可滚动（<see cref="ScrollBarVisibility.Auto"/> 或 <see cref="ScrollBarVisibility.Visible"/>）
/// 时内容区右侧让出槽位，横向可滚动时底部让出槽位。槽位不随滚动条出现或消失变化，内容不会左右跳动、重新换行或改变列宽。
/// <para>
/// 槽位宽度是滚动条宽度（主题资源 <c>ScrollBarSize</c>）加一段固定间距，右对齐的内容与滚动条之间总是这段间距。
/// 内容自身外边距已经留出的部分不再叠加（页面级滚动区把滚动条放在页面边距里）。
/// 输入框内部的滚动区和没有滚动条部件的滚动区（菜单的上下翻页按钮）不留槽位。
/// 槽位里的滚轮操作滚动所在的滚动区，不落到外层。
/// </para>
/// </summary>
public static class ScrollGutter
{
    /// <summary>滚动条与内容之间的间距。</summary>
    public const double Gap = 4;

    private const double FallbackBarSize = 16;
    private const double WheelStep = 50;
    private static bool installed;

    /// <summary>注册到所有滚动区；应用初始化时调用一次。</summary>
    public static void Install()
    {
        if (installed) return;
        installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ScrollViewer>((scroller, _) => Apply(scroller));
        ScrollViewer.VerticalScrollBarVisibilityProperty.Changed.AddClassHandler<ScrollViewer>((scroller, _) => Apply(scroller));
        ScrollViewer.HorizontalScrollBarVisibilityProperty.Changed.AddClassHandler<ScrollViewer>((scroller, _) => Apply(scroller));
        ContentControl.ContentProperty.Changed.AddClassHandler<ScrollViewer>((scroller, _) => Apply(scroller));
        InputElement.PointerWheelChangedEvent.AddClassHandler<ScrollViewer>(OnWheel, RoutingStrategies.Bubble);
    }

    /// <summary>槽位宽度：滚动条宽度加间距。</summary>
    public static double Width(Control anchor) =>
        (anchor.TryFindResource("ScrollBarSize", anchor.ActualThemeVariant, out var value) && value is double size ? size : FallbackBarSize) + Gap;

    /// <summary>滚动区内容区当前让出的槽位（右侧与底部）。</summary>
    public static Thickness Reserved(ScrollViewer? scroller) =>
        scroller?.Presenter is ScrollContentPresenter presenter ? presenter.Margin : default;

    private static void Apply(ScrollViewer scroller)
    {
        if (scroller.Presenter is not ScrollContentPresenter presenter) return;
        if (scroller.TemplatedParent is TextBox)
        {
            presenter.ClearValue(Layoutable.MarginProperty);
            return;
        }

        var width = Width(scroller);
        var content = (scroller.Content as Layoutable)?.Margin ?? default;
        var right = Reserves(scroller, scroller.VerticalScrollBarVisibility, "PART_VerticalScrollBar") ? Math.Max(0, width - content.Right) : 0;
        var bottom = Reserves(scroller, scroller.HorizontalScrollBarVisibility, "PART_HorizontalScrollBar") ? Math.Max(0, width - content.Bottom) : 0;
        presenter.Margin = new Thickness(0, 0, right, bottom);
    }

    private static bool Reserves(ScrollViewer scroller, ScrollBarVisibility visibility, string part) =>
        visibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible
        && scroller.GetVisualDescendants().OfType<ScrollBar>().Any(bar => bar.Name == part && bar.TemplatedParent == scroller);

    // 指针在槽位上（包括滚动条本身）：滚轮事件不经过内容区，在这里按内容区的步长滚动本滚动区。
    private static void OnWheel(ScrollViewer scroller, PointerWheelEventArgs e)
    {
        if (e.Handled || scroller.Presenter is not ScrollContentPresenter presenter) return;
        if (e.Source is Visual source && (source == presenter || presenter.IsVisualAncestorOf(source))) return;
        var offset = scroller.Offset;
        var max = new Vector(Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width), Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
        var delta = e.Delta;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && delta.X == 0) delta = new Vector(delta.Y, 0);
        var next = new Vector(
            Math.Clamp(offset.X - delta.X * WheelStep, 0, max.X),
            Math.Clamp(offset.Y - delta.Y * WheelStep, 0, max.Y));
        if (next == offset) return;
        scroller.Offset = next;
        e.Handled = true;
    }
}
