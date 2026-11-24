using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>目标色集合缩略图的容器：高度不小于宽度 × 265 / 688（原工具 RegionCard），可用空间更高时占满。</summary>
public sealed class AtlasFrame : Decorator
{
    public const double Ratio = 265.0 / 688.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        Child?.Measure(new Size(width, width * Ratio));
        return new Size(width, Math.Round(width * Ratio));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
