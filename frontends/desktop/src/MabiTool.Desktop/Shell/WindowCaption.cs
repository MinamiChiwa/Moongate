using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 无边框窗口的拖动区判定：窗口顶部一条带内、指针下没有可交互控件的位置算作空白处，可以拖动窗口。
/// 判定只看指针下最上层的可见元素及其祖先，所以各页面不需要单独标记标题区。
/// </summary>
public static class WindowCaption
{
    /// <summary>主窗口顶部拖动带的高度：窗口外边距、页面上边距与 34 号页面标题一行。</summary>
    public const double MainBandHeight = 68;

    /// <summary>
    /// <paramref name="point"/>（<paramref name="root"/> 坐标）是否落在拖动带内的空白处。
    /// 带的范围是 0 到 <paramref name="bandBottom"/>；指针下是按钮、输入框、滑块、滚动条等可交互控件或其内部时不算。
    /// </summary>
    public static bool IsDragSurface(Visual root, Point point, double bandBottom)
    {
        if (point.Y < 0 || point.Y >= bandBottom || point.X < 0 || point.X >= root.Bounds.Width) return false;
        var hit = root.GetVisualAt(point, visual => visual.IsVisible && (visual is not IInputElement input || input.IsHitTestVisible));
        for (var visual = hit; visual is not null && visual != root; visual = visual.GetVisualParent())
            if (IsInteractive(visual)) return false;
        return true;
    }

    /// <summary>会自己响应指针或键盘的控件。停用的控件也算，避免在停用按钮上按下时窗口被拖走。</summary>
    public static bool IsInteractive(Visual visual) => visual switch
    {
        Button or TextBox or ComboBox or Slider or ScrollBar or Thumb or ListBoxItem or MenuItem or ToggleSwitch => true,
        ScrollViewer or ContentControl or ItemsControl or UserControl => false,
        InputElement { Focusable: true } => true,
        _ => false
    };
}
