using Avalonia;
using Avalonia.Media;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 按钮悬停放大（外观层 <c>PART_Face</c> 放大到 1.03，每边外扩自身宽高的 1.5%）需要的余量。
/// 会裁切的容器（展开容器、溢出行、表格单元格、编辑布局入口、卡片的滚动区）在不需要裁切的方向上把裁切范围外扩这么多，
/// 贴边的按钮放大后不被切掉；需要裁切的方向（展开过渡时的高度、溢出行收起时的右边）仍按边界裁切。
/// 8 覆盖宽高不超过 533 的按钮，更宽的按钮放在留有更大内边距的容器里。
/// <para>
/// 按钮自身与用户控件默认也裁切到自己的边界（模板化控件的默认值），主题把它们设为不裁切；
/// 直接贴着边缘的滚动区（弹出层、记录列表）用样式类 <c>room</c>（外边距 −8、内边距 8）。
/// </para>
/// </summary>
public static class HoverRoom
{
    public const double Size = 8;

    /// <summary>把裁切设为给定矩形（本控件坐标）；矩形没变时不换几何，排版反复调用不产生新对象。控件自身的 ClipToBounds 须为假。</summary>
    public static void Clip(Visual visual, Rect rect)
    {
        if (visual.Clip is RectangleGeometry current && current.Rect == rect) return;
        visual.Clip = new RectangleGeometry(rect);
    }
}
