using Avalonia;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 一个元件在游戏浮窗里的矩形（逻辑像素，窗口坐标）：外框、外框内右上角的锁图标与锁左侧的“－／＋”（只在解锁时显示，
/// 隐藏时为空矩形）；尺寸手柄也在外框内。<see cref="Visible"/> 为假表示元件因展示主题缺失而不显示。
/// </summary>
public readonly record struct WidgetHitArea(Rect Frame, Rect Lock, bool Locked, bool Visible, Rect Zoom = default);

/// <summary>
/// 游戏浮窗的两种区域。锁图标与缩放手柄都在外框内，所以：
/// 画出内容的区域（<see cref="Painted"/>）是各可见元件的外框；
/// 接收鼠标的区域（<see cref="Interactive"/>）是已解锁元件的外框（含其中的“－／＋”）与已上锁元件的锁图标。
/// 隐藏的元件两者都不计。逻辑矩形按窗口缩放比例换成物理像素并向外取整，裁到窗口范围内。
/// </summary>
public static class OverlayHitRegion
{
    public static IReadOnlyList<PixelRect> Interactive(IEnumerable<WidgetHitArea> widgets, double scaling, PixelSize window) =>
        Rectangles(widgets.Where(widget => widget.Visible)
            .Select(widget => widget.Locked ? widget.Lock : Union(widget.Frame, widget.Zoom)), scaling, window);

    public static IReadOnlyList<PixelRect> Painted(IEnumerable<WidgetHitArea> widgets, double scaling, PixelSize window) =>
        Rectangles(widgets.Where(widget => widget.Visible).Select(widget => Union(widget.Frame, widget.Zoom)), scaling, window);

    // “－／＋”在外框内，并集通常就是外框；元件缩得很小时也保证按钮整块计入。
    private static Rect Union(Rect frame, Rect zoom) => zoom.Width > 0 && zoom.Height > 0 ? frame.Union(zoom) : frame;

    /// <summary>逻辑矩形换算成物理像素矩形；空矩形与窗口外的部分去掉。</summary>
    public static IReadOnlyList<PixelRect> Rectangles(IEnumerable<Rect> rectangles, double scaling, PixelSize window)
    {
        var bounds = new PixelRect(0, 0, window.Width, window.Height);
        var result = new List<PixelRect>();
        foreach (var widget in rectangles)
        {
            if (widget.Width <= 0 || widget.Height <= 0) continue;
            var left = (int)Math.Floor(widget.X * scaling);
            var top = (int)Math.Floor(widget.Y * scaling);
            var right = (int)Math.Ceiling(widget.Right * scaling);
            var bottom = (int)Math.Ceiling(widget.Bottom * scaling);
            var rect = new PixelRect(left, top, right - left, bottom - top).Intersect(bounds);
            if (rect.Width > 0 && rect.Height > 0) result.Add(rect);
        }
        return result;
    }

    /// <summary>窗口内的物理像素点是否落在这些矩形里。</summary>
    public static bool Accepts(IReadOnlyList<PixelRect> rectangles, PixelPoint point) =>
        rectangles.Any(rect => point.X >= rect.X && point.X < rect.Right && point.Y >= rect.Y && point.Y < rect.Bottom);
}
