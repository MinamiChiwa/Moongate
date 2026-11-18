using Avalonia;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 按所在屏幕工作区（逻辑像素，已扣除任务栏）收缩窗口的初始尺寸与最小尺寸：1366×768 或 125%／150% 缩放时，
/// 设计尺寸会高过工作区，标题栏落到屏幕外。收缩后的窗口四周留出 <see cref="Margin"/>，内容由各页的滚动区承接。
/// </summary>
public static class WindowFit
{
    /// <summary>窗口与工作区边缘的留白（逻辑像素）。</summary>
    public const double Margin = 16;

    /// <param name="desired">设计的初始尺寸。</param>
    /// <param name="minimum">设计的最小尺寸。</param>
    /// <param name="workArea">所在屏幕的工作区尺寸（逻辑像素）。</param>
    public static (Size Size, Size Minimum) Fit(Size desired, Size minimum, Size workArea)
    {
        double Limit(double work) => Math.Max(1, work - 2 * Margin);
        var minimumFit = new Size(Math.Min(minimum.Width, Limit(workArea.Width)), Math.Min(minimum.Height, Limit(workArea.Height)));
        var size = new Size(Math.Max(minimumFit.Width, Math.Min(desired.Width, Limit(workArea.Width))),
            Math.Max(minimumFit.Height, Math.Min(desired.Height, Limit(workArea.Height))));
        return (size, minimumFit);
    }

    /// <summary>窗口左上角拉回工作区内（物理像素）：保证标题栏可见、可以拖动。</summary>
    public static PixelPoint Clamp(PixelPoint position, PixelSize size, PixelRect workArea) => new(
        Math.Clamp(position.X, workArea.X, Math.Max(workArea.X, workArea.Right - size.Width)),
        Math.Clamp(position.Y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - size.Height)));
}
